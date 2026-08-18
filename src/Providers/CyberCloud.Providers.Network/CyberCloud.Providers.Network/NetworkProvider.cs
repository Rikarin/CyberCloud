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
///             <b><c>dnsZones</c>, <c>loadBalancers</c>, <c>vpnGateways</c> — owed.</b> Each is M1 in
///             docs/plan/14 and none is declared. ⚠ <c>dnsZones</c> is the one whose absence is least
///             about software: docs/plan/14 is explicit that <i>"the provider is 1.5 EM; the
///             operations are the cost"</i> — anycast nameservers, DDoS absorption, and being the
///             reason a customer's whole business is offline when it breaks — and that <b>the decision
///             whether the platform runs authoritative DNS or fronts a wholesale provider has not been
///             taken</b>. A resource type declared before that decision would pick it by accident.
///             ⚠ <c>loadBalancers</c> and <c>vpnGateways</c> both need the reshape
///             <see cref="NetworkSecurityGroups" /> established — a backend set and a peer list are
///             both arrays of objects, which <c>SchemaProperty.ElementKind</c> refuses — and
///             <c>loadBalancers</c> additionally needs a <b>reader</b>: docs/plan/14 has its backend
///             pools <i>"reference resource ids (a VM, a scale set, a cluster's node pool), resolved
///             by the reconciler into endpoints"</i>, and rule 2 routes a cross-resource reference
///             through <c>CyberCloud.ResourceManager</c>, which a reconciler cannot reach. It is the
///             same shape as <c>a-security-group-cannot-be-a-remote</c> and it wants the same reader.
///             ⚠ <c>publicIpAddresses</c> shipping is nevertheless the half of
///             docs/plan/14 § Load balancing that could be built alone: the address exists now and
///             something can be given it.
///         </item>
///     </list>
///     <para>
///         ⚠ <b>NO <c>applicationGateways</c>, <c>natGateways</c>, <c>privateEndpoints</c> OR
///         <c>peerings</c>.</b> docs/plan/14 puts them at M2/M3 and they are out of scope rather than
///         owed.
///     </para>
///     <para>
///         ⚠ <b>THE SHORT NAMES WERE CHECKED AGAINST EVERY GROUP KEY AND EVERY EXISTING SHORT NAME AS
///         LITERALS, AND AGAINST EACH OTHER.</b> <c>CliEmitter</c> derives the CLI group key from the
///         provider namespace, so this namespace's group is already <c>network</c> — which is why
///         neither type is called that, exactly as <c>CyberCloud.Storage/accounts</c> ships as
///         <c>objectstore</c> rather than the <c>storage</c> docs/plan/21 § Grammar would spell.
///         System.CommandLine's <c>ValidTokens</c> is <b>one dictionary</b> of every command token and
///         every alias in the tree, so a group and an alias that share a string throw
///         <c>ArgumentException: An item with the same key has already been added</c> on the first
///         parse of <i>any</i> command line — a failure that names neither the provider nor the
///         string. <c>NetworkDeclarationTests</c> asserts both short names against the ten group keys
///         and the twelve existing short names, as literals, and against each other.
///         ⚠ <c>ProviderRegistry.Build</c> still refuses only a <b>duplicate</b> short name and still
///         never compares one against a group name; <c>short-name-collides-with-the-group</c> stays
///         owed and this family is the second and third type to have to satisfy it by hand.
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
            // ⚠ `secgroup` — checked against the eleven group keys (sample, dbforpostgresql, cache,
            // messaging, storage, search, documentdb, analytics, dbformysql, network,
            // containerservice) and against every existing short name, as literals, and against
            // `vnet` and `subnet`. NOT `sg`, which is Kube-OVN's own shortName: two characters is a
            // prefix somebody will collide with, and System.CommandLine's ValidTokens is ONE
            // dictionary of every command token and alias in the tree, so a collision throws
            // `ArgumentException` on the first parse of ANY command line, naming neither the provider
            // nor the string. NetworkDeclarationTests asserts it.
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
            // ⚠ `publicip` — checked against the fourteen group keys (sample, dbforpostgresql, cache,
            // messaging, storage, search, documentdb, analytics, dbformysql, network,
            // containerservice, monitor, terminal, containerregistry) and against every existing short
            // name (widget, postgres, valkey, kafka, nats, rabbitmq, objectstore, bucket, opensearch,
            // docdb, clickhouse, mariadb, aks, nodepool, workspace, shell, registry), as literals, and
            // against this family's own `vnet`, `subnet` and `secgroup`. NOT `pip` and NOT `eip`:
            // three characters is a prefix somebody will collide with, `eip` is the substrate's word
            // rather than the product's, and System.CommandLine's ValidTokens is ONE dictionary of
            // every command token and alias in the tree — a collision throws `ArgumentException` on
            // the first parse of ANY command line, naming neither the provider nor the string.
            .Display(
                "Public IP address",
                "Public IP addresses",
                shortName: "publicip",
                summary: "A public address allocated from the region's pool, which a load balancer or "
                + "a gateway can later be given. On its own it carries no traffic."
            )
            .Chart(PublicIpAddresses.ChartName)
            .SupportsTags()
            .RequiresCluster(PublicIpAddresses.ClusterIdPointer);
    }
}
