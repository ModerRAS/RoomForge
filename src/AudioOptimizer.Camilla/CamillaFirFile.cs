namespace AudioOptimizer.Camilla;

using System.Text;

/// <summary>Mono float32 WAV without the measurement writer's [-1, 1] clamp. FIR peaks may exceed full scale.</summary>
public static class CamillaFirFile
{
    public static void Write(string path, double[] samples, int sampleRate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(samples);
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (samples.Length == 0) throw new ArgumentException("FIR is empty.", nameof(samples));

        int dataBytes = samples.Length * 4;
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write((uint)(36 + dataBytes));
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16u);
        writer.Write((ushort)3);
        writer.Write((ushort)1);
        writer.Write((uint)sampleRate);
        writer.Write((uint)(sampleRate * 4));
        writer.Write((ushort)4);
        writer.Write((ushort)32);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write((uint)dataBytes);
        foreach (double sample in samples)
        {
            if (!double.IsFinite(sample)) throw new InvalidDataException("FIR contains a non-finite sample.");
            writer.Write((float)sample);
        }
    }

    public static (double[] Samples, int SampleRate) Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        if (stream.Length < 44) throw new InvalidDataException("FIR WAV is too short.");
        if (ReadTag(reader) != "RIFF") throw new InvalidDataException("FIR WAV is missing RIFF.");
        reader.ReadUInt32();
        if (ReadTag(reader) != "WAVE") throw new InvalidDataException("FIR WAV is missing WAVE.");

        ushort audioFormat = 0, channels = 0, bits = 0;
        uint sampleRate = 0;
        byte[]? data = null;
        bool sawFormat = false;
        while (stream.Position + 8 <= stream.Length)
        {
            string id = ReadTag(reader);
            uint size = reader.ReadUInt32();
            long next = stream.Position + size + (size & 1);
            if (stream.Position + size > stream.Length) throw new InvalidDataException("FIR WAV chunk is truncated.");
            if (id == "fmt ")
            {
                if (size < 16) throw new InvalidDataException("FIR WAV fmt chunk is short.");
                audioFormat = reader.ReadUInt16();
                channels = reader.ReadUInt16();
                sampleRate = reader.ReadUInt32();
                reader.ReadUInt32();
                reader.ReadUInt16();
                bits = reader.ReadUInt16();
                sawFormat = true;
            }
            else if (id == "data")
            {
                data = reader.ReadBytes((int)size);
            }

            if (next <= stream.Length) stream.Position = next;
        }

        if (!sawFormat || data is null) throw new InvalidDataException("FIR WAV is missing fmt or data.");
        if (audioFormat != 3 || channels != 1 || bits != 32)
            throw new InvalidDataException("FIR WAV must be mono float32.");
        if (data.Length % 4 != 0) throw new InvalidDataException("FIR WAV data is not a whole number of samples.");

        var samples = new double[data.Length / 4];
        for (int i = 0; i < samples.Length; i++) samples[i] = BitConverter.ToSingle(data, i * 4);
        return (samples, (int)sampleRate);
    }

    static string ReadTag(BinaryReader reader) => Encoding.ASCII.GetString(reader.ReadBytes(4));
}
