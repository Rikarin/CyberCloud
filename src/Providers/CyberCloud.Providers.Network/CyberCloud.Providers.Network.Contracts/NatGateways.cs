using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Network.Contracts;

/// <summary>
///     Everything addressable about <c>CyberCloud.Network/virtualNetworks/natGateways</c> — one
///     subnet's egress to the internet through one public address, as a Kube-OVN <c>OvnSnatRule</c>.
/// </summary>
/// <remarks>
///     <para>
///         <b>The authority is docs/plan/14 § Everything else</b>, whose row reads
///         <i>"<c>natGateways</c> · M2 · Kube-OVN <c>VpcNatGateway</c> + an SNAT address. Needed the
///         moment a private subnet wants outbound"</i>, and
///         <c>charts/managed/kube-ovn-eip/conformance.yaml</c>, which has said since the address type
///         shipped that <i>"§ Everything else has natGateways use one for SNAT"</i>.
///     </para>
///     <para>
///         ⚠
///         <b>
///             NOT A <c>VpcNatGateway</c>, AND docs/plan/14 IS CORRECTED RATHER THAN FOLLOWED ON THAT
///             ONE WORD.
///         </b> Read firsthand in <c>pkg/apis/kubeovn/v1/vpc-nat-gateway.go</c> at
///         <c>v1.16.2</c>: a <c>VpcNatGateway</c> <i>"represents a NAT gateway for a VPC, implemented
///         as a StatefulSet Pod"</i>, and the rules it serves are <c>IptablesSnatRule</c>s whose
///         <c>spec.eip</c> names an <c>IptablesEIP</c> — a <b>second</b> public-address kind, allocated
///         by that pod, which <see cref="PublicIpAddresses" /> does not render. A NAT gateway built on
///         it would either leave the platform's own address type unable to serve it or need a second
///         allocator drawing the same scarce meter. Kube-OVN's other NAT is the router's own:
///         <c>OvnSnatRule</c> names an <c>OvnEip</c> in <c>spec.ovnEip</c> and a <c>Subnet</c> in
///         <c>spec.vpcSubnet</c>, and <c>handleAddOvnSnatRule</c> turns the pair into one
///         <c>AddNat(vpc, SNAT, eip, cidr)</c> row on the VPC's logical router — no pod, no second
///         address kind. That is the object that joins the two rows the way the document wants, and it
///         is what this type renders.
///     </para>
///     <para>
///         ⚠
///         <b>
///             THIS IS THE ONLY EGRESS A TENANT SUBNET HAS, AND THE FLAG THAT LOOKS LIKE ONE IS NOT.
///         </b> <c>NetworkSubnets.Schema2026</c> declares <c>natOutgoing</c>, described as
///         <i>"whether workloads in this subnet reach the internet through source NAT"</i>. Read
///         firsthand in <c>pkg/daemon/gateway.go</c> at <c>v1.16.2</c>, <c>isSubnetNeedNat</c> requires
///         <c>subnet.Spec.Vpc == c.config.ClusterRouter</c> — the node-side masquerade honors the
///         flag <b>only for subnets of the default VPC</b>, and every subnet this provider renders binds
///         to a tenant's own <c>Vpc</c>. So on this platform <c>natOutgoing</c> is a control the
///         substrate ignores, which <c>NetworkSubnets</c>' own remarks call <i>"the worst of the three
///         possible outcomes"</i>. It stays declared because the api-version is published; its
///         description now says so, and
///         <c>charts/managed/kube-ovn-subnet/conformance.yaml § owed</c>,
///         <c>nat-outgoing-is-ignored-in-a-tenant-vpc</c>, records it. A tenant subnet that wants
///         outbound creates one of these.
///     </para>
///     <para>
///         ⚠ <b>A CHILD OF <c>virtualNetworks</c>, ON <see cref="LoadBalancers" />' ARGUMENT.</b>
///         docs/plan/14 spells the type with no network segment. Every object this type renders names a
///         subnet of exactly one VPC, and the subnet's object name is <c>{namespace}-{network}-{subnet}</c>
///         — so the network has to come from somewhere, and <see cref="ResourceId.ParentNames" /> is
///         the one place it cannot be wrong. A top-level spelling would need a network-name property
///         that nothing validates and that could name another tenant's network.
///     </para>
///     <para>
///         ⚠
///         <b>
///             THE TWO JOINS ARE NAMES IN THE SAME RESOURCE GROUP, AND THAT IS WHAT MAKES THEM
///             DERIVABLE WITHOUT THE READER THIS FAMILY IS OWED.
///         </b> <c>LoadBalancers</c> records that <c>ReconcileContext</c> carries nothing that
///         resolves a resource id, and that attaching a public address is <i>"a resource id this
///         provider would have to resolve through <c>CyberCloud.ResourceManager</c>"</i>. It is — for
///         an address anywhere. For an address in the <b>same subscription and resource group</b>, the
///         rendered <c>OvnEip</c> name is <see cref="PublicIpAddresses.ObjectNameOf" /> of this
///         resource's own namespace and the address's name, which is a pure function of two strings
///         the reconciler already holds. The same is true of the subnet through
///         <see cref="NetworkSubnets.ObjectNameOf(string, string, string)" />. So this type joins by
///         <i>name</i> and what it cannot do is name an address in another resource group —
///         <c>charts/managed/kube-ovn-snat/conformance.yaml § owed</c>,
///         <c>the-address-must-be-in-the-same-resource-group</c>. ⚠ Whether either name exists is a
///         fact in the cluster, checked by the fabric after the <c>202</c>: <c>handleAddOvnSnatRule</c>
///         fails with <i>"failed to get eip"</i> or <i>"failed to get vpc subnet"</i> and retries, and the
///         resource sits in <c>InProgress</c> — the same shape as
///         <c>an-address-is-not-checked-against-the-pool-before-202</c>.
///     </para>
///     <para>
///         ⚠
///         <b>
///             THE RULE IS IMMUTABLE ONCE READY, WHICH IS THE ADDRESS TYPE'S FINDING ON A SECOND
///             KIND.
///         </b> Read firsthand in <c>pkg/controller/ovn_snat.go</c> at <c>v1.16.2</c>:
///         <c>handleAddOvnSnatRule</c> returns at <i>"already ok"</i> once <c>status.ready</c> is set,
///         and <c>handleUpdateOvnSnatRule</c> recomputes the VPC, the address and the CIDR from the
///         new spec and refuses every difference by name — <i>"vpc changed"</i>, <i>"v4 eip
///         changed"</i>, <i>"v6 eip changed"</i>, <i>"v4 ip cidr changed"</i>, <i>"v6 ip cidr
///         changed"</i>. Both body properties are therefore <c>Immutable</c>, and what the shared
///         suite can and cannot prove about an update is
///         <c>conformance.yaml § owed</c>, <c>a-nat-rule-cannot-be-changed</c>.
///     </para>
///     <para>
///         ⚠ <b>THE CONTROLLER NEVER WRITES TO <c>.spec</c> ON THIS KIND</b>, which is the first time
///         that has been true in this family. It patches a finalizer, two labels
///         (<c>ovn.kubernetes.io/eip_v4_ip</c>, <c>…/eip_v6_ip</c>), one annotation
///         (<c>ovn.kubernetes.io/vpc_eip</c>) and the status subresource — and it patches the
///         <b>address's</b> object too, writing the rule's name into the <c>OvnEip</c>'s
///         <c>ovn.kubernetes.io/vpc_nat</c> annotation and <c>status.nat</c>. <see cref="Matches" />
///         is still containment, for the labels and the finalizer, but the three spec fields it
///         compares are compared verbatim.
///     </para>
///     <para>
///         ⚠ <b>ONE SUBNET PER NAT GATEWAY, AND SEVERAL MAY SHARE ONE ADDRESS.</b> Azure attaches a
///         NAT gateway to many subnets; here a gateway is one <c>OvnSnatRule</c> and a rule is one
///         subnet. A network with three private subnets makes three of these naming the same address,
///         which the substrate allows — <c>getOvnEipNat</c> lists rules by the
///         <c>ovn.kubernetes.io/eip_v4_ip</c> label and refuses to release the address while any
///         remain. Why not a subnet list: removing a name from a list has to delete the rule that name
///         rendered, and a reconciler that lists its own objects is the reader this family is owed
///         twice already. <c>§ owed</c>, <c>one-subnet-per-nat-gateway</c>.
///     </para>
///     <para>
///         ⚠ <b>EGRESS ALSO NEEDS THE NETWORK'S <c>enableExternal</c>, AND NOTHING CHECKS IT.</b> The
///         NAT row is programmed on the router regardless; packets leave through the external logical
///         router port that <c>enableExternal: true</c> attaches, and without it they are translated
///         and dropped. The network's flag is in another resource's body, unreadable from here —
///         <c>§ owed</c>, <c>egress-needs-the-networks-external-attachment</c>.
///     </para>
///     <para>
///         ⚠ <b>No <c>SupportsSoftDelete</c>, for the family's reason</b> — <c>RestoreAsync</c> and
///         <c>PurgeAsync</c> have no HTTP route, so a window parks the name and holds the quota with
///         no way to use either half.
///     </para>
/// </remarks>
public static class NatGateways {
    /// <summary>The provider namespace — the family's.</summary>
    public const string ProviderNamespace = VirtualNetworks.ProviderNamespace;

    /// <summary>
    ///     The type path. ⚠ <b>A child of <c>virtualNetworks</c>, which docs/plan/14 does not spell.</b>
    /// </summary>
    /// <remarks>See this class's remarks: every object rendered here names a subnet of one VPC.</remarks>
    public const string TypePath = "virtualNetworks/natGateways";

    /// <summary>The one api-version. ⚠ Equal to the rest of the family's.</summary>
    public const string V2026 = VirtualNetworks.V2026;

    /// <summary>The chart this type is the configuration surface of.</summary>
    public const string ChartName = "managed/kube-ovn-snat";

    /// <summary>The pointer <c>RequiresCluster</c> names.</summary>
    public const string ClusterIdPointer = ClusterPlacement.DefaultPointer;

    /// <summary>The type, namespace and path together.</summary>
    public static ResourceTypeName Type { get; } = new(ProviderNamespace, TypePath);

    /// <summary>The action that reports what the fabric is translating, and to what.</summary>
    /// <remarks>
    ///     ⚠ <b>The body names two resources and the answer is two addresses</b>, and the addresses
    ///     exist nowhere but on <c>OvnSnatRule.status</c>: <c>v4Eip</c> is the public address the
    ///     fabric allocated to the named address resource, and <c>v4IpCidr</c> is the subnet's range
    ///     as the fabric resolved it. <c>ready</c> is the field that stops both from being read as
    ///     promises — a rule whose address or subnet does not exist yet reports neither and is not
    ///     ready, which is the state this action exists to make visible.
    /// </remarks>
    public const string EgressAction = "showEgress";

    /// <summary>The permission <see cref="EgressAction" /> checks.</summary>
    /// <remarks>⚠ <c>read</c>. A public address is public and a subnet range is in the tenant's own body.</remarks>
    public const string EgressPermission = "read";

    // ── The object a NAT gateway IS ───────────────────────────────────────────────────────────

    /// <summary>The Kube-OVN <c>OvnSnatRule</c> custom resource.</summary>
    /// <remarks>
    ///     ⚠ <b>The plural is <c>ovn-snat-rules</c>, HYPHENATED</b> — read firsthand from
    ///     <c>pkg/apis/kubeovn/v1/ovn-snat-rule.go</c>:
    ///     <c>+kubebuilder:resource:scope="Cluster",shortName="osnat",path="ovn-snat-rules",singular="ovn-snat-rule"</c>,
    ///     and confirmed in the CRD the chart installs. Third hyphenated plural in this family;
    ///     <c>ClusterConformanceHarness</c> derives its CRD stub's path from
    ///     <see cref="GroupVersionKind.Plural" />, so a guessed <c>ovnsnatrules</c> installs a
    ///     definition at a path the apply never reaches.
    ///     <para>
    ///         ⚠ <b>Cluster-scoped, like every Kube-OVN kind this family renders</b>, so
    ///         <see cref="ObjectRef.Namespace" /> is empty and the separation is inside
    ///         <see cref="ObjectNameOf(string, ResourceId)" />.
    ///     </para>
    /// </remarks>
    public static GroupVersionKind OvnSnatRuleKind { get; } =
        new() { Group = "kubeovn.io", Version = "v1", Kind = "OvnSnatRule", Plural = "ovn-snat-rules" };

    /// <summary>
    ///     The name of the <c>OvnSnatRule</c> a NAT gateway renders: its namespace, its network's name
    ///     and its own, joined.
    /// </summary>
    /// <param name="ns">The resource's namespace, used as a name component.</param>
    /// <param name="id">The NAT gateway's address.</param>
    /// <exception cref="ArgumentException"><paramref name="id" /> carries no parent name.</exception>
    /// <remarks>
    ///     ⚠ <b>Three components, for <see cref="NetworkSubnets.ObjectNameOf(string, ResourceId)" />'s
    ///     reason</b>: the object is cluster-scoped, so the namespace separates subscriptions and the
    ///     network's name separates two networks in one resource group that each hold a gateway
    ///     called <c>egress</c>. Dropping either is a silent collision, and on this kind a collision
    ///     is one tenant's subnet translated to another tenant's address.
    /// </remarks>
    public static string ObjectNameOf(string ns, ResourceId id) =>
        id.ParentNames.Length == 0
            ? throw new ArgumentException(
                $"'{id.Path}' carries no parent name, so the OvnSnatRule it renders would collide "
                + "with every other network's NAT gateway of the same name — and an OvnSnatRule is "
                + "CLUSTER-SCOPED, so the collision is platform-wide. A NAT gateway is a child type "
                + "and its address always interleaves its network — see NatGateways.TypePath.",
                nameof(id)
            )
            : ObjectNameOf(ns, id.ParentNames.Replace('/', '-'), id.Name);

    /// <summary>The same three components, joined, for a caller that holds the names.</summary>
    /// <param name="ns">The resource's namespace, used as a name component.</param>
    /// <param name="network">The virtual network's name.</param>
    /// <param name="gateway">The NAT gateway's name.</param>
    public static string ObjectNameOf(string ns, string network, string gateway) =>
        ns + "-" + network + "-" + gateway;

    /// <summary>The parent network's name.</summary>
    /// <param name="id">The NAT gateway's address.</param>
    /// <exception cref="ArgumentException"><paramref name="id" /> has no parent.</exception>
    public static string NetworkOf(ResourceId id) =>
        id.Parent?.Name
        ?? throw new ArgumentException(
            $"'{id.Path}' has no parent, so there is no virtual network whose subnet this rule "
            + "could translate. A NAT gateway is a child type — see NatGateways.TypePath.",
            nameof(id)
        );

    /// <summary>The <c>Vpc</c> object name the rule's <c>spec.vpc</c> carries.</summary>
    /// <param name="ns">The resource's namespace, used as a name component.</param>
    /// <param name="id">The NAT gateway's address.</param>
    /// <remarks>
    ///     ⚠ <b>Read off the address and composed through
    ///     <see cref="VirtualNetworks.ObjectNameOf" /></b>, on <c>NetworkSubnets.VpcRefOf</c>'s rule:
    ///     a second spelling of the parent's object name is the thing that stops agreeing the day the
    ///     parent's naming changes. ⚠ The controller derives the VPC from the subnet and ignores this
    ///     field when <c>vpcSubnet</c> is set; it is sent anyway so that <see cref="Matches" /> has a
    ///     field naming the network to compare, which is <c>spec.vpc</c>'s hazard on the subnet in a
    ///     third shape.
    /// </remarks>
    public static string VpcRefOf(string ns, ResourceId id) => VirtualNetworks.ObjectNameOf(ns, NetworkOf(id));

    /// <summary>The <c>Subnet</c> object name the rule translates.</summary>
    /// <param name="ns">The resource's namespace, a name component of the cluster-scoped Subnet.</param>
    /// <param name="id">The NAT gateway's address.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     ⚠ <b>The network half comes from the address and only the subnet half from the body</b>,
    ///     exactly as <c>LoadBalancers.LogicalSwitchOf</c> does — so a body cannot name a subnet in
    ///     another network, and the worst failure available here (translating a subnet in another
    ///     tenant's VPC out through this tenant's address) is not expressible.
    /// </remarks>
    public static string VpcSubnetOf(string ns, ResourceId id, JsonElement desired) =>
        NetworkSubnets.ObjectNameOf(ns, NetworkOf(id), Subnet(desired));

    /// <summary>The <c>OvnEip</c> object name the rule translates to.</summary>
    /// <param name="ns">The resource's namespace, a name component of the cluster-scoped OvnEip.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     ⚠ <b>Composed through <see cref="PublicIpAddresses.ObjectNameOf" /></b> rather than spelled
    ///     again, and it is the namespace that makes the join safe: an address resource in another
    ///     subscription renders a differently-named <c>OvnEip</c>, so a body can only ever name an
    ///     address this resource group holds.
    /// </remarks>
    public static string OvnEipOf(string ns, JsonElement desired) =>
        PublicIpAddresses.ObjectNameOf(ns, PublicIpAddress(desired));

    /// <summary>The <c>OvnSnatRule</c> a NAT gateway owns.</summary>
    /// <param name="ns">The resource's namespace, used as a name component.</param>
    /// <param name="id">The NAT gateway's address.</param>
    /// <remarks>⚠ Cluster-scoped: <c>Namespace</c> is empty. See <see cref="VirtualNetworks.VpcRef" />.</remarks>
    public static ObjectRef OvnSnatRuleRef(string ns, ResourceId id) =>
        new() { Kind = OvnSnatRuleKind, Namespace = string.Empty, Name = ObjectNameOf(ns, id) };

    // ── The body shape ────────────────────────────────────────────────────────────────────────

    /// <summary>The default subnet name. ⚠ The family's example subnet, so the fixtures cohere.</summary>
    public const string DefaultSubnet = "web";

    /// <summary>The default address name.</summary>
    /// <remarks>
    ///     A required patterned property must carry a default its own pattern accepts, or the
    ///     generated chart's literal fails <c>helm lint</c> — the interaction <c>NetworkSubnets</c>
    ///     recorded. It is a plausible name rather than a placeholder, because the chart renders with it.
    /// </remarks>
    public const string DefaultPublicIpAddress = "egress";

    /// <summary>
    ///     The body shape at <see cref="V2026" />.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>TWO NAMES AND NOTHING ELSE, AND THAT IS THE TYPE.</b> An <c>OvnSnatRule</c> is a
    ///         join: which subnet, which address. There is no port, no protocol and no size, because
    ///         source NAT on an OVN router translates every packet from the range and consumes no pod.
    ///         What a reader expects and does not find — a CIDR to translate — is deliberately not
    ///         here: <c>spec.v4IpCidr</c> is the substrate's escape hatch for a range that is not a
    ///         subnet, and offering it would let a body name a range in another tenant's VPC.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>BOTH PROPERTIES ARE <c>Immutable</c>, AND THE MANAGER DOES NOT ENFORCE THAT.</b>
    ///         <c>SchemaProperty.Immutable</c> is a declaration — its own remarks say so — so a
    ///         <c>PUT</c> that changes one is accepted, applied and reported <c>Succeeded</c> while
    ///         <c>handleUpdateOvnSnatRule</c> refuses it by name. See this class's remarks.
    ///     </para>
    /// </remarks>
    public static ResourceSchema Schema2026 { get; } =
        ResourceSchema.Of(
            [
                new(
                    "/location",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The region the NAT gateway is billed in. ⚠ It must be the region "
                    + "its virtual network is in — nothing checks that, because the network's own "
                    + "region is not readable from here."
                ) {
                    Format = SchemaFormat.Region,
                    Widget = WidgetHint.Region,
                    Immutable = true,
                    ExampleJson = "\"eu-central\""
                },
                new("/properties", SchemaKind.Nested, Description: "The NAT gateway's own settings."),
                new(
                    ClusterIdPointer,
                    SchemaKind.Text,
                    Required: true,
                    Description: "The cluster whose fabric holds the network. ⚠ It must be the cluster "
                    + "the virtual network and the public address were created in: a rule in another "
                    + "cluster names a subnet and an address that do not exist there."
                ) { Format = SchemaFormat.Uuid, Widget = WidgetHint.Cluster, Immutable = true },
                new(
                    "/properties/subnet",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The subnet of this virtual network whose workloads egress through "
                    + "the address. One subnet per NAT gateway; a network with several private "
                    + "subnets creates one per subnet, and they may share the address. ⚠ A name that "
                    + "is not a subnet of this network is refused by the fabric rather than by the "
                    + "API, and the gateway never becomes ready."
                ) {
                    Pattern = ResourceNaming.Pattern,
                    MaxLength = ResourceNaming.MaxLength,
                    Widget = WidgetHint.Subnet,
                    Immutable = true,
                    DefaultJson = "\"" + DefaultSubnet + "\"",
                    ExampleJson = "\"web\""
                },
                new(
                    "/properties/publicIpAddress",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The name of a publicIpAddresses resource in the same resource group "
                    + "whose address the subnet's traffic leaves with. ⚠ A name, not a resource id: "
                    + "the address must be in this subscription and resource group, and one in "
                    + "another cannot be named. An address that does not exist is refused by the "
                    + "fabric rather than by the API."
                ) {
                    Pattern = ResourceNaming.Pattern,
                    MaxLength = ResourceNaming.MaxLength,
                    Immutable = true,
                    DefaultJson = "\"" + DefaultPublicIpAddress + "\"",
                    ExampleJson = "\"egress\""
                }
            ]
        );

    /// <summary>The pointers <see cref="Schema2026" /> declares, in declaration order.</summary>
    public static ImmutableArray<string> Pointers2026 { get; } = [.. Schema2026.Properties.Select(x => x.JsonPointer)];

    /// <summary>
    ///     What a <c>POST …/showEgress</c> returns.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Every figure is off <c>OvnSnatRule.status</c>, written by the controller once it has
    ///     resolved both joins.</b> Before that the four addresses are empty and <c>ready</c> is
    ///     false, which is the honest answer to "why is nothing leaving my subnet" during the first
    ///     seconds and forever when the address or the subnet name is wrong.
    /// </remarks>
    public static ResourceSchema EgressResponse { get; } =
        ResourceSchema.Of(
            [
                new(
                    "/publicV4",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The IPv4 address the subnet's traffic leaves with, or empty until "
                    + "the fabric has resolved the named public address."
                ),
                new(
                    "/publicV6",
                    SchemaKind.Text,
                    Description: "The IPv6 address the subnet's traffic leaves with, or empty for an "
                    + "IPv4-only pool."
                ),
                new(
                    "/sourceV4",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The IPv4 range being translated — the subnet's prefix as the fabric "
                    + "resolved it, or empty until it has."
                ),
                new(
                    "/sourceV6",
                    SchemaKind.Text,
                    Description: "The IPv6 range being translated, or empty for an IPv4-only subnet."
                ),
                new(
                    "/ready",
                    SchemaKind.Boolean,
                    Required: true,
                    Description: "Whether the fabric has programmed the translation. ⚠ False with "
                    + "every address empty is a rule whose subnet or public address the fabric "
                    + "cannot find — check both names."
                ),
                new(
                    "/sampledAt",
                    SchemaKind.Text,
                    Required: true,
                    Description: "When the platform read the object, RFC 3339."
                ) { Format = SchemaFormat.DateTime }
            ]
        );

    // ── The desired body, read ────────────────────────────────────────────────────────────────

    /// <summary>The region a body declares.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string Location(JsonElement desired) => VirtualNetworks.Location(desired);

    /// <summary>The subnet the rule translates.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string Subnet(JsonElement desired) => Text(desired, "subnet", DefaultSubnet);

    /// <summary>The address resource the rule translates to.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string PublicIpAddress(JsonElement desired) =>
        Text(desired, "publicIpAddress", DefaultPublicIpAddress);

    // ── The object a desired body becomes ─────────────────────────────────────────────────────

    /// <summary>The <c>OvnSnatRule</c> document a desired body becomes.</summary>
    /// <param name="ns">The resource's namespace, used as a name component three times over.</param>
    /// <param name="id">The NAT gateway's address — its network's name comes from here.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>NO <c>v4IpCidr</c>, NO <c>v6IpCidr</c>, NO <c>ipName</c>.</b> Each is the
    ///         substrate's way of translating something that is not a whole subnet — a bare range, or
    ///         one pod's <c>IP</c> object — and each would let a body reach a range this resource's
    ///         address does not vouch for. The controller derives the CIDRs from the subnet when the
    ///         two are absent, which is the only path this type takes.
    ///     </para>
    ///     <para>
    ///         ⚠ No labels, no annotations and no namespace here — ADR-013's seven labels and two
    ///         annotations are injected by <c>KubeCommand</c> non-overridably, and this object is
    ///         cluster-scoped so there is no namespace to write.
    ///     </para>
    /// </remarks>
    public static string OvnSnatRuleJson(string ns, ResourceId id, JsonElement desired) =>
        new JsonObject {
            ["kind"] = OvnSnatRuleKind.Kind,
            ["metadata"] = new JsonObject { ["name"] = ObjectNameOf(ns, id) },
            ["spec"] = new JsonObject {
                ["ovnEip"] = OvnEipOf(ns, desired),
                ["vpcSubnet"] = VpcSubnetOf(ns, id, desired),
                ["vpc"] = VpcRefOf(ns, id)
            }
        }.ToJsonString();

    /// <summary>
    ///     Whether an <c>OvnSnatRule</c> read back from a cluster carries what the desired body asks
    ///     for.
    /// </summary>
    /// <param name="objectJson">The object's JSON, exactly as the API server returned it.</param>
    /// <param name="ns">The resource's namespace, used as a name component.</param>
    /// <param name="id">The NAT gateway's address.</param>
    /// <param name="desired">The desired body.</param>
    /// <remarks>
    ///     ⚠ <b>All three spec fields are compared, and two of them are derived from the address.</b>
    ///     An <c>OvnSnatRule</c> whose <c>vpcSubnet</c> or <c>vpc</c> was rewritten is a translation
    ///     of some other range out through this tenant's address, under this tenant's resource id —
    ///     <c>NetworkSubnets.Matches</c>' <c>spec.vpc</c> argument, on the kind where it costs packets.
    ///     Containment, because the controller adds labels and a finalizer; but the controller never
    ///     writes this kind's <c>.spec</c>, so the three fields are compared as strings.
    /// </remarks>
    public static bool Matches(string objectJson, string ns, ResourceId id, JsonElement desired) =>
        Spec(objectJson) is { } spec
        && spec["ovnEip"]?.GetValue<string>() == OvnEipOf(ns, desired)
        && spec["vpcSubnet"]?.GetValue<string>() == VpcSubnetOf(ns, id, desired)
        && spec["vpc"]?.GetValue<string>() == VpcRefOf(ns, id);

    /// <summary>The <c>status</c> of an <c>OvnSnatRule</c> document, or <see langword="null" />.</summary>
    /// <param name="objectJson">The object's JSON.</param>
    public static JsonObject? Status(string objectJson) =>
        Parse(objectJson) is { } document && document["status"] is JsonObject status ? status : null;

    /// <summary>The <c>spec</c> of an <c>OvnSnatRule</c> document, or <see langword="null" />.</summary>
    /// <param name="objectJson">The object's JSON.</param>
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
            && document["kind"]?.GetValue<string>() is (null or "OvnSnatRule")
                ? document
                : null;
    }

    // ── A body, for tests, fixtures and the conformance case ──────────────────────────────────

    /// <summary>Builds a body that satisfies <see cref="Schema2026" />.</summary>
    /// <param name="clusterId">The cluster the network is in.</param>
    /// <param name="subnet">The subnet to translate.</param>
    /// <param name="publicIpAddress">The address resource to translate to.</param>
    /// <param name="location">The region.</param>
    public static string Body(
        Guid clusterId,
        string subnet = DefaultSubnet,
        string publicIpAddress = DefaultPublicIpAddress,
        string location = "eu-central"
    ) =>
        new JsonObject {
            ["location"] = location,
            ["properties"] = new JsonObject {
                ["clusterId"] = clusterId.ToString("D", CultureInfo.InvariantCulture),
                ["subnet"] = subnet,
                ["publicIpAddress"] = publicIpAddress
            }
        }.ToJsonString();

    // ── Reading one pointer out of a body ─────────────────────────────────────────────────────

    static string Text(JsonElement desired, string name, string fallback) =>
        desired.ValueKind is JsonValueKind.Object
        && desired.TryGetProperty("properties", out var properties)
        && properties.ValueKind is JsonValueKind.Object
        && properties.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.String
        && value.GetString() is { Length: > 0 } text
            ? text
            : fallback;
}
