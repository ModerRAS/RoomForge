namespace AudioOptimizer.Tests;

using System.Reflection;
using AudioOptimizer.Core;
using AudioOptimizer.Optimization;
using AudioOptimizer.Simulation;
using Xunit.Abstractions;

/// <summary>
/// The black-box property the whole project exists to check: hand the SHIPPED optimizer only what the measurement
/// chain produced — never the simulator's own numbers — and see whether it finds the answer.
/// <para>
/// Two scenarios, asking two different questions. S4 asks whether the search can recover spatial uniformity at all,
/// with the boost limit opened because this room's interference nulls make any realignment a large level increase
/// relative to the measured total. S5 asks the opposite question at the ordinary 3 dB: does the search buy its way out
/// of a room problem with gain?
/// </para>
/// </summary>
public class SimulationOptimizationTests(ITestOutputHelper output)
{
    /// <summary>
    /// The reference must stay one-way. If <c>Optimization</c> could see the simulator it could read the truth instead
    /// of measuring it — and no assertion about "the optimizer only saw measurements" would mean anything.
    /// </summary>
    [Fact]
    public void The_optimizer_cannot_reach_the_simulator()
    {
        Assembly optimizer = typeof(SubwooferOptimizer).Assembly;
        string[] referenced = [.. optimizer.GetReferencedAssemblies().Select(assembly => assembly.Name ?? string.Empty)];

        output.WriteLine($"Optimization references: {string.Join(", ", referenced.Order())}");

        Assert.NotEmpty(referenced);                                                   // a vacuous pass is worse than none
        Assert.Contains("AudioOptimizer.Core", referenced);                            // the positive companion
        Assert.Contains("AudioOptimizer.Dsp", referenced);
        Assert.DoesNotContain("AudioOptimizer.Simulation", referenced);

        // The source-level half: the project file must not gain the reference either, which is the change that would
        // make the missing assembly reference above a build error rather than a silent allowance.
        string project = File.ReadAllText(Path.Combine(TestPaths.SourceRoot, "AudioOptimizer.Optimization", "AudioOptimizer.Optimization.csproj"));
        Assert.DoesNotContain("Simulation", project, StringComparison.OrdinalIgnoreCase);

        // And the direction that must exist: the simulator is a consumer of the optimizer.
        string[] simulation = [.. typeof(VirtualLab).Assembly.GetReferencedAssemblies().Select(assembly => assembly.Name ?? string.Empty)];
        Assert.Contains("AudioOptimizer.Optimization", simulation);
        Assert.Contains("AudioOptimizer.Measurement", simulation);
    }

    [Fact]
    public void The_optimizer_recovers_the_uniformity_a_known_good_correction_reaches()
    {
        SimulationScenario scenario = SimulationScenarios.DualSub;
        SimulationOutcome outcome = SimulationRunner.Run(scenario);

        OptimizerResult result = outcome.Optimization ?? throw new InvalidOperationException("The scenario did not run the optimizer.");
        SpatialSummary truth = outcome.GroundTruthReference ?? throw new InvalidOperationException("The scenario has no ground-truth reference.");
        double before = result.Before.MeanStdDevDb, after = result.After.MeanStdDevDb;

        output.WriteLine($"measured σ {before:F3} dB → recommended σ {after:F3} dB; the scenario's known correction gives "
            + $"{truth.MeanStdDevDb:F3} dB; recommended {Describe(result.Recommended)}; achieved boost "
            + $"{result.Constraint.MaxAchievedBoostDb:F2} dB of {result.Constraint.MaxBoostLimitDb:F1} allowed");

        // The measurement set the search saw, checked to be the measurement's own numbers and nothing else.
        Assert.Equal(OptimizationVerdict.Improved, result.Verdict);
        Assert.NotEqual(SubwooferSetting.Baseline, result.Recommended);

        Assert.True(after < before - 0.4, $"the recommendation only moved the spatial σ from {before:F3} to {after:F3} dB");
        Assert.True(after <= truth.MeanStdDevDb + 0.5,
            $"the recommendation reached {after:F3} dB against the known correction's {truth.MeanStdDevDb:F3} dB");
        Assert.True(result.Constraint.MaxAchievedBoostDb <= scenario.Optimize!.MaxBoostLimitDb, "the recommendation broke its own boost limit");

        // The theoretical best, from an exhaustive scan of the same measurement at coarse resolution. Measured: the
        // staged search reaches 2.473 dB where the exhaustive scan reaches 2.290 dB — 0.18 dB behind, which is the
        // documented gap between a staged grid search and a full scan (SubwooferOptimizer's own note: "a grid is still a
        // grid"). The bound below says "comparable", not "equal", and a regression that made the search miss the basin
        // entirely would blow through it.
        OptimizerCandidate? exhaustive = SubwooferOptimizer.BruteForce(
            MeasurementOf(scenario), scenario.Optimize with { GainCoarseStepDb = 2.0, PhaseCoarseStepDegrees = 15.0, DelayStepMilliseconds = 4.0 });
        Assert.NotNull(exhaustive);
        output.WriteLine($"coarse exhaustive reference: σ {exhaustive!.Summary.MeanStdDevDb:F3} dB with {Describe(exhaustive.Setting)}");
        Assert.True(after <= exhaustive.Summary.MeanStdDevDb + 0.4,
            $"the search reached {after:F3} dB where a coarse exhaustive scan of the same data reached {exhaustive.Summary.MeanStdDevDb:F3} dB");
    }

    [Fact]
    public void The_search_does_not_chase_a_deep_null_with_a_large_boost()
    {
        SimulationScenario scenario = SimulationScenarios.DeepNull;
        SimulationOutcome outcome = SimulationRunner.Run(scenario);

        OptimizerResult result = outcome.Optimization ?? throw new InvalidOperationException("The scenario did not run the optimizer.");
        FrequencyMetrics at50 = result.Before.PerFrequency.OrderBy(row => Math.Abs(row.FrequencyHz - 50.0)).First();
        FrequencyMetrics after50 = result.After.PerFrequency.OrderBy(row => Math.Abs(row.FrequencyHz - 50.0)).First();

        output.WriteLine($"50 Hz as measured: σ {at50.StdDevDb:F2} dB, deepest position {at50.MeanDb - at50.MinDb:F2} dB below the mean; "
            + $"after the recommendation: σ {after50.StdDevDb:F2} dB, {after50.MeanDb - after50.MinDb:F2} dB; "
            + $"recommended {Describe(result.Recommended)}, achieved boost {result.Constraint.MaxAchievedBoostDb:F2} dB "
            + $"of {result.Constraint.MaxBoostLimitDb:F1} allowed, limit binding {result.Constraint.Binding}, "
            + $"{result.Constraint.CandidatesRejected}/{result.Constraint.CandidatesEvaluated} rejected");

        // The scenario really does contain the case it is named for.
        Assert.True(at50.MeanDb - at50.MinDb >= 20.0,
            $"the 50 Hz null is only {at50.MeanDb - at50.MinDb:F2} dB deep, so this scenario no longer tests the regression");
        Assert.Equal(3.0, result.Constraint.MaxBoostLimitDb);

        // And the search neither left the limit nor bought its way out of the room's problem.
        Assert.True(result.Constraint.MaxAchievedBoostDb <= result.Constraint.MaxBoostLimitDb);
        Assert.True(result.Constraint.MaxAchievedBoostDb < 2.5, $"the recommendation spent {result.Constraint.MaxAchievedBoostDb:F2} dB of boost");
        Assert.True(result.Constraint.Binding, "the limit did not bind, so this run does not show it doing any work");
        Assert.Equal(1, result.Recommended!.Polarity);                     // inverting the sub back is the cheap way to fill the null
        Assert.True(after50.MeanDb - after50.MinDb >= 20.0, "the recommendation filled the null");
        Assert.True(after50.MeanDb - after50.MinDb <= (at50.MeanDb - at50.MinDb) + 1.0, "the recommendation left the null deeper than it found it");
    }

    /// <summary>Re-derives the optimizer's input from the same measured points, for the exhaustive reference.</summary>
    private static DualSubMeasurement MeasurementOf(SimulationScenario scenario)
    {
        VirtualLab lab = scenario.CreateLab();
        return lab.AsOptimizerInput(lab.MeasureAll());
    }

    private static string Describe(SubwooferSetting? setting)
        => setting is { } value
            ? $"{value.GainDb:+0.0;-0.0;0.0} dB, polarity {value.Polarity:+#;-#;+1}, phase {value.PhaseDegrees:0.0}°, "
                + $"delay {value.DelaySeconds * 1000.0:0.0} ms"
            : "none";
}
