# `src/Hosts/` — the processes

| Host | What it is |
|---|---|
| `CyberCloud.Silo.Host` | the Orleans silo — loads every provider module |
| `CyberCloud.Gateway.Host` | REST + SignalR; an Orleans **client**, not a silo |
| `CyberCloud.Identity.Host` | OIDC endpoints, cookies, sign-in/sign-up pages |
| `CyberCloud.Portal.Host` | serves the API shim + static assets (the Angular SSR node process is separate) |
| `CyberCloud.Ingest.Host` | OTLP + metrics ingest — high volume, separate scaling |
| `CyberCloud.Worker.Host` | reconcile workers, informer bridges, billing rollups |
| `CyberCloud.Admin.Host` | platform-admin UI backend |
| `CyberCloud.AppHost` | Aspire — **local development only** (ADR-014) |

## Why the silo and the gateway are separate processes

The gateway is I/O-bound and scales with request rate; the silo is memory-bound and scales with
resident grains; the ingest host scales with telemetry volume, which is two orders of magnitude
larger than both. Co-hosting means one of the three is always the wrong size. The gateway is
therefore an Orleans *client* (`CreateClient`), which also means a gateway deploy does not move
grains.

## The ingest host is not an Orleans client at all

It writes straight to NATS and ClickHouse. Putting a million spans per second through a grain call
is the one design mistake in this shape that would be expensive to undo, so it is excluded by
process boundary rather than by discipline.

A host is the **only** thing allowed to reference a provider *implementation* assembly, and the
gateway is not allowed to reference one at all — only `.Contracts` and `.Application`.

## What exists today

`CyberCloud.Silo.Host` and `CyberCloud.AppHost`. The other six are named above because
[docs/plan/03 § Hosts](../../docs/plan/03-repository-layout.md) names them; none of them exists yet.

`./build.sh Compile` then:

```
dotnet run --project src/Hosts/CyberCloud.AppHost
```

brings up Redis, one PostgreSQL server carrying three shard databases, NATS, a k3s in Docker, and
**two** silos — [docs/plan/24 § Phase 0](../../docs/plan/24-roadmap.md)'s exit criterion.
`CyberCloud.AppHost.Tests` runs that same AppHost and asserts the criterion; it is a per-PR test.

⚠ **The AppHost fixes five ports** — 11111/30011 and 11112/30012 for the two silos' Orleans sockets,
6443 for the k3s API server. Orleans' sockets are opened from configuration rather than from an
Aspire endpoint, so Aspire cannot allocate them and cannot detect a collision. A second `dotnet run`,
or a `dotnet run` beside `CyberCloud.AppHost.Tests`, fails with `AddressInUseException`.

⚠ **`CyberCloud.Silo.Host --apply-durable-schema`** is a one-shot mode, not a silo. It creates the
Orleans grain-storage schema on every configured durable shard and exits;
`Microsoft.Orleans.Persistence.AdoNet` ships no SQL and does not migrate, so without it a silo fails
at start. The AppHost runs it as its own resource and both silos `WaitForCompletion` on it. Nothing
in `deploy/` or `charts/` runs it yet — see
`CyberCloud.ServiceDefaults/Storage/OrleansAdoNetSchema.cs`.
