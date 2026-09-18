namespace AudioOptimizer.Core;

/// <summary>
/// One row of a microphone calibration file: a frequency and the dB to ADD to the measured level at it.
/// </summary>
/// <param name="FrequencyHz">
/// Must be greater than zero: the correction is interpolated logarithmically in frequency, so 0 Hz has no
/// meaningful position on the axis and a negative one has no logarithm at all.
/// </param>
/// <param name="CorrectionDb">Finite dB, added to the measured level at this frequency.</param>
public readonly record struct CalibrationPoint(double FrequencyHz, double CorrectionDb);

/// <summary>
/// Whether a frequency is covered by a calibration, and the correction at it. A nullable double cannot express
/// this: "not calibrated here" and "calibrated, correction 0 dB" are different facts, and a caller that
/// conflates them reports accuracy it does not have. The <c>default</c> value is the <b>uncalibrated</b> one, so
/// a forgotten branch reads as uncalibrated rather than as a free 0 dB correction.
/// </summary>
public readonly record struct CalibrationCorrection(bool IsCalibrated, double CorrectionDb);

/// <summary>
/// Microphone calibration data: the identity of the calibration, the sensitivity that turns a relative reading
/// into an absolute one, and the per-frequency shape correction. This exists so that "SPL" can never be claimed
/// without saying what it is based on. Naming the calibrated reference member here would defeat that: the
/// production doc-naming scan asserts the words stay in exactly one file.
/// <para>
/// Two homes were possible and this one is deliberate: the payload is parsed by <c>AudioOptimizer.IO</c>,
/// persisted in that project's format, and consumed by <c>AudioOptimizer.Visualization</c> for the axis label,
/// so it must live at or below the lowest of the three. Two shapes of one fact would be a second format in all
/// but name.
/// </para>
/// </summary>
public sealed record MicrophoneCalibration
{
    public MicrophoneCalibration(string identity, double sensitivityDbSplPerFullScale, IReadOnlyList<CalibrationPoint>? points = null)
    {
        if (string.IsNullOrWhiteSpace(identity))
            throw new ArgumentException("A calibration must name itself; 'SPL' with an anonymous calibration is not reproducible.", nameof(identity));
        if (!double.IsFinite(sensitivityDbSplPerFullScale))
            throw new ArgumentOutOfRangeException(nameof(sensitivityDbSplPerFullScale), sensitivityDbSplPerFullScale, "The sensitivity must be finite.");

        // Sorted internally, because a file's row order is a property of the file and not a claim about the
        // microphone: a reader that only works on pre-sorted input would refuse real files, and a reader that
        // interpolates on unsorted input would report a correction that is not the calibration's.
        var ordered = points is null ? [] : new List<CalibrationPoint>(points);
        ordered.Sort((left, right) => left.FrequencyHz.CompareTo(right.FrequencyHz));
        for (int i = 0; i < ordered.Count; i++)
        {
            CalibrationPoint point = ordered[i];
            if (!double.IsFinite(point.FrequencyHz) || point.FrequencyHz <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(points), point.FrequencyHz, "A calibration frequency must be a finite value greater than 0 Hz.");
            if (!double.IsFinite(point.CorrectionDb))
                throw new ArgumentOutOfRangeException(nameof(points), point.CorrectionDb, "A calibration correction must be finite.");
            if (i > 0 && point.FrequencyHz == ordered[i - 1].FrequencyHz)
                throw new ArgumentException($"Two calibration points share {point.FrequencyHz} Hz; the correction there would be ambiguous.", nameof(points));
        }

        Identity = identity;
        SensitivityDbSplPerFullScale = sensitivityDbSplPerFullScale;
        Points = ordered;
    }

    /// <summary>Which calibration this is, e.g. a file name or serial. Required: an unattributed calibration claim is not a claim.</summary>
    public string Identity { get; }

    /// <summary>
    /// dB SPL that corresponds to a full-scale reading of the microphone, in dBFS/SPL. Required, so a file that
    /// carries no absolute reference cannot produce a payload at all and the label stays relative.
    /// ponytail: the sign convention is the calibration source's own and is unverified because no real UMIK-1
    /// file has been read on this machine; fix it against one before any SPL number is shown to a user.
    /// </summary>
    public double SensitivityDbSplPerFullScale { get; }

    /// <summary>The shape correction, ascending in frequency and strictly increasing. Empty is legal: an absolute-only calibration.</summary>
    public IReadOnlyList<CalibrationPoint> Points { get; }

    /// <summary>
    /// The frequency range the correction actually covers, or <c>null</c> when there are no points. This is the
    /// <b>achieved</b> extent: the label states it, so a reader can see that a value above 5 kHz came from a file
    /// that stops at 359 Hz.
    /// </summary>
    public (double MinHz, double MaxHz)? CalibratedExtent =>
        Points.Count == 0 ? null : (Points[0].FrequencyHz, Points[^1].FrequencyHz);

    /// <summary>Inclusive at both edges, matching <see cref="FrequencyBand.Contains"/> and for the same reason: the edges were measured.</summary>
    public bool CoversFrequency(double frequencyHz)
        => double.IsFinite(frequencyHz) && frequencyHz > 0.0 && Points.Count > 0
            && frequencyHz >= Points[0].FrequencyHz && frequencyHz <= Points[^1].FrequencyHz;

    /// <summary>
    /// The correction at a frequency: logarithmically interpolated inside the file's range, and <b>uncalibrated
    /// outside it</b> — never extrapolated. The rule lives here rather than at each call site because a
    /// calibration that can report a value beyond its own range is the same defect as a settable band.
    /// </summary>
    public CalibrationCorrection CorrectionAt(double frequencyHz)
    {
        if (!CoversFrequency(frequencyHz)) return new CalibrationCorrection(false, 0.0);

        int last = Points.Count - 1;
        if (frequencyHz == Points[last].FrequencyHz) return new CalibrationCorrection(true, Points[last].CorrectionDb);

        int k = 0;
        while (k + 1 < last && Points[k + 1].FrequencyHz <= frequencyHz) k++;

        CalibrationPoint low = Points[k];
        CalibrationPoint high = Points[k + 1];
        // Log-frequency interpolation: t = ln(f/f_low) / ln(f_high/f_low), correction = c_low + t·(c_high − c_low).
        // At the geometric midpoint t = 0.5, i.e. the arithmetic mean of the two dB values — a linear-in-frequency
        // interpolation puts the midpoint at (f_low+f_high)/2 and reports a different number there.
        double t = Math.Log(frequencyHz / low.FrequencyHz) / Math.Log(high.FrequencyHz / low.FrequencyHz);
        return new CalibrationCorrection(true, low.CorrectionDb + t * (high.CorrectionDb - low.CorrectionDb));
    }
}
