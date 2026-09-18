namespace AudioOptimizer.Dsp;

using System.Numerics;
using AudioOptimizer.Core;

/// <summary>
/// IR → complex frequency response. The already-cropped impulse response is windowed, zero-padded to
/// a power of two, transformed, and returned as bins k = 0 … N/2 at f_k = k·fs/N.
/// Magnitudes are divided by the window's coherent gain (mean of the window, i.e. Rectangular 1.0,
/// periodic Hann 0.5), so the reported level does not change when the analysis window changes.
/// Phase is relative to Samples[0] of the impulse response; the absolute delay lives in
/// <see cref="ImpulseResponse.AbsolutePeakIndex"/>.
/// </summary>
public static class FrequencyResponseCalculator
{
    public static FrequencyResponse[] Compute(ImpulseResponse impulseResponse, WindowType windowType, int? fftSize = null)
    {
        ArgumentNullException.ThrowIfNull(impulseResponse);
        double[] samples = impulseResponse.Samples;
        if (samples.Length == 0) throw new ArgumentException("Impulse response has no samples.", nameof(impulseResponse));

        double[] window = WindowFunctions.Create(windowType, samples.Length);
        int n = fftSize ?? Fft.NextPowerOfTwo(samples.Length);
        if (n < samples.Length) throw new ArgumentOutOfRangeException(nameof(fftSize), n, "fftSize must be >= the impulse response length (no truncation).");

        var buffer = new Complex[n];
        double windowSum = 0.0;
        for (int i = 0; i < samples.Length; i++)
        {
            buffer[i] = new Complex(samples[i] * window[i], 0.0);
            windowSum += window[i];
        }
        double coherentGain = windowSum / samples.Length;
        Fft.Forward(buffer);

        int binCount = n / 2 + 1;
        var real = new double[binCount];
        var imaginary = new double[binCount];
        var magnitudeDb = new double[binCount];
        var wrapped = new double[binCount];

        for (int k = 0; k < binCount; k++)
        {
            Complex value = buffer[k] / coherentGain;
            real[k] = value.Real;
            imaginary[k] = value.Imaginary;
            magnitudeDb[k] = ComplexMath.LinearToDb(ComplexMath.Magnitude(value));
            wrapped[k] = ComplexMath.Phase(value);
        }

        double[] unwrapped = ComplexMath.Unwrap(wrapped);
        double binHz = (double)impulseResponse.SampleRate / n;

        var response = new FrequencyResponse[binCount];
        for (int k = 0; k < binCount; k++)
            response[k] = new FrequencyResponse(k * binHz, real[k], imaginary[k], magnitudeDb[k], wrapped[k], unwrapped[k]);
        return response;
    }
}
