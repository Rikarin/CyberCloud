{{/*
The ScheduledBackup's name: `{vault}-{item}-{digest}`.

⚠ IT IS THE RELEASE NAME, AND THE JOINING THAT MAKES IT UNIQUE HAPPENS BEFORE THIS CHART SEES IT.
`RecoveryVaults.ScheduledBackupNameOf` is `{vaultName}-{itemName}` and twelve hex digits of SHA-256
over `{vaultName}/{itemName}`, the two names stemmed to 24 characters each when the whole does not fit
in 63, and the reconciler computes it from the vault's address and the item's. Two vaults in one
resource group may protect one server, and one vault protects many, so neither name alone is unique
in the namespace — and the digest is there even when the names fit, because a hyphen join is not
unique either: vault `a` protecting `b-c` and vault `a-b` protecting `c` would otherwise both own
`a-b-c`, under one field manager, so the API server would let each take it from the other.
*/}}
{{- define "recovery-vault.scheduleName" -}}
{{- default .Release.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{/*
The CloudNativePG Cluster the schedule backs up.

⚠ PROJECTED IN, NEVER DERIVED. The reconciler learns this name from ReconcileContext.View — the
address of the object charts/managed/postgres rendered for the protected server — and a template that
derived it from the item's name would bind this chart to another chart's naming through a
coincidence. Empty means a lint run, which gets a placeholder that satisfies the CRD and nothing else.
*/}}
{{- define "recovery-vault.clusterName" -}}
{{- default "protected-cluster" .Values.platform.clusterName -}}
{{- end -}}

{{/*
The seven cybercloud.io/* labels — docs/plan/02 § ADR-013 — plus the eighth this type carries.

⚠ `resource-type` is lower-cased with `/` replaced by `_`, matching KubeLabels.ResourceTypeValue —
`cybercloud.recoveryservices_vaults`.

⚠ `recoveryservices.cybercloud.io/protected-item` IS THE LABEL THE ACTIONS READ. listRecoveryPoints
and recover find the vault's schedules by the seven and read which item each serves off this one;
the object name folds the two halves through a digest and cannot be split back.
*/}}
{{- define "recovery-vault.platformLabels" -}}
cybercloud.io/tenant-id: {{ .Values.platform.tenantId | quote }}
cybercloud.io/subscription-id: {{ .Values.platform.subscriptionId | quote }}
cybercloud.io/resource-group: {{ .Values.platform.resourceGroup | quote }}
cybercloud.io/resource-id: {{ .Values.platform.resourceId | quote }}
cybercloud.io/resource-type: {{ .Values.platform.resourceType | replace "/" "_" | lower | quote }}
cybercloud.io/api-version: {{ .Values.platform.apiVersion | quote }}
cybercloud.io/managed-by: {{ .Values.platform.managedBy | quote }}
recoveryservices.cybercloud.io/protected-item: {{ .Values.platform.protectedItem | quote }}
{{- end -}}
