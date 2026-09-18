namespace AudioOptimizer.Dsp;

using System.Numerics;
using MathNet.Numerics.IntegralTransforms;

/// <summary>
/// Thin wrapper over MathNet <see cref="Fourier"/>.
/// Convention (FourierOptions.AsymmetricScaling): forward is the unscaled DFT sum
/// X[k] = Σ x[n]·e^(-j2πkn/N), inverse divides by N — so Inverse(Forward(x)) == x
/// and a full-scale sine of N samples gives |X[k0]| = N/2.
/// </summary>
public static class Fft
{
    /// <summary>In-place forward DFT, unscaled (X[k0] = N/2 for a sine at bin k0).</summary>
    public static Complex[] Forward(Complex[] spectrum)
    {
        Fourier.Forward(spectrum, FourierOptions.AsymmetricScaling);
        return spectrum;
    }

    /// <summary>In-place inverse DFT with the 1/N normalisation matching <see cref="Forward"/>.</summary>
    public static Complex[] Inverse(Complex[] spectrum)
    {
        Fourier.Inverse(spectrum, FourierOptions.AsymmetricScaling);
        return spectrum;
    }

    /// <summary>Forward DFT of a real signal; imaginary parts are discarded.</summary>
    public static Complex[] Forward(double[] samples)
    {
        var spectrum = new Complex[samples.Length];
        for (int i = 0; i < samples.Length; i++) spectrum[i] = new Complex(samples[i], 0.0);
        return Forward(spectrum);
    }

    /// <summary>Smallest power of two ≥ <paramref name="length"/> — the zero-pad target for FFT convolution.</summary>
    public static int NextPowerOfTwo(int length)
    {
        int power = 1;
        while (power < length) power <<= 1;
        return power;
    }

    /// <summary>
    /// Linear (not circular) convolution: zero-pad both inputs to ≥ a.Length + b.Length - 1,
    /// multiply the spectra, inverse transform, trim to the exact result length.
    /// </summary>
    public static double[] Convolve(double[] a, double[] b)
    {
        if (a.Length == 0 || b.Length == 0) return [];

        int resultLength = a.Length + b.Length - 1;
        int n = NextPowerOfTwo(resultLength);

        // ponytail: two padded allocations per call; hand in pre-padded arrays if this ever
        // lands in an inner loop of the optimizer.
        var spectrumA = new Complex[n];
        var spectrumB = new Complex[n];
        for (int i = 0; i < a.Length; i++) spectrumA[i] = new Complex(a[i], 0.0);
        for (int i = 0; i < b.Length; i++) spectrumB[i] = new Complex(b[i], 0.0);

        Forward(spectrumA);
        Forward(spectrumB);
        for (int k = 0; k < n; k++) spectrumA[k] *= spectrumB[k];
        var convolution = Inverse(spectrumA);

        var result = new double[resultLength];
        for (int i = 0; i < resultLength; i++) result[i] = convolution[i].Real;
        return result;
    }
}
