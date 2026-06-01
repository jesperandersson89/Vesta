/**
 * RFC 8785 JCS-style canonical JSON + Ed25519 event signing.
 * Output must match the C# `EventSigner.BuildSigningInput`.
 */

import * as ed from "@noble/ed25519";
import { sha256 } from "@noble/hashes/sha256";
import { sha512 } from "@noble/hashes/sha512";

import type { VestaEvent } from "./types.js";

// Configure synchronous SHA-512 for @noble/ed25519 so sign/verify can run sync.
ed.etc.sha512Sync = (...m: Uint8Array[]) => sha512(ed.etc.concatBytes(...m));

// ─── base64url ─────────────────────────────────────────────────────────────

const B64URL_PAD = /=+$/;
const B64URL_PLUS = /\+/g;
const B64URL_SLASH = /\//g;

export function bytesToBase64Url(bytes: Uint8Array): string {
  let bin = "";
  for (let i = 0; i < bytes.length; i++) bin += String.fromCharCode(bytes[i]!);
  const b64 = typeof btoa !== "undefined" ? btoa(bin) : Buffer.from(bin, "binary").toString("base64");
  return b64.replace(B64URL_PLUS, "-").replace(B64URL_SLASH, "_").replace(B64URL_PAD, "");
}

export function base64UrlToBytes(s: string): Uint8Array {
  const b64 = s.replace(/-/g, "+").replace(/_/g, "/") + "=".repeat((4 - (s.length % 4)) % 4);
  const bin = typeof atob !== "undefined" ? atob(b64) : Buffer.from(b64, "base64").toString("binary");
  const out = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
  return out;
}

// ─── client id derivation ──────────────────────────────────────────────────

/** clientId = base64url(sha256(publicKey)).slice(0, 22) — matches C# VestaIdentity.DeriveClientId. */
export function deriveClientId(publicKey: Uint8Array): string {
  const digest = sha256(publicKey);
  return bytesToBase64Url(digest).slice(0, 22);
}

// ─── JCS canonicalization ──────────────────────────────────────────────────

/**
 * Produce RFC 8785-compatible canonical JSON for the subset of types
 * we use in event payloads (null/bool/string/number/array/object).
 */
export function canonicalize(value: unknown): string {
  if (value === null || value === undefined) return "null";
  if (typeof value === "boolean") return value ? "true" : "false";
  if (typeof value === "number") {
    if (!Number.isFinite(value)) throw new Error("Cannot canonicalize non-finite number");
    // JSON.stringify produces ECMA-compliant numbers; close enough to JCS for integer/double values we emit.
    return JSON.stringify(value);
  }
  if (typeof value === "string") return JSON.stringify(value);
  if (Array.isArray(value)) {
    return "[" + value.map(canonicalize).join(",") + "]";
  }
  if (typeof value === "object") {
    const obj = value as Record<string, unknown>;
    const keys = Object.keys(obj).sort();
    const parts: string[] = [];
    for (const k of keys) {
      parts.push(JSON.stringify(k) + ":" + canonicalize(obj[k]));
    }
    return "{" + parts.join(",") + "}";
  }
  throw new Error(`Cannot canonicalize value of type ${typeof value}`);
}

/** Normalize an ISO timestamp string to `yyyy-MM-ddTHH:mm:ssZ` (second precision, UTC). */
export function normalizeTimestampForSigning(ts: string | Date): string {
  const d = typeof ts === "string" ? new Date(ts) : ts;
  const pad = (n: number) => n.toString().padStart(2, "0");
  return (
    d.getUTCFullYear() +
    "-" + pad(d.getUTCMonth() + 1) +
    "-" + pad(d.getUTCDate()) +
    "T" + pad(d.getUTCHours()) +
    ":" + pad(d.getUTCMinutes()) +
    ":" + pad(d.getUTCSeconds()) +
    "Z"
  );
}

/** Canonical signing-input bytes for a VestaEvent (matches C# EventSigner.BuildSigningInput). */
export function buildSigningInput(event: VestaEvent): Uint8Array {
  const fields: Record<string, unknown> = {
    channelId: event.channelId,
    clientId: event.clientId,
    id: event.id,
    parentId: event.parentId ?? null,
    payload: event.payload,
    timestamp: normalizeTimestampForSigning(event.timestamp),
    type: event.eventType,
  };
  return new TextEncoder().encode(canonicalize(fields));
}

/** Sign an event in place and return it. Requires a 32-byte Ed25519 private seed. */
export function signEvent(event: VestaEvent, privateKey: Uint8Array): VestaEvent {
  const input = buildSigningInput(event);
  const sig = ed.sign(input, privateKey);
  event.signature = bytesToBase64Url(sig);
  return event;
}
