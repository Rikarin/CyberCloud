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
///             <b><c>virtualNetworks/securityGroups</c> — owed, and buildable.</b> Unlike route
///             tables it has a real object: <c>SecurityGroup</c>, cluster-scoped, plural
///             <b><c>security-groups</c></b> (hyphenated — a detail the CRD-stub derivation reads and
///             a hand-written plural would get wrong). Its rule shape is
///             <c>SecurityGroupRule{ipVersion, protocol, priority, remoteType, remoteAddress,
///             portRangeMin, portRangeMax, policy}</c> — an <b>array of objects</b>, which is the
///             same <c>ElementKind</c> refusal static routes meet. So it is owed <i>and</i> it has a
///             blocker, and the blocker is the platform's rather than the substrate's. See
///             <c>charts/managed/kube-ovn-vpc/conformance.yaml § owed</c>,
///             <c>security-group-rules-are-arrays-of-objects</c>.
///         </item>
///         <item>
///             <b><c>publicIpAddresses</c>, <c>dnsZones</c>, <c>loadBalancers</c>,
///             <c>vpnGateways</c> — owed.</b> Each is M1 in docs/plan/14 and none is declared.
///             ⚠ <c>publicIpAddresses</c> is the one whose absence costs the most and it is recorded
///             with what was learned about it rather than as a bare gap: it is the first type in the
///             platform that would draw <c>QuotaMeter.PublicIps</c>, which no shipping type has ever
///             drawn — every provider that reached for it found
///             <c>QuotaGrain.TryReserveAsync</c> refusing a non-positive amount for a
///             <i>conditional</i> meter. <b>An address resource does not have that problem</b>: it
///             draws exactly one, unconditionally, which is the first body in the tree for which that
///             meter is expressible at all. ⚠ And doc 14 § Load balancing names the real design
///             hazard, which was verified: the allocator depends on where the address lives —
///             Cilium LB-IPAM for the platform fabric, a Kube-OVN <c>IptablesEIP</c>/<c>OvnEip</c>
///             bound to the VPC's router for a tenant address — and both surface as one resource
///             type. Both objects exist and are cluster-scoped.
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
///         ⚠ <b>No <c>SupportsSoftDelete</c> on either type</b>, for the reason every provider before
///         this one gives: nothing in the manager reads <c>SoftDeleteDays</c>.
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
                response: VirtualNetworks.ShowIsolationResponse
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
                response: NetworkSubnets.AddressUsageResponse
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
            .RequiresCluster(NetworkSubnets.ClusterIdPointer);
    }
}
