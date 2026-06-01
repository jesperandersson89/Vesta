using System.Text.Json;

namespace VestaCore.Events;

/// <summary>
/// Client-authored, immutable, signable event.
/// This is what the client creates and signs before publishing.
///
/// <para>
/// <b>Signed fields</b> (included in <see cref="EventSigner"/> input): Id, ChannelId,
/// Timestamp, ClientId, EventType, Payload, ParentId.
/// </para>
/// <para>
/// <b>Unsigned transport fields</b> (NOT signed; safe for the server to inspect or
/// strip): Signature itself, <see cref="Volatile"/>, <see cref="Replace"/>, and
/// <see cref="Metadata"/>. These are wire-level hints — the server may act on them
/// (e.g. compute <c>expires_at</c> from <c>metadata.ttlSeconds</c>) but they are
/// not part of the cryptographic identity of the event.
/// </para>
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
    bool? Volatile = null,      // Transport hint: don't store in DB, just relay to current subscribers
    bool Replace = false,       // Transport hint: replace previous event of same (channelId, clientId, eventType)
                                // Not signed — stripped from storage after processing
    JsonElement? Metadata = null // Transport-level hints (e.g. { "ttlSeconds": 30 }). NOT signed.
                                 // Read by the server (TTL → expires_at) but not persisted.
);
