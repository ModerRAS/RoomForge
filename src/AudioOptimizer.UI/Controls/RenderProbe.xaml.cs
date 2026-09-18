namespace AudioOptimizer.UI.Controls;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using AudioOptimizer.Visualization;

/// <summary>
/// A minimal plot surface used as the M0 evidence surface. It is deliberately not decorative: the frame and the
/// series are laid out with <see cref="PlotArea"/> and <see cref="AxisScale"/>, which is exactly how the
/// analysis views will be built, so the render harness is proving the real composition path and not a mock.
/// Painted geometry is aliased (edge mode) so a pixel probe is a fact rather than a guess.
/// </summary>
public partial class RenderProbe : UserControl
{
    /// <summary>Pinned surface size. Every probe coordinate in the render tests depends on these.</summary>
    public const double PinnedWidth = 320;

    public const double PinnedHeight = 200;

    /// <summary>The data-carrying rectangle inside the pinned surface.</summary>
    public static readonly PlotArea PlotBounds = new(8, 8, 312, 172);

    /// <summary>A distinctive stroke colour, so a probe can tell the series from the frame and the background.</summary>
    public static readonly RgbColour CurveColour = new(0x1E, 0x90, 0xFF);

    private static readonly RgbColour FrameColour = new(0x80, 0x80, 0x80);

    public RenderProbe()
    {
        InitializeComponent();
        DrawFrame();
    }

    /// <summary>
    /// Shows the §23 interpolation disclosure. Collapsed by default, and asserted in both directions: a label
    /// that is drawn unconditionally would pass any presence-only check.
    /// </summary>
    public bool ShowInterpolatedLabel
    {
        get => InterpolatedLabel.Visibility == Visibility.Visible;
        set => InterpolatedLabel.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Frames the plot area and draws one series across it. Shared values share one x step: the series is
    /// indexed, and the y scale is the caller's data range so the figure is readable before any axis exists.
    /// </summary>
    public void Plot(IReadOnlyList<double> values, double dataMin, double dataMax)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count < 2) throw new ArgumentException("A series needs at least two points to be a line.", nameof(values));

        AxisScale x = PlotBounds.Horizontal(0, values.Count - 1);
        AxisScale y = PlotBounds.Vertical(dataMin, dataMax);

        var points = new PointCollection(values.Count);
        for (int i = 0; i < values.Count; i++)
            points.Add(new Point(x.ToPixel(i), y.ToPixel(values[i])));

        var polyline = new Polyline
        {
            Points = points,
            Stroke = VisualColours.ToBrush(CurveColour),
            StrokeThickness = 1,
            SnapsToDevicePixels = true,
        };

        // Aliased edges: with antialiasing a one-pixel line spreads its colour over neighbours and a probe
        // would have to guess a blend. This is a rendering choice for determinism, not for looks.
        RenderOptions.SetEdgeMode(polyline, EdgeMode.Aliased);
        Surface.Children.Add(polyline);
    }

    private void DrawFrame()
    {
        var frame = new Rectangle
        {
            Width = PlotBounds.Width,
            Height = PlotBounds.Height,
            Stroke = VisualColours.ToBrush(FrameColour),
            StrokeThickness = 1,
            SnapsToDevicePixels = true,
        };
        Canvas.SetLeft(frame, PlotBounds.Left);
        Canvas.SetTop(frame, PlotBounds.Top);
        RenderOptions.SetEdgeMode(frame, EdgeMode.Aliased);
        Surface.Children.Add(frame);
    }
}
