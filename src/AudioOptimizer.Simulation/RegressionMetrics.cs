namespace AudioOptimizer.Simulation;

using AudioOptimizer.Core;
using AudioOptimizer.Optimization;

/// <summary>
/// Everything one randomized regression scenario measured, as data. The statistics are the engine's own
/// (<see cref="SpatialMetrics"/> / <see cref="BandSpatialStats"/>) and the boost verdict is the optimizer's own
/// (<see cref="ConstraintReport"/>); nothing here re-derives either. The ground-truth comparison is ADVISORY: the
/// known-good correction is the generator's reference, never an optimizer input.
/// </summary>
public sealed record RegressionMetrics
{
    /// <summary>Boost comparisons are exact up to this: both numbers come from the same double arithmetic.</summary>
    public const double BoostEpsilonDb = 1e-9;

    // Identity.
    public int Index { get; init; }
    public int Seed { get; init; }
    public string ScenarioId { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;

    // The room and the physical state the scenario was built from.
    public double RoomLengthMetres { get; init; }
    public double RoomWidthMetres { get; init; }
    public double RoomHeightMetres { get; init; }
    public int ImageSourceOrder { get; init; }
    public RegressionLayout Layout { get; init; }
    public RegressionAlignmentFamily Family { get; init; }
    public VirtualSubwoofer? SubA { get; init; }
    public VirtualSubwoofer? SubB { get; init; }
    public RegressionImperfectionProfile? Imperfection { get; init; }
    public RegressionTargetCurve TargetCurve { get; init; }

    // Advisory recommendation-against-reference errors. Null when the optimizer recommended nothing.
    public double? GainErrorDb { get; init; }
    public int? PolarityMismatch { get; init; }
    public double? PhaseErrorDegrees { get; init; }
    public double? DelayErrorMilliseconds { get; init; }

    // Spatial outcome, straight from the optimizer result.
    public BandSpatialStats Before { get; init; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0);
    public BandSpatialStats After { get; init; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0);
    public double ScoreBefore { get; init; }
    public double ScoreAfter { get; init; }
    public double LevelChangeDb { get; init; }
    public double MaxAchievedBoostDb { get; init; }
    public double MaxBoostLimitDb { get; init; }
    public bool RecommendedSomething { get; init; }
    public OptimizationVerdict Verdict { get; init; }
    public SubwooferSetting? Recommended { get; init; }
    public SubwooferSetting? GroundTruth { get; init; }

    // Optimizer internals, report-only: whether a legal improvement existed even when nothing was recommended.
    public double? BestScoreDb { get; init; }
    public SubwooferSetting? Best { get; init; }
    public int CandidatesEvaluated { get; init; }
    public int CandidatesRejected { get; init; }
    public bool BoostBinding { get; init; }

    /// <summary>The advisory reference's own predicted numbers: σ, score and boost of the known-good correction.</summary>
    public double? GroundTruthReferenceStdDevDb { get; init; }
    public double? GroundTruthReferenceScoreDb { get; init; }
    public double? GroundTruthReferenceBoostDb { get; init; }

    /// <summary>Target-curve error before/after: mean |per-frequency spatial mean − target| over the band, dB.</summary>
    public double? TargetErrorBeforeDb { get; init; }
    public double? TargetErrorAfterDb { get; init; }

    // Judgement inputs.
    /// <summary>Median |AB−(A+B)| over the median response scale; see <see cref="RegressionRunner.PhysicalResidualRatio"/>.</summary>
    public double PhysicalResidualRatio { get; init; }
    public bool NonFiniteValues { get; init; }
    public bool ValidSetting { get; init; } = true;

    // Report-only.
    public double RuntimeMilliseconds { get; init; }

    /// <summary>Where the per-scenario time went: the measurement chain (81 captures) and the shipped search.</summary>
    public double MeasureRuntimeMilliseconds { get; init; }
    public double OptimizerRuntimeMilliseconds { get; init; }

    /// <summary>Before σ minus After σ: positive is an improvement. The headline regression metric.</summary>
    public double ImprovementDb => Before.StdDevDb - After.StdDevDb;

    public bool BoostLimitViolated => MaxAchievedBoostDb > MaxBoostLimitDb + BoostEpsilonDb;

    public double? BandMeanLevelBeforeDb { get; init; }

    /// <summary>One compact line of outcome numbers; used by <c>--replay</c> and by evidence collection.</summary>
    public string SummaryLine()
    {
        string target = TargetErrorBeforeDb is { } beforeTarget && TargetErrorAfterDb is { } afterTarget
            ? $"{beforeTarget:F3} → {afterTarget:F3} dB"
            : "n/a";
        string errors = GainErrorDb is { } gain
            ? $"gain {gain:+0.00;-0.00;0.00} dB, polarityΔ {PolarityMismatch}, phase {PhaseErrorDegrees:+0.0;-0.0;0.0}°, delay {DelayErrorMilliseconds:+0.0;-0.0;0.0} ms"
            : "n/a";
        string reference = GroundTruthReferenceStdDevDb is { } referenceStdDev && GroundTruthReferenceScoreDb is { } referenceScore
            ? $"reference σ {referenceStdDev:F3} score {referenceScore:F3}"
            : "reference n/a";
        return $"σ {Before.StdDevDb:F3} → {After.StdDevDb:F3} dB (improvement {ImprovementDb:+0.00;-0.00;0.00}), "
             + $"score {ScoreBefore:F3} → {ScoreAfter:F3}, best {BestScoreDb:F3}, verdict {Verdict}, recommended {(RecommendedSomething ? "yes" : "no")}, "
             + $"boost {MaxAchievedBoostDb:F3}/{MaxBoostLimitDb:F1} dB, binding {BoostBinding}, rejected {CandidatesRejected}/{CandidatesEvaluated}, "
             + $"{reference}, "
             + $"physical residual ratio {PhysicalResidualRatio:E2}, target error {target}, GT error {errors}, "
             + $"measure {MeasureRuntimeMilliseconds:F0} ms + optimizer {OptimizerRuntimeMilliseconds:F0} ms, "
             + $"{RuntimeMilliseconds:F0} ms";
    }

    /// <summary>One line naming every dimension a reader needs to reproduce the scenario by hand.</summary>
    public string DescribeConfig()
    {
        string subs = SubA is { } a && SubB is { } b
            ? $"A={a.Position} {a.GainDb:+0.0;-0.0;0.0}dB pol{a.Polarity:+#;-#;+1} {a.PhaseDegrees:0.0}deg {a.DelaySeconds * 1000.0:0.0}ms; "
              + $"B={b.Position} {b.GainDb:+0.0;-0.0;0.0}dB pol{b.Polarity:+#;-#;+1} {b.PhaseDegrees:0.0}deg {b.DelaySeconds * 1000.0:0.0}ms"
            : "n/a";

        string imperfection = Imperfection?.Describe() ?? "n/a";
        string recommended = Recommended is { } setting
            ? $"{setting.GainDb:+0.0;-0.0;0.0}dB pol{setting.Polarity:+#;-#;+1} {setting.PhaseDegrees:0.0}deg {setting.DelaySeconds * 1000.0:0.0}ms"
            : "none";
        string truth = GroundTruth is { } reference
            ? $"{reference.GainDb:+0.0;-0.0;0.0}dB pol{reference.Polarity:+#;-#;+1} {reference.PhaseDegrees:0.0}deg {reference.DelaySeconds * 1000.0:0.0}ms"
            : "none";

        return $"{ScenarioId} seed={Seed} index={Index} layout={Layout} family={Family} target={TargetCurve} "
             + $"room={RoomLengthMetres:F2}x{RoomWidthMetres:F2}x{RoomHeightMetres:F2}m ism={ImageSourceOrder} "
             + $"{subs}; {imperfection}; recommended {recommended}; reference {truth}";
    }
}
