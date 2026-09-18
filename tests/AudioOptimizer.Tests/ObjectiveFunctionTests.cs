using AudioOptimizer.Optimization;

namespace AudioOptimizer.Tests;

/// <summary>
/// The objective's contract: an explicit weighted sum, a boost defined against the measurement, a hard
/// rejection that is not the penalty, and a target curve that stays outside the score.
/// </summary>
public class ObjectiveFunctionTests
{
    private static SpatialSummary Summary(double sigmaDb, double p90p10Db, double worstNullDb, double peakAboveMeanDb = 0.0)
        => new(sigmaDb, 0.0, p90p10Db, worstNullDb, peakAboveMeanDb, [new FrequencyMetrics(50.0, 0.0, 0.0, sigmaDb, 0.0, 0.0, 0.0, 0.0, 0.0, p90p10Db)], OptimizationTestData.Band);

    [Fact]
    public void ScoreIsTheWeightedSumOfTheFourTerms()
    {
        // σ 1.0, P90−P10 2.0, boost 1.5, null 0.5 with w = (1, 1, 0.5, 0.5):
        // 1·1.0 + 1·2.0 + 0.5·1.5 + 0.5·0.5 = 1.0 + 2.0 + 0.75 + 0.25 = 4.0
        var weights = new ObjectiveWeights(MeanStdDev: 1.0, MeanP90P10: 1.0, PeakPenalty: 0.5, NullPenalty: 0.5);
        ObjectiveTerms terms = ObjectiveFunction.Terms(Summary(1.0, 2.0, 0.5), 1.5);

        Assert.Equal(1.0, terms.MeanStdDevDb, 12);
        Assert.Equal(2.0, terms.MeanP90P10Db, 12);
        Assert.Equal(1.5, terms.PeakPenaltyDb, 12);
        Assert.Equal(0.5, terms.NullPenaltyDb, 12);
        Assert.Equal(4.0, terms.Score(weights), 12);
        // With w3 = 0 the same setting scores 1·1.0 + 1·2.0 + 0 + 0.5·0.5 = 3.25, so the weight is a real
        // parameter and not a hidden constant.
        Assert.Equal(3.25, terms.Score(weights with { PeakPenalty = 0.0 }), 12);
    }

    [Fact]
    public void BoostIsMeasuredAgainstTheMeasuredResponse()
    {
        var measurement = new DualSubMeasurement(
            [OptimizationTestData.OneBin(50.0, 1.0, 0.0)],
            [OptimizationTestData.OneBin(50.0, 1.0, 0.0)]);

        IReadOnlyList<PositionResponse> baseline = SubwooferModel.Combine(measurement, SubwooferSetting.Baseline);
        // The measured setting achieves exactly 0 dB of boost by definition — that is what makes a legal
        // candidate always exist for a non-negative limit.
        Assert.Equal(0.0, ObjectiveFunction.MaxBoostVsBaselineDb(baseline, baseline), 12);

        // A = 1, B = 1, so the baseline total is 2. B at +6.020599913279624 dB → 2 → total 3:
        // boost = 20·log10(3/2) = 3.521825181113627 dB.
        IReadOnlyList<PositionResponse> boosted = SubwooferModel.Combine(measurement, OptimizationTestData.Setting(6.020599913279624, 0.0));
        Assert.Equal(3.521825181113627, ObjectiveFunction.MaxBoostVsBaselineDb(boosted, baseline), 12);

        // B at −6.020599913279624 dB → 0.5 → total 1.5: boost = 20·log10(1.5/2) = 20·log10(0.75) = −2.498774732166 dB.
        IReadOnlyList<PositionResponse> quieter = SubwooferModel.Combine(measurement, OptimizationTestData.Setting(-6.020599913279624, 0.0));
        Assert.Equal(-2.498774732165999, ObjectiveFunction.MaxBoostVsBaselineDb(quieter, baseline), 12);
    }

    [Fact]
    public void AttenuationCannotLowerTheScoreButItsBoostPenaltyIsClampedToZero()
    {
        // Two positions scaled together by a common factor keep σ, range and P90−P10, and a negative boost
        // must not become a negative penalty (the score cannot be won by making the room quieter).
        var measurement = new DualSubMeasurement(
            // One grid: two positions at the same frequency with different levels, which is what "spread across
            // positions" means. FALSIFIER: because (C) validates the band rather than selecting by it, a fixture band
            // can only change which fixtures throw, never a computed value — so every Phase 11-17 number must read
            // identically (43802 / 4.497669687 / gap 1.776e-15 / 820-of-1066 at 2.974091 / 0-of-1066 / 104-of-1066 /
            // 1.331711 dB / the five §21 recipes at 0.000000 dB). If one moves, something IS selecting by band.
            [OptimizationTestData.OneBin(50.0, 1.0, 0.0), OptimizationTestData.OneBin(50.0, 2.0, 0.0)],
            [OptimizationTestData.OneBin(50.0, 1.0, 0.0), OptimizationTestData.OneBin(50.0, 2.0, 0.0)]);

        IReadOnlyList<PositionResponse> baseline = SubwooferModel.Combine(measurement, SubwooferSetting.Baseline);
        IReadOnlyList<PositionResponse> quieter = SubwooferModel.Combine(measurement, OptimizationTestData.Setting(-6.0, 0.0));

        SpatialSummary baselineSummary = SpatialMetrics.Compute(baseline);
        SpatialSummary quieterSummary = SpatialMetrics.Compute(quieter);
        Assert.Equal(baselineSummary.MeanStdDevDb, quieterSummary.MeanStdDevDb, 12);
        Assert.Equal(baselineSummary.MeanP90P10Db, quieterSummary.MeanP90P10Db, 12);

        double boost = ObjectiveFunction.MaxBoostVsBaselineDb(quieter, baseline);
        Assert.True(boost < 0.0, $"expected an attenuating boost, measured {boost}");
        Assert.Equal(0.0, ObjectiveFunction.Terms(quieterSummary, boost).PeakPenaltyDb, 12);
    }

    [Fact]
    public void TheLimitIsAHardComparison()
    {
        Assert.True(ObjectiveFunction.WithinBoostLimit(3.0, 3.0));
        Assert.True(ObjectiveFunction.WithinBoostLimit(2.999999999, 3.0));
        Assert.False(ObjectiveFunction.WithinBoostLimit(3.000000001, 3.0));
        Assert.False(ObjectiveFunction.WithinBoostLimit(6.0, 3.0));
    }

    [Fact]
    public void TheTargetCurveIsASeparateMetricAndCannotEnterTheScore()
    {
        var measurement = new DualSubMeasurement(
            // Same grid, different levels per position (see the falsifier note in the fact above).
            [OptimizationTestData.OneBin(50.0, 1.0, 0.0), OptimizationTestData.OneBin(50.0, 2.0, 0.0)],
            [OptimizationTestData.OneBin(50.0, 1.0, 0.0), OptimizationTestData.OneBin(50.0, 2.0, 0.0)]);

        IReadOnlyList<PositionResponse> totals = SubwooferModel.Combine(measurement, SubwooferSetting.Baseline);
        SpatialSummary summary = SpatialMetrics.Compute(totals);

        // Two positions, one frequency, and A = B, so the measured total is A + B = 2·A: levels are
        // 20·log10(2) = 6.020599913279624 dB and 20·log10(4) = 12.041199826559248 dB.
        // Against a 0 dB target: mean |deviation| = max = signed = (6.0206 + 12.0412)/2 = 9.030899869919436 dB.
        TargetCurveError error = TargetCurve.Deviation(summary, [0.0]);
        Assert.Equal(9.030899869919436, error.MeanAbsoluteDeviationDb, 12);
        Assert.Equal(9.030899869919436, error.MaxDeviationDb, 12);
        Assert.Equal(9.030899869919436, error.MeanSignedDeviationDb, 12);

        // A perfectly matching target reports 0 dB of deviation, while the uniformity score is a function of
        // the summary alone and cannot move: 1·3.010299956639812 + 1·6.020599913279624 + 0.5·0 + 0.5·3.010299956639812
        // = 3.010299956639812 + 6.020599913279624 + 1.505149978319906 = 10.536049848239342, with or without a target curve.
        Assert.Equal(0.0, TargetCurve.Deviation(summary, [9.030899869919436]).MeanAbsoluteDeviationDb, 12);
        Assert.Equal(10.536049848239342, ObjectiveFunction.Score(summary, 0.0, ObjectiveWeights.Default), 12);
    }
}
