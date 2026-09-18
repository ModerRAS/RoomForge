namespace AudioOptimizer.IO;

using System.Diagnostics.CodeAnalysis;
using AudioOptimizer.Core;

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
/// <param name="Calibration">The microphone calibration a calibrated level reference is based on. Absent in v1/v2 files and absent when the project claims no absolute reference; when present it IS the claim, and a load refuses it if it cannot be rebuilt.</param>
public sealed record SessionManifest(
    int SchemaVersion,
    DateTime CreatedUtc,
    SweepManifest Sweep,
    GridManifest Grid,
    List<SlotManifest> Slots,
    string ProjectId = "",
    CalibrationManifest? Calibration = null)
{
    /// <summary>
    /// 1 = the original session manifest (state only). 2 = adds the per-measurement capture metadata and the
    /// explicit signal file names. 3 = adds the microphone calibration a calibrated level reference is based on.
    /// Readers accept anything up to this and refuse anything newer, because a newer writer may mean fields we
    /// would silently drop.
    /// </summary>
    public const int CurrentSchemaVersion = 3;

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

/// <summary>
/// The persisted form of a <see cref="MicrophoneCalibration"/>. Every field is nullable or defaulted on purpose: a
/// hand-edited or torn file must become a structured <see cref="ProjectLoadProblem"/>, not an exception thrown
/// from a validating constructor while the file is being read. <see cref="TryBuild"/> is the one translation back,
/// so the domain type stays the single authority on what a calibration is.
/// </summary>
public sealed record CalibrationManifest(
    string Identity = "",
    double? SensitivityDbSplPerFullScale = null,
    List<CalibrationPoint>? Points = null)
{
    public static CalibrationManifest From(MicrophoneCalibration calibration)
    {
        ArgumentNullException.ThrowIfNull(calibration);
        return new(calibration.Identity, calibration.SensitivityDbSplPerFullScale, [.. calibration.Points]);
    }

    /// <summary>Rebuilds the payload, or says why this claim is not reproducible.</summary>
    public bool TryBuild([NotNullWhen(true)] out MicrophoneCalibration? calibration, out string reason)
    {
        calibration = null;
        if (string.IsNullOrWhiteSpace(Identity))
        {
            reason = "it names no calibration";
            return false;
        }

        if (SensitivityDbSplPerFullScale is not { } sensitivity || !double.IsFinite(sensitivity))
        {
            reason = "it carries no absolute sensitivity, so the reference it claims is not reproducible";
            return false;
        }

        try
        {
            calibration = new MicrophoneCalibration(Identity, sensitivity, Points ?? []);
        }
        catch (ArgumentException exception)
        {
            reason = exception.Message;
            return false;
        }

        reason = "";
        return true;
    }
}
