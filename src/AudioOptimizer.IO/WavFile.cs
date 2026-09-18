namespace AudioOptimizer.IO;

using System.Text;

/// <summary>
/// Minimal RIFF/WAVE reader and writer for measurement captures: mono or stereo, interleaved,
/// 32-bit float or 16/24-bit signed PCM. No NAudio, no device access — plain <see cref="System.IO"/>.
/// </summary>
public static class WavFile
{
    private const ushort FormatPcm = 1;
    private const ushort FormatIeeeFloat = 3;

    /// <summary>
    /// Writes <paramref name="samples"/> (interleaved when <paramref name="channels"/> &gt; 1) as a
    /// standard 44-byte-header RIFF/WAVE file. Samples are clamped to [-1, 1].
    /// </summary>
    public static void Write(string path, double[] samples, int sampleRate, WavSampleFormat format = WavSampleFormat.Float32, int channels = 1)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(samples);
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "Sample rate must be > 0.");
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels), channels, "Channel count must be > 0.");
        if (samples.Length % channels != 0)
            throw new ArgumentException($"Sample count {samples.Length} is not a whole number of {channels}-channel frames.", nameof(samples));

        int bitsPerSample = format switch
        {
            WavSampleFormat.Float32 => 32,
            WavSampleFormat.Pcm16 => 16,
            WavSampleFormat.Pcm24 => 24,
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported sample format."),
        };
        int bytesPerSample = bitsPerSample / 8;
        ushort audioFormat = format == WavSampleFormat.Float32 ? FormatIeeeFloat : FormatPcm;
        int dataBytes = samples.Length * bytesPerSample;

        // ponytail: no WAVE_FORMAT_EXTENSIBLE, no channel mask, no directory creation — a plain
        // 44-byte canonical header is all a mono/stereo measurement capture needs. Extend when a real
        // multi-channel file has to be written.
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write((uint)(36 + dataBytes));                 // RIFF size = 4 ("WAVE") + 24 (fmt) + 8 + data
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16u);                                    // PCM/float fmt chunk size
        writer.Write(audioFormat);
        writer.Write((ushort)channels);
        writer.Write((uint)sampleRate);
        writer.Write((uint)(sampleRate * channels * bytesPerSample)); // byte rate
        writer.Write((ushort)(channels * bytesPerSample));            // block align
        writer.Write((ushort)bitsPerSample);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write((uint)dataBytes);

        foreach (double sample in samples) WriteSample(writer, sample, format);
    }

    /// <summary>
    /// Reads a PCM/float RIFF/WAVE file and returns de-interleaved sample values in [-1, 1]:
    /// interleaved when channels &gt; 1. Malformed or truncated files throw
    /// <see cref="InvalidDataException"/> rather than returning partial garbage.
    /// </summary>
    public static (double[] Samples, int SampleRate, int Channels) Read(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        if (stream.Length < 12) throw new InvalidDataException($"Not a WAV file: only {stream.Length} bytes long.");
        if (ReadTag(reader) != "RIFF") throw new InvalidDataException("Not a RIFF file (missing 'RIFF' id).");
        ReadUInt32(reader); // overall RIFF size: ignored, chunks are scanned to the end of the file
        if (ReadTag(reader) != "WAVE") throw new InvalidDataException("Not a WAVE file (missing 'WAVE' id).");

        ushort audioFormat = 0, channels = 0, bitsPerSample = 0;
        uint sampleRate = 0;
        bool sawFormat = false;
        byte[]? data = null;

        while (stream.Position + 8 <= stream.Length)
        {
            string chunkId = ReadTag(reader);
            uint chunkSize = ReadUInt32(reader);
            long next = stream.Position + chunkSize + (chunkSize & 1); // RIFF chunks are word aligned
            long remaining = stream.Length - stream.Position;
            if (chunkSize > remaining)
                throw new InvalidDataException($"Truncated WAV file: '{chunkId}' chunk declares {chunkSize} bytes but only {remaining} remain.");

            if (chunkId == "fmt ")
            {
                if (chunkSize < 16) throw new InvalidDataException($"fmt chunk is {chunkSize} bytes; a PCM/float header needs 16.");
                audioFormat = ReadUInt16(reader);
                channels = ReadUInt16(reader);
                sampleRate = ReadUInt32(reader);
                ReadUInt32(reader); // byte rate (derived, not trusted)
                ReadUInt16(reader); // block align (derived, not trusted)
                bitsPerSample = ReadUInt16(reader);
                sawFormat = true;
            }
            else if (chunkId == "data")
            {
                data = ReadBytes(reader, (int)chunkSize);
            }
            // ponytail: unknown chunks (LIST/fact/bext, WAVE_FORMAT_EXTENSIBLE's extra fmt bytes) are
            // skipped, never interpreted. Parse the extensible sub-format GUID and channel mask only
            // when a real UMIK/loopback capture turns out to need them.

            if (next <= stream.Length) stream.Position = next;
        }

        if (!sawFormat) throw new InvalidDataException("No fmt chunk found.");
        if (data is null) throw new InvalidDataException("No data chunk found.");
        if (channels == 0 || sampleRate == 0) throw new InvalidDataException($"Invalid fmt chunk: {channels} channels at {sampleRate} Hz.");
        if (bitsPerSample == 0 || bitsPerSample % 8 != 0)
            throw new InvalidDataException($"Invalid fmt chunk: {bitsPerSample} bits per sample.");
        if (audioFormat != FormatPcm && audioFormat != FormatIeeeFloat)
            throw new NotSupportedException($"Unsupported WAV format code {audioFormat}; only PCM (1) and IEEE float (3) are read.");

        int bytesPerSample = bitsPerSample / 8;
        int frameBytes = bytesPerSample * channels;
        if (data.Length % frameBytes != 0)
            throw new InvalidDataException($"data chunk length {data.Length} is not a whole number of {frameBytes}-byte frames.");

        var samples = new double[data.Length / bytesPerSample];
        for (int i = 0; i < samples.Length; i++) samples[i] = ReadSample(data, i * bytesPerSample, audioFormat, bitsPerSample);
        return (samples, (int)sampleRate, channels);
    }

    private static void WriteSample(BinaryWriter writer, double sample, WavSampleFormat format)
    {
        double clamped = Math.Clamp(sample, -1.0, 1.0);
        switch (format)
        {
            case WavSampleFormat.Float32:
                writer.Write((float)clamped);
                break;
            case WavSampleFormat.Pcm16:
                // full scale = 2^15, clamped to the largest representable positive code 32767
                writer.Write((short)Math.Clamp((int)Math.Round(clamped * 32768.0), -32768, 32767));
                break;
            case WavSampleFormat.Pcm24:
                int value = Math.Clamp((int)Math.Round(clamped * 8388608.0), -8388608, 8388607);
                writer.Write((byte)(value & 0xFF));
                writer.Write((byte)((value >> 8) & 0xFF));
                writer.Write((byte)((value >> 16) & 0xFF));
                break;
        }
    }

    /// <summary>
    /// Both directions use the same scale (32768 / 8388608, the standard WAV convention), so a
    /// round trip is within half an LSB of the quantised value — 1.526e-5 for 16-bit,
    /// 5.96e-8 for 24-bit — and within a whole LSB even at the clipped full-scale end point.
    /// </summary>
    private static double ReadSample(byte[] data, int offset, ushort audioFormat, int bitsPerSample) => (audioFormat, bitsPerSample) switch
    {
        (FormatPcm, 16) => BitConverter.ToInt16(data, offset) / 32768.0,
        (FormatPcm, 24) => SignExtend24(data, offset) / 8388608.0,
        (FormatIeeeFloat, 32) => BitConverter.ToSingle(data, offset),
        _ => throw new NotSupportedException($"Unsupported WAV sample encoding: format code {audioFormat}, {bitsPerSample} bits."),
    };

    private static int SignExtend24(byte[] data, int offset)
    {
        int value = data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16);
        return (value & 0x800000) != 0 ? value - 0x1000000 : value;
    }

    private static string ReadTag(BinaryReader reader) => Encoding.ASCII.GetString(ReadBytes(reader, 4));

    private static byte[] ReadBytes(BinaryReader reader, int count)
    {
        byte[] bytes = reader.ReadBytes(count);
        if (bytes.Length != count)
            throw new InvalidDataException($"Truncated WAV file: expected {count} more bytes, found {bytes.Length}.");
        return bytes;
    }

    private static ushort ReadUInt16(BinaryReader reader)
    {
        try { return reader.ReadUInt16(); }
        catch (EndOfStreamException e) { throw new InvalidDataException("Truncated WAV file: expected 2 more bytes.", e); }
    }

    private static uint ReadUInt32(BinaryReader reader)
    {
        try { return reader.ReadUInt32(); }
        catch (EndOfStreamException e) { throw new InvalidDataException("Truncated WAV file: expected 4 more bytes.", e); }
    }
}
