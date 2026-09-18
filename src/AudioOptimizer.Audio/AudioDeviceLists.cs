namespace AudioOptimizer.Audio;

/// <summary>WASAPI-style sharing mode. Exclusive bypasses the Windows mixer but takes the endpoint from other apps.</summary>
public enum AudioShareMode
{
    Shared,
    Exclusive,
}

/// <summary>Where a capture comes from: the endpoint's input, or its own render stream (WASAPI loopback).</summary>
public enum AudioCaptureMode
{
    Device,
    LoopbackCapture,
}

/// <summary>One endpoint, identified by the backend's stable id (used to reopen it) plus a human name (used in evidence).</summary>
public sealed record AudioDeviceInfo(string Id, string Name)
{
    public override string ToString() => $"{Name} [{Id}]";
}

/// <summary>
/// Inputs and outputs are always separate lists: on a UMIK-1 + DAC rig they are different devices, and a
/// single combined list is how a capture gets opened on the wrong endpoint.
/// </summary>
public sealed record AudioDeviceLists(IReadOnlyList<AudioDeviceInfo> Inputs, IReadOnlyList<AudioDeviceInfo> Outputs)
{
    public static AudioDeviceLists Empty { get; } = new([], []);

    public bool HasAnyDevice => Inputs.Count > 0 || Outputs.Count > 0;

    /// <summary>Pure, empty-tolerant rendering used by the smoke test's list-devices mode.</summary>
    public string Format()
    {
        var text = new System.Text.StringBuilder();
        text.AppendLine($"Input devices ({Inputs.Count}):");
        foreach (AudioDeviceInfo device in Inputs) text.AppendLine($"  {device}");
        if (Inputs.Count == 0) text.AppendLine("  (none)");
        text.AppendLine($"Output devices ({Outputs.Count}):");
        foreach (AudioDeviceInfo device in Outputs) text.AppendLine($"  {device}");
        if (Outputs.Count == 0) text.AppendLine("  (none)");
        return text.ToString();
    }

    /// <summary>Resolves a device by id or by name (case-insensitive); null when absent — never throws.</summary>
    public AudioDeviceInfo? Find(string idOrName)
    {
        ArgumentNullException.ThrowIfNull(idOrName);
        foreach (AudioDeviceInfo device in Inputs.Concat(Outputs))
            if (string.Equals(device.Id, idOrName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(device.Name, idOrName, StringComparison.OrdinalIgnoreCase))
                return device;
        return null;
    }
}
