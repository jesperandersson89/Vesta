/**
 * Vesta Protocol — TypeScript type definitions
 *
 * Mirrors VestaCore.Protocol and VestaCore.Events from the C# implementation.
 */

// ── Events ───────────────────────────────────────────────────────────────────

/** A client-authored immutable event. */
export interface VestaEvent {
  id: string;
  channelId: string;
  timestamp: string;
  clientId: string;
  eventType: string;
  payload: unknown;
  parentId?: string | null;
  signature?: string | null;
  replace?: boolean;
  metadata?: Record<string, unknown> | null;
}

/** A VestaEvent wrapped with server-assigned sequence metadata. */
export interface SequencedEvent {
  event: VestaEvent;
  sequence: number;
  receivedAt: string;
}

// ── Protocol Messages ────────────────────────────────────────────────────────

/** Client → Server: Initial handshake. */
export interface HelloMessage {
  type: "HELLO";
  clientId: string;
  channels: string[];
  lastSequences: Record<string, number>;
  publicKey?: string | null;
}

/** Client → Server: Publish an event. */
export interface PublishMessage {
  type: "PUBLISH";
  channelId: string;
  event: VestaEvent;
}

/** Client → Server: Subscribe to a channel. */
export interface SubscribeMessage {
  type: "SUBSCRIBE";
  channelId: string;
  fromSequence?: number | null;
}

/** Client → Server: Unsubscribe from a channel. */
export interface UnsubscribeMessage {
  type: "UNSUBSCRIBE";
  channelId: string;
}

/** Client → Server: Fetch historical events. */
export interface FetchMessage {
  type: "FETCH";
  channelId: string;
  fromSequence: number;
  toSequence?: number | null;
  limit?: number | null;
}

/** Server → Client: Handshake confirmation. */
export interface WelcomeMessage {
  type: "WELCOME";
  serverId: string;
  channels: string[];
}

/** Server → Client: A single real-time event. */
export interface EventMessage {
  type: "EVENT";
  channelId: string;
  event: VestaEvent;
  sequence: number;
  receivedAt: string;
}

/** Server → Client: A batch of sequenced events (catch-up or fetch response). */
export interface EventsBatchMessage {
  type: "EVENTS_BATCH";
  channelId: string;
  events: SequencedEvent[];
}

/** Server → Client: Acknowledgement that a publish was accepted. */
export interface AckMessage {
  type: "ACK";
  channelId: string;
  eventId: string;
  sequence: number;
}

/** Server → Client: Error response. */
export interface ErrorMessage {
  type: "ERROR";
  code: string;
  message: string;
}

/** Union of all client → server messages. */
export type ClientMessage =
  | HelloMessage
  | PublishMessage
  | SubscribeMessage
  | UnsubscribeMessage
  | FetchMessage;

/** Union of all server → client messages. */
export type ServerMessage =
  | WelcomeMessage
  | EventMessage
  | EventsBatchMessage
  | AckMessage
  | ErrorMessage;
