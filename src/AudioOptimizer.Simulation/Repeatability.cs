namespace AudioOptimizer.Simulation;

using AudioOptimizer.Core;
using AudioOptimizer.Dsp;
using AudioOptimizer.Measurement;

/// <summary>How repeated captures of one point compare against the first capture: magnitude in dB, phase in degrees.</summary>
public sealed record RepeatabilityStats(
    int Repeats,
    double MeanMagnitudeErrorDb,
    double MaxMagnitudeErrorDb,
    double P90MagnitudeErrorDb,
    double MeanPhaseErrorDegrees,
    double MaxPhaseErrorDegrees);

/// <summary>
/// The measurement's own noise floor expressed as a repeatability number: the same point and the same geometry are
/// captured <c>repeats</c> times through the shipped chain (<see cref="VirtualLab"/> → <c>PointMeasurement.Run</c>),
/// each with a different noise seed, and every in-band complex response is compared against the first capture. No
/// statistics framework, no ground truth — the difference between two captures is exactly the noise.
/// </summary>
public static class Repeatability
{
    /// <summary>
    /// Measures one point <paramref name="repeats"/> times on sub A (always measurable on a one- or two-sub rig) and
    /// returns the magnitude and phase errors of every later capture against the first.
    /// </summary>
    public static RepeatabilityStats Measure(
        SimulationConfig config,
        IReadOnlyList<VirtualSubwoofer> subs,
        MeasurementPoint point,
        int repeats = 8)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(subs);
        ArgumentNullException.ThrowIfNull(point);
        if (repeats < 2)
            throw new ArgumentOutOfRangeException(nameof(repeats), repeats, "Repeatability needs at least two captures.");

        FrequencyBand band = FrequencyBand.Of(config.Sweep);
        FrequencyResponse[]? first = null;
        var magnitudeErrors = new List<double>();
        var phaseErrors = new List<double>();

        for (int run = 0; run < repeats; run++)
        {
            var lab = new VirtualLab(config with { NoiseSeed = config.NoiseSeed + run }, subs, [point]);
            FrequencyResponse[] bins = MeasurementSession.InBand(lab.Measure(SubMode.A, point).Result.Response, band);
            if (first is null)
            {
                first = bins;
                continue;
            }

            for (int k = 0; k < first.Length; k++)
            {
                double reference = Math.Sqrt((first[k].Real * first[k].Real) + (first[k].Imag * first[k].Imag));
                double measured = Math.Sqrt((bins[k].Real * bins[k].Real) + (bins[k].Imag * bins[k].Imag));
                magnitudeErrors.Add(Math.Abs(ComplexMath.LinearToDb(measured / reference)));
                phaseErrors.Add(Math.Abs(Math.IEEERemainder(bins[k].PhaseWrappedRad - first[k].PhaseWrappedRad, Math.Tau))
                    * 180.0 / Math.PI);
            }
        }

        magnitudeErrors.Sort();
        phaseErrors.Sort();
        return new RepeatabilityStats(
            repeats,
            magnitudeErrors.Average(),
            magnitudeErrors[^1],
            Percentile(magnitudeErrors, 0.90),
            phaseErrors.Average(),
            phaseErrors[^1]);
    }

    /// <summary>Nearest-rank percentile of a sorted list: the smallest value with at least <paramref name="p"/> below it.</summary>
    private static double Percentile(List<double> sorted, double p)
        => sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(p * sorted.Count) - 1)];
}
