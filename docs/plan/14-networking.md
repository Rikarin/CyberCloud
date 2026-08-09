# 14 — Networking

The layer where "an Azure-like cloud" stops being a control-plane exercise. Everything here is real
packets on real hardware, and the honest framing is that **the software is the easy half**.

## Substrate

| Layer | Choice | Why |
|---|---|---|
| Node/pod CNI, service datapath, policy, observability | **Cilium**, as the **primary** CNI | eBPF datapath, kube-proxy replacement, network policy, Hubble flow logs, Gateway API. ADR-019 |
| Tenant VPC | **Kube-OVN**, alongside — `ENABLE_LB=false`, `ENABLE_NP=false` | The only thing with a real multi-tenant VPC model: `Vpc`, `Subnet`, `VpcNatGateway`, EIP/FIP, per-VPC routing. It provides *tenant* networking, not cluster networking. ADR-019 |
| Platform service VIPs | **Cilium LB-IPAM + BGP Control Plane** | Replaces MetalLB where BGP is available. ADR-019 |
| Tenant public addresses | **Kube-OVN EIP / FloatingIP / VpcNatGateway** | Never MetalLB — an address inside an OVN logical router is not something MetalLB can model |
| ⚠ Fallback where the fabric is L2-only | **MetalLB, L2 mode** | Not Cilium L2 Announcements. ADR-019 explains why |
| L7 | **Envoy Gateway** (Gateway API) | Where Kubernetes ingress is going; ingress-nginx is the legacy path. Cilium's own Gateway API implementation is the alternative and is evaluated in ADR-019 |
| WAF | **Coraza** (OWASP CRS) as an Envoy filter | Apache-2.0, actively maintained |

> ⚠ **Corrected.** An earlier draft of this table had Kube-OVN as the primary CNI with Cilium chained
> on top as a policy layer, and cited Cozystack for it. That is not what Cozystack runs — their
> `cilium` values carry `kubeProxyReplacement: true`, `ipam.mode: kubernetes`, `nodePort.enabled`,
> `externalIPs.enabled`, `gatewayAPI.enabled` and an Envoy DaemonSet, which is Cilium *as the CNI*;
> and their `kube-ovn` values carry `ENABLE_LB: false`, `ENABLE_NP: false` with
> `CNI_CONFIG_PRIORITY: "10"`. Read firsthand from the repository, and it changes which component
> owns the service datapath — which is exactly the question "can Cilium replace MetalLB" turns on.

## Virtual networks — `CyberCloud.Network/virtualNetworks` · M1 · 2.5 EM

A tenant's VPC is a Kube-OVN `Vpc`; subnets are `Subnet`s bound to it.

```
virtualNetworks/{name}
  ├─ addressSpace: [10.20.0.0/16]
  ├─ subnets/{name}          → prefix, gateway, DHCP, NAT flag
  ├─ securityGroups/{name}   → rules; Cilium policies + Kube-OVN ACLs
  ├─ routeTables/{name}      → static routes, next-hop
  └─ peerings/{name}         → VPC-to-VPC within a tenant (M3)
```

**Address space is the tenant's problem and the platform's constraint.** Overlapping CIDRs between a
tenant's VPCs is fine; overlapping with the platform's underlay is not. The API validates against a
per-region reserved list and rejects with the conflicting range named.

⚠ **The isolation claim needs to be precise.** Kube-OVN gives per-VPC L3 isolation with separate
routing tables and overlapping address spaces — genuine tenant separation at the network layer. What it
does **not** give is a hardware boundary; a kernel bug in OVS is a cross-tenant risk. For tenants who
need more, the answer is a dedicated cluster on dedicated hardware, which the brief already
contemplates, and the marketing must not claim more than the substrate delivers.

## DNS — `CyberCloud.Network/dnsZones` · M1 · 1.5 EM

Public authoritative and private zones, one resource type, `zoneType` distinguishes them.

- **Backend: PowerDNS** with a custom backend reading from a grain-fed projection, or CoreDNS with the
  same. PowerDNS wins on DNSSEC, AXFR and operational maturity.
- Record sets are sub-resources (`dnsZones/{zone}/A/{name}`), each an ordinary resource with the usual
  authorization and audit.
- Private zones are linked to VPCs and resolved by the VPC's resolver only.
- DNSSEC signing is on by default for public zones; key management in Vault.
- **Zone apex uniqueness is global**, which makes `IDnsZoneIndexGrain` a null-tenant index grain in the
  global cluster ([04](04-orleans-topology.md)) — one of very few things that genuinely must be.

⚠ **The provider is 1.5 EM; the operations are the cost.** Running public authoritative DNS means
anycast nameservers, DDoS absorption, and being the reason a customer's whole business is offline when
it breaks. The decision to make before M1 is whether we run it or front a wholesale provider — and
either is defensible, but pretending the software is the whole job is not.

## Load balancing — `CyberCloud.Network/loadBalancers` · M1 · 0.8 EM

L4. An address from the tenant's pool plus a `Service type=LoadBalancer` (announced per ADR-019) or an
HAProxy deployment for TCP with health checks and connection limits. Backend pools reference resource
ids (a VM, a scale set, a cluster's node pool), resolved by the reconciler into endpoints.

⚠ **Which allocator serves this depends on where the address lives**, and the resource type hides the
difference: an address on the *platform's* fabric comes from Cilium LB-IPAM; an address inside a
**tenant VPC** is a Kube-OVN `IptablesEIP`/`OvnEip` bound to that VPC's router, because a VPC address
is not reachable from the host network namespace at all. Both surface as
`CyberCloud.Network/publicIpAddresses` and the reconciler picks. Assuming one allocator for both is
the mistake this note exists to prevent.

## Application gateway — `CyberCloud.Network/applicationGateways` · M2 · 2.0 EM

L7 over Envoy Gateway: listeners, host/path routes, TLS (with cert-manager and our own ACME or an
uploaded certificate from Vault), header rewrites, rate limits, and the Coraza WAF with a
rule-set/paranoia-level selection.

## VPN — `CyberCloud.Network/vpnGateways` · M1 · 1.5 EM

**WireGuard**, per the brief.

- **Point-to-site:** a gateway resource plus `vpnClients` sub-resources, each with its own keypair
  (private key generated client-side where possible; where the portal generates it, it is shown once
  and never stored). Config and QR code downloadable.
- **Site-to-site:** peer definitions with allowed IPs and endpoints, into the tenant's VPC routing.
- **IPsec/IKEv2** via strongSwan as a second `protocol` value — M3, and only because enterprise
  equipment often speaks nothing else.

⚠ **Do not copy Cozystack's VPN choice.** It ships Outline/Shadowsocks, which is a censorship-circumvention
tool with different threat model, different traffic shape and different legal exposure. A corporate VPN
and a circumvention proxy are not the same product and conflating them would be a serious mistake.

## Private endpoints — `CyberCloud.Network/privateEndpoints` · M3 · 1.5 EM

A managed service reachable from a tenant's VPC over private addressing, with no public exposure. In
Kube-OVN terms: a service in the consumer's VPC whose backend is routed into the producer's namespace,
brokered by the control plane so both sides consent.

This is the feature that makes managed services usable by customers with a compliance department, and
it is the reason [12](12-managed-data-services.md) defaults external exposure to off — the private
path must be the good path, not the awkward one.

## Everything else

| Resource | M | Notes |
|---|---|---|
| `natGateways` | M2 | Kube-OVN `VpcNatGateway` + an SNAT address. Needed the moment a private subnet wants outbound |
| `publicIpAddresses` | M1 | ⊂ the VPC provider; a metered, quota'd, allocatable resource in its own right — because IPv4 is scarce and must be accounted |
| `firewallPolicies` | M3 | Centralised egress filtering |
| `trafficManagerProfiles` | M3 | DNS-based failover over our own DNS |
| `cdnProfiles` | M3 | Cozystack's `http-cache` (nginx) at each PoP. ⚠ Called a caching proxy until there are PoPs; calling it a CDN before then is a lie with a support cost |
| `frontDoors` | ✗ | Anycast + a global PoP footprint. Physical, not software |
| `expressRoute` | ✗ | Carrier interconnect |
| DDoS protection | ✗ as a product | Bought upstream, exposed as a status flag, never as a capability we implement |

## IPv6

**Dual-stack from day one, not retrofitted.** Kube-OVN supports it, KubeVirt supports it, and adding
IPv6 to a live network model is the kind of migration that takes a year. Every subnet may carry a v4
prefix, a v6 prefix, or both; every load balancer and gateway takes both families.

⚠ The cost is that every provider's connectivity code handles two families and every firewall rule set
is two rule sets. That is real and it is much smaller than the retrofit.

## Observability

Hubble flow logs into ClickHouse, per tenant, with a retention that is a plan attribute. It is the
data behind connection troubleshooting, security review and egress billing — three features from one
pipeline, which is why it is worth the volume.

⚠ Flow logs are the highest-cardinality data in the platform. Sampling is on by default above a rate
threshold and the sampling rate is visible in the UI, because a silently sampled flow log is a
debugging trap.

## Effort

| Piece | M | EM |
|---|---|---|
| VPC, subnets, security groups, route tables, public IPs, dual-stack | M1 | 2.5 |
| DNS zones + records + DNSSEC | M1 | 1.5 |
| L4 load balancers | M1 | 0.8 |
| WireGuard VPN | M1 | 1.5 |
| Application gateway + WAF | M2 | 2.0 |
| NAT gateways, peering | M2 | 0.8 |
| Private endpoints | M3 | 1.5 |
| Flow logs + troubleshooting views | M2 | 0.8 |
| **Total** | | **11.4** |
