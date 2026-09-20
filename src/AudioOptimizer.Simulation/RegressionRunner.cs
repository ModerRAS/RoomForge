namespace AudioOptimizer.Simulation;

using System.Diagnostics;
using System.Numerics;
using System.Text;
using AudioOptimizer.Optimization;

/// <summary>
/// One scenario's verdict: the raw metrics plus the four judgement layers. Reasons live in
/// <see cref="HardFailures"/> (safety) and <see cref="Regressions"/> (the optimizer made things worse); a scenario
/// with neither passed.
/// </summary>
public sealed record ScenarioJudgement(
    RegressionScenario Scenario,
    RegressionMetrics Metrics,
    bool Optimizable,
    bool ClearlyWorse,
    bool Crashed,
    IReadOnlyList<string> HardFailures,
    IReadOnlyList<string> Regressions,
    string? CrashDetail = null)
{
    public bool Passed => HardFailures.Count == 0 && Regressions.Count == 0;

    public bool RegisteredAsFixture => RegressionFixtures.Contains(Scenario.Seed);

    /// <summary>The failure block a run prints: seed, full config, the replay command, then the reason.</summary>
    public string FailureReport()
    {
        var text = new StringBuilder();
        foreach (string reason in HardFailures)
            text.AppendLine($"FAIL       seed={Scenario.Seed} index={Scenario.Index} {reason}");
        foreach (string reason in Regressions)
            text.AppendLine($"REGRESSION seed={Scenario.Seed} index={Scenario.Index} {reason}");

        text.AppendLine($"  config: {Metrics.DescribeConfig()}");
        text.AppendLine($"  replay: {Scenario.ReplayCommand}");
        if (CrashDetail is { } detail)
            text.AppendLine($"  exception: {detail}");
        return text.ToString();
    }
}

/// <summary>
/// The aggregation of a run. Every count is over the ordered judgements, so the summary is deterministic regardless
/// of how the scenarios were scheduled. Headline improvements are Before σ − After σ per scenario.
/// </summary>
public sealed record RegressionReport(IReadOnlyList<ScenarioJudgement> Scenarios)
{
    public double RuntimeMilliseconds { get; init; }

    public int Count => Scenarios.Count;

    public int PassedCount => Scenarios.Count(judgement => judgement.Passed);

    public bool AllPassed => PassedCount == Count && Count > 0;

    public int HardFailureCount => Scenarios.Sum(judgement => judgement.HardFailures.Count);

    public int OptimizationRegressionCount => Scenarios.Sum(judgement => judgement.Regressions.Count);

    public int NonFiniteCount => Scenarios.Count(judgement => judgement.Metrics.NonFiniteValues);

    public int BoostViolationCount => Scenarios.Count(judgement => judgement.Metrics.BoostLimitViolated);

    public int CrashCount => Scenarios.Count(judgement => judgement.Crashed);

    public double MedianImprovementDb => SpatialMetrics.Median([.. Scenarios.Select(judgement => judgement.Metrics.ImprovementDb)]);

    public double P90ImprovementDb => SpatialMetrics.PercentileNearestRank([.. Scenarios.Select(judgement => judgement.Metrics.ImprovementDb)], 90.0);

    /// <summary>
    /// The explicit split of the run: total, actionable vs near-optimal, and what each run actually did. The pass
    /// count alone is not the report — a run of 90 no-ops and 10 improvements is a different product fact.
    /// </summary>
    public ClassificationReport Classification => ScenarioClassifier.ClassifyAll(Scenarios);

    /// <summary>The compact summary block: counts, the improvement distribution, and the dimensions broken down.</summary>
    public string Format()
    {
        ArgumentNullException.ThrowIfNull(Scenarios);
        ScenarioJudgement worst = Scenarios.OrderBy(judgement => judgement.Metrics.ImprovementDb).First();
        string worstLine = worst.Metrics.ImprovementDb < 0.0
            ? $"{worst.Metrics.ImprovementDb,7:F2} dB (seed {worst.Scenario.Seed}, index {worst.Scenario.Index})"
            : "none";

        var text = new StringBuilder();
        text.AppendLine($"Scenarios:                {Count}");
        text.AppendLine($"Passed:                   {PassedCount}");
        text.AppendLine($"Hard failures:            {HardFailureCount}");
        text.AppendLine($"Optimization regressions: {OptimizationRegressionCount}");
        text.AppendLine($"NaN-Inf:                  {NonFiniteCount}");
        text.AppendLine($"Boost violations:         {BoostViolationCount}");
        text.AppendLine($"Crashes:                  {CrashCount}");
        text.AppendLine($"Median spatial improvement: {MedianImprovementDb,7:F2} dB");
        text.AppendLine($"P90:                      {P90ImprovementDb,7:F2} dB");
        text.AppendLine($"Worst regression:         {worstLine}");
        text.AppendLine($"Optimizable rooms:        {Scenarios.Count(judgement => judgement.Optimizable)} (Before σ > {RegressionRunner.NearOptimalSigmaDb:F2} dB)");
        text.AppendLine($"Near-optimal rooms:       {Scenarios.Count(judgement => !judgement.Optimizable)}");

        ClassificationReport classification = Classification;
        text.AppendLine($"Classified:               {classification.Improved} improved, {classification.Unchanged} unchanged, "
            + $"{classification.NearOptimalNoOp} near-optimal no-ops, {classification.Regressed} regressed, "
            + $"{classification.ConstraintViolations} contract violations, {classification.HardFailures} hard failures");

        text.AppendLine("By draw family:");
        foreach (RegressionAlignmentFamily family in Enum.GetValues<RegressionAlignmentFamily>())
            AppendDimension(text, $"  {family,-12}", Scenarios.Where(j => j.Metrics.Family == family));

        text.AppendLine("By imperfection class:");
        foreach (RegressionImperfection imperfection in Enum.GetValues<RegressionImperfection>())
            AppendDimension(text, $"  {imperfection,-9}", Scenarios.Where(j => j.Metrics.Imperfection?.Class == imperfection));

        text.AppendLine("By clock drift:");
        AppendDimension(text, "  |ppm| <= 5 ", Scenarios.Where(j => Math.Abs(ClockPpm(j)) <= 5.0));
        AppendDimension(text, "  |ppm| <= 20", Scenarios.Where(j => Math.Abs(ClockPpm(j)) is > 5.0 and <= 20.0));
        AppendDimension(text, "  |ppm| > 20 ", Scenarios.Where(j => Math.Abs(ClockPpm(j)) > 20.0));

        text.AppendLine("By noise floor:");
        foreach (double floor in Scenarios.Select(j => j.Metrics.Imperfection?.NoiseFloorDb ?? double.NaN).Distinct().OrderDescending())
            AppendDimension(text, $"  {floor,7:0.#} dBFS", Scenarios.Where(j => j.Metrics.Imperfection?.NoiseFloorDb == floor));

        text.AppendLine("By microphone deviation:");
        AppendDimension(text, "  |dB| <= 0.5", Scenarios.Where(j => Math.Abs(MicrophoneDeviation(j)) <= 0.5));
        AppendDimension(text, "  |dB| <= 1.0", Scenarios.Where(j => Math.Abs(MicrophoneDeviation(j)) is > 0.5 and <= 1.0));
        AppendDimension(text, "  |dB| > 1.0 ", Scenarios.Where(j => Math.Abs(MicrophoneDeviation(j)) > 1.0));

        foreach (ScenarioJudgement judgement in Scenarios.Where(judgement => !judgement.Passed))
            text.Append(judgement.FailureReport());

        return text.ToString();
    }

    private static double ClockPpm(ScenarioJudgement judgement) => judgement.Metrics.Imperfection?.ClockPpm ?? 0.0;

    private static double MicrophoneDeviation(ScenarioJudgement judgement) => judgement.Metrics.Imperfection?.MicrophoneDeviationDb ?? 0.0;

    private static void AppendDimension(StringBuilder text, string label, IEnumerable<ScenarioJudgement> group)
    {
        var list = group.ToList();
        if (list.Count == 0) return;

        double median = SpatialMetrics.Median([.. list.Select(judgement => judgement.Metrics.ImprovementDb)]);
        text.AppendLine($"{label} n={list.Count,-3} passed={list.Count(judgement => judgement.Passed),-3} median improvement {median,6:F2} dB");
    }
}

/// <summary>
/// The randomized optimizer regression lab's runner and judge.
/// <para>
/// One scenario goes through the SAME chain a fixed scenario does — <c>PointMeasurement.Run</c> for every point,
/// then the shipped <see cref="SubwooferOptimizer"/> on the measured A/B/AB complex responses — with no ground truth
/// on the input path. The judgement has four layers:
/// </para>
/// <list type="number">
/// <item><description>
/// <b>A — safety:</b> NaN/Inf anywhere in the result, an invalid or out-of-API-range recommendation, a boost-limit
/// violation, or an exception is a hard failure; each is caught per scenario and reported with seed and config.
/// </description></item>
/// <item><description>
/// <b>B — physical consistency:</b> the measured AB must be the complex sum of the measured A and B. The check uses
/// the MEDIAN of |AB−(A+B)| over all positions and bins divided by the median response scale, with tolerance
/// <see cref="PhysicalResidualToleranceRatio"/>. It cannot flake: with the noise off the residual is float round-off
/// (~1e-9 relative); with a noise floor each of the three captures carries its own realization, so the residual's
/// median is a stable estimate of the floor's contribution — a median over thousands of bins moves by a few percent
/// between seeds, not by a factor, unlike a max which has an unbounded upper tail.
/// </description></item>
/// <item><description>
/// <b>C — optimization:</b> a scenario is optimizable when Before σ is clearly above the near-optimal floor
/// (<see cref="NearOptimalSigmaDb"/>); only then is After required to stay within
/// <see cref="OptimizableWorseningToleranceDb"/> of Before. An already-even room is never asked to improve.
/// </description></item>
/// <item><description>
/// <b>D — regression:</b> a clearly worse After (<see cref="ClearlyWorseSigmaDb"/>) fails the run and names the seed
/// to register in <see cref="RegressionFixtures"/>; a score that got worse at all is also a regression, because it is
/// the optimizer's own ordering being violated.
/// </description></item>
/// </list>
/// </summary>
public static class RegressionRunner
{
    /// <summary>Before σ above this is an optimizable room; below it the room is already near-optimal.</summary>
    public const double NearOptimalSigmaDb = 0.15;

    /// <summary>On an optimizable room, After may exceed Before σ by at most this before it is a failure.</summary>
    public const double OptimizableWorseningToleranceDb = 0.25;

    /// <summary>A σ worsening beyond this is "clearly worse": a regression that must be registered as a fixture.</summary>
    public const double ClearlyWorseSigmaDb = 0.50;

    /// <summary>
    /// Complex-domain physical-identity bound. See the class remarks; 0.5 is orders of magnitude above the median
    /// noise contribution at every generated floor (−120…−60 dBFS) while a swapped or mis-paired capture is O(1).
    /// </summary>
    public const double PhysicalResidualToleranceRatio = 0.5;

    /// <summary>
    /// Worker count for one plan: full logical width, deliberately. A generated scenario is ~10–16 s of mostly-FFT
    /// CPU and this loop is the only place the plan's work can spread out, so it is left at
    /// <see cref="Environment.ProcessorCount"/> rather than capped. Measured on this host (a 16-logical-core box
    /// whose effective CPU share was ~1.3 cores): all 16 workers were scheduled, each scenario's own runtime inflated
    /// to ~134 s while the wall clock stayed quota-bound, and the FFT underneath is MathNet 5.0.0's
    /// <c>Fourier</c> — its per-call <c>Control.TryUseNative()</c> resolves providers under a global lock, so
    /// concurrent Fourier calls serialize here. That is a property of the package/host, not of this loop: on a
    /// machine/package where the FFT is not lock-bound the same code scales across cores, and no workaround is baked
    /// in. Determinism does not depend on the count — <c>Run</c> aggregates in plan order.
    /// </summary>
    public static int PlanParallelism => Environment.ProcessorCount;

    /// <summary>
    /// Runs a plan. Scenarios are parallelized; the aggregation is ordered, so the report is deterministic.
    /// </summary>
    public static RegressionReport Run(RegressionMode mode)
    {
        IReadOnlyList<RegressionScenario> plan = RandomScenarioGenerator.Plan(mode);
        var judgements = new ScenarioJudgement[plan.Count];
        var stopwatch = Stopwatch.StartNew();
        Parallel.For(0, plan.Count,
            new ParallelOptions { MaxDegreeOfParallelism = PlanParallelism },
            index => judgements[index] = RunScenario(plan[index]));
        stopwatch.Stop();

        return new RegressionReport(judgements) { RuntimeMilliseconds = stopwatch.Elapsed.TotalMilliseconds };
    }

    /// <summary>Runs and judges one scenario end to end, catching everything so one bad draw cannot kill a run.</summary>
    public static ScenarioJudgement RunScenario(RegressionScenario scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            VirtualLab lab = scenario.Scenario.CreateLab();
            var measureWatch = Stopwatch.StartNew();
            IReadOnlyList<SimulatedMeasurement> measurements = lab.MeasureAll();
            measureWatch.Stop();

            var searchWatch = Stopwatch.StartNew();
            DualSubMeasurement input = lab.AsOptimizerInput(measurements);
            OptimizerResult result = SubwooferOptimizer.Search(input, scenario.Scenario.Optimize);
            searchWatch.Stop();
            stopwatch.Stop();

            double physicalRatio = PhysicalResidualRatio(input);
            RegressionMetrics metrics = MetricsOf(scenario, result, input, physicalRatio,
                measureWatch.Elapsed.TotalMilliseconds, searchWatch.Elapsed.TotalMilliseconds, stopwatch.Elapsed.TotalMilliseconds);
            return Judge(scenario, metrics);
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            RegressionMetrics metrics = MetricsOf(scenario, exception, stopwatch.Elapsed.TotalMilliseconds);
            string reason = $"crashed: {exception.GetType().Name}: {exception.Message}";
            return new ScenarioJudgement(scenario, metrics, Optimizable: false, ClearlyWorse: false, Crashed: true,
                HardFailures: [reason], Regressions: [], CrashDetail: exception.ToString());
        }
    }

    /// <summary>
    /// The four judgement layers over one scenario's metrics. Pure: the test file drives it with synthetic metrics.
    /// </summary>
    public static ScenarioJudgement Judge(RegressionScenario scenario, RegressionMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(metrics);

        var hardFailures = new List<string>();
        if (metrics.NonFiniteValues)
            hardFailures.Add("NaN or infinity in the optimizer result");
        if (!metrics.ValidSetting)
            hardFailures.Add("recommended setting is invalid or outside the optimizer's own search range");
        if (metrics.BoostLimitViolated)
            hardFailures.Add($"boost limit violated: achieved {metrics.MaxAchievedBoostDb:F3} dB > allowed {metrics.MaxBoostLimitDb:F1} dB");
        if (!(metrics.PhysicalResidualRatio <= PhysicalResidualToleranceRatio))
            hardFailures.Add($"physical inconsistency: median |AB−(A+B)| / median scale = {metrics.PhysicalResidualRatio:F4} > {PhysicalResidualToleranceRatio:F2}");

        bool optimizable = metrics.Before.StdDevDb > NearOptimalSigmaDb;
        bool clearlyWorse = metrics.RecommendedSomething && metrics.ImprovementDb < -ClearlyWorseSigmaDb;

        var regressions = new List<string>();
        if (metrics.ScoreAfter > metrics.ScoreBefore + RegressionMetrics.BoostEpsilonDb)
            regressions.Add($"score regression: {metrics.ScoreBefore:F4} dB → {metrics.ScoreAfter:F4} dB (a recommendation that scores worse than the measurement)");
        if (optimizable && metrics.After.StdDevDb > metrics.Before.StdDevDb + OptimizableWorseningToleranceDb)
            regressions.Add($"optimization regression: spatial σ {metrics.Before.StdDevDb:F3} → {metrics.After.StdDevDb:F3} dB "
                + $"(+{metrics.After.StdDevDb - metrics.Before.StdDevDb:F3} dB, tolerance {OptimizableWorseningToleranceDb:F2} dB)");
        if (clearlyWorse)
        {
            string action = RegressionFixtures.Contains(scenario.Seed)
                ? "seed is already a checked-in fixture"
                : $"add seed {scenario.Seed} to RegressionFixtures.Seeds and replay with --replay {scenario.Seed}";
            regressions.Add($"optimization regression (clearly worse): spatial σ {metrics.Before.StdDevDb:F3} → {metrics.After.StdDevDb:F3} dB "
                + $"({metrics.ImprovementDb:F3} dB); {action}");
        }

        return new ScenarioJudgement(scenario, metrics, optimizable, clearlyWorse, Crashed: false, hardFailures, regressions);
    }

    /// <summary>True when any value is NaN or infinite — the layer-A scan over every number a result carries.</summary>
    public static bool AnyNonFinite(params double[] values) => values.Any(value => !double.IsFinite(value));

    /// <summary>True when a returned setting is invalid or outside the search box it claims to have searched.</summary>
    public static bool SettingOutOfRange(SubwooferSetting? setting, OptimizerOptions? options)
    {
        if (setting is null) return false;
        try
        {
            setting.Validate();
        }
        catch (ArgumentOutOfRangeException)
        {
            return true;
        }

        OptimizerOptions resolved = options ?? OptimizerOptions.Default;
        const double epsilon = 1e-9;
        if (setting.GainDb < resolved.GainMinDb - epsilon || setting.GainDb > resolved.GainMaxDb + epsilon) return true;
        if (setting.PhaseDegrees < resolved.PhaseMinDegrees - epsilon || setting.PhaseDegrees > resolved.PhaseMaxDegrees + epsilon) return true;
        if (setting.DelaySeconds < -epsilon) return true;
        if (!resolved.IncludeDelay && setting.DelaySeconds > epsilon) return true;
        if (resolved.IncludeDelay && setting.DelaySeconds > (resolved.DelayMaxMilliseconds / 1000.0) + epsilon) return true;
        return false;
    }

    /// <summary>Layer B: median |AB−(A+B)| over the median per-bin response scale. See the class remarks.</summary>
    public static double PhysicalResidualRatio(DualSubMeasurement measurement)
    {
        ArgumentNullException.ThrowIfNull(measurement);
        measurement.Validate();
        if (measurement.AB is not { Count: > 0 } measured) return double.PositiveInfinity;

        int binCount = measured[0].Bins.Count;
        if (binCount == 0) return double.PositiveInfinity;

        var residuals = new List<double>(measured.Count * binCount);
        var scales = new List<double>(measured.Count * binCount);

        for (int i = 0; i < measured.Count; i++)
        {
            for (int k = 0; k < measured[i].Bins.Count; k++)
            {
                var a = new Complex(measurement.A[i].Bins[k].Real, measurement.A[i].Bins[k].Imag);
                var b = new Complex(measurement.B[i].Bins[k].Real, measurement.B[i].Bins[k].Imag);
                var ab = new Complex(measured[i].Bins[k].Real, measured[i].Bins[k].Imag);

                residuals.Add(Complex.Abs(ab - a - b));
                scales.Add(Math.Max(Complex.Abs(ab), Math.Max(Complex.Abs(a), Complex.Abs(b))));
            }
        }

        double scale = SpatialMetrics.Median(scales);
        return SpatialMetrics.Median(residuals) / Math.Max(scale, 1e-300);
    }

    /// <summary>Layer C's optimizable test, exposed so the unit cases read as the rule rather than restating it.</summary>
    public static bool IsOptimizable(double beforeStdDevDb) => beforeStdDevDb > NearOptimalSigmaDb;

    /// <summary>Layer D's "clearly worse" test on a Before σ − After σ improvement.</summary>
    public static bool IsClearlyWorse(double improvementDb) => improvementDb < -ClearlyWorseSigmaDb;

    private static RegressionMetrics MetricsOf(RegressionScenario scenario, OptimizerResult result, DualSubMeasurement input,
        double physicalRatio, double measureMilliseconds, double optimizerMilliseconds, double runtimeMilliseconds)
    {
        bool nonFinite = AnyNonFinite(
            result.ScoreBefore, result.ScoreAfter, result.LevelChangeDb,
            result.Constraint.MaxBoostLimitDb, result.Constraint.MaxAchievedBoostDb ?? 0.0,
            result.Recommended?.GainDb ?? 0.0, result.Recommended?.PhaseRad ?? 0.0, result.Recommended?.DelaySeconds ?? 0.0)
            || !FiniteStats(result.Before) || !FiniteStats(result.After);

        var before = BandSpatialStats.Of(result.Before);
        var after = BandSpatialStats.Of(result.After);
        bool recommendedSomething = result.Recommended is { } recommended && recommended != SubwooferSetting.Baseline;

        (double? targetBefore, double? targetAfter) = TargetErrors(scenario, result);
        (double? gainError, int? polarityMismatch, double? phaseError, double? delayError) = ReferenceErrors(scenario, result.Recommended);
        (double? referenceStdDev, double? referenceScore, double? referenceBoost) = ReferenceOutcome(scenario, input, result);

        return new RegressionMetrics
        {
            Index = scenario.Index,
            Seed = scenario.Seed,
            ScenarioId = scenario.Scenario.Id,
            Title = scenario.Scenario.Title,
            RoomLengthMetres = scenario.Room.LengthMetres,
            RoomWidthMetres = scenario.Room.WidthMetres,
            RoomHeightMetres = scenario.Room.HeightMetres,
            ImageSourceOrder = scenario.Config.ImageSourceOrder,
            Layout = scenario.Layout,
            Family = scenario.Family,
            SubA = scenario.SubA,
            SubB = scenario.SubB,
            Imperfection = scenario.Imperfection,
            TargetCurve = scenario.TargetCurve,
            GainErrorDb = gainError,
            PolarityMismatch = polarityMismatch,
            PhaseErrorDegrees = phaseError,
            DelayErrorMilliseconds = delayError,
            Before = before,
            After = after,
            ScoreBefore = result.ScoreBefore,
            ScoreAfter = result.ScoreAfter,
            LevelChangeDb = result.LevelChangeDb,
            MaxAchievedBoostDb = result.Constraint.MaxAchievedBoostDb ?? 0.0,
            MaxBoostLimitDb = result.Constraint.MaxBoostLimitDb,
            RecommendedSomething = recommendedSomething,
            Verdict = result.Verdict,
            Recommended = result.Recommended,
            GroundTruth = scenario.Scenario.GroundTruthSetting,
            BestScoreDb = result.Best?.Score,
            Best = result.Best?.Setting,
            CandidatesEvaluated = result.Constraint.CandidatesEvaluated,
            CandidatesRejected = result.Constraint.CandidatesRejected,
            BoostBinding = result.Constraint.Binding,
            GroundTruthReferenceStdDevDb = referenceStdDev,
            GroundTruthReferenceScoreDb = referenceScore,
            GroundTruthReferenceBoostDb = referenceBoost,
            TargetErrorBeforeDb = targetBefore,
            TargetErrorAfterDb = targetAfter,
            BandMeanLevelBeforeDb = result.Before.PerFrequency.Average(row => row.MeanDb),
            PhysicalResidualRatio = physicalRatio,
            NonFiniteValues = nonFinite,
            ValidSetting = !SettingOutOfRange(result.Recommended, result.Options),
            MeasureRuntimeMilliseconds = measureMilliseconds,
            OptimizerRuntimeMilliseconds = optimizerMilliseconds,
            RuntimeMilliseconds = runtimeMilliseconds,
        };
    }

    /// <summary>The crash shapes carry no result; identity plus the crash facts is all the report needs.</summary>
    private static RegressionMetrics MetricsOf(RegressionScenario scenario, Exception _, double runtimeMilliseconds) => new()
    {
        Index = scenario.Index,
        Seed = scenario.Seed,
        ScenarioId = scenario.Scenario.Id,
        Title = scenario.Scenario.Title,
        RoomLengthMetres = scenario.Room.LengthMetres,
        RoomWidthMetres = scenario.Room.WidthMetres,
        RoomHeightMetres = scenario.Room.HeightMetres,
        ImageSourceOrder = scenario.Config.ImageSourceOrder,
        Layout = scenario.Layout,
        Family = scenario.Family,
        SubA = scenario.SubA,
        SubB = scenario.SubB,
        Imperfection = scenario.Imperfection,
        TargetCurve = scenario.TargetCurve,
        GroundTruth = scenario.Scenario.GroundTruthSetting,
        PhysicalResidualRatio = double.NaN,
        RuntimeMilliseconds = runtimeMilliseconds,
    };

    /// <summary>
    /// The advisory reference's own predicted numbers, computed through the same shipped helpers the optimizer uses
    /// (<see cref="SubwooferModel.Combine"/>, <see cref="SpatialMetrics.Compute"/>, <see cref="ObjectiveFunction"/>).
    /// Report-only: it explains whether the optimizer's decline is a miss or the geometry being position-dominated,
    /// and it never feeds a pass/fail by itself.
    /// </summary>
    private static (double? StdDev, double? Score, double? Boost) ReferenceOutcome(RegressionScenario scenario, DualSubMeasurement input, OptimizerResult result)
    {
        if (scenario.Scenario.GroundTruthSetting is not { } reference) return (null, null, null);

        IReadOnlyList<PositionResponse> baselineTotals = SubwooferModel.Combine(input, SubwooferSetting.Baseline);
        IReadOnlyList<PositionResponse> referenceTotals = SubwooferModel.Combine(input, reference);
        SpatialSummary summary = SpatialMetrics.Compute(referenceTotals);
        double boost = ObjectiveFunction.MaxBoostVsBaselineDb(referenceTotals, baselineTotals);
        return (summary.MeanStdDevDb, ObjectiveFunction.Score(summary, boost, result.Options.Weights), boost);
    }

    /// <summary>
    /// Target error before and after: the shape (Flat / LowFrequencyHouse / MildDownward) anchored to the measured
    /// band mean, so the deviation measures tilt rather than absolute level. <see cref="TargetCurve.Deviation"/> does
    /// the arithmetic; nothing here re-derives it.
    /// </summary>
    private static (double? Before, double? After) TargetErrors(RegressionScenario scenario, OptimizerResult result)
    {
        if (result.Before.PerFrequency.Count == 0) return (null, null);

        double anchor = result.Before.PerFrequency.Average(row => row.MeanDb);
        double[] frequencies = [.. result.Before.PerFrequency.Select(row => row.FrequencyHz)];
        double[] target = RandomScenarioGenerator.TargetShape(scenario.TargetCurve, frequencies, anchor);
        return (TargetCurve.Deviation(result.Before, target).MeanAbsoluteDeviationDb,
                TargetCurve.Deviation(result.After, target).MeanAbsoluteDeviationDb);
    }

    /// <summary>Advisory per-parameter error against the scenario's known-good correction; null when either is absent.</summary>
    private static (double? Gain, int? Polarity, double? Phase, double? Delay) ReferenceErrors(RegressionScenario scenario, SubwooferSetting? recommended)
    {
        if (recommended is not { } setting || scenario.Scenario.GroundTruthSetting is not { } truth) return (null, null, null, null);

        return (setting.GainDb - truth.GainDb,
                setting.Polarity == truth.Polarity ? 0 : 1,
                Math.IEEERemainder(setting.PhaseDegrees - truth.PhaseDegrees, 360.0),
                (setting.DelaySeconds - truth.DelaySeconds) * 1000.0);
    }

    private static bool FiniteStats(SpatialSummary summary)
    {
        if (AnyNonFinite(summary.MeanStdDevDb, summary.MeanRangeDb, summary.MeanP90P10Db, summary.WorstNullDb, summary.MaxPeakAboveMeanDb))
            return false;

        foreach (FrequencyMetrics row in summary.PerFrequency)
            if (AnyNonFinite(row.FrequencyHz, row.MeanDb, row.MedianDb, row.StdDevDb, row.MinDb, row.MaxDb, row.RangeDb, row.P10Db, row.P90Db, row.P90P10Db))
                return false;

        return true;
    }
}
