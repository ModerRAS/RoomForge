namespace AudioOptimizer.Tests;

using AudioOptimizer.IO;

/// <summary>
/// RIFF/WAVE round trips and failure modes. Quantisation bounds: Float32 → float round-off
/// (≤ 6e-8 for |x| ≤ 1), Pcm16 → half an LSB = 1/65536 = 1.526e-5, Pcm24 → half an LSB
/// = 1/16777216 = 5.96e-8. Both directions use the same scale (32768 / 8388608), so a round trip is
/// within half an LSB for |x| ≤ 1 and within one whole LSB at the clipped full-scale end point.
/// </summary>
public class WavFileTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"ao_wav_{Guid.NewGuid():N}");

    public WavFileTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, true);

    private string Path_(string name) => Path.Combine(_directory, name);

    private static double[] Tone(int length, double amplitude, double cycles)
    {
        var samples = new double[length];
        for (int i = 0; i < length; i++) samples[i] = amplitude * Math.Sin(2.0 * Math.PI * cycles * i / length);
        return samples;
    }

    private static double MaxError(double[] expected, double[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        double worst = 0.0;
        for (int i = 0; i < expected.Length; i++) worst = Math.Max(worst, Math.Abs(expected[i] - actual[i]));
        return worst;
    }

    [Theory]
    [InlineData(WavSampleFormat.Float32, 1e-7)]            // measured 2.98e-8 (float round-off)
    [InlineData(WavSampleFormat.Pcm16, 3.0517578125e-5)]   // 1 LSB = 2^-15; measured 1.52e-5 = 0.498 LSB
    [InlineData(WavSampleFormat.Pcm24, 1.1920928955078125e-7)] // 1 LSB = 2^-23; measured 5.94e-8 = 0.499 LSB
    public void Round_trip_is_within_one_lsb(WavSampleFormat format, double tolerance)
    {
        double[] samples = Tone(1000, 0.9, 37.0);
        string path = Path_($"roundtrip_{format}.wav");
        WavFile.Write(path, samples, 44100, format);

        var (readBack, sampleRate, channels) = WavFile.Read(path);

        Assert.Equal(1000, readBack.Length);
        Assert.Equal(44100, sampleRate);   // sample rate preserved
        Assert.Equal(1, channels);         // mono preserved
        Assert.True(MaxError(samples, readBack) <= tolerance, $"max error {MaxError(samples, readBack):E3} > {tolerance:E3}");
    }

    [Theory]
    [InlineData(WavSampleFormat.Pcm16, 3.0517578125e-5)]        // full scale clamps to 32767 → exactly 1 LSB = 2^-15
    [InlineData(WavSampleFormat.Pcm24, 1.1920928955078125e-7)]  // full scale clamps to 8388607 → exactly 1 LSB = 2^-23
    public void Full_scale_is_within_one_lsb(WavSampleFormat format, double tolerance)
    {
        double[] samples = [1.0, -1.0, 1.0, -1.0];
        string path = Path_($"fullscale_{format}.wav");
        WavFile.Write(path, samples, 48000, format);

        var (readBack, _, _) = WavFile.Read(path);

        Assert.True(MaxError(samples, readBack) <= tolerance, $"max error {MaxError(samples, readBack):E3} > {tolerance:E3}");
    }

    [Fact]
    public void Stereo_is_interleaved_and_the_channel_count_survives()
    {
        double[] mono = Tone(1000, 0.8, 11.0);
        var interleaved = new double[2 * mono.Length];
        for (int i = 0; i < mono.Length; i++)
        {
            interleaved[2 * i] = mono[i];          // left
            interleaved[2 * i + 1] = -0.5 * mono[i]; // right
        }

        string path = Path_("stereo.wav");
        WavFile.Write(path, interleaved, 48000, WavSampleFormat.Pcm24, channels: 2);

        var (readBack, sampleRate, channels) = WavFile.Read(path);

        Assert.Equal(48000, sampleRate);
        Assert.Equal(2, channels);
        Assert.Equal(interleaved.Length, readBack.Length);
        Assert.True(MaxError(interleaved, readBack) <= 1.1920928955078125e-7, $"stereo max error {MaxError(interleaved, readBack):E3}");   // 1 LSB = 2^-23
    }

    [Fact]
    public void Write_rejects_a_partial_frame()
    {
        // 3 samples with channels: 2 is not a whole number of frames
        Assert.Throws<ArgumentException>(() => WavFile.Write(Path_("bad.wav"), [0.1, 0.2, 0.3], 48000, WavSampleFormat.Float32, channels: 2));
    }

    [Fact]
    public void Short_header_fails_with_a_clear_exception()
    {
        string fourBytes = Path_("four.wav");
        File.WriteAllBytes(fourBytes, "RIFF"u8.ToArray());
        Assert.Throws<InvalidDataException>(() => WavFile.Read(fourBytes));   // 4 bytes < 12 → not a WAV file

        string headerOnly = Path_("headeronly.wav");
        using (var writer = new BinaryWriter(File.Create(headerOnly)))
        {
            writer.Write("RIFF"u8); writer.Write(0u); writer.Write("WAVE"u8);   // 12 bytes, no fmt/data chunk
        }
        Assert.Throws<InvalidDataException>(() => WavFile.Read(headerOnly));

        string missingFormat = Path_("noformat.wav");
        using (var writer = new BinaryWriter(File.Create(missingFormat)))
        {
            writer.Write("RIFF"u8); writer.Write(12u); writer.Write("WAVE"u8);
            writer.Write("data"u8); writer.Write(4u); writer.Write(0); writer.Write(0);   // data chunk only
        }
        Assert.Throws<InvalidDataException>(() => WavFile.Read(missingFormat));

        string wrongMagic = Path_("notriff.wav");
        using (var writer = new BinaryWriter(File.Create(wrongMagic)))
        {
            writer.Write("RIFX"u8); writer.Write(0u); writer.Write("WAVE"u8);
        }
        Assert.Throws<InvalidDataException>(() => WavFile.Read(wrongMagic));
    }

    [Fact]
    public void Truncated_data_chunk_fails_instead_of_returning_garbage()
    {
        // a valid 2000-byte Pcm16 file cut down to the first 10 bytes of its data chunk: the data chunk
        // declares 2000 bytes but only 10 remain → InvalidDataException, never a short/wrong array
        string full = Path_("truncate_source.wav");
        WavFile.Write(full, Tone(1000, 0.9, 37.0), 48000, WavSampleFormat.Pcm16);
        byte[] bytes = File.ReadAllBytes(full);
        string truncated = Path_("truncated.wav");
        File.WriteAllBytes(truncated, bytes[..(44 + 10)]);

        var exception = Assert.Throws<InvalidDataException>(() => WavFile.Read(truncated));
        Assert.Contains("Truncated", exception.Message);
    }
}
