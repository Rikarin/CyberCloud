{{/*
The Kubernetes object's name.

⚠ TWO COMPONENTS RATHER THAN THE THREE THIS FAMILY'S CHILDREN NEED, BECAUSE THIS TYPE HAS NO PARENT.
`PublicIpAddresses.ObjectNameOf` is `{namespace}-{name}` and the reconciler computes it. The namespace
is still mandatory: an OvnEip is cluster-scoped, so two subscriptions each creating an address called
`web` would render ONE object, each converging by overwriting the other, with nothing reporting an
error. ⚠ AND THE COLLISION COSTS MORE HERE THAN ANYWHERE ELSE IN THE FAMILY, because the object is an
ALLOCATION: two tenants would be handed the same address, and the second tenant's traffic would
arrive at the first tenant's NAT rule.
*/}}
{{- define "kube-ovn-eip.objectName" -}}
{{- default .Release.Name .Values.nameOverride | trunc 253 | trimSuffix "-" -}}
{{- end -}}

{{/*
The seven cybercloud.io/* labels — docs/plan/02 § ADR-013, "Every object carries ...".

⚠ `resource-type` is lower-cased with `/` replaced by `_`, matching KubeLabels.ResourceTypeValue. This
type's path has ONE slash rather than the two its siblings carry — `cybercloud.network_publicipaddresses`
— because it is not a child. Helm's `replace` is a replace-all either way.
*/}}
{{- define "kube-ovn-eip.platformLabels" -}}
cybercloud.io/tenant-id: {{ .Values.platform.tenantId | quote }}
cybercloud.io/subscription-id: {{ .Values.platform.subscriptionId | quote }}
cybercloud.io/resource-group: {{ .Values.platform.resourceGroup | quote }}
cybercloud.io/resource-id: {{ .Values.platform.resourceId | quote }}
cybercloud.io/resource-type: {{ .Values.platform.resourceType | replace "/" "_" | lower | quote }}
cybercloud.io/api-version: {{ .Values.platform.apiVersion | quote }}
cybercloud.io/managed-by: {{ .Values.platform.managedBy | quote }}
{{- end -}}
