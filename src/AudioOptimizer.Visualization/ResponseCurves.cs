namespace AudioOptimizer.Visualization;

using System.Globalization;

/// <summary>One position's measured levels, in the pipeline's own dB, paired with the label to draw it under.</summary>
public sealed record PositionLevels(string Label, IReadOnlyList<double> LevelsDb);

/// <summary>
/// The per-frequency spatial aggregation of a measurement, in the pipeline's own dB: what
/// <c>SpatialMetrics</c> computes, expressed here as plain numbers so this assembly stays dependency-free. The
/// caller maps its own types onto this shape.
/// </summary>
public sealed record SpatialLevels
{
    public SpatialLevels(
        IReadOnlyList<double> frequenciesHz,
        IReadOnlyList<double> meanDb,
        IReadOnlyList<double> stdDevDb,
        IReadOnlyList<double> rangeDb,
        double maxMeasuredDb,
        int positionCount)
    {
        ArgumentNullException.ThrowIfNull(frequenciesHz);
        ArgumentNullException.ThrowIfNull(meanDb);
        ArgumentNullException.ThrowIfNull(stdDevDb);
        ArgumentNullException.ThrowIfNull(rangeDb);
        if (frequenciesHz.Count < 2)
            throw new ArgumentException($"A curve needs at least two frequencies; got {frequenciesHz.Count}.", nameof(frequenciesHz));
        if (meanDb.Count != frequenciesHz.Count || stdDevDb.Count != frequenciesHz.Count || rangeDb.Count != frequenciesHz.Count)
            throw new ArgumentException($"Every curve must cover the same {frequenciesHz.Count} bins.", nameof(meanDb));
        if (positionCount < 1)
            throw new ArgumentOutOfRangeException(nameof(positionCount), positionCount, "A measurement has at least one position.");
        if (!double.IsFinite(maxMeasuredDb))
            throw new ArgumentOutOfRangeException(nameof(maxMeasuredDb), maxMeasuredDb, "The maximum must be finite.");

        FrequenciesHz = frequenciesHz;
        MeanDb = meanDb;
        StdDevDb = stdDevDb;
        RangeDb = rangeDb;
        MaxMeasuredDb = maxMeasuredDb;
        PositionCount = positionCount;
    }

    public IReadOnlyList<double> FrequenciesHz { get; }

    public IReadOnlyList<double> MeanDb { get; }

    public IReadOnlyList<double> StdDevDb { get; }

    public IReadOnlyList<double> RangeDb { get; }

    /// <summary>The loudest measured bin across every position — the maximum a max-normalised reference needs.</summary>
    public double MaxMeasuredDb { get; }

    public int PositionCount { get; }
}

/// <summary>
/// Builds the figures §22 asks for: a level figure with the mean and its ±1σ ribbon, the spread across positions
/// against frequency, and a before/after overlay. Pure and pixel-free — the UI maps these curves onto a plot area.
/// <para>
/// The one thing that must not be got wrong here is the level reference. A level figure is stated against a single
/// <see cref="LevelContext"/> <b>shared by every series in it</b>: referencing each series to itself would normalise
/// the positions (or the two configurations of an overlay) to their own mean, which turns a level comparison into a
/// shape comparison while still looking like a level plot. Sharing the context is what keeps a 3 dB difference
/// between two curves visible as 3 dB.
/// </para>
/// </summary>
public static class ResponseCurves
{
    public const string FrequencyAxisLabel = "frequency (Hz)";

    /// <summary>Default samples per measured interval for <see cref="Interpolated"/>.</summary>
    public const int DefaultInterpolationSamples = 4;

    public static readonly RgbColour MeanColour = new(0x10, 0x10, 0x10);
    public static readonly RgbColour BandColour = new(0xBF, 0xD3, 0xE6);
    public static readonly RgbColour BeforeColour = new(0x0B, 0x3D, 0x91);
    public static readonly RgbColour AfterColour = new(0xD6, 0x20, 0x2A);
    public static readonly RgbColour StdDevColour = new(0x1E, 0x90, 0xFF);
    public static readonly RgbColour RangeColour = new(0x8A, 0x2B, 0xE2);

    /// <summary>
    /// One series per measured position (however many the grid actually has), plus the mean curve and the ±1σ
    /// ribbon. The axis label is the reference's own, so a figure cannot claim a reference it did not use.
    /// </summary>
    public static CurvePlot Spatial(SpatialLevels summary, IReadOnlyList<PositionLevels> positions, LevelReference reference, string title)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(reference);
        if (positions.Count == 0)
            throw new ArgumentException("At least one measured position is needed to draw a level figure.", nameof(positions));
        foreach (PositionLevels position in positions)
            if (position.LevelsDb.Count != summary.FrequenciesHz.Count)
                throw new ArgumentException($"'{position.Label}' has {position.LevelsDb.Count} bins and the summary has {summary.FrequenciesHz.Count}.", nameof(positions));

        var context = new LevelContext(summary.MeanDb.Average(), summary.MaxMeasuredDb);
        double[] meanCurve = [.. summary.MeanDb.Select(level => reference.ToReferenceUnits(level, context))];
        double[] upper = [.. Enumerable.Range(0, meanCurve.Length).Select(k => meanCurve[k] + summary.StdDevDb[k])];
        double[] lower = [.. Enumerable.Range(0, meanCurve.Length).Select(k => meanCurve[k] - summary.StdDevDb[k])];

        var series = new List<CurveSeries>();
        for (int i = 0; i < positions.Count; i++)
            series.Add(new CurveSeries(
                positions[i].Label,
                CurveRole.Position,
                Curve(summary.FrequenciesHz, [.. positions[i].LevelsDb.Select(level => reference.ToReferenceUnits(level, context))]),
                PositionColour(i, positions.Count)));
        series.Add(new CurveSeries("mean", CurveRole.Mean, Curve(summary.FrequenciesHz, meanCurve), MeanColour));
        series.Add(new CurveSeries("±1σ", CurveRole.Band, Ring(summary.FrequenciesHz, upper, lower), BandColour, filled: true));

        double worstSigma = summary.StdDevDb.Max();
        string[] legend =
        [
            $"mean of {summary.PositionCount} measured position(s): {Number(meanCurve.Average())} dB mean, "
                + $"{Number(meanCurve.Min())} … {Number(meanCurve.Max())} dB, σ {Number(Spread(meanCurve))} dB across the band",
            $"±1σ ribbon: σ {Number(summary.StdDevDb.Average())} dB on average, worst {Number(worstSigma)} dB at "
                + $"{Number(summary.FrequenciesHz[IndexOf(summary.StdDevDb, worstSigma)], 1)} Hz",
        ];

        return new CurvePlot(title, FrequencyAxisLabel, reference.AxisLabel, series, legend);
    }

    /// <summary>
    /// Spread across positions against frequency: standard deviation or max − min. There is deliberately no
    /// <see cref="LevelReference"/> parameter — a spread is a difference between levels, so it has nothing to be
    /// referenced against, and borrowing the level figure's label would state something false about the axis. The
    /// label says what the dB are instead.
    /// </summary>
    public static CurvePlot Spread(SpatialLevels summary, CurveRole role, string title)
    {
        ArgumentNullException.ThrowIfNull(summary);
        if (role is not (CurveRole.StdDev or CurveRole.Range))
            throw new ArgumentException($"A spread figure is either {CurveRole.StdDev} or {CurveRole.Range}; got {role}.", nameof(role));

        bool standardDeviation = role == CurveRole.StdDev;
        IReadOnlyList<double> values = standardDeviation ? summary.StdDevDb : summary.RangeDb;
        CurvePoint[] points = [.. Enumerable.Range(0, values.Count).Select(k => new CurvePoint(summary.FrequenciesHz[k], values[k]))];
        double worst = values.Max();
        string[] legend =
        [
            $"mean {Number(values.Average())} dB, worst {Number(worst)} dB at "
                + $"{Number(summary.FrequenciesHz[IndexOf(values, worst)], 1)} Hz over {points.Length} bins",
        ];
        CurveSeries series = new(
            standardDeviation ? "standard deviation across positions" : "max − min across positions",
            role,
            points,
            standardDeviation ? StdDevColour : RangeColour);
        string yAxisLabel = standardDeviation
            ? "dB (standard deviation across positions)"
            : "dB (max − min across positions)";
        return new CurvePlot(title, FrequencyAxisLabel, yAxisLabel, [series], legend);
    }

    /// <summary>
    /// Before/after overlay of two configurations' mean curves, in absolute numbers. The reference context is
    /// anchored to <paramref name="before"/> (not to each curve separately), so an after-curve that is louder is
    /// drawn louder and the difference in the legend is a real level difference.
    /// </summary>
    public static CurvePlot Overlay(SpatialLevels before, SpatialLevels after, LevelReference reference, string afterLabel, string title)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentException.ThrowIfNullOrWhiteSpace(afterLabel);
        if (before.FrequenciesHz.Count != after.FrequenciesHz.Count)
            throw new ArgumentException(
                $"The two configurations cover {before.FrequenciesHz.Count} and {after.FrequenciesHz.Count} bins; they cannot be overlaid.",
                nameof(after));

        for (int k = 0; k < before.FrequenciesHz.Count; k++)
            if (before.FrequenciesHz[k] != after.FrequenciesHz[k])
                throw new ArgumentException($"Bin {k} is {before.FrequenciesHz[k]} Hz before and {after.FrequenciesHz[k]} Hz after.", nameof(after));

        var context = new LevelContext(before.MeanDb.Average(), Math.Max(before.MaxMeasuredDb, after.MaxMeasuredDb));
        double[] beforeUnits = [.. before.MeanDb.Select(level => reference.ToReferenceUnits(level, context))];
        double[] afterUnits = [.. after.MeanDb.Select(level => reference.ToReferenceUnits(level, context))];
        double delta = afterUnits.Average() - beforeUnits.Average();

        string[] legend =
        [
            $"before: mean {Number(beforeUnits.Average())} dB, σ {Number(Spread(beforeUnits))} dB across the band "
                + $"({before.PositionCount} position(s))",
            $"after ({afterLabel}): mean {Number(afterUnits.Average())} dB, σ {Number(Spread(afterUnits))} dB — "
                + $"Δ mean {delta.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture)} dB",
        ];

        return new CurvePlot(
            title,
            FrequencyAxisLabel,
            reference.AxisLabel,
            [
                new CurveSeries("before", CurveRole.Before, Curve(before.FrequenciesHz, beforeUnits), BeforeColour),
                new CurveSeries($"after ({afterLabel})", CurveRole.After, Curve(after.FrequenciesHz, afterUnits), AfterColour),
            ],
            legend);
    }

    /// <summary>
    /// Densifies every series by straight-line interpolation between measured bins and flags the figure as
    /// interpolated — the state §23 requires the UI to show rather than hide.
    /// ponytail: linear only. If faceted curves ever matter, the upgrade is a monotone cubic through the same
    /// samples (monotone, not just smoother: an overshooting spline would draw a dip the room does not have).
    /// </summary>
    public static CurvePlot Interpolated(CurvePlot plot, int samplesPerInterval = DefaultInterpolationSamples)
    {
        ArgumentNullException.ThrowIfNull(plot);
        if (samplesPerInterval < 2)
            throw new ArgumentOutOfRangeException(nameof(samplesPerInterval), samplesPerInterval, "Interpolation needs at least two samples per interval.");

        CurveSeries[] densified = [.. plot.Series.Select(series => new CurveSeries(
            series.Label,
            series.Role,
            Densify(series.Points, samplesPerInterval),
            series.Colour,
            series.Filled))];
        string[] legend = [.. plot.Legend, $"Interpolated visualization: {samplesPerInterval} samples per measured interval."];
        return new CurvePlot(plot.Title, plot.XAxisLabel, plot.YAxisLabel, densified, legend, interpolated: true);
    }

    private static CurvePoint[] Curve(IReadOnlyList<double> x, IReadOnlyList<double> y)
        => [.. Enumerable.Range(0, x.Count).Select(k => new CurvePoint(x[k], y[k]))];

    /// <summary>The ±1σ ribbon as a closed ring: the upper edge left to right, then the lower edge right to left.</summary>
    private static CurvePoint[] Ring(IReadOnlyList<double> x, IReadOnlyList<double> upper, IReadOnlyList<double> lower)
    {
        var ring = new CurvePoint[x.Count * 2];
        for (int k = 0; k < x.Count; k++) ring[k] = new CurvePoint(x[k], upper[k]);
        for (int k = 0; k < x.Count; k++) ring[(x.Count * 2) - 1 - k] = new CurvePoint(x[k], lower[k]);
        return ring;
    }

    private static CurvePoint[] Densify(IReadOnlyList<CurvePoint> points, int samples)
    {
        var densified = new List<CurvePoint>(((points.Count - 1) * samples) + 1);
        for (int i = 0; i < points.Count - 1; i++)
        {
            for (int s = 0; s < samples; s++)
            {
                double t = s / (double)samples;
                densified.Add(new CurvePoint(
                    points[i].X + ((points[i + 1].X - points[i].X) * t),
                    points[i].Y + ((points[i + 1].Y - points[i].Y) * t)));
            }
        }

        densified.Add(points[^1]);
        return [.. densified];
    }

    /// <summary>
    /// Population standard deviation of a curve's own values — spread across <b>frequencies</b>, which is a
    /// different quantity from the spread across positions that <c>SpatialLevels</c> carries, so it is not that.
    /// Same ÷N definition: these are the whole set being described, not a sample of one.
    /// </summary>
    private static double Spread(IReadOnlyList<double> values)
    {
        double mean = values.Average();
        return Math.Sqrt(values.Sum(value => (value - mean) * (value - mean)) / values.Count);
    }

    private static int IndexOf(IReadOnlyList<double> values, double value)
    {
        for (int i = 0; i < values.Count; i++)
            if (values[i] == value) return i;
        return 0;
    }

    private static string Number(double value, int decimals = 2)
        => value.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    private static RgbColour PositionColour(int index, int count)
        => RgbColour.Lerp(BeforeColour, AfterColour, count <= 1 ? 0.0 : index / (double)(count - 1));
}
