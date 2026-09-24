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
| L7 (platform ingress) | **Envoy Gateway** (Gateway API) | Where Kubernetes ingress is going; ingress-nginx is the legacy path. Cilium's own Gateway API implementation is the alternative and is evaluated in ADR-019. ⚠ **Not the tenant L7** — `applicationGateways` runs HAProxy, see § Application gateway |
| L7 (tenant `applicationGateways`) | **HAProxy**, one pod on the tenant's subnet | ⚠ **Decided (#31)**: nothing to install, a pod the platform templates, addresses as backends, and `loadBalancers`' precedent — § Application gateway |
| WAF | **Coraza** (OWASP CRS) ~~as an Envoy filter~~ as HAProxy's SPOE agent, `coraza-spoa` | Apache-2.0, actively maintained, and its CI runs the CRS regression suite against HAProxy 3.2 |

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
  ├─ subnets/{name}          → prefix, gateway, DHCP (⚠ the NAT flag does nothing here — egress is natGateways)
  ├─ securityGroups/{name}   → rules; Cilium policies + Kube-OVN ACLs
  ├─ routeTables/{name}      → static routes, next-hop
  ├─ natGateways/{name}      → one subnet's egress through a publicIpAddresses resource (M2, shipped)
  ├─ applicationGateways/{name} → L7 routes + the OWASP CRS WAF on HAProxy (M2, shipped)
  └─ peerings/{name}         → VPC-to-VPC within a tenant, in one resource group (M2, shipped)
```

> ⚠ **`peerings` shipped as the first type that owns no object** (#31, on #89's co-owned apply). A
> Kube-OVN peering has no kind of its own: it is an entry in each network's `Vpc.spec.vpcPeerings`
> plus a static route per exchanged range in each network's `Vpc.spec.staticRoutes` — both arrays
> carry no `x-kubernetes-list-type`, so both are atomic under server-side apply, which is the
> `routeTables` refusal twice over and across two parents. The type writes each network's half as a
> **fragment** through [09 § A second writer on an object](09-kubernetes-fabric.md): the network's
> labels stay, every peering of one `Vpc` applies under one manager named for the network, each
> fragment is written down on the object beside the others' and merged with them on every apply, the
> live `resourceVersion` makes two peerings racing onto one `Vpc` lose loudly, and a peering's
> teardown withdraws its fragment and leaves both networks standing. The body is one name and three
> ranges — the remote network, what each side advertises, and a `/30` link the two peer ports meet
> on — and the reconciler refuses the three overlapping each other, by name and terminally. ⚠ **The
> remote is a name in the same resource group, not a resource id**, although the schema could have
> taken one: the write path authorizes the caller against the peering alone, and a resource group is
> the smallest scope on which write on the peering implies write on both networks —
> `charts/managed/kube-ovn-vpc-peering/conformance.yaml § owed`,
> `the-remote-must-be-in-the-same-resource-group`. ⚠ The qualified name is not what holds that
> boundary — hyphenated group and network names can render one `Vpc` name across two groups, which
> the #31 review showed — the co-owned apply's check of the live object's subscription and group
> labels is ([09 § A second writer on an object](09-kubernetes-fabric.md)). ⚠ **And what is proven is the write, not the
> routing.** No harness here has a Kube-OVN controller, so the peer ports and the routes are reasoned
> from `pkg/controller/vpc.go` and wait for the VM lane (#95): `routing-is-unproven-until-the-vm-lane`
> in the same file. The shared conformance suite grew the co-writer's reading of its ownership
> assertions to run this type at the network's exact count; the cluster-backed suite did not, and the
> peering's real-API-server class is a dedicated one — `the-shared-cluster-suite-presumes-ownership`.

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

~~L7 over Envoy Gateway~~ **L7 on HAProxy, with the Coraza WAF as its SPOE agent (#31, shipped as
`virtualNetworks/applicationGateways`)**. The plan's scope: listeners, host/path routes, TLS (with
cert-manager and our own ACME or an uploaded certificate from Vault), header rewrites, rate limits, and
the Coraza WAF with a rule-set/paranoia-level selection. ⚠ **2026-08-01 ships part of it** — listeners,
host/path routes, TLS from a certificate the tenant holds in Vault, and the WAF; ACME, header rewrites
and rate limits are owed (below, and `conformance.yaml § owed`). This paragraph claimed all of it until
#31's review.

> ⚠ **Decided (#31): HAProxy + Coraza SPOA, rendered directly, and not Envoy Gateway, Cilium's Gateway
> API or Caddy.** The measurement that owed this type (`charts/managed/haproxy/conformance.yaml § owed`,
> `application-gateway-is-not-an-http-mode-of-this-proxy`) assumed the Envoy Gateway named above and
> found its first deliverable was a bundle component. Four readings turned the choice instead.
>
> 1. **Nothing to install.** `charts/bundle` carries no Gateway API definitions, no Envoy Gateway and no
>    Cilium. Envoy Gateway is a controller, a CRD family and a certgen job in every cluster before it is
>    one tenant's proxy; HAProxy is an image `loadBalancers` already runs.
> 2. **The proxy has to be a pod the platform templates.** It sits on the tenant's subnet or it is
>    useless, which is two Kube-OVN annotations on the pod (`logical_switch`, `ip_pool`). A controller-
>    managed Envoy puts them there only through `GatewayNamespace` deploy mode and an `EnvoyProxy` pod
>    template, and every object is then owned by a controller — the CloudNativePG shape, with the
>    cluster-backed suite proving none of it. Rendered directly, the platform owns the pod, and a real
>    k3s runs it (below).
> 3. **Backends are addresses** — no Service and no DNS in a tenant VPC (#23) — which HAProxy takes
>    natively and Gateway API reaches only through Envoy Gateway's `Backend` extension, off by default
>    *"due to security considerations"*. Cilium's own Gateway API implementation is a *shared* Envoy per
>    node, the placement ADR-019 already calls "a harder place to put" per-tenant isolation and a WAF.
> 4. **The WAF.** HAProxy's own is HAProxy Enterprise's. `corazawaf/coraza-spoa` is the OWASP Coraza
>    project's agent for HAProxy's SPOE — Apache-2.0, Coraza v3 with the OWASP CRS compiled in — and its
>    CI runs the CRS regression suite (go-ftw) through HAProxy 2.8, 3.0 and 3.2 on every push
>    (`.github/workflows/ftw.yaml` at `v0.7.3`). Envoy's equivalent, `coraza-proxy-wasm`, needs the
>    module fetched into the proxy at runtime; Caddy's needs a custom `xcaddy` build the platform would
>    have to own.
>
> **What shipped.** One pod on the tenant's subnet — HAProxy `3.2.23-alpine` and coraza-spoa `0.7.3`,
> both **by digest** because they are a tested pair — the agent on the pod's loopback. Listeners are HTTP
> and, with a vault handle under the tenant's own prefix, HTTPS. Routes are **a list on the gateway**,
> `host/path=pool`, first match wins, a path matched on whole segments — not a child each, because on
> HAProxy the routing table is lines in one file and a child would be `routeTables`' refusal again.
> Pools are `pool=target:port`, where a target is an address or a `CyberCloud.Compute/virtualMachines`
> id resolved through #90's view to the address KubeVirt reports; container groups are not a published
> type and are refused by name. Health is an HTTP GET per member. The WAF policy is a mode (`off`,
> `detection`, `prevention`), a CRS version (the one the pinned agent embeds, 4.25), a paranoia level,
> rule exclusions and custom rules — every list in a closed grammar checked before rendering, so no
> tenant text reaches HAProxy's configuration or SecLang as text. In prevention an unanswering agent is
> a 503, never a pass. **Proven on a real k3s** by `ApplicationGatewayTrafficConformance`: routed by host
> and by path to real backend pods, HTTPS from the harness vault, a SQL-injection probe 403 in
> prevention and passed with `waf-rules:942100` on the gateway's log line in detection, custom denies
> holding for every spelling of a host or path routing treats as the same, a 503 from the gateway's own
> pod template with no agent in it, and a pool's member taken out when it stops answering its probe.
> The objects are named `{network}.{name}` — not the load balancer's `{network}-{name}`, which one name
> in one network made the same `Deployment` until #31's review (`ApplicationGatewayBesideALoadBalancerConformance`).
>
> ⚠ **Owed** — `charts/managed/application-gateway/conformance.yaml § owed`: it is private to the VPC
> until the inbound attachment lands (`charts/managed/kube-ovn-eip/conformance.yaml § owed`,
> `only-a-nat-gateway-can-be-given-an-address`), one replica, bodies inspected up to HAProxy's buffer,
> no response inspection, no header rewrites, rate limits or ACME, and — because flannel ignores the
> Kube-OVN annotations — the tenant-subnet half waits for #95's lane.

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
| `natGateways` | M2 | ~~Kube-OVN `VpcNatGateway` + an SNAT address.~~ ⚠ **Corrected (#31): a Kube-OVN `OvnSnatRule` naming the `OvnEip` that `publicIpAddresses` renders**, shipped as `virtualNetworks/natGateways`. A `VpcNatGateway` is a StatefulSet pod whose rules name an `IptablesEIP` — a second public-address kind this platform does not allocate — while an `OvnSnatRule` is one NAT row on the VPC's own router, with no pod and no second allocator. ⚠ And it is a tenant subnet's *only* egress: the `natOutgoing` flag on `subnets` is honored by Kube-OVN's node gateway for the default VPC alone (`isSubnetNeedNat` requires `subnet.Spec.Vpc == ClusterRouter`), so on a tenant VPC it is accepted and does nothing. Needed the moment a private subnet wants outbound |
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

> ⚠ **Owed (#31), and there is nothing for a `Network` resource type to render** —
> `charts/managed/kube-ovn-vpc/conformance.yaml § owed`, `flow-logs-have-nothing-to-render`. The
> paragraph above names Hubble, and `charts/bundle` installs no Cilium: Kube-OVN is the bundle's CNI
> and the only one, so a bundle-built cluster has no Hubble to read, and whether Hubble would see
> OVN-switched tenant traffic at all is unmeasured. What the substrate itself exposes was read at
> v1.16.2: `SubnetSpec` has 41 fields and none of them exports a flow; `acls[]` is
> `{direction, priority, match, action}` with no log field; the OVN ACLs Kube-OVN *does* log are
> NetworkPolicy's (`ENABLE_NP=false` here), AdminNetworkPolicy's, and a `private: true` subnet's
> default drop — never a security group's — and the lines land in each node's `ovn-controller` log;
> mirroring (`--enable-mirror` on `kube-ovn-cni`, or `ovn.kubernetes.io/mirror: "true"` on a pod)
> copies packets to a node NIC for `tcpdump`; and OVS's own IPFIX is per-node `ovs-vsctl` state on
> nodes [ADR-020](02-technology-decisions.md) gives no shell. So a flow log is a *collector* the
> bundle does not carry yet plus [16](16-observability.md)'s pipeline plus a query view —
> [01](01-azure-parity-catalogue.md) files it under `CyberCloud.Monitor` at M3 — and not a chart under
> `charts/managed`.
>
> ⚠ **Re-read for #31 on 2026-09-23, and still owed — the same file and row.** Kube-OVN v1.16.7 (the
> bundle pins v1.16.2) adds an experimental `--enable-acl-sampling`: OVN samples on **NetworkPolicy**
> ACLs, delivered per node through a kernel psample group to a debug command — nothing on this
> platform, where `ENABLE_NP=false` removes those ACLs and a security group's are not sampled. Hubble
> is still unmeasured against OVN-switched tenant pods, because no harness carries Cilium. What a
> tenant **does** get now is the application gateway's per-request log line — client, pool member,
> timings, status and the WAF's verdict — which is an L7 record of traffic *through a gateway* and no
> more.

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
