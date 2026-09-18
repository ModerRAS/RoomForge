namespace AudioOptimizer.Tests.Visualization;

using AudioOptimizer.Visualization;
using Xunit;

/// <summary>
/// The frequency selector's snapping rule. The point of these tests is the overclaim, not the rounding: a figure may
/// only ever display a frequency that is a member of the analysis's own bin list, and when that differs from what was
/// requested the two numbers must be shown as distinct.
/// </summary>
public sealed class BinSnapTests
{
    /// <summary>48 kHz with the deconvolution padded to FFT 131072: fs/N = 48000/131072 = 0.3662109375 Hz per bin.</summary>
    private static double[] DefaultSpacing(double fromHz = 20.0, double toHz = 130.0)
    {
        const double spacing = 48000.0 / 131072.0;
        int first = (int)Math.Ceiling(fromHz / spacing);
        int last = (int)Math.Floor(toHz / spacing);
        return [.. Enumerable.Range(first, last - first + 1).Select(index => index * spacing)];
    }

    /// <summary>A different analysis: 1 Hz bins, where a requested 25 Hz *is* exactly a bin.</summary>
    private static double[] OneHzBins() => [.. Enumerable.Range(20, 111).Select(hertz => (double)hertz)];

    [Fact]
    public void A_request_between_bins_snaps_to_the_nearest_real_bin_and_reports_it()
    {
        double[] bins = DefaultSpacing();
        (int index, double achieved) = BinSnap.Snap(bins, 25.0);

        // The index is relative to the list handed in (it starts at the first bin ≥ 20 Hz, global bin 55), so 25 Hz
        // lands on global bin 68 = list index 13. Global arithmetic: 25 / 0.3662109375 = 68.26 → bin 68 →
        // 68 × 0.3662109375 = 24.90234375 Hz, the value the lead's hand calculation and this project's own loopback
        // run (0.3662 Hz) both give.
        Assert.Equal(13, index);
        Assert.Equal(68, (int)Math.Round(achieved / (48000.0 / 131072.0)));
        Assert.Equal(24.90234375, achieved, 12);
        Assert.Equal(bins[index], achieved);                 // echoed from the analysis's own list, not recomputed
        Assert.Contains(achieved, bins);                     // the displayed frequency IS a measured bin
        Assert.NotEqual(25.0, achieved);                     // and the request can never pass as the measurement
    }

    [Fact]
    public void A_request_that_is_already_a_bin_lands_on_it_exactly()
    {
        // Same rule, different spacing: with 1 Hz bins the ladder value 25 Hz is a real bin, so nothing moves and the
        // two displayed numbers coincide. A grid of pixels is not required for correctness — this is the twin.
        double[] bins = OneHzBins();
        (int index, double achieved) = BinSnap.Snap(bins, 25.0);

        Assert.Equal(5, index);
        Assert.Equal(25.0, achieved, 12);
        Assert.Contains(achieved, bins);
    }

    [Fact]
    public void A_tie_takes_the_lower_bin_and_a_request_outside_the_band_clamps_inside_it()
    {
        double[] halfHz = [.. Enumerable.Range(40, 41).Select(i => i * 0.5)];   // 20.0, 20.5 … 40.0
        Assert.Equal((9, 24.5), BinSnap.Snap(halfHz, 24.75));                   // 24.5 and 25.0 are both 0.25 away → lower
        Assert.Equal((10, 25.0), BinSnap.Snap(halfHz, 25.25));

        double[] bins = DefaultSpacing();
        Assert.Equal((0, bins[0]), BinSnap.Snap(bins, 20.0));                   // the first real bin is 20.5088 Hz
        Assert.Equal((0, bins[0]), BinSnap.Snap(bins, 5.0));                    // below the band: snap in, not out
        Assert.Equal((bins.Length - 1, bins[^1]), BinSnap.Snap(bins, 500.0));   // and above it
    }

    [Fact]
    public void The_ladder_comes_from_the_band_it_is_given()
    {
        double[] bins = DefaultSpacing();
        IReadOnlyList<double> ladder = BinSnap.Ladder(bins, 5.0);

        Assert.Equal(25.0, ladder[0]);                       // first step ≥ the band's first bin (20.5088 → 25)
        Assert.All(ladder, request => Assert.InRange(request, bins[0], bins[^1]));
        Assert.All(ladder, request => Assert.Equal(0.0, Math.IEEERemainder(request, 5.0), 12));

        // A different band gives a different ladder: nothing about 20…120 Hz is hardcoded.
        double[] wide = DefaultSpacing(10.0, 200.0);
        // The first step at or above the band's first real bin — the band starts at 10.25 Hz here, so the first
        // offer is 15 Hz, not 10: the ladder follows the bins, it is not a fixed 20…120 list.
        Assert.Equal(15.0, BinSnap.Ladder(wide, 5.0)[0]);
        Assert.True(BinSnap.Ladder(wide, 5.0)[0] >= wide[0] - 5.0);
        Assert.True(BinSnap.Ladder(wide, 5.0).Count > ladder.Count);
    }

    [Fact]
    public void An_unsorted_or_empty_bin_list_is_refused()
    {
        Assert.Throws<ArgumentException>(() => BinSnap.Snap([], 25.0));
        Assert.Throws<ArgumentException>(() => BinSnap.Snap([24.0, 23.0], 25.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => BinSnap.Snap([24.0, 25.0], double.NaN));
        Assert.Throws<ArgumentException>(() => BinSnap.Ladder([]));
    }
}
