namespace AudioOptimizer.Tests.ProductContract;

using AudioOptimizer.Core;
using AudioOptimizer.Optimization;
using AudioOptimizer.Simulation;

/// <summary>
/// A4 — partial measurement coverage. The counts are product knowledge (how many points the session planned, how many
/// the declared region has); the optimizer only knows how many it measured. These tests pin the classification, the
/// verbatim warning, and — on the real chain — that a recommendation from a 3-point subset can be illegal on the
/// 9-point field, which is why the guarantee must never be stated beyond the measured positions.
/// </summary>
public class MeasurementCoverageTests
{
    [Theory]
    [InlineData(3, 3, 27, SpatialGuarantee.WholeMeasuredSession)]   // a 3-point session, delivered complete
    [InlineData(3, 5, 27, SpatialGuarantee.MeasuredPositionsOnly)]  // a 5-point session, 2 points missing
    [InlineData(5, 5, 27, SpatialGuarantee.WholeMeasuredSession)]
    [InlineData(9, 9, 27, SpatialGuarantee.WholeMeasuredSession)]
    [InlineData(15, 15, 27, SpatialGuarantee.WholeMeasuredSession)]
    [InlineData(3, null, 27, SpatialGuarantee.MeasuredPositionsOnly)]
    [InlineData(27, 27, 27, SpatialGuarantee.WholeDeclaredRegion)]
    [InlineData(9, 9, 9, SpatialGuarantee.WholeDeclaredRegion)]
    public void Coverage_semantics_for_partial_and_complete_sessions(int measured, int? session, int declared, SpatialGuarantee expected)
    {
        MeasurementCoverage coverage = MeasurementCoverage.Classify(measured, session, declared);

        Assert.Equal(expected, coverage.Guarantee);
        Assert.Equal(measured, coverage.MeasuredPositionCount);
        if (expected == SpatialGuarantee.WholeDeclaredRegion)
        {
            Assert.True(coverage.CoversWholeDeclaredRegion);
            Assert.Equal(MeasurementCoverage.WholeDeclaredRegionStatement, coverage.WarningText);
            Assert.Equal(0, coverage.MissingDeclaredRegionPositions);
        }
        else
        {
            Assert.False(coverage.CoversWholeDeclaredRegion);
            Assert.Equal(MeasurementCoverage.MeasuredRegionOnlyWarning, coverage.WarningText);
        }
    }

    [Fact]
    public void The_required_user_facing_warning_is_the_verbatim_sentence()
    {
        Assert.Equal("本次结果只对已测位置提供空间均匀性保证。", MeasurementCoverage.MeasuredRegionOnlyWarning);

        MeasurementCoverage three = MeasurementCoverage.Classify(3, 3, 27);
        Assert.Equal("本次结果只对已测位置提供空间均匀性保证。", three.WarningText);
        Assert.Equal(24, three.MissingDeclaredRegionPositions);
        Assert.Equal(0, three.MissingSessionPositions);

        MeasurementCoverage incomplete = MeasurementCoverage.Classify(3, 5, 27);
        Assert.Equal("本次结果只对已测位置提供空间均匀性保证。", incomplete.WarningText);
        Assert.Equal(2, incomplete.MissingSessionPositions);
    }

    [Fact]
    public void Invalid_counts_are_rejected_instead_of_extrapolated()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MeasurementCoverage.Classify(0, 3, 27));
        Assert.Throws<ArgumentOutOfRangeException>(() => MeasurementCoverage.Classify(3, 0, 27));
        Assert.Throws<ArgumentOutOfRangeException>(() => MeasurementCoverage.Classify(3, 3, -1));
    }

    [Fact]
    public void The_optimizer_result_carries_the_coverage_statement()
    {
        // Fixture-first: the type exists and classifies today; the result property this pins is the OptimizerResult
        // diff in the report. Until that diff is applied this test is red by design.
        System.Reflection.PropertyInfo? property = typeof(OptimizerResult).GetProperty("Coverage");
        Assert.NotNull(property);
        Assert.Equal(typeof(MeasurementCoverage), property!.PropertyType);
    }

    [Fact]
    public void A_three_point_subset_can_recommend_a_setting_the_full_field_rejects()
    {
        // Real chain, noise off. The same physical room measured two ways: a 3-point session the product offers
        // and the 9-point z = 0 plane. The subset search only sees three positions. The subset is corners + centre
        // of the plane's diagonal, the cheapest spread of the region.
        MeasurementPoint[] nine = [.. LabFixtures.NinePoints()];
        IReadOnlyList<MeasurementPoint> subsetMicrophones = [nine[0], nine[2], nine[6]];
        DualSubMeasurement subsetInput = LabFixtures.Measure(LabFixtures.SilentConfig, subsetMicrophones);
        DualSubMeasurement fullInput = LabFixtures.Measure(LabFixtures.SilentConfig, LabFixtures.NinePoints());

        OptimizerResult subset = SubwooferOptimizer.Search(subsetInput, LabFixtures.Capability30);
        OptimizerResult full = SubwooferOptimizer.Search(fullInput, LabFixtures.Capability30);

        Assert.Equal(OptimizationVerdict.Improved, subset.Verdict);
        Assert.NotNull(subset.Recommended);
        Assert.Equal(OptimizationVerdict.Improved, full.Verdict);

        double subsetBoostOnSubset = LabFixtures.BoostOf(subsetInput, subset.Recommended!);
        double subsetBoostOnFull = LabFixtures.BoostOf(fullInput, subset.Recommended!);
        double fullBoostOnFull = LabFixtures.BoostOf(fullInput, full.Recommended!);

        // The subset's own measurement is satisfied: its recommendation is legal where it was measured.
        Assert.True(subsetBoostOnSubset <= 30.0 + BoostPolicy.BoostEpsilonDb, $"subset recommendation spent {subsetBoostOnSubset:F3} dB on its own subset");

        // The full-field answer is legal on the field it was measured on. With the delay-joint refine it is
        // (−1.1 dB, 12.0°, +1, 2.70 ms), not the canonical 2.0 ms node: the canonical node stays legal but is
        // no longer the best the search can find, so legality — not the old fixture constant — is the precondition.
        Assert.True(fullBoostOnFull <= 30.0 + BoostPolicy.BoostEpsilonDb, $"full recommendation spent {fullBoostOnFull:F3} dB on the full field");

        // The subset answer is a different setting, and on the full field it breaks the same 30 dB limit the
        // full-field search respected. This is why the result may only promise the measured positions.
        Assert.NotEqual(full.Recommended, subset.Recommended);
        Assert.True(subsetBoostOnFull > 30.0,
            $"the fixture no longer reproduces the subset overfit: subset recommendation boosts {subsetBoostOnFull:F3} dB on the full field");

        MeasurementCoverage coverage = MeasurementCoverage.Classify(
            measuredPositionCount: subsetInput.PositionCount,
            sessionPositionCount: 3,
            declaredRegionPositionCount: LabFixtures.TwentySevenPoints().Count);
        Assert.Equal(SpatialGuarantee.WholeMeasuredSession, coverage.Guarantee);
        Assert.Equal("本次结果只对已测位置提供空间均匀性保证。", coverage.WarningText);
        Assert.Equal(24, coverage.MissingDeclaredRegionPositions);
    }
}
