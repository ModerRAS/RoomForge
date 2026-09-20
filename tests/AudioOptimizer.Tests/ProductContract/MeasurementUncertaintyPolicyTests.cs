namespace AudioOptimizer.Tests.ProductContract;

using AudioOptimizer.Optimization;
using AudioOptimizer.Simulation;

/// <summary>
/// A2 — measurement uncertainty → score uncertainty → constraint margin → recommendation safety, fixed by the one
/// policy this product ships: a fixed safety margin. The unit cases are analytic and fast; the adversarial case is
/// the E-F1 room through the real chain, where the same physical correction is applied or declined depending on the
/// microphone-noise seed — the unstable flip the margin removes. Both E-F1 realizations are measured once and the
/// plain result is compared with the margin result on the same input, so the only variable is the policy.
/// </summary>
public class MeasurementUncertaintyPolicyTests
{
    [Fact]
    public void A_zero_margin_is_the_shipped_search_unchanged()
    {
        DualSubMeasurement room = OptimizationTestData.Room();
        var options = new OptimizerOptions();
        OptimizerResult plain = SubwooferOptimizer.Search(room, options);

        UncertaintyPolicyResult policy = RecommendationUncertaintyPolicy.Search(room, options, 0.0);

        Assert.Equal(plain.Verdict, policy.Result.Verdict);
        Assert.Equal(plain.Recommended, policy.Result.Recommended);
        Assert.Equal(plain.ScoreAfter, policy.Result.ScoreAfter, 12);
        Assert.Equal(plain.Constraint.MaxBoostLimitDb, policy.Result.Constraint.MaxBoostLimitDb, 12);
        Assert.Equal(0.0, policy.SafetyMarginDb, 12);
        Assert.False(policy.MarginDecidedOutcome);
        Assert.Contains("no safety margin", policy.PolicySummary);
    }

    [Fact]
    public void The_margin_bites_and_the_final_recommendation_stays_inside_the_enforced_limit()
    {
        // The pinned conflicted room: without a margin the recommendation spends 2.974091 dB of a 3 dB limit.
        DualSubMeasurement conflicted = OptimizationTestData.ConflictedRoom(6.0, 0.0);
        var noPenalty = new ObjectiveWeights(MeanStdDev: 1.0, MeanP90P10: 1.0, PeakPenalty: 0.0, NullPenalty: 0.0);
        var options = new OptimizerOptions { MaxBoostLimitDb = 3.0, Weights = noPenalty };

        UncertaintyPolicyResult plain = RecommendationUncertaintyPolicy.Search(conflicted, options, 0.0);
        UncertaintyPolicyResult margined = RecommendationUncertaintyPolicy.Search(conflicted, options, RecommendationUncertaintyPolicy.FixedSafetyMarginDb);

        Assert.Equal(2.974091, plain.FinalRecommendationBoostDb!.Value, 6);
        Assert.Equal(3.0, margined.RequestedLimitDb, 12);
        Assert.Equal(2.5, margined.EnforcedLimitDb, 12);
        Assert.True(margined.FinalRecommendationBoostDb <= margined.EnforcedLimitDb + BoostPolicy.BoostEpsilonDb,
            $"final recommendation spent {margined.FinalRecommendationBoostDb:F3} dB against the enforced {margined.EnforcedLimitDb:F3} dB");
        Assert.True(margined.FinalRecommendationBoostDb < plain.FinalRecommendationBoostDb);
        Assert.True(margined.MarginDecidedOutcome, "the margin must have removed the candidate the plain search returned");
        Assert.True(double.IsFinite(margined.Result.ScoreAfter));
    }

    [Fact]
    public void A_margin_larger_than_the_limit_is_an_honest_no_solution_not_an_exception()
    {
        DualSubMeasurement room = OptimizationTestData.Room();
        UncertaintyPolicyResult policy = RecommendationUncertaintyPolicy.Search(room, new OptimizerOptions { MaxBoostLimitDb = 3.0 }, 4.0);

        Assert.Equal(-1.0, policy.EnforcedLimitDb, 12);
        Assert.Equal(-1.0, policy.Result.Constraint.MaxBoostLimitDb, 12);
        Assert.Contains("the enforced margin limit bound the search", policy.PolicySummary);
        if (policy.FinalRecommendationBoostDb is { } boost)
            Assert.True(boost <= -1.0 + BoostPolicy.BoostEpsilonDb, $"returned setting still spends {boost:F3} dB");
    }

    [Fact]
    public void Negative_or_non_finite_margins_are_rejected()
    {
        DualSubMeasurement room = OptimizationTestData.Room();
        var options = new OptimizerOptions();

        Assert.Throws<ArgumentOutOfRangeException>(() => RecommendationUncertaintyPolicy.Search(room, options, -0.1));
        Assert.Throws<ArgumentOutOfRangeException>(() => RecommendationUncertaintyPolicy.Search(room, options, double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => RecommendationUncertaintyPolicy.Search(room, options, double.PositiveInfinity));
    }

    [Fact]
    public void The_fixed_safety_margin_removes_the_seed_driven_flip_on_the_real_chain()
    {
        // E-F1: one room, one canonical correction (0 dB / 0° / +1 / 2.0 ms), two microphone-noise realizations at
        // −60 dBFS. The measured boost of that correction straddles the 30 dB limit, so the plain search applies it
        // in one realization and declines it in the other. Everything below is measured, never simulated physics.
        (DualSubMeasurement first, GroundTruth truth) = LabFixtures.MeasureWithTruth(LabFixtures.SeedConfig(20260101), LabFixtures.NinePoints());
        DualSubMeasurement second = LabFixtures.Measure(LabFixtures.SeedConfig(20260103), LabFixtures.NinePoints());

        double firstCanonicalBoost = LabFixtures.BoostOf(first, LabFixtures.CanonicalDelay);
        double secondCanonicalBoost = LabFixtures.BoostOf(second, LabFixtures.CanonicalDelay);

        OptimizerResult plainFirst = SubwooferOptimizer.Search(first, LabFixtures.Capability30);
        OptimizerResult plainSecond = SubwooferOptimizer.Search(second, LabFixtures.Capability30);

        // The instability, reproduced: same room, same correction, different verdicts.
        Assert.Equal(OptimizationVerdict.Improved, plainFirst.Verdict);
        Assert.Equal(OptimizationVerdict.NegligibleImprovement, plainSecond.Verdict);
        Assert.True(firstCanonicalBoost <= 30.0, $"seed 20260101's canonical boost is {firstCanonicalBoost:F4} dB");
        Assert.True(secondCanonicalBoost > 30.0, $"seed 20260103's canonical boost is {secondCanonicalBoost:F4} dB");

        // The policy: the same two realizations, the same margin, one verdict.
        UncertaintyPolicyResult policyFirst = RecommendationUncertaintyPolicy.Search(first, LabFixtures.Capability30, 0.5);
        UncertaintyPolicyResult policySecond = RecommendationUncertaintyPolicy.Search(second, LabFixtures.Capability30, 0.5);

        Assert.Equal(OptimizationVerdict.NegligibleImprovement, policyFirst.Result.Verdict);
        Assert.Equal(OptimizationVerdict.NegligibleImprovement, policySecond.Result.Verdict);
        Assert.Equal(policyFirst.Result.Verdict, policySecond.Result.Verdict);
        Assert.Equal(policyFirst.Result.Recommended, policySecond.Result.Recommended);

        // The decline is caused by the enforced limit in both realizations (the canonical setting is above 29.5 dB);
        // for seed 20260103 the plain search also declined, so the margin changed which candidate was returned,
        // not whether that seed's canonical setting was legal.
        Assert.True(firstCanonicalBoost > policyFirst.EnforcedLimitDb, $"{firstCanonicalBoost:F4} dB is not above {policyFirst.EnforcedLimitDb:F3} dB");
        Assert.True(secondCanonicalBoost > policySecond.EnforcedLimitDb, $"{secondCanonicalBoost:F4} dB is not above {policySecond.EnforcedLimitDb:F3} dB");
        Assert.True(policyFirst.MarginDecidedOutcome);
        Assert.True(policySecond.MarginDecidedOutcome);
        Assert.Equal(29.5, policyFirst.EnforcedLimitDb, 12);

        // The documented limitation, as numbers: the policy declines a correction worth ~2.2 dB of true improvement.
        // The value is measured on the noiseless ground-truth room (test-side evaluation only, never an optimizer input).
        DualSubMeasurement truthInput = truth.AsOptimizerInput();
        double declinedValue = LabFixtures.ScoreOf(truthInput, SubwooferSetting.Baseline)
            - LabFixtures.ScoreOf(truthInput, LabFixtures.CanonicalDelay);
        Assert.True(declinedValue > 2.0, $"the fixture's canonical correction is only worth {declinedValue:F3} score dB on the truth");
    }

    [Fact]
    public void The_named_product_limitation_is_shipped_and_does_not_claim_the_flip_is_fixed()
    {
        string limitation = RecommendationUncertaintyPolicy.ProductLimitation;

        Assert.Contains("PRODUCT LIMITATION", limitation);
        Assert.Contains("fixed safety margin", limitation);
        Assert.Contains("declined in", limitation);
        Assert.Contains("not a measurement-uncertainty fix", limitation);
        Assert.DoesNotContain("fixed the measurement", limitation);
    }
}
