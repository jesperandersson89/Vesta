# Vesta.Client

.NET client library for [Vesta](https://github.com/jesperandersson89/Vesta) — a protocol and
runtime for building networked applications without surrendering ownership to a central service.

Connects to a Vesta relay over WebSocket, signs events with an Ed25519 identity, and persists an
offline outbox + local event cache to SQLite so applications keep working when disconnected.

```csharp
using VestaClient;
using VestaClient.Relay;
using VestaCore.Identity;

VestaIdentity identity = VestaIdentity.LoadOrCreate("my-app.identity.json");
VestaAppConfig appConfig = new("my-app", identity.PublicKey, [new Uri("wss://relay.example.com/ws")]);

await using VestaConnection connection = new(identity.ClientId, appConfig, identity: identity);
await connection.ConnectAsync(["my-app/todo-list"]);
```

See the [project README](https://github.com/jesperandersson89/Vesta#readme) and
[docs](https://github.com/jesperandersson89/Vesta/tree/main/docs) for the full protocol,
reconnect/outbox semantics, and projection primitives.
