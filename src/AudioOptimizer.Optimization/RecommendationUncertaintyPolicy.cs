namespace AudioOptimizer.Optimization;

/// <summary>
/// The outcome of an uncertainty-aware search: the shipped <see cref="OptimizerResult"/> plus the limit arithmetic
/// that produced it, so the difference between "the room needs more than allowed" and "the measurement could not
/// certify the room needed more" is a number in the result, not a sentence in a log.
/// </summary>
/// <param name="RequestedLimitDb">The limit the caller configured (the product promise).</param>
/// <param name="EnforcedLimitDb">The limit actually enforced: <see cref="RequestedLimitDb"/> minus the safety margin.</param>
/// <param name="MarginDecidedOutcome">True when the enforced limit bound the search: the highest-scoring candidate the search found (ignoring the limit) was itself rejected. Same fact as <see cref="ConstraintReport.Binding"/>; it does not by itself prove the outcome would differ without the margin — a candidate above the requested limit is rejected either way.</param>
public sealed record UncertaintyPolicyResult(
    OptimizerResult Result,
    double RequestedLimitDb,
    double EnforcedLimitDb,
    double SafetyMarginDb,
    double? FinalRecommendationBoostDb,
    bool MarginDecidedOutcome,
    string PolicySummary);

/// <summary>
/// The measurement-uncertainty policy this product ships: a <b>fixed safety margin</b> between the requested boost
/// limit and the limit the search is allowed to enforce. Of the three approaches studied (fixed margin; hysteresis /
/// tolerance band; repeated measurement / confidence estimate), this is the one chosen, because it is the only one
/// that is (a) one number, (b) memoryless — no state, no repeat runs, so it is reproducible per session and testable
/// analytically — and (c) conservative in the only direction a hard safety constraint may be conservative.
/// <para>
/// The chain it models: a measured boost carries seed-to-seed uncertainty from the measurement chain itself
/// (microphone self-noise, clock, capture alignment). A candidate whose measured boost sits within that uncertainty
/// of the limit therefore has an <em>unmeasurable</em> legality: different noise realizations declare the same
/// physical setting legal and illegal (E-F1: 29.96 dB true boost against a 30 dB limit, rejected by 3 of 10 seeds at
/// −60 dBFS). Enforcing the request exactly makes the recommendation a coin flip weighted by noise. The margin makes
/// the decision one-sided: a candidate must clear the requested limit by the margin before the product will
/// recommend it, so a room inside the margin is declined in every realization — stable, and conservative.
/// </para>
/// <para>
/// <b>NAMED PRODUCT LIMITATION — fixed safety margin.</b> The margin is not a fix for the measurement's uncertainty
/// and must never be described as one: it trades the correction away when the measurement cannot certify it. On E-F1
/// the canonical 2.0 ms alignment is declined in every noise realization at a 0.5 dB margin, sacrificing the
/// 2.1–2.3 dB true improvement the wave measured; the alternative (accepting it) is the unstable flip the margin
/// removes. Raising the limit instead is forbidden — the limit is the product's safety promise, not a tuning knob.
/// The limitation is deliberate and remains until the chain can estimate its own uncertainty (approach c) and the
/// search can consume it.
/// </para>
/// </summary>
public static class RecommendationUncertaintyPolicy
{
    /// <summary>
    /// The default allowance, in dB. Chosen so the enforced limit (requested − margin) sits below every measured
    /// realization of the E-F1 canonical correction: at the noisiest supported floor (−60 dBFS) that correction's
    /// measured boost spans 29.56–30.20 dB across independent re-runs (wave 29.552–30.173; auditor 29.560–30.202),
    /// its minimum ≈0.44 dB below the 30 dB limit, so a 0.5 dB margin leaves every realization above the enforced
    /// 29.5 dB and the verdict cannot flip with the seed. It is a policy constant, not a physical constant.
    /// </summary>
    public const double FixedSafetyMarginDb = 0.5;

    /// <summary>The limitation above, as a single string a report can print verbatim.</summary>
    public const string ProductLimitation =
        "PRODUCT LIMITATION (fixed safety margin): a recommendation must clear the requested boost limit by the "
        + "margin. A room whose true correction sits inside that margin (E-F1: 29.96 dB against 30 dB) is declined in "
        + "every noise realization; the improvement is deliberately sacrificed for a seed-independent verdict. This is "
        + "not a measurement-uncertainty fix and the limit is never widened to recover it.";

    /// <summary>
    /// Runs the shipped search with the requested limit reduced by <paramref name="safetyMarginDb"/>. A zero margin is
    /// exactly <see cref="SubwooferOptimizer.Search"/>: the enforced limit is bit-identical to the requested one, so
    /// the policy is opt-in and the shipped behaviour is unchanged by default.
    /// </summary>
    public static UncertaintyPolicyResult Search(
        DualSubMeasurement measurement,
        OptimizerOptions options,
        double safetyMarginDb = FixedSafetyMarginDb)
    {
        ArgumentNullException.ThrowIfNull(measurement);
        ArgumentNullException.ThrowIfNull(options);
        if (!double.IsFinite(safetyMarginDb) || safetyMarginDb < 0.0)
            throw new ArgumentOutOfRangeException(nameof(safetyMarginDb), safetyMarginDb, "The safety margin must be finite and >= 0 dB.");

        double requested = options.MaxBoostLimitDb;
        double enforced = requested - safetyMarginDb;
        OptimizerResult result = SubwooferOptimizer.Search(measurement, options with { MaxBoostLimitDb = enforced });
        double? finalBoost = BoostPolicy.AchievedBoostDb(measurement, result.Recommended);
        bool marginDecided = result.Constraint.Binding;

        string summary = safetyMarginDb == 0.0
            ? $"no safety margin: the requested limit ({requested:F3} dB) is enforced exactly."
            : marginDecided
                ? $"the enforced margin limit bound the search: the search required candidates to clear the requested {requested:F3} dB "
                    + $"by {safetyMarginDb:F3} dB, so the enforced limit was {enforced:F3} dB, and the best candidate found could not clear it."
                : $"the margin ({safetyMarginDb:F3} dB) was applied as an enforced limit of {enforced:F3} dB but did not decide the outcome.";

        return new UncertaintyPolicyResult(result, requested, enforced, safetyMarginDb, finalBoost, marginDecided, summary);
    }
}
