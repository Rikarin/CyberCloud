# 02 — Technology Decisions

## Platform baseline

| Item | Choice | Note |
|---|---|---|
| SDK | .NET 10 | `global.json` pins `10.0.100` with `rollForward: latestFeature`, matching `~/Projects/Survival/Server` |
| TFM | `net10.0` | Single TFM everywhere. No multi-targeting, no netstandard |
| Language | C# 14, `LangVersion=latest` | See [00](00-vision-and-principles.md) for the subset |
| Solution | `CyberCloud.slnx` | Plus `.slnf` filters per area for fast IDE loads |
| Packages | Central Package Management | `Directory.Packages.props`, no floating versions |
| Frontend | Angular 22, Tailwind 4, zoneless, SSR | Matches `~/Projects/Rikarin/xui` exactly — the library dictates the version, not the other way round |
| Node | 22 LTS, pnpm | xUI is a pnpm workspace; the portal joins that convention |

## Dependency register

Versions verified against `api.nuget.org` on 2026-08-08. These go verbatim into
`Directory.Packages.props`. Anything not here needs an ADR.

### Orleans and hosting

| Package | Version | Used by | Why this and not X |
|---|---|---|---|
| `Microsoft.Orleans.Sdk` | 10.2.2 | every grain and contract project | Orleans 10 targets .NET 10 and is what Survival runs. Source-generated serializers, so no reflection at activation |
| `Microsoft.Orleans.Server` / `.Client` | 10.2.2 | silo hosts / gateway | |
| `Microsoft.Orleans.Clustering.Redis` | 10.2.2 | **local dev only** | ADR-004 — production membership is Kubernetes |
| `Orleans.Clustering.Kubernetes` | 10.0.0 | silo hosts | `UseKubeMembership` writes silo entries as CRs in the silo's own namespace. Zero external dependencies for membership, which is the first half of "no single database" |
| `Microsoft.Orleans.Persistence.Redis` | 10.2.2 | hot tier | ADR-003 |
| `Microsoft.Orleans.Reminders.Redis` | 10.2.2 | reminder service | Sharded with the hot tier |
| `Microsoft.Orleans.Streaming` | 10.2.2 | everywhere | |
| `Rikarin.Orleans.Streaming.NATs` | 9.1.0-alpha.1 → **needs a 10.x build** | streams | ⚠ **This is ours and it is on Orleans 9.** Bumping it to Orleans 10 is a prerequisite task in [24](24-roadmap.md), not a given. Fallback if it slips: `Microsoft.Orleans.Streaming.EventHubs`-shaped custom adapter over `NATS.Client.JetStream`, which is what the package already is |
| `Orleans.Multitenant` | 4.0.0 | every tenant-scoped grain | ADR-002. Targets Orleans 10 on .NET 10 per its README |
| `Microsoft.Orleans.Serialization.SystemTextJson` | 10.2.2 | grain storage serializer | Human-readable state in Redis is worth the bytes during the first year. Revisit against MemoryPack when a grain shows up in a profile |
| `Microsoft.Orleans.TestingHost` | 10.2.2 | tests | In-process cluster per test class |

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
| `System.CommandLine` | 2.0.10 | `cc` | Finally stable after years of preview |
| `OpenIddict.AspNetCore` | 7.3.0 | identity host | ADR-015. ⚠ **7.3.0, not the 8.0 preview** — an auth server is the last place to run a preview |
| `Fido2.AspNet` | 4.0.0-beta9 | identity host | ⚠ The only maintained .NET WebAuthn library, and it is a beta with no stable successor. Wrapped behind `IPasskeyService` so replacing it is one file |
| `Riok.Mapperly` | 4.3.1 | providers | Source-generated mapping; no AutoMapper reflection |
| `Polly` | 8.6.5 | fabric | Retry/circuit-break on cluster connections |
| `Serilog.AspNetCore` + `.Sinks.OpenTelemetry` | 10.0.0 / 4.2.0 | all hosts | |
| `OpenTelemetry.*` | 1.17.0 | all hosts | Traces and metrics; `AddActivityPropagation()` on the Orleans builder so a trace crosses grain calls |

### Test and build

| Package | Version |
|---|---|
| `xunit.v3` | 3.2.2 |
| `NSubstitute` | 5.3.0 |
| `Shouldly` | 4.3.0 |
| `Testcontainers` | latest at bring-up — real Redis/Postgres/NATS/`k3s` in integration tests |
| `Nuke.Common` | 10.1.0 |
| `Aspire.Hosting.*` | 13.4.6 — ADR-014, **local development only** |

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
`{tenantId}|{keyWithinTenant}` (verified against `Orleans.Multitenant/Internal/Extensions.cs` — `|`
is the separator, doubled to escape, `~` prefixed when the inner key starts with either).

**This is the reconciliation of two requirements that appear to conflict.** The brief says "GUID as
ID"; `Orleans.Multitenant` only supports string keys. Both are satisfied: identifiers in the API, the
database, the SDK and the URL are GUIDs. The *grain key* is a composed string that contains them.
One type formats and parses it:

```csharp
readonly record struct ResourceKey(Guid SubscriptionId, string ResourceGroup, ResourceType Type, Guid ResourceId)
{
    public string ToKeyWithinTenant() => $"{SubscriptionId:N}/{ResourceGroup}/{Type}/{ResourceId:N}";
    public static bool TryParse(string keyWithinTenant, out ResourceKey key) { … }
}
```

Nothing else in the codebase may concatenate a grain key, enforced by an analyzer that flags string
literals containing `|` in `GetGrain` arguments.

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
Orleans learns its pod identity. Both are three lines. Also: our own silos must run *somewhere*, and
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
   generates the dashboard form. We do the same, with the schema also generating the OpenAPI body, the
   CLI flags and the SDK model — ADR-012.
3. **The sizing-preset vocabulary** (`t1.micro`, `c1.large`, `s1.xlarge`, …). Users understand
   instance families; inventing our own names would be gratuitous.

**Not taken:** the aggregated API server, the namespace-per-tenant model, `HelmRelease` as desired
state, FluxCD as the reconciler, Keycloak, Talos as a requirement, Outline as the VPN.

**Their charts are a starting point, not a dependency.** Where a chart is close, fork it into
`charts/` with the upstream commit recorded in a `SOURCE` file. A drifting vendored chart with no
provenance is how a platform ends up unable to upgrade Postgres.

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

### ADR-012 — The provider registry is the one source; four surfaces are generated

**Decision.** A resource provider declares, in C#, its namespace, its resource types, their API
versions, and a JSON Schema per version. From that registry a build step generates:

| Surface | Generated |
|---|---|
| OpenAPI 3.1 document | Paths, bodies, LRO headers, error shapes |
| `cc` CLI | Verb tree, flags, help, completion |
| .NET SDK | Clients, models, `Operation<T>` pollers |
| Portal forms | Angular reactive forms + xUI controls from the schema, with `x-cybercloud-*` hints for widgets (a `storageclass` picker, a region picker) |

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

**⚠ Version coupling.** Angular 22.0.8 / Tailwind 4.3.3 are xUI's pins today. The portal does not
choose these; it follows. An Angular major upgrade is an xUI task first.

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
