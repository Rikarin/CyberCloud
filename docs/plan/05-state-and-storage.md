# 05 — State and Storage

The document that has to pay for the claim in [00](00-vision-and-principles.md): *no single database
is a bottleneck*. It is a claim about a specific set of paths, and the way to make it checkable is to
enumerate every store in the system and say what its write rate is a function of.

## Every store, and what its load scales with

| Store | Purpose | Writes scale with | Single point? |
|---|---|---|---|
| Kubernetes API (our own cluster) | Silo membership | silos × heartbeat | Per region; already HA; would have taken us down anyway |
| Redis Cluster (**Hot** tier) | Session, observed, cache, counters | tenant activity | **Sharded by tenant.** 12+ shards |
| PostgreSQL (**Durable** tier) | Desired state, identity, tuples, ledger | control-plane writes | **Sharded by tenant.** 16+ instances |
| NATS JetStream | Streams, events, work queues | everything | Per region; subject-sharded; horizontally scalable |
| ClickHouse | Logs, traces, metering rollups, resource graph | telemetry volume | Per region; database-per-tenant; shard+replica |
| VictoriaMetrics | Metrics | series churn | Per region; native multi-tenant `accountID` |
| SeaweedFS | Blobs | tenant data | Per region; volume servers scale out |
| **Global tenant directory** | tenant → region, shards, status | **new tenants** | ⚠ Yes — see below |

Everything except the last is sharded, per-region, or both. The last one is the honest exception and
it gets its own section.

## The tenant directory — the one global thing

**What it holds.** Per tenant: id, slug, home region, hot-shard id, durable-shard id, status,
directory version. About 200 bytes. For 1 000 000 tenants that is 200 MB — small enough to be resident
in every gateway process.

**Why it must be global.** A request arrives at any gateway in any region carrying a token for tenant
X. Before anything else can happen, that gateway must know which region X lives in. There is no way to
shard that lookup by tenant, because the tenant id is what you are looking up.

**How it avoids being a bottleneck.**

1. It lives in the **global directory Orleans cluster** ([04](04-orleans-topology.md)), in the durable
   tier, replicated across regions with the region's replica read-only.
2. Every gateway process holds the whole thing in memory as an immutable snapshot, refreshed by
   subscribing to `cc.platform.directory` and applying deltas.
3. Reads never leave the process. A cache miss (a tenant created 200 ms ago in another region) falls
   back to a grain call — measured, alerted on, and expected to be a handful per second worldwide.
4. Writes happen on tenant creation, tenant suspension, and region migration. At 10 000 new tenants a
   day that is 0.12 writes per second.

**The failure mode, named.** If the global cluster is unreachable, no *new* tenants can be created and
no directory changes propagate — but every existing tenant keeps working from cache, in every region,
indefinitely. That is the correct blast radius and it is what the design is for. It is tested: the
chaos suite blackholes the global cluster for ten minutes and asserts zero tenant-facing errors.

## The two tiers

Restating ADR-003 with the operational detail.

### Hot — Redis Cluster

- **Topology.** Redis Cluster (not Sentinel), 12 shards to start, one replica each, `appendonly yes`,
  `appendfsync everysec`, `maxmemory-policy noeviction` (a hot-tier eviction is a correctness bug, not
  a capacity event — it must page, not silently drop state).
- **Key layout.** `{cc:t:<tenantId>}:<grainType>:<keyWithinTenant>`. The braces are a **Redis Cluster
  hash tag**: everything inside them determines the slot, so *all of a tenant's keys land on one
  shard*. That makes a tenant's state one-shard-local — a multi-key read is one round trip, a
  tenant delete is one `SCAN` on one shard — and it makes shard assignment automatic from the tenant
  id rather than something the shard map has to store.
- ⚠ **The cost of that choice is a hot tenant is a hot shard.** There is no per-tenant spreading. The
  mitigation is that the hot tier holds only session-shaped state, which is bounded by *concurrent
  activity*, not by tenant size; and the shard map ([below](#the-shard-map)) can pin a named large
  tenant to a dedicated shard by overriding the hash tag. That override exists from day one because
  retrofitting it means rewriting keys.
- **What is in it:** terminal sessions, SignalR connection state, observed cluster state, ReBAC check
  cache, rate-limit counters, metric pre-aggregates, portal UI state, in-flight LRO progress.
- **What loss costs:** a warm-up. Sessions drop (users re-connect), observed state is re-read from the
  informers, caches refill. The chaos suite runs `FLUSHALL` against the staging hot tier and asserts
  the platform is fully functional within 60 seconds and lost no durable state.

### Durable — PostgreSQL, sharded

- **Topology.** N independent PostgreSQL instances (16 to start), each with a synchronous replica.
  Not Citus, not a distributed SQL engine, not sharded-by-hash-inside-one-logical-database. **N plain
  Postgres servers that do not know about each other.** A shard is added by starting a server and
  putting it in the map.
- **Schema.** The Orleans ADO.NET grain storage schema, one row per grain: `(GrainIdHash, GrainIdN0,
  GrainIdN1, GrainTypeHash, GrainTypeString, GrainIdExtensionString, ServiceId, PayloadBinary,
  ModifiedOn, Version)`. ⚠ **There is no `PayloadJson` column** — an earlier draft of this list had
  one, and the provider reads and writes `PayloadBinary` only, whatever serializer is configured.
  Storing JSON means putting JSON *bytes* in `PayloadBinary`, which is what makes the `psql`
  argument below work; it does not mean a second column. Asserted by a test that reads the live
  schema. Plus, in the same database, the small number of genuinely relational things that are not
  grain state: the billing ledger (append-only, needs `SUM` over a period) and the audit index.
- **Why not one big Postgres with partitioning.** Because a partitioned table is still one server's
  WAL, one server's connection pool and one server's failover. Sixteen servers have sixteen. The cost
  is that there is no cross-tenant `JOIN`, ever — which is a feature, since a cross-tenant `JOIN` is
  the thing that turns into a data leak.
- **What is in it:** tenants, subscriptions, resource groups, resource desired state, users,
  credentials, service principals, ReBAC tuples, operations, cluster connections, quota grants,
  billing ledger, audit cursors.

### Choosing a tier

```csharp
public sealed class ResourceGrain : Grain, IResourceGrain
{
    [PersistentState("desired", StorageTiers.Durable)] IPersistentState<ResourceDesired> desired;
    [PersistentState("observed", StorageTiers.Hot)]    IPersistentState<ResourceObserved> observed;
}
```

Splitting a single conceptual entity across both tiers is normal and intended: what the tenant asked
for is durable, what the cluster currently reports is rebuildable. A `ResourceGrain` that lost its
`observed` state re-reads it from the informer cache on next reconcile and nothing is wrong.

**The enforcement** (from [00](00-vision-and-principles.md)) is an architecture test over a checked-in
list: every grain type in `durable-grains.txt` must bind its primary state to `Durable`, and any grain
type not on the list that binds to `Durable` must carry `[DurableStateRationale("…")]`. The list is
reviewed like a schema migration, because that is what it is.

## The shard map

A null-tenant grain in the global cluster, mirrored into every silo and gateway alongside the tenant
directory.

```csharp
[Alias("Platform.ShardMap")]
public interface IShardMapGrain : IGrainWithStringKey
{
    Task<Result<ShardAssignment>> AssignAsync(Guid tenantId, string region);
    Task<Result<ShardMapSnapshot>> GetSnapshotAsync(long knownVersion);
    Task<Result> PinAsync(Guid tenantId, string durableShard, string? hotOverride);
}
```

**Assignment is at tenant creation and it is permanent.** New tenants go to the least-loaded shard by
a simple weighted pick. There is **no automatic rebalancing**, and that is a decision rather than an
omission:

> Rebalancing a tenant means moving its durable state while it is live, which means either a write
> freeze or a two-phase copy with a cutover. Both are correct and both are a quarter of work for a
> problem that does not exist until a shard is genuinely full — at which point the answer is to stop
> assigning new tenants to it, which costs nothing. **Capacity is added at the front, not
> redistributed at the back.**

What *is* built, because it is needed and it is small: **`PinAsync`**, a manual, operator-initiated
move for the one tenant that outgrows a shard. It quiesces the tenant (rejects writes with `503
Retry-After`), copies the grain rows, flips the map, and un-quiesces. Minutes of read-only for one
tenant, run deliberately. Budgeted at 0.5 EM in M2, not M1.

## Storage provider wiring

`Orleans.Multitenant`'s `configureTenantOptions` callback is where the sharding actually happens, and
it is worth showing because it is the load-bearing five lines of this document:

⚠ **The version below is the corrected one. The original five lines did not compile or run** — four
separate defects, all found by building them against `Orleans.Multitenant` 4.0.0 and Orleans 10.2.2.

```csharp
// (options, tenantId) — TWO arguments. There is no IServiceProvider parameter.
static void ConfigureForTenant(AdoNetGrainStorageOptions o, string tenantId)
{
    // `tenantId` is the literal string "Null" for a null-tenant grain, so Guid.Parse throws.
    var shard = shardMapCache.DurableShardFor(TenantId.Parse(tenantId));   // captured singleton
    o.ConnectionString = shardConnections.Durable(shard);                  // MaxPoolSize=5, No Reset On Close=true
    o.Invariant = "Npgsql";
    o.GrainStorageSerializer = new SystemTextJsonGrainStorageSerializer(); // ours
    o.HashPicker = new OrleansDefaultHasher();                             // ⚠ see below
}
```

| Defect in the original | Reality |
|---|---|
| Signature `(sp, options, tenantId)` | The callback is `(options, tenantId)`. The service provider is reachable only through `getProviderParameters`, which runs **after** the callback — so dependencies are captured in singletons instead. There is also an undocumented third generic parameter, `TGrainStorageOptionsValidator`, with no default |
| `Guid.Parse(tenantId)` | ⚠ **Throws for null-tenant grains** — `Orleans.Multitenant` passes the string `"Null"`. Every Platform grain in [04 § Grain taxonomy](04-orleans-topology.md) is null-tenant *and* durable, so this is a live path, not an edge case |
| `OrleansJsonGrainStorageSerializer` | Does not exist in Orleans 10.2.2. Ours is a `System.Text.Json` implementation — ⚠ configured with `UnsafeRelaxedJsonEscaping`, because the default encoder escapes `'` and non-ASCII, so `Müller's database` persists as `'`/`ü`: valid JSON, unreadable in `psql`, which is the *entire* stated reason for choosing JSON below |
| *(absent)* `HashPicker` | Orleans supplies it via `IPostConfigureOptions`, and **per-tenant providers never go through the options system** — so every post-configured default is silently missing. It surfaces as `OrleansConfigurationException: … HashPicker is required` on the first durable write. Set it explicitly to Orleans' own hasher so row hashes stay compatible |

⚠ **One more, and it is the dangerous one because it fails silently.** `AdoNetGrainStorage` takes
`IOptions<AdoNetGrainStorageOptions>`, not the raw options. Without `getProviderParameters` wrapping
them, `ActivatorUtilities` resolves the options from the container and **every tenant quietly shares
the bootstrap shard** — sharded storage, no error, no log line.

Called once per tenant per silo, at first touch, and cached by the multitenant storage provider —
**measured: 12 invocations for 12 tenants across 600 concurrent grain writes**, so the shard lookup
is genuinely not on the hot path. The tenant id it receives is the one extracted from the grain key,
so a grain physically *cannot* be stored on the wrong tenant's shard, because the key is what
selects the connection. That was tested across seven attack routes; six are blocked by construction
and the seventh (handing a raw physical grain key to `IGrainFactory`) is closed by
`AddMultitenantCommunicationSeparation`, not by storage.

⚠ **Connection pool arithmetic, because this is where it bites.** 30 silos × 16 durable shards × a
pool of 20 is 9 600 potential Postgres connections. Postgres tops out well below that. Two required
mitigations: **PgBouncer in transaction mode in front of every shard** (non-negotiable), and a pool
`MaxPoolSize` of 5 per silo per shard, which is ample because grain storage calls are short. This is
the kind of number that is obvious in retrospect and fatal in production, so it is in the plan.

Three corrections to that paragraph, none of which change the conclusion:

- **The unit is per *tenant* per shard, not per silo per shard.** Npgsql pools by connection string,
  and `Orleans.Multitenant` builds one provider per tenant, so a per-tenant connection string
  multiplies the pool by tenant count. Measured: 24 tenants writing concurrently on one shard peaked
  at ≤ 5 connections, because the string is byte-identical for every tenant on that shard — which is
  now a test, since it is the property the arithmetic depends on.
- **Transaction mode is compatible, and for a different reason than expected.** The provider never
  calls `Prepare()`, uses no `LISTEN/NOTIFY`, no advisory locks, no session `SET`, and every
  operation is a single statement. Npgsql's `MaxAutoPrepare` defaults to **0**, so the
  "Npgsql auto-prepares" concern does not apply to a default connection string. The one setting that
  *does* need changing is **`No Reset On Close=true`**, or Npgsql issues `DISCARD ALL` on return.
- **The same arithmetic is never done for Redis, and it needs to be.** Naive per-tenant wiring opens
  one multiplexer per tenant per silo. The hot tier shares a single multiplexer across tenants;
  only the key prefix differs.

⚠ **Nothing creates the durable schema, and that is a real gap rather than an omission from this
paragraph.** `Microsoft.Orleans.Persistence.AdoNet` ships **zero** SQL and no driver — it loads
Npgsql reflectively, so a typo in `Invariant` is a runtime `AggregateException`, not a compile error.
`AdoNetGrainStorage.Init` runs `SELECT … FROM OrleansQuery` and expects four rows; against a fresh
database that is `relation "orleansquery" does not exist`. The DDL is two files in the `dotnet/orleans`
repo at the matching tag (`Shared/PostgreSQL-Main.sql`, then
`Orleans.Persistence.AdoNet/PostgreSQL-Persistence.sql`, in that order). So
[§ The shard map](#the-shard-map)'s "a shard is added by starting a server and putting it in the map"
is missing a step. **That step now has an owner:** `OrleansAdoNetSchema` embeds both scripts,
`CyberCloud.Silo.Host --apply-durable-schema` is the one-shot job that runs them, and `deploy/`
schedules it as a Helm `pre-install`/`pre-upgrade` hook before any silo starts.

⚠ **Two files means a half-applied state, and the obvious probe cannot see it.** A run that dies
*between* the scripts leaves `orleansquery` present and `orleansstorage` absent, so a probe that asks
only `to_regclass('orleansquery')` answers "already applied" on every later run and the shard stays
unusable forever. The applier therefore probes all five objects the two files create — both tables,
the index, the `writetostorage` function, and the four `OrleansQuery` rows `Init` reads back — applies
both scripts inside **one transaction**, and takes a transaction-scoped advisory lock so that two
appliers on one shard produce a no-op for the loser rather than a `42P07`. A mixture no interrupted
run can produce is refused with an inventory rather than guessed at, because the only way to force
those scripts over it is to drop `orleansstorage`, which is the tenants' state.

⚠ **A silo starts healthy with an unreachable durable shard.** The bootstrap provider is never
constructed, so nothing fails at startup: the silo joins, passes readiness, takes traffic, and only
the affected shard's tenants error. Shard reachability therefore gets **its own health check**,
`durable-shards`, and the decision that matters is that it carries **no tag at all**: it reports on
`/api/health`, where a scrape and an alert read it, and the readiness probe never runs it. Readiness
deliberately does not probe storage — an unreachable shard evicting its silos concentrates that
shard's load on its neighbours, which turns one shard's outage into a cascade. The alternative,
reporting `Degraded` from a `ready`-tagged check, is worse in both directions: ASP.NET treats
`Degraded` as *passing* a probe, so it would change nothing today, and it would sit one word away from
causing exactly that cascade tomorrow.

## Serialization and schema evolution

Grain state is JSON via `Microsoft.Orleans.Serialization.SystemTextJson`, chosen deliberately over
MemoryPack or the binary serializer for the durable tier.

**Why JSON for state that we care about:** because in year two someone will need to answer "what did
this resource look like before the bad deploy" with `psql`, and a binary blob makes that a program
rather than a query. The bytes cost is real (2–3× over MemoryPack) and is paid on a tier that is
measured in hundreds of gigabytes, not terabytes. The **hot** tier may use MemoryPack where a profile
justifies it, because nobody debugs a session by reading it.

**Evolution rules**, enforced by the serialization compatibility test in
[23](23-build-ci-and-testing.md):

1. Every persisted type has `[GenerateSerializer]` and a stable `[Alias]`.
2. `[Id(n)]` numbers are never reused, never reordered. Removing a member leaves its number burned.
3. A new member must be optional with a default that means "as before".
4. A semantic change to an existing member is a **new member plus a migration on read**, never a
   reinterpretation. `OnActivateAsync` upgrades in place and writes back on next save.
5. Renaming a type without `[Alias]` is a data-loss bug; the analyzer makes it a compile error.

⚠ **Rules 1, 2 and 5 describe the Orleans *binary* format, and the durable tier is JSON.** Under a
`System.Text.Json` grain storage serializer, neither `[Id(n)]` nor `[Alias]` is read at all — **the
property name is the persisted contract.** So on this tier:

- Renaming a *property* is the data-loss bug, not renaming a type. `[Alias]` protects the wire
  (grain-to-grain calls, rolling upgrades), which is a different concern from what is at rest in
  Postgres, and the two must both be honoured for different reasons.
- Reordering members is harmless; renaming one is not. That is the exact inverse of the binary
  format's failure mode, which is why stating one set of rules for both tiers is misleading.
- The compatibility test therefore has to check **property names** for durable types, in addition to
  the `[Id(n)]` manifest it checks for wire types. ⚠ Also note rule 5's "the analyzer makes it a
  compile error" is still aspirational — see [04 § Failure and upgrade](04-orleans-topology.md); it
  is a reflection test today, and no analyzer covers property renames on JSON state at all.

## What is deliberately *not* in either tier

| Thing | Where it goes | Why |
|---|---|---|
| Secrets, keys, certificates | OpenBao ([18](18-security-vault-and-malware-scan.md)) | Grain state is JSON in Postgres and in backups. A secret there is a secret in every backup forever. Grains hold `SecretRef`, never a value — analyzer-enforced ([00](00-vision-and-principles.md)) |
| Logs, traces, spans | ClickHouse | Volume is 100× the control plane; it would swamp both tiers |
| Metric samples | VictoriaMetrics | Same |
| Blobs, disks, images | SeaweedFS / LINSTOR | Same, plus they are tenant data, not platform state |
| Kubernetes observed state, in full | Informer cache in the connection grain's silo | Only the summary a resource grain needs is persisted to Hot |
| Anything a tenant uploads | Tenant data plane | The control plane never stores tenant payloads |

## Backup and restore

| Tier | Method | RPO | RTO | Tested by |
|---|---|---|---|---|
| Durable (Postgres) | WAL archiving to SeaweedFS via `pgBackRest`, per shard, 5-minute segments | 5 min | 30 min/shard | Weekly restore of one random shard into a scratch namespace, asserted by row count and a spot-check query |
| Hot (Redis) | None | — | — | Deliberate. It is rebuildable; backing it up would imply it is not |
| ClickHouse | Native `BACKUP` to SeaweedFS, daily, per-tenant database | 24 h | hours | Monthly |
| SeaweedFS | Replication factor ≥ 2 across racks; cross-region async for tenants who pay for it | — | — | — |
| Vault (OpenBao) | Raft snapshot, encrypted, offsite | 1 h | 1 h | ⚠ Quarterly **full** unseal-and-restore drill, with the unseal-key holders in a room. This is the restore that fails when nobody has practiced it |

**A tenant restore is not the same as a shard restore**, and only the second one is built for M1. A
per-tenant point-in-time restore requires the grain rows for one tenant to be extractable from a
WAL-based restore, which means restoring the whole shard to a scratch instance and copying rows out.
That is 20 minutes of operator time and it is the M1 answer. A self-service tenant restore is M3, and
it is more product than plumbing.
