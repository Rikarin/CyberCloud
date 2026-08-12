{{/*
Name stem. `nameOverride` is chart plumbing (@internal in values.yaml) and is not part of the
resource body.
*/}}
{{- define "ferretdb.name" -}}
{{- default .Release.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{/*
The CloudNativePG Cluster's name.

⚠ SUFFIXED, where charts/managed/postgres names its Cluster after the release. An account owns two
workloads and the one a driver connects to is the FerretDB Deployment; giving the PostgreSQL cluster
the bare name would put the tenant-facing endpoint on the suffixed object. The suffix also keeps
CloudNativePG's own generated names — {name}-pg-rw, {name}-pg-app, {name}-pg-superuser — visibly the
operator's rather than ours.
*/}}
{{- define "ferretdb.clusterName" -}}
{{- printf "%s-pg" (include "ferretdb.name" .) -}}
{{- end -}}

{{/*
The seven cybercloud.io/* labels — docs/plan/02 § ADR-013, "Every object carries ...".

⚠ `resource-type` is lower-cased with `/` replaced by `_`, matching KubeLabels.ResourceTypeValue in
src/CyberCloud.Kubernetes.Contracts. A `/` is not a legal Kubernetes label *value* character, so
rendering the resource type verbatim would produce an object the API server refuses — and the
refusal arrives at apply time, per object, not at lint time.
*/}}
{{- define "ferretdb.platformLabels" -}}
cybercloud.io/tenant-id: {{ .Values.platform.tenantId | quote }}
cybercloud.io/subscription-id: {{ .Values.platform.subscriptionId | quote }}
cybercloud.io/resource-group: {{ .Values.platform.resourceGroup | quote }}
cybercloud.io/resource-id: {{ .Values.platform.resourceId | quote }}
cybercloud.io/resource-type: {{ .Values.platform.resourceType | replace "/" "_" | lower | quote }}
cybercloud.io/api-version: {{ .Values.platform.apiVersion | quote }}
cybercloud.io/managed-by: {{ .Values.platform.managedBy | quote }}
{{- end -}}

{{/*
The labels the Deployment's selector, its pod template and the PodMonitor all match on.

⚠ NONE OF THESE IS ONE OF ADR-013's SEVEN, AND NONE MAY EVER BECOME ONE. A Deployment's
spec.selector is immutable after create and its pod labels have to match it, so every key here has
to be stable for the life of the resource — and cybercloud.io/api-version is by construction not.
The consequence is charts/managed/nats' `pod-labels` finding, second sighting: a FerretDB pod cannot
be attributed to a tenant by label, and closing it is IKubeCommandBuilder learning to inject
pod-template labels that are NOT part of the selector.
*/}}
{{- define "ferretdb.selectorLabels" -}}
app.kubernetes.io/name: ferretdb
app.kubernetes.io/instance: {{ include "ferretdb.name" . | quote }}
app.kubernetes.io/component: gateway
app.kubernetes.io/managed-by: cybercloud
{{- end -}}

{{/*
The two image tags one `version` pairs with.

⚠ ONE PROPERTY, TWO IMAGES, AND THE PAIRING IS THE WHOLE REASON THIS TABLE EXISTS. FerretDB and the
DocumentDB PostgreSQL extension are released together and upstream tags the PostgreSQL image
`{pgMajor}-{documentdbVersion}-ferretdb-{ferretdbVersion}`. A pair that was never released together
is a proxy talking to an extension whose call signatures it does not know, so the API offers one
version and this table turns it back into two.

⚠ A second copy of DocumentDbAccounts.Versions in
src/Providers/CyberCloud.Providers.DocumentDB/CyberCloud.Providers.DocumentDB.Contracts. It exists
because CyberCloud.Kubernetes.Charts does not, so the reconciler builds the objects in C#.
DocumentDbSizingTests diffs the two by reading this file as text — ChartSurfaces filters templates/
out of the chart tree on purpose, so no emitter will ever read it.

⚠ An unrecognised member falls back to the DEFAULT version's pair rather than to an empty tag. An
empty tag renders `ghcr.io/ferretdb/ferretdb:` and fails per pod, after the caller was told 202.
*/}}
{{- define "ferretdb.versions" -}}
{{- $versions := dict
  "2.5" (dict "gateway" "2.5.0" "postgres" "17-0.106.0-ferretdb-2.5.0")
  "2.7" (dict "gateway" "2.7.0" "postgres" "17-0.107.0-ferretdb-2.7.0") -}}
{{- get $versions .Values.version | default (get $versions "2.7") | toJson -}}
{{- end -}}

{{- define "ferretdb.gatewayImage" -}}
{{- $pair := include "ferretdb.versions" . | fromJson -}}
{{- default (printf "ghcr.io/ferretdb/ferretdb:%s" (get $pair "gateway")) .Values.gatewayImage -}}
{{- end -}}

{{- define "ferretdb.postgresImage" -}}
{{- $pair := include "ferretdb.versions" . | fromJson -}}
{{- default (printf "ghcr.io/ferretdb/postgres-documentdb:%s" (get $pair "postgres")) .Values.postgresImage -}}
{{- end -}}

{{/*
The PostgreSQL instance's sizing preset.

Only the s1 family is tabulated here: values.yaml constrains `sizing.preset` to it, so a preset from
another family cannot reach this template. A preset that is not in the table renders no
requests/limits pair at all rather than a wrong one.

⚠ A second copy of DocumentDbAccounts.Presets, for the reason `ferretdb.versions` gives.

⚠ AND THIS IS THE THIRD `s1` TABLE IN THE TREE. charts/managed/postgres spells s1.small as
(500m, 2Gi) and charts/managed/seaweedfs spells it (1, 4Gi) — the same ratio one rung apart — while
docs/plan/12 § Sizing vocabulary opens "One table, defined once, used by every service and every VM".
This one holds the family's declared 1:4 on every rung, which charts/managed/postgres' s1.nano
(100m to 512Mi, which is 5:1) does not. See conformance.yaml § owed, `sizing-table-is-not-shared`.
*/}}
{{- define "ferretdb.presets" -}}
{{- $presets := dict
  "s1.nano"    (dict "cpu" "250m" "memory" "1Gi")
  "s1.micro"   (dict "cpu" "500m" "memory" "2Gi")
  "s1.small"   (dict "cpu" "1"    "memory" "4Gi")
  "s1.medium"  (dict "cpu" "2"    "memory" "8Gi")
  "s1.large"   (dict "cpu" "4"    "memory" "16Gi")
  "s1.xlarge"  (dict "cpu" "8"    "memory" "32Gi")
  "s1.2xlarge" (dict "cpu" "16"   "memory" "64Gi")
  "s1.4xlarge" (dict "cpu" "32"   "memory" "128Gi") -}}
{{- get $presets .Values.sizing.preset | default dict | toJson -}}
{{- end -}}

{{- define "ferretdb.cpu" -}}
{{- $preset := include "ferretdb.presets" . | fromJson -}}
{{- default (get $preset "cpu") .Values.sizing.cpu -}}
{{- end -}}

{{- define "ferretdb.memory" -}}
{{- $preset := include "ferretdb.presets" . | fromJson -}}
{{- default (get $preset "memory") .Values.sizing.memory -}}
{{- end -}}

{{/*
What one FerretDB pod requests.

⚠ A constant, and it is still not free. FerretDB holds no data and scales with connection count
rather than with the tenant's dataset, which makes it a bad candidate for a sizing property and a
good one for a constant — but an account with two gateway pods runs two containers before a document
is written, which is why DocumentDbProvider's quota derivations are a sum over two populations
rather than instances × one figure.
*/}}
{{- define "ferretdb.gatewayResources" -}}
requests:
  cpu: "250m"
  memory: "512Mi"
limits:
  cpu: "250m"
  memory: "512Mi"
{{- end -}}
