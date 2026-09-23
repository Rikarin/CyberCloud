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
are the one noun left, undesigned and unpriced by this document, and recorded as
`charts/managed/monitor-workspace/conformance.yaml § owed`,
`observability-app-insights-views-not-landed`, so the row's ◐ still has an entry behind it and not
only a sentence.

## Managed Grafana — `CyberCloud.Dashboard/grafanas` · M2 · 0.8 EM

`grafana-operator`, one instance per tenant, OIDC against our identity system, datasources pre-wired
to that tenant's workspace **and nothing else**. Dashboards as a sub-resource so they are versioned and
restorable like any resource.

The portal does not embed Grafana's UI. It renders its own charts (`@xui/echarts`) for the common
views — resource health, the four golden signals, cost — and links out to Grafana for exploration.
Embedding someone else's SPA inside ours produces two auth models, two themes and two bug trackers.
⚠ Since #41 the first-line exploration is the portal's own as well — a metrics explorer and a log
search over the workspace, § Querying a workspace — and Grafana is where a tenant goes for
dashboards, which the portal has none of.

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

## Querying a workspace — landed 2026-09-23 (#41)

The portal's metrics explorer and log search read a workspace's stores through three read actions on
`CyberCloud.Monitor/workspaces`, all three checking `read` — the permission the Reader role grants:

```
POST …/workspaces/{name}/queryMetrics      { query, start?, end?, time?, stepSeconds? }
POST …/workspaces/{name}/listMetricLabels  { label?, match?, start?, end? }        → { values[], truncated }
POST …/workspaces/{name}/searchLogs        { from, to, text?, severities[]?, service?,
                                             attributes[]? (key=value), traceId?, top?,
                                             bucketSeconds?, estimate? }
```

⚠ **Actions, not a fifth component behind the gateway, and the address decided it.** #54's resource
graph has its own route because a graph query has no resource to hang off; a metrics query has
exactly one. The action path already resolves the address with the *token's* tenant, answers `404`
for an address that is not there or not readable, checks the permission through the one enforcement
seam ([07](07-rebac-authorization.md)), and hands the handler the workspace's GUID — which is where
the `accountID` (`MonitorWorkspaces.AccountId`) and the database (`MonitorWorkspaces.Database`) come
from. So the tenancy coordinate the store is asked under is derived from what the platform resolved,
never from the request: the body has no member that names an account, a database or a URL, the
schema refuses members it does not declare (`extra_label`, VictoriaMetrics' own tenancy-narrowing
knob, is a `400`), and the stores' endpoints are `CyberCloud:Monitor:Query` configuration. Synchronous
actions run inside `ResourceManagerService` in the gateway's process, so the gateway's configuration
is the one that names vmselect and ClickHouse; a half left unconfigured refuses by name.

**Metrics** go to vmselect at `/select/{accountID}/prometheus/api/v1/query[_range]` and the label
APIs, one `VMCluster` per retention tier (`MetricsEndpoint` carries a `{tier}` placeholder). A range
query may cover 400 days — the longest retention — at no more than 11 000 points per series,
Prometheus' own ceiling, and gets 240 points when it names no step; vmselect is told the 10-second
timeout and the handler cancels a second later; the response body is read to at most 64 MiB, and
series past 500 are counted and dropped with `truncated` set, because vmselect has no per-request
series cap on a range query. A malformed expression is a `400` carrying vmselect's own sentence;
anything else is a `500` whose detail is in the gateway's log. ⚠ MetricsQL cannot name an account:
`vm_account_id` is a filter only on `/select/multitenant/`, which nothing builds —
`MonitorQueryOverHttpTests.TheOtherTenantsWorkspaceOfTheSameNameReadsOnlyItsOwnAccount` sends one.

⚠ **The accountID is the GUID folded to 32 bits, so "each workspace reads its own series" holds only
while no two workspaces fold alike.** `MonitorWorkspaces.AccountId` derives the account rather than
allocating it, and the birthday bound puts a collision at even odds around 77 000 workspaces — inside
target scale. Before #41 nothing read under the account; now the explorer does, so a collision lets a
Reader of either workspace query both tenants' metrics, and the gateway, which checks the workspace
the caller named, cannot see that a second one shares its account. Nothing detects a collision. The
closure is `conformance.yaml § owed`, `accountid-is-folded-not-allocated` — `accountID:projectID`,
changed in the write path's vmauth suffix and in this read together.

**Logs** are searched in `{database}.otel_logs`, the collector's ClickHouse exporter's table
(`MonitorLogsTable`), and ⚠ **the search is a structured filter and not #54's KQL.** The translator
was the obvious reuse and was declined for three reasons: its subset refuses `ago`, `now` and `bin`,
and a log search is a window and a histogram; the window has to be a bound the API *requires*
(90 days at most, the longest log retention) rather than a `where` a translator would have to find
and prove present; and every statement it emits ANDs in the resource graph's per-row access filter,
which a log row does not have — a log row is visible to whoever may read the workspace, decided once
by the action. Every value is a ClickHouse `{name:Type}` parameter — ⚠ sent in ClickHouse's
*escaped* format, because the server reads a `param_` value that way: sent raw, the `\t` in
`C:\temp` was a tab and a lone backslash failed the search with a `500`; the database is the one identifier
spelled into the SQL and must match `ws_` plus 32 hex digits; each statement carries `readonly=2`,
`max_execution_time`, `max_rows_to_read` and `timeout_overflow_mode=throw`, and a breach is a `400`
naming the budget. Time is compared as Unix integers, never as zoned values, because a `DateTime64`
with no zone renders in the *server's* zone. Severity filters on OpenTelemetry's `SeverityNumber`
bands, not on the source's spelling of `SeverityText`. The answer is the newest `top` rows (at most
1 000) plus a histogram bucketed from `from` by severity, with what the store read; `estimate: true`
answers ClickHouse's `EXPLAIN ESTIMATE` instead and runs nothing — the query cost preview
[20](20-portal.md) said log search needs. A workspace with no table yet answers empty with a note.

⚠ **Two of the three responses are undeclared.** A series list and a row list are arrays of objects,
which the registry's schema refuses, so `queryMetrics` and `searchLogs` publish a request and no
response and the generated clients type the body `unknown`; the portal checks the shape at run time.

`MonitorQueryOverHttpTests` drives all three through the gateway's pipeline behind Kestrel, the real
write path over an Orleans test cluster, and a VictoriaMetrics *cluster* (three containers — the
single-node image has no `accountID`) and a ClickHouse in Testcontainers, seeded for two workspaces
of the same name in two tenants: each reads its own series and rows, another tenant's path is `404`,
a caller without `read` is `404` and one with only `read` is `200`, the limits refuse before the
store is asked, and a `')) OR 1=1 --`, a Windows path, a lone backslash and a tab in the search each
match the one row that contains them.

What this does not do, each `charts/managed/monitor-workspace/conformance.yaml § owed`: declare the
two responses (`query-responses-are-undeclared`); offer a query language over logs
(`logs-have-no-query-language`); verify the logs DDL against the exporter's source
(`log-search-reads-the-exporters-table-by-hand`); read a tier the workspace used to have, or go
through vmauth (`metrics-query-reads-one-tier`); count as reads at the rate limiter
(`query-actions-count-as-writes`); page a log window, save or pin a query, or explore traces
(`explorers-are-a-first-cut`); give two workspaces accounts that cannot collide
(`accountid-is-folded-not-allocated`, above); or answer anywhere but a laptop's log search — the
AppHost's gateway reads the region's ClickHouse and has no VictoriaMetrics, no chart deploys a
gateway, and a region's vmselect has no TLS to satisfy an `https` endpoint
(`explorers-are-wired-on-a-laptop-only`). The alert evaluator's query seam is still the refusing default
(`alert-rules-query-seam-is-refusing`), though the stores it would use now exist.

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
