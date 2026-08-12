{{/*
The Kubernetes object's name — shared by all three objects a pool renders.

⚠ IT IS THE RELEASE NAME, AND THE JOINING THAT MAKES IT UNIQUE HAPPENS BEFORE THIS CHART SEES IT.
`AgentPools.ObjectNameOf` is `{clusterName}-{poolName}` and the reconciler computes it from the
resource's ADDRESS, because that is the only place the cluster's name lives — docs/plan/12 § Child
resources makes the parent a pure function of the address.

⚠ WHY IT MUST BE QUALIFIED AT ALL: `ReconcileDriver.NamespaceFor` is
`{subscriptionId:N}-{resourceGroup}`, so a parent RESOURCE lives inside a namespace rather than being
one. Two clusters in one resource group may each hold a pool called `workers`, and a renderer that
named the objects for the pool alone would have the two fighting over one MachineDeployment — which on
this type means every worker VM in the resource group moving from one cluster to the other and back on
each pass.
*/}}
{{- define "kubernetes-agentpool.objectName" -}}
{{- default .Release.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{/*
The Cluster this pool's machines join.

⚠ IT COMES FROM THE ADDRESS AND NEVER FROM THE BODY. A `clusterName` property in the body would be a
second spelling of a fact ResourceId.Parent already answers, and the two would disagree the first time
a body was sent under the wrong path. `platform.parentName` is @internal in values.yaml for exactly
that reason.

⚠ NO `required` GUARD, which is the tree's convention rather than an oversight: every `platform.*`
value in every managed chart defaults to empty and `helm lint --strict` runs a chart against its own
defaults. The refusal lives in C#, where the fact does — `AgentPools.ObjectNameOf` throws on an id
with no parent name, and ResourceId itself enforces `ParentNames.Count == Type.Depth - 1`.
*/}}
{{- define "kubernetes-agentpool.clusterName" -}}
{{- .Values.platform.parentName -}}
{{- end -}}

{{/*
The label the MachineDeployment selects its machines with.

⚠ IT IS WRITTEN INTO TWO PLACES THAT MUST AGREE, AND ADR-013's SEVEN LABELS COVER NEITHER.
KubeCommandBuilder injects the seven into the OBJECT's metadata.labels; a MachineDeployment's
spec.selector and its spec.template.metadata.labels are a different pair, are injected by nothing, and
Cluster API's own validating webhook refuses the object when they disagree. One helper, used twice, is
why they cannot.
*/}}
{{- define "kubernetes-agentpool.selectorLabels" -}}
cybercloud.io/agent-pool: {{ include "kubernetes-agentpool.objectName" . | quote }}
{{- end -}}

{{/*
The full Kubernetes version a minor is rendered as.

⚠ THE SAME PIN THE CONTROL PLANE USES — ManagedClusters.PinnedPatch, and charts/managed/kubernetes'
own `kubernetes.pinnedVersion`. Two tables would drift, and the drift would be a version skew nobody
declared. ManagedClusterSizingTests compares all three copies.
*/}}
{{- define "kubernetes-agentpool.pinnedVersion" -}}
{{- $patches := dict "1.32" "v1.32.9" "1.33" "v1.33.4" -}}
{{- get $patches .Values.version | default (printf "v%s" .Values.version) -}}
{{- end -}}

{{/*
The seven cybercloud.io/* labels — docs/plan/02 § ADR-013.

⚠ TWO underscores in the resource-type value, not one, because a CHILD type path has two slashes and
`/` is not a legal label VALUE character. Helm's `replace` is already a replace-all; stating it is the
point, because a single-replacement spelling renders
`cybercloud.containerservice_managedclusters/agentPools` and is refused at admission, per object,
rather than at lint time.
*/}}
{{- define "kubernetes-agentpool.platformLabels" -}}
cybercloud.io/tenant-id: {{ .Values.platform.tenantId | quote }}
cybercloud.io/subscription-id: {{ .Values.platform.subscriptionId | quote }}
cybercloud.io/resource-group: {{ .Values.platform.resourceGroup | quote }}
cybercloud.io/resource-id: {{ .Values.platform.resourceId | quote }}
cybercloud.io/resource-type: {{ .Values.platform.resourceType | replace "/" "_" | lower | quote }}
cybercloud.io/api-version: {{ .Values.platform.apiVersion | quote }}
cybercloud.io/managed-by: {{ .Values.platform.managedBy | quote }}
{{- end -}}
