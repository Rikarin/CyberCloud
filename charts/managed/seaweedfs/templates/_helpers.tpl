{{/*
Name stem. `nameOverride` is chart plumbing (@internal in values.yaml) and is not part of the
resource body.
*/}}
{{- define "seaweedfs.name" -}}
{{- default .Release.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{/*
The seven cybercloud.io/* labels — docs/plan/02 § ADR-013, "Every object carries ...".

⚠ `resource-type` is lower-cased with `/` replaced by `_`, matching KubeLabels.ResourceTypeValue in
src/CyberCloud.Kubernetes.Contracts. A `/` is not a legal Kubernetes label *value* character, so
rendering the resource type verbatim would produce an object the API server refuses — and the
refusal arrives at apply time, per object, not at lint time.
*/}}
{{- define "seaweedfs.platformLabels" -}}
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

⚠ THE OPERATOR HAS NO DEFAULT AND AN EMPTY IMAGE IS A LEGAL Seaweed. api/v1/image.go's
`applyVersion(image, version)` returns `image` unchanged when it is empty, and every component's
`Image()` goes through it — so an unset spec.image renders pods with no image at all, which fails
per pod at admission rather than at apply. The tag goes here and spec.version stays unset, so the
version is stated once rather than twice with one silently overriding the other.
*/}}
{{- define "seaweedfs.image" -}}
{{- default (printf "chrislusf/seaweedfs:%s" .Values.version) .Values.imageName -}}
{{- end -}}

{{/*
The three-digit SeaweedFS replication code the `replication` member names.

⚠ The digits are a PLACEMENT, not a count: `xyz` is x extra copies in other data centres, y in other
racks of the same data centre, z on other servers in the same rack. So `001` is two copies in total
and `000` is one. Publishing the codes would be publishing somebody else's encoding; publishing a
count would lose the placement, which is the whole of what the code says.

⚠ This table is a second copy of StorageAccounts.ReplicationCodes in
src/Providers/CyberCloud.Providers.Storage/CyberCloud.Providers.Storage.Contracts. It exists because
CyberCloud.Kubernetes.Charts does not, so the reconciler builds the object in C#. StorageSizingTests
diffs the two by reading this file as text — ChartSurfaces filters templates/ out of the chart tree
on purpose, so no emitter will ever read it.

⚠ An unrecognised member falls back to `001` rather than to `000`. A typo must not quietly become
"one copy of everything".
*/}}
{{- define "seaweedfs.replication" -}}
{{- $codes := dict
  "None"                "000"
  "SameRack"            "001"
  "DifferentRack"       "010"
  "DifferentDataCenter" "100" -}}
{{- get $codes .Values.replication | default "001" -}}
{{- end -}}

{{/*
The volume server's sizing preset.

Only the s1 family is tabulated here: values.yaml constrains `sizing.preset` to it, so a preset from
another family cannot reach this template. A preset that is not in the table renders no
requests/limits pair at all rather than a wrong one.

⚠ A second copy of StorageAccounts.Presets, for the reason `seaweedfs.replication` gives.
*/}}
{{- define "seaweedfs.presets" -}}
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

{{- define "seaweedfs.cpu" -}}
{{- $preset := include "seaweedfs.presets" . | fromJson -}}
{{- default (get $preset "cpu") .Values.sizing.cpu -}}
{{- end -}}

{{- define "seaweedfs.memory" -}}
{{- $preset := include "seaweedfs.presets" . | fromJson -}}
{{- default (get $preset "memory") .Values.sizing.memory -}}
{{- end -}}

{{/*
What every pod that is NOT a volume server requests.

⚠ A constant, and it is still not free. A master holds the volume topology in memory, a filer serves
metadata and the S3 gateway is a stateless proxy; none of them scales with the tenant's data and none
is a knob worth publishing. An account with three masters, a filer and two gateways is six pods
before a byte is stored, which is why StorageProvider's quota derivations are a sum over two
populations rather than replicas × one figure.
*/}}
{{- define "seaweedfs.controlPlaneResources" -}}
requests:
  cpu: "250m"
  memory: "512Mi"
limits:
  cpu: "250m"
  memory: "512Mi"
{{- end -}}
