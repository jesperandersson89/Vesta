using System.Text.Json;

namespace VestaCore.Events;

/// <summary>
/// Client-authored, immutable, signable event.
/// This is what the client creates and signs before publishing.
/// </summary>
public sealed record VestaEvent(
    Guid Id,                    // UUID v7, client-generated
    string ChannelId,           // Human-readable slug (e.g. "myapp/chat/general")
    DateTimeOffset Timestamp,   // Client wall-clock at creation time
    string ClientId,            // base64url(sha256(pubkey))[:22]
    string EventType,           // e.g. "app.todo.item-added"
    JsonElement Payload,        // Arbitrary structured data
    Guid? ParentId = null,      // Optional causal link to a previous event
    string? Signature = null,   // base64url Ed25519 signature (null when unsigned)
    bool Replace = false        // Transport hint: replace previous event of same (channelId, clientId, eventType)
                                // Not signed — stripped from storage after processing
);
