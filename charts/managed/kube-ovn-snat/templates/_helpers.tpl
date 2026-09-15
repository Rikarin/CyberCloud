{{/*
The Kubernetes object's name.

⚠ THREE COMPONENTS, AND DROPPING ANY ONE OF THEM IS A DIFFERENT SILENT COLLISION.
`NatGateways.ObjectNameOf` is `{namespace}-{network}-{name}` and the reconciler computes it from the
resource's ADDRESS, because that is the only place the network's name lives. Without the namespace,
two subscriptions collide; without the parent's name, two networks in ONE resource group collide.
An OvnSnatRule is `+kubebuilder:resource:scope="Cluster"`, so both collisions are platform-wide —
and on this kind a collision is one tenant's subnet translated to another tenant's address.
*/}}
{{- define "kube-ovn-snat.objectName" -}}
{{- default .Release.Name .Values.nameOverride | trunc 253 | trimSuffix "-" -}}
{{- end -}}

{{/*
The seven cybercloud.io/* labels — docs/plan/02 § ADR-013, "Every object carries ...".

⚠ `resource-type` is lower-cased with `/` replaced by `_`, matching KubeLabels.ResourceTypeValue. A
CHILD type path has TWO slashes — `cybercloud.network_virtualnetworks_natgateways` — so the
replacement must be a replace-ALL, which Helm's `replace` is.
*/}}
{{- define "kube-ovn-snat.platformLabels" -}}
cybercloud.io/tenant-id: {{ .Values.platform.tenantId | quote }}
cybercloud.io/subscription-id: {{ .Values.platform.subscriptionId | quote }}
cybercloud.io/resource-group: {{ .Values.platform.resourceGroup | quote }}
cybercloud.io/resource-id: {{ .Values.platform.resourceId | quote }}
cybercloud.io/resource-type: {{ .Values.platform.resourceType | replace "/" "_" | lower | quote }}
cybercloud.io/api-version: {{ .Values.platform.apiVersion | quote }}
cybercloud.io/managed-by: {{ .Values.platform.managedBy | quote }}
{{- end -}}
