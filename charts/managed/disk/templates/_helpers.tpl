{{/*
The Kubernetes object's name — the resource's own, which is also the claim's name.

⚠ THE BARE RESOURCE NAME, AND A VIRTUAL MACHINE DEPENDS ON THAT. `Disks.ObjectNameOf` adds nothing,
so a machine's body naming this disk renders `persistentVolumeClaim.claimName: <disk>` without reading
the disk. A prefix here would be a prefix the machine has to know about, in a second place.
*/}}
{{- define "disk.objectName" -}}
{{- default .Release.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{/*
The seven cybercloud.io/* labels — docs/plan/02 § ADR-013.
*/}}
{{- define "disk.platformLabels" -}}
cybercloud.io/tenant-id: {{ .Values.platform.tenantId | quote }}
cybercloud.io/subscription-id: {{ .Values.platform.subscriptionId | quote }}
cybercloud.io/resource-group: {{ .Values.platform.resourceGroup | quote }}
cybercloud.io/resource-id: {{ .Values.platform.resourceId | quote }}
cybercloud.io/resource-type: {{ .Values.platform.resourceType | replace "/" "_" | lower | quote }}
cybercloud.io/api-version: {{ .Values.platform.apiVersion | quote }}
cybercloud.io/managed-by: {{ .Values.platform.managedBy | quote }}
{{- end -}}
