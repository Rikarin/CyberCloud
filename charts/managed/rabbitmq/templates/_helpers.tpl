{{/*
Name stem. `nameOverride` is chart plumbing (@internal in values.yaml) and is not part of the
resource body.
*/}}
{{- define "rabbitmq.name" -}}
{{- default .Release.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{/*
The seven cybercloud.io/* labels — docs/plan/02 § ADR-013, "Every object carries ...".

⚠ `resource-type` is lower-cased with `/` replaced by `_`, matching KubeLabels.ResourceTypeValue in
src/CyberCloud.Kubernetes.Contracts. A `/` is not a legal Kubernetes label *value* character, so
rendering the resource type verbatim would produce an object the API server refuses — and the
refusal arrives at apply time, per object, not at lint time.
*/}}
{{- define "rabbitmq.platformLabels" -}}
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

Only the c1 family is tabulated here: values.yaml constrains `sizing.preset` to it, so a preset from
another family cannot reach this template.

⚠ AND UNLIKE THE KAFKA AND NATS CHARTS, A PRESET THAT IS NOT IN THE TABLE IS NOT SAFE HERE. Those
two render no `resources` block and get a pod with no requests and no limits — visible, and
BestEffort. This CRD DEFAULTS spec.resources to
{limits: {cpu: 2000m, memory: 2Gi}, requests: {cpu: 1000m, memory: 2Gi}}, so the same omission
produces a BURSTABLE pod at quantities nobody chose while the preset name still says c1, which
docs/plan/12 defines as guaranteed. The quota meters refuse a body whose preset does not resolve
before a reconcile ever runs, which is what keeps the branch unreachable — see MessagingProvider.

⚠ Requests equal limits. docs/plan/12 § Sizing vocabulary calls c1 "1:2, guaranteed", and Guaranteed
is a Kubernetes QoS class you get by setting the two equal — not a word in a table.

⚠ This table is the other half of RabbitmqClusters.Presets. RabbitmqSizingTests reads THIS FILE,
embedded as a resource, and asserts the two agree value for value. The duplication exists because
CyberCloud.Kubernetes.Charts — the in-process Helm renderer docs/plan/03 § src describes — does not
exist, so the reconciler builds the object in C# instead of rendering this chart.
*/}}
{{- define "rabbitmq.resources" -}}
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

{{/*
The rabbitmq.conf fragment the operator files as 90-userDefinedConfiguration.conf.

⚠ THIS IS WHERE docs/plan/12's HEADLINE SENTENCE ACTUALLY LANDS, AND IT IS A STRING RATHER THAN A
FIELD. `default_queue_type` has no member on the RabbitmqCluster CRD; the only route to it is
spec.rabbitmq.additionalConfig, whose only constraint is maxLength: 100000. A misspelled key is not
rejected — the broker logs it and starts — so values.yaml's @enum on `queues.defaultType` is the
only thing standing between a body and an unreplicated cluster, and it constrains the VALUE rather
than the key.

⚠ It must be valid INI: the operator parses this block looking for default_user, default_pass and
auth_mechanisms, and a block it cannot parse fails the reconcile rather than being ignored. `key =
value` with spaces around the equals is what upstream's own rabbitmq.conf.example writes.

⚠ IT LAYERS ON TOP OF THE OPERATOR'S DEFAULTS RATHER THAN REPLACING THEM, AND NOT BY CONCATENATION.
The operator writes its own block to 10-operatorDefaults.conf and this one to
90-userDefinedConfiguration.conf; RabbitMQ merges conf.d by filename order, so 90 wins over 10. That
is also why the string round-trips through spec verbatim, which is what lets RabbitmqClusters.Matches
compare it. Checked against the operator, because prepending-into-the-tenant's-config is exactly what
ValkeyCaches found spotahome's validator doing.

⚠ default_user AND default_pass ARE DELIBERATELY ABSENT. Writing them would put a plaintext password
into the resource body and into grain state, which docs/plan/05 forbids, and would take the
credential out of the operator's hands for no gain — it generates a random 24-byte user and password
into <name>-default-user, and `guest` is never created. See SOURCE.

⚠ Alphabetical by key. Clause 1 of docs/plan/08 § The reconcile loop wants the same body to render
the same string on every pass, and a reader diffing two clusters wants the same key on the same line.
*/}}
{{- define "rabbitmq.additionalConfig" -}}
default_queue_type = {{ .Values.queues.defaultType }}
max_message_size = {{ .Values.limits.maxMessageSize }}
{{ end -}}
