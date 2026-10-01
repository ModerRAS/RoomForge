namespace AudioOptimizer.Art;

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

/// <summary>
/// Reads and writes <see cref="ArtSessionDocument"/>. Documents older than
/// <see cref="ArtSessionDocument.CurrentVersion"/> stay readable: a missing role is
/// <see cref="SpeakerRole.Unassigned"/>. A newer version is refused so fields are not dropped.
/// </summary>
public static class ArtSessionSerializer
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string Serialize(ArtSessionDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var errors = new List<string>();
        ValidateChannels(document.Channels, errors);
        if (document.SampleRate <= 0) errors.Add("Sample rate must be positive.");
        if (errors.Count > 0) throw new ArtValidationException(errors);

        var payload = new ArtSessionDocument
        {
            Version = ArtSessionDocument.CurrentVersion,
            SessionId = document.SessionId ?? "",
            SampleRate = document.SampleRate,
            Channels = document.Channels,
        };
        return JsonSerializer.Serialize(payload, Json);
    }

    public static ArtSessionDocument Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new ArtValidationException($"Art session is not readable JSON: {exception.Message}");
        }

        if (root is not JsonObject obj)
            throw new ArtValidationException("Art session JSON must be an object.");

        int version = ArtSessionDocument.OldestReadableVersion;
        if (obj.TryGetPropertyValue("version", out JsonNode? versionNode) && versionNode is not null)
        {
            try
            {
                version = versionNode.GetValue<int>();
            }
            catch (Exception exception) when (exception is FormatException or InvalidOperationException)
            {
                throw new ArtValidationException("Art session version is not an integer.");
            }
        }

        if (version < ArtSessionDocument.OldestReadableVersion || version > ArtSessionDocument.CurrentVersion)
        {
            throw new ArtValidationException(
                $"Art session version {version} is outside the readable range "
                + $"{ArtSessionDocument.OldestReadableVersion}..{ArtSessionDocument.CurrentVersion}.");
        }

        int sampleRate = 48000;
        if (obj.TryGetPropertyValue("sampleRate", out JsonNode? rateNode) && rateNode is not null)
            sampleRate = rateNode.GetValue<int>();
        if (sampleRate <= 0) throw new ArtValidationException("Sample rate must be positive.");

        string sessionId = "";
        if (obj.TryGetPropertyValue("sessionId", out JsonNode? idNode) && idNode is not null)
            sessionId = idNode.GetValue<string>() ?? "";

        var channels = new List<ArtChannelConfig>();
        if (obj.TryGetPropertyValue("channels", out JsonNode? channelsNode) && channelsNode is not null)
        {
            if (channelsNode is not JsonArray array)
                throw new ArtValidationException("Art session channels must be an array.");

            foreach (JsonNode? entry in array)
            {
                if (entry is not JsonObject channel)
                    throw new ArtValidationException("Each art-session channel must be an object.");
                channels.Add(ReadChannel(channel));
            }
        }

        var errors = new List<string>();
        ValidateChannels(channels, errors);
        if (errors.Count > 0) throw new ArtValidationException(errors);

        return new ArtSessionDocument
        {
            Version = version,
            SessionId = sessionId,
            SampleRate = sampleRate,
            Channels = channels,
        };
    }

    private static ArtChannelConfig ReadChannel(JsonObject channel)
    {
        string channelId = channel["channelId"]?.GetValue<string>() ?? "";
        int? hardware = null;
        if (channel.TryGetPropertyValue("hardwareOutputIndex", out JsonNode? hardwareNode) && hardwareNode is not null)
            hardware = hardwareNode.GetValue<int>();

        return new ArtChannelConfig
        {
            ChannelId = channelId,
            Role = ReadRole(channel),
            HardwareOutputIndex = hardware,
        };
    }

    private static SpeakerRole ReadRole(JsonObject channel)
    {
        if (!channel.TryGetPropertyValue("role", out JsonNode? roleNode) || roleNode is null)
            return SpeakerRole.Unassigned;

        if (roleNode is not JsonValue value)
            throw new ArtValidationException("Speaker role must be a string or an integer.");

        if (value.TryGetValue<string>(out string? text))
        {
            if (string.IsNullOrWhiteSpace(text)) return SpeakerRole.Unassigned;
            if (!Enum.TryParse(text, ignoreCase: true, out SpeakerRole parsed) || !Enum.IsDefined(parsed))
                throw new ArtValidationException($"Unknown speaker role '{text}'.");
            return parsed;
        }

        if (value.TryGetValue<int>(out int code))
        {
            if (!Enum.IsDefined(typeof(SpeakerRole), code))
                throw new ArtValidationException($"Unknown speaker role code {code}.");
            return (SpeakerRole)code;
        }

        throw new ArtValidationException("Speaker role must be a string or an integer.");
    }

    private static void ValidateChannels(IReadOnlyList<ArtChannelConfig> channels, List<string> errors)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (ArtChannelConfig channel in channels)
        {
            if (channel is null)
            {
                errors.Add("A channel entry is missing.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(channel.ChannelId))
            {
                errors.Add("A channel is missing its id.");
                continue;
            }

            if (!seen.Add(channel.ChannelId))
                errors.Add($"Duplicate channel id '{channel.ChannelId}'.");
        }
    }
}
