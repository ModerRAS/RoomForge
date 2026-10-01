namespace AudioOptimizer.UI.ViewModels;

using System.IO;                  // WPF's implicit-using set does not include System.IO
using AudioOptimizer.IO;
using AudioOptimizer.UI.Localization;

/// <summary>
/// The project-loading view-model. It talks to <see cref="SessionStore"/> and to the file system and to nothing
/// else: this file must not reference the audio stack, because "no device is touched on the load path" is a
/// property of the load path, not of a comment — a test scans this file's source for audio types as well as
/// checking the assembly boundary.
/// <para>
/// Problems are reported as data: the store's message, plus EVERY affected path (derived here per slot, so a
/// project missing two recordings and an impulse response lists three files rather than one category).
/// </para>
/// </summary>
public sealed class ProjectLoadViewModel : ObservableObject
{
    private string _projectDirectory = string.Empty;
    private ProjectLoadProblem _problem = ProjectLoadProblem.ManifestMissing;
    private string _message = UiText.Get("Project.None");
    private bool _isLoaded;
    private string _projectId = "—";
    private string _schema = "—";
    private string _sweep = "—";
    private string _grid = "—";
    private IReadOnlyList<ProjectSlotViewModel> _slots = [];
    private IReadOnlyList<string> _affectedPaths = [];
    private SessionManifest? _loadedManifest;

    /// <summary>The directory to open. Set by the shell's path box; a file-picker can wrap this later.</summary>
    public string ProjectDirectory
    {
        get => _projectDirectory;
        set => Set(ref _projectDirectory, value ?? string.Empty);
    }

    public ProjectLoadProblem Problem
    {
        get => _problem;
        private set => Set(ref _problem, value);
    }

    public string Message
    {
        get => _message;
        private set => Set(ref _message, value);
    }

    public bool IsLoaded
    {
        get => _isLoaded;
        private set => Set(ref _isLoaded, value);
    }

    public string ProjectId
    {
        get => _projectId;
        private set => Set(ref _projectId, value);
    }

    public string Schema
    {
        get => _schema;
        private set => Set(ref _schema, value);
    }

    public string Sweep
    {
        get => _sweep;
        private set => Set(ref _sweep, value);
    }

    public string Grid
    {
        get => _grid;
        private set => Set(ref _grid, value);
    }

    public string SchemaLine => UiText.Format("Project.Schema", Schema);

    public string SweepLine => UiText.Format("Project.Sweep", Sweep);

    public string GridLine => UiText.Format("Project.Grid", Grid);

    public string IdLine => UiText.Format("Project.Id", ProjectId);

    public IReadOnlyList<ProjectSlotViewModel> Slots
    {
        get => _slots;
        private set
        {
            if (Set(ref _slots, value)) Raise(nameof(SlotCount));
        }
    }

    /// <summary>Every file the load could not use: the store's list plus the per-slot files that are gone.</summary>
    public IReadOnlyList<string> AffectedPaths
    {
        get => _affectedPaths;
        private set
        {
            if (Set(ref _affectedPaths, value))
            {
                Raise(nameof(AffectedPathSummary));
                Raise(nameof(ShowAffected));
            }
        }
    }

    /// <summary>
    /// The manifest as loaded, for the caller that wants to continue the project: the measurement flow takes the
    /// grid and the sweep from it so that a resume cannot be refused for a mismatch the user never chose. Null
    /// when nothing is loaded.
    /// </summary>
    public SessionManifest? LoadedManifest
    {
        get => _loadedManifest;
        private set => Set(ref _loadedManifest, value);
    }

    public int SlotCount => Slots.Count;

    public int DoneCount => Slots.Count(slot => slot.State == "Done");

    public int InvalidCount => Slots.Count(slot => slot.State == "Invalid");

    public int PendingCount => Slots.Count(slot => slot.State == "Pending");

    public int SkippedCount => Slots.Count(slot => slot.State == "Skipped");

    public int MissingSignalCount => Slots.Count(slot => slot.IsMissingSignalFile);

    public string StatusLine => IsLoaded
        ? UiText.Format("Project.StatusLine", SlotCount, DoneCount, InvalidCount, SkippedCount, PendingCount)
        : Problem == ProjectLoadProblem.ManifestMissing
            ? UiText.Get("Project.PickFirst")
            : UiText.Format("Project.NotLoaded", ProblemText);

    private string ProblemText => !UiText.IsChinese
        ? Problem.ToString()
        : Problem switch
        {
            ProjectLoadProblem.DirectoryMissing => "文件夹不存在",
            ProjectLoadProblem.ManifestUnreadable => "项目文件读不出来",
            ProjectLoadProblem.SchemaVersionUnsupported => "项目文件是更新的版本，这一版读不了",
            ProjectLoadProblem.SignalFileMissing => "有录音或脉冲文件不见了",
            ProjectLoadProblem.SignalFileUnreadable => "有录音文件打不开",
            ProjectLoadProblem.CalibrationNotReproducible => "里面的校准信息重建不了",
            _ => Problem.ToString(),
        };

    public bool ShowAffected => AffectedPaths.Count > 0;

    public string AffectedPathSummary => AffectedPaths.Count == 0
        ? UiText.Get("Project.NoMissing")
        : UiText.Format("Project.Affected", AffectedPaths.Count, string.Join(", ", AffectedPaths.Select(Path.GetFileName)));

    protected override void OnCultureChanged()
    {
        if (!IsLoaded && Problem == ProjectLoadProblem.ManifestMissing && Slots.Count == 0)
            _message = UiText.Get("Project.None");
    }

    /// <summary>
    /// Loads <see cref="ProjectDirectory"/>. Never throws for a bad project — a missing or corrupt file is a
    /// state the user must be able to read, so it becomes <see cref="Problem"/>/<see cref="Message"/> plus the
    /// list of paths. Signals are checked for existence only; the samples are read when a view needs them.
    /// </summary>
    public void Load()
    {
        ProjectLoadResult result = SessionStore.Load(ProjectDirectory);
        if (result.Manifest is not { } manifest)
        {
            Problem = result.Problem;
            Message = result.Message;
            IsLoaded = false;
            LoadedManifest = null;
            Slots = [];
            AffectedPaths = [.. result.AffectedPaths];
            ProjectId = Schema = Sweep = Grid = "—";
            Raise(nameof(StatusLine));
            return;
        }

        var rows = new List<ProjectSlotViewModel>(manifest.Slots.Count);
        var affected = new List<string>(result.AffectedPaths);
        foreach (SlotManifest slot in manifest.Slots)
        {
            bool needsSignals = slot.State is "Done" or "Invalid";
            string recordingPath = Path.Combine(ProjectDirectory, slot.RecordingFile);
            string impulseResponsePath = Path.Combine(ProjectDirectory, slot.ImpulseResponseFile);
            bool hasRecording = File.Exists(recordingPath);
            bool hasImpulseResponse = File.Exists(impulseResponsePath);

            // The manifest is a claim, the disk is the fact: a measured slot with no files on disk is exactly
            // what the user needs told, per file, rather than as one aggregate category.
            if (needsSignals && !hasRecording) affected.Add(recordingPath);
            if (needsSignals && !hasImpulseResponse) affected.Add(impulseResponsePath);

            rows.Add(new ProjectSlotViewModel(slot, hasRecording, hasImpulseResponse));
        }

        Problem = result.Problem;
        Message = result.Message;
        IsLoaded = true;
        LoadedManifest = manifest;
        ProjectId = string.IsNullOrEmpty(manifest.ProjectId) ? "(v1 project: no id)" : manifest.ProjectId;
        Schema = $"schema {manifest.SchemaVersion}";
        Sweep = $"{manifest.Sweep.StartHz:0.##}–{manifest.Sweep.EndHz:0.##} Hz, {manifest.Sweep.DurationSeconds:0.##} s @ {manifest.Sweep.SampleRate:0} Hz";
        Grid = $"{manifest.Grid.CountX} × {manifest.Grid.CountY} × {manifest.Grid.CountZ} "
            + $"= {manifest.Grid.CountX * manifest.Grid.CountY * manifest.Grid.CountZ} points, "
            + $"{manifest.Grid.WidthMetres:0.##} × {manifest.Grid.DepthMetres:0.##} × {manifest.Grid.HeightMetres:0.##} m";
        Slots = rows;
        AffectedPaths = affected;
        Raise(nameof(StatusLine));
    }

    /// <summary>Puts a message on screen for a failure the view caught. Keeps the shell from ever crashing.</summary>
    public void ReportFailure(string message)
    {
        Problem = ProjectLoadProblem.ManifestUnreadable;
        Message = message;
        IsLoaded = false;
        Raise(nameof(StatusLine));
    }
}
