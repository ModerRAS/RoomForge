namespace AudioOptimizer.Optimization;

/// <summary>How far the achieved spatial mean sits from the target curve, in dB. Absolute, plus the sign.</summary>
public sealed record TargetCurveError(double MeanAbsoluteDeviationDb, double MaxDeviationDb, double MeanSignedDeviationDb);

/// <summary>
/// The target curve, kept deliberately OUT of the spatial-uniformity objective: the two answer different
/// questions (is it even across the room vs is it the level you asked for). A single score would let a
/// uniform-but-wrong setting beat a slightly uneven but correctly-tilted one, so the optimizer never trades
/// one for the other — this is reported alongside the result and enters no score.
/// </summary>
public static class TargetCurve
{
    /// <summary>Deviation of the per-frequency spatial mean from the target curve, over the same bins.</summary>
    public static TargetCurveError Deviation(SpatialSummary summary, IReadOnlyList<double> targetDb)
    {
        if (targetDb.Count != summary.PerFrequency.Count)
            throw new ArgumentException($"The target curve has {targetDb.Count} points, the response has {summary.PerFrequency.Count} bins.", nameof(targetDb));

        double sumAbsolute = 0.0, sumSigned = 0.0, max = 0.0;
        for (int k = 0; k < summary.PerFrequency.Count; k++)
        {
            double deviation = summary.PerFrequency[k].MeanDb - targetDb[k];
            sumAbsolute += Math.Abs(deviation);
            sumSigned += deviation;
            max = Math.Max(max, Math.Abs(deviation));
        }

        return new TargetCurveError(sumAbsolute / summary.PerFrequency.Count, max, sumSigned / summary.PerFrequency.Count);
    }
}
