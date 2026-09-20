namespace AudioOptimizer.Tests.ProductContract;

using AudioOptimizer.Optimization;
using AudioOptimizer.Simulation;

/// <summary>
/// A1 — the two operating modes and the three boosting roles. The unit cases run on the synthetic conflicted room
/// (fast, deterministic, and already pinned by <c>SubwooferOptimizerTests</c>); the contract-violation case runs on
/// the real measurement chain so the violation being detected is one a product could actually ship.
/// </summary>
public class BoostPolicyTests
{
    [Fact]
    public void Product_safety_clamps_the_limit_to_three_db_and_capability_keeps_the_configured_limit()
    {
        var negotiated = new OptimizerOptions { MaxBoostLimitDb = 30.0 };

        Assert.Equal(30.0, BoostPolicy.EffectiveLimitDb(OptimizerOperatingMode.Capability, 30.0), 12);
        Assert.Equal(3.0, BoostPolicy.EffectiveLimitDb(OptimizerOperatingMode.ProductSafety, 30.0), 12);
        // A product-safety request below the ceiling is honoured as-is: the ceiling is a maximum, not a target.
        Assert.Equal(2.0, BoostPolicy.EffectiveLimitDb(OptimizerOperatingMode.ProductSafety, 2.0), 12);
        Assert.Equal(2.0, BoostPolicy.ModeOptions(new OptimizerOptions { MaxBoostLimitDb = 2.0 }, OptimizerOperatingMode.ProductSafety).MaxBoostLimitDb, 12);
        Assert.Equal(30.0, BoostPolicy.ModeOptions(negotiated, OptimizerOperatingMode.Capability).MaxBoostLimitDb, 12);
        Assert.Equal(3.0, BoostPolicy.ModeOptions(negotiated, OptimizerOperatingMode.ProductSafety).MaxBoostLimitDb, 12);

        Assert.Throws<ArgumentOutOfRangeException>(() => BoostPolicy.EffectiveLimitDb(OptimizerOperatingMode.Capability, double.NaN));
    }

    [Fact]
    public void The_theoretical_optimum_legal_candidate_and_final_recommendation_are_three_separate_numbers()
    {
        // The pinned conflicted room: the unconstrained optimum needs 16.58 dB, the 3 dB limit returns a
        // compromise. Reusing the shipped fixture keeps this test tied to the numbers the search tests pin.
        DualSubMeasurement conflicted = OptimizationTestData.ConflictedRoom(6.0, 0.0);
        var noPenalty = new ObjectiveWeights(MeanStdDev: 1.0, MeanP90P10: 1.0, PeakPenalty: 0.0, NullPenalty: 0.0);
        var capabilityOptions = new OptimizerOptions { MaxBoostLimitDb = 1e9, Weights = noPenalty };
        var productOptions = new OptimizerOptions { MaxBoostLimitDb = 3.0, Weights = noPenalty };

        BoostRoles roles = BoostPolicy.Compare(conflicted, capabilityOptions, productOptions);

        Assert.NotNull(roles.TheoreticalOptimum);
        Assert.NotNull(roles.LegalCandidate);
        Assert.NotNull(roles.FinalRecommendation);
        Assert.Equal(16.5774, roles.TheoreticalBoostDb!.Value, 4);
        Assert.True(roles.LegalBoostDb!.Value <= 3.0 + BoostPolicy.BoostEpsilonDb, $"legal candidate spent {roles.LegalBoostDb:F3} dB");
        Assert.True(roles.FinalRecommendationBoostDb!.Value <= 3.0 + BoostPolicy.BoostEpsilonDb, $"final recommendation spent {roles.FinalRecommendationBoostDb:F3} dB");

        // The roles are genuinely distinct answers: the theoretical optimum is unreachable in product mode.
        Assert.True(roles.TheoreticalBoostDb > roles.LegalBoostDb);
        Assert.Equal(0.700, roles.FinalRecommendation.GainDb, 9);
        Assert.Equal(6.000, roles.FinalRecommendation.PhaseDegrees, 9);
        Assert.Equal(2.974091, roles.FinalRecommendationBoostDb.Value, 6);
    }

    [Fact]
    public void A_final_recommendation_over_the_product_limit_is_a_true_contract_violation_on_real_measured_data()
    {
        // Real chain, noise off: 3 of the 9 z = 0 microphones. The optimization itself is diagnostic (30 dB) —
        // this test is about the contract layer over it, not about the search.
        DualSubMeasurement input = LabFixtures.Measure(LabFixtures.SilentConfig, LabFixtures.ThreePoints());
        OptimizerResult capability = SubwooferOptimizer.Search(input, LabFixtures.Capability30);

        Assert.Equal(OptimizationVerdict.Improved, capability.Verdict);
        Assert.NotNull(capability.Recommended);

        // Against its own 30 dB limit the result is fine; against product safety the same recommendation is a
        // violation — the mode decides the contract, not the result text.
        BoostContractReport asCapability = BoostPolicy.Verify(input, capability, OptimizerOperatingMode.Capability);
        Assert.False(asCapability.Violated);
        Assert.True(asCapability.ReportedValueDescribesFinalRecommendation, asCapability.Status);
        Assert.Equal(30.0, asCapability.EffectiveLimitDb, 12);

        BoostContractReport asProduct = BoostPolicy.Verify(input, capability, OptimizerOperatingMode.ProductSafety);
        Assert.Equal(3.0, asProduct.EffectiveLimitDb, 12);
        Assert.True(asProduct.Violated);
        Assert.Contains("contract violation", asProduct.Status);
        Assert.True(asProduct.FinalRecommendationBoostDb > 3.0);

        // And the violation is detected on the recommendation itself, not by quoting a rejected candidate:
        // substitute a large-boost setting as the recommendation; the check re-measures it from the input.
        SubwooferSetting overLimit = SubwooferSetting.FromDegrees(6.0, 0.0);
        double overBoost = LabFixtures.BoostOf(input, overLimit);
        Assert.True(overBoost > 3.0, $"the fixture setting only boosts {overBoost:F3} dB, so it cannot demonstrate a violation");

        OptimizerResult violating = capability with { Recommended = overLimit };
        BoostContractReport detected = BoostPolicy.Verify(input, violating, OptimizerOperatingMode.ProductSafety);
        Assert.True(detected.Violated);
        Assert.Equal(overBoost, detected.FinalRecommendationBoostDb!.Value, 9);
        Assert.Contains("contract violation", detected.Status);
    }

    [Fact]
    public void The_reported_achieved_boost_describes_the_final_recommendation_not_the_best_legal_candidate()
    {
        // L10, reproduced deterministically: the search legal candidate is (0.9 dB, 136.0°) and the report grid
        // 0.5 dB × 5° snaps the recommendation to (0.5 dB, 130.0°). The two settings have different achieved
        // boosts, so a report that quotes the search candidate is describing a setting the product will never hold.
        // Fixture-first: this test is red until the L10 diff lands in SubwooferOptimizer.BuildResult.
        DualSubMeasurement room = OptimizationTestData.Room();
        var coarseReport = new OptimizerOptions { MaxBoostLimitDb = 3.0, ReportGainStepDb = 0.5, ReportPhaseStepDegrees = 5.0 };
        OptimizerResult result = SubwooferOptimizer.Search(room, coarseReport);

        Assert.Equal(OptimizationVerdict.Improved, result.Verdict);
        Assert.NotNull(result.Best);
        Assert.NotNull(result.Recommended);
        Assert.Equal(0.900, result.Best!.Setting.GainDb, 9);
        Assert.Equal(136.000, result.Best.Setting.PhaseDegrees, 9);
        Assert.Equal(0.500, result.Recommended!.GainDb, 9);
        Assert.Equal(130.000, result.Recommended.PhaseDegrees, 9);

        double legalBoost = result.Best.MaxBoostVsBaselineDb;
        double finalBoost = BoostPolicy.AchievedBoostDb(room, result.Recommended)!.Value;

        // The fixture really separates the two numbers: −0.0049 dB for the search candidate against +0.0129 dB
        // for the snapped recommendation.
        Assert.NotEqual(legalBoost, finalBoost);
        Assert.Equal(-0.004870, legalBoost, 6);
        Assert.Equal(0.012945, finalBoost, 6);

        // The reported number must describe the final recommendation.
        Assert.Equal(finalBoost, result.Constraint.MaxAchievedBoostDb ?? double.NaN, 9);

        BoostContractReport report = BoostPolicy.Verify(room, result, OptimizerOperatingMode.ProductSafety);
        Assert.True(report.ReportedValueDescribesFinalRecommendation, report.Status);
        Assert.False(report.Violated);
    }
}
