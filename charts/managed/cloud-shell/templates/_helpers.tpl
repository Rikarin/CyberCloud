{{/*
The Kubernetes object-name stem — the console's own name.
*/}}
{{- define "cloudshell.objectName" -}}
{{- default .Release.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{/*
The home volume's name.

⚠ THE JOINING IS IN C# TOO — CloudConsoles.HomeClaimName — and ConsoleSizingTests compares the two.
Two spellings of one object's name is a pod that mounts a claim nobody created.
*/}}
{{- define "cloudshell.homeName" -}}
{{- printf "%s-home" (include "cloudshell.objectName" .) | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{/*
The shell's name — the ServiceAccount's, the NetworkPolicy's and the Pod's.

⚠ ONE NAME FOR THREE OBJECTS, AND FOR THE POD IT IS WHAT MAKES `connect` IDEMPOTENT: a second browser
tab applies the same object and gets it back unchanged, so re-joining a live shell and starting one
are the same call. CloudConsoles.ShellName is the same joining.
*/}}
{{- define "cloudshell.shellName" -}}
{{- printf "%s-shell" (include "cloudshell.objectName" .) | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{/*
What one shell costs.

⚠ THE FACT THE QUOTA METER DOES *NOT* DEPEND ON, WHICH IS THE UNUSUAL PART. Every other chart in this
directory has a sizing table its provider reserves vCPU and memory against; TerminalProvider declares
neither meter, because a console's pod exists only while somebody is typing into it and a state-based
reservation from the body would hold 2 vCPU against a subscription for a terminal that was closed a
week ago. CloudConsoles.Presets is the same table and ConsoleSizingTests compares it row for row.

⚠ THE LADDER STOPS AT 2 vCPU AND 4 GiB. That is docs/plan/19 § The pod's ceiling — "0.5–2 vCPU,
1–4 GB" — and not a round number. There is deliberately no larger preset.
*/}}
{{- define "cloudshell.resources" -}}
{{- $presets := dict
  "c1.small"  (dict "cpu" "500m" "memory" "1Gi")
  "c1.medium" (dict "cpu" "1"    "memory" "2Gi")
  "c1.large"  (dict "cpu" "2"    "memory" "4Gi") -}}
{{- $chosen := get $presets .Values.sizing.preset | default (get $presets "c1.small") -}}
requests:
  cpu: {{ $chosen.cpu | quote }}
  memory: {{ $chosen.memory | quote }}
  ephemeral-storage: {{ .Values.ephemeralStorageLimit | quote }}
limits:
  cpu: {{ $chosen.cpu | quote }}
  memory: {{ $chosen.memory | quote }}
  ephemeral-storage: {{ .Values.ephemeralStorageLimit | quote }}
{{- end -}}

{{/*
The image, by digest.

⚠ A DIGEST AND NEVER A TAG — docs/plan/18 § Platform security. A shell image resolved by tag would let
a registry change what every tenant's terminal is, silently, between two attaches of one session.
⚠ THE DIGESTS ARE PLACEHOLDERS. Nothing in this repository builds this image — see
conformance.yaml § owed, `no-image-pipeline` — and a plausible-looking digest would be a reference that
fails to pull with nothing in the tree to say why. CloudConsoles.ImageDigests holds the same two.
*/}}
{{- define "cloudshell.image" -}}
{{- $digests := dict
  "default" "sha256:0000000000000000000000000000000000000000000000000000000000000000"
  "minimal" "sha256:1111111111111111111111111111111111111111111111111111111111111111" -}}
{{- printf "%s@%s" .Values.imageRepository (get $digests .Values.image.variant | default (get $digests "default")) -}}
{{- end -}}

{{/*
The seven cybercloud.io/* labels — docs/plan/02 § ADR-013, "Every object carries ...".

⚠ `resource-type` is lower-cased with `/` replaced by `_`, matching KubeLabels.ResourceTypeValue in
src/CyberCloud.Kubernetes.Contracts.
*/}}
{{- define "cloudshell.platformLabels" -}}
cybercloud.io/tenant-id: {{ .Values.platform.tenantId | quote }}
cybercloud.io/subscription-id: {{ .Values.platform.subscriptionId | quote }}
cybercloud.io/resource-group: {{ .Values.platform.resourceGroup | quote }}
cybercloud.io/resource-id: {{ .Values.platform.resourceId | quote }}
cybercloud.io/resource-type: {{ .Values.platform.resourceType | replace "/" "_" | lower | quote }}
cybercloud.io/api-version: {{ .Values.platform.apiVersion | quote }}
cybercloud.io/managed-by: {{ .Values.platform.managedBy | quote }}
{{- end -}}
