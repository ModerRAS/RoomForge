namespace AudioOptimizer.Tests;

using AudioOptimizer.Audio;
using NAudio.Wave;

/// <summary>
/// The evidence line for a negotiated device format. Its whole job is to show what actually ran, so the
/// WAVE_FORMAT_EXTENSIBLE unwrap is pinned here: "Extensible" on its own hides whether the samples are float
/// or integer PCM, which is exactly the mistake the first hardware run made. No device is opened.
/// </summary>
public sealed class AudioEngineInfoTests
{
    [Fact]
    public void Describe_unwraps_an_extensible_device_format_so_the_line_shows_the_real_format()
    {
        // Hardware case (Realtek WASAPI Shared-mode mix): Extensible, 32-bit, 2 ch, sub-format
        // 00000003-0000-0010-8000-00aa00389b71 (IEEE_FLOAT) => AsStandardWaveFormat() = IeeeFloat 32-bit.
        var format = new WaveFormatExtensible(48000, 32, 2, useIeeeFloat: true, validBitsPerSample: 32, channelMask: 3);

        string described = AudioEngineInfo.Describe(format);

        Assert.Contains("Extensible 32-bit, 48000 Hz, 2 ch", described);
        Assert.Contains("→ standard: IeeeFloat 32-bit", described);
        Assert.Contains("00000003-0000-0010-8000-00aa00389b71", described);
    }

    [Fact]
    public void Describe_leaves_a_plain_format_alone()
    {
        // new WaveFormat(44100, 24, 1) => Encoding Pcm, 24 bits => reported as-is, with no unwrap suffix,
        // so the presence of "→ standard:" in a log line is a reliable signal that the wrapper was present.
        Assert.Equal("Pcm 24-bit, 44100 Hz, 1 ch", AudioEngineInfo.Describe(new WaveFormat(44100, 24, 1)));
    }
}
