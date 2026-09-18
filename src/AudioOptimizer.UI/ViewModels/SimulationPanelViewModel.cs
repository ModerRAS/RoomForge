namespace AudioOptimizer.UI.ViewModels;

using System.Globalization;
using AudioOptimizer.Core;
using AudioOptimizer.Measurement;
using AudioOptimizer.Optimization;
using AudioOptimizer.Simulation;
using AudioOptimizer.Visualization;

/// <summary>
/// The Offline Simulation page: the Virtual Acoustic Lab with the same knobs a person would want to turn, and the
/// product's own figures showing what came out.
/// <para>
/// It owns no physics and no DSP. The run is <see cref="SimulationRunner"/>, which drives the SHIPPED measurement chain
/// through a virtual <c>IAudioBackend</c>, and every figure below the controls is built by
/// <see cref="ResponseCurves"/> and <see cref="Heatmaps"/> — the same builders the analysis and heatmap pages use. The
/// asynchrony lives here, exactly as the measurement flow's does, because the law layers are deliberately thread-free
/// (<c>ThreadFreeLayersTests</c>).
/// </para>
/// <para>
/// The editable fields are positions and settings, never the scenario's own ground truth: selecting a scenario loads
/// its numbers, and moving a sub or changing the room edits the experiment without rewriting the answer it will be
/// compared against.
/// </para>
/// </summary>
public sealed class SimulationPanelViewModel : ObservableObject
{
    private static readonly LevelReference Reference = LevelReference.RelativeToBandMean;

    private SimulationScenario _scenario = SimulationScenarios.SingleSub;
    private double _roomLength = SimulationScenarios.SingleSub.Config.Room.LengthMetres;
    private double _roomWidth = SimulationScenarios.SingleSub.Config.Room.WidthMetres;
    private double _roomHeight = SimulationScenarios.SingleSub.Config.Room.HeightMetres;
    private double _subAX, _subAY, _subAZ, _subBX, _subBY, _subBZ;
    private double _regionWidth = SimulationScenarios.SingleSub.Config.ListeningRegion.WidthMetres;
    private double _regionDepth = SimulationScenarios.SingleSub.Config.ListeningRegion.DepthMetres;
    private double _regionHeight = SimulationScenarios.SingleSub.Config.ListeningRegion.HeightMetres;
    private double _startHz = SimulationScenarios.SingleSub.Config.SweepStartHz;
    private double _endHz = SimulationScenarios.SingleSub.Config.SweepEndHz;
    private double _durationSeconds = SimulationScenarios.SingleSub.Config.SweepSeconds;
    private double _noiseLevel = SimulationScenarios.SingleSub.Config.MicrophoneNoiseLevel;
    private double _heatmapFrequencyHz = 50.0;
    private bool _isBusy;
    private string _status = "Pick a scenario and press Generate simulation. Nothing is measured and no audio device is opened.";
    private string _pipelineText = string.Empty;
    private string _statsText = string.Empty;
    private string _optimizerText = "No search has run.";
    private string _groundTruthText = string.Empty;
    private IReadOnlyList<CurvePlot> _plots = [];
    private IReadOnlyList<Heatmap> _planes = [];

    public SimulationPanelViewModel()
    {
        GenerateCommand = new RelayCommand(async () => await GenerateAsync());
        LoadScenario(SimulationScenarios.SingleSub);
    }

    public RelayCommand GenerateCommand { get; }

    public IReadOnlyList<SimulationScenario> Scenarios => SimulationScenarios.All;

    /// <summary>Which experiment the page is set up for. Selecting one loads its numbers into every field below.</summary>
    public SimulationScenario Scenario
    {
        get => _scenario;
        set
        {
            if (value is null || ReferenceEquals(value, _scenario)) return;
            LoadScenario(value);
            Raise(nameof(Scenario));
        }
    }

    public double RoomLength { get => _roomLength; set => Set(ref _roomLength, value); }

    public double RoomWidth { get => _roomWidth; set => Set(ref _roomWidth, value); }

    public double RoomHeight { get => _roomHeight; set => Set(ref _roomHeight, value); }

    public double SubAX { get => _subAX; set => Set(ref _subAX, value); }

    public double SubAY { get => _subAY; set => Set(ref _subAY, value); }

    public double SubAZ { get => _subAZ; set => Set(ref _subAZ, value); }

    public double SubBX { get => _subBX; set => Set(ref _subBX, value); }

    public double SubBY { get => _subBY; set => Set(ref _subBY, value); }

    public double SubBZ { get => _subBZ; set => Set(ref _subBZ, value); }

    public double RegionWidth { get => _regionWidth; set => Set(ref _regionWidth, value); }

    public double RegionDepth { get => _regionDepth; set => Set(ref _regionDepth, value); }

    public double RegionHeight { get => _regionHeight; set => Set(ref _regionHeight, value); }

    public double StartHz { get => _startHz; set => Set(ref _startHz, value); }

    public double EndHz { get => _endHz; set => Set(ref _endHz, value); }

    public double DurationSeconds { get => _durationSeconds; set => Set(ref _durationSeconds, value); }

    public double NoiseLevel { get => _noiseLevel; set => Set(ref _noiseLevel, value); }

    /// <summary>The frequency the 3 × 3 planes are drawn at; clamped into the sweep's band when the figures are built.</summary>
    public double HeatmapFrequencyHz { get => _heatmapFrequencyHz; set => Set(ref _heatmapFrequencyHz, value); }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value)) Raise(nameof(CanGenerate));
        }
    }

    public bool CanGenerate => !IsBusy;

    public string Status { get => _status; private set => Set(ref _status, value); }

    /// <summary>What the shipped quality checks made of every point, per configuration.</summary>
    public string PipelineText { get => _pipelineText; private set => Set(ref _pipelineText, value); }

    /// <summary>The band's spatial numbers: mean, median, σ, min, max, range, P10, P90, P90 − P10.</summary>
    public string StatsText { get => _statsText; private set => Set(ref _statsText, value); }

    public string OptimizerText { get => _optimizerText; private set => Set(ref _optimizerText, value); }

    /// <summary>The scenario's own answer, which the optimizer never saw, beside what the optimizer found.</summary>
    public string GroundTruthText { get => _groundTruthText; private set => Set(ref _groundTruthText, value); }

    public IReadOnlyList<CurvePlot> Plots { get => _plots; private set => Set(ref _plots, value); }

    public IReadOnlyList<Heatmap> Planes { get => _planes; private set => Set(ref _planes, value); }

    /// <summary>The experiment as the fields currently describe it. No measurement, no optimizer: pure configuration.</summary>
    public SimulationScenario Configured()
    {
        RoomModel room = _scenario.Config.Room with
        {
            LengthMetres = RoomLength,
            WidthMetres = RoomWidth,
            HeightMetres = RoomHeight,
        };

        ListeningRegion region = ListeningRegion.CentredIn(
            room, RegionWidth, RegionDepth, RegionHeight, _scenario.Config.ListeningRegion.CentreZ);

        SimulationConfig config = _scenario.Config with
        {
            Room = room,
            ListeningRegion = region,
            SweepStartHz = StartHz,
            SweepEndHz = EndHz,
            SweepSeconds = DurationSeconds,
            MicrophoneNoiseLevel = NoiseLevel,
        };

        // Positions are the user's; gain, polarity, phase and delay stay the scenario's own — they are the ground truth
        // this experiment is built to be compared against.
        var subs = new List<VirtualSubwoofer>();
        Position? positionA = _scenario.Subs.Count > 0 ? new Position(SubAX, SubAY, SubAZ) : null;
        if (positionA is { } a) subs.Add(_scenario.Subs[0] with { Position = a });
        if (_scenario.Subs.Count > 1) subs.Add(_scenario.Subs[1] with { Position = new Position(SubBX, SubBY, SubBZ) });

        // A text box is a trust boundary: a sub outside its own room is not a hard case for the image-source sum, it is a
        // nonsense experiment, and the honest answer is to say so instead of drawing a plausible figure for it.
        foreach (VirtualSubwoofer sub in subs)
            if (sub.Position.X < 0 || sub.Position.X > room.LengthMetres
                || sub.Position.Y < 0 || sub.Position.Y > room.WidthMetres
                || sub.Position.Z < 0 || sub.Position.Z > room.HeightMetres)
                throw new InvalidOperationException(
                    $"a sub at {sub.Position} is outside the {room.LengthMetres} × {room.WidthMetres} × {room.HeightMetres} m room");

        IReadOnlyList<MeasurementPoint> microphones = _scenario.Microphones.Count == 1
            ? [region.Points.Single(point => point.Id == "x0_y0_z0")]
            : region.Points;

        return _scenario with { Config = config, Subs = subs, Microphones = microphones };
    }

    /// <summary>
    /// Runs the configured experiment off the UI thread, then fills in the figures. The work runs on a worker thread
    /// and every assignment below happens back on the caller's context, which is the same shape the measurement flow
    /// uses.
    /// </summary>
    public async Task GenerateAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        Status = "Simulating…";
        try
        {
            SimulationScenario scenario = Configured();
            SimulationOutcome outcome = await Task.Run(() => SimulationRunner.Run(scenario)).ConfigureAwait(true);
            Apply(outcome);
        }
        catch (Exception exception)
        {
            Status = $"Simulation failed: {exception.Message}";
            PipelineText = string.Empty;
            StatsText = string.Empty;
            OptimizerText = "No search has run.";
            GroundTruthText = string.Empty;
            Plots = [];
            Planes = [];
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Apply(SimulationOutcome outcome)
    {
        SimulationMeasurementSummary summary = outcome.MeasuredSummary;
        PipelineText = $"{summary.Count} points measured through the shipped chain — "
            + string.Join(", ", summary.Verdicts.Select(verdict => $"{verdict.Mode}: {verdict.Verdict}"));
        StatsText = Describe(BandSpatialStats.Of(outcome.Measured));
        OptimizerText = DescribeOptimizer(outcome.Optimization);
        GroundTruthText = DescribeGroundTruth(outcome);
        Plots = Curves(outcome);
        Planes = PlanesAt(outcome, HeatmapFrequencyHz);
        Status = $"Done: {summary.Count} measurements, {outcome.Measured.PerFrequency.Count} bins inside {outcome.Measured.AnalysisBand}."
            + (outcome.Optimization is { } result ? $" Search verdict: {result.Verdict}." : string.Empty);
    }

    private void LoadScenario(SimulationScenario scenario)
    {
        _scenario = scenario;
        RoomLength = scenario.Config.Room.LengthMetres;
        RoomWidth = scenario.Config.Room.WidthMetres;
        RoomHeight = scenario.Config.Room.HeightMetres;
        RegionWidth = scenario.Config.ListeningRegion.WidthMetres;
        RegionDepth = scenario.Config.ListeningRegion.DepthMetres;
        RegionHeight = scenario.Config.ListeningRegion.HeightMetres;
        StartHz = scenario.Config.SweepStartHz;
        EndHz = scenario.Config.SweepEndHz;
        DurationSeconds = scenario.Config.SweepSeconds;
        NoiseLevel = scenario.Config.MicrophoneNoiseLevel;
        HeatmapFrequencyHz = Math.Clamp(HeatmapFrequencyHz, StartHz, EndHz);

        if (scenario.Subs.Count > 0) (SubAX, SubAY, SubAZ) = (scenario.Subs[0].Position.X, scenario.Subs[0].Position.Y, scenario.Subs[0].Position.Z);
        if (scenario.Subs.Count > 1) (SubBX, SubBY, SubBZ) = (scenario.Subs[1].Position.X, scenario.Subs[1].Position.Y, scenario.Subs[1].Position.Z);

        Status = $"{scenario.Id}: {scenario.Description}";
    }

    /// <summary>
    /// The band's nine numbers, in the order the page labels them. Static so it can be checked without running a
    /// simulation — the wording and the arithmetic are the whole content of this string.
    /// </summary>
    public static string Describe(BandSpatialStats stats)
    {
        ArgumentNullException.ThrowIfNull(stats);
        return $"mean {F(stats.MeanDb)} dB, median {F(stats.MedianDb)} dB, σ {F(stats.StdDevDb)} dB, "
            + $"min {F(stats.MinDb)} dB, max {F(stats.MaxDb)} dB, range {F(stats.RangeDb)} dB, "
            + $"P10 {F(stats.P10Db)} dB, P90 {F(stats.P90Db)} dB, P90−P10 {F(stats.P90P10Db)} dB";
    }

    /// <summary>The optimizer's answer, in its own terms: verdict, recommendation, boost used against the limit.</summary>
    public static string DescribeOptimizer(OptimizerResult? result)
    {
        if (result is null) return "No search has run.";

        string recommendation = result.Recommended is { } setting
            ? $"recommended {F(setting.GainDb)} dB, {F(setting.PhaseDegrees)}°, polarity {setting.Polarity:+#;-#;+1}, "
                + $"{F(setting.DelaySeconds * 1000.0)} ms"
            : "no setting within the limit";

        return $"{result.Verdict}; {recommendation}; spatial σ {F(result.Before.MeanStdDevDb)} → {F(result.After.MeanStdDevDb)} dB; "
            + $"boost {F(result.Constraint.MaxAchievedBoostDb ?? 0.0)} dB of {F(result.Constraint.MaxBoostLimitDb)} dB allowed"
            + (result.Constraint.Binding ? " (the limit bound the search)" : " (the limit did not bind)")
            + $"; {result.Constraint.CandidatesRejected} of {result.Constraint.CandidatesEvaluated} candidates rejected";
    }

    /// <summary>What the scenario knows and the optimizer was never told.</summary>
    public static string DescribeGroundTruth(SimulationOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (outcome.Scenario.GroundTruthSetting is not { } truth)
            return outcome.Optimization is null
                ? "This scenario declares no known correction."
                : "This scenario declares no known correction — the search's own before/after above is the whole answer.";

        string known = $"{F(truth.GainDb)} dB, {F(truth.PhaseDegrees)}°, polarity {truth.Polarity:+#;-#;+1}, "
            + $"{F(truth.DelaySeconds * 1000.0)} ms";
        string achieved = outcome.GroundTruthReference is { } reference
            ? $" It reaches {Describe(BandSpatialStats.Of(reference))}."
            : string.Empty;
        return $"known correction {known}; the optimizer never saw it.{achieved}";
    }

    /// <summary>
    /// One level figure per configuration the rig measured: every position's own curve, the mean, and the ±1σ ribbon,
    /// all against the one reference this page declares.
    /// </summary>
    public static IReadOnlyList<CurvePlot> Curves(SimulationOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        var plots = new List<CurvePlot>();
        foreach (SubMode mode in Enum.GetValues<SubMode>())
        {
            IReadOnlyList<PositionResponse> positions = Positions(outcome, mode);
            if (positions.Count == 0) continue;

            SpatialSummary summary = SpatialMetrics.Compute(positions);
            var levels = new SpatialLevels(
                [.. summary.PerFrequency.Select(row => row.FrequencyHz)],
                [.. summary.PerFrequency.Select(row => row.MeanDb)],
                [.. summary.PerFrequency.Select(row => row.StdDevDb)],
                [.. summary.PerFrequency.Select(row => row.RangeDb)],
                summary.PerFrequency.Max(row => row.MaxDb),
                positions.Count);

            IReadOnlyList<PositionLevels> perPosition =
                [.. positions.Select(position => new PositionLevels(position.PointId, [.. position.Bins.Select(bin => bin.MagnitudeDb)]))];
            plots.Add(ResponseCurves.Spatial(levels, perPosition, Reference,
                $"{outcome.Scenario.Id} — {mode}, {positions.Count} position(s)"));
        }

        return plots;
    }

    /// <summary>
    /// One 3 × 3 plane per height in the measured grid, at the bin nearest <paramref name="frequencyHz"/>. All the
    /// planes share one colour context, so a brighter cell always means a louder level; nothing here is interpolated,
    /// which is why no plane claims to be.
    /// </summary>
    public static IReadOnlyList<Heatmap> PlanesAt(SimulationOutcome outcome, double frequencyHz)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        SubMode mode = outcome.Measurements.Any(measurement => measurement.Mode == SubMode.AB) ? SubMode.AB : SubMode.A;
        IReadOnlyList<SimulatedMeasurement> measured = [.. outcome.Measurements.Where(measurement => measurement.Mode == mode)];
        if (measured.Count == 0) return [];

        var heights = new SortedSet<int>(measured.Select(measurement => measurement.Point.GridZ));
        Dictionary<(int X, int Y, int Z), double> levelAt = [];
        foreach (SimulatedMeasurement measurement in measured)
        {
            FrequencyResponse bin = measurement.Result.Response
                .OrderBy(response => Math.Abs(response.FrequencyHz - frequencyHz)).First();
            levelAt[(measurement.Point.GridX, measurement.Point.GridY, measurement.Point.GridZ)] = bin.MagnitudeDb;
        }

        LevelContext context = Heatmaps.SharedContext(levelAt.Values);
        var planes = new List<Heatmap>(heights.Count);
        foreach (int z in heights)
        {
            var rows = new List<IReadOnlyList<double>>();
            for (int y = -1; y <= 1; y++)
            {
                var row = new List<double>(3);
                for (int x = -1; x <= 1; x++)
                    if (levelAt.TryGetValue((x, y, z), out double level)) row.Add(level);
                if (row.Count == 3) rows.Add(row);
            }

            if (rows.Count != 3) continue;                       // a plane that is not a full 3 × 3 is not a plane
            planes.Add(Heatmaps.Matrix(
                $"{mode} at {frequencyHz:0.#} Hz, height level z{z}",
                "x (25/50/75 %)",
                "y (25/50/75 %)",
                rows,
                Reference,
                context));
        }

        return planes;
    }

    private static IReadOnlyList<PositionResponse> Positions(SimulationOutcome outcome, SubMode mode)
        => [.. outcome.Measurements
            .Where(measurement => measurement.Mode == mode)
            .Select(measurement => new PositionResponse(
                measurement.Point.Id,
                FrequencyBand.Of(outcome.Scenario.Config.Sweep),
                MeasurementSession.InBand(measurement.Result.Response, FrequencyBand.Of(outcome.Scenario.Config.Sweep))))
            .Where(position => position.Bins.Count > 0)];

    private static string F(double value) => value.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture);
}
