namespace AudioOptimizer.Tests;

using System.IO;
using Xunit;

/// <summary>
/// The band-limited accessor is now the only way anything downstream obtains measured frequencies, and a comment
/// asking callers to use it is not a guard — so the raw name is scanned for, with a positive control. Without the
/// control, a zero hit count would be indistinguishable from a scanner that finds nothing at all.
/// </summary>
public sealed class RawAccessorBoundaryTests
{
    [Fact]
    public void Nothing_in_src_reads_the_full_spectrum_accessor_outside_the_session_type()
    {
        string src = TestPaths.SourceRoot;
        Assert.True(Directory.Exists(src), $"source root not found: {src}");

        var rawFiles = new List<string>();
        var rawCalls = 0;
        var bandCalls = 0;
        foreach (string file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Replace('\\', '/').Contains("/obj/", StringComparison.Ordinal) || file.Replace('\\', '/').Contains("/bin/", StringComparison.Ordinal)) continue;
            string text = File.ReadAllText(file);
            int raw = Occurrences(text, "FrequencyResponseOf(");
            bandCalls += Occurrences(text, "InBandResponseOf(");
            if (raw == 0) continue;
            rawCalls += raw;
            if (!file.EndsWith("MeasurementSession.cs", StringComparison.Ordinal)) rawFiles.Add($"{file} ({raw})");
        }

        // The session type itself is where the raw accessor lives and is allowed to exist; nothing else may call it.
        Assert.Empty(rawFiles);
        // Positive control: the same scanner does find the band-limited name, so the zero above is absence, not a
        // broken scan. Measured 7 when this test was written.
        Assert.True(bandCalls > 0, "the scanner found no band-limited calls at all — it is not scanning");
    }

    private static int Occurrences(string text, string name)
    {
        int count = 0;
        int index = text.IndexOf(name, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = text.IndexOf(name, index + name.Length, StringComparison.Ordinal);
        }

        return count;
    }
}
