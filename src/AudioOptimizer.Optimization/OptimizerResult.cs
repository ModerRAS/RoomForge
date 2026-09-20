namespace AudioOptimizer.Optimization;

/// <summary>What the search concluded. <see cref="NegligibleImprovement"/> is a real answer, not a failure.</summary>
public enum OptimizationVerdict
{
    /// <summary>The recommended setting improves the score by at least the configured threshold.</summary>
    Improved,

    /// <summary>Nothing worth applying exists: the best achievable change is below the threshold.</summary>
    NegligibleImprovement,

    /// <summary>No candidate respected the boost limit, so nothing is returned. See <see cref="ConstraintReport"/>.</summary>
    NoSettingWithinBoostLimit,
}

/// <summary>
/// One evaluated setting, with the numbers it produced. Score is lower-is-better.
/// <see cref="MaxBoostVsBaselineDb"/> is the boost this setting achieves over the measured response — the
/// quantity the limit filters on (see <see cref="ObjectiveFunction.MaxBoostVsBaselineDb"/>).
/// </summary>
public sealed record OptimizerCandidate(
    SubwooferSetting Setting,
    SpatialSummary Summary,
    ObjectiveTerms Terms,
    double Score,
    double MaxBoostVsBaselineDb);

/// <summary>
/// What the boost limit did during the search, as data rather than a log line, plus the boost actually
/// achieved at the FINAL returned setting — the recommendation the hardware will hold, never the best legal
/// candidate the search visited, and never only a percentage or a claim. A constraint that filtered most of
/// the search and one that never fired are completely different outcomes: <see cref="Binding"/> means the
/// highest-scoring setting found while ignoring the limit was itself rejected, i.e. the room would have wanted
/// more boost than allowed. <see cref="LeastAchievedBoostDb"/> is the smallest boost any candidate achieved —
/// in the all-rejected case it says how far the room is from obeying the limit.
/// </summary>
public sealed record ConstraintReport(
    double MaxBoostLimitDb,
    int CandidatesEvaluated,
    int CandidatesRejected,
    bool Binding,
    double? MaxAchievedBoostDb,
    double LeastAchievedBoostDb);

/// <summary>
/// One row of the per-stage trace: the best legal setting after a named stage. Read top to bottom it is the
/// search's actual progress, and it makes the non-regression invariant checkable as data — a stage must never
/// score worse than the one before it.
/// </summary>
public sealed record OptimizerStage(string Stage, double BestScore, SubwooferSetting BestSetting);

/// <summary>
/// Per-frequency outcome of the best legal setting: how much of the spread at this frequency the parameters
/// could actually remove. <see cref="PositionDominated"/> means that improvement was below the threshold —
/// the spread here is room/position behaviour (modal behaviour, geometry) that gain, phase, polarity and
/// delay cannot fix, so it is not the sub alignment's fault and should not drive the recommendation.
/// </summary>
public sealed record FrequencyDiagnosis(double FrequencyHz, double BeforeStdDevDb, double AfterStdDevDb, double ImprovementDb, bool PositionDominated);

/// <summary>
/// The outcome of one optimisation. Before/After are absolute numbers, never percentages:
/// <see cref="After"/> is always the metrics of <see cref="Recommended"/> — the setting the hardware can
/// hold — never of an internal resolution the search happened to reach. <see cref="Best"/> is the best setting
/// found INSIDE the boost limit whether or not it was worth recommending (null when the limit rejected
/// everything). <see cref="LevelChangeDb"/> is reported because the objective is deliberately level-blind:
/// without it, a setting that makes the room quieter looks identical to one that makes it even.
/// </summary>
public sealed record OptimizerResult(
    OptimizationVerdict Verdict,
    SubwooferSetting? Recommended,
    OptimizerCandidate? Best,
    SpatialSummary Before,
    SpatialSummary After,
    ObjectiveTerms TermsBefore,
    ObjectiveTerms TermsAfter,
    double ScoreBefore,
    double ScoreAfter,
    double LevelChangeDb,
    ConstraintReport Constraint,
    IReadOnlyList<FrequencyDiagnosis> Diagnosis,
    IReadOnlyList<OptimizerStage> Trace,
    TargetCurveError? TargetBefore,
    TargetCurveError? TargetAfter,
    OptimizerOptions Options)
{
    /// <summary>
    /// The best candidate found ignoring the boost limit — the theoretical optimum, beside <see cref="Best"/>
    /// (the best candidate inside the limit) and <see cref="Recommended"/> (the final report-grid setting the
    /// hardware will hold). Three roles, three values: the limit and the report grid can make all three differ.
    /// </summary>
    public OptimizerCandidate? TheoreticalBest { get; init; }

    /// <summary>
    /// What the recommendation's spatial promise covers: only the measured positions, the whole planned session, or
    /// the whole declared region. A partial statement carries the required user-facing warning — see
    /// <see cref="MeasurementCoverage.MeasuredRegionOnlyWarning"/> — because nothing outside the measured positions
    /// is known and nothing is extrapolated.
    /// </summary>
    public MeasurementCoverage? Coverage { get; init; }
}
