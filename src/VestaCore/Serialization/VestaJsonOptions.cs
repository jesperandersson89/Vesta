using System.Text.Json;
using System.Text.Json.Serialization;
using VestaCore.Identity;
using VestaCore.Protocol;

namespace VestaCore.Serialization;

/// <summary>
/// Shared JSON serializer options for the Vesta protocol.
/// Both server and client must use these same options to ensure interoperability.
/// </summary>
public static class VestaJsonOptions
{
    /// <summary>
    /// Default options for serializing/deserializing Vesta protocol messages and events.
    /// </summary>
    public static JsonSerializerOptions Default { get; } = CreateDefault();

    private static JsonSerializerOptions CreateDefault()
    {
        JsonSerializerOptions options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
        };

        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.TypeInfoResolverChain.Add(VestaJsonContext.Default);

        return options;
    }
}

/// <summary>
/// Source-generated JSON context for AOT-compatible serialization of protocol types.
/// </summary>
[JsonSerializable(typeof(ProtocolMessage))]
[JsonSerializable(typeof(HelloMessage))]
[JsonSerializable(typeof(PublishMessage))]
[JsonSerializable(typeof(SubscribeMessage))]
[JsonSerializable(typeof(UnsubscribeMessage))]
[JsonSerializable(typeof(FetchMessage))]
[JsonSerializable(typeof(CreateChannelMessage))]
[JsonSerializable(typeof(GrantAccessMessage))]
[JsonSerializable(typeof(WelcomeMessage))]
[JsonSerializable(typeof(EventMessage))]
[JsonSerializable(typeof(EventsBatchMessage))]
[JsonSerializable(typeof(AckMessage))]
[JsonSerializable(typeof(ErrorMessage))]
[JsonSerializable(typeof(DeviceAnnounce))]
[JsonSerializable(typeof(DeviceLink))]
[JsonSerializable(typeof(PairingPayload))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
public partial class VestaJsonContext : JsonSerializerContext;
