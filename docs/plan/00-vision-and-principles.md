# 00 — Vision and Principles

## What Cyber Cloud is

A **cloud platform** — an Azure-shaped control plane, resource model, identity system, portal, CLI and
SDK — that provisions and operates managed services on **Kubernetes clusters it does not have to own**.

Three product shapes, in this order. Where they compete for effort, earlier wins:

1. **A control plane.** ✅ *Primary.* Tenants, subscriptions, resource groups, resources, providers,
   long-running operations, ReBAC authorization, quota, metering, audit. This is the part that is hard
   to buy and hard to retrofit, and it is the part that decides whether module #14 takes a week or a
   quarter.
2. **A managed-service catalogue.** Postgres, Redis, MongoDB, NATS, Kafka, ClickHouse, RabbitMQ,
   object storage, VMs, container registry, DNS, VPN, mail, telemetry. Each one is a *resource
   provider* over the control plane, not a bespoke service with its own API.
3. **The surfaces** — portal, `cyc` CLI, .NET SDK, REST, SignalR. All three are generated from, or
   check themselves against, one machine-readable description of the resource model.

The consequence of that ordering: **a module is done when it is a provider**, not when its pods run.
A managed Postgres that you can only create by hand is not a feature of this platform.

## The four constraints that shape everything

These came from the brief and they are load-bearing. Every document below is downstream of them.

### 1. Millions of users — so Orleans first, and no single database

The unit of state is a **grain**, not a row. Every entity with identity and behaviour — a tenant, a
subscription, a resource, a user, a session, a cluster connection, an authorization object — is a
grain, and the grain is the only writer of its own state. There is no service that reads a table,
mutates it, and writes it back.

**"No single database" is a specific engineering claim, and it is checked:** at no point in a request
that a tenant can issue at high rate does a shared, cluster-wide, single-writer store appear. What
that costs and how it is arranged is [05 — State and Storage](05-state-and-storage.md). The short
version: clustering is Kubernetes (no DB), the grain directory is Orleans' own distributed directory
(no DB), grain state is Redis Cluster sharded by tenant, reminders are Redis, streams are NATS
JetStream, and the *one* global thing — the tenant directory — is a few hundred bytes per tenant,
read-mostly, and cached in every silo behind a version stamp.

> ⚠️ **Redis alone is not a system of record**, and the brief's suggestion is taken with one
> correction rather than verbatim. Redis with AOF `everysec` and a replica loses up to a second of
> acknowledged writes on an unclean failover. That is fine for a session, a cached projection, or a
> live status; it is not fine for "who owns this subscription" or "what did we bill". §
> [05 § The two tiers](05-state-and-storage.md) names exactly which grains are allowed in which tier
> and makes the choice a compile-time attribute rather than a convention.

### 2. Multi-tenant to the bone

Tenancy is not a column. A tenant id is **part of every grain key**, enforced by
[`Orleans.Multitenant`](https://github.com/VincentH-Net/Orleans.Multitenant): cross-tenant grain calls
throw `UnauthorizedAccessException` by default, streams are filtered, and each tenant's storage
provider instance is configured separately. A bug that would have been a cross-tenant data leak is
instead an exception with a tenant id in the message. See [06 — Tenancy and the Resource Model](06-tenancy-and-resource-model.md).

The same discipline runs into Kubernetes: **no object reaches a cluster without tenant, subscription
and resource labels**, and that is enforced by the type system of the command builder rather than by
review — [09 § The command builder](09-kubernetes-fabric.md).

### 3. The platform manages clusters it does not run

A tenant supplies a **cluster connection** — a kubeconfig, an OIDC-issuer trust, or a service-account
token — and Cyber Cloud provisions into it. Tenants who do not want to run hardware get an in-house
cluster, and in-house clusters are themselves **Kubernetes in Kubernetes**: control planes as pods
(Kamaji), worker nodes as VMs (KubeVirt), driven by Cluster API. Both paths present the same
`IKubeClusterConnection` to every provider, so a managed Postgres does not know or care.

This is also the deployment story: **Cyber Cloud is first deployed on an existing cluster and manages
a second one**, and only after the second is boring does the platform move onto it. That ordering is
not caution, it is a design forcing function — a platform that cannot manage a cluster it is not
running in has an implicit dependency it will discover at the worst time.

### 4. Everything a resource, everything a provider

`CyberCloud.Cache/redis`, `CyberCloud.DBforPostgreSQL/servers`, `CyberCloud.Network/virtualNetworks`,
`CyberCloud.Terminal/consoles` — every capability is a **resource type** in a **provider namespace**,
addressed by an Azure-shaped resource ID, created by a `PUT`, deleted by a `DELETE`, and reconciled by
a loop that compares desired state in a grain to observed state in a cluster. The portal form, the CLI
verb, the SDK client and the OpenAPI document are all generated from the provider's declared schema.

**Adding a managed service must be a data-and-chart exercise, not a platform change.** Cozystack
proves this shape works: its entire catalogue — 24 applications — is Helm charts plus an annotated
`values.yaml` from which a JSON Schema, a form and documentation are generated. Cyber Cloud takes that
idea and puts the desired state, the identity, the authorization and the billing in Orleans instead of
in `HelmRelease` annotations.

## What Cyber Cloud is not

- **Not a Cozystack fork.** Cozystack is Apache-2.0 so copying would be legal; it is not copied because
  its control plane *is* Kubernetes — tenants are namespaces, the API is an aggregated API server,
  desired state is a `HelmRelease`, and the tenancy boundary is RBAC. That design tops out where a
  single management cluster's etcd tops out, and it has no answer for "a tenant brings their own
  cluster in another datacentre". What *is* taken, deliberately and with attribution, is its
  **operator selection per managed service** — which operator to run for Postgres, ClickHouse, Kafka —
  because that is a survey we would otherwise repeat, and its **annotated-values → schema → form**
  pipeline. See [12 — Managed Data Services](12-managed-data-services.md).
- **Not an Azure clone.** The Azure surface is ~200 services; [01](01-azure-parity-catalogue.md) is the
  audit and it declines about half by name and with a reason. The *shape* is copied — resource IDs,
  providers, API versions, long-running operations, RBAC scopes — because it is a good shape and
  because it makes the SDK and CLI feel familiar on day one.
- **Not a Kubernetes wrapper.** The Kubernetes API is an implementation detail of the data plane. A
  tenant never sees a `HelmRelease`, never needs `kubectl`, and — importantly — a resource's desired
  state does not live in etcd. Etcd holds what the cluster is currently asked to run; the grain holds
  what the tenant asked for, and the two are reconciled. When they disagree, the grain wins.
- **Not "portal later".** The portal, the CLI and the SDK are generated, so they arrive with the
  provider or the provider is not finished.

## Non-negotiables

Checked by CI, not by good intentions. See [23 — Build, CI and Testing](23-build-ci-and-testing.md).

| Principle | Enforcement |
|---|---|
| No cross-tenant read is possible without an explicit, logged authorization | `AddMultitenantCommunicationSeparation` + `ICrossTenantAuthorizer`; an integration test suite that drives every provider's API as tenant A with tenant B's resource ids and asserts 404 (never 403 — existence is not disclosed) |
| No Kubernetes object without tenant/subscription/resource labels | The command builder is a type-state chain; `Build()` does not exist until `WithTenantId` and `WithResourceId` have been called. Plus an admission policy on every managed cluster that rejects unlabelled objects in a tenant namespace |
| No shared single-writer store on a tenant-rate path | An architecture test walks the grain graph and fails on a `[PersistentState]` bound to the platform-global provider from a grain whose key carries a tenant id |
| Every resource type is reachable from the generated OpenAPI, CLI and SDK | The provider registry is the source; a build gate diffs generated surfaces against the registry and fails on drift |
| Secrets never reach grain state | Analyzer bans `[Id]`-annotated members named `*Password`, `*Secret`, `*Token`, `*Key` outside `CyberCloud.Vault`; secrets are `SecretRef` handles resolved at the data plane |
| Every long-running operation is resumable | Operation grains are `durable`-tier and re-drive on activation; a chaos test kills silos mid-provision and asserts the resource reaches `Succeeded` or `Failed`, never `Creating` forever |
| Warnings are errors | `TreatWarningsAsErrors`, `AnalysisLevel=latest-recommended`, nullable enabled, no `#pragma warning disable` without a linked issue |
| Every module has tests | xUnit v3 + `Orleans.TestingHost` + NSubstitute + Shouldly; a provider without a conformance-suite pass is not registered |

## Layer discipline

Dependencies flow strictly downward. A violation is a build break.

```
                    Hosts (Gateway, Identity, Portal SSR, Worker, Admin)
                                       │
        ┌──────────────────┬───────────┴────────┬──────────────────┐
   Providers.*        Portal (Angular)      Cli / Sdk          Admin
   (Compute, Data,          │                   │                │
    Network, Storage,       └────── OpenAPI ────┴────────────────┘
    Identity, Terminal…)                (generated)
        │
   CyberCloud.ResourceManager      ← provider registry, LRO, reconcile scheduler
        │
   CyberCloud.Authorization (ReBAC)  ·  CyberCloud.Tenancy  ·  CyberCloud.Metering
        │
   CyberCloud.Kubernetes            ← cluster connections, command builder, informers
        │
   CyberCloud.Core                  ← ids, results, errors, clock, serialization, contracts
        │
   Orleans · ABP · .NET 10
```

Hard rules:

- **`CyberCloud.Core` references no Orleans hosting, no Kubernetes client, no ABP application layer.**
  It is contracts, identifiers, error codes and pure functions. Grain *interfaces* live beside the
  module that owns them, not in a god-assembly.
- **A provider never talks to another provider's grains directly.** It goes through the resource
  manager, which is where authorization, quota and audit sit. Cross-provider references (a VM
  referencing a subnet) are resource IDs resolved by the manager, not grain references.
- **Nothing above `CyberCloud.Kubernetes` constructs a Kubernetes object literal.** Providers describe
  intent (a chart, values, a namespace, a resource id); the fabric renders it, labels it and applies it.
- **The portal has no privileged path.** It calls the same public REST API as the CLI, with the same
  token. There is no `/internal` the portal uses and the SDK does not. Platform-admin operations are
  ordinary resources under a `CyberCloud.Platform` provider, guarded by ReBAC, not a second API.

## Coding standards (C# 14 / .NET 10)

The intent, in full in `.editorconfig` and `Directory.Build.props`:

- **Grain interfaces are `IGrainWithStringKey`** for anything tenant-scoped, because that is the only
  key kind `Orleans.Multitenant` can carry a tenant in (verified against its source). GUIDs remain the
  *identifier*; the key is a composed string. `GrainKeys` is the one type allowed to format or parse
  one — [06 § Grain keys](06-tenancy-and-resource-model.md).
- **Every grain interface method returns `Task<Result<T>>` or `Task<Result>`**, never throws for a
  domain outcome. Exceptions are for bugs and infrastructure. The gateway maps `Result` to an Azure-shaped
  error body; a thrown exception maps to `500` and pages someone.
- **`[GenerateSerializer]`, `[Alias]` and explicit `[Id(n)]` on every wire type**, with `[Alias]`
  strings that are stable across renames — Survival's convention, and the reason a rolling silo upgrade
  works. An analyzer fails a `[GenerateSerializer]` type with no `[Alias]`.
- **`readonly record struct` for identifiers and value objects**; `sealed` by default; `internal` by
  default, `public` needs a reason.
- **No `async void`, no `.Result`, no `.Wait()`** — analyzer-enforced. Grain code is single-threaded
  per activation and blocking it deadlocks the silo.
- **`ValueTask` on grain interfaces only where Orleans supports it**; `Task` elsewhere. Do not
  micro-optimize the wire.
- **Naming: PascalCase for anything a caller can name; camelCase, no `_` prefix, for private state.**
  Accessibility modifiers written only when they change something.
- **`ILogger` with `[LoggerMessage]` source-generated methods.** No interpolated log strings, ever —
  they defeat structured logging and they are the single biggest source of PII in log stores.

## The quality bar, concretely

The plan is credible only if these hold at 1.0. Each is a test in the perf suite, not an aspiration.

- **Control-plane read** (`GET` a resource, warm grain, authorized): p99 **< 25 ms** at the gateway,
  measured with 1 000 000 resources across 10 000 tenants resident.
- **Control-plane write** (`PUT` accepted, operation created, 202 returned): p99 **< 60 ms**. The
  provisioning itself is asynchronous and measured separately.
- **ReBAC `Check`** on a 5-deep group graph with 10 000 members: p99 **< 10 ms** warm, **< 50 ms** cold.
- **Silo cold start to serving**: **< 20 s**, including Kubernetes membership join.
- **Rolling silo upgrade** of a 30-silo cluster with 2 000 000 active grains: **zero** failed tenant
  requests, measured by the gateway's own error counter.
- **Tenant onboarding** (create tenant → first resource created) on the in-house fabric: **< 15 min**,
  dominated by control-plane bootstrap, not by us.
- **A new managed service** — new chart, annotated values, provider registration, conformance suite —
  is **≤ 2 engineer-weeks** end to end including portal form and CLI verbs, with no change to
  `CyberCloud.ResourceManager`. This is the number that decides whether the catalogue grows.
- **Loss of the entire Redis tier** loses **no** durable-tier state and no acknowledged control-plane
  write; recovery is a warm-up, not a restore. Verified by a scheduled chaos run that flushes Redis in
  staging.
