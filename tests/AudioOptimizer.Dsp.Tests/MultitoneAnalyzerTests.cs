namespace AudioOptimizer.Dsp.Tests;

using Xunit.Abstractions;

/// <summary>Multi-tone analysis: tone levels, peak cap, and clutter detection under nonlinearity.</summary>
public class MultitoneAnalyzerTests(ITestOutputHelper output)
{
    private const double SampleRate = 48000.0;
    private const double Duration = 1.0; // 1 Hz bins
    private static readonly double[] Tones = [500, 1200, 2500, 6000];

    [Fact]
    public void Generated_multitone_peak_never_exceeds_full_scale()
    {
        double[] s = MultiToneGenerator.Generate(Tones, 0.25, SampleRate, Duration, fadeSeconds: 0);
        double peak = 0.0;
        foreach (double v in s) peak = Math.Max(peak, Math.Abs(v));
        Assert.True(peak <= 1.0 + 1e-12, $"peak was {peak:R}");
    }

    [Fact]
    public void Tone_frequencies_land_within_one_bin_at_expected_level()
    {
        // 0.25 peak per tone → 20·log10(0.25) = −12.04 dBFS.
        double[] s = MultiToneGenerator.Generate(Tones, 0.25, SampleRate, Duration, fadeSeconds: 0);
        MultitoneResult r = MultitoneAnalyzer.Analyze(s, SampleRate, Tones);
        Assert.Equal(Tones.Length, r.Tones.Count);
        for (int i = 0; i < Tones.Length; i++)
        {
            output.WriteLine($"tone {Tones[i]} Hz → measured {r.Tones[i].MeasuredHz:F2} Hz @ {r.Tones[i].LevelDbFs:F2} dBFS");
            Assert.True(Math.Abs(r.Tones[i].MeasuredHz - Tones[i]) <= 1.0,
                $"tone {Tones[i]} measured at {r.Tones[i].MeasuredHz:F2} Hz");
            Assert.Equal(-12.04, r.Tones[i].LevelDbFs, 1.0);
        }
    }

    [Fact]
    public void Clean_multitone_has_no_strong_clutter()
    {
        double[] s = MultiToneGenerator.Generate(Tones, 0.25, SampleRate, Duration, fadeSeconds: 0);
        MultitoneResult r = MultitoneAnalyzer.Analyze(s, SampleRate, Tones);
        output.WriteLine($"clean worst clutter = {r.WorstClutterDbC:F1} dBc");
        Assert.True(r.WorstClutterDbC < -70.0, $"clean clutter was {r.WorstClutterDbC:F1} dBc");
    }

    [Fact]
    public void Cubic_nonlinearity_shows_up_as_non_tone_clutter()
    {
        double[] s = MultiToneGenerator.Generate(Tones, 0.25, SampleRate, Duration, fadeSeconds: 0);
        var y = new double[s.Length];
        for (int i = 0; i < s.Length; i++) y[i] = s[i] + 0.5 * s[i] * s[i] * s[i];
        MultitoneResult r = MultitoneAnalyzer.Analyze(s, SampleRate, Tones);
        MultitoneResult d = MultitoneAnalyzer.Analyze(y, SampleRate, Tones);
        output.WriteLine($"clean {r.WorstClutterDbC:F1} dBc → distorted {d.WorstClutterDbC:F1} dBc");
        foreach (var c in d.StrongestClutter)
            output.WriteLine($"  clutter {c.FrequencyHz:F1} Hz @ {c.LevelDbFs:F1} dBFS ({c.LevelDbC:F1} dBc)");
        Assert.True(d.WorstClutterDbC > r.WorstClutterDbC + 20.0,
            $"distortion should raise clutter by > 20 dB (clean {r.WorstClutterDbC:F1}, distorted {d.WorstClutterDbC:F1})");
        // Every reported clutter peak honours the exclusion bands around the tones.
        foreach (var c in d.StrongestClutter)
            foreach (double t in Tones)
                Assert.True(Math.Abs(c.FrequencyHz - t) > 20.0, $"clutter at {c.FrequencyHz:F1} Hz is inside tone {t} Hz band");
    }
}
