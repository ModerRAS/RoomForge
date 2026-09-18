namespace AudioOptimizer.Core;

/// <summary>
/// Why a measurement is not trustworthy. Named reasons, not booleans and not log strings: the Dsp checks only
/// say which of these happened, and the caller decides whether a point is retried, skipped or kept.
/// </summary>
public enum QualityIssue
{
    /// <summary>The excitation itself runs flat-topped at full scale: what was played is already clipped.</summary>
    InputClipping,

    /// <summary>The recorded signal runs flat-topped at full scale: the capture path clipped.</summary>
    OutputClipping,

    /// <summary>No sweep arrival was found in the recording (silence, wrong device, or a muted input).</summary>
    SweepNotDetected,

    /// <summary>A pre-roll was requested but the recording still starts on the sweep, so its head may be missing.</summary>
    SweepTruncatedAtStart,

    /// <summary>The recording ends before the sweep does.</summary>
    SweepTruncatedAtEnd,

    /// <summary>No usable impulse peak, or the peak sits outside the window it can physically be in.</summary>
    ImpulseResponseNotFound,

    /// <summary>The impulse peak does not stand clear of the noise floor far enough to be a measurement.</summary>
    LowSignalToNoise,

    /// <summary>The impulse peak diverges from the reference (session median) magnitude by more than allowed.</summary>
    ImpulseResponseOutlier,

    /// <summary>The recording length is outside the window the caller considers acceptable.</summary>
    RecordingLengthAnomaly,

    /// <summary>A zero run or a step discontinuity inside the sweep region: the stream dropped samples.</summary>
    DropoutDetected,

    /// <summary>
    /// The user aborted the run while this capture was already in flight, so the completed measurement is kept but
    /// is not counted as usable. This is the only member of this enum that is <b>not</b> a signal-quality verdict:
    /// no <c>QualityChecks</c> method may return it (asserted by <c>AbortReasonBoundaryTests</c>), which is what
    /// lets the UI word it as "aborted by you — re-measure or skip" instead of showing a clipping-style reason.
    /// </summary>
    AbortedInFlight,
}
