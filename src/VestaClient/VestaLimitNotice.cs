using VestaCore.Protocol;

namespace VestaClient;

/// <summary>
/// A structured "your app is being limited" signal raised by <see cref="VestaConnection.OnLimited"/>.
/// Unlike the raw <see cref="ErrorMessage"/> surfaced through <c>OnError</c>, this is the client's
/// semantic interpretation of a relay limit — quota, publish-rate, or app-registration refusal — so
/// app code can react (back off, surface UI, prompt an upgrade) without string-matching error codes.
/// </summary>
/// <param name="Code">The raw server error code (e.g. <c>RATE_LIMITED</c>, <c>QUOTA_EXCEEDED</c>).</param>
/// <param name="Message">Human-readable detail from the relay.</param>
/// <param name="ChannelId">The channel the limited request targeted, when the relay stamped it.</param>
/// <param name="EventId">The event the limit applied to, when the relay stamped it.</param>
/// <param name="IsTransient">
/// True when retrying later may succeed (e.g. <c>RATE_LIMITED</c> — the token bucket refills).
/// False when a retry of the same event cannot succeed without an operator/owner change
/// (e.g. <c>QUOTA_EXCEEDED</c>, <c>UNKNOWN_APP</c>, <c>ACCESS_DENIED</c>).
/// </param>
public sealed record VestaLimitNotice(
    string Code,
    string Message,
    string? ChannelId,
    Guid? EventId,
    bool IsTransient);

/// <summary>
/// Client-side classification of server <see cref="ErrorMessage"/> codes. Centralizes the policy
/// for "is this a limit the app should hear about?" and "can the offending event ever succeed on
/// retry, or should it be dead-lettered?" so <see cref="VestaConnection"/> stays declarative.
/// </summary>
public static class VestaErrorCodes
{
    public const string RateLimited = "RATE_LIMITED";
    public const string QuotaExceeded = "QUOTA_EXCEEDED";
    public const string UnknownApp = "UNKNOWN_APP";
    public const string AccessDenied = "ACCESS_DENIED";
    public const string AppNotAllowed = "APP_NOT_ALLOWED";

    /// <summary>
    /// The outcome of classifying a server error code.
    /// </summary>
    /// <param name="IsLimit">True when the error represents the relay limiting/refusing the app
    /// (quota, rate, registration, access) and should surface via <c>OnLimited</c>.</param>
    /// <param name="IsTransient">True when retrying the same event later may succeed.</param>
    /// <param name="IsEventFatal">True when retrying the same event can never succeed, so a
    /// matching outbox entry should be dead-lettered rather than re-sent forever.</param>
    public readonly record struct Classification(bool IsLimit, bool IsTransient, bool IsEventFatal);

    /// <summary>
    /// Classify a server error code into limit / retry semantics.
    /// </summary>
    public static Classification Classify(string code) => code switch
    {
        // Limits the app should hear about.
        RateLimited => new Classification(IsLimit: true, IsTransient: true, IsEventFatal: false),
        QuotaExceeded => new Classification(IsLimit: true, IsTransient: false, IsEventFatal: true),
        UnknownApp => new Classification(IsLimit: true, IsTransient: false, IsEventFatal: true),
        AccessDenied => new Classification(IsLimit: true, IsTransient: false, IsEventFatal: true),
        AppNotAllowed => new Classification(IsLimit: true, IsTransient: false, IsEventFatal: true),

        // Doomed events that are client/protocol errors, not "limits": still dead-letter so the
        // offline outbox doesn't re-send them on every reconnect.
        "INVALID_CHANNEL" or "INVALID_SIGNATURE" or "SIGNATURE_REQUIRED" or
        "CLIENT_ID_MISMATCH" or "PROTOCOL_NAMESPACE_RESERVED" or "CHANNEL_DELETED"
            => new Classification(IsLimit: false, IsTransient: false, IsEventFatal: true),

        // Unknown codes: be conservative — don't dead-letter, don't claim it's a limit.
        _ => new Classification(IsLimit: false, IsTransient: true, IsEventFatal: false),
    };
}
