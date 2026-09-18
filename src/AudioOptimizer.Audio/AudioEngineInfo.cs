namespace AudioOptimizer.Audio;

using NAudio.Wave;

/// <summary>
/// What the endpoints actually negotiated, as opposed to what was requested. Requesting 48 kHz mono float32
/// says nothing about what the device ran: Windows hands a Shared-mode client the endpoint's own mix format,
/// which on real hardware arrives as WAVE_FORMAT_EXTENSIBLE (32-bit, IEEE_FLOAT sub-format, stereo). This
/// snapshot is taken mid-operation because that is the only moment the numbers exist — the recorder's latency
/// is zero until StartRecording initializes the audio client, and the player's is not known until Init.
/// </summary>
public sealed record AudioEngineInfo(
    WaveFormat OutputFormat,
    WaveFormat? DeviceMixFormat,
    int OutputLatencyMilliseconds,
    WaveFormat CaptureFormat,
    int CaptureLatencyMilliseconds)
{
    /// <summary>
    /// One-line description of a device format. A WAVE_FORMAT_EXTENSIBLE wrapper is unwrapped through NAudio's
    /// own <see cref="WaveFormatExtensible.AsStandardWaveFormat"/> so the line shows the format that really ran
    /// instead of "Extensible", which on its own hides whether the samples are float or PCM.
    /// </summary>
    public static string Describe(WaveFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        string described = $"{format.Encoding} {format.BitsPerSample}-bit, {format.SampleRate} Hz, {format.Channels} ch";
        if (format is not WaveFormatExtensible extensible) return described;

        WaveFormat standard = extensible.AsStandardWaveFormat();
        return $"{described} → standard: {standard.Encoding} {standard.BitsPerSample}-bit, sub-format {extensible.SubFormat}";
    }
}
