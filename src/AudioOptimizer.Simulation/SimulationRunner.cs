namespace AudioOptimizer.Simulation;

using System.Globalization;
using AudioOptimizer.Core;
using AudioOptimizer.Measurement;
using AudioOptimizer.Optimization;

/// <summary>
/// Runs one scenario end to end and reports it. The order is the point of the whole project:
/// <list type="number">
/// <item><description>measure every configuration at every microphone through the product's own chain;</description></item>
/// <item><description>hand the optimizer ONLY what that chain produced;</description></item>
/// <item><description>compare the recommendation against the truth, which the optimizer never saw.</description></item>
/// </list>
/// </summary>
public static class SimulationRunner
{
    public static SimulationOutcome Run(SimulationScenario scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);

        VirtualLab lab = scenario.CreateLab();
        IReadOnlyList<SimulatedMeasurement> measurements = lab.MeasureAll();
        SpatialSummary measured = SpatialMetrics.Compute(Totals(lab, measurements));

        var diagnostics = new List<string>();
        AddDirectPathDiagnostics(scenario, measurements, diagnostics);
        AddWorstSpreadDiagnostic(measured, diagnostics);

        OptimizerResult? optimization = null;
        if (scenario.RunOptimizer)
        {
            // The ONLY input: the measured A/B/A+B complex responses, in band. No ground truth is in scope here.
            optimization = SubwooferOptimizer.Search(lab.AsOptimizerInput(measurements), scenario.Optimize);
        }

        SpatialSummary? reference = scenario.GroundTruthSetting is { } known
            ? SpatialMetrics.Compute(SubwooferModel.Combine(lab.GroundTruth.AsOptimizerInput(), known))
            : null;

        if (optimization is { Recommended: { } recommended } && scenario.GroundTruthSetting is { } truth)
            diagnostics.Add($"recommendation vs truth: {Setting(recommended)} against {Setting(truth)} — spatial σ "
                + $"{optimization.After.MeanStdDevDb:0.00} dB against the truth's "
                + $"{reference?.MeanStdDevDb.ToString("0.00", CultureInfo.InvariantCulture) ?? "n/a"} dB");

        return new SimulationOutcome(scenario, measurements, measured, optimization, reference, diagnostics);
    }

    /// <summary>
    /// The as-measured total at every position: the measured A+B pass when there is one (it is a real capture, not a
    /// prediction), and A alone otherwise. In-band only, exactly like the hardware session's analysis.
    /// </summary>
    private static IReadOnlyList<PositionResponse> Totals(VirtualLab lab, IReadOnlyList<SimulatedMeasurement> measurements)
    {
        SubMode wanted = measurements.Any(measurement => measurement.Mode == SubMode.AB) ? SubMode.AB : SubMode.A;
        var positions = new List<PositionResponse>();
        foreach (SimulatedMeasurement measurement in measurements)
        {
            if (measurement.Mode != wanted) continue;
            FrequencyResponse[] bins = MeasurementSession.InBand(measurement.Result.Response, lab.Band);
            if (bins.Length > 0) positions.Add(new PositionResponse(measurement.Point.Id, lab.Band, bins));
        }

        return positions;
    }

    /// <summary>The frequency in 40–100 Hz where the room is least even across the positions.</summary>
    private static void AddWorstSpreadDiagnostic(SpatialSummary measured, List<string> diagnostics)
    {
        FrequencyMetrics? worst = null;
        foreach (FrequencyMetrics row in measured.PerFrequency)
            if (row.FrequencyHz is >= 40.0 and <= 100.0 && (worst is null || row.RangeDb > worst.RangeDb))
                worst = row;

        if (worst is { } spread)
            diagnostics.Add($"worst positional spread in 40–100 Hz: {spread.FrequencyHz:0.0} Hz σ {spread.StdDevDb:0.00} dB, "
                + $"range {spread.RangeDb:0.00} dB, deepest position {spread.MeanDb - spread.MinDb:0.00} dB below the mean");
    }

    private static string Setting(SubwooferSetting setting)
        => $"{setting.GainDb:+0.0;-0.0;0.0} dB, polarity {setting.Polarity:+#;-#;+1}, phase {setting.PhaseDegrees:0.0}°, "
        + $"delay {setting.DelaySeconds * 1000.0:0.0} ms";

    /// <summary>
    /// For a single-sub, single-microphone scenario: what the closed forms say against what the chain measured. The
    /// scenario's own geometry is the reference, and the chain has no idea it is being checked.
    /// </summary>
    private static void AddDirectPathDiagnostics(
        SimulationScenario scenario,
        IReadOnlyList<SimulatedMeasurement> measurements,
        List<string> diagnostics)
    {
        if (scenario.Subs.Count != 1 || scenario.Microphones.Count != 1 || measurements.Count == 0) return;

        VirtualSubwoofer sub = scenario.Subs[0];
        double sampleRate = scenario.Config.SampleRate;
        int preRollSamples = (int)Math.Round(scenario.Config.PreRollSeconds * sampleRate);

        foreach (SimulatedMeasurement measurement in measurements)
        {
            double distance = sub.Position.DistanceTo(new Position(measurement.Point.X, measurement.Point.Y, measurement.Point.Z));
            double delaySeconds = distance / scenario.Config.Room.SpeedOfSound;
            double expectedSamples = preRollSamples + (delaySeconds * sampleRate);
            int measuredSamples = measurement.Result.Alignment.PeakIndex - measurement.Result.Alignment.ZeroLagIndex;

            diagnostics.Add($"{measurement.Point.Id}: d = {distance:0.000} m, d/c = {delaySeconds * 1000.0:0.000} ms, "
                + $"IR peak {measuredSamples} samples vs {expectedSamples:0.0} expected ({measuredSamples - expectedSamples:+0.0;-0.0;0.0})");

            foreach (double frequency in new[] { 50.0, 100.0 })
            {
                FrequencyResponse bin = measurement.Result.Response
                    .OrderBy(response => Math.Abs(response.FrequencyHz - frequency)).First();
                double expectedRadians = -Math.Tau * bin.FrequencyHz * ((preRollSamples / sampleRate) + delaySeconds);
                double expectedWrapped = Math.IEEERemainder(expectedRadians, Math.Tau);
                diagnostics.Add($"  phase at {bin.FrequencyHz:0.0} Hz: measured {bin.PhaseWrappedRad:+0.000;-0.000;0.000} rad vs "
                    + $"{expectedWrapped:+0.000;-0.000;0.000} rad expected ({bin.PhaseWrappedRad - expectedWrapped:+0.0000;-0.0000;0.0000})");
            }
        }
    }
}
