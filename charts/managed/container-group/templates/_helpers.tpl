{{/*
The pod's name — the resource's own. ContainerGroups.PodName.
*/}}
{{- define "container-group.objectName" -}}
{{- default .Release.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{/*
The Kube-OVN Subnet object the pod joins, or empty for the pod network — NetworkSubnets.ObjectNameOf's
rule, ContainerGroups.LogicalSwitchOf's spelling of it.
*/}}
{{- define "container-group.logicalSwitch" -}}
{{- if and .Values.network.virtualNetwork .Values.network.subnet -}}
{{- printf "%s-%s-%s" .Release.Namespace .Values.network.virtualNetwork .Values.network.subnet -}}
{{- end -}}
{{- end -}}

{{/*
The labels the group's pod carries beside the platform's — ContainerGroups.NameLabel and InstanceLabel.
*/}}
{{- define "container-group.selectorLabels" -}}
app.kubernetes.io/name: container-group
app.kubernetes.io/instance: {{ include "container-group.objectName" . | quote }}
{{- end -}}

{{/*
The seven cybercloud.io/* labels — docs/plan/02 § ADR-013.

⚠ `resource-type` is lower-cased with `/` replaced by `_`, matching KubeLabels.ResourceTypeValue.
*/}}
{{- define "container-group.platformLabels" -}}
cybercloud.io/tenant-id: {{ .Values.platform.tenantId | quote }}
cybercloud.io/subscription-id: {{ .Values.platform.subscriptionId | quote }}
cybercloud.io/resource-group: {{ .Values.platform.resourceGroup | quote }}
cybercloud.io/resource-id: {{ .Values.platform.resourceId | quote }}
cybercloud.io/resource-type: {{ .Values.platform.resourceType | replace "/" "_" | lower | quote }}
cybercloud.io/api-version: {{ .Values.platform.apiVersion | quote }}
cybercloud.io/managed-by: {{ .Values.platform.managedBy | quote }}
{{- end -}}
