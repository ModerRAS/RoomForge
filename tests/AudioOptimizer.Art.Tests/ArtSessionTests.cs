namespace AudioOptimizer.Art.Tests;

public class ArtSessionTests
{
    [Fact]
    public void Version1_without_roles_reads_as_unassigned()
    {
        const string json = """
            {
              "version": 1,
              "sessionId": "legacy",
              "sampleRate": 48000,
              "channels": [
                { "channelId": "A" },
                { "channelId": "B", "hardwareOutputIndex": 1 }
              ]
            }
            """;

        ArtSessionDocument document = ArtSessionSerializer.Deserialize(json);

        Assert.Equal(1, document.Version);
        Assert.Equal("legacy", document.SessionId);
        Assert.Equal(SpeakerRole.Unassigned, document.Channels[0].Role);
        Assert.Equal(SpeakerRole.Unassigned, document.Channels[1].Role);
        Assert.Equal(1, document.Channels[1].HardwareOutputIndex);
    }

    [Fact]
    public void Missing_version_is_treated_as_version_1()
    {
        const string json = """{ "channels": [ { "channelId": "L" } ] }""";
        ArtSessionDocument document = ArtSessionSerializer.Deserialize(json);
        Assert.Equal(1, document.Version);
        Assert.Equal(SpeakerRole.Unassigned, document.Channels[0].Role);
    }

    [Fact]
    public void Round_trip_writes_current_version_and_roles()
    {
        var document = new ArtSessionDocument
        {
            SessionId = "art-1",
            SampleRate = 48000,
            Channels =
            [
                new ArtChannelConfig { ChannelId = "L", Role = SpeakerRole.Primary, HardwareOutputIndex = 0 },
                new ArtChannelConfig { ChannelId = "R", Role = SpeakerRole.Support, HardwareOutputIndex = 1 },
                new ArtChannelConfig { ChannelId = "C", Role = SpeakerRole.Unassigned },
            ],
        };

        ArtSessionDocument loaded = ArtSessionSerializer.Deserialize(ArtSessionSerializer.Serialize(document));

        Assert.Equal(ArtSessionDocument.CurrentVersion, loaded.Version);
        Assert.Equal(SpeakerRole.Primary, loaded.Channels[0].Role);
        Assert.Equal(SpeakerRole.Support, loaded.Channels[1].Role);
        Assert.Equal(SpeakerRole.Unassigned, loaded.Channels[2].Role);
        Assert.Equal(1, loaded.Channels[1].HardwareOutputIndex);
    }

    [Fact]
    public void Numeric_role_and_future_version()
    {
        ArtSessionDocument numbered = ArtSessionSerializer.Deserialize(
            """{ "version": 1, "channels": [ { "channelId": "L", "role": 1 } ] }""");
        Assert.Equal(SpeakerRole.Primary, numbered.Channels[0].Role);

        ArtValidationException rejected = Assert.Throws<ArtValidationException>(() =>
            ArtSessionSerializer.Deserialize("""{ "version": 99, "channels": [] }"""));
        Assert.Contains("99", rejected.Message);
    }
}
