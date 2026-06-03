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
    ANNOUNCE_EVENT_TYPE,
    buildAnnounce,
    buildLink,
    buildUnlink,
    DeviceGroupProjection,
    deviceGroupChannel,
    generateGroupId,
    isProtocolChannel,
    LINK_EVENT_TYPE,
    PairingPayload,
    PROTOCOL_CHANNEL_PREFIX,
    UNLINK_EVENT_TYPE,
} from "./device-groups.js";
export type {
    DeviceAnnouncePayload,
    DeviceGroup,
    DeviceLinkPayload,
} from "./device-groups.js";
export {
    AppendOnlyLog,
    EventReducer,
    LwwMap,
    LwwMapUpdate,
    LwwRegister,
} from "./projections/index.js";
export type { ProjectionCheckpoint } from "./projections/index.js";
export { InMemoryClientEventStore } from "./storage.js";
export type { ClientEventStore, OutboxEntry, OutboxStatus } from "./storage.js";
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
    DeleteChannelMessage,
    ErrorMessage,
    EventMessage,
    EventsBatchMessage,
    FetchMessage,
    GrantAccessMessage,
    HelloMessage,
    PublishMessage,
    RegisterAppMessage,
    SequencedEvent,
    ServerMessage,
    SubscribeMessage,
    UnsubscribeMessage,
    VestaEvent,
    WelcomeMessage,
} from "./types.js";
