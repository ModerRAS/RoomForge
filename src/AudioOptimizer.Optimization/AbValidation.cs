namespace AudioOptimizer.Optimization;

using System.Numerics;
using AudioOptimizer.Core;
using AudioOptimizer.Dsp;

/// <summary>Something the user should check when the predicted and measured dual-sub passes disagree.</summary>
public enum AbCheckKind
{
    /// <summary>The measured A+B looks like B is inverted relative to the model: check polarity.</summary>
    Polarity,

    /// <summary>The whole compared band is offset by a constant level: check gain (and level normalisation).</summary>
    Gain,

    /// <summary>A phase offset that is not a polarity flip: check the phase setting that was actually applied.</summary>
    PhaseSetting,

    /// <summary>The error varies with frequency rather than being constant: check DSP/EQ on one of the paths.</summary>
    DeviceDsp,

    /// <summary>A material disagreement with none of the structured causes above: check measurement sync/timing.</summary>
    MeasurementSync,
}

/// <summary>How well the linear-superposition prediction matched the measured dual-sub pass.</summary>
public enum AbValidationVerdict
{
    /// <summary>Within the thresholds at (almost) every frequency: the model describes this rig.</summary>
    Agrees,

    MagnitudeMismatch,
    PhaseMismatch,
    MagnitudeAndPhaseMismatch,
}

/// <summary>
/// The thresholds, as parameters with printed defaults. 1.0 dB magnitude: comfortably above this rig's own
/// repeatability (a repeated offline pass reproduces to well under 0.01 dB) and below the level difference a
/// listener would call "a different setup". 15° phase: at 50 Hz that is 0.83 ms, less than the sub/main
/// alignment error that is audible as a change in integration, so a larger error is worth reporting rather
/// than explaining away. Both are compared as P90 over all compared frequencies (see
/// <see cref="AbValidation.Compare"/>).
/// </summary>
public sealed record AbValidationOptions(double MagnitudeErrorThresholdDb = 1.0, double PhaseErrorThresholdDegrees = 15.0)
{
    public static AbValidationOptions Default { get; } = new();
}

/// <summary>
/// One predicted-vs-measured row. <see cref="MagnitudeErrorDb"/> is predicted − real, so a positive value means
/// the model promises more level than the room delivered. <see cref="WrappedPhaseErrorDegrees"/> is the
/// difference of the two phases folded into (−180°, 180°], which is why the band summary needs circular
/// statistics rather than a plain average.
/// </summary>
public sealed record AbFrequencyError(
    string PointId,
    double FrequencyHz,
    double PredictedMagnitudeDb,
    double RealMagnitudeDb,
    double MagnitudeErrorDb,
    double PredictedPhaseDegrees,
    double RealPhaseDegrees,
    double WrappedPhaseErrorDegrees,
    bool Degenerate);

/// <summary>
/// The band summary over every compared (position, frequency) pair. Magnitude errors are signed magnitudes and
/// absolute values both — <see cref="MeanMagnitudeErrorDb"/> IS the level offset, which is exactly what the
/// gain check reads. Phase summaries are circular: <see cref="CircularMeanPhaseErrorDegrees"/> is the angle of
/// the mean unit phasor of the wrapped differences, not an arithmetic mean of degrees (which would report ≈0°
/// for a band that is uniformly ±180° out). Percentiles are nearest-rank, via
/// <see cref="SpatialMetrics.PercentileNearestRank"/> — the same definition the spatial metrics use.
/// </summary>
public sealed record AbBandError(
    double MeanMagnitudeErrorDb,
    double MeanAbsoluteMagnitudeErrorDb,
    double MaxAbsoluteMagnitudeErrorDb,
    double P90AbsoluteMagnitudeErrorDb,
    double MagnitudeErrorSpreadDb,
    double CircularMeanPhaseErrorDegrees,
    double MeanAbsolutePhaseErrorDegrees,
    double MaxAbsolutePhaseErrorDegrees,
    double P90AbsolutePhaseErrorDegrees,
    int FrequenciesCompared,
    int DegenerateFrequencies);

/// <summary>
/// The verdict plus the evidence behind it. <see cref="Checks"/> is renderable data for a UI ("check polarity"),
/// not a log line, and it is ordered by how specific the cause is.
/// </summary>
public sealed record AbValidationResult(
    AbValidationVerdict Verdict,
    AbBandError Band,
    IReadOnlyList<AbFrequencyError> Errors,
    IReadOnlyList<AbCheckKind> Checks,
    AbValidationOptions Options)
{
    public bool Agrees => Verdict == AbValidationVerdict.Agrees;
}

/// <summary>
/// §21: does the linear-superposition model describe the rig? Predicts the dual-sub response from the measured
/// H_A and H_B through <see cref="SubwooferModel"/> — the SAME model the optimizer searches with, because a
/// second implementation of one equation is how a prediction and a search silently diverge — then compares it
/// against the measured dual-sub pass and reports the error as data.
/// <para>
/// A disagreement is a finding about the hardware (or about how it was measured), not a bug in this tool: the
/// model assumes A and B add as complex phasors with nothing else in the path. A limiter, an EQ, a crossover,
/// a DSP delay or two passes that were not captured at the same time all break that assumption, and this type's
/// job is to say so with numbers instead of quietly returning a wrong optimum.
/// </para>
/// </summary>
/// <remarks>
/// ponytail: one band summary over every position and frequency, so a rig fault confined to one seat is
/// averaged in with the rest. Per-position summaries are the upgrade if a single-position diagnosis is needed.
/// </remarks>
public static class AbValidation
{
    /// <summary>
    /// Ten decades below full scale: a bin whose real magnitude is below this is a measured null, and its
    /// relative error is meaningless (it would divide by ~0). Such rows are reported with the flag set and left
    /// out of the band statistics, the same way the optimizer skips numerically-zero reference bins.
    /// </summary>
    private const double MinimumMagnitude = 1e-12;

    /// <summary>A phase error beyond this many degrees is read as a polarity flip rather than a phase offset.</summary>
    private const double PolaritySuspectDegrees = 150.0;

    public static AbValidationResult Compare(
        DualSubMeasurement measurement,
        IReadOnlyList<PositionResponse> realAb,
        SubwooferSetting setting,
        AbValidationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(measurement);
        ArgumentNullException.ThrowIfNull(realAb);
        ArgumentNullException.ThrowIfNull(setting);
        measurement.Validate();
        setting.Validate();

        options ??= AbValidationOptions.Default;
        if (options.MagnitudeErrorThresholdDb <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), options.MagnitudeErrorThresholdDb, "The magnitude threshold must be > 0 dB.");
        if (options.PhaseErrorThresholdDegrees is <= 0 or >= 180)
            throw new ArgumentOutOfRangeException(nameof(options), options.PhaseErrorThresholdDegrees, "The phase threshold must be in (0°, 180°).");

        IReadOnlyList<PositionResponse> predicted = SubwooferModel.Combine(measurement, setting);
        RequireSameGrid(predicted, realAb);

        var errors = new List<AbFrequencyError>(predicted.Count * predicted[0].Bins.Count);
        for (int i = 0; i < predicted.Count; i++)
        {
            PositionResponse p = predicted[i];
            PositionResponse r = realAb[i];
            for (int k = 0; k < p.Bins.Count; k++)
            {
                FrequencyResponse pb = p.Bins[k];
                FrequencyResponse rb = r.Bins[k];
                var predictedValue = new Complex(pb.Real, pb.Imag);
                var realValue = new Complex(rb.Real, rb.Imag);
                double predictedMagnitude = ComplexMath.Magnitude(predictedValue);
                double realMagnitude = ComplexMath.Magnitude(realValue);
                bool degenerate = predictedMagnitude < MinimumMagnitude || realMagnitude < MinimumMagnitude;

                // Signed difference of the two levels, floored so a measured null yields a finite number.
                double magnitudeError = ComplexMath.LinearToDb(Math.Max(predictedMagnitude, MinimumMagnitude))
                    - ComplexMath.LinearToDb(Math.Max(realMagnitude, MinimumMagnitude));

                // Wrap the phase difference into (−180°, 180°] through ComplexMath.Phase.
                double delta = ComplexMath.Phase(predictedValue) - ComplexMath.Phase(realValue);
                double wrapped = ComplexMath.Phase(new Complex(Math.Cos(delta), Math.Sin(delta)));

                errors.Add(new AbFrequencyError(
                    p.PointId,
                    pb.FrequencyHz,
                    predictedMagnitude <= 0 ? double.NegativeInfinity : ComplexMath.LinearToDb(predictedMagnitude),
                    realMagnitude <= 0 ? double.NegativeInfinity : ComplexMath.LinearToDb(realMagnitude),
                    magnitudeError,
                    RadiansToDegrees(ComplexMath.Phase(predictedValue)),
                    RadiansToDegrees(ComplexMath.Phase(realValue)),
                    RadiansToDegrees(wrapped),
                    degenerate));
            }
        }

        AbBandError band = Summarize(errors);
        AbValidationVerdict verdict = VerdictFor(band, options);
        return new AbValidationResult(verdict, band, errors, Diagnose(band, verdict, options), options);
    }

    /// <summary>
    /// The prediction and the measurement must describe the same positions on the same frequency grid; pairing
    /// unrelated bins would produce a number nobody can act on, so this throws instead.
    /// </summary>
    private static void RequireSameGrid(IReadOnlyList<PositionResponse> predicted, IReadOnlyList<PositionResponse> realAb)
    {
        if (predicted.Count != realAb.Count)
            throw new ArgumentException(
                $"Predicted {predicted.Count} positions but the measured A+B pass has {realAb.Count}; they must match.", nameof(realAb));

        for (int i = 0; i < predicted.Count; i++)
        {
            // Identity is a different invariant from grid equality, so the PointId check stays local rather than moving
            // into PositionGrid. The grid rule itself is shared: this was a fourth copy of the same loop.
            if (predicted[i].PointId != realAb[i].PointId)
                throw new ArgumentException(
                    $"Position {i} is '{predicted[i].PointId}' in the prediction and '{realAb[i].PointId}' in the measured A+B pass.", nameof(realAb));
            if (predicted[i].Bins.Count != realAb[i].Bins.Count)
                throw new ArgumentException(
                    $"Position '{predicted[i].PointId}' has {predicted[i].Bins.Count} bins predicted and {realAb[i].Bins.Count} measured.", nameof(realAb));

            if (!PositionGrid.SameGrid(predicted[i], realAb[i]))
            {
                int k = PositionGrid.FirstDifferentBin(predicted[i], realAb[i]);
                throw new ArgumentException(
                    $"Position '{predicted[i].PointId}' bin {k} is {predicted[i].Bins[k].FrequencyHz} Hz predicted and {realAb[i].Bins[k].FrequencyHz} Hz measured.",
                    nameof(realAb));
            }
        }
    }

    /// <summary>
    /// Band statistics over the non-degenerate rows. Magnitude: mean (the level offset), mean/max/P90 of the
    /// absolute error, and the P90−P10 spread that tells a constant offset (0) from a frequency-dependent one.
    /// Phase: circular mean of the wrapped differences, plus mean/max/P90 of their absolute value.
    /// </summary>
    private static AbBandError Summarize(IReadOnlyList<AbFrequencyError> errors)
    {
        double[] magnitudeErrors = [.. errors.Where(e => !e.Degenerate).Select(e => e.MagnitudeErrorDb)];
        double[] absoluteMagnitudeErrors = [.. magnitudeErrors.Select(Math.Abs)];
        double[] wrappedPhaseDegrees = [.. errors.Where(e => !e.Degenerate).Select(e => e.WrappedPhaseErrorDegrees)];
        double[] absolutePhaseErrors = [.. wrappedPhaseDegrees.Select(Math.Abs)];
        // Circular mean: the angle of the mean unit phasor of the wrapped differences. The differences are
        // degrees, so they are converted to radians before sin/cos — averaging sin(20 rad) would be nonsense.
        double[] wrappedPhaseRadians = [.. wrappedPhaseDegrees.Select(DegreesToRadians)];

        if (magnitudeErrors.Length == 0)
            return new AbBandError(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, errors.Count);

        double meanSine = wrappedPhaseRadians.Average(Math.Sin);
        double meanCosine = wrappedPhaseRadians.Average(Math.Cos);
        double circularMeanDegrees = RadiansToDegrees(ComplexMath.Phase(new Complex(meanCosine, meanSine)));

        return new AbBandError(
            magnitudeErrors.Average(),
            absoluteMagnitudeErrors.Average(),
            absoluteMagnitudeErrors.Max(),
            SpatialMetrics.PercentileNearestRank(absoluteMagnitudeErrors, 90),
            SpatialMetrics.PercentileNearestRank(absoluteMagnitudeErrors, 90) - SpatialMetrics.PercentileNearestRank(absoluteMagnitudeErrors, 10),
            circularMeanDegrees,
            absolutePhaseErrors.Average(),
            absolutePhaseErrors.Max(),
            SpatialMetrics.PercentileNearestRank(absolutePhaseErrors, 90),
            magnitudeErrors.Length,
            errors.Count - magnitudeErrors.Length);
    }

    /// <summary>
    /// P90 rather than max: one noisy bin is not a material disagreement, but a broad error is. A P90 above the
    /// threshold on either axis trips that axis; both trips report both.
    /// </summary>
    private static AbValidationVerdict VerdictFor(AbBandError band, AbValidationOptions options)
    {
        bool magnitude = band.P90AbsoluteMagnitudeErrorDb > options.MagnitudeErrorThresholdDb;
        bool phase = band.P90AbsolutePhaseErrorDegrees > options.PhaseErrorThresholdDegrees;

        return (magnitude, phase) switch
        {
            (true, true) => AbValidationVerdict.MagnitudeAndPhaseMismatch,
            (true, false) => AbValidationVerdict.MagnitudeMismatch,
            (false, true) => AbValidationVerdict.PhaseMismatch,
            _ => AbValidationVerdict.Agrees,
        };
    }

    /// <summary>
    /// Turn the numbers into the short, ordered list of things to check. Each rule is a pattern in the error
    /// shape, not a guess: a constant level offset is gain, a band-wide ~180° is polarity, a phase offset that
    /// is not a flip is the phase setting, an error that varies with frequency is something in the path (DSP or
    /// EQ on one sub), and a material disagreement matching none of those is most likely a synchronisation or
    /// timing problem between the two passes.
    /// </summary>
    private static IReadOnlyList<AbCheckKind> Diagnose(AbBandError band, AbValidationVerdict verdict, AbValidationOptions options)
    {
        double circularMean = Math.Abs(band.CircularMeanPhaseErrorDegrees);
        var checks = new List<AbCheckKind>(4);

        if (circularMean > PolaritySuspectDegrees) checks.Add(AbCheckKind.Polarity);
        if (Math.Abs(band.MeanMagnitudeErrorDb) > options.MagnitudeErrorThresholdDb) checks.Add(AbCheckKind.Gain);
        if (circularMean > options.PhaseErrorThresholdDegrees && circularMean <= PolaritySuspectDegrees) checks.Add(AbCheckKind.PhaseSetting);
        if (band.MagnitudeErrorSpreadDb > 2 * options.MagnitudeErrorThresholdDb) checks.Add(AbCheckKind.DeviceDsp);
        if (verdict != AbValidationVerdict.Agrees && checks.Count == 0) checks.Add(AbCheckKind.MeasurementSync);

        return checks;
    }

    private static double RadiansToDegrees(double radians) => radians * (180.0 / Math.PI);

    private static double DegreesToRadians(double degrees) => degrees * (Math.PI / 180.0);
}
