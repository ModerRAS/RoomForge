namespace AudioOptimizer.Optimization;

using AudioOptimizer.Dsp;

/// <summary>
/// The four knobs the optimizer is allowed to touch, in the units the hardware holds them in.
/// <para>
/// <see cref="PhaseRad"/> is frequency-INDEPENDENT (exp(jφ) — one rotation for the whole band) and
/// <see cref="DelaySeconds"/> is frequency-PROPORTIONAL (exp(-j2πfΔt) — the rotation grows with f).
/// They are separate parameters and must stay separate: never convert one into the other, never let a
/// fixed phase stand in for a delay. A 90° phase is 5 ms at 50 Hz but only 2.5 ms at 100 Hz, and 5 ms is
/// 90° at 50 Hz but 180° at 100 Hz. Same phase, different delays.
/// </para>
/// </summary>
public sealed record SubwooferSetting(double GainDb, double PhaseRad, int Polarity, double DelaySeconds)
{
    /// <summary>The measured state of B: no gain, no phase, positive polarity, no delay.</summary>
    public static readonly SubwooferSetting Baseline = new(0.0, 0.0, 1, 0.0);

    /// <summary>G in dB → amplitude ratio 10^(G/20).</summary>
    public double GainLinear => ComplexMath.DbToLinear(GainDb);

    public double PhaseDegrees => PhaseRad * 180.0 / Math.PI;

    public SubwooferSetting Flipped => this with { Polarity = -Polarity };

    /// <summary>
    /// The compiler-generated formatting prints every public property, including <see cref="Flipped"/>, which
    /// builds another setting and prints that — recursion until the stack dies. Print the four stored knobs only.
    /// </summary>
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("GainDb = ").Append(GainDb)
            .Append(", PhaseRad = ").Append(PhaseRad)
            .Append(", Polarity = ").Append(Polarity)
            .Append(", DelaySeconds = ").Append(DelaySeconds);
        return true;
    }

    /// <summary>
    /// Snaps gain and phase onto the coarsest grid the hardware can actually hold. The search may refine
    /// more finely than this internally; this is the number that goes in the recommendation.
    /// </summary>
    public SubwooferSetting Quantized(double gainStepDb, double phaseStepDeg)
    {
        double phaseStepRad = phaseStepDeg * Math.PI / 180.0;
        return new SubwooferSetting(
            Math.Round(GainDb / gainStepDb) * gainStepDb,
            Math.Round(PhaseRad / phaseStepRad) * phaseStepRad,
            Polarity,
            DelaySeconds);
    }

    public static SubwooferSetting FromDegrees(double gainDb, double phaseDegrees, int polarity = 1, double delaySeconds = 0.0)
        => new(gainDb, phaseDegrees * Math.PI / 180.0, polarity, delaySeconds);

    /// <summary>Shared by the model and the optimizer: a zero or ±2 polarity is a bug, not a setting.</summary>
    public void Validate()
    {
        if (!double.IsFinite(GainDb)) throw new ArgumentOutOfRangeException(nameof(GainDb), GainDb, "Gain must be finite.");
        if (!double.IsFinite(PhaseRad)) throw new ArgumentOutOfRangeException(nameof(PhaseRad), PhaseRad, "Phase must be finite.");
        if (Polarity is not (1 or -1)) throw new ArgumentOutOfRangeException(nameof(Polarity), Polarity, "Polarity must be +1 or -1.");
        if (!double.IsFinite(DelaySeconds) || DelaySeconds < 0.0)
            throw new ArgumentOutOfRangeException(nameof(DelaySeconds), DelaySeconds, "Delay must be finite and >= 0 s.");
    }
}
