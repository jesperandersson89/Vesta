using System.Text.Json;
using VestaCore.Events;
using VestaCore.Identity;
using VestaCore.Serialization;

namespace VestaCore.Tests.Events;

public class VestaEventMetadataTests
{
  private static VestaEvent CreateBase(VestaIdentity identity, JsonElement? metadata = null)
  {
    JsonElement payload = JsonSerializer.Deserialize<JsonElement>("""{"x":1}""");
    return new VestaEvent(
        Id: Guid.Parse("01961a3e-7c5d-7f8a-b1c2-d3e4f5a6b7c8"),
        ChannelId: "test/channel",
        Timestamp: new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero),
        ClientId: identity.ClientId,
        EventType: "app.test",
        Payload: payload,
        Metadata: metadata);
  }

  [Fact]
  public void SignEvent_IsIdenticalRegardlessOfMetadata()
  {
    using VestaIdentity identity = VestaIdentity.Generate();
    JsonElement md = JsonSerializer.Deserialize<JsonElement>("""{"ttlSeconds":30}""");

    VestaEvent withoutMeta = EventSigner.SignEvent(CreateBase(identity), identity);
    VestaEvent withMeta = EventSigner.SignEvent(CreateBase(identity, md), identity);

    Assert.Equal(withoutMeta.Signature, withMeta.Signature);
  }

  [Fact]
  public void Serialize_OmitsMetadataWhenNull()
  {
    using VestaIdentity identity = VestaIdentity.Generate();
    VestaEvent evt = CreateBase(identity);

    string json = JsonSerializer.Serialize(evt, VestaJsonOptions.Default);

    Assert.DoesNotContain("\"metadata\"", json);
  }

  [Fact]
  public void Serialize_IncludesMetadataWhenSet()
  {
    using VestaIdentity identity = VestaIdentity.Generate();
    JsonElement md = JsonSerializer.Deserialize<JsonElement>("""{"ttlSeconds":42}""");
    VestaEvent evt = CreateBase(identity, md);

    string json = JsonSerializer.Serialize(evt, VestaJsonOptions.Default);

    Assert.Contains("\"metadata\":{\"ttlSeconds\":42}", json);
  }

  [Fact]
  public void RoundTrip_PreservesMetadata()
  {
    using VestaIdentity identity = VestaIdentity.Generate();
    JsonElement md = JsonSerializer.Deserialize<JsonElement>("""{"ttlSeconds":15,"foo":"bar"}""");
    VestaEvent evt = CreateBase(identity, md);

    string json = JsonSerializer.Serialize(evt, VestaJsonOptions.Default);
    VestaEvent? parsed = JsonSerializer.Deserialize<VestaEvent>(json, VestaJsonOptions.Default);

    Assert.NotNull(parsed);
    Assert.NotNull(parsed!.Metadata);
    Assert.True(VestaEventMetadata.TryGetTtlSeconds(parsed, out int ttl));
    Assert.Equal(15, ttl);
  }

  [Fact]
  public void TryGetTtlSeconds_ReturnsFalseWhenAbsent()
  {
    using VestaIdentity identity = VestaIdentity.Generate();
    VestaEvent evt = CreateBase(identity);

    Assert.False(VestaEventMetadata.TryGetTtlSeconds(evt, out _));
  }

  [Fact]
  public void TryGetTtlSeconds_ReturnsFalseForNonPositiveValue()
  {
    using VestaIdentity identity = VestaIdentity.Generate();
    JsonElement md = JsonSerializer.Deserialize<JsonElement>("""{"ttlSeconds":0}""");
    VestaEvent evt = CreateBase(identity, md);

    Assert.False(VestaEventMetadata.TryGetTtlSeconds(evt, out _));
  }
}
