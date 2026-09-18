using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Network.Contracts;

/// <summary>
///     Everything addressable about <c>CyberCloud.Network/virtualNetworks/peerings</c> — a route
///     exchange between two of a tenant's virtual networks, written onto <b>both</b> networks'
///     Kube-OVN <c>Vpc</c> objects as a second writer.
/// </summary>
/// <remarks>
///     <para>
///         <b>The authority is docs/plan/14 § Virtual networks</b>, whose tree draws
///         <i>"<c>peerings/{name}</c> → VPC-to-VPC within a tenant"</i>, and docs/plan/09 § A second
///         writer on an object, which is the apply mode this type is the first to use.
///     </para>
///     <para>
///         ⚠ <b>A PEERING HAS NO OBJECT OF ITS OWN, AND THAT IS WHY IT TOOK TWO ISSUES.</b> Read
///         firsthand in <c>pkg/apis/kubeovn/v1/vpc.go</c> and <c>pkg/controller/vpc.go</c> at
///         <c>v1.16.2</c>: a peering is one entry in <c>Vpc.spec.vpcPeerings</c> —
///         <c>{remoteVpc, localConnectIP}</c>, which <c>handleAddOrUpdateVpc</c> turns into
///         <c>CreatePeerRouterPort(vpc, remoteVpc, localConnectIP)</c>, a logical router port named
///         <c>{vpc}-{remoteVpc}</c> peered with <c>{remoteVpc}-{vpc}</c> — plus one
///         <c>Vpc.spec.staticRoutes</c> entry per exchanged range naming the peer's connect address as
///         <c>nextHopIP</c>. Both entries are needed on <b>both</b> networks' objects, each of which is
///         owned by its own <c>virtualNetworks</c> resource, and both arrays are atomic under
///         server-side apply. #31 found that the ordinary build could not write it; #89 built
///         <c>IKubeCommandBuilder.CoWriting</c>, and this type reaches it through
///         <see cref="ReconcileContext.CoWriter" />: the owner's labels stay, the peering's slice is
///         written down on each object under <c>cybercloud.io/fragment.{id}</c>, and teardown
///         withdraws the slice rather than deleting either network's object.
///     </para>
///     <para>
///         ⚠
///         <b>
///             THE REMOTE IS A NAME IN THE SAME RESOURCE GROUP, AND THE SCHEMA COULD HAVE SAID
///             MORE.
///         </b> <c>SchemaFormat.ResourceId</c> exists — <c>CyberCloud.Monitor</c>'s alert rules
///         name a sending service by full path — so a cross-subscription peering was expressible at
///         the API. It is not offered, for two reasons that are not the schema's. First, the write
///         path authorizes the caller against the <i>peering's</i> address and nothing else, and no
///         seam lets a provider require a permission on a second resource at reconcile time
///         (<see cref="NetworkAddressing" /> records the same absence for <c>Validates</c>). Roles
///         are granted on subscriptions and groups (docs/plan/07), so one resource group is the
///         smallest scope on which write on the peering implies write on both networks, and a body
///         naming a network elsewhere would inject routes into a router its author may hold no role
///         on. Second, the remote's object name is <see cref="VirtualNetworks.ObjectNameOf" /> of
///         the remote's <i>namespace</i>, and the namespace rule is
///         <c>ReconcileDriver.NamespaceFor</c>'s, whose own remarks forbid a second spelling in a
///         provider. So the remote's name is qualified with this resource's own namespace.
///         <c>charts/managed/kube-ovn-vpc-peering/conformance.yaml § owed</c>,
///         <c>the-remote-must-be-in-the-same-resource-group</c>.
///     </para>
///     <para>
///         ⚠ <b>THE NAME QUALIFIER IS NOT THE BOUNDARY; THE CO-OWNED APPLY'S CHECK IS.</b> The
///         first version of this class said the qualified name was "what makes the join unable to
///         reach outside the group", and the #31 review showed it does not: a <c>Vpc</c> name is
///         <c>{sub}-{group}-{network}</c> and <see cref="ResourceNaming.Pattern" /> admits hyphens
///         in both halves, so <c>prod</c>'s network <c>a-b</c> and <c>prod-a</c>'s network
///         <c>b</c> are <i>one object name</i>, and a peering in <c>prod</c> naming <c>a-b</c>
///         converged with a peer port and a route written into <c>prod-a</c>'s router. What holds
///         the boundary now is the co-owned apply itself: the builder and
///         <c>KubeCommand.CheckCoOwnedAgainst</c> hold the live object's <c>subscription-id</c> and
///         <c>resource-group</c> labels against the writer's, beside the tenant, and refuse by name
///         (docs/plan/09 § A second writer on an object). The reconciler reports that refusal as
///         <c>Failed</c>: a name another group's network occupies is not a wait.
///         <c>NetworkPeeringTests.ARemoteWhoseNameIsAnotherGroupsNetworkIsRefusedByGroupAndThatRouterIsUntouched</c>
///         is the review's probe kept as the test. ⚠ The collision itself is the network type's —
///         two networks in two such groups render one object and overwrite each other under the
///         family's one manager — and is recorded on the network's chart:
///         <c>charts/managed/kube-ovn-vpc/conformance.yaml § owed</c>,
///         <c>hyphenated-groups-can-render-one-object-name</c>.
///     </para>
///     <para>
///         ⚠ <b>THE BODY CARRIES THE RANGES, BECAUSE NEITHER NETWORK'S IS READABLE FROM HERE.</b> A
///         <c>Vpc</c> carries no CIDR — <see cref="VirtualNetworks" /> renders <c>addressSpace</c>
///         nowhere — and a reconciler cannot read another resource's body (the reader this family is
///         owed three times over). So a peering says which range each side advertises to the other,
///         and a link range the two peer ports address each other on. <see cref="AddressProblem" />
///         refuses the three overlapping each other, by name: a route to the remote's range that
///         overlapped the local one would black-hole the local network's own traffic. ⚠ Nothing
///         checks the ranges against the networks' declared address spaces — the same absence
///         <c>charts/managed/kube-ovn-vpc/conformance.yaml § owed</c> records as
///         <c>subnets-are-not-checked-against-the-address-space</c>, one level up.
///     </para>
///     <para>
///         ⚠
///         <b>
///             IPv4 ONLY AT THIS API-VERSION, WHICH IS A GAP THE FAMILY'S DUAL-STACK RULE MAKES
///             WORTH NAMING.
///         </b> <c>CreatePeerRouterPort</c> splits <c>localConnectIP</c> on commas into the
///         port's <c>networks</c>, so a dual-stack link is expressible on the substrate; what it
///         needs on this side is a v6 link range and a v6 route per side with a v6 next hop, three
///         more optional properties and a rule that all three arrive together. Recorded rather than
///         half-done: <c>§ owed</c>, <c>peering-is-ipv4-only</c>.
///     </para>
///     <para>
///         ⚠ <b>THE STATIC ROUTE CARRIES <c>policy: policyDst</c> EXPLICITLY.</b> <c>formatVpc</c>
///         fills an empty policy with <c>policyDst</c> and issues an <c>Update</c>, which makes the
///         controller a co-owner of the atomic list; a later apply of the list <i>without</i> the
///         policy would then be a change to a field two managers own, which is a
///         <c>FieldManagerConflict</c> forever. Sending the value the controller would write keeps the
///         two managers agreeing on the whole list. ⚠ That is reasoned from the source and unproven:
///         no harness this repository runs has a Kube-OVN controller (#95's VM lane is where one
///         will), so <c>§ owed</c>, <c>routing-is-unproven-until-the-vm-lane</c>.
///     </para>
///     <para>
///         ⚠ <b><c>remoteNetwork</c> IS <c>Immutable</c>, AND THE MANAGER DOES NOT ENFORCE THAT.</b>
///         <c>SchemaProperty.Immutable</c> is a declaration. A <c>PUT</c> that names a different
///         remote would apply a fragment onto the new remote's <c>Vpc</c> and never withdraw the
///         one on the old remote's. ⚠ Not an orphan to <c>DriftScanner</c>, as this paragraph once
///         said: the orphan pass skips every writer whose grain exists, and the peering's does. It
///         is a <c>Diverged</c> finding naming the old remote's object — a fragment of this
///         resource's on an object its <c>ExpectedResource.Fragments</c> does not place it on, "a
///         slice left behind" — and that finding is the only place the leftover is ever named.
///         <c>§ owed</c>, <c>a-changed-remote-leaves-a-fragment-behind</c>.
///     </para>
///     <para>
///         ⚠ <b>No <c>SupportsSoftDelete</c>, for the family's reason</b> — <c>RestoreAsync</c> and
///         <c>PurgeAsync</c> have no HTTP route, so a window parks the name and holds the quota with
///         no way to use either half.
///     </para>
/// </remarks>
public static class VirtualNetworkPeerings {
    /// <summary>The provider namespace — the family's.</summary>
    public const string ProviderNamespace = VirtualNetworks.ProviderNamespace;

    /// <summary>The type path — docs/plan/14's own spelling, a child of <c>virtualNetworks</c>.</summary>
    /// <remarks>
    ///     The network the peering hangs off is the <i>local</i> side; the body names the remote. A
    ///     peering is therefore created from one side and writes onto both, which is what the
    ///     co-owned mode is for.
    /// </remarks>
    public const string TypePath = "virtualNetworks/peerings";

    /// <summary>The one api-version. ⚠ Equal to the rest of the family's.</summary>
    public const string V2026 = VirtualNetworks.V2026;

    /// <summary>The chart this type is the configuration surface of.</summary>
    public const string ChartName = "managed/kube-ovn-vpc-peering";

    /// <summary>The pointer <c>RequiresCluster</c> names.</summary>
    public const string ClusterIdPointer = ClusterPlacement.DefaultPointer;

    /// <summary>The type, namespace and path together.</summary>
    public static ResourceTypeName Type { get; } = new(ProviderNamespace, TypePath);

    /// <summary>The action that reports what each side's <c>Vpc</c> carries for this peering.</summary>
    /// <remarks>
    ///     ⚠ <b>Two objects, two answers, and "programmed" is neither of them.</b> The fragment being
    ///     on an object means the platform wrote it; whether OVN built the peer ports is
    ///     <c>Vpc.status.vpcPeerings</c>, which the controller fills with the remote names it has
    ///     connected. Both are reported, per side, so a peering whose one side is missing — the
    ///     remote's network deleted, or a hand edit — is visible as exactly that.
    /// </remarks>
    public const string RoutesAction = "showRoutes";

    /// <summary>The permission <see cref="RoutesAction" /> checks.</summary>
    /// <remarks>⚠ <c>read</c>. Everything it reports is in the tenant's own two bodies or on their objects.</remarks>
    public const string RoutesPermission = "read";

    /// <summary>The route policy the fragment carries — Kube-OVN's <c>PolicyDst</c> constant.</summary>
    public const string RoutePolicy = "policyDst";

    /// <summary>The longest link prefix that still holds two host addresses.</summary>
    /// <remarks>
    ///     A <c>/31</c> has two addresses and no room for the network address this type's derivation
    ///     skips, and a <c>/32</c> has one. Refused rather than special-cased, because a peering's
    ///     link is two router ports and nothing else, and a tenant who reaches for <c>/31</c> is
    ///     saving nothing that is scarce inside their own VPC.
    /// </remarks>
    public const int MaxLinkPrefixLength = 30;

    // ── The two objects a peering writes onto ─────────────────────────────────────────────────

    /// <summary>The local network's name — the parent, read off the address.</summary>
    /// <param name="id">The peering's address.</param>
    /// <exception cref="ArgumentException"><paramref name="id" /> has no parent.</exception>
    public static string NetworkOf(ResourceId id) =>
        id.Parent?.Name
        ?? throw new ArgumentException(
            $"'{id.Path}' has no parent, so there is no local virtual network for this peering to "
            + "write onto. A peering is a child type — see VirtualNetworkPeerings.TypePath.",
            nameof(id)
        );

    /// <summary>The local network's <c>Vpc</c> — the parent's object.</summary>
    /// <param name="ns">The resource's namespace, used as a name component.</param>
    /// <param name="id">The peering's address.</param>
    public static ObjectRef LocalVpcRef(string ns, ResourceId id) => VirtualNetworks.VpcRef(ns, NetworkOf(id));

    /// <summary>The remote network's <c>Vpc</c> — the sibling's object, in the same resource group.</summary>
    /// <param name="ns">The resource's namespace, used as a name component.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     ⚠ Qualified with <b>this</b> resource's namespace, so a body can only <i>name</i> a network
    ///     in its own subscription and group — see this class's remarks for why that is a decision
    ///     rather than a limitation of the schema. ⚠ The name alone is not what keeps the write inside
    ///     the group: hyphenated group and network names can render one object name across two groups,
    ///     and it is the co-owned apply's check of the live object's <c>resource-group</c> label that
    ///     refuses the write. The class remarks say how that was found.
    /// </remarks>
    public static ObjectRef RemoteVpcRef(string ns, JsonElement desired) =>
        VirtualNetworks.VpcRef(ns, RemoteNetwork(desired));

    /// <summary>The <c>Vpc</c> object name of the local network.</summary>
    /// <param name="ns">The resource's namespace, used as a name component.</param>
    /// <param name="id">The peering's address.</param>
    public static string LocalVpcNameOf(string ns, ResourceId id) => VirtualNetworks.ObjectNameOf(ns, NetworkOf(id));

    /// <summary>The <c>Vpc</c> object name of the remote network.</summary>
    /// <param name="ns">The resource's namespace, used as a name component.</param>
    /// <param name="desired">The validated desired body.</param>
    public static string RemoteVpcNameOf(string ns, JsonElement desired) =>
        VirtualNetworks.ObjectNameOf(ns, RemoteNetwork(desired));

    /// <summary>The OVN logical router port a side's peering entry becomes — <c>{vpc}-{remoteVpc}</c>.</summary>
    /// <param name="vpc">The side's own <c>Vpc</c> object name.</param>
    /// <param name="remoteVpc">The other side's.</param>
    /// <remarks>Read firsthand from <c>CreatePeerRouterPort</c>; reported by <see cref="RoutesAction" />.</remarks>
    public static string PeerPortOf(string vpc, string remoteVpc) => vpc + "-" + remoteVpc;

    // ── The body shape ────────────────────────────────────────────────────────────────────────

    /// <summary>The default remote network name. ⚠ The harness's sibling is called this.</summary>
    public const string DefaultRemoteNetwork = "spoke";

    /// <summary>The default local range — docs/plan/14's worked example, the network type's own default.</summary>
    public const string DefaultLocalAddressSpace = "10.20.0.0/16";

    /// <summary>The default remote range. Disjoint from the local default and from every reserved row.</summary>
    public const string DefaultRemoteAddressSpace = "10.30.0.0/16";

    /// <summary>The default link. A <c>/30</c> at the top of RFC 1918's <c>10/8</c>, clear of both defaults.</summary>
    public const string DefaultLinkAddressSpace = "10.255.255.0/30";

    /// <summary>
    ///     The body shape at <see cref="V2026" />.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>ONE NAME AND THREE RANGES.</b> The name is the remote network; the ranges are what
    ///         each side advertises and the link the two router ports meet on. There is no
    ///         <c>allowForwardedTraffic</c>, no <c>useRemoteGateways</c> and no
    ///         <c>allowGatewayTransit</c>: the substrate has one knob per peering — the routes — and
    ///         an Azure-shaped switch that changed nothing would be the worst kind of property.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             Every range is a required, patterned property with a default its pattern
    ///             accepts
    ///         </b>, for the reason <see cref="VirtualNetworks.Schema2026" /> gives at length:
    ///         the generated chart lints against its own defaults, and <c>""</c> does not match a CIDR
    ///         pattern.
    ///     </para>
    /// </remarks>
    public static ResourceSchema Schema2026 { get; } =
        ResourceSchema.Of(
            [
                new(
                    "/location",
                    SchemaKind.Text,
                    true,
                    Description: "The region the peering is billed in. ⚠ It must be the region both "
                    + "virtual networks are in — nothing checks that, because neither network's own "
                    + "region is readable from here."
                ) {
                    Format = SchemaFormat.Region,
                    Widget = WidgetHint.Region,
                    Immutable = true,
                    ExampleJson = "\"eu-central\""
                },
                new("/properties", SchemaKind.Nested, Description: "The peering's own settings."),
                new(
                    ClusterIdPointer,
                    SchemaKind.Text,
                    true,
                    Description: "The cluster whose fabric holds both networks. ⚠ Two networks in two "
                    + "clusters cannot be peered: a Kube-OVN peering is two ports on one OVN "
                    + "northbound database."
                ) { Format = SchemaFormat.Uuid, Widget = WidgetHint.Cluster, Immutable = true },
                new(
                    "/properties/remoteNetwork",
                    SchemaKind.Text,
                    true,
                    Description: "The name of the virtualNetworks resource in the same resource group "
                    + "to peer this network with. ⚠ A name, not a resource id: the remote must be in "
                    + "this subscription and resource group, and a network in another cannot be "
                    + "named — write on the peering has to imply write on both networks. A remote "
                    + "that does not exist keeps the peering in progress until it does. Cannot be "
                    + "changed once created; delete the peering and create another."
                ) {
                    Pattern = ResourceNaming.Pattern,
                    MaxLength = ResourceNaming.MaxLength,
                    Immutable = true,
                    DefaultJson = "\"" + DefaultRemoteNetwork + "\"",
                    ExampleJson = "\"spoke\""
                },
                new(
                    "/properties/localAddressSpace",
                    SchemaKind.Nested,
                    Description: "The range this network advertises to the remote — the remote's "
                    + "router gets one static route to it."
                ),
                new(
                    "/properties/localAddressSpace/v4",
                    SchemaKind.Text,
                    true,
                    Description: "This network's IPv4 range, in CIDR form — normally its address space. "
                    + "⚠ It may not overlap the remote range or the link, and the refusal names both."
                ) {
                    Pattern = Cidr.V4Pattern,
                    Widget = WidgetHint.Cidr,
                    DefaultJson = "\"" + DefaultLocalAddressSpace + "\"",
                    ExampleJson = "\"10.20.0.0/16\""
                },
                new(
                    "/properties/remoteAddressSpace",
                    SchemaKind.Nested,
                    Description: "The range the remote advertises to this network — this network's "
                    + "router gets one static route to it."
                ),
                new(
                    "/properties/remoteAddressSpace/v4",
                    SchemaKind.Text,
                    true,
                    Description: "The remote network's IPv4 range, in CIDR form — normally its address "
                    + "space. ⚠ Two networks with overlapping ranges cannot be peered, which is the "
                    + "one place this platform's 'overlapping your own networks is fine' stops "
                    + "applying: a route to a range you also hold has nowhere to go."
                ) {
                    Pattern = Cidr.V4Pattern,
                    Widget = WidgetHint.Cidr,
                    DefaultJson = "\"" + DefaultRemoteAddressSpace + "\"",
                    ExampleJson = "\"10.30.0.0/16\""
                },
                new(
                    "/properties/link",
                    SchemaKind.Nested,
                    Description: "The point-to-point range the two routers address each other on."
                ),
                new(
                    "/properties/link/v4",
                    SchemaKind.Text,
                    true,
                    Description: "A small IPv4 range, /30 or wider, that is in neither network. This "
                    + "network's peer port takes its first host address and the remote's takes the "
                    + "second. ⚠ It is checked against the platform's reserved ranges like any "
                    + "other, so link-local space cannot be used."
                ) {
                    Pattern = Cidr.V4Pattern,
                    Widget = WidgetHint.Cidr,
                    DefaultJson = "\"" + DefaultLinkAddressSpace + "\"",
                    ExampleJson = "\"10.255.255.0/30\""
                }
            ]
        );

    /// <summary>The pointers <see cref="Schema2026" /> declares, in declaration order.</summary>
    public static ImmutableArray<string> Pointers2026 { get; } =
        [.. Schema2026.Properties.Select(static x => x.JsonPointer)];

    /// <summary>
    ///     What a <c>POST …/showRoutes</c> returns.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Per side, and "written" and "connected" are different columns.</b> <c>localWritten</c>
    ///     is whether the local <c>Vpc</c> carries this peering's fragment as the platform rendered
    ///     it; <c>localConnected</c> is whether the controller lists the remote in
    ///     <c>status.vpcPeerings</c>, which is the only evidence OVN built the port. On a cluster
    ///     with no Kube-OVN controller the first is true and the second is false for the life of the
    ///     resource, which is the honest answer.
    /// </remarks>
    public static ResourceSchema RoutesResponse { get; } =
        ResourceSchema.Of(
            [
                new("/localVpc", SchemaKind.Text, true, Description: "The local network's Vpc object name."),
                new(
                    "/remoteVpc",
                    SchemaKind.Text,
                    true,
                    Description: "The remote network's Vpc object name."
                ),
                new(
                    "/localConnectIP",
                    SchemaKind.Text,
                    true,
                    Description: "The address, with the link's prefix, this network's peer port carries."
                ),
                new(
                    "/remoteConnectIP",
                    SchemaKind.Text,
                    true,
                    Description: "The address, with the link's prefix, the remote's peer port carries."
                ),
                new(
                    "/localWritten",
                    SchemaKind.Boolean,
                    true,
                    Description: "Whether the local Vpc carries this peering's entry and route."
                ),
                new(
                    "/remoteWritten",
                    SchemaKind.Boolean,
                    true,
                    Description: "Whether the remote Vpc carries this peering's entry and route. ⚠ "
                    + "False with localWritten true is a remote network that was deleted or never "
                    + "existed."
                ),
                new(
                    "/localConnected",
                    SchemaKind.Boolean,
                    true,
                    Description: "Whether the fabric lists the remote in the local Vpc's "
                    + "status.vpcPeerings — the peer port exists. False on a cluster without the "
                    + "Kube-OVN controller."
                ),
                new(
                    "/remoteConnected",
                    SchemaKind.Boolean,
                    true,
                    Description: "The same, read off the remote Vpc."
                ),
                new(
                    "/sampledAt",
                    SchemaKind.Text,
                    true,
                    Description: "When the platform read the objects, RFC 3339."
                ) { Format = SchemaFormat.DateTime }
            ]
        );

    // ── The desired body, read ────────────────────────────────────────────────────────────────

    /// <summary>The region a body declares.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string Location(JsonElement desired) => VirtualNetworks.Location(desired);

    /// <summary>The remote network's resource name.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string RemoteNetwork(JsonElement desired) =>
        Text(Property(desired, "remoteNetwork"), DefaultRemoteNetwork);

    /// <summary>The range this network advertises.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string LocalAddressSpaceV4(JsonElement desired) =>
        Text(Member(desired, "localAddressSpace", "v4"), DefaultLocalAddressSpace);

    /// <summary>The range the remote advertises.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string RemoteAddressSpaceV4(JsonElement desired) =>
        Text(Member(desired, "remoteAddressSpace", "v4"), DefaultRemoteAddressSpace);

    /// <summary>The link range.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string LinkV4(JsonElement desired) => Text(Member(desired, "link", "v4"), DefaultLinkAddressSpace);

    /// <summary>
    ///     What is wrong with a body's ranges, or <see langword="null" /> when nothing is.
    /// </summary>
    /// <param name="id">The peering's address — the local network's name comes from here.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             Runs after the <c>202</c>, for the reason <see cref="NetworkAddressing" /> gives:
    ///             nothing on the write path lets a provider compare two properties.
    ///         </b> Every refusal is
    ///         terminal — a body whose ranges overlap can never converge — and names the pointer and
    ///         both values, which is docs/plan/08 § Errors' standard.
    ///     </para>
    ///     <para>
    ///         The rules, in order: the remote is not the local network; each of the three ranges
    ///         parses and overlaps no reserved range in the region
    ///         (<see cref="NetworkAddressing.ProblemWith" />); the link is <c>/30</c> or wider; and
    ///         no two of the three overlap.
    ///     </para>
    /// </remarks>
    public static string? AddressProblem(ResourceId id, JsonElement desired) {
        var local = NetworkOf(id);
        var remote = RemoteNetwork(desired);

        if (string.Equals(local, remote, StringComparison.Ordinal)) {
            return $"'/properties/remoteNetwork' is '{remote}', which is this peering's own network. A "
                + "network cannot peer with itself: the peer port would be named "
                + $"'{PeerPortOf(local, local)}' on both ends of one router.";
        }

        var region = Location(desired);

        var ranges = new (string Pointer, string Prefix)[] {
            ("/properties/localAddressSpace/v4", LocalAddressSpaceV4(desired)),
            ("/properties/remoteAddressSpace/v4", RemoteAddressSpaceV4(desired)),
            ("/properties/link/v4", LinkV4(desired))
        };

        foreach (var (pointer, prefix) in ranges) {
            if (NetworkAddressing.ProblemWith(prefix, region, pointer) is { } reserved) {
                return reserved;
            }
        }

        // Every one parsed a moment ago, or ProblemWith would have refused it by name.
        var parsed = ranges.Select(static x => (x.Pointer, x.Prefix,
                Cidr: Cidr.TryParse(x.Prefix, out var cidr) ? cidr : default)
        )
            .ToArray();

        if (parsed.Any(static x => x.Cidr.Network is null || x.Cidr.IsV6)) {
            return "a peering's ranges are IPv4 at this api-version — see "
                + "charts/managed/kube-ovn-vpc-peering/conformance.yaml § owed, peering-is-ipv4-only.";
        }

        if (parsed[2].Cidr.PrefixLength > MaxLinkPrefixLength) {
            return $"'/properties/link/v4' is '{parsed[2].Prefix}', and a link needs two host addresses "
                + $"— one per peer port — so it must be /{MaxLinkPrefixLength.ToString(CultureInfo.InvariantCulture)} "
                + "or wider.";
        }

        for (var i = 0; i < parsed.Length; i++) {
            for (var j = i + 1; j < parsed.Length; j++) {
                if (parsed[i].Cidr.Overlaps(parsed[j].Cidr)) {
                    return $"'{parsed[i].Pointer}' is '{parsed[i].Prefix}' and '{parsed[j].Pointer}' is "
                        + $"'{parsed[j].Prefix}', and the two overlap. A peering exchanges routes between "
                        + "disjoint ranges — a route to a range this network also holds has nowhere to "
                        + "go. Choose ranges that do not overlap each other; overlapping another of your "
                        + "networks that is not this peering's remote is still fine.";
                }
            }
        }

        return null;
    }

    // ── The link, split into two peer addresses ───────────────────────────────────────────────

    /// <summary>This network's peer-port address, with the link's prefix — <c>localConnectIP</c> on the local side.</summary>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     ⚠ <b>The first host address, and the peering decides which side is first.</b> Both
    ///     fragments are rendered by one resource, so the assignment is deterministic and needs no
    ///     agreement: the local side is the network the peering hangs off, the remote is the one its
    ///     body names.
    /// </remarks>
    public static string LocalConnectIP(JsonElement desired) => HostAt(LinkV4(desired), 1, true);

    /// <summary>The remote network's peer-port address, with the link's prefix.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string RemoteConnectIP(JsonElement desired) => HostAt(LinkV4(desired), 2, true);

    /// <summary>The bare address the local side's route to the remote range points at — the remote's port.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string LocalNextHop(JsonElement desired) => HostAt(LinkV4(desired), 2, false);

    /// <summary>The bare address the remote side's route to the local range points at — this network's port.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string RemoteNextHop(JsonElement desired) => HostAt(LinkV4(desired), 1, false);

    // ── The two fragments a desired body becomes ──────────────────────────────────────────────

    /// <summary>The slice of the <b>local</b> network's <c>Vpc</c> this peering contributes.</summary>
    /// <param name="ns">The resource's namespace, used as a name component of the remote's object name.</param>
    /// <param name="id">The peering's address.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A fragment, not an object.</b> No <c>kind</c>, no <c>metadata</c>, no labels: the
    ///         co-owned builder refuses every one of those by name, because they say whose the object
    ///         is and that is the network's to say. What is here is exactly the two array entries
    ///         Kube-OVN reads, and the builder concatenates them with every other peering's.
    ///     </para>
    ///     <para>
    ///         ⚠ No <c>ecmpMode</c>, <c>bfdId</c> or <c>routeTable</c> on the route: each is a
    ///         property of how a region's fabric is wired rather than something a tenant chooses,
    ///         and <c>routeTable</c> in particular is the bare string <c>route-tables-have-no-object</c>
    ///         is about.
    ///     </para>
    /// </remarks>
    public static string LocalFragmentJson(string ns, ResourceId id, JsonElement desired) =>
        Fragment(
            RemoteVpcNameOf(ns, desired),
            LocalConnectIP(desired),
            RemoteAddressSpaceV4(desired),
            LocalNextHop(desired)
        );

    /// <summary>The slice of the <b>remote</b> network's <c>Vpc</c> this peering contributes — the mirror image.</summary>
    /// <param name="ns">The resource's namespace, used as a name component of the local network's object name.</param>
    /// <param name="id">The peering's address.</param>
    /// <param name="desired">The validated desired body.</param>
    public static string RemoteFragmentJson(string ns, ResourceId id, JsonElement desired) =>
        Fragment(
            LocalVpcNameOf(ns, id),
            RemoteConnectIP(desired),
            LocalAddressSpaceV4(desired),
            RemoteNextHop(desired)
        );

    static string Fragment(string remoteVpc, string localConnectIP, string cidr, string nextHop) =>
        new JsonObject {
            ["spec"] = new JsonObject {
                ["vpcPeerings"] = new JsonArray(
                    new JsonObject { ["remoteVpc"] = remoteVpc, ["localConnectIP"] = localConnectIP }
                ),
                ["staticRoutes"] = new JsonArray(
                    new JsonObject { ["policy"] = RoutePolicy, ["cidr"] = cidr, ["nextHopIP"] = nextHop }
                )
            }
        }.ToJsonString();

    /// <summary>Which of the two objects a read-back is being checked as.</summary>
    public enum Side {
        /// <summary>The parent network's <c>Vpc</c>.</summary>
        Local,

        /// <summary>The network the body names.</summary>
        Remote
    }

    /// <summary>
    ///     Whether a <c>Vpc</c> read back from a cluster carries this peering's slice for one side.
    /// </summary>
    /// <param name="objectJson">The object's JSON, exactly as the API server returned it.</param>
    /// <param name="ns">The resource's namespace, used as a name component.</param>
    /// <param name="id">The peering's address.</param>
    /// <param name="desired">The desired body.</param>
    /// <param name="side">Which network's object <paramref name="objectJson" /> is.</param>
    /// <remarks>
    ///     ⚠ <b>Containment over two lists, and the lists are shared.</b> Other peerings' entries
    ///     sit in the same arrays, the controller fills <c>policy</c> in and adds a finalizer, and
    ///     the owner's own <c>enableExternal</c> is beside them; what this asks is only whether
    ///     <i>this</i> peering's entry and route are present. The route's prefix is compared parsed,
    ///     for <see cref="Cidr.Canonical" />'s reason.
    /// </remarks>
    public static bool Matches(string objectJson, string ns, ResourceId id, JsonElement desired, Side side) {
        if (Spec(objectJson) is not { } spec) {
            return false;
        }

        var (remoteVpc, connect, cidr, nextHop) = side == Side.Local
            ? (RemoteVpcNameOf(ns, desired), LocalConnectIP(desired), RemoteAddressSpaceV4(desired),
                LocalNextHop(desired))
            : (LocalVpcNameOf(ns, id), RemoteConnectIP(desired), LocalAddressSpaceV4(desired), RemoteNextHop(desired));

        var peered = spec["vpcPeerings"] is JsonArray peerings
            && peerings.OfType<JsonObject>()
                .Any(x => x["remoteVpc"]?.GetValue<string>() == remoteVpc
                    && x["localConnectIP"]?.GetValue<string>() == connect
                );

        if (!peered || !Cidr.TryParse(cidr, out var wanted)) {
            return false;
        }

        return spec["staticRoutes"] is JsonArray routes
            && routes.OfType<JsonObject>()
                .Any(x => Cidr.TryParse(x["cidr"]?.GetValue<string>(), out var found)
                    && found == wanted
                    && x["nextHopIP"]?.GetValue<string>() == nextHop
                    && x["policy"]?.GetValue<string>() is null or RoutePolicy
                );
    }

    /// <summary>Whether a <c>Vpc</c> document carries <paramref name="writer" />'s fragment annotation.</summary>
    /// <param name="objectJson">The object's JSON.</param>
    /// <param name="writer">The peering's GUID.</param>
    public static bool CarriesFragmentOf(string objectJson, Guid writer) =>
        Parse(objectJson) is { } document
        && document["metadata"] is JsonObject metadata
        && metadata["annotations"] is JsonObject annotations
        && annotations.ContainsKey(KubeLabels.FragmentAnnotation(writer));

    /// <summary>The remote names the controller has connected — <c>status.vpcPeerings</c>, or empty.</summary>
    /// <param name="objectJson">The object's JSON.</param>
    public static ImmutableArray<string> ConnectedPeers(string objectJson) =>
        Parse(objectJson) is { } document
        && document["status"] is JsonObject status
        && status["vpcPeerings"] is JsonArray peers
            ? [.. peers.OfType<JsonValue>().Select(static x => x.GetValue<string>())]
            : [];

    /// <summary>The <c>spec</c> of a <c>Vpc</c> document, or <see langword="null" />.</summary>
    static JsonObject? Spec(string objectJson) =>
        Parse(objectJson) is { } document && document["spec"] is JsonObject spec ? spec : null;

    static JsonObject? Parse(string objectJson) {
        JsonNode? parsed;
        try {
            parsed = JsonNode.Parse(objectJson);
        } catch (JsonException) {
            return null;
        }

        return parsed is JsonObject document
            && document["kind"]?.GetValue<string>() is null or "Vpc"
                ? document
                : null;
    }

    // ── A body, for tests, fixtures and the conformance case ──────────────────────────────────

    /// <summary>Builds a body that satisfies <see cref="Schema2026" />.</summary>
    /// <param name="clusterId">The cluster both networks are in.</param>
    /// <param name="remoteNetwork">The remote network's name.</param>
    /// <param name="localAddressSpaceV4">The range this network advertises.</param>
    /// <param name="remoteAddressSpaceV4">The range the remote advertises.</param>
    /// <param name="linkV4">The link range.</param>
    /// <param name="location">The region.</param>
    /// <remarks>
    ///     ⚠ Every property it writes is a <b>leaf</b>, for the reason every provider's <c>Body</c>
    ///     gives: <c>ResourceSchema.Project</c> skips a <see cref="SchemaKind.Nested" /> container and
    ///     rebuilds it from whichever leaf lands first.
    /// </remarks>
    public static string Body(
        Guid clusterId,
        string remoteNetwork = DefaultRemoteNetwork,
        string localAddressSpaceV4 = DefaultLocalAddressSpace,
        string remoteAddressSpaceV4 = DefaultRemoteAddressSpace,
        string linkV4 = DefaultLinkAddressSpace,
        string location = "eu-central"
    ) =>
        new JsonObject {
            ["location"] = location,
            ["properties"] = new JsonObject {
                ["clusterId"] = clusterId.ToString("D", CultureInfo.InvariantCulture),
                ["remoteNetwork"] = remoteNetwork,
                ["localAddressSpace"] = new JsonObject { ["v4"] = localAddressSpaceV4 },
                ["remoteAddressSpace"] = new JsonObject { ["v4"] = remoteAddressSpaceV4 },
                ["link"] = new JsonObject { ["v4"] = linkV4 }
            }
        }.ToJsonString();

    // ── Address arithmetic ────────────────────────────────────────────────────────────────────

    /// <summary>The <paramref name="offset" />th address above a v4 prefix's network address.</summary>
    /// <param name="prefix">The link, as the body spells it.</param>
    /// <param name="offset">1 for the first host, 2 for the second.</param>
    /// <param name="withPrefix">
    ///     Whether to append <c>/length</c>, which <c>localConnectIP</c> wants and <c>nextHopIP</c>
    ///     refuses.
    /// </param>
    /// <remarks>
    ///     ⚠ Answers empty for a link that does not parse or is not IPv4 rather than throwing, so a
    ///     body <see cref="AddressProblem" /> is about to refuse renders nothing that looks like an
    ///     address on the way to the refusal.
    /// </remarks>
    static string HostAt(string prefix, int offset, bool withPrefix) {
        if (!Cidr.TryParse(prefix, out var link) || link.IsV6) {
            return string.Empty;
        }

        var bytes = link.Network.GetAddressBytes();
        var value = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
        value += (uint)offset;

        var host = new IPAddress(
            [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]
        ).ToString();

        return withPrefix ? host + "/" + link.PrefixLength.ToString(CultureInfo.InvariantCulture) : host;
    }

    // ── Reading one pointer out of a body ─────────────────────────────────────────────────────

    static JsonElement? Property(JsonElement desired, string name) =>
        desired.ValueKind is JsonValueKind.Object
        && desired.TryGetProperty("properties", out var properties)
        && properties.ValueKind is JsonValueKind.Object
        && properties.TryGetProperty(name, out var value)
            ? value
            : null;

    static JsonElement? Member(JsonElement desired, string parent, string name) =>
        Property(desired, parent) is { ValueKind: JsonValueKind.Object } section
        && section.TryGetProperty(name, out var value)
            ? value
            : null;

    static string Text(JsonElement? value, string fallback) =>
        value is { ValueKind: JsonValueKind.String } text && text.GetString() is { Length: > 0 } read
            ? read
            : fallback;
}
