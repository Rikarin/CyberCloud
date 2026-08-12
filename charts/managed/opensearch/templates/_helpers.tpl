{{/*
Name stem. `nameOverride` is chart plumbing (@internal in values.yaml) and is not part of the
resource body.
*/}}
{{- define "opensearch.name" -}}
{{- default .Release.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{/*
The seven cybercloud.io/* labels — docs/plan/02 § ADR-013, "Every object carries ...".

⚠ `resource-type` is lower-cased with `/` replaced by `_`, matching KubeLabels.ResourceTypeValue in
src/CyberCloud.Kubernetes.Contracts. A `/` is not a legal Kubernetes label *value* character, so
rendering the resource type verbatim would produce an object the API server refuses — and the
refusal arrives at apply time, per object, not at lint time.
*/}}
{{- define "opensearch.platformLabels" -}}
cybercloud.io/tenant-id: {{ .Values.platform.tenantId | quote }}
cybercloud.io/subscription-id: {{ .Values.platform.subscriptionId | quote }}
cybercloud.io/resource-group: {{ .Values.platform.resourceGroup | quote }}
cybercloud.io/resource-id: {{ .Values.platform.resourceId | quote }}
cybercloud.io/resource-type: {{ .Values.platform.resourceType | replace "/" "_" | lower | quote }}
cybercloud.io/api-version: {{ .Values.platform.apiVersion | quote }}
cybercloud.io/managed-by: {{ .Values.platform.managedBy | quote }}
{{- end -}}

{{/*
The node-pool sizing preset.

Only the m1 family is tabulated here: values.yaml constrains `sizing.preset` to it, so a preset from
another family cannot reach this template. A preset that is not in the table renders no
requests/limits pair at all rather than a wrong one.

⚠ m1 is 1 vCPU to 8 GiB — docs/plan/12 § Sizing vocabulary, "m1.* · 1:8 · Memory-bound — caches,
analytics" — and these values are deliberately the SAME as charts/managed/valkey's for every key the
two tables share, because two m1 tables that disagreed would make the family name mean two things.

⚠ The two smallest rungs of that table, m1.nano and m1.micro, are NOT offered here. OpenSearch sets
its JVM heap from the container limit and a node under 4 GiB fails a bootstrap check after passing
its readiness probe. A cache at 1 GiB is a small cache; a search node at 1 GiB is an outage with a
green probe.

⚠ A second copy of OpenSearchServices.Presets in
src/Providers/CyberCloud.Providers.Search/CyberCloud.Providers.Search.Contracts. It exists because
CyberCloud.Kubernetes.Charts does not, so the reconciler builds the object in C#.
OpenSearchSizingTests diffs the two by reading this file as text — ChartSurfaces filters templates/
out of the chart tree on purpose, so no emitter will ever read it.
*/}}
{{- define "opensearch.presets" -}}
{{- $presets := dict
  "m1.small"   (dict "cpu" "500m" "memory" "4Gi")
  "m1.medium"  (dict "cpu" "1"    "memory" "8Gi")
  "m1.large"   (dict "cpu" "2"    "memory" "16Gi")
  "m1.xlarge"  (dict "cpu" "4"    "memory" "32Gi")
  "m1.2xlarge" (dict "cpu" "8"    "memory" "64Gi")
  "m1.4xlarge" (dict "cpu" "16"   "memory" "128Gi") -}}
{{- get $presets .Values.sizing.preset | default dict | toJson -}}
{{- end -}}

{{- define "opensearch.cpu" -}}
{{- $preset := include "opensearch.presets" . | fromJson -}}
{{- default (get $preset "cpu") .Values.sizing.cpu -}}
{{- end -}}

{{- define "opensearch.memory" -}}
{{- $preset := include "opensearch.presets" . | fromJson -}}
{{- default (get $preset "memory") .Values.sizing.memory -}}
{{- end -}}

{{/*
What a dedicated cluster-manager node requests, and the volume every node that is not a data node
gets.

⚠ Constants, and they are still not free. A cluster-manager node holds the cluster state in memory
and does not scale with the tenant's index size; it is not a knob worth publishing. What it is is
three JVMs before a document is indexed, which is why SearchProvider's quota derivations are a sum
over two populations rather than replicas × one figure.

⚠ 500m/2Gi rather than the 250m/512Mi charts/managed/seaweedfs uses for the same job. A SeaweedFS
master is a Go binary; this is a JVM, and 512 MiB is below what OpenSearch's own startup heap check
passes. A control-plane share copied from that chart would CrashLoopBackOff before joining, which
reads as a cluster that will not form rather than as a sizing mistake.
*/}}
{{- define "opensearch.controlPlaneResources" -}}
requests:
  cpu: "500m"
  memory: "2Gi"
limits:
  cpu: "500m"
  memory: "2Gi"
{{- end -}}

{{- define "opensearch.controlPlaneVolume" -}}10Gi{{- end -}}
