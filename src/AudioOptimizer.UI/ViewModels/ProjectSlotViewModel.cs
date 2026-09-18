namespace AudioOptimizer.UI.ViewModels;

using AudioOptimizer.IO;

/// <summary>
/// One row of the project list: exactly the fields the stored manifest carries, formatted for display. No
/// lookup, no math — if a row needs a number that is not in the file, that number belongs in Visualization.
/// </summary>
/// <param name="hasRecording">Whether the file the manifest names is actually on disk; the manifest is a claim.</param>
public sealed class ProjectSlotViewModel(SlotManifest slot, bool hasRecording, bool hasImpulseResponse)
{
    public string MeasurementId => slot.MeasurementId;

    public string Mode => slot.Mode;

    public string PositionId => slot.PointId;

    public string State => slot.State;

    public string Reasons => slot.Reasons.Count == 0 ? "—" : string.Join(", ", slot.Reasons);

    public string PeakMagnitude => slot.PeakMagnitude is { } peak
        ? peak.ToString("0.0000E+0", System.Globalization.CultureInfo.InvariantCulture)
        : "—";

    /// <summary>"recording + IR", "recording only", "IR only" or "—": what a reopened project can show for this slot.</summary>
    public string Signals => (hasRecording, hasImpulseResponse) switch
    {
        (true, true) => "recording + IR",
        (true, false) => "recording only",
        (false, true) => "IR only",
        _ => "—",
    };

    /// <summary>A measured slot whose two files are not both on disk: the manifest claims signals that are gone.</summary>
    public bool IsMissingSignalFile => State is "Done" or "Invalid" && !(hasRecording && hasImpulseResponse);

    public string Capture => slot.Capture is { } capture ? $"{capture.InputDevice} → {capture.OutputDevice}" : "—";

    public string Setting => slot.Capture is { } capture
        ? $"gain {capture.PlaybackGain:0.###}, polarity {capture.Polarity:+#;-#;0}, phase {capture.PhaseDegrees:0.#}°"
        : "—";
}
