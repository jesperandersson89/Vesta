using System.Text.Json;
using VestaCore.Events;
using VestaCore.Serialization;

namespace VestaCore.Tests.Serialization;

public class VestaEventSerializationTests
{
    private static readonly JsonSerializerOptions Options = VestaJsonOptions.Default;

    private static VestaEvent CreateSampleEvent()
    {
        JsonElement payload = JsonDocument.Parse("""{"title":"Buy milk","done":false}""").RootElement;

        return new VestaEvent(
            Id: Guid.Parse("01961a3e-7c5d-7f8a-b1c2-d3e4f5a6b7c8"),
            ChannelId: "my-todo-list",
            Timestamp: DateTimeOffset.Parse("2026-05-29T12:00:00Z"),
            ClientId: "Xk9mQ2pLvN3wR8tY5uA7bC",
            EventType: "app.todo.item-added",
            Payload: payload,
            ParentId: null,
            Signature: "dGVzdC1zaWduYXR1cmU"
        );
    }

    [Fact]
    public void Serialize_VestaEvent_ProducesCamelCaseJson()
    {
        VestaEvent evt = CreateSampleEvent();

        string json = JsonSerializer.Serialize(evt, Options);

        Assert.Contains("\"id\":", json);
        Assert.Contains("\"channelId\":\"my-todo-list\"", json);
        Assert.Contains("\"clientId\":\"Xk9mQ2pLvN3wR8tY5uA7bC\"", json);
        Assert.Contains("\"eventType\":\"app.todo.item-added\"", json);
        Assert.Contains("\"payload\":{\"title\":\"Buy milk\",\"done\":false}", json);
        Assert.Contains("\"signature\":\"dGVzdC1zaWduYXR1cmU\"", json);
    }

    [Fact]
    public void Serialize_VestaEvent_OmitsNullParentId()
    {
        VestaEvent evt = CreateSampleEvent();

        string json = JsonSerializer.Serialize(evt, Options);

        Assert.DoesNotContain("\"parentId\"", json);
    }

    [Fact]
    public void Serialize_VestaEvent_IncludesParentIdWhenSet()
    {
        Guid parentId = Guid.Parse("01961a3e-0000-0000-0000-000000000001");
        VestaEvent evt = CreateSampleEvent() with { ParentId = parentId };

        string json = JsonSerializer.Serialize(evt, Options);

        Assert.Contains($"\"parentId\":\"{parentId}\"", json);
    }

    [Fact]
    public void RoundTrip_VestaEvent_PreservesAllFields()
    {
        VestaEvent original = CreateSampleEvent();

        string json = JsonSerializer.Serialize(original, Options);
        VestaEvent? deserialized = JsonSerializer.Deserialize<VestaEvent>(json, Options);

        Assert.NotNull(deserialized);
        Assert.Equal(original.Id, deserialized.Id);
        Assert.Equal(original.ChannelId, deserialized.ChannelId);
        Assert.Equal(original.Timestamp, deserialized.Timestamp);
        Assert.Equal(original.ClientId, deserialized.ClientId);
        Assert.Equal(original.EventType, deserialized.EventType);
        Assert.Equal(original.Payload.GetRawText(), deserialized.Payload.GetRawText());
        Assert.Equal(original.ParentId, deserialized.ParentId);
        Assert.Equal(original.Signature, deserialized.Signature);
    }

    [Fact]
    public void RoundTrip_VestaEvent_WithNullOptionalFields()
    {
        VestaEvent original = CreateSampleEvent() with { ParentId = null, Signature = null };

        string json = JsonSerializer.Serialize(original, Options);
        VestaEvent? deserialized = JsonSerializer.Deserialize<VestaEvent>(json, Options);

        Assert.NotNull(deserialized);
        Assert.Null(deserialized.ParentId);
        Assert.Null(deserialized.Signature);
    }

    [Fact]
    public void Deserialize_VestaEvent_FromExternalJson()
    {
        // Simulates JSON produced by a JS or Python client
        string json = """
        {
            "id": "01961a3e-7c5d-7f8a-b1c2-d3e4f5a6b7c8",
            "channelId": "my-todo-list",
            "timestamp": "2026-05-29T12:00:00+00:00",
            "clientId": "Xk9mQ2pLvN3wR8tY5uA7bC",
            "eventType": "app.todo.item-added",
            "payload": {"title": "Buy milk", "done": false}
        }
        """;

        VestaEvent? evt = JsonSerializer.Deserialize<VestaEvent>(json, Options);

        Assert.NotNull(evt);
        Assert.Equal("my-todo-list", evt.ChannelId);
        Assert.Equal("app.todo.item-added", evt.EventType);
        Assert.Null(evt.ParentId);
        Assert.Null(evt.Signature);
    }
}
