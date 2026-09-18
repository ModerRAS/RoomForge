namespace AudioOptimizer.Tests;

using AudioOptimizer.Audio;
using Xunit.Abstractions;

/// <summary>
/// Capture accounting and device-model plumbing, all headless. The device lists are deliberately tested with
/// EMPTY inputs (a machine with no audio devices): every helper must pass, not throw.
/// </summary>
public class AudioPlumbingTests(ITestOutputHelper output)
{
    [Fact]
    public void Ring_buffer_keeps_the_newest_samples_and_accounts_for_everything()
    {
        var ring = new BlockRingBuffer(capacity: 5);
        Assert.Equal(5, ring.Capacity);
        Assert.Equal(0, ring.Count);
        Assert.False(ring.IsFull);
        Assert.Empty(ring.ToArray());

        ring.Write([1, 2, 3]);
        Assert.Equal(3, ring.Count);
        Assert.Equal(3, ring.TotalWritten);
        Assert.Equal(0, ring.TotalDropped);
        Assert.Equal([1, 2, 3], ring.ToArray());          // oldest → newest

        ring.Write([4, 5, 6, 7]);                          // overflows by 2
        Assert.Equal(5, ring.Count);
        Assert.True(ring.IsFull);
        Assert.Equal(7, ring.TotalWritten);
        Assert.Equal(2, ring.TotalDropped);
        Assert.Equal([3, 4, 5, 6, 7], ring.ToArray());     // oldest two (1, 2) were dropped
        Assert.Equal(ring.TotalWritten - ring.Count, ring.TotalDropped);

        // A block larger than the whole buffer keeps only its tail.
        ring.Write([10, 11, 12, 13, 14, 15, 16]);
        Assert.Equal(5, ring.Count);
        Assert.Equal([12, 13, 14, 15, 16], ring.ToArray());

        ring.Clear();
        Assert.Equal(0, ring.Count);
        Assert.Empty(ring.ToArray());
        Assert.Equal(14, ring.TotalWritten);               // counters are cumulative, Clear only empties

        Assert.Throws<ArgumentOutOfRangeException>(() => new BlockRingBuffer(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BlockRingBuffer(-1));
        ring.Write([]);                                    // empty block is a no-op, not an error
        Assert.Equal(0, ring.Count);
    }

    [Fact]
    public void Settings_default_to_48k_shared_and_reject_unsupported_rates()
    {
        var settings = new AudioBackendSettings();
        settings.Validate();
        Assert.Equal(48000, settings.SampleRate);
        Assert.Equal(AudioShareMode.Shared, settings.ShareMode);
        Assert.Equal([44100, 48000, 96000], AudioBackendSettings.SupportedSampleRates);
        output.WriteLine($"default: {settings}");

        foreach (int rate in AudioBackendSettings.SupportedSampleRates)
            new AudioBackendSettings(rate, AudioShareMode.Exclusive, 50).Validate();

        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioBackendSettings(8000).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioBackendSettings(192000).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioBackendSettings(48000, AudioShareMode.Shared, 0).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioBackendSettings(48000, AudioShareMode.Shared, -5).Validate());
    }

    [Fact]
    public void Device_lists_stay_separate_and_tolerate_being_empty()
    {
        // The no-audio-device machine case: nothing here may throw.
        AudioDeviceLists empty = AudioDeviceLists.Empty;
        Assert.Empty(empty.Inputs);
        Assert.Empty(empty.Outputs);
        Assert.False(empty.HasAnyDevice);
        string emptyText = empty.Format();
        output.WriteLine(emptyText.ReplaceLineEndings(" | "));
        Assert.Contains("Input devices (0):", emptyText);
        Assert.Contains("Output devices (0):", emptyText);
        Assert.Contains("(none)", emptyText);
        Assert.Null(empty.Find("UMIK-1"));
        Assert.Null(empty.Find(""));

        var umik = new AudioDeviceInfo("{0.0.1.00000000}.mic", "UMIK-1");
        var dac = new AudioDeviceInfo("{0.0.0.00000000}.dac", "USB DAC");
        var lists = new AudioDeviceLists([umik], [dac]);
        Assert.True(lists.HasAnyDevice);
        Assert.Same(umik, lists.Find("UMIK-1"));
        Assert.Same(umik, lists.Find("umik-1"));                 // case-insensitive name
        Assert.Same(dac, lists.Find("{0.0.0.00000000}.DAC"));    // case-insensitive id
        Assert.Null(lists.Find("not a device"));

        string text = lists.Format();
        output.WriteLine(text.ReplaceLineEndings(" | "));
        Assert.Contains("Input devices (1):", text);
        Assert.Contains("Output devices (1):", text);
        Assert.Contains("UMIK-1", text);
        Assert.Contains("USB DAC", text);
        Assert.DoesNotContain("(none)", text);
    }

    [Fact]
    public void Open_failure_messages_name_the_device_the_rate_and_the_fix()
    {
        var exclusive = new AudioBackendSettings(96000, AudioShareMode.Exclusive, 100);
        string busy = DeviceOpenError.Describe(
            AudioOpenFailureKind.DeviceInUse, "Speakers (USB DAC)", exclusive, "0x88890004");
        output.WriteLine(busy);
        Assert.Contains("Speakers (USB DAC)", busy);
        Assert.Contains("96000 Hz", busy);
        Assert.Contains("Exclusive", busy);
        Assert.Contains("in use", busy);
        Assert.Contains("--shared", busy);            // exclusive mode offers the shared-mode escape hatch
        Assert.Contains("0x88890004", busy);          // backend detail preserved for the log

        string sharedBusy = DeviceOpenError.Describe(
            AudioOpenFailureKind.DeviceInUse, "UMIK-1", new AudioBackendSettings(), null);
        output.WriteLine(sharedBusy);
        Assert.Contains("in use", sharedBusy);
        Assert.DoesNotContain("--shared", sharedBusy);
        Assert.DoesNotContain("Backend said", sharedBusy);   // null cause must not leak "null"

        string rate = DeviceOpenError.Describe(
            AudioOpenFailureKind.SampleRateNotSupported, "USB DAC", exclusive, "AUDCLNT_E_UNSUPPORTED_FORMAT", "48000 Hz");
        output.WriteLine(rate);
        Assert.Contains("does not accept 96000 Hz", rate);
        Assert.Contains("does accept 48000 Hz", rate);
        Assert.Contains("44100", rate);
        // The alternatives must EXCLUDE the rate that was just rejected: a message that says "does not accept
        // 96000 Hz, try one of 44100, 48000, 96000" is self-contradictory (printed by a real exclusive-mode open).
        Assert.Contains("Try one of: 44100, 48000 Hz", rate);
        Assert.DoesNotContain("96000,", rate);

        string missing = DeviceOpenError.Describe(AudioOpenFailureKind.DeviceNotFound, "Gone", new AudioBackendSettings());
        output.WriteLine(missing);
        Assert.Contains("no longer present", missing);
        Assert.Contains("list-devices", missing);

        // Every kind, worst-case inputs (no device name, no cause): must produce readable text, never throw.
        foreach (AudioOpenFailureKind kind in Enum.GetValues<AudioOpenFailureKind>())
        {
            string text = DeviceOpenError.Describe(kind, "", new AudioBackendSettings(), null);
            output.WriteLine($"{kind}: {text}");
            Assert.False(string.IsNullOrWhiteSpace(text));
            Assert.Contains("(unknown device)", text);
            Assert.DoesNotContain("null", text);
        }

        Assert.Throws<ArgumentNullException>(() => DeviceOpenError.Describe(AudioOpenFailureKind.Unknown, "x", null!));
    }
}
