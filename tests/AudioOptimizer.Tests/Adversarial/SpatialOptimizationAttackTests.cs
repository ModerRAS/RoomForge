namespace AudioOptimizer.Tests;

using System.Numerics;
using AudioOptimizer.Core;
using AudioOptimizer.Optimization;
using AudioOptimizer.Simulation;

/// <summary>
/// Worker D adversarial wave: spatial-subset optimization attacks. These are the FAST pins of the mechanisms the
/// 27-point simulation sweeps exposed (scratch harness C:/Temp/rf-d, seeds and recipes in the wave report):
/// <list type="number">
/// <item>A one-position measurement carries no spatial spread, so the score is 0 for every setting and the
/// optimizer correctly declines — the 1-point session is a no-op, never a regression.</item>
/// <item>The reported band number <see cref="BandSpatialStats.P90P10Db"/> is an across-frequency extreme
/// (max P90 − min P10), while the optimizer's objective term is the across-position mean P90−P10. The two can
/// move in OPPOSITE directions on the same data, so the headline number is not the quantity the search minimized.</item>
/// <item>Boost is max-over-positions: a setting legal on a subset can exceed the limit on a position the session
/// did not measure. The optimizer can only constrain what it was shown.</item>
/// <item>The subset optimum is only the optimum on the subset: a synthetic room where the two measured positions
/// are exactly flat at a setting that spreads the two unmeasured positions by 12 dB. The search rightly improves
/// its input and worsens the room — subset coverage is the missing guard, not the search.</item>
/// </list>
/// </summary>
public class SpatialOptimizationAttackTests
{
    private static readonly FrequencyBand Band = new(5.0, 200.0);

    private static PositionResponse Position(string id, params (double Hz, double Re, double Im)[] bins)
        => new(id, Band, [.. bins.Select(b => OptimizationTestData.Bin(b.Hz, b.Re, b.Im))]);

    private static DualSubMeasurement Measurement(params (PositionResponse A, PositionResponse B)[] positions)
        => new([.. positions.Select(p => p.A)], [.. positions.Select(p => p.B)]);

    private static double FieldScore(DualSubMeasurement measurement, SubwooferSetting setting, OptimizerOptions options)
    {
        IReadOnlyList<PositionResponse> totals = SubwooferModel.Combine(measurement, setting);
        IReadOnlyList<PositionResponse> baseline = SubwooferModel.Combine(measurement, SubwooferSetting.Baseline);
        double boost = ObjectiveFunction.MaxBoostVsBaselineDb(totals, baseline);
        return ObjectiveFunction.Score(SpatialMetrics.Compute(totals), Math.Max(0.0, boost), options.Weights);
    }

    // 1 — the 1-point session: no spread to see, so the search has nothing to improve and declines.
    [Fact]
    public void A_single_measured_position_is_a_no_op_because_one_position_has_no_spread()
    {
        DualSubMeasurement input = OptimizationTestData.Room(positions: 1);

        OptimizerResult result = SubwooferOptimizer.Search(input);

        Assert.Equal(OptimizationVerdict.NegligibleImprovement, result.Verdict);
        Assert.Equal(SubwooferSetting.Baseline, result.Recommended);
        // σ, P90−P10 and the null term are all defined ACROSS positions, so one position scores exactly 0
        // (and a boost-free baseline cannot be improved on by any legal candidate).
        Assert.Equal(0.0, result.ScoreBefore, 12);
        Assert.Equal(0.0, result.ScoreAfter, 12);
    }

    // 2 — the reported band P90−P10 is max(P90) − min(P10) across frequencies; the objective is the mean of the
    // per-frequency P90−P10 across positions. Same inputs, two different numbers (2 dB vs 12 dB).
    [Fact]
    public void Reported_band_p90p10_is_an_across_frequency_extreme_not_the_objective_mean_p90p10()
    {
        // Two positions, two frequencies. Each frequency has exactly 2 dB of across-position spread, but the
        // 100 Hz pair sits 10 dB above the 50 Hz pair.
        var totals = new List<PositionResponse>
        {
            Position("p0", (50.0, 1.0, 0.0), (100.0, Math.Pow(10, 10.0 / 20.0), 0.0)),
            Position("p1", (50.0, Math.Pow(10, 2.0 / 20.0), 0.0), (100.0, Math.Pow(10, 12.0 / 20.0), 0.0)),
        };

        SpatialSummary objective = SpatialMetrics.Compute(totals);
        BandSpatialStats reported = BandSpatialStats.Of(objective);

        Assert.Equal(2.0, objective.MeanP90P10Db, 12);            // (2 + 2) / 2 — what the search minimizes
        Assert.Equal(12.0, reported.P90P10Db, 12);                // max P90 (12) − min P10 (0) — what a report shows
        Assert.Equal(objective.MeanStdDevDb, reported.StdDevDb, 12); // σ happens to agree; P90−P10 does not
        Assert.Equal(10.0, reported.P90P10Db - objective.MeanP90P10Db, 12);
    }

    // 3 — the boost limit is per-position; a subset only sees its own positions, so its measured boost is a
    // LOWER BOUND for the same setting's boost on the rest of the room.
    [Fact]
    public void Max_boost_measured_on_a_subset_is_only_a_lower_bound_for_the_full_field()
    {
        // p0: candidate grows |A+gB| from 1.000 to 1.078 → +0.65 dB (legal under 3 dB).
        // p1: candidate grows |A+gB| from 1.000 to 1.778 → +5.00 dB (NOT legal under 3 dB).
        var full = Measurement(
            (Position("p0", (50.0, 0.9, 0.0)), Position("p0", (50.0, 0.1, 0.0))),
            (Position("p1", (50.0, 0.0, 0.0)), Position("p1", (50.0, 1.0, 0.0))));
        var setting = new SubwooferSetting(5.0, 0.0, 1, 0.0); // +5 dB on B
        IReadOnlyList<PositionResponse> baseline = SubwooferModel.Combine(full, SubwooferSetting.Baseline);
        IReadOnlyList<PositionResponse> candidate = SubwooferModel.Combine(full, setting);

        var subset = new DualSubMeasurement([full.A[0]], [full.B[0]]);
        IReadOnlyList<PositionResponse> subsetBaseline = SubwooferModel.Combine(subset, SubwooferSetting.Baseline);
        IReadOnlyList<PositionResponse> subsetCandidate = SubwooferModel.Combine(subset, setting);

        double subsetBoost = ObjectiveFunction.MaxBoostVsBaselineDb(subsetCandidate, subsetBaseline);
        double fullBoost = ObjectiveFunction.MaxBoostVsBaselineDb(candidate, baseline);

        Assert.Equal(0.65, subsetBoost, 2);
        Assert.Equal(5.00, fullBoost, 2);
        Assert.True(ObjectiveFunction.WithinBoostLimit(subsetBoost, 3.0));
        Assert.False(ObjectiveFunction.WithinBoostLimit(fullBoost, 3.0));
        Assert.True(fullBoost > subsetBoost);
    }

    // 4 — the subset optimum is only the optimum on the subset. Two measured positions are exactly flat at
    // c = +6 dB / 90°, while the two UNMEASURED positions are flat at the measured setting and 12 dB apart at c.
    // The search correctly improves its input; the same setting wrecks the room.
    [Fact]
    public void A_subset_optimum_can_be_worse_on_the_positions_the_session_did_not_measure()
    {
        Complex c = Complex.FromPolarCoordinates(2.0, Math.PI / 2.0); // +6.02 dB, 90° (search box caps gain at 6)

        // Measured pair: A = T − c·B with T = 1∠0, so the total at c is exactly 1 for both.
        Complex a0 = 1.0 - c * new Complex(0.5, 0.0);
        Complex a1 = 1.0 - c * new Complex(-0.5, 0.0);
        // Unmeasured pair: chosen so the MEASURED setting is exactly 1 for both, and c spreads them (p3 → 4).
        Complex a2 = new(1.0, 0.0);
        Complex a3 = new(1.6, 1.2);
        Complex b3 = new(-0.6, -1.2);

        var full = Measurement(
            (Position("p0", (50.0, a0.Real, a0.Imaginary)), Position("p0", (50.0, 0.5, 0.0))),
            (Position("p1", (50.0, a1.Real, a1.Imaginary)), Position("p1", (50.0, -0.5, 0.0))),
            (Position("p2", (50.0, a2.Real, a2.Imaginary)), Position("p2", (50.0, 0.0, 0.0))),
            (Position("p3", (50.0, a3.Real, a3.Imaginary)), Position("p3", (50.0, b3.Real, b3.Imaginary))));
        var measuredPair = new DualSubMeasurement(full.A.Take(2).ToArray(), full.B.Take(2).ToArray());

        var options = new OptimizerOptions(); // default 3 dB limit is not binding on the measured pair
        OptimizerResult result = SubwooferOptimizer.Search(measuredPair, options);

        Assert.Equal(OptimizationVerdict.Improved, result.Verdict);
        Assert.NotNull(result.Recommended);

        // The session input improves a lot...
        Assert.True(result.ScoreAfter < result.ScoreBefore - 5.0,
            $"subset score should improve by >5 dB, was {result.ScoreBefore:F3} → {result.ScoreAfter:F3}");

        // ...and the identical objective recomputed over the unmeasured pair shows the room got much worse.
        double fullBefore = FieldScore(full, SubwooferSetting.Baseline, options);
        double fullAfter = FieldScore(full, result.Recommended!, options);
        Assert.True(fullAfter > fullBefore + 5.0,
            $"full-field score should regress by >5 dB, was {fullBefore:F3} → {fullAfter:F3}");
    }
}
