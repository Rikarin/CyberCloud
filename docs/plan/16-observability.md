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
| Dashboards | **Grafana** per tenant — ⚠ landed as one Deployment per `CyberCloud.Dashboard/grafanas` resource, not `grafana-operator`; § Managed Grafana says why | — | ⚠ AGPL: we distribute unmodified, we do not link. The portal embeds rendered panels by URL |
| Alerting | **vmalert** + a notification grain — ⚠ landed as one evaluator grain per workspace on a reminder, with no `vmalert` between it and the store; § Alerts says why | — | Rules are a resource; delivery is [17](17-communication-and-email.md) |

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

### Resource model — landed 2026-09-17 (#32)

⚠ **Under the workspace, and the configuration is rendered rather than accepted.** The sketch above is a
tenant declaring receivers, processors and exporters; what landed offers the allow-list's safest subset
as two switches and renders the whole file itself, so there is no config to validate and no exporter a
tenant can point elsewhere:

```
CyberCloud.Monitor/workspaces/{workspace}/collectors/{name}
  ├─ clusterId — ⚠ the cluster the workspace publishes into, see below
  ├─ receivers/{otlpGrpc, otlpHttp} — at least one; both off is refused at the first pass
  ├─ replicas ≤ 3, sizing/preset (c1.small | c1.medium | c1.large)
  └─ action: listEndpoints → the Service's in-cluster address for each protocol that is on
```

Three objects — a `ConfigMap` holding the collector configuration, a `Deployment` of upstream's
`otel/opentelemetry-collector-contrib` **pinned by digest in the bundle's shape**, and a `ClusterIP`
`Service`. The exporters address the workspace's stores exactly as the workspace's own `listKeys` does:
`prometheus_remote_write` to the remote-write endpoint under its `accountID`, `clickhouse` to the query
host under its database, both authenticated as the workspace's `VMUser` with its ingest key.

⚠ **The workspace's coordinates are in none of the three documents, and that is the design.** A child's
reconcile pass never learns its parent's GUID, and the `accountID` is a fold of that GUID — the fact
that keyed the alert evaluator. So the configuration writes `${env:CYBERCLOUD_ACCOUNT_ID}`,
`${env:CYBERCLOUD_DATABASE}` and `${env:CYBERCLOUD_INGEST_KEY}`, and the `Deployment` hands the pod
those three from the workspace's own row (`configMapKeyRef`) and ingest-key `Secret`
(`secretKeyRef`): the **kubelet** does the reading at pod start. Every rendered document is therefore a
pure function of the address and the body, the ingest key never passes through the control plane a
second time, and a collector whose workspace has not converged is a pod the kubelet holds by name
until it has. The cost is stated rather than discovered: the collector runs in the cluster its
workspace publishes into, which is the regional cluster and not a tenant's connected one —
`charts/managed/monitor-collector/conformance.yaml § owed`,
`collector-runs-where-its-workspace-is-published`. § Ingest's row, published for a host that does not
exist, has two readers now, and both are pods.

⚠ **The pod is proved to start.** `MonitorCollectorClusterBackedConformance.TheCollectorPodStartsAndAcceptsAnOtlpExport`
runs the rendered `Deployment` on the cluster-backed suite's k3s — a kubelet, the workspace's row and
key, and nothing else — and asserts the pod goes Ready and an OTLP/HTTP export POSTed through the API
server's service proxy is answered `200`. It is the first cluster-backed assertion in the tree that
reads what a node did rather than what the API server holds. What it does not prove is where the
export goes: both exporters point at hosts the k3s does not resolve, and the batch processor is what
lets the receiver answer in front of them.

What the sketch above promises and this does not, each `charts/managed/monitor-collector/conformance.yaml
§ owed`: the declarative receiver, processor and exporter lists with the allow-list over them
(`tenant-authored-config-is-not-accepted`, which also covers `daemonset` mode and tenant-owned
exporters with their metered egress); an authenticated ingress (`collector-ingress-is-unauthenticated`);
the ClickHouse tables the exporter writes into, which nothing creates
(`collector-clickhouse-tables-are-the-exporters-shape`); and the two platform hosts the configuration
names and nothing resolves (`the-endpoints-are-named-not-resolved`).

## Alerts — M2

`CyberCloud.Monitor/alertRules`: a query, a threshold, a duration, a severity, an action group.
Evaluated by vmalert against the tenant's data; firing alerts go to `cc.{tenant}.alerts`, consumed by
a notification grain that fans out via [17](17-communication-and-email.md) (email, SMS, WhatsApp,
webhook, and a portal inbox).

⚠ **Two of those five are not deliverable today, and nothing below this line changes that
(2026-09-15, #32 review).** The landed action group is one channel of the sending module's own
`ChannelKind` — `sms`, `whatsapp`, `email`, `push`, `voice` — because the notification goes through
`IMessageSender` and that is what it sends. A webhook is not a channel the sending module has, and a
portal inbox is not a channel at all but a store the portal reads; neither landed with #32, and both
are `charts/managed/monitor-workspace/conformance.yaml § owed`, `alert-rules-webhook-and-inbox-not-landed`.

⚠ **Alert-rule evaluation is tenant-authored query execution on shared infrastructure.** Query cost
limits, a per-workspace concurrent-evaluation cap and a max look-back are mandatory from day one, not
hardening added later.

### Resource model — landed 2026-09-15 (#32)

One type, under the workspace rather than beside it, because a rule's query has no meaning without a
store to run against and the store is the workspace:

```
CyberCloud.Monitor/workspaces/{workspace}/alertRules/{name}
  ├─ enabled, severity (critical|error|warning|informational)
  ├─ condition/{signal (metrics|logs), query, operator, threshold, lookbackSeconds ≤ 86400}
  ├─ evaluation/{intervalSeconds ≥ 60, forSeconds}
  ├─ actionGroup/{service — a CyberCloud.Communication/services resource id path,
  │               channel (sms|whatsapp|email|push|voice), recipients[], notifyOnResolve}
  └─ action: listInstances → every firing, oldest first: fired, resolved, value, and what the
                             sending module said per recipient
```

⚠ **The evaluator is a grain on a reminder and not `vmalert`, which is what § The stack names, and
the reason is what would have sat between them.** `vmalert` evaluates rules from a file or a CRD and
posts to an Alertmanager-shaped endpoint; wiring it means a per-workspace `VMRule`, a receiver that
turns an Alertmanager webhook into a grain call, and the NATS subject above in between — three things
that do not exist, in front of the one that carries the product: a notification through
[17](17-communication-and-email.md). The platform already has a scheduler every module uses
([04 § Reminders](04-orleans-topology.md), now five uses) and a sending module with idempotency and
suppression built in, so `IAlertEvaluatorGrain` asks a question on a schedule and sends on the answer.
**One grain per workspace, keyed by the workspace's address** — a child's reconcile pass never learns
its parent's GUID, which is the same fact that keyed the sending module's grains — and its
single-threaded activation *is* the per-workspace concurrency cap the paragraph above makes mandatory.
The look-back is a schema maximum; the query's length, the rules a workspace may carry (50), a timeout
per query (10 s) and a budget per pass (15 s) bound its cost. What none of these does is inspect the
query. ⚠ **The cap is also a queue, and the first version priced it as one query (2026-09-15, #32
review).** A pass runs in one turn of the activation, so a reconcile pass or a `listInstances` queued
behind it waits for the whole pass against Orleans' 30-second response timeout; fifty queries at the
old 30-second timeout could hold it for twenty-five minutes when the store stopped answering. The
reads interleave now, the pass stops asking at its budget and the rules it did not reach go first on
the next tick, and the budget plus one query timeout is pinned under the response timeout. A seam that
*throws* rather than fails — what `HttpClient` does on a refused connection — is recorded on that
rule and the pass goes on, where it used to end the pass at that rule and leave every rule after it
unevaluated for as long as the fault lasted.

⚠ **The action group names the sending service by resource id path — the first property in the
catalogue with `SchemaFormat.ResourceId`.** [03 § Assembly graph rules](03-repository-layout.md), rule
2, sends cross-provider traffic *"through CyberCloud.ResourceManager by resource id"*; here the id is
enough on its own, because the sending module keys a service's grain on a GUID derived from that very
path. The rule reaches the tenant's service with no index read and no reference to the other
provider — and is refused at the pointer when the path is another tenant's, or names anything but a
`CyberCloud.Communication/services` resource. Suppression is the sending module's: a recipient on the
service's list comes back as a refusal, per recipient, recorded on the instance by name.

⚠ **What ◐ on the roadmap row does not mean.** No host registers a real `IAlertQuerySeam`: every
evaluation in this tree runs against the refusing default, records its sentence on the rule and moves
nothing — a store that does not answer neither fires nor resolves, deliberately. The real seam — a
PromQL `/api/v1/query` under the workspace's `accountID`, a SQL query under its database, both resolved
from the row the workspace publishes — is `charts/managed/monitor-workspace/conformance.yaml § owed`,
`alert-rules-query-seam-is-refusing`, and it needs a real VictoriaMetrics to be proved against. One
rule is one instance however many series offend, the schema having no array of objects; the
per-series shape is the api-version that grows the tree. ⚠ **This paragraph used to end by naming
three nouns of #32's four this did not take; two of them landed on 2026-09-17** — `collectors` above
and managed Grafana below, each a resource type with a chart of its own. The App Insights-shaped views
were the one noun left, undesigned and unpriced by this document; they landed on 2026-09-23 as
[§ Application views](#application-views--cybercloudmonitorworkspacescomponents--m2) and the owed row
that stood for them is gone.

## Managed Grafana — `CyberCloud.Dashboard/grafanas` · M2 · 0.8 EM

`grafana-operator`, one instance per tenant, OIDC against our identity system, datasources pre-wired
to that tenant's workspace **and nothing else**. Dashboards as a sub-resource so they are versioned and
restorable like any resource.

The portal does not embed Grafana's UI. It renders its own charts (`@xui/echarts`) for the common
views — resource health, the four golden signals, cost — and links out to Grafana for exploration.
Embedding someone else's SPA inside ours produces two auth models, two themes and two bug trackers.

### Resource model — landed 2026-09-17 (#32)

```
CyberCloud.Dashboard/grafanas/{name}
  ├─ clusterId — the workspace's cluster
  ├─ workspace — a CyberCloud.Monitor/workspaces resource id path, ⚠ this tenant's, this resource group's
  ├─ anonymousViewers, sizing/preset
  └─ action: url → the in-cluster URL, the admin user, and the admin password minted once into the vault
```

⚠ **ADR-011, read for a deployed component, is what lets the type converge.** The row says *"Offerable
as a managed instance (we distribute, we do not modify). Our portal must not embed or link Grafana code
— it embeds rendered dashboards by URL."* What landed is upstream's `grafana/grafana` **by digest,
unmodified**, configured through `GF_*` variables and a datasource provisioning file, in one
`Deployment` per resource; the portal takes no Grafana package (`GrafanaDeclarationTests` reads
`portal/package.json` to keep it that way) and the `url` action is the one integration. The exception
ADR-011 § Enforcement asks for is written into `build/Build.Licence.cs § LicenceExceptions` beside the
image with this reading as its argument — ⚠ and the scan does not read that image yet, because it reads
bundle components and platform images and a chart under `charts/managed/` is neither
(`charts/managed/grafana/conformance.yaml § owed`, `licence-scan-does-not-read-workload-images`). Had
the row refused AGPL for a deployed component, the type would be published exactly as it is and the
reconciler would fail every pass naming the ADR; it does not, so it converges.

⚠ **Not `grafana-operator`, which the heading above names.** The operator is a bundle component that
does not exist, a CRD the cluster-backed harness would stub with an open schema, and a second
reconciler between this one and the pod; what it would buy — dashboards as the sub-resource the
paragraph above asks for — needs an api-version with an array of objects the schema does not have.
Grafana's state is an `emptyDir` on purpose: a claim would make dashboards survive a restart and look
versioned while being neither. `charts/managed/grafana/conformance.yaml § owed`,
`dashboards-are-not-a-sub-resource` and `not-grafana-operator`.

The two datasources are the workspace's `listKeys` endpoints — Prometheus at the PromQL endpoint,
the ClickHouse plugin over HTTP at the SQL endpoint — authenticated as the workspace's `VMUser`, both
`editable: false`, and both reach the workspace's coordinates the way the collector does: Grafana's own
`$VAR` provisioning interpolation over the three variables the kubelet fills from the workspace's row
and `Secret`. That is why the workspace must be in the instance's resource group — a pod cannot mount
a `Secret` from another namespace — and the reconciler refuses a pointer that is another tenant's,
another group's or another type's before it mints anything. **OIDC against our identity system is not
wired** (`oidc-against-identity-is-not-wired`); the URL is in-cluster (`no-external-endpoint`); the
ClickHouse plugin is fetched from grafana.com on every pod start by Grafana's own installer rather than
baked into an image (`clickhouse-plugin-is-fetched-at-start`).

⚠ **The pod is proved on a kubelet, and the proof is each datasource's health rather than the
server's.** The first version of this branch left the Grafana pod's start unproved by record, and the
adversarial review ran the image: at 13.2.2 the Prometheus datasource is a *bundled plugin* on the
root filesystem, Grafana's installer updates preinstalled plugins in place on start by default, and
on the read-only root the update stopped the plugin and could not put it back — while `/api/health`,
the readiness probe, answered `200` throughout and the provisioned `Metrics` datasource answered
`Plugin not registered`. `GF_PLUGINS_PREINSTALL_AUTO_UPDATE=false` is the fix, and
`GF_PLUGINS_PREINSTALL_SYNC` at a pinned `id@version` is how the ClickHouse plugin is installed;
`GrafanaClusterBackedConformance.TheGrafanaPodStartsAndBothDatasourcesAnswer` watches the kubelet hold
the pod for a workspace row that is not there yet, writes the workspace's row and `Secret` as the
workspace's reconciler would, waits for Ready, and then asks both datasources' health through the API
server's proxy — asserting each answered with a request at the workspace's own accountID and database
and neither with `plugin.notRegistered`. `charts/managed/grafana/SOURCE § What was run` carries the
transcript, including the finding that a cluster with no egress gets a pod that exits `1` before it
listens rather than a Grafana with one datasource of two.

## Application views — `CyberCloud.Monitor/workspaces/components` · M2

[01](01-azure-parity-catalogue.md)'s row is *"Application Insights | ⊂ workspaces | M2 | OTLP ingest,
a per-tenant ClickHouse database, and the trace/exception views"*, with no estimate; this section is
the design that row never had, written as it landed (2026-09-23, #32's fourth noun).

```
CyberCloud.Monitor/workspaces/{workspace}/components/{name}
  ├─ clusterId — the cluster the connection string is published in (its collector's)
  ├─ collector — a sibling collector's NAME; protocol (http/protobuf | grpc)
  ├─ action: listConnectionString → OTEL_EXPORTER_OTLP_ENDPOINT, _PROTOCOL, OTEL_RESOURCE_ATTRIBUTES
  └─ actions, each POST {timespanMinutes ≤ 1440, top ≤ 100}:
       requests       — server/consumer spans by service and operation: count, failures, rate/min,
                        failure rate, exact p50/p95/p99
       dependencies   — client/producer spans by caller, type (db|http|messaging|rpc|other), target, name
       exceptions     — `exception` span events and log records carrying exception.type, by type
       applicationMap — nodes, and edges where a span's parent is in another service
       transaction    — POST {traceId, timespanMinutes ≤ 10080}: one trace's spans and logs, in order
```

**A component is a lens, not a store.** The workspace is the tenancy — one ClickHouse database; a
component names the slice one application writes, every span and log record whose resource carries
`service.namespace` equal to the component's name, and its connection string sets exactly that. One
object is rendered: a `ConfigMap` holding the three `OTEL_*` variables, so a pod is wired with
`envFrom`. ⚠ The namespace is the client's assertion — it separates applications, not tenants
(`charts/managed/monitor-component/conformance.yaml § owed`, `the-namespace-is-the-clients-assertion`).

⚠ **The tenant boundary is the database, and the database comes from the platform's index.** A view
runs on the request path — the gateway's process, like every synchronous action — and reads
`ws_{guid:N}` of the workspace's GUID. A child's reconcile pass never learns its parent's GUID, so the
action path now carries it: `ResourceManagerService` resolves the parent through the tenant's own
index on every action and hands it to the handler as `ActionContext.Parent`
(`ActionParentTests`). The workspace's row `ConfigMap` also names the database and was rejected as the
source: it lives in a namespace of a cluster the tenant may administer, and a rewritten row would be a
read of another tenant's telemetry.

⚠ **Every statement is the platform's and every value is bound.** The views' SQL is constants in
`ComponentViews`; the window, the row limit, the namespace and a trace id (held to 32 hex digits by the
schema) are `{name:Type}` parameters; the database is the request's `database=` setting, never text in a
statement. Five settings ride on every query — `readonly=2`, `max_execution_time`, `max_rows_to_read`,
`max_memory_usage`, and unquoted 64-bit integers — and ClickHouse's own words go to the log, never to
the caller: a budget refusal is a `400` naming it, an unprovisioned workspace a `409`, anything else a
bare `500`. The percentiles are exact (`quantileExact`: position ⌊level × n⌋, no interpolation), which is
what the look-back cap and `max_memory_usage` pay for. **Columns, not rows**: the schema has no array of
objects, so each view is parallel arrays a chart can take as series.

⚠ **Proved against the real thing.** `ComponentViewsAgainstClickHouseTests` emits OTLP, the pinned
collector running the configuration `MonitorCollectors.CollectorConfig` renders exports it into a real
ClickHouse, and every view's numbers are asserted through the real manager, dispatcher and handler —
including that a component sees only its namespace and another tenant's component only its own
database. `MonitorComponentViewsOverHttpTests` puts the gateway's stages on a real socket in front of the
same manager: the owner's view answers `200` with its numbers, another tenant's caller gets `404` and the
store is never asked. And the tables: `MonitorTelemetrySchema` holds the four statements the exporter
itself creates with `create_schema: true` — measured, not recalled — and
`ComponentViewsAgainstClickHouseTests.ThePlatformsTablesAreTheExportersShape` compares every column,
key and index. ⚠ **Nothing in production runs them yet**, so a view on a real workspace answers `409`
naming `collector-clickhouse-tables-are-the-exporters-shape`; the store the gateway reads is
`CyberCloud:Monitor:Telemetry`, the refusing default until a region sets it, with one read credential
for every workspace (`one-read-credential-for-every-workspace`). The portal's pages over the five
views are #41's (`portal-views-are-not-built`); `cyc monitor component requests|dependencies|exceptions|application-map|transaction`
are generated from the declaration like every other verb.

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
