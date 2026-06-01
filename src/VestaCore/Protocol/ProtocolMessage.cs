using System.Text.Json.Serialization;

namespace VestaCore.Protocol;

/// <summary>
/// Base type for all Vesta protocol messages sent over WebSocket.
/// Uses a discriminator property "type" for polymorphic JSON serialization.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HelloMessage), "HELLO")]
[JsonDerivedType(typeof(PublishMessage), "PUBLISH")]
[JsonDerivedType(typeof(SubscribeMessage), "SUBSCRIBE")]
[JsonDerivedType(typeof(UnsubscribeMessage), "UNSUBSCRIBE")]
[JsonDerivedType(typeof(FetchMessage), "FETCH")]
[JsonDerivedType(typeof(CreateChannelMessage), "CREATE_CHANNEL")]
[JsonDerivedType(typeof(GrantAccessMessage), "GRANT_ACCESS")]
[JsonDerivedType(typeof(RegisterAppMessage), "REGISTER_APP")]
[JsonDerivedType(typeof(DeleteChannelMessage), "DELETE_CHANNEL")]
[JsonDerivedType(typeof(WelcomeMessage), "WELCOME")]
[JsonDerivedType(typeof(EventMessage), "EVENT")]
[JsonDerivedType(typeof(EventsBatchMessage), "EVENTS_BATCH")]
[JsonDerivedType(typeof(AckMessage), "ACK")]
[JsonDerivedType(typeof(ErrorMessage), "ERROR")]
public abstract record ProtocolMessage;
