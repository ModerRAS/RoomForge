namespace AudioOptimizer.Tests;

using AudioOptimizer.Core;

/// <summary>
/// Peak search and cropping. Cropping must never lose the absolute delay: PeakIndex becomes relative
/// to the new Samples while OffsetSamples absorbs the shift, so AbsolutePeakIndex is invariant.
/// </summary>
public class ImpulseResponseTests
{
    [Fact]
    public void Find_peak_returns_the_largest_magnitude_in_the_requested_range()
    {
        double[] samples = [0.1, -0.9, 0.3, 0.0, 0.5, -0.4];   // global argmax|x| = index 1 (|−0.9|)
        var response = new ImpulseResponse(samples, 48000);

        Assert.Equal(1, response.FindPeak());                    // default range = whole array
        Assert.Equal(4, response.FindPeak(from: 3));             // skip the pre-arrival spike: argmax over [3,6) = index 4 (0.5)
        Assert.Equal(2, response.FindPeak(from: 2, to: 4));      // argmax over [2,4) = index 2 (0.3)
        Assert.Equal(1, response.FindPeak(to: 3));               // argmax over [0,3) = index 1 (|−0.9|)
    }

    [Fact]
    public void Peak_search_start_is_used_as_the_default_lower_bound()
    {
        // a leading noise spike at index 0 (|1.0|) that must be excluded from the direct-sound search
        double[] samples = [1.0, 0.2, 0.1, 0.6, 0.05];
        var response = new ImpulseResponse(samples, 48000, PeakSearchStart: 2);

        Assert.Equal(3, response.FindPeak());                  // default range starts at PeakSearchStart = 2, so the spike at 0 is excluded
        Assert.Equal(0, response.FindPeak(from: 0));           // explicitly unrestricted: the noise spike at index 0 wins
        Assert.Equal(3, response.FindPeak(from: response.PeakSearchStart));   // restricted to [2,5): index 3 (0.6)
        Assert.Equal(3, response.WithPeak().PeakIndex);
    }

    [Fact]
    public void Crop_keeps_the_absolute_peak_index()
    {
        double[] samples = new double[100];
        samples[40] = 1.0;
        var response = new ImpulseResponse(samples, 48000, 0).WithPeak();
        Assert.Equal(40, response.PeakIndex);
        Assert.Equal(40, response.AbsolutePeakIndex);

        // crop to [peak−10, peak+10] = indices 30…50 of the original → 21 samples, peak at local index 10
        ImpulseResponse cropped = response.Crop(10, 10);
        Assert.Equal(21, cropped.Samples.Length);
        Assert.Equal(10, cropped.PeakIndex);
        Assert.Equal(30, cropped.OffsetSamples);
        Assert.Equal(40, cropped.AbsolutePeakIndex);          // the delay is retained, not zeroed
        Assert.Equal(1.0, cropped.Samples[10], 1e-15);

        // a second crop must still not move the absolute index: [peak−5, peak+5] → local index 5, offset 35
        ImpulseResponse twice = cropped.Crop(5, 5);
        Assert.Equal(11, twice.Samples.Length);
        Assert.Equal(5, twice.PeakIndex);
        Assert.Equal(35, twice.OffsetSamples);
        Assert.Equal(40, twice.AbsolutePeakIndex);

        // cropping past the array bounds clamps instead of throwing
        ImpulseResponse clamped = response.Crop(1000, 1000);
        Assert.Equal(100, clamped.Samples.Length);
        Assert.Equal(40, clamped.AbsolutePeakIndex);
    }

    [Fact]
    public void Empty_or_invalid_input_is_rejected_clearly()
    {
        var empty = new ImpulseResponse([], 48000);
        Assert.Throws<InvalidOperationException>(() => empty.FindPeak());
        Assert.Throws<InvalidOperationException>(() => empty.Crop(1, 1));

        var response = new ImpulseResponse([0.1, 0.2], 48000);
        Assert.Throws<ArgumentOutOfRangeException>(() => response.Crop(-1, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => response.Crop(1, -2));
    }
}
