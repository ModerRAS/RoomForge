namespace AudioOptimizer.Art.Tests;

using System.Text.Json;

public class CamillaExportTests
{
    [Fact]
    public void Package_is_parseable_and_bypass_flags_are_independent()
    {
        string root = Path.Combine(Path.GetTempPath(), "roomforge-art-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (CorrectionBypass bypass in CorrectionBypass.All)
            {
                string dest = Path.Combine(root, $"p{Bool(bypass.Phase)}-s{Bool(bypass.Support)}");
                CamillaExporter.Export(dest, Request(bypass.Phase, bypass.Support));

                string yaml = File.ReadAllText(Path.Combine(dest, "camilla.yml"));
                using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(dest, "manifest.json")));
                JsonElement rootElement = manifest.RootElement;
                Assert.Equal("1", rootElement.GetProperty("export_version").GetString());
                Assert.Equal("3", rootElement.GetProperty("camilla_major").GetString());
                Assert.Equal(bypass.Phase, rootElement.GetProperty("phase_bypass").GetBoolean());
                Assert.Equal(bypass.Support, rootElement.GetProperty("support_bypass").GetBoolean());
                Assert.Contains("mixers:", yaml, StringComparison.Ordinal);
                Assert.Contains("pipeline:", yaml, StringComparison.Ordinal);
                Assert.Contains($"roomforge_phase_bypass: {Bool(bypass.Phase)}", yaml, StringComparison.Ordinal);
                Assert.Contains($"roomforge_support_bypass: {Bool(bypass.Support)}", yaml, StringComparison.Ordinal);

                bool phaseFile = File.Exists(Path.Combine(dest, "fir", "phase_L.wav"));
                bool supportFile = File.Exists(Path.Combine(dest, "fir", "support_R.wav"));
                Assert.Equal(!bypass.Phase, phaseFile);
                Assert.Equal(!bypass.Support, supportFile);
                Assert.Equal(!bypass.Phase, yaml.Contains("fir/phase_L.wav", StringComparison.Ordinal));
                Assert.Equal(!bypass.Support, yaml.Contains("fir/support_R.wav", StringComparison.Ordinal));
                if (!bypass.Support)
                {
                    Assert.Contains("type: Biquad", yaml, StringComparison.Ordinal);
                    Assert.Contains("type: Conv", yaml, StringComparison.Ordinal);
                    Assert.Contains("support_level:", yaml, StringComparison.Ordinal);
                }

                Assert.Contains("CamillaDSP", File.ReadAllText(Path.Combine(dest, "README.txt")), StringComparison.Ordinal);
                Assert.Contains("相位", File.ReadAllText(Path.Combine(dest, "README.txt")), StringComparison.Ordinal);

                JsonElement channels = rootElement.GetProperty("channels");
                Assert.Equal(2, channels.GetArrayLength());
                Assert.Equal("L", channels[0].GetProperty("logical_name").GetString());
                Assert.Equal("Primary", channels[0].GetProperty("role").GetString());
                Assert.Equal(0, channels[0].GetProperty("camilla_index").GetInt32());
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Fir_wav_round_trips_without_clamping()
    {
        string dest = Path.Combine(Path.GetTempPath(), "roomforge-fir-" + Guid.NewGuid().ToString("N"));
        double[] phase = [0.25, -1.5, 0.5];
        double[] support = [0.1, 2.25, -0.3, 0.05];
        try
        {
            CamillaExporter.Export(dest, Request(phaseBypass: false, supportBypass: false, phase, support));
            (double[] phaseSamples, int phaseRate) = CamillaFirFile.Read(Path.Combine(dest, "fir", "phase_L.wav"));
            (double[] supportSamples, int supportRate) = CamillaFirFile.Read(Path.Combine(dest, "fir", "support_R.wav"));
            Assert.Equal(48000, phaseRate);
            Assert.Equal(48000, supportRate);
            AssertClose(phase, phaseSamples);
            AssertClose(support, supportSamples);

            using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(dest, "manifest.json")));
            JsonElement fir = manifest.RootElement.GetProperty("firs")[0];
            Assert.Equal(64, fir.GetProperty("sha256").GetString()!.Length);
            Assert.Equal(phase.Length, fir.GetProperty("length").GetInt32());
        }
        finally
        {
            if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
        }
    }

    [Fact]
    public void Validation_failure_does_not_write_a_package()
    {
        string parent = Path.Combine(Path.GetTempPath(), "roomforge-fail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        string dest = Path.Combine(parent, "export");
        Directory.CreateDirectory(dest);
        File.WriteAllText(Path.Combine(dest, "keep.txt"), "keep");
        File.WriteAllText(Path.Combine(parent, "sentinel.txt"), "sentinel");
        try
        {
            CamillaExportRequest broken = Request(phaseBypass: false, supportBypass: false);
            broken = new CamillaExportRequest
            {
                PrimaryChannelId = broken.PrimaryChannelId,
                Channels = broken.Channels,
                PrimaryPhaseFir = [],
                SupportFirs = broken.SupportFirs,
                PhaseBypass = false,
                SupportBypass = false,
            };

            Assert.Throws<CamillaExportException>(() => CamillaExporter.Export(dest, broken));
            Assert.Equal("keep", File.ReadAllText(Path.Combine(dest, "keep.txt")));
            Assert.False(File.Exists(Path.Combine(dest, "camilla.yml")));
            Assert.Equal("sentinel", File.ReadAllText(Path.Combine(parent, "sentinel.txt")));

            CamillaExportRequest duplicate = Request(false, false);
            duplicate = new CamillaExportRequest
            {
                PrimaryChannelId = "L",
                Channels =
                [
                    new CamillaChannelMap { LogicalName = "L", Role = SpeakerRole.Primary, CamillaIndex = 0 },
                    new CamillaChannelMap { LogicalName = "R", Role = SpeakerRole.Support, CamillaIndex = 0 },
                ],
                PrimaryPhaseFir = [1],
                SupportFirs = new Dictionary<string, double[]> { ["R"] = [1] },
            };
            Assert.Throws<CamillaExportException>(() => CamillaExporter.Export(dest, duplicate));
            Assert.False(File.Exists(Path.Combine(dest, "manifest.json")));
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    static CamillaExportRequest Request(bool phaseBypass, bool supportBypass, double[]? phase = null, double[]? support = null)
        => new()
        {
            SampleRate = 48000,
            PrimaryChannelId = "L",
            PhaseBypass = phaseBypass,
            SupportBypass = supportBypass,
            BandLowHz = 20,
            BandHighHz = 150,
            SupportLevelDb = -6,
            PrimaryPhaseFir = phase ?? [1, 0, 0],
            SupportFirs = new Dictionary<string, double[]> { ["R"] = support ?? [0.2, 0.1] },
            Channels =
            [
                new CamillaChannelMap { LogicalName = "L", Role = SpeakerRole.Primary, CamillaIndex = 0, HardwareOutputIndex = 0 },
                new CamillaChannelMap { LogicalName = "R", Role = SpeakerRole.Support, CamillaIndex = 1, HardwareOutputIndex = 1 },
            ],
        };

    static string Bool(bool value) => value ? "true" : "false";

    static void AssertClose(double[] expected, double[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
            Assert.InRange(actual[i], expected[i] - 1e-5, expected[i] + 1e-5);
    }
}
