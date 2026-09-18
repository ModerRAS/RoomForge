namespace AudioOptimizer.IO;

/// <summary>
/// The project file: everything needed to reopen a session and continue it, as an explicit schema rather than by
/// serialising the live objects — the format then stays readable, and adding a field to a domain type cannot
/// silently change the file. Enums are strings, so renumbering an enum cannot rewrite what history says
/// happened. This type is the ONLY project format; the raw recording and the impulse response travel beside it
/// as float32 WAV through <see cref="WavFile"/>, and the frequency response is deliberately absent because it is
/// a derived view (fs, window and crop all change it, so persisting one would freeze one interpretation as
/// truth).
/// Lives in this dependency-free assembly on purpose: the load path must not be able to open a device.
/// </summary>
/// <param name="SchemaVersion">See <see cref="CurrentSchemaVersion"/>.</param>
/// <param name="Slots">One entry per (mode, grid point); empty entries are never written.</param>
/// <param name="ProjectId">Stable id of the project this session belongs to; absent in v1 files, filled on read.</param>
public sealed record SessionManifest(
    int SchemaVersion,
    DateTime CreatedUtc,
    SweepManifest Sweep,
    GridManifest Grid,
    List<SlotManifest> Slots,
    string ProjectId = "")
{
    /// <summary>
    /// 1 = the original session manifest (state only). 2 = adds the per-measurement capture metadata and the
    /// explicit signal file names. Readers accept anything up to this and refuse anything newer, because a
    /// newer writer may mean fields we would silently drop.
    /// </summary>
    public const int CurrentSchemaVersion = 2;

    /// <summary>Version written by this build; the reader also accepts older ones.</summary>
    public const int OldestReadableSchemaVersion = 1;
}

/// <summary>The sweep a session was measured with — the whole session shares one, so a resume cannot mix two.</summary>
public sealed record SweepManifest(double StartHz, double EndHz, double DurationSeconds, double SampleRate);

/// <summary>
/// The measurement grid. Regenerating the grid from these numbers must yield the identical point ids, which is
/// what makes a resume refuse a mismatched grid instead of merging two different rooms.
/// </summary>
public sealed record GridManifest(double WidthMetres, double DepthMetres, double HeightMetres, int CountX, int CountY, int CountZ);

/// <summary>
/// One measurement in the project: identity, state, the files that hold its signal data, and the capture
/// conditions. <see cref="MeasurementId"/>, <see cref="RecordingFile"/> and <see cref="ImpulseResponseFile"/>
/// are always filled by the writer and filled on read when a v1 file lacks them (they are derivable from the
/// mode and point id).
/// </summary>
public sealed record SlotManifest(
    string Mode,
    string PointId,
    string State,
    List<string> Reasons,
    double? PeakMagnitude,
    DateTime? CompletedUtc,
    string MeasurementId = "",
    string RecordingFile = "",
    string ImpulseResponseFile = "",
    MeasurementCapture? Capture = null);
