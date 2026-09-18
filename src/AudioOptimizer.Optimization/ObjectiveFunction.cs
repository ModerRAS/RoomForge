namespace AudioOptimizer.Optimization;

/// <summary>
/// The weights of the spatial-uniformity score. Every weight is an explicit parameter with a documented
/// default — there is no constant hidden inside the objective, so a caller can see exactly what was traded
/// against what. Defaults: mean σ and mean P90−P10 are the primary terms (1.0 each, dB of spatial spread),
/// and <see cref="PeakPenalty"/> (boost used) and <see cref="NullPenalty"/> (worst dip left) are secondary
/// terms (0.5 each) that discourage spending the boost allowance just because it is allowed.
/// </summary>
public sealed record ObjectiveWeights(
    double MeanStdDev = 1.0,
    double MeanP90P10 = 1.0,
    double PeakPenalty = 0.5,
    double NullPenalty = 0.5)
{
    public static readonly ObjectiveWeights Default = new();

    public void Validate()
    {
        if (!double.IsFinite(MeanStdDev) || !double.IsFinite(MeanP90P10) || !double.IsFinite(PeakPenalty) || !double.IsFinite(NullPenalty))
            throw new ArgumentOutOfRangeException(nameof(MeanStdDev), "Objective weights must be finite.");
    }
}

/// <summary>The score's four additive terms, kept separate so any result can show what produced it.</summary>
public sealed record ObjectiveTerms(
    double MeanStdDevDb,
    double MeanP90P10Db,
    double PeakPenaltyDb,
    double NullPenaltyDb)
{
    public double Score(ObjectiveWeights weights)
        => weights.MeanStdDev * MeanStdDevDb
         + weights.MeanP90P10 * MeanP90P10Db
         + weights.PeakPenalty * PeakPenaltyDb
         + weights.NullPenalty * NullPenaltyDb;
}

/// <summary>
/// score = w1·meanStdDev + w2·meanP90P10 + w3·peakPenalty + w4·nullPenalty, all in dB, lower is better.
/// Every term is non-negative, so a score of 0 means a perfectly even response.
/// <para>
/// The score is <b>level-invariant</b>: σ, range and P90−P10 are translation-invariant in dB, and both
/// penalties are measured against a reference level rather than an absolute one. Raising or lowering the
/// whole response, or cancelling energy, therefore cannot move the score at all — uniformity is measured on
/// the per-frequency spread, not on magnitude. The level change that a setting causes is reported separately
/// (<see cref="OptimizerResult.LevelChangeDb"/>) so a level/uniformity trade is visible instead of hidden.
/// </para>
/// </summary>
public static class ObjectiveFunction
{
    /// <summary>
    /// peakPenalty is the boost the setting actually uses (0 when it does not boost); nullPenalty is the
    /// deepest dip it leaves below the per-frequency spatial mean. Both are relative, hence level-invariant.
    /// </summary>
    public static ObjectiveTerms Terms(SpatialSummary summary, double maxBoostVsBaselineDb)
        => new(summary.MeanStdDevDb, summary.MeanP90P10Db, Math.Max(0.0, maxBoostVsBaselineDb), summary.WorstNullDb);

    public static double Score(SpatialSummary summary, double maxBoostVsBaselineDb, ObjectiveWeights weights)
    {
        weights.Validate();
        return Terms(summary, maxBoostVsBaselineDb).Score(weights);
    }

    /// <summary>
    /// The achieved boost, and therefore the quantity the max-boost limit constrains: the largest increase of
    /// the predicted |H_total| over the <b>measured (pre-optimization)</b> |H_total|, in dB, taken over every
    /// position and every frequency in the analysis band. It is negative when the setting only attenuates.
    /// This is deliberately NOT "the loudest position above the spatial mean" — that is a uniformity measure
    /// (reported as <see cref="SpatialSummary.MaxPeakAboveMeanDb"/>) and a setting can raise the whole room by
    /// 6 dB without moving it. "Do not make my room louder than it is now by more than N dB" needs the
    /// reference to be the measurement, not the band mean.
    /// ponytail: a reference bin that is numerically zero (&lt; 1e-12, i.e. below −240 dB) is skipped — any
    /// energy is infinitely dB above a perfect null, and a measured null is never exactly zero.
    /// </summary>
    public static double MaxBoostVsBaselineDb(IReadOnlyList<PositionResponse> totals, IReadOnlyList<PositionResponse> baselineTotals)
    {
        if (totals.Count != baselineTotals.Count)
            throw new ArgumentException($"The candidate covers {totals.Count} positions, the baseline {baselineTotals.Count}.", nameof(baselineTotals));

        double max = double.NegativeInfinity;
        for (int i = 0; i < totals.Count; i++)
        {
            IReadOnlyList<Core.FrequencyResponse> candidate = totals[i].Bins;
            IReadOnlyList<Core.FrequencyResponse> reference = baselineTotals[i].Bins;
            if (candidate.Count != reference.Count)
                throw new ArgumentException($"Position {totals[i].PointId} has {candidate.Count} bins, the baseline has {reference.Count}.", nameof(baselineTotals));

            for (int k = 0; k < candidate.Count; k++)
            {
                double referenceMagnitude = Math.Sqrt(reference[k].Real * reference[k].Real + reference[k].Imag * reference[k].Imag);
                if (referenceMagnitude < 1e-12) continue;
                double candidateMagnitude = Math.Sqrt(candidate[k].Real * candidate[k].Real + candidate[k].Imag * candidate[k].Imag);
                max = Math.Max(max, 20.0 * Math.Log10(candidateMagnitude / referenceMagnitude));
            }
        }

        return double.IsNegativeInfinity(max) ? 0.0 : max;
    }

    /// <summary>
    /// The safety boundary, applied as a hard rejection while the search runs, never as a check on the winner:
    /// a penalty can be outvoted — a large uniformity gain can outweigh it and still come back above the limit,
    /// which breaks the promise the limit exists to make. A rejection cannot be outvoted.
    /// <see cref="ObjectiveWeights.PeakPenalty"/> still applies <em>inside</em> the limit: reject outside the
    /// region, penalise inside it.
    /// </summary>
    public static bool WithinBoostLimit(double maxBoostVsBaselineDb, double maxBoostLimitDb)
        => maxBoostVsBaselineDb <= maxBoostLimitDb;
}
