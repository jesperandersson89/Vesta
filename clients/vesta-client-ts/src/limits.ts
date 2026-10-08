/**
 * Client-side classification of server `ErrorMessage` codes — the TypeScript mirror of the C#
 * `VestaClient.VestaLimitNotice` / `VestaErrorCodes`. Centralizes "is this a limit the app should
 * hear about?" and "can the offending event ever succeed on retry, or should it be dead-lettered?"
 * so `VestaConnection` stays declarative.
 */

/** A structured "your app is being limited" signal raised via the connection's `limited` event. */
export interface VestaLimitNotice {
    /** The raw server error code (e.g. `RATE_LIMITED`, `QUOTA_EXCEEDED`). */
    code: string;
    /** Human-readable detail from the relay. */
    message: string;
    /** The channel the limited request targeted, when the relay stamped it. */
    channelId?: string;
    /** The event the limit applied to, when the relay stamped it. */
    eventId?: string;
    /** True when retrying later may succeed (e.g. `RATE_LIMITED` — the token bucket refills). */
    isTransient: boolean;
}

export const VestaErrorCodes = {
    RateLimited: "RATE_LIMITED",
    QuotaExceeded: "QUOTA_EXCEEDED",
    MessageQuotaExceeded: "MESSAGE_QUOTA_EXCEEDED",
    UnknownApp: "UNKNOWN_APP",
    AccessDenied: "ACCESS_DENIED",
    AppNotAllowed: "APP_NOT_ALLOWED",
    /** The operator paused the app: publishes are refused until it is resumed. */
    AppPaused: "APP_PAUSED",
} as const;

/** The outcome of classifying a server error code. */
export interface ErrorClassification {
    /** True when the error represents the relay limiting/refusing the app (quota, rate, registration, access). */
    isLimit: boolean;
    /** True when retrying the same event later may succeed. */
    isTransient: boolean;
    /** True when retrying the same event can never succeed, so a matching outbox entry should be dead-lettered. */
    isEventFatal: boolean;
}

/** Classify a server error code into limit / retry semantics. */
export function classifyErrorCode(code: string): ErrorClassification {
    switch (code) {
        case VestaErrorCodes.RateLimited:
        case VestaErrorCodes.AppPaused:
            return { isLimit: true, isTransient: true, isEventFatal: false };
        case VestaErrorCodes.QuotaExceeded:
        case VestaErrorCodes.MessageQuotaExceeded:
        case VestaErrorCodes.UnknownApp:
        case VestaErrorCodes.AccessDenied:
        case VestaErrorCodes.AppNotAllowed:
            return { isLimit: true, isTransient: false, isEventFatal: true };

        // Doomed events that are client/protocol errors, not "limits": still dead-letter so the
        // offline outbox doesn't re-send them on every reconnect.
        case "INVALID_CHANNEL":
        case "INVALID_SIGNATURE":
        case "SIGNATURE_REQUIRED":
        case "CLIENT_ID_MISMATCH":
        case "PROTOCOL_NAMESPACE_RESERVED":
        case "CHANNEL_DELETED":
            return { isLimit: false, isTransient: false, isEventFatal: true };

        // Unknown codes: be conservative — don't dead-letter, don't claim it's a limit.
        default:
            return { isLimit: false, isTransient: true, isEventFatal: false };
    }
}
