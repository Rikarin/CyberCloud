{{/*
The Kubernetes object-name stem — the release's name unless overridden.
*/}}
{{- define "agent.name" -}}
{{- default .Release.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{/*
The labels every object here carries.

⚠ NOT the seven cybercloud.io/* labels of ADR-013. Those mark objects the PLATFORM applied into a
cluster it manages, and they are what its informers select on and its billing joins against. Nothing
in this chart is such an object: the tenant applied it, into a cluster the platform reaches only
through the pod this chart runs. Stamping managed-by=cybercloud here would make the agent's own
Deployment look like a resource the platform owes a reconcile for.
*/}}
{{- define "agent.labels" -}}
app.kubernetes.io/name: cybercloud-agent
app.kubernetes.io/instance: {{ .Release.Name | quote }}
app.kubernetes.io/component: agent
app.kubernetes.io/part-of: cybercloud
app.kubernetes.io/managed-by: {{ .Release.Service | quote }}
cybercloud.io/connected-cluster: {{ .Values.cluster.id | quote }}
{{- end -}}

{{/*
The selector — stable across upgrades, so a Deployment's selector never changes.
*/}}
{{- define "agent.selectorLabels" -}}
app.kubernetes.io/name: cybercloud-agent
app.kubernetes.io/instance: {{ .Release.Name | quote }}
{{- end -}}

{{/*
The Secret holding the enrollment token, and the path it is mounted at.

⚠ THE PATH IS ALSO IN C# — AgentOptions.EnrollmentTokenFile defaults to it — and the Deployment
passes it explicitly anyway, so a change here is a change the agent is told about.
*/}}
{{- define "agent.enrollmentSecretName" -}}
{{- printf "%s-enrollment" (include "agent.name" .) | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "agent.enrollmentTokenPath" -}}
/var/run/cybercloud/enrollment-token
{{- end -}}
