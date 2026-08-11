# 02 — Technology Decisions

## Platform baseline

| Item | Choice | Note |
|---|---|---|
| SDK | .NET 10 | `global.json` pins `10.0.100` with `rollForward: latestFeature`, matching `~/Projects/Survival/Server` |
| TFM | `net10.0` | Single TFM everywhere. No multi-targeting, no netstandard |
| Language | C# 14, `LangVersion=latest` | See [00](00-vision-and-principles.md) for the subset |
| Solution | `CyberCloud.slnx` | Plus `.slnf` filters per area for fast IDE loads |
| Packages | Central Package Management | `Directory.Packages.props`, no floating versions |
| Frontend | Angular 22, Tailwind 4, zoneless, SSR | The library dictates the version, not the other way round — but ⚠ read it from **npm**, not from `~/Projects/Rikarin/xui`. xUI's CI bumps the published version without reflecting it back into the checkout. `@xui/* ^2.2.0`, peering `@angular/*: 22`. See ADR-017 |
| Node | ✅ **24 (Active LTS)** — decided 2026-08-11 | ⚠ Was "22 LTS", which is now **maintenance-only**. Four inputs: Angular 22 permits `^22.22.3 \|\| ^24.15.0 \|\| >=26.0.0`, so it does not decide; Node 22 is Maintenance, 24 is Active LTS, 26 is Current (LTS 2026-10-20); **xUI itself pins `engines.node: 24.x`**, which is the strongest signal and makes this less open than it looked; the dev host runs 26.5.0 and must keep working. 26 was rejected — a platform's portal should not build on a non-LTS release. Pinned in `portal/.nvmrc`, `.node-version` and `engines`; the check **warns locally, fails in CI** (`pnpm node:gate --strict`), because a blocked local install stops work over a version that builds fine while a drifted CI image silently produces unreproducible artifacts. ⚠ The CI image must be Node 24 — that half lives outside this repo |

## Dependency register

Versions verified against `api.nuget.org` on 2026-08-08. These go verbatim into
`Directory.Packages.props`. Anything not here needs an ADR.

### Orleans and hosting

| Package | Version | Used by | Why this and not X |
|---|---|---|---|
| `Microsoft.Orleans.Sdk` | 10.2.2 | every grain and contract project | Orleans 10 targets .NET 10 and is what Survival runs. Source-generated serializers, so no reflection at activation |
| `Microsoft.Orleans.Server` / `.Client` | 10.2.2 | silo hosts / gateway | |
| `Microsoft.Orleans.Clustering.Redis` | 10.2.2 | **local dev only** | ADR-004 — production membership is Kubernetes |
| `Orleans.Clustering.Kubernetes` | ⚠ **10.0.1** | silo hosts | `UseKubeMembership` writes silo entries as CRs in the silo's own namespace. Zero external dependencies for membership, which is the first half of "no single database" |
| `Microsoft.Orleans.Persistence.Redis` | 10.2.2 | hot tier | ADR-003 |
| `Microsoft.Orleans.Reminders.Redis` | 10.2.2 | reminder service | Sharded with the hot tier |
| `Microsoft.Orleans.Streaming` | 10.2.2 | everywhere | |
| ~~`Rikarin.Orleans.Streaming.NATs`~~ **`Microsoft.Orleans.Streaming.NATS`** | `$(OrleansVersion)-alpha.1` | streams | ⚠ **REPLACED.** The Rikarin package is not relevant; Orleans ships a first-party NATS provider built against Orleans 10, so ADR-005's fallback and [24](24-roadmap.md)'s 0.4 EM Phase-0 bump are both dropped. ⚠ **Version trap:** every release carries `-alpha.1`, and `10.2.2-rc.2.alpha.1` sorts *above* `10.2.2-alpha.1` under SemVer while being built against an Orleans RC — so the pin tracks `$(OrleansVersion)`, never "latest" |
| `Orleans.Multitenant` | 4.0.0 | every tenant-scoped grain | ADR-002. Targets Orleans 10 on .NET 10 per its README |
| `Microsoft.Orleans.Serialization.SystemTextJson` | 10.2.2 | grain storage serializer | Human-readable state in Redis is worth the bytes during the first year. Revisit against MemoryPack when a grain shows up in a profile |
| `Microsoft.Orleans.TestingHost` | 10.2.2 | tests | In-process cluster per test class |
| `Microsoft.Orleans.Persistence.AdoNet` | `$(OrleansVersion)` | durable tier | ⚠ **Was missing, and ADR-003 cannot be built without it** — the only source of `AddAdoNetGrainStorage`. It carries **no driver** (it loads `Npgsql` reflectively, so a typo in `Invariant` is a runtime `AggregateException`) and **no SQL** — see [05 § Durable](05-state-and-storage.md) |
| `Microsoft.Orleans.Hosting.Kubernetes` | `$(OrleansVersion)` | silo hosts | ⚠ **Was missing, and ADR-004 requires it** — the only source of `UseKubernetesHosting()`. A *different* package from `Orleans.Clustering.Kubernetes` above, by a different author: that one is the contrib membership provider, this one maps `SIGTERM` to a graceful `StopAsync` with grain migration |

### ABP — used where it earns its place

| Package | Version | Used by |
|---|---|---|
| `Volo.Abp.Autofac` | 10.6.0 | every host — module system + DI |
| `Volo.Abp.AspNetCore.Mvc` | 10.6.0 | gateway, identity host |
| `Volo.Abp.AspNetCore.SignalR` | 10.6.0 | gateway |
| `Volo.Abp.Ddd.Domain` / `.Application` | 10.6.0 | providers |
| `Volo.Abp.Authorization` | 10.6.0 | ⚠ **the attribute plumbing only** — the policy source is ours, ADR-007 |
| `Volo.Abp.EntityFrameworkCore.PostgreSql` | 10.6.0 | durable tier, billing ledger, identity |
| `Volo.Abp.AspNetCore.Serilog` | 10.6.0 | all hosts |
| `Volo.Abp.TestBase` | 10.6.0 | tests |

**Not used from ABP:** the multi-tenancy module (ADR-002 — Orleans.Multitenant owns tenancy and two
tenancy systems is worse than none), the permission-management module (ADR-007), the audit-logging
module (audit goes to the telemetry pipeline, not a SQL table), `Volo.Abp.Identity` (ADR-015), and
anything with a UI. **ABP is a module system and an application-service convention here, not a
framework we live inside.**

### Data, transport, Kubernetes

| Package | Version | Used by | Note |
|---|---|---|---|
| `StackExchange.Redis` | 3.1.13 | hot tier | Cluster-aware; hash tags carry the tenant, ADR-003 |
| `Npgsql` | 10.0.3 | durable tier | |
| `NATS.Client.Core` / `.JetStream` | 3.1.0 | event backbone | ADR-005 |
| `KubernetesClient` | 19.0.2 | `CyberCloud.Kubernetes` | Generic-object + server-side-apply support is what the command builder needs. ⚠ Survival pins 18.0.13; 19 changed the generic client surface — a small migration, done once, here |
| `Microsoft.AspNetCore.SignalR.Client` | 10.0.10 | CLI, tests | |
| `System.CommandLine` | 2.0.10 | `cyc` | Finally stable after years of preview |
| `OpenIddict.AspNetCore` | ⚠ **7.6.0** | identity host | ADR-015. The reasoning — newest stable, never a preview — is unchanged; 7.3.0 was a snapshot of "newest stable on 2026-08-08" and 7.4/7.5/7.6 have since shipped. 8.0 is still preview |
| `Fido2.AspNet` | ⚠ **4.0.1** | identity host | ⚠ **The "beta with no stable successor" claim was false** — 4.0.0 and 4.0.1 are both published stable. Still wrapped behind `IPasskeyService` so replacing it is one file |
| `Riok.Mapperly` | 4.3.1 | providers | Source-generated mapping; no AutoMapper reflection |
| `Polly` | 8.6.5 | fabric, **and `CyberCloud.Sdk`** | Retry/circuit-break on cluster connections. ⚠ Also the SDK's whole resilience layer, which is why rejecting `Azure.Core` cost no new dependency — see [21 § The .NET SDK](21-cli-and-sdks.md) |
| `Konscious.Security.Cryptography.Argon2` | 1.3.1 | identity host | ⚠ **Was missing.** .NET ships no Argon2 — `System.Security.Cryptography` has PBKDF2 and stops — so [11 § Credentials](11-identity.md)'s Argon2id was unbuildable. Chosen over `Isopoh` (stale, stops at net7.0) and `NSec` (better maintained, but its libsodium binding fixes parallelism at 1 and cannot express the specified `p=4`). Its `KnownSecret` is the pepper as RFC 9106's secret input |
| `Serilog.AspNetCore` + `.Sinks.OpenTelemetry` | 10.0.0 / 4.2.0 | all hosts | |
| `OpenTelemetry.*` | 1.17.0 | all hosts | Traces and metrics; `AddActivityPropagation()` on the Orleans builder so a trace crosses grain calls |

### Test and build

| Package | Version |
|---|---|
| `xunit.v3` | 3.2.2 |
| `NSubstitute` | 5.3.0 |
| `Shouldly` | 4.3.0 |
| `Testcontainers` | ⚠ **4.13.0** — pinned. "Latest at bring-up" is not expressible under the Central Package Management this same document mandates above, which forbids floating versions |
| `Nuke.Common` | 10.1.0 |
| `Aspire.Hosting.*` | 13.4.6 — ADR-014, **local development only** |
| `Microsoft.CodeAnalysis.CSharp` | 5.6.0 — `CyberCloud.Analyzers` builds against it |
| `Microsoft.CodeAnalysis.Analyzers` | 5.6.0 — the RS1xxx analyzer-authoring rules |
| `Microsoft.CodeAnalysis.CSharp.Analyzer.Testing` | 1.1.4 — the analyzer test harness |
| `Microsoft.CodeAnalysis.CSharp.Workspaces` | 5.6.0 — **transitive pin only**, see below |

⚠ **These four rows were missing, and their absence was a real gap rather than an oversight in
transcription.** Four documents assert "analyzer-enforced" — [00 § Coding standards](00-vision-and-principles.md),
[00 § Non-negotiables](00-vision-and-principles.md), ADR-002 below, and
[04 § Failure and upgrade](04-orleans-topology.md) — and this register listed no
analyzer-authoring or analyzer-testing package at all, so nothing in it could have been written.

⚠ **The version rule is a ceiling, not a preference.** `Microsoft.CodeAnalysis.*` must not exceed
the compiler that loads the analyzer. `global.json` rolls forward to SDK 10.0.302, whose `csc`
reports `5.6.0-2.26329.109`; 5.6.0 is the released build of that same compiler and is therefore the
pin. A newer Roslyn produces an analyzer the SDK silently fails to load.

⚠ **`Microsoft.CodeAnalysis.CSharp.Workspaces` is not referenced by anything.** It is pinned so that
central transitive pinning can lift it: `…Analyzer.Testing` 1.1.4 declares its Roslyn dependency as
a *floor* of 1.0.1, NuGet resolves a floor to the floor, and the .NET Framework-only 1.0.1 package
produces four `NU1701` warnings — which `MSBuildTreatWarningsAsErrors` makes fatal.

⚠ **Not `…Analyzer.Testing.XUnit`.** That variant binds to xUnit v2 and ADR-018 makes this
repository `xunit.v3`. The base package ships `DefaultVerifier`, which needs no test framework.

### Rejected / reference-only

| Thing | Verdict | Reason |
|---|---|---|
| Cozystack | **Reference only** | ADR-010 |
| OpenFGA / SpiceDB | **Reference only** | ADR-007 |
| `Rikarin/MalwareMultiScan` | **Reference only** | 2021, .NET Core 3.1, unmaintained since. The *backend abstraction* is the good idea; the code is five years stale |
| Keycloak | Not used | ADR-015. Cozystack uses it; we are an identity provider, so shipping someone else's is a strange product |
| Loki | Not used | ADR-016 |
| vcluster | Not used | ADR-009 |
| MediatR | Not used | Survival uses it at the gateway; here the gateway dispatches straight to grains and the indirection buys nothing. ⚠ Also: MediatR went commercial in 2025 |
| Hashicorp Vault, Redis (the trademarked one), MongoDB, Terraform | Not offered as managed services | ADR-011 — licences |
| AutoMapper | Not used | Also went commercial in 2025; Mapperly is source-generated and free |

---

## Architecture Decision Records

### ADR-001 — Orleans is the control plane; the Kubernetes API is a data plane

**Decision.** Desired state lives in grains. The Kubernetes API is written to, watched, and reconciled
*against*, but it is never the source of truth for what a tenant asked for.

**Rationale.** The obvious alternative — Cozystack's — is to make Kubernetes the control plane: a
tenant is a namespace, a resource is a custom resource, the API is an aggregated API server, and etcd
holds everything. It is elegant and it is much less code. It also:

- puts every control-plane read and write through one etcd, which is a single Raft group with a
  practical ceiling around 8 GB and low tens of thousands of writes per second — precisely the "single
  database bottleneck" the brief rules out;
- cannot represent a tenant whose cluster is somewhere else, because desired state and the cluster are
  the same object;
- makes authorization Kubernetes RBAC, which is role-based, cluster-scoped and cannot express
  "this user, on this resource, through this group, until Friday";
- makes a control-plane outage a cluster outage and vice versa.

Grains give per-entity single-threaded consistency without a lock manager, horizontal scale by adding
silos, and a natural home for the reconcile loop (a grain with a reminder). The cost is that we own the
durability story, which is ADR-003, and that Orleans expertise becomes a hiring requirement.

**Consequence.** Every resource has a `ResourceGrain` with `Desired`, `Observed` and `Provisioning`
state, and a reconcile pass is a grain method. The cluster can be wiped and rebuilt from grain state;
grain state cannot be rebuilt from the cluster.

### ADR-002 — Tenant id lives in the grain key; GUIDs remain the identifier

**Decision.** Every tenant-scoped grain is `IGrainWithStringKey`, and its key is produced by
`Orleans.Multitenant`'s `GetTenantGrainFactory(tenantId)`. The physical key is
`{tenantId}|{keyWithinTenant}`.

⚠ **The encoding was re-verified against `Orleans.Multitenant` 4.0.0 and this paragraph was partly
wrong.** Checked three independent ways — the decompiled assembly, the upstream
`Internal/Extensions.cs` at the commit the nuspec pins, and executing the package — with these
results:

| Claim | Verdict |
|---|---|
| `\|` is the separator | ✅ confirmed |
| `\|` is **doubled to escape** | ⚠ **partly refuted** — doubling applies to the **tenant id**, not to the key within the tenant. The inner key is copied **verbatim**: `ForTenant("t1").GetGrain(_, "a\|b")` yields `t1\|a\|b`, not `t1\|a\|\|b` |
| `~` prefixed when the inner key starts with `\|` or `~` | ✅ confirmed, and `~` elsewhere is untouched |
| *(not previously documented)* | ⚠ the **null-tenant** branch is a different encoding — no prefix, no `~` rule, and the whole key has its `\|` doubled. [06 § Grain keys](06-tenancy-and-resource-model.md) makes `IClusterConnectionGrain` null-tenant, so this branch is live |

The encoding is still lossless and injective — the terminator is the first *un*doubled `|`, and the
tenant id cannot contain one — so no inner key can forge a different tenant. That was tested
directly.

**This is the reconciliation of two requirements that appear to conflict.** The brief says "GUID as
ID"; `Orleans.Multitenant` only supports string keys. Both are satisfied: identifiers in the API, the
database, the SDK and the URL are GUIDs. The *grain key* is a composed string that contains them.

⚠ **Corrected: the key shapes are the table in [06 § Grain keys](06-tenancy-and-resource-model.md),
not the code block that used to stand here.** That block showed `IResourceGrain` keyed by
`{subscriptionId:N}/{resourceGroup}/{type}/{resourceId:N}`, which contradicted 06's
`res/{resourceId:N}`. **06 wins**, for the reason 06 itself gives: a resource grain keyed by GUID
makes a rename a metadata update rather than a grain migration, and a key containing the resource
group would make *moving* a resource a migration too. It also referred to a type `ResourceType` that
does not exist — the real one is `ResourceTypeName`.

One type formats and parses **every** key shape in 06's table — not just the resource one. The
earlier wording implied `ResourceKey` covered the lot while the block defined only a resource key,
leaving `sub/…`, `rg/…`, `idx/path/…`, `user/…`, `idx/email/…`, `op/…` and `cluster/…` with no home.

```csharp
// Formats and parses the key WITHIN a tenant. The tenant qualification itself is
// Orleans.Multitenant's job, per the encoding table above.
public static class GrainKeys
{
    public static string Resource(Guid resourceId)              => $"res/{resourceId:N}";
    public static string Subscription(Guid subscriptionId)      => $"sub/{subscriptionId:N}";
    public static string ResourceGroup(Guid subscriptionId, string name)
                                                                => $"sub/{subscriptionId:N}/rg/{name}";
    public static string User(Guid userId)                      => $"user/{userId:N}";
    public static string Operation(Guid operationId)            => $"op/{operationId:N}";
    public static string ClusterConnection(Guid clusterId)      => $"cluster/{clusterId:N}";
    public static string PathIndex(ResourceId id)               => $"idx/path/{Sha256Prefix(id.CanonicalPath)}";
    public static string EmailIndex(Guid tenantId, string email) => $"idx/email/{Sha256Prefix(tenantId, NormalizeEmail(email))}";
}
```

⚠ `PathIndex` takes a `ResourceId`, **not a string**. A `string` overload would accept `Path` exactly
as readily as `CanonicalPath`, and the difference between those two *is* the bug — see
[06 § Grain keys](06-tenancy-and-resource-model.md), which also defines `NormalizeEmail` and explains
why it is not `ToLowerInvariant()`.

⚠ `PathIndex` hashes the **canonical** path, not `ResourceId.Path`. The provider namespace is
case-preserving, so `CyberCloud.Cache/redis` and `cybercloud.cache/redis` would otherwise claim two
index entries for one name and defeat the two-phase create in
[06 § Two-phase create](06-tenancy-and-resource-model.md).

Nothing else in the codebase may concatenate a grain key, enforced by an analyzer that flags string
literals containing `|` in `GetGrain` arguments.

✅ **That analyzer exists: `CC1004`, in `src/CyberCloud.Analyzers`.** It covers interpolated strings
as well as literals, because `$"{tenant}|{key}"` is the same defect in the spelling somebody
actually writes, and it fires on the tenant-qualified `ForTenant(t).GetGrain(…)` overload too — the
qualification is applied *on top of* whatever is passed, so a `|` there still lands inside a
physical key. Only the literal text is inspected; a `|` arriving at run time through an
interpolation hole is out of reach of any compile-time rule, which is what
`GrainKeys.IsTenantQualificationSafe` and `GrainKeysTests.EveryGeneratedKeyIsSafeForTenantQualification`
are for.

⚠ `ResourceId` remains a separate type and keeps subscription, resource group, type and name — it is
the *address*, and [06 § Identifiers](06-tenancy-and-resource-model.md) is explicit that the address
and the identity answer different questions. What changed is only that the **grain key** is derived
from the resource GUID alone.

**Consequences.**
- `AddMultitenantCommunicationSeparation` is on. Cross-tenant grain calls throw
  `UnauthorizedAccessException` unless our `ICrossTenantAuthorizer` allows them. Exactly two things
  are allowed to cross: the platform-admin path (§ [06](06-tenancy-and-resource-model.md)) and
  Lighthouse-style delegation, both of which log every crossing.
- Storage providers are per-tenant instances. `configureTenantOptions` selects the Redis shard and the
  Postgres shard for that tenant — ADR-003.
- Streams are tenant-qualified and `TenantSeparatingStreamFilter` blocks the rest.
- **Platform-global grains** (the tenant directory, the shard map, the provider registry) are *null
  tenant* grains, are few, and are read-mostly. A null-tenant grain that accepts a write on a
  tenant-rate path is an architecture-test failure.

**Rejected alternative.** ABP's `ICurrentTenant` ambient-scope multi-tenancy. It is a filter on
queries; forget the filter and you have a cross-tenant leak that no test catches. Orleans.Multitenant
makes the tenant part of the address, so forgetting it is a `KeyNotFoundException` rather than a
breach. Two tenancy systems in one codebase is worse than either, so ABP's is off.

### ADR-003 — Two storage tiers, and Redis is not the system of record

**Decision.** Two named grain-storage providers, chosen per grain by attribute:

```csharp
[PersistentState("state", StorageTiers.Hot)]     // Redis Cluster
[PersistentState("state", StorageTiers.Durable)] // PostgreSQL, sharded
```

| Tier | Store | For | Loss tolerance |
|---|---|---|---|
| **Hot** | Redis Cluster, AOF `everysec`, 1 replica per shard | Sessions, live status, observed cluster state, caches, rate counters, ReBAC check cache, terminal sessions, metric aggregates | Rebuildable. Losing it costs a warm-up |
| **Durable** | PostgreSQL, sharded by tenant, synchronous replica | Tenants, subscriptions, resource desired state, users and credentials, ReBAC tuples, operations, billing ledger, audit cursors, cluster connections | **Zero.** An acknowledged write survives the loss of any single node |

**Why not Redis for everything, as the brief suggested.** Redis with `appendfsync everysec` can lose
up to one second of acknowledged writes when a primary dies uncleanly, and `WAIT` does not make it
durable — it makes it *replicated*, and the replica has the same fsync window. That is the correct
trade for a session. It is the wrong trade for "which tenant owns this subscription", because the
failure mode is not a slow page, it is a resource with no owner and a support ticket nobody can answer.

**Why not Postgres for everything.** Because then it *is* a single database, or a sharded one with
Redis-latency requirements it cannot meet. The hot tier is what makes the p99 numbers in
[00](00-vision-and-principles.md) reachable.

**Neither tier is global.** Both are sharded by tenant, and a shard is added by adding an instance and
assigning new tenants to it — never by rebalancing. [05](05-state-and-storage.md) has the shard map,
the rebalancing story (there isn't one, deliberately), and the Redis hash-tag scheme that keeps a
tenant's keys on one shard so `MGET` across a tenant is one round trip.

**Enforcement.** An architecture test walks every `[PersistentState]` and fails when a grain type on
the durable list is bound to Hot, or when a grain reachable from the gateway's high-rate paths is
bound to Durable without a `[HotPathExempt]` attribute carrying a reason.

### ADR-004 — Membership is Kubernetes; there is no clustering database

**Decision.** `UseKubeMembership()` in production, exactly as Survival does. Redis clustering only in
local development where a Kubernetes API is not present.

**Rationale.** Membership is the one thing every silo writes to on a timer. Putting it in Redis or SQL
gives a store whose outage takes down the cluster, and whose write rate is O(silos × heartbeat).
Kubernetes membership writes silo entries as custom resources in the silo's own namespace — the API
server is already a dependency of every pod, already HA, and already the thing that would have killed
us anyway. It also means a silo that Kubernetes has evicted is a silo Orleans knows is gone.

**Cost.** The silo needs RBAC to read/write its membership CRs, and `UseKubernetesHosting()` so
Orleans learns its pod identity. ~~Both are three lines.~~

⚠ **CORRECTED: "three lines" is wrong, and the half it leaves out is the half that constrains the
install.** Measured while writing `deploy/bootstrap/20-rbac.yaml` and `deploy/bootstrap/10-orleans-crds.yaml`,
with every verb read off the shipped assemblies rather than copied from a sample:

| | Objects | Scope | Who can apply it |
|---|---|---|---|
| The grant | 2 `Role`s, 3 `ServiceAccount`s, 2 `RoleBinding`s | Namespaced | The platform's own namespace |
| **The schema** | `silos.orleans.dot.net`, `clusterversions.orleans.dot.net` | **Cluster** | ⚠ Only an identity with `apiextensions.k8s.io` rights — which no Cyber Cloud ServiceAccount has |

**The count is not the finding; the scope split is.** `20-rbac.yaml` grants the silo nothing on
`apiextensions.k8s.io`, on purpose, so a compromised silo cannot rewrite the schema of its own
membership store. The consequence is that the CRD half can never be self-service: it is an operator
step that runs before any Cyber Cloud identity exists, and it is why `deploy/bootstrap/` is a directory
you run by hand rather than a chart the platform installs. The definitions are also not in
`charts/platform` — Helm's `crds/` directory is installed once and never upgraded or deleted, so a
chart-owned CRD is a CRD nobody can change. See [09 § The platform's own cluster](09-kubernetes-fabric.md),
where phase 0 now says so.

Three ServiceAccounts rather than one because the three identities want different things: the silo
writes membership, the gateway only reads it, and the durable-schema job talks to PostgreSQL and to
nothing else — so it is granted nothing and its token is not even mounted. One shared account would
hand the job the silo's membership rights for no reason.

Also: our own silos must run *somewhere*, and
that somewhere is a cluster — see § [09 § Bootstrap](09-kubernetes-fabric.md) for the chicken-and-egg,
which is resolved by deploying to an existing cluster first, as the brief already decided.

### ADR-005 — NATS JetStream is the event backbone and the stream provider

**Decision.** One NATS cluster per region carries: Orleans streams, the resource-change event log, the
reconcile work queue, telemetry ingest, and the cluster-informer fan-out. It is also offered as a
managed service, which means we operate it well or we find out early.

**Rationale.** Kafka is the alternative and is heavier to operate for what is mostly at-least-once
fan-out with short retention. JetStream gives durable consumers, per-subject retention, and
subject-based sharding that maps naturally onto `cc.{tenant}.{provider}.{resource}`. We already have
`Rikarin.Orleans.Streaming.NATs`.

⚠ **The dependency is on Orleans 9 and must be moved to 10.** This is a real, scheduled task, not an
assumption. If it slips past M1, the fallback is `Microsoft.Orleans.Streaming.Memory` for in-silo
streams plus direct `NATS.Client.JetStream` for the event log — which is where the value is anyway.

**Ordering guarantee, stated once.** Per-subject ordering only. Anything needing global order (the
billing ledger) uses the durable tier and a per-tenant sequence, not the stream.

### ADR-006 — ABP is a module system, not a framework we live inside

**Decision.** Take ABP's `AbpModule` + `[DependsOn]` composition, Autofac DI, `ApplicationService`
base, the `IRepository` abstraction for the durable tier, and the ASP.NET Core integration. Take
nothing whose state model conflicts with grains.

**Rationale.** ABP's value here is that a host is `AddApplicationAsync<TModule>()` and every provider
is a module that declares its dependencies — which is exactly the composition property a
twenty-provider catalogue needs, and exactly what Survival uses it for. ABP's *other* half — a
DDD/EF-Core application built on ambient tenancy, permission tables and audit tables — is a different
architecture from grains, and mixing them produces two sources of truth per entity.

**The line, concretely.** An ABP `ApplicationService` in a provider is allowed to: validate input,
resolve a `IGrainFactory`, call grains, and map results. It is *not* allowed to hold a `DbContext`
that touches resource state. The durable tier is reached through the grain, never around it.

### ADR-007 — ReBAC is written here, in C#, over grains

**Decision.** `CyberCloud.Authorization` implements a Zanzibar-derived relationship-based
authorization engine: a schema of object types and relations, relation tuples, and `Check`, `Expand`,
`ListObjects` and `ListSubjects` operations. Tuples live in the durable tier, sharded by tenant; the
check path is grain-resident with a bounded cache.

**Rationale for building rather than adopting.** OpenFGA and SpiceDB are both good, and both are
*separate distributed systems with their own datastore*. Adopting one means: a second scaling story, a
second sharding story, a second multi-tenancy story, network round trips on the hottest path in the
platform, and a consistency token we have to thread through our own API anyway. The brief already asks
for it in C#, and the reason it is the right call is that **our tuples are already sharded by tenant
and already sitting in grains** — the hard part of Zanzibar is the storage and replication, and we
have solved it once already for everything else.

**What is taken from the papers rather than invented:** the tuple shape
(`object#relation@subject`), userset rewrites (union / intersection / exclusion / tupleset-to-userset),
the consistency token (our `AuthorizationToken` is a per-tenant version stamp, not a Spanner
timestamp), and the Leopard-style denormalized membership index for deep group nesting.

**What is honestly harder than it looks**, and is called out in [07](07-rebac-authorization.md)
rather than discovered later: `ListObjects` is not a graph walk, it is a reverse index; negative
relations break monotonic caching; and a check that is fast at depth 3 is a timeout at depth 12
without the index. The plan budgets 4 EM, not 1.

**Azure RBAC compatibility.** Role assignments at a scope are expressed as ReBAC relations, so
`Owner` on a resource group is `resourceGroup:X#owner@user:Y` and inheritance is a rewrite rule. The
API can present Azure-shaped role assignments over a ReBAC store; the reverse is not possible, which
is why this direction was chosen.

### ADR-008 — Object storage speaks S3, not the Azure Blob API

**Decision.** `CyberCloud.Storage` exposes the S3 API. Azure Blob's REST dialect is not implemented.

**Rationale.** Every SDK, CLI, backup tool, CI runner and framework in existence speaks S3. Azure Blob
compatibility would buy us customers migrating *from Azure specifically*, at the cost of a second API
surface with its own semantics (blocks vs parts, leases vs no leases, a different auth signature).
SeaweedFS already implements S3. This is the one place where "Azure-shaped" is the wrong instinct and
we should say so out loud rather than discover it during the first integration.

### ADR-009 — In-house clusters are Cluster API + Kamaji + KubeVirt

**Decision.** A Cyber Cloud–hosted Kubernetes cluster is: a `KamajiControlPlane` (control plane
components as pods in the management cluster, with a dedicated etcd per tenant from
`etcd-operator`), `KubevirtMachine` worker nodes (real VMs, real kubelets, real isolation), stitched
by Cluster API. This is Cozystack's stack and it is taken deliberately.

**Rejected alternative: vcluster.** vcluster is faster to provision (seconds vs minutes), far denser,
and genuinely excellent — for *developer environments*. It syncs pods to the host cluster, which means
a tenant's workload shares a kernel and a node with other tenants' workloads. For a product where a
tenant is a paying stranger, the isolation boundary must be a VM. The performance comparison is real
and vcluster wins it; it is losing on the axis that matters here.

**⚠ The honest cost of Kamaji**, from the same comparison: provisioning is minutes, per-tenant
overhead is a control-plane pod set plus an etcd plus VMs, and the platform around it — UI, SSO,
observability, day-2 — is substantial DIY. That DIY *is this project*, so it is budgeted rather than
surprising. What it means practically is that **tenant cluster creation is a minutes-long LRO with a
progress model**, not a spinner, and the portal must be designed for that from the first sketch.

**Both paths, one interface.** A BYO cluster and a Kamaji cluster present the same
`IKubeClusterConnection`. A provider that behaves differently between them is a bug.

### ADR-010 — Cozystack is read, not forked; here is exactly what is taken

**Decision.** No Cozystack code is vendored. Three things are taken:

1. **The operator selection per managed service.** CloudNativePG for Postgres, Altinity for
   ClickHouse, Strimzi for Kafka, spotahome for Redis/Valkey, mariadb-operator, RabbitMQ Cluster
   Operator, OpenSearch operator, FerretDB, Qdrant, Harbor, SeaweedFS, LINSTOR/Piraeus, KubeVirt+CDI,
   Kube-OVN, Cilium, MetalLB, Kamaji, etcd-operator, Velero. This is a survey we would otherwise
   repeat badly, and it is the single most valuable thing in that repository.
2. **The annotated-`values.yaml` → JSON Schema → form → docs pipeline.** Their charts carry
   `## @param {type} name - description` annotations that generate `values.schema.json`, which
   generates the dashboard form. We take the *mechanism* and reverse its *direction* — see
   "Which end authors the schema" below.

   > ⚠ **CORRECTED 2026-08-11.** This clause read: "We do the same, with the schema also generating
   > the OpenAPI body, the CLI flags and the SDK model — ADR-012." Taken literally that makes the
   > chart the source for the API, and ADR-012 says the C# registry is. Two clauses in one document
   > each claiming to be the one source. Found by auditing the two against the code rather than
   > against each other.
3. **The sizing-preset vocabulary** (`t1.micro`, `c1.large`, `s1.xlarge`, …). Users understand
   instance families; inventing our own names would be gratuitous.

**Not taken:** the aggregated API server, the namespace-per-tenant model, `HelmRelease` as desired
state, FluxCD as the reconciler, Keycloak, Talos as a requirement, Outline as the VPN.

**Their charts are a starting point, not a dependency.** Where a chart is close, fork it into
`charts/` with the upstream commit recorded in a `SOURCE` file. A drifting vendored chart with no
provenance is how a platform ends up unable to upgrade Postgres.

#### Which end authors the schema — DECIDED 2026-08-11

**The C# `ResourceSchema` is authored. The chart's `@param` annotations are generated from it and
diffed.** A new tenant-facing field is added in C# by the provider author; `Build.Charts` rewrites the
annotated block in `values.yaml` and fails on drift, exactly as `Build.Generate` already does for the
four ADR-012 surfaces.

**Built 2026-08-12** — see the note under the ADR-012 table for what landed, what is refused rather
than dropped, and why the gate is currently vacuous.

**What the audit found, because two of the three obvious reasons for this decision are wrong.**

- ✗ *"The chart holds facts the registry cannot."* **Refuted.** Every annotation kind
  `charts/managed/postgres/values.yaml` uses has a `SchemaProperty` field: `@enum` → `AllowedValues`,
  `@required` → `Required`, `@range` → `Minimum`/`Maximum`, `@widget` → `Widget`, `@secret` →
  `Secret`, `@immutable` → `Immutable`. This document asserted the opposite until the audit checked.
- ✓ *The gap runs the other way.* `SchemaProperty` also carries `Format`, `Pattern`,
  `MinLength`/`MaxLength`, `DefaultJson`, `ExampleJson`, `Nullable` and `ElementKind` — seven facts
  the annotation vocabulary has no syntax for. Authoring in the chart would mean growing seven new
  annotation kinds before it stopped being lossy.
- ✓ *Not every resource type has a chart.* A chart-authored schema has no answer for a resource that
  renders no Helm release, and the first provider in the tree is one.

**The 10 rows that are not API at all.** Of 36 `@param` rows in that chart, 10 are `@internal`: Helm
plumbing (`nameOverride`), reconciler-injected identity (`platform.*`, seven rows) and an operator
escape hatch (`imageName`). Generation covers the other 26; `@internal` rows stay hand-written in the
chart, because they are rendering inputs and a resource body has no place for them. So the two files
are sources for **different things** that overlap on 26 rows, not two sources for one thing.

**How this went unnoticed.** The overlap is zero resource types wide today.
`charts/managed/postgres/conformance.yaml` declares `CyberCloud.DBforPostgreSQL/servers` and no C#
provider declares that type — it appears in `src/` only in test fixtures. `Build.Charts` checks
`SOURCE`, `conformance.yaml`'s presence and its `resourceType` against `Chart.yaml`, and never opens a
registry. Its own header comment describes the parse result as "the shape a `ResourceSchema` would be
built from": the seam was seen and left open. It becomes 26 rows wide the moment the Postgres provider
lands.

### ADR-011 — The licence audit, done once, written down

Offering software *as a service* is exactly the use that several 2023–2025 licence changes exist to
prevent. This is a product-blocking category of mistake, so it is a decision record rather than a
footnote.

| Wanted | Licence | Verdict |
|---|---|---|
| Redis (≥ 7.4) | RSALv2 / SSPL | ✗ — **Valkey** (BSD-3) instead. API-compatible; say "Valkey" on the product page |
| HashiCorp Vault (≥ 1.15) | BUSL | ✗ — **OpenBao** (MPL-2.0, Linux Foundation fork) |
| MongoDB | SSPL | ✗ — **FerretDB** (Apache-2.0) over Postgres. ⚠ A compatibility layer, not MongoDB; state the supported subset explicitly |
| Terraform (≥ 1.6) | BUSL | ✗ as a managed service. We *publish a provider* for it, which is fine |
| Elasticsearch | SSPL/Elastic | ✗ — **OpenSearch** (Apache-2.0) |
| Grafana | AGPL-3.0 | ⚠ Offerable as a managed instance (we distribute, we do not modify). Our *portal* must not embed or link Grafana code — it embeds rendered dashboards by URL |
| Harbor, KubeVirt, Kube-OVN, Cilium, CloudNativePG, Strimzi, SeaweedFS, Kamaji, LINSTOR¹ | Apache-2.0 | ✓ |
| ClamAV | GPL-2.0 | ✓ — separate process, separate container, no linking |

¹ ⚠ **LINSTOR is GPL-3.0 and DRBD is GPL-2.0**, and LINBIT's commercial support is how the project is
funded. Running them is fine; a support contract is a business decision to make before the first
customer's data is on DRBD, not after.

**Enforcement.** A build gate runs a licence scan over the chart set and the container images in the
platform bundle, and fails on any SSPL/BUSL/AGPL image outside an allow-list with a written reason.

### ADR-012 — The provider registry is the one source; five surfaces are generated

**Decision.** A resource provider declares, in C#, its namespace, its resource types, their API
versions, and a JSON Schema per version. From that registry a build step generates:

| Surface | Generated |
|---|---|
| OpenAPI 3.1 document | Paths, bodies, LRO headers, error shapes |
| `cyc` CLI | Verb tree, flags, help, completion |
| .NET SDK | Clients, models, `Operation<T>` pollers |
| Portal forms | Angular reactive forms + xUI controls from the schema, with `x-cybercloud-*` hints for widgets (a `storageclass` picker, a region picker) |
| Chart `@param` annotations | The non-`@internal` block of a managed chart's `values.yaml`, rewritten in place and diffed — ADR-010 § Which end authors the schema, DECIDED 2026-08-11. `ChartAnnotationEmitter` + `ChartSurfaces`, gated by `Build.Charts` |

> ⚠ **CORRECTED 2026-08-11.** The heading said *four* surfaces and the table listed four. ADR-010
> clause 2 separately made the chart's annotations the source that generates "the OpenAPI body, the
> CLI flags and the SDK model", which is a fifth surface pointing the other way. Both clauses claimed
> to be the one source; neither cited the other. Found by auditing each against the code — the chart
> and the registry have never been compared by anything, because no resource type has both.

> **BUILT 2026-08-12.** The emitter is
> `src/CyberCloud.ResourceManager.Contracts/Generation/ChartAnnotationEmitter.cs` and the gate is
> `build/Build.Charts.cs`. Three things about it are worth knowing before reading the code:
>
> * **It reads the registry, not the OpenAPI document**, and it is the only one of the five that
>   does. docs/plan/21 § Generation's one hop exists so the compatibility diff over the published
>   document covers the CLI, the SDK and the forms. It cannot cover this one: the pairing fact is
>   `ResourceTypeRegistration.Chart`, which no emitted document carries, so there is nothing to read
>   a pairing back out of. Reading the registry also keeps *declaration order*, which every document
>   destroys by sorting `properties` ordinally — a `values.yaml` opening with `backup` and closing
>   with `version` is a configuration file nobody can read.
> * **Seven `SchemaProperty` members are refused rather than dropped.** `Format`, `Pattern`,
>   `MinLength`, `MaxLength`, `ExampleJson`, `Nullable` and a non-text `ElementKind` have no
>   annotation syntax — the gap this ADR already names — so a schema declaring one is a *generation
>   failure* naming the property and the directive that would close it. The vocabulary was
>   deliberately not grown for them: six new directives in `build/Build.Charts.cs`, a project with no
>   test suite, serving zero current consumers, is untested code that nothing exercises. It grows
>   with the first provider that needs it, at which point there is a chart to exercise it against.
> * **The gate is loud at zero pairs, which is the state it is in today.** No resource type in the
>   tree has both a chart and a registry declaration, so `Build.Charts` reports "1 managed chart, 0
>   registry types naming a chart, 0 pairs compared" as a warning with both halves of the mismatch
>   named — the `GateStatus.Vacuous` convention. It is a pass and it is worth nobody's trust yet.

**Rationale.** Four hand-written surfaces over twenty providers is eighty artifacts that drift. This is
the mechanism behind the *2 engineer-weeks per managed service* target; without it that number is
fiction. It is also the mechanism behind the "portal has no privileged path" rule, because a surface
that is generated cannot quietly grow a back door.

**Cost, stated honestly.** A generated portal form is worse than a hand-written one for the ten
resources people use daily. The escape hatch is per-type **form overrides**: a generated form is the
default and a hand-written Angular component may replace it, keyed by resource type and version. The
generated OpenAPI, CLI and SDK have no such escape hatch.

### ADR-013 — Server-side apply, mandatory labels, type-state builder

**Decision.** All writes to a managed cluster are server-side apply with a stable field manager
(`cybercloud/{provider}`). Every object carries:

```
cybercloud.io/tenant-id, /subscription-id, /resource-group,
/resource-id, /resource-type, /api-version, /managed-by
```

The builder makes omission impossible:

```csharp
var cmd = KubeCommand.For(connection)   // → INeedsTenant
    .WithTenantId(tenantId)             // → INeedsResource
    .WithResourceId(resourceId)         // → IKubeCommand   ← Build() appears only here
    .InNamespace(ns)
    .Apply(helmRelease);
```

`Build()` and `Apply()` exist only on the fully-qualified interface, so an unlabelled object does not
compile. Belt and braces: a validating admission policy on every managed cluster rejects unlabelled
objects in tenant namespaces, because a cluster we manage may also be written to by the tenant.

**Why this matters more than it looks.** These labels are how billing attributes a pod, how the
reconciler finds orphans, how deletion is complete, and how a support engineer answers "whose is
this". Getting them on 99 % of objects is worth nothing; the 1 % is what you page about.

### ADR-014 — Aspire is for local development only

**Decision.** `CyberCloud.AppHost` uses .NET Aspire to bring up Redis, Postgres, NATS, a `k3s` in
Docker, and every host, for `dotnet run`. Production deployment is Helm charts and Kubernetes
manifests. Aspire's deployment story is not used.

**Rationale.** Aspire is the best local-orchestration experience .NET has and Survival already proves
the pattern. Its production path targets Azure Container Apps and Kubernetes generation that is less
capable than the charts we need anyway. Using it for both would make the local topology and the
deployed topology the same file, which sounds good and means the local file grows production concerns.

### ADR-015 — OpenIddict, not IdentityServer, not Keycloak, not ABP Identity

**Decision.** `CyberCloud.Identity` implements OAuth 2.1 / OIDC on OpenIddict, with our own user,
group, credential and session grains.

**Rationale.** Duende IdentityServer is commercially licensed above a revenue threshold and this
platform is a business. Keycloak is a whole Java application with its own database, its own clustering,
and its own admin model — running it means our identity system's scaling story is not ours, which is
unacceptable when identity is on the hot path of every request. ABP Identity is EF-Core and
table-shaped, which conflicts with ADR-001. OpenIddict is a library: it handles the protocol, we own
the stores, and the stores are grains.

⚠ **Pinned at 7.3.0.** OpenIddict 8.0 is in preview at plan time. An authorization server is the last
component in the system that should run a preview build.

### ADR-016 — VictoriaMetrics and ClickHouse; not Prometheus TSDB, not Loki

**Decision.** Metrics → VictoriaMetrics (cluster mode, per-tenant via the native multi-tenant
`accountID`). Logs and traces → ClickHouse, one database per tenant. Both behind
`CyberCloud.Monitor/workspaces`.

**Rationale.** Prometheus's own TSDB is single-node and its remote-write ecosystem exists because of
that; VictoriaMetrics is the drop-in that is multi-tenant natively, which is the property we need and
the one Prometheus does not have. Loki — Cozystack's choice — indexes labels only, so the query
"find every log line carrying this correlation id, across this tenant, in the last hour" is a brute
scan. That query is the single most common thing anyone does in a support ticket, and ClickHouse
answers it in a bounded time with a skip index. We also need ClickHouse for the resource-graph
projection and for billing aggregation, so it is not an extra component.

### ADR-017 — One design system: xUI. No second component library

**Decision.** The portal is Angular 22, zoneless, SSR, styled by Tailwind 4, built from
[`@xui/*`](https://xuijs.org) — 90 components, one npm package each, themed from a token layer. No
Angular Material, no PrimeNG, no per-page bespoke widgets that duplicate an xUI component.

**Rationale.** xUI is in-house and already carries the hard parts a cloud portal needs and that
general libraries do not: `data-table`, `dock-manager`, `omnibar`, `node-graph`, `tree`, `transfer`,
`splitter`, `code-block`, `rich-text-editor`, `date-range-picker`, `echarts`. A cloud portal is a
data-table-and-form application with a topology view, and that is the exact set. Its `dark:`-free
token model also means the portal themes by changing CSS variables rather than by auditing 400 templates.

**Consequence, and it runs the other way too.** Where the portal needs a component xUI does not have,
**it is built in xUI and released there**, not in the portal. The portal is xUI's largest consumer and
therefore its best forcing function — the same relationship Vixen's editor has to its UI framework.

**⚠ Version coupling, and where to read it from.** The portal does not choose the Angular version; it
follows xUI, and an Angular major upgrade is an xUI task first. That much is unchanged.

What *is* corrected: **the local checkout at `~/Projects/Rikarin/xui` is not the contract.** xUI's CI
increments the package version at publish time and does not reflect it back into the repository, so
the working tree is behind what consumers actually resolve. Read the registry, not the repo.

| | Local checkout | Published on npm (2026-08-11) |
|---|---|---|
| Version | — (workspace) | `@xui/* ` **2.2.0** (`next`: 2.2.0-alpha.0) |
| Tailwind | `^4.3.3` in devDependencies | **not a peer dependency at all** |
| Other peers | — | `clsx >=2`, `luxon >=3`, `rxjs >=7`, `tailwind-merge >=3`, ⚠ `@ng-icons/*: 34` |

⚠ **CORRECTED AGAIN, 2026-08-11: the portal is NOT free within Angular 22.x.** An earlier version of
this note read `@xui/core`'s peers, saw `@angular/*: 22`, and generalised. **The peer ranges are not
uniform across the packages**, and the exceptions are in the M1 shell:

| Package | Peer | |
|---|---|---|
| `@xui/panel-stack`, `popover`, `tooltip`, `breadcrumb` | `@angular/common: 22.0.8` | **exact** |
| `@xui/echarts` | `@angular/cdk: 22.0.6` | **exact, and a different version** |
| `@xui/core` and the rest | `@angular/*: 22` | the major range this note used to claim for all of them |

`@angular/common@22.0.8` peers `@angular/core@22.0.8` exactly, so **one exact peer drags the whole
framework to a point release**. With `@angular/*` at the 22.1.1 head and `strict-peer-dependencies`,
`pnpm install` **fails**. The portal therefore pins `@angular/* = 22.0.8`, `@angular/cdk = 22.0.6`
and `@ng-icons/* = 34.0.0` (the registry head is 35.0.1, and `@xui/*` peers `34`).

The lesson generalises past this row: **reading one package's manifest is not reading the library's
contract.** `@xui/*` is ~92 independently-published packages, and a claim about "the peers" has to be
checked across the set the portal actually imports.

So the portal depends on `@xui/* ^2.2.0` from the registry at a **pinned** Angular point release. The
"90 components, one npm package each" claim is confirmed — the checkout carries 92 libraries under
`libs/ui`, and every component this plan names by hand (`data-table`, `dock-manager`, `omnibar`,
`node-graph`, `splitter`, `code-block`, `rich-text-editor`, `date-range-picker`, `echarts`,
`transfer`, `tree`) exists. ⚠ Do not confuse the scoped `@xui/*` packages with the unrelated
unscoped `xui` package on npm.

### ADR-018 — Tests are siblings, and the Orleans test host is the default

**Decision.** `Foo/` and `Foo.Tests/` sit next to each other. Grain behaviour is tested against a real
in-process `TestCluster` (`Microsoft.Orleans.TestingHost`) with real storage providers backed by
Testcontainers, not against mocked `IGrainFactory`.

**Rationale.** Mocking `IGrainFactory` tests the mock. The failure modes that matter in an Orleans
system — reentrancy, activation races, serialization drift across a rolling upgrade, storage etag
conflicts — are only visible with a real cluster and real storage. A `TestCluster` starts in about a
second, which is cheap enough to make this the default rather than the exception.

⚠ The one thing this does *not* cover is multi-silo failover, because a `TestCluster` is in-process.
That is covered by a nightly chaos job against the staging cluster — [23](23-build-ci-and-testing.md).

### ADR-019 — Cilium is the primary CNI and replaces MetalLB, with two exceptions

**Decision.** Cilium is the CNI on the platform cluster and in the managed-cluster bundle, with
`kubeProxyReplacement: true`. **Cilium LB-IPAM + BGP Control Plane** allocate and announce platform
service VIPs; **MetalLB is not installed** where a BGP peer exists. Kube-OVN runs alongside with
`ENABLE_LB=false` and `ENABLE_NP=false`, providing tenant VPC networking only.

**What MetalLB was actually doing, and who takes each part.** The question "can Cilium replace
MetalLB" only has an answer once the three jobs are separated:

| Job | Owner | Note |
|---|---|---|
| Allocate + announce **platform** service VIPs (gateway, identity, portal, ingress) | **Cilium LB-IPAM + BGP CP** | The one MetalLB was genuinely doing. Cilium takes it |
| Allocate + announce **tenant VPC** public addresses | **Kube-OVN EIP/FIP/VpcNatGateway** | Never MetalLB's. An address terminating on an OVN logical router is invisible to a host-network speaker |
| `Service type=LoadBalancer` **inside a tenant's own cluster** | Whatever the bundle ships there | The tenant's cluster, the tenant's choice |

**Why this is a change of shape, not just a component swap.** Cilium LB-IPAM only *allocates*; the
documentation is explicit that it "doesn't provide load balancing services by itself" and must be
paired with BGP CP or L2 Announcements to advertise. Both of those depend on Cilium owning the service
datapath. So swapping MetalLB for Cilium is only available if Cilium is the CNI — which is why this
ADR also settles the CNI question, and why the earlier draft of [14](14-networking.md) (Kube-OVN as CNI,
Cilium chained for policy) could **not** have made this swap. In `chainingMode: generic-veth`, Cilium
is not the datapath owner, kube-proxy replacement is not the supported configuration, and Cilium's own
docs note BGP CP "does not program the datapath" — it advertises a route to traffic something else
must then handle.

**Rejected: Cilium L2 Announcements.** It is the obvious MetalLB-L2 replacement and it is the wrong
choice at our scale, on the documented numbers rather than on taste:

- Still **beta**.
- **Requires** kube-proxy replacement.
- Leader election is per-service and generates continuous API traffic at
  `QPS = #services × (1 / leaseRenewDeadline)` **per node**. At the default 15 s lease that is ~32.5
  QPS per node for **65 services**, against a default client rate limit of 5 QPS. A platform with
  thousands of services would be a self-inflicted API-server denial of service.
- Incompatible with `externalTrafficPolicy: Local`, and it announces from one node so there is no
  pre-cluster balancing.

⚠ **The exception, and it is a real one: if the datacentre fabric is L2-only with no BGP peer, keep
MetalLB in L2 mode.** MetalLB's L2 speaker uses one memberlist-based election for the whole speaker
set rather than a Kubernetes Lease per service, so it does not carry that QPS curve. The rule is
therefore: **BGP available → Cilium; L2-only → MetalLB.** Not "Cilium always".

**Second exception, kept open rather than decided: BGP capability.** MetalLB's BGP backend moved to
**FRR-K8s**, which is FRR — BFD, route maps, communities, multiple address families. Cilium BGP CP
uses GoBGP internally and exposes a deliberately smaller surface (`CiliumBGPClusterConfig`,
`CiliumBGPPeerConfig`, `CiliumBGPAdvertisement`, `CiliumBGPNodeConfigOverride`). For "peer with the
top-of-rack and advertise these service IPs" it is GA and sufficient. If the network team's design
needs BFD sub-second failover or non-trivial route policy, MetalLB + FRR-K8s is the more capable
speaker and running it *for BGP only* alongside Cilium is legitimate. **This is question 11 in
[25](25-risks-and-open-questions.md) and it is the network team's call, not ours.**

**What the swap buys.** One fewer controller, one fewer CRD family, one fewer speaker DaemonSet on
every node, one address-pool model instead of two, and — the operational one — service IP announcement
and network policy share an identity model and one flow-log pipeline, so "why is this VIP not
reachable" is answered in Hubble rather than by correlating two components.

**Consequence for Envoy Gateway.** Cilium ships its own Gateway API implementation, and Cozystack
enables it (`gatewayAPI.enabled: true`, with an Envoy DaemonSet). [14](14-networking.md) still names
**Envoy Gateway** for the L7 tier because `CyberCloud.Network/applicationGateways` needs per-tenant
listener isolation and a Coraza WAF filter chain, and a shared Cilium-managed Envoy is a harder place
to put both. ⚠ This is worth a measured comparison before the M2 application-gateway work rather than
a decision taken here — it is the one place where "use Cilium for that too" may also be right.
