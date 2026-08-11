# 04 — Orleans Topology

How the cluster is shaped, how grains are placed, what happens when a silo dies, and what "millions of
users" means in silos and gigabytes rather than in adjectives.

## The clusters, plural

There are three distinct Orleans clusters and conflating them is the first mistake to avoid.

| Cluster | Scope | Silos | What lives there |
|---|---|---|---|
| **Regional control plane** | One per region | 10–60 | Everything tenant-scoped: tenants, subscriptions, resources, operations, users, sessions, ReBAC, terminal sessions |
| **Global directory** | One, worldwide | 3–5 | The tenant directory, the shard map, the provider registry, global uniqueness (email, tenant slug, DNS zone apex) |
| *(a managed cluster)* | — | — | **Not an Orleans cluster.** A tenant's Kubernetes cluster runs the tenant's workloads. Cyber Cloud reaches into it over the Kubernetes API and never runs a silo there |

A tenant is **homed to exactly one region** at creation. Cross-region is a routing problem at the
gateway (a `GET` for a tenant homed in `eu-central` issued at `us-east` is a proxy, not a distributed
transaction), which is the only honest answer that does not require a globally-consistent store.

⚠ **The global directory is the one thing that looks like a single point.** It is: a few hundred bytes
per tenant, written on tenant creation and on region migration, read once per gateway process per
tenant and then cached behind a version stamp. Its write rate is O(new tenants per day). If it is ever
on a per-request path, that is a bug with a name — [05 § The tenant directory](05-state-and-storage.md).

## Silo composition

Every silo loads every provider module. There are no specialised silo roles.

**Why not roles.** Role-partitioned silos ("data providers here, network providers there") sound like
isolation and deliver a placement problem: a resource grain calls its provider's reconciler, and if
those are on different silos every reconcile is a network hop. Uniform silos let Orleans place a
tenant's grains together (§ Placement) and let capacity be one number instead of twelve.

**What that costs:** every silo carries every provider's assemblies and dependencies, so silo memory
has a fixed floor of a few hundred MB and a deploy touches everything. Accepted. The moment a provider
needs a genuinely different resource shape — GPU, huge memory — it stops being a provider and becomes
a data-plane workload in a cluster, which is where it belonged anyway.

```csharp
// CyberCloud.ServiceDefaults — descended from Survival's OrleansApplication
// ⚠ async, because AddApplicationAsync is. The earlier synchronous signature could not have
// compiled once the required module registration below was added.
public static async Task<WebApplicationBuilder> CreateSiloAsync<TSiloModule>(string[] args)
    where TSiloModule : IAbpModule
{
    var builder = WebApplication.CreateBuilder(args);
    builder.Host.AddAppSettingsSecretsJson().UseAutofac().UseSerilog();

    // ⚠ REQUIRED, and an earlier draft of this block omitted it. `UseAutofac()` installs ABP's
    // service-provider factory, which resolves IModuleContainer during Build(). Without a module
    // registered, the caller's builder.Build() throws
    //     InvalidOperationException: Could not find singleton service: Volo.Abp.Modularity.IModuleContainer
    // — a message naming neither UseAutofac nor the missing call. Survival gets this right; this
    // excerpt did not. Either register a module here or do not call UseAutofac.
    await builder.AddApplicationAsync<TSiloModule>();

    builder.AddServiceDefaults();
    builder.AddOrleansHealthChecks();

    builder.UseOrleans(b =>
    {
        b.AddMultitenantGrainStorageAsDefault<RedisGrainStorage, RedisStorageOptions>(
             StorageTiers.Hot,     (sp, o, tenantId) => o.ConfigureForTenant(sp, tenantId))
         .AddMultitenantGrainStorage<AdoNetGrainStorage, AdoNetGrainStorageOptions>(
             StorageTiers.Durable, (sp, o, tenantId) => o.ConfigureForTenant(sp, tenantId))
         .AddMultitenantStreams(StreamProviders.Events, NatsStreamProvider.Configure)
         .AddMultitenantCommunicationSeparation(_ => new PlatformCrossTenantAuthorizer())
         .UseRedisReminderService(o => o.ConfigureSharded())
         .AddActivityPropagation();

        if (builder.Environment.IsDevelopment())
            // ⚠ Ports are explicit, not defaulted. Bare UseLocalhostClustering() binds Orleans'
            // defaults 11111/30000; 30000 collides with unrelated software often enough that it
            // was hit on the first machine this ran on, and the failure is an AddressInUseException
            // from a socket bind inside Orleans naming neither the port nor the holder. It also
            // makes running two silos locally impossible without editing code.
            //
            // ⚠ AND THE THIRD ARGUMENT IS NOT OPTIONAL FOR THE SECOND SILO. With it omitted,
            // UseLocalhostClustering defaults the primary-silo endpoint to 127.0.0.1:<its own
            // siloPort>, so two silos started on distinct ports each hold their own development
            // membership table: two one-silo clusters, both healthy, neither aware of the other,
            // with no error anywhere. 24 § Phase 0's exit criterion is a TWO-SILO cluster, so the
            // non-primary silo must name the primary's port —
            // CyberCloudClusterOptions.LocalhostPrimarySiloPort, 0 meaning "I am the primary".
            b.UseLocalhostClustering(
                options.SiloPort,
                options.GatewayPort,
                options.PrimarySiloPort == 0
                    ? null
                    : new IPEndPoint(IPAddress.Loopback, options.PrimarySiloPort));
        else
        {
            b.UseKubeMembership();
            builder.Services.UseKubernetesHosting();   // pod identity → silo identity
        }
    });
    return builder;
}
```

Three things in that block are decisions, not boilerplate:

- **`AddMultitenantCommunicationSeparation` is not optional.** Without it, a bug in one provider can
  read another tenant's grain and nothing complains. With it, that is an `UnauthorizedAccessException`
  with both tenant ids in the message. Verified against a real cluster: the message is
  `Tenant "{source}" attempted to access tenant "{target}"`.

  ⚠ **It covers grain-to-grain calls only, and that is a property of the mechanism, not of our
  wiring.** `TenantSeparatingCallFilter` returns without consulting the authorizer when the call has
  no source grain, when the source is a **client**, or when it is a system target. Since the gateway
  is an Orleans *client* by design (§ [03](03-repository-layout.md)), it is permanently outside this
  filter. The sentence above stays true as written — a provider runs inside a grain — but it must
  not be read as "no code can reach another tenant's grain". See
  [00 § The tenant-separation row, corrected](00-vision-and-principles.md) for the two-mechanism
  statement.
- **`UseKubernetesHosting()`** makes the silo's identity its pod identity, so a `SIGTERM` from a
  rolling update becomes a graceful `StopAsync` with grain migration rather than a 60-second gap.
- **`AddActivityPropagation()`** is what makes a trace survive a grain call. Without it, distributed
  tracing stops at the gateway and every latency investigation becomes archaeology.

## Grain taxonomy

Five kinds, and the kind determines the storage tier, the placement and the lifetime.

| Kind | Example | Key | Tier | Lifetime |
|---|---|---|---|---|
| **Entity** | `ITenantGrain`, `ISubscriptionGrain`, `IUserGrain`, `IResourceGrain` | tenant-qualified | Durable | Long; collected after idle |
| **Session** | `ITerminalSessionGrain`, `ISignalRConnectionGrain` | tenant-qualified | Hot | Minutes to hours; dies with the connection |
| **Coordinator** | `IReconcileSchedulerGrain`, `IClusterConnectionGrain`, `IQuotaGrain` | tenant- or cluster-qualified | Hot + a durable checkpoint | Pinned while work exists |
| **Index** | `IEmailIndexGrain`, `IResourceNameIndexGrain`, `IRelationIndexGrain` | derived from the indexed value | Durable | Long |
| **Platform** | `ITenantDirectoryGrain`, `IShardMapGrain`, `IProviderRegistryGrain` | null-tenant | Durable, global cluster | Permanent |

**Index grains are how uniqueness works without a unique constraint.** "This email is not taken" is
`IEmailIndexGrain.TryClaim(email, userId)` — a grain keyed by the hash of the normalized email, whose
single-threaded activation *is* the mutex. This is the Orleans answer to the thing people reach for a
relational database to do, and it is why there is no global users table.

⚠ It is also the sharpest edge in the model: an index grain is a hot spot if the indexed value is not
high-cardinality. `IEmailIndexGrain` keyed by email is fine (one activation per email). An index grain
keyed by *resource type* would be a single activation serialising every create in the platform. The
review question for any new index grain is "what is the cardinality of the key", and if the answer is
"small", it is not an index grain.

## Placement

Default is Orleans' `RandomPlacement` for entity grains and it is left alone. Two deliberate exceptions:

**1. Tenant affinity for the hot path.** A tenant's `ISubscriptionGrain`, its resource grains and its
ReBAC grains are called together on almost every request. `PreferLocalPlacement` does not help
(the caller is the gateway, on a different node). Instead a custom `TenantHashPlacement` maps
`hash(tenantId) → silo` from the current membership snapshot, so a tenant's grains gather on one silo
and a request that touches five of them is one network hop instead of five.

⚠ **The failure mode is the whole reason to mention it:** hashing to a silo means a silo loss moves an
entire tenant at once, and a large tenant becomes a hot silo. Mitigations, both required: the hash
includes a *bucket* (`hash(tenantId + bucket) `, 16 buckets per tenant) so one tenant spreads across up
to 16 silos while still colocating related grains; and the placement director falls back to random
when the target silo's CPU is above a threshold. Without those two, this optimization is a load-imbalance
generator. It is therefore **behind a flag, off by default, and turned on only after the load suite
shows the hop count is actually the bottleneck.**

**2. Cluster-connection affinity.** `IClusterConnectionGrain` holds a live `KubernetesClient` with
watches open. It is `[PreferLocalPlacement]` relative to the informer bridge that consumes it and is
pinned by a reminder so it does not deactivate under a watch. One activation per managed cluster,
platform-wide — enforced by the grain key being the cluster id and by the single-activation guarantee.

## Streams

One stream provider, `Events`, over NATS JetStream, multitenant-wrapped so a stream id carries the
tenant and `TenantSeparatingStreamFilter` rejects the rest.

| Namespace | Subject pattern | Producer | Consumers |
|---|---|---|---|
| `resource-changed` | `cc.{tenant}.res.{provider}.{type}.{id}` | `IResourceGrain` on every state transition | Portal SignalR fan-out, resource-graph projection, audit sink, billing |
| `operation-progress` | `cc.{tenant}.op.{id}` | operation grains | Portal, CLI `--wait` |
| `cluster-observed` | `cc.{tenant}.k8s.{cluster}.{kind}` | informer bridge | Resource grains (drift), monitor |
| `metering` | `cc.{tenant}.usage.{meter}` | providers | Metering rollup workers |
| `platform` | `cc.platform.{topic}` | null-tenant | Admin, alerting |

**Implicit subscriptions where the consumer is a grain** (`[ImplicitStreamSubscription]`), explicit
elsewhere. The rule: a stream is for fan-out of facts, never for issuing commands. A command is a grain
call, because a grain call has a result and a stream does not, and "did it work" is not a question you
want to answer by correlating a second stream.

⚠ **Delivery is at-least-once and per-subject-ordered only.** Every stream consumer must be
idempotent, and the resource-changed events carry a monotonic `Version` from the grain's etag so a
consumer can drop what it has already seen. Anything requiring global order does not use streams.

## Reminders

Redis reminder service, sharded with the hot tier. Reminders are used for exactly four things:

1. **Reconcile ticks** — every resource grain in a non-terminal state has a reminder that fires on a
   backoff schedule (10 s → 30 s → 2 min → 10 min, capped) until it reaches `Succeeded` or `Failed`.
   This is what makes provisioning resumable across a silo restart.
2. **Drift detection** — resources in `Succeeded` get a slow reminder (hourly, jittered) that
   re-reads observed state and re-applies if it diverges.
3. **Lease renewal** — cluster connections and terminal sessions.
4. **Rollups** — metering aggregation windows.

⚠ **Reminder count is a real scaling number and it is easy to get wrong.** One reminder per resource
at hourly drift detection, with 5 000 000 resources, is ~1 400 reminder firings per second across the
cluster, forever, whether or not anything changed. That is affordable but it is not free, and it
grows linearly with the catalogue's success. The mitigation is in the design: **drift reminders are
per-cluster, not per-resource.** One reminder per managed cluster does a full list-and-diff against
the informer cache and pokes only the resources that diverged. That turns 1 400/s into roughly one
firing per cluster per hour, and it is why the informer bridge exists at all.

## Failure and upgrade

**Silo loss.** Kubernetes membership marks the silo dead within the probe timeout; Orleans reactivates
its grains elsewhere on next call. Grains with in-flight work re-drive from their durable checkpoint
on activation — this is the property the operation grain design exists to provide
([08 § Long-running operations](08-resource-manager.md)). The chaos suite kills a random silo every
90 seconds during a provisioning storm and asserts zero resources stuck in a transitional state.

**Rolling upgrade.** Contract assemblies change under `[Alias]` discipline (ADR-018's sibling rule:
every `[GenerateSerializer]` type has a stable `[Alias]`, analyzer-enforced), so silos of version N and
N+1 coexist. The gate is a **serialization compatibility test** that loads the previous release's
contract assembly and round-trips every wire type through both — run in CI against the last three
tags, not just the previous one, because a hotfix branch will eventually be older than you think.

⚠ **Two corrections to that sentence, both found by building it.**

*"analyzer-enforced" is not true yet.* No analyzer requires `[Alias]` on a `[GenerateSerializer]`
type; nothing shipping with the SDK does this. It is currently enforced by a test that reflects over
the contracts assembly and fails on the next type added without one. That is a real gate, but it
lives in the test suite rather than the compiler, and [23](23-build-ci-and-testing.md)'s gate table
should say so until a `CyberCloud.Analyzers` project exists. The same caveat applies to
[00](00-vision-and-principles.md)'s claim that `async void` / `.Result` / `.Wait()` are
analyzer-banned: `CA1849` covers only the case where a blocking call sits inside an already-`async`
method, which is not the shape that deadlocks a silo.

*The cross-release half is unimplementable until there is a release.* "Loads the previous release's
contract assembly" needs a tag, and there is none. What **can** be written on day one — and is —
is the cross-*version* half: unwritten fields defaulting correctly, unknown error codes surviving,
half-written payloads. The cross-release half switches on at the first tag, and the `[Id(n)]`
baseline manifest is checked in now precisely so that it has something to compare against when it
does.

**Region loss.** Not solved, and said so plainly. A tenant homed in a lost region is down until the
region returns. Multi-region active-active for a tenant requires either a globally consistent store
(rejected — it is the bottleneck the brief rules out) or per-tenant conflict resolution (a product
decision nobody has asked for). What *is* budgeted is **region migration**: a tenant can be moved
between regions in a planned window by quiescing, draining state, and repointing the directory —
[24](24-roadmap.md), M3.

## Sizing, concretely

Numbers so the plan can be wrong in a checkable way rather than an unfalsifiable one. Assumptions:
1 000 000 tenants, 5 resources per tenant average, 10 000 000 users, 1 % of tenants active in any
5-minute window.

| Quantity | Estimate | Basis |
|---|---|---|
| Resident grains at peak | ~2 500 000 | 10 000 active tenants × ~250 grains each (subscription, resources, ReBAC, sessions) |
| Grain footprint | 2–8 KB | Resource desired+observed state dominates; measured per type in the load suite |
| Silo working set | ~12 GB | 150 000 grains/silo × 6 KB + runtime + provider assemblies |
| Silos per region | 20–30 | At 16 GB request / 24 GB limit |
| Redis hot tier | ~40 GB across 12 shards | Sessions and observed state only; the durable tier is not in RAM |
| Postgres durable tier | ~600 GB across 16 shards | ~60 KB/tenant of durable state |
| Gateway pods | 30–60 | I/O bound; scales on request rate, independent of silos |
| Ingest pods | 20–100 | Scales on telemetry volume — the largest and least predictable tier |

**The number to watch is not any of these; it is grains-per-silo.** Everything else follows from it,
and it is the one the load suite measures directly. If a silo cannot hold 150 000 grains at the target
p99, the answer is smaller grain state, not more silos — more silos increases the directory and the
membership chatter, and at some point that becomes the cost.
