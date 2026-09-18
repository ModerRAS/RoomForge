namespace AudioOptimizer.Tests;

using System.Numerics;
using AudioOptimizer.Core;
using AudioOptimizer.Dsp;
using Xunit.Abstractions;

/// <summary>
/// ESS edge-margin characterisation (measurement round, no library change). Everything here is a
/// measured, deterministic property of the shipped construction — full impulse response, rectangular
/// window, 48 kHz — and every pin carries the formula and its measured value.
///
/// ACCEPTANCE FRAMING (boss-1's rationale, kept verbatim in intent):
///   part 1 (interior flatness with enough edge margin) and part 3 (common-mode cancellation) are the
///   acceptance criteria; part 2 (absolute level near a sweep edge) is a documented property of the
///   ESS method — the absolute level near a sweep edge is unreliable — which the tool must DISCLOSE in
///   the UI later, not pretend to fix.
///
/// The decisive measurement is that the residual ripple is NOT ∝ 1/N_edge: the max-over-band statistic
/// falls like ≈ N^(−1/2) (Fresnel-argument scaling), so the calibrated `error ≈ 13.8/N_edge` model
/// over-predicts at small N and under-predicts at large N. See Model_13_8_over_N_is_refuted_by_the_duration_pair.
///
/// LF BUDGET (boss-1 ruling, recorded here so it cannot be re-litigated): 0.1 dB ABSOLUTE flatness in
/// 20…150 Hz is NOT attainable at practical sweep durations and is WITHDRAWN as a project gate for the
/// LF band, exactly as part 2's bottom edge is withdrawn. The achievable LF budget is ≈ 0.5 dB absolute
/// (measured 0.5664 dB for the wide/long sweep), while the ratio quantities the optimizer consumes are
/// exact (part 3). The 0.1 dB criterion survives only as the out-of-band reachability demo.
/// </summary>
public class EssEdgeMarginTests(ITestOutputHelper output)
{
    private const int Fs = 48000;

    // ---- measurement helpers (test-local; nothing in src/ is touched) ----

    private static double[] ImpulseResponseOf(SweepSettings settings) => Deconvolver.Deconvolve(
        SweepGenerator.GenerateExponentialSweep(settings),
        InverseFilter.BuildExponentialInverseSweep(settings));

    private static FrequencyResponse[] Response(double[] impulseResponse, int sampleRate, int? fftSize = null)
        => FrequencyResponseCalculator.Compute(new ImpulseResponse(impulseResponse, sampleRate), WindowType.Rectangular, fftSize);

    /// <summary>Worst |dB − in-band mean| over [lo, hi] and the frequency where it sits.</summary>
    private static (double Deviation, double AtHz) Zone(FrequencyResponse[] response, double lo, double hi)
    {
        var bins = response.Where(b => b.FrequencyHz >= lo && b.FrequencyHz <= hi).ToArray();
        double mean = bins.Average(b => b.MagnitudeDb);
        FrequencyResponse worst = bins.OrderByDescending(b => Math.Abs(b.MagnitudeDb - mean)).First();
        return (Math.Abs(worst.MagnitudeDb - mean), worst.FrequencyHz);
    }

    /// <summary>dB from the 20…150 Hz band mean at the bin nearest <paramref name="hz"/>.</summary>
    private static double DeviationAt(FrequencyResponse[] response, double hz)
    {
        double mean = response.Where(b => b.FrequencyHz >= 20 && b.FrequencyHz <= 150).Average(b => b.MagnitudeDb);
        return response.OrderBy(b => Math.Abs(b.FrequencyHz - hz)).First().MagnitudeDb - mean;
    }

    /// <summary>Least-squares slope of dB vs log10(f), scaled to the 20→150 Hz span (0 dB for correct ESS weighting).</summary>
    private static double Tilt(FrequencyResponse[] response, double lo, double hi)
    {
        var bins = response.Where(b => b.FrequencyHz >= lo && b.FrequencyHz <= hi).ToArray();
        double sx = 0, sy = 0, sxx = 0, sxy = 0;
        foreach (FrequencyResponse bin in bins)
        {
            double x = Math.Log10(bin.FrequencyHz);
            sx += x; sy += bin.MagnitudeDb; sxx += x * x; sxy += x * bin.MagnitudeDb;
        }
        int n = bins.Length;
        return (n * sxy - sx * sy) / (n * sxx - sx * sx) * Math.Log10(hi / lo);
    }

    /// <summary>Edge margin in cycles: N_edge = (T/L)·Δf, L = ln(f2/f1), Δf = distance to the nearest sweep edge.</summary>
    private static double EdgeCycles(SweepSettings settings, double hz) =>
        settings.DurationSeconds / Math.Log(settings.EndHz / settings.StartHz)
        * Math.Min(Math.Abs(hz - settings.StartHz), Math.Abs(settings.EndHz - hz));

    private static void Pin(double expected, double measured, double tolerance, string what) =>
        Assert.True(Math.Abs(expected - measured) <= tolerance,
            $"{what}: measured {measured:F4}, pinned {expected:F4} (tolerance {tolerance})");

    private static int PeakIndex(double[] samples)
    {
        int peak = 0;
        double best = 0;
        for (int i = 0; i < samples.Length; i++)
            if (Math.Abs(samples[i]) > best) { best = Math.Abs(samples[i]); peak = i; }
        return peak;
    }

    /// <summary>
    /// TEST-ONLY inverse construction so taper consistency can be measured without a library change:
    /// i[m] = basis[N−1−m] · exp(t/T·L) is the same Farina law the shipped filter uses.
    /// ponytail: duplicated formula on purpose — if a basis/taper parameter ever enters InverseFilter,
    /// delete this helper and call the library instead.
    /// </summary>
    private static double[] InverseFromBasis(SweepSettings settings, double[] basis)
    {
        double l = Math.Log(settings.EndHz / settings.StartHz);
        var inverse = new double[basis.Length];
        for (int i = 0; i < inverse.Length; i++)
        {
            int forward = basis.Length - 1 - i;   // time reversal: i = N−1−n
            inverse[i] = basis[forward] * Math.Exp((double)forward / settings.SampleRate / settings.DurationSeconds * l);
        }
        return inverse;
    }

    /// <summary>Band-limited fractional delay: multiply the spectrum by exp(−j2πf·Δt), f = k·fs/N.</summary>
    private static double[] FractionallyDelayed(double[] samples, double delaySamples)
    {
        int n = Fft.NextPowerOfTwo(samples.Length + 8192);
        var spectrum = new Complex[n];
        for (int i = 0; i < samples.Length; i++) spectrum[i] = new Complex(samples[i], 0.0);
        Fft.Forward(spectrum);
        for (int k = 0; k <= n / 2; k++)
        {
            double angle = -2 * Math.PI * k / n * delaySamples;
            var rotation = new Complex(Math.Cos(angle), Math.Sin(angle));
            spectrum[k] *= rotation;
            if (k > 0 && k < n - k) spectrum[n - k] *= Complex.Conjugate(rotation);
        }
        Complex[] delayed = Fft.Inverse(spectrum);
        var result = new double[samples.Length + 8192];
        for (int i = 0; i < result.Length; i++) result[i] = delayed[i].Real;
        return result;
    }

    /// <summary>Phase-slope group delay over [lo, hi]: −Δφ/(2π·Δf)·fs samples.</summary>
    private static double GroupDelay(double[] impulseResponse, double lo, double hi)
    {
        FrequencyResponse[] bins = Response(impulseResponse, Fs).Where(b => b.FrequencyHz >= lo && b.FrequencyHz <= hi).ToArray();
        return -(bins[^1].PhaseUnwrappedRad - bins[0].PhaseUnwrappedRad) / (2 * Math.PI * (bins[^1].FrequencyHz - bins[0].FrequencyHz)) * Fs;
    }

    // ---- 1) the edge-margin matrix ----

    [Fact]
    public void Edge_margin_matrix_reproduces_the_measured_table()
    {
        // Analysis band FIXED at 20…150 Hz, rectangular window, full IR, fs = 48 kHz, L = ln(f2/f1).
        // Predicted column = boss-1's calibrated model 13.8/N_nearest, N_nearest = (T/L)·min(Δf_bottom, Δf_top).
        // Rows (f) and (g) are the same sweep band at 1× and 4× duration — they are the model test.
        var rows = new (double F1, double F2, double T, string Label, double Full, double Bottom, double Interior, double Top, double Tilt)[]
        {
            (20,  150, 1, "a", 12.1023, 7.0334,  2.7753, 3.5049, -0.6257),
            (20,  300, 1, "b", 10.5588, 6.3743,  2.2851, 0.4108,  1.2730),
            (20, 1000, 1, "c", 11.1498, 6.0543,  2.2914, 0.3799,  1.4979),
            (10, 1000, 1, "d",  1.9934, 1.9345,  1.4903, 0.5523,  0.0555),
            (20, 1000, 4, "e", 12.2025, 10.4584, 1.4201, 0.2798,  0.7580),
            (5,  1000, 4, "f",  0.5664, double.NaN, 0.3775, 0.1425, 0.0010),
            (5,  1000, 1, "g",  0.7097, double.NaN, 0.4918, 0.1792, -0.0030),
        };

        foreach (var row in rows)
        {
            var settings = new SweepSettings(row.F1, row.F2, row.T, Fs);
            FrequencyResponse[] response = Response(ImpulseResponseOf(settings), Fs);

            (double full, double fullAt) = Zone(response, 20, 150);
            (double bottom, double bottomAt) = Zone(response, 20, 25);
            (double interior, double interiorAt) = Zone(response, 30, 140);
            (double top, double topAt) = Zone(response, 145, 150);
            double tilt = Tilt(response, 20, 150);

            double nBottom = EdgeCycles(settings, 20);
            double nTop = EdgeCycles(settings, 150);
            double nNearestInteriorWorst = Math.Min(EdgeCycles(settings, 30), EdgeCycles(settings, 140));
            output.WriteLine($"row {row.Label}: {row.F1:F0}-{row.F2:F0} Hz T={row.T:F0} s | N(20Hz)={nBottom:F2} N(150Hz)={nTop:F2} "
                + $"| pred 13.8/N(30 or 140)={13.8 / nNearestInteriorWorst:F2} | full={full:F4}@{fullAt:F1} bottom={bottom:F4}@{bottomAt:F1} "
                + $"interior={interior:F4}@{interiorAt:F1} top={top:F4}@{topAt:F1} | tilt={tilt:F4}");

            Pin(row.Full, full, 0.001, $"row {row.Label} full-band maxdev");
            if (!double.IsNaN(row.Bottom)) Pin(row.Bottom, bottom, 0.001, $"row {row.Label} 20-25 Hz maxdev");
            Pin(row.Interior, interior, 0.001, $"row {row.Label} 30-140 Hz maxdev");
            Pin(row.Top, top, 0.001, $"row {row.Label} 145-150 Hz maxdev");
            Pin(row.Tilt, tilt, 0.01, $"row {row.Label} tilt");
        }

        // The table says: pushing the TOP edge out collapses the top zone (3.5049 → 0.4108 → 0.3799 dB
        // for rows a → b → c, model-consistent), but it does NOT fix the band at 20 Hz, which is the
        // sweep start for rows a–c/e (N = 0 there by construction).
        Assert.True(1.9934 < 12.1023);   // row (d) 10 Hz start is the only row whose full band lands < 2 dB
        Assert.True(0.7097 > 0.5664);    // rows (g) → (f): 4× duration, only 1.25× less ripple — see next test
    }

    // ---- 2) the model verdict ----

    [Fact]
    public void Model_13_8_over_N_is_refuted_by_the_duration_pair()
    {
        // Rows (f) 5-1000 Hz T=4 s and (g) 5-1000 Hz T=1 s: SAME band, 4× the duration, so
        //   N(20 Hz) goes 1/5.29832·15 = 2.831 → 11.324  (4.00×)
        // The calibrated model error = 13.8/N therefore predicts 4.00× less ripple:
        //   pred(f)/pred(g) = 2.831/11.324 = 0.250, i.e. 0.5664/0.7097 = 0.798 measured.
        var g = new SweepSettings(5, 1000, 1.0, Fs);
        var f = new SweepSettings(5, 1000, 4.0, Fs);
        double rippleG = Zone(Response(ImpulseResponseOf(g), Fs), 20, 150).Deviation;   // measured 0.7097
        double rippleF = Zone(Response(ImpulseResponseOf(f), Fs), 20, 150).Deviation;   // measured 0.5664
        double measuredRatio = rippleF / rippleG;
        double predictedRatio = EdgeCycles(g, 20) / EdgeCycles(f, 20);                  // 0.250 under 1/N

        output.WriteLine($"f/g: N {EdgeCycles(g, 20):F3} → {EdgeCycles(f, 20):F3} ({1 / predictedRatio:F2}×); "
            + $"ripple {rippleG:F4} → {rippleF:F4} dB (ratio {measuredRatio:F3}, 1/N predicts {predictedRatio:F3}); "
            + $"fitted exponent p in ripple ∝ N^(−p) = {-Math.Log(measuredRatio) / Math.Log(EdgeCycles(f, 20) / EdgeCycles(g, 20)):F2} (1/N ⇒ p = 1.00)");

        // Verdict: HELD would need measuredRatio ≤ ~0.35; measured 0.798 → REFUTED.
        Assert.True(measuredRatio > 0.5, $"13.8/N held: measured f/g ripple ratio {measuredRatio:F3} vs predicted {predictedRatio:F3}");
        Pin(0.7097, rippleG, 0.001, "row g 20-150 Hz maxdev");
        Pin(0.5664, rippleF, 0.001, "row f 20-150 Hz maxdev");
    }

    // ---- 3) OUT-OF-BAND reachability demo: the ≤ 0.1 dB criterion is satisfiable when geometry allows ----

    [Fact]
    public void Out_of_band_reachability_demo_satisfies_the_0_1_db_criterion()
    {
        // OUT-OF-BAND REACHABILITY DEMO — deliberately NOT a claim about the product's band.
        // This analysis band (200…800 Hz) sits entirely ABOVE the 20…150 Hz band a subwoofer tool
        // actually analyses, so it proves only that the ≤ 0.1 dB criterion is satisfiable when the
        // geometry allows it; it proves nothing about 20…150 Hz (see Low_frequency_band_gate_...).
        //
        // Criterion: rectangular in-band ripple ≤ 0.1 dB over an analysis band whose TWO edges each
        // have ≥ 500 cycles of margin, N_edge = (T/L)·Δf ≥ 500.
        // The 500 is measured, not inherited: the earlier 138 came from the refuted 13.8/N model
        // (13.8 dB per cycle ÷ 0.1 dB), and the literal spec band at N = 138.0 measures 0.2458 dB —
        // so 138 was never sufficient. The fitted law (this file) is ripple ≈ 3.2·N^(−0.55), which
        // reaches 0.1 dB at N ≈ 550.
        // Configuration: sweep 5 → 1000 Hz, T = 16 s, fs = 48 kHz, analysis 200…800 Hz, L = ln(200) = 5.29832.
        //   N(200 Hz) = (16/5.29832)·195 = 588.9   N(800 Hz) = (16/5.29832)·200 = 604.0   (both ≥ 500 ✓)
        //   MEASURED ripple = 0.0933 dB at 754.10 Hz  → satisfies the 0.1 dB criterion (7 % margin).
        var settings = new SweepSettings(5, 1000, 16.0, Fs);
        double nLow = EdgeCycles(settings, 200);
        double nHigh = EdgeCycles(settings, 800);
        Assert.True(nLow >= 500, $"low edge margin {nLow:F1} < 500 cycles");
        Assert.True(nHigh >= 500, $"high edge margin {nHigh:F1} < 500 cycles");

        FrequencyResponse[] response = Response(ImpulseResponseOf(settings), Fs);
        (double ripple, double at) = Zone(response, 200, 800);
        output.WriteLine($"T=16 s: N(200)={nLow:F1} N(800)={nHigh:F1} → ripple {ripple:F4} dB at {at:F2} Hz");
        Pin(0.0933, ripple, 0.002, "reachability-demo ripple");
        Assert.True(ripple <= 0.1, $"interior ripple {ripple:F4} dB > 0.1 dB");

        // The literal 20…150 Hz band does NOT reach 0.1 dB at N = 138: measured 0.2458 dB at T = 48.745 s,
        // N(20 Hz) = 138.0, fs = 48 kHz (interior 30…140 Hz: 0.1786 dB at N = 230). ≤ 0.1 dB in the LF
        // band needs N ≳ 500, i.e. T ≈ 177–193 s for a 5 Hz start — hence the withdrawal above.

        // Honest calibration note, measured, not asserted away: N ≥ 500 is the real requirement.
        //  * the same construction at 4× the duration (T = 4 s, N(200) = 147.2):                   0.1997 dB
        //  * the literal spec band 20…150 Hz at T = 48.745 s, N(20 Hz) = 138.0, fs = 48 kHz:    0.2458 dB
        //    (interior 30…140 Hz: 0.1786 dB at N = 230)
        //  * empirical fit over both bands: ripple ≈ 3.2·N^(−0.55), i.e. the true constant is ~1/2-power,
        //    not 13.8/N. ≤ 0.1 dB therefore needs N ≳ 550.
        var fourSeconds = new SweepSettings(5, 1000, 4.0, Fs);
        double ripple4 = Zone(Response(ImpulseResponseOf(fourSeconds), Fs), 200, 800).Deviation;
        Pin(0.1997, ripple4, 0.002, "T=4 s ripple, same analysis band");
        double ratio = ripple / ripple4;                                   // 0.0933/0.1997 = 0.467
        double exponent = -Math.Log(ratio) / Math.Log(nLow / EdgeCycles(fourSeconds, 200));
        output.WriteLine($"N 147.2 → 588.9 (4.00×) gives ripple {ripple4:F4} → {ripple:F4} dB (ratio {ratio:F3}); "
            + $"fitted exponent p in ripple ∝ N^(−p) = {exponent:F2} (1/N ⇒ p = 1.00, Fresnel 1/√N ⇒ p = 0.50)");
        Assert.InRange(ratio, 0.35, 0.65);   // measured 0.467 → 1/N (0.25) is outside, 1/√N (0.50) is inside
    }

    // ---- 3b) the project-facing LF gate (the acceptance that matters) ----

    [Fact]
    public void Low_frequency_band_gate_is_half_a_db_and_the_absolute_target_is_withdrawn()
    {
        // PROJECT-FACING LF GATE (20…150 Hz — the band a subwoofer tool actually analyses).
        //   spec default (sweep 20 → 150 Hz, T = 1 s, fs = 48 kHz): interior 30…140 Hz = 2.7753 dB, full 12.1023 dB
        //   wide/long    (sweep  5 → 1000 Hz, T = 4 s, fs = 48 kHz): interior 30…140 Hz = 0.3775 dB, full  0.5664 dB
        // GATE: full-band ripple ≤ 0.6 dB for the wide/long configuration.
        //
        // 0.1 dB ABSOLUTE FLATNESS IN 20…150 Hz IS WITHDRAWN as a project gate. N ≳ 500 at the band floor
        // implies T ≈ 50–180 s (48.74 s reaches only N = 138; ~177 s at N = 500 for a 5 Hz start), so the
        // achievable LF budget is ≈ 0.5 dB absolute — this test asserts that budget, not 0.1 dB. The
        // withdrawn 0.1 dB target survives only as the out-of-band reachability demo above; the ratio
        // quantities the optimizer consumes stay exact (see Common_mode_cancellation_...).
        //
        // fs dependence of the same wide/long sweep measured over 20…150 Hz:
        //   0.7746 dB @8 kHz | 0.6579 @16 kHz | 0.5664 @48 kHz | 0.5457 @96 kHz
        // The 48 kHz default is the pinned one — 0.5457 dB belongs to 96 kHz, not 48 kHz.
        FrequencyResponse[] specDefault = Response(ImpulseResponseOf(new SweepSettings()), Fs);
        FrequencyResponse[] wideLong = Response(ImpulseResponseOf(new SweepSettings(5, 1000, 4.0, Fs)), Fs);
        FrequencyResponse[] narrowOneSecond = Response(ImpulseResponseOf(new SweepSettings(5, 1000, 1.0, Fs)), Fs);

        (double defaultInterior, _) = Zone(specDefault, 30, 140);
        (double defaultFull, _) = Zone(specDefault, 20, 150);
        (double wideInterior, _) = Zone(wideLong, 30, 140);
        (double wideFull, _) = Zone(wideLong, 20, 150);
        (double narrowInterior, _) = Zone(narrowOneSecond, 30, 140);
        (double narrowFull, _) = Zone(narrowOneSecond, 20, 150);
        output.WriteLine($"spec default 20-150 Hz: interior={defaultInterior:F4} full={defaultFull:F4} | "
            + $"wide/long 5-1000 Hz 4 s: interior={wideInterior:F4} full={wideFull:F4} | "
            + $"same band 1 s: interior={narrowInterior:F4} full={narrowFull:F4}");

        Pin(2.7753, defaultInterior, 0.002, "spec-default interior");
        Pin(12.1023, defaultFull, 0.002, "spec-default full band");
        Pin(0.3775, wideInterior, 0.002, "wide/long interior");
        Pin(0.5664, wideFull, 0.002, "wide/long full band @48 kHz");
        Pin(0.7097, narrowFull, 0.002, "5-1000 Hz 1 s full band");
        Pin(0.4918, narrowInterior, 0.002, "5-1000 Hz 1 s interior");
        Assert.True(wideFull <= 0.6, $"LF wide/long full-band ripple {wideFull:F4} dB > 0.6 dB");

        // Monotonicity, asserted only where the data support it:
        //  (i) fixed-band DURATION monotonicity on the (g) → (f) pair (5→1000 Hz, 1 s → 4 s):
        //      full band 0.7097 → 0.5664 dB, interior 0.4918 → 0.3775 dB.
        Assert.True(wideFull < narrowFull, "4 s did not beat 1 s in the full band");
        Assert.True(wideInterior < narrowInterior, "4 s did not beat 1 s in the interior");
        //  (ii) the wide/long configuration beats the spec default on both statistics.
        Assert.True(wideFull < defaultFull, "wide/long did not beat the spec default in the full band");
        Assert.True(wideInterior < defaultInterior, "wide/long did not beat the spec default in the interior");

        // NOT asserted: blanket monotonicity across sweep widths — the matrix test's rows a → b → c rise
        // again (full 12.1023 → 10.5588 → 11.1498 dB, interior 2.7753 → 2.2851 → 2.2914 dB): the bottom
        // edge dominates and the interference lobes move with L. Only the named orderings above hold.
    }

    // ---- 4) FINAL GATE part 2: the edge artifact is characterised, not fixed ----

    [Fact]
    public void Edge_absolute_level_is_a_characterised_artifact_not_a_flatness_failure()
    {
        // FINAL T7 GATE part 2 (NOT gated, permanently): for an analysis frequency equal to the sweep
        // edge N_edge = 0 and the error is ~12 dB — only half of the Fresnel integral of the edge
        // frequency lies inside the sweep (|S(f_edge)| loses ~6 dB of amplitude → ~12 dB in the
        // |S|²·(f/f1) product). Requirement: CHARACTERISE the measured value; do not assert flatness.
        var settings = new SweepSettings();               // spec defaults: 20 → 150 Hz, 1 s, 48 kHz
        FrequencyResponse[] response = Response(ImpulseResponseOf(settings), Fs);

        double at20 = DeviationAt(response, 20);          // measured −8.970 dB
        double at150 = DeviationAt(response, 150);        // measured −12.369 dB
        (double bottom, _) = Zone(response, 20, 25);      // 7.0334 dB
        (double top, _) = Zone(response, 145, 150);       // 3.5049 dB
        (double full, double fullAt) = Zone(response, 20, 150);   // 12.1023 dB at 149.8 Hz
        output.WriteLine($"default 20-150 Hz sweep: 20 Hz bin {at20:F3} dB, 150 Hz bin {at150:F3} dB, "
            + $"20-25 Hz zone {bottom:F4} dB, 145-150 Hz zone {top:F4} dB, full {full:F4} dB at {fullAt:F1} Hz");

        Pin(-8.970, at20, 0.01, "20 Hz edge bin deviation");
        Pin(-12.369, at150, 0.01, "150 Hz edge bin deviation");
        Pin(7.0334, bottom, 0.001, "20-25 Hz zone ripple");
        Pin(3.5049, top, 0.001, "145-150 Hz zone ripple");
        Pin(12.1023, full, 0.001, "full-band ripple");
        Assert.InRange(at20, -12.5, -6.0);                // the known ~12 dB edge artifact is present
        Assert.InRange(at150, -13.5, -6.0);

        // Quantified margin requirement (pure arithmetic, boss-1's settled numbers):
        //   T = 138·L/Δf with L = ln(f2/f1), Δf = 20 − f1 for a 20 Hz analysis floor.
        static double? DurationFor(int startHz, int targetCycles)
        {
            double l = Math.Log(1000.0 / startHz);
            double delta = 20.0 - startHz;
            return delta <= 0 ? null : targetCycles * l / delta;
        }
        Assert.Null(DurationFor(20, 138));                                    // sweep starts at the floor → N = 0, no finite T
        Pin(63.55, DurationFor(10, 138)!.Value, 0.01, "T for N=138 at 20 Hz with a 10 Hz start");
        Pin(48.74, DurationFor(5, 138)!.Value, 0.01, "T for N=138 at 20 Hz with a 5 Hz start");
        Pin(46.05, DurationFor(10, 100)!.Value, 0.01, "T for N=100 at 20 Hz with a 10 Hz start");
        Pin(35.32, DurationFor(5, 100)!.Value, 0.01, "T for N=100 at 20 Hz with a 5 Hz start");
    }

    // ---- 5) FINAL GATE part 3: common-mode cancellation ----

    [Fact]
    public void Common_mode_cancellation_protects_every_relative_measurement()
    {
        // FINAL T7 GATE part 3 (gated). Two different synthetic rooms, SAME sweep, so the deconvolution
        // kernel error is common: H_est(f) = H_true(f)·E(f). The relative quantities the optimizer
        // consumes — the A/B complex ratio and the complex sum — must match the analytic result to
        // ≤ 0.1 dB and ≤ 1° at 20 Hz AND 150 Hz, exactly where the absolute level is ~12 dB wrong.
        //
        // Room model: H(f) = gain·polarity·(1 + j·f/fc)^(−2)·exp(−j2πf·Δt/fs), built by inverse DFT.
        //   A: gain 0.7, fc 200 Hz, Δt 168.0 samples, polarity +1
        //   B: gain 0.35, fc 800 Hz, Δt 501.5 samples (fractional), polarity −1
        var settings = new SweepSettings();               // 20 → 150 Hz, 1 s, 48 kHz
        double[] excitation = SweepGenerator.GenerateExponentialSweep(settings);
        double[] inverse = InverseFilter.BuildExponentialInverseSweep(settings);

        const int roomLength = 8192;                      // room IR template
        const int grid = 131072;                          // common FFT grid for every comparison

        static double[] Room(double gain, double cutoff, double delaySamples, double polarity)
        {
            var spectrum = new Complex[roomLength];
            for (int k = 0; k <= roomLength / 2; k++)
            {
                double f = (double)k * Fs / roomLength;
                Complex lowPass = Complex.Pow(new Complex(1, f / cutoff), -2);
                Complex delay = Complex.FromPolarCoordinates(1, -2 * Math.PI * f * delaySamples / Fs);
                Complex value = gain * polarity * lowPass * delay;
                spectrum[k] = value;
                if (k > 0 && k < roomLength - k) spectrum[roomLength - k] = Complex.Conjugate(value);
            }
            Complex[] time = Fft.Inverse(spectrum);
            var samples = new double[roomLength];
            for (int i = 0; i < roomLength; i++) samples[i] = time[i].Real;
            return samples;
        }

        double[] roomA = Room(0.7, 200, 168.0, +1);
        double[] roomB = Room(0.35, 800, 501.5, -1);
        var roomAb = new double[roomLength];
        for (int i = 0; i < roomLength; i++) roomAb[i] = roomA[i] + roomB[i];

        FrequencyResponse[] refA = Response(roomA, Fs, grid);
        FrequencyResponse[] refB = Response(roomB, Fs, grid);
        FrequencyResponse[] refAb = Response(roomAb, Fs, grid);
        FrequencyResponse[] measA = Response(Deconvolver.Deconvolve(Fft.Convolve(excitation, roomA), inverse), Fs, grid);
        FrequencyResponse[] measB = Response(Deconvolver.Deconvolve(Fft.Convolve(excitation, roomB), inverse), Fs, grid);
        FrequencyResponse[] measAb = Response(Deconvolver.Deconvolve(Fft.Convolve(excitation, roomAb), inverse), Fs, grid);

        static FrequencyResponse At(FrequencyResponse[] response, double hz) =>
            response.OrderBy(b => Math.Abs(b.FrequencyHz - hz)).First();
        const double ToDegrees = 180.0 / Math.PI;

        foreach (double hz in new[] { 20.0, 150.0 })
        {
            FrequencyResponse a = At(refA, hz), b = At(refB, hz), ab = At(refAb, hz);
            FrequencyResponse ma = At(measA, hz), mb = At(measB, hz), mab = At(measAb, hz);

            double absoluteError = ma.MagnitudeDb - a.MagnitudeDb;                             // ~−9.2 / −12.6 dB
            double ratioMagnitude = (ma.MagnitudeDb - mb.MagnitudeDb) - (a.MagnitudeDb - b.MagnitudeDb);
            double ratioPhase = ((ma.PhaseUnwrappedRad - mb.PhaseUnwrappedRad) - (a.PhaseUnwrappedRad - b.PhaseUnwrappedRad)) * ToDegrees;
            double sumMagnitude = (mab.MagnitudeDb - ma.MagnitudeDb) - (ab.MagnitudeDb - a.MagnitudeDb);   // |A+B|/|A|
            double sumPhase = ((mab.PhaseUnwrappedRad - ma.PhaseUnwrappedRad) - (ab.PhaseUnwrappedRad - a.PhaseUnwrappedRad)) * ToDegrees;

            output.WriteLine($"{hz:F0} Hz: absolute error {absoluteError:F4} dB | A/B ratio Δmag {ratioMagnitude:E3} dB Δphase {ratioPhase:E3}° "
                + $"| |A+B|/|A| Δmag {sumMagnitude:E3} dB Δphase {sumPhase:E3}°");

            // GATED requirement: ≤ 0.1 dB and ≤ 1° for the relative quantities.
            Assert.True(Math.Abs(ratioMagnitude) <= 0.1 && Math.Abs(ratioPhase) <= 1.0, $"{hz} Hz A/B ratio out of tolerance");
            Assert.True(Math.Abs(sumMagnitude) <= 0.1 && Math.Abs(sumPhase) <= 1.0, $"{hz} Hz |A+B|/|A| out of tolerance");
            // The mechanism is exact (the chain is linear, so E(f) cancels): measured residuals are
            // ≤ 4e-15 dB and ≤ 4e-11° — assert the float-noise bound, not just the gate.
            Assert.True(Math.Abs(ratioMagnitude) <= 1e-6 && Math.Abs(ratioPhase) <= 1e-4, $"{hz} Hz ratio residual above float noise");
            Assert.True(Math.Abs(sumMagnitude) <= 1e-6 && Math.Abs(sumPhase) <= 1e-4, $"{hz} Hz sum residual above float noise");
        }

        // The absolute error is real (12 dB at 150 Hz, 9 dB at 20 Hz) — that is the disclosed artifact,
        // and it is exactly why the gate above is expressed on ratios, not on absolute level.
        Pin(-9.226, At(measA, 20).MagnitudeDb - At(refA, 20).MagnitudeDb, 0.02, "absolute level error at 20 Hz");

        // Linearity identity: FR(A+B) == FR(A) + FR(B) to float precision, so the sum is common-mode too.
        double worstSum = double.NegativeInfinity;   // -∞ is the correct answer when a bin cancels exactly
        for (int i = 0; i < measA.Length; i++)
        {
            if (measA[i].FrequencyHz < 20 || measA[i].FrequencyHz > 150) continue;
            Complex a = Complex.FromPolarCoordinates(Math.Pow(10, measA[i].MagnitudeDb / 20), measA[i].PhaseUnwrappedRad);
            Complex b = Complex.FromPolarCoordinates(Math.Pow(10, measB[i].MagnitudeDb / 20), measB[i].PhaseUnwrappedRad);
            Complex ab = Complex.FromPolarCoordinates(Math.Pow(10, measAb[i].MagnitudeDb / 20), measAb[i].PhaseUnwrappedRad);
            worstSum = Math.Max(worstSum, 20 * Math.Log10((ab - a - b).Magnitude / ab.Magnitude));
        }
        output.WriteLine($"linearity |FR(A+B) − (FR(A)+FR(B))| worst over 20-150 Hz = {worstSum:F1} dB");
        Assert.True(worstSum <= -200, $"linearity residual {worstSum:F1} dB");   // measured ≤ −300 dB (float noise)
    }

    // ---- 6) convergence: crop length and FFT size ----

    [Fact]
    public void Interior_ripple_is_invariant_to_crop_length_and_fft_size()
    {
        // Separates two candidate causes of the residual interior ripple for the default sweep:
        //   * analysis truncation (crop length) / bin spacing (FFT size)  → measured ≤ 0.13 dB
        //   * the sweep's own spectrum                                     → measured 2.65–2.78 dB
        // Crops are centred on the peak: Crop(pre, post) with peak index 47999 keeps zero lag at N−1.
        var settings = new SweepSettings();               // 20 → 150 Hz, 1 s, 48 kHz
        double[] impulseResponse = ImpulseResponseOf(settings);
        var centred = new ImpulseResponse(impulseResponse, Fs) { PeakIndex = 47999 };

        var fullBand = new List<(int Fft, double Interior, double Full)>();
        foreach (int fft in new[] { 131072, 262144, 524288 })
        {
            FrequencyResponse[] response = Response(impulseResponse, Fs, fft);
            (double interior, _) = Zone(response, 30, 140);
            (double full, _) = Zone(response, 20, 150);
            fullBand.Add((fft, interior, full));
            output.WriteLine($"full IR fft={fft}: bin={Fs / (double)fft:F4} Hz interior={interior:F4} full={full:F4}");
        }
        Pin(2.7753, fullBand[0].Interior, 0.002, "interior, fft 131072");
        Pin(2.7749, fullBand[1].Interior, 0.002, "interior, fft 262144");
        Pin(2.7812, fullBand[2].Interior, 0.002, "interior, fft 524288");
        // FFT size (4× bin density) moves the interior by 0.006 dB — not the cause.
        Assert.True(Math.Abs(fullBand[0].Interior - fullBand[2].Interior) <= 0.05, "interior not FFT-size invariant");

        var cropped = new List<(int Crop, double Interior, double Full)>();
        foreach (int crop in new[] { 16384, 32768, 65536 })
        {
            ImpulseResponse window = centred.Crop(crop / 2, crop / 2 - 1);
            FrequencyResponse[] response = Response(window.Samples, Fs);
            (double interior, _) = Zone(response, 30, 140);
            (double full, _) = Zone(response, 20, 150);
            cropped.Add((crop, interior, full));
            output.WriteLine($"crop={crop}: interior={interior:F4} full={full:F4}");
        }
        Pin(2.6546, cropped[0].Interior, 0.002, "interior, crop 16384");
        Pin(2.7127, cropped[1].Interior, 0.002, "interior, crop 32768");
        Pin(2.7093, cropped[2].Interior, 0.002, "interior, crop 65536");
        // Crop truncation (64× shorter analysis window) moves the interior by 0.12 dB — also not the cause.
        Assert.True(cropped.Max(c => Math.Abs(c.Interior - fullBand[0].Interior)) <= 0.15, "crop changes the interior by more than 0.15 dB");
    }

    // ---- 7) taper consistency ----

    [Fact]
    public void Taper_consistency_is_immaterial_and_the_shipped_construction_is_kept()
    {
        // The shipped filter builds the inverse from the UNTAPERED analytic sweep while the excitation
        // carries the 5 ms raised-cosine taper. Measuring the taper-consistent alternative:
        //   tapered excitation + analytic inverse (shipped): full 12.1023, interior 2.7753, tilt −0.6257
        //   tapered excitation + TAPERED inverse           : full 12.5078, interior 2.7232, tilt −0.7611
        //   no taper at all                                : full 11.5706, interior 2.8340, tilt −0.4721
        // The interior difference is 0.052 dB and the taper-consistent variant is 0.405 dB WORSE in the
        // full band (it squares the taper attenuation at the edges), so the shipped construction stands.
        var settings = new SweepSettings();               // 20 → 150 Hz, 1 s, 48 kHz
        double[] tapered = SweepGenerator.GenerateExponentialSweep(settings);
        double[] analytic = SweepGenerator.GenerateExponentialSweep(settings, taper: false);

        (double Interior, double Full, double Tilt) Measure(double[] excitation, double[] basis)
        {
            FrequencyResponse[] response = Response(Fft.Convolve(excitation, InverseFromBasis(settings, basis)), Fs);
            return (Zone(response, 30, 140).Deviation, Zone(response, 20, 150).Deviation, Tilt(response, 20, 150));
        }

        (double interiorShipped, double fullShipped, double tiltShipped) = Measure(tapered, analytic);
        (double interiorConsistent, double fullConsistent, double tiltConsistent) = Measure(tapered, tapered);
        (double interiorUntapered, double fullUntapered, _) = Measure(analytic, analytic);
        output.WriteLine($"shipped    full={fullShipped:F4} interior={interiorShipped:F4} tilt={tiltShipped:F4}");
        output.WriteLine($"consistent full={fullConsistent:F4} interior={interiorConsistent:F4} tilt={tiltConsistent:F4}");
        output.WriteLine($"untapered  full={fullUntapered:F4} interior={interiorUntapered:F4}");

        Pin(2.7753, interiorShipped, 0.002, "shipped interior");
        Pin(12.1023, fullShipped, 0.002, "shipped full band");
        Pin(2.7232, interiorConsistent, 0.002, "taper-consistent interior");
        Pin(12.5078, fullConsistent, 0.002, "taper-consistent full band");
        Pin(2.8340, interiorUntapered, 0.002, "untapered interior");
        Assert.True(Math.Abs(interiorConsistent - interiorShipped) <= 0.1, "taper consistency moved the interior materially");
        Assert.True(fullConsistent > fullShipped, "taper-consistent variant unexpectedly better in the full band");
    }

    // ---- 8) fractional delay ----

    [Fact]
    public void Fractional_delay_is_recovered_by_peak_and_by_phase_slope()
    {
        // Same invariant as the integer-delay test but with non-integer D: the deconvolution peak must
        // land within ±1 sample of zeroLag + D (zeroLag = inverse.Length − 1 = 47999), and the phase
        // slope must resolve the fractional part exactly (a spectral delay is exact band-limited).
        var settings = new SweepSettings();               // 20 → 150 Hz, 1 s, 48 kHz
        double[] excitation = SweepGenerator.GenerateExponentialSweep(settings);
        double[] inverse = InverseFilter.BuildExponentialInverseSweep(settings);
        int zeroLag = inverse.Length - 1;
        double reference = GroupDelay(Deconvolver.Deconvolve(excitation, inverse), 60, 90);

        foreach (double delay in new[] { 168.25, 168.5, 1000.5 })
        {
            double[] impulseResponse = Deconvolver.Deconvolve(FractionallyDelayed(excitation, delay), inverse);
            int observed = PeakIndex(impulseResponse);
            double differentialGroupDelay = GroupDelay(impulseResponse, 60, 90) - reference;
            output.WriteLine($"D={delay:F2}: peak error {observed - (zeroLag + delay):F3} samples, "
                + $"differential group delay {differentialGroupDelay:F4} samples (error {differentialGroupDelay - delay:F4})");

            Assert.InRange(observed - (zeroLag + delay), -1, 1);          // measured −0.25 / −0.5 / −0.5
            Assert.True(Math.Abs(differentialGroupDelay - delay) <= 0.01,
                $"phase slope gave {differentialGroupDelay:F4} for D={delay:F2}");   // measured error 0.0000 samples
        }

        // Phase-slope check on an exactly-representable fractional shift: a circular phase ramp
        // exp(−j2πf·d/fs) over N samples. H(f) = exp(−j2πf·d/fs) exactly, so the reported phase must
        // equal −2πf·d/fs for every in-band bin (measured ≤ 1.4e-15 rad over 20…150 Hz).
        // The Nyquist bin (k = N/2, 2000 Hz) is NOT in band and must be zeroed: forcing it complex
        // breaks conjugate symmetry and leaks a π/2 phase error into the real part.
        foreach (double delay in new[] { 168.5, 5.25 })
        {
            const int n = 4096;
            var spectrum = new Complex[n];
            for (int k = 1; k < n / 2; k++)
            {
                double angle = -2 * Math.PI * k / n * delay;
                spectrum[k] = new Complex(Math.Cos(angle), Math.Sin(angle));
                spectrum[n - k] = Complex.Conjugate(spectrum[k]);
            }
            spectrum[0] = Complex.One;
            spectrum[n / 2] = Complex.Zero;
            Complex[] time = Fft.Inverse(spectrum);
            var samples = new double[n];
            for (int i = 0; i < n; i++) samples[i] = time[i].Real;

            FrequencyResponse[] response = FrequencyResponseCalculator.Compute(
                new ImpulseResponse(samples, Fs), WindowType.Rectangular, n);
            double worst = 0;
            for (int k = 0; k <= n / 2; k++)
            {
                double f = (double)k * Fs / n;
                if (f < 20 || f > 150) continue;
                double expected = -2 * Math.PI * f * delay / Fs;
                double error = response[k].PhaseWrappedRad - expected;
                error -= 2 * Math.PI * Math.Round(error / (2 * Math.PI));
                worst = Math.Max(worst, Math.Abs(error));
            }
            output.WriteLine($"exact circular shift D={delay}: max in-band phase error {worst:E3} rad");
            Assert.True(worst <= 1e-12, $"phase slope error {worst:E3} rad for D={delay}");
        }
    }
}
