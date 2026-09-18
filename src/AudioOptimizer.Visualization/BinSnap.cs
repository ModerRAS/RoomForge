namespace AudioOptimizer.Visualization;

/// <summary>
/// Snapping a requested analysis frequency onto a real measurement bin.
/// <para>
/// A frequency ladder is a set of <b>requests</b>; the measurement only has bins. Asking for 25 Hz where the bins are
/// 24.5352 and 24.9023 Hz and then labelling the axis "25 Hz" claims a precision the measurement does not have, so the
/// label must echo the frequency actually used. The spacing is not a constant — it is <c>fs/FFT</c>, so a different
/// sweep duration, sample rate or analysis crop changes it — which is why every rule here is expressed against the
/// bin list the analysis reports and nothing is hardcoded.
/// </para>
/// </summary>
public static class BinSnap
{
    /// <summary>
    /// The bin nearest <paramref name="requestedHz"/>, clamped into the band.
    /// <para>
    /// Rule, in full: nearest bin by absolute distance; <b>on an exact tie the lower (earlier) bin wins</b>, because
    /// the tie means two bins are equally close and taking the earlier one is the conventional, stable choice; a
    /// request outside the band clamps to the first or last real bin rather than snapping outside it.
    /// </para>
    /// </summary>
    public static (int Index, double AchievedHz) Snap(IReadOnlyList<double> binFrequencies, double requestedHz)
    {
        ArgumentNullException.ThrowIfNull(binFrequencies);
        if (binFrequencies.Count == 0) throw new ArgumentException("Snapping needs at least one real bin.", nameof(binFrequencies));
        if (!double.IsFinite(requestedHz)) throw new ArgumentOutOfRangeException(nameof(requestedHz), requestedHz, "The requested frequency must be finite.");
        for (int i = 1; i < binFrequencies.Count; i++)
            if (binFrequencies[i] <= binFrequencies[i - 1])
                throw new ArgumentException($"The bin frequencies must be strictly ascending; index {i} is {binFrequencies[i]} after {binFrequencies[i - 1]}.", nameof(binFrequencies));

        if (requestedHz <= binFrequencies[0]) return (0, binFrequencies[0]);
        int last = binFrequencies.Count - 1;
        if (requestedHz >= binFrequencies[last]) return (last, binFrequencies[last]);

        int best = 0;
        double bestDistance = double.MaxValue;
        for (int i = 0; i < binFrequencies.Count; i++)
        {
            double distance = Math.Abs(binFrequencies[i] - requestedHz);
            // Strictly closer only: an equal distance keeps the earlier bin, which is the stated tie rule.
            if (distance >= bestDistance) continue;
            best = i;
            bestDistance = distance;
        }

        return (best, binFrequencies[best]);
    }

    /// <summary>
    /// The requested frequencies to offer inside the analysis band, stepping by <paramref name="stepHz"/> and derived
    /// from the band's own edges — the ladder is a property of the measurement, not a hardcoded 20…120 Hz.
    /// </summary>
    public static IReadOnlyList<double> Ladder(IReadOnlyList<double> binFrequencies, double stepHz = 5.0)
    {
        ArgumentNullException.ThrowIfNull(binFrequencies);
        if (binFrequencies.Count == 0) throw new ArgumentException("A ladder needs a real band.", nameof(binFrequencies));
        if (!double.IsFinite(stepHz) || stepHz <= 0) throw new ArgumentOutOfRangeException(nameof(stepHz), stepHz, "The step must be finite and > 0.");

        double first = binFrequencies[0];
        double last = binFrequencies[binFrequencies.Count - 1];
        var ladder = new List<double>();
        for (double request = Math.Ceiling(first / stepHz) * stepHz; request <= last && ladder.Count < 10_000; request += stepHz)
            ladder.Add(request);
        return ladder.Count == 0 ? [first] : ladder;
    }
}
