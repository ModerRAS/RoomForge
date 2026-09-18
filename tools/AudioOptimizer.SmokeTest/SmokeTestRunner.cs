namespace AudioOptimizer.SmokeTest;

using AudioOptimizer.Audio;
using AudioOptimizer.Core;
using AudioOptimizer.Dsp;
using AudioOptimizer.IO;
using AudioOptimizer.Measurement;

/// <summary>
/// The two modes of the harness. Everything it prints is evidence for a hardware run: which devices were opened,
/// the sample rate, the FFT geometry, the detected latency, the IR peak and the frequency-response summary, so a
/// run that "felt fine" can be checked afterwards. The measurement itself is NOT here: it calls the same
/// <see cref="PointMeasurement"/> the session runner does, so the two cannot drift apart.
/// </summary>
internal static class SmokeTestRunner
{
    public static int Run(string[] args, TextWriter output)
    {
        if (!SmokeTestOptions.TryParse(args, out SmokeTestOptions options, out string? error))
        {
            output.WriteLine($"error: {error}");
            output.WriteLine();
            output.WriteLine(SmokeTestOptions.Usage);
            return 2;
        }

        if (options.Help)
        {
            output.WriteLine(SmokeTestOptions.Usage);
            return 0;
        }

        using IAudioBackend backend = new WasapiAudioBackend();
        AudioDeviceLists devices = backend.EnumerateDevices();

        output.WriteLine($"RoomForge AudioOptimizer smoke test — backend {backend.Name}");
        output.WriteLine($"Timestamp      : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        output.WriteLine($"Default rate   : {new AudioBackendSettings().SampleRate} Hz (44.1/48/96 kHz supported)");
        output.WriteLine();
        output.Write(devices.Format());

        if (options.ListOnly)
        {
            output.WriteLine();
            output.WriteLine("No device was opened (list mode). Re-run with --measure to play and record a sweep.");
            return 0;
        }

        return Measure(backend, devices, options, output);
    }

    private static int Measure(IAudioBackend backend, AudioDeviceLists devices, SmokeTestOptions options, TextWriter output)
    {
        AudioDeviceInfo? render = Select(devices.Outputs, options.OutputName, "output");
        if (render is null)
        {
            output.WriteLine(options.OutputName is null
                ? "error: no active output (render) device found; connect a DAC/headphones and re-run."
                : $"error: no output device matches '{options.OutputName}'; re-run without --measure to list them.");
            return 1;
        }

        // Loopback records the render endpoint's own stream, so the capture device IS the output device.
        AudioDeviceInfo? capture = options.Loopback ? render : Select(devices.Inputs, options.InputName, "input");
        if (capture is null)
        {
            output.WriteLine(options.InputName is null
                ? "error: no active input (capture) device found; connect a microphone (e.g. UMIK-1) and re-run."
                : $"error: no input device matches '{options.InputName}'; re-run without --measure to list them.");
            return 1;
        }

        var settings = new AudioBackendSettings(
            options.SampleRate,
            options.Exclusive ? AudioShareMode.Exclusive : AudioShareMode.Shared);
        settings.Validate();

        var sweepSettings = new SweepSettings(options.SweepStartHz, options.SweepEndHz, options.SweepSeconds, options.SampleRate);
        sweepSettings.Validate();
        AudioCaptureMode captureMode = options.Loopback ? AudioCaptureMode.LoopbackCapture : AudioCaptureMode.Device;

        output.WriteLine();
        output.WriteLine("--- hardware configuration ---");
        output.WriteLine($"Output device  : {render}");
        output.WriteLine($"Input device   : {capture}{(options.Loopback ? " (loopback: the output stream itself)" : string.Empty)}");
        output.WriteLine($"Sample rate    : {settings.SampleRate} Hz");
        output.WriteLine($"Share mode     : {settings.ShareMode}");
        output.WriteLine($"Sweep          : {sweepSettings.StartHz:F1}-{sweepSettings.EndHz:F1} Hz, {sweepSettings.DurationSeconds:F3} s, "
            + $"{sweepSettings.SampleCount} samples, {SweepGenerator.TaperSeconds * 1000:F0} ms taper");
        output.WriteLine($"Recording      : {options.PreRollSeconds:F3} s pre-roll + sweep + {options.PostRollSeconds:F3} s post-roll");
        output.WriteLine($"Playback gain  : {options.PlaybackGain:F3} (recorded level scales with this; the inverse filter does not)");
        output.WriteLine();

        output.WriteLine("--- measuring ---");
        AudioEngineInfo? engine = null;
        PointMeasurementResult measurement = PointMeasurement.Run(
            backend, render, capture, settings, sweepSettings,
            TimeSpan.FromSeconds(options.PreRollSeconds),
            TimeSpan.FromSeconds(options.PostRollSeconds),
            options.PlaybackGain,
            captureMode,
            info =>
            {
                // Printed here because these values only exist mid-operation: the recorder's latency is zero
                // until StartRecording initializes the client, and the player's is not known until Init.
                engine = info;
                output.WriteLine();
                output.WriteLine("--- negotiated device formats (read after init, before playback) ---");
                output.WriteLine($"Requested      : output {settings.SampleRate} Hz mono float32 in {settings.ShareMode} mode; "
                    + "capture: nothing requested, the endpoint's own format is used");
                output.WriteLine($"Output format  : {AudioEngineInfo.Describe(info.OutputFormat)}");
                output.WriteLine($"Endpoint mix   : {(info.DeviceMixFormat is null ? "(not exposed by the backend in this mode)" : AudioEngineInfo.Describe(info.DeviceMixFormat))}");
                output.WriteLine($"Capture format : {AudioEngineInfo.Describe(info.CaptureFormat)}");
                output.WriteLine($"Endpoint latency: output {info.OutputLatencyMilliseconds} ms, capture {info.CaptureLatencyMilliseconds} ms "
                    + $"reported by the device (requested buffer: {settings.LatencyMilliseconds} ms)");
            });

        // The system temp directory, never the working directory: a capture is easily tens of megabytes and must
        // not sit one `git add -A` away from being committed. --wav still overrides this.
        string wavPath = options.WavPath ?? Path.Combine(Path.GetTempPath(), $"smoketest-{DateTime.Now:yyyyMMdd-HHmmss}.wav");
        WavFile.Write(wavPath, measurement.Recording, settings.SampleRate, WavSampleFormat.Float32, channels: 1);

        SweepArrivalAnalysis analysis = measurement.Alignment;
        output.WriteLine();
        output.WriteLine("--- analysis ---");
        output.WriteLine($"Recording      : {measurement.Recording.Length} samples ({measurement.Recording.Length / (double)settings.SampleRate:F3} s) at {settings.SampleRate} Hz, mono float32");
        output.WriteLine($"WAV written    : {Path.GetFullPath(wavPath)}");
        output.WriteLine($"Deconvolution  : recording {measurement.Recording.Length} ⊛ inverse {measurement.SweepSampleCount} → "
            + $"{measurement.ImpulseResponse.Length} samples, FFT {measurement.FftSize} (NextPowerOfTwo), bin {settings.SampleRate / (double)measurement.FftSize:F4} Hz");
        output.WriteLine($"Sweep arrival  : {analysis.ArrivalIndex} samples (pre-roll requested: {options.PreRollSeconds * settings.SampleRate:F0} samples)");
        output.WriteLine($"Sweep onset    : {SweepAlignment.FindOnset(measurement.Recording, analysis.ArrivalIndex)} samples "
            + "(first sample above digital silence; the arrival above is window-aligned and can precede it)");
        output.WriteLine($"IR peak index  : {analysis.PeakIndex} (zero-lag reference {analysis.ZeroLagIndex})");
        output.WriteLine($"Latency        : {analysis.LatencySamples} samples = {analysis.LatencySamples * 1000.0 / settings.SampleRate:F1} ms "
            + "(from the start of the recording; includes the pre-roll — subtract it for device latency alone)");
        output.WriteLine($"IR crop        : {analysis.Cropped.Samples.Length} samples, peak at {analysis.Cropped.PeakIndex} "
            + $"(absolute {analysis.Cropped.AbsolutePeakIndex})");
        if (engine is not null)
        {
            output.WriteLine($"Endpoint vs measured: device reports {engine.OutputLatencyMilliseconds} ms output / {engine.CaptureLatencyMilliseconds} ms capture, "
                + $"measured {analysis.LatencySamples} samples ({analysis.LatencySamples * 1000.0 / settings.SampleRate:F1} ms)");
            output.WriteLine("                 These are different quantities: the device figures are the buffer latencies in use,");
            output.WriteLine("                 the measured figure is when the sweep actually landed in the recorded file.");
        }

        output.WriteLine();
        output.WriteLine("--- frequency response (rectangular window, full deconvolution) ---");
        WriteBand(output, measurement.Response, 20, 150, "20-150 Hz (analysis band)");
        WriteBand(output, measurement.Response, 30, 140, "30-140 Hz (interior)");
        WriteBand(output, measurement.Response, 20, 25, "20-25 Hz  (LOW EDGE ZONE)");
        WriteBand(output, measurement.Response, 145, 150, "145-150 Hz (HIGH EDGE ZONE)");
        output.WriteLine("Edge note      : absolute level inside the edge zones is unreliable by design (ESS edge artifact,");
        output.WriteLine("                 measured ~12 dB at the sweep start); A/B and A/B/AB ratios are unaffected.");
        output.WriteLine();
        return Verdict(measurement, output);
    }

    /// <summary>
    /// The verdict is the SAME quality-check family the session runner uses, not an ad-hoc rule living only here:
    /// a detected arrival is not the same as a detected sweep, and the muted-microphone case — a correlation peak
    /// before the zero-lag reference, which no played sweep can produce — is one of those checks.
    /// </summary>
    private static int Verdict(PointMeasurementResult measurement, TextWriter output)
    {
        if (measurement.IsValid)
        {
            output.WriteLine("Result         : OK");
            return 0;
        }

        output.WriteLine($"Result         : NOT TRUSTWORTHY — {string.Join(", ", measurement.Issues)}");
        SweepArrivalAnalysis analysis = measurement.Alignment;
        if (measurement.Issues.Contains(QualityIssue.SweepNotDetected))
        {
            output.WriteLine("                 no sweep arrival was found in the recording: the input may be muted, the wrong");
            output.WriteLine("                 device, or too quiet — nothing was deconvolved that means anything.");
        }
        else if (measurement.Issues.Contains(QualityIssue.ImpulseResponseNotFound))
        {
            output.WriteLine($"                 the correlation peak sits at {analysis.PeakIndex}, the zero-lag reference at {analysis.ZeroLagIndex};");
            output.WriteLine("                 a peak before the reference cannot happen for a played sweep, so the input carried");
            output.WriteLine("                 noise (muted microphone, no speaker path, or the wrong input device), not the sweep.");
        }
        else
        {
            output.WriteLine("                 the recording was made, but it does not hold a clean sweep — the reasons above name why.");
        }
        output.WriteLine("                 Check the input device and its level, then re-run (full scale is --gain=1.0).");
        return 1;
    }

    private static void WriteBand(TextWriter output, FrequencyResponse[] response, double lowHz, double highHz, string label)
    {
        FrequencyResponse[] bins = response.Where(b => b.FrequencyHz >= lowHz && b.FrequencyHz <= highHz).ToArray();
        if (bins.Length == 0)
        {
            output.WriteLine($"{label,-28}: no bins in the analysed band");
            return;
        }

        double mean = bins.Average(b => b.MagnitudeDb);
        FrequencyResponse minimum = bins.MinBy(b => b.MagnitudeDb)!;
        FrequencyResponse maximum = bins.MaxBy(b => b.MagnitudeDb)!;
        FrequencyResponse worst = bins.MaxBy(b => Math.Abs(b.MagnitudeDb - mean))!;
        double sx = 0, sy = 0, sxx = 0, sxy = 0;
        foreach (FrequencyResponse bin in bins)
        {
            double x = Math.Log10(bin.FrequencyHz);
            sx += x; sy += bin.MagnitudeDb; sxx += x * x; sxy += x * bin.MagnitudeDb;
        }
        int n = bins.Length;
        double tilt = (n * sxy - sx * sy) / (n * sxx - sx * sx) * Math.Log10(highHz / lowHz);

        output.WriteLine($"{label,-28}: {n} bins | mean {mean,7:F2} dB | min {minimum.MagnitudeDb,7:F2} @ {minimum.FrequencyHz,7:F2} Hz "
            + $"| max {maximum.MagnitudeDb,7:F2} @ {maximum.FrequencyHz,7:F2} Hz | maxdev {Math.Abs(worst.MagnitudeDb - mean),6:F2} dB @ {worst.FrequencyHz,7:F2} Hz | tilt {tilt,6:F2} dB");
    }

    private static AudioDeviceInfo? Select(IReadOnlyList<AudioDeviceInfo> devices, string? requested, string kind)
    {
        if (requested is null) return devices.Count > 0 ? devices[0] : null;

        foreach (AudioDeviceInfo device in devices)
            if (string.Equals(device.Id, requested, StringComparison.OrdinalIgnoreCase)
                || string.Equals(device.Name, requested, StringComparison.OrdinalIgnoreCase))
                return device;

        _ = kind;
        return null;
    }
}
