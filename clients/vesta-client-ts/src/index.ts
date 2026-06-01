export type { SocketFactory, VestaConnectionEvents, VestaConnectionOptions, VestaSocket } from "./connection.js";
export { VestaConnection } from "./connection.js";
export { createEvent } from "./events.js";
export { loadOrCreateIdentity } from "./identity.js";
export type {
  AckMessage,
  ClientMessage,
  ErrorMessage,
  EventMessage,
  EventsBatchMessage,
  FetchMessage,
  HelloMessage,
  PublishMessage,
  SequencedEvent,
  ServerMessage,
  SubscribeMessage,
  UnsubscribeMessage,
  VestaEvent,
  WelcomeMessage,
} from "./types.js";
