namespace AudioOptimizer.Visualization;

/// <summary>
/// A figure's data and its wording, with no pixels in it: the data ranges plus the axis labels that must come from
/// a <see cref="LevelReference"/> rather than from a literal at the call site. The chart maps these through a
/// <see cref="PlotArea"/>, which is what lets a test derive the expected pixel independently of the production
/// transform.
/// </summary>
public sealed record CurvePlot
{
    /// <summary>
    /// How far a flat series is spread out so it can still be drawn: a plot whose data has zero extent has no legal
    /// axis scale, and "everything is the same value" is a normal result here (one measurement position has a
    /// standard deviation of exactly 0 dB at every frequency). Padding beats throwing.
    /// </summary>
    public const double DegeneratePad = 1.0;

    public CurvePlot(
        string title,
        string xAxisLabel,
        string yAxisLabel,
        IReadOnlyList<CurveSeries> series,
        IReadOnlyList<string>? legend = null,
        bool interpolated = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(xAxisLabel);
        ArgumentException.ThrowIfNullOrWhiteSpace(yAxisLabel);
        ArgumentNullException.ThrowIfNull(series);
        if (series.Count == 0) throw new ArgumentException("A plot needs at least one series.", nameof(series));

        Title = title;
        XAxisLabel = xAxisLabel;
        YAxisLabel = yAxisLabel;
        Series = series;
        Legend = legend ?? [];
        Interpolated = interpolated;

        XMin = series.Min(s => s.MinX);
        XMax = series.Max(s => s.MaxX);
        YMin = series.Min(s => s.MinY);
        YMax = series.Max(s => s.MaxY);
        if (XMax - XMin <= 0) { XMin -= DegeneratePad; XMax += DegeneratePad; }
        if (YMax - YMin <= 0) { YMin -= DegeneratePad; YMax += DegeneratePad; }
    }

    public string Title { get; }

    public string XAxisLabel { get; }

    /// <summary>For a level plot this is <see cref="LevelReference.AxisLabel"/>, so the figure states what its dB are relative to.</summary>
    public string YAxisLabel { get; }

    public IReadOnlyList<CurveSeries> Series { get; }

    public IReadOnlyList<string> Legend { get; }

    /// <summary>True when the samples are denser than the measurement bins (see <see cref="ResponseCurves.Interpolated"/>).</summary>
    public bool Interpolated { get; }

    public double XMin { get; }

    public double XMax { get; }

    public double YMin { get; }

    public double YMax { get; }

    public IReadOnlyList<CurveSeries> Of(CurveRole role) => [.. Series.Where(series => series.Role == role)];

    public bool HasPositionSeries => Series.Any(series => series.Role == CurveRole.Position);
}
