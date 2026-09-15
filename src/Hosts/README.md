# `src/Hosts/` — the processes

| Host | What it is |
|---|---|
| `CyberCloud.Silo.Host` | the Orleans silo — loads every provider module |
| `CyberCloud.Gateway.Host` | REST + SignalR; an Orleans **client**, not a silo |
| `CyberCloud.Identity.Host` | OIDC endpoints, cookies, sign-in/sign-up pages |
| `CyberCloud.Registry.Feeds.Host` | the NuGet v3, npm and Maven wire protocols of `ContainerRegistry/feeds`; an Orleans **client** that validates the gateway's bearer tokens and stores artefacts on the platform's object store |
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

`CyberCloud.Silo.Host`, `CyberCloud.Gateway.Host`, `CyberCloud.Identity.Host`,
`CyberCloud.Registry.Feeds.Host` and `CyberCloud.AppHost`. The other four are named above because
[docs/plan/03 § Hosts](../../docs/plan/03-repository-layout.md) names them; none of those exists yet.

⚠ **The feeds host references one provider's `.Application` assembly where the silo and the
gateway reference all of them** — `ContainerRegistry.Application`, which names it with
`[assembly: OwningHost("CyberCloud.Registry.Feeds.Host")]` so the assembly-graph gate's rule 4 knows
the reference is declared rather than a leak. ⚠ That `.Application` assembly references the family's
*implementation*, and its module registers the provider — so `CyberCloud.Providers.ContainerRegistry`
is loaded into the feeds process and its reconcilers are registered in its container, exactly as in
the silo. What keeps the host from *running* any of it is that it is an Orleans client
(`OrleansApplication.CreateClient`): no grain activates there, no reconcile driver runs there, and
the catalogue is reached through `IFeedGrain` in the Contracts assembly. `FeedsIsolationTests` pins the
compile-time half — the host's own AssemblyRef table names no `FeedGrain`, no Kubernetes client and no
identity store — which is a statement about what the host's code can call, not about what its process
loads; the review of #29 drew the line. `HostCompositionTests` starts it against a real silo and asserts
that the two agree about the feeds type, and that composing it without
`CyberCloud:Feeds:Identity:Issuer` refuses.

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
