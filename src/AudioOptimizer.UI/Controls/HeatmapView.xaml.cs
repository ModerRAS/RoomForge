namespace AudioOptimizer.UI.Controls;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using AudioOptimizer.Visualization;

/// <summary>
/// Draws one <see cref="Heatmap"/>: a cell per matrix entry, plus the colour bar whose caption is the scale's own
/// label, plus the §23 interpolation statement when the figure is interpolated. Presentation only — every colour,
/// level and string comes from the heatmap, which is what keeps the picture and its reference in step.
/// </summary>
public sealed class HeatmapView : UserControl
{
    public const double ViewWidth = 420.0;
    public const double ViewHeight = 300.0;

    /// <summary>(left, top, right, bottom) of the cell matrix, in this control's pixels.</summary>
    public static readonly PlotArea MapBounds = new(20.0, 24.0, 320.0, 224.0);

    /// <summary>The colour bar strip, same vertical extent as the matrix.</summary>
    public static readonly PlotArea BarBounds = new(336.0, 24.0, 360.0, 224.0);

    private const int BarSlices = 24;

    private static readonly Brush FrameBrush = Frozen(Color.FromRgb(0x60, 0x60, 0x60));
    private static readonly Brush TextBrush = Frozen(Color.FromRgb(0x20, 0x20, 0x20));
    private static readonly Brush NoteBrush = Frozen(Color.FromRgb(0x40, 0x40, 0x40));

    public HeatmapView()
    {
        Width = ViewWidth;
        Height = ViewHeight;
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
    }

    public Heatmap? Map { get; private set; }

    public void Clear()
    {
        Map = null;
        Content = null;
    }

    public void Show(Heatmap map)
    {
        ArgumentNullException.ThrowIfNull(map);
        Map = map;

        var canvas = new Canvas { Width = ViewWidth, Height = ViewHeight, Background = Brushes.White };
        double cellWidth = MapBounds.Width / map.Columns;
        double cellHeight = MapBounds.Height / map.Rows;
        foreach (HeatmapCell cell in map.Cells)
        {
            var rectangle = new Rectangle
            {
                Width = cellWidth,
                Height = cellHeight,
                Fill = Frozen(cell.Colour),
            };
            Canvas.SetLeft(rectangle, MapBounds.Left + (cell.Column * cellWidth));
            Canvas.SetTop(rectangle, MapBounds.Top + (cell.Row * cellHeight));
            canvas.Children.Add(rectangle);
        }

        canvas.Children.Add(new Rectangle
        {
            Width = MapBounds.Width,
            Height = MapBounds.Height,
            Stroke = FrameBrush,
            StrokeThickness = 1.0,
        }.Placed(MapBounds.Left, MapBounds.Top));

        // The colour bar is drawn from the map's own scale, so the strip and its caption cannot disagree with the cells.
        double sliceHeight = BarBounds.Height / BarSlices;
        for (int slice = 0; slice < BarSlices; slice++)
        {
            double level = map.Scale.MinDb + ((map.Scale.MaxDb - map.Scale.MinDb) * (slice + 0.5) / BarSlices);
            var rectangle = new Rectangle
            {
                Width = BarBounds.Width,
                Height = sliceHeight,
                Fill = Frozen(map.Scale.ColourAt(level)),
            };
            Canvas.SetLeft(rectangle, BarBounds.Left);
            Canvas.SetTop(rectangle, BarBounds.Bottom - ((slice + 1) * sliceHeight));
            canvas.Children.Add(rectangle);
        }

        canvas.Children.Add(Label(map.Title, MapBounds.Left, 3.0, 280.0, TextAlignment.Left, bold: true, size: 12.0));
        if (map.Interpolated)
            canvas.Children.Add(Label(CurveChart.InterpolatedLabel, 200.0, 6.0, 220.0, TextAlignment.Left, size: 11.0, brush: NoteBrush));

        canvas.Children.Add(Label(map.XAxisLabel, MapBounds.Left, MapBounds.Bottom + 4.0, MapBounds.Width, TextAlignment.Left, size: 11.0, brush: NoteBrush));
        canvas.Children.Add(Label(map.YAxisLabel, MapBounds.Left, MapBounds.Bottom + 18.0, MapBounds.Width, TextAlignment.Left, size: 11.0, brush: NoteBrush));
        canvas.Children.Add(Label(AxisScale.FormatTick(map.Scale.MaxDb, 1), BarBounds.Left, BarBounds.Top - 16.0, 60.0, TextAlignment.Left));
        canvas.Children.Add(Label(AxisScale.FormatTick(map.Scale.MinDb, 1), BarBounds.Left, BarBounds.Bottom + 2.0, 60.0, TextAlignment.Left));
        canvas.Children.Add(Label(map.Scale.Label, BarBounds.Left - 4.0, BarBounds.Bottom + 18.0, 80.0, TextAlignment.Left, size: 10.0, brush: NoteBrush));

        Content = canvas;
    }

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
            TextWrapping = TextWrapping.Wrap,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = brush ?? TextBrush,
        };
        Canvas.SetLeft(block, left);
        Canvas.SetTop(block, top);
        return block;
    }

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
