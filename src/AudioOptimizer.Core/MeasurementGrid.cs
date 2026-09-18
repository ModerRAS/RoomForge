namespace AudioOptimizer.Core;

/// <summary>
/// The box a session measures over, as a lattice of nx·ny·nz points spread across the physical extents.
/// Nothing hardcodes 27: the count is nx·ny·nz, so 3×3×3 is 27 because 3·3·3 is 27, and 3×3×2 is 18 (the z
/// axis then only visits the two ends). Coordinates come from the extents, never from a constant.
/// ponytail: signed ±1 levels are the ceiling here — they express 1, 2 or 3 positions per axis and no more
/// (a 5-point axis needs a fractional level). Add a `double Level` and lerp when a denser grid is wanted.
/// </summary>
public sealed record MeasurementGrid(
    double WidthMetres,
    double DepthMetres,
    double HeightMetres,
    int CountX = 3,
    int CountY = 3,
    int CountZ = 3)
{
    public IReadOnlyList<MeasurementPoint> Points { get; } = Build(WidthMetres, DepthMetres, HeightMetres, CountX, CountY, CountZ);

    public int PointCount => Points.Count;

    public static MeasurementGrid Create(double widthMetres, double depthMetres, double heightMetres, int nx = 3, int ny = 3, int nz = 3)
        => new(widthMetres, depthMetres, heightMetres, nx, ny, nz);

    /// <summary>Signed levels for one axis: 1 point is the centre, 2 are the two ends, 3 adds the centre.</summary>
    public static int[] LevelsFor(int count) => count switch
    {
        1 => [0],
        2 => [-1, 1],
        3 => [-1, 0, 1],
        _ => throw new ArgumentOutOfRangeException(nameof(count), count, "Points per axis must be 1, 2 or 3; see the ceiling note on MeasurementGrid."),
    };

    /// <summary>Level −1 → 0 m, 0 → the middle, +1 → the whole extent: x = (level + 1) / 2 · extent.</summary>
    private static double Level(int level, double extent) => (level + 1) * 0.5 * extent;

    private static MeasurementPoint[] Build(double width, double depth, double height, int nx, int ny, int nz)
    {
        if (width < 0 || depth < 0 || height < 0) throw new ArgumentOutOfRangeException(nameof(width), "Grid extents must be >= 0 m.");

        int[] xs = LevelsFor(nx);
        int[] ys = LevelsFor(ny);
        int[] zs = LevelsFor(nz);
        var points = new MeasurementPoint[xs.Length * ys.Length * zs.Length];
        int index = 0;
        foreach (int x in xs)
            foreach (int y in ys)
                foreach (int z in zs)
                    points[index++] = new MeasurementPoint($"x{x}_y{y}_z{z}", x, y, z, Level(x, width), Level(y, depth), Level(z, height));
        return points;
    }
}
