namespace AudioOptimizer.Tests.Refinement;

using AudioOptimizer.Core;
using AudioOptimizer.Optimization;
using AudioOptimizer.Simulation;

/// <summary>
/// Worker B (optimizer-hardening round): the delay-joint refinement, pinned through the REAL pipeline —
/// <c>VirtualAudioBackend → PointMeasurement.Run → deconvolution → alignment → FFT → FrequencyResponse →
/// MeasurementResult → DualSubMeasurement</c> — with the optimizer seeing only <c>VirtualLab.AsOptimizerInput</c>.
/// No <c>ComplexFrequencyResponse</c> is ever handed to the search and the simulator's GroundTruth is never an
/// optimizer input (it is not read here at all).
/// <para>
/// <b>This file requires the B2 diff in <c>SubwooferOptimizer.cs</c></b> (the <c>DelayJointRefine</c> stage that
/// runs after the existing 2-D local refine inside the IncludeDelay block). On the 5d8dc8d baseline the two
/// improvement pins fail with the pre-refinement scores recorded in each comment; every other file in the suite
/// passes both before and after the diff (the diff's only other effect is one expectation in
/// <c>MeasurementRobustnessAttackTests</c>, which the diff updates with its truth-score evidence).
/// </para>
/// <para>
/// Fixture recipes (all deterministic: <see cref="SimulationConfig.Default"/> — room 3.3×3.6×2.6 m, 48 kHz,
/// 20–150 Hz 1 s sweep, ISM order 3, −120 dBFS floor, noise seed 20260101, playback gain 0.25):
/// <list type="bullet">
/// <item><description><b>delay+phase</b> (test 1): A at (0.45, 0.45, 0.35) 0 dB/+1/0°/0 ms; B at
/// (2.85, 3.15, 0.35) −4 dB/+1/+70°/0 ms — the S4 misalignment. 9 microphones: the z=1.2 plane of
/// <c>ListeningRegion.Default</c>. Options: <c>IncludeDelay=true, MaxBoostLimitDb=30</c>.</description></item>
/// <item><description><b>delay+polarity</b> (test 2): A at (0.35, 0.35, 0.30) 0 dB/+1/0°/0 ms; B at
/// (2.95, 3.25, 0.30) +0.8 dB/−1/0°/0 ms — the S5 mirror pair, whose fix trades polarity, phase and delay.
/// Same 9 microphones and options.</description></item>
/// </list>
/// </para>
/// </summary>
public class DelayJointRefinementTests
{
    private static readonly OptimizerOptions Capability = new() { IncludeDelay = true, MaxBoostLimitDb = 30.0 };

    private static IReadOnlyList<MeasurementPoint> NinePoints() =>
        [.. ListeningRegion.Default.Points.Where(point => point.GridZ == 0)];

    /// <summary>The only path an optimizer input may take: the shipped chain's own measured bins, in band.</summary>
    private static DualSubMeasurement Measure(IReadOnlyList<VirtualSubwoofer> subs, IReadOnlyList<MeasurementPoint> microphones)
    {
        var lab = new VirtualLab(SimulationConfig.Default, subs, microphones);
        return lab.AsOptimizerInput(lab.MeasureAll());
    }

    private static IReadOnlyList<VirtualSubwoofer> S4Rig() =>
    [
        new VirtualSubwoofer(new Position(0.45, 0.45, 0.35)),
        new VirtualSubwoofer(new Position(2.85, 3.15, 0.35), -4.0, 1, 70.0, 0.0),
    ];

    private static IReadOnlyList<VirtualSubwoofer> MirrorPairRig() =>
    [
        new VirtualSubwoofer(new Position(0.35, 0.35, 0.30)),
        new VirtualSubwoofer(new Position(2.95, 3.25, 0.30), 0.8, -1, 0.0, 0.0),
    ];

    /// <summary>The recommendation's boost, re-derived from the measured input rather than read from the report.</summary>
    private static double BoostOf(DualSubMeasurement input, SubwooferSetting setting)
        => ObjectiveFunction.MaxBoostVsBaselineDb(
            SubwooferModel.Combine(input, setting),
            SubwooferModel.Combine(input, SubwooferSetting.Baseline));

    [Fact]
    public void The_delay_joint_refine_recovers_a_delay_phase_coupled_alignment()
    {
        DualSubMeasurement input = Measure(S4Rig(), NinePoints());

        OptimizerResult result = SubwooferOptimizer.Search(input, Capability);

        // Pre-diff (5d8dc8d): 0.0 dB, +0°, 2.00 ms, best score 29.4215. Post-diff: −1.1 dB, 12.0°, 2.70 ms,
        // best score 26.9989 — the coarse delay grid's 2.0 ms node is not the optimum for the refined gain/phase.
        Assert.Equal(OptimizationVerdict.Improved, result.Verdict);
        Assert.NotNull(result.Best);
        Assert.NotNull(result.Recommended);
        Assert.True(result.Best!.Score <= 27.5, $"the delay-joint refine returned {result.Best.Score:F4}, not the measured 26.9989");
        Assert.True(result.ScoreAfter <= 27.5, $"the recommended setting scores {result.ScoreAfter:F4}");
        Assert.True(result.Best.Score < result.ScoreBefore - 2.0, $"only {result.ScoreBefore - result.Best.Score:F4} dB of improvement");
        Assert.True(result.Recommended!.DelaySeconds * 1000.0 > 2.0 + 1e-9,
            $"the recommendation stayed on the coarse 2.0 ms node: {result.Recommended.DelaySeconds * 1000.0:F4} ms");
        Assert.True(BoostOf(input, result.Recommended) <= Capability.MaxBoostLimitDb,
            $"the recommendation uses {BoostOf(input, result.Recommended):F3} dB of a {Capability.MaxBoostLimitDb} dB limit");
    }

    [Fact]
    public void The_delay_joint_refine_recovers_a_delay_polarity_coupled_alignment()
    {
        DualSubMeasurement input = Measure(MirrorPairRig(), NinePoints());

        OptimizerResult result = SubwooferOptimizer.Search(input, Capability);

        // Pre-diff (5d8dc8d): −4.7 dB, +1, 141.0°, 0.00 ms, best score 30.1849. Post-diff: −4.1 dB, +1, 157.0°,
        // 0.55 ms, best score 28.8429 — the fix is jointly a gain, a phase and a delay move.
        Assert.Equal(OptimizationVerdict.Improved, result.Verdict);
        Assert.NotNull(result.Best);
        Assert.NotNull(result.Recommended);
        Assert.True(result.Best!.Score <= 29.5, $"the delay-joint refine returned {result.Best.Score:F4}, not the measured 28.8429");
        Assert.True(result.ScoreAfter <= 29.5, $"the recommended setting scores {result.ScoreAfter:F4}");
        Assert.True(result.Best.Setting.DelaySeconds * 1000.0 >= 0.5 - 1e-9,
            $"the recommendation's delay is only {result.Best.Setting.DelaySeconds * 1000.0:F4} ms");
        Assert.True(BoostOf(input, result.Recommended) <= Capability.MaxBoostLimitDb,
            $"the recommendation uses {BoostOf(input, result.Recommended):F3} dB of a {Capability.MaxBoostLimitDb} dB limit");
    }

    [Fact]
    public void The_delay_joint_refine_is_deterministic_and_does_not_explode_the_candidate_count()
    {
        DualSubMeasurement input = Measure(S4Rig(), NinePoints());

        OptimizerResult first = SubwooferOptimizer.Search(input, Capability);
        OptimizerResult second = SubwooferOptimizer.Search(input, Capability);

        // Deterministic by construction: the input is fixed, the grid is a grid, no RNG and no wall clock.
        Assert.Equal(first.Recommended, second.Recommended);
        Assert.Equal(first.ScoreAfter, second.ScoreAfter, 12);
        Assert.Equal(first.Constraint.CandidatesEvaluated, second.Constraint.CandidatesEvaluated);

        // No combinatorial explosion: the stage adds 5×5×11 = 275 evaluations per improving round, not a
        // delay × gain × phase × polarity product. The 9-mic S4 search evaluates ~1.9k candidates pre-diff and ~3.8k after.
        Assert.True(first.Constraint.CandidatesEvaluated <= 6500,
            $"the search evaluated {first.Constraint.CandidatesEvaluated} candidates");
        Assert.True(first.Constraint.CandidatesEvaluated > 1000, "the count is implausibly small, so this bound proves nothing");
    }
}
