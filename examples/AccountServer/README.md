# AccountServer

A reference account server for a Vesta game that wants **"log in with a username and password on any device"**. It is a small ASP.NET minimal API you run yourself (or copy into your backend). It deliberately has **no Vesta dependency**: it stores an opaque encrypted blob and verifies an opaque secret, so any client SDK (C#, TypeScript, Python) works against it.

## What it can and cannot see

| It stores                                          | It never sees                      |
| -------------------------------------------------- | ---------------------------------- |
| a salted hash of the client's **login secret**     | the password                       |
| the **encrypted identity** blob, exactly as sent   | the private key (or the encryption key) |

The client derives both from `(appId, username, password)` with one Argon2id run (`RelayBackup`, see [docs/identity.md](../../docs/identity.md)):

- `AuthSecret` is `HMAC-SHA256(derived material, "vesta-account-auth-v1")`: one-way, so it proves you know the password without revealing the key that decrypts the blob.
- the blob is the identity encrypted with AES-256-GCM under a different part of the same derived material.

A database leak therefore gives an attacker the same thing a leaked exported backup file does: the ability to guess passwords offline, each guess costing a full Argon2id run. Enforce a strong password in your game UI.

## Endpoints

All bodies are JSON; usernames are trimmed, NFKC-normalised and lower-cased (the same rules the SDKs use).

| Request                                                                  | Result                                                   |
| ------------------------------------------------------------------------ | -------------------------------------------------------- |
| `POST /accounts` `{ username, authSecret, blob }`                        | `201`, or `409` if the username is taken                 |
| `POST /accounts/login` `{ username, authSecret }`                        | `200 { blob }`, `401`, or `429` when throttled           |
| `PUT /accounts/blob` `{ username, authSecret, blob }`                    | `204`: replace the stored blob                           |
| `POST /accounts/password` `{ username, authSecret, newAuthSecret, blob }` | `204`: re-key after a password change                    |
| `DELETE /accounts` `{ username, authSecret }`                            | `204`                                                    |

An unknown username and a wrong secret return the same `401`, and both count towards the throttle (10 failures per username and 50 per address per 15 minutes; see [LoginThrottle.cs](LoginThrottle.cs) for the lockout trade-off).

## The game side

```csharp
// Sign up (first device): the identity is created locally and only its encrypted form leaves the device.
RelayBackup keys = RelayBackup.Derive("mygame", username, password);
await http.PostAsJsonAsync("/accounts", new { username, authSecret = keys.AuthSecret, blob = keys.ExportBlob(identity) });

// Log in (any device): derive again, fetch the blob, decrypt locally.
RelayBackup keys = RelayBackup.Derive("mygame", username, password);
HttpResponseMessage response = await http.PostAsJsonAsync("/accounts/login", new { username, authSecret = keys.AuthSecret });
VestaIdentity identity = keys.ImportBlob((await response.Content.ReadFromJsonAsync<LoginResponse>())!.Blob);
```

The TypeScript (`await RelayBackup.derive(...)`, `authSecret`, `exportBlob`, `importBlob`) and Python (`RelayBackup.derive(...)`, `auth_secret`, `export_blob`, `import_blob`) calls are equivalent and byte-compatible. After login the game connects to a relay with that identity as usual; the relay never sees the password.

## Run it

```bash
cd examples/AccountServer
dotnet run                                   # in-memory accounts
dotnet run -- --Accounts:DataFile=accounts.json   # persist to a file
```

Put it behind HTTPS: the login secret and blob travel in request bodies. Behind a reverse proxy, enable forwarded headers so the per-address throttle sees real client addresses (the same caveat as the relay's per-IP limits).

## Not included (on purpose)

This is a reference, not a product. Left to you: a real database, email or recovery flows (the identity's [recovery phrase](../../docs/identity.md#recovery-phrase) is the fallback if the password is lost), session tokens (this API re-authenticates each call), signup abuse protection, and a way for players to export their blob. That last one matters: a player who holds their blob and password can restore their identity without your server, which is what keeps the game playable if you stop running it.
