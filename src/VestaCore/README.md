# Vesta.Core

Shared protocol types, event/identity primitives, and conflict-resolution helpers for
[Vesta](https://github.com/jesperandersson89/Vesta) — a protocol and runtime for building
networked applications without surrendering ownership to a central service.

`Vesta.Core` has no dependency on ASP.NET Core or any storage backend; it's shared by both the
relay (`VestaServer`) and the client library (`Vesta.Client`). Most application code should
depend on [`Vesta.Client`](https://www.nuget.org/packages/Vesta.Client) instead, which references
this package.

See the [project README](https://github.com/jesperandersson89/Vesta#readme) and
[docs](https://github.com/jesperandersson89/Vesta/tree/main/docs) for protocol details,
event semantics, and the `VestaCore.Projections` conflict-resolution primitives (LWW register,
LWW map, append-only log).
