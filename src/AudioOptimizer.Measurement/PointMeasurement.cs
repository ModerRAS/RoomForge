namespace AudioOptimizer.Measurement;

using AudioOptimizer.Audio;
using AudioOptimizer.Core;
using AudioOptimizer.Dsp;
using AudioOptimizer.IO;

/// <summary>
/// What one point measured. The recording is the irreplaceable artifact; the impulse response and the frequency
/// response are derived from it, and the alignment carries the arrival/peak/latency the evidence needs.
/// </summary>
public sealed record PointMeasurementResult(
    double[] Recording,
    double[] ImpulseResponse,
    FrequencyResponse[] Response,
    SweepArrivalAnalysis Alignment,
    IReadOnlyList<QualityIssue> Issues,
    double PeakMagnitude,
    int FftSize,
    int SweepSampleCount)
{
    public bool IsValid => Issues.Count == 0;
}

/// <summary>
/// ONE point end to end: play the sweep, record it, deconvolve ONCE, then derive the arrival, the impulse peak,
/// the latency, the frequency response and the quality verdict. The smoke test and <see cref="MeasurementRunner"/>
/// both call this — a second copy of this chain is how a harness and a product drift apart.
/// Seven of the eight quality checks run here; the peak sanity check needs the session median and is therefore
/// the runner's job, because Dsp must not know what a session is.
/// </summary>
public static class PointMeasurement
{
    public static PointMeasurementResult Run(
        IAudioBackend backend,
        AudioDeviceInfo renderDevice,
        AudioDeviceInfo captureDevice,
        AudioBackendSettings settings,
        SweepSettings sweep,
        TimeSpan preRoll,
        TimeSpan postRoll,
        double playbackGain = 1.0,
        AudioCaptureMode captureMode = AudioCaptureMode.Device,
        Action<AudioEngineInfo>? onEngineReady = null)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(renderDevice);
        ArgumentNullException.ThrowIfNull(captureDevice);
        settings.Validate();
        sweep.Validate();

        double[] excitation = SweepGenerator.GenerateExponentialSweep(sweep);
        double[] inverseFilter = InverseFilter.BuildExponentialInverseSweep(sweep);
        // Only the playback copy is scaled: the inverse filter stays the untapered analytic construction, so a
        // quiet run measures exactly the same chain as a full-scale one.
        double[] playback = playbackGain == 1.0 ? excitation : Scale(excitation, playbackGain);
        double[] recording = backend.PlayAndRecord(
            renderDevice, captureDevice, settings, playback, preRoll, postRoll, captureMode, onEngineReady);

        int sampleRate = settings.SampleRate;
        double[] impulseResponse = Deconvolver.Deconvolve(recording, inverseFilter);
        SweepArrivalAnalysis alignment = SweepAlignment.AnalyzeImpulseResponse(
            recording, impulseResponse, inverseFilter.Length - 1, sampleRate);
        int fftSize = Fft.NextPowerOfTwo(impulseResponse.Length);
        FrequencyResponse[] response = FrequencyResponseCalculator.Compute(
            new ImpulseResponse(impulseResponse, sampleRate), WindowType.Rectangular, fftSize);
        double peakMagnitude = alignment.PeakIndex >= 0 ? Math.Abs(impulseResponse[alignment.PeakIndex]) : 0.0;

        int preRollSamples = (int)Math.Round(preRoll.TotalSeconds * sampleRate);
        int postRollSamples = (int)Math.Round(postRoll.TotalSeconds * sampleRate);
        int expectedLength = preRollSamples + excitation.Length + postRollSamples;

        // The dropout check gets the sweep itself, so it must start at the signal's ONSET rather than at the
        // window-aligned arrival: hardware measured 65 samples of leading digital silence in a loopback capture,
        // and slicing at the arrival would hand the check that silence AND cut the sweep's own last 65 samples.
        int onset = SweepAlignment.FindOnset(recording, alignment.ArrivalIndex);
        int regionStart = Math.Max(0, onset);
        int regionLength = onset < 0 ? 0 : Math.Min(excitation.Length, recording.Length - regionStart);

        var checks = new List<QualityCheckResult>
        {
            QualityChecks.CheckInputClipping(excitation),
            QualityChecks.CheckOutputClipping(recording),
            // A loopback capture is armed while the render endpoint is idle, so it carries no pre-roll at all;
            // 0 tells the check that the missing pre-roll is by design rather than a truncated head.
            QualityChecks.CheckSweepCompleteness(
                recording,
                excitation.Length,
                alignment.ArrivalIndex,
                captureMode == AudioCaptureMode.LoopbackCapture ? 0 : preRollSamples),
            // The peak cannot precede the deconvolution's zero-lag reference for a played sweep.
            QualityChecks.CheckImpulseResponseFound(
                impulseResponse, alignment.PeakIndex, alignment.ZeroLagIndex, impulseResponse.Length - 1),
            QualityChecks.CheckSignalToNoise(impulseResponse, alignment.PeakIndex),
            // The window has a floor of one whole sweep and a ceiling of what was asked for plus 1 s of slack.
            QualityChecks.CheckRecordingLength(recording.Length, excitation.Length, expectedLength + sampleRate),
            QualityChecks.CheckDropouts(recording.AsSpan(regionStart, regionLength)),
        };

        MeasurementOutcome outcome = MeasurementOutcome.From([.. checks]);
        return new PointMeasurementResult(
            recording, impulseResponse, response, alignment, outcome.Reasons, peakMagnitude, fftSize, excitation.Length);
    }

    private static double[] Scale(double[] samples, double gain)
    {
        var scaled = new double[samples.Length];
        for (int i = 0; i < samples.Length; i++) scaled[i] = samples[i] * gain;
        return scaled;
    }
}
