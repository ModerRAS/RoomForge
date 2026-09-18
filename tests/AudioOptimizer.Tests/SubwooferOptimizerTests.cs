using AudioOptimizer.Optimization;

namespace AudioOptimizer.Tests;

/// <summary>
/// The anti-fake-pass set. The two assertions that matter most: the staged search matches an exhaustive
/// reference grid at least as fine as its own final step, and a constructed optimum is recovered exactly.
/// </summary>
public class SubwooferOptimizerTests
{
    private static readonly OptimizerOptions Options = new();

    /// <summary>Options whose grid (and therefore the brute-force reference) is as fine as the search's final step.</summary>
    private static OptimizerOptions FineReference => Options with { GainCoarseStepDb = 0.1, PhaseCoarseStepDegrees = 1.0 };

    [Fact]
    public void StagedSearchMatchesAnExhaustiveReferenceGrid()
    {
        // Reference grid 0.1 dB × 1° × 2 polarities = 121 × 181 × 2 = 43,802 candidates, i.e. exactly the
        // search's own final resolution (GainFineStepDb 0.1, PhaseFineStepDegrees 1). Epsilon = 1e-9 dB.
        DualSubMeasurement room = OptimizationTestData.Room();
        OptimizerCandidate reference = SubwooferOptimizer.BruteForce(room, FineReference)!;
        OptimizerResult staged = SubwooferOptimizer.Search(room, Options);

        double gap = staged.Best!.Score - reference.Score;
        Assert.True(gap <= 1e-9, $"staged {staged.Best.Score:R} vs reference {reference.Score:R} (gap {gap:R})");
        // Measured: staged 4.497669687 at (0.900 dB, 136.000°, +1); reference the same score and setting; gap 1.78e-15.
        Assert.Equal(4.497669687, reference.Score, 9);
        Assert.Equal(reference.Setting.GainDb, staged.Best.Setting.GainDb, 9);
        Assert.Equal(reference.Setting.PhaseDegrees, staged.Best.Setting.PhaseDegrees, 9);
        Assert.Equal(reference.Setting.Polarity, staged.Best.Setting.Polarity);
    }

    [Fact]
    public void AConstructedOptimumIsRecoveredExactly()
    {
        // A = T − c*·B makes the predicted total exactly a unit phasor at (3.0 dB, 40°), so σ = 0, every
        // penalty is 0 and the score is exactly 0 there — the global minimum, since the score is a sum of
        // non-negative terms. The grids are anchored at 0, so 3.0 dB and 40° are grid points exactly.
        DualSubMeasurement measurement = OptimizationTestData.KnownOptimum(3.0, 40.0);
        OptimizerResult result = SubwooferOptimizer.Search(measurement, Options);

        Assert.NotNull(result.Best);
        Assert.Equal(3.0, result.Best!.Setting.GainDb, 9);
        Assert.Equal(40.0, result.Best.Setting.PhaseDegrees, 9);
        Assert.Equal(1, result.Best.Setting.Polarity);
        Assert.True(result.Best.Score < 1e-12, $"score {result.Best.Score:R} should be 0");
        Assert.Equal(0.0, result.After.MeanStdDevDb, 9);
        Assert.Equal(OptimizationVerdict.Improved, result.Verdict);

        // The same point is the exhaustive reference's optimum, so the recovery is not a local artifact.
        OptimizerCandidate reference = SubwooferOptimizer.BruteForce(measurement, FineReference)!;
        Assert.Equal(3.0, reference.Setting.GainDb, 9);
        Assert.Equal(40.0, reference.Setting.PhaseDegrees, 9);
        Assert.Equal(reference.Score, result.Best.Score, 12);
    }

    [Fact]
    public void TheJointStageConfirmsTheOptimumInsteadOfWanderingOffIt()
    {
        // Falsifier: the intended optimum is exactly on the coarse 2-D mesh, so the coarse pass must land on
        // it and the local refine must not move unless it finds something strictly better. 0 is the floor of a
        // non-negative objective, so it can only confirm.
        OptimizerResult result = SubwooferOptimizer.Search(OptimizationTestData.KnownOptimum(3.0, 40.0), Options);
        OptimizerStage coarse = result.Trace.Single(s => s.Stage.StartsWith("joint 2-D coarse", StringComparison.Ordinal));
        OptimizerStage local = result.Trace[^1];

        Assert.Equal(0.0, coarse.BestScore, 12);
        Assert.Equal(3.0, coarse.BestSetting.GainDb, 9);
        Assert.Equal(40.0, coarse.BestSetting.PhaseDegrees, 9);
        Assert.True(local.BestScore <= coarse.BestScore, $"local {local.BestScore:R} regressed against coarse {coarse.BestScore:R}");
        Assert.Equal(coarse.BestScore, local.BestScore, 12);
        Assert.Equal(coarse.BestSetting.GainDb, local.BestSetting.GainDb, 9);
        Assert.Equal(coarse.BestSetting.PhaseDegrees, local.BestSetting.PhaseDegrees, 9);
    }

    [Fact]
    public void StagesNeverRegressAndTheSearchActuallyImproves()
    {
        // Non-regression on a scenario with a NON-ZERO optimum, so it cannot pass trivially by every stage
        // returning the same thing: the room's optimum scores 4.497669687, not 0.
        OptimizerResult result = SubwooferOptimizer.Search(OptimizationTestData.Room(), Options);
        Assert.Equal(8, result.Trace.Count);
        Assert.NotEqual(0.0, result.Trace[0].BestScore);

        int improvements = 0;
        for (int i = 1; i < result.Trace.Count; i++)
        {
            Assert.True(result.Trace[i].BestScore <= result.Trace[i - 1].BestScore,
                $"stage '{result.Trace[i].Stage}' scored {result.Trace[i].BestScore:R} after '{result.Trace[i - 1].Stage}' scored {result.Trace[i - 1].BestScore:R}");
            if (result.Trace[i].BestScore < result.Trace[i - 1].BestScore - 1e-9) improvements++;
        }

        Assert.True(improvements >= 3, $"expected real progress across stages, found {improvements} improving stages");
        // Measured progression: 4.963894 → 4.963894 → 4.963894 → 4.560798 → 4.510456 → 4.509974 → 4.509974 → 4.497670.
        Assert.Equal(4.963894210, result.Trace[0].BestScore, 9);
        Assert.Equal(4.497669687, result.Trace[^1].BestScore, 9);
    }

    [Fact]
    public void TheJointStageIsWhatDeliversTheFinalImprovement()
    {
        // The 1-D stages stall at 4.509974 (gain fine then phase fine); the 2-D stage is what reaches the
        // optimum. Evidence that the joint stage is load-bearing and not decoration.
        OptimizerResult result = SubwooferOptimizer.Search(OptimizationTestData.Room(), Options);
        double afterOneDimensionalStages = result.Trace[5].BestScore;
        Assert.Equal("phase fine 1°", result.Trace[5].Stage);
        Assert.Equal(4.509974, afterOneDimensionalStages, 6);
        Assert.True(result.Trace[^1].BestScore < afterOneDimensionalStages,
            $"the 2-D stage must improve on the stalled 1-D result ({afterOneDimensionalStages:R} → {result.Trace[^1].BestScore:R})");
    }

    [Fact]
    public void SpatiallyIdenticalSubsReportNoImprovementInsteadOfABogusSetting()
    {
        DualSubMeasurement room = OptimizationTestData.Room(5, 30);
        OptimizerResult result = SubwooferOptimizer.Search(new DualSubMeasurement(room.A, room.A), Options);

        // With A = B every setting scales each frequency by the same complex factor across positions, so the
        // spread is identical for all of them: the score cannot move and the measured setting is the answer.
        Assert.Equal(OptimizationVerdict.NegligibleImprovement, result.Verdict);
        Assert.Equal(0.0, result.Recommended!.GainDb, 12);
        Assert.Equal(0.0, result.Recommended.PhaseDegrees, 12);
        Assert.Equal(1, result.Recommended.Polarity);
        Assert.Equal(0.0, result.Recommended.DelaySeconds, 12);
        Assert.Equal(0.0, result.ScoreBefore - result.ScoreAfter, 12);
        Assert.Equal(2.660204814871, result.ScoreBefore, 9);

        // Absolute before/after numbers, not a percentage: identical in every one.
        Assert.Equal(0.426532678, result.Before.MeanStdDevDb, 9);
        Assert.Equal(1.203983148, result.Before.MeanRangeDb, 9);
        Assert.Equal(1.203983148, result.Before.MeanP90P10Db, 9);
        Assert.Equal(result.Before.MeanStdDevDb, result.After.MeanStdDevDb, 12);
        Assert.Equal(result.Before.MeanRangeDb, result.After.MeanRangeDb, 12);
        Assert.Equal(result.Before.MeanP90P10Db, result.After.MeanP90P10Db, 12);

        // And the verdict is structured per frequency: all 30 bins are position-dominated.
        Assert.Equal(30, result.Diagnosis.Count);
        Assert.All(result.Diagnosis, d => Assert.True(d.PositionDominated));
        Assert.All(result.Diagnosis, d => Assert.Equal(0.0, d.ImprovementDb, 12));
    }

    [Fact]
    public void TheConstraintBindsWhenTheUnconstrainedOptimumBoostsTooMuch()
    {
        // Case demonstrated: BINDING. The penalty weights are set to 0 here on purpose, so the hard rejection
        // is the only thing that can stop a boost-happy setting — if w3 were carrying the load, this would prove
        // nothing about the rejection.
        var noPenalty = new ObjectiveWeights(MeanStdDev: 1.0, MeanP90P10: 1.0, PeakPenalty: 0.0, NullPenalty: 0.0);
        DualSubMeasurement conflicted = OptimizationTestData.ConflictedRoom(6.0, 0.0);

        OptimizerResult unconstrained = SubwooferOptimizer.Search(conflicted, Options with { MaxBoostLimitDb = 1e9, Weights = noPenalty });
        Assert.True(unconstrained.Best!.MaxBoostVsBaselineDb > 3.0,
            $"the unconstrained optimum must exceed the default 3 dB limit, achieved {unconstrained.Best.MaxBoostVsBaselineDb:R}");
        Assert.Equal(16.7154, unconstrained.Best.MaxBoostVsBaselineDb, 4);

        OptimizerResult limited = SubwooferOptimizer.Search(conflicted, Options with { MaxBoostLimitDb = 3.0, Weights = noPenalty });
        Assert.NotNull(limited.Recommended);
        Assert.True(limited.Constraint.Binding, "the constraint changed the outcome, so Binding must be true");
        Assert.True(limited.Constraint.CandidatesRejected > 0, $"rejected {limited.Constraint.CandidatesRejected}");
        // Measured: rejected 820 of 1066, recommended 0.700 dB / 6.000°, achieved 2.974091 dB ≤ 3.
        Assert.Equal(820, limited.Constraint.CandidatesRejected);
        Assert.Equal(1066, limited.Constraint.CandidatesEvaluated);
        Assert.Equal(2.974091, limited.Constraint.MaxAchievedBoostDb!.Value, 6);
        Assert.True(limited.Constraint.MaxAchievedBoostDb!.Value <= 3.0, "a returned setting may never exceed the limit");
        Assert.Equal(0.700, limited.Recommended!.GainDb, 9);
        Assert.Equal(6.000, limited.Recommended.PhaseDegrees, 9);
    }

    [Fact]
    public void TheConstraintNeverFiresWhenTheLimitIsGenerous()
    {
        // Case demonstrated: NON-BINDING, never fired (rejected == 0). A constraint test that only exercises
        // this path would prove the mechanism exists but not that it bites — hence both cases.
        DualSubMeasurement room = OptimizationTestData.Room();
        OptimizerResult generous = SubwooferOptimizer.Search(room, Options with { MaxBoostLimitDb = 6.0 });

        Assert.Equal(0, generous.Constraint.CandidatesRejected);
        Assert.False(generous.Constraint.Binding);
        Assert.Equal(-0.004870, generous.Constraint.MaxAchievedBoostDb!.Value, 6);
        // Nothing was taken away, so the result is the unconstrained one.
        Assert.Equal(4.497669687, generous.ScoreAfter, 9);
        Assert.Equal(0.900, generous.Recommended!.GainDb, 9);

        // A limit that does filter candidates without changing the outcome is reported as filtered but not binding.
        OptimizerResult filtering = SubwooferOptimizer.Search(room, Options with { MaxBoostLimitDb = 3.0 });
        Assert.True(filtering.Constraint.CandidatesRejected > 0);
        Assert.False(filtering.Constraint.Binding);
        Assert.Equal(filtering.ScoreAfter, generous.ScoreAfter, 12);
    }

    [Fact]
    public void EveryCandidateRejectedIsStructuredDataNotACrashOrASilentFallback()
    {
        // The measured setting achieves exactly 0 dB of boost, so a non-negative limit always has a legal
        // candidate; reaching the all-rejected state needs a limit below the most attenuating candidate.
        OptimizerResult result = SubwooferOptimizer.Search(OptimizationTestData.Room(), Options with { MaxBoostLimitDb = -7.0 });

        Assert.Equal(OptimizationVerdict.NoSettingWithinBoostLimit, result.Verdict);
        Assert.Null(result.Recommended);
        Assert.Null(result.Best);
        Assert.True(result.Constraint.Binding);
        Assert.Equal(result.Constraint.CandidatesEvaluated, result.Constraint.CandidatesRejected);
        Assert.Null(result.Constraint.MaxAchievedBoostDb);
        // And it says how far the room is from obeying the limit, as data.
        Assert.Equal(-6.792489, result.Constraint.LeastAchievedBoostDb, 6);
        // Nothing was returned, so nothing changed: after == before, in absolute numbers.
        Assert.Equal(result.ScoreBefore, result.ScoreAfter, 12);
        Assert.Equal(result.Before.MeanStdDevDb, result.After.MeanStdDevDb, 12);
    }

    [Fact]
    public void SearchResolutionIsReportedSeparatelyFromRecommendablePrecision()
    {
        DualSubMeasurement room = OptimizationTestData.Room();
        OptimizerResult fine = SubwooferOptimizer.Search(room, Options);
        Assert.Equal(0.900, fine.Best!.Setting.GainDb, 9);
        Assert.Equal(136.000, fine.Best.Setting.PhaseDegrees, 9);
        Assert.Equal(0.900, fine.Recommended!.GainDb, 9);

        // Ask for a coarser report grid: the search still refines at 0.1 dB / 1°, but the recommendation is
        // snapped to something the hardware can hold, and it stays legal.
        OptimizerResult coarseReport = SubwooferOptimizer.Search(room, Options with { ReportGainStepDb = 0.5, ReportPhaseStepDegrees = 5.0 });
        Assert.Equal(0.900, coarseReport.Best!.Setting.GainDb, 9);
        Assert.Equal(0.0, coarseReport.Recommended!.GainDb % 0.5, 9);
        Assert.Equal(0.0, coarseReport.Recommended.PhaseDegrees % 5.0, 9);
        Assert.True(coarseReport.Constraint.MaxAchievedBoostDb!.Value <= 3.0);
    }

    [Fact]
    public void DelayIsSearchedOnlyWhenAskedForAndNeverWorsensTheResult()
    {
        DualSubMeasurement room = OptimizationTestData.Room();
        OptimizerResult without = SubwooferOptimizer.Search(room, Options);
        OptimizerResult with = SubwooferOptimizer.Search(room, Options with { IncludeDelay = true });

        // The delay grid contains 0 s, so the search with delay can never do worse than the search without.
        Assert.True(with.Best!.Score <= without.Best!.Score + 1e-9, $"with delay {with.Best.Score:R} vs without {without.Best.Score:R}");
        Assert.True(with.Recommended!.DelaySeconds >= 0.0 && with.Recommended.DelaySeconds <= 0.010, $"delay {with.Recommended.DelaySeconds:R} s");
        Assert.True(with.Constraint.MaxAchievedBoostDb!.Value <= 3.0);

        // With delay off, no delay can ever be returned.
        Assert.Equal(0.0, without.Recommended!.DelaySeconds, 12);
    }

    [Fact]
    public void LevelChangeIsReportedNextToUniformity()
    {
        // The objective is level-blind by design, so the level change it cannot see is reported as data:
        // the room's recommended setting improves σ by 0.145676 dB while sitting 4.294121 dB quieter.
        OptimizerResult result = SubwooferOptimizer.Search(OptimizationTestData.Room(), Options);
        Assert.Equal(-4.294121, result.LevelChangeDb, 6);
        Assert.Equal(0.922710547, result.Before.MeanStdDevDb, 9);
        Assert.Equal(0.777034942, result.After.MeanStdDevDb, 9);
        Assert.Equal(2.649896287, result.Before.MeanRangeDb, 9);
        Assert.Equal(2.196894379, result.After.MeanRangeDb, 9);
        Assert.Equal(2.649896287, result.Before.MeanP90P10Db, 9);
        Assert.Equal(2.196894379, result.After.MeanP90P10Db, 9);
        Assert.Equal(4.963894210, result.ScoreBefore, 9);
        Assert.Equal(4.497669687, result.ScoreAfter, 9);
    }

    [Fact]
    public void TheTargetCurveIsReportedBesideTheResultAndNeverInsideIt()
    {
        DualSubMeasurement room = OptimizationTestData.Room();
        OptimizerResult plain = SubwooferOptimizer.Search(room, Options);
        Assert.Null(plain.TargetBefore);
        Assert.Null(plain.TargetAfter);

        OptimizerResult targeted = SubwooferOptimizer.Search(room, Options with { TargetDb = new double[40] });
        Assert.NotNull(targeted.TargetBefore);
        Assert.NotNull(targeted.TargetAfter);
        // Identical uniformity numbers with and without a target curve: it cannot enter the score.
        Assert.Equal(plain.ScoreBefore, targeted.ScoreBefore, 12);
        Assert.Equal(plain.ScoreAfter, targeted.ScoreAfter, 12);
        Assert.Equal(plain.Recommended!.GainDb, targeted.Recommended!.GainDb, 12);
    }
}
