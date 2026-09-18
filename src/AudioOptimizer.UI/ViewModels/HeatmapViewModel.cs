namespace AudioOptimizer.UI.ViewModels;

using AudioOptimizer.Core;
using AudioOptimizer.Measurement;
using AudioOptimizer.Visualization;

/// <summary>
/// M4: the heatmap panel's state. One plane per analysed height, plus a frequency × position map, built from the
/// session's measured points.
/// <para>
/// Two rules are load-bearing here and neither is a presentation choice:
/// </para>
/// <para>
/// The <b>frequency</b> offered is a request; the bin used is what the analysis actually has. <see cref="BinSnap"/>
/// resolves it against the bin list read from the measurement's own frequency responses, and the text shows both
/// numbers, so the figure can never display a frequency it did not measure.
/// </para>
/// <para>
/// The <b>level context</b> is derived once, from every plane's cells at the selected bin, and passed to every plane.
/// Per-plane contexts would normalise each plane to its own maximum, so a dimmer plane could render brighter — the
/// same silent substitution the curve overlay avoids, one level up and harder to see.
/// </para>
/// </summary>
public sealed class HeatmapViewModel : ObservableObject
{
    private readonly LevelReference _colourReference;

    private MeasurementFlowViewModel? _flow;
    private double[] _binFrequencies = [];
    private IReadOnlyList<double> _requests = [];
    private double _requestedHz;
    private bool _interpolated;
    private string _message = "Start or resume a session to map measured levels.";
    private string _frequencyText = string.Empty;
    private IReadOnlyList<Heatmap> _planes = [];
    private Heatmap? _positionMap;

    /// <summary>The requested frequencies this analysis can offer — derived from the band, not a fixed list.</summary>
    public IReadOnlyList<double> Frequencies => _requests;

    public double RequestedHz
    {
        get => _requestedHz;
        set
        {
            if (!Set(ref _requestedHz, value)) return;
            Rebuild();
        }
    }

    /// <summary>§23: interpolated cells are denser than the measured grid, and the figure says so.</summary>
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

    /// <summary>Both numbers: what was requested and the bin the analysis actually has. Never a request shown as a measurement.</summary>
    public string FrequencyText
    {
        get => _frequencyText;
        private set => Set(ref _frequencyText, value);
    }

    /// <summary>One per analysed height, in the grid's own Z order.</summary>
    public IReadOnlyList<Heatmap> Planes
    {
        get => _planes;
        private set => Set(ref _planes, value);
    }

    public Heatmap? PositionMap
    {
        get => _positionMap;
        private set => Set(ref _positionMap, value);
    }

    public int PlaneCount => Planes.Count;

    /// <summary>
    /// The reference every plane's colours <b>and</b> the colour bar's legend come from. Production always uses the
    /// pipeline's own reference; the parameter exists because the two artefacts must be proven to move together when
    /// the source changes, and a test can only prove that by changing it.
    /// </summary>
    public HeatmapViewModel(LevelReference? colourReference = null)
    {
        _colourReference = colourReference ?? LevelReference.RelativeToBandMean;
    }

    /// <summary>The scale actually used by the planes, so a test can read the fractions the cells were mapped through.</summary>
    public ColourScale? Scale => Planes.Count > 0 ? Planes[0].Scale : null;

    /// <summary>Reports a failure on this path as text, so a tab switch can never crash the window.</summary>
    public void ReportFailure(string message) => Message = message;

    public void Refresh(MeasurementFlowViewModel flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        _flow = flow;
        Rebuild();
    }

    private void Rebuild()
    {
        MeasurementSession? session = _flow?.Session;
        if (session is null)
        {
            Reset("Start or resume a session to map measured levels.");
            return;
        }

        MeasurementGrid grid = session.Grid;
        IReadOnlyList<MeasurementSlot> measured = [.. session.Slots.Where(slot => slot.State == MeasurementSlotState.Done && session.InBandResponseOf(slot) is { Length: > 0 })];
        if (measured.Count == 0)
        {
            Reset("No measured points yet — measure a point, then map its level.");
            return;
        }

        // The analysis's own IN-BAND bins, echoed rather than recomputed: the sweep excited nothing outside the band, so
        // the selector, the ladder, the plane rows and the position rows all come from this one list.
        _binFrequencies = [.. session.InBandResponseOf(measured[0])!.Select(bin => bin.FrequencyHz)];
        _requests = BinSnap.Ladder(_binFrequencies, 5.0);
        if (!_requests.Contains(_requestedHz)) _requestedHz = _requests[0];
        Raise(nameof(Frequencies));

        (int _, double achieved) = BinSnap.Snap(_binFrequencies, _requestedHz);
        FrequencyText = $"requested {_requestedHz:F1} Hz → analysis bin {achieved:F4} Hz"
            + (Math.Abs(achieved - _requestedHz) < 1e-9 ? " (exact)" : $" (nearest of {_binFrequencies.Length} bins)");

        // Groups are the grid's heights: one plane per Z, in Z order, whatever CountZ is.
        var byHeight = measured
            .GroupBy(slot => slot.Point.GridZ)
            .OrderBy(group => group.Key)
            .ToList();

        // ONE context for every plane, from every plane's cells at the selected bin.
        var allLevels = new List<double>();
        var planeRows = new List<IReadOnlyList<IReadOnlyList<double>>>();
        foreach (IGrouping<int, MeasurementSlot> height in byHeight)
        {
            double[][] rows = Grid(group: height, session, achieved);
            planeRows.Add(rows);
            allLevels.AddRange(rows.SelectMany(row => row));
        }

        LevelContext context = Heatmaps.SharedContext(allLevels);
        var planes = new List<Heatmap>();
        for (int index = 0; index < planeRows.Count; index++)
        {
            double z = byHeight[index].First().Point.Z;
            Heatmap plane = Heatmaps.Matrix(
                $"Measured level at height Z {z:F2} m, {achieved:F2} Hz",
                "width (m)",
                "depth (m)",
                planeRows[index],
                _colourReference,
                context);
            planes.Add(_interpolated ? Heatmaps.Interpolated(plane) : plane);
        }

        // §22.3: frequency × position. Its own figure, so its own context: it is a different matrix with different axes.
        var positionRows = new List<IReadOnlyList<double>>();
        foreach (MeasurementSlot slot in measured.OrderBy(slot => slot.Point.GridZ).ThenBy(slot => slot.Point.Id, StringComparer.Ordinal))
            positionRows.Add([.. session.InBandResponseOf(slot)!.Select(bin => bin.MagnitudeDb)]);
        Heatmap map = Heatmaps.Matrix(
            "Level by frequency and position",
            "frequency (Hz)",
            "position (measured point)",
            positionRows,
            _colourReference,
            Heatmaps.SharedContext(positionRows.SelectMany(row => row)));
        PositionMap = _interpolated ? Heatmaps.Interpolated(map) : map;

        Planes = planes;
        Message = $"{measured.Count} measured point(s) over {grid.CountZ} height(s); requested band {session.Band}, "
            + $"{_binFrequencies.Length} bins {_binFrequencies[0]:F4}–{_binFrequencies[^1]:F4} Hz; scale {_colourReference.AxisLabel}.";
    }

    /// <summary>One height's rows, laid out on the grid's own index axes so the plane matches the room.</summary>
    private static double[][] Grid(IGrouping<int, MeasurementSlot> group, MeasurementSession session, double achievedHz)
    {
        List<MeasurementSlot> slots = [.. group];
        int columns = slots.Max(slot => slot.Point.GridX) - slots.Min(slot => slot.Point.GridX) + 1;
        int rows = slots.Max(slot => slot.Point.GridY) - slots.Min(slot => slot.Point.GridY) + 1;
        double[][] levels = [.. Enumerable.Range(0, rows).Select(_ => new double[columns])];
        foreach (MeasurementSlot slot in slots)
        {
            int column = slot.Point.GridX - slots.Min(s => s.Point.GridX);
            int row = slot.Point.GridY - slots.Min(s => s.Point.GridY);
            FrequencyResponse bin = session.InBandResponseOf(slot)!.OrderBy(response => Math.Abs(response.FrequencyHz - achievedHz)).First();
            levels[row][column] = bin.MagnitudeDb;
        }

        return levels;
    }

    private void Reset(string message)
    {
        _binFrequencies = [];
        _requests = [];
        Planes = [];
        PositionMap = null;
        FrequencyText = string.Empty;
        Message = message;
        Raise(nameof(Frequencies));
    }
}
