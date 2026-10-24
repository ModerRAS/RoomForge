namespace AudioOptimizer.Dsp;

/// <summary>
/// Multi-tone stimulus generator: a coherent sum of sines with a shared zero phase.
/// Dual-tone IMD is the two-tone special case — <see cref="DualToneGenerator"/>
/// delegates here so there is exactly one synthesis path.
/// </summary>
public static class MultiToneGenerator
{
    /// <summary>
    /// Generates <c>s[n] = Σ a[i]·sin(2π·f[i]·n/fs)</c>, zero-phase (all tones start at
    /// sin(0) = 0, so the waveform is deterministic and reproducible), with an optional
    /// raised-cosine fade in/out and a hard peak cap.
    /// </summary>
    /// <param name="frequencies">Tone frequencies in Hz; each must be in (0, fs/2).</param>
    /// <param name="amplitudes">Peak amplitude per tone (1.0 = digital full scale); same length as frequencies.</param>
    /// <param name="sampleRate">Sample rate in Hz.</param>
    /// <param name="durationSeconds">Duration in seconds; length = round(duration·fs).</param>
    /// <param name="fadeSeconds">Raised-cosine fade at each end (0 = none).</param>
    /// <param name="maxPeak">Hard cap on |sample|; the buffer is scaled down if it exceeds this. Must be in (0, 1].</param>
    public static double[] Generate(
        IReadOnlyList<double> frequencies,
        IReadOnlyList<double> amplitudes,
        double sampleRate,
        double durationSeconds,
        double fadeSeconds = 0.005,
        double maxPeak = 1.0)
    {
        ArgumentNullException.ThrowIfNull(frequencies);
        ArgumentNullException.ThrowIfNull(amplitudes);
        Validate(frequencies, amplitudes, sampleRate, durationSeconds, fadeSeconds, maxPeak);

        int n = (int)Math.Round(durationSeconds * sampleRate);
        var samples = new double[n];
        for (int i = 0; i < n; i++)
        {
            double t = i / sampleRate;
            double s = 0.0;
            for (int k = 0; k < frequencies.Count; k++)
                s += amplitudes[k] * Math.Sin(2.0 * Math.PI * frequencies[k] * t);
            samples[i] = s;
        }

        ApplyFade(samples, sampleRate, fadeSeconds);
        LimitPeak(samples, maxPeak);
        return samples;
    }

    /// <summary>
    /// Equal-amplitude convenience overload: every tone gets <paramref name="amplitudePerTone"/>.
    /// </summary>
    public static double[] Generate(
        IReadOnlyList<double> frequencies,
        double amplitudePerTone,
        double sampleRate,
        double durationSeconds,
        double fadeSeconds = 0.005,
        double maxPeak = 1.0)
    {
        ArgumentNullException.ThrowIfNull(frequencies);
        var amplitudes = new double[frequencies.Count];
        Array.Fill(amplitudes, amplitudePerTone);
        return Generate(frequencies, amplitudes, sampleRate, durationSeconds, fadeSeconds, maxPeak);
    }

    internal static void Validate(
        IReadOnlyList<double> frequencies,
        IReadOnlyList<double> amplitudes,
        double sampleRate,
        double durationSeconds,
        double fadeSeconds,
        double maxPeak)
    {
        if (frequencies.Count == 0) throw new ArgumentException("At least one tone is required.", nameof(frequencies));
        if (frequencies.Count != amplitudes.Count)
            throw new ArgumentException("Frequencies and amplitudes must have the same length.", nameof(amplitudes));
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "SampleRate must be > 0.");
        if (durationSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(durationSeconds), durationSeconds, "Duration must be > 0.");
        if ((int)Math.Round(durationSeconds * sampleRate) <= 0)
            throw new ArgumentOutOfRangeException(nameof(durationSeconds), durationSeconds, "Duration is too short for this sample rate.");
        if (fadeSeconds < 0) throw new ArgumentOutOfRangeException(nameof(fadeSeconds), fadeSeconds, "Fade must be >= 0.");
        if (maxPeak <= 0 || maxPeak > 1.0 || !double.IsFinite(maxPeak))
            throw new ArgumentOutOfRangeException(nameof(maxPeak), maxPeak, "MaxPeak must be in (0, 1].");

        double nyquist = sampleRate / 2.0;
        for (int k = 0; k < frequencies.Count; k++)
        {
            if (!double.IsFinite(frequencies[k]) || frequencies[k] <= 0 || frequencies[k] >= nyquist)
                throw new ArgumentOutOfRangeException(nameof(frequencies), frequencies[k], $"Tone {k} must be in (0, Nyquist).");
            if (!double.IsFinite(amplitudes[k]) || amplitudes[k] < 0)
                throw new ArgumentOutOfRangeException(nameof(amplitudes), amplitudes[k], $"Amplitude {k} must be finite and >= 0.");
        }
    }

    internal static void ApplyFade(double[] samples, double sampleRate, double fadeSeconds)
    {
        int m = (int)Math.Round(fadeSeconds * sampleRate);
        if (m <= 0) return;
        if (2 * m >= samples.Length) m = samples.Length / 2;
        for (int i = 0; i < m; i++)
        {
            double w = 0.5 - 0.5 * Math.Cos(Math.PI * i / m);
            samples[i] *= w;
            samples[samples.Length - 1 - i] *= w;
        }
    }

    internal static void LimitPeak(double[] samples, double maxPeak)
    {
        double peak = 0.0;
        foreach (double s in samples)
        {
            double a = Math.Abs(s);
            if (a > peak) peak = a;
        }
        if (peak > maxPeak && peak > 0.0)
        {
            double gain = maxPeak / peak;
            for (int i = 0; i < samples.Length; i++) samples[i] *= gain;
        }
    }
}
