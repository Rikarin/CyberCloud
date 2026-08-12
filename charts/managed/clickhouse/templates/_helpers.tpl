{{/*
Name stem. `nameOverride` is chart plumbing (@internal in values.yaml) and is not part of the
resource body.
*/}}
{{- define "clickhouse.name" -}}
{{- default .Release.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{/*
The Service the operator puts in front of the Keeper installation.

⚠ THE MOST LOAD-BEARING STRING IN THIS CHART. The ClickHouseInstallation names this in
spec.configuration.zookeeper.nodes, and NOTHING THIS CHART RENDERS CREATES IT — the operator does,
off the ClickHouseKeeperInstallation, with a `keeper-` prefix. Taken from upstream's own
docs/chk-examples/01-chi-simple-with-keeper.yaml, whose CHI names `host: keeper-simple-1` against a
CHK called `simple-1` with the comment "This is a service name of chk/simple-1".

A wrong prefix here produces a cluster that applies cleanly, reads back cleanly, converges, answers
SELECT 1 — and cannot create a replicated table. Nothing before a tenant's first DDL would notice.
*/}}
{{- define "clickhouse.keeperService" -}}
{{- printf "keeper-%s" (include "clickhouse.name" .) -}}
{{- end -}}

{{/*
The seven cybercloud.io/* labels — docs/plan/02 § ADR-013, "Every object carries ...".

⚠ `resource-type` is lower-cased with `/` replaced by `_`, matching KubeLabels.ResourceTypeValue in
src/CyberCloud.Kubernetes.Contracts. A `/` is not a legal Kubernetes label *value* character, so
rendering the resource type verbatim would produce an object the API server refuses — and the
refusal arrives at apply time, per object, not at lint time.
*/}}
{{- define "clickhouse.platformLabels" -}}
cybercloud.io/tenant-id: {{ .Values.platform.tenantId | quote }}
cybercloud.io/subscription-id: {{ .Values.platform.subscriptionId | quote }}
cybercloud.io/resource-group: {{ .Values.platform.resourceGroup | quote }}
cybercloud.io/resource-id: {{ .Values.platform.resourceId | quote }}
cybercloud.io/resource-type: {{ .Values.platform.resourceType | replace "/" "_" | lower | quote }}
cybercloud.io/api-version: {{ .Values.platform.apiVersion | quote }}
cybercloud.io/managed-by: {{ .Values.platform.managedBy | quote }}
{{- end -}}

{{/*
The two images.

⚠ ONE VERSION, TWO IMAGES, AND THAT IS A STATEMENT RATHER THAN A CONVENIENCE. ClickHouse Keeper and
ClickHouse server share a release train and a wire protocol; a cluster whose coordination is two
majors ahead of its servers is a combination nobody tests. The two `@internal` overrides exist for an
upgrade window and are not a tenant setting.
*/}}
{{- define "clickhouse.serverImage" -}}
{{- default (printf "clickhouse/clickhouse-server:%s" .Values.version) .Values.imageName -}}
{{- end -}}

{{- define "clickhouse.keeperImage" -}}
{{- default (printf "clickhouse/clickhouse-keeper:%s" .Values.version) .Values.keeperImageName -}}
{{- end -}}

{{/*
The ClickHouse server's sizing preset.

Only the m1 family is tabulated here: values.yaml constrains `sizing.preset` to it, so a preset from
another family cannot reach this template. A preset that is not in the table renders no
requests/limits pair at all rather than a wrong one.

⚠ A second copy of ClickHouseClusters.Presets in
src/Providers/CyberCloud.Providers.Analytics/CyberCloud.Providers.Analytics.Contracts. It exists
because CyberCloud.Kubernetes.Charts does not, so the reconciler builds the objects in C#.
ClickHouseSizingTests diffs the two by reading this file as text — ChartSurfaces filters templates/
out of the chart tree on purpose, so no emitter will ever read it.

⚠ It is also the SAME eight rows charts/managed/valkey carries, character for character, and that is
the point rather than duplication to remove: docs/plan/12 § Sizing vocabulary is one table, `m1.*` is
"1:8 · Memory-bound — caches, analytics", and two services in one family disagreeing would be the
defect.
*/}}
{{- define "clickhouse.presets" -}}
{{- $presets := dict
  "m1.nano"    (dict "cpu" "100m" "memory" "1Gi")
  "m1.micro"   (dict "cpu" "250m" "memory" "2Gi")
  "m1.small"   (dict "cpu" "500m" "memory" "4Gi")
  "m1.medium"  (dict "cpu" "1"    "memory" "8Gi")
  "m1.large"   (dict "cpu" "2"    "memory" "16Gi")
  "m1.xlarge"  (dict "cpu" "4"    "memory" "32Gi")
  "m1.2xlarge" (dict "cpu" "8"    "memory" "64Gi")
  "m1.4xlarge" (dict "cpu" "16"   "memory" "128Gi") -}}
{{- get $presets .Values.sizing.preset | default dict | toJson -}}
{{- end -}}

{{- define "clickhouse.cpu" -}}
{{- $preset := include "clickhouse.presets" . | fromJson -}}
{{- default (get $preset "cpu") .Values.sizing.cpu -}}
{{- end -}}

{{- define "clickhouse.memory" -}}
{{- $preset := include "clickhouse.presets" . | fromJson -}}
{{- default (get $preset "memory") .Values.sizing.memory -}}
{{- end -}}

{{/*
What every Keeper pod requests, and the volume it gets.

⚠ Constants, and they are still not free. A Keeper's working set is the coordination log rather than
the tenant's data, so none of this scales with anything a tenant sets and none is a knob worth
publishing. A three-node quorum is three pods and three volumes before a row is inserted, which is
why AnalyticsProvider's quota derivations are a PRODUCT (shards × replicas) PLUS a SUM (the Keeper
population) rather than either on its own.
*/}}
{{- define "clickhouse.keeperResources" -}}
requests:
  cpu: "250m"
  memory: "512Mi"
limits:
  cpu: "250m"
  memory: "512Mi"
{{- end -}}
