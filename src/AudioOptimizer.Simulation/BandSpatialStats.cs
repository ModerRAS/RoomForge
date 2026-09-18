namespace AudioOptimizer.Simulation;

using AudioOptimizer.Optimization;

/// <summary>
/// The band-level spatial numbers a report and a figure need, aggregated from
/// <see cref="SpatialSummary.PerFrequency"/> rather than recomputed: the engine already computes each frequency's
/// mean, median, σ, min, max, range, P10 and P90 over the positions, and these are the means/extremes of those across
/// the band. <see cref="StdDevDb"/> is the engine's own band σ (the mean of the per-frequency σ), not the σ of the
/// per-frequency means — one definition of "spatial spread", used everywhere.
/// </summary>
public sealed record BandSpatialStats(
    double MeanDb,
    double MedianDb,
    double StdDevDb,
    double MinDb,
    double MaxDb,
    double RangeDb,
    double P10Db,
    double P90Db,
    double P90P10Db)
{
    public static BandSpatialStats Of(SpatialSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        IReadOnlyList<FrequencyMetrics> rows = summary.PerFrequency;
        return new BandSpatialStats(
            rows.Average(row => row.MeanDb),
            SpatialMetrics.Median([.. rows.Select(row => row.MedianDb)]),
            summary.MeanStdDevDb,
            rows.Min(row => row.MinDb),
            rows.Max(row => row.MaxDb),
            rows.Max(row => row.MaxDb) - rows.Min(row => row.MinDb),
            rows.Min(row => row.P10Db),
            rows.Max(row => row.P90Db),
            rows.Max(row => row.P90Db) - rows.Min(row => row.P10Db));
    }
}
