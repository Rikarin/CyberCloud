{{/*
The Kubernetes object's name.

⚠ THE OBJECT IS CLUSTER-SCOPED, SO THIS NAME IS THE ONLY THING SEPARATING TWO TENANTS.
`VirtualNetworks.ObjectNameOf` is `{namespace}-{name}` and the reconciler computes it, because the
namespace — `ReconcileDriver.NamespaceFor`, `{subscriptionId:N}-{resourceGroup}` — is the platform's
and not this chart's. So this template is `default .Release.Name .Values.nameOverride`, exactly as
every other managed chart's is, and the qualification happened before the chart saw it.

⚠ WHY IT MUST BE QUALIFIED AT ALL, since nothing here shows it: every object the nine earlier
provider families render is NAMESPACED, and the namespace is what has kept two tenants' identically
named resources apart for all of them without any provider thinking about it. A `Vpc` is
`+kubebuilder:resource:scope="Cluster"`. Two subscriptions each creating a network called `prod`
would render one object named `prod`, each converging by overwriting the other, with nothing
reporting an error anywhere.
*/}}
{{- define "kube-ovn-vpc.objectName" -}}
{{- default .Release.Name .Values.nameOverride | trunc 253 | trimSuffix "-" -}}
{{- end -}}

{{/*
The seven cybercloud.io/* labels — docs/plan/02 § ADR-013, "Every object carries ...".

⚠ `resource-type` is lower-cased with `/` replaced by `_`, matching KubeLabels.ResourceTypeValue in
src/CyberCloud.Kubernetes.Contracts. A `/` is not a legal Kubernetes label *value* character.
*/}}
{{- define "kube-ovn-vpc.platformLabels" -}}
cybercloud.io/tenant-id: {{ .Values.platform.tenantId | quote }}
cybercloud.io/subscription-id: {{ .Values.platform.subscriptionId | quote }}
cybercloud.io/resource-group: {{ .Values.platform.resourceGroup | quote }}
cybercloud.io/resource-id: {{ .Values.platform.resourceId | quote }}
cybercloud.io/resource-type: {{ .Values.platform.resourceType | replace "/" "_" | lower | quote }}
cybercloud.io/api-version: {{ .Values.platform.apiVersion | quote }}
cybercloud.io/managed-by: {{ .Values.platform.managedBy | quote }}
{{- end -}}
