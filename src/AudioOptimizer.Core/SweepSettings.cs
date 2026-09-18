namespace AudioOptimizer.Core;

/// <summary>
/// Definition of one exponential sweep measurement. Defaults are the RoomForge Phase 1 case:
/// 20 Hz → 150 Hz over 1 s at 48 kHz.
/// </summary>
public sealed record SweepSettings(
    double StartHz = 20,
    double EndHz = 150,
    double DurationSeconds = 1.0,
    double SampleRate = 48000)
{
    /// <summary>Sample count for the whole sweep: n = round(T · fs).</summary>
    public int SampleCount => (int)Math.Round(DurationSeconds * SampleRate);

    /// <summary>
    /// Checks f1 &gt; 0, f2 &gt; f1, T &gt; 0, fs &gt; 0. Called on demand (by the generator, not the
    /// constructor) so settings can be assembled and inspected field by field first.
    /// </summary>
    public void Validate()
    {
        if (StartHz <= 0)
            throw new ArgumentOutOfRangeException(nameof(StartHz), StartHz, "StartHz must be > 0.");
        if (EndHz <= StartHz)
            throw new ArgumentOutOfRangeException(nameof(EndHz), EndHz, "EndHz must be > StartHz.");
        if (DurationSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(DurationSeconds), DurationSeconds, "DurationSeconds must be > 0.");
        if (SampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(SampleRate), SampleRate, "SampleRate must be > 0.");
    }
}
