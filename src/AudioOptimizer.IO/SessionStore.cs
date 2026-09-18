namespace AudioOptimizer.IO;

using System.Text.Json;

/// <summary>Why a project could not be fully read. Never an exception: the caller renders this.</summary>
public enum ProjectLoadProblem
{
    /// <summary>Everything asked for was present and readable.</summary>
    None,

    /// <summary>The project directory itself does not exist.</summary>
    DirectoryMissing,

    /// <summary>session.json is not there — a different directory, or a project that was never saved.</summary>
    ManifestMissing,

    /// <summary>session.json exists but is not readable JSON, or is missing a section it must have.</summary>
    ManifestUnreadable,

    /// <summary>The file was written by a newer build; reading it would silently drop fields.</summary>
    SchemaVersionUnsupported,

    /// <summary>A recording or impulse-response file named by the manifest is not on disk.</summary>
    SignalFileMissing,

    /// <summary>A signal file is present but could not be parsed as WAV.</summary>
    SignalFileUnreadable,
}

/// <summary>One measurement's metadata plus the signal data the project stored for it.</summary>
public sealed record LoadedMeasurement(SlotManifest Slot, double[]? Recording, double[]? ImpulseResponse)
{
    public bool HasSignals => Recording is not null && ImpulseResponse is not null;
}

/// <summary>
/// The outcome of a load. <see cref="Problem"/> is <see cref="ProjectLoadProblem.None"/> on a clean read;
/// otherwise <see cref="Message"/> says what was missing in plain words and
/// <see cref="AffectedPaths"/> lists the exact files, so a UI can point at them. A partial load is still a
/// load: a missing recording for one slot leaves every other measurement and the resume state usable.
/// </summary>
public sealed record ProjectLoadResult(
    SessionManifest? Manifest,
    IReadOnlyList<LoadedMeasurement> Measurements,
    ProjectLoadProblem Problem,
    string Message,
    IReadOnlyList<string> AffectedPaths)
{
    public bool IsLoaded => Manifest is not null;

    /// <summary>The manifest, or an exception carrying the already-written explanation (opt-in, for callers that treat absence as fatal).</summary>
    public SessionManifest Require() => Manifest ?? throw new InvalidOperationException(Message);

    public static ProjectLoadResult Failed(ProjectLoadProblem problem, string message, params string[] affectedPaths)
        => new(null, [], problem, message, affectedPaths);
}

/// <summary>
/// Reads and writes a project directory: <c>session.json</c> plus the float32 WAVs it names. Everything here is
/// file IO and arithmetic — no device is opened, and this assembly cannot open one (it has no reference to
/// AudioOptimizer.Audio; a test asserts that). The write path creates directories; the read path never throws
/// for a missing or corrupt file, it returns <see cref="ProjectLoadProblem"/> instead.
/// </summary>
public static class SessionStore
{
    public const string ManifestFileName = "session.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,   // the manifest is meant to be read by a human too
    };

    public static string ManifestPath(string directory) => Path.Combine(directory, ManifestFileName);

    /// <summary>Writes the manifest. Throws only for a programming error (null/blank arguments), never for IO.</summary>
    public static void Save(string directory, SessionManifest manifest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(manifest.Sweep);
        ArgumentNullException.ThrowIfNull(manifest.Grid);

        Directory.CreateDirectory(directory);
        File.WriteAllText(ManifestPath(directory), JsonSerializer.Serialize(manifest, Json));
    }

    /// <summary>
    /// Loads a project. <paramref name="withSignals"/> also reads the recording and impulse response of every
    /// measurement — the metadata-only load is what a resume needs, and skipping 27 recordings matters when the
    /// only question is "which slots are still pending?".
    /// ponytail: the signal load is whole-file into memory (an 81-slot project of 1 s float32 captures is
    /// ~100 MB). Stream per slot if projects grow past a run this size.
    /// </summary>
    public static ProjectLoadResult Load(string directory, bool withSignals = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!Directory.Exists(directory))
            return ProjectLoadResult.Failed(ProjectLoadProblem.DirectoryMissing, $"The project directory '{directory}' does not exist.", directory);

        string path = ManifestPath(directory);
        if (!File.Exists(path))
            return ProjectLoadResult.Failed(ProjectLoadProblem.ManifestMissing, $"'{path}' is missing: this project has no session manifest.", path);

        SessionManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<SessionManifest>(File.ReadAllText(path), Json);
        }
        catch (JsonException exception)
        {
            return ProjectLoadResult.Failed(ProjectLoadProblem.ManifestUnreadable, $"'{path}' is not readable JSON: {exception.Message}", path);
        }
        catch (IOException exception)
        {
            return ProjectLoadResult.Failed(ProjectLoadProblem.ManifestUnreadable, $"'{path}' could not be read: {exception.Message}", path);
        }

        if (manifest is null || manifest.Sweep is null || manifest.Grid is null || manifest.Slots is null)
            return ProjectLoadResult.Failed(ProjectLoadProblem.ManifestUnreadable, $"'{path}' is missing its sweep, grid or measurement list.", path);

        if (manifest.SchemaVersion < SessionManifest.OldestReadableSchemaVersion || manifest.SchemaVersion > SessionManifest.CurrentSchemaVersion)
            return ProjectLoadResult.Failed(
                ProjectLoadProblem.SchemaVersionUnsupported,
                $"'{path}' has project schema version {manifest.SchemaVersion}; this build reads versions "
                + $"{SessionManifest.OldestReadableSchemaVersion} to {SessionManifest.CurrentSchemaVersion}. "
                + (manifest.SchemaVersion > SessionManifest.CurrentSchemaVersion
                    ? "It was written by a newer build — update before opening it, or its extra fields would be dropped."
                    : "It is not a project file this build recognises."),
                path);

        SessionManifest normalized = Normalize(manifest);
        if (!withSignals)
            return new ProjectLoadResult(normalized, [], ProjectLoadProblem.None, $"Loaded '{path}'.", []);

        var measurements = new List<LoadedMeasurement>(normalized.Slots.Count);
        var missing = new List<string>();
        var unreadable = new List<string>();
        foreach (SlotManifest slot in normalized.Slots)
        {
            // A pending slot has not been measured yet and a skipped one deliberately will not be: neither has
            // signal data by definition, so their absence is not a problem to report. Only Done/Invalid slots
            // claim to have files — those are the state names the format stores (MeasurementSlotState).
            if (slot.State is not ("Done" or "Invalid"))
            {
                measurements.Add(new LoadedMeasurement(slot, null, null));
                continue;
            }

            double[]? recording = ReadSignal(directory, slot.RecordingFile, missing, unreadable);
            double[]? impulseResponse = ReadSignal(directory, slot.ImpulseResponseFile, missing, unreadable);
            measurements.Add(new LoadedMeasurement(slot, recording, impulseResponse));
        }

        if (missing.Count > 0 || unreadable.Count > 0)
        {
            (ProjectLoadProblem problem, string what) = missing.Count > 0
                ? (ProjectLoadProblem.SignalFileMissing, "missing")
                : (ProjectLoadProblem.SignalFileUnreadable, "unreadable");
            string[] affected = [.. missing, .. unreadable];
            return new ProjectLoadResult(
                normalized, measurements, problem,
                $"'{path}' loaded, but {affected.Length} signal file(s) are {what}: {string.Join(", ", affected)}.",
                affected);
        }

        return new ProjectLoadResult(normalized, measurements, ProjectLoadProblem.None, $"Loaded '{path}' with {measurements.Count} measurement(s).", []);
    }

    /// <summary>Writes one measurement's raw recording as float32 WAV, creating the mode subdirectory.</summary>
    public static void WriteRecording(string directory, SlotManifest slot, double[] samples, int sampleRate)
        => WriteSignal(directory, slot.RecordingFile, samples, sampleRate);

    /// <summary>
    /// Writes one measurement's impulse response as float32 WAV. It is stored even though the recording
    /// determines it: a reload then hands back the IR that was actually used, and a mismatch against the
    /// recomputed one is a detectable corruption rather than a silent re-interpretation.
    /// </summary>
    public static void WriteImpulseResponse(string directory, SlotManifest slot, double[] samples, int sampleRate)
        => WriteSignal(directory, slot.ImpulseResponseFile, samples, sampleRate);

    /// <summary>Fills the fields a v1 file could not carry; derivation is by name, so no v1 field is invented.</summary>
    private static SessionManifest Normalize(SessionManifest manifest)
        => manifest with
        {
            ProjectId = string.IsNullOrWhiteSpace(manifest.ProjectId) ? string.Empty : manifest.ProjectId,
            Slots = [.. manifest.Slots.Select(slot => slot with
            {
                MeasurementId = string.IsNullOrWhiteSpace(slot.MeasurementId) ? $"{slot.Mode}/{slot.PointId}" : slot.MeasurementId,
                RecordingFile = string.IsNullOrWhiteSpace(slot.RecordingFile) ? $"{slot.Mode}/{slot.PointId}.wav" : slot.RecordingFile,
                ImpulseResponseFile = string.IsNullOrWhiteSpace(slot.ImpulseResponseFile) ? $"{slot.Mode}/{slot.PointId}.ir.wav" : slot.ImpulseResponseFile,
                Reasons = slot.Reasons ?? [],
            })],
        };

    private static void WriteSignal(string directory, string relativePath, double[] samples, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        string path = Path.Combine(directory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        WavFile.Write(path, samples, sampleRate, WavSampleFormat.Float32, channels: 1);
    }

    private static double[]? ReadSignal(string directory, string relativePath, List<string> missing, List<string> unreadable)
    {
        string path = Path.Combine(directory, relativePath);
        if (!File.Exists(path))
        {
            missing.Add(path);
            return null;
        }

        try
        {
            return WavFile.Read(path).Samples;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or FormatException or ArgumentException or NotSupportedException)
        {
            unreadable.Add(path);
            return null;
        }
    }
}
