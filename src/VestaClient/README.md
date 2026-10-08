# VestaProtocol.Client

.NET client library for [Vesta](https://github.com/jesperandersson89/Vesta) — a protocol and
runtime for building networked applications without surrendering ownership to a central service.

Connects to a Vesta relay over WebSocket, signs events with an Ed25519 identity, and persists an
offline outbox + local event cache to SQLite so applications keep working when disconnected.

```csharp
using Vesta;

VestaIdentity identity = VestaIdentity.LoadOrCreate("my-app.identity.json");
VestaAppConfig appConfig = new("my-app", Base64Url.Encode(identity.PublicKey), "wss://relay.example.com/ws");

await using VestaConnection connection = new(identity, appConfig);
await connection.ConnectAsync(["my-app/todo-list"]);
```

Every public type in `VestaProtocol.Core` and `VestaProtocol.Client` lives in the single `Vesta`
namespace, so one `using Vesta;` is all an app needs.

See the [project README](https://github.com/jesperandersson89/Vesta#readme) and
[docs](https://github.com/jesperandersson89/Vesta/tree/main/docs) for the full protocol,
reconnect/outbox semantics, and projection primitives.
