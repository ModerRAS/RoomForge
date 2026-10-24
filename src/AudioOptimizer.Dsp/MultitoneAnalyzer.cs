namespace AudioOptimizer.Dsp;

/// <summary>One stimulus tone and its measured level.</summary>
/// <param name="FrequencyHz">Nominal stimulus frequency.</param>
/// <param name="MeasuredHz">Interpolated spectral-peak frequency.</param>
/// <param name="LevelDbFs">Measured peak level, 0 dBFS = peak 1.0.</param>
public sealed record MultitoneToneLevel(double FrequencyHz, double MeasuredHz, double LevelDbFs);

/// <summary>One non-tone spectral peak (harmonic, intermod product, or noise).</summary>
/// <param name="FrequencyHz">Interpolated peak frequency.</param>
/// <param name="LevelDbFs">Peak level, 0 dBFS = peak 1.0.</param>
/// <param name="LevelDbC">Level relative to the strongest measured tone (dBc).</param>
public sealed record MultitoneClutterPeak(double FrequencyHz, double LevelDbFs, double LevelDbC);

/// <summary>Multi-tone analysis result: per-tone levels plus the strongest non-tone peaks.</summary>
/// <param name="Tones">Per-tone measurements in stimulus order.</param>
/// <param name="StrongestClutter">Strongest non-tone peaks, descending by level.</param>
/// <param name="WorstClutterDbC">Strongest clutter peak vs strongest tone (dBc); −∞ when none found.</param>
public sealed record MultitoneResult(
    IReadOnlyList<MultitoneToneLevel> Tones,
    IReadOnlyList<MultitoneClutterPeak> StrongestClutter,
    double WorstClutterDbC);

/// <summary>
/// Offline multi-tone analysis: measures each stimulus tone and lists the strongest
/// spectral peaks OUTSIDE the exclusion bands around the tones. Clutter here honestly
/// means "strongest non-tone peaks" (harmonics + intermod products + noise peaks) —
/// not a total residual-energy integral.
/// </summary>
public static class MultitoneAnalyzer
{
    /// <param name="capture">Captured (or synthetic) buffer.</param>
    /// <param name="sampleRate">Sample rate in Hz.</param>
    /// <param name="toneFrequencies">Stimulus tone frequencies, each in (0, fs/2).</param>
    /// <param name="searchWidthHz">Peak-search half-width around each tone.</param>
    /// <param name="exclusionWidthHz">Bins within this distance of any tone are tone bins, never clutter.</param>
    /// <param name="topPeaks">How many strongest clutter peaks to report.</param>
    public static MultitoneResult Analyze(
        double[] capture,
        double sampleRate,
        IReadOnlyList<double> toneFrequencies,
        double searchWidthHz = 10.0,
        double exclusionWidthHz = 20.0,
        int topPeaks = 5)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(toneFrequencies);
        if (capture.Length < 64) throw new ArgumentException("Capture is too short for multi-tone analysis.", nameof(capture));
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "SampleRate must be > 0.");
        if (toneFrequencies.Count == 0) throw new ArgumentException("At least one tone is required.", nameof(toneFrequencies));
        if (searchWidthHz <= 0) throw new ArgumentOutOfRangeException(nameof(searchWidthHz), searchWidthHz, "Search width must be > 0.");
        if (exclusionWidthHz <= 0) throw new ArgumentOutOfRangeException(nameof(exclusionWidthHz), exclusionWidthHz, "Exclusion width must be > 0.");
        if (topPeaks <= 0) throw new ArgumentOutOfRangeException(nameof(topPeaks), topPeaks, "TopPeaks must be > 0.");

        double nyquist = sampleRate / 2.0;
        for (int i = 0; i < toneFrequencies.Count; i++)
            if (!double.IsFinite(toneFrequencies[i]) || toneFrequencies[i] <= 0 || toneFrequencies[i] >= nyquist)
                throw new ArgumentOutOfRangeException(nameof(toneFrequencies), toneFrequencies[i], $"Tone {i} must be in (0, Nyquist).");

        double[] mags = SpectrumPeak.HannMagnitudes(capture, sampleRate, out double binHz);
        int n = (mags.Length - 1) * 2;

        var tones = new List<MultitoneToneLevel>(toneFrequencies.Count);
        foreach (double f in toneFrequencies)
        {
            var (measuredHz, amplitude) = SpectrumPeak.MeasureAt(mags, binHz, f, searchWidthHz);
            tones.Add(new MultitoneToneLevel(f, measuredHz, SpectrumPeak.ToDbFs(amplitude)));
        }
        double referenceDb = tones.Max(t => t.LevelDbFs);

        bool IsToneBin(int k)
        {
            double f = k * binHz;
            foreach (double t in toneFrequencies)
                if (Math.Abs(f - t) <= exclusionWidthHz) return true;
            return false;
        }

        var clutter = new List<MultitoneClutterPeak>();
        for (int k = 1; k < mags.Length - 1; k++)
        {
            if (IsToneBin(k)) continue;
            if (!(mags[k] > mags[k - 1] && mags[k] >= mags[k + 1])) continue; // local maximum only
            double delta = 0.0;
            double denom = mags[k - 1] - 2.0 * mags[k] + mags[k + 1];
            if (denom != 0.0) delta = Math.Clamp(0.5 * (mags[k - 1] - mags[k + 1]) / denom, -0.5, 0.5);
            double refined = mags[k] - 0.25 * (mags[k - 1] - mags[k + 1]) * delta;
            double amplitude = refined * 4.0 / n;
            double level = SpectrumPeak.ToDbFs(amplitude);
            if (double.IsNegativeInfinity(level)) continue;
            clutter.Add(new MultitoneClutterPeak((k + delta) * binHz, level, level - referenceDb));
        }

        clutter.Sort((a, b) => b.LevelDbFs.CompareTo(a.LevelDbFs));
        var top = clutter.Take(topPeaks).ToList();
        double worst = top.Count > 0 ? top[0].LevelDbC : double.NegativeInfinity;
        return new MultitoneResult(tones, top, worst);
    }
}
