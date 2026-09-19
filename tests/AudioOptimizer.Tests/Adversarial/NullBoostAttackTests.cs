namespace AudioOptimizer.Tests.Adversarial;

using AudioOptimizer.Core;
using AudioOptimizer.Optimization;
using AudioOptimizer.Simulation;

/// <summary>
/// Worker B adversarial wave: deep nulls driven through the REAL pipeline (virtual room → shipped measurement chain
/// → shipped optimizer) with the boost limit swept 0/3/6/30. Every case here is deterministic (the default noise
/// seed) and small: the search grids are 3–5 microphones, and the one wide-grid case measures the shipped 27-point
/// region once.
/// <para>
/// These are CHARACTERISATION pins, not ideal-behaviour assertions. Each one documents a measured attack-surface
/// result from the wave; the comment beside it names the raw numbers observed and what a future change would have
/// to preserve or knowingly alter.
/// </para>
/// </summary>
public class NullBoostAttackTests
{
    private static readonly double[] Limits = [0.0, 3.0, 6.0, 30.0];

    private static readonly Position Front = new(0.35, 0.35, 0.30);
    private static readonly Position Rear = new(2.95, 3.25, 0.30);
    private static readonly Position LineA = new(0.50, 1.80, 1.20);
    private static readonly Position LineB = new(2.80, 1.80, 1.20);

    /// <summary>The direct-sound-only room: two-source interference with no ISM reflections in the way.</summary>
    private static SimulationConfig Direct => SimulationConfig.Default with { ImageSourceOrder = 0 };

    /// <summary>The five-microphone cross the search sees: the region's centre and its four face centres.</summary>
    private static IReadOnlyList<MeasurementPoint> Cross =>
        RegionMics("x0_y0_z0", "x-1_y0_z0", "x1_y0_z0", "x0_y-1_z0", "x0_y1_z0");

    /// <summary>Three microphones on the y = 1.8 m line joining the two subs of the line case.</summary>
    private static IReadOnlyList<MeasurementPoint> Line => RegionMics("x-1_y0_z0", "x0_y0_z0", "x1_y0_z0");

    private static List<MeasurementPoint> RegionMics(params string[] ids)
        => [.. ids.Select(id => ListeningRegion.Default.Points.Single(point => point.Id == id))];

    /// <summary>
    /// The B gain whose inverted-polarity residual at an equidistant microphone is <paramref name="depthDb"/> below
    /// A: total = A·|1 − g|, so g = 1 − 10^(−depth/20). Physical design of the fixture, not a product constant.
    /// </summary>
    private static double GainForNull(double depthDb) => 20.0 * Math.Log10(1.0 - Math.Pow(10.0, -depthDb / 20.0));

    private static DualSubMeasurement Measure(
        SimulationConfig config,
        VirtualSubwoofer a,
        VirtualSubwoofer b,
        IReadOnlyList<MeasurementPoint> microphones)
    {
        var scenario = new SimulationScenario("attack", "attack", "attack", config, [a, b], microphones);
        VirtualLab lab = scenario.CreateLab();
        return lab.AsOptimizerInput(lab.MeasureAll());
    }

    private static OptimizerResult Search(DualSubMeasurement input, double limit)
        => SubwooferOptimizer.Search(input, new OptimizerOptions { IncludeDelay = true, MaxBoostLimitDb = limit });

    /// <summary>The worst signed dB change of any position/bin between two predicted totals — the "explosion" measure.</summary>
    private static double WorstDropDb(IReadOnlyList<PositionResponse> before, IReadOnlyList<PositionResponse> after)
    {
        double worst = 0.0;
        for (int i = 0; i < before.Count; i++)
        {
            for (int k = 0; k < before[i].Bins.Count; k++)
            {
                double b = Magnitude(before[i].Bins[k]), a = Magnitude(after[i].Bins[k]);
                if (b < 1e-12 || a < 1e-12) continue;
                worst = Math.Min(worst, 20.0 * Math.Log10(a / b));
            }
        }

        return worst;
    }

    private static double Magnitude(FrequencyResponse bin) => Math.Sqrt(bin.Real * bin.Real + bin.Imag * bin.Imag);

    /// <summary>
    /// A 40 dB deep null at the equidistant centre microphone (mirror-image subs, one inverted). The fixture really
    /// contains the null, and the sweep behaves: limit 0 is an honest no-op with zero boost spent, limit 3 improves
    /// the spread while the limit binds and is never exceeded.
    /// Measured: AB worst null 35.39 dB, σ 15.880 dB; limit 0 → NegligibleImprovement, achieved boost 0.000;
    /// limit 3 → Improved, achieved boost 2.332 dB (binding), σ 14.993 dB.
    /// </summary>
    [Fact]
    public void A_deep_null_spends_no_boost_at_limit_zero_and_stays_inside_the_limit_at_three()
    {
        DualSubMeasurement input = Measure(Direct, new VirtualSubwoofer(Front), new VirtualSubwoofer(Rear, GainForNull(40.0), -1), Cross);

        SpatialSummary measured = SpatialMetrics.Compute(input.AB ?? SubwooferModel.Combine(input, SubwooferSetting.Baseline));
        Assert.True(measured.WorstNullDb >= 30.0, $"the fixture's worst null is only {measured.WorstNullDb:F2} dB");

        OptimizerResult zero = Search(input, 0.0);
        Assert.Equal(OptimizationVerdict.NegligibleImprovement, zero.Verdict);
        Assert.Equal(SubwooferSetting.Baseline, zero.Recommended);
        Assert.Equal(0.0, zero.Constraint.MaxAchievedBoostDb ?? double.NaN, 6);
        Assert.Equal(zero.Before.MeanStdDevDb, zero.After.MeanStdDevDb, 9);

        OptimizerResult three = Search(input, 3.0);
        Assert.Equal(OptimizationVerdict.Improved, three.Verdict);
        Assert.NotNull(three.Constraint.MaxAchievedBoostDb);
        Assert.True(three.Constraint.MaxAchievedBoostDb <= 3.0, $"achieved {three.Constraint.MaxAchievedBoostDb:F3} dB of a 3 dB limit");
        Assert.True(three.Constraint.Binding, "the fixture no longer binds the limit, so it no longer tests it");
        Assert.True(three.After.MeanStdDevDb < three.Before.MeanStdDevDb);
    }

    /// <summary>
    /// A frequency-localized null: B delayed by exactly 5 ms with positive polarity, so the equidistant centre
    /// microphone nulls at 100 Hz (measured 51.17 dB deep) while 50 and 150 Hz remain ~+3 dB sums. Every limit up to
    /// 30 dB returns the measured setting: any candidate that fills the notch raises it by more than the limit. The
    /// explicit fill that WOULD fit inside 30 dB (gain −0.1 dB, achieved boost 23.481 dB, notch 51.16 → 32.33 dB)
    /// raises the shipped score by 2.17 dB, because the worst-null gain is the raise minus the per-frequency mean
    /// rise while the boost penalty is charged on the raise alone.
    /// Measured: limit 0/3/6/30 → NegligibleImprovement, achieved boost 0.000, candidate summary unchanged.
    /// </summary>
    [Fact]
    public void A_frequency_localized_null_is_refused_even_at_limit_thirty_because_filling_it_scores_worse()
    {
        DualSubMeasurement input = Measure(
            Direct,
            new VirtualSubwoofer(Front),
            new VirtualSubwoofer(Rear, 0.0, 1, 0.0, 0.005),
            Cross);

        SpatialSummary measured = SpatialMetrics.Compute(input.AB ?? SubwooferModel.Combine(input, SubwooferSetting.Baseline));
        Assert.True(measured.WorstNullDb >= 45.0, $"the 100 Hz notch is only {measured.WorstNullDb:F2} dB deep");

        foreach (double limit in Limits)
        {
            OptimizerResult result = Search(input, limit);
            Assert.Equal(OptimizationVerdict.NegligibleImprovement, result.Verdict);
            Assert.Equal(SubwooferSetting.Baseline, result.Recommended);
            Assert.Equal(0.0, result.Constraint.MaxAchievedBoostDb ?? double.NaN, 6);
        }

        // The obvious fill the limit would allow, evaluated directly: it makes the shipped score worse.
        IReadOnlyList<PositionResponse> baselineTotals = SubwooferModel.Combine(input, SubwooferSetting.Baseline);
        IReadOnlyList<PositionResponse> candidateTotals = SubwooferModel.Combine(input, SubwooferSetting.FromDegrees(-0.1, 0.0));
        SpatialSummary candidate = SpatialMetrics.Compute(candidateTotals);
        double boost = ObjectiveFunction.MaxBoostVsBaselineDb(candidateTotals, baselineTotals);
        double beforeScore = ObjectiveFunction.Score(SpatialMetrics.Compute(baselineTotals), 0.0, ObjectiveWeights.Default);
        double candidateScore = ObjectiveFunction.Score(candidate, boost, ObjectiveWeights.Default);

        Assert.True(boost is > 20.0 and <= 30.0, $"the fill's achieved boost is {boost:F3} dB");
        Assert.True(candidate.WorstNullDb <= 35.0, $"the fill only moved the notch to {candidate.WorstNullDb:F2} dB");
        Assert.True(candidateScore > beforeScore, $"the fill scored {candidateScore:F4} against the baseline's {beforeScore:F4}");
    }

    /// <summary>
    /// A pair that cancels everywhere: the two subs sit at the same position with opposite polarity, so at every
    /// microphone the rendered impulse responses are numerically identical and the A+B capture is the noise floor
    /// (measured mean −138.53 dB, worst null 24.39 dB). Un-cancelling needs more boost than any limit here allows, so
    /// all four limits refuse and return the measured setting — the safe answer, even though the physical fix (one
    /// polarity switch) is trivial.
    /// </summary>
    [Fact]
    public void An_exactly_cancelling_pair_is_refused_at_every_limit_including_thirty()
    {
        var position = new Position(0.80, 0.90, 0.30);
        DualSubMeasurement input = Measure(Direct, new VirtualSubwoofer(position), new VirtualSubwoofer(position, 0.0, -1), Cross);

        SpatialSummary measured = SpatialMetrics.Compute(input.AB ?? SubwooferModel.Combine(input, SubwooferSetting.Baseline));
        Assert.True(measured.WorstNullDb >= 15.0, $"the coincident pair's measured null is only {measured.WorstNullDb:F2} dB");

        foreach (double limit in Limits)
        {
            OptimizerResult result = Search(input, limit);
            Assert.Equal(OptimizationVerdict.NegligibleImprovement, result.Verdict);
            Assert.Equal(SubwooferSetting.Baseline, result.Recommended);
            Assert.Equal(0.0, result.Constraint.MaxAchievedBoostDb ?? double.NaN, 6);
        }
    }

    /// <summary>
    /// The sparse-grid overfit pin. The search sees only the five-microphone cross; at 30 dB it recommends a large
    /// polarity-flip correction that its own five points score as excellent. Applied to the full 27-point shipped
    /// region, the same setting drives one unmeasured corner microphone into a deep null.
    /// Measured: search σ 7.754 → 0.633 dB, achieved boost 23.234 dB of 30 (recommended −6.0 dB, polarity −1,
    /// phase 4.0°, delay 0.5 ms); on the 27-point region the worst position/bin drops 29.89 dB
    /// (x1_y1_z-1 at 133.7 Hz) while overall σ improves 9.751 → 2.418 dB.
    /// Separately, searching the full 27-point region directly at 30 dB returns +6.0 dB, polarity −1, phase 123.0°,
    /// σ 9.751 → 2.293 dB with a LevelChangeDb of +10.68 dB: legal under the limit, but the whole room is 10.7 dB
    /// louder and only LevelChangeDb says so.
    /// </summary>
    [Fact]
    public void A_sparse_search_grid_lets_limit_thirty_create_a_deep_null_off_grid()
    {
        var a = new VirtualSubwoofer(Front);
        var b = new VirtualSubwoofer(Rear, GainForNull(20.0), -1);
        DualSubMeasurement sparse = Measure(Direct, a, b, Cross);

        OptimizerResult result = Search(sparse, 30.0);
        Assert.Equal(OptimizationVerdict.Improved, result.Verdict);
        Assert.NotNull(result.Recommended);
        Assert.NotNull(result.Constraint.MaxAchievedBoostDb);
        Assert.True(result.Constraint.MaxAchievedBoostDb <= 30.0, "the recommendation broke its own boost limit");
        Assert.True(result.After.MeanStdDevDb < result.Before.MeanStdDevDb - 1.0, "the sparse search no longer reports the improvement it is pinned for");

        // Apply the recommendation to B and predict the full shipped region from the same measured A/B.
        SubwooferSetting recommended = result.Recommended!;
        DualSubMeasurement wide = Measure(Direct, a, b, ListeningRegion.Default.Points);
        IReadOnlyList<PositionResponse> wideBaseline = SubwooferModel.Combine(wide, SubwooferSetting.Baseline);
        IReadOnlyList<PositionResponse> wideAfter = SubwooferModel.Combine(wide, recommended);

        double worstDrop = WorstDropDb(wideBaseline, wideAfter);
        Assert.True(worstDrop <= -15.0, $"the off-grid worst drop is only {worstDrop:F2} dB, so this pin no longer records the overfit");

        // The same room searched on the full grid buys a smaller spread with a much larger room-wide level rise.
        OptimizerResult wideSearch = Search(wide, 30.0);
        Assert.Equal(OptimizationVerdict.Improved, wideSearch.Verdict);
        Assert.True(wideSearch.LevelChangeDb >= 5.0, $"the full-grid limit-30 recommendation only raises the room by {wideSearch.LevelChangeDb:F2} dB");
        Assert.True(wideSearch.Constraint.MaxAchievedBoostDb is null or <= 30.0);
    }
}
