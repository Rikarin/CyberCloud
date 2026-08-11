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

See [docs/plan/03 § Hosts](../../docs/plan/03-repository-layout.md).
