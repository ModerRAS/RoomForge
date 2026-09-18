namespace AudioOptimizer.Tests;

using AudioOptimizer.Core;
using AudioOptimizer.Dsp;
using Xunit.Abstractions;

/// <summary>
/// Spec §7 chain (arrival → IR peak → latency → cropped IR) against synthetic recordings: the sweep delayed
/// by a known amount, with added deterministic noise. No hardware, no sleeps, fixed seed.
/// Zero-lag reference for the ESS inverse is inverseFilter.Length − 1 = 47999 (Phase 4 convention), so a
/// recording delayed by D samples must peak at 47999 + D and report LatencySamples == D.
/// </summary>
public class SweepAlignmentTests(ITestOutputHelper output)
{
    private const int Fs = 48000;
    private static readonly SweepSettings Settings = new();   // 20 → 150 Hz, 1 s, 48 kHz
    private static readonly double[] Excitation = SweepGenerator.GenerateExponentialSweep(Settings);
    private static readonly double[] Inverse = InverseFilter.BuildExponentialInverseSweep(Settings);

    /// <summary>[lead-in silence][sweep delayed by delaySamples][lead-out silence] + noise of given amplitude.</summary>
    private static double[] Recording(int delaySamples, int leadIn = 12000, int leadOut = 12000, double noiseAmplitude = 0.0, int seed = 12345)
    {
        var recording = new double[leadIn + delaySamples + Excitation.Length + leadOut];
        Array.Copy(Excitation, 0, recording, leadIn + delaySamples, Excitation.Length);
        if (noiseAmplitude > 0)
        {
            var random = new Random(seed);
            for (int i = 0; i < recording.Length; i++)
                recording[i] += noiseAmplitude * (2.0 * random.NextDouble() - 1.0);
        }
        return recording;
    }

    [Theory]
    [InlineData(0, 0.0)]
    [InlineData(168, 0.0)]
    [InlineData(1000, 0.0)]
    [InlineData(4800, 0.0)]
    [InlineData(168, 0.01)]      // ~40 dB SNR against the 0.66 sweep peak
    [InlineData(1000, 0.01)]
    [InlineData(4800, 0.01)]
    public void Delay_is_recovered_from_the_peak_with_and_without_noise(int delaySamples, double noiseAmplitude)
    {
        double[] recording = Recording(delaySamples, noiseAmplitude: noiseAmplitude);
        SweepArrivalAnalysis analysis = SweepAlignment.Analyze(recording, Inverse, Fs);

        output.WriteLine($"D={delaySamples} noise={noiseAmplitude}: arrival={analysis.ArrivalIndex} peak={analysis.PeakIndex} "
            + $"zeroLag={analysis.ZeroLagIndex} latency={analysis.LatencySamples} (true lead-in+delay={12000 + delaySamples})");

        // Shift theorem: delaying the recording by D moves the deconvolution peak by exactly D, so the
        // measured latency is the recording's own pre-roll (12000) plus D — measured 12168 for D = 168.
        Assert.Equal(Inverse.Length - 1, analysis.ZeroLagIndex);
        Assert.InRange(analysis.LatencySamples - (12000 + delaySamples), -1, 1);

        // The arrival is an energy-envelope threshold, so it lands shortly AFTER the true sweep start
        // (the 5 ms taper plus the slow 20 Hz opening): measured within ~2 windows (512 samples) below
        // and ~3000 samples above. Tolerance is deliberately loose here — it is a gate, not a measurement.
        Assert.InRange(analysis.ArrivalIndex, 12000 + delaySamples - 512, 12000 + delaySamples + 3000);
    }

    [Fact]
    public void Cropped_impulse_response_is_peak_aligned_and_keeps_absolute_position()
    {
        const int delay = 1000;
        double[] recording = Recording(delay, noiseAmplitude: 0.01);
        SweepArrivalAnalysis analysis = SweepAlignment.Analyze(recording, Inverse, Fs, cropPre: 2048, cropPost: 8192);

        output.WriteLine($"cropped: len={analysis.Cropped.Samples.Length} peakIndex={analysis.Cropped.PeakIndex} "
            + $"offset={analysis.Cropped.OffsetSamples} absolutePeak={analysis.Cropped.AbsolutePeakIndex}");

        Assert.Equal(2048 + 8192 + 1, analysis.Cropped.Samples.Length);   // [peak − pre, peak + post] inclusive
        Assert.Equal(2048, analysis.Cropped.PeakIndex);                    // not clamped: the peak is far from both ends
        Assert.Equal(analysis.PeakIndex, analysis.Cropped.AbsolutePeakIndex);
        Assert.Equal(12000 + delay, analysis.Cropped.AbsolutePeakIndex - analysis.ZeroLagIndex);   // pre-roll + delay
        Assert.Equal(Fs, analysis.Cropped.SampleRate);
    }

    [Fact]
    public void Near_the_edges_the_crop_clamps_instead_of_throwing()
    {
        // Peak near the very start: the leading margin is clamped, the length shrinks, AbsolutePeakIndex holds.
        double[] recording = new double[Excitation.Length + 48000 + 47999];
        Array.Copy(Excitation, 0, recording, 0, Excitation.Length);
        SweepArrivalAnalysis analysis = SweepAlignment.Analyze(recording, Inverse, Fs, cropPre: 2048, cropPost: 8192);
        output.WriteLine($"edge case: peak={analysis.PeakIndex} cropped len={analysis.Cropped.Samples.Length} "
            + $"croppedPeak={analysis.Cropped.PeakIndex} absolute={analysis.Cropped.AbsolutePeakIndex}");
        Assert.True(analysis.Cropped.Samples.Length <= 2048 + 8192 + 1);
        Assert.Equal(analysis.PeakIndex, analysis.Cropped.AbsolutePeakIndex);
    }

    [Fact]
    public void Silence_and_noise_only_report_no_arrival_instead_of_throwing()
    {
        // Digital silence: the whole point is a clear "nothing here", not a divide-by-zero or an exception.
        double[] silence = new double[48000];
        Assert.Equal(-1, SweepAlignment.FindArrival(silence));

        // Noise only: peakRms ≈ 1.1·noiseFloor, so max(0.1·peakRms, 4·noiseFloor) has no chance of being crossed.
        var random = new Random(999);
        var noise = new double[48000];
        for (int i = 0; i < noise.Length; i++) noise[i] = 0.01 * (2.0 * random.NextDouble() - 1.0);
        int arrival = SweepAlignment.FindArrival(noise);
        output.WriteLine($"noise-only arrival = {arrival}");
        Assert.Equal(-1, arrival);

        // Wall-to-wall signal (no quiet stretch to call a noise floor) reports "unknown", never a wrong index.
        Assert.Equal(-1, SweepAlignment.FindArrival(Excitation));

        Assert.Throws<ArgumentException>(() => SweepAlignment.FindArrival([]));
        Assert.Throws<ArgumentOutOfRangeException>(() => SweepAlignment.FindArrival(silence, windowLength: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => SweepAlignment.Analyze(silence, Inverse, sampleRate: 0));
    }

    [Fact]
    public void FindOnset_reports_where_the_sweep_really_starts_not_the_analysis_window()
    {
        // Hardware case: a WASAPI loopback recording starts with 65 samples of digital silence (1.35 ms at
        // 48 kHz) before the taper's first sample. FindArrival then reports window 0 (a multiple of 256), which
        // precedes the signal by up to windowLength − 1 = 255 samples, so the sweep's region must be sliced at
        // the onset instead.
        double[] sweep = SweepGenerator.GenerateExponentialSweep(new SweepSettings(20, 150, 1.0, 48000));
        // Exactly the shape of the captured file. The engine silence is 64 samples and the sweep's own first
        // sample is 0.0 (the raised-cosine taper starts at zero), which together are the measured 65-sample zero
        // run, so the first sample above silence is index 65. The tail is not decoration either: FindArrival
        // takes its noise floor from the quietest 10% of the recording, so a wall-to-wall signal reports
        // "arrival unknown" (−1) by design.
        double[] recording = [.. new double[64], .. sweep, .. new double[18000]];

        // Window 0 (samples 0–255) crosses the threshold, so the arrival is reported as 0 — a window boundary
        // 65 samples before the signal. That is the gap FindOnset exists to close.
        Assert.Equal(0, SweepAlignment.FindArrival(recording));
        Assert.Equal(65, SweepAlignment.FindOnset(recording, 0));           // the first sample above silence
        Assert.Equal(65, SweepAlignment.FindOnset(recording, 64));          // searching from just before the onset
        Assert.Equal(65, SweepAlignment.FindOnset(recording, 65));          // already at the onset: returns it unchanged
        Assert.Equal(-1, SweepAlignment.FindOnset(recording, recording.Length));   // past the end: nothing found
        Assert.Equal(-1, SweepAlignment.FindOnset(recording, -1));          // "no arrival was found" passes through

        // All-silent input has no onset, and the null argument is a caller bug rather than a silent −1.
        Assert.Equal(-1, SweepAlignment.FindOnset(new double[512], 0));
        Assert.Throws<ArgumentNullException>(() => SweepAlignment.FindOnset(null!, 0));
    }
}
