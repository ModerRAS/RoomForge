namespace AudioOptimizer.Dsp;

using AudioOptimizer.Core;

/// <summary>
/// What a recorded sweep worked out to: where the sweep arrived, where the deconvolved peak landed, the
/// latency that implies, and the cropped impulse response around that peak.
/// </summary>
/// <param name="ArrivalIndex">First sample of the sweep's arrival in the recording (−1 when none was found).</param>
/// <param name="PeakIndex">Index of the impulse peak in the full deconvolution.</param>
/// <param name="ZeroLagIndex">Where a zero-delay IR peaks for this inverse filter: inverseFilter.Length − 1.</param>
/// <param name="LatencySamples">PeakIndex − ZeroLagIndex: the round-trip delay the recording actually carries.</param>
/// <param name="Cropped">Peak-aligned window of the deconvolution (AbsolutePeakIndex preserved).</param>
public sealed record SweepArrivalAnalysis(
    int ArrivalIndex,
    int PeakIndex,
    int ZeroLagIndex,
    int LatencySamples,
    ImpulseResponse Cropped);

/// <summary>
/// Spec §7 chain for a captured sweep: arrival → IR peak → latency → cropped IR. The smoke test prints these
/// numbers, so a hardware run that claims timing without them is not evidence.
/// LatencySamples is measured from the START OF THE RECORDING: it is (pre-roll + device + acoustic) delay,
/// and the recording's own pre-roll has to be subtracted by the caller to get the device latency alone.
/// ponytail: arrival is an energy-envelope threshold with a 10th-percentile noise floor, not a matched filter.
/// That needs at least ~10% of the recording to be noise-only (i.e. a capture with pre/post-roll silence, which
/// is what the harness records). A recording that is wall-to-wall signal reports −1 for "arrival unknown"
/// rather than a wrong index; use a correlation-based arrival if back-to-back sweeps ever get captured.
/// </summary>
public static class SweepAlignment
{
    /// <summary>
    /// First sample index where the recording's short-window RMS crosses max(peakFraction·peakRms,
    /// noiseMultiple·noiseFloor), or −1 when nothing crosses (silence, or no quiet stretch to call a floor).
    /// </summary>
    public static int FindArrival(double[] recording, int windowLength = 256, double peakFraction = 0.1, double noiseMultiple = 4.0)
    {
        ArgumentNullException.ThrowIfNull(recording);
        if (recording.Length == 0) throw new ArgumentException("Recording is empty.", nameof(recording));
        if (windowLength < 1) throw new ArgumentOutOfRangeException(nameof(windowLength), windowLength, "Window must be >= 1 sample.");

        int windows = (recording.Length + windowLength - 1) / windowLength;
        var rms = new double[windows];
        double peakRms = 0;
        for (int w = 0; w < windows; w++)
        {
            int start = w * windowLength;
            int end = Math.Min(start + windowLength, recording.Length);
            double sum = 0;
            for (int i = start; i < end; i++) sum += recording[i] * recording[i];
            rms[w] = Math.Sqrt(sum / (end - start));
            peakRms = Math.Max(peakRms, rms[w]);
        }

        if (peakRms <= 0) return -1;   // digital silence

        // 10th percentile, not median: a 1 s sweep with 0.25 s of pre/post-roll already puts 66% of the
        // windows in the signal, and the median of that is a signal window (measured: median gives −1).
        var sorted = (double[])rms.Clone();
        Array.Sort(sorted);
        double noiseFloor = sorted[sorted.Length / 10];
        double threshold = Math.Max(peakFraction * peakRms, noiseMultiple * noiseFloor);

        for (int w = 0; w < windows; w++)
            if (rms[w] >= threshold) return w * windowLength;
        return -1;
    }

    /// <summary>
    /// First sample at or after <paramref name="from"/> whose magnitude exceeds <paramref name="silenceLevel"/>:
    /// where the sweep really starts. <see cref="FindArrival"/> deliberately reports a WINDOW start — a multiple
    /// of the window length, up to windowLength − 1 samples early — and a WASAPI loopback recording genuinely
    /// begins with digital silence (measured: 65 samples, 1.35 ms at 48 kHz, before the taper's first sample).
    /// So anything that slices "the sweep" out of a recording needs this, or it hands the quality checks a region
    /// with pre-sweep silence at one end and the sweep's own tail cut off at the other. Returns −1 when nothing
    /// after <paramref name="from"/> rises above silence (and for a negative <paramref name="from"/>, which is
    /// how a caller passes "no arrival was found").
    /// </summary>
    public static int FindOnset(double[] recording, int from, double silenceLevel = 1e-9)
    {
        ArgumentNullException.ThrowIfNull(recording);
        if (from < 0) return -1;

        for (int i = Math.Max(0, from); i < recording.Length; i++)
            if (Math.Abs(recording[i]) > silenceLevel) return i;
        return -1;
    }

    /// <summary>
    /// Deconvolves the recording, finds the impulse peak, derives the latency and crops a peak-aligned window.
    /// The peak search starts at the detected arrival so the pre-arrival noise floor cannot win.
    /// </summary>
    public static SweepArrivalAnalysis Analyze(
        double[] recording,
        double[] inverseFilter,
        int sampleRate,
        int cropPre = 2048,
        int cropPost = 8192)
    {
        ArgumentNullException.ThrowIfNull(recording);
        ArgumentNullException.ThrowIfNull(inverseFilter);
        return AnalyzeImpulseResponse(recording, Deconvolver.Deconvolve(recording, inverseFilter), inverseFilter.Length - 1, sampleRate, cropPre, cropPost);
    }

    /// <summary>
    /// The same analysis for a caller that already has the deconvolution — the measurement path deconvolves
    /// once and feeds both this and the frequency response. The arrival is still found in the RECORDING: it is
    /// an envelope property of the capture, not of the impulse response, and the peak search starts there so the
    /// pre-arrival noise floor cannot win. <paramref name="zeroLagIndex"/> is inverseFilter.Length − 1.
    /// </summary>
    public static SweepArrivalAnalysis AnalyzeImpulseResponse(
        double[] recording,
        double[] impulseResponse,
        int zeroLagIndex,
        int sampleRate,
        int cropPre = 2048,
        int cropPost = 8192)
    {
        ArgumentNullException.ThrowIfNull(recording);
        ArgumentNullException.ThrowIfNull(impulseResponse);
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "Sample rate must be > 0.");
        if (impulseResponse.Length == 0) throw new ArgumentException("Impulse response is empty.", nameof(impulseResponse));

        int arrival = FindArrival(recording);
        int zeroLag = zeroLagIndex;

        var response = new ImpulseResponse(impulseResponse, sampleRate);
        int searchFrom = arrival >= 0 ? Math.Min(arrival, impulseResponse.Length - 1) : 0;
        int peak = response.FindPeak(from: searchFrom);

        ImpulseResponse cropped = (response with { PeakIndex = peak }).Crop(cropPre, cropPost);
        return new SweepArrivalAnalysis(arrival, peak, zeroLag, peak - zeroLag, cropped);
    }
}
