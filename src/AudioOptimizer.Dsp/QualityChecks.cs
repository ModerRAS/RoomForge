namespace AudioOptimizer.Dsp;

using AudioOptimizer.Core;

/// <summary>
/// The eight quality checks one captured point has to survive before its impulse response is worth trusting.
/// Every check returns named reasons (<see cref="QualityIssue"/>), never a boolean and never a log string: the
/// caller decides whether a point is retried, skipped or kept, and the wording lives wherever the reasons are
/// shown. Thresholds are parameters with conservative defaults so a test can pin both a pass and a fail.
/// </summary>
public static class QualityChecks
{
    /// <summary>Full scale minus one int16 LSB: 1 − 1/32768 = 0.999969482421875.</summary>
    public const double HeadroomLimit = 1.0 - 1.0 / 32768.0;

    /// <summary>
    /// Input clipping: the excitation runs flat-topped at full scale, so what was played is already clipped.
    /// A clipped waveform is a plateau — consecutive samples that are EQUAL at full scale — while a sine that is
    /// merely near its peak is not flat: at 20 Hz/48 kHz about six consecutive samples of the shipped sweep sit
    /// within 1 LSB of full scale yet differ by ~1e-5, so a level threshold alone would false-positive on the
    /// very signal the generator produces. Equality is what separates "loud" from "clipped".
    /// ponytail: an exactly-equal plateau; a clipped signal that was later resampled or dithered smears it into
    /// near-equality, so widen <paramref name="flatTolerance"/> from 1e-9 if that ever shows up.
    /// </summary>
    public static QualityCheckResult CheckInputClipping(
        ReadOnlySpan<double> excitation,
        double headroom = HeadroomLimit,
        int minimumRun = 3,
        double flatTolerance = 1e-9)
        => FlatTop(excitation, QualityIssue.InputClipping, headroom, minimumRun, flatTolerance);

    /// <summary>Output clipping: the same flat-top signature, but in the recording, i.e. the capture path clipped.</summary>
    public static QualityCheckResult CheckOutputClipping(
        ReadOnlySpan<double> recording,
        double headroom = HeadroomLimit,
        int minimumRun = 3,
        double flatTolerance = 1e-9)
        => FlatTop(recording, QualityIssue.OutputClipping, headroom, minimumRun, flatTolerance);

    /// <summary>
    /// Sweep completeness: the arrival must be found, the sweep must fit inside the recording, and a capture
    /// that was asked for a pre-roll must not start right on the sweep. <paramref name="expectedPreRollSamples"/>
    /// is the caller's knowledge — a WASAPI loopback capture has no pre-roll at all by design, so its caller
    /// passes 0 and the missing pre-roll is not held against it.
    /// </summary>
    public static QualityCheckResult CheckSweepCompleteness(
        ReadOnlySpan<double> recording,
        int sweepSampleCount,
        int arrivalIndex,
        int expectedPreRollSamples = 0)
    {
        if (sweepSampleCount <= 0) throw new ArgumentOutOfRangeException(nameof(sweepSampleCount), sweepSampleCount, "Sweep length must be > 0 samples.");

        if (arrivalIndex < 0) return QualityCheckResult.Fail(QualityIssue.SweepNotDetected);

        var issues = new List<QualityIssue>();
        // The sweep must end inside the file: arrival + sweepLength <= recordingLength.
        if (arrivalIndex + sweepSampleCount > recording.Length) issues.Add(QualityIssue.SweepTruncatedAtEnd);
        // Arrival at sample 0 while a pre-roll was requested means the recording started on the sweep itself.
        if (arrivalIndex == 0 && expectedPreRollSamples > 0) issues.Add(QualityIssue.SweepTruncatedAtStart);
        return issues.Count == 0 ? QualityCheckResult.Pass : new QualityCheckResult(issues);
    }

    /// <summary>
    /// IR found: a real peak inside the window where it can physically be. For a played sweep that window starts
    /// at the deconvolution's zero-lag reference — a correlation peak before it would mean the recorder heard
    /// something before the sweep, which is how a muted microphone manages to look like a measurement.
    /// </summary>
    public static QualityCheckResult CheckImpulseResponseFound(
        ReadOnlySpan<double> impulseResponse,
        int peakIndex,
        int expectedFirstSample,
        int expectedLastSample)
    {
        bool found = peakIndex >= expectedFirstSample
            && peakIndex <= expectedLastSample
            && peakIndex >= 0
            && peakIndex < impulseResponse.Length
            && impulseResponse[peakIndex] != 0.0;
        return found ? QualityCheckResult.Pass : QualityCheckResult.Fail(QualityIssue.ImpulseResponseNotFound);
    }

    /// <summary>
    /// SNR: the impulse peak against everything else in the deconvolution. signal = RMS over
    /// [peak − window, peak + window], noise = RMS outside [peak − guard, peak + guard],
    /// snr_dB = 20·log10(signal / noise), needs >= <paramref name="minimumDb"/> (default 20 dB).
    /// </summary>
    public static QualityCheckResult CheckSignalToNoise(
        ReadOnlySpan<double> impulseResponse,
        int peakIndex,
        int peakWindowSamples = 16,
        int guardSamples = 64,
        double minimumDb = 20.0)
    {
        if (peakIndex < 0 || peakIndex >= impulseResponse.Length)
            return QualityCheckResult.Fail(QualityIssue.LowSignalToNoise);   // no peak: nothing to compare

        double signal = Rms(impulseResponse, peakIndex - peakWindowSamples, peakIndex + peakWindowSamples + 1);
        double noise = RmsOutside(impulseResponse, peakIndex - guardSamples, peakIndex + guardSamples + 1);
        if (noise <= 0) return QualityCheckResult.Pass;                      // digital silence elsewhere
        if (signal <= 0) return QualityCheckResult.Fail(QualityIssue.LowSignalToNoise);

        double decibels = 20.0 * Math.Log10(signal / noise);
        return decibels >= minimumDb ? QualityCheckResult.Pass : QualityCheckResult.Fail(QualityIssue.LowSignalToNoise);
    }

    /// <summary>
    /// Peak sanity against a reference magnitude (the caller passes the session median — Dsp has no idea what a
    /// session is): |20·log10(|peak| / reference)| must stay within <paramref name="maximumDeviationDb"/>
    /// (default 12 dB, i.e. a factor of 3.98). A non-positive peak or reference fails: there is then nothing to
    /// compare, and silently passing would let a dead measurement count as a reference.
    /// </summary>
    public static QualityCheckResult CheckImpulseResponseOutlier(
        double peakMagnitude,
        double referencePeakMagnitude,
        double maximumDeviationDb = 12.0)
    {
        double magnitude = Math.Abs(peakMagnitude);
        if (referencePeakMagnitude <= 0 || magnitude <= 0) return QualityCheckResult.Fail(QualityIssue.ImpulseResponseOutlier);

        double deviationDb = 20.0 * Math.Log10(magnitude / referencePeakMagnitude);
        return Math.Abs(deviationDb) <= maximumDeviationDb ? QualityCheckResult.Pass : QualityCheckResult.Fail(QualityIssue.ImpulseResponseOutlier);
    }

    /// <summary>
    /// Recording length anomaly: the capture must land inside [minimumSamples, maximumSamples]. An explicit
    /// window rather than a tolerance, because what is acceptable depends on the capture mode: a loopback
    /// recording legitimately has no pre-roll, so its window is narrower and shifted.
    /// </summary>
    public static QualityCheckResult CheckRecordingLength(int recordingLength, int minimumSamples, int maximumSamples)
    {
        if (minimumSamples > maximumSamples)
            throw new ArgumentOutOfRangeException(nameof(minimumSamples), minimumSamples, "Minimum length must be <= maximum length.");

        return recordingLength >= minimumSamples && recordingLength <= maximumSamples
            ? QualityCheckResult.Pass
            : QualityCheckResult.Fail(QualityIssue.RecordingLengthAnomaly);
    }

    /// <summary>
    /// Dropouts inside the sweep: a zero run (>= <paramref name="minimumZeroRunSamples"/> samples at or below
    /// <paramref name="silenceLevel"/>, default 32 samples ≈ 0.67 ms at 48 kHz) or a step larger than
    /// <paramref name="maximumStep"/> (default 0.25; a full-scale 150 Hz sine steps by 2π·150/48000 = 0.0196,
    /// so only a stream discontinuity reaches it). Callers pass the SWEEP's own region: the pre-roll of a normal
    /// capture is silence by construction and would otherwise be reported as a dropout.
    /// </summary>
    public static QualityCheckResult CheckDropouts(
        ReadOnlySpan<double> sweepRegion,
        int minimumZeroRunSamples = 32,
        double silenceLevel = 1e-9,
        double maximumStep = 0.25)
    {
        if (minimumZeroRunSamples < 1) throw new ArgumentOutOfRangeException(nameof(minimumZeroRunSamples), minimumZeroRunSamples, "Run length must be >= 1 sample.");

        int zeroRun = 0;
        for (int i = 0; i < sweepRegion.Length; i++)
        {
            zeroRun = Math.Abs(sweepRegion[i]) <= silenceLevel ? zeroRun + 1 : 0;
            if (zeroRun >= minimumZeroRunSamples) return QualityCheckResult.Fail(QualityIssue.DropoutDetected);
            if (i > 0 && Math.Abs(sweepRegion[i] - sweepRegion[i - 1]) > maximumStep) return QualityCheckResult.Fail(QualityIssue.DropoutDetected);
        }
        return QualityCheckResult.Pass;
    }

    private static QualityCheckResult FlatTop(
        ReadOnlySpan<double> samples,
        QualityIssue issue,
        double headroom,
        int minimumRun,
        double flatTolerance)
    {
        if (minimumRun < 1) throw new ArgumentOutOfRangeException(nameof(minimumRun), minimumRun, "Run length must be >= 1 sample.");

        int run = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            bool flat = Math.Abs(samples[i]) >= headroom
                && (i == 0 || Math.Abs(samples[i] - samples[i - 1]) <= flatTolerance);
            run = flat ? run + 1 : 0;
            if (run >= minimumRun) return QualityCheckResult.Fail(issue);
        }
        return QualityCheckResult.Pass;
    }

    /// <summary>RMS over [from, to), clamped to the buffer; 0 for an empty range.</summary>
    private static double Rms(ReadOnlySpan<double> samples, int from, int to)
    {
        int start = Math.Max(0, from);
        int end = Math.Min(samples.Length, to);
        if (end <= start) return 0;

        double sum = 0;
        for (int i = start; i < end; i++) sum += samples[i] * samples[i];
        return Math.Sqrt(sum / (end - start));
    }

    /// <summary>RMS of everything outside [from, to) — the noise the peak has to stand clear of.</summary>
    private static double RmsOutside(ReadOnlySpan<double> samples, int from, int to)
    {
        int start = Math.Clamp(from, 0, samples.Length);
        int end = Math.Clamp(to, 0, samples.Length);
        int count = start + (samples.Length - end);
        if (count <= 0) return 0;

        double sum = 0;
        for (int i = 0; i < start; i++) sum += samples[i] * samples[i];
        for (int i = end; i < samples.Length; i++) sum += samples[i] * samples[i];
        return Math.Sqrt(sum / count);
    }
}
