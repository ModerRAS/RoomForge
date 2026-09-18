namespace AudioOptimizer.UI.ViewModels;

using AudioOptimizer.Core;
using AudioOptimizer.Measurement;
using AudioOptimizer.Optimization;
using AudioOptimizer.Visualization;

/// <summary>
/// M3: the analysis panel's state. It turns the measurement flow's current session into the four §22 figures —
/// levels with the mean and its ±1σ ribbon, spatial standard deviation, max − min, and a before/after overlay —
/// and it holds no plot math: the curves come from <see cref="ResponseCurves"/>, the spread numbers from
/// <see cref="SpatialMetrics"/>. It also touches no audio device; it reads what the session has already stored.
/// <para>
/// The level reference is deliberately one constant here (<see cref="LevelReference.RelativeToBandMean"/>, the
/// pipeline's own reference) so that no view can claim a reference it did not use: the label beside the numbers is
/// the reference's own <see cref="LevelReference.AxisLabel"/>. No path in this app can produce the calibrated SPL
/// reference, which is unconstructable without a microphone calibration and must stay so until one exists.
/// </para>
/// </summary>
public sealed class AnalysisViewModel : ObservableObject
{
    private static readonly LevelReference CurveReference = LevelReference.RelativeToBandMean;

    private MeasurementFlowViewModel? _flow;
    private SubMode _mode = SubMode.A;
    private bool _modeChosen;
    private bool _showAllPositions;
    private bool _interpolated;
    private string _message = "Start or resume a session to analyse measured points.";
    private int _positionCount;
    private CurvePlot? _levelsPlot;
    private CurvePlot? _stdDevPlot;
    private CurvePlot? _rangePlot;
    private CurvePlot? _overlayPlot;

    public IReadOnlyList<SubMode> Modes { get; } = [SubMode.A, SubMode.B, SubMode.AB];

    /// <summary>Which subwoofer configuration is being analysed. Changing it re-reads the session.</summary>
    public SubMode Mode
    {
        get => _mode;
        set
        {
            if (!Set(ref _mode, value)) return;
            _modeChosen = true;
            Rebuild();
        }
    }

    /// <summary>§22.1: the figure is one or the other, and which one is a state the user can see.</summary>
    public bool ShowAllPositions
    {
        get => _showAllPositions;
        set => Set(ref _showAllPositions, value);
    }

    /// <summary>§23: interpolated samples, and the figure says so rather than looking like measured resolution.</summary>
    public bool Interpolated
    {
        get => _interpolated;
        set
        {
            if (Set(ref _interpolated, value)) Rebuild();
        }
    }

    public string Message
    {
        get => _message;
        private set => Set(ref _message, value);
    }

    public int PositionCount
    {
        get => _positionCount;
        private set => Set(ref _positionCount, value);
    }

    /// <summary>The wording that must accompany every level number in this panel.</summary>
    public string LevelAxisLabel => CurveReference.AxisLabel;

    public CurvePlot? LevelsPlot
    {
        get => _levelsPlot;
        private set => Set(ref _levelsPlot, value);
    }

    public CurvePlot? StdDevPlot
    {
        get => _stdDevPlot;
        private set => Set(ref _stdDevPlot, value);
    }

    public CurvePlot? RangePlot
    {
        get => _rangePlot;
        private set => Set(ref _rangePlot, value);
    }

    public CurvePlot? OverlayPlot
    {
        get => _overlayPlot;
        private set => Set(ref _overlayPlot, value);
    }

    /// <summary>
    /// Re-reads the session the measurement flow currently holds. Called when the analysis tab is shown rather than
    /// on every measurement, so a figure is built from a settled session and never from a half-written one.
    /// </summary>
    public void Refresh(MeasurementFlowViewModel flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        _flow = flow;
        if (!_modeChosen) _mode = BestMode(flow);
        Rebuild();
    }

    /// <summary>Reports a failure on the analysis path as text, so a tab switch can never crash the window.</summary>
    public void ReportFailure(string message) => Message = message;

    private void Rebuild()
    {
        MeasurementSession? session = _flow?.Session;
        if (session is null)
        {
            PositionCount = 0;
            LevelsPlot = StdDevPlot = RangePlot = OverlayPlot = null;
            Message = "Start or resume a session to analyse measured points.";
            return;
        }

        IReadOnlyList<PositionResponse> positions = Measured(session, _mode);
        PositionCount = positions.Count;
        if (positions.Count == 0)
        {
            LevelsPlot = StdDevPlot = RangePlot = OverlayPlot = null;
            Message = $"No measured points in mode {_mode} yet — measure a point, or pick another configuration.";
            return;
        }

        SpatialSummary spread = SpatialMetrics.Compute(positions);
        SpatialLevels levels = Summary(spread, positions);
        LevelsPlot = Figure(ResponseCurves.Spatial(levels, PositionLevels(positions), CurveReference, $"Measured levels, mode {_mode}"));
        StdDevPlot = Figure(ResponseCurves.Spread(levels, CurveRole.StdDev, "Spread across positions"));
        RangePlot = Figure(ResponseCurves.Spread(levels, CurveRole.Range, "Max − min across positions"));

        IReadOnlyList<PositionResponse> a = Measured(session, SubMode.A);
        IReadOnlyList<PositionResponse> b = Measured(session, SubMode.B);
        (IReadOnlyList<PositionResponse> pairedA, IReadOnlyList<PositionResponse> pairedB) = Pair(a, b);
        if (pairedA.Count > 0)
        {
            IReadOnlyList<PositionResponse> predicted = SubwooferModel.Combine(new DualSubMeasurement(pairedA, pairedB), SubwooferSetting.Baseline);
            OverlayPlot = Figure(ResponseCurves.Overlay(
                Summary(SpatialMetrics.Compute(pairedA), pairedA),
                Summary(SpatialMetrics.Compute(predicted), predicted),
                CurveReference,
                "predicted, gain 0.0 dB, polarity +1, delay 0.00 ms",
                "Before / after"));
        }
        else
        {
            OverlayPlot = null;
        }

        string overlayNote = OverlayPlot is null
            ? " The overlay needs both A and B measured on the same positions."
            : $" The overlay compares measured A with the model's A+B at the identity setting ({pairedA.Count} position(s)).";
        Message = $"{positions.Count} measured position(s), mode {_mode}: "
            + $"mean ±1σ by default, worst σ {spread.PerFrequency.Max(metrics => metrics.StdDevDb):F2} dB, "
            + $"worst max − min {spread.PerFrequency.Max(metrics => metrics.RangeDb):F2} dB.{overlayNote}";
    }

    private CurvePlot Figure(CurvePlot plot) => _interpolated ? ResponseCurves.Interpolated(plot) : plot;

    /// <summary>The seam between the optimizer's aggregation and the figure layer, which stays domain-free.</summary>
    private static SpatialLevels Summary(SpatialSummary summary, IReadOnlyList<PositionResponse> positions) => new(
        [.. summary.PerFrequency.Select(metrics => metrics.FrequencyHz)],
        [.. summary.PerFrequency.Select(metrics => metrics.MeanDb)],
        [.. summary.PerFrequency.Select(metrics => metrics.StdDevDb)],
        [.. summary.PerFrequency.Select(metrics => metrics.RangeDb)],
        positions.SelectMany(position => position.Bins).Max(bin => bin.MagnitudeDb),
        positions.Count);

    private static IReadOnlyList<PositionLevels> PositionLevels(IReadOnlyList<PositionResponse> positions)
        => [.. positions.Select(position => new PositionLevels(position.PointId, [.. position.Bins.Select(bin => bin.MagnitudeDb)]))];

    /// <summary>The measured positions of one mode, in the grid's own point order.</summary>
    private static IReadOnlyList<PositionResponse> Measured(MeasurementSession session, SubMode mode)
    {
        var positions = new List<PositionResponse>();
        foreach (MeasurementSlot slot in session.Slots)
        {
            if (slot.Mode != mode || slot.State != MeasurementSlotState.Done) continue;
            // In-band only: the sweep excited nothing outside it, so anything else would dilute a spatial summary or an
            // objective over a region with no measurement in it.
            if (session.InBandResponseOf(slot) is { Length: > 0 } bins)
                positions.Add(new PositionResponse(slot.Point.Id, session.Band, bins));
        }

        return positions;
    }

    /// <summary>Pairs A and B by point id, so a mode measured at different points cannot silently pair unrelated bins.</summary>
    private static (IReadOnlyList<PositionResponse> A, IReadOnlyList<PositionResponse> B) Pair(
        IReadOnlyList<PositionResponse> a,
        IReadOnlyList<PositionResponse> b)
    {
        Dictionary<string, PositionResponse> byId = b.ToDictionary(position => position.PointId);
        var pairedA = new List<PositionResponse>();
        var pairedB = new List<PositionResponse>();
        foreach (PositionResponse position in a)
        {
            if (!byId.TryGetValue(position.PointId, out PositionResponse? match)) continue;
            pairedA.Add(position);
            pairedB.Add(match);
        }

        return (pairedA, pairedB);
    }

    /// <summary>The mode with the most measured points, so the panel opens on real data when it can.</summary>
    private static SubMode BestMode(MeasurementFlowViewModel flow)
    {
        MeasurementSession? session = flow.Session;
        if (session is null) return SubMode.A;

        SubMode best = SubMode.A;
        int bestCount = -1;
        foreach (SubMode mode in new[] { SubMode.A, SubMode.B, SubMode.AB })
        {
            int count = Measured(session, mode).Count;
            if (count <= bestCount) continue;
            best = mode;
            bestCount = count;
        }

        return best;
    }
}
