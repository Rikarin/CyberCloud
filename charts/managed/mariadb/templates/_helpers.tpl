{{/*
Name stem. `nameOverride` is chart plumbing (@internal in values.yaml) and is not part of the
resource body.
*/}}
{{- define "mariadb.name" -}}
{{- default .Release.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{/*
The seven cybercloud.io/* labels — docs/plan/02 § ADR-013, "Every object carries ...".

⚠ `resource-type` is lower-cased with `/` replaced by `_`, matching KubeLabels.ResourceTypeValue in
src/CyberCloud.Kubernetes.Contracts. A `/` is not a legal Kubernetes label *value* character, so
rendering the resource type verbatim would produce an object the API server refuses — and the
refusal arrives at apply time, per object, not at lint time.
*/}}
{{- define "mariadb.platformLabels" -}}
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

⚠ NOT the operator's default. `MariaDB.GetImage` falls back to `env.RelatedMariadbImage` when
`spec.image` is empty — an image chosen by whoever installed the operator, at whatever version they
shipped. The api-version's `version` enum is a promise about which MariaDB the tenant gets, and
inheriting a cluster-wide default is how that promise is broken without anybody editing anything.
*/}}
{{- define "mariadb.image" -}}
{{- default (printf "mariadb:%s" .Values.version) .Values.imageName -}}
{{- end -}}

{{/*
The instance count.

⚠ NOT A VALUES KEY, and that is the decision this chart is most likely to be "fixed" against.
Galera splits its brain on an even count: the CRD carries "An odd number of MariaDB instances
(mariadb.spec.replicas) is required to avoid split brain situations for Galera", with an opt-out at
`galera.replicasAllowEvenNumber` that this chart does not render. A `replicas` values key of 1..5
would be a value that validates here and produces a CR the API server refuses — after the caller was
already told 202 — for every even number in the range, and `ResourceSchema` has no way to spell
"odd". So the topology names the count: None is 1, Galera is 3.

⚠ Three rather than five for the same reason Valkey pins three Sentinels: it is the smallest odd
number that can hold a majority opinion, and the size of the quorum is not a thing a tenant is in a
position to choose well.
*/}}
{{- define "mariadb.replicas" -}}
{{- if eq .Values.highAvailability "Galera" -}}3{{- else -}}1{{- end -}}
{{- end -}}

{{/*
The memory and CPU quantities in force: the explicit ones, or the preset's.

Only the s1 family is tabulated here: values.yaml constrains `sizing.preset` to it, so a preset from
another family cannot reach this template. A preset that is not in the table renders no `resources`
block at all rather than a wrong one.

⚠ This table is a second copy of MariaDbServers.Presets in
src/Providers/CyberCloud.Providers.DBforMySQL/CyberCloud.Providers.DBforMySQL.Contracts. It exists
because CyberCloud.Kubernetes.Charts does not, so the reconciler builds the object in C#. The two are
diffed by ChartRegistryPairTests, which reads this file as text — ChartSurfaces filters templates/
out of the chart tree on purpose, so no emitter will ever read it.

⚠ It is ALSO a second copy of the s1 family in charts/managed/postgres/templates/_helpers.tpl, and
nothing anywhere compares those two. docs/plan/03 § Assembly graph rules, rule 2 forbids this
provider referencing CyberCloud.Providers.DBforPostgreSQL.Contracts even for a `const`, and the s1
table is a docs/plan/12 § Sizing vocabulary fact rather than a Kubernetes one, so it has no home in
CyberCloud.ResourceManager.Contracts the way KubeQuantity.Pattern did. Recorded rather than fixed.
*/}}
{{- define "mariadb.presets" -}}
{{- $presets := dict
  "s1.nano"    (dict "cpu" "100m" "memory" "512Mi")
  "s1.micro"   (dict "cpu" "250m" "memory" "1Gi")
  "s1.small"   (dict "cpu" "500m" "memory" "2Gi")
  "s1.medium"  (dict "cpu" "1"    "memory" "4Gi")
  "s1.large"   (dict "cpu" "2"    "memory" "8Gi")
  "s1.xlarge"  (dict "cpu" "4"    "memory" "16Gi")
  "s1.2xlarge" (dict "cpu" "8"    "memory" "32Gi")
  "s1.4xlarge" (dict "cpu" "16"   "memory" "64Gi") -}}
{{- get $presets .Values.sizing.preset | default dict | toJson -}}
{{- end -}}

{{- define "mariadb.cpu" -}}
{{- $preset := include "mariadb.presets" . | fromJson -}}
{{- default (get $preset "cpu") .Values.sizing.cpu -}}
{{- end -}}

{{- define "mariadb.memory" -}}
{{- $preset := include "mariadb.presets" . | fromJson -}}
{{- default (get $preset "memory") .Values.sizing.memory -}}
{{- end -}}

{{/*
Resources, from the sizing preset unless an explicit quantity overrides it — docs/plan/12
§ Sizing vocabulary, "the preset is a default, not a cage".
*/}}
{{- define "mariadb.resources" -}}
{{- $cpu := include "mariadb.cpu" . -}}
{{- $memory := include "mariadb.memory" . -}}
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
The my.cnf the server starts with.

⚠ One setting, and it is the one whose default is wrong for a managed database. MariaDB ships
`max_connections=151`; a pooled application tier reaches that on a bad afternoon and the symptom is
"Too many connections" rather than anything a tenant would attribute to a server setting. The same
number as charts/managed/postgres' `max_connections`, so that the two managed relational databases
answer the same question the same way.

⚠ `[mariadb]` and not `[mysqld]`. Both are read by a MariaDB server, and the row is MariaDB — a
group header claiming otherwise would be the compatibility pretence Chart.yaml's header rejects.
*/}}
{{- define "mariadb.myCnf" -}}
[mariadb]
max_connections=200
{{- end -}}
