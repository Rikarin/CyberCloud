{{/*
The Kubernetes object's name.

⚠ THREE COMPONENTS, AND DROPPING ANY ONE OF THEM IS A DIFFERENT SILENT COLLISION.
`NetworkSubnets.ObjectNameOf` is `{namespace}-{network}-{name}` and the reconciler computes it from
the resource's ADDRESS, because that is the only place the network's name lives — docs/plan/12
§ Child resources makes the parent a pure function of the address. Without the namespace, two
subscriptions collide; without the parent's name, two networks in ONE resource group collide. And
because a Subnet is `+kubebuilder:resource:scope="Cluster"`, both collisions are platform-wide rather
than confined to a namespace.
*/}}
{{- define "kube-ovn-subnet.objectName" -}}
{{- default .Release.Name .Values.nameOverride | trunc 253 | trimSuffix "-" -}}
{{- end -}}

{{/*
The `cidrBlock` string — IPv4 first, comma-separated when dual-stack.

⚠ THE ORDER IS NOT COSMETIC. util.CheckProtocol returns `Dual` only when exactly two entries parse as
one v4 and one v6, and the rest of Kube-OVN's dual-stack handling — gateway, excludeIps,
u2oInterconnectionIP — follows the same comma convention and the same family order. The C# twin is
`NetworkSubnets.CidrBlock`, and the two agree because both are one expression rather than two habits.
*/}}
{{- define "kube-ovn-subnet.cidrBlock" -}}
{{- if .Values.addressPrefix.v6 -}}
{{- printf "%s,%s" .Values.addressPrefix.v4 .Values.addressPrefix.v6 -}}
{{- else -}}
{{- .Values.addressPrefix.v4 -}}
{{- end -}}
{{- end -}}

{{/*
The seven cybercloud.io/* labels — docs/plan/02 § ADR-013, "Every object carries ...".

⚠ `resource-type` is lower-cased with `/` replaced by `_`, matching KubeLabels.ResourceTypeValue. A
CHILD type path has TWO slashes — `cybercloud.network_virtualnetworks_subnets` — so the replacement
must be a replace-ALL. Helm's `replace` already is one; stating it is the point, because a
single-replacement spelling would render `cybercloud.network_virtualnetworks/subnets` and be refused
at admission, per object, rather than at lint time.
*/}}
{{- define "kube-ovn-subnet.platformLabels" -}}
cybercloud.io/tenant-id: {{ .Values.platform.tenantId | quote }}
cybercloud.io/subscription-id: {{ .Values.platform.subscriptionId | quote }}
cybercloud.io/resource-group: {{ .Values.platform.resourceGroup | quote }}
cybercloud.io/resource-id: {{ .Values.platform.resourceId | quote }}
cybercloud.io/resource-type: {{ .Values.platform.resourceType | replace "/" "_" | lower | quote }}
cybercloud.io/api-version: {{ .Values.platform.apiVersion | quote }}
cybercloud.io/managed-by: {{ .Values.platform.managedBy | quote }}
{{- end -}}
