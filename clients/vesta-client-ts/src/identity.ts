import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { homedir } from "node:os";
import { join } from "node:path";

const VESTA_DIR = join(homedir(), ".vesta");

/**
 * Load or create a persistent client identity for a given app/room/username combination.
 * Stores a stable clientId in ~/.vesta/{prefix}-identity.json.
 */
export function loadOrCreateIdentity(prefix: string): string {
  mkdirSync(VESTA_DIR, { recursive: true });
  const path = join(VESTA_DIR, `${prefix}-identity.json`);

  if (existsSync(path)) {
    const data = JSON.parse(readFileSync(path, "utf-8"));
    return data.clientId;
  }

  const chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
  let clientId = "";
  for (let i = 0; i < 22; i++) {
    clientId += chars[Math.floor(Math.random() * chars.length)];
  }

  writeFileSync(path, JSON.stringify({ clientId }, null, 2));
  return clientId;
}
