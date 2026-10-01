namespace AudioOptimizer.Camilla;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AudioOptimizer.Art;

/// <summary>Logical channel ↔ Camilla index. Indices must be unique or export fails closed.</summary>
public sealed class CamillaChannelMap
{
    public required string LogicalName { get; init; }

    public required SpeakerRole Role { get; init; }

    public required int CamillaIndex { get; init; }

    public int? HardwareOutputIndex { get; init; }
}

public sealed class CamillaExportRequest
{
    public const string CurrentExportVersion = "1";

    public string ExportVersion { get; init; } = CurrentExportVersion;

    public string CamillaMajorHint { get; init; } = "3";

    public int SampleRate { get; init; } = 48000;

    public required string PrimaryChannelId { get; init; }

    public required IReadOnlyList<CamillaChannelMap> Channels { get; init; }

    public double[]? PrimaryPhaseFir { get; init; }

    public string PhaseCalVersion { get; init; } = "1";

    public IReadOnlyDictionary<string, double[]> SupportFirs { get; init; }
        = new Dictionary<string, double[]>();

    public double BandLowHz { get; init; } = 20;

    public double BandHighHz { get; init; } = 150;

    public double SupportLevelDb { get; init; } = -6;

    public bool PhaseBypass { get; init; }

    public bool SupportBypass { get; init; }

    public bool IncludeChineseReadme { get; init; } = true;

    public string OptimizerVersion { get; init; } = "art-p0";
}

public sealed class CamillaExportException : Exception
{
    public IReadOnlyList<string> Errors { get; }

    public CamillaExportException(IReadOnlyList<string> errors)
        : base(errors.Count == 0 ? "Camilla export failed validation." : string.Join("; ", errors))
    {
        Errors = errors.Count == 0 ? ["Camilla export failed validation."] : errors;
    }
}

/// <summary>
/// Writes a CamillaDSP package (YAML + FIR WAV + manifest, optional Chinese README).
/// Validation runs before the destination is touched. A failed package is deleted from the temp
/// directory and is not moved into place.
/// </summary>
public static class CamillaExporter
{
    public static void Export(string destination, CamillaExportRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        ArgumentNullException.ThrowIfNull(request);

        IReadOnlyList<string> errors = CamillaExportValidator.ValidateRequest(request);
        if (errors.Count > 0) throw new CamillaExportException(errors);

        string fullDest = Path.GetFullPath(destination);
        string? parent = Path.GetDirectoryName(fullDest);
        if (string.IsNullOrEmpty(parent)) throw new CamillaExportException(["Export destination has no parent directory."]);
        Directory.CreateDirectory(parent);

        string temp = Path.Combine(parent, ".rf-export-" + Guid.NewGuid().ToString("N"));
        try
        {
            WritePackage(temp, request);
            IReadOnlyList<string> written = CamillaExportValidator.ValidatePackage(temp, request);
            if (written.Count > 0) throw new CamillaExportException(written);
            ReplaceDestination(fullDest, temp);
            temp = "";
        }
        catch
        {
            if (!string.IsNullOrEmpty(temp) && Directory.Exists(temp))
                Directory.Delete(temp, recursive: true);
            throw;
        }
    }

    static void ReplaceDestination(string fullDest, string temp)
    {
        if (!Directory.Exists(fullDest))
        {
            Directory.Move(temp, fullDest);
            return;
        }

        string backup = fullDest + ".replacing-" + Guid.NewGuid().ToString("N");
        Directory.Move(fullDest, backup);
        try
        {
            Directory.Move(temp, fullDest);
            Directory.Delete(backup, recursive: true);
        }
        catch
        {
            if (!Directory.Exists(fullDest) && Directory.Exists(backup))
                Directory.Move(backup, fullDest);
            throw;
        }
    }

    static void WritePackage(string directory, CamillaExportRequest request)
    {
        Directory.CreateDirectory(directory);
        string firDir = Path.Combine(directory, "fir");
        Directory.CreateDirectory(firDir);

        var firs = new List<CamillaFirRecord>();
        if (!request.PhaseBypass)
        {
            string relative = $"fir/phase_{request.PrimaryChannelId}.wav";
            string absolute = Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar));
            CamillaFirFile.Write(absolute, request.PrimaryPhaseFir!, request.SampleRate);
            firs.Add(Record(relative, "phase", request.PrimaryChannelId, request.PrimaryPhaseFir!, absolute));
        }

        if (!request.SupportBypass)
        {
            foreach (KeyValuePair<string, double[]> pair in request.SupportFirs.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                string relative = $"fir/support_{pair.Key}.wav";
                string absolute = Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar));
                CamillaFirFile.Write(absolute, pair.Value, request.SampleRate);
                firs.Add(Record(relative, "support", pair.Key, pair.Value, absolute));
            }
        }

        var manifest = new CamillaManifest
        {
            ExportVersion = request.ExportVersion,
            CamillaMajor = request.CamillaMajorHint,
            PhaseCalVersion = request.PhaseCalVersion,
            OptimizerVersion = request.OptimizerVersion,
            SampleRate = request.SampleRate,
            PhaseBypass = request.PhaseBypass,
            SupportBypass = request.SupportBypass,
            BandLowHz = request.BandLowHz,
            BandHighHz = request.BandHighHz,
            SupportLevelDb = request.SupportLevelDb,
            Channels = request.Channels
                .OrderBy(channel => channel.CamillaIndex)
                .Select(channel => new CamillaChannelRecord
                {
                    LogicalName = channel.LogicalName,
                    Role = channel.Role.ToString(),
                    CamillaIndex = channel.CamillaIndex,
                    HardwareOutputIndex = channel.HardwareOutputIndex,
                })
                .ToList(),
            Firs = firs,
        };

        File.WriteAllText(Path.Combine(directory, "camilla.yml"), CamillaYaml.Write(request));
        File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(manifest, ManifestJson));
        if (request.IncludeChineseReadme)
            File.WriteAllText(Path.Combine(directory, CamillaReadme.FileName), CamillaReadme.Chinese);
    }

    static CamillaFirRecord Record(string relative, string kind, string channel, double[] samples, string absolute)
        => new()
        {
            Path = relative,
            Kind = kind,
            Channel = channel,
            Length = samples.Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(absolute))).ToLowerInvariant(),
        };

    static readonly JsonSerializerOptions ManifestJson = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

public static class CamillaReadme
{
    public const string FileName = "README.txt";

    public const string Chinese =
        """
        RoomForge 后加校正包（CamillaDSP）
        这是可选的相位 / 支撑 FIR 回放包，不是双低音优化器的替代。

        加载步骤：
        1. 安装 CamillaDSP 3.x。本包不启动 Camilla，也不绑定声卡。
        2. 在本目录执行：camilladsp camilla.yml
        3. 打开 manifest.json，按 channels 把逻辑名接到播放设备索引。
        4. phase_bypass 与 support_bypass 彼此独立，可只关其中一个。
        5. FIR 在 fir/ 目录，采样率、export_version 与 sha256 见 manifest.json。
        6. 校验失败时 RoomForge 不会留下半份配置。
        """;
}

static class CamillaYaml
{
    public static string Write(CamillaExportRequest request)
    {
        var supports = request.SupportFirs.Keys.OrderBy(id => id, StringComparer.Ordinal).ToList();
        int buses = 1 + supports.Count;
        var builder = new StringBuilder();
        builder.AppendLine("---");
        builder.AppendLine($"# export_version: {request.ExportVersion}");
        builder.AppendLine($"# roomforge_phase_bypass: {request.PhaseBypass.ToString().ToLowerInvariant()}");
        builder.AppendLine($"# roomforge_support_bypass: {request.SupportBypass.ToString().ToLowerInvariant()}");
        builder.AppendLine("description: RoomForge extension export (phase + support FIR)");
        builder.AppendLine("# Playback devices are chosen on the Camilla host. Channel indices live in manifest.json.");
        builder.AppendLine("filters:");
        if (request.PhaseBypass)
            AppendGain(builder, $"phase_{request.PrimaryChannelId}", 0, mute: false);
        else
            AppendConv(builder, $"phase_{request.PrimaryChannelId}", $"fir/phase_{request.PrimaryChannelId}.wav");

        AppendBiquad(builder, "support_band_hp", "Highpass", request.BandLowHz);
        AppendBiquad(builder, "support_band_lp", "Lowpass", request.BandHighHz);
        AppendGain(builder, "support_level", request.SupportLevelDb, mute: false);
        foreach (string support in supports)
        {
            if (request.SupportBypass)
                AppendGain(builder, $"support_{support}", 0, mute: true);
            else
                AppendConv(builder, $"support_{support}", $"fir/support_{support}.wav");
        }

        builder.AppendLine("mixers:");
        builder.AppendLine("  fanout:");
        builder.AppendLine("    channels:");
        builder.AppendLine("      in: 1");
        builder.AppendLine($"      out: {buses}");
        builder.AppendLine("    mapping:");
        for (int bus = 0; bus < buses; bus++)
        {
            builder.AppendLine($"      - dest: {bus}");
            builder.AppendLine("        sources:");
            builder.AppendLine("          - channel: 0");
            builder.AppendLine("            gain: 0");
            builder.AppendLine("            inverted: false");
            builder.AppendLine("            mute: false");
        }

        builder.AppendLine("  sum:");
        builder.AppendLine("    channels:");
        builder.AppendLine($"      in: {buses}");
        builder.AppendLine("      out: 1");
        builder.AppendLine("    mapping:");
        builder.AppendLine("      - dest: 0");
        builder.AppendLine("        sources:");
        builder.AppendLine("          - channel: 0");
        builder.AppendLine("            gain: 0");
        builder.AppendLine("            inverted: false");
        builder.AppendLine("            mute: false");
        for (int s = 0; s < supports.Count; s++)
        {
            builder.AppendLine($"          - channel: {s + 1}");
            builder.AppendLine("            gain: 0");
            builder.AppendLine("            inverted: false");
            builder.AppendLine($"            mute: {request.SupportBypass.ToString().ToLowerInvariant()}");
        }

        builder.AppendLine("pipeline:");
        builder.AppendLine("  - type: Mixer");
        builder.AppendLine("    name: fanout");
        builder.AppendLine("  - type: Filter");
        builder.AppendLine("    channels: [0]");
        builder.AppendLine("    names:");
        builder.AppendLine($"      - phase_{request.PrimaryChannelId}");
        for (int s = 0; s < supports.Count; s++)
        {
            builder.AppendLine("  - type: Filter");
            builder.AppendLine($"    channels: [{s + 1}]");
            builder.AppendLine("    names:");
            if (request.SupportBypass)
            {
                builder.AppendLine($"      - support_{supports[s]}");
            }
            else
            {
                builder.AppendLine("      - support_band_hp");
                builder.AppendLine("      - support_band_lp");
                builder.AppendLine("      - support_level");
                builder.AppendLine($"      - support_{supports[s]}");
            }
        }

        builder.AppendLine("  - type: Mixer");
        builder.AppendLine("    name: sum");
        return builder.ToString();
    }

    static void AppendConv(StringBuilder builder, string name, string filename)
    {
        builder.AppendLine($"  {name}:");
        builder.AppendLine("    type: Conv");
        builder.AppendLine("    parameters:");
        builder.AppendLine("      type: Wav");
        builder.AppendLine($"      filename: {filename}");
    }

    static void AppendGain(StringBuilder builder, string name, double gain, bool mute)
    {
        builder.AppendLine($"  {name}:");
        builder.AppendLine("    type: Gain");
        builder.AppendLine("    parameters:");
        builder.AppendLine($"      gain: {gain.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}");
        builder.AppendLine("      inverted: false");
        builder.AppendLine($"      mute: {mute.ToString().ToLowerInvariant()}");
    }

    static void AppendBiquad(StringBuilder builder, string name, string kind, double frequency)
    {
        builder.AppendLine($"  {name}:");
        builder.AppendLine("    type: Biquad");
        builder.AppendLine("    parameters:");
        builder.AppendLine($"      type: {kind}");
        builder.AppendLine($"      freq: {frequency.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}");
        builder.AppendLine("      q: 0.707");
    }
}

sealed class CamillaManifest
{
    [JsonPropertyName("export_version")]
    public string ExportVersion { get; set; } = "";

    [JsonPropertyName("camilla_major")]
    public string CamillaMajor { get; set; } = "";

    [JsonPropertyName("phase_cal_version")]
    public string PhaseCalVersion { get; set; } = "";

    [JsonPropertyName("optimizer_version")]
    public string OptimizerVersion { get; set; } = "";

    [JsonPropertyName("sample_rate")]
    public int SampleRate { get; set; }

    [JsonPropertyName("phase_bypass")]
    public bool PhaseBypass { get; set; }

    [JsonPropertyName("support_bypass")]
    public bool SupportBypass { get; set; }

    [JsonPropertyName("band_low_hz")]
    public double BandLowHz { get; set; }

    [JsonPropertyName("band_high_hz")]
    public double BandHighHz { get; set; }

    [JsonPropertyName("support_level_db")]
    public double SupportLevelDb { get; set; }

    [JsonPropertyName("channels")]
    public List<CamillaChannelRecord> Channels { get; set; } = [];

    [JsonPropertyName("firs")]
    public List<CamillaFirRecord> Firs { get; set; } = [];
}

sealed class CamillaChannelRecord
{
    [JsonPropertyName("logical_name")]
    public string LogicalName { get; set; } = "";

    [JsonPropertyName("role")]
    public string Role { get; set; } = "";

    [JsonPropertyName("camilla_index")]
    public int CamillaIndex { get; set; }

    [JsonPropertyName("hardware_output_index")]
    public int? HardwareOutputIndex { get; set; }
}

sealed class CamillaFirRecord
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    [JsonPropertyName("channel")]
    public string Channel { get; set; } = "";

    [JsonPropertyName("length")]
    public int Length { get; set; }

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";
}

public static class CamillaExportValidator
{
    public static IReadOnlyList<string> ValidateRequest(CamillaExportRequest request)
    {
        var errors = new List<string>();
        if (request.ExportVersion != CamillaExportRequest.CurrentExportVersion)
            errors.Add($"export_version '{request.ExportVersion}' is not {CamillaExportRequest.CurrentExportVersion}.");
        if (request.SampleRate <= 0) errors.Add("Sample rate must be positive.");
        if (string.IsNullOrWhiteSpace(request.PrimaryChannelId) || !IsToken(request.PrimaryChannelId))
            errors.Add("Primary channel id must be a simple token.");
        if (!(request.BandLowHz >= 0) || !(request.BandHighHz > request.BandLowHz) || request.BandHighHz >= request.SampleRate / 2.0)
            errors.Add("Band limits are not inside Nyquist.");
        if (!double.IsFinite(request.SupportLevelDb)) errors.Add("Support level must be finite.");
        if (request.Channels is null || request.Channels.Count == 0)
            errors.Add("Channel map is empty.");

        try
        {
            PrimaryPathPolicy.EnsurePhaseFir(request.PhaseBypass, request.PrimaryPhaseFir);
        }
        catch (ArtValidationException exception)
        {
            errors.Add(exception.Message);
        }

        if (!request.PhaseBypass && request.PrimaryPhaseFir!.Any(sample => !double.IsFinite(sample)))
            errors.Add("Primary phase FIR contains a non-finite sample.");

        var indices = new HashSet<int>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        int primaries = 0;
        if (request.Channels is not null)
        {
            foreach (CamillaChannelMap channel in request.Channels)
            {
                if (channel is null || !IsToken(channel.LogicalName))
                {
                    errors.Add("A channel map entry has a missing or unsafe logical name.");
                    continue;
                }

                if (!names.Add(channel.LogicalName)) errors.Add($"Duplicate logical name '{channel.LogicalName}'.");
                if (channel.CamillaIndex < 0) errors.Add($"Channel '{channel.LogicalName}' has a negative Camilla index.");
                else if (!indices.Add(channel.CamillaIndex)) errors.Add($"Duplicate Camilla index {channel.CamillaIndex}.");
                if (channel.Role == SpeakerRole.Primary)
                {
                    primaries++;
                    if (channel.LogicalName != request.PrimaryChannelId)
                        errors.Add("Primary channel id does not match the primary channel map entry.");
                }
            }
        }

        if (primaries != 1) errors.Add("Channel map needs exactly one primary.");

        if (request.SupportFirs is null || request.SupportFirs.Count < 1)
            errors.Add("At least one support FIR entry is required.");
        else
        {
            foreach (KeyValuePair<string, double[]> pair in request.SupportFirs)
            {
                if (!IsToken(pair.Key)) errors.Add($"Support id '{pair.Key}' is not a simple token.");
                if (pair.Key == request.PrimaryChannelId) errors.Add($"Primary '{pair.Key}' cannot also be a support.");
                if (pair.Value is null || pair.Value.Length == 0) errors.Add($"Support '{pair.Key}' FIR is empty.");
                else if (pair.Value.Any(sample => !double.IsFinite(sample))) errors.Add($"Support '{pair.Key}' FIR is non-finite.");
                if (request.Channels is not null && request.Channels.All(channel => channel.LogicalName != pair.Key || channel.Role != SpeakerRole.Support))
                    errors.Add($"Support '{pair.Key}' is missing from the channel map.");
            }
        }

        return errors;
    }

    public static IReadOnlyList<string> ValidatePackage(string directory, CamillaExportRequest request)
    {
        var errors = new List<string>();
        string yamlPath = Path.Combine(directory, "camilla.yml");
        string manifestPath = Path.Combine(directory, "manifest.json");
        if (!File.Exists(yamlPath)) errors.Add("camilla.yml was not written.");
        if (!File.Exists(manifestPath)) errors.Add("manifest.json was not written.");
        if (errors.Count > 0) return errors;

        string yaml = File.ReadAllText(yamlPath);
        if (!yaml.Contains("mixers:", StringComparison.Ordinal) || !yaml.Contains("pipeline:", StringComparison.Ordinal))
            errors.Add("camilla.yml is missing mixers or pipeline.");
        if (!yaml.Contains($"roomforge_phase_bypass: {request.PhaseBypass.ToString().ToLowerInvariant()}", StringComparison.Ordinal))
            errors.Add("camilla.yml phase bypass flag does not match the request.");
        if (!yaml.Contains($"roomforge_support_bypass: {request.SupportBypass.ToString().ToLowerInvariant()}", StringComparison.Ordinal))
            errors.Add("camilla.yml support bypass flag does not match the request.");
        if (!request.PhaseBypass && !yaml.Contains($"fir/phase_{request.PrimaryChannelId}.wav", StringComparison.Ordinal))
            errors.Add("camilla.yml does not reference the primary phase FIR.");
        if (request.PhaseBypass && yaml.Contains($"fir/phase_{request.PrimaryChannelId}.wav", StringComparison.Ordinal))
            errors.Add("Phase-bypass YAML still references a phase FIR.");
        foreach (string support in request.SupportFirs.Keys)
        {
            string needle = $"fir/support_{support}.wav";
            bool mentioned = yaml.Contains(needle, StringComparison.Ordinal);
            if (request.SupportBypass && mentioned) errors.Add($"Support-bypass YAML still references {needle}.");
            if (!request.SupportBypass && !mentioned) errors.Add($"camilla.yml does not reference {needle}.");
        }

        if (!request.SupportBypass)
        {
            if (!yaml.Contains("type: Biquad", StringComparison.Ordinal)) errors.Add("camilla.yml is missing the band-limit biquad.");
            if (!yaml.Contains("support_level:", StringComparison.Ordinal)) errors.Add("camilla.yml is missing the support gain.");
            if (!yaml.Contains("type: Conv", StringComparison.Ordinal)) errors.Add("camilla.yml is missing a Conv filter.");
        }

        CamillaManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<CamillaManifest>(File.ReadAllText(manifestPath), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
        }
        catch (JsonException exception)
        {
            errors.Add($"manifest.json is not readable: {exception.Message}");
            return errors;
        }

        if (manifest is null || manifest.ExportVersion != request.ExportVersion)
            errors.Add("manifest export_version does not match the request.");
        if (manifest is null) return errors;
        if (manifest.PhaseBypass != request.PhaseBypass || manifest.SupportBypass != request.SupportBypass)
            errors.Add("manifest bypass flags do not match the request.");
        if (manifest.Channels.Count != request.Channels.Count)
            errors.Add("manifest channel map length does not match the request.");

        var expected = new HashSet<string>(StringComparer.Ordinal);
        if (!request.PhaseBypass) expected.Add($"fir/phase_{request.PrimaryChannelId}.wav");
        if (!request.SupportBypass)
            foreach (string support in request.SupportFirs.Keys) expected.Add($"fir/support_{support}.wav");
        if (manifest.Firs.Count != expected.Count) errors.Add("manifest FIR list does not match the bypass flags.");

        foreach (CamillaFirRecord fir in manifest.Firs)
        {
            if (string.IsNullOrWhiteSpace(fir.Path) || fir.Path.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(fir.Path))
            {
                errors.Add("manifest FIR path is not a relative path inside the package.");
                continue;
            }

            string absolute = Path.GetFullPath(Path.Combine(directory, fir.Path.Replace('/', Path.DirectorySeparatorChar)));
            string root = Path.GetFullPath(directory + Path.DirectorySeparatorChar);
            if (!absolute.StartsWith(root, StringComparison.Ordinal))
            {
                errors.Add($"FIR path '{fir.Path}' escapes the package.");
                continue;
            }

            if (!File.Exists(absolute))
            {
                errors.Add($"FIR '{fir.Path}' is missing.");
                continue;
            }

            string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(absolute))).ToLowerInvariant();
            if (!string.Equals(hash, fir.Sha256, StringComparison.Ordinal))
                errors.Add($"FIR '{fir.Path}' hash does not match the manifest.");
            try
            {
                (double[] samples, int rate) = CamillaFirFile.Read(absolute);
                if (rate != request.SampleRate) errors.Add($"FIR '{fir.Path}' sample rate is {rate}.");
                if (samples.Length != fir.Length) errors.Add($"FIR '{fir.Path}' length does not match the manifest.");
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException)
            {
                errors.Add($"FIR '{fir.Path}' is not a readable WAV: {exception.Message}");
            }
        }

        if (request.IncludeChineseReadme)
        {
            string readmePath = Path.Combine(directory, CamillaReadme.FileName);
            if (!File.Exists(readmePath) || !File.ReadAllText(readmePath).Contains("CamillaDSP", StringComparison.Ordinal))
                errors.Add("Chinese README is missing.");
        }

        return errors;
    }

    static bool IsToken(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-');
}
