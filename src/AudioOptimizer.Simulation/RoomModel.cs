namespace AudioOptimizer.Simulation;

/// <summary>A point in the room, in metres. Axis convention, stated once and used everywhere below:
/// <b>x = Length</b>, <b>y = Width</b>, <b>z = Height</b>, with the room occupying [0, Length] × [0, Width] × [0, Height].
/// (<c>MeasurementGrid</c> calls its x extent "width" and its y extent "depth"; <see cref="ListeningRegion"/> maps
/// onto it explicitly rather than relying on the two agreeing.)</summary>
public readonly record struct Position(double X, double Y, double Z)
{
    public double DistanceTo(Position other)
    {
        double dx = X - other.X, dy = Y - other.Y, dz = Z - other.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    public override string ToString() => $"({X:0.###}, {Y:0.###}, {Z:0.###}) m";
}

/// <summary>
/// The six surfaces of the box, each with one frequency-independent pressure reflection coefficient in [0, 1].
/// <para>
/// <b>Sign convention:</b> a positive coefficient is a rigid (pressure-antinode) boundary — the classic shoebox
/// assumption, and the one that makes the axial resonances land exactly at c·n/2L. A pressure-release boundary would
/// flip the sign on every reflection; that is NOT modelled here (see the limitations list).
/// </para>
/// <para>
/// A reflection applies per propagation path — it multiplies that image source's amplitude — and is never applied as
/// a global EQ curve.
/// </para>
/// </summary>
public sealed record SurfaceReflections(
    double Left,
    double Right,
    double Front,
    double Rear,
    double Floor,
    double Ceiling)
{
    /// <summary>Walls 0.7, floor and ceiling 0.5: a furnished room's low-frequency behaviour without pretending to
    /// model furniture. Every value is a caller-visible parameter.</summary>
    public static readonly SurfaceReflections Default = new(0.7, 0.7, 0.7, 0.7, 0.5, 0.5);

    public void Validate()
    {
        foreach ((string name, double value) in new[]
                 {
                     (nameof(Left), Left), (nameof(Right), Right), (nameof(Front), Front),
                     (nameof(Rear), Rear), (nameof(Floor), Floor), (nameof(Ceiling), Ceiling),
                 })
            if (!(value >= 0.0 && value <= 1.0))
                throw new ArgumentOutOfRangeException(name, value, $"{name} must be a reflection coefficient in [0, 1].");
    }
}

/// <summary>
/// A rectangular (shoebox) room. Its modal behaviour is not written down anywhere in this project: the axial and
/// tangential resonances at f = c/2·√((nx/Lx)² + (ny/Ly)² + (nz/Lz)²) EMERGE from the image-source sum below, which
/// is the only reason the images are generated at all.
/// </summary>
public sealed record RoomModel(
    double LengthMetres = 3.3,
    double WidthMetres = 3.6,
    double HeightMetres = 2.6,
    double SpeedOfSound = 343.0)
{
    /// <summary>The Phase 1 room: 3.3 × 3.6 × 2.6 m at 343 m/s.</summary>
    public static readonly RoomModel Default = new();

    public SurfaceReflections Reflections { get; init; } = SurfaceReflections.Default;

    public void Validate()
    {
        if (!(LengthMetres > 0)) throw new ArgumentOutOfRangeException(nameof(LengthMetres), LengthMetres, "Length must be > 0 m.");
        if (!(WidthMetres > 0)) throw new ArgumentOutOfRangeException(nameof(WidthMetres), WidthMetres, "Width must be > 0 m.");
        if (!(HeightMetres > 0)) throw new ArgumentOutOfRangeException(nameof(HeightMetres), HeightMetres, "Height must be > 0 m.");
        if (!(SpeedOfSound > 0)) throw new ArgumentOutOfRangeException(nameof(SpeedOfSound), SpeedOfSound, "Speed of sound must be > 0 m/s.");
        Reflections.Validate();
    }
}

/// <summary>One propagation path: how long it takes and what it arrives with, before any subwoofer setting is applied.
/// Amplitude is a pressure ratio at the reference distance (see <see cref="ImageSourceMethod"/>).</summary>
public readonly record struct Arrival(double DelaySeconds, double Amplitude);
