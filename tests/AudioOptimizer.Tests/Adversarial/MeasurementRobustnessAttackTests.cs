namespace AudioOptimizer.Tests;

using System.Reflection;
using AudioOptimizer.Core;
using AudioOptimizer.Optimization;
using AudioOptimizer.Simulation;
using Xunit.Abstractions;

/// <summary>
/// Worker E adversarial wave: measurement-reality robustness. These are the FAST pins of what the sweeps in
/// <c>C:/Temp/rf-e</c> exposed (full matrices and replay recipes are in the wave report); <c>src</c> is frozen and
/// nothing here is a fix.
/// <list type="number">
/// <item>
/// <b>Seed-dependent verdict (category 4, P2).</b> The S4 dual-sub room's only real correction is a 2.0 ms delay whose
/// measured maximum boost sits 0.04 dB inside the 30 dB limit (29.96 clean). At a −60 dBFS microphone floor the
/// measured boost of that same setting has a seed-to-seed spread of 0.62 dB (29.55…30.17), so some noise realizations
/// are declared illegal and the search returns "no change" while others apply the correction. The truth is identical
/// in both realizations (the canonical setting lands on the same truth score to 9 places), so the seed changes the
/// recommendation, not the room. Over 10 seeds at −60 dBFS: 7 applied a ~2.2 dB true improvement, 3 declined;
/// at −80 and −100 dBFS: 0 declined. This is the optimization-time cost of enforcing a hard constraint on a
/// measured quantity whose uncertainty is larger than the margin.
/// </item>
/// <item>
/// <b>Microphone tilt is invisible (graceful).</b> A tilt that multiplies every capture by the same per-frequency
/// factor cannot move the A-vs-B relation the search reads, so the recommendation and both scores are unchanged.
/// </item>
/// <item>
/// <b>The boost measure is unbounded at a baseline null (mechanism).</b> <see cref="ObjectiveFunction.MaxBoostVsBaselineDb"/>
/// divides by the measured baseline magnitude; 1e-6 deep in a null a 0.001° phase rotation already reads 24.9 dB, and a
/// 1e-5 relative nudge of the measurement swings that number by 18.8 dB. A hard limit can therefore be crossed or
/// missed by a measurement error far below the chain's own repeatability, which is what item 1 observes end to end.
/// </item>
/// </list>
/// </summary>
public class MeasurementRobustnessAttackTests(ITestOutputHelper output)
{
    private static readonly Position SubAPosition = new(0.45, 0.45, 0.35);
    private static readonly Position SubBPosition = new(2.85, 3.15, 0.35);

    /// <summary>The S4 room's correction: no gain, no phase, positive polarity, 2.0 ms of delay.</summary>
    private static readonly SubwooferSetting CanonicalDelay = new(0.0, 0.0, 1, 0.002);

    private static IReadOnlyList<VirtualSubwoofer> Subs() =>
    [
        new VirtualSubwoofer(SubAPosition),
        new VirtualSubwoofer(SubBPosition, GainDb: -4.0, PhaseDegrees: 70.0),
    ];

    /// <summary>The z = 0 plane: the 9-position session the sweep used (27 captures per realization).</summary>
    private static IReadOnlyList<MeasurementPoint> NinePoints() =>
        [.. ListeningRegion.Default.Points.Where(point => point.GridZ == 0)];

    /// <summary>Corners and centre: the cheapest 3-position session that still spans the region.</summary>
    private static IReadOnlyList<MeasurementPoint> ThreePoints()
    {
        MeasurementPoint[] nine = [.. NinePoints()];
        return [nine[0], nine[4], nine[8]];
    }

    private static OptimizerOptions Dynamics() => new() { IncludeDelay = true, MaxBoostLimitDb = 30.0 };

    private static (DualSubMeasurement Input, GroundTruth Truth, OptimizerResult Result) MeasureAndSearch(
        SimulationConfig config, IReadOnlyList<MeasurementPoint> microphones)
    {
        var lab = new VirtualLab(config, Subs(), microphones);
        IReadOnlyList<SimulatedMeasurement> measurements = lab.MeasureAll();
        DualSubMeasurement input = lab.AsOptimizerInput(measurements);
        return (input, lab.GroundTruth, SubwooferOptimizer.Search(input, Dynamics()));
    }

    /// <summary>The achieved boost of one setting over the measured baseline — the constraint's own measure.</summary>
    private static double BoostOf(DualSubMeasurement input, SubwooferSetting setting)
        => ObjectiveFunction.MaxBoostVsBaselineDb(
            SubwooferModel.Combine(input, setting),
            SubwooferModel.Combine(input, SubwooferSetting.Baseline));

    /// <summary>The objective score of one setting on a field. Evaluation only; never an optimizer input.</summary>
    private static double ScoreOf(DualSubMeasurement field, SubwooferSetting setting)
    {
        IReadOnlyList<PositionResponse> totals = SubwooferModel.Combine(field, setting);
        SpatialSummary summary = SpatialMetrics.Compute(totals);
        double boost = ObjectiveFunction.MaxBoostVsBaselineDb(totals, SubwooferModel.Combine(field, SubwooferSetting.Baseline));
        return ObjectiveFunction.Score(summary, boost, ObjectiveWeights.Default);
    }

    [Fact]
    public void A_sixty_dBFS_noise_seed_decides_whether_the_canonical_alignment_is_applied()
    {
        SimulationConfig S4(int seed) => SimulationConfig.Default with { MicrophoneNoiseFloorDb = -60.0, NoiseSeed = seed };

        // Both realizations are the same room and the same session; only the microphone self-noise differs.
        var applied = MeasureAndSearch(S4(20260101), NinePoints());
        var declined = MeasureAndSearch(S4(20260103), NinePoints());

        double appliedCanonBoost = BoostOf(applied.Input, CanonicalDelay);
        double declinedCanonBoost = BoostOf(declined.Input, CanonicalDelay);
        double appliedTruth = ScoreOf(applied.Truth.AsOptimizerInput(), CanonicalDelay);
        double declinedTruth = ScoreOf(declined.Truth.AsOptimizerInput(), CanonicalDelay);
        double appliedOutcome = ScoreOf(applied.Truth.AsOptimizerInput(), applied.Result.Recommended ?? SubwooferSetting.Baseline);
        double declinedOutcome = ScoreOf(declined.Truth.AsOptimizerInput(), declined.Result.Recommended ?? SubwooferSetting.Baseline);

        output.WriteLine($"seed 20260101: {applied.Result.Verdict}, recommended " + Describe(applied.Result.Recommended)
            + $", canonical measured boost {appliedCanonBoost:F3} dB, truth outcome {appliedOutcome:F3}");
        output.WriteLine($"seed 20260103: {declined.Result.Verdict}, recommended " + Describe(declined.Result.Recommended)
            + $", canonical measured boost {declinedCanonBoost:F3} dB, truth outcome {declinedOutcome:F3}");
        output.WriteLine($"canonical setting's TRUTH score — seed 20260101 {appliedTruth:F9}, seed 20260103 {declinedTruth:F9}");

        // The applied realization: the 2.0 ms alignment is legal and returned.
        Assert.Equal(OptimizationVerdict.Improved, applied.Result.Verdict);
        Assert.NotNull(applied.Result.Recommended);
        Assert.Equal(2.0, applied.Result.Recommended!.DelaySeconds * 1000.0, 9);
        Assert.True(appliedCanonBoost <= 30.0, $"the applied realization's canonical boost was {appliedCanonBoost:F3} dB");

        // The declined realization: the SAME setting was measured as over the limit, so the search returns no change.
        Assert.Equal(OptimizationVerdict.NegligibleImprovement, declined.Result.Verdict);
        Assert.Equal(SubwooferSetting.Baseline, declined.Result.Recommended);
        Assert.True(declinedCanonBoost > 30.0, $"the declined realization's canonical boost was {declinedCanonBoost:F3} dB");

        // The room — and therefore the truth — is one object: the canonical setting lands on the same truth score.
        Assert.Equal(appliedTruth, declinedTruth, 9);

        // What the user gets differs by the whole available improvement (2.1-2.3 dB on the truth in the sweep).
        Assert.True(declinedOutcome - appliedOutcome > 2.0,
            $"the declined realization was only {declinedOutcome - appliedOutcome:F3} dB worse on the truth");
    }

    [Fact]
    public void A_microphone_tilt_is_invisible_to_the_alignment_search()
    {
        // Noise off, so the only difference between the two sessions is the microphone's own response.
        SimulationConfig silent = SimulationConfig.Default with { MicrophoneNoiseFloorDb = double.NegativeInfinity };
        SimulationConfig tilted = silent with
        {
            MicrophoneProfile = MicrophoneResponseProfile.LowFrequencyTilt,
            MicrophoneDeviationDb = 1.0,
        };

        var perfect = MeasureAndSearch(silent, ThreePoints());
        var coloured = MeasureAndSearch(tilted, ThreePoints());

        output.WriteLine($"perfect : {perfect.Result.Verdict}, {Describe(perfect.Result.Recommended)}, "
            + $"score {perfect.Result.ScoreBefore:F6} → {perfect.Result.ScoreAfter:F6}");
        output.WriteLine($"tilted  : {coloured.Result.Verdict}, {Describe(coloured.Result.Recommended)}, "
            + $"score {coloured.Result.ScoreBefore:F6} → {coloured.Result.ScoreAfter:F6}");

        // Every capture of both configurations is multiplied by the same per-frequency factor, so the A/B relation
        // the search reads is untouched: the same setting, the same verdict, the same scores to well below a step.
        Assert.Equal(perfect.Result.Recommended, coloured.Result.Recommended);
        Assert.Equal(perfect.Result.Verdict, coloured.Result.Verdict);
        Assert.Equal(perfect.Result.ScoreBefore, coloured.Result.ScoreBefore, 3);
        Assert.Equal(perfect.Result.ScoreAfter, coloured.Result.ScoreAfter, 3);

        // And the truth is bit-identical, because the microphone response never reaches it.
        DualSubMeasurement perfectTruth = perfect.Truth.AsOptimizerInput();
        DualSubMeasurement colouredTruth = coloured.Truth.AsOptimizerInput();
        Assert.Equal(
            perfectTruth.A.Select(position => position.Bins.Select(bin => (bin.FrequencyHz, bin.Real, bin.Imag))),
            colouredTruth.A.Select(position => position.Bins.Select(bin => (bin.FrequencyHz, bin.Real, bin.Imag))));
    }

    /// <summary>
    /// The setting as numbers. Deliberately NOT the record's own ToString: the record's generated PrintMembers
    /// includes its <c>Flipped</c> property, so <c>SubwooferSetting.ToString()</c> recurses until the stack guard
    /// fires. The cause is pinned by
    /// <see cref="A_setting_exposes_a_self_typed_property_that_makes_the_generated_ToString_recurse"/>.
    /// </summary>
    private static string Describe(SubwooferSetting? setting) => setting is null
        ? "none"
        : $"g {setting.GainDb:+0.0;-0.0;0.0} dB, p {setting.PhaseDegrees:0.0}°, pol {setting.Polarity:+#;-#;+1}, "
          + $"d {setting.DelaySeconds * 1000.0:0.0} ms";

    [Fact]
    public void A_setting_exposes_a_self_typed_property_that_makes_the_generated_ToString_recurse()
    {
        // Root cause of the printing defect found while pinning this wave: the positional record's generated
        // PrintMembers appends EVERY public instance property, including Flipped, whose type is the record itself —
        // so SubwooferSetting.ToString(), and any record that contains one (OptimizerResult, OptimizerCandidate,
        // GroundTruth, SimulationOutcome), recurses until the runtime's stack guard fires. The product's own
        // formatters avoid ToString, so no shipped path hits it today; a log line would. This pin states the cause
        // without triggering the crash (which the wave report replays in a scratch process).
        var property = typeof(SubwooferSetting).GetProperty("Flipped", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(property);
        Assert.Equal(typeof(SubwooferSetting), property!.PropertyType);
    }

    [Fact]
    public void A_deep_baseline_null_amplifies_a_relative_measurement_error_into_decibels_of_boost()
    {
        FrequencyBand band = new(5.0, 200.0);

        // One position, one bin: A = 1∠0°, B = (1−depth)∠180°, so the baseline |A+B| is exactly the depth.
        // The candidate rotates B by 0.001° — the single-bin footprint of a 2 ms delay at 100 Hz is far larger, so
        // this is a small slice of a real alignment, not an extreme construction.
        static DualSubMeasurement Pair(FrequencyBand band, double depth)
            => new(
                [new PositionResponse("p0", band, [OptimizationTestData.Bin(100.0, 1.0, 0.0)])],
                [new PositionResponse("p0", band, [OptimizationTestData.Bin(100.0, -(1.0 - depth), 0.0)])]);

        static double BoostAt(DualSubMeasurement pair, double phaseDegrees)
        {
            SubwooferSetting setting = new(0.0, phaseDegrees * Math.PI / 180.0, 1, 0.0);
            return ObjectiveFunction.MaxBoostVsBaselineDb(
                SubwooferModel.Combine(pair, setting),
                SubwooferModel.Combine(pair, SubwooferSetting.Baseline));
        }

        // A −120 dB baseline null: a 0.001° rotation already reads 24.85 dB of "boost".
        double deepBoost = BoostAt(Pair(band, 1e-6), 0.001);

        // Nudge B by 1e-5 of its magnitude — a relative measurement error of −100 dB, far below the chain's own
        // repeatability at any floor the product supports. The baseline null shallows by 10× and the same rotation
        // now reads 6.07 dB: 18.8 dB of movement in the quantity the hard limit is compared against.
        double nudgedBoost = BoostAt(Pair(band, 1e-5), 0.001);

        // The same nudge at an ordinary −40 dB null costs only ~0.009 dB; the sensitivity is proportional to the
        // null depth, and null depth is exactly what measurement noise perturbs.
        double shallowSwing = Math.Abs(BoostAt(Pair(band, 1e-2), 0.001) - BoostAt(Pair(band, 1e-2 + 1e-5), 0.001));

        output.WriteLine($"−120 dB null, 0.001°: {deepBoost:F4} dB of boost");
        output.WriteLine($"−100 dB null (a 1e-5 nudge), 0.001°: {nudgedBoost:F4} dB of boost; swing {deepBoost - nudgedBoost:F4} dB");
        output.WriteLine($"−40 dB null, the same 1e-5 nudge: {shallowSwing:E3} dB of boost");

        Assert.True(deepBoost > 20.0, $"{deepBoost:F4} dB at the deep null");
        Assert.True(deepBoost - nudgedBoost > 10.0, $"the 1e-5 nudge moved the boost by only {deepBoost - nudgedBoost:F4} dB");
        Assert.True(shallowSwing < 0.05, $"the same nudge at an ordinary null moved the boost by {shallowSwing:E3} dB");
    }
}
