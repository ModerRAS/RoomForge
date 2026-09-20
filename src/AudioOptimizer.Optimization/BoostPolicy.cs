namespace AudioOptimizer.Optimization;

/// <summary>
/// How the max-boost limit is interpreted, as two explicit modes rather than one number a caller has to remember to
/// shrink:
/// <list type="bullet">
/// <item><description>
/// <b><see cref="Capability"/></b> — the configured limit is used as-is. This is the DIAGNOSTIC mode: a large limit
/// lets the search show what the room could achieve (deep nulls, multiple local extrema, constraint conflicts) and
/// the theoretical optimum is exposed next to the recommendation. A result produced in this mode is never a product
/// recommendation by itself.
/// </description></item>
/// <item><description>
/// <b><see cref="ProductSafety"/></b> — the effective limit is never larger than <see cref="BoostPolicy.ProductSafetyMaxBoostDb"/>
/// (3.0 dB), whatever the configured number was. The final recommendation is verified against that limit from the
/// measurement, not merely trusted from the search: see <see cref="BoostPolicy.Verify"/>.
/// </description></item>
/// </list>
/// </summary>
public enum OptimizerOperatingMode
{
    /// <summary>Diagnostic: the configured limit is the search limit; the theoretical optimum is reported.</summary>
    Capability,

    /// <summary>User-facing: the effective limit is min(configured, 3.0 dB) and the final recommendation is verified against it.</summary>
    ProductSafety,
}

/// <summary>
/// The three roles a boosting decision has, kept as three separate values instead of one "best" that changes meaning
/// with context:
/// <list type="number">
/// <item><description><b>Theoretical optimum</b> — the best candidate the search found ignoring the product limit
/// (a capability-mode search's <see cref="OptimizerResult.Best"/>).</description></item>
/// <item><description><b>Legal candidate</b> — the best candidate inside the product limit (a product-mode search's
/// <see cref="OptimizerResult.Best"/>).</description></item>
/// <item><description><b>Final recommendation</b> — the setting the result actually returns, snapped to the report
/// grid (<see cref="OptimizerResult.Recommended"/>); null when nothing legal was found.</description></item>
/// </list>
/// A report that cannot say which of the three a number describes is the L10 reporting defect; this record is the
/// place the three are named.
/// </summary>
public sealed record BoostRoles(
    OptimizerCandidate? TheoreticalOptimum,
    OptimizerCandidate? LegalCandidate,
    SubwooferSetting? FinalRecommendation,
    double? TheoreticalBoostDb,
    double? LegalBoostDb,
    double? FinalRecommendationBoostDb);

/// <summary>
/// The product contract check, as data. <see cref="FinalRecommendationExceedsLimit"/> is the TRUE contract
/// violation: the final recommendation, re-measured from the input, spends more boost than the mode's effective
/// limit allows. That is a different statement from "the best candidate was rejected" — the search may reject as
/// many candidates as it likes while the recommendation it returns stays legal, and conversely a result can report
/// a flattering number while the returned setting is illegal.
/// <see cref="ReportedValueDescribesFinalRecommendation"/> pins the L10 semantics: the reported achieved boost must
/// describe the FINAL recommendation, not the best legal candidate the search happened to visit.
/// </summary>
public sealed record BoostContractReport(
    OptimizerOperatingMode Mode,
    double RequestedLimitDb,
    double EffectiveLimitDb,
    double? ReportedAchievedBoostDb,
    double? FinalRecommendationBoostDb,
    bool FinalRecommendationExceedsLimit,
    bool ReportedValueDescribesFinalRecommendation,
    string Status)
{
    /// <summary>True when the returned recommendation breaks the mode's contract. Never true because a candidate was rejected.</summary>
    public bool Violated => FinalRecommendationExceedsLimit;
}

/// <summary>
/// The mode and contract layer over <see cref="SubwooferOptimizer"/>: it does not change the search, it makes the
/// limit interpretation and the returned number checkable. Nothing here feeds the optimizer — <see cref="Verify"/>
/// re-derives the recommendation's boost from the measured <see cref="DualSubMeasurement"/> through
/// <see cref="SubwooferModel"/> and <see cref="ObjectiveFunction"/>, the same shipped helpers the search uses.
/// </summary>
public static class BoostPolicy
{
    /// <summary>The product-safety ceiling. The configured limit may be smaller; it may never be larger in product mode.</summary>
    public const double ProductSafetyMaxBoostDb = 3.0;

    /// <summary>Boost comparisons are exact up to this: both sides come from the same double arithmetic.</summary>
    public const double BoostEpsilonDb = 1e-9;

    /// <summary>The limit a mode is allowed to enforce: product safety clamps to 3.0, capability passes the configured value through.</summary>
    public static double EffectiveLimitDb(OptimizerOperatingMode mode, double configuredLimitDb)
    {
        if (!double.IsFinite(configuredLimitDb))
            throw new ArgumentOutOfRangeException(nameof(configuredLimitDb), configuredLimitDb, "The configured boost limit must be finite.");
        return mode == OptimizerOperatingMode.ProductSafety
            ? Math.Min(configuredLimitDb, ProductSafetyMaxBoostDb)
            : configuredLimitDb;
    }

    /// <summary>The optimizer options one mode actually runs with — the only sanctioned way to turn a mode into a search limit.</summary>
    public static OptimizerOptions ModeOptions(OptimizerOptions options, OptimizerOperatingMode mode)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options with { MaxBoostLimitDb = EffectiveLimitDb(mode, options.MaxBoostLimitDb) };
    }

    /// <summary>
    /// The achieved boost of one setting over the measured baseline, recomputed from the measurement. Null when the
    /// setting is null; exactly 0 when the setting is the measured (baseline) setting.
    /// </summary>
    public static double? AchievedBoostDb(DualSubMeasurement measurement, SubwooferSetting? setting)
    {
        ArgumentNullException.ThrowIfNull(measurement);
        if (setting is null) return null;

        IReadOnlyList<PositionResponse> baselineTotals = SubwooferModel.Combine(measurement, SubwooferSetting.Baseline);
        IReadOnlyList<PositionResponse> totals = SubwooferModel.Combine(measurement, setting);
        return ObjectiveFunction.MaxBoostVsBaselineDb(totals, baselineTotals);
    }

    /// <summary>
    /// Verifies a shipped result against its mode's contract, from the measurement the search was given. The check
    /// is deliberately independent of the search: it re-measures the <see cref="OptimizerResult.Recommended"/> setting
    /// and compares that number with the effective limit and with the number the result reports.
    /// </summary>
    public static BoostContractReport Verify(DualSubMeasurement measurement, OptimizerResult result, OptimizerOperatingMode mode)
    {
        ArgumentNullException.ThrowIfNull(measurement);
        ArgumentNullException.ThrowIfNull(result);

        double effectiveLimit = EffectiveLimitDb(mode, result.Constraint.MaxBoostLimitDb);
        double? finalBoost = AchievedBoostDb(measurement, result.Recommended);
        bool exceeds = finalBoost is { } boost && boost > effectiveLimit + BoostEpsilonDb;

        double? reported = result.Constraint.MaxAchievedBoostDb;
        bool describesFinal = reported is null && finalBoost is null
            || reported is { } r && finalBoost is { } f && Math.Abs(r - f) <= BoostEpsilonDb;

        string status;
        if (exceeds)
        {
            status = $"contract violation: the final recommendation achieves {finalBoost:F3} dB of boost against the "
                + $"{effectiveLimit:F3} dB {ModeName(mode)} limit (requested {result.Constraint.MaxBoostLimitDb:F3} dB).";
        }
        else if (!describesFinal)
        {
            status = $"report mismatch: the reported achieved boost ({Format(reported)} dB) does not describe the final "
                + $"recommendation ({Format(finalBoost)} dB); they are different candidates.";
        }
        else
        {
            status = $"contract holds: the final recommendation achieves {Format(finalBoost)} dB of boost against the "
                + $"{effectiveLimit:F3} dB {ModeName(mode)} limit; the reported value describes the final recommendation.";
        }

        return new BoostContractReport(
            mode,
            result.Constraint.MaxBoostLimitDb,
            effectiveLimit,
            reported,
            finalBoost,
            exceeds,
            describesFinal,
            status);
    }

    /// <summary>
    /// Runs both modes over one measurement and returns the three roles. Two searches by design: the theoretical
    /// optimum and the legal candidate are answers to different questions and one search cannot report both without
    /// the ambiguity this policy exists to remove.
    /// </summary>
    public static BoostRoles Compare(DualSubMeasurement measurement, OptimizerOptions capabilityOptions, OptimizerOptions productOptions)
    {
        ArgumentNullException.ThrowIfNull(measurement);
        ArgumentNullException.ThrowIfNull(capabilityOptions);
        ArgumentNullException.ThrowIfNull(productOptions);

        OptimizerResult capability = SubwooferOptimizer.Search(measurement, capabilityOptions);
        OptimizerResult product = SubwooferOptimizer.Search(measurement, productOptions);
        return new BoostRoles(
            capability.Best,
            product.Best,
            product.Recommended,
            capability.Best?.MaxBoostVsBaselineDb,
            product.Best?.MaxBoostVsBaselineDb,
            AchievedBoostDb(measurement, product.Recommended));
    }

    private static string ModeName(OptimizerOperatingMode mode)
        => mode == OptimizerOperatingMode.ProductSafety ? "product-safety" : "capability";

    private static string Format(double? value)
        => value is { } dB ? dB.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) : "none";
}
