namespace AudioOptimizer.Dsp;

/// <summary>
/// Shared spectrum readout for the IMD / multi-tone analyzers: one periodic-Hann
/// FFT per capture, then per-target peak search with parabolic interpolation.
/// Levels are peak amplitudes with 0 dBFS = peak 1.0 (a full-scale sine reads
/// 20·log10(A)); the window's coherent gain is compensated so coherent and
/// non-coherent tones read the same within interpolation error.
/// </summary>
internal static class SpectrumPeak
{
    /// <summary>Magnitudes for bins 0..N/2 of the periodic-Hann-windowed capture.</summary>
    internal static double[] HannMagnitudes(double[] capture, double sampleRate, out double binHz)
    {
        int n = capture.Length;
        binHz = sampleRate / n;
        var windowed = new System.Numerics.Complex[n];
        for (int i = 0; i < n; i++)
        {
            double w = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / n); // periodic Hann, mean 0.5
            windowed[i] = new System.Numerics.Complex(capture[i] * w, 0.0);
        }
        Fft.Forward(windowed);
        var mags = new double[n / 2 + 1];
        for (int k = 0; k < mags.Length; k++) mags[k] = windowed[k].Magnitude;
        return mags;
    }

    /// <summary>
    /// Peak amplitude (linear, peak 1.0 = 0 dBFS) of the strongest bin within
    /// ±<paramref name="searchHz"/> of <paramref name="targetHz"/>, parabolically
    /// interpolated. Returns 0 when the spectrum is silent there.
    /// </summary>
    internal static double AmplitudeAt(double[] mags, double binHz, double targetHz, double searchHz)
        => MeasureAt(mags, binHz, targetHz, searchHz).Amplitude;

    /// <summary>Interpolated frequency and peak amplitude of the strongest bin near the target.</summary>
    internal static (double FrequencyHz, double Amplitude) MeasureAt(
        double[] mags, double binHz, double targetHz, double searchHz)
    {
        int n = (mags.Length - 1) * 2;
        int centre = (int)Math.Round(targetHz / binHz);
        int half = Math.Max(1, (int)Math.Ceiling(searchHz / binHz));
        int lo = Math.Max(0, centre - half);
        int hi = Math.Min(mags.Length - 1, centre + half);

        int best = lo;
        for (int k = lo + 1; k <= hi; k++)
            if (mags[k] > mags[best]) best = k;

        double delta = 0.0;
        if (best > 0 && best < mags.Length - 1)
        {
            double m1 = mags[best - 1], m2 = mags[best], m3 = mags[best + 1];
            double denom = m1 - 2.0 * m2 + m3;
            if (denom != 0.0)
                delta = Math.Clamp(0.5 * (m1 - m3) / denom, -0.5, 0.5);
        }

        double refined = mags[best];
        if (delta != 0.0)
        {
            double m1 = mags[best - 1], m3 = mags[best + 1];
            refined = mags[best] - 0.25 * (m1 - m3) * delta;
        }

        double amplitude = refined * 4.0 / n; // Hann CG 0.5: A = |X|·2/(N·0.5)
        return ((best + delta) * binHz, amplitude);
    }

    internal static double ToDbFs(double amplitudePeak)
        => amplitudePeak <= 0.0 ? double.NegativeInfinity : 20.0 * Math.Log10(amplitudePeak);
}
