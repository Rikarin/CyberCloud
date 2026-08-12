{{/*
Name stem. `nameOverride` is chart plumbing (@internal in values.yaml) and is not part of the
resource body.
*/}}
{{- define "nats.name" -}}
{{- default .Release.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{/*
The headless Service's name. It is the StatefulSet's `serviceName`, the Service's own name, and the
middle segment of every route in nats.conf. Defining it once is what keeps those three the same
string — two spellings is a cluster whose servers never find each other, which NATS reports as three
healthy standalone servers rather than as an error.
*/}}
{{- define "nats.headless" -}}
{{- include "nats.name" . }}-headless
{{- end -}}

{{/*
The seven cybercloud.io/* labels — docs/plan/02 § ADR-013, "Every object carries ...".

⚠ `resource-type` is lower-cased with `/` replaced by `_`, matching KubeLabels.ResourceTypeValue in
src/CyberCloud.Kubernetes.Contracts. A `/` is not a legal Kubernetes label *value* character, so
rendering the resource type verbatim would produce an object the API server refuses — and the
refusal arrives at apply time, per object, not at lint time.
*/}}
{{- define "nats.platformLabels" -}}
cybercloud.io/tenant-id: {{ .Values.platform.tenantId | quote }}
cybercloud.io/subscription-id: {{ .Values.platform.subscriptionId | quote }}
cybercloud.io/resource-group: {{ .Values.platform.resourceGroup | quote }}
cybercloud.io/resource-id: {{ .Values.platform.resourceId | quote }}
cybercloud.io/resource-type: {{ .Values.platform.resourceType | replace "/" "_" | lower | quote }}
cybercloud.io/api-version: {{ .Values.platform.apiVersion | quote }}
cybercloud.io/managed-by: {{ .Values.platform.managedBy | quote }}
{{- end -}}

{{/*
The pod selector — NOT the seven above.

⚠ THE DISTINCTION IS LOAD-BEARING AND IT IS THE ONE THING IN THIS CHART A READER IS MOST LIKELY TO
GET WRONG. The seven identify a RESOURCE and every one of them can change: an api-version moves, a
resource group is renamed. A StatefulSet's `spec.selector` is IMMUTABLE after create, and the two
Services select on the same set. Selecting on a label that can change is a resource that can never
be updated again, which the API server reports as an invalid update rather than as drift.

⚠ This block is the other half of NatsClusters.PodLabels. NatsSizingTests reads THIS FILE, embedded
as a resource, and asserts the two agree key for key.
*/}}
{{- define "nats.selectorLabels" -}}
app.kubernetes.io/name: "nats"
app.kubernetes.io/instance: {{ include "nats.name" . | quote }}
app.kubernetes.io/component: "server"
app.kubernetes.io/managed-by: "cybercloud"
{{- end -}}

{{/*
Resources, from the sizing preset unless an explicit quantity overrides it — docs/plan/12
§ Sizing vocabulary, "the preset is a default, not a cage".

Only the c1 family is tabulated here: values.yaml constrains `sizing.preset` to it, so a preset from
another family cannot reach this template. A preset that is not in the table renders no `resources`
block at all rather than a wrong one.

⚠ Requests equal limits. docs/plan/12 § Sizing vocabulary calls c1 "1:2, guaranteed", and Guaranteed
is a Kubernetes QoS class you get by setting the two equal — not a word in a table.

⚠ This table is the other half of NatsClusters.Presets, and it is deliberately a SECOND table rather
than a reference to charts/managed/kafka's identical one. A chart cannot include another chart's
helpers, and the two rows have different reasons to move. NatsSizingTests reads this file, embedded
as a resource, and asserts it against NatsClusters.Presets value for value. The duplication against
C# exists because CyberCloud.Kubernetes.Charts — the in-process Helm renderer docs/plan/03 § src
describes — does not exist, so the reconciler builds the objects in C# instead of rendering this
chart.
*/}}
{{- define "nats.resources" -}}
{{- $presets := dict
  "c1.nano"    (dict "cpu" "250m" "memory" "512Mi")
  "c1.micro"   (dict "cpu" "500m" "memory" "1Gi")
  "c1.small"   (dict "cpu" "1"    "memory" "2Gi")
  "c1.medium"  (dict "cpu" "2"    "memory" "4Gi")
  "c1.large"   (dict "cpu" "4"    "memory" "8Gi")
  "c1.xlarge"  (dict "cpu" "8"    "memory" "16Gi")
  "c1.2xlarge" (dict "cpu" "16"   "memory" "32Gi")
  "c1.4xlarge" (dict "cpu" "32"   "memory" "64Gi") -}}
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
