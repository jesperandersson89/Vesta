import { randomUUID } from "node:crypto";
import type { VestaEvent } from "./types.js";

/**
 * Create a new VestaEvent with sensible defaults.
 */
export function createEvent(
  channelId: string,
  clientId: string,
  eventType: string,
  payload: unknown,
  options?: {
    replace?: boolean;
    parentId?: string;
    metadata?: Record<string, unknown>;
  }
): VestaEvent {
  return {
    id: randomUUID(),
    channelId,
    timestamp: new Date().toISOString(),
    clientId,
    eventType,
    payload,
    replace: options?.replace,
    parentId: options?.parentId,
    metadata: options?.metadata,
  };
}
