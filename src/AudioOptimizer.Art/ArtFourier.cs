namespace AudioOptimizer.Art;

using System.Numerics;
using AudioOptimizer.Dsp;

/// <summary>Real FFT helpers shared by the extension-track designers. Same convention as <see cref="Fft"/>.</summary>
static class ArtFourier
{
    public static int PowerOfTwoAtLeast(int length) => Fft.NextPowerOfTwo(Math.Max(length, 1));

    public static Complex[] ForwardReal(ReadOnlySpan<double> samples, int n)
    {
        if (samples.Length > n)
            throw new ArgumentException($"Signal length {samples.Length} exceeds FFT size {n}.", nameof(samples));

        var spectrum = new Complex[n];
        for (int i = 0; i < samples.Length; i++) spectrum[i] = samples[i];
        return Fft.Forward(spectrum);
    }

    public static double[] InverseReal(Complex[] spectrum)
    {
        var buffer = (Complex[])spectrum.Clone();
        Fft.Inverse(buffer);
        var samples = new double[buffer.Length];
        for (int i = 0; i < buffer.Length; i++) samples[i] = buffer[i].Real;
        return samples;
    }
}
