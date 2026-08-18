{{/*
Name stem. `nameOverride` is chart plumbing (@internal in values.yaml) and is not part of the
resource body.
*/}}
{{- define "monitorWorkspace.name" -}}
{{- default .Release.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{/*
The object names, all three of them derived from one stem.

⚠ The Secret is named here even though this chart does not render it. MonitorWorkspaceReconciler
applies it, the VMUser below names it in passwordRef, and the row names it in `ingestKeySecret` — so
the stem has to be spelled once and reachable by both templates rather than inlined twice.
*/}}
{{- define "monitorWorkspace.rowName" -}}
{{- printf "monitor-%s" (include "monitorWorkspace.name" .) -}}
{{- end -}}

{{- define "monitorWorkspace.keySecretName" -}}
{{- printf "monitor-%s-ingest" (include "monitorWorkspace.name" .) -}}
{{- end -}}

{{/*
The vmstorage group that holds one retention tier.

⚠⚠ THE MOST LOAD-BEARING LINE IN THIS CHART, AND IT EXISTS BECAUSE OF A LIMIT OF THE OPEN-SOURCE
ENGINE RATHER THAN A PREFERENCE. VictoriaMetrics' `-retentionPeriod` is one global flag per vmstorage
node; the per-tenant form, `-retentionFilter`, is an ENTERPRISE feature. Upstream's own answer for
the open-source case is one storage group per retention period
(docs.victoriametrics.com/guides/guide-vmcluster-multiple-retention-setup/), so a workspace's metrics
retention is a ROUTING decision and not a number sent anywhere.

Consequences a reader should not have to rediscover: there are exactly three VMClusters per region,
one per tier, installed by charts/bundle/ rather than by this chart; changing a workspace's tier
moves where its NEW samples go and leaves the ones already written in the group that holds them; and
the day counts in MonitorWorkspaces.RetentionDays have to match how each group is actually
provisioned, which nothing in this repository checks. See SOURCE and conformance.yaml § owed.
*/}}
{{- define "monitorWorkspace.metricsCluster" -}}
{{- printf "telemetry-%s" .Values.retention.metrics -}}
{{- end -}}

{{/*
How many days one signal is kept at the tier this workspace asked for.

⚠ A SECOND COPY of MonitorWorkspaces.RetentionDays, and it exists because
CyberCloud.Kubernetes.Charts does not — the reconciler builds the objects in C#, so the two
renderings are independent and MonitorRetentionTests diffs them by reading this file as text.
ChartSurfaces filters templates/ out of the chart tree on purpose, so no emitter will ever read it.

⚠ The nine numbers are docs/plan/16 § CyberCloud.Monitor/workspaces', verbatim: "metrics 15/90/400
days, logs 7/30/90, traces 3/14/30".
*/}}
{{- define "monitorWorkspace.retentionDays" -}}
{{- $days := dict
  "metrics" (dict "short" 15 "standard" 90 "extended" 400)
  "logs"    (dict "short" 7  "standard" 30 "extended" 90)
  "traces"  (dict "short" 3  "standard" 14 "extended" 30) -}}
{{- $days | toJson -}}
{{- end -}}

{{- define "monitorWorkspace.metricsDays" -}}
{{- $days := include "monitorWorkspace.retentionDays" . | fromJson -}}
{{- get (get $days "metrics") .Values.retention.metrics -}}
{{- end -}}

{{- define "monitorWorkspace.logsDays" -}}
{{- $days := include "monitorWorkspace.retentionDays" . | fromJson -}}
{{- get (get $days "logs") .Values.retention.logs -}}
{{- end -}}

{{- define "monitorWorkspace.tracesDays" -}}
{{- $days := include "monitorWorkspace.retentionDays" . | fromJson -}}
{{- get (get $days "traces") .Values.retention.traces -}}
{{- end -}}

{{/*
The seven cybercloud.io/* labels — docs/plan/02 § ADR-013, "Every object carries ...".

⚠ `resource-type` is lower-cased with `/` replaced by `_`, matching KubeLabels.ResourceTypeValue in
src/CyberCloud.Kubernetes.Contracts. A `/` is not a legal Kubernetes label *value* character, so
rendering the resource type verbatim would produce an object the API server refuses — and the
refusal arrives at apply time, per object, not at lint time.
*/}}
{{- define "monitorWorkspace.platformLabels" -}}
cybercloud.io/tenant-id: {{ .Values.platform.tenantId | quote }}
cybercloud.io/subscription-id: {{ .Values.platform.subscriptionId | quote }}
cybercloud.io/resource-group: {{ .Values.platform.resourceGroup | quote }}
cybercloud.io/resource-id: {{ .Values.platform.resourceId | quote }}
cybercloud.io/resource-type: {{ .Values.platform.resourceType | replace "/" "_" | lower | quote }}
cybercloud.io/api-version: {{ .Values.platform.apiVersion | quote }}
cybercloud.io/managed-by: {{ .Values.platform.managedBy | quote }}
{{- end -}}
