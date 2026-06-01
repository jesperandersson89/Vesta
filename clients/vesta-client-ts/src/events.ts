import type { VestaIdentity } from "./identity.js";
import { signEvent } from "./signing.js";
import type { VestaEvent } from "./types.js";

function randomId(): string {
  if (typeof globalThis.crypto?.randomUUID === "function") {
    return globalThis.crypto.randomUUID();
  }
  // Fallback (should be unreachable on Node 19+/modern browsers).
  const bytes = new Uint8Array(16);
  globalThis.crypto.getRandomValues(bytes);
  bytes[6] = (bytes[6]! & 0x0f) | 0x40;
  bytes[8] = (bytes[8]! & 0x3f) | 0x80;
  const hex = Array.from(bytes, (b) => b.toString(16).padStart(2, "0"));
  return `${hex.slice(0, 4).join("")}-${hex.slice(4, 6).join("")}-${hex.slice(6, 8).join("")}-${hex.slice(8, 10).join("")}-${hex.slice(10, 16).join("")}`;
}

/**
 * Create a new VestaEvent with sensible defaults.
 *
 * When `identity` is provided, the event is signed in place using the
 * identity's Ed25519 private key. The `clientId` is taken from the identity
 * if omitted, ensuring it always matches the signing key.
 */
export function createEvent(
  channelId: string,
  clientIdOrIdentity: string | VestaIdentity,
  eventType: string,
  payload: unknown,
  options?: {
    replace?: boolean;
    parentId?: string;
    metadata?: Record<string, unknown>;
    identity?: VestaIdentity;
  }
): VestaEvent {
  const isIdentity = typeof clientIdOrIdentity !== "string";
  const identity = options?.identity ?? (isIdentity ? clientIdOrIdentity : undefined);
  const clientId = isIdentity ? clientIdOrIdentity.clientId : clientIdOrIdentity;

  const event: VestaEvent = {
    id: randomId(),
    channelId,
    timestamp: new Date().toISOString(),
    clientId,
    eventType,
    payload,
    replace: options?.replace,
    parentId: options?.parentId,
    metadata: options?.metadata,
  };

  if (identity) {
    signEvent(event, identity.privateKey);
  }

  return event;
}
