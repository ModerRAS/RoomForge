namespace AudioOptimizer.UI.Controls;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using AudioOptimizer.Visualization;

/// <summary>
/// Draws one <see cref="CurvePlot"/>. Presentation only: every coordinate comes from the plot's data ranges mapped
/// through <see cref="PlotArea"/>, and every string is supplied by the plot — including the axis label that says
/// what the dB are relative to. Sizes and the plot rectangle are pinned literals so a test can restate them and
/// derive the expected pixels with plain arithmetic.
/// </summary>
public sealed class CurveChart : UserControl
{
    public const double ChartWidth = 460.0;
    public const double ChartHeight = 280.0;

    /// <summary>(left, top, right, bottom) of the area that carries data, in this control's pixels.</summary>
    public static readonly PlotArea PlotBounds = new(40.0, 24.0, 450.0, 156.0);

    /// <summary>The exact text shown when the samples are denser than the measurement bins (§23).</summary>
    public const string InterpolatedLabel = "Interpolated visualization";

    private static readonly Brush AxisBrush = Frozen(Color.FromRgb(0x60, 0x60, 0x60));
    private static readonly Brush TextBrush = Frozen(Color.FromRgb(0x20, 0x20, 0x20));
    private static readonly Brush NoteBrush = Frozen(Color.FromRgb(0x40, 0x40, 0x40));

    public CurveChart()
    {
        Width = ChartWidth;
        Height = ChartHeight;
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
    }

    /// <summary>The plot currently drawn, or null after <see cref="Clear"/> and before the first <see cref="Show"/>.</summary>
    public CurvePlot? Plot { get; private set; }

    /// <summary>Removes the figure: nothing to draw is a normal state (no measured points yet).</summary>
    public void Clear()
    {
        Plot = null;
        Content = null;
    }

    /// <summary>
    /// Draws <paramref name="plot"/>. <paramref name="includePositionSeries"/> is the §22.1 view switch: false
    /// shows the mean with its ±1σ ribbon, true adds every measured position's own curve.
    /// </summary>
    public void Show(CurvePlot plot, bool includePositionSeries = true)
    {
        ArgumentNullException.ThrowIfNull(plot);
        Plot = plot;

        var canvas = new Canvas { Width = ChartWidth, Height = ChartHeight, Background = Brushes.White };
        AxisScale x = PlotBounds.Horizontal(plot.XMin, plot.XMax);
        AxisScale y = PlotBounds.Vertical(plot.YMin, plot.YMax);

        canvas.Children.Add(new Rectangle
        {
            Width = PlotBounds.Width,
            Height = PlotBounds.Height,
            Stroke = AxisBrush,
            StrokeThickness = 1.0,
        }.Placed(PlotBounds.Left, PlotBounds.Top));

        foreach (double tick in x.TickValues(XTickStep(plot.XMax - plot.XMin)))
            canvas.Children.Add(GridLine(x.ToPixel(tick), PlotBounds.Top, PlotBounds.Bottom, horizontal: false));
        foreach (double tick in y.TickValues(YTickStep(plot.YMax - plot.YMin)))
            canvas.Children.Add(GridLine(y.ToPixel(tick), PlotBounds.Left, PlotBounds.Right, horizontal: true));

        foreach (double tick in y.TickValues(YTickStep(plot.YMax - plot.YMin)))
            canvas.Children.Add(Label(AxisScale.FormatTick(tick), 0.0, y.ToPixel(tick) - 8.0, 36.0, TextAlignment.Right));
        foreach (double tick in x.TickValues(XTickStep(plot.XMax - plot.XMin)))
            canvas.Children.Add(Label(AxisScale.FormatTick(tick), x.ToPixel(tick) - 20.0, PlotBounds.Bottom + 2.0, 40.0, TextAlignment.Center));

        foreach (CurveSeries series in plot.Series)
        {
            if (!includePositionSeries && series.Role == CurveRole.Position) continue;
            canvas.Children.Add(Shape(series, x, y));
        }

        canvas.Children.Add(Label(plot.Title, PlotBounds.Left, 4.0, ChartWidth - PlotBounds.Left - 8.0, TextAlignment.Left, bold: true, size: 12.0));
        if (plot.Interpolated)
            canvas.Children.Add(Label(InterpolatedLabel, 250.0, 6.0, 200.0, TextAlignment.Left, size: 11.0, brush: NoteBrush));
        canvas.Children.Add(Label(plot.XAxisLabel, PlotBounds.Left, PlotBounds.Bottom + 16.0, PlotBounds.Width, TextAlignment.Left, size: 11.0, brush: NoteBrush));
        canvas.Children.Add(Label(plot.YAxisLabel, PlotBounds.Left, PlotBounds.Bottom + 30.0, PlotBounds.Width, TextAlignment.Left, size: 11.0, brush: NoteBrush));

        var notes = new StackPanel { Width = PlotBounds.Width };
        foreach (string line in plot.Legend)
            notes.Children.Add(new TextBlock { Text = line, FontSize = 10.0, Foreground = NoteBrush, TextWrapping = TextWrapping.Wrap });
        Canvas.SetLeft(notes, PlotBounds.Left);
        Canvas.SetTop(notes, PlotBounds.Bottom + 46.0);
        canvas.Children.Add(notes);

        Content = canvas;
    }

    private static Shape Shape(CurveSeries series, AxisScale x, AxisScale y)
    {
        var points = new PointCollection(series.Points.Select(point => new Point(x.ToPixel(point.X), y.ToPixel(point.Y))));
        return series.Filled
            ? new Polygon { Points = points, Fill = Frozen(series.Colour) }
            : new Polyline { Points = points, Stroke = Frozen(series.Colour), StrokeThickness = 1.5 };
    }

    private static Line GridLine(double pixel, double from, double to, bool horizontal) => new()
    {
        X1 = horizontal ? from : pixel,
        Y1 = horizontal ? pixel : from,
        X2 = horizontal ? to : pixel,
        Y2 = horizontal ? pixel : to,
        Stroke = Frozen(Color.FromRgb(0xE0, 0xE0, 0xE0)),
        StrokeThickness = 1.0,
    };

    private static TextBlock Label(
        string text,
        double left,
        double top,
        double width,
        TextAlignment alignment,
        bool bold = false,
        double size = 10.0,
        Brush? brush = null)
    {
        var block = new TextBlock
        {
            Text = text,
            Width = width,
            TextAlignment = alignment,
            FontSize = size,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = brush ?? TextBrush,
        };
        Canvas.SetLeft(block, left);
        Canvas.SetTop(block, top);
        return block;
    }

    private static double XTickStep(double span) => span switch
    {
        <= 5.0 => 1.0,
        <= 60.0 => 25.0,
        <= 400.0 => 50.0,
        _ => 100.0,
    };

    private static double YTickStep(double span) => span switch
    {
        <= 5.0 => 1.0,
        <= 12.0 => 2.0,
        <= 30.0 => 5.0,
        <= 60.0 => 10.0,
        _ => 20.0,
    };

    private static Brush Frozen(RgbColour colour)
    {
        var brush = new SolidColorBrush(Color.FromRgb(colour.R, colour.G, colour.B));
        brush.Freeze();
        return brush;
    }

    private static Brush Frozen(Color colour)
    {
        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        return brush;
    }
}
