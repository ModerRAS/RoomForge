namespace AudioOptimizer.Audio;

using System.Buffers.Binary;
using NAudio.Wave;

/// <summary>
/// Device-buffer ↔ double conversion for the three formats a WASAPI endpoint can hand over or accept.
/// Scaling matches <c>WavFile</c> exactly (Int16 v/32768, Int24 v/8388608, Float32 as-is) so a recorded
/// device buffer and the WAV written from it carry identical numbers; writes clamp to [-1, 1] like WavFile.
/// </summary>
public static class SampleFormatConverter
{
    /// <summary>
    /// Maps what a device actually hands over onto a converter format, or null when it is not one of the three
    /// shapes this library converts. WAVE_FORMAT_EXTENSIBLE is unwrapped with NAudio's own
    /// <see cref="WaveFormatExtensible.AsStandardWaveFormat"/> instead of a hand-rolled sub-format GUID
    /// comparison: a real WASAPI Shared-mode stream reports Extensible/32-bit with an IEEE_FLOAT sub-format
    /// (found on a Realtek loopback capture), and that is only recognised after the unwrap.
    /// </summary>
    public static AudioSampleFormat? TryFromWaveFormat(WaveFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        WaveFormat standard = format is WaveFormatExtensible extensible ? extensible.AsStandardWaveFormat() : format;
        return standard.Encoding switch
        {
            WaveFormatEncoding.IeeeFloat when standard.BitsPerSample == 32 => AudioSampleFormat.Float32,
            WaveFormatEncoding.Pcm when standard.BitsPerSample == 16 => AudioSampleFormat.Int16,
            WaveFormatEncoding.Pcm when standard.BitsPerSample == 24 => AudioSampleFormat.Int24,
            _ => null,
        };
    }

    public static int BytesPerSample(AudioSampleFormat format) => format switch
    {
        AudioSampleFormat.Float32 => 4,
        AudioSampleFormat.Int16 => 2,
        AudioSampleFormat.Int24 => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown sample format."),
    };

    /// <summary>Frames in an interleaved buffer; a trailing partial frame is ignored (a torn callback).</summary>
    public static int FrameCount(ReadOnlySpan<byte> interleaved, AudioSampleFormat format, int channels)
    {
        if (channels < 1) throw new ArgumentOutOfRangeException(nameof(channels), channels, "Channel count must be >= 1.");
        return interleaved.Length / (BytesPerSample(format) * channels);
    }

    /// <summary>Extracts one channel from an interleaved buffer as doubles in [-1, 1].</summary>
    public static double[] ToMonoDoubles(ReadOnlySpan<byte> interleaved, AudioSampleFormat format, int channels, int channel = 0)
    {
        if (channels < 1) throw new ArgumentOutOfRangeException(nameof(channels), channels, "Channel count must be >= 1.");
        if (channel < 0 || channel >= channels)
            throw new ArgumentOutOfRangeException(nameof(channel), channel, $"Channel must be in [0, {channels - 1}].");

        int bytesPerSample = BytesPerSample(format);
        int frameSize = bytesPerSample * channels;
        int frames = FrameCount(interleaved, format, channels);
        var result = new double[frames];
        for (int frame = 0; frame < frames; frame++)
            result[frame] = Read(interleaved.Slice(frame * frameSize + channel * bytesPerSample, bytesPerSample), format);
        return result;
    }

    /// <summary>Mono doubles → interleaved device bytes; the same mono value is written to every channel.</summary>
    public static byte[] ToInterleavedBytes(ReadOnlySpan<double> mono, AudioSampleFormat format, int channels)
    {
        if (channels < 1) throw new ArgumentOutOfRangeException(nameof(channels), channels, "Channel count must be >= 1.");

        int bytesPerSample = BytesPerSample(format);
        var result = new byte[mono.Length * channels * bytesPerSample];
        for (int frame = 0; frame < mono.Length; frame++)
        {
            int frameStart = frame * channels * bytesPerSample;
            for (int channel = 0; channel < channels; channel++)
                Write(result.AsSpan(frameStart + channel * bytesPerSample, bytesPerSample), mono[frame], format);
        }
        return result;
    }

    private static double Read(ReadOnlySpan<byte> source, AudioSampleFormat format) => format switch
    {
        AudioSampleFormat.Float32 => BinaryPrimitives.ReadSingleLittleEndian(source),
        AudioSampleFormat.Int16 => BinaryPrimitives.ReadInt16LittleEndian(source) / 32768.0,
        AudioSampleFormat.Int24 => ReadInt24(source) / 8388608.0,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown sample format."),
    };

    private static int ReadInt24(ReadOnlySpan<byte> source)
    {
        int value = source[0] | (source[1] << 8) | (source[2] << 16);
        return (value & 0x800000) != 0 ? value - 0x1000000 : value;   // sign-extend the 24-bit sample
    }

    private static void Write(Span<byte> target, double value, AudioSampleFormat format)
    {
        double clamped = Math.Clamp(value, -1.0, 1.0);
        switch (format)
        {
            case AudioSampleFormat.Float32:
                BinaryPrimitives.WriteSingleLittleEndian(target, (float)clamped);
                break;
            case AudioSampleFormat.Int16:
                // full scale = 2^15, clamped to the largest representable positive code 32767 (same as WavFile)
                BinaryPrimitives.WriteInt16LittleEndian(target, (short)Math.Clamp(Math.Round(clamped * 32768.0), -32768.0, 32767.0));
                break;
            default:
                int scaled = (int)Math.Clamp(Math.Round(clamped * 8388608.0), -8388608.0, 8388607.0);
                target[0] = (byte)scaled;
                target[1] = (byte)(scaled >> 8);
                target[2] = (byte)(scaled >> 16);
                break;
        }
    }
}
