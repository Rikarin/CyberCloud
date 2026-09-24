{{/*
The pool's name — the resource's own, which the pool controller prefixes every machine with:
`{pool}-{index}`. VirtualMachineScaleSets.ObjectNameOf.
*/}}
{{- define "virtual-machine-scale-set.objectName" -}}
{{- default .Release.Name .Values.nameOverride | trunc 60 | trimSuffix "-" -}}
{{- end -}}

{{/*
The root disk's DataVolume template name. ⚠ The pool controller appends each machine's index, so
machine `web-0` clones into `web-root-0`: VirtualMachines.RootDataVolumeName, indexed by KubeVirt.
*/}}
{{- define "virtual-machine-scale-set.rootDataVolume" -}}
{{- printf "%s-root" (include "virtual-machine-scale-set.objectName" .) -}}
{{- end -}}

{{/*
The one cloud-init Secret every machine mounts — `{name}-cloud-init`, key `userdata`, rendered by the
reconciler from the resolved handle. ⚠ `nameGeneration.appendIndexToSecretRefs` is left off: the user
data is one value for the whole set.
*/}}
{{- define "virtual-machine-scale-set.cloudInitSecret" -}}
{{- printf "%s-cloud-init" (include "virtual-machine-scale-set.objectName" .) -}}
{{- end -}}

{{/*
The closed set of sizes — VirtualMachines.Sizes, spelled a third time.

⚠ THE SAME FOUR ROWS AS charts/managed/virtual-machine's helpers AND THE C# TABLE, and
ComputeChartDriftTests compares all three. A rung that drifted here would be a set billed at one size
and booted at another, N times over.
*/}}
{{- define "virtual-machine-scale-set.cores" -}}
{{- $cores := dict "s1.small" 1 "s1.medium" 2 "s1.large" 4 "s1.xlarge" 8 -}}
{{- get $cores .Values.size -}}
{{- end -}}

{{- define "virtual-machine-scale-set.memory" -}}
{{- $memory := dict "s1.small" "4Gi" "s1.medium" "8Gi" "s1.large" "16Gi" "s1.xlarge" "32Gi" -}}
{{- get $memory .Values.size -}}
{{- end -}}

{{/*
The Kube-OVN Subnet object every machine's interface joins, or empty for the pod network —
NetworkSubnets.ObjectNameOf's rule, VirtualMachines.LogicalSwitchOf's spelling of it.
*/}}
{{- define "virtual-machine-scale-set.logicalSwitch" -}}
{{- if and .Values.network.virtualNetwork .Values.network.subnet -}}
{{- printf "%s-%s-%s" .Release.Namespace .Values.network.virtualNetwork .Values.network.subnet -}}
{{- end -}}
{{- end -}}

{{/*
The labels the pool selects its machines by — VirtualMachineScaleSets.NameLabel and InstanceLabel.
*/}}
{{- define "virtual-machine-scale-set.selectorLabels" -}}
app.kubernetes.io/name: virtual-machine-scale-set
app.kubernetes.io/instance: {{ include "virtual-machine-scale-set.objectName" . | quote }}
{{- end -}}

{{/*
The replica count: what the pool says, which the reconciler passes; the capacity for a set not yet
created. ⚠ -1 is "not given" and not a count — `default` would turn a deliberate zero into the capacity.
*/}}
{{- define "virtual-machine-scale-set.replicas" -}}
{{- if ge (int .Values.replicas) 0 -}}
{{- .Values.replicas -}}
{{- else -}}
{{- .Values.capacity -}}
{{- end -}}
{{- end -}}

{{/*
The seven cybercloud.io/* labels — docs/plan/02 § ADR-013.

⚠ `resource-type` is lower-cased with `/` replaced by `_`, matching KubeLabels.ResourceTypeValue.
*/}}
{{- define "virtual-machine-scale-set.platformLabels" -}}
cybercloud.io/tenant-id: {{ .Values.platform.tenantId | quote }}
cybercloud.io/subscription-id: {{ .Values.platform.subscriptionId | quote }}
cybercloud.io/resource-group: {{ .Values.platform.resourceGroup | quote }}
cybercloud.io/resource-id: {{ .Values.platform.resourceId | quote }}
cybercloud.io/resource-type: {{ .Values.platform.resourceType | replace "/" "_" | lower | quote }}
cybercloud.io/api-version: {{ .Values.platform.apiVersion | quote }}
cybercloud.io/managed-by: {{ .Values.platform.managedBy | quote }}
{{- end -}}
