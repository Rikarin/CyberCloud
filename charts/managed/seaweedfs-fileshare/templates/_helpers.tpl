{{/*
The claim's name.

⚠ IT IS THE RELEASE NAME, AND THE JOINING THAT MAKES IT UNIQUE HAPPENS BEFORE THIS CHART SEES IT.
`StorageFileShares.ObjectNameOf` is `{accountName}-{shareName}` and the reconciler computes it from
the resource's ADDRESS — docs/plan/12 § Child resources makes the parent a pure function of the
address. Two accounts in one resource group share a namespace, and each may hold a share called
`home`; a claim named for the share alone would have the two fighting over one object.
*/}}
{{- define "seaweedfs-fileshare.claimName" -}}
{{- default .Release.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{/*
The account's SeaweedCSIDriver object: `{accountName}-csi`.

⚠ ONE PER ACCOUNT, NOT ONE PER SHARE, AND THIS CHART RENDERS IT ANYWAY. The driver's filer is a
process argument, so a driver serves exactly one account's filer and every share of that account
renders the SAME document — server-side apply of an unchanged document is a no-op, and the last share
out removes it. A share called `csi` renders a CLAIM called `{account}-csi` as well; the two are
different kinds on different REST paths and do not collide.
*/}}
{{- define "seaweedfs-fileshare.driverObjectName" -}}
{{- printf "%s-csi" (default "account" .Values.platform.parentName) | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{/*
The driver's `driverName`, which is also the StorageClass's name.

⚠ CLUSTER-UNIQUE, AND COMPUTED BY THE RECONCILER RATHER THAN HERE. The controller writes a
cluster-scoped CSIDriver and StorageClass under this name and refuses a second SeaweedCSIDriver that
claims it, so two accounts called `main` in two resource groups must get two names.
`StorageFileShares.DriverNameOf` folds the namespace in through a digest; a template has no SHA-256
of its own, so the value arrives projected in `platform.driverName` and a lint run against the
defaults gets a placeholder that satisfies the CRD's pattern and nothing else.
*/}}
{{- define "seaweedfs-fileshare.driverName" -}}
{{- default (printf "%s.csi.cybercloud.io" .Release.Name) .Values.platform.driverName | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{/*
The seven cybercloud.io/* labels — docs/plan/02 § ADR-013 — plus the eighth this type carries.

⚠ `resource-type` is lower-cased with `/` replaced by `_`, matching KubeLabels.ResourceTypeValue —
`cybercloud.storage_accounts_fileshares`, two underscores, as the bucket chart records.

⚠ `storage.cybercloud.io/account` IS THE LABEL THE DELETE LISTS ON. StorageFileShareReconciler
decides whether the account's driver still has a user by listing the claims in the namespace that
carry this label for this account; ADR-013's seven name the resource and none of them names the
account, and the object name cannot be split back unambiguously when both halves contain hyphens.
*/}}
{{- define "seaweedfs-fileshare.platformLabels" -}}
cybercloud.io/tenant-id: {{ .Values.platform.tenantId | quote }}
cybercloud.io/subscription-id: {{ .Values.platform.subscriptionId | quote }}
cybercloud.io/resource-group: {{ .Values.platform.resourceGroup | quote }}
cybercloud.io/resource-id: {{ .Values.platform.resourceId | quote }}
cybercloud.io/resource-type: {{ .Values.platform.resourceType | replace "/" "_" | lower | quote }}
cybercloud.io/api-version: {{ .Values.platform.apiVersion | quote }}
cybercloud.io/managed-by: {{ .Values.platform.managedBy | quote }}
storage.cybercloud.io/account: {{ .Values.platform.parentName | quote }}
{{- end -}}
