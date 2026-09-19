namespace AudioOptimizer.Tests;

using System.Globalization;
using System.Reflection;
using AudioOptimizer.Core;
using AudioOptimizer.Optimization;
using AudioOptimizer.Simulation;
using Xunit.Abstractions;

/// <summary>
/// The randomized regression lab's own tests. The suite is deliberately bounded: generator determinism and clamps are
/// checked over the first 100 scenarios (no measurement), the judgement layers are driven with synthetic metrics, and
/// only two scenarios go through the full chain — the same "one small end-to-end probe plus cheap structural guards"
/// shape the rest of this repository uses. The full plans are run from the command line, not from xUnit.
/// </summary>
public class SimulationRegressionTests(ITestOutputHelper output)
{
    private static readonly object ConsoleGate = new();

    // ---------------------------------------------------------------------------------------------------------
    // Generator: determinism, pinned seeds, clamps.
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void Create_is_deterministic_and_replay_rebuilds_the_same_scenario()
    {
        RegressionScenario first = RandomScenarioGenerator.Create(42);
        RegressionScenario second = RandomScenarioGenerator.Create(42);
        RegressionScenario replayed = RandomScenarioGenerator.Replay(first.Seed);

        Assert.Equal(42, first.Index);
        Assert.Equal(RandomScenarioGenerator.SeedForIndex(42), first.Seed);
        Assert.Equal(first.Signature(), second.Signature());
        Assert.Equal(first.Signature(), replayed.Signature());
        Assert.Equal(first.Seed, replayed.Seed);

        Assert.NotEqual(RandomScenarioGenerator.Create(41).Signature(), first.Signature());
        Assert.NotEqual(RandomScenarioGenerator.Create(99).Signature(), first.Signature());
    }

    [Fact]
    public void The_seed_derivation_is_pinned_for_the_first_hundred_indices()
    {
        // Seed(index) = MasterSeed + index; these literals are the contract the CLI's --replay relies on.
        Assert.Equal(20260919, RandomScenarioGenerator.MasterSeed);
        Assert.Equal(20260919, RandomScenarioGenerator.SeedForIndex(0));
        Assert.Equal(20260960, RandomScenarioGenerator.SeedForIndex(41));
        Assert.Equal(20260961, RandomScenarioGenerator.SeedForIndex(42));
        Assert.Equal(20261018, RandomScenarioGenerator.SeedForIndex(99));

        Assert.Equal(20260919, RandomScenarioGenerator.Create(0).Seed);
        Assert.Equal(20260960, RandomScenarioGenerator.Create(41).Seed);
        Assert.Equal(20260961, RandomScenarioGenerator.Create(42).Seed);
        Assert.Equal(20261018, RandomScenarioGenerator.Create(99).Seed);

        Assert.Equal(0, RandomScenarioGenerator.IndexForSeed(20260919));
        Assert.Equal(41, RandomScenarioGenerator.IndexForSeed(20260960));
        Assert.Equal(42, RandomScenarioGenerator.IndexForSeed(20260961));
        Assert.Equal(99, RandomScenarioGenerator.IndexForSeed(20261018));

        Assert.Throws<ArgumentOutOfRangeException>(() => RandomScenarioGenerator.Create(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => RandomScenarioGenerator.IndexForSeed(123456789));
    }

    [Fact]
    public void The_first_hundred_scenarios_stay_inside_every_clamp()
    {
        const double clearance = RandomScenarioGenerator.SubWallClearanceMetres;

        for (int index = 0; index < 100; index++)
        {
            RegressionScenario scenario = RandomScenarioGenerator.Create(index);
            RoomModel room = scenario.Room;

            Assert.Equal(index, scenario.Index);
            Assert.Equal(RandomScenarioGenerator.SeedForIndex(index), scenario.Seed);
            Assert.InRange(room.LengthMetres, RandomScenarioGenerator.MinRoomLengthMetres, RandomScenarioGenerator.MaxRoomLengthMetres);
            Assert.InRange(room.WidthMetres, RandomScenarioGenerator.MinRoomWidthMetres, RandomScenarioGenerator.MaxRoomWidthMetres);
            Assert.InRange(room.HeightMetres, RandomScenarioGenerator.MinRoomHeightMetres, RandomScenarioGenerator.MaxRoomHeightMetres);

            // 27 microphones, inside the room, from the listening-region machinery rather than raw grid corners.
            Assert.Equal(RandomScenarioGenerator.MeasurementPoints, scenario.Scenario.Microphones.Count);
            foreach (MeasurementPoint microphone in scenario.Scenario.Microphones)
            {
                Assert.InRange(microphone.X, 0.1, room.LengthMetres - 0.1);
                Assert.InRange(microphone.Y, 0.1, room.WidthMetres - 0.1);
                Assert.InRange(microphone.Z, 0.1, room.HeightMetres - 0.1);
            }

            foreach (VirtualSubwoofer sub in new[] { scenario.SubA, scenario.SubB })
            {
                Assert.InRange(sub.GainDb, RandomScenarioGenerator.MinSubGainDb, RandomScenarioGenerator.MaxSubGainDb);
                Assert.InRange(Math.Abs(sub.PhaseDegrees), 0.0, 180.0);
                Assert.InRange(Math.Abs(sub.DelaySeconds * 1000.0), 0.0, RandomScenarioGenerator.MaxSubDelayMilliseconds);
                Assert.Contains(sub.Polarity, new[] { -1, 1 });

                Assert.InRange(sub.Position.X, clearance, room.LengthMetres - clearance);
                Assert.InRange(sub.Position.Y, clearance, room.WidthMetres - clearance);
                Assert.InRange(sub.Position.Z, clearance, RandomScenarioGenerator.MaxSubHeightMetres);
            }

            Assert.True(scenario.SubA.Position.DistanceTo(scenario.SubB.Position) >= RandomScenarioGenerator.MinimumSubSeparationMetres - 1e-12,
                $"scenario {index}: subs only {scenario.SubA.Position.DistanceTo(scenario.SubB.Position):F3} m apart");

            // The optimizer is pointed at every generated scenario, inside its documented search box.
            Assert.True(scenario.Scenario.RunOptimizer);
            OptimizerOptions optimize = scenario.Scenario.Optimize ?? throw new InvalidOperationException("generated scenarios must carry optimizer options");
            Assert.True(optimize.IncludeDelay);
            Assert.Equal(RandomScenarioGenerator.OptimizerDelayMaxMilliseconds, optimize.DelayMaxMilliseconds);
            Assert.Equal(RandomScenarioGenerator.OptimizerMaxBoostLimitDb, optimize.MaxBoostLimitDb);

            // The advisory reference, when present, is a real setting (and therefore a legal B-only correction).
            if (scenario.GroundTruth is { } reference)
            {
                reference.Validate();
                Assert.True(reference.DelaySeconds >= 0.0);
            }
        }
    }

    [Fact]
    public void Imperfection_classes_and_families_follow_their_deterministic_bands()
    {
        int light = 0, moderate = 0, stress = 0, independent = 0, misaligned = 0;

        for (int index = 0; index < 100; index++)
        {
            RegressionScenario scenario = RandomScenarioGenerator.Create(index);
            RegressionImperfectionProfile imperfection = scenario.Imperfection;

            Assert.Equal(RandomScenarioGenerator.ClassForIndex(index), imperfection.Class);
            switch (imperfection.Class)
            {
                case RegressionImperfection.Light:
                    light++;
                    Assert.Equal(-120.0, imperfection.NoiseFloorDb);
                    Assert.InRange(Math.Abs(imperfection.ClockPpm), 0.0, 5.0);
                    Assert.InRange(Math.Abs(imperfection.MicrophoneDeviationDb), 0.0, 0.5);
                    break;
                case RegressionImperfection.Moderate:
                    moderate++;
                    Assert.Contains(imperfection.NoiseFloorDb, new[] { -100.0, -80.0 });
                    Assert.InRange(Math.Abs(imperfection.ClockPpm), 0.0, 20.0);
                    Assert.InRange(Math.Abs(imperfection.MicrophoneDeviationDb), 0.0, 1.0);
                    break;
                default:
                    stress++;
                    Assert.Equal(-60.0, imperfection.NoiseFloorDb);
                    Assert.InRange(Math.Abs(imperfection.ClockPpm), 0.0, 50.0);
                    Assert.InRange(Math.Abs(imperfection.MicrophoneDeviationDb), 0.0, 2.0);
                    break;
            }

            // A perfect microphone carries no deviation; a tilt profile carries one inside the class's band.
            if (imperfection.MicrophoneProfile == MicrophoneResponseProfile.Perfect)
                Assert.Equal(0.0, imperfection.MicrophoneDeviationDb);

            RegressionAlignmentFamily expected = index % 3 == 0 ? RegressionAlignmentFamily.Independent : RegressionAlignmentFamily.Misaligned;
            Assert.Equal(expected, scenario.Family);
            if (expected == RegressionAlignmentFamily.Independent) independent++; else misaligned++;
        }

        Assert.Equal(70, light);
        Assert.Equal(20, moderate);
        Assert.Equal(10, stress);
        Assert.Equal(34, independent);
        Assert.Equal(66, misaligned);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Plans and fixtures.
    // ---------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(RegressionMode.Quick, 20)]
    [InlineData(RegressionMode.Standard, 100)]
    [InlineData(RegressionMode.Stress, 500)]
    public void Every_plan_covers_its_indices_and_keeps_seeds_unique(RegressionMode mode, int expectedCount)
    {
        IReadOnlyList<RegressionScenario> plan = RandomScenarioGenerator.Plan(mode);
        output.WriteLine($"{mode}: {plan.Count} scenarios (expected >= {expectedCount})");

        Assert.True(plan.Count >= expectedCount);
        Assert.Equal(plan.Count, plan.Select(scenario => scenario.Seed).Distinct().Count());
        Assert.Equal(plan.Count, plan.Select(scenario => scenario.Index).Distinct().Count());
        Assert.Equal(plan.OrderBy(scenario => scenario.Index).Select(scenario => scenario.Index).ToArray(),
                     plan.Select(scenario => scenario.Index).ToArray());

        // Coverage is exactly the first N indices plus fixtures: no index may be dropped, ever.
        var indices = plan.Select(scenario => scenario.Index).ToHashSet();
        for (int index = 0; index < expectedCount; index++)
            Assert.Contains(index, indices);
    }

    [Fact]
    public void Every_fixture_seed_is_in_the_standard_plan_replayable_and_in_range()
    {
        Assert.NotEmpty(RegressionFixtures.Seeds);
        IReadOnlyList<RegressionScenario> standard = RandomScenarioGenerator.Plan(RegressionMode.Standard);

        foreach (int seed in RegressionFixtures.Seeds)
        {
            int index = RandomScenarioGenerator.IndexForSeed(seed);
            RegressionScenario listed = standard.SingleOrDefault(scenario => scenario.Seed == seed)
                ?? throw new InvalidOperationException($"fixture seed {seed} is missing from the standard plan");
            RegressionScenario replayed = RandomScenarioGenerator.Replay(seed);

            Assert.Equal(index, listed.Index);
            Assert.Equal(index, replayed.Index);
            Assert.Equal(RandomScenarioGenerator.Create(index).Signature(), replayed.Signature());
            Assert.Equal(listed.Signature(), replayed.Signature());
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Judgement layers, driven with synthetic metrics.
    // ---------------------------------------------------------------------------------------------------------

    private static BandSpatialStats Stats(double stdDevDb) => new(0, 0, stdDevDb, 0, 0, 0, 0, 0, 0);

    private static RegressionMetrics Metrics(int index, BandSpatialStats before, BandSpatialStats after) => new()
    {
        Index = index,
        Seed = RandomScenarioGenerator.SeedForIndex(index),
        ScenarioId = $"reg-{index:D4}",
        Before = before,
        After = after,
        ScoreBefore = 10.0,
        ScoreAfter = 10.0,
        MaxAchievedBoostDb = 0.0,
        MaxBoostLimitDb = 30.0,
        RecommendedSomething = true,
        PhysicalResidualRatio = 1e-9,
        ValidSetting = true,
    };

    [Fact]
    public void Any_non_finite_value_is_detected()
    {
        Assert.True(RegressionRunner.AnyNonFinite(double.NaN));
        Assert.True(RegressionRunner.AnyNonFinite(1.0, double.PositiveInfinity));
        Assert.True(RegressionRunner.AnyNonFinite(double.NegativeInfinity));
        Assert.False(RegressionRunner.AnyNonFinite(0.0, -1.0, 1e-12));
    }

    [Fact]
    public void Out_of_range_settings_are_detected_against_the_search_box()
    {
        var options = new OptimizerOptions { IncludeDelay = true };

        Assert.False(RegressionRunner.SettingOutOfRange(null, options));
        Assert.False(RegressionRunner.SettingOutOfRange(SubwooferSetting.FromDegrees(6.0, 180.0, -1, 0.01), options));

        Assert.True(RegressionRunner.SettingOutOfRange(SubwooferSetting.FromDegrees(6.5, 0.0), options));
        Assert.True(RegressionRunner.SettingOutOfRange(SubwooferSetting.FromDegrees(-6.5, 0.0), options));
        Assert.True(RegressionRunner.SettingOutOfRange(SubwooferSetting.FromDegrees(0.0, -1.0), options));
        Assert.True(RegressionRunner.SettingOutOfRange(SubwooferSetting.FromDegrees(0.0, 181.0), options));
        Assert.True(RegressionRunner.SettingOutOfRange(SubwooferSetting.FromDegrees(0.0, 0.0, 0, 0.0), options));
        Assert.True(RegressionRunner.SettingOutOfRange(SubwooferSetting.FromDegrees(0.0, 0.0, 1, 0.011), options));
        Assert.True(RegressionRunner.SettingOutOfRange(SubwooferSetting.FromDegrees(0.0, 0.0, 1, 0.001), new OptimizerOptions()));
        Assert.True(RegressionRunner.SettingOutOfRange(SubwooferSetting.FromDegrees(double.NaN, 0.0), options));
    }

    [Fact]
    public void Both_the_judgement_thresholds_are_the_documented_ones()
    {
        Assert.True(RegressionRunner.IsOptimizable(RegressionRunner.NearOptimalSigmaDb + 0.01));
        Assert.False(RegressionRunner.IsOptimizable(RegressionRunner.NearOptimalSigmaDb));
        Assert.True(RegressionRunner.IsClearlyWorse(-RegressionRunner.ClearlyWorseSigmaDb - 0.01));
        Assert.False(RegressionRunner.IsClearlyWorse(-RegressionRunner.ClearlyWorseSigmaDb));
    }

    [Fact]
    public void NaN_boost_violations_and_out_of_range_settings_are_hard_failures()
    {
        RegressionScenario scenario = RandomScenarioGenerator.Create(2);

        ScenarioJudgement nan = RegressionRunner.Judge(scenario, Metrics(2, Stats(3.0), Stats(3.0)) with { NonFiniteValues = true });
        Assert.Contains("NaN", string.Join(" ", nan.HardFailures));
        Assert.False(nan.Passed);

        ScenarioJudgement boost = RegressionRunner.Judge(scenario, Metrics(2, Stats(3.0), Stats(3.0)) with { MaxAchievedBoostDb = 30.5 });
        Assert.Contains("boost limit", string.Join(" ", boost.HardFailures));
        Assert.False(boost.Passed);

        ScenarioJudgement range = RegressionRunner.Judge(scenario, Metrics(2, Stats(3.0), Stats(3.0)) with { ValidSetting = false });
        Assert.Contains("search range", string.Join(" ", range.HardFailures));
        Assert.False(range.Passed);

        ScenarioJudgement physical = RegressionRunner.Judge(scenario, Metrics(2, Stats(3.0), Stats(3.0)) with { PhysicalResidualRatio = 1.0 });
        Assert.Contains("physical inconsistency", string.Join(" ", physical.HardFailures));
        Assert.False(physical.Passed);
    }

    [Fact]
    public void Near_optimal_rooms_are_exempt_and_small_worsening_inside_tolerance_passes()
    {
        RegressionScenario scenario = RandomScenarioGenerator.Create(3);

        // Already even: a small worsening is inside layer C's tolerance and layer D's clearly-worse band.
        ScenarioJudgement nearOptimal = RegressionRunner.Judge(scenario, Metrics(3, Stats(0.10), Stats(0.30)));
        Assert.False(nearOptimal.Optimizable);
        Assert.False(nearOptimal.ClearlyWorse);
        Assert.True(nearOptimal.Passed, nearOptimal.FailureReport());

        // Clearly above the floor, but the worsening is inside the documented tolerance.
        ScenarioJudgement tolerant = RegressionRunner.Judge(scenario, Metrics(3, Stats(3.0), Stats(3.0 + (RegressionRunner.OptimizableWorseningToleranceDb / 2.0))));
        Assert.True(tolerant.Optimizable);
        Assert.True(tolerant.Passed, tolerant.FailureReport());
    }

    [Fact]
    public void A_worsening_beyond_tolerance_is_a_regression_and_clearly_worse_names_the_seed_to_register()
    {
        RegressionScenario scenario = RandomScenarioGenerator.Create(1);          // index 1 → seed 20260920, not a fixture
        Assert.False(RegressionFixtures.Contains(scenario.Seed));

        ScenarioJudgement beyond = RegressionRunner.Judge(scenario, Metrics(1, Stats(3.0), Stats(3.0 + (RegressionRunner.OptimizableWorseningToleranceDb * 1.5))));
        Assert.False(beyond.ClearlyWorse);
        Assert.Contains("tolerance", string.Join(" ", beyond.Regressions));
        Assert.False(beyond.Passed);

        ScenarioJudgement worse = RegressionRunner.Judge(scenario, Metrics(1, Stats(3.0), Stats(3.0 + RegressionRunner.ClearlyWorseSigmaDb + 0.1)));
        Assert.True(worse.ClearlyWorse);
        string text = string.Join(" ", worse.Regressions);
        Assert.Contains("clearly worse", text);
        Assert.Contains(scenario.Seed.ToString(CultureInfo.InvariantCulture), text);
        Assert.Contains("RegressionFixtures.Seeds", text);

        // A fixture seed is pointed at as already registered instead.
        RegressionScenario fixture = RandomScenarioGenerator.Replay(RegressionFixtures.Seeds[0]);
        ScenarioJudgement fixtureWorse = RegressionRunner.Judge(fixture, Metrics(fixture.Index, Stats(3.0), Stats(4.0)));
        Assert.Contains("already a checked-in fixture", string.Join(" ", fixtureWorse.Regressions));
    }

    [Fact]
    public void A_score_that_got_worse_is_a_regression_even_when_sigma_improves()
    {
        RegressionScenario scenario = RandomScenarioGenerator.Create(4);
        ScenarioJudgement judgement = RegressionRunner.Judge(scenario, Metrics(4, Stats(3.0), Stats(1.0)) with { ScoreAfter = 11.0 });

        Assert.Contains("score regression", string.Join(" ", judgement.Regressions));
        Assert.False(judgement.Passed);
    }

    [Fact]
    public void The_physical_identity_ratio_is_tiny_when_AB_is_A_plus_B_and_large_when_it_is_not()
    {
        var band = new FrequencyBand(20.0, 150.0);
        PositionResponse Point(double real, double imaginary)
            => new("p1", band, [new FrequencyResponse(50.0, real, imaginary, 0.0, 0.0, 0.0)]);

        var consistent = new DualSubMeasurement([Point(1.0, 0.0)], [Point(0.0, 1.0)], [Point(1.0, 1.0)]);
        Assert.True(RegressionRunner.PhysicalResidualRatio(consistent) < 1e-12);

        var broken = new DualSubMeasurement([Point(1.0, 0.0)], [Point(0.0, 1.0)], [Point(0.0, 0.0)]);
        Assert.True(RegressionRunner.PhysicalResidualRatio(broken) > 0.9,
            "AB missing the sum entirely must read as clearly inconsistent");
    }

    // ---------------------------------------------------------------------------------------------------------
    // Structural isolation: the optimizer must not be able to see the simulator.
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void No_public_optimizer_member_exposes_a_simulation_type()
    {
        Assembly simulation = typeof(VirtualLab).Assembly;
        Assembly optimization = typeof(SubwooferOptimizer).Assembly;
        Assert.NotEmpty(optimization.GetExportedTypes());

        var offenders = new List<string>();
        foreach (Type type in optimization.GetExportedTypes())
        {
            foreach (MemberInfo member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                foreach (Type exposed in ExposedTypes(member).SelectMany(Flatten))
                {
                    if (exposed == typeof(void) || exposed.IsGenericParameter) continue;
                    if (exposed.Assembly == simulation)
                        offenders.Add($"{type.Name}.{member.Name} → {exposed.Name}");
                }
            }
        }

        output.WriteLine($"Optimization: {optimization.GetExportedTypes().Length} public types checked");
        Assert.Empty(offenders);
    }

    [Fact]
    public void The_runner_hands_the_optimizer_a_DualSubMeasurement_and_nothing_from_the_simulator()
    {
        MethodInfo toInput = typeof(VirtualLab).GetMethod(nameof(VirtualLab.AsOptimizerInput))
            ?? throw new InvalidOperationException("VirtualLab.AsOptimizerInput is missing");
        MethodInfo search = typeof(SubwooferOptimizer).GetMethod(nameof(SubwooferOptimizer.Search))
            ?? throw new InvalidOperationException("SubwooferOptimizer.Search is missing");

        Assert.Equal(typeof(DualSubMeasurement), toInput.ReturnType);
        Assert.Equal(typeof(DualSubMeasurement), search.GetParameters()[0].ParameterType);

        string runnerSource = File.ReadAllText(Path.Combine(TestPaths.SourceRoot, "AudioOptimizer.Simulation", "RegressionRunner.cs"));
        Assert.Contains("lab.AsOptimizerInput(measurements)", runnerSource);
        Assert.Contains("SubwooferOptimizer.Search(input, scenario.Scenario.Optimize)", runnerSource);
    }

    // ---------------------------------------------------------------------------------------------------------
    // End-to-end: two generated scenarios through the whole chain.
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void Two_generated_scenarios_run_end_to_end_without_a_hard_failure()
    {
        // Index 0 (independent, light) and index 1 (misaligned, light) — one of each family. Run concurrently so the
        // suite pays one scenario's wall clock, not two.
        var judgements = new ScenarioJudgement[2];
        Parallel.Invoke(
            () => judgements[0] = RegressionRunner.RunScenario(RandomScenarioGenerator.Create(0)),
            () => judgements[1] = RegressionRunner.RunScenario(RandomScenarioGenerator.Create(1)));

        var report = new RegressionReport(judgements);
        output.WriteLine(report.Format());

        Assert.Equal(2, report.Count);
        Assert.Equal(0, report.HardFailureCount);
        Assert.Equal(0, report.CrashCount);
        Assert.Equal(0, report.NonFiniteCount);
        Assert.Equal(0, report.BoostViolationCount);

        foreach (ScenarioJudgement judgement in judgements)
        {
            RegressionMetrics metrics = judgement.Metrics;
            output.WriteLine($"{judgement.Scenario.Scenario.Id}: {metrics.SummaryLine()}");

            Assert.True(judgement.Passed, judgement.FailureReport());
            Assert.True(double.IsFinite(metrics.Before.StdDevDb));
            Assert.True(double.IsFinite(metrics.After.StdDevDb));
            Assert.InRange(metrics.PhysicalResidualRatio, 0.0, RegressionRunner.PhysicalResidualToleranceRatio);
            Assert.Equal(metrics.MaxBoostLimitDb, RandomScenarioGenerator.OptimizerMaxBoostLimitDb);
            Assert.Equal(judgement.Scenario.Seed, metrics.Seed);
            Assert.Equal(judgement.Scenario.Index, metrics.Index);
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // CLI argument handling — cheap paths only, no plan is executed.
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void Help_and_list_keep_working()
    {
        (int noArgsCode, _, _) = RunMain();
        Assert.Equal(1, noArgsCode);

        (int helpCode, string helpOut, _) = RunMain("--help");
        Assert.Equal(0, helpCode);
        Assert.Contains("--regression", helpOut);
        Assert.Contains("--replay", helpOut);

        (int listCode, string listOut, _) = RunMain("--list");
        Assert.Equal(0, listCode);
        foreach (SimulationScenario scenario in SimulationScenarios.All)
            Assert.Contains(scenario.Id, listOut);
    }

    [Fact]
    public void Bad_regression_plans_and_bad_seeds_exit_one_with_a_clear_error()
    {
        (int planCode, _, string planError) = RunMain("--regression", "bogus");
        Assert.Equal(1, planCode);
        Assert.Contains("quick|standard|stress", planError);

        (int missingCode, _, string missingError) = RunMain("--regression");
        Assert.Equal(1, missingCode);
        Assert.Contains("quick|standard|stress", missingError);

        (int textCode, _, string textError) = RunMain("--replay", "not-a-number");
        Assert.Equal(1, textCode);
        Assert.Contains("integer", textError);

        (int rangeCode, _, string rangeError) = RunMain("--replay", "123456789");
        Assert.Equal(1, rangeCode);
        Assert.Contains("123456789", rangeError);
    }

    private static (int ExitCode, string Out, string Error) RunMain(params string[] args)
    {
        lock (ConsoleGate)
        {
            TextWriter originalOut = Console.Out;
            TextWriter originalError = Console.Error;
            var capturedOut = new StringWriter();
            var capturedError = new StringWriter();
            try
            {
                Console.SetOut(capturedOut);
                Console.SetError(capturedError);
                int exitCode = Program.Main(args);
                return (exitCode, capturedOut.ToString(), capturedError.ToString());
            }
            finally
            {
                Console.SetOut(originalOut);
                Console.SetError(originalError);
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Reflection helpers.
    // ---------------------------------------------------------------------------------------------------------

    private static IEnumerable<Type> ExposedTypes(MemberInfo member)
    {
        switch (member)
        {
            case MethodInfo method:
                yield return method.ReturnType;
                foreach (ParameterInfo parameter in method.GetParameters()) yield return parameter.ParameterType;
                break;
            case PropertyInfo property:
                yield return property.PropertyType;
                break;
            case FieldInfo field:
                yield return field.FieldType;
                break;
            case ConstructorInfo constructor:
                foreach (ParameterInfo parameter in constructor.GetParameters()) yield return parameter.ParameterType;
                break;
        }
    }

    private static IEnumerable<Type> Flatten(Type type)
    {
        yield return type;
        if (type.IsArray && type.GetElementType() is { } element)
            foreach (Type nested in Flatten(element)) yield return nested;
        if (type.IsGenericType)
            foreach (Type argument in type.GetGenericArguments())
                foreach (Type nested in Flatten(argument)) yield return nested;
    }
}
