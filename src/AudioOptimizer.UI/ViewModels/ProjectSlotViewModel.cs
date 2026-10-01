namespace AudioOptimizer.UI.ViewModels;

using AudioOptimizer.IO;
using AudioOptimizer.UI.Localization;

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

    public string DisplayState => UiText.IsChinese && ZhState.TryGetValue(slot.State, out string? text) ? text : slot.State;

    public string Reasons => slot.Reasons.Count == 0 ? "—" : string.Join(", ", slot.Reasons.Select(Reason));

    public string PeakMagnitude => slot.PeakMagnitude is { } peak
        ? peak.ToString("0.0000E+0", System.Globalization.CultureInfo.InvariantCulture)
        : "—";

    /// <summary>"recording + IR", "recording only", "IR only" or "—": what a reopened project can show for this slot.</summary>
    public string Signals => (hasRecording, hasImpulseResponse) switch
    {
        (true, true) => UiText.Get("Slot.RecordingIr"),
        (true, false) => UiText.Get("Slot.Recording"),
        (false, true) => UiText.Get("Slot.Ir"),
        _ => "—",
    };

    private static string Reason(string reason) => UiText.IsChinese && ZhReasons.TryGetValue(reason, out string? text) ? text : reason;

    private static readonly Dictionary<string, string> ZhReasons = new()
    {
        ["InputClipping"] = "输入太大，削波了",
        ["OutputClipping"] = "输出太大，削波了",
        ["NoSignal"] = "没有收到信号",
        ["LowSignal"] = "信号太小",
        ["AbortedInFlight"] = "测量被你中途停掉了",
    };

    private static readonly Dictionary<string, string> ZhState = new()
    {
        ["Pending"] = "还没测",
        ["Done"] = "完成",
        ["Invalid"] = "没通过",
        ["Skipped"] = "跳过",
    };

    /// <summary>A measured slot whose two files are not both on disk: the manifest claims signals that are gone.</summary>
    public bool IsMissingSignalFile => State is "Done" or "Invalid" && !(hasRecording && hasImpulseResponse);

    public string Capture => slot.Capture is { } capture ? $"{capture.InputDevice} → {capture.OutputDevice}" : "—";

    public string Setting => slot.Capture is { } capture
        ? $"gain {capture.PlaybackGain:0.###}, polarity {capture.Polarity:+#;-#;0}, phase {capture.PhaseDegrees:0.#}°"
        : "—";
}
