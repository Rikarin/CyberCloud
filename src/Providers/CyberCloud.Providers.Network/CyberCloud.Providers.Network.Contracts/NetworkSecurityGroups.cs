using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Network.Contracts;

/// <summary>
///     One port, or one contiguous run of ports — the shape a rule's port list is built out of.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>A type rather than a string, for the reason <see cref="Cidr" /> is one</b>: every
///         question this file asks about a port list — is <c>443-80</c> backwards, how many rules does
///         <c>80,443,8000-8100</c> become, does the substrate's <c>portRangeMin</c> get 80 or 8000 —
///         is a question the text cannot answer.
///     </para>
///     <para>
///         ⚠ <b><see cref="PortPattern" /> IS EXACT WHERE <see cref="Cidr.V4Pattern" /> IS DELIBERATELY
///         LOOSE, AND THE DIFFERENCE IS NOT AN INCONSISTENCY.</b> A CIDR's exact grammar in a regular
///         expression is long, has embedded-v4 and <c>::</c> compression to get wrong, and is a known
///         backtracking hazard — so that family checks the <i>shape</i> at the API and the
///         <i>meaning</i> in <see cref="Cidr.TryParse" />. A port is a bounded decimal integer, so the
///         alternation below is six branches with disjoint leading digits, no nesting and no
///         ambiguity, and it refuses <c>0</c>, <c>65536</c> and <c>99999</c> <b>at the API, with a
///         JSON Pointer, before the write path answers <c>202</c></b>. Kube-OVN's own
///         <c>validateSgRule</c> refuses the same values in the controller, which is after everything.
///         Enforcing what can be enforced is the whole of
///         <c>charts/managed/kube-ovn-vpc/conformance.yaml § owed</c>'s
///         <c>address-space-is-validated-after-202</c> mitigation, applied to a property where it
///         happens to reach further.
///     </para>
///     <para>
///         ⚠ <b>What is still left over is exactly one relation: <c>min &lt;= max</c>.</b> That
///         compares two numbers <i>inside one string</i>, which no <see cref="SchemaProperty" />
///         constraint sees, so it runs in the reconciler as a terminal failure — the same seam, one
///         relation wide rather than a whole table wide. <c>§ owed</c>,
///         <c>a-backwards-port-range-is-refused-after-202</c>.
///     </para>
/// </remarks>
/// <param name="Low">The first port in the run.</param>
/// <param name="High">The last port in the run, equal to <paramref name="Low" /> for a single port.</param>
public readonly record struct PortRange(int Low, int High) {
    /// <summary>The lowest port number a rule may name.</summary>
    public const int MinPort = 1;

    /// <summary>The highest port number a rule may name.</summary>
    public const int MaxPort = 65535;

    /// <summary>One port, 1–65535, as a regular expression with no capture-free alternatives.</summary>
    /// <remarks>
    ///     ⚠ Six branches with <b>disjoint leading digits</b>, which is what keeps it linear: an input
    ///     can enter at most one branch, so there is nothing for the engine to backtrack into.
    ///     <c>SchemaProperty.Pattern</c> anchors what it is given as <c>^(?:…)$</c>, so no anchors are
    ///     written here.
    /// </remarks>
    public const string PortPattern =
        "(6553[0-5]|655[0-2][0-9]|65[0-4][0-9]{2}|6[0-4][0-9]{3}|[1-5][0-9]{4}|[1-9][0-9]{0,3})";

    /// <summary>One entry — a port, or two ports joined by a hyphen.</summary>
    public const string EntryPattern = PortPattern + "(-" + PortPattern + ")?";

    /// <summary>A non-empty comma-separated list of entries.</summary>
    public const string ListPattern = EntryPattern + "(," + EntryPattern + ")*";

    /// <summary>
    ///     <see cref="ListPattern" />, or the empty string.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The optional wrapper, for the reason <see cref="Cidr.OptionalV4Pattern" /> carries in
    ///     full</b>: <c>SchemaProperty.Incoherences</c> runs a declared <c>DefaultJson</c> through the
    ///     property's own constraints at <b>class initialisation</b>, so an optional property whose
    ///     default is <c>""</c> and whose pattern refuses <c>""</c> is a
    ///     <c>TypeInitializationException</c> at silo start rather than a validation that never fires.
    ///     Fourth sighting of that interaction in this family.
    /// </remarks>
    public const string OptionalListPattern = "(" + ListPattern + ")?";

    /// <summary>Whether this run names a single port.</summary>
    public bool IsSingle => Low == High;

    /// <inheritdoc />
    public override string ToString() =>
        IsSingle
            ? Low.ToString(CultureInfo.InvariantCulture)
            : Low.ToString(CultureInfo.InvariantCulture)
            + "-"
            + High.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    ///     Parses a comma-separated port list, in declaration order.
    /// </summary>
    /// <param name="text">The list, or the empty string.</param>
    /// <param name="ranges">The parsed runs, in the order they were written.</param>
    /// <returns>
    ///     <see langword="null" /> when the list is well-formed, or the sentence naming what is wrong
    ///     with it.
    /// </returns>
    /// <remarks>
    ///     ⚠ <b>Order is preserved and duplicates are not collapsed.</b> Each entry becomes one
    ///     Kube-OVN rule, and the count of rules a body renders has to be a pure function of the body
    ///     for the reconciler to be idempotent — collapsing would make it a function of the values
    ///     instead, which is the same answer arrived at with a branch that can be wrong.
    ///     <para>
    ///         ⚠ The empty list is <b>not</b> an error. It is how a body says "no TCP" — see
    ///         <see cref="NetworkSecurityGroups.Rules" /> for why that is the safe reading rather than
    ///         a missing configuration.
    ///     </para>
    /// </remarks>
    public static string? TryParseList(string text, out ImmutableArray<PortRange> ranges) {
        ranges = [];

        if (text.Length == 0) {
            return null;
        }

        var built = ImmutableArray.CreateBuilder<PortRange>();

        foreach (var entry in text.Split(',')) {
            var hyphen = entry.IndexOf('-', StringComparison.Ordinal);

            var lowText = hyphen < 0 ? entry : entry[..hyphen];
            var highText = hyphen < 0 ? entry : entry[(hyphen + 1)..];

            if (!TryPort(lowText, out var low) || !TryPort(highText, out var high)) {
                return $"'{entry}' is not a port or a port range. A port is a number from "
                    + $"{MinPort} to {MaxPort}, and a range is two of them joined by a hyphen, for "
                    + "example '8000-8100'.";
            }

            if (low > high) {
                // ⚠ THE ONE RELATION THE SCHEMA CANNOT SEE. Pattern refines one string against a
                // constant; this compares two numbers inside that string. It is the narrowest
                // possible instance of the defect this family already carries, and it is named as
                // one rather than described as a design.
                return $"'{entry}' names the range {low}-{high}, whose first port is higher than its "
                    + "last. A range is written low first, for example '8000-8100'.";
            }

            built.Add(new(low, high));
        }

        ranges = built.ToImmutable();
        return null;
    }

    /// <summary>Parses one port, refusing anything that is not a bare run of digits in range.</summary>
    /// <param name="text">The port.</param>
    /// <param name="port">The parsed port.</param>
    /// <remarks>
    ///     ⚠ <c>NumberStyles.None</c>, so <c>+80</c>, <c> 80</c> and <c>0x50</c> are all refused —
    ///     the same rule <see cref="Cidr.TryParse" /> applies to a prefix length, and for the same
    ///     reason: a port is a bare run of digits and nothing else.
    /// </remarks>
    static bool TryPort(string text, out int port) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port)
        && port is >= MinPort and <= MaxPort;
}

/// <summary>
///     One rule as the fabric will program it — the flattened form of a body's compact declaration.
/// </summary>
/// <param name="Direction">
///     <c>ingress</c> or <c>egress</c>. ⚠ Not rendered into the object: it selects <i>which array</i>
///     the rule lands in.
/// </param>
/// <param name="IpVersion">
///     <c>ipv4</c> or <c>ipv6</c>. ⚠ <b>Lower case.</b> Kube-OVN's <c>validateSgRule</c> is
///     <c>if rule.IPVersion != "ipv4" &amp;&amp; rule.IPVersion != "ipv6" { return errors.New(...) }</c>
///     — which is the <i>opposite</i> spelling to <c>Subnet.spec.protocol</c>'s
///     <c>IPv4</c>/<c>IPv6</c>/<c>Dual</c>, in the same API group, on an adjacent object. Getting it
///     wrong is a rule the controller refuses in its own logs long after the resource reported
///     <c>Succeeded</c>.
/// </param>
/// <param name="Protocol">
///     <c>tcp</c>, <c>udp</c> or <c>icmp</c>. ⚠ Kube-OVN also defines <c>all</c> and this
///     api-version never emits it — see <see cref="NetworkSecurityGroups.Schema2026" />.
/// </param>
/// <param name="RemoteAddress">The CIDR the rule matches against.</param>
/// <param name="Ports">The port run, or <see langword="null" /> for ICMP, which has none.</param>
public sealed record SecurityRule(
    string Direction,
    string IpVersion,
    string Protocol,
    string RemoteAddress,
    PortRange? Ports
) {
    /// <summary>The rule in one sentence, for <c>POST …/showEffectiveRules</c>.</summary>
    /// <remarks>
    ///     ⚠ <b>A sentence rather than four columns, and that is a registry limit showing through
    ///     rather than a choice</b> — <c>SchemaProperty.ElementKind</c> refuses an array of objects on
    ///     a response schema exactly as it does on a request one. It is the same flattening
    ///     <c>VirtualNetworks.ShowIsolationResponse</c> does to its limits table, and it is recorded
    ///     once, at <c>§ owed</c>, <c>an-array-of-objects-is-not-expressible</c>.
    /// </remarks>
    public string Describe() {
        var where = string.Equals(Direction, NetworkSecurityGroups.Ingress, StringComparison.Ordinal)
            ? "from"
            : "to";

        var ports = Ports is { } range
            ? range.IsSingle ? " port " + range : " ports " + range
            : string.Empty;

        return $"allow {Direction} {Protocol}{ports} {where} {RemoteAddress} ({IpVersion})";
    }
}

/// <summary>
///     Everything addressable about <c>CyberCloud.Network/virtualNetworks/securityGroups</c> — the
///     rules that become OVN ACLs on the ports in a tenant network, as a Kube-OVN
///     <c>SecurityGroup</c>.
/// </summary>
/// <remarks>
///     <para>
///         <b>The authority is docs/plan/14 § Virtual networks</b>, whose tree gives
///         <c>securityGroups/{name}</c> as a child of a virtual network, and
///         <c>VirtualNetworks.IsolationLimits</c>' <c>not-a-firewall-by-default</c> row, which
///         promises exactly this type by name as what a tenant who wants a deny-by-default perimeter
///         should ask for. Until now that row pointed at something that did not exist.
///     </para>
///     <para>
///         ⚠ <b>THE BLOCKER THIS TYPE WAS OWED FOR WAS REAL AND IT IS SOLVED BY RESHAPING, NOT BY
///         SURRENDERING — THE SAME MOVE <c>addressSpace</c> MADE, ONE STEP FURTHER.</b>
///         <c>NetworkProvider</c>'s remarks record it: <c>SecurityGroupSpec</c> is
///         <c>ingressRules</c>/<c>egressRules</c> of
///         <c>SecurityGroupRule{ipVersion, protocol, priority, remoteType, remoteAddress,
///         portRangeMin, portRangeMax, policy}</c> — an <b>array of objects</b>, which
///         <c>SchemaProperty.ElementKind</c> refuses outright: <i>"an array element is a scalar … a
///         nested or nested-array element would need its own pointer space, which is the flat-list
///         property ResourceSchema is built on"</i>. That refusal is not worked around here and it is
///         not disputed. What changed is the <b>question</b>: docs/plan/14 asks for a security group,
///         not for a JSON transcription of Kube-OVN's <c>SecurityGroupRule</c>, and a security group
///         is expressible in scalars.
///     </para>
///     <list type="number">
///         <item>
///             <b>A group is one coherent allow-set, and a port carries several groups.</b> That is
///             not this platform inventing a composition model to get out of trouble — it is
///             Kube-OVN's own. A port is attached to security groups through the
///             <c>…kubernetes.io/security_groups</c> annotation, whose value is a
///             <b>comma-separated list of group names</b>. So "one group per purpose, several groups
///             per workload" is the substrate's grain, and the arity a single group needs is
///             <i>one</i>.
///         </item>
///         <item>
///             <b>The remote is a v4 slot and a v6 slot</b>, which is <c>addressSpace</c>'s shape
///             exactly, models docs/plan/14 § IPv6's <i>"a v4 prefix, a v6 prefix, or both"</i>
///             by construction, and — the point — keeps <see cref="Cidr.V4Pattern" /> declarable as a
///             <c>Pattern</c>, which ADR-012's fifth surface refuses on an array.
///         </item>
///         <item>
///             <b>The ports are one patterned string per protocol</b>, not an array of numbers.
///             <see cref="PortRange.OptionalListPattern" /> refuses <c>0</c>, <c>65536</c> and
///             <c>99999</c> at the API with a JSON Pointer; an <c>Array</c> of
///             <c>SchemaKind.WholeNumber</c> could carry no <c>Minimum</c>/<c>Maximum</c> per element
///             at all, because those are <i>property</i> constraints and there is no element-bounds
///             member. So the string is <b>more</b> validated than the array shape would have been,
///             not less.
///         </item>
///     </list>
///     <para>
///         ⚠ <b>WHAT THE RESHAPE COSTS, STATED PLAINLY, BECAUSE IT IS NOT NOTHING.</b> A single group
///         cannot say "allow 443 from A and 5432 from B" — that is two groups. It cannot say
///         <c>protocol: all</c>, so a rule for SCTP or GRE is unreachable and "everything TCP" is
///         spelled <c>1-65535</c>. It cannot name another security group as the remote
///         (<c>remoteType: securityGroup</c>), which is a cross-resource reference with no reader —
///         rule 2 — and would need the referenced group's rendered object name. And it cannot express
///         a <c>drop</c> or <c>pass</c> rule, because with a default-deny base a <c>drop</c> is only
///         meaningful against a broader <c>allow</c> at a higher priority, which is the arity this
///         shape does not have. All four are recorded at
///         <c>charts/managed/kube-ovn-security-group/conformance.yaml § owed</c> rather than left to
///         be discovered.
///     </para>
///     <para>
///         ⚠ <b>AN EMPTY RULE SET DENIES EVERYTHING, AND THAT WAS ESTABLISHED FROM THE SUBSTRATE
///         RATHER THAN ASSUMED — IT IS THE ONE QUESTION THIS TYPE HAD TO GET RIGHT.</b> Read in
///         <c>pkg/ovs/ovn-nb-acl.go</c>: <c>CreateSgDenyAllACL</c> installs
///         <c>outport == @{portGroup} &amp;&amp; ip</c> and <c>inport == @{portGroup} &amp;&amp; ip</c>
///         with action <b>drop</b> at <c>util.SecurityGroupDropPriority</c> (2003);
///         <c>CreateSgBaseACL</c> adds ARP, ICMPv6, DHCP and VRRP exceptions at
///         <c>SecurityGroupBasePriority</c> (2005); and every rule this type renders lands at
///         <c>SecurityGroupAllowPriority</c> (2004). <c>pkg/controller/security_group.go</c> has
///         <b>no special case for an empty rule list</b> — the port group and the base ACLs are
///         created regardless. So a group that declares nothing is a group that permits nothing, the
///         empty body is the <i>safe</i> body, and a tenant cannot arrive at "allow everything" by
///         omission. That is the opposite of the failure this class of question usually has, and it is
///         why the schema has no <c>defaultPolicy</c> property: there is only one policy and the
///         substrate has already chosen it.
///     </para>
///     <para>
///         ⚠ <b><c>allowSameGroupTraffic</c> DEFAULTS TO <c>false</c>, AND IT IS THE ONE PLACE A
///         PERMISSIVE ANSWER COULD HAVE SLIPPED IN.</b> It is the single field on
///         <c>SecurityGroupSpec</c> that grants traffic nobody wrote a rule for. Kube-OVN declares it
///         <c>bool</c> with <b>no</b> <c>+kubebuilder:default</c>, so an omitted field is Go's zero
///         value and already <c>false</c> — and it is nevertheless <b>sent explicitly</b>, because
///         "the substrate's zero value happens to be safe" is a fact about a version of Go source
///         rather than a property of this resource, and because <c>Matches</c> can only compare a
///         field that was sent.
///     </para>
///     <para>
///         ⚠ <b>CLUSTER-SCOPED, LIKE THE REST OF THE FAMILY, AND THE PARENT EDGE IS THE PLATFORM'S
///         RATHER THAN THE FABRIC'S — WHICH IS A DIFFERENCE FROM <c>subnets</c> WORTH KNOWING.</b>
///         <c>pkg/apis/kubeovn/v1/security-group.go</c>:
///         <c>// +kubebuilder:resource:scope="Cluster",shortName="sg",path="security-groups",singular="security-group"</c>.
///         A <c>Subnet</c> binds to its parent through <c>spec.vpc</c>; a <c>SecurityGroup</c> has
///         <b>no <c>vpc</c> field at all</b> — it is a global port group that a <i>port</i> names.
///         So <see cref="ObjectNameOf" /> folds the parent network's name in for <i>naming</i>, and
///         <b>nothing in the fabric confines the group to that network</b>. What that does and does
///         not cost: it cannot leak, because the rendered name is
///         <c>{namespace}-{network}-{group}</c> and a workload can only reference a name it can
///         derive, and the platform resolves the reference by resource id through
///         <c>CyberCloud.ResourceManager</c> when docs/plan/13's workloads arrive. What it means is
///         that the containment a reader infers from the <i>address</i> is an authorization and
///         naming fact, not a routing one. <c>§ owed</c>,
///         <c>the-parent-edge-is-not-enforced-by-the-fabric</c>.
///     </para>
///     <para>
///         ⚠ <b>THE FIRST OBJECT IN THIS FAMILY WHOSE SPEC THE CONTROLLER DOES <i>NOT</i> REWRITE, AND
///         <see cref="Matches" /> IS STILL CONTAINMENT.</b> Checked rather than assumed:
///         <c>pkg/controller/security_group.go</c> writes through <c>patchSgStatus</c>, which is a
///         <c>MergePatchType</c> against the <b><c>"status"</c> subresource</b> — the object carries
///         <c>+kubebuilder:subresource:status</c> — and there is no <c>SecurityGroups().Update(...)</c>
///         of a spec anywhere in it. So the argument this family has used three times does not apply
///         here. Containment applies anyway, for the reason it applies to every object in the tree:
///         a finalizer, a future field, or another field manager's addition is not drift in what this
///         provider asked for, and an equality comparison would report one.
///     </para>
///     <para>
///         ⚠ <b>No <c>SupportsSoftDelete</c> — and the reason every earlier file in this family gives
///         is now WRONG and is corrected on <c>VirtualNetworks</c>.</b> The manager honours a recovery
///         window today. The reason this type does not declare one is that its rules <i>are</i> its
///         content, the content is small, and a group whose name is parked for a week while its ACLs
///         are gone is a perimeter a tenant would reasonably believe still exists.
///     </para>
/// </remarks>
public static class NetworkSecurityGroups {
    /// <summary>The provider namespace — the network's, because a child shares its parent's.</summary>
    public const string ProviderNamespace = VirtualNetworks.ProviderNamespace;

    /// <summary>The type path. ⚠ Interleaved, exactly as <c>virtualNetworks/subnets</c> is.</summary>
    public const string TypePath = "virtualNetworks/securityGroups";

    /// <summary>The one api-version. ⚠ Equal to the network's, and it must be.</summary>
    public const string V2026 = VirtualNetworks.V2026;

    /// <summary>The chart this type is the configuration surface of.</summary>
    public const string ChartName = "managed/kube-ovn-security-group";

    /// <summary>The pointer <c>RequiresCluster</c> names.</summary>
    public const string ClusterIdPointer = ClusterPlacement.DefaultPointer;

    /// <summary>The type, namespace and path together.</summary>
    public static ResourceTypeName Type { get; } = new(ProviderNamespace, TypePath);

    // ── The vocabulary the substrate uses, as constants ───────────────────────────────────────

    /// <summary>The inbound direction.</summary>
    public const string Ingress = "ingress";

    /// <summary>The outbound direction.</summary>
    public const string Egress = "egress";

    /// <summary>
    ///     Kube-OVN's spelling of the IPv4 family on a security-group rule.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Lower case, and the adjacent object in the same API group spells it the other
    ///     way.</b> <c>Subnet.spec.protocol</c> is <c>IPv4</c>/<c>IPv6</c>/<c>Dual</c>;
    ///     <c>SecurityGroupRule.ipVersion</c> is <c>ipv4</c>/<c>ipv6</c>, enforced by
    ///     <c>validateSgRule</c> in the controller. A constant rather than a literal because the two
    ///     spellings sit forty lines apart in this family's own code.
    /// </remarks>
    public const string IpV4 = "ipv4";

    /// <summary>Kube-OVN's spelling of the IPv6 family. See <see cref="IpV4" />.</summary>
    public const string IpV6 = "ipv6";

    /// <summary>The TCP protocol, as <c>SgProtocolTCP</c> spells it.</summary>
    public const string Tcp = "tcp";

    /// <summary>The UDP protocol, as <c>SgProtocolUDP</c> spells it.</summary>
    public const string Udp = "udp";

    /// <summary>The ICMP protocol, as <c>SgProtocolICMP</c> spells it.</summary>
    public const string Icmp = "icmp";

    /// <summary>
    ///     The rule action every rule this type renders carries.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b><c>allow</c>, and its two siblings are <c>drop</c> and <c>pass</c> — NOT
    ///     <c>deny</c>.</b> The doc comment in the Go source says <i>"allow, pass or deny"</i> and the
    ///     constants are <c>SgPolicyAllow</c>/<c>SgPolicyDrop</c>/<c>SgPolicyPass</c> bound to OVN ACL
    ///     actions, so the value is <c>drop</c>. That README-versus-code disagreement was recorded by
    ///     the previous pass over this family and is why the constant is here rather than typed
    ///     inline.
    /// </remarks>
    public const string PolicyAllow = "allow";

    /// <summary>The remote kind every rule this type renders carries — <c>SgRemoteTypeAddress</c>.</summary>
    public const string RemoteTypeAddress = "address";

    /// <summary>
    ///     The priority every rule this type renders carries.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>One priority for every rule, and that is correct rather than lazy, because every rule
    ///     is an <c>allow</c>.</b> Priority orders rules against each other, and a set of pure allows
    ///     over a default drop has no order — whichever matches, permits. It is sent explicitly
    ///     because Kube-OVN's <c>validateSgRule</c> refuses a priority outside
    ///     <c>SecurityGroupPriorityMin</c> (1) to <c>SecurityGroupPriorityMax</c> (16384), and an
    ///     omitted field is <c>0</c>.
    /// </remarks>
    public const int RulePriority = 1;

    // ── The object a security group IS ────────────────────────────────────────────────────────

    /// <summary>The Kube-OVN <c>SecurityGroup</c> custom resource.</summary>
    /// <remarks>
    ///     ⚠ <b>The plural is <c>security-groups</c>, HYPHENATED.</b>
    ///     <c>ClusterConformanceHarness</c> derives its CRD stub's path from
    ///     <see cref="GroupVersionKind.Plural" />, so a guessed <c>securitygroups</c> would install a
    ///     definition at a path the apply never reaches and every cluster-facing assertion would
    ///     <c>404</c> with a message about a missing operator.
    /// </remarks>
    public static GroupVersionKind SecurityGroupKind { get; } =
        new() {
            Group = "kubeovn.io", Version = "v1", Kind = "SecurityGroup", Plural = "security-groups"
        };

    /// <summary>
    ///     The name of the <c>SecurityGroup</c> a security group renders: its namespace, its network's
    ///     name and its own, joined.
    /// </summary>
    /// <param name="ns">The resource's namespace, used as a name component.</param>
    /// <param name="id">The security group's address.</param>
    /// <exception cref="ArgumentException"><paramref name="id" /> carries no parent name.</exception>
    /// <remarks>
    ///     ⚠ <b>Three components, exactly as <see cref="NetworkSubnets.ObjectNameOf(string, ResourceId)" /> — and here the
    ///     third one is doing MORE work than it does there.</b> A <c>Subnet</c> at least names its
    ///     <c>Vpc</c>, so a collision would be visible in the object. A <c>SecurityGroup</c> names
    ///     nothing, so this name is the <b>only</b> thing separating two networks' groups called
    ///     <c>web</c> — and a collision would merge two tenants' rule sets into one port group with no
    ///     error anywhere.
    /// </remarks>
    public static string ObjectNameOf(string ns, ResourceId id) =>
        id.ParentNames.Length == 0
            ? throw new ArgumentException(
                $"'{id.Path}' carries no parent name, so the SecurityGroup object it renders would "
                + "collide with every other network's group of the same name — and a SecurityGroup is "
                + "CLUSTER-SCOPED and carries no reference to its network, so the object itself would "
                + "show nothing wrong. A security group is a child type and its address always "
                + "interleaves its network — see NetworkSecurityGroups.TypePath.",
                nameof(id)
            )
            : ns + "-" + id.ParentNames.Replace('/', '-') + "-" + id.Name;

    /// <summary>The <c>SecurityGroup</c> a security group owns.</summary>
    /// <param name="ns">The resource's namespace, used as a name component.</param>
    /// <param name="id">The security group's address.</param>
    /// <remarks>⚠ Cluster-scoped: <c>Namespace</c> is empty. See <see cref="VirtualNetworks.VpcRef" />.</remarks>
    public static ObjectRef SecurityGroupRef(string ns, ResourceId id) =>
        new() { Kind = SecurityGroupKind, Namespace = string.Empty, Name = ObjectNameOf(ns, id) };

    // ── The action ────────────────────────────────────────────────────────────────────────────

    /// <summary>The action that reports the rules a body actually becomes.</summary>
    /// <remarks>
    ///     ⚠ <b>IT EXISTS BECAUSE OF THE RESHAPE, AND IT IS THE HALF OF THE RESHAPE THAT MAKES IT
    ///     HONEST.</b> A tenant writes six scalars and the fabric programs some number of rules; the
    ///     mapping is documented on <see cref="Rules" /> and a document is not where somebody checks a
    ///     firewall. This returns the flattened list, in the order it is applied, from the resource's
    ///     own stored body — so "did <c>tcpPorts: 80,443</c> and two remotes really become four
    ///     rules" is a question the platform answers rather than one the reader infers.
    ///     <para>
    ///         ⚠ It is a <b>pure function of the stored body</b> and reaches no cluster, which is what
    ///         lets it be synchronous and handler-backed. It reports what the platform <i>asked</i>
    ///         for, not what OVN currently has — <see cref="EffectiveRulesResponse" />'s
    ///         <c>/note</c> says so on every response rather than leaving the distinction to a reader.
    ///     </para>
    /// </remarks>
    public const string EffectiveRulesAction = "showEffectiveRules";

    /// <summary>The permission <see cref="EffectiveRulesAction" /> checks.</summary>
    /// <remarks>
    ///     ⚠ <c>read</c>. What leaves is a restatement of the resource's own body, which the caller
    ///     could already <c>GET</c>. A permission of its own would be a role nobody grants.
    /// </remarks>
    public const string EffectiveRulesPermission = "read";

    // ── The body shape ────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     The body shape at <see cref="V2026" />.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>SIX SCALARS PER DIRECTION AND NOT ONE ARRAY, WHICH IS THE WHOLE ARGUMENT OF THIS
    ///     FILE</b> — see the remarks on this class for why that is a reshape rather than a
    ///     concession, what it costs, and why an empty body is the safe one.
    ///     <para>
    ///         ⚠ <b>Nothing here is <c>Required</c> beyond the platform's own three, and that is
    ///         deliberate.</b> A required rule property would mean a security group could not be
    ///         created empty — and an empty security group is the <i>most</i> restrictive one there
    ///         is, so demanding a rule would be demanding that a tenant open something in order to
    ///         create a perimeter.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Nothing here is <c>Immutable</c> beyond the platform's own three either, and that
    ///         is the difference from every other type in this family.</b> An address space and a
    ///         prefix are immutable because changing one renumbers a live network. A rule is exactly
    ///         the thing a tenant edits — closing a port they opened last week is the ordinary
    ///         operation, and an immutable rule set would make it a delete and a re-create of the
    ///         perimeter, with a window in between during which nothing is attached.
    ///     </para>
    /// </remarks>
    public static ResourceSchema Schema2026 { get; } =
        ResourceSchema.Of(
            [
                new(
                    "/location",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The region the security group lives in. It must be the network's "
                    + "own region — nothing checks that."
                ) {
                    Format = SchemaFormat.Region,
                    Widget = WidgetHint.Region,
                    Immutable = true,
                    ExampleJson = "\"eu-central\""
                },
                new(
                    "/properties",
                    SchemaKind.Nested,
                    Description: "The security group's own settings."
                ),
                new(
                    ClusterIdPointer,
                    SchemaKind.Text,
                    Required: true,
                    Description: "The cluster whose fabric carries the security group. Must be the "
                    + "cluster the network is in — nothing checks that."
                ) {
                    Format = SchemaFormat.Uuid,
                    Widget = WidgetHint.Cluster,
                    Immutable = true
                },
                new(
                    "/properties/allowSameGroupTraffic",
                    SchemaKind.Boolean,
                    Description: "Whether workloads that carry this same security group may reach each "
                    + "other without a rule. Off by default: it is the one setting here that permits "
                    + "traffic nobody wrote a rule for."
                ) {
                    DefaultJson = "false"
                },
                // ── Inbound ───────────────────────────────────────────────────────────────────
                new(
                    "/properties/ingress",
                    SchemaKind.Nested,
                    Description: "What may reach workloads carrying this group. ⚠ Anything not "
                    + "allowed here is dropped — the fabric installs a default-deny for every port in "
                    + "a security group, so an empty section permits nothing."
                ),
                new(
                    "/properties/ingress/remoteV4",
                    SchemaKind.Text,
                    Description: "The IPv4 range inbound traffic may come from, in CIDR form, or empty "
                    + "for no IPv4 rules at all. Use 0.0.0.0/0 for the whole internet."
                ) {
                    Pattern = Cidr.OptionalV4Pattern,
                    Widget = WidgetHint.Cidr,
                    DefaultJson = "\"\"",
                    ExampleJson = "\"10.20.0.0/16\""
                },
                new(
                    "/properties/ingress/remoteV6",
                    SchemaKind.Text,
                    Description: "The IPv6 range inbound traffic may come from, or empty for no IPv6 "
                    + "rules at all. ⚠ A group with only an IPv4 remote silently permits nothing over "
                    + "IPv6, which on a dual-stack subnet is not the same as permitting nothing."
                ) {
                    Pattern = Cidr.OptionalV6Pattern,
                    Widget = WidgetHint.Cidr,
                    DefaultJson = "\"\"",
                    ExampleJson = "\"fd00:20::/48\""
                },
                new(
                    "/properties/ingress/tcpPorts",
                    SchemaKind.Text,
                    Description: "TCP ports inbound traffic may reach, as a comma-separated list of "
                    + "ports and ranges — for example 80,443,8000-8100. Empty means no TCP is allowed "
                    + "inbound. ⚠ There is no way to say 'every protocol'; 1-65535 says 'every TCP "
                    + "port'."
                ) {
                    Pattern = PortRange.OptionalListPattern,
                    DefaultJson = "\"\"",
                    ExampleJson = "\"80,443\""
                },
                new(
                    "/properties/ingress/udpPorts",
                    SchemaKind.Text,
                    Description: "UDP ports inbound traffic may reach, in the same form as tcpPorts. "
                    + "Empty means no UDP is allowed inbound."
                ) {
                    Pattern = PortRange.OptionalListPattern,
                    DefaultJson = "\"\"",
                    ExampleJson = "\"53\""
                },
                new(
                    "/properties/ingress/allowIcmp",
                    SchemaKind.Boolean,
                    Description: "Whether inbound ICMP is allowed from the remotes above. Off by "
                    + "default. ⚠ With this off, a workload in this group does not answer ping and "
                    + "does not receive path-MTU messages."
                ) {
                    DefaultJson = "false"
                },
                // ── Outbound ──────────────────────────────────────────────────────────────────
                new(
                    "/properties/egress",
                    SchemaKind.Nested,
                    Description: "What workloads carrying this group may reach. ⚠ Same default-deny: "
                    + "an empty section permits no outbound traffic at all, which for most workloads "
                    + "means no DNS and no package downloads."
                ),
                new(
                    "/properties/egress/remoteV4",
                    SchemaKind.Text,
                    Description: "The IPv4 range outbound traffic may reach, or empty for no IPv4 "
                    + "rules at all."
                ) {
                    Pattern = Cidr.OptionalV4Pattern,
                    Widget = WidgetHint.Cidr,
                    DefaultJson = "\"\"",
                    ExampleJson = "\"0.0.0.0/0\""
                },
                new(
                    "/properties/egress/remoteV6",
                    SchemaKind.Text,
                    Description: "The IPv6 range outbound traffic may reach, or empty for no IPv6 "
                    + "rules at all."
                ) {
                    Pattern = Cidr.OptionalV6Pattern,
                    Widget = WidgetHint.Cidr,
                    DefaultJson = "\"\"",
                    ExampleJson = "\"::/0\""
                },
                new(
                    "/properties/egress/tcpPorts",
                    SchemaKind.Text,
                    Description: "TCP ports outbound traffic may reach, in the same form as the "
                    + "inbound list. Empty means no outbound TCP."
                ) {
                    Pattern = PortRange.OptionalListPattern,
                    DefaultJson = "\"\"",
                    ExampleJson = "\"443\""
                },
                new(
                    "/properties/egress/udpPorts",
                    SchemaKind.Text,
                    Description: "UDP ports outbound traffic may reach. Empty means no outbound UDP — "
                    + "which includes DNS on port 53."
                ) {
                    Pattern = PortRange.OptionalListPattern,
                    DefaultJson = "\"\"",
                    ExampleJson = "\"53\""
                },
                new(
                    "/properties/egress/allowIcmp",
                    SchemaKind.Boolean,
                    Description: "Whether outbound ICMP is allowed to the remotes above. Off by "
                    + "default."
                ) {
                    DefaultJson = "false"
                }
            ]
        );

    /// <summary>The pointers <see cref="Schema2026" /> declares, in declaration order.</summary>
    public static ImmutableArray<string> Pointers2026 { get; } =
        [.. Schema2026.Properties.Select(x => x.JsonPointer)];

    /// <summary>
    ///     What a <c>POST …/showEffectiveRules</c> returns.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b><c>/rules</c> is an array of sentences for the reason
    ///     <c>VirtualNetworks.ShowIsolationResponse</c>'s <c>/limits</c> is</b> —
    ///     <see cref="SchemaProperty.ElementKind" /> refuses an array of objects on a response schema
    ///     exactly as on a request one. Fourth sighting in this family, and the cheapest of the four:
    ///     a client that wanted the columns can split on spaces, and the sentence is the form a human
    ///     auditing a firewall wants anyway.
    /// </remarks>
    public static ResourceSchema EffectiveRulesResponse { get; } =
        ResourceSchema.Of(
            [
                new(
                    "/rules",
                    SchemaKind.Array,
                    Required: true,
                    Description: "Every rule this security group's body becomes, in the order it is "
                    + "written to the fabric, one sentence each. An empty list means the group permits "
                    + "nothing, which is a valid and fully restrictive configuration."
                ) {
                    ElementKind = SchemaKind.Text
                },
                new(
                    "/count",
                    SchemaKind.WholeNumber,
                    Required: true,
                    Description: "How many rules there are. ⚠ Worth reading next to the body: one "
                    + "port list of three entries with two remotes is six rules."
                ),
                new(
                    "/defaultAction",
                    SchemaKind.Text,
                    Required: true,
                    Description: "What happens to traffic no rule above matches. It is always 'drop' "
                    + "— the fabric installs a default-deny for every port in a security group and "
                    + "this resource cannot change that."
                ),
                new(
                    "/allowSameGroupTraffic",
                    SchemaKind.Boolean,
                    Required: true,
                    Description: "Whether workloads carrying this same group reach each other without "
                    + "a rule. ⚠ Returned because it is the one permission that is not in the list "
                    + "above."
                ),
                new(
                    "/note",
                    SchemaKind.Text,
                    Required: true,
                    Description: "⚠ What this answer is and is not. It is what the platform asks the "
                    + "fabric for, derived from the resource's stored body — not a reading of the "
                    + "ACLs OVN currently holds."
                )
            ]
        );

    // ── The desired body, read ────────────────────────────────────────────────────────────────

    /// <summary>Whether a body lets members of the group reach each other without a rule.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static bool AllowSameGroupTraffic(JsonElement desired) =>
        Root(desired, "allowSameGroupTraffic") is { ValueKind: JsonValueKind.True };

    /// <summary>The IPv4 remote one direction declares, or empty.</summary>
    /// <param name="desired">The validated desired body.</param>
    /// <param name="direction"><see cref="Ingress" /> or <see cref="Egress" />.</param>
    public static string RemoteV4(JsonElement desired, string direction) =>
        Text(desired, direction, "remoteV4");

    /// <summary>The IPv6 remote one direction declares, or empty.</summary>
    /// <param name="desired">The validated desired body.</param>
    /// <param name="direction"><see cref="Ingress" /> or <see cref="Egress" />.</param>
    public static string RemoteV6(JsonElement desired, string direction) =>
        Text(desired, direction, "remoteV6");

    /// <summary>The TCP port list one direction declares, or empty.</summary>
    /// <param name="desired">The validated desired body.</param>
    /// <param name="direction"><see cref="Ingress" /> or <see cref="Egress" />.</param>
    public static string TcpPorts(JsonElement desired, string direction) =>
        Text(desired, direction, "tcpPorts");

    /// <summary>The UDP port list one direction declares, or empty.</summary>
    /// <param name="desired">The validated desired body.</param>
    /// <param name="direction"><see cref="Ingress" /> or <see cref="Egress" />.</param>
    public static string UdpPorts(JsonElement desired, string direction) =>
        Text(desired, direction, "udpPorts");

    /// <summary>Whether one direction allows ICMP.</summary>
    /// <param name="desired">The validated desired body.</param>
    /// <param name="direction"><see cref="Ingress" /> or <see cref="Egress" />.</param>
    public static bool AllowIcmp(JsonElement desired, string direction) =>
        Section(desired, direction) is { ValueKind: JsonValueKind.Object } section
        && section.TryGetProperty("allowIcmp", out var value)
        && value.ValueKind is JsonValueKind.True;

    /// <summary>
    ///     What is wrong with a body's port lists, or <see langword="null" /> when nothing is.
    /// </summary>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     ⚠ <b>Only ONE relation reaches here and it is <c>min &lt;= max</c>.</b> Every other thing
    ///     that can be wrong with a port list — a non-number, a zero, a 65536, a stray character — is
    ///     refused by <see cref="PortRange.OptionalListPattern" /> with a <c>400</c> and a JSON
    ///     Pointer before the write path answers. This is what is left over, it is named at
    ///     <c>§ owed</c> as <c>a-backwards-port-range-is-refused-after-202</c>, and it is a materially
    ///     smaller version of the same defect the address-space check is.
    /// </remarks>
    public static string? PortProblem(JsonElement desired) {
        foreach (var direction in Directions) {
            foreach (var (protocol, list) in
                     (ReadOnlySpan<(string, string)>)[
                         (Tcp, TcpPorts(desired, direction)),
                         (Udp, UdpPorts(desired, direction))
                     ]) {
                if (PortRange.TryParseList(list, out _) is { } problem) {
                    return $"'/properties/{direction}/{protocol}Ports' is '{list}': {problem}";
                }
            }
        }

        return null;
    }

    /// <summary>The two directions, in the order rules are written.</summary>
    /// <remarks>
    ///     ⚠ Order matters for <see cref="Matches" /> and for idempotency — the two arrays are
    ///     separate fields on the object, but the <i>within</i>-array order has to be a pure function
    ///     of the body or every reconcile pass would report drift.
    /// </remarks>
    public static ImmutableArray<string> Directions { get; } = [Ingress, Egress];

    /// <summary>
    ///     Every rule one direction of a body becomes, in the order it is written.
    /// </summary>
    /// <param name="desired">The validated desired body.</param>
    /// <param name="direction"><see cref="Ingress" /> or <see cref="Egress" />.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>THE EXPANSION, IN ONE PLACE, BECAUSE IT IS THE THING A READER MOST NEEDS TO BE
    ///         ABLE TO CHECK.</b> A direction declares up to two remotes and up to two port lists, and
    ///         the rules are the <b>cross product</b>: for each family whose remote is set, one rule
    ///         per TCP entry, then one rule per UDP entry, then one ICMP rule if asked. So
    ///         <c>remoteV4</c> and <c>remoteV6</c> both set with <c>tcpPorts: 80,443</c> is four
    ///         rules, and <c>POST …/showEffectiveRules</c> exists so a tenant can see that without
    ///         doing the arithmetic.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A remote with no protocols yields NOTHING, and that is the safe reading of an
    ///         unfinished configuration rather than an oversight.</b> The alternative — treating
    ///         "a remote and no ports" as <c>protocol: all</c> — would make a half-typed body the
    ///         most permissive one the type can express. <c>charts/managed/kafka</c>'s
    ///         <c>allowedCidrs</c> settled the same question the same way: <i>"an empty list with
    ///         external exposure on renders a load balancer that accepts nothing, which is the safe
    ///         reading of an unfinished configuration"</i>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A malformed port list yields nothing rather than throwing.</b> The reconciler
    ///         refuses the body through <see cref="PortProblem" /> before it renders anything, so this
    ///         is only ever reached with a list that parses — and a renderer that threw would turn a
    ///         tenant's typo into an unhandled exception on the reconcile path.
    ///     </para>
    /// </remarks>
    public static ImmutableArray<SecurityRule> Rules(JsonElement desired, string direction) {
        var built = ImmutableArray.CreateBuilder<SecurityRule>();

        var allowIcmp = AllowIcmp(desired, direction);

        PortRange.TryParseList(TcpPorts(desired, direction), out var tcp);
        PortRange.TryParseList(UdpPorts(desired, direction), out var udp);

        foreach (var (family, remote) in
                 (ReadOnlySpan<(string, string)>)[
                     (IpV4, RemoteV4(desired, direction)),
                     (IpV6, RemoteV6(desired, direction))
                 ]) {
            if (remote.Length == 0) {
                continue;
            }

            foreach (var range in tcp) {
                built.Add(new(direction, family, Tcp, remote, range));
            }

            foreach (var range in udp) {
                built.Add(new(direction, family, Udp, remote, range));
            }

            if (allowIcmp) {
                built.Add(new(direction, family, Icmp, remote, null));
            }
        }

        return built.ToImmutable();
    }

    /// <summary>Every rule a body becomes, both directions, in the order they are written.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static ImmutableArray<SecurityRule> AllRules(JsonElement desired) =>
        [.. Directions.SelectMany(x => Rules(desired, x))];

    // ── The object a desired body becomes ─────────────────────────────────────────────────────

    /// <summary>The <c>SecurityGroup</c> document a desired body becomes.</summary>
    /// <param name="ns">The resource's namespace, used as a name component.</param>
    /// <param name="id">The security group's address.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>NO <c>tier</c>, AND IT IS THE ONE OMITTED FIELD WORTH EXPLAINING.</b>
    ///         <c>SecurityGroupSpec.Tier</c> chooses which OVN ACL tier a group's rules land in —
    ///         <c>SecurityGroupAPITierMinimum</c> 0 to <c>SecurityGroupAPITierMaximum</c> 1 — and a
    ///         lower tier is evaluated first. It is how an <i>operator</i> layers a platform-wide
    ///         policy above a tenant's own, which makes it exactly the kind of field a tenant body
    ///         must not reach: a group that elected the lower tier would evaluate ahead of a policy
    ///         the platform had not yet written. Omitted is <c>0</c>, and every group being in one
    ///         tier is what makes "whichever allow matches, permits" true.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>NO <c>remoteSecurityGroup</c>, <c>localAddress</c> or source port range on any
    ///         rule.</b> The first is a cross-resource reference with no reader (rule 2); the other
    ///         two are per-rule refinements this shape has no slot for and no evidence anybody needs
    ///         at M1. <c>§ owed</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Zero-valued optional fields are OMITTED rather than sent as <c>0</c>.</b> An ICMP
    ///         rule has no ports, and <c>portRangeMin: 0</c> is a value Kube-OVN's own
    ///         <c>validateSgRule</c> would refuse if it looked at it. What is sent is what
    ///         <see cref="Matches" /> compares, so the two stay in step by construction.
    ///     </para>
    ///     <para>
    ///         ⚠ No labels, no annotations and no namespace here — ADR-013's seven labels and two
    ///         annotations are injected by <c>KubeCommand</c> non-overridably, and this object is
    ///         cluster-scoped so there is no namespace to write.
    ///     </para>
    /// </remarks>
    public static string SecurityGroupJson(string ns, ResourceId id, JsonElement desired) =>
        new JsonObject {
            ["kind"] = SecurityGroupKind.Kind,
            ["metadata"] = new JsonObject { ["name"] = ObjectNameOf(ns, id) },
            ["spec"] = new JsonObject {
                ["allowSameGroupTraffic"] = AllowSameGroupTraffic(desired),
                ["ingressRules"] = RuleArray(Rules(desired, Ingress)),
                ["egressRules"] = RuleArray(Rules(desired, Egress))
            }
        }.ToJsonString();

    /// <summary>One direction's rules, as the array Kube-OVN stores.</summary>
    /// <param name="rules">The rules, in order.</param>
    static JsonArray RuleArray(ImmutableArray<SecurityRule> rules) {
        var array = new JsonArray();

        foreach (var rule in rules) {
            var node = new JsonObject {
                ["ipVersion"] = rule.IpVersion,
                ["protocol"] = rule.Protocol,
                ["priority"] = RulePriority,
                ["remoteType"] = RemoteTypeAddress,
                ["remoteAddress"] = rule.RemoteAddress,
                ["policy"] = PolicyAllow
            };

            if (rule.Ports is { } ports) {
                node["portRangeMin"] = ports.Low;
                node["portRangeMax"] = ports.High;
            }

            array.Add(node);
        }

        return array;
    }

    /// <summary>
    ///     Whether a <c>SecurityGroup</c> read back from a cluster carries what the desired body asks
    ///     for.
    /// </summary>
    /// <param name="objectJson">The object's JSON, exactly as the API server returned it.</param>
    /// <param name="desired">The desired body.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>CONTAINMENT, AND FOR ONCE NOT BECAUSE THE CONTROLLER REWRITES THE SPEC — IT DOES
    ///         NOT.</b> Checked in <c>pkg/controller/security_group.go</c>: every write it makes is
    ///         <c>patchSgStatus</c>, a merge patch against the <c>"status"</c> subresource, and there
    ///         is no update of a spec anywhere in the file. This is the first object in the family for
    ///         which the previous three files' argument does not hold. Containment is used anyway,
    ///         because it is right for the general reason: a finalizer, a field a later Kube-OVN adds,
    ///         or another field manager's addition is not drift in what this provider asked for.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The RULE ARRAYS are compared element by element on the fields this provider sends,
    ///         and the COUNT must match.</b> A shorter array read back is a rule that was dropped and
    ///         a longer one is a rule somebody else added, and both are drift on the property that is
    ///         the whole subject of the resource. The arrays carry no <c>x-kubernetes-list-type</c>,
    ///         so they are atomic under server-side apply — which for <i>this</i> type is harmless
    ///         rather than fatal, because one resource owns the whole object and there is no second
    ///         writer to erase. (That is precisely the difference from <c>routeTables</c>, where two
    ///         resources would have shared one <c>Vpc</c>'s array.)
    ///     </para>
    /// </remarks>
    public static bool Matches(string objectJson, JsonElement desired) {
        if (Spec(objectJson) is not { } spec) {
            return false;
        }

        if (spec["allowSameGroupTraffic"]?.GetValue<bool>() != AllowSameGroupTraffic(desired)) {
            return false;
        }

        return SameRules(spec["ingressRules"], Rules(desired, Ingress))
            && SameRules(spec["egressRules"], Rules(desired, Egress));
    }

    /// <summary>Whether a stored rule array carries exactly the rules a body asks for, in order.</summary>
    /// <param name="found">The array on the object, or <see langword="null" />.</param>
    /// <param name="wanted">The rules the body renders.</param>
    /// <remarks>
    ///     ⚠ <b>An absent array and an empty one are the same thing here</b>, because Go's
    ///     <c>omitempty</c> on <c>ingressRules</c> means a group with no inbound rules round-trips as
    ///     a missing key rather than as <c>[]</c>. Treating those as different would make every
    ///     egress-only group report drift forever.
    /// </remarks>
    static bool SameRules(JsonNode? found, ImmutableArray<SecurityRule> wanted) {
        if (found is null) {
            return wanted.Length == 0;
        }

        if (found is not JsonArray array || array.Count != wanted.Length) {
            return false;
        }

        for (var index = 0; index < wanted.Length; index++) {
            if (array[index] is not JsonObject rule || !SameRule(rule, wanted[index])) {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether one stored rule carries the fields this provider sends.</summary>
    /// <param name="found">The rule on the object.</param>
    /// <param name="wanted">The rule the body renders.</param>
    static bool SameRule(JsonObject found, SecurityRule wanted) {
        if (Value(found, "ipVersion") != wanted.IpVersion
            || Value(found, "protocol") != wanted.Protocol
            || Value(found, "remoteType") != RemoteTypeAddress
            || Value(found, "remoteAddress") != wanted.RemoteAddress
            || Value(found, "policy") != PolicyAllow) {
            return false;
        }

        // ⚠ An ICMP rule sends no ports, so what is compared is that the object carries none either.
        // A `portRangeMin: 0` read back would be a value somebody else wrote.
        return wanted.Ports is { } ports
            ? Number(found, "portRangeMin") == ports.Low && Number(found, "portRangeMax") == ports.High
            : found["portRangeMin"] is null && found["portRangeMax"] is null;
    }

    static string? Value(JsonObject rule, string name) =>
        rule[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    static int? Number(JsonObject rule, string name) =>
        rule[name] is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;

    /// <summary>The <c>spec</c> of a <c>SecurityGroup</c> document, or <see langword="null" />.</summary>
    /// <param name="objectJson">The object's JSON.</param>
    static JsonObject? Spec(string objectJson) {
        JsonNode? parsed;
        try {
            parsed = JsonNode.Parse(objectJson);
        } catch (JsonException) {
            return null;
        }

        return parsed is JsonObject document
            && document["kind"]?.GetValue<string>() is (null or "SecurityGroup")
            && document["spec"] is JsonObject spec
                ? spec
                : null;
    }

    // ── A body, for tests, fixtures and the conformance case ──────────────────────────────────

    /// <summary>Builds a body that satisfies <see cref="Schema2026" />.</summary>
    /// <param name="clusterId">The cluster to place the security group in.</param>
    /// <param name="ingressRemoteV4">The IPv4 range inbound traffic may come from.</param>
    /// <param name="ingressTcpPorts">The TCP ports inbound traffic may reach.</param>
    /// <param name="egressRemoteV4">The IPv4 range outbound traffic may reach.</param>
    /// <param name="egressTcpPorts">The TCP ports outbound traffic may reach.</param>
    /// <param name="allowSameGroupTraffic">Whether members reach each other without a rule.</param>
    /// <param name="location">The region.</param>
    /// <remarks>
    ///     ⚠ <b>The default is a WEB TIER — inbound 80 and 443 from the whole internet, outbound 443
    ///     to it — rather than the empty group, and the choice is deliberate.</b> An empty body is a
    ///     valid security group and is the one this type is proudest of, but it renders <b>zero</b>
    ///     rules, so a fixture built from it would make every assertion about the rendered object
    ///     vacuously true — <c>Matches</c> would compare two empty arrays and pass on a renderer that
    ///     did nothing at all. <c>NetworkSecurityGroupTests</c> covers the empty group explicitly, as
    ///     a case, where it can be asserted rather than assumed.
    ///     <para>
    ///         ⚠ Every property this writes is a <b>leaf</b>, for the reason every provider's
    ///         <c>Body</c> gives: a <see cref="SchemaKind.Nested" /> container is skipped by
    ///         projection and rebuilt from whichever leaf lands first.
    ///     </para>
    /// </remarks>
    public static string Body(
        Guid clusterId,
        string ingressRemoteV4 = "0.0.0.0/0",
        string ingressTcpPorts = "80,443",
        string egressRemoteV4 = "0.0.0.0/0",
        string egressTcpPorts = "443",
        bool allowSameGroupTraffic = false,
        string location = "eu-central"
    ) =>
        new JsonObject {
            ["location"] = location,
            ["properties"] = new JsonObject {
                ["clusterId"] = clusterId.ToString("D", CultureInfo.InvariantCulture),
                ["allowSameGroupTraffic"] = allowSameGroupTraffic,
                ["ingress"] = new JsonObject {
                    ["remoteV4"] = ingressRemoteV4,
                    ["remoteV6"] = "",
                    ["tcpPorts"] = ingressTcpPorts,
                    ["udpPorts"] = "",
                    ["allowIcmp"] = false
                },
                ["egress"] = new JsonObject {
                    ["remoteV4"] = egressRemoteV4,
                    ["remoteV6"] = "",
                    ["tcpPorts"] = egressTcpPorts,
                    ["udpPorts"] = "",
                    ["allowIcmp"] = false
                }
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

    static JsonElement? Section(JsonElement desired, string direction) => Root(desired, direction);

    static string Text(JsonElement desired, string direction, string name) =>
        Section(desired, direction) is { ValueKind: JsonValueKind.Object } section
        && section.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}
