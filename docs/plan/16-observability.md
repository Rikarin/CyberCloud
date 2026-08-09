# 16 — Observability

Two audiences with the same pipeline: **our** operators, and **tenants** who buy monitoring as a
service. Building one pipeline for both is the decision that makes this affordable — and it is only
safe because tenancy is enforced at ingest, not at query.

## The stack

| Signal | Store | Query | Why |
|---|---|---|---|
| Metrics | **VictoriaMetrics** cluster | PromQL / MetricsQL | Native multi-tenancy via `accountID`, far cheaper than Prometheus + Thanos, drop-in remote-write and query APIs (ADR-016) |
| Logs | **ClickHouse** | SQL | ADR-016 — Loki cannot answer "find this correlation id across a tenant" in bounded time, and that is the query support actually runs |
| Traces | **ClickHouse** | SQL + a trace view | One store for logs and traces means a span links to its logs by join, not by hope |
| Events (audit, resource changes) | **ClickHouse** | SQL | Same pipeline, different table |
| Dashboards | **Grafana** per tenant | — | ⚠ AGPL: we distribute unmodified, we do not link. The portal embeds rendered panels by URL |
| Alerting | **vmalert** + a notification grain | — | Rules are a resource; delivery is [17](17-communication-and-email.md) |

## Ingest

`CyberCloud.Ingest.Host` — deliberately **not** an Orleans client ([03](03-repository-layout.md)).
Putting a million spans per second through a grain call is the one mistake in this shape that would be
expensive to undo, so it is excluded by process boundary.

```
OTLP/gRPC · OTLP/HTTP · Prometheus remote-write · syslog · Fluent-forward
  └─ Ingest.Host
      ├─ authenticate  → an ingest key resolved to (tenant, workspace) from a cached map
      ├─ enforce       → tenant label injected/overwritten, cardinality cap, rate limit, quota
      ├─ enrich        → resource-id from the cybercloud.io/* labels (ADR-013) → the resource blade link
      └─ route         → VictoriaMetrics (accountID) | ClickHouse (per-tenant database) | NATS (alerts)
```

Three rules in that block are load-bearing:

- **The tenant label is injected by us and overwrites anything the client sent.** A tenant able to
  write another tenant's label is a cross-tenant data-injection bug, and it is the single most likely
  way to get this wrong.
- **Cardinality caps are enforced at ingest**, per workspace, with the offending label named in the
  rejection. One tenant putting a request id in a metric label is how a shared TSDB dies, and the
  rejection must be diagnosable or they will just retry.
- **Enrichment from the standard labels is what connects a pod's telemetry to the resource blade.** It
  is the reason the label discipline in ADR-013 is worth enforcing at compile time.

## `CyberCloud.Monitor/workspaces` · M1 · 2.5 EM

The tenant-facing resource. Owns retention, quota, ingest keys, and the datasource wiring.

| Property | Notes |
|---|---|
| `retention` | Per signal: metrics 15/90/400 days, logs 7/30/90, traces 3/14/30. Priced |
| `ingestKeys` | Sub-resource; scoped to the workspace; rotatable with a grace period |
| `quota` | GB/day per signal, series cap, span cap. Over-quota **samples**, never silently drops — and says so in the UI |
| `dataSources` | Read-only endpoints for the tenant's own Grafana or an external one |

**Platform telemetry uses the same machinery under a platform workspace.** No separate stack. If the
tenant-facing pipeline is broken, we find out because our own dashboards are broken, which is the
correct incentive.

## OTel Collector as a service — `CyberCloud.Monitor/collectors` · M2 · 1.0 EM

From the brief, and it is the right primitive — Azure has no equivalent and it is genuinely useful.

A tenant-owned collector deployment in their cluster, configured declaratively:

```yaml
receivers:  [otlp, prometheus, filelog, kubeletstats]
processors: [batch, memory_limiter, k8sattributes, filter, transform]
exporters:  [cybercloud, ...tenant's own]
```

- The config is validated by the reconciler against an **allow-list of components** before apply —
  an arbitrary collector config is a data-exfiltration primitive and a code-execution surface, and
  `filter`/`transform` are exactly where a bad config leaks PII or costs a fortune.
- The `cybercloud` exporter is pre-wired to the tenant's workspace with a rotating key.
- Deployment mode is `daemonset` (node-level) or `deployment` (gateway), declared.
- ⚠ A tenant may export to their *own* backends as well. That is the feature; it also means egress is
  metered and the config allow-list must cover exporter endpoints.

## Alerts — M2

`CyberCloud.Monitor/alertRules`: a query, a threshold, a duration, a severity, an action group.
Evaluated by vmalert against the tenant's data; firing alerts go to `cc.{tenant}.alerts`, consumed by
a notification grain that fans out via [17](17-communication-and-email.md) (email, SMS, WhatsApp,
webhook, and a portal inbox).

⚠ **Alert-rule evaluation is tenant-authored query execution on shared infrastructure.** Query cost
limits, a per-workspace concurrent-evaluation cap and a max look-back are mandatory from day one, not
hardening added later.

## Managed Grafana — `CyberCloud.Dashboard/grafanas` · M2 · 0.8 EM

`grafana-operator`, one instance per tenant, OIDC against our identity system, datasources pre-wired
to that tenant's workspace **and nothing else**. Dashboards as a sub-resource so they are versioned and
restorable like any resource.

The portal does not embed Grafana's UI. It renders its own charts (`@xui/echarts`) for the common
views — resource health, the four golden signals, cost — and links out to Grafana for exploration.
Embedding someone else's SPA inside ours produces two auth models, two themes and two bug trackers.

## What the platform monitors about itself

Because a control plane that cannot see itself cannot be operated:

| Signal | Alert on |
|---|---|
| Grains per silo, activation rate, collection rate | Approaching the [04](04-orleans-topology.md) ceiling |
| Grain call latency p99 by interface | The single best leading indicator of trouble |
| Storage tier latency and error rate, per shard | A slow shard before it is a dead shard |
| Reconcile queue depth and age, per provider | Convergence falling behind |
| Operations in a transitional state > 30 min | The stuck-forever class of bug |
| Cluster connection health, per managed cluster | Ours vs theirs |
| Orphans and strays from drift detection | Billing and correctness both |
| ReBAC check p99 and cache hit rate | On the hot path of everything |
| Rate-limit rejections by tenant | Abuse, and legitimate customers being throttled |
| Ingest rejections by reason | Cardinality bombs, bad keys, quota |

## Cost and retention honesty

Telemetry is the largest data volume in the platform by two orders of magnitude, and the two failure
modes are both expensive:

- **Storing everything forever.** Prevented by per-signal retention that is a *paid* property with a
  cheap default, and by tiering old ClickHouse partitions to object storage.
- **Silently dropping.** Prevented by making over-quota behaviour *sampling with a visible rate*
  rather than a drop, and by surfacing rejections on the workspace blade with the reason.

A monitoring product that quietly loses data is worse than no monitoring product, because it is
trusted.
