namespace AudioOptimizer.Simulation;

using AudioOptimizer.Core;
using AudioOptimizer.Optimization;

/// <summary>
/// One experiment: a room, one or two subs with their settings, the microphones to measure at, and whether the
/// optimizer should be pointed at the result.
/// <para>
/// <see cref="Subs"/> and <see cref="GroundTruthSetting"/> are the truth: the first is the physical state during
/// measurement, the second is the correction the scenario's designer knows is right. Only the second is ever compared
/// against a recommendation; neither is ever an input to the search.
/// </para>
/// </summary>
public sealed record SimulationScenario(
    string Id,
    string Title,
    string Description,
    SimulationConfig Config,
    IReadOnlyList<VirtualSubwoofer> Subs,
    IReadOnlyList<MeasurementPoint> Microphones,
    bool RunOptimizer = false,
    OptimizerOptions? Optimize = null,
    SubwooferSetting? GroundTruthSetting = null)
{
    public VirtualLab CreateLab() => new(Config, Subs, Microphones);
}

/// <summary>
/// The five scenarios. They are data, not code paths: every one of them runs through exactly the same chain, and the
/// differences between them are the room, the ISM order, the sub settings and whether the optimizer is pointed at it.
/// </summary>
public static class SimulationScenarios
{
    /// <summary>A microphone at the middle of the listening region — the single-point scenario uses it.</summary>
    private static readonly MeasurementPoint CentreMic =
        ListeningRegion.Default.Points.Single(point => point.Id == "x0_y0_z0");

    private static readonly Position FrontCorner = new(0.35, 0.35, 0.30);

    /// <summary>The mirror of <see cref="FrontCorner"/> about the default listening region's centre (1.65, 1.80).</summary>
    private static readonly Position RearCorner = new(2.95, 3.25, 0.30);

    /// <summary>
    /// S1 — direct sound only. The analytic reference is exact here: amplitude 1/r from the 1 m calibration,
    /// delay = d/c, and phase(f) = −2πf·d/c, with no room to reason about. Measured against those closed forms the
    /// product's own deconvolution lands the impulse peak on the predicted sample exactly.
    /// </summary>
    public static readonly SimulationScenario SingleSub = new(
        "single-sub",
        "S1 single sub, direct sound",
        "One sub and one microphone with the ISM order at 0: distance attenuation, propagation delay and the "
        + "propagation phase are checked against their closed forms, through the product's own deconvolution.",
        SimulationConfig.Default with { ImageSourceOrder = 0 },
        [new VirtualSubwoofer(new Position(0.30, 0.40, 0.35))],
        [CentreMic]);

    /// <summary>
    /// S2 — two subs, 27 microphones, no noise. A noiseless rig on purpose: what is under test is the complex sum, so
    /// every dB of the result should come from the interference and none of it from a noise floor.
    /// </summary>
    public static readonly SimulationScenario TwoSubSimple = new(
        "two-sub",
        "S2 two subs, 27 microphones, no noise",
        "Two subs at −3 dB and +90° relative to each other, measured at all 27 positions with the noise floor at "
        + "zero: the A+B pass is the physical sum of the two, so gain, polarity and phase show up as interference. "
        + "The noiseless capture is also the one case the shipped dropout check has an opinion about — with the floor "
        + "at exactly zero its scan window overruns the sweep into digital-silence post-roll on the closest "
        + "positions, which it reports as DropoutDetected; the deconvolved results are unaffected.",
        SimulationConfig.Default with { ImageSourceOrder = 0, MicrophoneNoiseLevel = 0.0 },
        [
            new VirtualSubwoofer(new Position(0.30, 0.30, 0.35)),
            new VirtualSubwoofer(new Position(3.00, 3.30, 0.35), GainDb: -3.0, PhaseDegrees: 90.0),
        ],
        ListeningRegion.Default.Points);

    /// <summary>
    /// S3 — the modal room. The reflections are the only source of the resonances: nothing in this project writes down
    /// c/2·√((nx/L)² + …); the images sum into it.
    /// </summary>
    public static readonly SimulationScenario ModalRoom = new(
        "modal-room",
        "S3 modal room, 40–100 Hz",
        "A corner sub with reflections on all six surfaces and a deep ISM order, so the room's own resonances are "
        + "excited. The 27 positions show how uneven a small room is before anything is done.",
        SimulationConfig.Default with { ImageSourceOrder = 10, PostRollSeconds = 1.0 },
        [new VirtualSubwoofer(FrontCorner)],
        ListeningRegion.Default.Points);

    /// <summary>
    /// S4 — dual-sub spatial optimisation. Sub B is measured in a deliberately wrong state, and the scenario knows a
    /// correction that aligns it (see <see cref="GroundTruthSetting"/>); the optimizer has to find as good a setting
    /// from the A and B measurements alone, which is the black-box question this scenario exists for.
    /// <para>
    /// The boost limit is opened to 30 dB, deliberately and unlike the product's 3 dB default: the max-boost measure is
    /// relative to the MEASURED total, and this room's own interference nulls are tens of dB deep, so any realignment
    /// counts as a large level increase at the bins where that total is nulled — even the declared ground truth needs
    /// 39 dB of it. Asking the recovery question through a 3 dB limit gets "no change" for an answer, which is correct
    /// but answers a different question. The discipline question is S5's, at the ordinary 3 dB.
    /// </para>
    /// </summary>
    public static readonly SimulationScenario DualSub = new(
        "dual-sub",
        "S4 dual-sub spatial optimisation",
        "Sub A at 0 dB and sub B measured at −4 dB and +70°, which is a misalignment. The optimizer sees only the "
        + "measured A, B and A+B complex responses and must recover the spatial uniformity a known-good correction "
        + "reaches — measured, it does better than that correction while staying inside its boost limit.",
        SimulationConfig.Default,
        [
            new VirtualSubwoofer(new Position(0.45, 0.45, 0.35)),
            new VirtualSubwoofer(new Position(2.85, 3.15, 0.35), GainDb: -4.0, PhaseDegrees: 70.0),
        ],
        ListeningRegion.Default.Points,
        RunOptimizer: true,
        Optimize: new OptimizerOptions { IncludeDelay = true, MaxBoostLimitDb = 30.0 },
        GroundTruthSetting: new SubwooferSetting(2.5, 110.0 * Math.PI / 180.0, -1, 0.002));

    /// <summary>
    /// S5 — the null regression. The two subs are mirror images about the middle of the listening area and one of them
    /// is wired in opposite polarity, so they nearly cancel where they are equidistant. The residual at those
    /// positions is the room's own doing, and what the walk-in question wants to know is whether the search tries to
    /// buy its way out of a room problem with gain.
    /// </summary>
    public static readonly SimulationScenario DeepNull = new(
        "deep-null",
        "S5 deep null at 50 Hz",
        "Two mirror-image subs with one wired in opposite polarity and +0.8 dB, so the residual where they are "
        + "equidistant is small rather than zero: measured, the 50 Hz level at the deepest position sits 25 dB below "
        + "the 50 Hz spatial mean. The limit is the ordinary 3 dB, and the question is whether the search chases that "
        + "null.",
        SimulationConfig.Default,
        [
            new VirtualSubwoofer(FrontCorner),
            new VirtualSubwoofer(RearCorner, GainDb: 0.8, Polarity: -1),
        ],
        ListeningRegion.Default.Points,
        RunOptimizer: true,
        Optimize: new OptimizerOptions { MaxBoostLimitDb = 3.0 });

    public static readonly IReadOnlyList<SimulationScenario> All = [SingleSub, TwoSubSimple, ModalRoom, DualSub, DeepNull];

    /// <summary>Resolves a scenario by its command-line id; null when there is no such scenario.</summary>
    public static SimulationScenario? Find(string id)
        => All.FirstOrDefault(scenario => string.Equals(scenario.Id, id, StringComparison.OrdinalIgnoreCase));
}
