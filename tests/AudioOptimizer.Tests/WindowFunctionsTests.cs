namespace AudioOptimizer.Tests;

using AudioOptimizer.Core;
using AudioOptimizer.Dsp;

/// <summary>
/// Window shapes. Hann and Tukey are built in the PERIODIC form w[n] = 0.5 − 0.5·cos(2πn/N)
/// (denominator N, not N−1): an analysis window is implicitly repeated every FFT frame, so the
/// periodic form continues w[N−1] → w[0] without a step, and its coherent gain is exactly 0.5.
/// The symmetric form (denominator N−1, meant for filter design) would sum to (N−1)/2 = 31.5 instead
/// of 32 for N = 64 — that difference is asserted below.
/// </summary>
public class WindowFunctionsTests
{
    private const int N = 64;

    [Theory]
    [InlineData(WindowType.Rectangular, 1.0)]     // mean of a constant 1 window = 1.0 exactly
    [InlineData(WindowType.Hann, 0.5)]           // Σ(0.5 − 0.5cos(2πn/N))/N = 0.5 because Σcos(2πn/N) = 0
    public void Coherent_gain_matches_the_expected_value(WindowType type, double expected)
    {
        double sum = WindowFunctions.Create(type, N).Sum();
        Assert.Equal(expected * N, sum, 1e-12);
        Assert.Equal(expected, sum / N, 1e-12);
    }

    [Theory]
    [InlineData(0.0, 1.0)]      // α = 0 → no taper at all → rectangular: gain 1.0
    [InlineData(0.5, 0.75)]     // α = 0.5 → taper occupies half the window → gain 1 − α/2
    [InlineData(1.0, 0.5)]      // α = 1 → full Hann shape → gain 0.5
    public void Tukey_coherent_gain_follows_one_minus_alpha_over_two(double alpha, double expected)
    {
        double mean = WindowFunctions.Create(WindowType.Tukey, N, alpha).Average();
        Assert.Equal(expected, mean, 1e-12);
    }

    [Fact]
    public void Tukey_end_points_degenerate_to_rectangular_and_hann()
    {
        double[] rectangular = WindowFunctions.Create(WindowType.Rectangular, N);
        double[] hann = WindowFunctions.Create(WindowType.Hann, N);
        double[] tukeyZero = WindowFunctions.Create(WindowType.Tukey, N, 0.0);
        double[] tukeyOne = WindowFunctions.Create(WindowType.Tukey, N, 1.0);

        // α = 0 → every sample 1.0 (max difference 0)
        for (int i = 0; i < N; i++) Assert.Equal(rectangular[i], tukeyZero[i], 1e-6);
        // α = 1 → exactly the periodic Hann (measured max difference 3.33e-16, i.e. machine precision)
        for (int i = 0; i < N; i++) Assert.Equal(hann[i], tukeyOne[i], 1e-6);
    }

    [Fact]
    public void Tukey_half_alpha_is_unity_in_the_interior()
    {
        // taper length per side = floor(α·N/2) = floor(0.5·64/2) = 16, so samples 16…47 are exactly 1.0
        double[] window = WindowFunctions.Create(WindowType.Tukey, N, 0.5);
        for (int i = 16; i < N - 16; i++) Assert.Equal(1.0, window[i], 1e-15);
        // and the ends ramp from 0 up to 1: w[0] = 0.5(1−cos 0) = 0
        Assert.Equal(0.0, window[0], 1e-15);
        Assert.True(window[1] < window[2] && window[2] < window[3], "the taper must rise monotonically");
    }

    [Fact]
    public void Periodic_hann_is_not_the_symmetric_hann()
    {
        double[] window = WindowFunctions.Create(WindowType.Hann, N);

        // periodic: Σ w = N/2 = 32 exactly, so the coherent gain is exactly 0.5. The symmetric form
        // (denominator N−1) sums to (N−1)/2 = 31.5 — a 0.0156 coherent-gain bias — so this assertion
        // is what pins the periodic choice.
        Assert.Equal(N / 2.0, window.Sum(), 1e-12);

        // periodic Hann is symmetric about N/2: w[n] = w[N−n], w[0] = 0, w[N/2] = 1
        for (int i = 1; i < N / 2; i++) Assert.Equal(window[i], window[N - i], 1e-15);
        Assert.Equal(0.0, window[0], 1e-15);
        Assert.Equal(1.0, window[N / 2], 1e-15);
    }

    [Fact]
    public void Apply_in_place_matches_multiplication_by_the_window()
    {
        double[] signal = [1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 7.0, 8.0];
        double[] window = WindowFunctions.Create(WindowType.Tukey, signal.Length, 0.5);
        double[] expected = signal.Select((v, i) => v * window[i]).ToArray();

        double[] actual = (double[])signal.Clone();
        WindowFunctions.ApplyInPlace(actual, WindowType.Tukey, 0.5);

        for (int i = 0; i < signal.Length; i++) Assert.Equal(expected[i], actual[i], 1e-15);
    }

    [Fact]
    public void Invalid_arguments_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WindowFunctions.Create(WindowType.Hann, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => WindowFunctions.Create(WindowType.Hann, -4));
        Assert.Throws<ArgumentOutOfRangeException>(() => WindowFunctions.Create(WindowType.Tukey, N, -0.1));
        Assert.Throws<ArgumentOutOfRangeException>(() => WindowFunctions.Create(WindowType.Tukey, N, 1.1));
    }
}
