/** Browser-side persistent identity using localStorage. */

import { VestaIdentity, type SerializedIdentity } from "vesta-client";

const KEY = "vesta-chess-identity";
const USERNAME_KEY = "vesta-chess-username";

export function loadOrCreateBrowserIdentity(): VestaIdentity {
  const raw = localStorage.getItem(KEY);
  if (raw) {
    try {
      const data = JSON.parse(raw) as SerializedIdentity;
      if (data.privateKey && data.publicKey && data.clientId) {
        return VestaIdentity.fromJSON(data);
      }
    } catch {
      // Fall through and regenerate.
    }
  }
  const identity = VestaIdentity.generate();
  localStorage.setItem(KEY, JSON.stringify(identity.toJSON()));
  return identity;
}

export function loadUsername(): string {
  return localStorage.getItem(USERNAME_KEY) ?? "";
}

export function saveUsername(name: string): void {
  localStorage.setItem(USERNAME_KEY, name);
}
