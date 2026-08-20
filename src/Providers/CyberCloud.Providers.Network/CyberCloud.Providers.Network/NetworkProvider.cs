// ⚠ For `Result<decimal>`, which the load balancer's quota derivations return.
// `CyberCloud.Core.Resources` is global in this assembly and `CyberCloud.Core` itself is not; the
// `ErrorCode` alias in GlobalUsings still wins over the `Orleans.ErrorCode` this import would
// otherwise put back in play — the same note ContainerRegistryProvider carries.
using CyberCloud.Core;

namespace CyberCloud.Providers.Network;

/// <summary>
///     Tenant networking — a virtual network and the subnets inside it, on Kube-OVN.
/// </summary>
/// <remarks>
///     <para>
///         <b>docs/plan/14 § Virtual networks, M1 · 2.5 EM.</b> A tenant's VPC is a Kube-OVN
///         <c>Vpc</c> and subnets are <c>Subnet</c>s bound to it. ADR-019 puts Kube-OVN alongside
///         Cilium — <c>ENABLE_LB=false</c>, <c>ENABLE_NP=false</c> — because it provides <i>tenant</i>
///         networking rather than cluster networking, and this provider is the tenant-facing half of
///         that boundary.
///     </para>
///     <para>
///         ⚠ <b>WHAT LANDED AND WHAT DID NOT, PER TYPE, BECAUSE A PROVIDER THAT SHIPS TWO OF SEVEN
///         TYPES SHOULD SAY SO WHERE THE TYPES ARE DECLARED.</b>
///     </para>
///     <list type="bullet">
///         <item>
///             <b><c>virtualNetworks</c> — shipped.</b> The VPC itself, with a declared address space
///             checked against the region's reserved list.
///         </item>
///         <item>
///             <b><c>virtualNetworks/subnets</c> — shipped.</b> The range addresses are actually
///             allocated from, dual-stack, with NAT and isolation switches.
///         </item>
///         <item>
///             ⚠ <b><c>virtualNetworks/routeTables</c> — REFUSED, and docs/plan/14 is wrong to ask
///             for it against this substrate.</b> The full argument is on
///             <see cref="VirtualNetworks" />; the short form is that Kube-OVN defines 25
///             <c>kubeovn.io/v1</c> kinds and <b>none of them is a route table</b> — a "route table"
///             there is a bare string name referenced from <c>Vpc.spec.staticRoutes[].routeTable</c>,
///             with no object, no lifecycle and nothing to observe. A <c>routeTables</c> resource
///             would have to write into its parent's <c>Vpc.spec.staticRoutes</c>, which carries no
///             <c>x-kubernetes-list-type</c> and is therefore <b>atomic under server-side apply</b>:
///             two route tables in one network would each converge by erasing the other. ⚠ And a
///             static route is <c>{cidr, nextHop, policy}</c>, which
///             <c>SchemaProperty.ElementKind</c> refuses outright — <i>"an array element is a
///             scalar"</i> — so the body shape has nowhere to put one either. <b>A type in the
///             registry with no reconciler answers 202 and converges nothing</b>, and a type whose
///             two instances silently delete each other is worse than that, so it is not declared.
///         </item>
///         <item>
///             <b><c>virtualNetworks/securityGroups</c> — SHIPPED, and the blocker it was owed for is
///             solved by reshaping rather than disputed.</b> The blocker was real:
///             <c>SecurityGroupSpec</c> is <c>ingressRules</c>/<c>egressRules</c> of
///             <c>SecurityGroupRule{ipVersion, protocol, priority, remoteType, remoteAddress,
///             portRangeMin, portRangeMax, policy}</c> — an <b>array of objects</b>, which
///             <c>SchemaProperty.ElementKind</c> still refuses and which this type does not send
///             through a schema. What changed is the question. docs/plan/14 asks for a security group,
///             not for a JSON transcription of Kube-OVN's rule struct, and a security group is
///             expressible in scalars: <b>a group is one coherent allow-set and a port carries
///             several</b>, which is the substrate's own composition model — a port's
///             <c>…kubernetes.io/security_groups</c> annotation is a comma-separated list of group
///             names. So the arity a single group needs is one, the remote is a v4 slot and a v6 slot
///             (<c>addressSpace</c>'s shape, so <c>Cidr.V4Pattern</c> stays declarable), and the ports
///             are one patterned string per protocol — which validates <i>more</i> at the API than an
///             array of numbers could, because bounds are property constraints and there is no
///             element-bounds member. ⚠ <b>The costs are real and are written down</b> on
///             <see cref="NetworkSecurityGroups" /> and at
///             <c>charts/managed/kube-ovn-security-group/conformance.yaml § owed</c>: no
///             <c>protocol: all</c>, no security-group remote, no <c>drop</c> rule, and one group
///             cannot pair different ports with different remotes.
///         </item>
///         <item>
///             <b><c>publicIpAddresses</c> — SHIPPED, and it is the first type in the platform that
///             draws <see cref="QuotaMeter.PublicIps" />.</b> That meter has existed since
///             <c>QuotaGrain</c>'s defaults — 20 per subscription — and nothing had ever drawn it,
///             because every provider that reached for it wanted a <i>conditional</i> draw and
///             <c>QuotaGrain.TryReserveAsync</c> refuses a non-positive amount by name. An address
///             resource draws exactly one, unconditionally, and
///             <c>ResourceManagerService.AmountFor</c> answers <c>meter.Fallback ?? 1m</c> for an
///             empty <c>AmountPointer</c>, so <c>.Meters(QuotaMeter.PublicIps, QuotaMeter.Resources)</c>
///             is <b>flat</b> — no pointer, no fallback, no <c>MeterDerivation</c>.
///             ⚠ <b>What keeps it unconditional is an absence</b>: there is no <c>ipVersion</c>
///             property, because the families an EIP gets are the external pool's rather than the
///             tenant's, so a body that drew <i>zero</i> scarce addresses is not expressible.
///             <para>
///                 ⚠ <b>IT IS THE FIRST TYPE IN THIS FAMILY THAT IS NOT A CHILD, AND THE SUBSTRATE
///                 IS WHY.</b> An <c>OvnEip</c> names no VPC: it is allocated from the operator's
///                 external subnet and attached to a tenant's routing domain later, by a separate NAT
///                 object. So an unattached address is <b>inert</b>, which is this type's answer to
///                 the "what does an empty body expose" question — the second time in this family
///                 that answer has come back safe.
///             </para>
///             <para>
///                 ⚠ <b>AND IT IS THE FIRST TYPE IN THE TREE WITH NO MUTABLE PROPERTY.</b> Read
///                 firsthand in <c>pkg/controller/ovn_eip.go</c> at <c>v1.16.2</c>:
///                 <c>handleUpdateOvnEip</c> refuses a changed <c>v4Ip</c>, <c>v6Ip</c>,
///                 <c>macAddress</c> and <c>type</c> — four errors, one per field, each beginning
///                 <i>"not support change"</i> — and <c>handleAddOvnEip</c> returns early once
///                 <c>status.macAddress</c> is set. The shared conformance suite requires a
///                 <c>ChangedBody</c> that reaches the cluster and cannot express a type that has
///                 none; what that costs is
///                 <c>charts/managed/kube-ovn-eip/conformance.yaml § owed</c>,
///                 <c>an-allocated-address-cannot-be-changed</c>.
///             </para>
///             <para>
///                 ⚠ <b>NO SOFT-DELETE WINDOW, BECAUSE THE OBVIOUS READING IS BACKWARDS.</b> The
///                 tempting sentence is that releasing a scarce address is exactly what a recovery
///                 window is for. What <c>SupportsSoftDelete</c> <i>does</i> today is park the name in
///                 the index <b>and withhold the committed quota until a purge</b> —
///                 <c>OperationGrain</c> returns <c>CommittedQuota</c> only when the operation is a
///                 delete that is <i>not</i> soft — while <c>RestoreAsync</c> and <c>PurgeAsync</c>
///                 have <b>no HTTP route</b>. So a window here would hold a tenant's
///                 <c>PublicIps</c> allowance — 20 by default — against addresses they deleted, for
///                 the whole window, with no way to recover one and no way to release one early. That
///                 is a one-way quota leak on the platform's scarcest meter. <b>The window becomes
///                 right the day a purge route exists</b>, and not before.
///             </para>
///         </item>
///         <item>
///             ⚠ <b><c>dnsZones</c> — OWED, AND THE BLOCKER IS NOT THE PROVIDER.</b> docs/plan/14
///             § DNS asks for <i>"public authoritative and private zones, one resource type,
///             <c>zoneType</c> distinguishes them"</i> over PowerDNS <i>"with a custom backend reading
///             from a grain-fed projection"</i>. Three things were checked before deciding not to
///             declare it, and each of them alone is enough:
///             <list type="number">
///                 <item>
///                     <b>There is no DNS server anywhere in this repository.</b> No chart in
///                     <c>charts/managed</c> and no provider serves one, so a <c>dnsZones</c>
///                     reconciler would have nothing to converge onto — <i>"a type in the registry
///                     with no reconciler answers 202 and converges nothing"</i>, one step worse.
///                 </item>
///                 <item>
///                     <b>Global apex uniqueness has no seam, and docs/plan/14 knows it.</b> That
///                     document requires <i>"zone apex uniqueness is global, which makes
///                     <c>IDnsZoneIndexGrain</c> a null-tenant index grain in the global cluster —
///                     one of very few things that genuinely must be"</i>. <c>IResourceIndexGrain</c>
///                     is keyed on <c>GrainKeys.PathIndex(address)</c> and is reached through
///                     <c>ForTenant</c>, so every uniqueness it can enforce is <i>within</i> a
///                     tenant. <c>IEmailIndexGrain</c> is the shape a global one would take and it
///                     does not exist for zones. Adding it is <c>CyberCloud.Tenancy</c> and
///                     <c>CyberCloud.ResourceManager</c> surface, which is platform rather than a
///                     provider's — the same boundary <see cref="NetworkAddressing" /> records for
///                     <c>Validates(...)</c>.
///                 </item>
///                 <item>
///                     ⚠ <b>A decision the platform has not taken would be taken by accident.</b>
///                     docs/plan/14: <i>"the provider is 1.5 EM; the operations are the cost … the
///                     decision to make before M1 is whether we run it or front a wholesale
///                     provider — and either is defensible, but pretending the software is the whole
///                     job is not"</i>. Running public authoritative DNS means anycast nameservers,
///                     DDoS absorption, and being the reason a customer's whole business is offline.
///                     A shipped <c>zoneType: public</c> is that decision, made in a schema.
///                 </item>
///                 <item>
///                     ⚠ <b>A FOURTH BLOCKER, FOUND WHILE BUILDING <c>loadBalancers</c>, AND IT IS THE
///                     ONE THAT BITES THE <i>PRIVATE</i> HALF.</b> docs/plan/14 § DNS wants private
///                     zones <i>"linked to VPCs and resolved by the VPC's resolver only"</i>.
///                     Kube-OVN's in-VPC resolver is <c>VpcDns</c> — a CoreDNS deployment on the
///                     tenant's own switch — and its lister, its two work queues, its event handler,
///                     its two workers and <c>resyncVpcDNSConfig</c> are <b>all inside
///                     <c>if config.EnableLb</c></b> in <c>pkg/controller/controller.go</c>, which
///                     ADR-019 sets to false. <b>So a tenant VPC on this platform has no resolver for
///                     a private zone to be served by</b>, and the three blockers above are joined by
///                     one that applies even to the half nobody has to decide about. It is also why
///                     <see cref="LoadBalancers" /> takes addresses rather than names.
///                 </item>
///             </list>
///             ⚠ <b>What is NOT a blocker, and is worth recording because it looks like one</b>: the
///             address model already carries record sets. <c>ResourceIdTests</c> round-trips
///             <c>dnsZones/recordSets/a</c> — a <b>three-segment</b> type path — so
///             <c>…/dnsZones/{zone}/recordSets/{name}/a/{record}</c> needs nothing from
///             <c>ResourceId</c>.
///         </item>
///         <item>
///             <b><c>virtualNetworks/loadBalancers</c> — SHIPPED, and the object both docs/plan/14 and
///             this substrate point at is one nothing here reconciles.</b> Kube-OVN's native answer is
///             a <c>SwitchLBRule</c>: a VIP on the tenant's logical switch, served by OVN, with no pod
///             anywhere. Read firsthand in <c>pkg/controller/controller.go</c> at <c>v1.16.2</c> — the
///             lister, the three work queues, the event handler and the three workers for
///             <c>SwitchLBRule</c>, <b>and the whole of <c>VpcDns</c>, and the ordinary <c>Service</c>
///             handlers</b>, are every one of them inside <c>if config.EnableLb</c>. ADR-019 runs
///             Kube-OVN with <c>ENABLE_LB=false</c> so that Cilium owns the service datapath, so a
///             <c>SwitchLBRule</c> on this platform would be accepted by the API server, reported
///             <c>Succeeded</c>, and balance nothing. docs/plan/14 names the alternative in the same
///             sentence — <i>"an HAProxy deployment for TCP with health checks and connection
///             limits"</i> — and that is what landed: a proxy pod on the tenant's own subnet, joined
///             through <c>ovn.kubernetes.io/logical_switch</c>. The full argument, including why it is
///             a <b>child</b> where docs/plan/14 spells it top level, is on
///             <see cref="LoadBalancers" />.
///             <para>
///                 ⚠ <b>The reader is still owed and is no longer what blocks the row.</b>
///                 docs/plan/14 wants backend pools that <i>"reference resource ids … resolved by the
///                 reconciler into endpoints"</i>, and <c>ReconcileContext</c> carries a cluster
///                 connection, an <c>ISecretResolver</c>, an <c>ISecretWriter</c> and a log — nothing
///                 that resolves a resource id. ⚠ <b>What makes an address list honest rather than a
///                 workaround is the same <c>ENABLE_LB=false</c></b>: with the <c>Service</c> handlers
///                 and <c>VpcDns</c> in that block, a tenant VPC has no service discovery and no DNS,
///                 so an address is what a tenant has for every workload in their own network — and
///                 what the reader would resolve to is still an address.
///             </para>
///         </item>
///         <item>
///             ⚠ <b><c>vpnGateways</c> — OWED, AND THE TWO THINGS THAT WOULD HAVE BLOCKED IT NO LONGER
///             DO.</b> docs/plan/14 § VPN asks for WireGuard: a gateway plus <c>vpnClients</c>
///             sub-resources, <i>"each with its own keypair"</i>. Two earlier readings are now wrong.
///             <b>The peer list is not an array-of-objects problem</b> — that document's own shape puts
///             each peer in a <i>child resource</i>, which is the reshape this family already knows how
///             to do. And <b>a provider CAN mint a credential</b>: <c>ReconcileContext.SecretWriter</c>
///             is an <c>ISecretWriter</c> with mint-once semantics, so the gateway's private key is
///             writable to the tenant's vault by the reconciler that needs it.
///             <list type="number">
///                 <item>
///                     ⚠ <b>What is left is a real blocker and it is the child's.</b> WireGuard reads
///                     <b>one</b> configuration file listing every peer. So a <c>vpnClients</c> child
///                     would have to write into its parent's file — which is <c>routeTables</c>'
///                     refusal exactly: two children converging by erasing each other, with no
///                     <c>x-kubernetes-list-type</c> to make it safe. What would close it is either a
///                     WireGuard operator with a per-peer CRD (<b>which must be checked for existence,
///                     maintenance and licence before it is designed around</b>, ADR-011) or a
///                     reconciler on the <i>parent</i> that lists its own children — the same reader
///                     <c>loadBalancers</c> wants, in a second shape.
///                 </item>
///                 <item>
///                     ⚠ <b>And the gateway needs the kernel's WireGuard, which is a node property no
///                     schema can state.</b> A <c>wg</c> interface needs <c>NET_ADMIN</c> and a
///                     <c>wireguard</c> module on the host; a cluster without one runs the userspace
///                     implementation at a fraction of the throughput, and nothing in
///                     <c>ReconcileContext</c> can tell which cluster is which.
///                 </item>
///             </list>
///             ⚠ <b><c>publicIpAddresses</c> and this row share the unfinished half</b>: a gateway a
///             remote worker can reach needs a public address attached by an <c>OvnFip</c> or
///             <c>OvnDnatRule</c> naming <see cref="PublicIpAddresses.ObjectNameOf" />'s output, and
///             nothing creates one yet — <c>charts/managed/kube-ovn-eip/conformance.yaml § owed</c>,
///             <c>nothing-can-be-given-an-address-yet</c>.
///         </item>
///     </list>
///     <para>
///         ⚠ <b>NO <c>applicationGateways</c>, <c>natGateways</c>, <c>privateEndpoints</c> OR
///         <c>peerings</c>.</b> docs/plan/14 puts them at M2/M3 and they are out of scope rather than
///         owed.
///     </para>
///     <para>
///         ⚠ <b>THE SHORT NAMES HAVE TO STAY CLEAR OF THIS GROUP'S KEY AND OF EACH OTHER, AND OF
///         NOTHING ELSE.</b> <c>CliEmitter</c> derives the CLI group key from the provider namespace,
///         so this namespace's group is already <c>network</c> — which is why no type here is called
///         that, exactly as <c>CyberCloud.Storage/accounts</c> ships as <c>objectstore</c> rather than
///         the <c>storage</c> docs/plan/21 § Grammar would spell. A token given two meanings throws
///         <c>ArgumentException: An item with the same key has already been added</c> on every parse
///         that reaches the group, naming neither the provider nor the string. <c>CliTokens</c>
///         carries the rule and <c>CliTokenTests</c> carries the measurements.
///         ⚠ The literal lists that used to live here — every group key and every declared short name
///         in the tree — went stale on two consecutive passes and asked the wrong question besides:
///         the token dictionary is per <b>parent</b> command, so a short name equal to another group's
///         key cannot collide. <c>ProviderRegistry.Build</c> derives the question from what is
///         registered and refuses the silo naming both ends, which closed
///         <c>short-name-collides-with-the-group</c>.
///     </para>
///     <para>
///         ⚠ <b>NO <c>SupportsSoftDelete</c> ON ANY OF THE THREE — AND THE REASON EVERY PROVIDER IN
///         THE TREE GIVES FOR THAT IS NOW FALSE.</b> Eleven files, this one included, said
///         <i>"nothing in the manager reads <c>SoftDeleteDays</c>"</i>. It does:
///         <c>ResourceManagerService.DeleteAsync</c> branches on
///         <c>target.Registration.SoftDeleteDays &gt; 0</c> and calls
///         <c>IResourceIndexGrain.SoftDeleteAsync</c> instead of <c>ReleaseAsync</c>, the operation
///         spec carries <c>SoftDelete</c> forward, and <c>OperationGrain</c> withholds the committed
///         quota until a purge. ⚠ <b>What is still missing is the half that matters to a tenant:
///         <c>RestoreAsync</c> and <c>PurgeAsync</c> exist on the manager and have NO HTTP ROUTE.</b>
///         So declaring a window today parks the name and <i>holds the quota</i> for the whole window,
///         with no way for a tenant to recover the resource or to release it early. That is the wrong
///         trade for every type here and is stated as the actual reason rather than the stale one.
///         The type it would matter most for is <c>publicIpAddresses</c>, which is owed — see that
///         entry.
///     </para>
///     <para>
///         ⚠ <b>THE THREE ACTIONS ALL HAVE HANDLERS NOW, AND BEFORE THEY DID NOT THEY WERE
///         <c>500</c>s.</b> When this family shipped, no provider in the tree had an action handler
///         and there was nowhere to put one. The seam exists (<c>IResourceActionHandler</c>,
///         <c>ActionDispatcher</c>), and a <b>synchronous action with no handler is refused by
///         name</b> — <i>"declares the action '…' and no handler for it, so it cannot be run"</i>, an
///         <c>InternalError</c>. So <c>showIsolation</c>, the one action in the catalogue whose
///         content was ready, was publishing a <c>500</c> for the platform's own statement of what it
///         does not protect a tenant from. All three are <see cref="ActionKind.Post" />, synchronous
///         and handler-backed; none is <c>longRunning</c>, which <c>ProviderBuilder.Action</c> refuses
///         to combine with a handler at silo start.
///     </para>
/// </remarks>
public sealed class NetworkProvider : IResourceProvider {
    /// <inheritdoc />
    public string ProviderNamespace => VirtualNetworks.ProviderNamespace;

    /// <inheritdoc />
    public void Describe(IProviderBuilder builder) {
        ArgumentNullException.ThrowIfNull(builder);

        builder
            .ResourceType(VirtualNetworks.TypePath)
            .ApiVersion(VirtualNetworks.V2026, VirtualNetworks.Schema2026)
            .Reconciler<VirtualNetworkReconciler>()
            // ⚠ ONE METER, AND THE THREE THIS FAMILY DOES NOT DRAW ARE THE INTERESTING PART. Every
            // provider in the catalogue before this one draws vcpu, memoryGb and storageGb, because
            // every one of them provisions PODS. A Kube-OVN Vpc is an OVN logical router: a row in a
            // southbound database and a set of flows on nodes that already exist. It consumes no
            // CPU, no memory and no disk that is attributable to the tenant, and declaring a meter
            // for it would reserve capacity nothing uses and bill for hardware nobody added.
            //
            // ⚠ SO THIS IS THE FIRST TYPE IN THE TREE WHOSE ONLY QUOTA IS `Resources`, AND THAT IS A
            // REAL SHAPE RATHER THAN AN UNDER-DECLARATION. What limits how many networks a tenant may
            // have is the count — which is exactly what QuotaMeter.Resources is — plus, one day, the
            // scarce thing docs/plan/14 actually names, which is IPv4 and is publicIpAddresses'
            // to draw.
            .Meters(QuotaMeter.Resources)
            .Permissions("read", "write", "delete")
            .Action(
                VirtualNetworks.ShowIsolationAction,
                ActionKind.Post,
                VirtualNetworks.ShowIsolationPermission,
                response: VirtualNetworks.ShowIsolationResponse,
                // ⚠ WITHOUT THIS THE ACTION IS A 500. ActionDispatcher refuses a synchronous action
                // whose HandlerType is null, by name, as an InternalError — and this is the action
                // that publishes what the platform does NOT protect a tenant from.
                handler: typeof(ShowIsolationHandler)
            )
            // ⚠ `vnet`, which is docs/plan/21 § Grammar's own alias for this type and is neither a
            // group key nor an existing short name — see this class's remarks for the full check and
            // for why it is not `network`.
            .Display(
                "Virtual network",
                "Virtual networks",
                shortName: "vnet",
                summary: "A private routing domain on Kube-OVN with its own address space. "
                + VirtualNetworks.IsolationClaim
            )
            .Chart(VirtualNetworks.ChartName)
            .SupportsTags()
            .RequiresCluster(VirtualNetworks.ClusterIdPointer)
            // ── The child, docs/plan/14 § Virtual networks' `subnets/{name}` ───────────────────
            //
            // ⚠ EVERY CAPABILITY BELOW IS ONE THE SHARED CONFORMANCE SUITE HAS AN ASSERTION FOR. A
            // child that declared only a schema and three permissions would have no reconciler for
            // TheTypeIsRegisteredWithAReconcilerAndAllThreePermissions, own no objects for the four
            // cluster-facing assertions, declare no action for the two POST ones, and refuse tags —
            // and a case for it would then pass a suite that quietly asked less.
            .ResourceType(NetworkSubnets.TypePath)
            .ApiVersion(NetworkSubnets.V2026, NetworkSubnets.Schema2026)
            .Reconciler<NetworkSubnetReconciler>()
            // ⚠ `Resources` AND NOT AN ADDRESS COUNT, WHICH IS THE TEMPTING WRONG ANSWER. A /16
            // subnet holds 65 534 addresses and a /24 holds 254, so a meter over "addresses this
            // subnet contains" would make one create consume a tenant's entire allowance while
            // provisioning nothing scarce — a PRIVATE address is not scarce, which is the whole
            // reason RFC 1918 space is reusable and the whole reason overlapping VPCs are allowed.
            // What IS scarce is public IPv4, and docs/plan/14 puts that on publicIpAddresses, "a
            // metered, quota'd, allocatable resource in its own right — because IPv4 is scarce and
            // must be accounted". This meter is the count of subnets and nothing more.
            .Meters(QuotaMeter.Resources)
            .Permissions("read", "write", "delete")
            .Action(
                NetworkSubnets.AddressUsageAction,
                ActionKind.Post,
                NetworkSubnets.AddressUsagePermission,
                response: NetworkSubnets.AddressUsageResponse,
                handler: typeof(ListAddressUsageHandler)
            )
            // ⚠ `subnet` — neither a group key (sample, dbforpostgresql, cache, messaging, storage,
            // search, documentdb, analytics, dbformysql, network) nor any existing short name
            // (widget, postgres, valkey, kafka, nats, rabbitmq, objectstore, bucket, opensearch,
            // docdb, clickhouse, mariadb), and distinct from `vnet`. Asserted against all of those as
            // literals by NetworkDeclarationTests.
            .Display(
                "Subnet",
                "Subnets",
                shortName: "subnet",
                summary: "A range inside a virtual network that workloads are given addresses from, "
                + "with optional outbound NAT and optional isolation from other subnets."
            )
            .Chart(NetworkSubnets.ChartName)
            .SupportsTags()
            .RequiresCluster(NetworkSubnets.ClusterIdPointer)
            // ── The second child, docs/plan/14 § Virtual networks' `securityGroups/{name}` ─────
            //
            // ⚠ THE TYPE THE `not-a-firewall-by-default` ISOLATION LIMIT HAS BEEN POINTING AT SINCE
            // THIS FAMILY SHIPPED. VirtualNetworks.IsolationLimits tells a tenant who wants a
            // deny-by-default perimeter to "attach a securityGroups child, whose rules become OVN
            // ACLs on the ports in the network" — and until now that row named a type that did not
            // exist, which is the one kind of documentation defect a forbidden-word test cannot see.
            .ResourceType(NetworkSecurityGroups.TypePath)
            .ApiVersion(NetworkSecurityGroups.V2026, NetworkSecurityGroups.Schema2026)
            .Reconciler<NetworkSecurityGroupReconciler>()
            // ⚠ `Resources`, and the reasoning is the family's rather than a copy of it: a
            // SecurityGroup is an OVN port group and a handful of ACL rows. No pod, no disk — and
            // unlike a subnet there is not even a tempting wrong answer, because nothing about a rule
            // list is a quantity of anything scarce. What limits how many a tenant may have is the
            // count.
            .Meters(QuotaMeter.Resources)
            .Permissions("read", "write", "delete")
            .Action(
                NetworkSecurityGroups.EffectiveRulesAction,
                ActionKind.Post,
                NetworkSecurityGroups.EffectiveRulesPermission,
                response: NetworkSecurityGroups.EffectiveRulesResponse,
                handler: typeof(ShowEffectiveRulesHandler)
            )
            // ⚠ `secgroup` AND NOT `sg`, WHICH IS KUBE-OVN'S OWN shortName. Two characters is a token
            // somebody else will reach for, and the second claim on it throws `ArgumentException` on
            // every `cyc network …` parse, naming neither the provider nor the string. What it has to
            // stay clear of is this group's key and its siblings' names — CliTokens carries the rule,
            // ProviderRegistry.Build enforces it, and
            // NetworkDeclarationTests.TheShortNamesAreTheOnesTheProviderMeantToDeclare pins the word.
            .Display(
                "Security group",
                "Security groups",
                shortName: "secgroup",
                summary: "A deny-by-default set of allow rules that become OVN ACLs on the ports in a "
                + "virtual network. A workload may carry several."
            )
            .Chart(NetworkSecurityGroups.ChartName)
            .SupportsTags()
            .RequiresCluster(NetworkSecurityGroups.ClusterIdPointer)
            // ── The fourth type, and the first that is NOT a child — docs/plan/14 § Everything else ─
            //
            // ⚠ TOP LEVEL, WHICH IS THE OPPOSITE OF WHERE A READER OF THIS FAMILY WOULD PUT IT. Its
            // three siblings are a network and two things inside one. An OvnEip is inside nothing: it
            // is allocated from the OPERATOR'S external subnet and attached to a tenant's routing
            // domain later, by a separate OvnFip/OvnDnatRule/OvnSnatRule object that names it.
            // docs/plan/14 § Load balancing spells the type `CyberCloud.Network/publicIpAddresses`,
            // with no network segment in the path, and the substrate agrees.
            .ResourceType(PublicIpAddresses.TypePath)
            .ApiVersion(PublicIpAddresses.V2026, PublicIpAddresses.Schema2026)
            .Reconciler<PublicIpAddressReconciler>()
            // ⚠ THE FIRST TYPE IN THE PLATFORM THAT DRAWS QuotaMeter.PublicIps, AND THE MEASURE OF
            // WHY IT TOOK ELEVEN PROVIDERS. That meter has existed since QuotaGrain's defaults — 20
            // per subscription — and nothing has ever drawn it, because every provider that reached
            // for it wanted a CONDITIONAL draw (an address only when a body asked for external
            // exposure) and QuotaGrain.TryReserveAsync refuses a non-positive amount by name: "A
            // reservation must be positive; 0 is not." An address resource draws exactly one,
            // unconditionally, for its whole life.
            //
            // ⚠ AND IT IS A FLAT METER: ResourceManagerService.AmountFor answers `meter.Fallback ?? 1m`
            // for a meter with an empty AmountPointer, so this needs no pointer, no fallback and no
            // MeterDerivation. `Meters(...)` supplies 1m for every one of them.
            //
            // ⚠ WHAT KEEPS IT UNCONDITIONAL is that this type has no `ipVersion` property — see
            // PublicIpAddresses' remarks. A tenant who could ask for an IPv6-only address would be
            // asking for a resource that draws ZERO scarce addresses, and a zero draw is the exact
            // thing TryReserveAsync refuses. IPv6 rides along on a dual-stack pool and costs nothing,
            // which is right: PublicIps counts the scarce half.
            .Meters(QuotaMeter.PublicIps, QuotaMeter.Resources)
            .Permissions("read", "write", "delete")
            .Action(
                PublicIpAddresses.AllocationAction,
                ActionKind.Post,
                PublicIpAddresses.AllocationPermission,
                response: PublicIpAddresses.AllocationResponse,
                handler: typeof(ShowAllocationHandler)
            )
            // ⚠ `publicip`, AND NOT `pip` OR `eip`. Three characters is a token somebody else will
            // reach for, and `eip` is the SUBSTRATE'S word rather than the product's — docs/plan/21
            // § Grammar spells the type `publicIpAddresses`, and a tenant who has never heard of
            // Kube-OVN should be able to guess it. What the word has to stay clear of is this group's
            // key and its siblings' names; CliTokens carries the rule and ProviderRegistry.Build
            // enforces it, deriving the question from what is registered rather than from the lists
            // of group keys and short names this paragraph used to carry.
            .Display(
                "Public IP address",
                "Public IP addresses",
                shortName: "publicip",
                summary: "A public address allocated from the region's pool, which a load balancer or "
                + "a gateway can later be given. On its own it carries no traffic."
            )
            .Chart(PublicIpAddresses.ChartName)
            .SupportsTags()
            .RequiresCluster(PublicIpAddresses.ClusterIdPointer)
            // ── The fifth type — docs/plan/14 § Load balancing ────────────────────────────────
            //
            // ⚠ A CHILD, WHICH IS NOT HOW docs/plan/14 SPELLS IT, AND THE SUBSTRATE IS WHY. That
            // document writes `CyberCloud.Network/loadBalancers` with no network segment — the
            // spelling publicIpAddresses kept, because an OvnEip genuinely names no VPC. Every object
            // THIS type renders is annotated onto one subnet of one VPC and its frontend address is
            // only meaningful inside that VPC's address space, so the network comes from the ADDRESS
            // and cannot be wrong. The alternative is a network-name property nothing validates.
            .ResourceType(LoadBalancers.TypePath)
            .ApiVersion(LoadBalancers.V2026, LoadBalancers.Schema2026)
            .Reconciler<LoadBalancerReconciler>()
            // ⚠ THE FIRST TYPE IN THIS FAMILY THAT DRAWS COMPUTE, AND ADR-019 IS WHAT PUT IT THERE.
            // A Vpc, a Subnet and a SecurityGroup are rows in OVN's databases and consume nothing
            // attributable, which is why they draw `Resources` alone. This row would have been the
            // same — Kube-OVN's own SwitchLBRule is a VIP on a logical switch with no pod anywhere —
            // except that SwitchLBRule is served only when the controller runs with `--enable-lb`,
            // and ADR-019 runs Kube-OVN with ENABLE_LB=false so that Cilium owns the service
            // datapath. Read firsthand in pkg/controller/controller.go at v1.16.2: the lister, the
            // three queues, the event handler and the three workers are all inside
            // `if config.EnableLb`. So the proxy is a POD, and a pod is vCPU and memory somebody's
            // node actually gives up.
            .Meter(QuotaMeter.Vcpu, ProxyVcpuDrawn)
            .Meter(QuotaMeter.MemoryGb, ProxyMemoryDrawn)
            // ⚠ NO StorageGb. The proxy mounts one ConfigMap and writes nothing: its root filesystem
            // is read-only and it has no emptyDir, because HAProxy in TCP mode buffers in memory. A
            // storage meter here would reserve disk nothing allocates.
            .Meters(QuotaMeter.Resources)
            .Permissions("read", "write", "delete")
            .Action(
                LoadBalancers.BackendsAction,
                ActionKind.Post,
                LoadBalancers.BackendsPermission,
                response: LoadBalancers.BackendsResponse,
                handler: typeof(ShowBackendsHandler)
            )
            // ⚠ `loadbalancer`, AND NOT `lb`: two characters is the token `secgroup` was named to
            // stay off, and docs/plan/21 § Grammar spells the type `loadBalancers` — a tenant who has
            // never heard of HAProxy should be able to guess it. CliTokens carries what the word has
            // to stay clear of and ProviderRegistry.Build enforces it.
            //
            // ⚠ CommandTree.ReservedGroups WAS ON THAT LIST AND COULD NEVER HAVE FAILED. A short name
            // is an alias under `network` and a reserved group is a ROOT command, so the two are
            // never one token. The half that is real — a generated GROUP taking one of the nine —
            // CommandTree throws on while the root command is built, and cyc.Tests.ReservedGroupTests
            // asserts it over the whole tree rather than one family at a time.
            .Display(
                "Load balancer",
                "Load balancers",
                shortName: "loadbalancer",
                summary: "An L4 TCP proxy on an address inside a virtual network, spreading "
                + "connections across a pool of workload addresses with health checks and a "
                + "connection limit."
            )
            .Chart(LoadBalancers.ChartName)
            .SupportsTags()
            .RequiresCluster(LoadBalancers.ClusterIdPointer);
    }

    // ── What a load balancer draws ─────────────────────────────────────────────────────────────
    //
    // ⚠ BOTH DERIVE FROM ONE POINTER, WHICH IS THE SIMPLEST SHAPE MeterDerivation TAKES IN THE TREE
    // AND IS STILL NOT A CONSTANT. `sizing.preset` is the only body property that moves either
    // figure, because this row is exactly one pod: no replica count (see LoadBalancers' remarks on
    // why there is one) and no second component. A flat `Meters(Vcpu)` would have reserved 1 vCPU for
    // a proxy that asks for 250m, on every subscription, forever.

    /// <summary>vCPU: the proxy pod at its preset.</summary>
    /// <remarks>
    ///     ⚠ Refuses rather than reserving zero when the quantity does not parse. That is unreachable
    ///     from a validated body — <c>AllowedValues</c> is <see cref="LoadBalancers.Presets" />' own
    ///     keys — and is exactly the drift worth failing on when somebody adds a preset to the enum and
    ///     forgets the table.
    /// </remarks>
    static MeterDerivation ProxyVcpuDrawn { get; } =
        MeterDerivation.Of(
            "sizing.preset's cpu, in cores",
            ["/properties/sizing/preset"],
            body => KubeQuantity.TryParse(LoadBalancers.Resources(body).Cpu, out var cores)
                ? Result<decimal>.Success(cores)
                : Result<decimal>.Failure(
                    ErrorCode.InternalError,
                    "the sizing preset behind '/properties/sizing/preset' carries a cpu quantity that "
                    + "does not parse, so no reservation can be computed"
                )
        );

    /// <summary>Memory: the same pod, in gibibytes.</summary>
    static MeterDerivation ProxyMemoryDrawn { get; } =
        MeterDerivation.Of(
            "sizing.preset's memory, in GiB",
            ["/properties/sizing/preset"],
            body => KubeQuantity.TryGibibytes(LoadBalancers.Resources(body).Memory, out var gibibytes)
                ? Result<decimal>.Success(gibibytes)
                : Result<decimal>.Failure(
                    ErrorCode.InternalError,
                    "the sizing preset behind '/properties/sizing/preset' carries a memory quantity "
                    + "that does not parse, so no reservation can be computed"
                )
        );
}
