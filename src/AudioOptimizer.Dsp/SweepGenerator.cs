namespace AudioOptimizer.Dsp;

using AudioOptimizer.Core;

/// <summary>
/// Exponential ("log") sweep generator — the excitation signal for room measurements.
/// </summary>
/// <remarks>
/// f(t) = f1 · exp(t/T · L)               with L = ln(f2/f1)
/// φ(t) = 2π·f1·T/L · (exp(t/T·L) - 1)    (the integral of 2π·f(t) dt, φ(0) = 0)
/// s(t) = sin(φ(t)) at t_n = n/fs, n = 0 … round(T·fs) - 1
/// A linear chirp would instead be f(t) = f1 + (f2 - f1)·t/T (midpoint 85 Hz, not √(f1·f2)).
/// </remarks>
public static class SweepGenerator
{
    /// <summary>Raised-cosine taper length at each end when taper is enabled (~5 ms).</summary>
    public const double TaperSeconds = 0.005;

    /// <param name="taper">
    /// True (default) applies a raised-cosine fade in/out so the sweep does not click at the
    /// edges; false leaves the raw samples (what the energy/phase tests want).
    /// </param>
    public static double[] GenerateExponentialSweep(SweepSettings settings, bool taper = true)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();

        int sampleCount = settings.SampleCount;
        double duration = settings.DurationSeconds;
        double logRatio = Math.Log(settings.EndHz / settings.StartHz);

        // φ(t) = phaseScale · (exp(t/T · L) - 1), phaseScale = 2π·f1·T/L
        double phaseScale = 2.0 * Math.PI * settings.StartHz * duration / logRatio;

        var samples = new double[sampleCount];
        for (int n = 0; n < sampleCount; n++)
        {
            double t = n / settings.SampleRate;
            samples[n] = Math.Sin(phaseScale * (Math.Exp(t / duration * logRatio) - 1.0));
        }

        if (taper) ApplyRaisedCosineTaper(samples, settings.SampleRate, TaperSeconds);
        return samples;
    }

    /// <summary>
    /// Tukey-style raised-cosine fade: w(i) = 0.5·(1 - cos(π·i/m)) over the first and last
    /// m = round(taperSeconds·fs) samples, so w(0) = 0 at both ends and w(m) = 1.
    /// </summary>
    private static void ApplyRaisedCosineTaper(double[] samples, double sampleRate, double taperSeconds)
    {
        int m = (int)Math.Round(taperSeconds * sampleRate);
        if (m <= 0) return;
        // ponytail: taper longer than the sweep gets clamped to half the buffer instead of throwing —
        // revisit if sub-5 ms sweeps ever become a supported measurement.
        if (2 * m >= samples.Length) m = samples.Length / 2;

        for (int i = 0; i < m; i++)
        {
            double w = 0.5 - 0.5 * Math.Cos(Math.PI * i / m);
            samples[i] *= w;
            samples[samples.Length - 1 - i] *= w;
        }
    }
}
