namespace AudioOptimizer.Core;

/// <summary>
/// One measurement position, in grid terms and in physical terms. The indices are signed levels (−1, 0, +1)
/// rather than slot numbers, because the coordinate rule is derived from them: level −1 is 0 m, 0 is the middle
/// of the extent and +1 is its full width, so the levels are the grid and the metres are the answer.
/// </summary>
/// <param name="Id">Stable, filename-safe label built from the levels, e.g. "x-1_y0_z1".</param>
public sealed record MeasurementPoint(
    string Id,
    int GridX,
    int GridY,
    int GridZ,
    double X,
    double Y,
    double Z);
