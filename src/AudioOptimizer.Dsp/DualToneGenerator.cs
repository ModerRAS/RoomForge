namespace AudioOptimizer.Dsp;

/// <summary>
/// Dual-tone IMD stimulus: two coherent sines <c>a1·sin(2πf1t) + a2·sin(2πf2t)</c>.
/// </summary>
/// <remarks>
/// The API is deliberately generic — the caller chooses the frequency pair. Common
/// DIN/SMPTE-style choices for amplifier comparisons are e.g. 60 Hz + 7000 Hz at 4:1
/// amplitude (low-frequency tone large, high-frequency tone small) or a close high-frequency
/// pair such as 250 Hz + 8020 Hz (CCIF-style difference-tone test); this generator does not
/// prescribe any pair, it just synthesises whatever two tones are asked for.
/// Hardware playback/capture wiring (WASAPI duplex) is NOT part of this type — see
/// <c>docs/imd-multitone.md</c>.
/// </remarks>
public static class DualToneGenerator
{
    /// <summary>
    /// Generates two coherent sines with independent amplitudes, an optional
    /// raised-cosine fade in/out, and a hard peak cap (scaled down if exceeded).
    /// </summary>
    /// <param name="f1Hz">First tone in Hz, in (0, fs/2).</param>
    /// <param name="f2Hz">Second tone in Hz, in (0, fs/2).</param>
    /// <param name="amplitude1">Peak amplitude of tone 1 (1.0 = full scale).</param>
    /// <param name="amplitude2">Peak amplitude of tone 2 (1.0 = full scale).</param>
    /// <param name="sampleRate">Sample rate in Hz.</param>
    /// <param name="durationSeconds">Duration in seconds; length = round(duration·fs).</param>
    /// <param name="fadeSeconds">Raised-cosine fade at each end (0 = none).</param>
    /// <param name="maxPeak">Hard cap on |sample|, in (0, 1].</param>
    public static double[] Generate(
        double f1Hz,
        double f2Hz,
        double amplitude1,
        double amplitude2,
        double sampleRate,
        double durationSeconds,
        double fadeSeconds = 0.005,
        double maxPeak = 1.0)
        => MultiToneGenerator.Generate(
            [f1Hz, f2Hz], [amplitude1, amplitude2],
            sampleRate, durationSeconds, fadeSeconds, maxPeak);

    /// <summary>Equal-amplitude convenience overload: both tones get <paramref name="amplitudePerTone"/>.</summary>
    public static double[] GenerateEqual(
        double f1Hz,
        double f2Hz,
        double amplitudePerTone,
        double sampleRate,
        double durationSeconds,
        double fadeSeconds = 0.005,
        double maxPeak = 1.0)
        => MultiToneGenerator.Generate(
            [f1Hz, f2Hz], [amplitudePerTone, amplitudePerTone],
            sampleRate, durationSeconds, fadeSeconds, maxPeak);
}
