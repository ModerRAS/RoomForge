namespace AudioOptimizer.Dsp;

using AudioOptimizer.Core;

/// <summary>
/// Farina exponential-sweep (ESS) inverse filter.
/// </summary>
/// <remarks>
/// Stationary phase: the sweep's own spectrum has |S(f)| ∝ f^(-1/2), because the phase curvature at
/// the stationary point is φ''(t_f) = 2π·f·L/T with L = ln(f2/f1). A flat deconvolution therefore
/// needs |I(f)| ∝ f^(+1/2), which is obtained by time-reversing the sweep and multiplying by the
/// envelope f(t)/f1 = exp(t/T·L) built in FORWARD time and reversed together with the sweep, so the
/// envelope weight landing on frequency f is the one belonging to f.
/// Getting the reversal order backwards (or dropping the envelope) leaves a monotonic tilt across
/// the band instead of a flat response — see InverseFilterTests for the measured numbers.
/// </remarks>
public static class InverseFilter
{
    /// <summary>
    /// Inverse filter for <paramref name="settings"/>; same length as the excitation sweep.
    /// The excitation itself keeps its raised-cosine taper (that is the signal that gets played and
    /// recorded); the inverse is built from the untapered analytic sweep.
    /// </summary>
    public static double[] BuildExponentialInverseSweep(SweepSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();

        double[] excitation = SweepGenerator.GenerateExponentialSweep(settings);            // tapered
        double[] analytic = SweepGenerator.GenerateExponentialSweep(settings, taper: false); // analytic law
        int n = excitation.Length;
        double logRatio = Math.Log(settings.EndHz / settings.StartHz);

        var inverse = new double[n];
        for (int i = 0; i < n; i++)
        {
            int forward = n - 1 - i;                                                       // reversed position
            double t = (double)forward / settings.SampleRate;
            double envelope = Math.Exp(t / settings.DurationSeconds * logRatio);            // f(t)/f1, reversed with the sweep
            inverse[i] = analytic[forward] * envelope;
        }

        // Normalisation constant: the arithmetic mean of |H(f)| over the sweep band of the
        // unity-gain deconvolution (excitation ⊛ inverse, rectangular window), i.e. the inverse is
        // divided by that mean so that a unity-gain recording reports 0.0 dB in band — the level the
        // deconvolved IR must have before it can be compared against an analytic transfer function.
        // Rejected alternative: the zero-lag correlation Σ s[n]·i[n] (IR peak exactly 1.0). Under the
        // unscaled-DFT convention used by FrequencyResponseCalculator, a band-limited impulse with
        // in-band magnitude 1.0 has a peak of (band bins)/N ≈ 0.0027, so peak-normalising would put
        // the reported level ≈ 51 dB above unity and break the offline comparison.
        // ponytail: costs one extra FFT convolution (~10 ms at 48 kHz/1 s) per call; measure the
        // stationary-phase closed form |S(f)|² = T/(f·L) instead if this ever runs in a loop.
        double scale = UnityGainBandMeanMagnitude(excitation, inverse, settings);
        for (int i = 0; i < n; i++) inverse[i] /= scale;
        return inverse;
    }

    private static double UnityGainBandMeanMagnitude(double[] excitation, double[] inverse, SweepSettings settings)
    {
        double[] unityGainIr = Fft.Convolve(excitation, inverse);
        var impulseResponse = new ImpulseResponse(unityGainIr, (int)settings.SampleRate);
        FrequencyResponse[] response = FrequencyResponseCalculator.Compute(impulseResponse, WindowType.Rectangular);

        double sum = 0.0;
        int count = 0;
        foreach (FrequencyResponse bin in response)
        {
            if (bin.FrequencyHz < settings.StartHz || bin.FrequencyHz > settings.EndHz) continue;
            sum += ComplexMath.DbToLinear(bin.MagnitudeDb);
            count++;
        }
        if (count == 0) throw new InvalidOperationException("No frequency bins inside the sweep band; check SweepSettings and the sample rate.");
        return sum / count;
    }
}
