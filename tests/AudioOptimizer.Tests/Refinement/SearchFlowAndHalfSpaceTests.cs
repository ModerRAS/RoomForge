namespace AudioOptimizer.Tests.Refinement;

using AudioOptimizer.Core;
using AudioOptimizer.Optimization;
using AudioOptimizer.Simulation;

/// <summary>
/// Worker B (optimizer-hardening round): the search's invariants and the half-space audit, through the REAL
/// pipeline. These tests pass on the 5d8dc8d baseline and after the B2 delay-joint diff — they pin properties
/// the search must keep, not a score the diff happens to produce.
/// <para>
/// Half-space audit of the four knobs, from source rather than from a prompt's assumed order:
/// <list type="bullet">
/// <item><description><b>polarity</b> — discrete ±1, swept at every candidate by <c>CoarseJointSweep</c> after
/// the PD-1 fix, so with phase over [0, 180] the full drive-rotation circle is covered; test 2 exercises the
/// equivalence.</description></item>
/// <item><description><b>phase</b> — [PhaseMinDegrees, PhaseMaxDegrees], default [0, 180]; the half-turn
/// reparameterisation (pol −1, φ) ≡ (pol +1, φ + 180°) is exact, so no half of the circle is structurally
/// locked (test 1), and widening the box to the full circle must not change the outcome (test 2).</description></item>
/// <item><description><b>delay</b> — structurally unsigned, [0, DelayMaxMilliseconds]: the knob models a
/// physical delay line, which cannot advance a signal, and the optimizer searches only sub B. Test 5 records
/// what that means for the one case where the missing half would matter (a co-located pair with A delayed: the
/// correction would be a negative B delay, and no legal setting can move the spatial terms at all).</description></item>
/// <item><description><b>gain</b> — symmetric [GainMinDb, GainMaxDb] = [−6, +6], swept over the whole range;
/// test 6 checks every returned setting against the box.</description></item>
/// </list>
/// </para>
/// </summary>
public class SearchFlowAndHalfSpaceTests
{
    private static readonly OptimizerOptions Capability = new() { IncludeDelay = true, MaxBoostLimitDb = 30.0 };

    /// <summary>Room 3.3×3.6×2.6 m, 48 kHz, 20–150 Hz, ISM order 3, −120 dBFS, seed 20260101 (the defaults).</summary>
    private static readonly Lazy<DualSubMeasurement> S4NineMic = new(() => Measure(
        [
            new VirtualSubwoofer(new Position(0.45, 0.45, 0.35)),
            new VirtualSubwoofer(new Position(2.85, 3.15, 0.35), -4.0, 1, 70.0, 0.0),
        ],
        NinePoints()));

    /// <summary>Co-located subs, A drive-delayed 2.0 ms: every B setting is one per-frequency scalar on the field.</summary>
    private static readonly Lazy<DualSubMeasurement> CoLocatedADelayed = new(() => Measure(
        [
            new VirtualSubwoofer(new Position(1.65, 1.80, 0.60), 0.0, 1, 0.0, 0.002),
            new VirtualSubwoofer(new Position(1.65, 1.80, 0.60)),
        ],
        ThreePoints()));

    /// <summary>Co-located identical subs: the measured field is already the optimum the search can reach.</summary>
    private static readonly Lazy<DualSubMeasurement> CoLocatedIdentical = new(() => Measure(
        [
            new VirtualSubwoofer(new Position(1.65, 1.80, 0.60)),
            new VirtualSubwoofer(new Position(1.65, 1.80, 0.60)),
        ],
        ThreePoints()));

    private static IReadOnlyList<MeasurementPoint> NinePoints() =>
        [.. ListeningRegion.Default.Points.Where(point => point.GridZ == 0)];

    private static IReadOnlyList<MeasurementPoint> ThreePoints() =>
        [.. ListeningRegion.Default.Points.Where(point => point.Id is "x-1_y-1_z0" or "x0_y0_z0" or "x1_y1_z0")];

    private static DualSubMeasurement Measure(IReadOnlyList<VirtualSubwoofer> subs, IReadOnlyList<MeasurementPoint> microphones)
    {
        var lab = new VirtualLab(SimulationConfig.Default, subs, microphones);
        return lab.AsOptimizerInput(lab.MeasureAll());
    }

    [Fact]
    public void A_half_turn_reparameterisation_is_acoustically_exact_on_measured_data()
    {
        DualSubMeasurement input = S4NineMic.Value;
        IReadOnlyList<PositionResponse> baseline = SubwooferModel.Combine(input, SubwooferSetting.Baseline);

        // (pol −1, φ) is the same drive as (pol +1, φ + 180°) at every frequency, for any delay — the identity
        // the PD-1 fix relies on. On measured (not synthetic) bins it must hold to double noise.
        foreach (double phaseDegrees in new[] { 0.0, 40.0, 90.0, 135.0, 180.0 })
        {
            SubwooferSetting positive = SubwooferSetting.FromDegrees(-2.5, phaseDegrees, 1, 0.004);
            SubwooferSetting negative = SubwooferSetting.FromDegrees(-2.5, phaseDegrees + 180.0, -1, 0.004);

            SpatialSummary a = SpatialMetrics.Compute(SubwooferModel.Combine(input, positive));
            SpatialSummary b = SpatialMetrics.Compute(SubwooferModel.Combine(input, negative));

            Assert.Equal(a.MeanStdDevDb, b.MeanStdDevDb, 12);
            Assert.Equal(a.MeanP90P10Db, b.MeanP90P10Db, 12);
            Assert.Equal(a.WorstNullDb, b.WorstNullDb, 12);
            Assert.Equal(a.MaxPeakAboveMeanDb, b.MaxPeakAboveMeanDb, 12);

            double boostA = ObjectiveFunction.MaxBoostVsBaselineDb(SubwooferModel.Combine(input, positive), baseline);
            double boostB = ObjectiveFunction.MaxBoostVsBaselineDb(SubwooferModel.Combine(input, negative), baseline);
            Assert.Equal(boostA, boostB, 12);
        }
    }

    [Fact]
    public void The_search_is_invariant_to_widening_the_phase_box()
    {
        DualSubMeasurement input = S4NineMic.Value;

        // [0, 180] + both polarities and [0, 360] + both polarities cover the same drives, and both contain the
        // measured setting's drive (0°). The search must reach the same acoustics: measured, both return
        // (−1.1 dB, pol +1, 12.0°, 2.70 ms) with score 26.9989. (A box that EXCLUDES the measured setting is a
        // different case — see the B5-1 finding in the wave report: the stage clamp can change the incumbent's
        // drive when the incumbent's phase lies outside the box.)
        OptimizerResult plain = SubwooferOptimizer.Search(input, Capability);
        OptimizerResult wide = SubwooferOptimizer.Search(input, Capability with { PhaseMaxDegrees = 360.0 });

        Assert.NotNull(plain.Best);
        Assert.NotNull(wide.Best);
        Assert.Equal(plain.Best!.Score, wide.Best!.Score, 6);
        Assert.Equal(plain.Best.Setting, wide.Best.Setting);
        Assert.Equal(plain.ScoreAfter, wide.ScoreAfter, 6);
    }

    [Fact]
    public void The_search_trace_never_regresses()
    {
        OptimizerResult result = SubwooferOptimizer.Search(S4NineMic.Value, Capability);

        Assert.NotEmpty(result.Trace);
        for (int i = 1; i < result.Trace.Count; i++)
        {
            Assert.True(result.Trace[i].BestScore <= result.Trace[i - 1].BestScore + 1e-9,
                $"stage '{result.Trace[i].Stage}' scored {result.Trace[i].BestScore:F6} after '{result.Trace[i - 1].Stage}' scored {result.Trace[i - 1].BestScore:F6}");
        }

        Assert.NotNull(result.Best);
        Assert.Equal(result.Best!.Score, result.Trace[^1].BestScore, 9);
        Assert.Contains(result.Trace, stage => stage.Stage.Contains("delay", StringComparison.Ordinal));
    }

    [Fact]
    public void A_near_optimal_field_is_a_no_op()
    {
        // Two identical co-located subs: the measured field is exactly the best a B-only correction can reach
        // (every setting is one scalar per frequency over an already-optimal field), so the honest answer is
        // "no change", not an invented setting.
        OptimizerResult result = SubwooferOptimizer.Search(CoLocatedIdentical.Value, Capability);

        Assert.Equal(OptimizationVerdict.NegligibleImprovement, result.Verdict);
        Assert.Equal(SubwooferSetting.Baseline, result.Recommended);
    }

    [Fact]
    public void A_pure_delay_mismatch_on_co_located_subs_cannot_move_the_spatial_terms()
    {
        // A drive-delayed 2.0 ms, B not: the required correction is a negative B delay (an advance), which the
        // unsigned delay knob cannot express. The structural consequence is stronger than "the search declines":
        // co-located subs make every B setting one complex scalar per frequency over the field, so the spatial
        // terms cannot move at all — the search is right to decline, and the missing half of the delay axis is
        // a documented model limit, not a search failure.
        DualSubMeasurement input = CoLocatedADelayed.Value;
        SpatialSummary baseline = SpatialMetrics.Compute(SubwooferModel.Combine(input, SubwooferSetting.Baseline));

        foreach (SubwooferSetting setting in new[]
        {
            SubwooferSetting.FromDegrees(3.0, 90.0, 1, 0.0),
            SubwooferSetting.FromDegrees(-3.0, -90.0, -1, 0.005),
            SubwooferSetting.FromDegrees(0.0, 180.0, 1, 0.010),
        })
        {
            SpatialSummary summary = SpatialMetrics.Compute(SubwooferModel.Combine(input, setting));
            // A common complex scalar per frequency leaves every spatial term invariant; on measured data the
            // residual ~5e-6 dB is the independent microphone noise per capture, not physics (the exact form is checked below).
            Assert.Equal(baseline.MeanStdDevDb, summary.MeanStdDevDb, 3);
            Assert.Equal(baseline.MeanP90P10Db, summary.MeanP90P10Db, 3);
            Assert.Equal(baseline.WorstNullDb, summary.WorstNullDb, 3);
        }

        // The exact form of the same statement, on synthetic bins: H_A = H_B · exp(−j2πf·0.002) at every
        // position, so every B setting is a per-frequency scalar and the invariance must hold to double noise.
        {
            var band = new FrequencyBand(20.0, 150.0);
            double[] frequencies = [25.0, 60.0, 120.0];
            var exactA = new List<FrequencyResponse>();
            var exactB = new List<FrequencyResponse>();
            foreach (double frequency in frequencies)
            {
                var b = System.Numerics.Complex.FromPolarCoordinates(0.7, 0.9);
                var a = b * System.Numerics.Complex.FromPolarCoordinates(1.0, -Math.Tau * frequency * 0.002);
                exactA.Add(SubwooferModel.Bin(frequency, a));
                exactB.Add(SubwooferModel.Bin(frequency, b));
            }

            var exactInput = new DualSubMeasurement(
                [new PositionResponse("p0", band, exactA), new PositionResponse("p1", band, [.. exactA.Select(bin => SubwooferModel.Bin(bin.FrequencyHz, new System.Numerics.Complex(bin.Real * 1.4, bin.Imag * 1.4)))])],
                [new PositionResponse("p0", band, exactB), new PositionResponse("p1", band, [.. exactB.Select(bin => SubwooferModel.Bin(bin.FrequencyHz, new System.Numerics.Complex(bin.Real * 1.4, bin.Imag * 1.4)))])]);
            SpatialSummary exactBaseline = SpatialMetrics.Compute(SubwooferModel.Combine(exactInput, SubwooferSetting.Baseline));
            SpatialSummary exactCandidate = SpatialMetrics.Compute(SubwooferModel.Combine(exactInput, SubwooferSetting.FromDegrees(-3.0, -90.0, -1, 0.005)));
            Assert.Equal(exactBaseline.MeanStdDevDb, exactCandidate.MeanStdDevDb, 12);
            Assert.Equal(exactBaseline.MeanP90P10Db, exactCandidate.MeanP90P10Db, 12);
            Assert.Equal(exactBaseline.WorstNullDb, exactCandidate.WorstNullDb, 12);
        }

        OptimizerResult result = SubwooferOptimizer.Search(input, Capability);
        Assert.Equal(OptimizationVerdict.NegligibleImprovement, result.Verdict);
        Assert.Equal(SubwooferSetting.Baseline, result.Recommended);
    }

    [Fact]
    public void Every_recommended_setting_lies_inside_the_search_box()
    {
        OptimizerResult result = SubwooferOptimizer.Search(S4NineMic.Value, Capability);
        SubwooferSetting? setting = result.Recommended;
        Assert.NotNull(setting);

        Assert.InRange(setting!.GainDb, Capability.GainMinDb, Capability.GainMaxDb);
        Assert.InRange(setting.PhaseDegrees, Capability.PhaseMinDegrees, Capability.PhaseMaxDegrees);
        Assert.InRange(setting.DelaySeconds, 0.0, Capability.DelayMaxMilliseconds / 1000.0);
        Assert.Contains(setting.Polarity, new[] { -1, 1 });
    }
}
