namespace AudioOptimizer.Simulation;

/// <summary>
/// Shoebox image-source method (Allen–Berkley): every reflection is an unreflected path from a mirrored copy of the
/// source, so the room's response is a sum of delays and amplitudes rather than a filter curve.
/// <para>
/// For a source (s) and a receiver (r) in [0, L]³ the images are indexed by (i, j, k) ∈ [−N, N]³ with, per axis,
/// </para>
/// <code>
/// coord(n) = (n even) ? n·size + s : (n + 1)·size − s      // the mirrored copies, sign-flipped by construction
/// </code>
/// <para>
/// which places copy 0 at the source itself, copy +1 mirrored through the far wall (2L − s) and copy −1 mirrored
/// through the near wall (−s). The number of wall hits that produced image n is |n|: a non-negative n crosses the
/// wall at <c>size</c> first (so it hits that one ⌈n/2⌉ times) and a negative n crosses the wall at 0 first. Each hit
/// multiplies in that surface's <see cref="SurfaceReflections"/> coefficient — once per path, never as a global EQ.
/// </para>
/// <para>
/// <b>Level calibration:</b> every amplitude is scaled by <c>referenceDistance / pathDistance</c> with a default
/// reference distance of 1 m, so the direct sound of a source at 1 m has amplitude 1.0 and the absolute level does
/// not drift with the room size or the ISM order. The 1/r law and that calibration are the whole amplitude model;
/// air absorption and surface impedance are not modelled.
/// </para>
/// </summary>
public static class ImageSourceMethod
{
    /// <summary>
    /// Every path from <paramref name="source"/> to <paramref name="microphone"/> up to <paramref name="order"/>
    /// reflections per axis. Order 0 is the direct sound alone. Emitted in a fixed (i, j, k) order so the result is
    /// reproducible; the caller sums, so the order only fixes floating-point rounding, not the answer.
    /// </summary>
    public static IReadOnlyList<Arrival> Arrivals(
        RoomModel room,
        Position source,
        Position microphone,
        int order,
        double referenceDistanceMetres = 1.0)
    {
        ArgumentNullException.ThrowIfNull(room);
        room.Validate();
        if (order < 0) throw new ArgumentOutOfRangeException(nameof(order), order, "ISM order must be >= 0.");
        if (!(referenceDistanceMetres > 0))
            throw new ArgumentOutOfRangeException(nameof(referenceDistanceMetres), referenceDistanceMetres, "The reference distance must be > 0 m.");

        SurfaceReflections reflections = room.Reflections;
        var arrivals = new List<Arrival>((2 * order + 1) * (2 * order + 1) * (2 * order + 1));

        for (int i = -order; i <= order; i++)
        {
            double ix = Coordinate(i, room.LengthMetres, source.X);
            (int xLow, int xHigh) = WallHits(i);
            double rx = Math.Pow(reflections.Left, xLow) * Math.Pow(reflections.Right, xHigh);

            for (int j = -order; j <= order; j++)
            {
                double iy = Coordinate(j, room.WidthMetres, source.Y);
                (int yLow, int yHigh) = WallHits(j);
                double ry = rx * Math.Pow(reflections.Front, yLow) * Math.Pow(reflections.Rear, yHigh);

                for (int k = -order; k <= order; k++)
                {
                    double iz = Coordinate(k, room.HeightMetres, source.Z);
                    (int zLow, int zHigh) = WallHits(k);
                    double reflection = ry * Math.Pow(reflections.Floor, zLow) * Math.Pow(reflections.Ceiling, zHigh);

                    double distance = Math.Sqrt(
                        Square(ix - microphone.X) + Square(iy - microphone.Y) + Square(iz - microphone.Z));

                    // A receiver exactly on a mirror of the source is a coincidence, not a physical coincidence of
                    // zero length: 1/r would be infinite. Skipped rather than fudged.
                    if (distance < 1e-12) continue;

                    arrivals.Add(new Arrival(distance / room.SpeedOfSound, reflection * referenceDistanceMetres / distance));
                }
            }
        }

        return arrivals;
    }

    /// <summary>Coordinates of image source <paramref name="index"/> along one axis (see the class remarks).</summary>
    public static double Coordinate(int index, double size, double coordinate)
        => index % 2 == 0 ? (index * size) + coordinate : ((index + 1) * size) - coordinate;

    /// <summary>How many times image <paramref name="index"/> crossed the wall at 0 (low) and at <paramref name="size"/> (high).</summary>
    public static (int Low, int High) WallHits(int index)
    {
        int crossings = Math.Abs(index);
        int half = crossings / 2;
        // index >= 0 crosses the far wall on its first hop (crossing 1, 3, 5 … are the high wall); index < 0 crosses
        // the near wall first, so the roles of the two counts swap.
        return index >= 0 ? (half, crossings - half) : (crossings - half, half);
    }

    private static double Square(double value) => value * value;
}
