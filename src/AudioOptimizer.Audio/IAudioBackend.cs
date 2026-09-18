namespace AudioOptimizer.Audio;

/// <summary>
/// One audio device layer. WASAPI is the shipped implementation; ASIO is the planned second one, which is the
/// only reason this is an interface. The single operation is the one a sweep measurement actually needs — play
/// the sweep while recording — so the device-open, pre-roll, playback and post-roll ordering is a backend
/// concern and the callers stay synchronous. Threading lives inside the implementation (NAudio's capture
/// callback thread); the DSP library has none.
/// </summary>
public interface IAudioBackend : IDisposable
{
    /// <summary>Backend name for evidence output, e.g. "WASAPI".</summary>
    string Name { get; }

    /// <summary>Inputs and outputs as separate lists. Must not throw when there are no devices at all.</summary>
    AudioDeviceLists EnumerateDevices();

    /// <summary>
    /// Opens <paramref name="renderDevice"/> and <paramref name="captureDevice"/>, starts recording, plays the
    /// mono sweep, and returns the recording: <paramref name="preRoll"/> of silence, the sweep, then
    /// <paramref name="postRoll"/>. With <see cref="AudioCaptureMode.LoopbackCapture"/> the device is the render
    /// endpoint and <paramref name="captureDevice"/> is ignored (WASAPI records that endpoint's own stream).
    /// Throws <see cref="AudioDeviceOpenException"/> — never a raw COM exception — when a device cannot be opened,
    /// is busy, or rejects the sample rate.
    /// </summary>
    /// <param name="onEngineReady">
    /// Called once, after both endpoints are initialized and before playback starts, with the formats and buffer
    /// latencies that were actually negotiated. Diagnostics only — it cannot change the measurement.
    /// </param>
    double[] PlayAndRecord(
        AudioDeviceInfo renderDevice,
        AudioDeviceInfo captureDevice,
        AudioBackendSettings settings,
        double[] monoSweep,
        TimeSpan preRoll,
        TimeSpan postRoll,
        AudioCaptureMode captureMode = AudioCaptureMode.Device,
        Action<AudioEngineInfo>? onEngineReady = null);
}
