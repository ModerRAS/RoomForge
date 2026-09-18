namespace AudioOptimizer.Visualization;

/// <summary>
/// The rectangle inside a figure that actually carries the data, in the figure's own pixel coordinates
/// (top-left origin). Every figure in this project derives its axes from one of these, so the axes and the
/// series can never be laid out with two different ideas of where the plot area is.
/// </summary>
public sealed record PlotArea
{
    public PlotArea(double left, double top, double right, double bottom)
    {
        if (!double.IsFinite(left) || !double.IsFinite(top) || !double.IsFinite(right) || !double.IsFinite(bottom))
            throw new ArgumentOutOfRangeException(nameof(left), "Plot bounds must be finite.");
        if (right <= left || bottom <= top)
            throw new ArgumentOutOfRangeException(nameof(right), $"The plot area must have positive extent; got ({left}, {top})–({right}, {bottom}).");

        Left = left;
        Top = top;
        Right = right;
        Bottom = bottom;
    }

    public double Left { get; }

    public double Top { get; }

    public double Right { get; }

    public double Bottom { get; }

    public double Width => Right - Left;

    public double Height => Bottom - Top;

    /// <summary>Data left → right.</summary>
    public AxisScale Horizontal(double dataMin, double dataMax) => new(dataMin, dataMax, Left, Right);

    /// <summary>Data up → down the screen: the pixel ends are swapped so larger data is higher on the figure.</summary>
    public AxisScale Vertical(double dataMin, double dataMax) => new(dataMin, dataMax, Bottom, Top);

    public bool Contains(double x, double y) => x >= Left && x <= Right && y >= Top && y <= Bottom;

    /// <summary>Inset by <paramref name="margin"/> pixels on every side — used to place axis labels outside the data.</summary>
    public PlotArea Deflate(double margin) => new(Left + margin, Top + margin, Right - margin, Bottom - margin);
}
