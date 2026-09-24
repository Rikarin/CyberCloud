{{/*
The component's own name — the release name unless overridden. It is also the value of
`service.namespace`, which is what the views filter on.
*/}}
{{- define "monitorComponent.name" -}}
{{- default .Release.Name .Values.nameOverride -}}
{{- end -}}

{{/*
The ConfigMap's name — `component-{workspace}-{name}`, the C# side's MonitorComponents.ObjectNameOf.
The workspace's name is in it because the namespace is the resource group's and two workspaces in one
group may each hold a component called `shop`.
*/}}
{{- define "monitorComponent.objectName" -}}
{{- printf "component-%s-%s" .Values.platform.workspace (include "monitorComponent.name" .) -}}
{{- end -}}

{{/*
The collector's in-cluster endpoint — MonitorComponents.Endpoint, spelled again here because a chart
cannot include another chart's helper. The collector's Service is `collector-{workspace}-{collector}`
(charts/managed/monitor-collector's `monitorCollector.objectName`); 4318 for http/protobuf, 4317 for
grpc. ⚠ The collector's body is never read: a collector with that receiver off, or none of that name,
is an address that does not answer — ComponentDeclarationTests compares this helper to the C# side.
*/}}
{{- define "monitorComponent.endpoint" -}}
{{- $port := ternary "4317" "4318" (eq .Values.protocol "grpc") -}}
{{- printf "http://collector-%s-%s.%s.svc:%s" .Values.platform.workspace .Values.collector .Values.platform.namespace $port -}}
{{- end -}}

{{/*
The seven cybercloud.io/* labels — docs/plan/02 § ADR-013. `resource-type` is lower-cased with `/`
replaced by `_`, matching KubeLabels.ResourceTypeValue.
*/}}
{{- define "monitorComponent.platformLabels" -}}
cybercloud.io/tenant-id: {{ .Values.platform.tenantId | quote }}
cybercloud.io/subscription-id: {{ .Values.platform.subscriptionId | quote }}
cybercloud.io/resource-group: {{ .Values.platform.resourceGroup | quote }}
cybercloud.io/resource-id: {{ .Values.platform.resourceId | quote }}
cybercloud.io/resource-type: {{ .Values.platform.resourceType | replace "/" "_" | lower | quote }}
cybercloud.io/api-version: {{ .Values.platform.apiVersion | quote }}
cybercloud.io/managed-by: {{ .Values.platform.managedBy | quote }}
{{- end -}}
