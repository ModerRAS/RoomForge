namespace AudioOptimizer.Measurement;

using AudioOptimizer.Audio;
using AudioOptimizer.Core;
using AudioOptimizer.Dsp;
using AudioOptimizer.IO;

/// <summary>What running one point produced: the slot as it now stands, plus the full measurement.</summary>
public sealed record MeasurementRunOutcome(MeasurementSlot Slot, PointMeasurementResult Measurement);

/// <summary>
/// Runs ONE measurement point end to end through <see cref="IAudioBackend"/>: the shared point path, the one
/// quality check the shared path cannot make, and the session bookkeeping. The peak sanity check needs the
/// session median, which only exists once earlier points have been measured — the first point therefore defines
/// the reference for itself. Devices and rolls are constructor state because a session repeats them 81 times.
/// </summary>
public sealed class MeasurementRunner(
    IAudioBackend backend,
    MeasurementSession session,
    AudioDeviceInfo renderDevice,
    AudioDeviceInfo captureDevice,
    AudioBackendSettings settings,
    TimeSpan preRoll,
    TimeSpan postRoll,
    double playbackGain = 1.0,
    AudioCaptureMode captureMode = AudioCaptureMode.Device)
{
    public MeasurementSession Session { get; } = session;

    /// <summary>Measures the next pending slot, or returns null when the session is finished.</summary>
    public MeasurementRunOutcome? RunNext()
        => Session.NextPending is { } slot ? Run(slot) : null;

    /// <summary>Measures one specific slot and records the verdict in the session.</summary>
    public MeasurementRunOutcome Run(MeasurementSlot slot)
    {
        PointMeasurementResult measurement = PointMeasurement.Run(
            backend, renderDevice, captureDevice, settings, Session.Sweep, preRoll, postRoll, playbackGain, captureMode);

        var reasons = new List<QualityIssue>(measurement.Issues);
        double reference = Session.MedianPeakMagnitude();
        QualityCheckResult outlier = QualityChecks.CheckImpulseResponseOutlier(
            measurement.PeakMagnitude,
            reference > 0 ? reference : measurement.PeakMagnitude);
        foreach (QualityIssue issue in outlier.Issues)
            if (!reasons.Contains(issue)) reasons.Add(issue);

        // The raw sweep pass is taken with no polarity flip and no phase rotation — those are DSP the optimizer
        // recommends, not things this run applies — but they are recorded, because a measurement is only
        // comparable to the recommendation if the setting it was taken under is known.
        var capture = new MeasurementCapture(
            captureDevice.Name,
            renderDevice.Name,
            playbackGain,
            Polarity: 1,
            PhaseDegrees: 0.0,
            DelayMilliseconds: 0.0);

        MeasurementSlot updated = reasons.Count == 0
            ? Session.MarkDone(slot, measurement.Recording, measurement.ImpulseResponse, measurement.PeakMagnitude, capture)
            : Session.MarkInvalid(slot, measurement.Recording, measurement.ImpulseResponse, reasons, measurement.PeakMagnitude, capture);
        return new MeasurementRunOutcome(updated, measurement);
    }
}
