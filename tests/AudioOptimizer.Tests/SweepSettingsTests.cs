namespace AudioOptimizer.Tests;

using AudioOptimizer.Core;

/// <summary>SweepSettings validation is on demand, so the sweep maths can be inspected before checking.</summary>
public class SweepSettingsTests
{
    [Fact]
    public void Defaults_are_the_20_to_150_hz_phase1_case()
    {
        var settings = new SweepSettings();
        Assert.Equal(20.0, settings.StartHz);
        Assert.Equal(150.0, settings.EndHz);
        Assert.Equal(1.0, settings.DurationSeconds);
        Assert.Equal(48000.0, settings.SampleRate);
        Assert.Equal(48000, settings.SampleCount); // round(T·fs) = round(1.0 · 48000)

        settings.Validate(); // must not throw
    }

    [Fact]
    public void Rejects_end_below_or_equal_to_start()
    {
        // f2 == f1 and f2 < f1 are both invalid: the sweep would have no frequency span
        Assert.Throws<ArgumentOutOfRangeException>(() => new SweepSettings(EndHz: 20.0).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new SweepSettings(EndHz: 10.0).Validate());
        // f1 = 0 is invalid too (ln(f2/f1) undefined)
        Assert.Throws<ArgumentOutOfRangeException>(() => new SweepSettings(StartHz: 0.0).Validate());
    }

    [Fact]
    public void Rejects_non_positive_duration_or_sample_rate()
    {
        // T <= 0 would make t/T undefined; fs <= 0 would make t = n/fs undefined
        Assert.Throws<ArgumentOutOfRangeException>(() => new SweepSettings(DurationSeconds: 0.0).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new SweepSettings(DurationSeconds: -0.5).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new SweepSettings(SampleRate: 0.0).Validate());
    }
}
