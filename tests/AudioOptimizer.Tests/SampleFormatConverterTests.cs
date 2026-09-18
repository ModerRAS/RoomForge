namespace AudioOptimizer.Tests;

using AudioOptimizer.Audio;
using NAudio.Wave;
using Xunit.Abstractions;

/// <summary>
/// Sample-format conversion between device buffers and the double samples the DSP consumes.
/// Scaling is the WavFile convention (Int16 v/32768, Int24 v/8388608, Float32 as-is), so the tolerance for
/// every integer assertion is 1 LSB: 1/32768 = 3.0517578125e-5 and 1/8388608 = 1.1920928955078125e-7.
/// </summary>
public class SampleFormatConverterTests(ITestOutputHelper output)
{
    private const double OneLsb16 = 1.0 / 32768.0;        // 3.0517578125e-05
    private const double OneLsb24 = 1.0 / 8388608.0;      // 1.1920928955078125e-07

    [Theory]
    [InlineData(AudioSampleFormat.Float32, 4, 1e-7)]
    [InlineData(AudioSampleFormat.Int16, 2, OneLsb16)]
    [InlineData(AudioSampleFormat.Int24, 3, OneLsb24)]
    public void Mono_round_trip_is_within_one_lsb(AudioSampleFormat format, int expectedBytesPerSample, double tolerance)
    {
        // Values chosen to include 0, ±0.5, ±full scale and a value that is not representable exactly
        // (1/3) so quantisation, clamping and sign handling are all exercised.
        double[] mono = [0.0, 0.5, -0.5, 1.0, -1.0, 1.0 / 3.0, -1.0 / 3.0, 1e-4];

        Assert.Equal(expectedBytesPerSample, SampleFormatConverter.BytesPerSample(format));
        byte[] bytes = SampleFormatConverter.ToInterleavedBytes(mono, format, channels: 1);
        Assert.Equal(mono.Length * expectedBytesPerSample, bytes.Length);

        double[] back = SampleFormatConverter.ToMonoDoubles(bytes, format, channels: 1);
        Assert.Equal(mono.Length, back.Length);
        for (int i = 0; i < mono.Length; i++)
        {
            output.WriteLine($"{format} [{i}] {mono[i]:E6} → {back[i]:E6} (Δ {Math.Abs(mono[i] - back[i]):E3})");
            Assert.True(Math.Abs(mono[i] - back[i]) <= tolerance, $"{format}[{i}]: {mono[i]} → {back[i]}");
        }
    }

    [Fact]
    public void Full_scale_clamps_to_the_largest_representable_code()
    {
        // Int16 +1.0 must become 32767, never wrap to −32768: (short)32768 == −32768 is the classic bug.
        byte[] positive16 = SampleFormatConverter.ToInterleavedBytes([1.0], AudioSampleFormat.Int16, 1);
        Assert.Equal(32767, BitConverter.ToInt16(positive16, 0));
        byte[] negative16 = SampleFormatConverter.ToInterleavedBytes([-1.0], AudioSampleFormat.Int16, 1);
        Assert.Equal(-32768, BitConverter.ToInt16(negative16, 0));

        byte[] positive24 = SampleFormatConverter.ToInterleavedBytes([1.0], AudioSampleFormat.Int24, 1);
        Assert.Equal(8388607, positive24[0] | (positive24[1] << 8) | (positive24[2] << 16));
        byte[] negative24 = SampleFormatConverter.ToInterleavedBytes([-1.0], AudioSampleFormat.Int24, 1);
        Assert.Equal(-8388608, (negative24[0] | (negative24[1] << 8) | (negative24[2] << 16)) - 0x1000000);

        // Out-of-range input is clamped, not wrapped.
        Assert.Equal(32767, BitConverter.ToInt16(SampleFormatConverter.ToInterleavedBytes([1.7], AudioSampleFormat.Int16, 1), 0));
        Assert.Equal(-32768, BitConverter.ToInt16(SampleFormatConverter.ToInterleavedBytes([-1.7], AudioSampleFormat.Int16, 1), 0));
    }

    [Fact]
    public void Stereo_interleaved_buffers_keep_the_channels_apart()
    {
        // Build an interleaved stereo buffer the way a device hands it over: one frame at a time.
        var buffer = new List<byte>();
        foreach ((double left, double right) in new[] { (0.25, 0.75), (-0.5, -0.125), (0.0, 0.0) })
        {
            buffer.AddRange(SampleFormatConverter.ToInterleavedBytes([left], AudioSampleFormat.Int16, 1));
            buffer.AddRange(SampleFormatConverter.ToInterleavedBytes([right], AudioSampleFormat.Int16, 1));
        }
        byte[] stereo = [.. buffer];
        Assert.Equal(12, stereo.Length);
        Assert.Equal(3, SampleFormatConverter.FrameCount(stereo, AudioSampleFormat.Int16, 2));

        double[] channel0 = SampleFormatConverter.ToMonoDoubles(stereo, AudioSampleFormat.Int16, 2, channel: 0);
        double[] channel1 = SampleFormatConverter.ToMonoDoubles(stereo, AudioSampleFormat.Int16, 2, channel: 1);
        output.WriteLine($"ch0=[{string.Join(", ", channel0)}] ch1=[{string.Join(", ", channel1)}]");
        Assert.Equal([0.25, -0.5, 0.0], channel0);      // ±0.25/±0.5 are exactly representable at 15 bits
        Assert.Equal([0.75, -0.125, 0.0], channel1);

        // Mono written to stereo duplicates into both channels.
        byte[] duplicated = SampleFormatConverter.ToInterleavedBytes([0.25, -0.5], AudioSampleFormat.Float32, 2);
        double[] back0 = SampleFormatConverter.ToMonoDoubles(duplicated, AudioSampleFormat.Float32, 2, 0);
        double[] back1 = SampleFormatConverter.ToMonoDoubles(duplicated, AudioSampleFormat.Float32, 2, 1);
        Assert.Equal(back0, back1);
    }

    [Fact]
    public void Trailing_partial_frame_is_ignored_and_arguments_are_validated()
    {
        // A torn callback can end mid-frame; 5 bytes of Int16 stereo is 1 frame + 1 stray byte.
        byte[] torn = new byte[5];
        Assert.Equal(1, SampleFormatConverter.FrameCount(torn, AudioSampleFormat.Int16, 2));
        Assert.Single(SampleFormatConverter.ToMonoDoubles(torn, AudioSampleFormat.Int16, 2));
        Assert.Empty(SampleFormatConverter.ToMonoDoubles([], AudioSampleFormat.Float32, 1));

        Assert.Throws<ArgumentOutOfRangeException>(() => SampleFormatConverter.ToMonoDoubles(torn, AudioSampleFormat.Int16, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => SampleFormatConverter.ToMonoDoubles(torn, AudioSampleFormat.Int16, 2, channel: 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => SampleFormatConverter.ToInterleavedBytes([0.0], AudioSampleFormat.Int16, 0));
    }

    [Fact]
    public void Device_wave_formats_map_to_a_converter_format_and_extensible_is_unwrapped()
    {
        // Hardware-verified case: a WASAPI Shared-mode loopback stream on a Realtek endpoint reported
        // Extensible/32-bit/2ch with an IEEE_FLOAT sub-format. Its Encoding is NOT WaveFormatEncoding.IeeeFloat,
        // so it only maps after AsStandardWaveFormat() unwraps it (this is what the first hardware run got wrong).
        // new WaveFormatExtensible(48000, 32, 2, useIeeeFloat: true, validBitsPerSample: 32, channelMask: 3)
        //   -> AsStandardWaveFormat() = Encoding IeeeFloat, 32 bits  => AudioSampleFormat.Float32
        Assert.Equal(AudioSampleFormat.Float32, SampleFormatConverter.TryFromWaveFormat(
            new WaveFormatExtensible(48000, 32, 2, useIeeeFloat: true, validBitsPerSample: 32, channelMask: 3)));

        // WaveFormat.CreateIeeeFloatWaveFormat(48000, 2) => Encoding IeeeFloat, 32 bits => Float32
        Assert.Equal(AudioSampleFormat.Float32, SampleFormatConverter.TryFromWaveFormat(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)));

        // new WaveFormat(44100, 16, 1) => Encoding Pcm, 16 bits => Int16
        Assert.Equal(AudioSampleFormat.Int16, SampleFormatConverter.TryFromWaveFormat(new WaveFormat(44100, 16, 1)));

        // new WaveFormat(44100, 24, 1) => Encoding Pcm, 24 bits => Int24
        Assert.Equal(AudioSampleFormat.Int24, SampleFormatConverter.TryFromWaveFormat(new WaveFormat(44100, 24, 1)));

        // new WaveFormat(48000, 32, 2) => Encoding Pcm, 32 bits => not one of the three shapes => null (refused,
        // the harness then prints "cannot convert" instead of guessing at a 32-bit integer scale).
        Assert.Null(SampleFormatConverter.TryFromWaveFormat(new WaveFormat(48000, 32, 2)));
    }
}
