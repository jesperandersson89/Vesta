# Operating a relay

> **Who is this for? Relay operators only — probably not you.**
>
> If you are **building an app** with Vesta, you do **not** deploy a relay. You install a client SDK and point it at a relay URL that *someone else operates* — for example an [Atrium](../PLANNING.md#boundaries) managed endpoint. While developing, you can run a throwaway local relay with `docker compose up` (see [Local development relay](#local-development-relay)); that is a dev tool, not a hosting recipe.
>
> This page is for the rare case where you are the **operator**: a company, community, or service that runs a relay *for other people's apps to connect to* and takes on uptime, backups, TLS, quotas, and abuse handling. If that isn't you, stop here and go to the [README](../README.md).

## The two roles

| Role | You do | You need |
| --- | --- | --- |
| **App developer** (almost everyone) | Build an app, give it an identity, connect to a relay URL | A client SDK (`VestaProtocol.Client`, `vesta-client`) and **a relay URL from an operator** |
| **Relay operator** (rare) | Run `VestaServer` for others to connect to | The `ghcr.io/<owner>/vesta-server` image and this page |

Connecting to a managed relay and to one you run yourself is the same act — a WebSocket URL plus your Ed25519 identity — so you can switch later without changing app code.

## Local development relay

The repo-root [docker-compose.yml](../docker-compose.yml) starts a relay plus PostgreSQL in **open mode** with throwaway credentials and no TLS:

```bash
docker compose up --build   # relay on ws://localhost:5150/ws
```

Do not expose this stack to a network you don't control.

## The relay image

Operators run the published image:

```bash
docker pull ghcr.io/<owner>/vesta-server:<version>
```

| Tag | Meaning |
| --- | --- |
| `X.Y.Z` | Immutable release, published from a `server-vX.Y.Z` git tag |
| `latest` | Newest stable release (never a pre-release). **Pin an explicit version in production.** |
| `sha-<short>` | A commit on `main`; for testing, not for production |

The image version is independent of the SDK package version (`vX.Y.Z` tags publish NuGet/npm/PyPI; `server-vX.Y.Z` tags publish the image). The image listens on port **8080** as a non-root user, is stateless, and keeps all data in PostgreSQL. It does not terminate TLS.

Build it yourself from the repository root:

```bash
docker build -f src/VestaServer/Dockerfile -t vesta-server .
```

## Minimum production configuration

All keys are ordinary ASP.NET Core configuration; in containers set them as environment variables with `__` for nesting. See [server-configuration.md](server-configuration.md) for every key.

| Variable | Why |
| --- | --- |
| `ConnectionStrings__Vesta` | PostgreSQL connection string. Migrations run at startup. Without it the relay refuses to start (set `UseInMemoryStore=true` only for throwaway dev). |
| `Admin__BootstrapPublicKeys__0` | Operator Ed25519 public key for the `/admin/*` API. Generate a pair with `dotnet run .github/scripts/new-relay-keypair.cs`. |
| `Protocol__RequireSignedEvents=true` | Reject unsigned events. |
| `Protocol__RequireAppRegistration=true` | Every channel namespace must belong to a registered app (or pin `Protocol__AllowedApps__0=...` for a closed relay). |
| `Discovery__SigningKey` | Only if `Discovery__Enabled=true`. Without it the relay generates a key under `/app/.vesta/`, which is lost when the container is replaced. |
| `EventCleanup__Enabled`, `AppQuotaPruner__Enabled`, `ChannelDeletionPruner__Enabled` | Turn on the background sweeps you rely on. |

## Deploying

Bring your own platform. The requirements are: run the container, provide PostgreSQL 16+, terminate TLS in front of it, and **forward WebSocket upgrades** on `/ws`. Do not scale beyond one instance yet — rate-limit buckets and admin tokens are in-process (PLANNING.md TODO #15).

Health endpoints for orchestrators:

- `GET /health` — liveness (process is up).
- `GET /health/ready` — readiness (storage reachable).

## Upgrading

Pin the new `X.Y.Z`, restart. Schema migrations are applied automatically at startup and are forward-only, so back up PostgreSQL first.
