namespace AudioOptimizer.Visualization;

/// <summary>
/// A linear mapping between one data range and one pixel range, plus the tick values that go with it.
/// <para>
/// PixelMin may be greater than PixelMax: screen Y grows downward, so a vertical axis is this same mapping
/// with its ends swapped — not a special case and not a second type. Construction validates, so an instance
/// that exists can always be mapped through without a divide-by-zero.
/// </para>
/// </summary>
public sealed record AxisScale
{
    /// <summary>Maps <paramref name="dataMin"/> → <paramref name="pixelMin"/> and <paramref name="dataMax"/> → <paramref name="pixelMax"/>.</summary>
    public AxisScale(double dataMin, double dataMax, double pixelMin, double pixelMax)
    {
        if (!double.IsFinite(dataMin) || !double.IsFinite(dataMax))
            throw new ArgumentOutOfRangeException(nameof(dataMin), $"[{dataMin}, {dataMax}] must be finite.");
        if (dataMax <= dataMin)
            throw new ArgumentOutOfRangeException(nameof(dataMax), dataMax, $"The data range must be increasing; got [{dataMin}, {dataMax}].");
        if (!double.IsFinite(pixelMin) || !double.IsFinite(pixelMax) || pixelMin == pixelMax)
            throw new ArgumentOutOfRangeException(nameof(pixelMin), $"The pixel range [{pixelMin}, {pixelMax}] must be finite and non-degenerate.");

        DataMin = dataMin;
        DataMax = dataMax;
        PixelMin = pixelMin;
        PixelMax = pixelMax;
    }

    public double DataMin { get; }

    public double DataMax { get; }

    public double PixelMin { get; }

    public double PixelMax { get; }

    public double DataSpan => DataMax - DataMin;

    public double PixelSpan => PixelMax - PixelMin;

    /// <summary>Pixel for a data value, unsaturated: outside the range maps outside the pixel range.</summary>
    public double ToPixel(double value) => PixelMin + (value - DataMin) / DataSpan * PixelSpan;

    /// <summary>Data value for a pixel — the inverse mapping, so a click can be turned back into a frequency.</summary>
    public double ToData(double pixel) => DataMin + (pixel - PixelMin) / PixelSpan * DataSpan;

    /// <summary>
    /// Tick values from <paramref name="origin"/> stepping by <paramref name="step"/> while inside the data
    /// range, inclusive of both ends. Ticks are the axis's own numbers, so they cannot drift from the mapping.
    /// </summary>
    public double[] TickValues(double step, double origin = 0.0)
    {
        if (!double.IsFinite(step) || step <= 0)
            throw new ArgumentOutOfRangeException(nameof(step), step, "The tick step must be finite and > 0.");

        var ticks = new List<double>();
        double start = origin + Math.Ceiling((DataMin - origin) / step) * step;
        // Count is bounded by the range: an unguarded loop would spin forever if step underflows relative to it.
        for (double value = start, guard = 0; value <= DataMax && guard < 10_000; value += step, guard++)
            ticks.Add(value);
        return [.. ticks];
    }

    /// <summary>Fixed-decimal tick text, so labels do not flip between "20" and "20.000000000001".</summary>
    public static string FormatTick(double value, int decimals = 0)
        => value.ToString("F" + Math.Clamp(decimals, 0, 6), System.Globalization.CultureInfo.InvariantCulture);
}
