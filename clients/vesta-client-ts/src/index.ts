export type {
    SocketFactory,
    VestaConnectionEvents,
    VestaConnectionOptions,
    VestaSocket,
} from "./connection.js";
export { VestaConnection } from "./connection.js";
export { createEvent } from "./events.js";
export { loadOrCreateIdentity, VestaIdentity } from "./identity.js";
export type { SerializedIdentity } from "./identity.js";
export {
    base64UrlToBytes,
    bytesToBase64Url,
    buildSigningInput,
    canonicalize,
    deriveClientId,
    normalizeTimestampForSigning,
    signEvent,
} from "./signing.js";
export type {
    AckMessage,
    ClientMessage,
    CreateChannelMessage,
    ErrorMessage,
    EventMessage,
    EventsBatchMessage,
    FetchMessage,
    GrantAccessMessage,
    HelloMessage,
    PublishMessage,
    SequencedEvent,
    ServerMessage,
    SubscribeMessage,
    UnsubscribeMessage,
    VestaEvent,
    WelcomeMessage,
} from "./types.js";
