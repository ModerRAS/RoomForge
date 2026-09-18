namespace AudioOptimizer.Visualization;

/// <summary>One cell of a heatmap: where it sits in the matrix and the level it encodes, already in the figure's
/// reference units, with the colour that level maps to.</summary>
public sealed record HeatmapCell(int Column, int Row, double LevelDb, RgbColour Colour);

/// <summary>
/// A colour-mapped matrix of levels — the measured grid on the plane at one height, or a frequency × position map.
/// <para>
/// The colour scale is built from the <see cref="LevelReference"/>, so the ramp's endpoints and its caption come from
/// the same place as the numbers, and the cells' levels are in that reference's units. A figure has exactly one
/// <see cref="LevelContext"/> for all of its cells (see <see cref="Heatmaps.Matrix"/>): referencing cells
/// individually would flatten a real level difference into the ramp and leave no labelled axis to notice it on.
/// </para>
/// </summary>
public sealed record Heatmap
{
    public Heatmap(
        string title,
        string xAxisLabel,
        string yAxisLabel,
        ColourScale scale,
        int columns,
        int rows,
        IReadOnlyList<HeatmapCell> cells,
        bool interpolated = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(xAxisLabel);
        ArgumentException.ThrowIfNullOrWhiteSpace(yAxisLabel);
        ArgumentNullException.ThrowIfNull(scale);
        ArgumentNullException.ThrowIfNull(cells);
        if (columns < 1 || rows < 1)
            throw new ArgumentOutOfRangeException(nameof(columns), columns, "A heatmap needs a positive matrix.");
        if (cells.Count != columns * rows)
            throw new ArgumentException($"A {columns} × {rows} heatmap needs {columns * rows} cells; got {cells.Count}.", nameof(cells));

        Title = title;
        XAxisLabel = xAxisLabel;
        YAxisLabel = yAxisLabel;
        Scale = scale;
        Columns = columns;
        Rows = rows;
        Cells = cells;
        Interpolated = interpolated;
    }

    public string Title { get; init; }

    public string XAxisLabel { get; init; }

    public string YAxisLabel { get; init; }

    /// <summary>Endpoints and caption, taken from the reference rather than from a constant.</summary>
    public ColourScale Scale { get; init; }

    public int Columns { get; init; }

    public int Rows { get; init; }

    public IReadOnlyList<HeatmapCell> Cells { get; init; }

    /// <summary>True when the cells are denser than the measured grid — the state §23 requires the view to label.</summary>
    public bool Interpolated { get; init; }

    public HeatmapCell At(int column, int row) => Cells[(row * Columns) + column];
}

/// <summary>
/// Builds heatmaps from levels. Pure and pixel-free, like the curve builders: the view turns cells into rectangles.
/// </summary>
public static class Heatmaps
{
    /// <summary>Default subdivision per measured interval for <see cref="Interpolated"/>.</summary>
    public const int DefaultInterpolationFactor = 4;

    /// <summary>
    /// The one context a figure uses for every cell and every heatmap of that figure. Deriving it per cell (or per
    /// height) would normalise each away from the others, so a difference that is really there would vanish into the
    /// ramp — a defect with no labelled axis to notice it on.
    /// </summary>
    public static LevelContext SharedContext(IEnumerable<double> levelsDb)
    {
        ArgumentNullException.ThrowIfNull(levelsDb);
        double[] levels = [.. levelsDb];
        if (levels.Length == 0) throw new ArgumentException("A heatmap needs at least one level.", nameof(levelsDb));
        foreach (double level in levels)
            if (!double.IsFinite(level)) throw new ArgumentException("A heatmap cannot encode a non-finite level.", nameof(levelsDb));
        return new LevelContext(levels.Average(), levels.Max());
    }

    /// <summary>
    /// One heatmap from a row-major matrix of raw levels (rows are the slower axis: grid Y, or position).
    /// </summary>
    public static Heatmap Matrix(
        string title,
        string xAxisLabel,
        string yAxisLabel,
        IReadOnlyList<IReadOnlyList<double>> rowsDb,
        LevelReference reference,
        LevelContext context,
        bool interpolated = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(rowsDb);
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(context);
        if (rowsDb.Count == 0) throw new ArgumentException("A heatmap needs at least one row.", nameof(rowsDb));

        int columns = rowsDb[0].Count;
        if (columns == 0) throw new ArgumentException("A heatmap needs at least one column.", nameof(rowsDb));
        foreach (IReadOnlyList<double> row in rowsDb)
            if (row.Count != columns)
                throw new ArgumentException($"Every row must have {columns} columns; found one with {row.Count}.", nameof(rowsDb));

        ColourScale scale = ColourScale.For(reference);
        var cells = new List<HeatmapCell>(rowsDb.Count * columns);
        for (int row = 0; row < rowsDb.Count; row++)
        {
            for (int column = 0; column < columns; column++)
            {
                double level = reference.ToReferenceUnits(rowsDb[row][column], context);
                cells.Add(new HeatmapCell(column, row, level, scale.ColourAt(level)));
            }
        }

        return new Heatmap(title, xAxisLabel, yAxisLabel, scale, columns, rowsDb.Count, cells, interpolated);
    }

    /// <summary>
    /// Bilinear subdivision of every measured interval, so the figure shows its interpolation instead of looking like
    /// measured resolution — the flag is what makes the view label it (§23).
    /// ponytail: linear in the cell corners only. If the room's modes need a smooth surface, the upgrade is a
    /// distance-weighted interpolation over the measured points (Shepard or thin-plate), which is a different claim.
    /// </summary>
    public static Heatmap Interpolated(Heatmap heatmap, int factor = DefaultInterpolationFactor)
    {
        ArgumentNullException.ThrowIfNull(heatmap);
        if (factor < 2) throw new ArgumentOutOfRangeException(nameof(factor), factor, "Interpolation needs at least a 2× subdivision.");
        if (heatmap.Columns < 2 || heatmap.Rows < 2) return heatmap with { Interpolated = true, Cells = heatmap.Cells };

        int columns = ((heatmap.Columns - 1) * factor) + 1;
        int rows = ((heatmap.Rows - 1) * factor) + 1;
        var cells = new List<HeatmapCell>(columns * rows);
        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < columns; column++)
            {
                double level = Sample(heatmap, column / (double)factor, row / (double)factor);
                cells.Add(new HeatmapCell(column, row, level, heatmap.Scale.ColourAt(level)));
            }
        }

        return heatmap with { Columns = columns, Rows = rows, Cells = cells, Interpolated = true };
    }

    /// <summary>Bilinear sample at a fractional cell coordinate.</summary>
    private static double Sample(Heatmap heatmap, double column, double row)
    {
        int c0 = Math.Min((int)Math.Floor(column), heatmap.Columns - 2);
        int r0 = Math.Min((int)Math.Floor(row), heatmap.Rows - 2);
        double fc = column - c0;
        double fr = row - r0;
        double top = (heatmap.At(c0, r0).LevelDb * (1.0 - fc)) + (heatmap.At(c0 + 1, r0).LevelDb * fc);
        double bottom = (heatmap.At(c0, r0 + 1).LevelDb * (1.0 - fc)) + (heatmap.At(c0 + 1, r0 + 1).LevelDb * fc);
        return (top * (1.0 - fr)) + (bottom * fr);
    }
}
