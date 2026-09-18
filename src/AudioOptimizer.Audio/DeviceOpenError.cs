namespace AudioOptimizer.Audio;

/// <summary>
/// Why opening an endpoint failed. The backend maps NAudio's typed CoreAudio exceptions onto these, so the
/// message can be built and unit-tested without any hardware present.
/// </summary>
public enum AudioOpenFailureKind
{
    DeviceInUse,
    ExclusiveModeNotAllowed,
    SampleRateNotSupported,
    UnsupportedFormat,
    DeviceNotFound,
    DeviceDisconnected,
    Unknown,
}

/// <summary>A device could not be opened, with a message that already says what to do about it.</summary>
public sealed class AudioDeviceOpenException(string message, AudioOpenFailureKind kind, string deviceName, Exception? inner = null)
    : Exception(message, inner)
{
    public AudioOpenFailureKind Kind { get; } = kind;

    public string DeviceName { get; } = deviceName;
}

/// <summary>
/// Pure text for a failed device open. Headless-testable on purpose: "device busy in exclusive mode" is the
/// common real-world failure and the user should read an instruction, not a COM HRESULT.
/// </summary>
public static class DeviceOpenError
{
    public static string Describe(
        AudioOpenFailureKind kind,
        string deviceName,
        AudioBackendSettings settings,
        string? causeMessage = null,
        string? suggestedSampleRate = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string device = string.IsNullOrWhiteSpace(deviceName) ? "(unknown device)" : deviceName;

        string action = kind switch
        {
            AudioOpenFailureKind.DeviceInUse =>
                "The endpoint is in use by another application. Close it (or the app holding it), then retry"
                + (settings.ShareMode == AudioShareMode.Exclusive ? ", or retry in Shared mode (--shared)." : "."),
            AudioOpenFailureKind.ExclusiveModeNotAllowed =>
                "Windows refused exclusive mode for this endpoint. Other audio software or the device driver is"
                + " holding it; close it, or retry in Shared mode (--shared).",
            AudioOpenFailureKind.SampleRateNotSupported =>
                $"This endpoint does not accept {settings.SampleRate} Hz in {settings.ShareMode} mode."
                + (suggestedSampleRate is null ? "" : $" (it does accept {suggestedSampleRate})")
                + RateAlternatives(settings.SampleRate)
                + (settings.ShareMode == AudioShareMode.Exclusive
                    ? " Exclusive mode requires the endpoint to accept this exact format; Shared mode always accepts"
                      + " the endpoint's own mix format (--shared)."
                    : " Set the endpoint to one of those rates in the Windows sound settings, or close other audio apps."),
            AudioOpenFailureKind.UnsupportedFormat =>
                "This endpoint delivers a sample format the tool cannot convert (supported: 32-bit float, 16-bit PCM"
                + ", 24-bit PCM). Windows hands a Shared-mode client the endpoint's own mix format, so set the device"
                + " to 24-bit/48 kHz in the Windows sound settings, or use Exclusive mode (--exclusive).",
            AudioOpenFailureKind.DeviceNotFound =>
                "The device is no longer present. Re-run the list-devices mode and pick a device that is connected.",
            AudioOpenFailureKind.DeviceDisconnected =>
                "The device was disconnected while in use. Reconnect it and retry.",
            _ =>
                "Unexpected audio backend error. Check that the device is connected and not in use, then retry.",
        };

        string cause = string.IsNullOrWhiteSpace(causeMessage) ? string.Empty : $" Backend said: {causeMessage}";
        return $"Could not open '{device}' ({settings}). {action}{cause}";
    }

    /// <summary>
    /// The supported rates other than the one just rejected. Listing the rejected rate among the alternatives
    /// ("does not accept 48000 Hz ... choose one of: 44100, 48000, 96000") is self-contradictory, and it is what
    /// a real exclusive-mode open on a 48 kHz endpoint printed before this was split out.
    /// </summary>
    private static string RateAlternatives(int rejectedSampleRate)
    {
        int[] alternatives = [.. AudioBackendSettings.SupportedSampleRates.Where(rate => rate != rejectedSampleRate)];
        return alternatives.Length == 0 ? string.Empty : $" Try one of: {string.Join(", ", alternatives)} Hz.";
    }
}
