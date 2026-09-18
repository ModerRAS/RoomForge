namespace AudioOptimizer.Measurement;

using AudioOptimizer.Core;
using AudioOptimizer.IO;

/// <summary>Where one slot stands. Anything that is not Done has to be re-measured or deliberately skipped.</summary>
public enum MeasurementSlotState
{
    Pending,
    Done,
    Invalid,
    Skipped,
}

/// <summary>
/// One slot of a session — a (mode, grid point) pair — and what happened to it. Immutable: a state change
/// produces a new instance, so a caller can never hold a half-updated slot. These records ARE the resume log.
/// </summary>
/// <param name="PeakMagnitude">|IR peak| once measured, so the session median needs no re-deconvolution.</param>
/// <param name="Capture">The conditions it was measured under; null for a slot that was never measured.</param>
public sealed record MeasurementSlot(
    SubMode Mode,
    MeasurementPoint Point,
    MeasurementSlotState State,
    IReadOnlyList<QualityIssue> Reasons,
    double? PeakMagnitude,
    DateTime? CompletedUtc,
    MeasurementCapture? Capture = null)
{
    public static MeasurementSlot Pending(SubMode mode, MeasurementPoint point) => new(mode, point, MeasurementSlotState.Pending, [], null, null);

    /// <summary>Stable slot key, e.g. "AB/x-1_y0_z1". Also the measurement id the project file stores.</summary>
    public string Id => $"{Mode}/{Point.Id}";

    /// <summary>Recording path relative to the session directory, e.g. "AB/x-1_y0_z1.wav".</summary>
    public string RecordingFileName => $"{Mode}/{Point.Id}.wav";

    /// <summary>Impulse response path relative to the session directory, e.g. "AB/x-1_y0_z1.ir.wav".</summary>
    public string ImpulseResponseFileName => $"{Mode}/{Point.Id}.ir.wav";

    /// <summary>The project-file entry for this slot — the one place a slot becomes file format.</summary>
    public SlotManifest ToManifest() => new(
        Mode.ToString(),
        Point.Id,
        State.ToString(),
        [.. Reasons.Select(reason => reason.ToString())],
        PeakMagnitude,
        CompletedUtc,
        Id,
        RecordingFileName,
        ImpulseResponseFileName,
        Capture);
}
