namespace AudioOptimizer.Dsp;

using System.Numerics;

/// <summary>
/// Small complex/decibel helpers shared by the DSP kit. No state, no allocation beyond
/// <see cref="Unwrap"/>'s result array.
/// </summary>
public static class ComplexMath
{
    private const double TwoPi = 2.0 * Math.PI;

    /// <summary>linear = 10^(dB/20) — amplitude ratio, not power ratio.</summary>
    public static double DbToLinear(double decibels) => Math.Pow(10.0, decibels / 20.0);

    /// <summary>dB = 20·log10(linear). linear = 0 gives -∞.</summary>
    public static double LinearToDb(double linear) => 20.0 * Math.Log10(linear);

    /// <summary>|z| = sqrt(re² + im²).</summary>
    public static double Magnitude(Complex value) => Complex.Abs(value);

    /// <summary>Wrapped phase arg(z) = atan2(im, re) in (-π, π].</summary>
    public static double Phase(Complex value) => Math.Atan2(value.Imaginary, value.Real);

    /// <summary>
    /// Unwraps a wrapped phase sequence: every step is folded into (-π, π] and re-accumulated,
    /// so ±2π jumps from atan2 disappear.
    /// </summary>
    public static double[] Unwrap(double[] wrappedPhase)
    {
        var unwrapped = new double[wrappedPhase.Length];
        if (wrappedPhase.Length == 0) return unwrapped;

        unwrapped[0] = wrappedPhase[0];
        for (int i = 1; i < wrappedPhase.Length; i++)
        {
            double step = wrappedPhase[i] - wrappedPhase[i - 1];
            step -= TwoPi * Math.Round(step / TwoPi);
            unwrapped[i] = unwrapped[i - 1] + step;
        }
        return unwrapped;
    }

    /// <summary>Rotates by exp(jφ) = cos φ + j·sin φ.</summary>
    public static Complex Rotate(Complex value, double phaseRadians)
        => value * Complex.FromPolarCoordinates(1.0, phaseRadians);

    /// <summary>Propagation delay Δt: multiplies by exp(-j2πfΔt).</summary>
    public static Complex ApplyDelay(Complex value, double frequencyHz, double delaySeconds)
        => Rotate(value, -TwoPi * frequencyHz * delaySeconds);

    /// <summary>Complex (vector) sum — the interference at one frequency bin.</summary>
    public static Complex Sum(params Complex[] values)
    {
        var total = Complex.Zero;
        foreach (var value in values) total += value;
        return total;
    }
}
