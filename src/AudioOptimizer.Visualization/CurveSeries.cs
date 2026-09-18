namespace AudioOptimizer.Visualization;

/// <summary>
/// One sample of a curve in data units: frequency in Hz on X, a level or spread in dB on Y. Deliberately not
/// pixels — the chart maps these through the axis scales, so a test can derive the expected pixel independently.
/// </summary>
public sealed record CurvePoint(double X, double Y);

/// <summary>
/// What a series is, so a figure can switch between views (mean±σ vs every position) and a test can count the
/// series that carry measurements without parsing labels. A tag, not a type hierarchy.
/// </summary>
public enum CurveRole
{
    /// <summary>One measurement position's own curve.</summary>
    Position,

    /// <summary>The mean curve of the positions shown.</summary>
    Mean,

    /// <summary>The ±1σ ribbon: a closed ring whose fill is the band between mean+σ and mean−σ.</summary>
    Band,

    /// <summary>Standard deviation across positions, against frequency.</summary>
    StdDev,

    /// <summary>Max − min across positions, against frequency.</summary>
    Range,

    /// <summary>The baseline configuration in a before/after overlay.</summary>
    Before,

    /// <summary>The configuration being compared with it.</summary>
    After,
}

/// <summary>
/// One named curve of a <see cref="CurvePlot"/>: the samples, the colour to draw them in, and whether the samples
/// form a closed ring to fill rather than a polyline to stroke. Validated on construction, so a plot can never
/// contain a curve with no points or a non-finite sample.
/// </summary>
public sealed record CurveSeries
{
    public CurveSeries(string label, CurveRole role, IReadOnlyList<CurvePoint> points, RgbColour colour, bool filled = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count < 2)
            throw new ArgumentException($"A curve needs at least two points to draw; got {points.Count}.", nameof(points));
        foreach (CurvePoint point in points)
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
                throw new ArgumentException($"'{label}' contains a non-finite sample ({point.X}, {point.Y}).", nameof(points));

        Label = label;
        Role = role;
        Points = points;
        Colour = colour;
        Filled = filled;
    }

    public string Label { get; }

    public CurveRole Role { get; }

    public IReadOnlyList<CurvePoint> Points { get; }

    public RgbColour Colour { get; }

    /// <summary>True for the σ ribbon: the samples form a closed ring and are filled, not stroked.</summary>
    public bool Filled { get; }

    public double MinX => Points.Min(point => point.X);

    public double MaxX => Points.Max(point => point.X);

    public double MinY => Points.Min(point => point.Y);

    public double MaxY => Points.Max(point => point.Y);
}
