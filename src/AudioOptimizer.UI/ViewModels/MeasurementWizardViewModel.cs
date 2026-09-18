namespace AudioOptimizer.UI.ViewModels;

using System.Globalization;
using AudioOptimizer.Core;
using AudioOptimizer.Measurement;

/// <summary>
/// One presented step of §24's twelve. Every field is DERIVED on read — the wizard stores no step state, so a
/// stored copy cannot drift away from the panel the user is actually typing into.
/// </summary>
public sealed record WizardStep(int Number, string Title, string Detail, string Cue, bool IsDone, bool IsCurrent)
{
    /// <summary>"7. Sub A" as one string, so a render test asserts a row rather than two fragments of one.</summary>
    public string Heading => $"{Number}. {Title}";

    /// <summary>The state as text: a row says what it is, and a bitmap or tree assertion can look for it.</summary>
    public string StateText => IsDone ? "done" : IsCurrent ? "next" : "upcoming";
}

/// <summary>
/// §24: the twelve guided steps, as a PRESENTER over <see cref="MeasurementFlowViewModel"/>. It adds no state
/// machine, no command and no measurement logic — every row is derived from the flow's own properties and the
/// session it holds, and the only thing this class owns is the order of the twelve steps.
/// <para>
/// What is derived, derived once: which step is CURRENT. It is the first step that is not done, so the guide
/// advances on its own as the user works and cannot be left pointing at a step already finished. Nothing here
/// knows how to measure, validate or optimise; when a step has no existing action to point at, the row says so
/// rather than inventing one.
/// </para>
/// </summary>
public sealed class MeasurementWizardViewModel : ObservableObject
{
    private readonly MeasurementFlowViewModel _flow;
    private readonly OptimizerPanelViewModel _optimizer;

    public MeasurementWizardViewModel(MeasurementFlowViewModel flow, OptimizerPanelViewModel optimizer)
    {
        _flow = flow ?? throw new ArgumentNullException(nameof(flow));
        _optimizer = optimizer ?? throw new ArgumentNullException(nameof(optimizer));
        // Nothing to keep in sync, only something to say when an input moved. Every notification re-reads the
        // steps, which is why a settings change (which raises SweepText/GridText) is enough to move the guide.
        _flow.PropertyChanged += (_, _) => Refresh();
        _optimizer.PropertyChanged += (_, _) => Refresh();
    }

    /// <summary>The twelve steps, rebuilt on read. A stored list would be a second source of truth for step state.</summary>
    public IReadOnlyList<WizardStep> Steps
    {
        get
        {
            MeasurementSession? session = _flow.Session;
            bool areaUsable = _flow.WidthMetres > 0 && _flow.DepthMetres > 0 && _flow.HeightMetres > 0;
            int paired = PairedPointCount(session);

            // The brief's order, written once, here. Each row carries the state it is derived to have and the cue
            // for the action that already exists for it — the cue names an existing button, it does not add one.
            (bool Done, string Title, string Detail, string Cue)[] rows =
            [
                (_flow.SelectedInput is not null,
                    "Input device",
                    _flow.SelectedInput is { } input ? $"selected: {input.Name}" : "none selected",
                    "Press Refresh devices on the Measure tab, then pick the measurement microphone."),
                (_flow.SelectedOutput is not null,
                    "Output device",
                    _flow.SelectedOutput is { } output ? $"selected: {output.Name}" : "none selected",
                    "Pick the playback device the subwoofers are driven from."),
                (_flow.SampleRate > 0,
                    "Sample rate",
                    string.Create(CultureInfo.InvariantCulture, $"{_flow.SampleRate} Hz"),
                    "Set it on the Measure tab; it has to match the rate the devices are running at."),
                (IsUsable(_flow.Sweep),
                    "Sweep settings",
                    _flow.SweepText,
                    "20-150 Hz for 1 s is the default, not a constant: the band is a setting the wizard can set."),
                (areaUsable,
                    "Measurement area",
                    string.Create(CultureInfo.InvariantCulture,
                        $"{_flow.WidthMetres:0.##} x {_flow.DepthMetres:0.##} x {_flow.HeightMetres:0.##} m"),
                    "The room the grid covers, in metres."),
                (session is not null,
                    "Generate points",
                    _flow.GridText,
                    "Press Start / resume session: one slot is created per grid point per mode."),
                (IsModeComplete(session, SubMode.A),
                    "Sub A",
                    ModeDetail(session, SubMode.A),
                    "Measure each point with Space or Enter; the cursor walks A, then B, then A+B."),
                (IsModeComplete(session, SubMode.B),
                    "Sub B",
                    ModeDetail(session, SubMode.B),
                    "The same positions, sub B only."),
                (IsModeComplete(session, SubMode.AB),
                    "Sub A+B",
                    ModeDetail(session, SubMode.AB),
                    "Both subs together — the pass the optimisation is checked against."),
                (_flow.MeasuredCount > 0,
                    "Analysis",
                    _flow.MeasuredText,
                    "The Analysis tab draws the level, spread and overlay figures from the measured slots."),
                (paired > 0,
                    "Optimize",
                    paired == 0
                        ? "no point has both A and B measured yet"
                        : $"{paired} of {session!.Grid.PointCount} points have A and B measured",
                    "Press Run search on the Optimize tab."),
                (_optimizer.Result is not null,
                    "Results",
                    _optimizer.Result is null ? "no optimisation run yet" : _optimizer.RecommendationText,
                    "The recommendation, its binding constraint and the structured causes are on the Optimize tab."),
            ];

            int current = 0;
            while (current < rows.Length && rows[current].Done) current++;

            return [.. rows.Select((row, index) =>
                new WizardStep(index + 1, row.Title, row.Detail, row.Cue, row.Done, index == current))];
        }
    }

    /// <summary>The line the wizard leads with: the first step that is not done, and the action it names.</summary>
    public string GuideText
    {
        get
        {
            IReadOnlyList<WizardStep> steps = Steps;
            return steps.FirstOrDefault(step => step.IsCurrent) is { } current
                ? $"Step {current.Number} of {steps.Count}: {current.Title} — {current.Cue}"
                : $"All {steps.Count} steps are done: the project has a measured grid and an optimisation result.";
        }
    }

    private void Refresh()
    {
        Raise(nameof(Steps));
        Raise(nameof(GuideText));
    }

    /// <summary>
    /// Calls the type's own rule instead of restating it: a second copy of the validation is a second thing to
    /// drift away from it. <c>SweepSettings.Validate</c> is the only place that decides what a usable sweep is.
    /// </summary>
    private static bool IsUsable(SweepSettings sweep)
    {
        try
        {
            sweep.Validate();
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// A mode's step is done when nothing in it is still waiting on the user: every slot is Done or deliberately
    /// Skipped, and a skip is a recorded decision rather than a shortfall, so it does not hold the guide back.
    /// </summary>
    private static bool IsModeComplete(MeasurementSession? session, SubMode mode)
    {
        if (session is not { } open) return false;
        (int total, int done, int skipped) = ModeCounts(open, mode);
        return total > 0 && total == done + skipped;
    }

    /// <summary>"12 of 27 measured", with the skips named separately when there are any.</summary>
    private static string ModeDetail(MeasurementSession? session, SubMode mode)
    {
        if (session is not { } open) return "no session yet";
        (int total, int done, int skipped) = ModeCounts(open, mode);
        return skipped == 0 ? $"{done} of {total} measured" : $"{done} of {total} measured, {skipped} skipped";
    }

    private static (int Total, int Done, int Skipped) ModeCounts(MeasurementSession session, SubMode mode)
    {
        IReadOnlyList<MeasurementSlot> slots = [.. session.Slots.Where(slot => slot.Mode == mode)];
        return (
            slots.Count,
            slots.Count(slot => slot.State == MeasurementSlotState.Done),
            slots.Count(slot => slot.State == MeasurementSlotState.Skipped));
    }

    /// <summary>
    /// The points the optimiser can actually use, counted from the session rather than from the panel's own
    /// readiness flag: <c>OptimizerPanelViewModel.Refresh</c> documents reading the measured A and B passes and
    /// pairing them by point, and the search predicts A+B from its model rather than reading a measured A+B. So
    /// A+B is what this step needs, and asking the flow keeps the guide honest before anyone opens that tab.
    /// </summary>
    private static int PairedPointCount(MeasurementSession? session)
        => session is null
            ? 0
            : session.Grid.Points.Count(point =>
                session.Slots.Any(slot => slot.Mode == SubMode.A && slot.Point.Id == point.Id && slot.State == MeasurementSlotState.Done)
                && session.Slots.Any(slot => slot.Mode == SubMode.B && slot.Point.Id == point.Id && slot.State == MeasurementSlotState.Done));
}
