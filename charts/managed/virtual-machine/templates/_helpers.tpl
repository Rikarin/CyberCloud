{{/*
The Kubernetes object's name — the resource's own, which KubeVirt also gives the instance.

`VirtualMachines.ObjectNameOf` is the bare resource name and the reconciler passes it as the release
name; nothing is joined because a virtual machine is a root type in a namespace of its own resource
group. `ReconcileDriver.NamespaceFor` is `{subscriptionId:N}-{resourceGroup}`, so two groups holding
a machine called `web` are two namespaces.
*/}}
{{- define "virtual-machine.objectName" -}}
{{- default .Release.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{/*
The root disk's DataVolume name.

⚠ SUFFIXED, because an image's claim and a managed disk's claim are both the bare resource name in
the same namespace — VirtualMachines.RootDataVolumeName says why a root disk called after its machine
would collide with a managed disk called after the same machine.
*/}}
{{- define "virtual-machine.rootDataVolume" -}}
{{- printf "%s-root" (include "virtual-machine.objectName" .) -}}
{{- end -}}

{{/*
The cloud-init Secret's name — `{name}-cloud-init`, key `userdata`.

⚠ RENDERED BY THE RECONCILER, NEVER BY THIS CHART. A chart carries values and a vault value must not
become one; VirtualMachines.CloudInitSecretJson writes the Secret from the resolved handle, and this
chart only names it. VirtualMachines.CloudInitSecretName is the same spelling.
*/}}
{{- define "virtual-machine.cloudInitSecret" -}}
{{- printf "%s-cloud-init" (include "virtual-machine.objectName" .) -}}
{{- end -}}

{{/*
The closed set of sizes — VirtualMachines.Sizes, spelled a second time.

⚠ THE SAME FOUR ROWS AS THE C# TABLE, AND ComputeChartDriftTests COMPARES THEM. A Helm template is not
a schema and nothing generates it, so each row here is a hand-maintained copy of one in
VirtualMachines.Sizes; a rung that drifted would be a machine billed at one size and booted at
another. Whole cores only: `domain.cpu.cores` is an integer.
*/}}
{{- define "virtual-machine.cores" -}}
{{- $cores := dict "s1.small" 1 "s1.medium" 2 "s1.large" 4 "s1.xlarge" 8 -}}
{{- get $cores .Values.size -}}
{{- end -}}

{{- define "virtual-machine.memory" -}}
{{- $memory := dict "s1.small" "4Gi" "s1.medium" "8Gi" "s1.large" "16Gi" "s1.xlarge" "32Gi" -}}
{{- get $memory .Values.size -}}
{{- end -}}

{{/*
The Kube-OVN Subnet object the interface joins, or empty for the pod network.

⚠ `{namespace}-{network}-{subnet}`, which is NetworkSubnets.ObjectNameOf's rule and
VirtualMachines.LogicalSwitchOf's second spelling of it. Either name empty is the pod network: half a
join is not a guess.
*/}}
{{- define "virtual-machine.logicalSwitch" -}}
{{- if and .Values.network.virtualNetwork .Values.network.subnet -}}
{{- printf "%s-%s-%s" .Release.Namespace .Values.network.virtualNetwork .Values.network.subnet -}}
{{- end -}}
{{- end -}}

{{/*
The seven cybercloud.io/* labels — docs/plan/02 § ADR-013.

⚠ `resource-type` is lower-cased with `/` replaced by `_`, matching KubeLabels.ResourceTypeValue.
*/}}
{{- define "virtual-machine.platformLabels" -}}
cybercloud.io/tenant-id: {{ .Values.platform.tenantId | quote }}
cybercloud.io/subscription-id: {{ .Values.platform.subscriptionId | quote }}
cybercloud.io/resource-group: {{ .Values.platform.resourceGroup | quote }}
cybercloud.io/resource-id: {{ .Values.platform.resourceId | quote }}
cybercloud.io/resource-type: {{ .Values.platform.resourceType | replace "/" "_" | lower | quote }}
cybercloud.io/api-version: {{ .Values.platform.apiVersion | quote }}
cybercloud.io/managed-by: {{ .Values.platform.managedBy | quote }}
{{- end -}}
