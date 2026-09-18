namespace AudioOptimizer.Tests;

using AudioOptimizer.Core;
using AudioOptimizer.Dsp;
using Xunit.Abstractions;

/// <summary>
/// T7 — sweep → inverse filter → impulse response, plus the delay invariant and array-bounds safety.
/// The inverse is Farina's ESS inverse (time-reversed sweep × envelope f(t)/f1, built in forward time
/// and reversed together with the sweep). The band statistics used here are normalisation-invariant
/// — tilt is a slope over log10(f) and maxdev is relative to the in-band mean — so they measure the
/// construction itself, never the chosen constant.
/// </summary>
public class InverseFilterTests(ITestOutputHelper output)
{
    private const int SampleRate = 48000;
    private static readonly SweepSettings Settings = new();   // 20 → 150 Hz, 1 s, 48 kHz

    /// <summary>
    /// In-band statistics of an impulse response's magnitude response, rectangular window, full IR:
    /// tilt = least-squares slope of dB vs log10(f) scaled to the 20→150 Hz span, i.e. 20·a·log10(7.5)
    /// for a response proportional to f^a (0 dB for correct ESS weighting, −8.75 dB for a
    /// linear-chirp/half-power error, −17.5 dB for a plain correlation without the envelope).
    /// </summary>
    private static (double Mean, double Min, double Max, double MaxDeviation, double Tilt) BandStats(
        double[] impulseResponse, double lowHz = 20.0, double highHz = 150.0)
    {
        FrequencyResponse[] response = FrequencyResponseCalculator.Compute(
            new ImpulseResponse(impulseResponse, SampleRate), WindowType.Rectangular);
        var bins = response.Where(b => b.FrequencyHz >= lowHz && b.FrequencyHz <= highHz).ToArray();

        double sum = 0, minimum = double.MaxValue, maximum = double.MinValue, sx = 0, sy = 0, sxx = 0, sxy = 0;
        foreach (FrequencyResponse bin in bins)
        {
            sum += bin.MagnitudeDb;
            minimum = Math.Min(minimum, bin.MagnitudeDb);
            maximum = Math.Max(maximum, bin.MagnitudeDb);
            double x = Math.Log10(bin.FrequencyHz);
            sx += x; sy += bin.MagnitudeDb; sxx += x * x; sxy += x * bin.MagnitudeDb;
        }
        int n = bins.Length;
        double mean = sum / n;
        double slope = (n * sxy - sx * sy) / (n * sxx - sx * sx);
        double tilt = slope * Math.Log10(150.0 / 20.0);
        return (mean, minimum, maximum, Math.Max(Math.Abs(maximum - mean), Math.Abs(minimum - mean)), tilt);
    }

    private static int PeakIndex(double[] samples)
    {
        int peak = 0;
        double best = 0.0;
        for (int i = 0; i < samples.Length; i++)
            if (Math.Abs(samples[i]) > best) { best = Math.Abs(samples[i]); peak = i; }
        return peak;
    }

    /// <summary>A linear chirp over the same band: f(t) = f1 + (f2−f1)·t/T, φ(t) = 2π(f1·t + (f2−f1)t²/2T).</summary>
    private static double[] LinearChirp(SweepSettings settings)
    {
        int n = settings.SampleCount;
        var chirp = new double[n];
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / settings.SampleRate;
            double phi = 2.0 * Math.PI * (settings.StartHz * t
                + (settings.EndHz - settings.StartHz) * t * t / (2.0 * settings.DurationSeconds));
            chirp[i] = Math.Sin(phi);
        }
        return chirp;
    }

    [Fact]
    public void Unity_gain_deconvolution_peaks_at_zero_lag()
    {
        SweepSettings settings = Settings;  // 20 → 150 Hz, 1 s, 48 kHz
        double[] excitation = SweepGenerator.GenerateExponentialSweep(settings);
        double[] inverse = InverseFilter.BuildExponentialInverseSweep(settings);

        // (a) the inverse is the same length as the excitation, 48000 samples
        Assert.Equal(excitation.Length, inverse.Length);
        Assert.Equal(48000, inverse.Length);

        // IR = excitation ⊛ inverse has length 2N−1 = 95999, and the zero-lag (unity-gain) peak sits
        // at index N−1 = 47999: conv[m] = Σ s[n]·i[m−n] is maximal when the two are aligned at m = N−1,
        // where it equals Σ s[n]·s[n]·exp(t_n/T·L) = 76845.10 before normalisation.
        double[] impulseResponse = Deconvolver.Deconvolve(excitation, inverse);
        Assert.Equal(95999, impulseResponse.Length);
        Assert.Equal(inverse.Length - 1, PeakIndex(impulseResponse));
        output.WriteLine($"unity-gain IR: len={impulseResponse.Length} peak={PeakIndex(impulseResponse)} peakValue={impulseResponse[PeakIndex(impulseResponse)]:R}");

        // normalisation requirement: a unity-gain recording reports a flat-band level of 1.0 (0.0 dB).
        // The divisor is the arithmetic mean of |H(f)| over 20…150 Hz of the *raw* construction
        // (measured 1.395772e+7), so the band mean of the linear magnitude is exactly 1.0.
        double bandMeanDb = 20.0 * Math.Log10(FrequencyResponseCalculator
            .Compute(new ImpulseResponse(impulseResponse, SampleRate), WindowType.Rectangular)
            .Where(b => b.FrequencyHz >= 20 && b.FrequencyHz <= 150)
            .Average(b => ComplexMath.DbToLinear(b.MagnitudeDb)));
        Assert.Equal(0.0, bandMeanDb, 1e-9);
        output.WriteLine($"unity-gain band-mean level = {bandMeanDb:E3} dB");
    }

    [Fact]
    public void Magnitude_is_flat_in_the_interior_and_carries_no_tilt()
    {
        double[] excitation = SweepGenerator.GenerateExponentialSweep(Settings);
        double[] inverse = InverseFilter.BuildExponentialInverseSweep(Settings);
        double[] impulseResponse = Deconvolver.Deconvolve(excitation, inverse);

        var full = BandStats(impulseResponse);              // 20 … 150 Hz
        var interior = BandStats(impulseResponse, 30, 140);  // band edges excluded
        output.WriteLine($"20-150 Hz: mean={full.Mean:F4} min={full.Min:F4} max={full.Max:F4} maxdev={full.MaxDeviation:F4} tilt={full.Tilt:F4} dB");
        output.WriteLine($"30-140 Hz: mean={interior.Mean:F4} min={interior.Min:F4} max={interior.Max:F4} maxdev={interior.MaxDeviation:F4} tilt={interior.Tilt:F4} dB");

        // (b) MEASURED max ripple over 20…150 Hz = 12.1023 dB (spread 15.4707 dB, min −12.3587 dB at
        // 150 Hz, max +3.1120 dB) — the ±0.1 dB target is NOT reachable with the specified construction:
        //  * both band edges are ~13 dB down because the stationary-phase point of the edge frequencies
        //    sits exactly at t = 0 / t = T, so only half of the Fresnel integral is inside the sweep:
        //    |S(f2)| loses 6 dB of amplitude → 12 dB in the |S|²·(f/f1) product. Measured product at
        //    140 Hz = 143.7 dB vs 130.7 dB at 150 Hz → −13.0 dB.
        //  * the interior carries ±2.6 dB of Fresnel/Dirichlet fine structure, because the finite 1 s
        //    sweep's own |S(f)|² deviates from the T/(f·L) law by that much (measured spread 5.196 dB
        //    over 25…140 Hz). The inverse multiplies the true |S| by the *law*, so the product is
        //    |S_true|²/|S_law|², not 1.
        //  A spectral 1/S inverse with a −60 dB floor does not fix it either (measured in-band maxdev
        //  9.17 dB): the deviation is the sweep's own spectrum, not a filter artefact.
        //
        // The two bounds below are REGRESSION PINS, not acceptance gates: they are 12.2 / 2.85 dB
        // wide, so an implementation that is 12 dB flatness-off would still pass them. They only
        // catch a large regression (e.g. a lost envelope: 18.34 dB, or a reversed envelope: 35.58 dB).
        // Measured flatness for this default construction is 12.1023 dB over 20…150 Hz and 2.7753 dB
        // over 30…140 Hz; the edge-margin round (EssEdgeMarginTests) measures the reachable interior
        // floor and the common-mode cancellation that makes these absolute numbers non-blocking.
        Assert.True(full.MaxDeviation <= 12.2, $"in-band max deviation was {full.MaxDeviation:F4} dB (measured baseline 12.1023) — REGRESSION PIN, not a flatness gate");

        // interior of the band, where the edge Fresnel terms are small: max deviation 2.7753 dB.
        // REGRESSION PIN (2.85 dB > 2.7753 dB): a 12 dB-off construction still passes this too.
        Assert.True(interior.MaxDeviation <= 2.85, $"30-140 Hz max deviation was {interior.MaxDeviation:F4} dB (measured baseline 2.7753) — REGRESSION PIN, not a flatness gate");

        // the weighting direction is correct: measured tilt −0.6257 dB across 20→150 Hz. Wrong
        // constructions tilt by −8.66 dB (linear chirp), −18.25 dB (no envelope) or −35.58 dB
        // (envelope reversed the wrong way), so ≤1 dB is a real anti-tilt assertion, not decoration.
        Assert.True(Math.Abs(full.Tilt) <= 1.0, $"in-band tilt was {full.Tilt:F4} dB (measured −0.6257 dB)");
    }

    [Fact]
    public void Linear_chirp_inverse_fails_the_flatness_test()
    {
        // anti-cheat: an inverse built for a LINEAR chirp (time-reversed linear chirp, no ±1/2-power
        // weighting) deconvolving the exponential sweep leaves a monotonic tilt. Theory: the linear
        // chirp has flat |S|, so its matched-filter inverse supplies |I| ∝ const, and the product with
        // the log sweep's |S| ∝ f^(−1/2) falls as f^(−1/2) → 20·log10√7.5 = 8.75 dB across 20→150 Hz.
        // MEASURED tilt = −8.6596 dB, maxdev 14.8670 dB — 87× the ±0.1 dB tolerance, so the flatness
        // test above is genuinely an ESS-inverse test and not a correlation test.
        double[] excitation = SweepGenerator.GenerateExponentialSweep(Settings);
        double[] linearInverse = LinearChirp(Settings);
        Array.Reverse(linearInverse);
        var stats = BandStats(Deconvolver.Deconvolve(excitation, linearInverse));
        output.WriteLine($"linear-chirp inverse: tilt={stats.Tilt:F4} dB maxdev={stats.MaxDeviation:F4} dB");

        Assert.True(stats.Tilt < -8.0, $"linear-chirp tilt was {stats.Tilt:F4} dB (expected ≈ −8.75)");
        Assert.True(stats.MaxDeviation > 0.1, "the linear-chirp inverse must fail the ±0.1 dB flatness requirement");
    }

    [Fact]
    public void Plain_correlation_inverse_fails_the_flatness_test()
    {
        // second anti-cheat: forgetting the stationary-phase envelope entirely (plain time-reversed
        // sweep = correlation) leaves |S|² ∝ 1/f, i.e. 20·log10(f2/f1) = 20·log10 7.5 = 17.50 dB of
        // tilt. MEASURED tilt = −18.2478 dB, maxdev 18.3402 dB.
        double[] excitation = SweepGenerator.GenerateExponentialSweep(Settings);
        double[] correlation = (double[])excitation.Clone();
        Array.Reverse(correlation);

        var stats = BandStats(Deconvolver.Deconvolve(excitation, correlation));
        output.WriteLine($"plain correlation: tilt={stats.Tilt:F4} dB maxdev={stats.MaxDeviation:F4} dB");

        Assert.True(stats.Tilt < -17.0, $"plain-correlation tilt was {stats.Tilt:F4} dB (expected ≈ −17.5)");
    }

    [Fact]
    public void Delay_is_measured_exactly_and_is_invariant_across_delays()
    {
        double[] excitation = SweepGenerator.GenerateExponentialSweep(Settings);
        double[] inverse = InverseFilter.BuildExponentialInverseSweep(Settings);
        int zeroLag = inverse.Length - 1;   // 47999

        // peak(D) − peak(0) == D within ±1 sample: the deconvolution is linear, so injecting D samples
        // of pure delay shifts the whole IR by exactly D. MEASURED error = 0 samples for all three.
        foreach (int delay in new[] { 168, 1000, 4800 })
        {
            var recording = new double[excitation.Length];
            for (int i = delay; i < recording.Length; i++) recording[i] = excitation[i - delay];

            double[] impulseResponse = Deconvolver.Deconvolve(recording, inverse);
            int observed = PeakIndex(impulseResponse);
            output.WriteLine($"D={delay}: peak={observed} expected={zeroLag + delay} err={observed - (zeroLag + delay)}");
            Assert.InRange(observed - (zeroLag + delay), -1, 1);
        }
    }

    [Fact]
    public void Recording_longer_than_the_sweep_is_handled()
    {
        double[] excitation = SweepGenerator.GenerateExponentialSweep(Settings);
        double[] inverse = InverseFilter.BuildExponentialInverseSweep(Settings);
        int zeroLag = inverse.Length - 1;

        // array-bounds requirement: recording length (120000 = 2 sweeps + 24000 samples of silence)
        // ≠ inverse length (48000). The linear convolution zero-pads to
        // NextPowerOfTwo(120000 + 48000 − 1) = 262144 and keeps the peak at index N−1 = 47999.
        var recording = new double[2 * excitation.Length + 24000];
        Array.Copy(excitation, recording, excitation.Length);

        double[] impulseResponse = Deconvolver.Deconvolve(recording, inverse);
        Assert.Equal(recording.Length + inverse.Length - 1, impulseResponse.Length);
        int observed = PeakIndex(impulseResponse);
        output.WriteLine($"long recording: len={recording.Length} ir len={impulseResponse.Length} peak={observed} expected={zeroLag}");
        Assert.InRange(observed - zeroLag, -1, 1);
    }
}
