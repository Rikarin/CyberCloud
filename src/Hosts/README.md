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

brings up **the whole platform**: Redis, one PostgreSQL server carrying three shard databases, NATS,
a k3s in Docker, SeaweedFS as the object store, Mailpit as the mail relay, **two** silos —
[docs/plan/24 § Phase 0](../../docs/plan/24-roadmap.md)'s exit criterion — and, since 2026-09-15,
the three hosts a user reaches and the two Angular apps:

| Resource | Address | What it is |
|---|---|---|
| `gateway` | `http://localhost:5100` | `CyberCloud.Gateway.Host`, validating tokens against the identity host below |
| `identity` | `http://localhost:5101` | `CyberCloud.Identity.Host`; this address is also the `iss` every token carries |
| `feeds` | `http://localhost:5102` | `CyberCloud.Registry.Feeds.Host` — `dotnet nuget push`, `npm publish`, `mvn deploy` go here |
| `portal` | `http://localhost:4200` | `ng serve portal`; its `/api` is proxied to the gateway |
| `identity-app` | `http://localhost:4201` | `ng serve identity`; the sign-in and sign-up pages, proxied to the identity host |
| `seaweedfs` | `http://localhost:8333` | the S3 gateway, bucket `cybercloud`, created by the `seaweedfs-bucket` container |
| `mailpit` | `http://localhost:8025` | the inbox every email the platform sends on this run lands in (#93); SMTP on `localhost:1025`, which both silos are pointed at through `CyberCloud:Communication:Smtp` |

`CyberCloud.AppHost.Tests` runs that same AppHost — minus the two Angular apps,
`--CyberCloud:AppHost:Frontends=false` — and asserts the criterion; it is a per-PR test.
`AppHostTopologyTests` in the same project asserts the declarations above without starting anything,
including that the two proxy files name the ports in `CyberCloudResources`.

⚠ **The two Angular apps need Node 24 on `PATH`** (`portal/.nvmrc`); the Angular CLI refuses an
older major outright, and Aspire runs whatever `node` it finds. On such a machine the two resources
fail at start with the CLI's own message and nothing above them is affected. They also need
`pnpm install --frozen-lockfile` to have been run once in `portal/` — the AppHost deliberately does
not install (`WithPnpm(install: false)`), because an install that can write the lockfile is not a
thing to run on every start.

⚠ **What the portal can do against this run.** Open http://localhost:4200/: with no session the
portal leaves for the identity host's `/authorize`, which sends a person with no cookie to the
identity app's sign-in page. "Create one" runs the self-serve sign-up — an address, a six-digit
code, a name, an organisation and a passkey or a password — and lands back in the portal signed
in, with the new tenant's default subscription and resource group in the context bar; from there
the subscription and resource-group pages list what sign-up created and a resource can be created
and watched to `Succeeded` (#88). ⚠ **The code is in two places (#93).** It is mailed through the
platform's own communication service to Mailpit — open http://localhost:8025 — and the silo that
minted it also logs it at Warning: open the Aspire dashboard, Structured logs, and filter for
`DEVELOPMENT OTP`; the console log of `silo-1` or `silo-2` carries the same line. A `was NOT
mailed` Warning beside it says why the relay refused, if it did. The same relay carries a tenant's
own email channel (`CyberCloud.Communication/services/{name}/channels` with `kind: email`), so an
alert (#32) lands in the same inbox. ⚠ **A stop of the
AppHost empties the durable tier**, so the tenant is gone with it, but `.identity/` beside this
AppHost keeps the signing and encryption keys (gitignored), so a restart of the identity host alone
keeps every portal tab signed in. `PersonOverHttpTests` performs the same story against this
topology, minus the browser. `CyberCloud.Sample/widgets` needs a `clusterId`, so on a run where k3s
is still starting the type to create first is one that declares no cluster — a
`CyberCloud.Communication/services`, for one.

⚠ **The AppHost fixes thirteen ports** — 11111/30011 and 11112/30012 for the two silos' Orleans
sockets, 6443 for the k3s API server, 8333/8888 for SeaweedFS, 1025/8025 for Mailpit, and the five
hosts and apps in the table. Orleans' sockets are opened from configuration rather than from an
Aspire endpoint, so Aspire cannot allocate them and cannot detect a collision; the rest are pinned
because a proxy file, a kubeconfig, an S3 signature, an SMTP relay address and an OIDC issuer each
name a port. A second `dotnet run`, or a `dotnet run` beside `CyberCloud.AppHost.Tests`, fails with
`AddressInUseException`.

⚠ **`CyberCloud.Silo.Host --apply-durable-schema`** is a one-shot mode, not a silo. It creates the
Orleans grain-storage schema on every configured durable shard and exits;
`Microsoft.Orleans.Persistence.AdoNet` ships no SQL and does not migrate, so without it a silo fails
at start. The AppHost runs it as its own resource and both silos `WaitForCompletion` on it. Nothing
in `deploy/` or `charts/` runs it yet — see
`CyberCloud.ServiceDefaults/Storage/OrleansAdoNetSchema.cs`.
