/**
 * Vesta client identity — Ed25519 keypair + derived clientId.
 *
 * Identity creation/serialization is runtime-agnostic. Persistence
 * helpers (loadOrCreateIdentity) require Node.js fs; in the browser,
 * use VestaIdentity.generate() + toJSON()/fromJSON() with localStorage.
 */

import * as ed from "@noble/ed25519";

import { base64UrlToBytes, bytesToBase64Url, deriveClientId } from "./signing.js";

export interface SerializedIdentity {
  clientId: string;
  publicKey: string;   // base64url
  privateKey: string;  // base64url (32-byte seed)
}

export class VestaIdentity {
  readonly clientId: string;
  readonly publicKey: Uint8Array;
  readonly privateKey: Uint8Array;

  private constructor(clientId: string, publicKey: Uint8Array, privateKey: Uint8Array) {
    this.clientId = clientId;
    this.publicKey = publicKey;
    this.privateKey = privateKey;
  }

  get publicKeyB64(): string {
    return bytesToBase64Url(this.publicKey);
  }

  get privateKeyB64(): string {
    return bytesToBase64Url(this.privateKey);
  }

  /** Generate a fresh Ed25519 identity. */
  static generate(): VestaIdentity {
    const priv = ed.utils.randomPrivateKey();
    const pub = ed.getPublicKey(priv);
    return new VestaIdentity(deriveClientId(pub), pub, priv);
  }

  /** Reconstruct from a 32-byte Ed25519 seed. */
  static fromPrivateKey(seed: Uint8Array): VestaIdentity {
    const pub = ed.getPublicKey(seed);
    return new VestaIdentity(deriveClientId(pub), pub, seed);
  }

  static fromJSON(data: SerializedIdentity): VestaIdentity {
    return VestaIdentity.fromPrivateKey(base64UrlToBytes(data.privateKey));
  }

  toJSON(): SerializedIdentity {
    return {
      clientId: this.clientId,
      publicKey: this.publicKeyB64,
      privateKey: this.privateKeyB64,
    };
  }
}

// ─── Node-only persistence helper ─────────────────────────────────────────

/**
 * Load (or generate + save) an identity from `~/.vesta/{prefix}-identity.json`.
 * Node-only — for browser usage, persist via localStorage manually.
 */
export async function loadOrCreateIdentity(prefix: string): Promise<VestaIdentity> {
  const { existsSync, mkdirSync, readFileSync, writeFileSync } = await import("node:fs");
  const { homedir } = await import("node:os");
  const { join } = await import("node:path");

  const dir = join(homedir(), ".vesta");
  mkdirSync(dir, { recursive: true });
  const path = join(dir, `${prefix}-identity.json`);

  if (existsSync(path)) {
    const data = JSON.parse(readFileSync(path, "utf-8")) as Partial<SerializedIdentity>;
    if (data.privateKey && data.publicKey && data.clientId) {
      return VestaIdentity.fromJSON(data as SerializedIdentity);
    }
    // Legacy file (clientId only) — rotate to a full identity.
  }

  const identity = VestaIdentity.generate();
  writeFileSync(path, JSON.stringify(identity.toJSON(), null, 2));
  return identity;
}
