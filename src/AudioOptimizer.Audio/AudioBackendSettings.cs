namespace AudioOptimizer.Audio;

/// <summary>
/// What the smoke test opens: 48 kHz default (the Phase 1–6 DSP convention), shared by default because
/// exclusive mode fails on a busy endpoint.
/// </summary>
public sealed record AudioBackendSettings(int SampleRate = 48000, AudioShareMode ShareMode = AudioShareMode.Shared, int LatencyMilliseconds = 100)
{
    /// <summary>The rates the CLI accepts. 44.1/48/96 kHz covers the UMIK-1 (48 kHz native) and most DACs.</summary>
    public static IReadOnlyList<int> SupportedSampleRates { get; } = [44100, 48000, 96000];

    public void Validate()
    {
        if (!SupportedSampleRates.Contains(SampleRate))
            throw new ArgumentOutOfRangeException(nameof(SampleRate), SampleRate,
                $"Sample rate must be one of {string.Join(", ", SupportedSampleRates)} Hz.");
        if (LatencyMilliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(LatencyMilliseconds), LatencyMilliseconds, "Latency must be > 0 ms.");
    }

    public override string ToString() => $"{SampleRate} Hz, {ShareMode} mode, {LatencyMilliseconds} ms latency";
}
