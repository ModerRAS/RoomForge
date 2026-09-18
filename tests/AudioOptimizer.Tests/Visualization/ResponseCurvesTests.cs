namespace AudioOptimizer.Tests.Visualization;

using AudioOptimizer.Visualization;
using Xunit;

/// <summary>
/// The §22 curve builders. Every assertion carries its formula and the expected value; the numbers below are small
/// enough to check by hand, which is the point — these figures decide what a user believes about their room.
/// </summary>
public sealed class ResponseCurvesTests
{
    private static readonly double[] Frequencies = [50.0, 100.0, 150.0];

    /// <summary>Three bins of a flat level, so arithmetic stays checkable by hand.</summary>
    private static SpatialLevels Summary(params double[][] positions)
    {
        double[] mean = [.. Enumerable.Range(0, Frequencies.Length).Select(k => positions.Average(row => row[k]))];
        double[] sigma = [.. Enumerable.Range(0, Frequencies.Length).Select(k => Math.Sqrt(positions.Average(row => (row[k] - mean[k]) * (row[k] - mean[k]))))];
        double[] range = [.. Enumerable.Range(0, Frequencies.Length).Select(k => positions.Max(row => row[k]) - positions.Min(row => row[k]))];
        return new SpatialLevels(Frequencies, mean, sigma, range, positions.SelectMany(row => row).Max(), positions.Length);
    }

    [Fact]
    public void A_level_figure_has_one_curve_per_position_however_many_that_is()
    {
        // Two positions (a 1×1×2 grid) and then 27 of them: the count comes from the data, never from a constant.
        // Position A: 0/0/0 dB, position B: +3 at every bin → mean = (0+3)/2 = 1.5, σ = |3−0|/2 = 1.5.
        var two = Summary([0.0, 0.0, 0.0], [3.0, 3.0, 3.0]);
        CurvePlot small = ResponseCurves.Spatial(
            two,
            [new PositionLevels("p1", [0.0, 0.0, 0.0]), new PositionLevels("p2", [3.0, 3.0, 3.0])],
            LevelReference.RelativeToBandMean,
            "two");

        Assert.Equal(2, small.Of(CurveRole.Position).Count);
        Assert.Single(small.Of(CurveRole.Mean));
        // The ribbon is a closed ring: 3 bins up, then 3 bins back down = 2 × 3 points.
        Assert.Equal(Frequencies.Length * 2, small.Of(CurveRole.Band).Single().Points.Count);
        // Referenced mean over the band = the band mean itself → 0 dB by construction: (0+3)/2 = 1.5 is the band mean.
        Assert.All(small.Of(CurveRole.Mean).Single().Points, point => Assert.Equal(0.0, point.Y, 12));
        // ±1σ = 1.5 dB, so the ring's top is +1.5 and its bottom is −1.5.
        Assert.Equal(1.5, small.Of(CurveRole.Band).Single().MaxY, 12);
        Assert.Equal(-1.5, small.Of(CurveRole.Band).Single().MinY, 12);

        double[][] rows = [.. Enumerable.Range(0, 27).Select(i => new[] { (double)i, (double)i, (double)i })];
        var many = Summary(rows);
        CurvePlot large = ResponseCurves.Spatial(
            many,
            [.. Enumerable.Range(0, 27).Select(i => new PositionLevels($"x{i}", rows[i]))],
            LevelReference.RelativeToBandMean,
            "twenty-seven");

        Assert.Equal(27, large.Of(CurveRole.Position).Count);   // a literal 27 in the builder would fail the 2-position case
    }

    [Fact]
    public void One_shared_reference_keeps_the_level_difference_between_two_curves()
    {
        // A = 0 dB everywhere, B = +3 dB everywhere. The figure has ONE reference context (the band mean, 1.5 dB),
        // so B − A must still read exactly 3.0 dB at every bin. Per-series referencing would make it 0.0 — the
        // silent shape-for-level substitution §22's absolute-numbers requirement is about.
        var summary = Summary([0.0, 0.0, 0.0], [3.0, 3.0, 3.0]);
        CurvePlot plot = ResponseCurves.Spatial(
            summary,
            [new PositionLevels("quiet", [0.0, 0.0, 0.0]), new PositionLevels("loud", [3.0, 3.0, 3.0])],
            LevelReference.RelativeToBandMean,
            "shared");

        CurveSeries quiet = plot.Of(CurveRole.Position)[0];
        CurveSeries loud = plot.Of(CurveRole.Position)[1];
        for (int k = 0; k < Frequencies.Length; k++)
            Assert.Equal(3.0, loud.Points[k].Y - quiet.Points[k].Y, 12);
        // And the differencing is not hiding a shift: quiet sits 1.5 dB below the band mean, loud 1.5 above.
        Assert.All(quiet.Points, point => Assert.Equal(-1.5, point.Y, 12));
        Assert.All(loud.Points, point => Assert.Equal(1.5, point.Y, 12));
    }

    [Fact]
    public void The_reference_sets_both_the_label_and_the_values()
    {
        var summary = Summary([0.0, 0.0, 0.0], [3.0, 3.0, 3.0]);
        PositionLevels[] positions = [new PositionLevels("quiet", [0.0, 0.0, 0.0]), new PositionLevels("loud", [3.0, 3.0, 3.0])];

        CurvePlot relative = ResponseCurves.Spatial(summary, positions, LevelReference.RelativeToBandMean, "relative");
        CurvePlot normalised = ResponseCurves.Spatial(summary, positions, LevelReference.NormalisedToMax, "normalised");

        Assert.Equal("dB (relative to band mean)", relative.YAxisLabel);
        Assert.Equal("normalised dB (0 dB = maximum)", normalised.YAxisLabel);
        // Both figures use the same data and different references: the numbers must differ too, or the label is a caption.
        // Relative: band mean 1.5 → the loudest curve sits at +1.5. Normalised: the loudest bin is 3.0 → it sits at 0.0.
        Assert.Equal(1.5, relative.YMax, 12);
        Assert.Equal(0.0, normalised.YMax, 12);
        Assert.NotEqual(relative.YMin, normalised.YMin);
    }

    [Fact]
    public void A_spread_figure_states_a_spread_and_claims_no_level_reference()
    {
        var summary = Summary([0.0, 0.0, 0.0], [3.0, 3.0, 3.0]);
        CurvePlot sigma = ResponseCurves.Spread(summary, CurveRole.StdDev, "sigma");
        CurvePlot range = ResponseCurves.Spread(summary, CurveRole.Range, "range");

        Assert.Equal("dB (standard deviation across positions)", sigma.YAxisLabel);
        Assert.Equal("dB (max − min across positions)", range.YAxisLabel);
        Assert.DoesNotContain("relative to", sigma.YAxisLabel);
        Assert.DoesNotContain("normalised", sigma.YAxisLabel);
        // σ = |3−0|/2 = 1.5 dB and max − min = 3.0 dB, at every bin, taken from the summary rather than recomputed.
        Assert.All(sigma.Series.Single().Points, point => Assert.Equal(1.5, point.Y, 12));
        Assert.All(range.Series.Single().Points, point => Assert.Equal(3.0, point.Y, 12));
        Assert.Throws<ArgumentException>(() => ResponseCurves.Spread(summary, CurveRole.Mean, "not a spread"));
    }

    [Fact]
    public void A_flat_measurement_still_has_a_drawable_axis()
    {
        // One position has σ = 0 and max − min = 0 at every bin: a zero-extent axis has no legal scale, so the plot
        // pads it by DegeneratePad on each side instead of throwing during rendering.
        var flat = Summary([2.0, 2.0, 2.0]);
        CurvePlot sigma = ResponseCurves.Spread(flat, CurveRole.StdDev, "flat");

        Assert.Equal(0.0, sigma.YMax - sigma.YMin - (2.0 * CurvePlot.DegeneratePad), 12);
        Assert.Equal(-1.0, sigma.YMin, 12);
        Assert.Equal(1.0, sigma.YMax, 12);
    }

    [Fact]
    public void Interpolation_densifies_the_samples_and_declares_itself()
    {
        var summary = Summary([0.0, 0.0, 0.0], [3.0, 3.0, 3.0]);
        CurvePlot plain = ResponseCurves.Spatial(
            summary,
            [new PositionLevels("quiet", [0.0, 0.0, 0.0]), new PositionLevels("loud", [3.0, 3.0, 3.0])],
            LevelReference.RelativeToBandMean,
            "plain");
        CurvePlot smooth = ResponseCurves.Interpolated(plain);

        Assert.False(plain.Interpolated);
        Assert.True(smooth.Interpolated);
        // 3 bins with 4 samples per interval: (3 − 1) × 4 + 1 = 9 points per level curve, and (6 − 1) × 4 + 1 = 21 for
        // the ribbon ring (2 × 3 bins), so the assertion is per series against its own original count.
        // The level curves have 3 bins each; the ribbon is a 6-point ring (3 up, 3 back down).
        Assert.All(plain.Series.Where(series => !series.Filled), series => Assert.Equal(Frequencies.Length, series.Points.Count));
        Assert.Equal(Frequencies.Length * 2, plain.Of(CurveRole.Band).Single().Points.Count);
        Assert.All(Enumerable.Range(0, smooth.Series.Count), i =>
            Assert.Equal(((plain.Series[i].Points.Count - 1) * ResponseCurves.DefaultInterpolationSamples) + 1, smooth.Series[i].Points.Count));
        // The inserted samples sit on the straight line between their neighbours, not off it: the second of the four
        // intervals from 50 Hz to 100 Hz is at 50 + (50 × 1/4) = 62.5 Hz.
        CurveSeries mean = smooth.Of(CurveRole.Mean).Single();
        Assert.Equal(62.5, mean.Points[1].X, 12);
        Assert.Equal(mean.Points[0].Y, mean.Points[1].Y, 12);
        Assert.Contains(smooth.Legend, line => line.Contains("Interpolated visualization", StringComparison.Ordinal));
        Assert.DoesNotContain(plain.Legend, line => line.Contains("Interpolated", StringComparison.Ordinal));
    }

    [Fact]
    public void The_overlay_anchors_its_levels_to_the_baseline_and_reports_the_difference()
    {
        // "after" is the baseline +3 dB, built the way the optimizer's identity model would give it back.
        var before = Summary([0.0, 0.0, 0.0]);
        var after = Summary([3.0, 3.0, 3.0]);
        CurvePlot plot = ResponseCurves.Overlay(before, after, LevelReference.RelativeToBandMean, "predicted", "before/after");

        CurveSeries low = plot.Of(CurveRole.Before).Single();
        CurveSeries high = plot.Of(CurveRole.After).Single();
        for (int k = 0; k < Frequencies.Length; k++)
            Assert.Equal(3.0, high.Points[k].Y - low.Points[k].Y, 12);

        // The baseline is referenced to the shared band mean (0 dB here, since it is the only configuration in the
        // context), so its average is exactly 0 and the difference is +3.00 dB — not 0.00, which is what a
        // self-referenced overlay would report.
        Assert.Equal(0.0, low.Points.Average(point => point.Y), 12);
        Assert.Contains(plot.Legend, line => line.Contains("Δ mean +3.00 dB", StringComparison.Ordinal));
        Assert.Contains(plot.Legend, line => line.Contains("after (predicted):", StringComparison.Ordinal));
        Assert.Equal("dB (relative to band mean)", plot.YAxisLabel);
    }

    [Fact]
    public void A_mismatched_or_missing_grid_is_refused_rather_than_averaged()
    {
        var summary = Summary([0.0, 0.0, 0.0]);
        Assert.Throws<ArgumentException>(() => ResponseCurves.Spatial(summary, [], LevelReference.RelativeToBandMean, "empty"));
        Assert.Throws<ArgumentException>(() => ResponseCurves.Spatial(
            summary,
            [new PositionLevels("wrong length", [0.0, 0.0])],
            LevelReference.RelativeToBandMean,
            "mismatch"));
        Assert.Throws<ArgumentException>(() => ResponseCurves.Overlay(
            summary,
            new SpatialLevels([50.0, 100.0], [0.0, 0.0], [0.0, 0.0], [0.0, 0.0], 0.0, 1),
            LevelReference.RelativeToBandMean,
            "shorter",
            "overlay"));
    }
}
