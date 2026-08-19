{{/*
The Kubernetes object-name stem — one name for both objects.

⚠ TWO COMPONENTS RATHER THAN THE THREE THIS FAMILY'S OTHER CHILDREN NEED, BECAUSE THESE OBJECTS ARE
NAMESPACED. `LoadBalancers.ObjectNameOf` is `{network}-{name}` and the reconciler computes it: a
subscription and a resource group are already folded into the namespace the object lands in, which is
exactly the separation kube-ovn-subnet has to add by hand for a CLUSTER-SCOPED Subnet. What a
namespace does not separate is two networks in one resource group, which is why the parent's name is
still a component.
*/}}
{{- define "haproxy.objectName" -}}
{{- default .Release.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{/*
The Kube-OVN Subnet the proxy pod is placed on.

⚠ THE ONLY SEAM BY WHICH ANYTHING THIS PLATFORM RUNS IS *INSIDE* A TENANT'S NETWORK, and the name has
to be the one kube-ovn-subnet rendered — `{namespace}-{network}-{subnet}`. `LoadBalancers.LogicalSwitchOf`
composes it through NetworkSubnets.ObjectNameOf so there is one spelling in C#; this is the second
spelling and NetworkLoadBalancerTests compares them. A switch name that does not exist is a pod that
stays Pending with a CNI error; a switch name that belongs to somebody else is worse, which is why the
namespace and the network come from `platform` — written by the reconciler from the resource's own
address — rather than from a tenant-settable value.
*/}}
{{- define "haproxy.logicalSwitch" -}}
{{- printf "%s-%s-%s" .Values.platform.namespace .Values.platform.virtualNetwork .Values.subnet -}}
{{- end -}}

{{/*
The address the proxy answers on, as Kube-OVN's `ip_pool` annotation wants it.

⚠ COMMA-JOINED, WHICH KUBE-OVN READS AS *ONE* DUAL-STACK ADDRESS RATHER THAN AS TWO SERVERS.
pkg/controller/pod.go's `acquireStaticAddressHelper` splits the annotation on commas and folds a
two-entry list of DIFFERENT families into a single dual-stack address. A semicolon, which the same
function also accepts, would mean two single-stack addresses for two pods and would leave this proxy's
v6 half unallocated.
*/}}
{{- define "haproxy.addressPool" -}}
{{- if .Values.frontend.v6 -}}
{{- printf "%s,%s" .Values.frontend.v4 .Values.frontend.v6 -}}
{{- else -}}
{{- .Values.frontend.v4 -}}
{{- end -}}
{{- end -}}

{{/*
The image.

⚠ A TAG AND NOT A DIGEST, WHICH IS THE OPPOSITE OF cloud-shell's CHOICE AND IS ARGUED IN
conformance.yaml § owed, `the-image-is-a-tag-and-not-a-digest`: that image is the platform's own and
this one is Docker's `library/haproxy`, so a digest here would have to be bumped in this repository for
every HAProxy patch release. `-alpine` because the two variants are the same HAProxy and this one is a
fifth of the size. Both tags were resolved against the registry — see SOURCE.
*/}}
{{- define "haproxy.image" -}}
{{- printf "%s:%s-alpine" .Values.imageRepository .Values.version -}}
{{- end -}}

{{/*
What one proxy costs.

⚠ THE SAME TABLE IS IN C# — `LoadBalancers.Presets` — and NetworkLoadBalancerTests compares it row for
row. Two spellings of a sizing table is a resource that reserves one quantity through
QuotaMeter.Vcpu and runs another.

⚠ THE LADDER IS SHORT AND STARTS LOW ON PURPOSE. An L4 TCP proxy is almost all kernel work; the small
row carries far more than its size suggests, and the large row exists for many long-lived connections
rather than for throughput.
*/}}
{{- define "haproxy.resources" -}}
{{- $presets := dict
  "c1.small"  (dict "cpu" "250m" "memory" "256Mi")
  "c1.medium" (dict "cpu" "500m" "memory" "512Mi")
  "c1.large"  (dict "cpu" "1"    "memory" "1Gi") -}}
{{- $chosen := get $presets .Values.sizing.preset | default (get $presets "c1.small") -}}
requests:
  cpu: {{ $chosen.cpu | quote }}
  memory: {{ $chosen.memory | quote }}
limits:
  cpu: {{ $chosen.cpu | quote }}
  memory: {{ $chosen.memory | quote }}
{{- end -}}

{{/*
The proxy's configuration.

⚠ `mode tcp` AND NOT `mode http`, WHICH IS THE WHOLE OF WHAT THIS ROW CLAIMS. docs/plan/14 puts L7 —
host and path routing, TLS termination, header rewrites, a WAF — on `applicationGateways` at M2, over
Envoy. An HTTP-mode HAProxy here would be a second, quieter L7 product with none of those.

⚠ THE GLOBAL CONNECTION LIMIT IS TWICE THE FRONTEND'S, because HAProxy counts both sides of a proxied
connection against `maxconn`: a global limit equal to the frontend's presents as a proxy that stalls at
exactly half its configured limit.

⚠ `log stdout format raw` AND NEVER `log /dev/log`, which almost every HAProxy example carries. The
upstream image has no syslog daemon and that socket does not exist in a container, so HAProxy would
start and log nothing at all.

⚠ THE BRACKETS AROUND AN IPv6 BACKEND ARE NOT COSMETIC. `server s1 fd00::11:8080` is ambiguous to
HAProxy's own parser and it refuses to start — one backend written in the other family would take the
whole load balancer down.

⚠ THIS IS THE SECOND SPELLING OF `LoadBalancers.HaproxyConfig`, and the pod template's checksum is
taken over whichever one rendered it. The two must agree line for line or a chart install and a
platform reconcile produce proxies that behave differently under one resource type.
*/}}
{{- define "haproxy.config" -}}
# Generated by CyberCloud from CyberCloud.Network/virtualNetworks/loadBalancers.
# Edits are overwritten on the next reconcile pass.
global
  log stdout format raw local0 info
  maxconn {{ mul .Values.limits.maxConnections 2 }}

defaults
  mode tcp
  log global
  option tcplog
  option dontlognull
  timeout connect 5s
  timeout client 60s
  timeout server 60s

frontend inbound
  bind :{{ .Values.frontend.port }}
  maxconn {{ .Values.limits.maxConnections }}
  default_backend workloads

backend workloads
  balance roundrobin
  option tcp-check
{{- range $index, $address := splitList "," .Values.backend.addresses }}
{{- $target := trim $address }}
  server s{{ add1 $index }} {{ if contains ":" $target }}[{{ $target }}]{{ else }}{{ $target }}{{ end }}:{{ $.Values.backend.port }} check inter {{ mul $.Values.health.intervalSeconds 1000 }}ms rise {{ $.Values.health.healthyAfter }} fall {{ $.Values.health.unhealthyAfter }} maxconn {{ $.Values.limits.maxConnections }}
{{- end }}
{{- end -}}

{{/*
The seven cybercloud.io/* labels — docs/plan/02 § ADR-013, "Every object carries ...".

⚠ `resource-type` is lower-cased with `/` replaced by `_`, matching KubeLabels.ResourceTypeValue. This
type's path has TWO slashes, because it is a child.
*/}}
{{- define "haproxy.platformLabels" -}}
cybercloud.io/tenant-id: {{ .Values.platform.tenantId | quote }}
cybercloud.io/subscription-id: {{ .Values.platform.subscriptionId | quote }}
cybercloud.io/resource-group: {{ .Values.platform.resourceGroup | quote }}
cybercloud.io/resource-id: {{ .Values.platform.resourceId | quote }}
cybercloud.io/resource-type: {{ .Values.platform.resourceType | replace "/" "_" | lower | quote }}
cybercloud.io/api-version: {{ .Values.platform.apiVersion | quote }}
cybercloud.io/managed-by: {{ .Values.platform.managedBy | quote }}
{{- end -}}

{{/*
The two labels the Deployment selects its own pods by.

⚠ ADR-013's SEVEN ARE NOT THE SELECTOR, AND THAT IS DELIBERATE. `KubeCommand` injects them into the
object it applies — the Deployment — and NOT into `spec.template.metadata.labels`, which is a
different object's labels. A selector naming a label nothing puts on the pod is a Deployment that
creates pods forever and never counts one as its own. `LoadBalancers.DeploymentJson` writes the same
two.
*/}}
{{- define "haproxy.selectorLabels" -}}
app.kubernetes.io/name: haproxy
app.kubernetes.io/instance: {{ include "haproxy.objectName" . | quote }}
{{- end -}}
