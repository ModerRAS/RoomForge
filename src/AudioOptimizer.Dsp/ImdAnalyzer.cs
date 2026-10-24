namespace AudioOptimizer.Dsp;

/// <summary>One intermodulation product: where it should be and how large it is.</summary>
/// <param name="Label">Product name, e.g. "f2-f1" or "2f1+f2".</param>
/// <param name="FrequencyHz">Expected product frequency in Hz.</param>
/// <param name="LevelDbFs">Measured peak level, 0 dBFS = peak 1.0.</param>
/// <param name="LevelDbC">Level relative to the stronger fundamental (dBc).</param>
public sealed record ImdProduct(string Label, double FrequencyHz, double LevelDbFs, double LevelDbC);

/// <summary>Dual-tone IMD measurement result (offline analysis of a captured buffer).</summary>
/// <param name="F1Hz">First stimulus frequency.</param>
/// <param name="F2Hz">Second stimulus frequency.</param>
/// <param name="F1LevelDbFs">Measured fundamental 1 level, dBFS (peak 1.0 = 0).</param>
/// <param name="F2LevelDbFs">Measured fundamental 2 level, dBFS.</param>
/// <param name="SecondOrder">2nd-order products: |f1−f2|, f1+f2 (in-Nyquist only).</param>
/// <param name="ThirdOrder">3rd-order products: 2f1−f2, 2f2−f1, 2f1+f2, 2f2+f1 (in-Nyquist only).</param>
/// <param name="ImdRatioDb">
/// RSS IMD ratio: 20·log10( √(ΣP²) / √(ΣF²) ), P = linear peak amplitudes of all
/// reported in-band products, F = linear peak amplitudes of the two fundamentals.
/// Identical to an RMS ratio (the 1/√2 factors cancel); −∞ when no product is measurable.
/// </param>
/// <param name="ImdPercent">100 · 10^(ImdRatioDb/20); 0 when no product is measurable.</param>
public sealed record ImdResult(
    double F1Hz,
    double F2Hz,
    double F1LevelDbFs,
    double F2LevelDbFs,
    IReadOnlyList<ImdProduct> SecondOrder,
    IReadOnlyList<ImdProduct> ThirdOrder,
    double ImdRatioDb,
    double ImdPercent);

/// <summary>
/// Offline dual-tone intermodulation analysis. Given a captured buffer and the two
/// stimulus frequencies, measures the fundamentals and the 2nd/3rd-order products
/// with one Hann-windowed FFT and a local peak search per target.
/// </summary>
public static class ImdAnalyzer
{
    /// <summary>
    /// Measures fundamentals + 2nd/3rd-order products. Products at ≤ 0 Hz or at/above
    /// Nyquist are skipped (they cannot exist in the sampled capture) and simply absent
    /// from the result lists.
    /// </summary>
    /// <param name="capture">Captured (or synthetic) buffer.</param>
    /// <param name="sampleRate">Sample rate in Hz.</param>
    /// <param name="f1Hz">First stimulus frequency, in (0, fs/2).</param>
    /// <param name="f2Hz">Second stimulus frequency, in (0, fs/2).</param>
    /// <param name="searchWidthHz">Peak-search half-width around each target.</param>
    public static ImdResult Analyze(
        double[] capture,
        double sampleRate,
        double f1Hz,
        double f2Hz,
        double searchWidthHz = 10.0)
    {
        ArgumentNullException.ThrowIfNull(capture);
        if (capture.Length < 64) throw new ArgumentException("Capture is too short for IMD analysis.", nameof(capture));
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "SampleRate must be > 0.");
        double nyquist = sampleRate / 2.0;
        if (!double.IsFinite(f1Hz) || f1Hz <= 0 || f1Hz >= nyquist)
            throw new ArgumentOutOfRangeException(nameof(f1Hz), f1Hz, "f1 must be in (0, Nyquist).");
        if (!double.IsFinite(f2Hz) || f2Hz <= 0 || f2Hz >= nyquist)
            throw new ArgumentOutOfRangeException(nameof(f2Hz), f2Hz, "f2 must be in (0, Nyquist).");
        if (searchWidthHz <= 0) throw new ArgumentOutOfRangeException(nameof(searchWidthHz), searchWidthHz, "Search width must be > 0.");

        double[] mags = SpectrumPeak.HannMagnitudes(capture, sampleRate, out double binHz);
        double a1 = SpectrumPeak.AmplitudeAt(mags, binHz, f1Hz, searchWidthHz);
        double a2 = SpectrumPeak.AmplitudeAt(mags, binHz, f2Hz, searchWidthHz);
        double f1Db = SpectrumPeak.ToDbFs(a1);
        double f2Db = SpectrumPeak.ToDbFs(a2);
        double referenceDb = Math.Max(f1Db, f2Db);

        var second = new List<ImdProduct?>
        {
            MaybeProduct(mags, binHz, nyquist, "f2-f1", Math.Abs(f2Hz - f1Hz), searchWidthHz, referenceDb),
            MaybeProduct(mags, binHz, nyquist, "f1+f2", f1Hz + f2Hz, searchWidthHz, referenceDb),
        };
        var third = new List<ImdProduct?>
        {
            MaybeProduct(mags, binHz, nyquist, "2f1-f2", 2 * f1Hz - f2Hz, searchWidthHz, referenceDb),
            MaybeProduct(mags, binHz, nyquist, "2f2-f1", 2 * f2Hz - f1Hz, searchWidthHz, referenceDb),
            MaybeProduct(mags, binHz, nyquist, "2f1+f2", 2 * f1Hz + f2Hz, searchWidthHz, referenceDb),
            MaybeProduct(mags, binHz, nyquist, "2f2+f1", 2 * f2Hz + f1Hz, searchWidthHz, referenceDb),
        };
        second.RemoveAll(p => p is null);
        third.RemoveAll(p => p is null);

        double fundSquares = a1 * a1 + a2 * a2;
        double productSquares = 0.0;
        foreach (var p in Enumerable.Concat(second, third))
            productSquares += Math.Pow(Math.Pow(10.0, p!.LevelDbFs / 20.0), 2);

        double ratioDb = productSquares <= 0.0 ? double.NegativeInfinity
            : fundSquares <= 0.0 ? double.NaN
            : 10.0 * Math.Log10(productSquares / fundSquares);
        double percent = double.IsNegativeInfinity(ratioDb) ? 0.0
            : double.IsNaN(ratioDb) ? double.NaN
            : 100.0 * Math.Pow(10.0, ratioDb / 20.0);

        return new ImdResult(f1Hz, f2Hz, f1Db, f2Db, second!, third!, ratioDb, percent);
    }

    private static ImdProduct? MaybeProduct(
        double[] mags, double binHz, double nyquist,
        string label, double frequencyHz, double searchWidthHz, double referenceDb)
    {
        if (!double.IsFinite(frequencyHz) || frequencyHz < binHz / 2.0 || frequencyHz >= nyquist)
            return null; // outside the representable band — not measurable, not a failure
        double level = SpectrumPeak.ToDbFs(SpectrumPeak.AmplitudeAt(mags, binHz, frequencyHz, searchWidthHz));
        return new ImdProduct(label, frequencyHz, level, level - referenceDb);
    }
}
