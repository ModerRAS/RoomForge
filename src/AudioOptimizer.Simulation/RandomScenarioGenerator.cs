namespace AudioOptimizer.Simulation;

using AudioOptimizer.Core;
using AudioOptimizer.Optimization;

/// <summary>How many scenarios a regression run covers. <see cref="Stress"/> is coverage, not a bigger sweep.</summary>
public enum RegressionMode
{
    Quick = 20,
    Standard = 100,
    Stress = 500,
}

/// <summary>The imperfection band a scenario was drawn from — the classification the report breaks down by.</summary>
public enum RegressionImperfection
{
    /// <summary>Noise −120 dBFS, clock ±5 ppm, microphone deviation ±0.5 dB.</summary>
    Light,

    /// <summary>Noise −100 or −80 dBFS, clock ±20 ppm, microphone deviation ±1 dB.</summary>
    Moderate,

    /// <summary>Noise −60 dBFS, clock ±50 ppm, microphone deviation ±2 dB.</summary>
    Stress,
}

/// <summary>How the two subs' physical settings were drawn. Deterministic from the index, so every run covers both.</summary>
public enum RegressionAlignmentFamily
{
    /// <summary>A and B are independent uniform draws over the brief's full ranges.</summary>
    Independent,

    /// <summary>B is A plus a bounded error (the S4 recipe), so a reachable correction exists.</summary>
    Misaligned,
}

/// <summary>How the two subs sit relative to each other and the room. Purely a generation label for coverage.</summary>
public enum RegressionLayout
{
    Symmetric,
    Asymmetric,
    NearWall,
    SameWall,
    DifferentWall,
}

/// <summary>The target curve shape, drawn independently of the ground truth and anchored to the measured level.</summary>
public enum RegressionTargetCurve
{
    Flat,
    LowFrequencyHouse,
    MildDownward,
}

/// <summary>
/// One scenario's imperfection, as data: the four knobs a scenario turns on the recording side. The class gates the
/// band; the values are the exact draws, so a failure line can be replayed from the seed and checked by eye.
/// </summary>
public sealed record RegressionImperfectionProfile(
    RegressionImperfection Class,
    double ClockPpm,
    double NoiseFloorDb,
    MicrophoneResponseProfile MicrophoneProfile,
    double MicrophoneDeviationDb)
{
    public string Describe()
        => $"{Class}, clock {ClockPpm:+0.0;-0.0;0.0} ppm, noise {NoiseFloorDb:0.#} dBFS, "
         + $"mic {MicrophoneProfile} {MicrophoneDeviationDb:+0.0;-0.0;0.0} dB";
}

/// <summary>
/// One generated experiment: the scenario the lab runs, plus the generation-side facts a report needs. The whole
/// object is a pure function of <see cref="Seed"/> — <see cref="RandomScenarioGenerator.Replay"/> rebuilds it without
/// knowing the index, which is what makes <c>--replay &lt;seed&gt;</c> exact.
/// </summary>
public sealed record RegressionScenario(
    int Index,
    int Seed,
    RegressionLayout Layout,
    RegressionAlignmentFamily Family,
    RegressionTargetCurve TargetCurve,
    SimulationScenario Scenario)
{
    public SimulationConfig Config => Scenario.Config;

    public RoomModel Room => Scenario.Config.Room;

    public VirtualSubwoofer SubA => Scenario.Subs[0];

    public VirtualSubwoofer SubB => Scenario.Subs[1];

    /// <summary>
    /// The advisory reference, when a B-only drive-matching setting exists (B never later than A): B's drive is
    /// matched to A's. Advisory only — it is never an optimizer input — and absent for independent draws whose B
    /// delay exceeds A's, where no non-negative-delay correction can express it.
    /// </summary>
    public SubwooferSetting? GroundTruth => Scenario.GroundTruthSetting;

    /// <summary>Every generation-relevant value, as one string. Used by the determinism tests and by failure lines.</summary>
    public string Signature()
    {
        VirtualSubwoofer a = SubA, b = SubB;
        ListeningRegion region = Config.ListeningRegion;
        string reference = GroundTruth is { } truth
            ? $"gt={truth.GainDb:F4}dB|{truth.Polarity:+0;-0}|{truth.PhaseDegrees:F4}deg|{truth.DelaySeconds * 1000.0:F4}ms"
            : "gt=none";
        return $"index={Index} seed={Seed} layout={Layout} family={Family} target={TargetCurve} "
            + $"room={Room.LengthMetres:F4}x{Room.WidthMetres:F4}x{Room.HeightMetres:F4} ism={Config.ImageSourceOrder} "
            + $"region={region.WidthMetres:F4}x{region.DepthMetres:F4}x{region.HeightMetres:F4}@({region.CentreX:F4},{region.CentreY:F4},{region.CentreZ:F4}) "
            + $"mics={Scenario.Microphones.Count} noiseSeed={Config.NoiseSeed} "
            + $"A=({a.Position.X:F4},{a.Position.Y:F4},{a.Position.Z:F4})|{a.GainDb:F4}dB|{a.Polarity:+0;-0}|{a.PhaseDegrees:F4}deg|{a.DelaySeconds * 1000.0:F4}ms "
            + $"B=({b.Position.X:F4},{b.Position.Y:F4},{b.Position.Z:F4})|{b.GainDb:F4}dB|{b.Polarity:+0;-0}|{b.PhaseDegrees:F4}deg|{b.DelaySeconds * 1000.0:F4}ms "
            + $"imperfection=[{ImperfectionDescription()}] "
            + reference + " "
            + $"optimize=delay:{ConfigOptimizer().IncludeDelay},max{ConfigOptimizer().DelayMaxMilliseconds:F1}ms,boost{ConfigOptimizer().MaxBoostLimitDb:F1}dB";
    }

    /// <summary>A one-line configuration for a failure report: enough to reproduce by hand.</summary>
    public string Describe()
        => Scenario.Id
         + $" seed={Seed} room={Room.LengthMetres:F2}x{Room.WidthMetres:F2}x{Room.HeightMetres:F2}m "
         + $"A={SubA.Position} {SubA.GainDb:+0.0;-0.0;0.0}dB pol{SubA.Polarity:+#;-#;+1} {SubA.PhaseDegrees:0.0}deg {SubA.DelaySeconds * 1000.0:0.0}ms; "
         + $"B={SubB.Position} {SubB.GainDb:+0.0;-0.0;0.0}dB pol{SubB.Polarity:+#;-#;+1} {SubB.PhaseDegrees:0.0}deg {SubB.DelaySeconds * 1000.0:0.0}ms; "
         + $"{ImperfectionDescription()}";

    /// <summary>Replay command for the failure line: the seed is the only identifier a reader needs.</summary>
    public string ReplayCommand => $"--replay {Seed}";

    private OptimizerOptions ConfigOptimizer() => Scenario.Optimize ?? OptimizerOptions.Default;

    private string ImperfectionDescription() => Imperfection.Describe();

    public RegressionImperfectionProfile Imperfection { get; init; } = null!;
}

/// <summary>
/// Deterministic generator for the randomized optimizer regression lab.
/// <para>
/// <b>Seed derivation.</b> <c>Seed(index) = MasterSeed + index (mod 2^32)</c>; over the permitted index range
/// (0…<see cref="MaxScenarioIndex"/>) that is a bijection, so no two scenarios share a seed and <see cref="Replay"/>
/// recovers the index as <c>seed − MasterSeed</c> without a lookup table. The two seed values are never consecutive
/// in effect: the RNG mixes its state before drawing (see <see cref="Lcg"/>), so neighbouring indices are
/// decorrelated as well as distinct.
/// </para>
/// <para>
/// <b>Randomness source.</b> A hand-rolled 32-bit LCG with a splitmix-style finalizer, not <c>System.Random</c>, so
/// the stream is fixed by this file and a framework upgrade can never renumber scenarios. The physics itself is
/// untouched: every draw becomes an ordinary <see cref="SimulationConfig"/> value or sub position that the shipped
/// chain then treats exactly like a hand-written scenario.
/// </para>
/// <para>
/// <b>Draw order</b> (fixed forever; tests pin the derived seeds, so changing the order is a visible event):
/// room → layout mode → positions A, B → physical sub settings of the index's family (independent: A's and B's
/// gain/polarity/phase/delay; misaligned: A's gain/phase/delay/polarity and B's bounded error) → imperfection ppm,
/// noise floor, microphone profile, microphone deviation → target curve → listening-region scale. The imperfection
/// CLASS is not drawn: it is <c>index % 10</c> (0–6 light, 7–8 moderate, 9 stress), which makes the 70/20/10 split
/// exact in every plan rather than merely expected. The sub DRAW FAMILY is not drawn either: <c>index % 3 == 0</c> is
/// independent and the rest misaligned (1/3, 2/3), for the same reason.
/// </para>
/// <para>
/// <b>Clamps frozen here</b> (all inside the real API's accepted ranges): room 3.0–7.0 × 3.0–8.0 × 2.4–3.2 m;
/// sub wall clearance ≥ 0.25 m; sub height 0.25–0.6 m; minimum A–B separation 2.0 m (near-coincident subs carry no
/// optimisation question: the score is level-invariant, so a drive change on one source cannot move it); sub gain
/// ±6 dB; polarity ±1; phase ±180°; delay 0–30 ms. Sub B is drawn either independently or as A plus a bounded
/// misalignment (gain ±1.5–4.5 dB, phase ±40–160°, delay 0.5–8 ms, polarity optionally flipped) so both the
/// "declining is correct" and the "recoverable" families are exercised; see the draw comment in <see cref="Build"/>.
/// The OPTIMIZER's own search box is narrower and is what a recommendation must respect: gain B [−6, +6] dB (A fixed
/// at 0), phase [0, 180]°, polarity ±1, delay 0–10 ms (<see cref="OptimizerOptions.IncludeDelay"/> with
/// <see cref="OptimizerOptions.DelayMaxMilliseconds"/> at its 10 ms default). The physical-to-reference parameter
/// error is therefore ADVISORY: a random physical misalignment can sit outside the search box, and the report says so.
/// </para>
/// <para>
/// <b>Microphones.</b> 27 points from <see cref="ListeningRegion.CentredIn"/> scaled to each room, exactly as the
/// accepted lab design does: <c>MeasurementGrid</c>'s ±1 levels are the room's corners, not a listening area, so they
/// are deliberately not the coordinate rule here (see the <see cref="ListeningRegion"/> doc comment).
/// </para>
/// </summary>
public static class RandomScenarioGenerator
{
    /// <summary>The lab's one master seed. Every scenario seed is derived from it and the index.</summary>
    public const int MasterSeed = 20260919;

    /// <summary>Largest index the seed derivation accepts: seeds stay inside a positive int and Replay has a bound.</summary>
    public const int MaxScenarioIndex = 999_999;

    // Room bounds (m).
    public const double MinRoomLengthMetres = 3.0;
    public const double MaxRoomLengthMetres = 7.0;
    public const double MinRoomWidthMetres = 3.0;
    public const double MaxRoomWidthMetres = 8.0;
    public const double MinRoomHeightMetres = 2.4;
    public const double MaxRoomHeightMetres = 3.2;

    /// <summary>Minimum distance from every wall, including the floor: a sub never sits in the surface.</summary>
    public const double SubWallClearanceMetres = 0.25;

    /// <summary>Highest a generated sub stands above the floor — floor-standing, not on the ceiling.</summary>
    public const double MaxSubHeightMetres = 0.6;

    /// <summary>How close "near a wall" means: within this much of the clearance band's outer edge.</summary>
    public const double NearWallDepthMetres = 0.30;

    /// <summary>
    /// Minimum distance between the two subs. 2.0 m, not 0.5: a pair closer than a wavelength is nearly one source,
    /// so changing B's drive scales the total almost uniformly and the score (which is level-invariant) cannot move —
    /// the scenario would carry no optimisation question at all. Two metres keeps the pair spatially distinct at the
    /// sweep's lower bands without ever excluding the smallest permitted room (3.0 × 3.0 m still has a 4.2 m diagonal).
    /// </summary>
    public const double MinimumSubSeparationMetres = 2.0;

    public const double MinSubGainDb = -6.0;
    public const double MaxSubGainDb = 6.0;
    public const double MaxSubDelayMilliseconds = 30.0;

    /// <summary>The optimizer's search box, restated here so the report can check a recommendation against it.</summary>
    public const double OptimizerDelayMaxMilliseconds = 10.0;

    /// <summary>
    /// The boost limit every generated scenario runs under. It is S4's 30 dB, for S4's own reason: the boost is
    /// measured relative to the TOTAL as captured, so wherever A and B interfere into a deep null, realigning them is
    /// a tens-of-dB increase there, and the product's ordinary 3 dB would reject every candidate and answer "no
    /// change" — a regression lab that never lets the optimizer act would measure nothing. The ordinary 3 dB limit
    /// stays covered by S5.
    /// </summary>
    public const double OptimizerMaxBoostLimitDb = 30.0;

    /// <summary>The measured point count: 3 × 3 × 3 from the listening region's own fractions.</summary>
    public const int MeasurementPoints = 27;

    /// <summary>image-source order for generated rooms; low enough that 500 scenarios stay a sane wall-clock run.</summary>
    public const int ImageSourceOrder = 3;

    /// <summary>The scenario seed for an index. See the class remarks for why this is an additive bijection.</summary>
    public static int SeedForIndex(int index)
    {
        if (index < 0 || index > MaxScenarioIndex)
            throw new ArgumentOutOfRangeException(nameof(index), index, $"Scenario index must be in [0, {MaxScenarioIndex}].");

        return unchecked(MasterSeed + index);
    }

    /// <summary>The index a seed was derived from. Throws when the seed is not one this generator can have produced.</summary>
    public static int IndexForSeed(int seed)
    {
        uint offset = unchecked((uint)seed - (uint)MasterSeed);
        if (offset > MaxScenarioIndex)
            throw new ArgumentOutOfRangeException(nameof(seed), seed,
                $"Seed {seed} is not a RandomScenarioGenerator seed: it is not in [{SeedForIndex(0)}, {SeedForIndex(MaxScenarioIndex)}].");

        return (int)offset;
    }

    /// <summary>Builds scenario <paramref name="index"/>. Same index → same object, every run, every machine.</summary>
    public static RegressionScenario Create(int index) => Build(index, SeedForIndex(index));

    /// <summary>Rebuilds the scenario a seed came from, without the original index being stored anywhere.</summary>
    public static RegressionScenario Replay(int scenarioSeed) => Build(IndexForSeed(scenarioSeed), scenarioSeed);

    /// <summary>
    /// The plan a mode runs: the first <c>mode</c> indices, plus every checked-in fixture. Deduplicated by seed and
    /// ordered by index, so the aggregate is deterministic no matter how the scenarios are scheduled.
    /// </summary>
    public static IReadOnlyList<RegressionScenario> Plan(RegressionMode mode)
    {
        var bySeed = new Dictionary<int, RegressionScenario>();
        foreach (int seed in RegressionFixtures.Seeds)
            bySeed[seed] = Replay(seed);

        for (int index = 0; index < (int)mode; index++)
        {
            RegressionScenario generated = Create(index);
            bySeed[generated.Seed] = generated;
        }

        return [.. bySeed.Values.OrderBy(scenario => scenario.Index)];
    }

    /// <summary>The imperfection class of an index: 70 % light, 20 % moderate, 10 % stress, exactly.</summary>
    public static RegressionImperfection ClassForIndex(int index) => (index % 10) switch
    {
        <= 6 => RegressionImperfection.Light,
        7 or 8 => RegressionImperfection.Moderate,
        _ => RegressionImperfection.Stress,
    };

    /// <summary>Upper deviation bound a class may draw, dB. Zero for a perfect microphone, whatever the class.</summary>
    public static double MicrophoneDeviationBound(RegressionImperfection imperfection) => imperfection switch
    {
        RegressionImperfection.Light => 0.5,
        RegressionImperfection.Moderate => 1.0,
        _ => 2.0,
    };

    /// <summary>Clock-drift bound a class may draw, ppm: ±5 light, ±20 moderate, ±50 stress.</summary>
    public static double ClockPpmBound(RegressionImperfection imperfection) => imperfection switch
    {
        RegressionImperfection.Light => 5.0,
        RegressionImperfection.Moderate => 20.0,
        _ => 50.0,
    };

    /// <summary>
    /// The target curve as an absolute dB list: <paramref name="anchorDb"/> is added to a zero-mean shape, so the
    /// error against it measures the response's SHAPE (tilt), not its overall level, and a level change between
    /// before and after still shows up as a signed deviation. Flat = 0 dB; low-frequency house = +3 dB at 40 Hz
    /// falling to 0 dB at 120 Hz; mild downward = −2 dB across the sweep's band.
    /// </summary>
    public static double[] TargetShape(RegressionTargetCurve curve, IReadOnlyList<double> frequenciesHz, double anchorDb)
    {
        ArgumentNullException.ThrowIfNull(frequenciesHz);
        var values = new double[frequenciesHz.Count];
        for (int k = 0; k < values.Length; k++)
        {
            double f = frequenciesHz[k];
            double shape = curve switch
            {
                RegressionTargetCurve.Flat => 0.0,
                RegressionTargetCurve.LowFrequencyHouse => f <= 40.0 ? 3.0 : f >= 120.0 ? 0.0 : 3.0 * (120.0 - f) / 80.0,
                _ => -2.0 * (f - 20.0) / 130.0,
            };
            values[k] = shape + anchorDb;
        }

        return values;
    }

    /// <summary>The drivable listening region for a room: scaled fractions of its extents, so it is never the corners.</summary>
    public static ListeningRegion RegionFor(RoomModel room, double scale)
    {
        ArgumentNullException.ThrowIfNull(room);
        return ListeningRegion.CentredIn(room, 0.65 * room.LengthMetres * scale, 0.55 * room.WidthMetres * scale,
            0.24 * room.HeightMetres, 0.46 * room.HeightMetres);
    }

    private static RegressionScenario Build(int index, int seed)
    {
        var rng = new Lcg(seed);

        // 1. Room.
        var room = new RoomModel(
            rng.Next(MinRoomLengthMetres, MaxRoomLengthMetres),
            rng.Next(MinRoomWidthMetres, MaxRoomWidthMetres),
            rng.Next(MinRoomHeightMetres, MaxRoomHeightMetres));

        // 2. Layout and positions.
        var layout = (RegressionLayout)rng.NextInt(5);
        (Position positionA, Position positionB) = Layout(rng, room, layout);

        // 3. Physical sub settings. Two families, chosen deterministically from the index (index % 3 == 0 →
        // independent, 2/3 misaligned), so every run covers both:
        //   • INDEPENDENT: A and B are both uniform over the brief's full ranges. The pair is often already
        //     near-optimal or deeply cancelling, where the optimizer correctly answers "no change"; the lab
        //     measures that answer rather than forcing an improvement.
        //   • MISALIGNED (S4-style): B is A plus a bounded error (gain ±1.5–4.5 dB, phase ±40–160°, delay
        //     0.5–8 ms, polarity optionally flipped), so a reachable correction exists and the improvement path
        //     is exercised.
        // Every drawn value lies inside the brief's clamps: gain ±6 dB, polarity ±1, phase ±180°, delay 0–30 ms.
        RegressionAlignmentFamily family = index % 3 == 0 ? RegressionAlignmentFamily.Independent : RegressionAlignmentFamily.Misaligned;
        VirtualSubwoofer subA, subB;
        if (family == RegressionAlignmentFamily.Independent)
        {
            subA = new VirtualSubwoofer(positionA,
                rng.Next(MinSubGainDb, MaxSubGainDb), rng.NextPolarity(), rng.Next(-180.0, 180.0),
                rng.Next(0.0, MaxSubDelayMilliseconds) / 1000.0);
            subB = new VirtualSubwoofer(positionB,
                rng.Next(MinSubGainDb, MaxSubGainDb), rng.NextPolarity(), rng.Next(-180.0, 180.0),
                rng.Next(0.0, MaxSubDelayMilliseconds) / 1000.0);
        }
        else
        {
            double misalignmentGainDb = rng.Next(1.5, 4.5) * rng.NextPolarity();
            double gainA = rng.Next(MinSubGainDb - Math.Min(0, misalignmentGainDb), MaxSubGainDb - Math.Max(0, misalignmentGainDb));
            double misalignmentPhaseDegrees = rng.Next(40.0, 160.0) * rng.NextPolarity();
            double phaseA = rng.Next(-180.0, 180.0);
            double correctionDelaySeconds = rng.Next(0.5, 8.0) / 1000.0;
            double delayA = rng.Next(correctionDelaySeconds * 1000.0, MaxSubDelayMilliseconds) / 1000.0;
            int polarityA = rng.NextPolarity();

            subA = new VirtualSubwoofer(positionA, gainA, polarityA, phaseA, delayA);
            subB = new VirtualSubwoofer(positionB,
                gainA + misalignmentGainDb,
                rng.NextInt(2) == 0 ? polarityA : -polarityA,
                Math.IEEERemainder(phaseA + misalignmentPhaseDegrees, 360.0),
                delayA - correctionDelaySeconds);
        }

        // The advisory reference (drive-matching) exists only when it is a B-only setting; see GroundTruthFor.
        SubwooferSetting? groundTruth = GroundTruthFor(subA, subB);

        // 4. Imperfection: class from the index, values from the stream.
        RegressionImperfection imperfectionClass = ClassForIndex(index);
        double noiseFloorDb = imperfectionClass switch
        {
            RegressionImperfection.Light => -120.0,
            RegressionImperfection.Moderate => rng.NextInt(2) == 0 ? -100.0 : -80.0,
            _ => -60.0,
        };
        double clockPpm = rng.Next(-ClockPpmBound(imperfectionClass), ClockPpmBound(imperfectionClass));
        var microphoneProfile = (MicrophoneResponseProfile)rng.NextInt(3);
        double microphoneDeviationDb = microphoneProfile == MicrophoneResponseProfile.Perfect
            ? 0.0
            : rng.Next(-MicrophoneDeviationBound(imperfectionClass), MicrophoneDeviationBound(imperfectionClass));
        var imperfection = new RegressionImperfectionProfile(
            imperfectionClass, clockPpm, noiseFloorDb, microphoneProfile, microphoneDeviationDb);

        // 5. Target curve, drawn independently of the physical state and the ground truth.
        var targetCurve = (RegressionTargetCurve)rng.NextInt(3);

        // 6. Listening-region scale, then the config.
        double regionScale = rng.Next(0.85, 1.0);
        ListeningRegion region = RegionFor(room, regionScale);

        SimulationConfig config = SimulationConfig.Default with
        {
            Room = room,
            ImageSourceOrder = ImageSourceOrder,
            ClockPpm = imperfection.ClockPpm,
            MicrophoneNoiseFloorDb = imperfection.NoiseFloorDb,
            MicrophoneProfile = imperfection.MicrophoneProfile,
            MicrophoneDeviationDb = imperfection.MicrophoneDeviationDb,
            NoiseSeed = seed,
            ListeningRegion = region,
        };

        var scenario = new SimulationScenario(
            $"reg-{index:D4}",
            $"R{index:D4} random dual-sub regression",
            $"Generated scenario {index} ({imperfection.Describe()}; target {targetCurve}): two subs driven through the "
            + "shipped measurement chain, then the shipped optimizer, with no ground truth on the input path.",
            config,
            [subA, subB],
            region.Points,
            RunOptimizer: true,
            Optimize: new OptimizerOptions
            {
                IncludeDelay = true,
                DelayMaxMilliseconds = OptimizerDelayMaxMilliseconds,
                MaxBoostLimitDb = OptimizerMaxBoostLimitDb,
            },
            GroundTruthSetting: groundTruth);

        return new RegressionScenario(index, seed, layout, family, targetCurve, scenario) { Imperfection = imperfection };
    }

    /// <summary>
    /// The advisory known-good correction: B's drive matched to A's, normalised into the optimizer's own canonical
    /// box (phase in [0, 180]° with polarity absorbing the half turn). Returns null when B is later than A, because
    /// the correction would need a negative delay no B-only setting can express; the misaligned family guarantees
    /// B ≤ A, so the reference is always present there, and roughly half of the independent family carries none.
    /// </summary>
    private static SubwooferSetting? GroundTruthFor(VirtualSubwoofer a, VirtualSubwoofer b)
    {
        double delaySeconds = a.DelaySeconds - b.DelaySeconds;
        if (delaySeconds < 0.0) return null;

        double gainDb = a.GainDb - b.GainDb;
        int polarity = a.Polarity * b.Polarity;
        double phaseDegrees = Math.IEEERemainder(a.PhaseDegrees - b.PhaseDegrees, 360.0);
        if (phaseDegrees < 0.0)
        {
            phaseDegrees += 180.0;      // e^{j(φ−180)} ≡ −e^{jφ}: the half turn moves into the polarity
            polarity = -polarity;
        }

        return new SubwooferSetting(gainDb, phaseDegrees * Math.PI / 180.0, polarity, delaySeconds);
    }

    private static (Position A, Position B) Layout(Lcg rng, RoomModel room, RegressionLayout layout)
    {
        const double clearance = SubWallClearanceMetres;
        const int attempts = 64;

        for (int attempt = 0; attempt < attempts; attempt++)
        {
            (Position a, Position b) = layout switch
            {
                RegressionLayout.Symmetric => Symmetric(rng, room),
                RegressionLayout.Asymmetric => (Interior(rng, room), Interior(rng, room)),
                RegressionLayout.NearWall => (Interior(rng, room), NearWall(rng, room, rng.NextInt(4))),
                RegressionLayout.SameWall => SameWall(rng, room),
                _ => DifferentWall(rng, room),
            };

            if (a.DistanceTo(b) >= MinimumSubSeparationMetres) return (a, b);
        }

        // Deterministic fallback: the allowed box's diagonal is ≥ 1.25 m in the smallest permitted room, so the
        // opposite corners always clear the separation floor. Reached with negligible probability by the rejection
        // loop above, and never for Symmetric, which is separated by construction.
        return (new Position(clearance, clearance, clearance),
                new Position(room.LengthMetres - clearance, room.WidthMetres - clearance, clearance));
    }

    private static (Position A, Position B) Symmetric(Lcg rng, RoomModel room)
    {
        const double clearance = SubWallClearanceMetres;
        var a = new Position(
            rng.Next(clearance, room.LengthMetres / 2.0 - clearance),
            rng.Next(clearance, room.WidthMetres / 2.0 - clearance),
            rng.Next(clearance, MaxSubHeightMetres));
        var b = new Position(room.LengthMetres - a.X, room.WidthMetres - a.Y, a.Z);
        return (a, b);
    }

    private static (Position A, Position B) SameWall(Lcg rng, RoomModel room)
    {
        int wall = rng.NextInt(4);
        return (NearWall(rng, room, wall), NearWall(rng, room, wall));
    }

    private static (Position A, Position B) DifferentWall(Lcg rng, RoomModel room)
    {
        int wallA = rng.NextInt(4);
        int wallB = (wallA + 1 + rng.NextInt(3)) % 4;
        return (NearWall(rng, room, wallA), NearWall(rng, room, wallB));
    }

    private static Position Interior(Lcg rng, RoomModel room)
        => new(rng.Next(SubWallClearanceMetres, room.LengthMetres - SubWallClearanceMetres),
               rng.Next(SubWallClearanceMetres, room.WidthMetres - SubWallClearanceMetres),
               rng.Next(SubWallClearanceMetres, MaxSubHeightMetres));

    /// <summary>One coordinate pinned within the clearance band plus <see cref="NearWallDepthMetres"/> of one wall.</summary>
    private static Position NearWall(Lcg rng, RoomModel room, int wall)
    {
        double insetAlong = rng.Next(SubWallClearanceMetres, SubWallClearanceMetres + NearWallDepthMetres);
        double alongX = rng.Next(SubWallClearanceMetres, room.LengthMetres - SubWallClearanceMetres);
        double alongY = rng.Next(SubWallClearanceMetres, room.WidthMetres - SubWallClearanceMetres);
        double z = rng.Next(SubWallClearanceMetres, MaxSubHeightMetres);
        return wall switch
        {
            0 => new Position(insetAlong, alongY, z),
            1 => new Position(room.LengthMetres - insetAlong, alongY, z),
            2 => new Position(alongX, insetAlong, z),
            _ => new Position(alongX, room.WidthMetres - insetAlong, z),
        };
    }

    /// <summary>
    /// The deterministic replacement for <c>System.Random</c>: a 32-bit LCG whose stream is fixed by the constants
    /// below, with a splitmix32 finalizer on the seed. The finalizer is not decoration: seeds are MasterSeed + index,
    /// so consecutive scenarios differ by one, and an unmixed LCG would give their first draws nearly identical
    /// values. Uniform draws take the top 24 bits, exactly like <c>VirtualRoom.AddNoise</c>, so the sequence is
    /// reproducible down to the bit on any framework.
    /// </summary>
    private sealed class Lcg(int seed)
    {
        private const uint Multiplier = 1664525u;
        private const uint Increment = 1013904223u;
        private uint _state = Mix(unchecked((uint)seed));

        /// <summary>Uniform in [min, max).</summary>
        public double Next(double min, double max) => min + ((max - min) * Next01());

        /// <summary>Uniform in [0, 1).</summary>
        public double Next01()
        {
            Step();
            return (_state >> 8) * (1.0 / (1 << 24));
        }

        /// <summary>Uniform integer in [0, exclusiveMax).</summary>
        public int NextInt(int exclusiveMax)
        {
            Step();
            return (int)((_state >> 8) % (uint)exclusiveMax);
        }

        public int NextPolarity() => NextInt(2) == 0 ? 1 : -1;

        private void Step() => _state = (_state * Multiplier) + Increment;

        private static uint Mix(uint x)
        {
            x ^= x >> 16;
            x *= 0x7feb352du;
            x ^= x >> 15;
            x *= 0x846ca68bu;
            x ^= x >> 16;
            return x;
        }
    }
}
