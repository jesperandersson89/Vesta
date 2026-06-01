# Vesta Documentation

Reference docs for the Vesta protocol, SDK, and server. For high-level architecture decisions and rationale, read [PLANNING.md](../PLANNING.md). For quick-start instructions, see the top-level [README.md](../README.md).

## Contents

| Doc                                                | Audience         | What it covers                                                                                |
| -------------------------------------------------- | ---------------- | --------------------------------------------------------------------------------------------- |
| [events.md](events.md)                             | App developers   | The `VestaEvent` shape, signing, `metadata`, `replace`, `volatile`, TTL/ephemeral events      |
| [projections.md](projections.md)                   | App developers   | SDK conflict-resolution primitives (`EventReducer`, `AppendOnlyLog`, `LwwRegister`, `LwwMap`) |
| [protocol.md](protocol.md)                         | SDK implementers | Wire-level message types and the connection lifecycle                                         |
| [server-configuration.md](server-configuration.md) | Operators        | Environment variables, storage backends, ACL modes, background services                       |

## Status

These docs are a work in progress. The protocol itself is stable enough to build against; sections marked **draft** may still shift. When something here disagrees with the code, the code wins — please open an issue or send a fix.
