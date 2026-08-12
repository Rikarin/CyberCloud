# 12 — Managed Data Services

The catalogue: databases, caches, brokers, search. Per ADR-010 the operator selection is taken from
Cozystack's survey; what is *not* taken is the control plane, which is [08](08-resource-manager.md).

## The pattern, once

Every service in this document is the same eight things. If a service needs a ninth, that is a signal
the platform is missing something and the platform gets fixed, not the service.

| # | Piece | Where |
|---|---|---|
| 1 | A Helm chart with an annotated `values.yaml` | `charts/managed/{svc}/` |
| 2 | A generated `values.schema.json` → the resource type's API schema | `Build.Charts` (ADR-012) |
| 3 | A `ResourceType` registration with meters, permissions and actions | The provider's `Describe` |
| 4 | An `IResourceReconciler` — render, apply, observe | ~150 lines |
| 5 | Credential provisioning into the tenant's Vault, exposed by a `listKeys` action | ~~`ISecretResolver`~~ ⚠ `ISecretWriter` + `IResourceActionHandler` — corrected below |
| 6 | A `ServiceMonitor`/`VMPodScrape` + a Grafana dashboard | ~~The chart~~ ⚠ the operator, where there is one — corrected below |
| 7 | A backup policy binding (Velero + volume snapshots) | `charts/managed/{svc}/backup.yaml` — ⚠ under-specified, see below |
| 8 | A conformance manifest | `charts/managed/{svc}/conformance.yaml` |

⚠ **CORRECTED, piece 5: `ISecretResolver` cannot provision anything, and the row named the wrong
half of the story for as long as the story had only one half.** That interface has exactly one method,
`ResolveAsync`, which *reads* a `SecretRef`; nothing in it writes, and nothing in the tree wrote to a
vault at all. So piece 5 was undischargeable by construction, and every provider that reached for it
found a read seam where the mint was supposed to be. It cost the most on
`CyberCloud.Storage/accounts`: SeaweedFS sets `iam.isAuthEnabled = len(identities) > 0` and answers an
unauthenticated request as `ACTION_ADMIN` when that is false, so an account whose identities `Secret`
nobody could write was an S3 endpoint with no access control.

Piece 5 is **two** seams, and the split is what makes each testable:

- **`ISecretWriter.MintAsync`** puts the credential in the tenant's vault at create time, **once**.
  Mint-once is the whole semantic rather than an optimisation — a reconciler is idempotent and its
  reminder fires on a resource that already converged, so a writer that replaced what was there would
  hand the tenant a new credential on every pass and break the one they are using. `OpenBaoSecretWriter`
  spells it `kv-v2`'s `cas=0`, at the vault, because two silos reconciling the same resource would both
  read "absent" and both write.
- **`IResourceActionHandler`** is what a `listKeys` action actually runs. Before it,
  `IProviderBuilder.Action` took no handler at all and `OperationKind.Action` was written by
  `ResourceManagerService` and read by nothing — so a `POST` answered `202` and the operation grain
  re-ran the resource type's *reconciler*. Twelve actions across nine provider namespaces were declared,
  published to the OpenAPI document, the SDK and the CLI, and could not execute.

`ISecretResolver` keeps its place in the row's *second* half: the handler reads the value back through
it, and so does the reconciler when it renders the credential into whatever the data plane mounts.

⚠ **The result of a `secret: true` action does not travel on its operation.** `OperationSpec` and the
LRO status are durable and are readable by anyone holding `read` on the resource, while `listKeys`
checks a permission that is deliberately not `read` — [07 § Consistency](07-authorization.md) puts a key
export in the fully-consistent row for exactly that reason. So a synchronous action starts no operation:
it answers `200` with its own body, and the value is persisted nowhere.

⚠ **CORRECTED, piece 6: "The chart" is the wrong place for an operator-managed service.** The outcome
is right and stays — every managed service is scraped and has a dashboard — but the location is wrong,
and it is wrong in a way that only shows up on the second upgrade. A chart-authored `ServiceMonitor` or
`VMPodScrape` has to hard-code the operator's pod labels, its metrics port and its container name; the
operator changes one of them in a minor release and the scrape goes quiet without failing. Found
building `charts/managed/postgres`: CloudNativePG emits the `PodMonitor` itself, and the switch is
**`spec.monitoring.enablePodMonitor` on the `Cluster` CR** — one annotated boolean in `values.yaml`,
rendered into the CR, with the operator owning the selector it is uniquely qualified to own.

So piece 6 reads: **ask the operator for the scrape object wherever the operator accepts the request,
and hand-write one into the chart only when there is no operator to ask.** The Grafana dashboard stays
chart-side either way; that one is ours.

⚠ **Piece 7 is under-specified, and the first service did not use it.** Two things are wrong with it
as written. First, `backup.yaml` sits *outside* `templates/`, so Helm never renders it — which is
defensible, since a backup policy is chart data like `conformance.yaml` rather than a manifest — but
**nothing in this plan says which component reads it**, and `Build.Charts` requires `SOURCE` and
`conformance.yaml` while requiring nothing of this file. An unread data file drifts by definition.
Second, `charts/managed/postgres` took the same route piece 6 turned out to want: backup is an
annotated `backup` block in `values.yaml` rendering into the `Cluster` CR's barman-cloud `backup:`
stanza, not Velero and volume snapshots. See [03 § charts/](03-repository-layout.md) for the same
finding reached from the repository tree.

**The decision piece 7 needs**, written here so it is taken once rather than per service: whether the
piece means *a policy file some platform backup service reads*, or *the service is backed up, by
whatever mechanism its operator already provides* — with `backup.yaml` as the fallback for services
whose operator has none. The evidence so far points at the second, because the first service that had
an operator answer preferred it.

⚠ **The second service declines piece 7 outright, which the first reading has no way to express.**
`CyberCloud.Cache/redis` is not backed up: `persistence` is a restart-survival setting and a cache is
not durable, so a backup on it would be the promise this document warns produces "a support incident
waiting to happen". Under the first reading that is a missing `backup.yaml` — indistinguishable from
somebody forgetting. Under the second it is simply a service whose operator offers no backup because
there is nothing to back up. **A service declining the piece for a stated reason is worth more to this
open question than a third service implementing it**, and it settles the reading: piece 7 is a
property of the service, not a file every chart owes.

The **2 engineer-weeks per service** target from [00](00-vision-and-principles.md) is a claim about
this list. It is measured: the roadmap tracks actual elapsed time per service and treats a miss as a
platform defect to be investigated, not as an estimate that was optimistic.

## Child resources

Six services in this document have sub-resources — `servers/databases`, `servers/roles`,
`servers/firewallRules`, NATS accounts and users, Kafka topics and users — and until now nothing said
what a child's *address* is. Two shapes were possible and the platform half-carried both.

**Decided: a child interleaves with its parent's name, exactly as Azure spells it.**

```
/tenants/{t}/subscriptions/{s}/resourceGroups/{rg}
  /providers/CyberCloud.DBforPostgreSQL/servers/{serverName}/databases/{databaseName}
```

and *not* the flattened alternative, which ran the type path whole and put one name at the end:

```
  …/providers/CyberCloud.DBforPostgreSQL/servers/databases/{databaseName}
```

`ResourceId` gains `ParentNames` — the ancestors' names, `/`-separated, empty for a top-level type —
with the invariant `ParentNames.Count == Type.Depth - 1`. A top-level resource's address is
unchanged, character for character.

### Why, in the order the reasons actually weigh

**1. The flattened shape cannot express the parent at all, and ReBAC needs it.**
`IResourceRelationWriter.LinkToParentAsync` takes a `ResourceId` and nothing else — no body, no
registration, no grain call. Under the flattened shape `…/servers/databases/orders` does not say
*which* server, so the `parent` edge could only ever point at the resource group, which is exactly
what `ReBacResourceRelationWriter` writes today for every resource regardless of depth. Granting
somebody a Postgres server would grant nothing on its databases, and deleting the server would leave
its databases' tuples pointing at a resource group that never knew about them. Interleaved,
`ResourceId.Parent` is a pure function of the address: drop the last type segment and the last name.
This is the reason that decides it, and it is an argument from a method signature rather than from
parity.

**2. Names would otherwise be unique per resource group rather than per parent.** The path index is
keyed on `sha256(CanonicalPath)` and the flattened canonical path contains no parent name, so two
Kafka clusters in one resource group could not both have a topic called `events`. That is a
functional limitation users hit on day one, not a difference of taste.

**3. The ambiguity in the old grammar is removed rather than capped.**
[06](06-tenancy-and-resource-model.md) § Identifiers used to say the grammar "is ambiguous on its own,
and the naming rule is what saves it" — `servers` + name `databases/orders` and `servers/databases` +
name `orders` rendered the same string, and only the ban on `/` in a name kept the second
reading out. Interleaved, type segments sit at even offsets after the namespace and names at odd
ones, so the tail length is always even and the depth is half of it. There is no collision left to
close. The naming rule stays load-bearing as the *second* of two independent defences rather than the
only one, and `ResourceTypeName.MaxDepth` stays because a type path read on its own still has no such
structure.

**4. [14](14-networking.md) § DNS already assumed this shape.** It specifies record sets as
`dnsZones/{zone}/A/{name}` — interleaved — which the `ResourceId` sketched in
[06](06-tenancy-and-resource-model.md) § Identifiers could not express, because that record has one
`Type` and one `Name`. One of the two documents was going to be corrected either way; this decision
corrects 06 and leaves 14 standing.

**5. Azure parity.** Real, and the weakest of the five. A child that does not address like Azure's is
a surprise where users have the strongest priors — but parity is the reason to prefer this shape, not
the reason it is correct.

### What it cost

Nearly nothing, because it was made before the first child shipped. No provider registers a nested
type today, `openapi/2026-08-01.json` contains no nested path, and the two shapes are identical at
depth 1 — so no published URL moved, the OpenAPI compatibility gate had nothing to report, and the
generated CLI, SDK and forms did not churn. The same decision taken after Provider 4 ships topics
would have been a breaking change to a published api-version.

### What is still owed

The grammar is in place and the parent is derivable; one consequence is not yet built and belongs
with whoever adds the first child type.

- ~~**`ReBacResourceRelationWriter` still points every `parent` edge at the resource group.**~~
  **Done.** The subject is `resource:{parentId}` when `ResourceId.Parent` is non-null and the
  resource group only when it is null. The extra hop needed **no schema change and no
  `SchemaVersion` bump**, which was checked rather than assumed: `CyberCloudSchema` declares
  `parent` on `resource` with no subject-type constraint, and gives `resource` the same
  `This | From(parent, …)` rewrites it gives `resourceGroup`, so the rewrite composes with itself —
  four hops against `CheckLimits`' twelve.
  ⚠ The parent's GUID is **not** in the address, so it is resolved through `IResourceIndexGrain`
  **by the caller** and passed in: the create reuses the lookup the parent-existence check above
  already makes, and the delete resolves it once, at request time, onto
  `OperationSpec.ParentResourceId`. The writer must never resolve it itself — `UnlinkFromParentAsync`
  runs after `CompleteDeleteAsync`, retried from a reminder, by which time the parent may be gone
  (see the bullet below), so a lookup there would fail every retry and leak the tuple it was trying
  to remove. For the same reason the edge is now written on the **create only**: a resource's parent
  cannot change, and re-linking on an update — where the parent GUID is deliberately not
  resolved — would have left a child holding two `parent` tuples.
  `ParentEdgeTests.AChildsEdgePointsAtItsParentResourceRatherThanAtTheGroup` reads the tuple.
- ~~**Nothing checks that a parent exists at create.**~~ **Done.** `ResolveAsync` resolves
  `ResourceId.Parent` through `IResourceIndexGrain` and answers the same `404` from the same helper,
  for the enumeration-oracle reason. Only a *confirmed* binding counts, so a parent under an
  unexpired two-phase-create claim reads as absent. It runs on the **create** only — it sits just
  after the index read, because "is this a create" is that read's answer — since re-checking on every
  write would turn a deleted parent into a frozen child that answers `404` to a `GET` or a `PATCH`
  for a resource that plainly exists. `ParentExistenceTests` holds both halves.
- **Deleting a parent that has children is still unanswered in code.** The decision is
  [08 § Deleting a parent resource that has children](08-resource-manager.md) — refuse with a `409`,
  never cascade — and it is not implemented, because the platform cannot yet enumerate a resource's
  children. Until it is, deleting a parent leaves its children addressable and pointing at nothing.

## Sizing vocabulary

One table, defined once, used by every service and every VM ([13](13-compute-vm-containers.md)).
Taken from Cozystack (ADR-010) because instance families are a vocabulary users already have.

| Family | Ratio | For |
|---|---|---|
| `t1.*` | burstable, 1:2 | Dev, small |
| `c1.*` | 1:2, guaranteed | CPU-bound — brokers, gateways |
| `s1.*` | 1:4 | General — most databases |
| `m1.*` | 1:8 | Memory-bound — caches, analytics |
| `u1.*` | 1:4, no overcommit | Latency-sensitive |

Sizes `nano · micro · small · medium · large · xlarge · 2xlarge · 4xlarge`. A tenant may also give
explicit `cpu`/`memory` quantities; the preset is a default, not a cage.

## The catalogue

### PostgreSQL — `CyberCloud.DBforPostgreSQL/servers` · M1 · 1.2 EM

**CloudNativePG.** The best-run Postgres operator: streaming replication, automated failover,
declarative backup to S3 (which we have — [15](15-storage-blob-file.md)), PITR, online minor upgrades,
and a genuinely good `Cluster` CRD.

- Versions 16/17/18; the resource declares one and minor upgrades are automatic in a maintenance window.
- Replicas 1–5, synchronous or async, declared.
- Sub-resources: `servers/databases`, `servers/roles`, `servers/firewallRules`.
- Extensions from an allow-list (`pgvector`, `postgis`, `pg_stat_statements`, `timescaledb`) — an
  arbitrary-extension escape hatch is a code-execution surface and is not offered.
- Connectivity: in-cluster `Service` always; external via a Kube-OVN floating IP with a firewall list;
  ⚠ **connection pooling (PgBouncer) is on by default**, because a managed Postgres without it fails
  at the first serverless workload and adding it later changes the connection string.
- Backup: CNPG's own barman-cloud to the tenant's bucket. PITR window is a plan attribute.

### Valkey — `CyberCloud.Cache/redis` · M1 · 1.0 EM

**Valkey via `spotahome/redis-operator`,** not Redis (ADR-011 — licensing). API-compatible; the product
page says Valkey and the connection string works with every Redis client.

- ~~Modes: `Standalone`, `Sentinel` (HA), `Cluster` (sharded).~~ ⚠ **CORRECTED 2026-08-12 by the
  provider landing — the operator this row names implements one of the three.** The rule stands and is
  the reason the correction matters: these are not interchangeable and the API must not pretend they
  are, because a client that works against Sentinel may not work against Cluster (multi-key
  operations, `SELECT`). The mode is immutable after create and the docs say why. See below.
- Persistence: `None` / `RDB` / `AOF`, defaulting to `AOF` with `everysec`. The [05](05-state-and-storage.md)
  honesty about what that means is repeated in the product docs, because a customer treating a managed
  cache as durable is a support incident waiting to happen.
- ~~TLS on by default;~~ ⚠ **not offered — see below.** `requirepass` from Vault.
- Memory: an eviction policy, and a `maxmemory` **derived from the container's limit rather than
  exposed**. ⚠ Not in the original list and it is not a nicety: Valkey consults `maxmemory-policy`
  only when a ceiling is set, so a policy on its own is a setting that has never been read and the pod
  is OOM killed instead of evicting. Three quarters of the limit, because a background save forks and
  copies pages, and the replication backlog and client output buffers sit outside the ceiling.

> ⚠ **CORRECTED 2026-08-12, by building it. Three of the things above are not renderable on
> `spotahome/redis-operator`, and all three were checked against that operator's source rather than
> its README** — which is why they survived to be written down. `charts/managed/valkey/SOURCE` records
> the files and `charts/managed/valkey/conformance.yaml § owed` carries each as a named debt.
>
> * **`Cluster` mode does not exist.** The operator ships one CRD, `RedisFailover`, and it has no
>   sharding. There is nothing to render, so the type declares no such value.
> * **`Standalone` mode is not expressible.** `api/redisfailover/v1/validate.go` replaces a sentinel
>   replica count of `<= 0` with `defaultSentinelNumber`, which is 3 — so every `RedisFailover` runs a
>   Sentinel quorum whatever the spec says, and a `Standalone` member would be a value the API accepts
>   and the cluster ignores.
> * **TLS is not a field.** Neither `RedisSettings` nor `SentinelSettings` carries one, so "TLS on by
>   default" has nothing behind it. No `tls.enabled` property is declared, because a boolean the
>   reconciler accepts and cannot honour is worse than its absence.
>
> **So `mode` ships with exactly one member, and the property exists anyway.** Omitting it would be
> tidier today and would cost a new api-version later: a mode is immutable identity, an api-version is
> immutable once published, and a topology axis that appears for the first time in 2027 is a new date
> for every caller. One member is the API saying "this axis exists and today it has one value"; three
> would be the pretence this row's own ⚠ forbids.
>
> **What closing any of them takes** is a different operator (an ADR-010 clause 1 change), a sidecar
> proxy for TLS, or upstream work — none of which is a provider's to do, and all of which are cheaper
> to decide with the finding written down here than to rediscover per service.

⚠ **`requirepass` costs more here than the equivalent did on PostgreSQL, and the difference is worth
carrying into the remaining eight services.** Piece 5 — credential provisioning into the tenant's
Vault — is not built. CloudNativePG *generates its own password* when the `Secret` its CR references
is absent, so `CyberCloud.DBforPostgreSQL/servers` has a working database whose credentials `listKeys`
cannot yet hand out. spotahome generates nothing: with `spec.auth.secretPath` set and no `Secret`, the
cache does not come up. The provider renders the reference anyway, because the alternative — omitting
the block — is a running, unauthenticated Valkey reachable by anything in the tenant's namespace, and
this document's own **"a managed database on a public IP with a weak password is the single most
common cloud breach"** applies inside a namespace too. **A resource that visibly has not finished
beats one that quietly came up open**, and the general lesson is that "the operator will fill in the
gap" is a property of one operator rather than of the pattern.

### MongoDB-compatible — `CyberCloud.DocumentDB/accounts` · M2 · 1.2 EM

**FerretDB** (Apache-2.0) over a CloudNativePG cluster. ADR-011: real MongoDB is SSPL and cannot be
offered as a service.

⚠ **This is a compatibility layer and the product page must say so, with a supported-subset table.**
FerretDB covers CRUD, indexes, aggregation basics and the wire protocol; it does not cover change
streams, transactions across collections, or the full aggregation pipeline. Selling it as "MongoDB"
produces a churn event at the first `$lookup`. Selling it as "MongoDB-compatible document database,
here is exactly what works" produces a happy customer with a smaller use case.

Upside worth stating: because it is Postgres underneath, backup, PITR and HA are CloudNativePG's,
already built for the row above.

### NATS — `CyberCloud.Messaging/natsClusters` · M1 · 0.8 EM

The cheapest provider in the catalogue, because we run NATS for ourselves (ADR-005) and therefore
already know how.

- 3 or 5 servers, JetStream on, file storage on LINSTOR volumes.
- Accounts and users as sub-resources, with NKey/JWT credentials into Vault.
- Leaf-node connectivity so a tenant's edge can attach.
- Monitoring endpoint scraped; a dashboard ships with the chart.

### RabbitMQ — `CyberCloud.Messaging/rabbitmqClusters` · M2 · 0.8 EM

**RabbitMQ Cluster Operator** (official). Quorum queues by default — classic mirrored queues are
deprecated upstream and default-to-deprecated is a trap. Management UI exposed through the portal's
authenticated proxy rather than a public route.

### Kafka — `CyberCloud.Messaging/kafkaClusters` · M2 · 1.2 EM

**Strimzi**, KRaft mode (no ZooKeeper). Topics and users as sub-resources — Strimzi's `KafkaTopic` and
`KafkaUser` CRDs map to resource types almost one to one, which is why this is 1.2 and not 2.5.

⚠ Kafka is the most operationally demanding service here: rebalancing, retention sizing and broker
disk pressure are ongoing rather than one-time. Cruise Control ships in the chart and the runbook is
part of the deliverable, not a follow-up.

### ClickHouse — `CyberCloud.Analytics/clickhouseClusters` · M2 · 1.2 EM

**Altinity operator.** We run ClickHouse for telemetry and metering ([16](16-observability.md),
[22](22-billing-metering-and-quota.md)) so the operational knowledge is not incremental.

- Shards × replicas declared; ZooKeeper/ClickHouse Keeper managed by the operator.
- S3-backed disks for cold storage tiers.
- ⚠ Schema is the tenant's problem and the resource does not manage tables. A managed ClickHouse that
  tries to own DDL is a migration tool nobody asked for.

### MariaDB — `CyberCloud.DBforMySQL/servers` · M3 · 0.8 EM

**mariadb-operator.** Galera for HA, or async replication. Positioned as MySQL-compatible; the same
honesty rule as FerretDB applies to the compatibility claim.

### OpenSearch — `CyberCloud.Search/services` · M3 · 1.0 EM

**OpenSearch operator** (Apache-2.0, ADR-011 — Elasticsearch is not available to us). Data/master/coordinating
node roles, ISM policies, snapshot repository into the tenant's bucket.

### Qdrant — `CyberCloud.Search/vectorStores` · M3 · 0.6 EM

Not an Azure row. A 2026 catalogue without a vector store is dated on arrival, and Qdrant's operator
model is simple enough that this is the cheapest M3 item.

## Cross-cutting decisions

**Connectivity.** Every service gets an in-cluster DNS name always, and optional external exposure via
a Kube-OVN floating IP plus a firewall allow-list ([14](14-networking.md)). ⚠ **External exposure is
never the default** and the API requires an explicit CIDR list — a managed database on a public IP
with a weak password is the single most common cloud breach, and defaulting it off costs one flag.

**Credentials.** Generated at create, written to the tenant's Vault path, never in grain state
([05](05-state-and-storage.md)). `listKeys` is an action with its own permission, audited on every
call, and `regenerateKeys` is a separate action with a rolling grace period so rotation is not an outage.

**Versions and upgrades.** Each type declares supported major versions and a deprecation date. Minor
upgrades happen automatically in the tenant's maintenance window; major upgrades are an explicit
resource update with a documented path. A version leaving support is a portal notice, an email, and a
120-day window — decided now because the alternative is a catalogue where nothing can ever be upgraded.

**Backups.** Two layers: the engine's own (CNPG's barman, ClickHouse `BACKUP`, OpenSearch snapshots) to
the tenant's bucket, and Velero + volume snapshots for whole-namespace recovery. Restore is an action
on the resource that creates a *new* resource — restore-in-place is how people lose the good copy.

**Observability.** Every chart ships a scrape config and a dashboard, both surfaced under the
resource's Monitoring blade. A managed service the tenant cannot see the health of is a black box they
will not trust with production.

**HA is a plan attribute, not a checkbox.** `Basic` (single replica, no SLA, cheap), `Standard`
(2–3 replicas, zone-spread, backups), `Premium` (multi-replica, sync, PITR, priority support). It is a
single field on every service and it maps to concrete replica counts and anti-affinity per chart —
which stops "HA" meaning something different for each service.

## Effort

| Service | M | EM |
|---|---|---|
| PostgreSQL | M1 | 1.2 |
| Valkey | M1 | 1.0 |
| NATS | M1 | 0.8 |
| FerretDB (Mongo-compatible) | M2 | 1.2 |
| RabbitMQ | M2 | 0.8 |
| Kafka | M2 | 1.2 |
| ClickHouse | M2 | 1.2 |
| MariaDB | M3 | 0.8 |
| OpenSearch | M3 | 1.0 |
| Qdrant | M3 | 0.6 |
| Shared: sizing catalogue, credential flow, backup binding, HA plans, dashboards | — | 1.5 |
| **Total** | | **11.3** |
