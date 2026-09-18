namespace AudioOptimizer.Simulation;

/// <summary>
/// Where one arrival is written into the impulse response. v1 defaults to <see cref="LinearInterpolated"/> — a
/// fractional delay, so a path of 4.37 ms lands at 209.8 samples instead of being rounded to 210 — and
/// <see cref="NearestSample"/> exists as the integer-delay placement so the difference between the two is a
/// measurable, tested quantity rather than a claim. Nothing downstream of <see cref="ShoeboxRir"/> assumes either.
/// </summary>
public enum ArrivalPlacement
{
    /// <summary>Rounds each arrival to the nearest sample: the v1-acceptable integer delay.</summary>
    NearestSample,

    /// <summary>Splits each arrival across the two neighbouring samples (linear interpolation of the delay).</summary>
    LinearInterpolated,
}

/// <summary>
/// Renders a set of arrivals as a sampled impulse response at a given sample rate.
/// <para>
/// The buffer is <c>round(maxDelay·fs) + 1 + PlacementSlackSamples</c> samples long. The slack only covers the
/// fractional-placement split; a subwoofer's phase setting is NOT baked in here — it is a property of the drive
/// signal and lives in <see cref="PhaseRotation" />, which explains why an impulse train cannot carry it.
/// </para>
/// </summary>
public static class ShoeboxRir
{
    /// <summary>Samples kept after the last arrival so a fractional arrival has both of its neighbouring samples.</summary>
    public const int PlacementSlackSamples = 2;

    /// <summary>
    /// <paramref name="arrivals"/> rendered at <paramref name="sampleRate"/> Hz. Empty input gives an empty response
    /// (a receiver with no path to the source is silence, not an exception).
    /// </summary>
    public static double[] Render(
        IReadOnlyList<Arrival> arrivals,
        int sampleRate,
        ArrivalPlacement placement = ArrivalPlacement.LinearInterpolated)
    {
        ArgumentNullException.ThrowIfNull(arrivals);
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "Sample rate must be > 0.");
        if (arrivals.Count == 0) return [];

        double maxDelay = 0.0;
        foreach (Arrival arrival in arrivals) maxDelay = Math.Max(maxDelay, arrival.DelaySeconds);

        int length = (int)Math.Ceiling(maxDelay * sampleRate) + 1 + PlacementSlackSamples;
        var response = new double[length];
        foreach (Arrival arrival in arrivals) Place(response, arrival, sampleRate, placement);

        return response;
    }

    private static void Place(double[] response, Arrival arrival, int sampleRate, ArrivalPlacement placement)
    {
        double position = arrival.DelaySeconds * sampleRate;

        if (placement == ArrivalPlacement.NearestSample)
        {
            int index = (int)Math.Round(position, MidpointRounding.AwayFromZero);
            if (index >= 0 && index < response.Length) response[index] += arrival.Amplitude;
            return;
        }

        int lower = (int)Math.Floor(position);
        double fraction = position - lower;
        if (lower >= 0 && lower < response.Length) response[lower] += arrival.Amplitude * (1.0 - fraction);
        if (lower + 1 >= 0 && lower + 1 < response.Length) response[lower + 1] += arrival.Amplitude * fraction;
    }
}
