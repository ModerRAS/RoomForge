namespace AudioOptimizer.Tests;

using System.Numerics;
using AudioOptimizer.Dsp;

/// <summary>
/// T1: DFT convention — a single integer-cycle sine lands in exactly one bin with
/// |X[k0]| = N/2, and the inverse transform is normalised so IFFT(FFT(x)) == x.
/// </summary>
public class FftTests
{
    private const int N = 4800;           // 10 Hz at fs = 48 kHz over exactly 1/10 s → 4800 samples
    private const int K0 = 1;             // bin = f·N/fs = 10 · 4800 / 48000 = 1
    private const double HalfN = N / 2.0; // 2400 — expected |X[k0]| for a unit-amplitude sine

    private static double[] Sine() // x[n] = sin(2π·k0·n/N) = 10 Hz over exactly one tenth of a second
    {
        var x = new double[N];
        for (int n = 0; n < N; n++) x[n] = Math.Sin(2.0 * Math.PI * K0 * n / N);
        return x;
    }

    private static void AssertClose(double[] expected, double[] actual, double tolerance)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
            Assert.True(Math.Abs(expected[i] - actual[i]) <= tolerance,
                $"element {i}: {actual[i]} != {expected[i]} (tol {tolerance})");
    }

    [Fact]
    public void Integer_cycle_sine_is_one_bin_wide_with_magnitude_N_over_2()
    {
        var spectrum = Fft.Forward(Sine());

        // |X[k0]| = N/2 = 2400 exactly (integer cycle count, no window); rel tol 1e-6 → 0.0024
        Assert.Equal(HalfN, ComplexMath.Magnitude(spectrum[K0]), 1e-6 * HalfN);

        // every other bin ≈ 0: worst measured 8.9e-10 (bin 2), far below the 1e-6 absolute bound
        double worst = 0.0;
        for (int k = 0; k < N; k++)
        {
            if (k == K0 || k == N - K0) continue;   // k0 plus its conjugate image at N-k0
            worst = Math.Max(worst, ComplexMath.Magnitude(spectrum[k]));
        }
        Assert.True(worst < 1e-6, $"largest non-peak bin magnitude was {worst:E3}");

        // the negative-frequency image is the conjugate of the positive one: X[N-k0] = conj(X[k0])
        // (tol 1e-6 = the bin-noise floor: N = 4800 = 2^5·3·5² is not a power of two, so MathNet
        //  runs a mixed-radix transform and the real parts differ by ~2.6e-9 instead of 0)
        Assert.Equal(spectrum[K0].Real, spectrum[N - K0].Real, 1e-6);
        Assert.Equal(-spectrum[K0].Imaginary, spectrum[N - K0].Imaginary, 1e-6);
    }

    [Fact]
    public void Inverse_of_forward_reproduces_the_signal()
    {
        var x = Sine();
        var roundtrip = Fft.Inverse(Fft.Forward(x));

        // IFFT(FFT(x)) == x with the wrapper's asymmetric scaling (1/N on the inverse); measured 1.4e-12
        double worst = 0.0, worstImaginary = 0.0;
        for (int n = 0; n < N; n++)
        {
            worst = Math.Max(worst, (roundtrip[n] - new Complex(x[n], 0.0)).Magnitude);
            worstImaginary = Math.Max(worstImaginary, Math.Abs(roundtrip[n].Imaginary));
        }
        Assert.True(worst < 1e-10, $"roundtrip error was {worst:E3}");
        Assert.True(worstImaginary < 1e-10, $"imaginary residue was {worstImaginary:E3}");
    }

    [Fact]
    public void Next_power_of_two_rounds_up_and_never_down()
    {
        Assert.Equal(1, Fft.NextPowerOfTwo(1));     // already a power of two
        Assert.Equal(8, Fft.NextPowerOfTwo(8));     // exact powers stay put
        Assert.Equal(16, Fft.NextPowerOfTwo(9));    // 9 → 16
        Assert.Equal(8192, Fft.NextPowerOfTwo(4097)); // 4800-sample convolution pads to 8192
    }

    [Fact]
    public void Convolution_matches_the_analytic_product()
    {
        // (1 + 2x + 3x²)·(x + 0.5x²) = x + 2.5x² + 4x³ + 1.5x⁴  →  [0, 1, 2.5, 4, 1.5]
        AssertClose([0.0, 1.0, 2.5, 4.0, 1.5], Fft.Convolve([1.0, 2.0, 3.0], [0.0, 1.0, 0.5]), 1e-12);

        // linear (not circular): (1 + x)·(1 + x + x²) = 1 + 2x + 2x² + x³
        AssertClose([1.0, 2.0, 2.0, 1.0], Fft.Convolve([1.0, 1.0], [1.0, 1.0, 1.0]), 1e-12);
    }

    [Fact]
    public void Empty_convolution_input_is_empty()
    {
        Assert.Empty(Fft.Convolve([], [1.0, 2.0]));
    }

    [Fact]
    public void Generated_sine_lands_with_the_theory_phase_in_its_bin()
    {
        // Real samples, not hand-built complex numbers. With the wrapper's unscaled forward DFT,
        //   X[k] = Σ x[n]·e^(−j2πkn/N).
        // Write the sine as sin θ = (e^(jθ) − e^(−jθ))/(2j) with θ = 2πk0n/N + φ0. The positive-frequency
        // term telescopes: Σ e^(j(2πk0n/N + φ0))·e^(−j2πk0n/N) = e^(jφ0)·N, and 1/(2j) = (1/2)e^(−jπ/2), so
        //   X[k0] = (N/2)·e^(j(φ0 − π/2))       → |X[k0]| = N/2, phase = φ0 − π/2,
        // and the conjugate image is X[N−k0] = conj(X[k0]) = (N/2)·e^(−j(φ0 − π/2)).
        // This is independent of the FFT implementation: it follows from the sine's Fourier coefficients alone.
        const int n = 256, k0 = 16;
        const double phi0 = 0.3;
        var x = new double[n];
        for (int i = 0; i < n; i++) x[i] = Math.Sin(2.0 * Math.PI * k0 * i / n + phi0);

        var spectrum = Fft.Forward(x);
        double positivePhase = phi0 - Math.PI / 2.0;

        Assert.Equal(n / 2.0, ComplexMath.Magnitude(spectrum[k0]), 1e-9);
        Assert.Equal(positivePhase, ComplexMath.Phase(spectrum[k0]), 1e-9);
        Assert.Equal(-positivePhase, ComplexMath.Phase(spectrum[n - k0]), 1e-9);
    }

    [Fact]
    public void Generated_cosine_lands_with_the_signal_phase_in_its_bin()
    {
        // cos θ = (e^(jθ) + e^(−jθ))/2, so the same telescoping sum gives X[k0] = (N/2)·e^(jφ0) directly —
        // no −π/2 rotation, unlike the sine — and X[N−k0] = conj(X[k0]).
        const int n = 256, k0 = 16;
        const double phi0 = 0.3;
        var x = new double[n];
        for (int i = 0; i < n; i++) x[i] = Math.Cos(2.0 * Math.PI * k0 * i / n + phi0);

        var spectrum = Fft.Forward(x);

        Assert.Equal(n / 2.0, ComplexMath.Magnitude(spectrum[k0]), 1e-9);
        Assert.Equal(phi0, ComplexMath.Phase(spectrum[k0]), 1e-9);
        Assert.Equal(-phi0, ComplexMath.Phase(spectrum[n - k0]), 1e-9);
    }
}
