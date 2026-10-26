# Identity, backup and recovery

A Vesta identity is an Ed25519 keypair; the `clientId` (`base64url(sha256(publicKey))[:22]`) is derived from the public key and is how everyone else knows you. **There are no accounts and no password reset on the relay**: whoever holds the 32-byte private seed *is* the identity, and a lost seed cannot be recovered by anyone. This page covers how to keep that from happening. (Why events are signed and how clients verify them: [events.md](events.md).)

| Situation                                                | Use                                                                 |
| -------------------------------------------------------- | ------------------------------------------------------------------- |
| One device might break or be replaced                    | [Pair a second device early](#pair-a-second-device-early)           |
| Player should be able to save a backup file / password manager entry | [Encrypted export](#encrypted-export)                  |
| Player should be able to write something down            | [Recovery phrase](#recovery-phrase)                                 |
| Player lost every device and only remembers a login      | [Relay-stored backup](#relay-stored-backup)                         |
| Player should log in with a username and password on any device | [Developer-hosted accounts](#developer-hosted-accounts)      |

None of these is automatic. The SDK gives you the primitives; your game decides what to offer and when to prompt (the best moment is right after first launch, before there is anything to lose).

## Pair a second device early

Device groups let several keys vouch for each other ([protocol.md](protocol.md), `CreateDeviceGroupAsync` / `JoinDeviceGroupAsync` / `LinkDeviceAsync`). Private keys never move: each device keeps its own key, and your app treats the *group* as one player. If one device is lost, the others carry on and can unlink it. This only helps if the app keys player data by group membership rather than by a single `clientId`, and if a second device was paired **before** the loss.

## Encrypted export

Turns the identity into a JSON blob that is safe to store anywhere (a file, a cloud drive, a password manager). It is encrypted with a key derived from a password, so the blob is only as strong as that password: anyone holding it can guess offline.

| Language   | Export                                         | Import                                              |
| ---------- | ---------------------------------------------- | --------------------------------------------------- |
| C#         | `identity.ExportEncrypted(password)`           | `VestaIdentity.ImportEncrypted(blob, password)`      |
| TypeScript | `await identity.exportEncrypted(password)`     | `await VestaIdentity.importEncrypted(blob, password)` |
| Python     | `identity.export_encrypted(password)`          | `VestaIdentity.import_encrypted(blob, password)`     |

A wrong password, a damaged blob or an unsupported version throws `IdentityBackupException` / `IdentityBackupError`.

**Format (v1)** — the same in every SDK, see [IdentityBackup.cs](../src/VestaCore/Identity/IdentityBackup.cs):

```json
{ "format": "vesta-identity-backup", "version": 1, "clientId": "E545QOZLVJFyIIjZoNdBYo",
  "kdf": { "name": "argon2id", "memoryKiB": 65536, "iterations": 3, "parallelism": 1, "salt": "<16 bytes b64url>" },
  "cipher": { "name": "aes-256-gcm", "nonce": "<12 bytes b64url>" },
  "ciphertext": "<32-byte seed + 16-byte tag, b64url>" }
```

- Argon2id (RFC 9106) derives **64 bytes** from the NFKC-normalised UTF-8 password; the first 32 are the AES-256-GCM key (the rest locate [relay backups](#relay-stored-backup)). Default cost is 64 MiB and 3 passes (about a second); the cost is stored in the blob, so it can be raised later.
- The `clientId` is authenticated as associated data (`"vesta-identity-backup-v1\n" + clientId`), so a blob cannot be relabelled, and import checks the decrypted seed really derives that id.
- Blobs are untrusted input: import refuses a cost above 512 MiB / 10 passes before doing any work.
- Test vector (any SDK must open it): password `correct horse battery staple`, seed `32 × 0x00`, `memoryKiB` 256, `iterations` 1 — the blob in [IdentityBackupTests.cs](../tests/VestaCore.Tests/IdentityBackupTests.cs).

## Recovery phrase

`identity.ToRecoveryPhrase()` / `toRecoveryPhrase()` / `to_recovery_phrase()` returns 24 words from the BIP-39 English list; `FromRecoveryPhrase` reverses it (case and extra whitespace ignored, a typo is caught by the checksum). It encodes the seed directly (BIP-39 entropy-to-mnemonic: 256 bits plus an 8-bit SHA-256 checksum) and does **not** use BIP-39's PBKDF2 stretching or passphrase, so it is not interchangeable with a crypto-wallet seed. Vectors: 32 × `0x00` → `abandon` ×23 `art`; 32 × `0xFF` → `zoo` ×23 `vote`.

The phrase is the identity with no password in front of it. Tell players to write it down offline and never type it into anything but your game.

## Relay-stored backup

For the player who lost every device and remembers only a login. The encrypted identity is parked on the relay and found again from `(app, username, password)` alone, so there is no account to create and no extra server.

```
backup = RelayBackup.Derive(appId, username, password)     // one Argon2id run
backup.ChannelId  = "vesta/identity-backup/{32 hex}"      // derived, unguessable
publish:  backup.CreateEvent(identity)                    // signed, replace: true, type "vesta.identity-backup"
restore:  FETCH backup.ChannelId (all pages) → backup.Restore(events)
```

| Language   | Derive                                          | Publish an event                  | Restore                         |
| ---------- | ----------------------------------------------- | --------------------------------- | ------------------------------- |
| C#         | `RelayBackup.Derive(app, user, pw)`             | `backup.CreateEvent(identity)`    | `backup.Restore(events)`        |
| TypeScript | `await RelayBackup.derive(app, user, pw)`       | `await backup.createEvent(identity)` | `await backup.restore(events)` |
| Python     | `RelayBackup.derive(app, user, pw)`             | `backup.create_event(identity)`   | `backup.restore(events)`        |

How it is built, and why:

- **One derivation, two outputs.** The Argon2id salt is `sha256("vesta-backup-v1\n{appId}\n{username}")[:16]` (username trimmed, NFKC, lower-cased). Bytes 0–31 of the output encrypt, bytes 32–47 (hex) name the channel. Without the password you can neither find the channel nor enumerate users by guessing usernames.
- **The restoring device has no key.** It connects with a throwaway identity, fetches the channel and decrypts. Fetching is the ordinary `FETCH` (page until the end, see `Protocol:MaxFetchLimit` in [server-configuration.md](server-configuration.md#catch-up-paging)).
- **Anyone can write to the channel** (it is a public channel in the reserved `vesta/` namespace, so no app quota or registration applies), so junk is expected. `Restore` derives the key once, skips any blob whose salt or cost differs without running another KDF, and accepts only a blob that decrypts and matches its own `clientId`. Someone who learns the channel id can bury the real backup under junk but cannot read or replace it: `replace` is per publisher, and they are not you.
- **Updating** is publishing again from the same identity: the relay keeps only the latest event per `(channel, client, type)`.

What it does **not** do, deliberately:

- The relay (and anyone who copies the channel) can try passwords offline against the blob, exactly like an exported file. Enforce a strong password in your UI; the 64 MiB cost slows guessing but does not replace it.
- Changing the password means publishing under the new derivation; the old blob stays on the relay, still decryptable by the old password. Treat it as compromised if the old password was.
- It backs up the key as it was when published. A backup made before you rotated to a new identity restores the old one.
- The relay may prune or lose the channel; this is a convenience, not durable storage. Offer the [recovery phrase](#recovery-phrase) alongside it.
- There is no "forgot my password": that is what the phrase, a second paired device, or a developer-hosted account (planned) are for.

The key never leaves the client in plaintext, so this does not reopen the rejected idea of server-mediated key transfer.

## Developer-hosted accounts

If your game wants a conventional login ("username + password, any device") and you are willing to run a small service, keep the encrypted identity there instead of on a relay. The account server holds only an opaque blob and a verifier, so it **cannot read the key and never learns the password**; the identity stays a normal Vesta keypair, so the relay and every other player see nothing different.

```
sign up:  keys = RelayBackup.Derive(appId, username, password)
          POST /accounts { username, keys.AuthSecret, keys.ExportBlob(identity) }
log in:   keys = RelayBackup.Derive(appId, username, password)        // same derivation, any device
          POST /accounts/login { username, keys.AuthSecret }  →  { blob }
          identity = keys.ImportBlob(blob)                            // decrypted locally
```

- **`AuthSecret`** is `base64url(HMAC-SHA256(material, "vesta-account-auth-v1"))` over the same 64-byte Argon2id output that encrypts the blob (bytes 0–31 key, 32–47 relay channel). It is one-way: the server can check that a login derived the same secret but cannot get the encryption key from it. `ExportBlob` / `ImportBlob` are `RelayBackup`'s account-bound variants of the export format (the salt is derived from app and username, so the blob only opens for that account).
- **C#** `RelayBackup.AuthSecret`, `ExportBlob`, `ImportBlob`; **TypeScript** `authSecret`, `await exportBlob`, `await importBlob`; **Python** `auth_secret`, `export_blob`, `import_blob`. A shared test vector pins the secret across the three.
- **Reference server:** [examples/AccountServer](../examples/AccountServer/README.md) is a runnable ASP.NET minimal API with signup, login, blob update, password change and delete, a login throttle, and no Vesta dependency. Its tests ([AccountFlowTests.cs](../tests/AccountServer.Tests/AccountFlowTests.cs)) run the whole flow with the SDK.
- **Changing the password** re-derives everything: publish the new `AuthSecret` and a blob exported under the new derivation (`POST /accounts/password`). The identity itself does not change.
- **A leaked account database** equals a leaked exported backup: offline password guessing, each guess an Argon2id run. Enforce strong passwords and keep the 64 MiB default cost.

### If you stop running it

The point of building accounts this way is that the game survives its developer. Give players a way to **download their blob** (and show them the [recovery phrase](#recovery-phrase)): with the blob and the password they can restore the identity without your service, point the game at any other relay or account server, and start over there. What is lost is whatever only your service held (balances, purchases, the shopkeeper's ledger, see [patterns.md](patterns.md)); the identity and the freedom to move are not.
