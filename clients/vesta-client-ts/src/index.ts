export type {
    SocketFactory,
    VestaConnectionEvents,
    VestaConnectionOptions,
    VestaSocket,
} from "./connection.js";
export { remapChannel, setRelayPickerEnabled, VestaConnection } from "./connection.js";
export { createEvent } from "./events.js";
export { loadIdentityFile, loadOrCreateIdentity, VestaIdentity } from "./identity.js";
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
    SnapshotNotSupportedError,
} from "./projections/index.js";
export type { ProjectionCheckpoint, ProjectionSnapshot } from "./projections/index.js";
export {
    InMemoryProjectionStore,
    LocalStorageProjectionStore,
    restoreProjection,
    saveProjection,
} from "./projection-store.js";
export type { ProjectionStore } from "./projection-store.js";
export { classifyErrorCode, VestaErrorCodes } from "./limits.js";
export type { ErrorClassification, VestaLimitNotice } from "./limits.js";
export {
    buildDescriptorSigningInput,
    FederationClient,
    isDescriptorExpired,
    verifyDescriptor,
} from "./federation.js";
export type { DiscoverableApp, DiscoveredRelay, ServerDescriptor } from "./federation.js";
export { InMemoryClientEventStore } from "./storage.js";
export type { ClientEventStore, OutboxEntry, OutboxStatus } from "./storage.js";
export {
    buildManifestSigningInput,
    InMemoryManifestStore,
    InMemoryPeerCacheStore,
    InMemoryRelayOverrideStore,
    LocalStorageManifestStore,
    LocalStoragePeerCacheStore,
    LocalStorageRelayOverrideStore,
    manifestChannelFor,
    parseRelayOverride,
    RELAY_MANIFEST_EVENT_TYPE,
    RelayDirectory,
    resolveRelayCandidates,
    signManifest,
    verifyManifest,
} from "./relay.js";
export type {
    EscapeFallback,
    ManifestStore,
    PeerCacheStore,
    RelayAttempt,
    RelayEndpoint,
    RelayManifest,
    RelayOverride,
    RelayOverrideStore,
    RelaysExhaustedInfo,
    VestaAppConfig,
} from "./relay.js";
export { RelayRecoverySession } from "./relay-recovery.js";
export type {
    RelayAdoptOptions,
    RelayAdoptResult,
    RelayChoice,
    RelayRecoveryHost,
    RelayRecoveryPhase,
    RelayRecoverySnapshot,
} from "./relay-recovery.js";
export {
    defineRelayPicker,
    mountRelayPickerOverlay,
    UNVERIFIED_RELAY_WARNING,
    VestaRelayPickerElement,
} from "./relay-picker.js";
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
