{{/*
Name stem. `nameOverride` is chart plumbing (@internal in values.yaml) and is not part of the
resource body.
*/}}
{{- define "postgres.name" -}}
{{- default .Release.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{/*
The seven cybercloud.io/* labels — docs/plan/02 § ADR-013, "Every object carries ...".

⚠ `resource-type` is lower-cased with `/` replaced by `_`, matching KubeLabels.ResourceTypeValue in
src/CyberCloud.Kubernetes.Contracts. A `/` is not a legal Kubernetes label *value* character, so
rendering the resource type verbatim would produce an object the API server refuses — and the
refusal arrives at apply time, per object, not at lint time.
*/}}
{{- define "postgres.platformLabels" -}}
cybercloud.io/tenant-id: {{ .Values.platform.tenantId | quote }}
cybercloud.io/subscription-id: {{ .Values.platform.subscriptionId | quote }}
cybercloud.io/resource-group: {{ .Values.platform.resourceGroup | quote }}
cybercloud.io/resource-id: {{ .Values.platform.resourceId | quote }}
cybercloud.io/resource-type: {{ .Values.platform.resourceType | replace "/" "_" | lower | quote }}
cybercloud.io/api-version: {{ .Values.platform.apiVersion | quote }}
cybercloud.io/managed-by: {{ .Values.platform.managedBy | quote }}
{{- end -}}

{{/*
Resources, from the sizing preset unless an explicit quantity overrides it — docs/plan/12
§ Sizing vocabulary, "the preset is a default, not a cage".

Only the s1 family is tabulated here: values.yaml constrains `sizing.preset` to it, so a preset
from another family cannot reach this template. A preset that is not in the table renders no
`resources` block at all rather than a wrong one.
*/}}
{{- define "postgres.resources" -}}
{{- $presets := dict
  "s1.nano"    (dict "cpu" "100m" "memory" "512Mi")
  "s1.micro"   (dict "cpu" "250m" "memory" "1Gi")
  "s1.small"   (dict "cpu" "500m" "memory" "2Gi")
  "s1.medium"  (dict "cpu" "1"    "memory" "4Gi")
  "s1.large"   (dict "cpu" "2"    "memory" "8Gi")
  "s1.xlarge"  (dict "cpu" "4"    "memory" "16Gi")
  "s1.2xlarge" (dict "cpu" "8"    "memory" "32Gi")
  "s1.4xlarge" (dict "cpu" "16"   "memory" "64Gi") -}}
{{- $preset := get $presets .Values.sizing.preset | default dict -}}
{{- $cpu := default (get $preset "cpu") .Values.sizing.cpu -}}
{{- $memory := default (get $preset "memory") .Values.sizing.memory -}}
{{- if and $cpu $memory }}
requests:
  cpu: {{ $cpu | quote }}
  memory: {{ $memory | quote }}
limits:
  cpu: {{ $cpu | quote }}
  memory: {{ $memory | quote }}
{{- end }}
{{- end -}}
