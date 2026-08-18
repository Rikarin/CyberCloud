{{/*
The Kubernetes object's name.

⚠ THREE COMPONENTS, AND HERE THE THIRD ONE IS THE ONLY THING KEEPING TWO NETWORKS APART.
`NetworkSecurityGroups.ObjectNameOf` is `{namespace}-{network}-{name}` and the reconciler computes it
from the resource's ADDRESS, because that is the only place the network's name lives. A Subnet at
least carries `spec.vpc`, so a name collision there would be visible in the object; a SecurityGroup
has NO field naming its network, so a collision would merge two tenants' rule sets into one OVN port
group with nothing reporting an error.
*/}}
{{- define "kube-ovn-security-group.objectName" -}}
{{- default .Release.Name .Values.nameOverride | trunc 253 | trimSuffix "-" -}}
{{- end -}}

{{/*
One direction's rules, expanded.

⚠ THIS IS THE HELM TWIN OF `NetworkSecurityGroups.Rules`, AND THE TWO HAVE TO AGREE. The expansion is
a CROSS PRODUCT: for each family whose remote is set, one rule per TCP entry, then one rule per UDP
entry, then one ICMP rule if asked. So two remotes with `tcpPorts: 80,443` is four rules.
`NetworkSecurityGroupTests` pins the arithmetic on the C# side and `POST …/showEffectiveRules`
publishes it, because a tenant should not have to do it in their head to know what they opened.

⚠ A REMOTE WITH NO PROTOCOLS YIELDS NOTHING, deliberately. The alternative — reading "a remote and no
ports" as `protocol: all` — would make a half-typed body the most permissive one this type can
express. charts/managed/kafka's `allowedCidrs` settled the same question the same way.

⚠ `splitList "," ""` RETURNS A ONE-ELEMENT LIST HOLDING THE EMPTY STRING, which is why every loop
below guards on the entry being non-empty. Without the guard an empty port list renders one rule with
no ports, which is a rule that allows the whole protocol.

⚠ ORDER IS FIXED — v4 before v6, TCP before UDP before ICMP, entries in declaration order — because
both arrays are ATOMIC under server-side apply and `Matches` compares them element by element. A
renderer that reordered would report drift on a converged group forever.
*/}}
{{- define "kube-ovn-security-group.rules" -}}
{{- $section := .section -}}
{{- $remotes := list -}}
{{- if $section.remoteV4 -}}
{{- $remotes = append $remotes (dict "family" "ipv4" "cidr" $section.remoteV4) -}}
{{- end -}}
{{- if $section.remoteV6 -}}
{{- $remotes = append $remotes (dict "family" "ipv6" "cidr" $section.remoteV6) -}}
{{- end -}}
{{- range $remote := $remotes }}
{{- range $entry := splitList "," $section.tcpPorts }}
{{- if $entry }}
- ipVersion: {{ $remote.family | quote }}
  protocol: "tcp"
  priority: 1
  remoteType: "address"
  remoteAddress: {{ $remote.cidr | quote }}
  policy: "allow"
  portRangeMin: {{ splitList "-" $entry | first | int }}
  portRangeMax: {{ splitList "-" $entry | last | int }}
{{- end }}
{{- end }}
{{- range $entry := splitList "," $section.udpPorts }}
{{- if $entry }}
- ipVersion: {{ $remote.family | quote }}
  protocol: "udp"
  priority: 1
  remoteType: "address"
  remoteAddress: {{ $remote.cidr | quote }}
  policy: "allow"
  portRangeMin: {{ splitList "-" $entry | first | int }}
  portRangeMax: {{ splitList "-" $entry | last | int }}
{{- end }}
{{- end }}
{{- if $section.allowIcmp }}
- ipVersion: {{ $remote.family | quote }}
  protocol: "icmp"
  priority: 1
  remoteType: "address"
  remoteAddress: {{ $remote.cidr | quote }}
  policy: "allow"
{{- end }}
{{- end }}
{{- end -}}

{{/*
The seven cybercloud.io/* labels — docs/plan/02 § ADR-013, "Every object carries ...".

⚠ `resource-type` is lower-cased with `/` replaced by `_`, matching KubeLabels.ResourceTypeValue. A
CHILD type path has TWO slashes — `cybercloud.network_virtualnetworks_securitygroups` — so the
replacement must be a replace-ALL. Helm's `replace` already is one; stating it is the point, because
a single-replacement spelling would be refused at admission, per object, rather than at lint time.
*/}}
{{- define "kube-ovn-security-group.platformLabels" -}}
cybercloud.io/tenant-id: {{ .Values.platform.tenantId | quote }}
cybercloud.io/subscription-id: {{ .Values.platform.subscriptionId | quote }}
cybercloud.io/resource-group: {{ .Values.platform.resourceGroup | quote }}
cybercloud.io/resource-id: {{ .Values.platform.resourceId | quote }}
cybercloud.io/resource-type: {{ .Values.platform.resourceType | replace "/" "_" | lower | quote }}
cybercloud.io/api-version: {{ .Values.platform.apiVersion | quote }}
cybercloud.io/managed-by: {{ .Values.platform.managedBy | quote }}
{{- end -}}
