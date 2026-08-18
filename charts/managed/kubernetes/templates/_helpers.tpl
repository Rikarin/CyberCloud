{{/*
The Kubernetes object's name — the Cluster's and the KubevirtCluster's.

⚠ TWO OF THE THREE OBJECTS SHARE THIS NAME AND THE THIRD DOES NOT. Cluster API's own quickstart names
a control plane `{cluster}-control-plane`, and an operator running `kubectl get kamajicontrolplanes`
against a management cluster full of ours should see the shape they expect. The joining is in C# too
— ManagedClusters.ControlPlaneName — and ManagedClusterSizingTests compares the two.
*/}}
{{- define "kubernetes.objectName" -}}
{{- default .Release.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{/*
The KamajiControlPlane's name.
*/}}
{{- define "kubernetes.controlPlaneName" -}}
{{- printf "%s-control-plane" (include "kubernetes.objectName" .) | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{/*
The Secret Cluster API writes the admin kubeconfig into.

⚠ NOTHING IN THIS CHART CREATES IT AND NOTHING IN THIS PLATFORM READS IT. It is named here because
`listCredentials`' handler will need it and because a name nobody wrote down is a name that handler's
author will guess. See conformance.yaml § owed, `listcredentials-has-no-handler`.
*/}}
{{- define "kubernetes.kubeconfigSecretName" -}}
{{- printf "%s-kubeconfig" (include "kubernetes.objectName" .) | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{/*
The full Kubernetes version a minor is rendered as.

⚠ THE API TAKES A MINOR AND KAMAJI PARSES A SEMANTIC VERSION, so the patch is pinned by the platform.
`ManagedClusters.PinnedPatch` is the same table in C# and ManagedClusterSizingTests compares them row
for row.

⚠ THE PATCH IS CHOSEN FROM THE NODE-IMAGE REGISTRY, NOT FROM THE KUBERNETES RELEASE PAGE, and that is
the correction of 2026-08-18. quay.io/capk/ubuntu-2404-container-disk publishes one tag per MINOR —
v1.31.5, v1.32.1, v1.33.5, v1.34.1 — and charts/managed/kubernetes-agentpool renders this same string
as that tag. The previous pins, v1.32.9 and v1.33.4, are real Kubernetes releases and are not tags of
that repository, so every worker VM pulled an image that does not exist. See ManagedClusters.PinnedPatch
for what the choice costs, and SOURCE for what was read where.
*/}}
{{- define "kubernetes.pinnedVersion" -}}
{{- $patches := dict "1.32" "v1.32.1" "1.33" "v1.33.5" -}}
{{- get $patches .Values.version | default (printf "v%s" .Values.version) -}}
{{- end -}}

{{/*
What one control-plane container costs.

⚠ THE FACT THE QUOTA METERS DEPEND ON. ContainerServiceProvider reserves against the C# copy —
ManagedClusters.ControlPlaneCpu and ControlPlaneMemory — and if this one drifted upward the management
cluster would run pods a tenant is not charged for. ⚠ It is multiplied by THREE per replica, because a
Kamaji control-plane replica is kube-apiserver, kube-controller-manager and kube-scheduler and the CRD
takes a separate component block for each.
*/}}
{{- define "kubernetes.controlPlaneResources" -}}
requests:
  cpu: {{ .Values.controlPlaneCpu | quote }}
  memory: {{ .Values.controlPlaneMemory | quote }}
{{- end -}}

{{/*
The seven cybercloud.io/* labels — docs/plan/02 § ADR-013, "Every object carries ...".

⚠ `resource-type` is lower-cased with `/` replaced by `_`, matching KubeLabels.ResourceTypeValue in
src/CyberCloud.Kubernetes.Contracts.
*/}}
{{- define "kubernetes.platformLabels" -}}
cybercloud.io/tenant-id: {{ .Values.platform.tenantId | quote }}
cybercloud.io/subscription-id: {{ .Values.platform.subscriptionId | quote }}
cybercloud.io/resource-group: {{ .Values.platform.resourceGroup | quote }}
cybercloud.io/resource-id: {{ .Values.platform.resourceId | quote }}
cybercloud.io/resource-type: {{ .Values.platform.resourceType | replace "/" "_" | lower | quote }}
cybercloud.io/api-version: {{ .Values.platform.apiVersion | quote }}
cybercloud.io/managed-by: {{ .Values.platform.managedBy | quote }}
{{- end -}}
