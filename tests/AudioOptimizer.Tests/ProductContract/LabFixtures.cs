namespace AudioOptimizer.Tests.ProductContract;

using AudioOptimizer.Core;
using AudioOptimizer.Optimization;
using AudioOptimizer.Simulation;

/// <summary>
/// The one real measurement-chain fixture the product-contract tests share: the S4 room the adversarial wave used
/// (A at (0.45, 0.45, 0.35), B at (2.85, 3.15, 0.35) with −4 dB / 70°), measured through the shipped chain —
/// VirtualAudioBackend → PointMeasurement.Run → deconvolution → alignment → FFT → FrequencyResponse →
/// MeasurementResult → DualSubMeasurement — with the simulator's ground truth never entering the optimizer input.
/// The subset/full-field variants are counts on the same fixture: 3 points, the 9-point z = 0 plane, 27 points region.
/// </summary>
internal static class LabFixtures
{
    public static readonly Position SubAPosition = new(0.45, 0.45, 0.35);
    public static readonly Position SubBPosition = new(2.85, 3.15, 0.35);

    /// <summary>The S4 room's correction: no gain, no phase, positive polarity, 2.0 ms of delay.</summary>
    public static readonly SubwooferSetting CanonicalDelay = new(0.0, 0.0, 1, 0.002);

    /// <summary>Diagnostic-mode options: the 30 dB limit the E-F1 attack used, with delay searched.</summary>
    public static OptimizerOptions Capability30 => new() { IncludeDelay = true, MaxBoostLimitDb = 30.0 };

    /// <summary>Product-safety options: the shipped 3 dB limit, with delay searched.</summary>
    public static OptimizerOptions ProductSafety => new() { IncludeDelay = true, MaxBoostLimitDb = 3.0 };

    public static IReadOnlyList<VirtualSubwoofer> Subs() =>
    [
        new VirtualSubwoofer(SubAPosition),
        new VirtualSubwoofer(SubBPosition, GainDb: -4.0, PhaseDegrees: 70.0),
    ];

    /// <summary>The z = 0 plane: the 9-position session the E-F1 sweep used (27 captures per realization).</summary>
    public static IReadOnlyList<MeasurementPoint> NinePoints() =>
        [.. ListeningRegion.Default.Points.Where(point => point.GridZ == 0)];

    /// <summary>Corners and centre: the cheapest 3-position session that still spans the z = 0 plane.</summary>
    public static IReadOnlyList<MeasurementPoint> ThreePoints()
    {
        MeasurementPoint[] nine = [.. NinePoints()];
        return [nine[0], nine[4], nine[8]];
    }

    /// <summary>The full shipped region: all 27 points.</summary>
    public static IReadOnlyList<MeasurementPoint> TwentySevenPoints() => ListeningRegion.Default.Points;

    /// <summary>Maps region point ids to the exact points (the adversarial wave's helper, reused).</summary>
    public static IReadOnlyList<MeasurementPoint> RegionMics(params string[] ids) =>
        [.. ids.Select(id => ListeningRegion.Default.Points.Single(point => point.Id == id))];

    /// <summary>Runs the real chain for one session and returns the optimizer's input. GroundTruth is never touched.</summary>
    public static DualSubMeasurement Measure(SimulationConfig config, IReadOnlyList<MeasurementPoint> microphones)
    {
        var lab = new VirtualLab(config, Subs(), microphones);
        return lab.AsOptimizerInput(lab.MeasureAll());
    }

    /// <summary>
    /// The same real chain, plus the simulator's ground truth so a test can evaluate what a recommendation was
    /// worth on the noiseless room. The truth is simulator/test-side evaluation only — it never enters an optimizer
    /// input, and the optimizer is never handed it.
    /// </summary>
    public static (DualSubMeasurement Input, GroundTruth Truth) MeasureWithTruth(SimulationConfig config, IReadOnlyList<MeasurementPoint> microphones)
    {
        var lab = new VirtualLab(config, Subs(), microphones);
        return (lab.AsOptimizerInput(lab.MeasureAll()), lab.GroundTruth);
    }

    /// <summary>The E-F1 room at one microphone noise seed; the room itself is identical across seeds.</summary>
    public static SimulationConfig SeedConfig(int seed) =>
        SimulationConfig.Default with { MicrophoneNoiseFloorDb = -60.0, NoiseSeed = seed };

    /// <summary>Deterministic configuration: the same room with the microphone noise floor off.</summary>
    public static SimulationConfig SilentConfig => SimulationConfig.Default with { MicrophoneNoiseFloorDb = double.NegativeInfinity };

    /// <summary>The achieved boost of one setting over the measured baseline, recomputed from the input.</summary>
    public static double BoostOf(DualSubMeasurement input, SubwooferSetting setting)
        => BoostPolicy.AchievedBoostDb(input, setting) ?? double.NaN;

    /// <summary>The objective score of one setting on a field. Evaluation only; never an optimizer input.</summary>
    public static double ScoreOf(DualSubMeasurement field, SubwooferSetting? setting)
    {
        SubwooferSetting resolved = setting ?? SubwooferSetting.Baseline;
        IReadOnlyList<PositionResponse> totals = SubwooferModel.Combine(field, resolved);
        SpatialSummary summary = SpatialMetrics.Compute(totals);
        double boost = ObjectiveFunction.MaxBoostVsBaselineDb(totals, SubwooferModel.Combine(field, SubwooferSetting.Baseline));
        return ObjectiveFunction.Score(summary, boost, ObjectiveWeights.Default);
    }
}
