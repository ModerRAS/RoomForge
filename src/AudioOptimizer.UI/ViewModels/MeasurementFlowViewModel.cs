namespace AudioOptimizer.UI.ViewModels;

using System.Globalization;
using System.Runtime.CompilerServices;
using System.IO;
using AudioOptimizer.Audio;
using AudioOptimizer.UI.Localization;
using AudioOptimizer.Core;
using AudioOptimizer.IO;
using AudioOptimizer.Measurement;

/// <summary>
/// M2: the measurement flow. It owns device selection, sweep settings, the grid, the session and the cursor, and
/// it is the ONLY place in the UI that touches the audio stack.
/// <para>
/// Every progress number here is a pure derivation over <c>Session.Slots</c> — there is no <c>_completed++</c>
/// anywhere, because an incremented counter can go stale across a skip, a retry or a resume and a computed
/// property cannot. The three numbers are different questions:
/// <list type="bullet">
/// <item><c>CursorText</c> — position of the next non-terminal slot over <c>Slots.Count</c> (where am I).</item>
/// <item><c>PositionText</c> — the cursor's grid point over <c>Grid.PointCount</c> (where in the room).</item>
/// <item><c>MeasuredText</c> — Done slots over <c>Slots.Count</c> (how much is finished).</item>
/// </list>
/// Skipping one point leaves the denominators unchanged and advances only the cursor; an Invalid slot still holds
/// the cursor because it is retryable.
/// </para>
/// <para>
/// §35: the DSP/IO/Core/Optimization layers stay synchronous and thread-free, so a measurement runs on a worker
/// thread through <see cref="Task.Run(Action)"/> and the continuation resumes on the caller's
/// context — the WPF dispatcher, because every entry point here is invoked from the UI thread. Raising
/// <c>PropertyChanged</c> off the dispatcher is a defect, not a style question; a test pins it. Abort is a flag
/// this class reads a completed run's verdict against, not an interruption of the device call: the runner owns its
/// thread and cannot be stopped from outside without widening the accepted <c>IAudioBackend</c> contract.
/// </para>
/// </summary>
public sealed class MeasurementFlowViewModel : ObservableObject
{
    /// <summary>Matches the smoke test's own defaults: half a second of room either side of the sweep.</summary>
    private const double PreRollSeconds = 0.5;

    private const double PostRollSeconds = 0.5;
    private const int LatencyMilliseconds = 100;

    private readonly IAudioBackend _backend;

    private IReadOnlyList<AudioDeviceInfo> _inputDevices = [];
    private IReadOnlyList<AudioDeviceInfo> _outputDevices = [];
    private AudioDeviceInfo? _selectedInput;
    private AudioDeviceInfo? _selectedOutput;
    private bool _devicesRead;
    private string _deviceMessage = UiText.Get("Flow.NoDeviceList");
    private string _projectDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) is { Length: > 0 } documents
            ? documents
            : Path.GetTempPath(),
        "RoomForge");
    private double _startHz = 20.0;
    private double _endHz = 150.0;
    private double _durationSeconds = 1.0;
    private int _sampleRate = 48000;
    private double _widthMetres = 1.8;
    private double _depthMetres = 1.0;
    private double _heightMetres = 0.6;
    private int _countX = 3;
    private int _countY = 3;
    private int _countZ = 3;
    private MeasurementSession? _session;
    private MeasurementRunner? _runner;
    private MeasurementSlot? _awaitingDecision;
    private bool _isBusy;
    private bool _isAborted;
    private string _status = UiText.Get("Flow.NoSession");

    public MeasurementFlowViewModel(IAudioBackend backend)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        RefreshDevicesCommand = new RelayCommand(RefreshDevices);
        StartSessionCommand = new RelayCommand(StartSession, () => !IsBusy);
        MeasureCommand = new RelayCommand(() => _ = MeasureAsync(), () => CanMeasure);
        SkipCommand = new RelayCommand(Skip, () => CanSkip);
        AbortCommand = new RelayCommand(Abort, () => Session is not null);
        RefreshCommands();
    }

    public RelayCommand RefreshDevicesCommand { get; }

    public RelayCommand StartSessionCommand { get; }

    /// <summary>Measures the cursor slot — including the one an Invalid verdict is waiting on, which is a retry.</summary>
    public RelayCommand MeasureCommand { get; }

    public RelayCommand SkipCommand { get; }

    public RelayCommand AbortCommand { get; }

    public IReadOnlyList<AudioDeviceInfo> InputDevices
    {
        get => _inputDevices;
        private set => Set(ref _inputDevices, value);
    }

    public IReadOnlyList<AudioDeviceInfo> OutputDevices
    {
        get => _outputDevices;
        private set => Set(ref _outputDevices, value);
    }

    /// <summary>The capture device (the measurement microphone).</summary>
    public AudioDeviceInfo? SelectedInput
    {
        get => _selectedInput;
        set
        {
            if (Set(ref _selectedInput, value)) RefreshCommands();
        }
    }

    /// <summary>The render device (playback). Separate from the input list on purpose: on a UMIK-1 + DAC rig a
    /// single combined list is how a capture gets opened on the wrong endpoint.</summary>
    public AudioDeviceInfo? SelectedOutput
    {
        get => _selectedOutput;
        set
        {
            if (Set(ref _selectedOutput, value)) RefreshCommands();
        }
    }

    public string DeviceMessage
    {
        get => _deviceMessage;
        private set => Set(ref _deviceMessage, value);
    }

    public string ProjectDirectory
    {
        get => _projectDirectory;
        set => Set(ref _projectDirectory, value ?? string.Empty);
    }

    public double StartHz
    {
        get => _startHz;
        set => SetPlan(ref _startHz, value);
    }

    public double EndHz
    {
        get => _endHz;
        set => SetPlan(ref _endHz, value);
    }

    public double DurationSeconds
    {
        get => _durationSeconds;
        set => SetPlan(ref _durationSeconds, value);
    }

    public int SampleRate
    {
        get => _sampleRate;
        set => SetPlan(ref _sampleRate, value);
    }

    public double WidthMetres
    {
        get => _widthMetres;
        set => SetPlan(ref _widthMetres, value);
    }

    public double DepthMetres
    {
        get => _depthMetres;
        set => SetPlan(ref _depthMetres, value);
    }

    public double HeightMetres
    {
        get => _heightMetres;
        set => SetPlan(ref _heightMetres, value);
    }

    public int CountX
    {
        get => _countX;
        set => SetPlan(ref _countX, value);
    }

    public int CountY
    {
        get => _countY;
        set => SetPlan(ref _countY, value);
    }

    public int CountZ
    {
        get => _countZ;
        set => SetPlan(ref _countZ, value);
    }

    /// <summary>The grid the current settings describe. Rebuilt on read; a 27-point grid is 27 because 3·3·3 is 27.</summary>
    public MeasurementGrid Grid => MeasurementGrid.Create(WidthMetres, DepthMetres, HeightMetres, CountX, CountY, CountZ);

    /// <summary>Compact sweep display: a record's ToString is not something to put on a screen.</summary>
    public string SweepText => UiText.IsChinese
        ? string.Create(CultureInfo.InvariantCulture, $"{StartHz:0.##}–{EndHz:0.##} Hz，{DurationSeconds:0.##} 秒，采样率 {SampleRate}")
        : string.Create(CultureInfo.InvariantCulture, $"{StartHz:0.##}–{EndHz:0.##} Hz, {DurationSeconds:0.##} s @ {SampleRate} Hz");

    /// <summary>Compact grid display: extents and the lattice, whose product is the point count.</summary>
    public string GridText => UiText.IsChinese
        ? string.Create(CultureInfo.InvariantCulture, $"{WidthMetres:0.##} × {DepthMetres:0.##} × {HeightMetres:0.##} 米，{CountX} × {CountY} × {CountZ}，共 {Grid.PointCount} 个点")
        : string.Create(CultureInfo.InvariantCulture, $"{WidthMetres:0.##} × {DepthMetres:0.##} × {HeightMetres:0.##} m, {CountX} × {CountY} × {CountZ} = {Grid.PointCount} points");

    public string SweepLine => UiText.Format("Measure.SweepLine", SweepText);

    public string GridLine => UiText.Format("Measure.GridLine", GridText);

    public SweepSettings Sweep => new(StartHz, EndHz, DurationSeconds, SampleRate);

    public MeasurementSession? Session => _session;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value))
            {
                RefreshCommands();
                RaiseAll();
            }
        }
    }

    /// <summary>Set when the user aborted: the run stopped, and the status says what was recorded and where.</summary>
    public bool IsAborted
    {
        get => _isAborted;
        private set => Set(ref _isAborted, value);
    }

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    /// <summary>The slot the next measurement takes: Pending or Invalid, in slot order. Done and Skipped are passed over.</summary>
    public MeasurementSlot? CurrentSlot => _session?.Slots.FirstOrDefault(IsCandidate);

    /// <summary>1-based position of the cursor among all slots, 0 when none is left.</summary>
    public int NextSlotPosition
    {
        get
        {
            if (_session is not { } session || CurrentSlot is not { } slot) return 0;
            for (int index = 0; index < session.Slots.Count; index++)
                if (session.Slots[index].Id == slot.Id) return index + 1;
            return 0;
        }
    }

    public int SlotCount => _session?.Slots.Count ?? 0;

    /// <summary>Done only: an Invalid point was measured and rejected, a Skipped one was never taken.</summary>
    public int MeasuredCount => _session?.Slots.Count(slot => slot.State == MeasurementSlotState.Done) ?? 0;

    public string MeasuredText => UiText.Format("Flow.Measured", MeasuredCount, SlotCount);

    public string CursorText => NextSlotPosition == 0 ? UiText.Get("Flow.NoSlot") : $"{NextSlotPosition}/{SlotCount}";

    /// <summary>The cursor's point within the grid's own point list — the denominator is PointCount, never a literal.</summary>
    public string PositionText
    {
        get
        {
            if (CurrentSlot is not { } slot) return UiText.Get("Flow.NoPoint");
            for (int index = 0; index < Grid.Points.Count; index++)
                if (Grid.Points[index].Id == slot.Point.Id) return $"{index + 1}/{Grid.PointCount}";
            return UiText.Get("Flow.NoPoint");
        }
    }

    public string ProgressText => _session is null
        ? string.Empty
        : CurrentSlot is null
            ? MeasuredText
            : UiText.Format("Flow.Progress", CursorText, PositionText, MeasuredText);

    /// <summary>§25: the grid indices AND the physical coordinates, both read from the point the grid produced.</summary>
    public string PromptText => _session is null
        ? string.Empty
        : CurrentSlot is { } slot
        ? UiText.Format(
            "Flow.Prompt",
            slot.Mode,
            slot.Point.GridX,
            slot.Point.GridY,
            slot.Point.GridZ,
            slot.Point.X.ToString("0.00", CultureInfo.InvariantCulture),
            slot.Point.Y.ToString("0.00", CultureInfo.InvariantCulture),
            slot.Point.Z.ToString("0.00", CultureInfo.InvariantCulture))
        : UiText.Get("Flow.NothingLeft");

    /// <summary>Quality feedback: the named reasons the last measurement was rejected for, never a generic failure.</summary>
    /// <summary>
    /// The decision prompt for a point that needs one. An aborted-in-flight capture is worded as a user action
    /// rather than as a signal-quality verdict: its reason is kept for the record, but showing "InputClipping"
    /// beside an abort would blame the room for a decision the user made.
    /// </summary>
    public string FeedbackText => _awaitingDecision is { } slot
        ? slot.Reasons.Contains(QualityIssue.AbortedInFlight)
            ? UiText.Format("Flow.FeedbackAbort", PositionText, slot.Id, Reasons(slot))
            : UiText.Format("Flow.FeedbackReject", PositionText, slot.Id, Reasons(slot))
        : string.Empty;

    public bool HasFeedback => _awaitingDecision is not null;

    public bool CanMeasure => !IsBusy && _session is not null && _runner is not null && CurrentSlot is not null;

    public bool CanSkip => !IsBusy && _session is not null && _awaitingDecision is not null;

    protected override void OnCultureChanged()
    {
        if (_session is null)
            _status = UiText.Get("Flow.NoSession");
        if (_devicesRead)
            DescribeDevices();
        else
            _deviceMessage = UiText.Get("Flow.NoDeviceList");
    }

    private void DescribeDevices()
    {
        DeviceMessage = (InputDevices.Count, OutputDevices.Count) switch
        {
            (0, 0) => UiText.Get("Flow.NoDevices"),
            (0, _) => UiText.Format("Flow.NoInputs", OutputDevices.Count),
            (_, 0) => UiText.Format("Flow.NoOutputs", InputDevices.Count),
            _ => UiText.Format("Flow.DeviceCounts", InputDevices.Count, OutputDevices.Count),
        };
    }

    /// <summary>Reads the device lists. An empty list is a normal answer, not an exception: it renders as a list
    /// with nothing in it plus a message saying what is missing.</summary>
    public void RefreshDevices()
    {
        try
        {
            AudioDeviceLists devices = _backend.EnumerateDevices();
            InputDevices = devices.Inputs;
            OutputDevices = devices.Outputs;
            _devicesRead = true;
            if (SelectedInput is null) SelectedInput = InputDevices.FirstOrDefault();
            if (SelectedOutput is null) SelectedOutput = OutputDevices.FirstOrDefault();
            DescribeDevices();
        }
        catch (Exception exception)
        {
            InputDevices = [];
            OutputDevices = [];
            SelectedInput = null;
            SelectedOutput = null;
            _devicesRead = true;
            DeviceMessage = UiText.Format("Flow.DeviceListFailed", exception.Message);
        }

        RefreshCommands();
        RaiseAll();
    }

    /// <summary>
    /// Takes the grid and the sweep from a loaded project, so that resuming cannot be refused for a mismatch the
    /// user never chose: <c>MeasurementSession.Start</c> compares both against the manifest.
    /// </summary>
    public void UseProject(string directory, SessionManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ProjectDirectory = directory;
        StartHz = manifest.Sweep.StartHz;
        EndHz = manifest.Sweep.EndHz;
        DurationSeconds = manifest.Sweep.DurationSeconds;
        SampleRate = (int)manifest.Sweep.SampleRate;
        WidthMetres = manifest.Grid.WidthMetres;
        DepthMetres = manifest.Grid.DepthMetres;
        HeightMetres = manifest.Grid.HeightMetres;
        CountX = manifest.Grid.CountX;
        CountY = manifest.Grid.CountY;
        CountZ = manifest.Grid.CountZ;
        Status = UiText.Format("Flow.Adopted", directory, manifest.Slots.Count);
        RaiseAll();
    }

    /// <summary>Starts a new session or resumes the one already in <see cref="ProjectDirectory"/>.</summary>
    public void StartSession()
    {
        if (SelectedInput is not { } input || SelectedOutput is not { } output)
        {
            Status = UiText.Get("Flow.NeedDevices");
            return;
        }

        if (!TryBuildPlan(out SweepSettings sweep, out MeasurementGrid grid, out string problem))
        {
            Status = problem;
            return;
        }

        try
        {
            _session = MeasurementSession.Start(ProjectDirectory, grid, sweep);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            Status = UiText.Format("Flow.OpenFailed", ProjectDirectory, exception.Message);
            return;
        }

        _runner = new MeasurementRunner(
            _backend,
            _session,
            output,
            input,
            new AudioBackendSettings(SampleRate, AudioShareMode.Shared, LatencyMilliseconds),
            TimeSpan.FromSeconds(PreRollSeconds),
            TimeSpan.FromSeconds(PostRollSeconds));
        _awaitingDecision = null;
        IsAborted = false;
        Status = UiText.Format("Flow.SessionOpen", ProjectDirectory, ProgressText);
        RefreshCommands();
        RaiseAll();
    }

    /// <summary>
    /// Measures the cursor slot off the UI thread. Nothing throws out of here: a device that is busy, missing or in
    /// the wrong mode becomes a message, and the slot keeps the state it had.
    /// </summary>
    public async Task MeasureAsync()
    {
        if (_session is null || _runner is null || CurrentSlot is not { } slot)
        {
            Status = UiText.Get("Flow.NeedSession");
            return;
        }

        if (IsBusy) return;

        IsAborted = false;
        IsBusy = true;
        try
        {
            // §35: the measurement layers are synchronous by design, so the UI is what goes off-thread. The
            // continuation resumes on the context this method was called on (the dispatcher), which is what keeps
            // the progress notifications on the UI thread.
            //
            // No cancellation token is passed here, and that is a decision rather than an omission: the runner
            // cannot be interrupted from outside (the device call owns the thread), so a token could only decide by
            // scheduling lottery whether the queued delegate ran at all — leaving an aborted point either Invalid
            // with its capture kept, or silently never measured. Abort is instead a flag that Apply reads the
            // verdict against, which makes an aborted in-flight point a recorded state every time:
            // Invalid/AbortedInFlight with the capture kept.
            // ponytail: the ceiling is that the sweep already playing still plays out (≤ ~2.5 s); the upgrade is a
            // Stop() on IAudioBackend, which is a change to the Phase 7/8-accepted contract and needs a ruling.
            MeasurementRunOutcome outcome = await Task.Run(() => _runner.Run(slot)).ConfigureAwait(true);
            Apply(outcome);
        }
        catch (AudioDeviceOpenException exception)
        {
            Status = UiText.Format("Flow.NotMeasured", slot.Id, exception.Message, exception.Kind, exception.DeviceName, slot.State);
        }
        catch (Exception exception)
        {
            Status = UiText.Format("Flow.Failed", slot.Id, exception.GetType().Name, exception.Message);
        }
        finally
        {
            IsBusy = false;
            RefreshCommands();
            RaiseAll();
        }
    }

    /// <summary>
    /// Records the slot the user does not want to measure again. A skipped point still occupies its slot, so the
    /// denominators do not move; only the cursor does.
    /// </summary>
    public void Skip()
    {
        if (_session is null || _awaitingDecision is not { } slot) return;
        string where = PositionText;
        _session.MarkSkipped(slot);
        _awaitingDecision = null;
        Status = UiText.Format("Flow.Skipped", where, slot.Id, ProgressText);
        RefreshCommands();
        RaiseAll();
    }

    /// <summary>
    /// Stops the run. The slot keeps whatever the session already recorded — an Invalid verdict keeps its named
    /// reasons, an unmeasured slot stays Pending — so nothing is discarded and nothing advances quietly: the
    /// cursor and the tally are unchanged, and <see cref="Status"/> says exactly what was kept.
    /// <para>
    /// ponytail: this cancels the queued work and lets an in-flight capture finish, because the synchronous
    /// device call cannot be interrupted from outside without changing <c>IAudioBackend</c> (accepted in Phases
    /// 7/8). Ceiling: the user waits for the current sweep to end (≤ ~2.5 s). Upgrade: add a
    /// <c>Stop()</c> to the backend that trips its waits mid-capture, then abort is instant.
    /// </para>
    /// </summary>
    public void Abort()
    {
        // Abort is a flag, not an interruption: the in-flight capture cannot be stopped from here (see MeasureAsync),
        // so the run's own verdict decides what the point records — an aborted in-flight capture becomes
        // Invalid/AbortedInFlight with its data kept, and aborting with nothing in flight changes no state at all.
        IsAborted = true;
        string recorded = _awaitingDecision is { } awaiting
            ? UiText.Format("Flow.AbortKeep", PositionText, awaiting.State, Reasons(awaiting))
            : CurrentSlot is { } slot
                ? UiText.Format("Flow.AbortStill", PositionText, slot.State)
                : UiText.Get("Flow.AbortDone");
        _awaitingDecision = null;
        Status = UiText.Format("Flow.AbortStatus", recorded);
        RefreshCommands();
        RaiseAll();
    }

    private void Apply(MeasurementRunOutcome outcome)
    {
        MeasurementSlot slot = outcome.Slot;
        if (IsAborted)
        {
            // boss-1's ruling: a capture that completed while the user had already aborted is kept as data but
            // marked Invalid with AbortedInFlight — reporting a cancelled measurement as Done would count work the
            // user did not accept. Invalid is a cursor candidate (the progress ruling), so the user is returned to
            // this point instead of advancing past it, and the Done-only tally does not move. The capture session
            // metadata is carried over from the runner's own verdict so re-recording keeps the device names.
            slot = _session!.MarkInvalid(
                slot,
                outcome.Measurement.Recording,
                outcome.Measurement.ImpulseResponse,
                [QualityIssue.AbortedInFlight, .. outcome.Measurement.Issues],
                outcome.Measurement.PeakMagnitude,
                slot.Capture);
            _awaitingDecision = slot;
            Status = UiText.Format("Flow.AbortKept", slot.Id, PositionText);
            return;
        }

        if (slot.State == MeasurementSlotState.Invalid)
        {
            _awaitingDecision = slot;
            Status = UiText.Format("Flow.RejectedStatus", PositionText, slot.Id, Reasons(slot));
        }
        else
        {
            _awaitingDecision = null;
            Status = UiText.Format("Flow.Recorded", slot.Id, slot.State, ProgressText);
        }
    }

    private bool TryBuildPlan(out SweepSettings sweep, out MeasurementGrid grid, out string problem)
    {
        sweep = Sweep;
        grid = Grid;
        try
        {
            sweep.Validate();
        }
        catch (Exception exception) when (exception is ArgumentException or ArgumentOutOfRangeException)
        {
            problem = UiText.Format("Flow.SweepBad", exception.Message);
            return false;
        }

        try
        {
            grid = MeasurementGrid.Create(WidthMetres, DepthMetres, HeightMetres, CountX, CountY, CountZ);
        }
        catch (Exception exception) when (exception is ArgumentException or ArgumentOutOfRangeException)
        {
            problem = UiText.Format("Flow.GridBad", exception.Message);
            return false;
        }

        if (string.IsNullOrWhiteSpace(ProjectDirectory))
        {
            problem = UiText.Get("Flow.NeedFolder");
            return false;
        }

        problem = string.Empty;
        return true;
    }

    private static bool IsCandidate(MeasurementSlot slot)
        => slot.State is MeasurementSlotState.Pending or MeasurementSlotState.Invalid;

    private static string Reasons(MeasurementSlot slot)
        => slot.Reasons.Count == 0 ? UiText.Get("Flow.NoReasons") : string.Join(", ", slot.Reasons);

    private void SetPlan<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Set(ref field, value, propertyName))
        {
            Raise(nameof(SweepText));
            Raise(nameof(GridText));
            Raise(nameof(SweepLine));
            Raise(nameof(GridLine));
        }
    }

    private void RefreshCommands()
    {
        RefreshDevicesCommand.RaiseCanExecuteChanged();
        StartSessionCommand.RaiseCanExecuteChanged();
        MeasureCommand.RaiseCanExecuteChanged();
        SkipCommand.RaiseCanExecuteChanged();
        AbortCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// Raises the derived properties after a state change. Hand-listed on purpose: the derived progress numbers
    /// read the slot list, and WPF has no way to know that a slot list changed.
    /// ponytail: manual raise list — a drift here shows up as a stale label, not as a wrong number. Move to a
    /// computed-property dependency map only if this grows past the handful of names below.
    /// </summary>
    private void RaiseAll()
    {
        Raise(nameof(Session));
        Raise(nameof(CurrentSlot));
        Raise(nameof(NextSlotPosition));
        Raise(nameof(SlotCount));
        Raise(nameof(MeasuredCount));
        Raise(nameof(MeasuredText));
        Raise(nameof(CursorText));
        Raise(nameof(PositionText));
        Raise(nameof(ProgressText));
        Raise(nameof(PromptText));
        Raise(nameof(FeedbackText));
        Raise(nameof(HasFeedback));
        Raise(nameof(CanMeasure));
        Raise(nameof(CanSkip));
    }
}
