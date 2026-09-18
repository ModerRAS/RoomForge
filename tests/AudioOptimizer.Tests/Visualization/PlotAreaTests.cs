namespace AudioOptimizer.Tests.Visualization;

using AudioOptimizer.Visualization;

/// <summary>
/// The plot math every figure depends on: one linear mapping per axis, and the rectangle that carries the data.
/// Hand-computed expectations throughout, because these numbers are what the pixel probes are derived from.
/// </summary>
public sealed class PlotAreaTests
{
    private static readonly PlotArea Bounds = new(8, 8, 312, 172);

    [Fact]
    public void The_vertical_scale_puts_larger_data_higher_on_the_surface_and_round_trips()
    {
        // 20 Hz → 172 (bottom), 200 Hz → 8 (top): screen Y grows downward, so the pixel ends are swapped.
        AxisScale frequency = Bounds.Vertical(20, 200);
        Assert.Equal(172.0, frequency.ToPixel(20), 1e-12);
        Assert.Equal(8.0, frequency.ToPixel(200), 1e-12);

        // 110 Hz is the midpoint: 172 − (110−20)/(200−20) · 164 = 172 − 0.5 · 164 = 90.
        Assert.Equal(90.0, frequency.ToPixel(110), 1e-12);

        // Round trip: (ToPixel ∘ ToData)(p) = p for any pixel inside the axis.
        foreach (double pixel in new[] { 8.0, 33.5, 90.0, 171.9 })
            Assert.Equal(pixel, frequency.ToPixel(frequency.ToData(pixel)), 1e-9);

        // Horizontal axis, left to right: 20 Hz → 8, 150 Hz → 312, and 85 Hz at the midpoint = 160.
        AxisScale x = Bounds.Horizontal(20, 150);
        Assert.Equal(8.0, x.ToPixel(20), 1e-12);
        Assert.Equal(312.0, x.ToPixel(150), 1e-12);
        Assert.Equal(160.0, x.ToPixel(85), 1e-12);
    }

    [Fact]
    public void Tick_values_are_the_axis_own_numbers()
    {
        AxisScale frequency = Bounds.Horizontal(20, 150);

        // 20…150 Hz stepping 25: 25, 50, 75, 100, 125, 150 — inclusive of the end that lands on a tick.
        Assert.Equal([25.0, 50.0, 75.0, 100.0, 125.0, 150.0], frequency.TickValues(25));

        // Ticks never leave the data range: 0 is below 20 Hz, so it is not a tick of this axis.
        Assert.Equal([50.0, 100.0, 150.0], frequency.TickValues(50, origin: 0));

        // An origin off the range shifts the whole ladder: ceil((20 − 25)/50) = 0, so 25, 75, 125.
        Assert.Equal([25.0, 75.0, 125.0], frequency.TickValues(50, origin: 25));

        // Labels are fixed-decimal so a label never reads "20.000000000001".
        Assert.Equal("20", AxisScale.FormatTick(20.0));
        Assert.Equal("25.0", AxisScale.FormatTick(25.0, 1));
        Assert.Equal("0.50", AxisScale.FormatTick(0.5, 2));
    }

    [Fact]
    public void Degenerate_geometry_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AxisScale(10, 10, 0, 100));      // zero data span
        Assert.Throws<ArgumentOutOfRangeException>(() => new AxisScale(10, 5, 0, 100));       // reversed data span
        Assert.Throws<ArgumentOutOfRangeException>(() => new AxisScale(0, 10, 5, 5));         // zero pixel span
        Assert.Throws<ArgumentOutOfRangeException>(() => new AxisScale(double.NaN, 10, 0, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlotArea(8, 8, 8, 172));         // zero width
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlotArea(8, 8, 312, 8));         // zero height
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlotArea(8, 8, 312, 172).Deflate(100));   // inset past the centre
        Assert.Throws<ArgumentOutOfRangeException>(() => new AxisScale(0, 10, 0, 100).TickValues(0));
    }

    [Fact]
    public void The_plot_area_reports_its_extent_and_membership()
    {
        // 312 − 8 = 304 wide, 172 − 8 = 164 tall; the 1 px frame sits exactly on these coordinates.
        Assert.Equal(304.0, Bounds.Width, 1e-12);
        Assert.Equal(164.0, Bounds.Height, 1e-12);
        Assert.True(Bounds.Contains(8, 8));
        Assert.True(Bounds.Contains(312, 172));
        Assert.False(Bounds.Contains(7.9, 100));
        Assert.False(Bounds.Contains(160, 172.1));
        Assert.Equal(new PlotArea(16, 16, 304, 164), Bounds.Deflate(8));
    }
}
