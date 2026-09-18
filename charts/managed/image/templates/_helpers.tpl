{{/*
The Kubernetes object's name — the resource's own, which is also the claim's name.

⚠ THE BARE RESOURCE NAME, AND A VIRTUAL MACHINE DEPENDS ON THAT. `Images.ObjectNameOf` adds nothing,
so a machine's body naming this image renders `source.pvc.name: <image>` without reading the image.
*/}}
{{- define "image.objectName" -}}
{{- default .Release.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{/*
The address CDI imports from — the catalogue's pin for a catalogue body, the tenant's own otherwise.

⚠ THE SAME RESOLUTION Images.ImportUrl DOES, so a `helm template` of this chart and the reconciler's
render agree on the bytes a name means. An unknown catalogue name renders nothing here and is refused
by the schema's @enum before it gets this far.
*/}}
{{- define "image.importUrl" -}}
{{- if eq .Values.source.kind "catalogue" -}}
{{- get (include "image.catalogue" . | fromJson) .Values.source.name -}}
{{- else -}}
{{- .Values.source.url -}}
{{- end -}}
{{- end -}}

{{/*
The platform's own cloud images — Images.Catalogue, spelled a second time.

⚠ THE SAME FOUR ROWS AS THE C# TABLE, AND ComputeChartDriftTests COMPARES THEM. A catalogue name
carries a dot, which the values subset Build.Charts reads takes no key for, so the table is a template
dictionary rather than a value block. Every row is a quay.io/containerdisks reference pinned by the
digest quay.io served on the date SOURCE records; re-resolve with the OCI distribution API and move
both copies together.
*/}}
{{- define "image.catalogue" -}}
{{- $catalogue := dict
  "ubuntu-24.04" "docker://quay.io/containerdisks/ubuntu@sha256:1b49166bd3047c7d818be67cec73f891b12db7e792dbc91809412b6be1c20ec2"
  "ubuntu-22.04" "docker://quay.io/containerdisks/ubuntu@sha256:27d3bbe1374521aa43fc50b647d712c9c90693f1e8ce9516aa25aeb73f17681d"
  "debian-13" "docker://quay.io/containerdisks/debian@sha256:518a687c58255e8556906a6f51f45ed9e96b0997eedd6e8a12c36680d82e2956"
  "debian-12" "docker://quay.io/containerdisks/debian@sha256:ba8d5e83785ce239fa1ff9767ee0146019b96421f96543b68b154cf9e1ff4c7f"
-}}
{{- $catalogue | toJson -}}
{{- end -}}

{{/*
The seven cybercloud.io/* labels — docs/plan/02 § ADR-013.
*/}}
{{- define "image.platformLabels" -}}
cybercloud.io/tenant-id: {{ .Values.platform.tenantId | quote }}
cybercloud.io/subscription-id: {{ .Values.platform.subscriptionId | quote }}
cybercloud.io/resource-group: {{ .Values.platform.resourceGroup | quote }}
cybercloud.io/resource-id: {{ .Values.platform.resourceId | quote }}
cybercloud.io/resource-type: {{ .Values.platform.resourceType | replace "/" "_" | lower | quote }}
cybercloud.io/api-version: {{ .Values.platform.apiVersion | quote }}
cybercloud.io/managed-by: {{ .Values.platform.managedBy | quote }}
{{- end -}}
