# Encrypted channels

An **encrypted channel** is a private channel whose payloads the relay cannot read. Members encrypt before publishing and decrypt after receiving; the relay stores and forwards ciphertext, assigns sequences, enforces membership and refuses anything that is not an encrypted envelope. If a user ever fails over to a relay you do not operate, that relay sees nothing either — which is what makes relay fungibility safe for private data.

Use it for: a household's shared notes, a club committee's minutes, a support group's chat, anything you would not want a relay operator (including a managed one) to be able to read. Do **not** use it for presence or a public noticeboard: encryption costs a key handshake per member and makes payloads opaque to every tool, and the operator may [count and price](server-configuration.md#app-quotas--rate-limits) encrypted channels.

> **Status.** Relay, crypto primitives and the SDK glue described here are built in all three SDKs (C#, TypeScript, Python). Byte-compatible across languages. Not yet released: ships with `server-v0.4.0` / SDK `0.4.0`. Not in this version: forward secrecy (a removed member keeps what it already received), multi-admin key management, and an example app.

## The model in one paragraph

Each encrypted channel has one or more **admins** (the creator, plus anyone granted the `admin` role). An admin holds the **channel key** for the current **epoch** (an integer, starting at 1). A **member** derives an X25519 key pair from its own Ed25519 identity, publishes a signed **key request** on the channel, and an online admin answers with a **key grant**: the channel key for every epoch, **sealed** to that member's X25519 key. Members encrypt and decrypt app payloads with the epoch key. To remove a member, the admin revokes it on the relay and **rotates** to a new epoch, sealing the new key only to the remaining members. Trust is anchored the same way as the [trusted-writer pattern](patterns.md#one-trusted-writer-everyone-reads): the app knows the admin's public key (compiled in, or carried by an invite) and accepts key grants from nobody else.

## Quick start

**Admin** (creates the channel, holds the key):

```csharp
await using EncryptedChannel channel = await connection.CreateEncryptedChannelAsync(
    "household/notes",
    initialMembers: [partnerClientId],
    new EncryptedChannelOptions { KeyStore = new SqliteChannelKeyStore("Data Source=keys.db") });

channel.OnEvent += e => Console.WriteLine(e.Event.Event.Payload.GetProperty("text"));
await channel.PublishAsync("app.note", JsonSerializer.SerializeToElement(new { text = "buy milk" }));
```

**Member** (joins later, needs the admin's public key):

```csharp
await using EncryptedChannel channel = await connection.OpenEncryptedChannelAsync("household/notes", new EncryptedChannelOptions
{
    AdminPublicKeys = [adminPublicKey],
    KeyStore = new SqliteChannelKeyStore("Data Source=keys.db"),
});
await channel.WaitForKeyAsync(TimeSpan.FromSeconds(10));   // an admin must be online the first time
channel.OnEvent += e => ...;
await channel.PublishAsync("app.note", payload);
```

TypeScript and Python mirror this:

| Step                 | C#                                             | TypeScript                                            | Python                                                       |
| -------------------- | ---------------------------------------------- | ----------------------------------------------------- | ------------------------------------------------------------ |
| Create (admin)       | `CreateEncryptedChannelAsync(id, members, opts)` | `createEncryptedChannel(id, { members, ...opts })`   | `create_encrypted_channel(id, members=, options=)`           |
| Open (member/admin)  | `OpenEncryptedChannelAsync(id, opts)`           | `openEncryptedChannel(id, opts)`                      | `open_encrypted_channel(id, options)`                        |
| Wait for a key       | `WaitForKeyAsync(timeout)`                      | `await waitForKey(ms)`                                | `await wait_for_key(timeout)`                                |
| Publish              | `PublishAsync(type, payload, replace, metadata)` | `publish(type, payload, { replace, metadata })`      | `publish(type, payload, replace_previous=, metadata=)`       |
| Receive plaintext    | `OnEvent += (DecryptedEvent e)`                 | `on("event", (e) => …)`                               | `on_event(callback)`                                         |
| Rotate (admin)       | `RotateKeyAsync()`                              | `rotateKey()`                                         | `rotate_key()`                                               |
| Remove member (admin)| `RemoveMemberAsync(clientId)`                   | `removeMember(clientId)`                              | `remove_member(client_id)`                                   |
| Key stores           | `InMemoryChannelKeyStore`, `SqliteChannelKeyStore` | `InMemoryChannelKeyStore`, `LocalStorageChannelKeyStore`, `FileChannelKeyStore` (`vesta-client/node`) | `InMemoryChannelKeyStore`, `FileChannelKeyStore`, `SqliteChannelKeyStore` |

A `DecryptedEvent` carries the event with its **plaintext payload** (`Event` / `event`) and the **original ciphertext event** (`Sealed` / `sealed`). Feed the plaintext one to your projections; keep the sealed one if you need to re-verify the signature (the signature covers the ciphertext, not the plaintext).

## How a member gets keys

```
member ──(vesta.channel-key-request { publicKey, x25519PublicKey })──▶ channel
                                                                        │  admin verifies: Ed25519 key derives the
                                                                        │  author's clientId, signature checks out
member ◀──(vesta.channel-key { recipient, keys:[{epoch,epk,n,ct}…] })── admin
```

- A member publishes a request only when its key store holds no key for the channel (or `AlwaysRequestKeys` is set). The request is **self-authenticating**: anyone can check that the embedded Ed25519 key derives the author's client id and signed the event.
- An admin answers every request it sees from a client that is not itself, unless the app's `ApproveKeyRequest` hook says no. It does not need a membership list: the relay only lets members publish on a private channel, so a request on the channel already came from a member. It remembers the member's X25519 key in its key store so it can seal future epochs to it.
- A member accepts a grant only if the event was signed by one of `AdminPublicKeys` (the admin's own key is added automatically when `IsAdmin` is set) and is addressed to its own client id. Grants from anyone else are ignored, even if the relay delivered them.
- A new member receives **every** epoch, so it can read the whole history; after a rotation existing members receive only the new epoch.
- Events whose epoch key has not arrived yet are **buffered** and delivered in sequence order once it does, so a member joining a busy channel sees history in order.
- An admin re-opening the channel replays the history (from `FromSequence`, default 0) and answers only requests that have no grant covering the current epoch, so a restart does not re-grant to everyone. Give it the same key store it had before; without it, it has lost the channel key.

**The first join needs an admin online.** This is the price of the relay not holding keys. For an app where that is awkward, run the admin as a small always-on service (the trusted-writer pattern again) or pre-seal keys for `initialMembers` whose X25519 keys you already know.

## Removing a member

`RemoveMemberAsync(clientId)` does three things: `REVOKE_ACCESS` on the relay (the relay now refuses the member's publishes and fetches), forgets the member's X25519 key, and `RotateKeyAsync()` — a new epoch sealed to everyone still in the store. The removed member can still decrypt what it already had (it held those keys; there is **no forward secrecy** in this version), but nothing published afterwards.

Rotate on its own whenever a key may have leaked. Rotation is cheap: one `vesta.channel-key` event per remaining member.

## What the relay enforces, and what it cannot

| The relay…                                                                 | So…                                                                                                   |
| -------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------- |
| requires `encrypted` channels to be `private`                              | key distribution always has a membership to lean on                                                   |
| refuses any non-`vesta.*` payload that is not `{enc, kid, n, ct}` (`ENCRYPTION_REQUIRED`) | a misconfigured client cannot leak plaintext by accident; this is a guard, not a security boundary |
| never sees a key, cannot decrypt, cannot tell real ciphertext from random  | your users' data is safe from the operator and from a database leak                                   |
| still enforces membership, sequencing, signatures, `replace`, `ttlSeconds` | everything else in Vesta works unchanged                                                              |
| sees `eventType` and the author                                            | metadata leaks: who wrote how many events of which type, and when. Use one opaque type if that matters |

Key material lives on the device in the key store you choose (SQLite file by default on .NET). Protect that file like any other secret; on mobile, back the store interface with Keychain / Keystore.

## Wire details

For the exact byte formats (X25519 derivation from the Ed25519 seed, AES-256-GCM with the channel id, event id and type as associated data, ECIES-style sealing) see `PLANNING.md` #33, phase B. The primitives are exposed as `ChannelCrypto` / `channel-crypto.ts` / `channel_crypto.py`, with shared test vectors, for apps that need to build something the glue does not cover.

See also: [events.md](events.md#payloads-on-encrypted-channels), [protocol.md](protocol.md#encrypted-channels), [patterns.md](patterns.md#one-trusted-writer-everyone-reads), [server-configuration.md](server-configuration.md#app-quotas--rate-limits).
