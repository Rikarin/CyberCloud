{{/*
Name stem. `nameOverride` is chart plumbing (@internal in values.yaml) and is not part of the
resource body.
*/}}
{{- define "valkey.name" -}}
{{- default .Release.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{/*
The seven cybercloud.io/* labels — docs/plan/02 § ADR-013, "Every object carries ...".

⚠ `resource-type` is lower-cased with `/` replaced by `_`, matching KubeLabels.ResourceTypeValue in
src/CyberCloud.Kubernetes.Contracts. A `/` is not a legal Kubernetes label *value* character, so
rendering the resource type verbatim would produce an object the API server refuses — and the
refusal arrives at apply time, per object, not at lint time.
*/}}
{{- define "valkey.platformLabels" -}}
cybercloud.io/tenant-id: {{ .Values.platform.tenantId | quote }}
cybercloud.io/subscription-id: {{ .Values.platform.subscriptionId | quote }}
cybercloud.io/resource-group: {{ .Values.platform.resourceGroup | quote }}
cybercloud.io/resource-id: {{ .Values.platform.resourceId | quote }}
cybercloud.io/resource-type: {{ .Values.platform.resourceType | replace "/" "_" | lower | quote }}
cybercloud.io/api-version: {{ .Values.platform.apiVersion | quote }}
cybercloud.io/managed-by: {{ .Values.platform.managedBy | quote }}
{{- end -}}

{{/*
The image.

⚠ NOT the operator's default. spotahome's `defaultImage` is `redis:6.2.6-alpine`, which is the
licence ADR-011 rejected, so an empty `spec.redis.image` would ship Redis from a default without
anybody choosing it. `imageName` is the @internal escape hatch; the ordinary path is a Valkey tag
derived from `version`.
*/}}
{{- define "valkey.image" -}}
{{- default (printf "valkey/valkey:%s-alpine" .Values.version) .Values.imageName -}}
{{- end -}}

{{/*
The memory quantity in force: the explicit one, or the preset's.

Only the m1 family is tabulated here: values.yaml constrains `sizing.preset` to it, so a preset from
another family cannot reach this template. A preset that is not in the table renders no `resources`
block at all rather than a wrong one — and no `maxmemory` either, because a ceiling with no limit
behind it is a number nobody chose.

⚠ This table is a second copy of ValkeyCaches.Presets in
src/Providers/CyberCloud.Providers.Cache/CyberCloud.Providers.Cache.Contracts. It exists because
CyberCloud.Kubernetes.Charts does not, so the reconciler builds the object in C#. The two are diffed
by ChartRegistryPairTests, which reads this file as text — ChartSurfaces filters templates/ out of
the chart tree on purpose, so no emitter will ever read it.
*/}}
{{- define "valkey.presets" -}}
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

{{- define "valkey.cpu" -}}
{{- $preset := include "valkey.presets" . | fromJson -}}
{{- default (get $preset "cpu") .Values.sizing.cpu -}}
{{- end -}}

{{- define "valkey.memory" -}}
{{- $preset := include "valkey.presets" . | fromJson -}}
{{- default (get $preset "memory") .Values.sizing.memory -}}
{{- end -}}

{{/*
Resources, from the sizing preset unless an explicit quantity overrides it — docs/plan/12
§ Sizing vocabulary, "the preset is a default, not a cage".
*/}}
{{- define "valkey.resources" -}}
{{- $cpu := include "valkey.cpu" . -}}
{{- $memory := include "valkey.memory" . -}}
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
A Kubernetes memory quantity in bytes, or nothing when it is not one.

⚠ `floor` and not `%.0f` alone. The C# side truncates (`(long)(bytes * fraction)`), and `%.0f`
rounds to nearest — so on any quantity whose three-quarters is not a whole number the chart and the
reconciler would produce `maxmemory` values one byte apart, which is a diff nobody could explain.

⚠ `m` is milli. `500m` is half a byte, not 500 mebibytes, and it is converted rather than refused
because the pattern in values.yaml permits it — a caller who writes `512m` meaning mebibytes gets a
ceiling of zero and no ceiling at all, which is visible, rather than one a thousand times too large.
*/}}
{{- define "valkey.bytes" -}}
{{- $digits := regexFind "^[0-9]+(\\.[0-9]+)?" . -}}
{{- $suffix := regexFind "[A-Za-z]*$" . -}}
{{- if $digits -}}
{{- $scale := 1.0 -}}
{{- if eq $suffix "Ki" -}}{{- $scale = 1024.0 -}}
{{- else if eq $suffix "Mi" -}}{{- $scale = 1048576.0 -}}
{{- else if eq $suffix "Gi" -}}{{- $scale = 1073741824.0 -}}
{{- else if eq $suffix "Ti" -}}{{- $scale = 1099511627776.0 -}}
{{- else if eq $suffix "Pi" -}}{{- $scale = 1125899906842624.0 -}}
{{- else if eq $suffix "Ei" -}}{{- $scale = 1152921504606846976.0 -}}
{{- else if eq $suffix "k" -}}{{- $scale = 1000.0 -}}
{{- else if eq $suffix "M" -}}{{- $scale = 1000000.0 -}}
{{- else if eq $suffix "G" -}}{{- $scale = 1000000000.0 -}}
{{- else if eq $suffix "T" -}}{{- $scale = 1000000000000.0 -}}
{{- else if eq $suffix "P" -}}{{- $scale = 1000000000000000.0 -}}
{{- else if eq $suffix "E" -}}{{- $scale = 1000000000000000000.0 -}}
{{- else if eq $suffix "m" -}}{{- $scale = 0.001 -}}
{{- end -}}
{{- printf "%.0f" (floor (mulf (float64 $digits) $scale)) -}}
{{- end -}}
{{- end -}}

{{/*
The `redis.conf` lines the values imply — spec.redis.customConfig is a []string the operator
concatenates into a config file.

⚠ `maxmemory` is here because `maxmemory-policy` does NOTHING without it. Valkey applies an eviction
policy only when a ceiling is set; without one the process grows until the kernel's OOM killer takes
the pod, and the tenant's chosen policy has never been consulted. The ceiling is three quarters of
the container's memory, and the missing quarter is not caution: a fork for a background save copies
pages on write, the replication backlog and client output buffers sit outside `maxmemory`, and the
exporter shares the pod's limit.

⚠ `save ""` as well as `appendonly no` for None. Dropping the AOF alone leaves Valkey's default RDB
save points in place, so "None" would still snapshot — to a disk the pod does not have.
*/}}
{{- define "valkey.customConfig" -}}
{{- $memory := include "valkey.memory" . -}}
{{- with include "valkey.bytes" $memory }}
{{- if gt (float64 .) 0.0 }}
- {{ printf "maxmemory %.0f" (floor (mulf (float64 .) 0.75)) | quote }}
{{- end }}
{{- end }}
- {{ printf "maxmemory-policy %s" .Values.maxmemoryPolicy | quote }}
{{- if eq .Values.persistence.mode "None" }}
- "appendonly no"
- {{ `save ""` | quote }}
{{- else if eq .Values.persistence.mode "RDB" }}
- "appendonly no"
- "save 900 1 300 10 60 10000"
{{- else }}
- "appendonly yes"
- {{ printf "appendfsync %s" .Values.persistence.fsync | quote }}
{{- end }}
{{- end -}}
