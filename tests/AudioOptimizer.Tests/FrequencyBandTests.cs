namespace AudioOptimizer.Tests;

using System.IO;
using AudioOptimizer.Core;
using AudioOptimizer.Measurement;
using Xunit;

/// <summary>
/// The band's edge rule as a pure predicate. It cannot be tested against a real session: the real bin grid has no bin at
/// either edge, so an edge assertion there is vacuous (no such bin) or wrong (calling the last measured bin "the edge").
/// The expected result is hand-written rather than computed through the predicate under test, which would restate it.
/// </summary>
public sealed class FrequencyBandTests
{
    [Fact]
    public void The_band_is_inclusive_at_both_edges()
    {
        var band = new FrequencyBand(20.0, 150.0);
        double[] bins = [19.5, 20.0, 20.5, 149.5, 150.0, 150.5];

        // Hand-written: the two edge bins are IN (inclusive), the two outside are out — 4 of 6.
        Assert.Equal([20.0, 20.5, 149.5, 150.0], [.. bins.Where(band.Contains)]);
        Assert.Equal(4, bins.Count(band.Contains));
        Assert.True(band.Contains(20.0));
        Assert.True(band.Contains(150.0));
        Assert.False(band.Contains(19.999));
        Assert.False(band.Contains(150.001));
        Assert.Equal(130.0, band.SpanHz, 12);
        Assert.Equal("20–150 Hz", band.ToString());

        // The only way to obtain a band is from the sweep that produced the measurement: there is no "no band" value.
        Assert.Equal(band, FrequencyBand.Of(new SweepSettings(20.0, 150.0, 1.0, 48000)));
    }

    [Fact]
    public void The_in_band_bin_count_follows_the_capture_length_not_a_constant()
    {
        // WHERE THE RESOLUTION COMES FROM: FrequencyResponseCalculator.Compute is called with no fftSize, so it defaults
        // to NextPowerOfTwo(ir.Samples.Length), and the IR is the uncropped deconvolution output of length
        // recordingLength + inverseFilterLength − 1. Therefore
        //     Δf = fs / NextPowerOfTwo(recordingLength + inverseFilterLength − 1)
        // and the in-band BIN COUNT is a function of the CAPTURE LENGTH, not of the band. That is why both of these
        // are correct for their own recording, and why pasting either one in as a constant is the defect:
        //   143999 → 262144 → Δf = 48000/262144 = 0.18310546875  → [20,150] → 710 bins  (the full 2.0 s session: 0.5 s
        //                                                                          pre + 1 s sweep + 0.5 s post)
        //   114719 → 131072 → Δf = 48000/131072 = 0.3662109375   → [20,150] → 355 bins  (the 0.5 s loopback capture)
        (int longCount, double longFirst, double longLast, int longFull) = InBand(143999);
        (int shortCount, double shortFirst, double shortLast, int shortFull) = InBand(114719);

        Assert.Equal(131073, longFull);                               // 262144/2 + 1 one-sided bins
        Assert.Equal(65537, shortFull);                               // 131072/2 + 1
        Assert.Equal(710, longCount);
        Assert.Equal(355, shortCount);
        Assert.Equal(20.1416015625, longFirst, 9);                    // 110 × 0.18310546875
        Assert.Equal(149.96337890625, longLast, 9);                   // 819 × 0.18310546875
        Assert.Equal(20.1416015625, shortFirst, 9);                   // 55 × 0.3662109375 — same first bin, 55 × 2 = 110
        Assert.Equal(149.7802734375, shortLast, 9);                   // 409 × 0.3662109375

        // THE assertion that fails if someone later pastes a count in as a constant: two recordings of this same
        // sweep, same band, different counts.
        Assert.NotEqual(longCount, shortCount);
        Assert.NotEqual(710, shortCount);
        Assert.NotEqual(355, longCount);
    }

    /// <summary>
    /// A session whose stored impulse response has this many samples, and the in-band bins derived from it. The grid
    /// is set by the IR exactly as in a real capture, because the recording length is the inverse of the same formula.
    /// </summary>
    private static (int Count, double First, double Last, int FullCount) InBand(int irLength)
    {
        const int sampleRate = 48000;
        string directory = Path.Combine(Path.GetTempPath(), $"roomforge-band-{irLength}-{Guid.NewGuid():N}");
        var sweep = new SweepSettings(20.0, 150.0, 1.0, sampleRate);
        MeasurementSession session = MeasurementSession.Start(directory, MeasurementGrid.Create(1.8, 1.0, 0.6, 1, 1, 2), sweep);
        int recordingLength = irLength - sampleRate + 1;              // the exact inverse of the deconvolution length
        MeasurementSlot slot = session.MarkDone(
            session.NextPending!,
            new double[recordingLength],
            new double[irLength],                                      // the deconvolution output: its length sets the FFT
            peakMagnitude: 0.1);

        FrequencyResponse[] full = session.FrequencyResponseOf(slot)!;
        FrequencyResponse[] inBand = session.InBandResponseOf(slot)!;
        // Geometry-independent relation, true for any capture: the in-band list is the full spectrum filtered by the
        // band, same order and same length. Nothing here re-derives a frequency.
        Assert.Equal([.. full.Where(bin => session.Band.Contains(bin.FrequencyHz)).Select(bin => bin.FrequencyHz)],
            [.. inBand.Select(bin => bin.FrequencyHz)]);
        return (inBand.Length, inBand[0].FrequencyHz, inBand[^1].FrequencyHz, full.Length);
    }
}
