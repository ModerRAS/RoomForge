namespace AudioOptimizer.Simulation;

using AudioOptimizer.Core;

/// <summary>
/// One virtual subwoofer: where it stands and the four knobs a real one has. Gain, polarity, phase and delay are
/// applied to the sub's DRIVE signal (they are electrical settings, in the signal path); the room's own propagation
/// delays come from the image-source paths and are never folded into <see cref="DelaySeconds"/>.
/// <para>
/// Phase and delay are separate parameters and stay separate: <see cref="PhaseDegrees"/> is one rotation for the whole
/// band (exp(jφ)) while <see cref="DelaySeconds"/> rotates by exp(−j2πfΔt) — 5 ms is 90° at 50 Hz and 180° at 100 Hz,
/// so neither can stand in for the other.
/// </para>
/// </summary>
public sealed record VirtualSubwoofer(
    Position Position,
    double GainDb = 0.0,
    int Polarity = 1,
    double PhaseDegrees = 0.0,
    double DelaySeconds = 0.0)
{
    /// <summary>Amplitude ratio for <see cref="GainDb"/>: 10^(G/20).</summary>
    public double GainLinear => AudioOptimizer.Dsp.ComplexMath.DbToLinear(GainDb);

    /// <summary>The same sub with an offset applied — the ground-truth correction expressed as a sub setting.</summary>
    public VirtualSubwoofer Aligned(double gainDb, double phaseDegrees, int polarity, double delaySeconds)
        => this with { GainDb = gainDb, Polarity = polarity, PhaseDegrees = phaseDegrees, DelaySeconds = delaySeconds };

    public void Validate()
    {
        if (!double.IsFinite(GainDb)) throw new ArgumentOutOfRangeException(nameof(GainDb), GainDb, "Gain must be finite.");
        if (Polarity is not (1 or -1)) throw new ArgumentOutOfRangeException(nameof(Polarity), Polarity, "Polarity must be +1 or -1.");
        if (!double.IsFinite(PhaseDegrees)) throw new ArgumentOutOfRangeException(nameof(PhaseDegrees), PhaseDegrees, "Phase must be finite.");
        if (!double.IsFinite(DelaySeconds) || DelaySeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(DelaySeconds), DelaySeconds, "Delay must be finite and >= 0 s.");
    }

    /// <summary>
    /// The impulse response of this sub's path at <paramref name="microphone"/>: the image-source arrivals scaled by
    /// gain and polarity and shifted by the delay setting. The phase setting is deliberately NOT in here — it is a
    /// rotation of the drive signal and cannot be carried by a broadband impulse train (see <see cref="PhaseRotation"/>);
    /// the measurement chain still reports it, because deconvolution is linear.
    /// </summary>
    public double[] ImpulseResponse(
        RoomModel room,
        Position microphone,
        int imageSourceOrder,
        int sampleRate,
        double referenceDistanceMetres = 1.0,
        ArrivalPlacement placement = ArrivalPlacement.LinearInterpolated)
    {
        ArgumentNullException.ThrowIfNull(room);
        Validate();

        IReadOnlyList<Arrival> paths = ImageSourceMethod.Arrivals(room, Position, microphone, imageSourceOrder, referenceDistanceMetres);
        double scale = GainLinear * Polarity;
        var arrivals = new Arrival[paths.Count];
        for (int i = 0; i < paths.Count; i++)
            arrivals[i] = new Arrival(paths[i].DelaySeconds + DelaySeconds, paths[i].Amplitude * scale);

        return ShoeboxRir.Render(arrivals, sampleRate, placement);
    }
}

/// <summary>
/// One virtual microphone: where it stands and the two knobs a real one has. The rig-level modelling of both effects
/// lives on <see cref="SimulationConfig"/> (<c>ClockPpm</c>, <c>MicrophoneProfile</c>, applied to every microphone);
/// this record is the per-microphone form the rig would carry if the points ever needed to differ from each other.
/// </summary>
public sealed record VirtualMicrophone(
    Position Position,
    double Gain = 1.0,
    double NoiseLevel = 1e-6,
    double ClockPpm = 0.0,
    MicrophoneCalibration? Calibration = null);

/// <summary>
/// The box the 27 microphones sit in, as a region rather than as 27 literals. X and Y are sampled at 25 %, 50 % and
/// 75 % of the region's extents and Z at its low, middle and high level, so the coordinates follow from the extents:
/// the region is placed by its centre, and nothing here hardcodes a metre value.
/// <para>
/// <c>MeasurementGrid</c> is deliberately NOT reused as the coordinate rule: its levels are −1/0/+1, which map to 0,
/// half and the full extent — the room's corners, not a listening area. What is reused is
/// <see cref="MeasurementPoint"/>, so the points this produces are the same type the hardware session's slots carry
/// and the optimizer's <c>PositionResponse.PointId</c> labels still match the grid.
/// </para>
/// </summary>
public sealed record ListeningRegion(
    double WidthMetres,
    double DepthMetres,
    double HeightMetres,
    double CentreX,
    double CentreY,
    double CentreZ)
{
    /// <summary>Levels along each axis: 25 %, 50 %, 75 % of the extent — 3 × 3 × 3 = 27 points.</summary>
    private static readonly double[] Fractions = [0.25, 0.5, 0.75];

    /// <summary>The default listening area: 2.4 × 2.0 × 0.6 m, centred in the default room, from 0.9 m to 1.5 m.</summary>
    public static readonly ListeningRegion Default = CentredIn(RoomModel.Default, 2.4, 2.0, 0.6, 1.2);

    public static ListeningRegion CentredIn(RoomModel room, double width, double depth, double height, double centreZ)
    {
        ArgumentNullException.ThrowIfNull(room);
        return new ListeningRegion(width, depth, height, room.LengthMetres / 2.0, room.WidthMetres / 2.0, centreZ);
    }

    /// <summary>The 27 positions, in a fixed x/y/z order, as the measurement chain's own point type.</summary>
    public IReadOnlyList<MeasurementPoint> Points
    {
        get
        {
            var points = new List<MeasurementPoint>(27);
            for (int x = -1; x <= 1; x++)
            {
                for (int y = -1; y <= 1; y++)
                {
                    for (int z = -1; z <= 1; z++)
                    {
                        points.Add(new MeasurementPoint(
                            $"x{x}_y{y}_z{z}",
                            x, y, z,
                            CentreX + Axis(WidthMetres, x),
                            CentreY + Axis(DepthMetres, y),
                            CentreZ + Axis(HeightMetres, z)));
                    }
                }
            }

            return points;
        }
    }

    /// <summary>Level −1 → 25 %, 0 → 50 %, +1 → 75 % of the extent, measured from the region's centre.</summary>
    private static double Axis(double extent, int level) => (Fractions[level + 1] - 0.5) * extent;
}
