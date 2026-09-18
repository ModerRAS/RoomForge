namespace AudioOptimizer.Tests;

using AudioOptimizer.Core;
using AudioOptimizer.Dsp;
using Xunit.Abstractions;

/// <summary>
/// IR → complex frequency response. Phase is relative to Samples[0]; the absolute delay is carried by
/// <see cref="ImpulseResponse.AbsolutePeakIndex"/>. Magnitudes are divided by the window coherent gain.
/// </summary>
public class FrequencyResponseTests(ITestOutputHelper output)
{
    [Fact]
    public void Delta_at_zero_is_zero_db_with_zero_phase()
    {
        // h = δ[0] → H(f) = e^(−j2πf·0/fs) = 1 → 20·log10(1) = 0.0 dB and arg H = 0.0 rad for every bin
        var samples = new double[64];
        samples[0] = 1.0;

        FrequencyResponse[] response = FrequencyResponseCalculator.Compute(new ImpulseResponse(samples, 48000), WindowType.Rectangular);

        Assert.Equal(64 / 2 + 1, response.Length);            // bins k = 0 … N/2 inclusive
        Assert.Equal(0.0, response[5].FrequencyHz - 5 * 48000.0 / 64, 1e-12);   // f_k = k·fs/N = 5·750 = 3750 Hz
        for (int k = 0; k < response.Length; k++)
        {
            Assert.Equal(0.0, response[k].MagnitudeDb, 1e-9);
            Assert.Equal(0.0, response[k].PhaseWrappedRad, 1e-9);
            Assert.Equal(0.0, response[k].PhaseUnwrappedRad, 1e-9);
            Assert.Equal(1.0, response[k].Real, 1e-12);
        }
    }

    [Fact]
    public void Delta_at_d_has_a_linear_phase_slope()
    {
        // h = δ[d] → H(f) = e^(−j2πf·d/fs); the wrapped phase is −2π·d·k/N at bin k, e.g. for d = 5, N = 64:
        // bin 5 → −2π·5·5/64 = −2.454369260617 rad; bin 32 (Nyquist) → −2π·5·32/64 = −5π = −15.707963
        const int d = 5;
        var samples = new double[64];
        samples[d] = 1.0;

        FrequencyResponse[] response = FrequencyResponseCalculator.Compute(new ImpulseResponse(samples, 48000), WindowType.Rectangular);

        for (int k = 0; k <= 4; k++)
        {
            double expected = -2.0 * Math.PI * d * k / 64.0;
            Assert.Equal(expected, response[k].PhaseWrappedRad, 1e-12);
            Assert.Equal(expected, response[k].PhaseUnwrappedRad, 1e-12);
            Assert.Equal(Math.Cos(expected), response[k].Real, 1e-12);   // real/imag are the source of truth
            Assert.Equal(Math.Sin(expected), response[k].Imag, 1e-12);
            Assert.Equal(0.0, response[k].MagnitudeDb, 1e-9);
        }

        Assert.Equal(-2.0 * Math.PI * d * 32 / 64.0, response[32].PhaseUnwrappedRad, 1e-12);
        // the unwrapped phase of a pure delay decreases monotonically (measured: strictly decreasing
        // over k = 1 … 32), which is exactly what the wrapped atan2 sequence cannot show — atan2 wraps
        // at ±π around bin 6 for d = 5.
        for (int k = 1; k <= 32; k++)
            Assert.True(response[k].PhaseUnwrappedRad < response[k - 1].PhaseUnwrappedRad,
                $"unwrapped phase must decrease: bin {k} = {response[k].PhaseUnwrappedRad} vs bin {k - 1} = {response[k - 1].PhaseUnwrappedRad}");
    }

    [Fact]
    public void Coherent_gain_normalisation_makes_the_level_window_independent()
    {
        // An integer-cycle tone lands exactly on a bin: |X[k0]| = Σw/2 because the tone's transform is
        // convolved with the window transform's main lobe. Dividing by the coherent gain Σw/N gives
        // |H[k0]| = N/2 = 2048 for EVERY window shape — Rectangular, Hann, and Tukey at any α.
        // (Measured: 66.226599046076 dB = 20·log10(2048) for all five windows, machine precision.)
        // Note this is a *level* invariance: comparing per-bin magnitudes of a truncated IR instead
        // would differ by up to 6.5 dB, because the crop's hard edges leak (the reference pulse's 1/t
        // tails are still ~12 % of the peak 21 ms away) — that is leakage, not coherent gain.
        const int n = 4096;   // power of two, so the FFT size equals the IR length and bin 100 is exact
        var samples = new double[n];
        for (int i = 0; i < n; i++) samples[i] = Math.Sin(2.0 * Math.PI * 100 * i / n);   // 100 cycles

        double expectedDb = 20.0 * Math.Log10(n / 2.0);
        foreach (var (type, alpha) in new[]
        {
            (WindowType.Rectangular, 0.0), (WindowType.Hann, 0.0),
            (WindowType.Tukey, 0.0), (WindowType.Tukey, 0.5), (WindowType.Tukey, 1.0),
        })
        {
            FrequencyResponse[] response = FrequencyResponseCalculator.Compute(new ImpulseResponse(samples, 48000), type, n);
            Assert.Equal(100 * 48000.0 / 4096, response[100].FrequencyHz, 1e-9);   // bin 100 = 1171.875 Hz
            Assert.Equal(expectedDb, response[100].MagnitudeDb, 1e-9);
        }
    }

    [Fact]
    public void Fft_size_defaults_to_the_next_power_of_two_and_is_validated()
    {
        var samples = new double[3000];
        samples[1500] = 1.0;

        FrequencyResponse[] byDefault = FrequencyResponseCalculator.Compute(new ImpulseResponse(samples, 48000), WindowType.Hann);
        Assert.Equal(4096 / 2 + 1, byDefault.Length);                          // NextPowerOfTwo(3000) = 4096
        Assert.Equal(48000.0 / 4096, byDefault[1].FrequencyHz, 1e-12);          // bin spacing fs/N

        FrequencyResponse[] explicitSize = FrequencyResponseCalculator.Compute(new ImpulseResponse(samples, 48000), WindowType.Hann, 8192);
        Assert.Equal(8192 / 2 + 1, explicitSize.Length);

        Assert.Throws<ArgumentOutOfRangeException>(() => FrequencyResponseCalculator.Compute(new ImpulseResponse(samples, 48000), WindowType.Hann, 1024));
        Assert.Throws<ArgumentException>(() => FrequencyResponseCalculator.Compute(new ImpulseResponse([], 48000), WindowType.Hann));
    }

    [Fact]
    public void Magnitude_and_phase_agree_with_the_complex_bin()
    {
        // H(f) = 3 + 4·e^(−j2πf/fs). At bin k = 16 of N = 64 the phase is −2π·16/64 = −π/2, so
        // H = 3 − 4j: |H| = √(3²+4²) = 5 → 20·log10(5) = 13.979400086720375 dB,
        // arg H = atan2(−4, 3) = −0.9272952180016122 rad
        var samples = new double[64];
        samples[0] = 3.0;
        samples[1] = 4.0;

        FrequencyResponse[] response = FrequencyResponseCalculator.Compute(new ImpulseResponse(samples, 48000), WindowType.Rectangular);
        Assert.Equal(0.0, response[16].FrequencyHz - 16 * 48000.0 / 64, 1e-12);   // bin 16 → 12000 Hz
        Assert.Equal(3.0, response[16].Real, 1e-12);
        Assert.Equal(-4.0, response[16].Imag, 1e-12);
        Assert.Equal(13.979400086720375, response[16].MagnitudeDb, 1e-9);
        Assert.Equal(-Math.Atan2(4.0, 3.0), response[16].PhaseWrappedRad, 1e-12);
        Assert.Equal(5.0, ComplexMath.DbToLinear(response[16].MagnitudeDb), 1e-12);
        output.WriteLine($"H(12000 Hz) = {response[16].Real} + {response[16].Imag}j → {response[16].MagnitudeDb:F6} dB, {response[16].PhaseWrappedRad:F6} rad");
    }
}
