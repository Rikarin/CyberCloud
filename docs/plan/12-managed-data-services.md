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
| 5 | Credential provisioning into the tenant's Vault, exposed by a `listKeys` action | `ISecretResolver` |
| 6 | A `ServiceMonitor`/`VMPodScrape` + a Grafana dashboard | ~~The chart~~ ⚠ the operator, where there is one — corrected below |
| 7 | A backup policy binding (Velero + volume snapshots) | `charts/managed/{svc}/backup.yaml` — ⚠ under-specified, see below |
| 8 | A conformance manifest | `charts/managed/{svc}/conformance.yaml` |

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

The **2 engineer-weeks per service** target from [00](00-vision-and-principles.md) is a claim about
this list. It is measured: the roadmap tracks actual elapsed time per service and treats a miss as a
platform defect to be investigated, not as an estimate that was optimistic.

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

- Modes: `Standalone`, `Sentinel` (HA), `Cluster` (sharded). ⚠ These are not interchangeable and the
  API must not pretend they are — a client that works against Sentinel may not work against Cluster
  (multi-key operations, `SELECT`). The mode is immutable after create and the docs say why.
- Persistence: `None` / `RDB` / `AOF`, defaulting to `AOF` with `everysec`. The [05](05-state-and-storage.md)
  honesty about what that means is repeated in the product docs, because a customer treating a managed
  cache as durable is a support incident waiting to happen.
- TLS on by default; `requirepass` from Vault.

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
