namespace AudioOptimizer.Optimization;

using AudioOptimizer.Core;

/// <summary>
/// Spread of the predicted level across the N measurement positions at one frequency. All values are dB.
/// <para>
/// Definitions, because these are headline numbers and must be executable rather than folklore:
/// P10/P90 use <b>nearest-rank</b> — the ascending level at index ceil(p/100·N) − 1, clamped to [0, N−1],
/// with <b>no interpolation</b>. P90−P10 is therefore always one of the measured values minus another.
/// The median is the middle order statistic for odd N and the arithmetic mean of the two central values
/// for even N (deliberately not nearest-rank P50, which would pick one of the two and drop the other).
/// <see cref="StdDevDb"/> is the <b>population</b> standard deviation (÷N), not the sample one: the N grid
/// positions are the whole population being optimised over, not a sample drawn from a larger one.
/// <see cref="MeanDb"/> is the arithmetic mean of the positions' dB levels, not the dB of the mean magnitude.
/// </para>
/// </summary>
public sealed record FrequencyMetrics(
    double FrequencyHz,
    double MeanDb,
    double MedianDb,
    double StdDevDb,
    double MinDb,
    double MaxDb,
    double RangeDb,
    double P10Db,
    double P90Db,
    double P90P10Db);

/// <summary>
/// Band aggregates of <see cref="FrequencyMetrics"/> across the band's frequencies. The five summary
/// numbers are means/maxima over the frequencies passed in; the band is the positions' own
/// <see cref="PositionResponse.AnalysisBand"/>, enforced when a position is constructed rather than requested here.
/// <para>
/// <see cref="MaxPeakAboveMeanDb" /> and <see cref="WorstNullDb" /> are measured against the <b>per-frequency
/// spatial mean</b>, which makes every number here invariant to a per-frequency level shift: raising or
/// lowering a frequency across all positions does not move the spread. That is what keeps the objective
/// measuring uniformity rather than level. The boost the max-boost limit constrains is a different,
/// measurement-relative quantity — see <see cref="ObjectiveFunction.MaxBoostVsBaselineDb"/>.
/// </para>
/// <para>
/// <see cref="AnalysisBand"/> states what every number here covers. It is carried rather than left implicit so a
/// consumer can report configured-versus-achieved without re-deriving the band from the session, and it is read from the
/// shared reference position the grid guard has already proved representative.
/// </para>
/// </summary>
public sealed record SpatialSummary(
    double MeanStdDevDb,
    double MeanRangeDb,
    double MeanP90P10Db,
    double WorstNullDb,
    double MaxPeakAboveMeanDb,
    IReadOnlyList<FrequencyMetrics> PerFrequency,
    FrequencyBand AnalysisBand);

/// <summary>Level spread across positions, per frequency and aggregated over the band. Pure functions, no state.</summary>
public static class SpatialMetrics
{
    /// <summary>
    /// Per-frequency and band-level spread of the predicted totals. Out-of-band bins cannot be constructed, so the
    /// band is enforced by the type rather than requested here; this method additionally refuses a set of positions
    /// that disagrees about its band or its frequency grid, because the level at index k is labelled from
    /// <c>totals[0]</c>'s k-th frequency and a different grid elsewhere would be mislabelled rather than merely mixed.
    /// Levels are in <b>dB</b> (20·log10|H|), never linear magnitude: the headline numbers in the spec are dB
    /// and a linear-domain average would be dominated by the loudest bins instead of by uniformity.
    /// </summary>
    public static SpatialSummary Compute(IReadOnlyList<PositionResponse> totals)
    {
        if (totals.Count == 0) throw new ArgumentException("A spatial summary needs at least one position.", nameof(totals));

        int binCount = totals[0].Bins.Count;
        if (binCount == 0) throw new ArgumentException("The positions carry no bins.", nameof(totals));
        for (int i = 1; i < totals.Count; i++)
            if (totals[i].Bins.Count != binCount)
                throw new ArgumentException($"Position {totals[i].PointId} has {totals[i].Bins.Count} bins, position {totals[0].PointId} has {binCount}.", nameof(totals));

        // One session is one sweep (Resume refuses a mismatched sweep), so positions from measured data always agree;
        // a synthesised producer can disagree, and it is cheaper to refuse than to mislabel.
        // DELEGATION PRECONDITION: comparing every position against positions[0] instead of against every j is complete
        // only because PositionGrid.SameGrid is an equality relation (reflexive, symmetric, transitive). Reuse it for a
        // non-symmetric comparison ("bins are a prefix of") and this shortcut silently starts missing pairs.
        // Position 0 is therefore a declared reference grid, which is what makes labelling level k from
        // totals[0].Bins[k].FrequencyHz sound rather than merely lucky.
        for (int i = 1; i < totals.Count; i++)
        {
            if (totals[i].AnalysisBand != totals[0].AnalysisBand)
                throw new ArgumentException($"Position {totals[i].PointId} covers {totals[i].AnalysisBand} but {totals[0].PointId} covers {totals[0].AnalysisBand}.", nameof(totals));
            if (!PositionGrid.SameGrid(totals[0], totals[i]))
            {
                int different = PositionGrid.FirstDifferentBin(totals[0], totals[i]);
                throw new ArgumentException($"Position {totals[i].PointId} bin {different} is {totals[i].Bins[different].FrequencyHz} Hz but {totals[0].PointId} has {totals[0].Bins[different].FrequencyHz} Hz.", nameof(totals));
            }
        }

        var perFrequency = new FrequencyMetrics[binCount];
        var levels = new double[totals.Count];
        double sumStdDev = 0.0, sumRange = 0.0, sumP90P10 = 0.0;
        double worstNull = 0.0, maxPeakAboveMean = 0.0;

        for (int k = 0; k < binCount; k++)
        {
            for (int i = 0; i < totals.Count; i++) levels[i] = totals[i].Bins[k].MagnitudeDb;

            FrequencyMetrics metrics = AtFrequency(levels, totals[0].Bins[k].FrequencyHz);
            perFrequency[k] = metrics;

            sumStdDev += metrics.StdDevDb;
            sumRange += metrics.RangeDb;
            sumP90P10 += metrics.P90P10Db;

            // How far the deepest position sits below that frequency's mean level, and how far the loudest
            // one sits above it. Both parenthesised the same way so neither can go negative from rounding.
            worstNull = Math.Max(worstNull, metrics.MeanDb - metrics.MinDb);
            maxPeakAboveMean = Math.Max(maxPeakAboveMean, metrics.MaxDb - metrics.MeanDb);
        }

        return new SpatialSummary(
            sumStdDev / binCount,
            sumRange / binCount,
            sumP90P10 / binCount,
            worstNull,
            maxPeakAboveMean,
            perFrequency,
            totals[0].AnalysisBand);      // the reference position the guard proved representative
    }

    /// <summary>Level spread across the N positions at one frequency.</summary>
    public static FrequencyMetrics AtFrequency(IReadOnlyList<double> levelsDb, double frequencyHz)
    {
        if (levelsDb.Count == 0) throw new ArgumentException("A frequency needs at least one position.", nameof(levelsDb));

        var sorted = new double[levelsDb.Count];
        for (int i = 0; i < levelsDb.Count; i++) sorted[i] = levelsDb[i];
        Array.Sort(sorted);

        double mean = Mean(sorted);
        double sumSquares = 0.0;
        foreach (double level in sorted)
        {
            double deviation = level - mean;
            sumSquares += deviation * deviation;
        }

        double p10 = PercentileNearestRank(sorted, 10.0);
        double p90 = PercentileNearestRank(sorted, 90.0);

        return new FrequencyMetrics(
            frequencyHz,
            mean,
            Median(sorted),
            Math.Sqrt(sumSquares / sorted.Length),
            sorted[0],
            sorted[^1],
            sorted[^1] - sorted[0],
            p10,
            p90,
            p90 - p10);
    }

    /// <summary>
    /// Nearest-rank percentile: the ascending level at index ceil(p/100·N) − 1, clamped into range, with no
    /// interpolation between neighbours. Hand-checked case: levels −6, −2, 0, 3, 9 (N=5) give
    /// P10 = ceil(0.5) − 1 = 0 → −6.0 and P90 = ceil(4.5) − 1 = 4 → 9.0, so P90 − P10 = 15.0.
    /// </summary>
    public static double PercentileNearestRank(IReadOnlyList<double> levelsDb, double percentile)
    {
        if (levelsDb.Count == 0) throw new ArgumentException("A percentile needs at least one value.", nameof(levelsDb));
        if (percentile <= 0.0 || percentile > 100.0) throw new ArgumentOutOfRangeException(nameof(percentile), percentile, "Percentile must be in (0, 100].");

        var sorted = new double[levelsDb.Count];
        for (int i = 0; i < levelsDb.Count; i++) sorted[i] = levelsDb[i];
        Array.Sort(sorted);

        return PercentileNearestRank(sorted, percentile);
    }

    /// <summary>Median: middle order statistic for odd N, mean of the two central values for even N.</summary>
    public static double Median(IReadOnlyList<double> levelsDb)
    {
        if (levelsDb.Count == 0) throw new ArgumentException("A median needs at least one value.", nameof(levelsDb));

        var sorted = new double[levelsDb.Count];
        for (int i = 0; i < levelsDb.Count; i++) sorted[i] = levelsDb[i];
        Array.Sort(sorted);

        return Median(sorted);
    }

    private static double PercentileNearestRank(double[] ascending, double percentile)
    {
        int rank = (int)Math.Ceiling(percentile / 100.0 * ascending.Length);
        return ascending[Math.Clamp(rank - 1, 0, ascending.Length - 1)];
    }

    private static double Median(double[] ascending)
    {
        int middle = ascending.Length / 2;
        return ascending.Length % 2 == 1
            ? ascending[middle]
            : (ascending[middle - 1] + ascending[middle]) * 0.5;
    }

    private static double Mean(double[] values)
    {
        double sum = 0.0;
        foreach (double value in values) sum += value;
        return sum / values.Length;
    }
}
