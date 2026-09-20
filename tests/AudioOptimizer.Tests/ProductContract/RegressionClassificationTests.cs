namespace AudioOptimizer.Tests.ProductContract;

using AudioOptimizer.Core;
using AudioOptimizer.Optimization;
using AudioOptimizer.Simulation;

/// <summary>
/// A3 — the regression classification. The definitions live in <see cref="ScenarioClassifier"/>; these tests pin the
/// boundary cases and, above all, the swallowed case the shipped judge accepts: a near-optimal room degraded from
/// σ 0.05 to 0.45 dB. One fixture goes through the real measured chain with a deliberately bad recommendation so the
/// detector is exercised on numbers the product could actually produce, not only on constructed statistics.
/// </summary>
public class RegressionClassificationTests
{
    private static BandSpatialStats Stats(double stdDevDb) => new(0, 0, stdDevDb, 0, 0, 0, 0, 0, 0);

    private static RegressionMetrics Metrics(
        int index,
        BandSpatialStats before,
        BandSpatialStats after,
        bool recommendedSomething = true,
        double scoreBefore = 10.0,
        double scoreAfter = 10.0,
        double maxAchievedBoostDb = 0.0,
        double maxBoostLimitDb = 30.0) => new()
        {
            Index = index,
            Seed = RandomScenarioGenerator.SeedForIndex(index),
            ScenarioId = $"reg-{index:D4}",
            Before = before,
            After = after,
            ScoreBefore = scoreBefore,
            ScoreAfter = scoreAfter,
            MaxAchievedBoostDb = maxAchievedBoostDb,
            MaxBoostLimitDb = maxBoostLimitDb,
            RecommendedSomething = recommendedSomething,
            PhysicalResidualRatio = 1e-9,
            ValidSetting = true,
        };

    [Fact]
    public void The_near_optimal_degradation_the_shipped_judge_swallows_is_classified_as_a_regression()
    {
        // The documented gap: σ 0.05 → 0.45 is a 0.40 dB worsening, but the near-optimal exemption skips the
        // worsening check and −0.40 dB is inside the clearly-worse band, so the shipped judge passes it.
        RegressionScenario scenario = RandomScenarioGenerator.Create(3);
        RegressionMetrics metrics = Metrics(3, Stats(0.05), Stats(0.45));

        ScenarioJudgement shipped = RegressionRunner.Judge(scenario, metrics);
        Assert.True(shipped.Passed, "this fixture must reproduce the gap: the shipped judge accepts it");

        ScenarioClassification classification = ScenarioClassifier.Classify(shipped);
        Assert.Equal(ScenarioClass.Regressed, classification.Class);
        Assert.False(classification.Actionable);
        Assert.True(classification.NearOptimalNoOpExpected);
        Assert.True(classification.Failed);
        string reasons = string.Join(" ", classification.Reasons);
        Assert.Contains("0.400", reasons);
        Assert.Contains("0.25", reasons);
    }

    [Fact]
    public void The_actionable_worsening_boundary_is_the_documented_tolerance()
    {
        RegressionScenario scenario = RandomScenarioGenerator.Create(4);

        // Inside tolerance: actionable, not regressed, too small to be an improvement.
        ScenarioClassification inside = ScenarioClassifier.Classify(RegressionRunner.Judge(scenario,
            Metrics(4, Stats(3.0), Stats(3.0 + RegressionRunner.OptimizableWorseningToleranceDb - 0.01))));
        Assert.Equal(ScenarioClass.Unchanged, inside.Class);
        Assert.True(inside.Actionable);
        Assert.False(inside.Failed);

        // At the tolerance boundary the comparison is strict: > tolerance is a regression.
        ScenarioClassification beyond = ScenarioClassifier.Classify(RegressionRunner.Judge(scenario,
            Metrics(4, Stats(3.0), Stats(3.0 + RegressionRunner.OptimizableWorseningToleranceDb + 0.01))));
        Assert.Equal(ScenarioClass.Regressed, beyond.Class);
        Assert.True(beyond.Failed);
    }

    [Fact]
    public void A_score_ordering_violation_is_a_regression_even_when_the_spread_improved()
    {
        RegressionScenario scenario = RandomScenarioGenerator.Create(5);
        ScenarioClassification classification = ScenarioClassifier.Classify(RegressionRunner.Judge(scenario,
            Metrics(5, Stats(3.0), Stats(1.0), scoreAfter: 11.0)));

        Assert.Equal(ScenarioClass.Regressed, classification.Class);
        Assert.Contains("score regression", string.Join(" ", classification.Reasons));
    }

    [Fact]
    public void A_boost_contract_violation_is_its_own_category_with_priority_over_regression()
    {
        RegressionScenario scenario = RandomScenarioGenerator.Create(6);
        ScenarioClassification classification = ScenarioClassifier.Classify(RegressionRunner.Judge(scenario,
            Metrics(6, Stats(3.0), Stats(4.0), maxAchievedBoostDb: 30.5, maxBoostLimitDb: 30.0)));

        Assert.Equal(ScenarioClass.ConstraintViolation, classification.Class);
        Assert.Contains("product-contract", string.Join(" ", classification.Reasons));
        Assert.True(classification.Failed);
    }

    [Fact]
    public void A_hard_failure_takes_precedence_over_every_other_category()
    {
        RegressionScenario scenario = RandomScenarioGenerator.Create(7);
        RegressionMetrics nan = Metrics(7, Stats(3.0), Stats(4.0)) with { NonFiniteValues = true };
        ScenarioClassification classification = ScenarioClassifier.Classify(RegressionRunner.Judge(scenario, nan));

        Assert.Equal(ScenarioClass.HardFailure, classification.Class);
        Assert.Contains("NaN", string.Join(" ", classification.Reasons));
    }

    [Fact]
    public void Improvements_and_no_ops_are_counted_apart_and_the_totals_add_up()
    {
        RegressionScenario scenario = RandomScenarioGenerator.Create(8);
        var judgements = new[]
        {
            RegressionRunner.Judge(scenario, Metrics(8, Stats(3.0), Stats(1.0))),                                                // improved
            RegressionRunner.Judge(scenario, Metrics(8, Stats(3.0), Stats(2.95))),                                               // unchanged (actionable)
            RegressionRunner.Judge(scenario, Metrics(8, Stats(3.0), Stats(4.0))),                                                // regressed
            RegressionRunner.Judge(scenario, Metrics(8, Stats(0.10), Stats(0.10))),                                              // near-optimal no-op
            RegressionRunner.Judge(scenario, Metrics(8, Stats(3.0), Stats(3.0), maxAchievedBoostDb: 30.5)),                      // contract violation
            RegressionRunner.Judge(scenario, Metrics(8, Stats(3.0), Stats(3.0)) with { NonFiniteValues = true }),                // hard failure
        };

        ClassificationReport report = ScenarioClassifier.ClassifyAll(judgements);

        Assert.Equal(6, report.Total);
        Assert.Equal(5, report.Actionable);
        Assert.Equal(1, report.NearOptimal);
        Assert.Equal(1, report.Improved);
        Assert.Equal(1, report.Unchanged);
        Assert.Equal(1, report.Regressed);
        Assert.Equal(1, report.ConstraintViolations);
        Assert.Equal(1, report.HardFailures);
        Assert.Equal(1, report.NearOptimalNoOp);
        Assert.Equal(report.Total, report.Improved + report.Unchanged + report.Regressed + report.ConstraintViolations + report.HardFailures + report.NearOptimalNoOp);
        Assert.Equal(3, report.Failed);
    }

    [Fact]
    public void A_real_measured_room_with_a_physically_bad_recommendation_is_caught_by_the_classifier()
    {
        // Real chain, noise off, 3 points; the "recommendation" is a physically bad correction (opposite polarity,
        // +6 dB, phase 0) evaluated on the SAME measured A/B through the shipped model. This is the real-data
        // regression fixture: the numbers come from the measurement, not from the test's statistics.
        DualSubMeasurement input = LabFixtures.Measure(LabFixtures.SilentConfig, LabFixtures.ThreePoints());
        SpatialSummary before = SpatialMetrics.Compute(SubwooferModel.Combine(input, SubwooferSetting.Baseline));

        // Deterministically the worst of four physically distinct bad corrections; the point is a recommendation
        // the product could actually emit, not a hand-picked number.
        SubwooferSetting[] candidates =
        [
            SubwooferSetting.FromDegrees(0.0, 0.0, -1),          // wrong polarity
            SubwooferSetting.FromDegrees(6.0, 180.0, 1),         // full-scale gain at antiphase
            SubwooferSetting.FromDegrees(0.0, 0.0, 1, 0.010),    // 10 ms of misalignment
            SubwooferSetting.FromDegrees(-6.0, 0.0, 1),          // full-scale attenuation
        ];
        SubwooferSetting bad = candidates
            .OrderByDescending(setting => SpatialMetrics.Compute(SubwooferModel.Combine(input, setting)).MeanStdDevDb)
            .First();
        SpatialSummary after = SpatialMetrics.Compute(SubwooferModel.Combine(input, bad));
        Assert.True(after.MeanStdDevDb > before.MeanStdDevDb + RegressionRunner.OptimizableWorseningToleranceDb,
            $"the fixture's bad setting is not bad enough: σ {before.MeanStdDevDb:F3} → {after.MeanStdDevDb:F3} dB");

        RegressionMetrics metrics = Metrics(9,
            BandSpatialStats.Of(before),
            BandSpatialStats.Of(after),
            scoreBefore: 10.0,
            scoreAfter: 12.0);
        ScenarioJudgement judgement = RegressionRunner.Judge(RandomScenarioGenerator.Create(9), metrics);
        Assert.False(judgement.Passed, "the shipped judge must catch a worsening of this size on an actionable room");

        ScenarioClassification classification = ScenarioClassifier.Classify(judgement);
        Assert.Equal(ScenarioClass.Regressed, classification.Class);
        Assert.True(classification.Actionable);
    }
}
