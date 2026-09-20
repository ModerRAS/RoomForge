namespace AudioOptimizer.UI.ViewModels;

using System.Globalization;
using AudioOptimizer.Core;
using AudioOptimizer.Measurement;
using AudioOptimizer.Optimization;

/// <summary>
/// M4: the optimizer panel. It owns the weights, the max-boost limit, the run and the reading of the result, and it
/// deliberately owns no optimization logic — the search is <see cref="SubwooferOptimizer"/>, which stays synchronous
/// and thread-free (<c>ThreadFreeLayersTests</c> bans a <c>Task</c>-returning surface there), so the asynchrony lives
/// here exactly as the measurement flow's does.
/// <para>
/// The max-boost limit is the one setting with a closed domain: <see cref="AllowedMaxBoostDb"/> is the whole set, and
/// <see cref="MaxBoostDb"/> refuses anything else, so no path — not a spin box, not a text field, not a future
/// binding — can put a negative limit (or any fourth value) in front of the search.
/// </para>
/// </summary>
public sealed class OptimizerPanelViewModel : ObservableObject
{
    /// <summary>The entire space of legal limits: 0, 3 or 6 dB. Not a range, a set.</summary>
    public static readonly IReadOnlyList<double> AllowedMaxBoostDb = [0.0, 3.0, 6.0];

    private double _maxBoostDb = 3.0;
    private double _weightMeanStdDev = ObjectiveWeights.Default.MeanStdDev;
    private double _weightMeanP90P10 = ObjectiveWeights.Default.MeanP90P10;
    private double _weightPeakPenalty = ObjectiveWeights.Default.PeakPenalty;
    private double _weightNullPenalty = ObjectiveWeights.Default.NullPenalty;
    private bool _isBusy;
    private string _status = "Open a project with A and B measured, then run the search.";
    private string _verdictText = string.Empty;
    private string _recommendationText = string.Empty;
    private string _boostText = string.Empty;
    private string _bindingText = string.Empty;
    private IReadOnlyList<string> _diagnosisLines = [];
    private IReadOnlyList<string> _causeLines = ["no structured cause reported"];
    private OptimizerResult? _result;
    private DualSubMeasurement? _measurement;
    private string _modelVerdictText = "The model check has not run.";

    public OptimizerPanelViewModel()
    {
        RunCommand = new RelayCommand(async () => await RunAsync());
        ValidateModelCommand = new RelayCommand(ValidateModel);
    }

    public RelayCommand RunCommand { get; }

    public RelayCommand ValidateModelCommand { get; }

    public double MaxBoostDb
    {
        get => _maxBoostDb;
        set
        {
            // The domain is the three legal values. Refusing rather than clamping is deliberate: clamping a −1 to 0
            // would silently accept a request nobody made, and the point of the closed set is that the request and the
            // value the search sees are the same number.
            if (!AllowedMaxBoostDb.Contains(value))
            {
                Status = $"Max boost is offered as {string.Join(", ", AllowedMaxBoostDb.Select(value => value.ToString("F0", CultureInfo.InvariantCulture)))} dB only; "
                    + $"{value.ToString("F1", CultureInfo.InvariantCulture)} dB was refused and the limit stays "
                    + $"{_maxBoostDb.ToString("F1", CultureInfo.InvariantCulture)} dB.";
                return;
            }

            Set(ref _maxBoostDb, value);
        }
    }

    public double WeightMeanStdDev
    {
        get => _weightMeanStdDev;
        set => Set(ref _weightMeanStdDev, value);
    }

    public double WeightMeanP90P10
    {
        get => _weightMeanP90P10;
        set => Set(ref _weightMeanP90P10, value);
    }

    public double WeightPeakPenalty
    {
        get => _weightPeakPenalty;
        set => Set(ref _weightPeakPenalty, value);
    }

    public double WeightNullPenalty
    {
        get => _weightNullPenalty;
        set => Set(ref _weightNullPenalty, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value)) Raise(nameof(CanRun));
        }
    }

    public bool CanRun => !IsBusy && _measurement is not null;

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public string VerdictText
    {
        get => _verdictText;
        private set => Set(ref _verdictText, value);
    }

    public string RecommendationText
    {
        get => _recommendationText;
        private set => Set(ref _recommendationText, value);
    }

    /// <summary>The requested limit and the achieved boost, side by side: the first is what was asked for, the second
    /// is what the recommendation actually does to the measurement.</summary>
    public string BoostText
    {
        get => _boostText;
        private set => Set(ref _boostText, value);
    }

    /// <summary>Whether the limit bound the search — structured data from the constraint report, shown as a sentence.</summary>
    public string BindingText
    {
        get => _bindingText;
        private set => Set(ref _bindingText, value);
    }

    public IReadOnlyList<string> DiagnosisLines
    {
        get => _diagnosisLines;
        private set => Set(ref _diagnosisLines, value);
    }

    /// <summary>§21: one line per structured cause the engine reported, or an explicit statement that there is none.</summary>
    public IReadOnlyList<string> CauseLines
    {
        get => _causeLines;
        private set => Set(ref _causeLines, value);
    }

    /// <summary>§21's verdict on whether linear superposition describes this rig.</summary>
    public string ModelVerdictText
    {
        get => _modelVerdictText;
        private set => Set(ref _modelVerdictText, value);
    }

    public OptimizerResult? Result
    {
        get => _result;
        private set => Set(ref _result, value);
    }

    /// <summary>The options the run will use. The only route to a limit, so a refused value cannot reach the search.</summary>
    public OptimizerOptions BuildOptions() => OptimizerOptions.Default with
    {
        MaxBoostLimitDb = _maxBoostDb,
        Weights = new ObjectiveWeights(_weightMeanStdDev, _weightMeanP90P10, _weightPeakPenalty, _weightNullPenalty),
    };

    /// <summary>Reads the measured A and B passes from the flow's session and pairs them by point.</summary>
    public void Refresh(MeasurementFlowViewModel flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        if (flow.Session is not { } session)
        {
            _measurement = null;
            Status = "Open a project with A and B measured, then run the search.";
            Raise(nameof(CanRun));
            return;
        }

        IReadOnlyList<PositionResponse> a = Measured(session, SubMode.A);
        IReadOnlyList<PositionResponse> b = Measured(session, SubMode.B);
        var byId = b.ToDictionary(position => position.PointId);
        var pairedA = new List<PositionResponse>();
        var pairedB = new List<PositionResponse>();
        foreach (PositionResponse position in a)
        {
            if (!byId.TryGetValue(position.PointId, out PositionResponse? match)) continue;
            pairedA.Add(position);
            pairedB.Add(match);
        }

        if (pairedA.Count == 0)
        {
            UseMeasurement(null);
            return;
        }

        UseMeasurement(new DualSubMeasurement(pairedA, pairedB, Measured(session, SubMode.AB)));
    }

    /// <summary>
    /// The panel's one input path, the same shape as the load path: it takes the engine's own measurement type, so one
    /// call serves both the session and a test fixture and there is no second way for a measurement to get in.
    /// </summary>
    public void UseMeasurement(DualSubMeasurement? measurement)
    {
        _measurement = measurement;
        if (measurement is null || measurement.A.Count == 0 || measurement.B.Count == 0)
        {
            _measurement = null;
            Status = "The search needs both A and B measured on the same positions.";
            Raise(nameof(CanRun));
            return;
        }

        measurement.Validate();
        Result = null;
        ModelVerdictText = measurement.AB is { Count: > 0 }
            ? "The model check is ready to run against the measured A+B pass."
            : "The model check needs a measured A+B pass as well as A and B.";
        Status = $"{measurement.A.Count} position(s) ready; limit {_maxBoostDb.ToString("F0", CultureInfo.InvariantCulture)} dB.";
        Raise(nameof(CanRun));
    }

    /// <summary>
    /// §21's pipeline: predicts the dual-sub pass from the measured A and B through <c>SubwooferModel</c> and compares it
    /// with the measured A+B pass, then hands the engine's own structured causes to the panel. Without a measured A+B
    /// pass the panel says so rather than reporting a check it did not run.
    /// </summary>
    public void ValidateModel()
    {
        if (_measurement?.AB is not { Count: > 0 } realAb)
        {
            ModelVerdictText = "The model check needs a measured A+B pass as well as A and B.";
            ShowCauses([]);
            return;
        }

        try
        {
            AbValidationResult result = AbValidation.Compare(_measurement, realAb, SubwooferSetting.Baseline);
            ModelVerdictText = result.Agrees
                ? $"The model describes this rig: within {result.Options.MagnitudeErrorThresholdDb} dB and "
                    + $"{result.Options.PhaseErrorThresholdDegrees}° at the compared band (verdict {result.Verdict})."
                : $"The model does not describe this rig: verdict {result.Verdict}, band error "
                    + $"{result.Band.MeanMagnitudeErrorDb:+0.00;-0.00;0.00} dB, spread {result.Band.MagnitudeErrorSpreadDb:0.00} dB. "
                    + "Check the structured causes below before trusting a predicted optimum.";
            ShowCauses(result.Checks);
        }
        catch (Exception exception)
        {
            ModelVerdictText = $"The model check failed: {exception.GetType().Name}: {exception.Message}";
            ShowCauses([]);
        }
    }

    /// <summary>
    /// Runs the search off the UI thread and records the reading of it. The engine call is synchronous and stays that
    /// way; the continuation resumes on the caller's context (the dispatcher), which is what keeps the notifications
    /// on the UI thread — the same pattern the measurement flow uses, not a second one.
    /// </summary>
    public async Task RunAsync()
    {
        if (_measurement is null)
        {
            Status = "The search needs both A and B measured on the same positions.";
            return;
        }

        IsBusy = true;
        Status = "Searching…";
        try
        {
            OptimizerOptions options = BuildOptions();
            OptimizerResult result = await Task.Run(() => SubwooferOptimizer.Search(_measurement, options)).ConfigureAwait(true);
            Apply(result);
        }
        catch (Exception exception)
        {
            Status = $"The search failed: {exception.GetType().Name}: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
            Raise(nameof(CanRun));
        }
    }

    /// <summary>§21: renders the causes the engine reported. An empty list is stated, never filled in by guessing.</summary>
    public void ShowCauses(IReadOnlyList<AbCheckKind> causes)
    {
        ArgumentNullException.ThrowIfNull(causes);
        CauseLines = causes.Count == 0
            ? ["no structured cause reported"]
            : [.. causes.Select(CauseText)];
    }

    /// <summary>
    /// The wording for one structured cause. Exhaustive by construction and named in the fallback, so a cause added to
    /// the engine's type cannot be rendered as a blank line — the test enumerates the type and asserts each member
    /// reaches specific wording.
    /// </summary>
    public static string CauseText(AbCheckKind cause) => cause switch
    {
        AbCheckKind.Polarity => "polarity: the measured A+B looks inverted relative to the model — check the polarity of one sub",
        AbCheckKind.Gain => "gain: the compared band is offset by a constant level — check the gain applied to each path",
        AbCheckKind.PhaseSetting => "phase: a phase offset that is not a polarity flip — check the phase setting that was applied",
        AbCheckKind.DeviceDsp => "device DSP: the error varies with frequency — check EQ, crossover or delay on one path",
        AbCheckKind.MeasurementSync => "measurement sync: a material disagreement with none of the structured causes — check that A, B and A+B were captured in the same session",
        _ => $"unrecognised cause ({cause}): the engine reported a cause this panel does not describe",
    };

    public void ReportFailure(string message) => Status = message;

    private void Apply(OptimizerResult result)
    {
        Result = result;
        ConstraintReport constraint = result.Constraint;
        VerdictText = result.Verdict switch
        {
            OptimizationVerdict.Improved => $"Improved: score {F(result.ScoreBefore)} → {F(result.ScoreAfter)} dB "
                + $"({F(result.ScoreAfter - result.ScoreBefore)} dB); uniformity "
                + $"{F(result.Before.MeanStdDevDb)} → {F(result.After.MeanStdDevDb)} dB standard deviation across positions.",
            OptimizationVerdict.NegligibleImprovement => $"Not worth changing: the best legal setting improves the score by only "
                + $"{F(result.ScoreAfter - result.ScoreBefore)} dB, below the {F(result.Options.MinimumScoreImprovementDb)} dB threshold.",
            OptimizationVerdict.NoSettingWithinBoostLimit => "Not optimizable within the limit: every setting that improves uniformity needs more boost "
                + $"than the {F(constraint.MaxBoostLimitDb)} dB allowed. The per-frequency rows below say where.",
            _ => $"Verdict: {result.Verdict}.",
        };

        RecommendationText = result.Recommended is { } setting
            ? $"Recommended: gain {F(setting.GainDb)} dB, phase {F(setting.PhaseDegrees)}°, polarity {setting.Polarity:+#;-#;+1}, delay {F(setting.DelaySeconds * 1000.0)} ms"
            : "Recommended: none — no legal setting is being proposed.";

        // Two numbers, always: the limit is a request, the achieved boost is what the recommendation does.
        BoostText = constraint.MaxAchievedBoostDb is { } achieved
            ? $"Requested limit {F(constraint.MaxBoostLimitDb)} dB; achieved boost {F(achieved)} dB "
                + $"(the least any compared position gets is {F(constraint.LeastAchievedBoostDb)} dB)."
            : $"Requested limit {F(constraint.MaxBoostLimitDb)} dB; no boost was achieved because no setting was proposed.";

        BindingText = constraint.Binding
            ? $"The limit bound the search: {constraint.CandidatesRejected} of {constraint.CandidatesEvaluated} candidates were rejected as illegal."
            : $"The limit did not bind: none of the {constraint.CandidatesEvaluated} candidates was rejected.";

        DiagnosisLines = result.Diagnosis.Count == 0
            ? ["no per-frequency diagnosis"]
            : [.. result.Diagnosis.Select(row =>
                $"{F(row.FrequencyHz)} Hz: {F(row.BeforeStdDevDb)} → {F(row.AfterStdDevDb)} dB spread "
                + $"({F(row.ImprovementDb)} dB){(row.PositionDominated ? " — dominated by one or two positions" : string.Empty)}")];

        Status = $"Search complete: {constraint.CandidatesEvaluated} candidates, {constraint.CandidatesRejected} rejected."
            + (result.Coverage is { } coverage ? $" {coverage.WarningText}" : string.Empty);
    }

    private static IReadOnlyList<PositionResponse> Measured(MeasurementSession session, SubMode mode)
    {
        var positions = new List<PositionResponse>();
        foreach (MeasurementSlot slot in session.Slots)
        {
            if (slot.Mode != mode || slot.State != MeasurementSlotState.Done) continue;
            // In-band only: §21's comparison and the search must both see the band that was measured, not the spectrum.
            if (session.InBandResponseOf(slot) is { Length: > 0 } bins)
                positions.Add(new PositionResponse(slot.Point.Id, session.Band, bins));
        }

        return positions;
    }

    private static string F(double value) => value.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture);
}
