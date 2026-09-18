namespace AudioOptimizer.Simulation;

using AudioOptimizer.Audio;
using AudioOptimizer.Core;

/// <summary>
/// The offline rig wearing <see cref="IAudioBackend"/>. This is the whole integration: the product's measurement chain
/// (<c>PointMeasurement.Run</c>) asks for a capture and gets a synthesized one instead of a WASAPI one, and everything
/// after that — deconvolution, alignment, FFT, complex frequency response, the quality checks — is the shipped code
/// path, not a second copy of it.
/// <para>
/// Routing is by device, exactly as the hardware workflow does it: the render endpoint is the sub configuration being
/// driven ("Sub A", "Sub B", "Sub A+B") and the capture endpoint is the microphone position. No mutable "current
/// point" state exists, so a measurement cannot be taken against a request that was never set.
/// </para>
/// <para>
/// It opens no device, enumerates none, and cannot fail to open: the three failure modes a real backend has (no
/// device, busy device, unsupported rate) have no meaning here.
/// </para>
/// </summary>
public sealed class VirtualAudioBackend : IAudioBackend
{
    private readonly VirtualRoom _rig;
    private readonly Dictionary<string, int> _microphoneIndexByDeviceId = [];
    private readonly Dictionary<SubMode, AudioDeviceInfo> _subDevices = [];

    public VirtualAudioBackend(VirtualRoom rig)
    {
        ArgumentNullException.ThrowIfNull(rig);
        _rig = rig;

        var inputs = new List<AudioDeviceInfo>(rig.Microphones.Count);
        for (int i = 0; i < rig.Microphones.Count; i++)
        {
            var device = new AudioDeviceInfo($"mic:{rig.Microphones[i].Id}", rig.Microphones[i].Id);
            _microphoneIndexByDeviceId[device.Id] = i;
            inputs.Add(device);
        }

        var outputs = new List<AudioDeviceInfo>(2);
        foreach (SubMode mode in Modes(rig))
        {
            var device = new AudioDeviceInfo(DeviceId(mode), Label(mode));
            _subDevices[mode] = device;
            outputs.Add(device);
        }

        Devices = new AudioDeviceLists(inputs, outputs);
    }

    public string Name => "virtual (image-source room)";

    public AudioDeviceLists Devices { get; }

    /// <summary>The configurations this rig can drive: A alone, plus B and A+B when a second sub exists.</summary>
    public IReadOnlyList<SubMode> SupportedModes => Modes(_rig);

    /// <summary>The render endpoint that drives <paramref name="mode"/>. A scenario passes this straight to the chain.</summary>
    public AudioDeviceInfo SubDevice(SubMode mode)
        => _subDevices.TryGetValue(mode, out AudioDeviceInfo? device)
            ? device
            : throw new ArgumentOutOfRangeException(nameof(mode), mode, $"This rig has no '{mode}' configuration.");

    /// <summary>The capture endpoint at <paramref name="point"/>. Throws for a point that is not on this rig's grid.</summary>
    public AudioDeviceInfo MicrophoneDevice(MeasurementPoint point)
    {
        ArgumentNullException.ThrowIfNull(point);
        string id = $"mic:{point.Id}";
        return _microphoneIndexByDeviceId.ContainsKey(id)
            ? new AudioDeviceInfo(id, point.Id)
            : throw new ArgumentException($"'{point.Id}' is not one of this rig's microphone positions.", nameof(point));
    }

    public AudioDeviceLists EnumerateDevices() => Devices;

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
        _ = captureMode;
        _ = onEngineReady;
        ArgumentNullException.ThrowIfNull(renderDevice);
        ArgumentNullException.ThrowIfNull(captureDevice);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(monoSweep);
        if (settings.SampleRate != _rig.Config.SampleRate)
            throw new AudioDeviceOpenException(
                $"the virtual rig is configured for {_rig.Config.SampleRate} Hz; the chain asked for {settings.SampleRate} Hz "
                + "(one simulation rate, and the sweep, the impulse responses and the recording all use it)",
                AudioOpenFailureKind.UnsupportedFormat, renderDevice.Name);

        SubMode mode = ModeOf(renderDevice);
        if (!_microphoneIndexByDeviceId.TryGetValue(captureDevice.Id, out int microphoneIndex))
            throw new AudioDeviceOpenException(
                $"'{captureDevice}' is not a microphone of this virtual rig", AudioOpenFailureKind.DeviceNotFound, captureDevice.Name);

        // onEngineReady is deliberately not called: it reports the formats and buffer latencies that were
        // NEGOTIATED, and nothing was negotiated here — no endpoint exists to have a mix format. An
        // AudioEngineInfo with null formats would be a claim about hardware that is not there.
        return _rig.Record(
            monoSweep,
            mode,
            microphoneIndex,
            preRoll.TotalSeconds,
            postRoll.TotalSeconds,
            _rig.NoiseSeedFor(mode, microphoneIndex));
    }

    public void Dispose()
    {
        // Nothing to release: no device was ever opened. Present because IAudioBackend is disposable.
    }

    private SubMode ModeOf(AudioDeviceInfo device)
    {
        foreach ((SubMode mode, AudioDeviceInfo known) in _subDevices)
            if (known.Id == device.Id) return mode;

        throw new AudioDeviceOpenException(
            $"'{device}' is not a subwoofer configuration of this virtual rig", AudioOpenFailureKind.DeviceNotFound, device.Name);
    }

    private static string DeviceId(SubMode mode) => $"sub:{mode}";

    private static string Label(SubMode mode) => mode switch
    {
        SubMode.A => "Sub A",
        SubMode.B => "Sub B",
        SubMode.AB => "Sub A+B",
        _ => mode.ToString(),
    };

    private static IReadOnlyList<SubMode> Modes(VirtualRoom rig)
        => rig.Subs.Count > 1 ? [SubMode.A, SubMode.B, SubMode.AB] : [SubMode.A];
}
