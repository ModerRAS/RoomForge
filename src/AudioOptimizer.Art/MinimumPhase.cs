namespace AudioOptimizer.Art;

using System.Numerics;
using AudioOptimizer.Dsp;

/// <summary>
/// Minimum-phase / excess-phase split via the real cepstrum.
/// H = |H| · exp(j φ_min) · exp(j φ_excess), with φ_min from the causal cepstral window.
/// </summary>
public static class MinimumPhaseDecomposition
{
    public const string Method = PhaseCalibrationOptions.Decomposition;

    public static PhaseDecomposition Decompose(double[] impulse, int sampleRate, int fftSize = 0)
    {
        ArgumentNullException.ThrowIfNull(impulse);
        if (impulse.Length == 0) throw new ArtValidationException("Impulse response is empty.");
        if (sampleRate <= 0) throw new ArtValidationException("Sample rate must be positive.");

        int n = fftSize == 0 ? ArtFourier.PowerOfTwoAtLeast(Math.Max(impulse.Length * 4, 2048)) : fftSize;
        if (n < impulse.Length || (n & (n - 1)) != 0)
            throw new ArtValidationException($"FFT size {n} must be a power of two at least as long as the impulse.");

        Complex[] spectrum = ArtFourier.ForwardReal(impulse, n);
        Complex[] minimum = MinimumPhaseSpectrum(spectrum);
        int bins = n / 2 + 1;
        var frequency = new double[bins];
        var magnitude = new double[bins];
        var minWrapped = new double[bins];
        var obsWrapped = new double[bins];
        for (int k = 0; k < bins; k++)
        {
            frequency[k] = k * (double)sampleRate / n;
            magnitude[k] = spectrum[k].Magnitude;
            minWrapped[k] = ComplexMath.Phase(minimum[k]);
            obsWrapped[k] = ComplexMath.Phase(spectrum[k]);
        }

        double[] minUnwrapped = ComplexMath.Unwrap(minWrapped);
        double[] obsUnwrapped = ComplexMath.Unwrap(obsWrapped);
        var excess = new double[bins];
        for (int k = 0; k < bins; k++) excess[k] = obsUnwrapped[k] - minUnwrapped[k];

        return new PhaseDecomposition
        {
            FftSize = n,
            SampleRate = sampleRate,
            FrequencyHz = frequency,
            Magnitude = magnitude,
            MinimumPhaseRad = minUnwrapped,
            ExcessPhaseRad = excess,
        };
    }

    /// <summary>Minimum-phase spectrum whose magnitude matches <paramref name="magnitude"/> (full Hermitian FFT).</summary>
    public static Complex[] SpectrumFromMagnitude(double[] magnitude)
    {
        ArgumentNullException.ThrowIfNull(magnitude);
        int n = magnitude.Length;
        if (n < 2 || (n & (n - 1)) != 0)
            throw new ArtValidationException("Magnitude FFT size must be a power of two.");

        var logMag = new Complex[n];
        for (int k = 0; k < n; k++)
        {
            double mag = magnitude[k];
            if (mag < 0 || !double.IsFinite(mag))
                throw new ArtValidationException("Magnitude bins must be finite and non-negative.");
            logMag[k] = Math.Log(Math.Max(mag, 1e-20));
        }

        return ExponentiateCausalCepstrum(logMag);
    }

    static Complex[] MinimumPhaseSpectrum(Complex[] spectrum)
    {
        double peak = 0;
        for (int k = 0; k < spectrum.Length; k++) peak = Math.Max(peak, spectrum[k].Magnitude);
        double floor = Math.Max(peak, 1e-12) * 1e-12;
        var logMag = new Complex[spectrum.Length];
        for (int k = 0; k < spectrum.Length; k++)
            logMag[k] = Math.Log(Math.Max(spectrum[k].Magnitude, floor));
        return ExponentiateCausalCepstrum(logMag);
    }

    /// <summary>
    /// Real cepstrum → causal window (1 at DC, 2 on the positive quefrencies, 1 at Nyquist) → exp(FFT).
    /// </summary>
    static Complex[] ExponentiateCausalCepstrum(Complex[] logMagnitude)
    {
        int n = logMagnitude.Length;
        var cepstrum = (Complex[])logMagnitude.Clone();
        Fft.Inverse(cepstrum);

        var causal = new Complex[n];
        causal[0] = new Complex(cepstrum[0].Real, 0);
        for (int i = 1; i < n / 2; i++) causal[i] = new Complex(2.0 * cepstrum[i].Real, 0);
        causal[n / 2] = new Complex(cepstrum[n / 2].Real, 0);

        Fft.Forward(causal);
        var minimum = new Complex[n];
        for (int k = 0; k < n; k++) minimum[k] = Complex.Exp(causal[k]);
        return minimum;
    }
}

/// <summary>
/// Nonlinear excess: RMS of unwrapped excess phase after a best-fit line in frequency is removed.
/// A pure delay scores ~0. An all-pass does not. A minimum-phase magnitude EQ does not remove it.
/// </summary>
public static class ExcessMetrics
{
    public static double Measure(double[] impulse, int sampleRate, double lowHz, double highHz, int fftSize = 0)
    {
        PhaseDecomposition decomposition = MinimumPhaseDecomposition.Decompose(impulse, sampleRate, fftSize);
        return NonlinearRms(decomposition, lowHz, highHz).Rms;
    }

    public static ExcessBand NonlinearRms(PhaseDecomposition decomposition, double lowHz, double highHz)
    {
        ArgumentNullException.ThrowIfNull(decomposition);
        int bins = decomposition.FrequencyHz.Length;
        double maxMag = 0;
        for (int i = 0; i < bins; i++)
        {
            double hz = decomposition.FrequencyHz[i];
            if (hz >= lowHz && hz <= highHz) maxMag = Math.Max(maxMag, decomposition.Magnitude[i]);
        }

        double floor = maxMag * 1e-5;
        var frequencies = new List<double>();
        var values = new List<double>();
        for (int i = 0; i < bins; i++)
        {
            double hz = decomposition.FrequencyHz[i];
            if (hz < lowHz || hz > highHz) continue;
            if (decomposition.Magnitude[i] < floor) continue;
            frequencies.Add(hz);
            values.Add(decomposition.ExcessPhaseRad[i]);
        }

        if (frequencies.Count < 8)
            throw new ArtValidationException("Excess metric band does not contain enough bins.");

        FitLine(frequencies, values, out double intercept, out double slope);
        var residual = new double[frequencies.Count];
        double energy = 0;
        for (int i = 0; i < frequencies.Count; i++)
        {
            residual[i] = values[i] - (intercept + slope * frequencies[i]);
            energy += residual[i] * residual[i];
        }

        return new ExcessBand
        {
            FrequencyHz = [.. frequencies],
            ResidualRad = residual,
            Rms = Math.Sqrt(energy / frequencies.Count),
        };
    }

    static void FitLine(IReadOnlyList<double> x, IReadOnlyList<double> y, out double intercept, out double slope)
    {
        double n = x.Count;
        double sx = 0, sy = 0, sxx = 0, sxy = 0;
        for (int i = 0; i < x.Count; i++)
        {
            sx += x[i];
            sy += y[i];
            sxx += x[i] * x[i];
            sxy += x[i] * y[i];
        }

        double det = n * sxx - sx * sx;
        if (Math.Abs(det) < 1e-18)
        {
            intercept = sy / n;
            slope = 0;
            return;
        }

        slope = (n * sxy - sx * sy) / det;
        intercept = (sy - slope * sx) / n;
    }
}

public sealed class ExcessBand
{
    public double[] FrequencyHz { get; init; } = [];

    public double[] ResidualRad { get; init; } = [];

    public double Rms { get; init; }
}

/// <summary>
/// Magnitude-only minimum-phase EQ. This is the contrast path for phase calibration:
/// it can flatten |H| and still leave excess phase in place.
/// </summary>
public static class MinimumPhaseEqualizer
{
    public static double[] InvertMagnitude(double[] impulse, int fftSize, double relativeFloor = 1e-4)
    {
        ArgumentNullException.ThrowIfNull(impulse);
        if (fftSize < impulse.Length || (fftSize & (fftSize - 1)) != 0)
            throw new ArtValidationException("Equalizer FFT size must be a power of two at least as long as the impulse.");
        if (relativeFloor <= 0) throw new ArtValidationException("Relative floor must be positive.");

        Complex[] spectrum = ArtFourier.ForwardReal(impulse, fftSize);
        double peak = 0;
        for (int k = 0; k < fftSize; k++) peak = Math.Max(peak, spectrum[k].Magnitude);
        double floor = Math.Max(peak, 1e-12) * relativeFloor;
        var desired = new double[fftSize];
        for (int k = 0; k < fftSize; k++) desired[k] = 1.0 / Math.Max(spectrum[k].Magnitude, floor);

        Complex[] minimum = MinimumPhaseDecomposition.SpectrumFromMagnitude(desired);
        return ArtFourier.InverseReal(minimum);
    }
}
