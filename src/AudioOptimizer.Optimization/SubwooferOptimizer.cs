namespace AudioOptimizer.Optimization;

/// <summary>
/// The staged search over B's settings. The staged order — polarity → gain coarse (0.5 dB) → phase coarse
/// (10°) → gain fine (0.1 dB) → phase fine (1°) → delay (optional) — is a cheap pre-pass, a performance
/// optimisation rather than the search itself: each of those stages optimises one parameter at a stale value
/// of the others, so a coupled basin is unreachable through them no matter how fine the steps are. The search
/// proper is the last stage: a <b>joint 2-D</b> coarse sweep of gain × phase over the whole allowed region,
/// then a local 2-D refine around the best node. A is fixed at 0 dB; only B is searched.
/// <para>Two invariants, enforced by construction rather than by inspection:</para>
/// <list type="bullet">
/// <item><description>
/// <b>Non-regression:</b> a stage may only replace the incumbent with something strictly better, so best-so-far
/// is monotone across the whole <see cref="OptimizerResult.Trace"/> and a stage that finds nothing changes
/// nothing. A stage can never clear the incumbent.
/// </description></item>
/// <item><description>
/// <b>Compare on score, gate on legality:</b> the incumbent is the best <em>legal</em> candidate, compared on
/// objective score, so a rejected candidate with a flattering score can never win the comparison and be
/// returned. Only when every candidate is rejected does the best overall become the incumbent — the node the
/// local refine projects toward the constraint boundary from.
/// </description></item>
/// </list>
/// </summary>
public static class SubwooferOptimizer
{
    private const double RadiansPerDegree = Math.PI / 180.0;
    private const int MaxLocalRefineRounds = 8;

    public static OptimizerResult Search(DualSubMeasurement measurement, OptimizerOptions? options = null)
    {
        OptimizerOptions resolved = options ?? new OptimizerOptions();
        resolved.Validate();
        measurement.Validate();
        return new StagedSearch(measurement, resolved).Run();
    }

    /// <summary>
    /// Exhaustive grid over the whole parameter space at the steps held in <paramref name="options"/> — pass
    /// the final refinement steps so the reference is at least as fine as the search it validates — for both
    /// polarities and every delay in the delay grid. Returns null when the boost limit rejects every point.
    /// ponytail: exhaustive, so it is for small synthetic cases only (0.1 dB × 1° × 2 polarities ≈ 44k points).
    /// </summary>
    public static OptimizerCandidate? BruteForce(DualSubMeasurement measurement, OptimizerOptions? options = null)
    {
        OptimizerOptions o = options ?? new OptimizerOptions();
        o.Validate();
        measurement.Validate();

        IReadOnlyList<PositionResponse> baselineTotals = SubwooferModel.Combine(measurement, SubwooferSetting.Baseline);
        IReadOnlyList<double> delayGrid = DelayGrid(o);
        OptimizerCandidate? best = null;

        for (int polarityIndex = 0; polarityIndex < 2; polarityIndex++)
        {
            int polarity = polarityIndex == 0 ? 1 : -1;
            foreach (double gain in Steps(o.GainMinDb, o.GainMaxDb, o.GainCoarseStepDb))
            {
                foreach (double phaseDegrees in Steps(o.PhaseMinDegrees, o.PhaseMaxDegrees, o.PhaseCoarseStepDegrees))
                {
                    foreach (double delaySeconds in delayGrid)
                    {
                        var setting = new SubwooferSetting(gain, phaseDegrees * RadiansPerDegree, polarity, delaySeconds);
                        OptimizerCandidate candidate = Measure(measurement, o, setting, baselineTotals);
                        if (!ObjectiveFunction.WithinBoostLimit(candidate.MaxBoostVsBaselineDb, o.MaxBoostLimitDb)) continue;
                        if (best is null || IsBetter(candidate, best, o.ScoreTieEpsilonDb)) best = candidate;
                    }
                }
            }
        }

        return best;
    }

    private static IReadOnlyList<double> DelayGrid(OptimizerOptions options)
        => options.IncludeDelay
            ? [.. Steps(0.0, options.DelayMaxMilliseconds, options.DelayStepMilliseconds).Select(ms => ms / 1000.0)]
            : [0.0];

    /// <summary>Evaluates one setting against the measured response. Deliberately uncached: a few thousand calls.</summary>
    private static OptimizerCandidate Measure(
        DualSubMeasurement measurement,
        OptimizerOptions options,
        SubwooferSetting setting,
        IReadOnlyList<PositionResponse> baselineTotals)
    {
        IReadOnlyList<PositionResponse> totals = SubwooferModel.Combine(measurement, setting);
        SpatialSummary summary = SpatialMetrics.Compute(totals);
        double maxBoost = ObjectiveFunction.MaxBoostVsBaselineDb(totals, baselineTotals);
        ObjectiveTerms terms = ObjectiveFunction.Terms(summary, maxBoost);

        // A total that is exactly zero at one bin is −inf dB there, so the spread terms come out NaN ((−inf)−(−inf)).
        // The honest score of an unbounded spread is +inf, not unrankable: mapping it keeps every candidate
        // comparable, so IsBetter never falls through to the distance tie-break on a NaN score.
        double score = terms.Score(options.Weights);
        if (!double.IsFinite(score)) score = double.PositiveInfinity;
        return new OptimizerCandidate(setting, summary, terms, score, maxBoost);
    }

    /// <summary>
    /// Lower score wins. Within <paramref name="tieEpsilonDb"/> the candidate closest to the measured setting
    /// wins, so equal scores cannot produce an arbitrary invented setting.
    /// </summary>
    private static bool IsBetter(OptimizerCandidate candidate, OptimizerCandidate incumbent, double tieEpsilonDb)
    {
        if (candidate.Score < incumbent.Score - tieEpsilonDb) return true;
        if (candidate.Score > incumbent.Score + tieEpsilonDb) return false;
        return Distance(candidate.Setting).CompareTo(Distance(incumbent.Setting)) < 0;
    }

    /// <summary>Lexicographic "how far from the measured setting", in the order gain, phase, polarity, delay.</summary>
    private static (double Gain, double Phase, int Polarity, double Delay) Distance(SubwooferSetting setting)
        => (Math.Abs(setting.GainDb), Math.Abs(setting.PhaseRad), setting.Polarity == 1 ? 0 : 1, setting.DelaySeconds);

    /// <summary>Ascending grid from..to inclusive. Values are rounded so repeated adds cannot drift.</summary>
    private static IEnumerable<double> Steps(double from, double to, double step)
    {
        int count = (int)Math.Floor((to - from) / step + 1e-9);
        for (int i = 0; i <= count; i++) yield return Math.Round(from + i * step, 9);
    }

    private static double Clamp(double value, double min, double max) => Math.Min(max, Math.Max(min, value));

    private sealed class StagedSearch
    {
        private readonly DualSubMeasurement _measurement;
        private readonly OptimizerOptions _options;

        private readonly IReadOnlyList<PositionResponse> _baselineTotals;
        private readonly List<OptimizerStage> _trace = [];

        private OptimizerCandidate? _bestLegal;
        private OptimizerCandidate? _bestAny;
        private OptimizerCandidate _baseline = null!;
        private int _evaluated;
        private int _rejected;
        private double _leastBoost = double.PositiveInfinity;

        internal StagedSearch(DualSubMeasurement measurement, OptimizerOptions options)
        {
            _measurement = measurement;
            _options = options;
            _baselineTotals = SubwooferModel.Combine(measurement, SubwooferSetting.Baseline);
        }

        /// <summary>
        /// The incumbent the next stage searches around: the best LEGAL candidate, falling back to the best
        /// overall only when the constraint has rejected everything so far.
        /// </summary>
        private OptimizerCandidate Incumbent => _bestLegal ?? _bestAny!;

        internal OptimizerResult Run()
        {
            _baseline = Consider(SubwooferSetting.Baseline);
            Trace("baseline (measured setting)");

            SweepPolarity();
            Trace("polarity");
            SweepGain(_options.GainCoarseStepDb);
            Trace($"gain coarse {_options.GainCoarseStepDb} dB");
            SweepPhase(_options.PhaseCoarseStepDegrees);
            Trace($"phase coarse {_options.PhaseCoarseStepDegrees}°");
            SweepGain(_options.GainFineStepDb);
            Trace($"gain fine {_options.GainFineStepDb} dB");
            SweepPhase(_options.PhaseFineStepDegrees);
            Trace($"phase fine {_options.PhaseFineStepDegrees}°");
            if (_options.IncludeDelay)
            {
                SweepDelay();
                Trace($"delay {_options.DelayStepMilliseconds} ms");
            }

            // The search proper: gain and phase evaluated together, never one at a stale value of the other.
            CoarseJointSweep();
            Trace($"joint 2-D coarse {_options.GainCoarseStepDb} dB × {_options.PhaseCoarseStepDegrees}°");
            LocalJointRefine();
            Trace($"joint 2-D local {_options.GainFineStepDb} dB × {_options.PhaseFineStepDegrees}°");

            if (_options.IncludeDelay)
            {
                SweepDelay();
                LocalJointRefine();
                Trace("joint 2-D local with delay");
            }

            if (_bestLegal is null)
            {
                // Every candidate so far broke the limit. Refine around the best node anyway at full resolution
                // so a legal point just inside the boundary can still be found; the incumbent here is the best
                // overall, which is the only thing to descend from.
                LocalJointRefine();
                Trace("all-rejected refine toward the boundary");
            }

            return BuildResult();
        }

        private OptimizerResult BuildResult()
        {
            OptimizerCandidate? legal = _bestLegal;
            var constraint = new ConstraintReport(
                _options.MaxBoostLimitDb,
                _evaluated,
                _rejected,
                // Binding = the constraint changed the outcome: the best setting found ignoring the limit was itself rejected.
                _rejected > 0 && !ObjectiveFunction.WithinBoostLimit(_bestAny!.MaxBoostVsBaselineDb, _options.MaxBoostLimitDb),
                legal?.MaxBoostVsBaselineDb,
                double.IsPositiveInfinity(_leastBoost) ? 0.0 : _leastBoost);

            // The recommendation must be legal (the safety promise) and holdable (the report grid). Snapping
            // can move the achieved boost by a fraction of a step, so the snapped setting is re-measured and
            // only accepted if it still respects the limit.
            OptimizerCandidate? recommended = legal is null ? null : RecommendOnReportGrid(legal);
            if (legal is not null && recommended is null)
            {
                // Only reachable with a razor-thin limit around the optimum: the honest answer is no change.
                OptimizerCandidate fallback = Measure(_measurement, _options, _baseline.Setting, _baselineTotals);
                recommended = ObjectiveFunction.WithinBoostLimit(fallback.MaxBoostVsBaselineDb, _options.MaxBoostLimitDb) ? fallback : null;
            }

            OptimizationVerdict verdict;
            SubwooferSetting? setting;
            SpatialSummary after;
            ObjectiveTerms termsAfter;
            double scoreAfter;

            if (recommended is null)
            {
                verdict = OptimizationVerdict.NoSettingWithinBoostLimit;
                setting = null;
                after = _baseline.Summary;
                termsAfter = _baseline.Terms;
                scoreAfter = _baseline.Score;
            }
            else if (_baseline.Score - recommended.Score < _options.MinimumScoreImprovementDb)
            {
                // Do not recommend a setting that does nothing: keep the measured setting and say why.
                verdict = OptimizationVerdict.NegligibleImprovement;
                setting = SubwooferSetting.Baseline;
                after = _baseline.Summary;
                termsAfter = _baseline.Terms;
                scoreAfter = _baseline.Score;
            }
            else
            {
                verdict = OptimizationVerdict.Improved;
                setting = recommended.Setting;
                after = recommended.Summary;
                termsAfter = recommended.Terms;
                scoreAfter = recommended.Score;
            }

            // Diagnosis is about what these parameters could achieve at best inside the limit.
            OptimizerCandidate ceiling = legal ?? _baseline;
            var diagnosis = new List<FrequencyDiagnosis>(_baseline.Summary.PerFrequency.Count);
            for (int k = 0; k < _baseline.Summary.PerFrequency.Count; k++)
            {
                FrequencyMetrics before = _baseline.Summary.PerFrequency[k];
                FrequencyMetrics achievable = ceiling.Summary.PerFrequency[k];
                double improvement = before.StdDevDb - achievable.StdDevDb;
                diagnosis.Add(new FrequencyDiagnosis(
                    before.FrequencyHz,
                    before.StdDevDb,
                    achievable.StdDevDb,
                    improvement,
                    improvement < _options.PositionDominatedThresholdDb));
            }

            IReadOnlyList<double>? target = _options.TargetDb;
            return new OptimizerResult(
                verdict,
                setting,
                legal,
                _baseline.Summary,
                after,
                _baseline.Terms,
                termsAfter,
                _baseline.Score,
                scoreAfter,
                LevelChangeDb(_baseline.Summary, after),
                constraint,
                diagnosis,
                _trace,
                target is null ? null : TargetCurve.Deviation(_baseline.Summary, target),
                target is null ? null : TargetCurve.Deviation(after, target),
                _options);
        }

        /// <summary>Mean change in the spatial mean level across the band: the trade the score is blind to.</summary>
        private static double LevelChangeDb(SpatialSummary before, SpatialSummary after)
        {
            double sum = 0.0;
            for (int k = 0; k < before.PerFrequency.Count; k++)
                sum += after.PerFrequency[k].MeanDb - before.PerFrequency[k].MeanDb;
            return sum / before.PerFrequency.Count;
        }

        /// <summary>
        /// Best legal setting within one report step of the fine optimum, measured on the report grid.
        /// Returns null when none of the nine is legal.
        /// </summary>
        private OptimizerCandidate? RecommendOnReportGrid(OptimizerCandidate fine)
        {
            SubwooferSetting anchor = fine.Setting.Quantized(_options.ReportGainStepDb, _options.ReportPhaseStepDegrees);
            double phaseStepRad = _options.ReportPhaseStepDegrees * RadiansPerDegree;
            OptimizerCandidate? best = null;

            for (int gainIndex = -1; gainIndex <= 1; gainIndex++)
            {
                for (int phaseIndex = -1; phaseIndex <= 1; phaseIndex++)
                {
                    var setting = new SubwooferSetting(
                        Clamp(anchor.GainDb + gainIndex * _options.ReportGainStepDb, _options.GainMinDb, _options.GainMaxDb),
                        Clamp(anchor.PhaseRad + phaseIndex * phaseStepRad, _options.PhaseMinDegrees * RadiansPerDegree, _options.PhaseMaxDegrees * RadiansPerDegree),
                        fine.Setting.Polarity,
                        fine.Setting.DelaySeconds);

                    OptimizerCandidate candidate = Measure(_measurement, _options, setting, _baselineTotals);
                    if (!ObjectiveFunction.WithinBoostLimit(candidate.MaxBoostVsBaselineDb, _options.MaxBoostLimitDb)) continue;
                    if (best is null || IsBetter(candidate, best, _options.ScoreTieEpsilonDb)) best = candidate;
                }
            }

            return best;
        }

        private OptimizerCandidate Consider(SubwooferSetting setting)
        {
            OptimizerCandidate candidate = Measure(_measurement, _options, setting, _baselineTotals);
            _evaluated++;
            _leastBoost = Math.Min(_leastBoost, candidate.MaxBoostVsBaselineDb);

            bool legal = ObjectiveFunction.WithinBoostLimit(candidate.MaxBoostVsBaselineDb, _options.MaxBoostLimitDb);
            if (!legal) _rejected++;
            if (_bestAny is null || IsBetter(candidate, _bestAny, _options.ScoreTieEpsilonDb)) _bestAny = candidate;
            if (legal && (_bestLegal is null || IsBetter(candidate, _bestLegal, _options.ScoreTieEpsilonDb))) _bestLegal = candidate;
            return candidate;
        }

        private void Trace(string stage)
        {
            OptimizerCandidate best = Incumbent;
            _trace.Add(new OptimizerStage(stage, best.Score, best.Setting));
        }

        private void SweepPolarity()
        {
            SubwooferSetting current = Incumbent.Setting;
            Consider(current with { Polarity = 1 });
            Consider(current with { Polarity = -1 });
        }

        private void SweepGain(double step)
        {
            SubwooferSetting current = Incumbent.Setting;
            foreach (double gain in Steps(_options.GainMinDb, _options.GainMaxDb, step))
                Consider(current with { GainDb = gain });
        }

        private void SweepPhase(double stepDegrees)
        {
            SubwooferSetting current = Incumbent.Setting;
            foreach (double degrees in Steps(_options.PhaseMinDegrees, _options.PhaseMaxDegrees, stepDegrees))
                Consider(current with { PhaseRad = degrees * RadiansPerDegree });
        }

        private void SweepDelay()
        {
            SubwooferSetting current = Incumbent.Setting;
            foreach (double milliseconds in Steps(0.0, _options.DelayMaxMilliseconds, _options.DelayStepMilliseconds))
                Consider(current with { DelaySeconds = milliseconds / 1000.0 });
        }

        /// <summary>
        /// Joint 2-D sweep of gain × phase over the whole allowed region: the stage that makes a coupled basin
        /// reachable. BOTH polarities are swept: with phase searched over [PhaseMin, PhaseMax] a single polarity
        /// reaches only half the drive-rotation circle (the other half is that circle offset by 180°), so
        /// inheriting the baseline polarity here would lock the search out of half the parameter space.
        /// </summary>
        private void CoarseJointSweep()
        {
            SubwooferSetting current = Incumbent.Setting;

            foreach (int polarity in new[] { 1, -1 })
            {
                foreach (double gain in Steps(_options.GainMinDb, _options.GainMaxDb, _options.GainCoarseStepDb))
                {
                    foreach (double degrees in Steps(_options.PhaseMinDegrees, _options.PhaseMaxDegrees, _options.PhaseCoarseStepDegrees))
                        Consider(current with { GainDb = gain, PhaseRad = degrees * RadiansPerDegree, Polarity = polarity });
                }
            }
        }

        /// <summary>
        /// Local joint 2-D refine: an 11 × 11 grid one fine step either side of the incumbent in both axes
        /// (the ±5 steps are the ± half-a-coarse-step neighbourhood), repeated while it keeps improving.
        /// One pass is what the spec asks for; repeating is safe because a pass can only replace the incumbent
        /// with something strictly better.
        /// ponytail: a grid is still a grid — an optimum narrower than one fine cell can be missed, and the
        /// coarse mesh can only find a basin that has a node in it. The tests bound the gap against an
        /// exhaustive reference at this same resolution; go to a coarse-to-fine simplex if a case ever needs
        /// finer than 0.1 dB / 1°.
        /// </summary>
        private void LocalJointRefine()
        {
            for (int round = 0; round < MaxLocalRefineRounds; round++)
            {
                OptimizerCandidate before = Incumbent;
                SubwooferSetting current = before.Setting;
                double phaseStepRad = _options.PhaseFineStepDegrees * RadiansPerDegree;

                for (int gainIndex = -5; gainIndex <= 5; gainIndex++)
                {
                    for (int phaseIndex = -5; phaseIndex <= 5; phaseIndex++)
                    {
                        Consider(current with
                        {
                            GainDb = Clamp(current.GainDb + gainIndex * _options.GainFineStepDb, _options.GainMinDb, _options.GainMaxDb),
                            PhaseRad = Clamp(current.PhaseRad + phaseIndex * phaseStepRad, _options.PhaseMinDegrees * RadiansPerDegree, _options.PhaseMaxDegrees * RadiansPerDegree),
                        });
                    }
                }

                if (!IsBetter(Incumbent, before, _options.ScoreTieEpsilonDb)) return;
            }
        }
    }
}
