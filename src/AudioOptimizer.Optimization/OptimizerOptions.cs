namespace AudioOptimizer.Optimization;

/// <summary>
/// Every knob the search uses, as an explicit parameter. Search resolution (coarse/fine steps) is separate
/// from recommendable precision (the report steps): the search may refine more finely than any DSP can hold,
/// but only the report steps are ever put in front of a user.
/// </summary>
public sealed record OptimizerOptions
{
    public static readonly OptimizerOptions Default = new();

    // Search bounds. Gain A is fixed at 0 dB by design — only B is searched.
    public double GainMinDb { get; init; } = -6.0;
    public double GainMaxDb { get; init; } = 6.0;
    public double PhaseMinDegrees { get; init; } = 0.0;
    public double PhaseMaxDegrees { get; init; } = 180.0;

    // Staged search resolution, in the order the stages run.
    public double GainCoarseStepDb { get; init; } = 0.5;
    public double PhaseCoarseStepDegrees { get; init; } = 10.0;
    public double GainFineStepDb { get; init; } = 0.1;
    public double PhaseFineStepDegrees { get; init; } = 1.0;

    // What the hardware can actually hold, and therefore what a recommendation may say.
    public double ReportGainStepDb { get; init; } = 0.1;
    public double ReportPhaseStepDegrees { get; init; } = 1.0;

    // Coverage statement inputs. Product knowledge the search never reads: how many positions the session planned,
    // and how many the declared listening region has. Used only by MeasurementCoverage on the result.
    public int? SessionPointCount { get; init; }
    public int DeclaredRegionPointCount { get; init; }

    // Delay is a proportionally-phased parameter (exp(−j2πfΔt)) and is off unless asked for.
    public bool IncludeDelay { get; init; }
    public double DelayMaxMilliseconds { get; init; } = 10.0;
    public double DelayStepMilliseconds { get; init; } = 0.5;

    // The safety boundary on what may be returned (a hard rejection, filtered during the search), and the
    // preference inside it (w3·peakPenalty, the same measure, softened). The measure is the achieved boost:
    // the largest increase of the predicted |H_total| over the MEASURED (pre-optimization) |H_total|, in dB,
    // taken over every position and every frequency in the analysis band — not "the loudest position above
    // the spatial mean", which a setting can leave untouched while raising the whole room.
    public double MaxBoostLimitDb { get; init; } = 3.0;
    public ObjectiveWeights Weights { get; init; } = ObjectiveWeights.Default;

    // Verdict thresholds.
    public double MinimumScoreImprovementDb { get; init; } = 0.10;
    public double PositionDominatedThresholdDb { get; init; } = 0.10;

    /// <summary>Optional target curve, reported beside the result. It never enters the score.</summary>
    public IReadOnlyList<double>? TargetDb { get; init; }

    /// <summary>
    /// Two candidates whose scores differ by less than this are treated as equal, so the tie-break can
    /// prefer the one closest to the measured setting. Comfortably above float noise (~1e-13 dB here).
    /// </summary>
    public double ScoreTieEpsilonDb { get; init; } = 1e-9;

    public void Validate()
    {
        if (GainMaxDb <= GainMinDb) throw new ArgumentOutOfRangeException(nameof(GainMaxDb), GainMaxDb, $"GainMaxDb must exceed GainMinDb ({GainMinDb}).");
        if (PhaseMaxDegrees <= PhaseMinDegrees) throw new ArgumentOutOfRangeException(nameof(PhaseMaxDegrees), PhaseMaxDegrees, $"PhaseMaxDegrees must exceed PhaseMinDegrees ({PhaseMinDegrees}).");
        if (GainCoarseStepDb <= 0 || GainFineStepDb <= 0 || PhaseCoarseStepDegrees <= 0 || PhaseFineStepDegrees <= 0 || ReportGainStepDb <= 0 || ReportPhaseStepDegrees <= 0)
            throw new ArgumentOutOfRangeException(nameof(GainCoarseStepDb), "Search steps must all be > 0.");
        if (IncludeDelay && (DelayStepMilliseconds <= 0 || DelayMaxMilliseconds < 0))
            throw new ArgumentOutOfRangeException(nameof(DelayStepMilliseconds), DelayStepMilliseconds, "A delay search needs a positive step and a non-negative ceiling.");
        if (!double.IsFinite(MaxBoostLimitDb)) throw new ArgumentOutOfRangeException(nameof(MaxBoostLimitDb), MaxBoostLimitDb, "The boost limit must be finite.");
        if (MinimumScoreImprovementDb < 0) throw new ArgumentOutOfRangeException(nameof(MinimumScoreImprovementDb), MinimumScoreImprovementDb, "The improvement threshold must be >= 0 dB.");
        if (SessionPointCount is <= 0) throw new ArgumentOutOfRangeException(nameof(SessionPointCount), SessionPointCount, "A session size is either null (unknown) or a positive count.");
        if (DeclaredRegionPointCount < 0) throw new ArgumentOutOfRangeException(nameof(DeclaredRegionPointCount), DeclaredRegionPointCount, "The declared region size cannot be negative.");
        Weights.Validate();
    }
}
