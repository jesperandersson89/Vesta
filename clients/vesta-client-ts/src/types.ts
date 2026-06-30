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

/** Client → Server: Create a channel with explicit visibility and initial members. */
export interface CreateChannelMessage {
    type: "CREATE_CHANNEL";
    channelId: string;
    visibility: "public" | "private";
    initialMembers: string[];
}

/** Client → Server: Grant access to a private channel (admin only). */
export interface GrantAccessMessage {
    type: "GRANT_ACCESS";
    channelId: string;
    clientId: string;
    role: "member" | "admin";
}

/** Client → Server: Register an app namespace. The connecting client becomes the owner. */
export interface RegisterAppMessage {
    type: "REGISTER_APP";
    appId: string;
}

/**
 * Client → Server: Soft-delete a channel. Server admin only — the connection's
 * public key must be in the server's `Admin:BootstrapPublicKeys` allow-list.
 * Existing events are retained for a future hard-delete sweep; further
 * PUBLISH / SUBSCRIBE / FETCH / CREATE_CHANNEL for that channel are rejected
 * with `CHANNEL_DELETED`.
 */
export interface DeleteChannelMessage {
    type: "DELETE_CHANNEL";
    channelId: string;
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
    /** The event the error applied to, when the relay correlated it (e.g. publish rejections). */
    eventId?: string;
    /** The channel the failed request targeted, when the relay stamped it. */
    channelId?: string;
}

/** Union of all client → server messages. */
export type ClientMessage =
    | HelloMessage
    | PublishMessage
    | SubscribeMessage
    | UnsubscribeMessage
    | FetchMessage
    | CreateChannelMessage
    | GrantAccessMessage
    | RegisterAppMessage
    | DeleteChannelMessage;

/** Union of all server → client messages. */
export type ServerMessage =
    | WelcomeMessage
    | EventMessage
    | EventsBatchMessage
    | AckMessage
    | ErrorMessage;
