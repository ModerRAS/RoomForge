namespace AudioOptimizer.Simulation;

using System.Globalization;
using AudioOptimizer.Core;
using AudioOptimizer.Optimization;

/// <summary>Everything one scenario run produced, as data. <see cref="Format"/> renders it; nothing here is hidden in
/// a log line a caller cannot read back.</summary>
public sealed record SimulationOutcome(
    SimulationScenario Scenario,
    IReadOnlyList<SimulatedMeasurement> Measurements,
    SpatialSummary Measured,
    OptimizerResult? Optimization,
    SpatialSummary? GroundTruthReference,
    IReadOnlyList<string> Diagnostics)
{
    public SimulationMeasurementSummary MeasuredSummary => SimulationMeasurementSummary.Of(Measurements);

    /// <summary>The report a person reads: pipeline results, spatial spread, the recommendation, and the truth.</summary>
    public string Format()
    {
        var text = new System.Text.StringBuilder();
        text.AppendLine($"{Scenario.Id} — {Scenario.Title}");
        text.AppendLine($"  {Scenario.Description}");
        RoomModel room = Scenario.Config.Room;
        text.AppendLine($"  room {room.LengthMetres} × {room.WidthMetres} × {room.HeightMetres} m, c = {room.SpeedOfSound} m/s, "
            + $"ISM order {Scenario.Config.ImageSourceOrder}, reflections {room.Reflections}");
        text.AppendLine($"  sweep {Scenario.Config.SweepStartHz}–{Scenario.Config.SweepEndHz} Hz over "
            + $"{Scenario.Config.SweepSeconds} s at {Scenario.Config.SampleRate} Hz; noise floor {Scenario.Config.MicrophoneNoiseLevel:E1}; "
            + $"playback gain {Scenario.Config.PlaybackGain}");
        foreach (VirtualSubwoofer sub in Scenario.Subs)
            text.AppendLine($"  sub at {sub.Position}: {sub.GainDb:+0.0;-0.0;0.0} dB, polarity {sub.Polarity:+#;-#;+1}, "
                + $"phase {sub.PhaseDegrees:0.0}°, delay {sub.DelaySeconds * 1000.0:0.0} ms");

        text.AppendLine($"  pipeline: {Measurements.Count} points through PointMeasurement.Run");
        foreach ((string mode, string verdict) in MeasuredSummary.Verdicts)
            text.AppendLine($"    {mode}: {verdict}");

        text.AppendLine("  measured spatial spread over " + Measured.AnalysisBand + " across "
            + $"{Measured.PerFrequency.Count} bins:");
        text.AppendLine("    " + Describe(BandSpatialStats.Of(Measured)));

        foreach (string line in Diagnostics)
            text.AppendLine("  " + line);

        if (Optimization is { } result)
        {
            text.AppendLine($"  optimizer: {result.Verdict}; {"before".PadRight(6)} {Describe(BandSpatialStats.Of(result.Before))}");
            text.AppendLine($"  optimizer: {"after".PadRight(13)} {Describe(BandSpatialStats.Of(result.After))}");
            text.AppendLine("    recommended " + Setting(result.Recommended)
                + $"; search score {result.ScoreBefore:0.000} → {result.ScoreAfter:0.000} dB; level change {result.LevelChangeDb:+0.00;-0.00;0.00} dB");
            text.AppendLine($"    boost limit {result.Constraint.MaxBoostLimitDb:0.0} dB, achieved "
                + $"{(result.Constraint.MaxAchievedBoostDb is { } achieved ? achieved.ToString("0.00", CultureInfo.InvariantCulture) + " dB" : "none")}, "
                + $"{result.Constraint.CandidatesRejected}/{result.Constraint.CandidatesEvaluated} candidates rejected, "
                + $"binding {result.Constraint.Binding}");
        }

        if (Scenario.GroundTruthSetting is { } known)
        {
            text.AppendLine($"  ground truth: the known correction is {Setting(known)}");
            if (GroundTruthReference is { } reference)
                text.AppendLine($"    it achieves {Describe(BandSpatialStats.Of(reference))}");
        }

        return text.ToString();
    }

    private static string Describe(BandSpatialStats stats)
        => $"mean {stats.MeanDb:+0.00;-0.00;0.00} dB, median {stats.MedianDb:+0.00;-0.00;0.00}, "
        + $"σ {stats.StdDevDb:0.00}, min {stats.MinDb:0.00}, max {stats.MaxDb:0.00}, range {stats.RangeDb:0.00}, "
        + $"P10 {stats.P10Db:0.00}, P90 {stats.P90Db:0.00}, P90−P10 {stats.P90P10Db:0.00}";

    private static string Setting(SubwooferSetting? setting)
        => setting is { } value
            ? $"{value.GainDb:+0.0;-0.0;0.0} dB, polarity {value.Polarity:+#;-#;+1}, phase {value.PhaseDegrees:0.0}°, "
                + $"delay {value.DelaySeconds * 1000.0:0.0} ms"
            : "none";
}

/// <summary>How the shipped quality checks graded a run — the count and the named reasons, never a bare "ok".</summary>
public sealed record SimulationMeasurementSummary(
    int Count,
    int Clean,
    IReadOnlyList<QualityIssue> Issues,
    IReadOnlyList<(string Mode, string Verdict)> Verdicts)
{
    public bool AllClean => Clean == Count;

    public static SimulationMeasurementSummary Of(IReadOnlyList<SimulatedMeasurement> measurements)
    {
        ArgumentNullException.ThrowIfNull(measurements);

        var issues = new List<QualityIssue>();
        var verdicts = new List<(string Mode, string Verdict)>();
        int clean = 0;
        foreach (SimulatedMeasurement measurement in measurements)
        {
            IReadOnlyList<QualityIssue> reasons = measurement.Result.Issues;
            if (reasons.Count == 0) clean++;
            foreach (QualityIssue reason in reasons)
                if (!issues.Contains(reason)) issues.Add(reason);
        }

        foreach (SubMode mode in Enum.GetValues<SubMode>())
        {
            List<SimulatedMeasurement> ofMode = [.. measurements.Where(measurement => measurement.Mode == mode)];
            if (ofMode.Count == 0) continue;
            int valid = ofMode.Count(measurement => measurement.Result.IsValid);
            List<QualityIssue> modeIssues = [.. ofMode.SelectMany(measurement => measurement.Result.Issues).Distinct()];
            verdicts.Add((mode.ToString(), modeIssues.Count == 0
                ? $"{valid}/{ofMode.Count} clean"
                : $"{valid}/{ofMode.Count} clean, flagged {string.Join(", ", modeIssues)}"));
        }

        return new SimulationMeasurementSummary(measurements.Count, clean, issues, verdicts);
    }
}
