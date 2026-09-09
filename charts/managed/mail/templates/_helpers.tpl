{{/*
Name stem. `nameOverride` is chart plumbing (@internal in values.yaml) and is not part of the
resource body.
*/}}
{{- define "mail.name" -}}
{{- default .Release.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{/*
The Service's name. It is also the StatefulSet's `serviceName`, which is what gives the single pod a
stable DNS record for the shared inbound pool to deliver to over LMTP. Defining it once is what keeps
those two the same string.
*/}}
{{- define "mail.service" -}}
{{- include "mail.name" . }}-mail
{{- end -}}

{{/*
The seven cybercloud.io/* labels — docs/plan/02 § ADR-013, "Every object carries ...".

⚠ `resource-type` is lower-cased with `/` replaced by `_`, matching KubeLabels.ResourceTypeValue in
src/CyberCloud.Kubernetes.Contracts. A `/` is not a legal Kubernetes label *value* character, so
rendering the resource type verbatim would produce an object the API server refuses — and the
refusal arrives at apply time, per object, not at lint time.
*/}}
{{- define "mail.platformLabels" -}}
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

⚠ THE DISTINCTION IS LOAD-BEARING. The seven identify a RESOURCE and every one of them can change: an
api-version moves, a resource group is renamed. A StatefulSet's `spec.selector` is IMMUTABLE after
create, the Service routes on the same set, and the PodMonitor scrapes by it. Selecting on a label
that can change is a resource that can never be updated again, which the API server reports as an
invalid update rather than as drift.

⚠ ON THIS CHART THERE IS A FOURTH CONSUMER AND IT IS THE ONE WITH TEETH. MailDomains.RetainedClaims
hands these labels to VolumeReclaimer as the RetainedVolume's `OwnedBy` evidence, and the reclaimer
refuses to delete a claim whose stored object does not carry them exactly. So a drift between this
block and MailDomains.SelectorLabels does not only break routing — it makes every purge of a mail
domain refuse, which is the safe direction and still a bug.

⚠ `component: backend` and not `dovecot`. The three container names describe processes inside one
pod; this describes the pod. MailSizingTests reads THIS FILE, embedded as a resource, and asserts the
two agree key for key.
*/}}
{{- define "mail.selectorLabels" -}}
app.kubernetes.io/name: "mail"
app.kubernetes.io/instance: {{ include "mail.name" . | quote }}
app.kubernetes.io/component: "backend"
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

⚠ THIS TABLE STOPS AT c1.xlarge WHERE THE OTHER CHARTS' GO TO c1.4xlarge, AND THAT IS NOT AN
OVERSIGHT. A mail back end is ONE pod holding ONE volume, so the largest useful size is bounded by
what a single node can give it; the two larger presets would offer a shape this type cannot use. It
is the other half of MailDomains.Presets and MailSizingTests asserts the two value for value.
*/}}
{{- define "mail.resources" -}}
{{- $presets := dict
  "c1.nano"   (dict "cpu" "250m" "memory" "512Mi")
  "c1.micro"  (dict "cpu" "500m" "memory" "1Gi")
  "c1.small"  (dict "cpu" "1"    "memory" "2Gi")
  "c1.medium" (dict "cpu" "2"    "memory" "4Gi")
  "c1.large"  (dict "cpu" "4"    "memory" "8Gi")
  "c1.xlarge" (dict "cpu" "8"    "memory" "16Gi") -}}
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
