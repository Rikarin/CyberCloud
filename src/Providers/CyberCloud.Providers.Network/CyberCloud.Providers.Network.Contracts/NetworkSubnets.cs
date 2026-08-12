using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Network.Contracts;

/// <summary>
///     Everything addressable about <c>CyberCloud.Network/virtualNetworks/subnets</c> — the range a
///     workload actually gets an address from, as a Kube-OVN <c>Subnet</c>.
/// </summary>
/// <remarks>
///     <para>
///         <b>The authority is docs/plan/14 § Virtual networks</b>, whose tree gives
///         <c>subnets/{name} → prefix, gateway, DHCP, NAT flag</c>, and whose opening sentence is
///         <i>"subnets are <c>Subnet</c>s bound to it"</i>. All four of those are here; the binding is
///         <see cref="VpcRefOf" />.
///     </para>
///     <para>
///         ⚠ <b>THIS IS WHERE A TENANT'S ADDRESS SPACE ACTUALLY IS, AND THE PARENT'S IS NOT.</b> A
///         Kube-OVN <c>Vpc</c> carries no CIDR — <see cref="VirtualNetworks.Schema2026" />'s
///         <c>addressSpace</c> is a declaration the platform checks and does not render. This
///         property <i>is</i> rendered, it is what an address is allocated from, and it is therefore
///         the one the reserved-range check most needs to run against. ⚠ <b>Nothing checks it against
///         its parent's declared address space</b>, because that is a relation between two resources'
///         bodies and <c>ResourceSchema</c> validates each property against constants — the same shape
///         as <c>charts/managed/seaweedfs-bucket</c>'s
///         <c>bucket-cluster-may-differ-from-its-accounts</c>. So a subnet may legally sit outside the
///         network that contains it, which reads as a contradiction and is one; it is recorded at
///         <c>charts/managed/kube-ovn-subnet/conformance.yaml § owed</c>,
///         <c>subnets-are-not-checked-against-the-address-space</c>.
///     </para>
///     <para>
///         ⚠ <b>NOTHING IN THE BODY NAMES THE NETWORK, AND THAT IS THE WHOLE OF A CHILD TYPE.</b> The
///         <i>address</i> names it — <c>…/virtualNetworks/{network}/subnets/{subnet}</c> — and
///         <see cref="ResourceId.Parent" /> is a pure function of that address. A <c>parentNetwork</c>
///         property would be a second spelling of the same fact and the two would disagree the first
///         time a body was sent under the wrong path. The only places the parent's name is read are
///         <see cref="VpcRefOf" /> and <see cref="ObjectNameOf" />, both of which take the id.
///     </para>
///     <para>
///         ⚠ <b>CLUSTER-SCOPED, LIKE ITS PARENT, AND THE NAME HAS TO CARRY THREE THINGS RATHER THAN
///         TWO.</b> <c>pkg/apis/kubeovn/v1/subnet.go</c>:
///         <c>// +kubebuilder:resource:scope="Cluster",shortName="subnet",path="subnets"</c>. So
///         <see cref="ObjectNameOf" /> folds in the namespace <i>and</i> the parent network's name —
///         the namespace because two subscriptions must not collide, and the parent's name because two
///         networks in ONE resource group may each hold a subnet called <c>web</c>. A renderer that
///         folded in only the namespace would have those two fighting over one <c>Subnet</c> object,
///         each converging by overwriting the other, with nothing reporting an error. That is
///         <c>StorageBuckets.ObjectNameOf</c>'s hazard with one more level on it.
///     </para>
///     <para>
///         ⚠ <b>THE CONTROLLER REWRITES THIS OBJECT'S SPEC MORE THAN ANY OTHER IN THE FAMILY, AND
///         <see cref="Matches" /> IS BUILT AROUND THAT FACT RATHER THAN AROUND DEFAULTING.</b>
///         <c>pkg/controller/subnet.go</c>'s <c>formatSubnet</c> and <c>formatAddress</c>, read
///         firsthand, do all of the following on an object this provider applied and then issue a full
///         <c>Subnets().Update(...)</c>:
///     </para>
///     <list type="bullet">
///         <item>
///             <b><c>cidrBlock</c> is CANONICALIZED</b> — each comma-separated element goes through
///             Go's <c>net.ParseCIDR</c> and is written back as <c>ipNet.String()</c>. A tenant who
///             sends <c>10.20.5.7/24</c> gets <c>10.20.5.0/24</c> stored. ⚠ <b>A string comparison
///             here is the bug</b>: it reports drift on a perfectly converged subnet, forever. This is
///             why <see cref="Cidr.Canonical" /> exists and why <see cref="Matches" /> compares parsed
///             networks.
///         </item>
///         <item>
///             <b><c>protocol</c> is ALWAYS overwritten</b> from the CIDR
///             (<c>subnet.Spec.Protocol = util.CheckProtocol(subnet.Spec.CIDRBlock)</c>,
///             unconditionally). So this provider does not send it at all — see
///             <see cref="SubnetJson" />.
///         </item>
///         <item>
///             <b><c>gateway</c> is DERIVED</b> when empty, and in dual-stack a missing family is
///             appended.
///         </item>
///         <item>
///             <b><c>excludeIps</c> GROWS</b> — every gateway IP is appended and the list is
///             <c>sort.Strings()</c>ed in place, so a client that sent none gets one back.
///         </item>
///         <item>
///             <b><c>provider</c>, <c>vpc</c>, <c>gatewayType</c> and <c>enableLb</c> are filled</b>
///             when empty.
///         </item>
///     </list>
///     <para>
///         ⚠ <b>And all of that is quite separate from the usual containment argument, which is FALSE
///         here.</b> The <c>Subnet</c> CRD declares <b>zero</b> <c>default:</c> values — the
///         <c>default:</c> lines in its schema are a property <i>named</i> <c>default</c>
///         (<c>SubnetSpec.Default bool</c>), which is a trap worth naming — and Kube-OVN ships <b>no
///         mutating webhook at all</b>; its only webhook is a validating one that is off in a default
///         install. Three families argue containment from structural defaulting and it would not have
///         applied here; the reason containment is nonetheless mandatory is the controller, which is a
///         third mechanism and the hardest of the three to see.
///     </para>
/// </remarks>
public static class NetworkSubnets {
    /// <summary>The provider namespace — the network's, because a child shares its parent's.</summary>
    public const string ProviderNamespace = VirtualNetworks.ProviderNamespace;

    /// <summary>
    ///     The type path. ⚠ <b><c>virtualNetworks/subnets</c>, interleaved — not a flattened
    ///     <c>subnets</c>.</b>
    /// </summary>
    /// <remarks>
    ///     docs/plan/12 § Child resources chose <c>…/virtualNetworks/{network}/subnets/{subnet}</c>
    ///     over the flattened form for the reason <c>test/CyberCloud.Isolation/ParentEdgeTests</c>
    ///     states: a flattened address has nowhere to put the parent's name, so the ReBAC
    ///     <c>parent</c> edge would have to name the resource <i>group</i> — and granting somebody the
    ///     network would then grant nothing on its subnets. ⚠ On this type the flattened form would
    ///     also be a lie about the substrate: a <c>Subnet</c> that is not bound to a <c>Vpc</c> is a
    ///     subnet of the <i>default</i> VPC, which is the platform's own.
    /// </remarks>
    public const string TypePath = "virtualNetworks/subnets";

    /// <summary>The one api-version. ⚠ Equal to the network's, and it must be.</summary>
    public const string V2026 = VirtualNetworks.V2026;

    /// <summary>The chart this type is the configuration surface of.</summary>
    public const string ChartName = "managed/kube-ovn-subnet";

    /// <summary>The pointer <c>RequiresCluster</c> names.</summary>
    public const string ClusterIdPointer = ClusterPlacement.DefaultPointer;

    /// <summary>The type, namespace and path together.</summary>
    public static ResourceTypeName Type { get; } = new(ProviderNamespace, TypePath);

    /// <summary>The action that reports how much of the subnet's range is allocated.</summary>
    /// <remarks>
    ///     ⚠ <b>THE FIRST ACTION IN THE CATALOGUE WHOSE NUMBERS ARE ALREADY ON THE OBJECT.</b> Every
    ///     other provider's declared-and-unserved action waits on something that does not exist — a
    ///     Vault for <c>listKeys</c>, docs/plan/22's usage pipeline for <c>stats</c>. Kube-OVN's
    ///     <c>Subnet</c> writes <c>status.v4availableIPs</c>, <c>status.v4usingIPs</c> and their v6
    ///     counterparts itself, so the reconciler is already reading the object that holds the answer.
    ///     What is missing is only the <b>handler seam</b>: no provider in the tree has one, actions
    ///     are declared into the registry and routed by the gateway, and there is nowhere to put the
    ///     code. So this is a plumbing gap rather than a data gap, which is a materially better place
    ///     to be and is recorded as such rather than as another blocked action.
    ///     <para>
    ///         ⚠ <b>It is also the number a tenant most needs and cannot otherwise get.</b> "Why did
    ///         my workload fail to schedule" is answered by "this subnet has no addresses left", and
    ///         without this action the only way to learn that is to read a Kubernetes object the
    ///         tenant has no access to.
    ///     </para>
    /// </remarks>
    public const string AddressUsageAction = "listAddressUsage";

    /// <summary>The permission <see cref="AddressUsageAction" /> checks.</summary>
    /// <remarks>⚠ <c>read</c> — a count of free addresses is neither a credential nor a capability.</remarks>
    public const string AddressUsagePermission = "read";

    // ── The object a subnet IS ────────────────────────────────────────────────────────────────

    /// <summary>The Kube-OVN <c>Subnet</c> custom resource.</summary>
    public static GroupVersionKind SubnetKind { get; } =
        new() { Group = "kubeovn.io", Version = "v1", Kind = "Subnet", Plural = "subnets" };

    /// <summary>
    ///     The name of the <c>Subnet</c> a subnet renders: its namespace, its network's name and its
    ///     own, joined.
    /// </summary>
    /// <param name="ns">The resource's namespace, used as a name component.</param>
    /// <param name="id">The subnet's address.</param>
    /// <exception cref="ArgumentException"><paramref name="id" /> carries no parent name.</exception>
    /// <remarks>
    ///     ⚠ <b>THREE COMPONENTS, AND DROPPING ANY ONE OF THEM IS A DIFFERENT SILENT COLLISION.</b>
    ///     Without the namespace, two subscriptions collide; without
    ///     <see cref="ResourceId.ParentNames" />, two networks in one resource group collide; without
    ///     the resource's own name there is nothing left. Every cluster-facing conformance assertion
    ///     depends on this having read the ancestors — an id built without them renders a different
    ///     name and the read-back fails.
    /// </remarks>
    public static string ObjectNameOf(string ns, ResourceId id) =>
        id.ParentNames.Length == 0
            ? throw new ArgumentException(
                $"'{id.Path}' carries no parent name, so the Subnet object it renders would collide "
                + "with every other network's subnet of the same name — and a Subnet is CLUSTER-SCOPED, "
                + "so the collision is platform-wide rather than confined to one namespace. A subnet is "
                + "a child type and its address always interleaves its network — see "
                + "NetworkSubnets.TypePath.",
                nameof(id)
            )
            : ns + "-" + id.ParentNames.Replace('/', '-') + "-" + id.Name;

    /// <summary>The <c>Vpc</c> object name a subnet binds to.</summary>
    /// <param name="ns">The resource's namespace, used as a name component.</param>
    /// <param name="id">The subnet's address.</param>
    /// <remarks>
    ///     ⚠ <b>Read off the ADDRESS and never off the body, and composed through
    ///     <see cref="VirtualNetworks.ObjectNameOf" /> rather than spelled again here.</b> The parent's
    ///     object name is the parent's business; a second spelling of it in this file would be the
    ///     thing that silently stops agreeing the day the parent's naming changes, and the symptom
    ///     would be a subnet bound to a VPC that does not exist — which Kube-OVN treats as a subnet of
    ///     the default VPC, i.e. the platform's own. That is the worst available failure and it would
    ///     be reported by nothing.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="id" /> has no parent.</exception>
    public static string VpcRefOf(string ns, ResourceId id) =>
        id.Parent?.Name is { } parent
            ? VirtualNetworks.ObjectNameOf(ns, parent)
            : throw new ArgumentException(
                $"'{id.Path}' has no parent, so there is no Vpc for its Subnet to bind to. An unbound "
                + "Subnet is a subnet of Kube-OVN's DEFAULT VPC, which is the platform's own — so this "
                + "throws rather than rendering an empty `spec.vpc`.",
                nameof(id)
            );

    /// <summary>The <c>Subnet</c> a subnet owns.</summary>
    /// <param name="ns">The resource's namespace, used as a name component.</param>
    /// <param name="id">The subnet's address.</param>
    /// <remarks>⚠ Cluster-scoped: <c>Namespace</c> is empty. See <see cref="VirtualNetworks.VpcRef" />.</remarks>
    public static ObjectRef SubnetRef(string ns, ResourceId id) =>
        new() { Kind = SubnetKind, Namespace = string.Empty, Name = ObjectNameOf(ns, id) };

    // ── The body shape ────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     The body shape at <see cref="V2026" />.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>TWO PREFIX PROPERTIES RATHER THAN ONE COMMA-SEPARATED STRING, AND THE SUBSTRATE
    ///         WANTS THE COMMA.</b> Kube-OVN spells dual-stack as
    ///         <c>cidrBlock: "10.20.1.0/24,fd00:20:1::/64"</c> — one string, IPv4 first, split on
    ///         <c>,</c> by <c>util.CheckProtocol</c>. Exposing that spelling in the API would make the
    ///         property unpatternable in practice and would put the ordering rule in a description
    ///         where nothing enforces it. Two properties make each one individually patterned,
    ///         individually reportable through its own JSON Pointer when it is wrong, and make "IPv4
    ///         first" a fact of <see cref="CidrBlock" /> rather than of a tenant's typing.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>natOutgoing</c> DEFAULTS TO <c>false</c>, WHICH IS THE OPPOSITE OF KUBE-OVN'S
    ///         OWN CONVENTION AND IS THE SECURITY DECISION.</b> Kube-OVN's default subnet has NAT on,
    ///         because a cluster pod network is expected to reach the internet. A tenant subnet is
    ///         not: docs/plan/12 § Cross-cutting decisions defaults external exposure to off across
    ///         the platform, and a subnet that silently egresses to the internet is the same class of
    ///         surprise as a publicly readable bucket. A tenant who wants egress asks for it. ⚠ Note
    ///         that this is the <i>only</i> field in the family where this provider's default and the
    ///         substrate's convention disagree, which is why it is called out rather than left to be
    ///         inferred from the JSON.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>private</c> IS DECLARED AND <c>allowSubnets</c> IS NOT, WHICH IS HALF A FEATURE
    ///         AND IS DELIBERATE.</b> <c>spec.private</c> isolates a subnet from every other subnet;
    ///         <c>spec.allowSubnets</c> then punches named holes in that. The holes name <i>other
    ///         subnets' CIDRs</i>, which is an array of strings referring to sibling resources — a
    ///         cross-resource reference with no reader (rule 2), and an array whose per-element pattern
    ///         ADR-012's fifth surface refuses. So the isolation switch ships and the exception list
    ///         does not, and the honest reading of <c>private: true</c> in this api-version is "no
    ///         traffic from other subnets at all". <c>§ owed</c>, <c>private-has-no-exceptions</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>NO <c>protocol</c> PROPERTY, AND ITS ABSENCE IS LOAD-BEARING RATHER THAN AN
    ///         OVERSIGHT.</b> Kube-OVN's <c>spec.protocol</c> is <c>IPv4</c>/<c>IPv6</c>/<c>Dual</c>,
    ///         and the controller recomputes it from the CIDR on every pass, unconditionally,
    ///         discarding whatever was sent. A property here would be a control that does nothing —
    ///         the worst of the three possible outcomes, because the document would promise it and the
    ///         substrate would ignore it. The family is implied by which of
    ///         <c>addressPrefix/v4</c> and <c>addressPrefix/v6</c> are set, which is the same
    ///         information without the lie.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>NO <c>gateway</c> PROPERTY EITHER, FOR A DIFFERENT AND WEAKER REASON.</b>
    ///         docs/plan/14's tree lists <c>gateway</c> as a subnet attribute. The controller derives
    ///         it from the CIDR when it is empty and this provider lets it — the first address of the
    ///         range is right for effectively every subnet, and a tenant-set gateway is a field whose
    ///         wrong value produces a subnet that allocates addresses nobody can route from. It is a
    ///         real omission rather than an impossible one, and it is recorded as
    ///         <c>§ owed</c>, <c>gateway-is-not-selectable</c>.
    ///     </para>
    /// </remarks>
    public static ResourceSchema Schema2026 { get; } =
        ResourceSchema.Of(
            [
                new(
                    "/location",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The region the subnet lives in. ⚠ It selects which reserved ranges "
                    + "the prefix is checked against, and it must be the network's own region — "
                    + "nothing checks that."
                ) {
                    Format = SchemaFormat.Region,
                    Widget = WidgetHint.Region,
                    Immutable = true,
                    ExampleJson = "\"eu-central\""
                },
                new("/properties", SchemaKind.Nested, Description: "The subnet's own settings."),
                new(
                    ClusterIdPointer,
                    SchemaKind.Text,
                    Required: true,
                    Description: "The cluster whose fabric carries the subnet. Must be the cluster the "
                    + "network is in — nothing checks that, and a subnet placed elsewhere binds to a "
                    + "Vpc that does not exist there."
                ) {
                    Format = SchemaFormat.Uuid,
                    Widget = WidgetHint.Cluster,
                    Immutable = true
                },
                new(
                    "/properties/addressPrefix",
                    SchemaKind.Nested,
                    Description: "The range addresses are allocated from."
                ),
                new(
                    "/properties/addressPrefix/v4",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The IPv4 prefix, in CIDR form. ⚠ Host bits are cleared by the "
                    + "fabric, so 10.20.1.7/24 is stored as 10.20.1.0/24 and both name the same "
                    + "network. It may not overlap a range the platform reserves, which is refused "
                    + "with the conflicting range named."
                ) {
                    Pattern = Cidr.V4Pattern,
                    Immutable = true,
                    // ⚠ A default on a REQUIRED property — see VirtualNetworks.Schema2026's
                    // `/properties/addressSpace/v4` for the full argument. The short form: without
                    // one, ChartAnnotationEmitter writes `v4: ""` and `./build.sh Charts` refuses the
                    // chart because `helm lint` runs it against its own defaults and `""` does not
                    // match this pattern. The write path does not apply a default, so the API still
                    // demands the property.
                    DefaultJson = "\"10.20.1.0/24\"",
                    ExampleJson = "\"10.20.1.0/24\""
                },
                new(
                    "/properties/addressPrefix/v6",
                    SchemaKind.Text,
                    Description: "The IPv6 prefix, or empty for an IPv4-only subnet. Setting both "
                    + "makes the subnet dual-stack — docs/plan/14 § IPv6."
                ) {
                    // ⚠ The OPTIONAL pattern — see Cidr.OptionalV4Pattern for why an "empty means
                    // none" default and a Pattern that refuses "" is a silo-start failure.
                    Pattern = Cidr.OptionalV6Pattern,
                    Immutable = true,
                    DefaultJson = "\"\"",
                    ExampleJson = "\"fd00:20:1::/64\""
                },
                new(
                    "/properties/natOutgoing",
                    SchemaKind.Boolean,
                    Description: "Whether workloads in this subnet reach the internet through source "
                    + "NAT. Off by default. ⚠ This is the opposite of Kube-OVN's own default for its "
                    + "cluster subnet, deliberately: a tenant subnet that silently egresses is a "
                    + "surprise, and docs/plan/12 § Cross-cutting decisions defaults external exposure "
                    + "to off. It also requires the network's enableExternal to be on; without it the "
                    + "flag is accepted and nothing egresses."
                ) {
                    DefaultJson = "false"
                },
                new(
                    "/properties/private",
                    SchemaKind.Boolean,
                    Description: "Whether the subnet refuses traffic from other subnets. Off by "
                    + "default. ⚠ In this api-version it has no exception list, so on means no traffic "
                    + "from any other subnet in the network at all."
                ) {
                    DefaultJson = "false"
                },
                new(
                    "/properties/enableDhcp",
                    SchemaKind.Boolean,
                    Description: "Whether the fabric answers DHCP in this subnet. Off by default: an "
                    + "address is assigned to a workload's port when the port is created, and DHCP is "
                    + "for guests that insist on asking — a virtual machine rather than a container."
                ) {
                    DefaultJson = "false"
                }
            ]
        );

    /// <summary>The pointers <see cref="Schema2026" /> declares, in declaration order.</summary>
    public static ImmutableArray<string> Pointers2026 { get; } =
        [.. Schema2026.Properties.Select(x => x.JsonPointer)];

    /// <summary>
    ///     What a <c>POST …/listAddressUsage</c> returns.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Counts are per family and are reported separately rather than summed.</b> A
    ///     dual-stack subnet that has exhausted its IPv4 range and has 2^64 IPv6 addresses left is
    ///     <i>full</i> for anything that needs a v4 address, and a single total would say the opposite
    ///     in the most confident possible way. docs/plan/14 § IPv6's warning that <i>"every provider's
    ///     connectivity code handles two families"</i> is the same point arriving on a response shape.
    ///     <para>
    ///         ⚠ <c>v6</c> figures are <see cref="SchemaKind.Text" /> and the <c>v4</c> ones are
    ///         numbers, which looks inconsistent and is correct: a /64 holds 18 446 744 073 709 551 616
    ///         addresses, and <see cref="SchemaKind.WholeNumber" /> validates through
    ///         <c>TryGetInt64</c> — the value fits in a signed 64-bit integer only just, and a /63 does
    ///         not fit at all. A number that silently loses precision on a real subnet is worse than a
    ///         string.
    ///     </para>
    /// </remarks>
    public static ResourceSchema AddressUsageResponse { get; } =
        ResourceSchema.Of(
            [
                // ⚠ The two containers are declared explicitly. ResourceSchema.Declares is an exact
                // pointer match and the unknown-property walk descends level by level, so an
                // undeclared `/v4` would make every leaf under it unreachable — the same reason every
                // request schema in the tree declares its SchemaKind.Nested containers.
                new("/v4", SchemaKind.Nested, Description: "IPv4 address usage."),
                new(
                    "/v4/total",
                    SchemaKind.WholeNumber,
                    Required: true,
                    Description: "How many IPv4 addresses the prefix contains that may be allocated, "
                    + "excluding the network address, the broadcast address and the gateway."
                ),
                new(
                    "/v4/used",
                    SchemaKind.WholeNumber,
                    Required: true,
                    Description: "How many IPv4 addresses are currently allocated to ports."
                ),
                new(
                    "/v4/available",
                    SchemaKind.WholeNumber,
                    Required: true,
                    Description: "How many IPv4 addresses remain. ⚠ Zero here is the answer to 'why "
                    + "will nothing schedule in this subnet'."
                ),
                new("/v6", SchemaKind.Nested, Description: "IPv6 address usage."),
                new(
                    "/v6/total",
                    SchemaKind.Text,
                    Description: "How many IPv6 addresses the prefix contains, as a decimal string, "
                    + "or empty for an IPv4-only subnet. ⚠ A string because a /64 does not fit in the "
                    + "signed 64-bit integer SchemaKind.WholeNumber validates through."
                ),
                new(
                    "/v6/available",
                    SchemaKind.Text,
                    Description: "How many IPv6 addresses remain, as a decimal string, or empty for an "
                    + "IPv4-only subnet."
                ),
                new(
                    "/sampledAt",
                    SchemaKind.Text,
                    Required: true,
                    Description: "When the fabric last reported these figures, RFC 3339. ⚠ Returned "
                    + "because a count with no timestamp is a count a caller will read as live, and "
                    + "these come from the Subnet object's status rather than from a live query."
                ) {
                    Format = SchemaFormat.DateTime
                }
            ]
        );

    // ── The desired body, read ────────────────────────────────────────────────────────────────

    /// <summary>The IPv4 prefix a body declares.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string PrefixV4(JsonElement desired) => Text(desired, "addressPrefix", "v4");

    /// <summary>The IPv6 prefix a body declares, or empty.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string PrefixV6(JsonElement desired) => Text(desired, "addressPrefix", "v6");

    /// <summary>Whether a body asks for outbound NAT.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static bool NatOutgoing(JsonElement desired) => Flag(desired, "natOutgoing");

    /// <summary>Whether a body asks for subnet isolation.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static bool Private(JsonElement desired) => Flag(desired, "private");

    /// <summary>Whether a body asks for DHCP.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static bool EnableDhcp(JsonElement desired) => Flag(desired, "enableDhcp");

    /// <summary>The region a body declares.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string Location(JsonElement desired) => VirtualNetworks.Location(desired);

    /// <summary>
    ///     The <c>spec.cidrBlock</c> a body becomes — one string, IPv4 first.
    /// </summary>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     ⚠ <b>THE ORDER IS NOT COSMETIC.</b> <c>util.CheckProtocol</c> returns <c>Dual</c> only when
    ///     exactly two entries parse as one v4 and one v6, and the rest of Kube-OVN's dual-stack
    ///     handling — <c>gateway</c>, <c>excludeIps</c>, <c>u2oInterconnectionIP</c> — follows the same
    ///     comma convention and the same family order. Producing the string in one place rather than
    ///     letting a tenant type it is what makes the order a property of this function.
    /// </remarks>
    public static string CidrBlock(JsonElement desired) {
        var v6 = PrefixV6(desired);
        return v6.Length == 0 ? PrefixV4(desired) : PrefixV4(desired) + "," + v6;
    }

    /// <summary>
    ///     What is wrong with a body's prefixes, or <see langword="null" /> when nothing is.
    /// </summary>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     ⚠ The check docs/plan/14 asks the API for and that runs after the <c>202</c> instead — the
    ///     argument, and what would close it, is on <see cref="NetworkAddressing" />. ⚠ It matters
    ///     more here than on the parent: a network's address space is a declaration, and <b>this</b>
    ///     prefix is what the fabric actually programs.
    /// </remarks>
    public static string? AddressProblem(JsonElement desired) {
        var region = Location(desired);

        if (NetworkAddressing.ProblemWith(
                PrefixV4(desired),
                region,
                "/properties/addressPrefix/v4"
            ) is { } v4) {
            return v4;
        }

        var v6 = PrefixV6(desired);

        return v6.Length == 0
            ? null
            : NetworkAddressing.ProblemWith(v6, region, "/properties/addressPrefix/v6");
    }

    // ── The object a desired body becomes ─────────────────────────────────────────────────────

    /// <summary>The <c>Subnet</c> document a desired body becomes.</summary>
    /// <param name="ns">The resource's namespace, used as a name component.</param>
    /// <param name="id">The subnet's address — its network's name comes from here.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>NO <c>namespaces</c>, AND IT IS THE FIELD A KUBE-OVN USER WOULD EXPECT FIRST.</b>
    ///         <c>spec.namespaces</c> binds a subnet to Kubernetes namespaces so that pods created
    ///         there get addresses from it. This platform's namespace is
    ///         <c>ReconcileDriver.NamespaceFor</c>'s and a tenant body naming namespaces could bind
    ///         another tenant's. When docs/plan/13's workloads arrive they will join a subnet by
    ///         <i>port annotation</i>, which is per-pod and carries no such reach.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>NO <c>protocol</c>, NO <c>gateway</c>, NO <c>excludeIps</c>, NO
    ///         <c>gatewayType</c>, NO <c>provider</c>.</b> Every one of them is written by the
    ///         controller — see the remarks on this class — so sending them is at best redundant and
    ///         at worst a value that is overwritten one pass later while the resource reports it as
    ///         desired. <c>vpc</c> IS sent, because the controller only fills it when empty and
    ///         letting it default would bind the subnet to the platform's own default VPC.
    ///     </para>
    ///     <para>
    ///         ⚠ No labels, no annotations and no namespace here — ADR-013's seven labels and two
    ///         annotations are injected by <c>KubeCommand</c> non-overridably, and this object is
    ///         cluster-scoped so there is no namespace to write.
    ///     </para>
    /// </remarks>
    public static string SubnetJson(string ns, ResourceId id, JsonElement desired) =>
        new JsonObject {
            ["kind"] = SubnetKind.Kind,
            ["metadata"] = new JsonObject { ["name"] = ObjectNameOf(ns, id) },
            ["spec"] = new JsonObject {
                // ⚠ THE ONE FIELD THAT MAKES THIS A CHILD, AND IT COMES FROM THE ADDRESS.
                ["vpc"] = VpcRefOf(ns, id),
                ["cidrBlock"] = CidrBlock(desired),
                ["natOutgoing"] = NatOutgoing(desired),
                ["private"] = Private(desired),
                ["enableDHCP"] = EnableDhcp(desired)
            }
        }.ToJsonString();

    /// <summary>
    ///     Whether a <c>Subnet</c> read back from a cluster carries what the desired body asks for.
    /// </summary>
    /// <param name="objectJson">The object's JSON, exactly as the API server returned it.</param>
    /// <param name="ns">The resource's namespace, used as a name component.</param>
    /// <param name="id">The subnet's address.</param>
    /// <param name="desired">The desired body.</param>
    /// <remarks>
    ///     ⚠ <b><c>spec.vpc</c> IS COMPARED AND IT IS THE FIELD THIS TYPE MOST NEEDS COMPARED.</b> A
    ///     <c>Subnet</c> whose <c>vpc</c> was rewritten — by a merge, by an admission policy, by a
    ///     <c>kubectl edit</c> — is a range being handed out inside a <i>different tenant's</i> routing
    ///     domain under this tenant's resource id. It is derived from the address rather than from the
    ///     body precisely so the comparison cannot be satisfied by a body that agrees with itself.
    /// </remarks>
    public static bool Matches(string objectJson, string ns, ResourceId id, JsonElement desired) =>
        MatchesBody(objectJson, desired)
        && Spec(objectJson) is { } spec
        && spec["vpc"]?.GetValue<string>() == VpcRefOf(ns, id);

    /// <summary>
    ///     The half of <see cref="Matches" /> that a desired <b>body</b> alone decides.
    /// </summary>
    /// <param name="objectJson">The object's JSON, exactly as the API server returned it.</param>
    /// <param name="desired">The desired body.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>THE SPLIT EXISTS FOR THE REASON <c>StorageBuckets.MatchesBody</c> RECORDS — SECOND
    ///         SIGHTING, AND THE FIRST WHERE IT WAS PREDICTED RATHER THAN DISCOVERED BY A RED SUITE.</b>
    ///         <c>ProviderConformanceCase.ObjectMatchesDesired</c> is
    ///         <c>(objectJson, desiredJson) =&gt; bool</c> and carries <b>no address</b>, so the
    ///         predicate the shared suite can evaluate for a child is strictly smaller than the one the
    ///         reconciler evaluates. What the suite therefore does not check for a subnet: that the
    ///         rendered object binds to the right <c>Vpc</c>. <c>NetworkReconcilerTests</c>
    ///         asserts it against real addresses, including the case the harness cannot build — two
    ///         networks in ONE resource group each holding a subnet called <c>web</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>cidrBlock</c> IS COMPARED AS PARSED NETWORKS AND NOT AS STRINGS.</b> The
    ///         controller canonicalizes it — <c>10.20.1.7/24</c> is stored as <c>10.20.1.0/24</c> — so
    ///         a string comparison reports drift on a converged subnet forever and the reconciler never
    ///         leaves <c>InProgress</c>. This is the single most likely bug in the family and
    ///         <c>NetworkMatchesTests</c> runs it red.
    ///     </para>
    /// </remarks>
    public static bool MatchesBody(string objectJson, JsonElement desired) {
        if (Spec(objectJson) is not { } spec) {
            return false;
        }

        return SameNetworks(spec["cidrBlock"]?.GetValue<string>(), CidrBlock(desired))
            && spec["natOutgoing"]?.GetValue<bool>() == NatOutgoing(desired)
            && spec["private"]?.GetValue<bool>() == Private(desired)
            && spec["enableDHCP"]?.GetValue<bool>() == EnableDhcp(desired);
    }

    /// <summary>
    ///     Whether two <c>cidrBlock</c> strings describe the same networks, in the same order.
    /// </summary>
    /// <param name="found">What the object carries.</param>
    /// <param name="wanted">What the body asks for.</param>
    /// <remarks>
    ///     ⚠ <b>Order IS significant and that is not laziness.</b> Kube-OVN reads element 0 as the
    ///     IPv4 half and element 1 as the IPv6 half throughout, so two prefixes in the other order are
    ///     a different subnet rather than the same one written differently. What is <i>not</i>
    ///     significant is the spelling of each element, which is the whole reason this function exists.
    /// </remarks>
    static bool SameNetworks(string? found, string wanted) {
        if (found is null) {
            return false;
        }

        var left = found.Split(',');
        var right = wanted.Split(',');

        if (left.Length != right.Length) {
            return false;
        }

        for (var index = 0; index < left.Length; index++) {
            if (!Cidr.TryParse(left[index].Trim(), out var a)
                || !Cidr.TryParse(right[index].Trim(), out var b)
                || a != b) {
                return false;
            }
        }

        return true;
    }

    /// <summary>The <c>spec</c> of a <c>Subnet</c> document, or <see langword="null" />.</summary>
    /// <param name="objectJson">The object's JSON.</param>
    static JsonObject? Spec(string objectJson) {
        JsonNode? parsed;
        try {
            parsed = JsonNode.Parse(objectJson);
        } catch (JsonException) {
            return null;
        }

        return parsed is JsonObject document
            && document["kind"]?.GetValue<string>() is (null or "Subnet")
            && document["spec"] is JsonObject spec
                ? spec
                : null;
    }

    // ── A body, for tests, fixtures and the conformance case ──────────────────────────────────

    /// <summary>Builds a body that satisfies <see cref="Schema2026" />.</summary>
    /// <param name="clusterId">The cluster to place the subnet in.</param>
    /// <param name="prefixV4">The IPv4 prefix.</param>
    /// <param name="prefixV6">The IPv6 prefix, or empty.</param>
    /// <param name="natOutgoing">Whether workloads egress through NAT.</param>
    /// <param name="isPrivate">Whether the subnet refuses traffic from other subnets.</param>
    /// <param name="enableDhcp">Whether the fabric answers DHCP.</param>
    /// <param name="location">The region.</param>
    /// <remarks>
    ///     ⚠ The default prefix <c>10.20.1.0/24</c> sits inside
    ///     <c>VirtualNetworks.Body</c>'s default address space, which is the relation nothing
    ///     enforces — see this class's remarks. It is chosen to be consistent so that the fixtures
    ///     read as a coherent network rather than as an illustration of the gap.
    /// </remarks>
    public static string Body(
        Guid clusterId,
        string prefixV4 = "10.20.1.0/24",
        string prefixV6 = "",
        bool natOutgoing = false,
        bool isPrivate = false,
        bool enableDhcp = false,
        string location = "eu-central"
    ) =>
        new JsonObject {
            ["location"] = location,
            ["properties"] = new JsonObject {
                ["clusterId"] = clusterId.ToString("D", CultureInfo.InvariantCulture),
                ["addressPrefix"] = new JsonObject { ["v4"] = prefixV4, ["v6"] = prefixV6 },
                ["natOutgoing"] = natOutgoing,
                ["private"] = isPrivate,
                ["enableDhcp"] = enableDhcp
            }
        }.ToJsonString();

    // ── Reading one pointer out of a body ─────────────────────────────────────────────────────

    static JsonElement? Root(JsonElement desired, string name) =>
        desired.ValueKind is JsonValueKind.Object
        && desired.TryGetProperty("properties", out var properties)
        && properties.ValueKind is JsonValueKind.Object
        && properties.TryGetProperty(name, out var value)
            ? value
            : null;

    static string Text(JsonElement desired, string parent, string name) =>
        Root(desired, parent) is { ValueKind: JsonValueKind.Object } section
        && section.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    static bool Flag(JsonElement desired, string name) =>
        Root(desired, name) is { ValueKind: JsonValueKind.True };
}
