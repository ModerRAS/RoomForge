namespace AudioOptimizer.Tests;

using AudioOptimizer.Core;
using AudioOptimizer.Dsp;
using AudioOptimizer.Measurement;
using AudioOptimizer.Optimization;
using AudioOptimizer.Simulation;
using Xunit.Abstractions;

/// <summary>
/// The measurement-reality layer: playback-vs-capture clock error, the microphone's own response, the noise floor and
/// repeatability. Every response here was produced by the shipped chain (<c>VirtualLab</c> → <c>PointMeasurement.Run</c>);
/// the ground truth is never allowed to see the errors the measurement is supposed to carry.
/// </summary>
public class SimulationRealityTests(ITestOutputHelper output)
{
    private static readonly MeasurementPoint Centre = ListeningRegion.Default.Points.Single(point => point.Id == "x0_y0_z0");

    private static readonly Position SubPosition = new(0.30, 0.40, 0.35);

    /// <summary>Direct sound only: the reality knobs, with no room to complicate the comparison.</summary>
    private static SimulationConfig DirectOnly(SimulationConfig? config = null)
        => (config ?? SimulationConfig.Default) with { ImageSourceOrder = 0 };

    private static SimulatedMeasurement MeasureA(SimulationConfig config, VirtualSubwoofer sub, MeasurementPoint? point = null)
    {
        MeasurementPoint microphone = point ?? Centre;
        return new VirtualLab(config, [sub], [microphone]).Measure(SubMode.A, microphone);
    }

    /// <summary>Nine points (the z = 0 plane) is enough for a spatial metric without paying for all 27.</summary>
    private static MeasurementPoint[] NinePoints()
        => [.. ListeningRegion.Default.Points.Where(point => point.GridZ == 0)];

    private static FrequencyResponse BinAt(IReadOnlyList<FrequencyResponse> bins, double frequencyHz)
        => bins.OrderBy(bin => Math.Abs(bin.FrequencyHz - frequencyHz)).First();

    private static double[] RecordOf(SimulationConfig config, double[] playback, uint seed)
    {
        var rig = new VirtualRoom(config, config.Room, [new VirtualSubwoofer(SubPosition)], [Centre]);
        return rig.Record(playback, SubMode.A, 0, config.PreRollSeconds, config.PostRollSeconds, seed);
    }

    // ── 2.1 clock drift ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_zero_clock_error_leaves_the_capture_bit_identical()
    {
        SimulationConfig baseline = DirectOnly();
        var sub = new VirtualSubwoofer(SubPosition);

        double[] plain = MeasureA(baseline, sub).Result.Recording;
        double[] zero = MeasureA(baseline with { ClockPpm = 0.0 }, sub).Result.Recording;

        output.WriteLine($"{plain.Length} samples; equal: {plain.SequenceEqual(zero)}");
        Assert.Equal(plain, zero);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(5.0)]
    [InlineData(10.0)]
    [InlineData(20.0)]
    [InlineData(50.0)]
    public void A_non_zero_clock_error_rescales_the_capture_and_the_signs_differ(double ppm)
    {
        SimulationConfig config = DirectOnly();
        double[] playback = SweepGenerator.GenerateExponentialSweep(config.Sweep);
        double[] baseline = RecordOf(config, playback, 12345);
        double[] plus = RecordOf(config with { ClockPpm = ppm }, playback, 12345);
        double[] minus = RecordOf(config with { ClockPpm = -ppm }, playback, 12345);

        int changed = 0;
        double maxDifference = 0.0;
        for (int i = 0; i < baseline.Length; i++)
        {
            if (baseline[i] != plus[i]) changed++;
            maxDifference = Math.Max(maxDifference, Math.Abs(baseline[i] - plus[i]));
        }

        output.WriteLine($"{ppm:+0;-0} ppm: {changed}/{plus.Length} samples changed, max |Δ| {maxDifference:E3}, "
            + $"± signs differ by {plus.Zip(minus).Max(pair => Math.Abs(pair.First - pair.Second)):E3}");

        Assert.NotEqual(baseline, plus);
        Assert.NotEqual(baseline, minus);
        Assert.NotEqual(plus, minus);
        Assert.True(maxDifference > 0.0, "a non-zero clock error changed no sample");
        Assert.All(plus, sample => Assert.True(double.IsFinite(sample)));
        Assert.All(minus, sample => Assert.True(double.IsFinite(sample)));
    }

    [Theory]
    [InlineData(50.0)]
    [InlineData(-50.0)]
    public void A_drifted_dual_sub_scenario_still_measures_and_optimizes(double ppm)
    {
        SimulationConfig config = DirectOnly() with { ClockPpm = ppm };
        var a = new VirtualSubwoofer(new Position(0.45, 0.45, 0.35));
        var b = new VirtualSubwoofer(new Position(2.85, 3.15, 0.35), GainDb: -4.0, PhaseDegrees: 70.0);
        var lab = new VirtualLab(config, [a, b], NinePoints());

        IReadOnlyList<SimulatedMeasurement> measurements = lab.MeasureAll();
        Assert.Equal(27, measurements.Count);
        foreach (SimulatedMeasurement measurement in measurements)
        {
            Assert.All(measurement.Result.Recording, sample => Assert.True(double.IsFinite(sample), measurement.Id));
            Assert.All(measurement.Result.ImpulseResponse, sample => Assert.True(double.IsFinite(sample), measurement.Id));
            Assert.All(measurement.Result.Response,
                bin => Assert.True(double.IsFinite(bin.Real) && double.IsFinite(bin.Imag), measurement.Id));
        }

        OptimizerResult result = SubwooferOptimizer.Search(
            lab.AsOptimizerInput(measurements),
            new OptimizerOptions { IncludeDelay = true, MaxBoostLimitDb = 6.0 });

        Assert.NotNull(result.Recommended);
        result.Recommended!.Validate();
        Assert.NotNull(result.Constraint.MaxAchievedBoostDb);
        Assert.True(result.Constraint.MaxAchievedBoostDb.Value <= result.Constraint.MaxBoostLimitDb,
            $"{result.Constraint.MaxAchievedBoostDb.Value:F2} dB of boost against a {result.Constraint.MaxBoostLimitDb:F1} dB limit");
        Assert.True(double.IsFinite(result.ScoreBefore) && double.IsFinite(result.ScoreAfter));

        output.WriteLine($"{ppm:+0;-0} ppm: {result.Verdict}, recommended {result.Recommended.GainDb:+0.0;-0.0;0.0} dB / "
            + $"{result.Recommended.PhaseDegrees:0.0}° / {result.Recommended.DelaySeconds * 1000.0:0.0} ms, boost "
            + $"{result.Constraint.MaxAchievedBoostDb.Value:F2} dB of {result.Constraint.MaxBoostLimitDb:F1} allowed");
    }

    // ── 2.2 microphone response ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_zero_deviation_leaves_the_capture_bit_identical()
    {
        SimulationConfig baseline = DirectOnly();
        SimulationConfig tiltedButFlat = baseline with
        {
            MicrophoneProfile = MicrophoneResponseProfile.HighFrequencyTilt,
            MicrophoneDeviationDb = 0.0,
        };
        var sub = new VirtualSubwoofer(SubPosition);

        double[] plain = MeasureA(baseline, sub).Result.Recording;
        double[] flat = MeasureA(tiltedButFlat, sub).Result.Recording;

        Assert.Equal(plain, flat);
        Assert.Same(plain, MicrophoneResponse.Apply(plain, baseline.SampleRate, MicrophoneResponseProfile.Perfect, 0.0));
        Assert.Same(plain, MicrophoneResponse.Apply(plain, baseline.SampleRate, MicrophoneResponseProfile.LowFrequencyTilt, 0.0));
        output.WriteLine($"a zero deviation returned the same {plain.Length}-sample array by reference");
    }

    [Theory]
    [InlineData(MicrophoneResponseProfile.LowFrequencyTilt, -2.0)]
    [InlineData(MicrophoneResponseProfile.LowFrequencyTilt, -1.0)]
    [InlineData(MicrophoneResponseProfile.LowFrequencyTilt, -0.5)]
    [InlineData(MicrophoneResponseProfile.LowFrequencyTilt, 0.5)]
    [InlineData(MicrophoneResponseProfile.LowFrequencyTilt, 1.0)]
    [InlineData(MicrophoneResponseProfile.LowFrequencyTilt, 2.0)]
    [InlineData(MicrophoneResponseProfile.HighFrequencyTilt, -2.0)]
    [InlineData(MicrophoneResponseProfile.HighFrequencyTilt, -1.0)]
    [InlineData(MicrophoneResponseProfile.HighFrequencyTilt, -0.5)]
    [InlineData(MicrophoneResponseProfile.HighFrequencyTilt, 0.5)]
    [InlineData(MicrophoneResponseProfile.HighFrequencyTilt, 1.0)]
    [InlineData(MicrophoneResponseProfile.HighFrequencyTilt, 2.0)]
    public void A_microphone_deviation_changes_the_measured_response(MicrophoneResponseProfile profile, double deviationDb)
    {
        SimulationConfig baseline = DirectOnly();
        SimulationConfig colored = baseline with { MicrophoneProfile = profile, MicrophoneDeviationDb = deviationDb };
        var sub = new VirtualSubwoofer(SubPosition);

        SimulatedMeasurement plain = MeasureA(baseline, sub);
        SimulatedMeasurement moved = MeasureA(colored, sub);

        double differenceAt50Hz = BinAt(moved.Result.Response, 50.0).MagnitudeDb - BinAt(plain.Result.Response, 50.0).MagnitudeDb;
        double analyticAt50Hz = MicrophoneResponse.DeviationDb(profile, deviationDb, 50.0);

        output.WriteLine($"{profile} {deviationDb:+0.0;-0.0} dB: measured Δ at 50 Hz {differenceAt50Hz:+0.0000;-0.0000} dB against "
            + $"analytic {analyticAt50Hz:+0.0000;-0.0000} dB");

        Assert.NotEqual(plain.Result.Recording, moved.Result.Recording);
        Assert.NotEqual(plain.Result.Response, moved.Result.Response);
        Assert.True(Math.Abs(differenceAt50Hz - analyticAt50Hz) < 0.02,
            $"measured {differenceAt50Hz:F4} dB against the shape's {analyticAt50Hz:F4} dB");
    }

    [Theory]
    [InlineData(MicrophoneResponseProfile.LowFrequencyTilt, 2.0)]
    [InlineData(MicrophoneResponseProfile.HighFrequencyTilt, -2.0)]
    public void The_measured_deviation_matches_the_analytic_shape(MicrophoneResponseProfile profile, double deviationDb)
    {
        SimulationConfig baseline = DirectOnly();
        SimulationConfig colored = baseline with { MicrophoneProfile = profile, MicrophoneDeviationDb = deviationDb };
        var sub = new VirtualSubwoofer(SubPosition);

        FrequencyResponse[] plain = MeasureA(baseline, sub).Result.Response;
        FrequencyResponse[] moved = MeasureA(colored, sub).Result.Response;

        double maxError = 0.0, maxStep = 0.0, previous = double.NaN;
        int bins = 0;
        foreach (FrequencyResponse bin in moved)
        {
            if (bin.FrequencyHz is < 25.0 or > 145.0) continue;
            double measured = bin.MagnitudeDb - BinAt(plain, bin.FrequencyHz).MagnitudeDb;
            maxError = Math.Max(maxError, Math.Abs(measured - MicrophoneResponse.DeviationDb(profile, deviationDb, bin.FrequencyHz)));
            if (!double.IsNaN(previous)) maxStep = Math.Max(maxStep, Math.Abs(measured - previous));
            previous = measured;
            bins++;
        }

        output.WriteLine($"{profile} {deviationDb:+0.0;-0.0} dB over {bins} bins: max |measured − analytic| {maxError:F4} dB, "
            + $"largest bin-to-bin step {maxStep:F4} dB");
        Assert.True(bins > 400, $"only {bins} bins were compared");
        Assert.True(maxError < 0.05, $"the measured deviation was {maxError:F4} dB away from the analytic shape");
        Assert.True(maxStep < 0.05, $"the deviation jumped {maxStep:F4} dB between neighbouring bins, which is a room mode, not a microphone");
    }

    [Fact]
    public void The_microphone_response_never_reaches_the_ground_truth()
    {
        SimulationConfig baseline = DirectOnly();
        SimulationConfig colored = baseline with
        {
            MicrophoneProfile = MicrophoneResponseProfile.LowFrequencyTilt,
            MicrophoneDeviationDb = 2.0,
        };
        var sub = new VirtualSubwoofer(SubPosition);

        GroundTruthPosition plain = new VirtualLab(baseline, [sub], [Centre]).GroundTruth.At(Centre.Id);
        GroundTruthPosition moved = new VirtualLab(colored, [sub], [Centre]).GroundTruth.At(Centre.Id);

        Assert.Equal(
            plain.BinsA.Select(bin => (bin.FrequencyHz, bin.Real, bin.Imag)),
            moved.BinsA.Select(bin => (bin.FrequencyHz, bin.Real, bin.Imag)));
        output.WriteLine($"{plain.BinsA.Length} ground-truth bins identical with and without a {colored.MicrophoneProfile} profile");
    }

    [Theory]
    [InlineData(MicrophoneResponseProfile.LowFrequencyTilt, 1.5)]
    [InlineData(MicrophoneResponseProfile.HighFrequencyTilt, -1.5)]
    public void A_microphone_response_does_not_burst_the_boost_limit(MicrophoneResponseProfile profile, double deviationDb)
    {
        SimulationConfig config = DirectOnly() with { MicrophoneProfile = profile, MicrophoneDeviationDb = deviationDb };
        var a = new VirtualSubwoofer(new Position(0.45, 0.45, 0.35));
        var b = new VirtualSubwoofer(new Position(2.85, 3.15, 0.35), GainDb: -4.0, PhaseDegrees: 70.0);
        var lab = new VirtualLab(config, [a, b], NinePoints());

        OptimizerResult result = SubwooferOptimizer.Search(
            lab.AsOptimizerInput(lab.MeasureAll()),
            new OptimizerOptions { MaxBoostLimitDb = 3.0 });

        Assert.NotNull(result.Recommended);
        result.Recommended!.Validate();
        Assert.NotNull(result.Constraint.MaxAchievedBoostDb);
        Assert.True(result.Constraint.MaxAchievedBoostDb.Value <= result.Constraint.MaxBoostLimitDb,
            $"{result.Constraint.MaxAchievedBoostDb.Value:F2} dB of boost against a {result.Constraint.MaxBoostLimitDb:F1} dB limit");
        Assert.All(result.After.PerFrequency, row => Assert.True(double.IsFinite(row.MeanDb) && double.IsFinite(row.StdDevDb)));
        output.WriteLine($"{profile} {deviationDb:+0.0;-0.0} dB: {result.Verdict}, boost "
            + $"{result.Constraint.MaxAchievedBoostDb.Value:F2} dB of {result.Constraint.MaxBoostLimitDb:F1} allowed, σ "
            + $"{result.Before.MeanStdDevDb:F3} → {result.After.MeanStdDevDb:F3} dB");
    }

    // ── 2.3 noise floor ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_noise_floor_spellings_are_one_knob()
    {
        SimulationConfig config = SimulationConfig.Default;
        Assert.Equal(-120.0, config.MicrophoneNoiseFloorDb, 6);
        Assert.Equal(1e-6, config.MicrophoneNoiseLevel, 12);

        SimulationConfig byDb = config with { MicrophoneNoiseFloorDb = -80.0 };
        Assert.Equal(1e-4, byDb.MicrophoneNoiseLevel, 12);
        Assert.Equal(-80.0, byDb.MicrophoneNoiseFloorDb, 6);

        SimulationConfig silent = config with { MicrophoneNoiseFloorDb = double.NegativeInfinity };
        Assert.Equal(0.0, silent.MicrophoneNoiseLevel);
        Assert.True(double.IsNegativeInfinity(silent.MicrophoneNoiseFloorDb));
    }

    [Theory]
    [InlineData(-120.0)]
    [InlineData(-100.0)]
    [InlineData(-80.0)]
    [InlineData(-60.0)]
    public void Noise_floors_scale_with_the_configured_level_and_stay_reproducible(double floorDb)
    {
        SimulationConfig config = DirectOnly() with { MicrophoneNoiseFloorDb = floorDb };
        var sub = new VirtualSubwoofer(SubPosition);

        SimulatedMeasurement first = MeasureA(config, sub);
        SimulatedMeasurement second = MeasureA(config, sub);
        SimulatedMeasurement otherSeed = MeasureA(config with { NoiseSeed = config.NoiseSeed + 1 }, sub);

        int preRoll = (int)Math.Round(config.PreRollSeconds * config.SampleRate);
        double measuredRms = Math.Sqrt(first.Result.Recording.Take(preRoll).Select(sample => sample * sample).Average());
        double expectedRms = ComplexMath.DbToLinear(floorDb);

        output.WriteLine($"{floorDb} dBFS: pre-roll RMS {measuredRms:E4} against {expectedRms:E4}, "
            + $"issues [{string.Join(", ", first.Result.Issues)}]");

        Assert.True(first.Result.IsValid, $"the chain flagged a {floorDb} dBFS floor: {string.Join(", ", first.Result.Issues)}");
        Assert.Equal(first.Result.Recording, second.Result.Recording);
        Assert.NotEqual(first.Result.Recording, otherSeed.Result.Recording);
        Assert.InRange(measuredRms, expectedRms * 0.9, expectedRms * 1.1);
        Assert.All(first.Result.Recording, sample => Assert.True(double.IsFinite(sample)));
    }

    [Fact]
    public void S2_grades_clean_at_its_declared_noise_floor()
    {
        SimulationScenario scenario = SimulationScenarios.TwoSubSimple;
        Assert.Equal(-120.0, scenario.Config.MicrophoneNoiseFloorDb, 6);

        SimulationOutcome outcome = SimulationRunner.Run(scenario);

        Assert.Equal(27 * 3, outcome.Measurements.Count);
        Assert.DoesNotContain(QualityIssue.DropoutDetected, outcome.MeasuredSummary.Issues);
        Assert.True(outcome.MeasuredSummary.AllClean,
            $"S2 flagged {outcome.MeasuredSummary.Count - outcome.MeasuredSummary.Clean} points: "
            + $"{string.Join(", ", outcome.MeasuredSummary.Issues)}");
        output.WriteLine($"{outcome.MeasuredSummary.Clean}/{outcome.MeasuredSummary.Count} points clean; "
            + string.Join(" | ", outcome.MeasuredSummary.Verdicts.Select(verdict => $"{verdict.Mode} {verdict.Verdict}")));
    }

    // ── 2.4 repeatability ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Repeatability_stats_are_finite_and_small_at_the_default_floor()
    {
        SimulationConfig config = DirectOnly() with { MicrophoneNoiseFloorDb = -120.0 };
        RepeatabilityStats stats = Repeatability.Measure(config, [new VirtualSubwoofer(SubPosition)], Centre, repeats: 4);

        Assert.True(stats.Repeats > 1);
        Assert.Equal(4, stats.Repeats);
        Assert.All(
            new[] { stats.MeanMagnitudeErrorDb, stats.MaxMagnitudeErrorDb, stats.P90MagnitudeErrorDb,
                stats.MeanPhaseErrorDegrees, stats.MaxPhaseErrorDegrees },
            value => Assert.True(double.IsFinite(value)));
        Assert.True(stats.MeanMagnitudeErrorDb < 0.5, $"mean magnitude error was {stats.MeanMagnitudeErrorDb:F3} dB");
        Assert.True(stats.MeanPhaseErrorDegrees < 10.0, $"mean phase error was {stats.MeanPhaseErrorDegrees:F3}°");

        output.WriteLine($"4 captures at −120 dBFS: magnitude mean/P90/max "
            + $"{stats.MeanMagnitudeErrorDb:F4}/{stats.P90MagnitudeErrorDb:F4}/{stats.MaxMagnitudeErrorDb:F4} dB, phase mean/max "
            + $"{stats.MeanPhaseErrorDegrees:F4}/{stats.MaxPhaseErrorDegrees:F4}°");
    }

    [Fact]
    public void A_higher_noise_floor_makes_the_measurement_less_repeatable()
    {
        RepeatabilityStats quiet = Repeatability.Measure(
            DirectOnly() with { MicrophoneNoiseFloorDb = -120.0 }, [new VirtualSubwoofer(SubPosition)], Centre, repeats: 4);
        RepeatabilityStats loud = Repeatability.Measure(
            DirectOnly() with { MicrophoneNoiseFloorDb = -80.0 }, [new VirtualSubwoofer(SubPosition)], Centre, repeats: 4);

        output.WriteLine($"mean magnitude error: −120 dBFS {quiet.MeanMagnitudeErrorDb:F4} dB against −80 dBFS {loud.MeanMagnitudeErrorDb:F4} dB");
        Assert.True(loud.MeanMagnitudeErrorDb > quiet.MeanMagnitudeErrorDb,
            $"a −80 dBFS floor was no less repeatable ({loud.MeanMagnitudeErrorDb:F4} dB) than a −120 dBFS one ({quiet.MeanMagnitudeErrorDb:F4} dB)");
    }

    // ── Validate() range checks ───────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(600.0)]
    [InlineData(-600.0)]
    [InlineData(double.NaN)]
    public void A_clock_error_outside_the_supported_range_is_refused(double ppm)
        => Assert.Throws<ArgumentOutOfRangeException>(() => (DirectOnly() with { ClockPpm = ppm }).Validate());

    [Theory]
    [InlineData(6.5)]
    [InlineData(-6.5)]
    [InlineData(double.NaN)]
    public void A_microphone_deviation_outside_the_supported_range_is_refused(double deviationDb)
        => Assert.Throws<ArgumentOutOfRangeException>(() => (DirectOnly() with
        {
            MicrophoneProfile = MicrophoneResponseProfile.LowFrequencyTilt,
            MicrophoneDeviationDb = deviationDb,
        }).Validate());

    [Fact]
    public void A_perfect_microphone_cannot_carry_a_deviation()
        => Assert.Throws<ArgumentOutOfRangeException>(() => (DirectOnly() with { MicrophoneDeviationDb = 1.0 }).Validate());
}
