namespace AudioOptimizer.Tests.Visualization;

using AudioOptimizer.Visualization;
using Xunit;

/// <summary>
/// Heatmap building. The one thing under test beyond the arithmetic is that a figure has <b>one</b> level context for
/// all of its cells: a colour ramp has no labelled axis, so a per-cell reference would hide a real level difference
/// inside the colours where nobody could see it.
/// </summary>
public sealed class HeatmapTests
{
    [Fact]
    public void A_known_level_difference_survives_into_the_colour_mapping()
    {
        // Two cells, 0 dB and +3 dB, against one shared context of the whole figure (mean 1.5, max 3.0). With the
        // context shared, their referenced levels are −1.5 and +1.5 → different colours, and the difference is the
        // 3 dB that is really there. A per-cell reference would put both at 0 dB and the same colour.
        double[][] rows = [[0.0, 3.0]];
        LevelContext context = Heatmaps.SharedContext(rows.SelectMany(row => row));
        Heatmap map = Heatmaps.Matrix("levels", "x", "y", rows, LevelReference.RelativeToBandMean, context);

        Assert.Equal(-1.5, map.At(0, 0).LevelDb, 12);
        Assert.Equal(1.5, map.At(1, 0).LevelDb, 12);
        Assert.Equal(3.0, map.At(1, 0).LevelDb - map.At(0, 0).LevelDb, 12);
        Assert.NotEqual(map.At(0, 0).Colour, map.At(1, 0).Colour);
        // And the difference reaches the ramp itself, not just the label: the scale's fraction differs by 3 dB of
        // its span. Default relative span is (−12, 12) dB, so 3 dB is 3/24 = 0.125 of the ramp.
        double low = map.Scale.FractionAt(map.At(0, 0).LevelDb);
        double high = map.Scale.FractionAt(map.At(1, 0).LevelDb);
        Assert.Equal(0.125, high - low, 12);
    }

    [Fact]
    public void The_reference_supplied_context_is_what_the_cells_are_referenced_against()
    {
        double[][] rows = [[0.0, 3.0]];
        Heatmap viaMax = Heatmaps.Matrix("max", "x", "y", rows, LevelReference.NormalisedToMax, new LevelContext(1.5, 3.0));

        // Normalised to the loudest measured bin: +3 dB is 0 dB and 0 dB is −3 dB, so the top of the ramp is reached.
        Assert.Equal(0.0, viaMax.At(1, 0).LevelDb, 12);
        Assert.Equal(-3.0, viaMax.At(0, 0).LevelDb, 12);
        Assert.Equal("normalised dB (0 dB = maximum)", viaMax.Scale.Label.Split('[')[0].Trim());
        Assert.DoesNotContain("SPL", viaMax.Scale.Label);
    }

    [Fact]
    public void The_matrix_is_row_major_over_the_data_it_is_given()
    {
        double[][] rows = [[0.0, 1.0, 2.0], [3.0, 4.0, 5.0]];
        Heatmap map = Heatmaps.Matrix(
            "grid",
            "width (m)",
            "depth (m)",
            rows,
            LevelReference.RelativeToBandMean,
            Heatmaps.SharedContext(rows.SelectMany(row => row)));

        Assert.Equal(3, map.Columns);
        Assert.Equal(2, map.Rows);
        Assert.Equal(6, map.Cells.Count);
        // Cell (column, row) must read the cell that was written at that position, with the mean (2.5) removed.
        Assert.Equal(0.0 - 2.5, map.At(0, 0).LevelDb, 12);
        Assert.Equal(5.0 - 2.5, map.At(2, 1).LevelDb, 12);
    }

    [Fact]
    public void Interpolation_subdivides_and_declares_itself()
    {
        double[][] rows = [[0.0, 4.0], [4.0, 8.0]];
        Heatmap exact = Heatmaps.Matrix(
            "plane",
            "width (m)",
            "depth (m)",
            rows,
            LevelReference.RelativeToBandMean,
            Heatmaps.SharedContext(rows.SelectMany(row => row)));
        Heatmap smooth = Heatmaps.Interpolated(exact, factor: 2);

        Assert.False(exact.Interpolated);
        Assert.True(smooth.Interpolated);
        // (2 − 1) × 2 + 1 = 3 cells per axis.
        Assert.Equal(3, smooth.Columns);
        Assert.Equal(3, smooth.Rows);
        Assert.Equal(9, smooth.Cells.Count);
        // The inserted centre is the mean of the four corners in reference units: (−4 + 0 + 0 + 4)/4 = 0 dB.
        Assert.Equal(0.0, smooth.At(1, 1).LevelDb, 12);
        // The corners are unchanged, so interpolation moved nothing that was measured.
        Assert.Equal(exact.At(0, 0).LevelDb, smooth.At(0, 0).LevelDb, 12);
        Assert.Equal(exact.At(1, 1).LevelDb, smooth.At(2, 2).LevelDb, 12);
        // A single-cell axis has no interval to subdivide: the flag still flips, so the label can never be out of step
        // with the figures it describes.
        Heatmap line = Heatmaps.Interpolated(
            Heatmaps.Matrix("line", "x", "y", [[1.0, 2.0]], LevelReference.RelativeToBandMean, new LevelContext(1.5, 2.0)));
        Assert.True(line.Interpolated);
        Assert.Equal(2, line.Cells.Count);
    }

    [Fact]
    public void Planes_of_one_figure_share_a_context_and_per_plane_contexts_would_hide_a_real_difference()
    {
        // Two heights, 3 dB apart: Z−1 at 0 dB, Z+1 at +3 dB. With ONE context for the figure (mean 1.5, max 3) the
        // difference survives: −1.5 vs +1.5 dB and different colours. Referencing each plane to itself is the defect
        // this guards: both planes read 0 dB with identical colours, so a dimmer plane could render as bright as a
        // louder one and nothing on the picture would say so.
        double[][] lower = [[0.0, 0.0], [0.0, 0.0]];
        double[][] upper = [[3.0, 3.0], [3.0, 3.0]];
        LevelContext shared = Heatmaps.SharedContext(lower.SelectMany(r => r).Concat(upper.SelectMany(r => r)));
        Heatmap planeLower = Heatmaps.Matrix("z-1", "width (m)", "depth (m)", lower, LevelReference.RelativeToBandMean, shared);
        Heatmap planeUpper = Heatmaps.Matrix("z+1", "width (m)", "depth (m)", upper, LevelReference.RelativeToBandMean, shared);

        Assert.Equal(3.0, planeUpper.At(0, 0).LevelDb - planeLower.At(0, 0).LevelDb, 12);
        Assert.NotEqual(planeLower.At(0, 0).Colour, planeUpper.At(0, 0).Colour);
        // Comparable panels: the two planes carry the same scale, so "brighter" always means "louder".
        Assert.Equal(planeLower.Scale.MinDb, planeUpper.Scale.MinDb, 12);
        Assert.Equal(planeLower.Scale.MaxDb, planeUpper.Scale.MaxDb, 12);
        Assert.Equal(planeLower.Scale.Label, planeUpper.Scale.Label);

        Heatmap selfLower = Heatmaps.Matrix("z-1", "width (m)", "depth (m)", lower, LevelReference.RelativeToBandMean, Heatmaps.SharedContext(lower.SelectMany(r => r)));
        Heatmap selfUpper = Heatmaps.Matrix("z+1", "width (m)", "depth (m)", upper, LevelReference.RelativeToBandMean, Heatmaps.SharedContext(upper.SelectMany(r => r)));
        Assert.Equal(0.0, selfLower.At(0, 0).LevelDb, 12);                       // the negative twin: both planes 0 dB
        Assert.Equal(0.0, selfUpper.At(0, 0).LevelDb, 12);
        Assert.Equal(selfLower.At(0, 0).Colour, selfUpper.At(0, 0).Colour);      // and the same colour, i.e. the bug
    }

    [Fact]
    public void A_ragged_or_empty_matrix_is_refused_rather_than_drawn()
    {
        LevelContext context = new(0.0, 0.0);
        Assert.Throws<ArgumentException>(() => Heatmaps.Matrix("empty", "x", "y", [], LevelReference.RelativeToBandMean, context));
        Assert.Throws<ArgumentException>(() => Heatmaps.Matrix("ragged", "x", "y", [[0.0, 1.0], [2.0]], LevelReference.RelativeToBandMean, context));
        Assert.Throws<ArgumentException>(() => Heatmaps.Matrix("nan", "x", "y", [[double.NaN]], LevelReference.RelativeToBandMean, Heatmaps.SharedContext([double.NaN])));
        Assert.Throws<ArgumentException>(() => Heatmaps.SharedContext([]));
    }
}
