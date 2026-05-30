using System.Text.Json;
using VestaCore.Events;
using VestaCore.Protocol;
using VestaCore.Serialization;

namespace VestaCore.Tests.Serialization;

public class ProtocolMessageSerializationTests
{
    private static readonly JsonSerializerOptions Options = VestaJsonOptions.Default;

    private static VestaEvent CreateSampleEvent()
    {
        JsonElement payload = JsonDocument.Parse("""{"text":"hello"}""").RootElement;

        return new VestaEvent(
            Id: Guid.Parse("01961a3e-7c5d-7f8a-b1c2-d3e4f5a6b7c8"),
            ChannelId: "chat/general",
            Timestamp: DateTimeOffset.Parse("2026-05-29T12:00:00Z"),
            ClientId: "Xk9mQ2pLvN3wR8tY5uA7bC",
            EventType: "app.chat.message",
            Payload: payload
        );
    }

    [Fact]
    public void Serialize_HelloMessage_IncludesTypeDiscriminator()
    {
        HelloMessage msg = new(
            ClientId: "Xk9mQ2pLvN3wR8tY5uA7bC",
            Channels: ["chat/general", "chat/random"],
            LastSequences: new Dictionary<string, long> { ["chat/general"] = 41 }
        );

        string json = JsonSerializer.Serialize<ProtocolMessage>(msg, Options);

        Assert.Contains("\"type\":\"HELLO\"", json);
        Assert.Contains("\"clientId\":\"Xk9mQ2pLvN3wR8tY5uA7bC\"", json);
        Assert.Contains("\"chat/general\"", json);
        Assert.Contains("\"chat/random\"", json);
    }

    [Fact]
    public void RoundTrip_HelloMessage_Polymorphic()
    {
        HelloMessage original = new(
            ClientId: "Xk9mQ2pLvN3wR8tY5uA7bC",
            Channels: ["chat/general"],
            LastSequences: new Dictionary<string, long> { ["chat/general"] = 10 }
        );

        string json = JsonSerializer.Serialize<ProtocolMessage>(original, Options);
        ProtocolMessage? deserialized = JsonSerializer.Deserialize<ProtocolMessage>(json, Options);

        HelloMessage result = Assert.IsType<HelloMessage>(deserialized);
        Assert.Equal(original.ClientId, result.ClientId);
        Assert.Equal(original.Channels, result.Channels);
        Assert.Equal(10L, result.LastSequences["chat/general"]);
    }

    [Fact]
    public void RoundTrip_PublishMessage_Polymorphic()
    {
        VestaEvent evt = CreateSampleEvent();
        PublishMessage original = new(ChannelId: "chat/general", Event: evt);

        string json = JsonSerializer.Serialize<ProtocolMessage>(original, Options);
        ProtocolMessage? deserialized = JsonSerializer.Deserialize<ProtocolMessage>(json, Options);

        PublishMessage result = Assert.IsType<PublishMessage>(deserialized);
        Assert.Equal("chat/general", result.ChannelId);
        Assert.Equal(evt.Id, result.Event.Id);
        Assert.Equal(evt.EventType, result.Event.EventType);
    }

    [Fact]
    public void RoundTrip_SubscribeMessage_Polymorphic()
    {
        SubscribeMessage original = new(ChannelId: "chat/general", FromSequence: 42);

        string json = JsonSerializer.Serialize<ProtocolMessage>(original, Options);
        ProtocolMessage? deserialized = JsonSerializer.Deserialize<ProtocolMessage>(json, Options);

        SubscribeMessage result = Assert.IsType<SubscribeMessage>(deserialized);
        Assert.Equal("chat/general", result.ChannelId);
        Assert.Equal(42L, result.FromSequence);
    }

    [Fact]
    public void RoundTrip_SubscribeMessage_NullFromSequence()
    {
        SubscribeMessage original = new(ChannelId: "chat/general");

        string json = JsonSerializer.Serialize<ProtocolMessage>(original, Options);
        ProtocolMessage? deserialized = JsonSerializer.Deserialize<ProtocolMessage>(json, Options);

        SubscribeMessage result = Assert.IsType<SubscribeMessage>(deserialized);
        Assert.Null(result.FromSequence);
    }

    [Fact]
    public void RoundTrip_UnsubscribeMessage_Polymorphic()
    {
        UnsubscribeMessage original = new(ChannelId: "chat/general");

        string json = JsonSerializer.Serialize<ProtocolMessage>(original, Options);
        ProtocolMessage? deserialized = JsonSerializer.Deserialize<ProtocolMessage>(json, Options);

        UnsubscribeMessage result = Assert.IsType<UnsubscribeMessage>(deserialized);
        Assert.Equal("chat/general", result.ChannelId);
    }

    [Fact]
    public void RoundTrip_FetchMessage_Polymorphic()
    {
        FetchMessage original = new(
            ChannelId: "chat/general",
            FromSequence: 10,
            ToSequence: 50,
            Limit: 25
        );

        string json = JsonSerializer.Serialize<ProtocolMessage>(original, Options);
        ProtocolMessage? deserialized = JsonSerializer.Deserialize<ProtocolMessage>(json, Options);

        FetchMessage result = Assert.IsType<FetchMessage>(deserialized);
        Assert.Equal("chat/general", result.ChannelId);
        Assert.Equal(10L, result.FromSequence);
        Assert.Equal(50L, result.ToSequence);
        Assert.Equal(25, result.Limit);
    }

    [Fact]
    public void RoundTrip_FetchMessage_OptionalFieldsNull()
    {
        FetchMessage original = new(ChannelId: "chat/general", FromSequence: 0);

        string json = JsonSerializer.Serialize<ProtocolMessage>(original, Options);
        ProtocolMessage? deserialized = JsonSerializer.Deserialize<ProtocolMessage>(json, Options);

        FetchMessage result = Assert.IsType<FetchMessage>(deserialized);
        Assert.Null(result.ToSequence);
        Assert.Null(result.Limit);
    }

    [Fact]
    public void RoundTrip_WelcomeMessage_Polymorphic()
    {
        WelcomeMessage original = new(
            ServerId: "server-01",
            Channels: ["chat/general", "chat/random"]
        );

        string json = JsonSerializer.Serialize<ProtocolMessage>(original, Options);
        ProtocolMessage? deserialized = JsonSerializer.Deserialize<ProtocolMessage>(json, Options);

        WelcomeMessage result = Assert.IsType<WelcomeMessage>(deserialized);
        Assert.Equal("server-01", result.ServerId);
        Assert.Equal(2, result.Channels.Count);
    }

    [Fact]
    public void RoundTrip_EventMessage_Polymorphic()
    {
        VestaEvent evt = CreateSampleEvent();
        EventMessage original = new(
            ChannelId: "chat/general",
            Event: evt,
            Sequence: 42,
            ReceivedAt: DateTimeOffset.Parse("2026-05-29T12:00:00.123Z")
        );

        string json = JsonSerializer.Serialize<ProtocolMessage>(original, Options);
        ProtocolMessage? deserialized = JsonSerializer.Deserialize<ProtocolMessage>(json, Options);

        EventMessage result = Assert.IsType<EventMessage>(deserialized);
        Assert.Equal("chat/general", result.ChannelId);
        Assert.Equal(42L, result.Sequence);
        Assert.Equal(evt.Id, result.Event.Id);
    }

    [Fact]
    public void RoundTrip_EventsBatchMessage_Polymorphic()
    {
        VestaEvent evt = CreateSampleEvent();
        SequencedEvent sequenced = new(evt, Sequence: 1, ReceivedAt: DateTimeOffset.UtcNow);
        EventsBatchMessage original = new(ChannelId: "chat/general", Events: [sequenced]);

        string json = JsonSerializer.Serialize<ProtocolMessage>(original, Options);
        ProtocolMessage? deserialized = JsonSerializer.Deserialize<ProtocolMessage>(json, Options);

        EventsBatchMessage result = Assert.IsType<EventsBatchMessage>(deserialized);
        Assert.Equal("chat/general", result.ChannelId);
        Assert.Single(result.Events);
        Assert.Equal(1L, result.Events[0].Sequence);
    }

    [Fact]
    public void RoundTrip_AckMessage_Polymorphic()
    {
        Guid eventId = Guid.Parse("01961a3e-7c5d-7f8a-b1c2-d3e4f5a6b7c8");
        AckMessage original = new(ChannelId: "chat/general", EventId: eventId, Sequence: 42);

        string json = JsonSerializer.Serialize<ProtocolMessage>(original, Options);
        ProtocolMessage? deserialized = JsonSerializer.Deserialize<ProtocolMessage>(json, Options);

        AckMessage result = Assert.IsType<AckMessage>(deserialized);
        Assert.Equal("chat/general", result.ChannelId);
        Assert.Equal(eventId, result.EventId);
        Assert.Equal(42L, result.Sequence);
    }

    [Fact]
    public void RoundTrip_ErrorMessage_Polymorphic()
    {
        ErrorMessage original = new(Code: "INVALID_CHANNEL", Message: "Channel ID format is invalid");

        string json = JsonSerializer.Serialize<ProtocolMessage>(original, Options);
        ProtocolMessage? deserialized = JsonSerializer.Deserialize<ProtocolMessage>(json, Options);

        ErrorMessage result = Assert.IsType<ErrorMessage>(deserialized);
        Assert.Equal("INVALID_CHANNEL", result.Code);
        Assert.Equal("Channel ID format is invalid", result.Message);
    }

    [Fact]
    public void Deserialize_UnknownMessageType_ThrowsOrReturnsNull()
    {
        string json = """{"type":"UNKNOWN","foo":"bar"}""";

        // System.Text.Json throws JsonException for unknown discriminator
        Assert.ThrowsAny<JsonException>(() =>
            JsonSerializer.Deserialize<ProtocolMessage>(json, Options));
    }

    [Fact]
    public void Serialize_TypeDiscriminator_IsUpperCase()
    {
        // Verify all message types produce uppercase discriminators
        ProtocolMessage[] messages =
        [
            new HelloMessage("c1", ["ch"], new Dictionary<string, long>()),
            new PublishMessage("ch", CreateSampleEvent()),
            new SubscribeMessage("ch"),
            new UnsubscribeMessage("ch"),
            new FetchMessage("ch", 0),
            new WelcomeMessage("s1", ["ch"]),
            new EventMessage("ch", CreateSampleEvent(), 1, DateTimeOffset.UtcNow),
            new EventsBatchMessage("ch", []),
            new AckMessage("ch", Guid.NewGuid(), 1),
            new ErrorMessage("ERR", "msg"),
        ];

        string[] expectedTypes =
            ["HELLO", "PUBLISH", "SUBSCRIBE", "UNSUBSCRIBE", "FETCH",
             "WELCOME", "EVENT", "EVENTS_BATCH", "ACK", "ERROR"];

        for (int i = 0; i < messages.Length; i++)
        {
            string json = JsonSerializer.Serialize<ProtocolMessage>(messages[i], Options);
            Assert.Contains($"\"type\":\"{expectedTypes[i]}\"", json);
        }
    }
}
