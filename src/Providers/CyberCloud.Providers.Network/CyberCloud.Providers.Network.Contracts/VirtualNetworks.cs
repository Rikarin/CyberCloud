using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Network.Contracts;

/// <summary>
///     Everything addressable about <c>CyberCloud.Network/virtualNetworks</c> — a tenant's VPC, as a
///     Kube-OVN <c>Vpc</c>.
/// </summary>
/// <remarks>
///     <para>
///         <b>The authority is docs/plan/14 § Virtual networks</b> — <i>"A tenant's VPC is a Kube-OVN
///         <c>Vpc</c>; subnets are <c>Subnet</c>s bound to it"</i>, M1 · 2.5 EM — and ADR-019, which
///         puts Kube-OVN alongside Cilium with <c>ENABLE_LB=false</c> and <c>ENABLE_NP=false</c>
///         because it provides <i>tenant</i> networking rather than cluster networking.
///     </para>
///     <para>
///         ⚠ <b>THE ISOLATION CLAIM, IN ONE SENTENCE, AND IT IS DELIBERATELY SMALLER THAN THE ONE A
///         READER EXPECTS.</b> <see cref="IsolationClaim" /> is the sentence; <see cref="IsolationLimits" />
///         is the table a test can walk. docs/plan/14 is explicit: Kube-OVN gives <i>"per-VPC L3
///         isolation with separate routing tables and overlapping address spaces — genuine tenant
///         separation at the network layer"</i>, and what it does <b>not</b> give is <i>"a hardware
///         boundary; a kernel bug in OVS is a cross-tenant risk"</i>, with the instruction that
///         <i>"the marketing must not claim more than the substrate delivers"</i>. The
///         <c>Display</c> summary this type registers with, the chart's description and the
///         portal copy all derive from <see cref="IsolationClaim" /> rather than restating it, and
///         <c>NetworkDeclarationTests</c> is what stops the three from drifting apart. The precedent is
///         <c>MariaDbServers.CompatibilityClaim</c> and its <c>SupportedSubset</c>: a claim that is
///         data is a claim a test can fail on, and a claim that is prose is a claim that gets
///         optimistic in a release note.
///     </para>
///     <para>
///         ⚠ <b>THIS IS THE FIRST TYPE IN THE TREE WHOSE OBJECT IS CLUSTER-SCOPED, AND IT CHANGES THE
///         ONE THING EVERY PROVIDER BEFORE IT COULD TAKE FOR GRANTED.</b> Checked in
///         <c>pkg/apis/kubeovn/v1/vpc.go</c> rather than in a README:
///         <c>// +kubebuilder:resource:scope="Cluster",shortName="vpc",path="vpcs"</c>. Every object
///         the nine earlier families render is <b>namespaced</b>, and
///         <c>ReconcileDriver.NamespaceFor</c> — <c>{subscriptionId:N}-{resourceGroup}</c> — is what
///         has kept two tenants' identically-named resources apart for all of them, without any
///         provider having to think about it. <b>A cluster-scoped object has no namespace to be kept
///         apart by.</b> Two tenants each creating a virtual network called <c>prod</c> would render
///         one <c>Vpc</c> named <c>prod</c>, each converging by overwriting the other, with neither
///         reporting an error anywhere — the same failure <c>StorageBuckets.ObjectNameOf</c> guards
///         against inside a namespace, one scope wider and with no namespace as a backstop.
///         <see cref="ObjectNameOf" /> is the answer and it takes the namespace <i>as a name
///         component</i> rather than as a placement.
///     </para>
///     <para>
///         ⚠ <b>AND THE SHARED CLUSTER-BACKED HARNESS COULD NOT HOST A CLUSTER-SCOPED OBJECT AT
///         ALL.</b> <c>ClusterConformanceHarness</c> derives a CRD stub per custom kind from the
///         case's own <c>Objects</c> — group, version, kind and plural — and hard-coded
///         <c>Scope = "Namespaced"</c>. A cluster-scoped apply goes to
///         <c>/apis/{group}/{version}/{plural}/{name}</c>, which a <c>Namespaced</c> definition does
///         not serve, so every cluster-facing assertion in this family failed with a <c>404</c>. The
///         scope is now <b>derived from the case's own <c>ObjectRef.IsClusterScoped</c></b>, which is
///         the same principle the harness already applies to group, version, kind and plural and
///         which the harness's own remarks give the reason for: a member a provider author could get
///         wrong by omission reports the omission through the worst error message in the suite. ⚠ It
///         changes nothing for the nine families that came before — every one of their
///         <c>ObjectRef</c>s carries a namespace, so every one still derives <c>Namespaced</c>.
///     </para>
///     <para>
///         ⚠ <b><c>Matches</c> IS CONTAINMENT, AND THE USUAL ARGUMENT FOR THAT IS FALSE HERE — THE
///         REAL REASON IS BIGGER.</b> Three families argue containment from structural defaulting;
///         <c>ClickHouseClusters</c> is the first to find an operator with no defaults and no webhook,
///         and this is the second. Checked in <c>charts/kube-ovn/templates/kube-ovn-crd.yaml</c> and
///         the Go types rather than in a README: across <c>Vpc</c>, <c>Subnet</c>,
///         <c>SecurityGroup</c>, <c>IptablesEIP</c> and <c>OvnEip</c> there is exactly <b>one</b>
///         <c>+kubebuilder:default</c> — <c>Vpc.spec.bfdPort.enabled=false</c> — and <b>no
///         <c>MutatingWebhookConfiguration</c> anywhere in the project</b>; the only webhook is a
///         <c>ValidatingWebhookConfiguration</c> that is <b>off in a default install</b>
///         (<c>dist/images/install.sh</c> ships none, the v2 chart gates it on
///         <c>validatingWebhook.enabled: false</c>) and that never returns a patch.
///         <b>The reason containment is nevertheless mandatory is that the Kube-OVN CONTROLLER writes
///         back to <c>.spec</c>.</b> <c>pkg/controller/vpc.go</c>'s <c>formatVpc</c> fills
///         <c>staticRoutes[].policy</c>, clears a <c>nextHopIP</c> on a non-reroute route, adds a
///         finalizer and issues a full <c>Vpcs().Update(...)</c>; <c>handleDeleteVpcStaticRoute</c>
///         <i>removes</i> entries from <c>spec.staticRoutes</c> outright. That is a third mechanism
///         after "the CRD defaults it" and "a mutating webhook rewrites it", it is the one that is
///         hardest to see, and it is the one this family meets. <c>NetworkMatchesTests</c> runs the
///         equality mistake against a controller-shaped read-back.
///     </para>
///     <para>
///         ⚠ <b>NO <c>routeTables</c> CHILD TYPE, AND docs/plan/14'S RESOURCE TREE IS WRONG TO ASK FOR
///         ONE AGAINST THIS SUBSTRATE.</b> That document draws
///         <c>routeTables/{name} → static routes, next-hop</c> as a child of a virtual network.
///         <b>Kube-OVN has no route-table object.</b> Checked against the full CRD file: the project
///         defines 25 <c>kubeovn.io/v1</c> kinds and none of them is a <c>RouteTable</c>,
///         <c>VpcRouteTable</c> or top-level <c>StaticRoute</c> — a "route table" in Kube-OVN is a
///         bare <i>string name</i> referenced from <c>Vpc.spec.staticRoutes[].routeTable</c> and
///         <c>Subnet.spec.routeTable</c>, with no independent existence, no lifecycle and nothing to
///         observe. (<c>RouterLBRule</c> and <c>SwitchLBRule</c> are OVN load-balancer rules; the
///         names mislead.) So a <c>routeTables</c> resource would have to write into its parent's
///         <c>Vpc.spec.staticRoutes</c> — and <b>that array is atomic under server-side apply</b>: it
///         carries no <c>x-kubernetes-list-type</c> and no <c>x-kubernetes-list-map-keys</c>, so the
///         last applier owns the whole list and two route tables in one VPC would each converge by
///         erasing the other. ⚠ <b>Routing is therefore not modelled at all in M1, and there is a
///         second, independent reason it could not be</b>: a static route is
///         <c>{cidr, nextHop, policy}</c>, which is an <i>array of objects</i>, and
///         <c>SchemaProperty.ElementKind</c> refuses that outright — <i>"an array element is a
///         scalar"</i> — so the body shape has nowhere to put one either. Both are recorded at
///         <c>charts/managed/kube-ovn-vpc/conformance.yaml § owed</c>, <c>route-tables-have-no-object</c>
///         and <c>static-routes-are-not-expressible</c>.
///     </para>
///     <para>
///         ⚠ <b>NOTHING CAN JOIN ONE OF THESE YET, AND SAYING SO IS PART OF SHIPPING IT.</b> This is
///         the first family whose resources other resources are meant to sit inside, and no such
///         resource exists — docs/plan/13's VMs and containers are the customers. What was avoided is
///         foreclosing it: a workload joins a tenant network by naming a <b>subnet</b>, and a subnet
///         binds to its VPC through <c>Subnet.spec.vpc</c> naming <see cref="ObjectNameOf" />'s
///         output, which is derivable from a resource id alone. So the join is a <i>resource id</i> a
///         future provider resolves through <c>CyberCloud.ResourceManager</c> — the route rule 2
///         requires — rather than an assembly reference, and nothing here needs to change for it. The
///         assumption that <i>is</i> being made: that a consumer names a subnet rather than a network,
///         because a Kube-OVN pod is annotated onto a <c>Subnet</c> and never onto a <c>Vpc</c>.
///     </para>
///     <para>
///         ⚠ <b>No <c>SupportsSoftDelete</c>, for the reason every provider before this one gives</b>:
///         nothing in the manager reads <c>SoftDeleteDays</c>, and declaring a recovery window the
///         platform does not honour would be a promise made to the users most likely to test it.
///     </para>
/// </remarks>
public static class VirtualNetworks {
    /// <summary>The provider namespace — docs/plan/14's own spelling.</summary>
    public const string ProviderNamespace = "CyberCloud.Network";

    /// <summary>The type path.</summary>
    public const string TypePath = "virtualNetworks";

    /// <summary>The one api-version.</summary>
    public const string V2026 = "2026-08-01";

    /// <summary>The chart this type is the configuration surface of.</summary>
    public const string ChartName = "managed/kube-ovn-vpc";

    /// <summary>The pointer <c>RequiresCluster</c> names.</summary>
    public const string ClusterIdPointer = ClusterPlacement.DefaultPointer;

    /// <summary>The type, namespace and path together.</summary>
    public static ResourceTypeName Type { get; } = new(ProviderNamespace, TypePath);

    /// <summary>The action that reports what this network's isolation does and does not guarantee.</summary>
    /// <remarks>
    ///     ⚠ <b>AN UNUSUAL ACTION, AND IT EXISTS BECAUSE docs/plan/14 MAKES OVERCLAIMING THE NAMED
    ///     RISK OF THIS ROW.</b> That document requires that <i>"the marketing must not claim more
    ///     than the substrate delivers"</i>, and the usual way that requirement is met is a paragraph
    ///     in a document nobody reads at the moment of the decision. This puts the claim <i>and its
    ///     limits</i> on the API, next to the resource, so a tenant evaluating whether a VPC is
    ///     sufficient for their compliance requirement can ask the platform rather than read the
    ///     marketing.
    ///     <para>
    ///         ⚠ <b>It is also the only action in the catalogue that is fully implementable today.</b>
    ///         Every other provider's action returns something the platform cannot yet produce — a
    ///         credential out of a Vault that is not wired (<c>listKeys</c>), a figure from a usage
    ///         pipeline that does not exist (<c>stats</c>). This one is a pure function of
    ///         <see cref="IsolationClaim" /> and <see cref="IsolationLimits" />, both compile-time
    ///         constants, so there is nothing to be owed. ⚠ <b>The handler seam itself is what is
    ///         still missing</b> — no provider in the tree has one, actions are declared into the
    ///         registry and routed by the gateway and there is nowhere to put the code — so this is
    ///         the first action whose <i>content</i> is ready and whose <i>plumbing</i> is not, which
    ///         is a different and more useful thing to record than another blocked credential.
    ///     </para>
    /// </remarks>
    public const string ShowIsolationAction = "showIsolation";

    /// <summary>The permission <see cref="ShowIsolationAction" /> checks.</summary>
    /// <remarks>
    ///     ⚠ <c>read</c>. Nothing that leaves through it is a credential or a capability — it is the
    ///     same sentence for every network in the platform — so a permission of its own would be a
    ///     role nobody grants and an audit line nobody reads. The contrast is
    ///     <c>StorageAccounts.ListKeysAction</c>, which has one because what leaves is a key.
    /// </remarks>
    public const string ShowIsolationPermission = "read";

    // ── What this type claims, as data ────────────────────────────────────────────────────────

    /// <summary>
    ///     What tenant isolation this type provides, in one sentence — the <b>only</b> sentence any
    ///     surface may make the claim in.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>docs/plan/14 § Virtual networks: <i>"the marketing must not claim more than the
    ///     substrate delivers"</i>.</b> The word doing the work is <i>network-layer</i>. It is not
    ///     "isolated", not "private" and not "secure", each of which a reader completes with a
    ///     stronger guarantee than OVS provides. <see cref="IsolationLimits" /> carries what is
    ///     explicitly <b>not</b> claimed, and <c>NetworkDeclarationTests</c> asserts that this string
    ///     reaches the registered summary and the chart description unchanged and that neither adds a
    ///     word from the forbidden list.
    /// </remarks>
    public const string IsolationClaim =
        "Network-layer tenant separation: a private routing domain with its own address space, on "
        + "shared hardware.";

    /// <summary>
    ///     One thing this type does <b>not</b> promise, and what a tenant who needs it should do.
    /// </summary>
    /// <param name="Id">A stable identifier, for tests and for the portal.</param>
    /// <param name="NotClaimed">The guarantee a reader might otherwise assume.</param>
    /// <param name="Because">Why the substrate does not deliver it.</param>
    /// <param name="Instead">What a tenant who needs it should ask for.</param>
    public sealed record IsolationLimit(string Id, string NotClaimed, string Because, string Instead);

    /// <summary>
    ///     The limits of <see cref="IsolationClaim" /> — docs/plan/14's own caveats, as a table.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Data rather than prose, on the <c>MariaDbServers.SupportedSubset</c> model.</b> A
    ///     caveat in a paragraph is a caveat that is dropped from the next summary somebody writes;
    ///     one in a table is one a test can require the presence of. Every row below is a sentence
    ///     from docs/plan/14 rather than an inference from it.
    /// </remarks>
    public static ImmutableArray<IsolationLimit> IsolationLimits { get; } = [
        new(
            "not-a-hardware-boundary",
            "Isolation enforced by separate physical hardware.",
            "Kube-OVN separates tenants in Open vSwitch on shared nodes. docs/plan/14: what it does "
            + "not give is \"a hardware boundary; a kernel bug in OVS is a cross-tenant risk\".",
            "A dedicated cluster on dedicated hardware, which docs/plan/14 contemplates for exactly "
            + "this requirement."
        ),
        new(
            "not-encryption",
            "Traffic inside the network is encrypted.",
            "A VPC is a routing domain. Packets between two workloads in one subnet are not encrypted "
            + "by this resource, and nothing in the Vpc object turns that on.",
            "Encrypt in the workload — mutual TLS between services — or terminate on a vpnGateways "
            + "resource for traffic entering from outside."
        ),
        new(
            "not-a-firewall-by-default",
            "Traffic is denied unless a rule allows it.",
            "docs/plan/14 runs Kube-OVN with ENABLE_NP=false; a Vpc with no securityGroups attached "
            + "permits traffic within itself. The default is a routing domain, not a deny-all "
            + "perimeter.",
            "Attach a securityGroups child, whose rules become OVN ACLs on the ports in the network."
        ),
        new(
            "no-isolation-from-the-platform",
            "The platform operator cannot see tenant traffic.",
            "Hubble flow logs are collected per tenant by design — docs/plan/14 § Observability makes "
            + "them the data behind troubleshooting, security review and egress billing.",
            "Nothing, at this layer. It is a property of a managed platform and is stated so that it "
            + "is not discovered."
        )
    ];

    // ── The object a virtual network IS ───────────────────────────────────────────────────────

    /// <summary>The Kube-OVN <c>Vpc</c> custom resource.</summary>
    /// <remarks>
    ///     ⚠ <b>Cluster-scoped, which <see cref="ObjectRef" /> spells as an empty namespace</b> —
    ///     <c>ObjectRef.IsClusterScoped</c> is <c>Namespace.Length == 0</c>, and <c>KubeApiClient</c>
    ///     branches on it to the non-namespaced REST path. The platform has modelled this since before
    ///     any provider needed it; this family is the first to exercise it.
    /// </remarks>
    public static GroupVersionKind VpcKind { get; } =
        new() { Group = "kubeovn.io", Version = "v1", Kind = "Vpc", Plural = "vpcs" };

    /// <summary>
    ///     The name of the <c>Vpc</c> a virtual network renders.
    /// </summary>
    /// <param name="ns">The namespace the resource's other objects would live in.</param>
    /// <param name="name">The resource's own name.</param>
    /// <remarks>
    ///     ⚠ <b>THE NAMESPACE IS A NAME COMPONENT HERE RATHER THAN A PLACEMENT, AND THAT INVERSION IS
    ///     THE WHOLE POINT.</b> A <c>Vpc</c> is cluster-scoped, so
    ///     <c>ReconcileDriver.NamespaceFor</c>'s <c>{subscriptionId:N}-{resourceGroup}</c> — the thing
    ///     that has kept every earlier provider's tenants apart for free — places nothing. Folding it
    ///     into the object's <i>name</i> restores exactly the separation it was providing: two
    ///     subscriptions each holding a network called <c>prod</c> render two differently-named
    ///     <c>Vpc</c>s, and two resource groups in one subscription do too.
    ///     <para>
    ///         ⚠ <b>It is NOT the tenant id, and that is deliberate.</b> A subscription belongs to
    ///         exactly one tenant, so the subscription GUID already separates tenants; adding the
    ///         tenant id would lengthen every name for no additional separation. ⚠ The consequence to
    ///         know: the tenant id is therefore <i>not</i> readable from a <c>Vpc</c>'s name — it is
    ///         readable from the <c>cybercloud.io/tenant-id</c> label, which ADR-013 injects
    ///         non-overridably and which is what an operator greps.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Length.</b> A Kubernetes object name is 253 characters; this is 32 (a GUID with no
    ///         hyphens) + 1 + a resource-group name + 1 + a resource name, and
    ///         <c>ResourceNaming.Pattern</c> caps each of the latter two at 63 — so the worst case is
    ///         160 and there is no truncation branch to get wrong. <c>NetworkDeclarationTests</c> pins
    ///         the arithmetic.
    ///     </para>
    /// </remarks>
    public static string ObjectNameOf(string ns, string name) => ns + "-" + name;

    /// <summary>The <c>Vpc</c> a virtual network owns.</summary>
    /// <param name="ns">The resource's namespace, used as a name component.</param>
    /// <param name="name">The resource's own name.</param>
    /// <remarks>
    ///     ⚠ <c>Namespace</c> is deliberately <b>empty</b>: that is how <see cref="ObjectRef" /> spells
    ///     cluster-scoped, and passing <paramref name="ns" /> here instead would apply the object to a
    ///     REST path the API server does not serve for this kind.
    /// </remarks>
    public static ObjectRef VpcRef(string ns, string name) =>
        new() { Kind = VpcKind, Namespace = string.Empty, Name = ObjectNameOf(ns, name) };

    // ── The body shape ────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     The body shape at <see cref="V2026" />.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>THE ADDRESS SPACE IS TWO TYPED STRINGS RATHER THAN docs/plan/14'S LIST, AND THAT
    ///         IS THE MOST CONSEQUENTIAL DECISION IN THIS FILE.</b> That document draws
    ///         <c>addressSpace: [10.20.0.0/16]</c> — an array. An array is exactly the shape that
    ///         makes the CIDR unenforceable: <c>ADR-012</c>'s fifth surface <b>refuses
    ///         <c>@pattern</c> on an array</b> while emitting <c>@enum</c> there, so
    ///         <c>./build.sh Charts</c> fails with <i>"it refines a string, and JSON Schema ignores it
    ///         on any other type"</i> — the gap <c>charts/managed/kafka</c> recorded as
    ///         <c>cidr-shape-is-unenforced</c>, whose cost it describes as a body that <i>"may send
    ///         999.0.0.1/99 and be accepted"</i>. Inheriting that here would be far worse than it was
    ///         there: on Kafka the unchecked array is an optional firewall list, and here it is <b>the
    ///         defining property of the resource</b>.
    ///         <br />⚠ <b>Two properties are also a better model of the requirement.</b> docs/plan/14
    ///         § IPv6 asks for <i>"dual-stack from day one"</i> and says every subnet may carry
    ///         <i>"a v4 prefix, a v6 prefix, or both"</i> — which is precisely a v4 slot and a v6
    ///         slot, not an unordered bag. An array cannot say "at most one of each family" and these
    ///         two say it by construction. So the pattern IS declared, a malformed prefix IS refused
    ///         with a <c>400</c> and a JSON Pointer before the <c>202</c>, and this family does not
    ///         inherit <c>cidr-shape-is-unenforced</c> at all.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>WHAT THE ADDRESS SPACE DOES <i>NOT</i> DO: IT IS NOT RENDERED INTO THE
    ///         <c>Vpc</c>.</b> A Kube-OVN <c>Vpc</c> carries no CIDR at all — the address space lives
    ///         on its <c>Subnet</c>s. So this property is a <i>platform</i> declaration with two real
    ///         jobs and one job a reader will assume and not get. It is the thing the reserved-range
    ///         check runs against at network level (<see cref="NetworkAddressing.ProblemWith" />), and
    ///         it is what a portal draws when it shows a network's plan. It does <b>not</b> constrain
    ///         the child subnets: nothing compares a subnet's <c>cidrBlock</c> to its parent's address
    ///         space, because that is a relation between two resources' bodies and
    ///         <c>ResourceSchema</c> validates each property against constants. That is the same
    ///         shape as <c>charts/managed/seaweedfs-bucket</c>'s
    ///         <c>bucket-cluster-may-differ-from-its-accounts</c> and it is recorded, not implied, at
    ///         <c>§ owed</c>, <c>subnets-are-not-checked-against-the-address-space</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>enableExternal</c> defaults to <c>false</c>, and the default is the security
    ///         decision.</b> It attaches the VPC's router to the external network, which is what makes
    ///         a NAT gateway or a floating IP possible — and a network that reaches the outside by
    ///         default is a network whose owner did not choose that. docs/plan/12 § Cross-cutting
    ///         decisions defaults external exposure to off across the platform and this is the same
    ///         rule at the network layer.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Every default here is the chart's default, spelled as JSON</b> — there is no
    ///         <c>@default</c> directive, because the chart's default <i>is</i> the YAML literal on the
    ///         annotated line.
    ///     </para>
    /// </remarks>
    public static ResourceSchema Schema2026 { get; } =
        ResourceSchema.Of(
            [
                new(
                    "/location",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The region the virtual network lives in. ⚠ It selects which reserved "
                    + "ranges the address space is checked against — a region's underlay is part of "
                    + "that list."
                ) {
                    Format = SchemaFormat.Region,
                    Widget = WidgetHint.Region,
                    Immutable = true,
                    ExampleJson = "\"eu-central\""
                },
                new(
                    "/properties",
                    SchemaKind.Nested,
                    Description: "The virtual network's own settings."
                ),
                new(
                    ClusterIdPointer,
                    SchemaKind.Text,
                    Required: true,
                    Description: "The cluster whose fabric carries the network."
                ) {
                    Format = SchemaFormat.Uuid,
                    Widget = WidgetHint.Cluster,
                    Immutable = true
                },
                new(
                    "/properties/addressSpace",
                    SchemaKind.Nested,
                    Description: "The address range the network plans for. ⚠ Declarative: it is "
                    + "checked against the region's reserved ranges and is not rendered into the "
                    + "fabric, because a Kube-OVN Vpc carries no CIDR — the subnets do."
                ),
                new(
                    "/properties/addressSpace/v4",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The IPv4 range the network plans for, in CIDR form. It may overlap "
                    + "another of your own virtual networks — that is what a VPC is for — and it may "
                    + "not overlap a range the platform reserves, which is refused with the "
                    + "conflicting range named."
                ) {
                    Pattern = Cidr.V4Pattern,
                    Immutable = true,
                    // ⚠ A DEFAULT ON A **REQUIRED** PROPERTY, WHICH LOOKS CONTRADICTORY AND IS THE
                    // ONLY THING THAT MAKES THE CHART LINTABLE. Third sighting of one interaction —
                    // see Cidr.OptionalV4Pattern for the first two. `ChartAnnotationEmitter` writes
                    // the YAML literal on the annotated line from `DefaultJson`, and with none it
                    // writes `""`; `./build.sh Charts` then refuses the chart, because `helm lint`
                    // runs a chart against its OWN defaults and `""` does not match this pattern.
                    //
                    // ⚠ The two alternatives are both worse. Dropping the pattern re-opens
                    // `cidr-shape-is-unenforced` on the defining property of the resource. Widening
                    // it to admit `""` — which is what the optional v6 sibling does — would make
                    // `{"v4": ""}` a body the API ACCEPTS, because `Required` means present and an
                    // empty string is present; the refusal would move back past the 202, which is the
                    // whole thing this type is trying not to do.
                    //
                    // ⚠ What it means, stated so nobody reads it as a promise: `ResourceSchema`'s
                    // own remarks say a default is "documentation and generator input; the write path
                    // does not apply it". So the API still demands this property, and what this value
                    // does is give the chart a literal that lints and the portal a placeholder a
                    // tenant can accept. It is docs/plan/14's own worked example, and
                    // NetworkAddressTests asserts it overlaps no reserved range in any region.
                    DefaultJson = "\"10.20.0.0/16\"",
                    ExampleJson = "\"10.20.0.0/16\""
                },
                new(
                    "/properties/addressSpace/v6",
                    SchemaKind.Text,
                    Description: "The IPv6 range the network plans for, or empty for an IPv4-only "
                    + "network. ⚠ docs/plan/14 § IPv6 asks for dual-stack from day one rather than as "
                    + "a retrofit, which is why this is here at the first api-version even though a "
                    + "v4-only network is the ordinary case."
                ) {
                    // ⚠ The OPTIONAL pattern, because the default is "" and
                    // SchemaProperty.Incoherences runs a declared default through this property's own
                    // constraints at class initialisation. Cidr.OptionalV4Pattern carries the full
                    // account of why that is a TypeInitializationException rather than a soft failure.
                    Pattern = Cidr.OptionalV6Pattern,
                    Immutable = true,
                    DefaultJson = "\"\"",
                    ExampleJson = "\"fd00:20::/48\""
                },
                new(
                    "/properties/enableExternal",
                    SchemaKind.Boolean,
                    Description: "Whether the network's router is attached to the external network. "
                    + "Off by default: a network that reaches the outside without being asked is a "
                    + "network whose owner did not choose that. ⚠ Turning it on requires the cluster "
                    + "to have an external subnet configured; without one the Vpc is accepted and the "
                    + "attachment never completes."
                ) {
                    DefaultJson = "false"
                }
            ]
        );

    /// <summary>The pointers <see cref="Schema2026" /> declares, in declaration order.</summary>
    public static ImmutableArray<string> Pointers2026 { get; } =
        [.. Schema2026.Properties.Select(x => x.JsonPointer)];

    /// <summary>
    ///     What a <c>POST …/showIsolation</c> returns.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>THE LIMITS ARE AN ARRAY OF SENTENCES RATHER THAN AN ARRAY OF ROWS, AND THAT IS A
    ///     REGISTRY LIMIT SHOWING THROUGH RATHER THAN A CHOICE.</b>
    ///     <see cref="IsolationLimits" /> is four fields per row —
    ///     <c>id</c>, <c>notClaimed</c>, <c>because</c>, <c>instead</c> — and
    ///     <see cref="SchemaProperty.ElementKind" /> refuses an array of objects outright:
    ///     <i>"an array element is a scalar … a nested or nested-array element would need its own
    ///     pointer space, which is the flat-list property ResourceSchema is built on"</i>. So the
    ///     structured table this platform holds is flattened to prose on the way out, and a client
    ///     that wanted to render the four columns separately has to split strings. It is the same
    ///     refusal that stops <c>routeTables</c> and <c>securityGroups</c> from being expressible, met
    ///     for a third time on a response rather than on a request — and it is the cheapest of the
    ///     three places to meet it, which is why this type ships with it rather than around it.
    ///     <c>§ owed</c>, <c>an-array-of-objects-is-not-expressible</c>.
    /// </remarks>
    public static ResourceSchema ShowIsolationResponse { get; } =
        ResourceSchema.Of(
            [
                new(
                    "/claim",
                    SchemaKind.Text,
                    Required: true,
                    Description: "What tenant isolation this network provides, in one sentence. It is "
                    + "the same sentence for every virtual network on the platform, and it is "
                    + "deliberately narrower than 'isolated'."
                ),
                new(
                    "/limits",
                    SchemaKind.Array,
                    Required: true,
                    Description: "What this network does NOT guarantee, one entry per limit, each "
                    + "naming the guarantee, why the substrate does not deliver it, and what to ask "
                    + "for instead. ⚠ Read this before deciding a virtual network satisfies a "
                    + "compliance requirement."
                ) {
                    ElementKind = SchemaKind.Text
                },
                new(
                    "/substrate",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The technology enforcing the separation, named so that a tenant's "
                    + "own security review has something to review."
                ) {
                    ExampleJson = "\"Kube-OVN (Open vSwitch)\""
                }
            ]
        );

    // ── The desired body, read ────────────────────────────────────────────────────────────────

    /// <summary>The IPv4 address space a body declares.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string AddressSpaceV4(JsonElement desired) => Text(desired, "addressSpace", "v4");

    /// <summary>The IPv6 address space a body declares, or empty.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string AddressSpaceV6(JsonElement desired) => Text(desired, "addressSpace", "v6");

    /// <summary>Whether a body asks for an external attachment.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static bool EnableExternal(JsonElement desired) =>
        Root(desired, "enableExternal") is { ValueKind: JsonValueKind.True };

    /// <summary>The region a body declares.</summary>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>⚠ Root-level, not under <c>properties</c> — <c>/location</c> is a platform property.</remarks>
    public static string Location(JsonElement desired) =>
        desired.ValueKind is JsonValueKind.Object
        && desired.TryGetProperty("location", out var value)
        && value.ValueKind is JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>
    ///     What is wrong with a body's address space, or <see langword="null" /> when nothing is.
    /// </summary>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     ⚠ <b>This is the check docs/plan/14 asks the API for and that runs after the <c>202</c>
    ///     instead.</b> The whole argument, and what would close it, is on
    ///     <see cref="NetworkAddressing" />. Both families are checked, so a dual-stack network whose
    ///     v6 half conflicts is refused for the v6 half by name rather than passing because its v4
    ///     half was fine.
    /// </remarks>
    public static string? AddressProblem(JsonElement desired) {
        var region = Location(desired);

        if (NetworkAddressing.ProblemWith(
                AddressSpaceV4(desired),
                region,
                "/properties/addressSpace/v4"
            ) is { } v4) {
            return v4;
        }

        var v6 = AddressSpaceV6(desired);

        return v6.Length == 0
            ? null
            : NetworkAddressing.ProblemWith(v6, region, "/properties/addressSpace/v6");
    }

    // ── The object a desired body becomes ─────────────────────────────────────────────────────

    /// <summary>The <c>Vpc</c> document a desired body becomes.</summary>
    /// <param name="ns">The resource's namespace, used as a name component.</param>
    /// <param name="name">The resource's own name.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A DELIBERATELY SMALL SPEC, AND EVERY OMISSION IS A DECISION.</b> <c>VpcSpec</c>
    ///         carries <c>defaultSubnet</c>, <c>namespaces</c>, <c>staticRoutes</c>,
    ///         <c>policyRoutes</c>, <c>vpcPeerings</c>, <c>enableExternal</c>,
    ///         <c>extraExternalSubnets</c>, <c>enableBfd</c> and <c>bfdPort</c>. What is rendered is
    ///         <c>enableExternal</c> and nothing else. Why not the rest:
    ///     </para>
    ///     <list type="bullet">
    ///         <item>
    ///             <b><c>namespaces</c></b> — it binds the VPC to Kubernetes namespaces, which is how
    ///             Kube-OVN's own multi-tenancy is usually driven. This platform's tenancy is
    ///             docs/plan/06's, the namespace is <c>ReconcileDriver.NamespaceFor</c>'s, and letting
    ///             a tenant body name namespaces would let one tenant bind another's namespace into
    ///             their router. It is the single most dangerous field on this CRD and it is
    ///             unreachable from the API on purpose.
    ///         </item>
    ///         <item>
    ///             <b><c>staticRoutes</c> and <c>policyRoutes</c></b> — the <c>routeTables</c>
    ///             discussion on this class. No object, an atomic array, and a shape
    ///             <c>ResourceSchema</c> cannot express.
    ///         </item>
    ///         <item>
    ///             <b><c>vpcPeerings</c></b> — docs/plan/14 puts <c>peerings</c> at <b>M3</b>, and a
    ///             peering names another VPC, which is a cross-resource reference this provider has no
    ///             reader for.
    ///         </item>
    ///         <item>
    ///             <b><c>enableBfd</c>, <c>bfdPort</c>, <c>extraExternalSubnets</c>,
    ///             <c>defaultSubnet</c></b> — each is a property of how the <i>platform</i> wires a
    ///             region's fabric rather than something a tenant chooses, and
    ///             <c>defaultSubnet</c> in particular would let a body elect one child subnet as the
    ///             VPC's default, which is a second, conflicting spelling of a fact the subnet's own
    ///             resource already owns.
    ///         </item>
    ///     </list>
    ///     <para>
    ///         ⚠ <b><c>bfdPort</c> is the one field with a CRD default and it is still not
    ///         rendered.</b> <c>+kubebuilder:default=false</c> on <c>bfdPort.enabled</c> means the API
    ///         server writes the object back carrying a <c>bfdPort</c> block this provider never sent
    ///         — which is the containment case in miniature and is exactly what
    ///         <c>NetworkMatchesTests</c> constructs, because the harness's derived CRD stub has an
    ///         open schema and will never produce it.
    ///     </para>
    ///     <para>
    ///         ⚠ No labels, no annotations and no namespace here — ADR-013's seven labels and two
    ///         annotations are injected by <c>KubeCommand</c> non-overridably, and this object is
    ///         cluster-scoped so there is no namespace to write.
    ///     </para>
    /// </remarks>
    public static string VpcJson(string ns, string name, JsonElement desired) =>
        new JsonObject {
            // ⚠ The kind is written here as well as being injected on the apply path, from the SAME
            // GroupVersionKind constant KubeCommandBuilder is handed — so the two cannot disagree.
            // ClickHouseClusters established the rule for a provider owning two kinds; this family
            // owns four and follows it for all of them.
            ["kind"] = VpcKind.Kind,
            ["metadata"] = new JsonObject { ["name"] = ObjectNameOf(ns, name) },
            ["spec"] = new JsonObject { ["enableExternal"] = EnableExternal(desired) }
        }.ToJsonString();

    /// <summary>
    ///     Whether a <c>Vpc</c> read back from a cluster carries what the desired body asks for.
    /// </summary>
    /// <param name="objectJson">The object's JSON, exactly as the API server returned it.</param>
    /// <param name="desired">The desired body.</param>
    /// <remarks>
    ///     ⚠ <b>CONTAINMENT. It checks the ONE field this provider sets and ignores everything
    ///     else.</b> The reasons are on this class and the short form is that the Kube-OVN controller
    ///     writes back to <c>.spec</c> — a finalizer, <c>staticRoutes[].policy</c>, and entries removed
    ///     from <c>staticRoutes</c> outright — quite apart from the <c>bfdPort</c> block the CRD
    ///     defaults in. An equality comparison would report drift on a converged network forever, and
    ///     the reconciler would answer <c>InProgress</c> on every pass for the life of the resource.
    /// </remarks>
    public static bool Matches(string objectJson, JsonElement desired) =>
        Spec(objectJson) is { } spec
        && spec["enableExternal"]?.GetValue<bool>() == EnableExternal(desired);

    /// <summary>The <c>spec</c> of a <c>Vpc</c> document, or <see langword="null" />.</summary>
    /// <param name="objectJson">The object's JSON.</param>
    static JsonObject? Spec(string objectJson) {
        JsonNode? parsed;
        try {
            parsed = JsonNode.Parse(objectJson);
        } catch (JsonException) {
            return null;
        }

        return parsed is JsonObject document
            && document["kind"]?.GetValue<string>() is (null or "Vpc")
            && document["spec"] is JsonObject spec
                ? spec
                : null;
    }

    // ── A body, for tests, fixtures and the conformance case ──────────────────────────────────

    /// <summary>Builds a body that satisfies <see cref="Schema2026" />.</summary>
    /// <param name="clusterId">The cluster to place the network in.</param>
    /// <param name="addressSpaceV4">The IPv4 address space.</param>
    /// <param name="addressSpaceV6">The IPv6 address space, or empty.</param>
    /// <param name="enableExternal">Whether to attach the router externally.</param>
    /// <param name="location">The region.</param>
    /// <remarks>
    ///     ⚠ <b>The default address space is <c>10.20.0.0/16</c>, which is docs/plan/14's own worked
    ///     example and — checked rather than assumed — overlaps no row of
    ///     <see cref="NetworkAddressing.ReservedRanges" /> in any region.</b>
    ///     <c>NetworkAddressTests</c> asserts that, because a default body that the reconciler then
    ///     refuses would make every conformance assertion in the family fail for a reason that has
    ///     nothing to do with what it was testing.
    ///     <para>
    ///         ⚠ Every property it writes is a <b>leaf</b>, for the reason every provider's
    ///         <c>Body</c> gives: <c>ResourceSchema.Project</c> skips a <see cref="SchemaKind.Nested" />
    ///         container and rebuilds it from whichever leaf lands first.
    ///     </para>
    /// </remarks>
    public static string Body(
        Guid clusterId,
        string addressSpaceV4 = "10.20.0.0/16",
        string addressSpaceV6 = "",
        bool enableExternal = false,
        string location = "eu-central"
    ) =>
        new JsonObject {
            ["location"] = location,
            ["properties"] = new JsonObject {
                ["clusterId"] = clusterId.ToString("D", CultureInfo.InvariantCulture),
                ["addressSpace"] = new JsonObject {
                    ["v4"] = addressSpaceV4, ["v6"] = addressSpaceV6
                },
                ["enableExternal"] = enableExternal
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
}
