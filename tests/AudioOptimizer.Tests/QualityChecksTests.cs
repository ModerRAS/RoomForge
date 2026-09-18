namespace AudioOptimizer.Tests;

using AudioOptimizer.Dsp;
using AudioOptimizer.Core;
using Xunit.Abstractions;

/// <summary>
/// The eight quality checks, each with a synthetic signal that must pass and one that must fail. Every check
/// returns named reasons: these tests assert the EXACT reason, not just "invalid", because a check that fails
/// for the wrong reason is as useless as one that never fails.
/// </summary>
public sealed class QualityChecksTests(ITestOutputHelper output)
{
    // 1 s exponential sweep 20 → 150 Hz at 48 kHz = 48000 samples, the shipped excitation.
    private static double[] Sweep(double gain = 1.0)
    {
        double[] sweep = SweepGenerator.GenerateExponentialSweep(new SweepSettings(20, 150, 1.0, 48000));
        if (gain != 1.0) for (int i = 0; i < sweep.Length; i++) sweep[i] *= gain;
        return sweep;
    }

    /// <summary>Deterministic "noise": an alternating ±level signal has RMS exactly = level, no RNG involved.</summary>
    private static double[] AlternatingNoise(int length, double level)
    {
        var signal = new double[length];
        for (int i = 0; i < length; i++) signal[i] = (i % 2 == 0 ? level : -level);
        return signal;
    }

    // ---------------------------------------------------------------- 1. input clipping

    [Fact]
    public void Input_clipping_passes_for_a_loud_sine_and_fails_for_a_full_scale_plateau()
    {
        // PASS: the shipped sweep peaks at ~0.9999966 ≥ headroom 1 − 1/32768 = 0.99996948 for about six
        // consecutive samples near each low-frequency peak, but adjacent samples differ by ~1e-5 ≫ 1e-9, so
        // there is no plateau. A pure level threshold would wrongly flag this.
        double[] loud = Sweep();
        QualityCheckResult loudResult = QualityChecks.CheckInputClipping(loud);
        output.WriteLine($"loud sweep: {loudResult.IsValid}, max |x| = {loud.Max(Math.Abs):F9}");
        Assert.True(loudResult.IsValid);
        Assert.True(loud.Max(Math.Abs) >= QualityChecks.HeadroomLimit);   // it really is above the limit

        // FAIL: five consecutive samples pinned to exactly 1.0 — a clamped flat top (min run 3).
        double[] clipped = Sweep();
        for (int i = 1000; i < 1005; i++) clipped[i] = 1.0;
        Assert.Equal([QualityIssue.InputClipping], QualityChecks.CheckInputClipping(clipped).Issues);
    }

    // ---------------------------------------------------------------- 2. output / headroom clipping

    [Fact]
    public void Output_clipping_passes_for_a_quiet_recording_and_fails_when_the_capture_clipped()
    {
        // PASS: half-scale sweep, max |x| = 0.5 < 0.99996948, no plateau.
        Assert.True(QualityChecks.CheckOutputClipping(Sweep(0.5)).IsValid);

        // FAIL: a capture that clipped holds the same code for a run of samples — four samples at 1.0.
        double[] clipped = Sweep(0.5);
        for (int i = 20000; i < 20004; i++) clipped[i] = 1.0;
        Assert.Equal([QualityIssue.OutputClipping], QualityChecks.CheckOutputClipping(clipped).Issues);
    }

    // ---------------------------------------------------------------- 3. sweep completeness

    [Fact]
    public void Sweep_completeness_passes_with_a_pre_roll_and_fails_for_each_kind_of_truncation()
    {
        // PASS: 24000 pre-roll + 48000 sweep + 24000 post-roll = 96000 samples, arrival at 24000.
        // arrival + sweep = 24000 + 48000 = 72000 ≤ 96000, and arrival != 0, so nothing is truncated.
        Assert.True(QualityChecks.CheckSweepCompleteness(new double[96000], 48000, 24000, expectedPreRollSamples: 24000).IsValid);

        // FAIL (no arrival): −1 is "not found", the value FindArrival returns for silence.
        Assert.Equal([QualityIssue.SweepNotDetected],
            QualityChecks.CheckSweepCompleteness(new double[96000], 48000, arrivalIndex: -1, expectedPreRollSamples: 24000).Issues);

        // FAIL (tail cut): the file ends at 30000, but 24000 + 48000 = 72000 samples are needed.
        Assert.Equal([QualityIssue.SweepTruncatedAtEnd],
            QualityChecks.CheckSweepCompleteness(new double[30000], 48000, 24000, expectedPreRollSamples: 24000).Issues);

        // FAIL (head cut): a pre-roll of 24000 was asked for, yet the recording starts on the sweep itself.
        Assert.Equal([QualityIssue.SweepTruncatedAtStart],
            QualityChecks.CheckSweepCompleteness(new double[96000], 48000, arrivalIndex: 0, expectedPreRollSamples: 24000).Issues);

        // Same arrival as the failure above, but no pre-roll was requested (a loopback capture): valid, because
        // a loopback recording legitimately begins with the sweep.
        Assert.True(QualityChecks.CheckSweepCompleteness(new double[96000], 48000, 0, expectedPreRollSamples: 0).IsValid);
    }

    // ---------------------------------------------------------------- 4. impulse response found

    [Fact]
    public void Impulse_response_found_passes_inside_the_window_and_fails_outside_it()
    {
        // PASS: a peak at sample 500, expected window [100, 1000], value != 0.
        double[] impulse = new double[4096];
        impulse[500] = 1.0;
        Assert.True(QualityChecks.CheckImpulseResponseFound(impulse, 500, 100, 1000).IsValid);

        // FAIL: the peak sits before the window. On hardware this is the muted-microphone case: the peak landed
        // 25516 samples before the zero-lag reference 47999, which no played sweep can do.
        Assert.Equal([QualityIssue.ImpulseResponseNotFound],
            QualityChecks.CheckImpulseResponseFound(impulse, 50, 100, 1000).Issues);

        // FAIL: no peak at all (−1), and a peak that is exactly zero is not a peak either.
        Assert.Equal([QualityIssue.ImpulseResponseNotFound], QualityChecks.CheckImpulseResponseFound(impulse, -1, 0, 4095).Issues);
        impulse[500] = 0.0;
        Assert.Equal([QualityIssue.ImpulseResponseNotFound], QualityChecks.CheckImpulseResponseFound(impulse, 500, 0, 4095).Issues);
    }

    // ---------------------------------------------------------------- 5. SNR

    [Fact]
    public void Signal_to_noise_passes_for_a_clear_peak_and_fails_for_a_peak_below_the_floor()
    {
        // PASS: alternating noise ±1e-4 (RMS = 1e-4 exactly) plus a unit spike at 500.
        // signal = RMS over [484, 516] = sqrt(1²/33) = 0.174078; noise = 1e-4 (the spike is inside the ±64 guard)
        // → SNR = 20·log10(0.174078/1e-4) = 64.82 dB ≥ 20 dB.
        double[] clear = AlternatingNoise(4096, 1e-4);
        clear[500] = 1.0;
        Assert.True(QualityChecks.CheckSignalToNoise(clear, 500).IsValid);

        // FAIL: the same noise with a spike of 2e-4 → signal = sqrt((32·(1e-4)² + (2e-4)²)/33) = 1.0445e-4
        // → SNR = 20·log10(1.0445e-4/1e-4) = 0.378 dB < 20 dB.
        double[] buried = AlternatingNoise(4096, 1e-4);
        buried[500] = 2e-4;
        QualityCheckResult buriedResult = QualityChecks.CheckSignalToNoise(buried, 500);
        output.WriteLine($"buried peak: {buriedResult.IsValid} (expected SNR 0.38 dB)");
        Assert.Equal([QualityIssue.LowSignalToNoise], buriedResult.Issues);
    }

    // ---------------------------------------------------------------- 6. peak sanity vs reference

    [Fact]
    public void Impulse_peak_outlier_passes_within_12_db_of_the_reference_and_fails_beyond_it()
    {
        // PASS: 20·log10(0.0055/0.0050) = 0.83 dB ≤ 12 dB.
        Assert.True(QualityChecks.CheckImpulseResponseOutlier(0.0055, 0.0050).IsValid);

        // FAIL: 20·log10(0.0055/0.00055) = 20.0 dB > 12 dB (the reference is the caller's business — here it
        // stands in for the session median, which Dsp is deliberately ignorant of).
        Assert.Equal([QualityIssue.ImpulseResponseOutlier], QualityChecks.CheckImpulseResponseOutlier(0.0055, 0.00055).Issues);

        // FAIL: no reference (<= 0) or no peak means nothing can be compared — passing would let a dead
        // measurement become the reference for every later point.
        Assert.Equal([QualityIssue.ImpulseResponseOutlier], QualityChecks.CheckImpulseResponseOutlier(0.0055, 0).Issues);
        Assert.Equal([QualityIssue.ImpulseResponseOutlier], QualityChecks.CheckImpulseResponseOutlier(0, 0.0055).Issues);
    }

    // ---------------------------------------------------------------- 7. recording length

    [Fact]
    public void Recording_length_passes_inside_the_window_and_fails_outside_it()
    {
        // PASS: 96000 samples inside [48000, 144000] (sweep .. expected + 1 s of slack).
        Assert.True(QualityChecks.CheckRecordingLength(96000, 48000, 144000).IsValid);

        // FAIL: a 1000-sample capture cannot hold a 48000-sample sweep, and 300000 is far longer than asked.
        Assert.Equal([QualityIssue.RecordingLengthAnomaly], QualityChecks.CheckRecordingLength(1000, 48000, 144000).Issues);
        Assert.Equal([QualityIssue.RecordingLengthAnomaly], QualityChecks.CheckRecordingLength(300000, 48000, 144000).Issues);

        // An inverted window is a caller bug, not a quality verdict.
        Assert.Throws<ArgumentOutOfRangeException>(() => QualityChecks.CheckRecordingLength(1000, 144000, 48000));
    }

    // ---------------------------------------------------------------- 8. dropouts

    [Fact]
    public void Dropouts_pass_for_a_tapered_sweep_and_fail_for_a_zero_run_or_a_step()
    {
        // PASS: the shipped sweep's own region. Its largest sample-to-sample step is 2π·150/48000 = 0.0196 ≪ 0.25,
        // and the 5 ms raised-cosine taper only produces 1–2 samples of exact zero at the very start, far below
        // the 32-sample zero run. This is why the caller must pass the sweep region: the pre-roll of a real
        // capture is silence by construction.
        double[] sweep = Sweep();
        Assert.True(QualityChecks.CheckDropouts(sweep).IsValid);

        // FAIL (zero run): 100 samples of digital silence in the middle of the sweep.
        double[] gapped = Sweep();
        for (int i = 20000; i < 20100; i++) gapped[i] = 0.0;
        Assert.Equal([QualityIssue.DropoutDetected], QualityChecks.CheckDropouts(gapped).Issues);

        // FAIL (discontinuity): a single 0.5 step, 25× the sweep's own 0.0196 maximum step.
        double[] stepped = Sweep();
        stepped[30000] += 0.5;
        Assert.Equal([QualityIssue.DropoutDetected], QualityChecks.CheckDropouts(stepped).Issues);
    }

    [Fact]
    public void A_region_sliced_at_the_window_arrival_fails_on_real_leading_silence_and_passes_once_onset_aligned()
    {
        // Measured on hardware, not imagined: a Realtek WASAPI loopback recording of a 0.05-gain sweep begins
        // with exactly 65 samples of digital zero (the recorder is armed before playback starts), its largest
        // sample-to-sample step is 0.000968, and its only zero run is that leading one.
        double[] sweep = Sweep(0.05);
        double[] recording = [.. new double[65], .. sweep];      // 65 leading zeros, then the real sweep

        // Slicing at FindArrival's window start (a multiple of 256) hands the check the leading silence, so it
        // is flagged — correctly: inside the passed region that IS a 65-sample zero run.
        Assert.Equal([QualityIssue.DropoutDetected],
            QualityChecks.CheckDropouts(recording.AsSpan(0, sweep.Length)).Issues);

        // Sliced at the signal onset (what PointMeasurement does now) the same recording is clean, and the sweep
        // is also no longer truncated by those 65 samples at the far end.
        Assert.True(QualityChecks.CheckDropouts(recording.AsSpan(65, sweep.Length)).IsValid);
    }
}
