{{/*
Name stem. `nameOverride` is chart plumbing (@internal in values.yaml) and is not part of the
resource body.
*/}}
{{- define "harbor.name" -}}
{{- default .Release.Name .Values.nameOverride | trunc 55 | trimSuffix "-" -}}
{{- end -}}

{{/*
The Secret every component reads its credentials out of.

⚠ THIS CHART NAMES IT AND DOES NOT CREATE IT — docs/plan/12 § The pattern, once, piece 5. The
platform mints six credentials into the tenant's vault and writes the Secret from what the vault
returned; a values key holding them would be grain state, and it would be one edit away from
goharbor/harbor-helm's own `harborAdminPassword: "Harbor12345"`.
*/}}
{{- define "harbor.credentialsSecret" -}}
{{- default (printf "%s-credentials" (include "harbor.name" .)) .Values.credentialsSecret -}}
{{- end -}}

{{/*
The seven cybercloud.io/* labels — docs/plan/02 § ADR-013, "Every object carries ...".

⚠ `resource-type` is lower-cased with `/` replaced by `_`, matching KubeLabels.ResourceTypeValue in
src/CyberCloud.Kubernetes.Contracts. A `/` is not a legal Kubernetes label *value* character, so
rendering the resource type verbatim would produce an object the API server refuses — and the refusal
arrives at apply time, per object, not at lint time.
*/}}
{{- define "harbor.platformLabels" -}}
cybercloud.io/tenant-id: {{ .Values.platform.tenantId | quote }}
cybercloud.io/subscription-id: {{ .Values.platform.subscriptionId | quote }}
cybercloud.io/resource-group: {{ .Values.platform.resourceGroup | quote }}
cybercloud.io/resource-id: {{ .Values.platform.resourceId | quote }}
cybercloud.io/resource-type: {{ .Values.platform.resourceType | replace "/" "_" | lower | quote }}
cybercloud.io/api-version: {{ .Values.platform.apiVersion | quote }}
cybercloud.io/managed-by: {{ .Values.platform.managedBy | quote }}
{{- end -}}

{{/*
The four app.kubernetes.io/* labels one component's pods carry.

⚠ THESE ARE NOT THE SEVEN ABOVE, and the difference is what a provider can get wrong. The seven are
injected onto each object's own metadata by KubeCommand and cannot be overridden. These four sit
inside spec.selector and spec.template.metadata.labels, which no builder reaches — and a workload
whose selector disagrees with the template it selects owns no pods. A second copy of
ContainerRegistries.PodLabels in src/Providers/CyberCloud.Providers.ContainerRegistry.Contracts;
ContainerRegistryChartTests diffs the two by reading this file as text.

⚠ A workload's spec.selector is IMMUTABLE after create, so changing this is six resources that can
never be updated again.

Call as: include "harbor.componentLabels" (dict "root" . "component" "core")
*/}}
{{- define "harbor.componentLabels" -}}
app.kubernetes.io/name: harbor
app.kubernetes.io/instance: {{ include "harbor.name" .root }}
app.kubernetes.io/component: {{ .component }}
app.kubernetes.io/managed-by: cybercloud
{{- end -}}

{{/*
The patch each offered minor is pinned to.

⚠ THE API TAKES A MINOR AND A CONTAINER IMAGE TAKES A FULL TAG. Harbor publishes v2.15.2 and not
v2.15, so rendering the bare minor is one image pull back-off per pod, after the caller was told 202.
A second copy of ContainerRegistries.PinnedPatch; ContainerRegistryChartTests diffs the two.

⚠ An unrecognised minor falls back to the default's tag rather than to the raw value: a typo must not
become an image reference nothing publishes.
*/}}
{{- define "harbor.tag" -}}
{{- $pinned := dict
  "2.14" "v2.14.4"
  "2.15" "v2.15.2" -}}
{{- get $pinned .Values.version | default "v2.15.2" -}}
{{- end -}}

{{/*
One component's image reference.

Call as: include "harbor.image" (dict "root" . "repository" "harbor-core")
*/}}
{{- define "harbor.image" -}}
{{- printf "%s/%s:%s" .root.Values.imageRegistry .repository (include "harbor.tag" .root) -}}
{{- end -}}

{{/*
The registry pod's sizing preset.

Only the s1 family is tabulated here: values.yaml constrains `sizing.preset` to it, so a preset from
another family cannot reach this template. A preset that is not in the table renders no
requests/limits pair at all rather than a wrong one.

⚠ A second copy of ContainerRegistries.Presets, for the reason harbor.tag gives.
*/}}
{{- define "harbor.presets" -}}
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

{{- define "harbor.cpu" -}}
{{- $preset := include "harbor.presets" . | fromJson -}}
{{- default (get $preset "cpu") .Values.sizing.cpu -}}
{{- end -}}

{{- define "harbor.memory" -}}
{{- $preset := include "harbor.presets" . | fromJson -}}
{{- default (get $preset "memory") .Values.sizing.memory -}}
{{- end -}}

{{/*
What every pod that is NOT the registry requests.

⚠ A constant, and it is still not free. A registry with two replicas runs eight pods — core, the
portal and the job service twice each, plus the database, Redis and the registry — so a quota meter
that counted only the registry would under-reserve by seven of them on the default body. See
ContainerRegistryProvider's derivations.
*/}}
{{- define "harbor.controlPlaneResources" -}}
requests:
  cpu: "250m"
  memory: "512Mi"
limits:
  cpu: "250m"
  memory: "512Mi"
{{- end -}}
