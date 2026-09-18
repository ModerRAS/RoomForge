namespace AudioOptimizer.Audio;

using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

/// <summary>
/// WASAPI implementation of <see cref="IAudioBackend"/> (NAudio 3.1.0, NAudio.Wasapi package).
/// The assembly targets net10.0-windows: every MMDevice/Wasapi member is annotated windows-only.
/// Threading lives here and nowhere else: the recorder pushes blocks on NAudio's own capture thread while the
/// calling thread waits on events. Nothing in DSP or the smoke test spawns a thread.
/// </summary>
public sealed class WasapiAudioBackend : IAudioBackend
{
    public string Name => "WASAPI";

    public AudioDeviceLists EnumerateDevices()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return new AudioDeviceLists(Enumerate(enumerator, DataFlow.Capture), Enumerate(enumerator, DataFlow.Render));
        }
        catch (COMException ex)
        {
            throw new AudioDeviceOpenException(
                DeviceOpenError.Describe(AudioOpenFailureKind.Unknown, "(device enumeration)", new AudioBackendSettings(), ex.Message),
                AudioOpenFailureKind.Unknown, "(device enumeration)", ex);
        }
    }

    private static AudioDeviceInfo[] Enumerate(MMDeviceEnumerator enumerator, DataFlow flow)
    {
        using MMDeviceCollection devices = enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active);
        var result = new AudioDeviceInfo[devices.Count];
        for (int i = 0; i < devices.Count; i++)
        {
            using MMDevice device = devices[i];
            result[i] = new AudioDeviceInfo(device.ID, device.FriendlyName);
        }
        return result;
    }

    public double[] PlayAndRecord(
        AudioDeviceInfo renderDevice,
        AudioDeviceInfo captureDevice,
        AudioBackendSettings settings,
        double[] monoSweep,
        TimeSpan preRoll,
        TimeSpan postRoll,
        AudioCaptureMode captureMode = AudioCaptureMode.Device,
        Action<AudioEngineInfo>? onEngineReady = null)
    {
        ArgumentNullException.ThrowIfNull(renderDevice);
        ArgumentNullException.ThrowIfNull(captureDevice);
        ArgumentNullException.ThrowIfNull(monoSweep);
        settings.Validate();
        if (preRoll < TimeSpan.Zero || postRoll < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(preRoll), "Pre/post roll must be >= 0.");
        if (monoSweep.Length == 0) throw new ArgumentException("Sweep is empty.", nameof(monoSweep));
        if (captureMode == AudioCaptureMode.LoopbackCapture && settings.ShareMode == AudioShareMode.Exclusive)
            throw new ArgumentException("WASAPI loopback capture is a shared-mode feature; exclusive mode cannot record its own render stream.", nameof(settings));

        using MMDevice render = Resolve(renderDevice, DataFlow.Render, settings);
        using MMDevice capture = Resolve(captureDevice, captureMode == AudioCaptureMode.LoopbackCapture ? DataFlow.Render : DataFlow.Capture, settings);

        int preRollSamples = (int)Math.Round(preRoll.TotalSeconds * settings.SampleRate);
        int postRollSamples = (int)Math.Round(postRoll.TotalSeconds * settings.SampleRate);
        int expected = preRollSamples + monoSweep.Length + postRollSamples;
        // Non-negotiable: the whole sweep must be in the buffer or the deconvolution has nothing to find.
        // The rolls only keep the arrival away from the edges, so they are best effort — a loopback recording
        // never contains the pre-roll at all, because the render endpoint is idle until playback starts.
        int required = monoSweep.Length;
        // 1 s of slack: the device hands over whole callback blocks, and a full ring silently drops the NEWEST data.
        var recording = new BlockRingBuffer(expected + settings.SampleRate);

        WasapiRecorderBuilder recorderBuilder = new WasapiRecorderBuilder()
            .WithDevice(capture)
            .WithBufferLength(settings.LatencyMilliseconds)
            .WithEventSync();
        if (captureMode == AudioCaptureMode.LoopbackCapture) recorderBuilder.WithLoopbackCapture();
        if (settings.ShareMode == AudioShareMode.Exclusive) recorderBuilder.WithExclusiveMode(); else recorderBuilder.WithSharedMode();

        using WasapiRecorder recorder = recorderBuilder.Build();
        if (SampleFormatConverter.TryFromWaveFormat(recorder.WaveFormat) is not { } captureFormat)
        {
            throw new AudioDeviceOpenException(
                DeviceOpenError.Describe(AudioOpenFailureKind.UnsupportedFormat, captureDevice.Name, settings,
                    $"capture format is {recorder.WaveFormat.Encoding} {recorder.WaveFormat.BitsPerSample}-bit "
                    + $"(sub-format {(recorder.WaveFormat as WaveFormatExtensible)?.SubFormat.ToString() ?? "n/a"}), "
                    + $"{recorder.WaveFormat.SampleRate} Hz, {recorder.WaveFormat.Channels} ch"),
                AudioOpenFailureKind.UnsupportedFormat, captureDevice.Name);
        }

        int captureChannels = recorder.WaveFormat.Channels;

        using var sweepCaptured = new ManualResetEventSlim(false);
        using var everythingCaptured = new ManualResetEventSlim(false);
        Exception? callbackFailure = null;
        recorder.DataAvailable += (buffer, _, _, _) =>
        {
            recording.Write(SampleFormatConverter.ToMonoDoubles(buffer, captureFormat, captureChannels));
            if (recording.Count >= required) sweepCaptured.Set();
            if (recording.Count >= expected) everythingCaptured.Set();
        };
        recorder.RecordingStopped += (_, e) => callbackFailure = e.Exception;

        try
        {
            recorder.StartRecording();

            // Let the pre-roll elapse before the sweep starts, so the arrival is not at the file's first sample.
            // Skipped for loopback: that stream only produces data while the endpoint is actually rendering.
            if (preRoll > TimeSpan.Zero && captureMode == AudioCaptureMode.Device)
                sweepCaptured.Wait(preRoll + TimeSpan.FromSeconds(0.5));

            WasapiPlayerBuilder playerBuilder = new WasapiPlayerBuilder()
                .WithDevice(render)
                .WithLatency(settings.LatencyMilliseconds)
                .WithEventSync();
            if (settings.ShareMode == AudioShareMode.Exclusive) playerBuilder.WithExclusiveMode(); else playerBuilder.WithSharedMode();

            using WasapiPlayer player = playerBuilder.Build();
            WaveFormat format = WaveFormat.CreateIeeeFloatWaveFormat(settings.SampleRate, channels: 1);
            if (settings.ShareMode == AudioShareMode.Exclusive && !player.IsFormatSupported(format))
            {
                throw new AudioDeviceOpenException(
                    DeviceOpenError.Describe(AudioOpenFailureKind.SampleRateNotSupported, renderDevice.Name, settings,
                        "the endpoint does not accept this format in exclusive mode"),
                    AudioOpenFailureKind.SampleRateNotSupported, renderDevice.Name);
            }

            using var playbackEnded = new ManualResetEventSlim(false);
            player.PlaybackStopped += (_, e) =>
            {
                callbackFailure ??= e.Exception;
                playbackEnded.Set();
            };

            byte[] sweepBytes = SampleFormatConverter.ToInterleavedBytes(monoSweep, AudioSampleFormat.Float32, 1);
            using var stream = new RawSourceWaveStream(sweepBytes, 0, sweepBytes.Length, format);
            player.Init(stream);
            // Read the negotiated values here: the recorder's LatencyMilliseconds is 0 until StartRecording has
            // initialized its audio client, and the player's is only meaningful after Init.
            onEngineReady?.Invoke(new AudioEngineInfo(
                player.OutputWaveFormat,
                TryDeviceMixFormat(player),
                player.LatencyMilliseconds,
                recorder.WaveFormat,
                recorder.LatencyMilliseconds));
            player.Play();
            if (!playbackEnded.Wait(PlaybackTimeout(monoSweep.Length, settings.SampleRate)))
                throw new AudioDeviceOpenException(
                    DeviceOpenError.Describe(AudioOpenFailureKind.Unknown, renderDevice.Name, settings, "playback did not finish"),
                    AudioOpenFailureKind.Unknown, renderDevice.Name);

            if (callbackFailure is not null) throw Map(callbackFailure, captureDevice, settings);

            // The sweep is a hard requirement; a gap here means a muted/quiet/wrong input, or dropped blocks.
            if (!sweepCaptured.Wait(TimeSpan.FromSeconds((double)monoSweep.Length / settings.SampleRate + 3)))
                throw new AudioDeviceOpenException(
                    DeviceOpenError.Describe(AudioOpenFailureKind.Unknown, captureDevice.Name, settings,
                        $"the input delivered {recording.Count} of {required} sweep samples "
                        + $"({recording.TotalDropped} dropped by the ring buffer)"),
                    AudioOpenFailureKind.Unknown, captureDevice.Name, callbackFailure);

            // Post-roll (room decay, or the last blocks of a loopback stream) is best effort: wait a bounded
            // amount and accept whatever arrived. ponytail: fixed bound, not an adaptive silence detector.
            if (postRollSamples > 0)
                everythingCaptured.Wait(TimeSpan.FromSeconds((double)postRollSamples / settings.SampleRate + 1));

            recorder.StopRecording();      // synchronous: joins the capture thread, so no more writes follow
            return recording.ToArray();
        }
        catch (CoreAudioException ex)
        {
            throw Map(ex, captureDevice, settings);
        }
        finally
        {
            if (recorder.CaptureState != CaptureState.Stopped) recorder.StopRecording();
        }
    }

    /// <summary>
    /// The endpoint's preferred mix format, when the player exposes one. Null in modes where it does not apply,
    /// so a purely diagnostic read can never take down a measurement that is otherwise fine.
    /// </summary>
    private static WaveFormat? TryDeviceMixFormat(WasapiPlayer player)
    {
        try
        {
            return player.DeviceMixFormat;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static TimeSpan PlaybackTimeout(int sampleCount, int sampleRate) =>
        TimeSpan.FromSeconds((double)sampleCount / sampleRate + 5);

    private static MMDevice Resolve(AudioDeviceInfo device, DataFlow requiredFlow, AudioBackendSettings settings)
    {
        using var enumerator = new MMDeviceEnumerator();
        try
        {
            MMDevice byId = enumerator.GetDevice(device.Id);
            if (byId.DataFlow == requiredFlow) return byId;
            byId.Dispose();
        }
        catch (COMException)
        {
            // Fall through to the friendly-name lookup: ids survive reboots but a stale id is a normal user error.
        }

        using MMDeviceCollection available = enumerator.EnumerateAudioEndPoints(requiredFlow, DeviceState.Active);
        for (int i = 0; i < available.Count; i++)
        {
            using MMDevice candidate = available[i];
            if (string.Equals(candidate.FriendlyName, device.Name, StringComparison.OrdinalIgnoreCase))
                return enumerator.GetDevice(candidate.ID);
        }

        string expected = requiredFlow == DataFlow.Render ? "output" : "input";
        throw new AudioDeviceOpenException(
            DeviceOpenError.Describe(AudioOpenFailureKind.DeviceNotFound, device.Name, settings,
                $"no active {expected} endpoint with this id or name"),
            AudioOpenFailureKind.DeviceNotFound, device.Name);
    }

    private static AudioDeviceOpenException Map(Exception failure, AudioDeviceInfo device, AudioBackendSettings settings)
    {
        AudioOpenFailureKind kind = failure switch
        {
            AudioDeviceInUseException => AudioOpenFailureKind.DeviceInUse,
            AudioExclusiveModeNotAllowedException => AudioOpenFailureKind.ExclusiveModeNotAllowed,
            AudioFormatNotSupportedException => AudioOpenFailureKind.SampleRateNotSupported,
            AudioDeviceDisconnectedException => AudioOpenFailureKind.DeviceDisconnected,
            _ => AudioOpenFailureKind.Unknown,
        };
        string? suggested = (failure as AudioFormatNotSupportedException)?.SuggestedFormat is { } suggestion
            ? $"{suggestion.SampleRate} Hz"
            : null;
        return new AudioDeviceOpenException(
            DeviceOpenError.Describe(kind, device.Name, settings, failure.Message, suggested), kind, device.Name, failure);
    }

    public void Dispose()
    {
        // Nothing to hold: every MMDevice/player/recorder is disposed by the operation that opened it.
    }
}
