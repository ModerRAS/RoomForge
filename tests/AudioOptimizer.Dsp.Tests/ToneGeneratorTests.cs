namespace AudioOptimizer.Dsp.Tests;

/// <summary>Tone stimulus generators: peak cap, tone placement, fade, validation.</summary>
public class ToneGeneratorTests
{
    private const double SampleRate = 48000.0;
    private const double Duration = 1.0; // N = 48000, bin = 1 Hz → integer-Hz tones are coherent

    private static double Peak(double[] s)
    {
        double peak = 0.0;
        foreach (double v in s) peak = Math.Max(peak, Math.Abs(v));
        return peak;
    }

    [Fact]
    public void DualTone_peak_never_exceeds_cap()
    {
        // 0.5 + 0.5 sines align at t = 0 → raw peak 1.0; cap 0.9 must scale down.
        double[] s = DualToneGenerator.Generate(1000, 1500, 0.5, 0.5, SampleRate, Duration, fadeSeconds: 0, maxPeak: 0.9);
        Assert.Equal(48000, s.Length);
        Assert.True(Peak(s) <= 0.9 + 1e-12, $"peak was {Peak(s):R}");
    }

    [Fact]
    public void MultiTone_clips_to_full_scale_with_many_tones()
    {
        double[] freqs = [100, 300, 700, 1200, 2500, 4000, 6000, 9000];
        double[] s = MultiToneGenerator.Generate(freqs, 0.2, SampleRate, Duration, fadeSeconds: 0);
        Assert.True(Peak(s) <= 1.0 + 1e-12, $"peak was {Peak(s):R}");
    }

    [Fact]
    public void MultiTone_equal_amplitude_overload_matches_explicit_amplitudes()
    {
        double[] freqs = [500, 1200, 2500];
        double[] a = MultiToneGenerator.Generate(freqs, 0.25, SampleRate, Duration, fadeSeconds: 0);
        double[] b = MultiToneGenerator.Generate(freqs, [0.25, 0.25, 0.25], SampleRate, Duration, fadeSeconds: 0);
        Assert.Equal(a, b);
    }

    [Fact]
    public void DualTone_delegates_to_multitone_path()
    {
        double[] a = DualToneGenerator.Generate(1000, 1500, 0.4, 0.3, SampleRate, Duration, fadeSeconds: 0);
        double[] b = MultiToneGenerator.Generate([1000.0, 1500.0], [0.4, 0.3], SampleRate, Duration, fadeSeconds: 0);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Coherent_tones_land_on_their_bins_at_expected_level()
    {
        double[] s = DualToneGenerator.Generate(1000, 1500, 0.4, 0.4, SampleRate, Duration, fadeSeconds: 0);
        ImdResult r = ImdAnalyzer.Analyze(s, SampleRate, 1000, 1500);
        // 20·log10(0.4) = −7.9588 dBFS; Hann + interpolation reads back within 0.5 dB.
        Assert.Equal(-7.9588, r.F1LevelDbFs, 0.5);
        Assert.Equal(-7.9588, r.F2LevelDbFs, 0.5);
    }

    [Fact]
    public void Fade_starts_and_ends_at_zero_without_changing_length()
    {
        double[] s = DualToneGenerator.Generate(1000, 1500, 0.4, 0.4, SampleRate, Duration);
        Assert.Equal(48000, s.Length);
        Assert.True(Math.Abs(s[0]) < 1e-12, $"first sample was {s[0]:E3}");
        Assert.True(Math.Abs(s[^1]) < 1e-12, $"last sample was {s[^1]:E3}");
        Assert.True(Peak(s) <= 1.0 + 1e-12, $"peak was {Peak(s):R}");
    }

    [Fact]
    public void Generator_rejects_out_of_band_frequencies()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DualToneGenerator.Generate(0, 1500, 0.4, 0.4, SampleRate, Duration));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DualToneGenerator.Generate(1000, SampleRate / 2.0, 0.4, 0.4, SampleRate, Duration));
        Assert.Throws<ArgumentException>(() =>
            MultiToneGenerator.Generate([], [], SampleRate, Duration));
        Assert.Throws<ArgumentException>(() =>
            MultiToneGenerator.Generate([100.0], [0.1, 0.2], SampleRate, Duration));
    }
}
