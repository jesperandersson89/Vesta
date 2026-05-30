using System.Text.Json;
using VestaCore.Events;
using VestaCore.Serialization;

namespace VestaCore.Tests.Serialization;

public class SequencedEventSerializationTests
{
    private static readonly JsonSerializerOptions Options = VestaJsonOptions.Default;

    private static SequencedEvent CreateSampleSequencedEvent()
    {
        JsonElement payload = JsonDocument.Parse("""{"title":"Buy milk","done":false}""").RootElement;

        VestaEvent innerEvent = new(
            Id: Guid.Parse("01961a3e-7c5d-7f8a-b1c2-d3e4f5a6b7c8"),
            ChannelId: "my-todo-list",
            Timestamp: DateTimeOffset.Parse("2026-05-29T12:00:00Z"),
            ClientId: "Xk9mQ2pLvN3wR8tY5uA7bC",
            EventType: "app.todo.item-added",
            Payload: payload,
            ParentId: null,
            Signature: "dGVzdC1zaWduYXR1cmU"
        );

        return new SequencedEvent(
            Event: innerEvent,
            Sequence: 42,
            ReceivedAt: DateTimeOffset.Parse("2026-05-29T12:00:00.123Z")
        );
    }

    [Fact]
    public void Serialize_SequencedEvent_WrapsEventCorrectly()
    {
        SequencedEvent sequenced = CreateSampleSequencedEvent();

        string json = JsonSerializer.Serialize(sequenced, Options);

        Assert.Contains("\"event\":", json);
        Assert.Contains("\"sequence\":42", json);
        Assert.Contains("\"receivedAt\":", json);
        // The inner event should be nested under "event"
        Assert.Contains("\"channelId\":\"my-todo-list\"", json);
    }

    [Fact]
    public void RoundTrip_SequencedEvent_PreservesAllFields()
    {
        SequencedEvent original = CreateSampleSequencedEvent();

        string json = JsonSerializer.Serialize(original, Options);
        SequencedEvent? deserialized = JsonSerializer.Deserialize<SequencedEvent>(json, Options);

        Assert.NotNull(deserialized);
        Assert.Equal(original.Sequence, deserialized.Sequence);
        Assert.Equal(original.ReceivedAt, deserialized.ReceivedAt);
        Assert.Equal(original.Event.Id, deserialized.Event.Id);
        Assert.Equal(original.Event.ChannelId, deserialized.Event.ChannelId);
        Assert.Equal(original.Event.ClientId, deserialized.Event.ClientId);
        Assert.Equal(original.Event.EventType, deserialized.Event.EventType);
        Assert.Equal(original.Event.Payload.GetRawText(), deserialized.Event.Payload.GetRawText());
        Assert.Equal(original.Event.Signature, deserialized.Event.Signature);
    }

    [Fact]
    public void Deserialize_SequencedEvent_FromExternalJson()
    {
        string json = """
        {
            "event": {
                "id": "01961a3e-7c5d-7f8a-b1c2-d3e4f5a6b7c8",
                "channelId": "my-todo-list",
                "timestamp": "2026-05-29T12:00:00+00:00",
                "clientId": "Xk9mQ2pLvN3wR8tY5uA7bC",
                "eventType": "app.todo.item-added",
                "payload": {"title": "Buy milk", "done": false},
                "signature": "dGVzdC1zaWduYXR1cmU"
            },
            "sequence": 42,
            "receivedAt": "2026-05-29T12:00:00.123+00:00"
        }
        """;

        SequencedEvent? sequenced = JsonSerializer.Deserialize<SequencedEvent>(json, Options);

        Assert.NotNull(sequenced);
        Assert.Equal(42, sequenced.Sequence);
        Assert.Equal("my-todo-list", sequenced.Event.ChannelId);
        Assert.Equal("app.todo.item-added", sequenced.Event.EventType);
    }
}
