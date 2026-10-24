namespace AudioOptimizer.Dsp.Tests;

using Xunit.Abstractions;

/// <summary>Dual-tone IMD analysis: clean floor, quadratic (2nd-order) and cubic (3rd-order) paths.</summary>
public class ImdAnalyzerTests(ITestOutputHelper output)
{
    private const double SampleRate = 48000.0;
    private const double Duration = 1.0; // N = 48000 → 1 Hz bins; f1/f2/products all coherent
    private const double F1 = 1000.0;
    private const double F2 = 1700.0; // 2f1-f2 = 300, f2-f1 = 700: all six products land on distinct bins

    private static double[] Clean(double a = 0.4)
        => DualToneGenerator.Generate(F1, F2, a, a, SampleRate, Duration, fadeSeconds: 0);

    private static double[] Distort(double[] s, Func<double, double> f)
    {
        var y = new double[s.Length];
        for (int i = 0; i < s.Length; i++) y[i] = f(s[i]);
        return y;
    }

    private static ImdProduct ByLabel(ImdResult r, string label)
        => r.SecondOrder.Concat(r.ThirdOrder).Single(p => p.Label == label);

    [Fact]
    public void Clean_dual_tone_products_sit_near_the_noise_floor()
    {
        ImdResult r = ImdAnalyzer.Analyze(Clean(), SampleRate, F1, F2);
        foreach (var p in r.SecondOrder.Concat(r.ThirdOrder))
        {
            output.WriteLine($"{p.Label} @ {p.FrequencyHz} Hz = {p.LevelDbFs:F1} dBFS ({p.LevelDbC:F1} dBc)");
            Assert.True(p.LevelDbFs < -80.0, $"{p.Label} was {p.LevelDbFs:F1} dBFS on a clean stimulus");
        }
        Assert.True(r.ImdRatioDb < -60.0, $"clean IMD ratio was {r.ImdRatioDb:F1} dB");
    }

    [Fact]
    public void Quadratic_nonlinearity_raises_second_order_products_only()
    {
        // y = x + 0.1·x²: cross term 0.016·cos(w1±w2) → −35.9 dBFS (−28 dBc vs −8 dBFS fundamentals).
        double[] y = Distort(Clean(), x => x + 0.1 * x * x);
        ImdResult r = ImdAnalyzer.Analyze(y, SampleRate, F1, F2);

        Assert.Equal(-35.9, ByLabel(r, "f2-f1").LevelDbFs, 3.0); // 500 Hz
        Assert.Equal(-35.9, ByLabel(r, "f1+f2").LevelDbFs, 3.0); // 2500 Hz
        output.WriteLine($"quadratic IMD ratio = {r.ImdRatioDb:F1} dB ({r.ImdPercent:F3} %)");
        Assert.InRange(r.ImdRatioDb, -32.0, -24.0);

        // No cubic term → 3rd-order products stay near the floor.
        foreach (var p in r.ThirdOrder)
            Assert.True(p.LevelDbFs < -70.0, $"{p.Label} was {p.LevelDbFs:F1} dBFS with quadratic-only distortion");
    }

    [Fact]
    public void Cubic_nonlinearity_raises_third_order_products()
    {
        // y = x + 0.3·x³: 2f1−f2 / 2f2−f1 at 3·A³·0.3/4 = 0.0144 → −36.8 dBFS.
        double[] y = Distort(Clean(), x => x + 0.3 * x * x * x);
        ImdResult r = ImdAnalyzer.Analyze(y, SampleRate, F1, F2);

        Assert.Equal(-36.8, ByLabel(r, "2f1-f2").LevelDbFs, 4.0); // 500 Hz
        Assert.Equal(-36.8, ByLabel(r, "2f2-f1").LevelDbFs, 4.0); // 2000 Hz
        Assert.True(r.ImdRatioDb > -60.0, $"cubic IMD ratio was {r.ImdRatioDb:F1} dB, indistinguishable from clean");
    }

    [Fact]
    public void Soft_clipping_raises_imd_well_above_the_clean_floor()
    {
        // tanh drive of 3 on a 0.4-peak dual tone: odd-order harmonics + IMD, no polynomial tuning.
        double[] y = Distort(Clean(), x => Math.Tanh(3.0 * x) / Math.Tanh(1.2));
        y = Distort(y, x => x * 0.4 / PeakOf(y)); // re-peak to 0.4 so fundamentals match clean
        ImdResult clean = ImdAnalyzer.Analyze(Clean(), SampleRate, F1, F2);
        ImdResult clipped = ImdAnalyzer.Analyze(y, SampleRate, F1, F2);
        output.WriteLine($"clean {clean.ImdRatioDb:F1} dB → soft-clipped {clipped.ImdRatioDb:F1} dB");
        Assert.True(clipped.ImdRatioDb > clean.ImdRatioDb + 20.0,
            $"soft clipping should raise IMD by > 20 dB (clean {clean.ImdRatioDb:F1}, clipped {clipped.ImdRatioDb:F1})");
    }

    [Fact]
    public void Products_above_nyquist_are_skipped_not_reported()
    {
        // f1+f2 = 43000 Hz and 2f2+f1 exceed Nyquist (24000) → absent, not failed.
        ImdResult r = ImdAnalyzer.Analyze(
            DualToneGenerator.Generate(20000, 23000, 0.4, 0.4, SampleRate, Duration, fadeSeconds: 0),
            SampleRate, 20000, 23000);
        Assert.DoesNotContain(r.SecondOrder.Concat(r.ThirdOrder), p => p.FrequencyHz >= SampleRate / 2.0);
        Assert.Contains(r.SecondOrder, p => p.Label == "f2-f1"); // 3000 Hz survives
    }

    private static double PeakOf(double[] s)
    {
        double peak = 0.0;
        foreach (double v in s) peak = Math.Max(peak, Math.Abs(v));
        return peak;
    }
}
