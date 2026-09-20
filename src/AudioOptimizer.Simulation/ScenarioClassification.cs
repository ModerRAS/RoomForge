namespace AudioOptimizer.Simulation;

/// <summary>
/// What one scenario's run is, in exactly one category. The classes are ordered by precedence — a hard failure is a
/// hard failure whatever else the numbers say — and every scenario lands in exactly one, so the counts add up to the
/// total:
/// <list type="number">
/// <item><description><see cref="HardFailure"/> — NaN/Inf, an invalid setting, a physical-identity break, or a crash.</description></item>
/// <item><description><see cref="ConstraintViolation"/> — the FINAL recommendation's achieved boost exceeds the
/// limit it was searched under. A product-contract violation, not a rejected candidate.</description></item>
/// <item><description><see cref="Regressed"/> — on any scenario, a recommendation that materially degraded the room
/// or a score ordering violation.</description></item>
/// <item><description><see cref="Improved"/> — a recommendation that moved the spatial σ by at least the materiality
/// threshold.</description></item>
/// <item><description><see cref="NearOptimalNoOp"/> — the room was already even (Before σ at or below the floor) and
/// the run materially changed nothing. The expected, healthy outcome for such a room.</description></item>
/// <item><description><see cref="Unchanged"/> — an actionable room where the recommendation moved σ by less than the
/// materiality threshold.</description></item>
/// </list>
/// </summary>
public enum ScenarioClass
{
    HardFailure,
    ConstraintViolation,
    Regressed,
    Improved,
    Unchanged,
    NearOptimalNoOp,
}

/// <summary>
/// One scenario's classification, with the reason as data. <see cref="Actionable"/> and
/// <see cref="NearOptimalNoOpExpected"/> are the partition labels (Before σ above / at-or-below the near-optimal
/// floor), independent of the outcome class; <see cref="Class"/> is the outcome.
/// </summary>
public sealed record ScenarioClassification(
    ScenarioClass Class,
    bool Actionable,
    bool NearOptimalNoOpExpected,
    double BeforeStdDevDb,
    double AfterStdDevDb,
    double ImprovementDb,
    IReadOnlyList<string> Reasons)
{
    /// <summary>True when the scenario's outcome is a failure of the run (hard, contract, or regression).</summary>
    public bool Failed => Class is ScenarioClass.HardFailure or ScenarioClass.ConstraintViolation or ScenarioClass.Regressed;
}

/// <summary>The classification totals over a run. Every field is a count over the ordered classifications, so the
/// summary is deterministic; <see cref="Total"/> equals the sum of the six class counts.</summary>
public sealed record ClassificationReport(
    int Total,
    int Actionable,
    int NearOptimal,
    int HardFailures,
    int ConstraintViolations,
    int Regressed,
    int Improved,
    int Unchanged,
    int NearOptimalNoOp)
{
    /// <summary>Scenarios whose outcome is a failure of the run.</summary>
    public int Failed => HardFailures + ConstraintViolations + Regressed;

    public static ClassificationReport Of(IEnumerable<ScenarioClassification> classifications)
    {
        ArgumentNullException.ThrowIfNull(classifications);
        List<ScenarioClassification> list = [.. classifications];
        return new ClassificationReport(
            list.Count,
            list.Count(classification => classification.Actionable),
            list.Count(classification => !classification.Actionable),
            list.Count(classification => classification.Class == ScenarioClass.HardFailure),
            list.Count(classification => classification.Class == ScenarioClass.ConstraintViolation),
            list.Count(classification => classification.Class == ScenarioClass.Regressed),
            list.Count(classification => classification.Class == ScenarioClass.Improved),
            list.Count(classification => classification.Class == ScenarioClass.Unchanged),
            list.Count(classification => classification.Class == ScenarioClass.NearOptimalNoOp));
    }
}

/// <summary>
/// Turns a judged scenario into one explicit class. The shipped judge answers "did the run pass?"; this answers
/// "what kind of run was it?", which is the question the 100/100-with-P90-0.00 dB summary hid: a run of 90 no-ops,
/// 10 improvements and 0 regressions is not the same report as 100 improvements, and neither is the same as any
/// hidden degradation.
/// <para>
/// Definitions, evidence-backed and deliberately not loosened:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>Actionable</b>: Before σ is strictly above <see cref="RegressionRunner.NearOptimalSigmaDb"/> (0.15 dB) — the
/// room gives the optimizer a job. <b>Near-optimal / no-op expected</b>: at or below it — there is nothing uniform
/// left to fix, so the only acceptable material outcome is none.
/// </description></item>
/// <item><description>
/// <b>Regressed</b> (this is the classification's definition, stricter than the shipped pass rule): a recommendation
/// was returned AND (After σ exceeds Before σ by more than
/// <see cref="RegressionRunner.OptimizableWorseningToleranceDb"/> (0.25 dB), OR the score got worse). The near-optimal
/// exemption is deliberately NOT applied to degradation: σ 0.05 → 0.45 dB is a 0.40 dB worsening and is caught, where
/// the shipped judge's normalizable pass window accepted it because the room started below the floor. Regression is
/// not "the optimizer found nothing"; acting on an actionable room and making it worse is.
/// </description></item>
/// <item><description>
/// <b>Improved</b>: a recommendation was returned and Before σ − After σ reaches <see cref="MeaningfulImprovementSigmaDb"/>
/// (0.10 dB — the optimizer's own materiality threshold, reused rather than invented here).
/// </description></item>
/// <item><description>
/// <b>Constraint violation</b>: <see cref="RegressionMetrics.BoostLimitViolated"/> — the final recommendation's
/// achieved boost passed the limit it was searched under. Kept separate from <see cref="Regressed"/> because the fix
/// is different: a contract bug, not a search-quality bug.
/// </description></item>
/// </list>
/// </summary>
public static class ScenarioClassifier
{
    /// <summary>σ movement below this is not worth calling an improvement: the optimizer's own materiality threshold, reused.</summary>
    public const double MeaningfulImprovementSigmaDb = 0.10;

    /// <summary>Classify a judged scenario, using the judge's own hard-failure list so the two layers cannot drift.</summary>
    public static ScenarioClassification Classify(ScenarioJudgement judgement)
    {
        ArgumentNullException.ThrowIfNull(judgement);
        return Classify(judgement.Metrics, judgement.Crashed, judgement.HardFailures);
    }

    /// <summary>Classify raw metrics; <paramref name="hardFailures"/> is the shipped judge's list when one exists.</summary>
    public static ScenarioClassification Classify(RegressionMetrics metrics, bool crashed = false, IReadOnlyList<string>? hardFailures = null)
    {
        ArgumentNullException.ThrowIfNull(metrics);

        double before = metrics.Before.StdDevDb;
        double after = metrics.After.StdDevDb;
        double improvement = before - after;
        bool actionable = before > RegressionRunner.NearOptimalSigmaDb;

        // Priority 1 — safety. A contract violation is NOT a generic hard failure: it gets its own category below,
        // so the shipped judge's boost hard-failure (when one is passed in) is moved to that category instead of
        // being counted twice.
        var carriedHardFailures = hardFailures ?? [];
        List<string> remainingHardFailures = [.. carriedHardFailures.Where(reason =>
            !(metrics.BoostLimitViolated && reason.StartsWith("boost limit violated", StringComparison.Ordinal)))];
        var physicalBreak = !(metrics.PhysicalResidualRatio <= RegressionRunner.PhysicalResidualToleranceRatio);
        if (crashed || remainingHardFailures.Count > 0 || metrics.NonFiniteValues || !metrics.ValidSetting || physicalBreak)
        {
            var reasons = new List<string>();
            if (crashed) reasons.Add("the scenario crashed");
            reasons.AddRange(remainingHardFailures);
            if (metrics.NonFiniteValues) reasons.Add("NaN or infinity in the optimizer result");
            if (!metrics.ValidSetting) reasons.Add("the recommended setting is invalid or outside the search range");
            if (physicalBreak) reasons.Add($"physical inconsistency: residual ratio {metrics.PhysicalResidualRatio:E3}");
            return new ScenarioClassification(ScenarioClass.HardFailure, actionable, !actionable, before, after, improvement, reasons);
        }

        // Priority 2 — product contract. The final recommendation exceeded the limit it was searched under.
        if (metrics.BoostLimitViolated)
        {
            return new ScenarioClassification(ScenarioClass.ConstraintViolation, actionable, !actionable, before, after, improvement,
                [$"product-contract violation: the final recommendation achieved {metrics.MaxAchievedBoostDb:F3} dB of a {metrics.MaxBoostLimitDb:F1} dB limit"]);
        }

        // Priority 3 — regression. Degradation is judged on the recommendation, at the same tolerance for every room:
        // a near-optimal room is no longer exempt from the worsening check (that exemption is exactly what swallowed
        // σ 0.05 → 0.45), it is only exempt from being *expected* to improve.
        bool recommendedSomething = metrics.RecommendedSomething;
        bool sigmaRegressed = recommendedSomething && after > before + RegressionRunner.OptimizableWorseningToleranceDb;
        bool scoreRegressed = metrics.ScoreAfter > metrics.ScoreBefore + RegressionMetrics.BoostEpsilonDb;
        if (sigmaRegressed || scoreRegressed)
        {
            var reasons = new List<string>();
            if (sigmaRegressed)
                reasons.Add($"optimization regression: spatial σ {before:F3} → {after:F3} dB "
                    + $"(+{after - before:F3} dB, tolerance {RegressionRunner.OptimizableWorseningToleranceDb:F2} dB)");
            if (scoreRegressed)
                reasons.Add($"score regression: {metrics.ScoreBefore:F4} → {metrics.ScoreAfter:F4} dB (the recommendation scores worse than the measurement)");
            return new ScenarioClassification(ScenarioClass.Regressed, actionable, !actionable, before, after, improvement, reasons);
        }

        // Priority 4 — improvement.
        if (recommendedSomething && improvement >= MeaningfulImprovementSigmaDb)
        {
            return new ScenarioClassification(ScenarioClass.Improved, actionable, !actionable, before, after, improvement,
                [$"improved: spatial σ {before:F3} → {after:F3} dB ({improvement:F3} dB, threshold {MeaningfulImprovementSigmaDb:F2} dB)"]);
        }

        // Priority 5 — near-optimal no-op (nothing was expected, nothing material happened).
        if (!actionable)
        {
            return new ScenarioClassification(ScenarioClass.NearOptimalNoOp, actionable, true, before, after, improvement,
                [$"near-optimal: Before σ {before:F3} dB is at or below the {RegressionRunner.NearOptimalSigmaDb:F2} dB floor and the run changed nothing material"]);
        }

        // Priority 6 — actionable room, recommendation too small to count.
        return new ScenarioClassification(ScenarioClass.Unchanged, actionable, false, before, after, improvement,
            [$"no material change: spatial σ {before:F3} → {after:F3} dB ({improvement:F3} dB below the {MeaningfulImprovementSigmaDb:F2} dB threshold)"]);
    }

    /// <summary>Classify a whole run in plan order and aggregate the counts.</summary>
    public static ClassificationReport ClassifyAll(IReadOnlyList<ScenarioJudgement> judgements)
    {
        ArgumentNullException.ThrowIfNull(judgements);
        return ClassificationReport.Of(judgements.Select(Classify));
    }
}
