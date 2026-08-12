using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace CyberCloud.Providers.Network.Contracts;

/// <summary>
///     One CIDR prefix, parsed — an address, a prefix length and the family they imply.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>A type rather than a string, because every question this family asks about an address
///         space is a question two strings cannot answer.</b> "Does <c>10.20.0.0/16</c> overlap
///         <c>10.20.5.0/24</c>" is true and no comparison of the two spellings says so; and
///         <c>10.0.0.5/24</c> and <c>10.0.0.0/24</c> are the same network written two ways, which
///         matters here for a reason beyond tidiness — see <see cref="Canonical" />.
///     </para>
///     <para>
///         ⚠ <b>Parsing is <see cref="IPAddress" />'s, not a regular expression's, and the two are
///         used for different jobs on purpose.</b> <see cref="V4Pattern" /> and
///         <see cref="V6Pattern" /> exist to be declared as a <see cref="SchemaProperty.Pattern" />,
///         so that a malformed prefix is refused by <c>ResourceSchema.Validate</c> <i>before</i> the
///         write path answers <c>202</c>. They are a shape check and nothing more. Everything that
///         needs the value's meaning — overlap, containment, family — goes through
///         <see cref="TryParse" />, which delegates to the BCL. A second, regular-expression opinion
///         about what an address <i>is</i> would be a second opinion this platform then has to keep
///         in step with the first.
///     </para>
/// </remarks>
public readonly record struct Cidr {
    /// <summary>The network address, with every host bit cleared.</summary>
    public IPAddress Network { get; private init; }

    /// <summary>The prefix length in bits.</summary>
    public int PrefixLength { get; private init; }

    /// <summary>Whether this is an IPv6 prefix.</summary>
    public bool IsV6 => Network.AddressFamily == AddressFamily.InterNetworkV6;

    /// <summary>The prefix in its one canonical spelling, <c>network/length</c>.</summary>
    /// <remarks>
    ///     ⚠ <b>This is what Kube-OVN stores, and that is why it is here rather than being a
    ///     convenience.</b> <c>pkg/controller/subnet.go</c>'s <c>formatCIDR</c> runs every element of
    ///     <c>spec.cidrBlock</c> through Go's <c>net.ParseCIDR</c> and writes back
    ///     <c>ipNet.String()</c> — so a tenant who sends <c>10.0.0.5/24</c> gets <c>10.0.0.0/24</c>
    ///     stored, on the object, by the controller. A comparison of the sent string against the read
    ///     string would therefore report permanent drift on a subnet that is perfectly converged, and
    ///     the reconciler would loop forever reporting <c>InProgress</c>. <c>NetworkSubnets.Matches</c>
    ///     compares parsed networks for exactly this reason, and <c>NetworkMatchesTests</c> pins the
    ///     case.
    /// </remarks>
    public string Canonical =>
        Network.ToString() + "/" + PrefixLength.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    ///     Parses a CIDR prefix, clearing host bits.
    /// </summary>
    /// <param name="text">The prefix, <c>address/length</c>.</param>
    /// <param name="cidr">The parsed prefix.</param>
    /// <returns>Whether <paramref name="text" /> is a well-formed prefix.</returns>
    /// <remarks>
    ///     ⚠ A prefix length outside the family's range is refused rather than clamped: <c>/33</c> on
    ///     an IPv4 address is a typo, and clamping it to <c>/32</c> would silently produce a network
    ///     the caller did not ask for.
    /// </remarks>
    public static bool TryParse(string? text, out Cidr cidr) {
        cidr = default;

        if (string.IsNullOrEmpty(text)) {
            return false;
        }

        var slash = text.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0 || slash == text.Length - 1) {
            return false;
        }

        if (!IPAddress.TryParse(text[..slash], out var address)) {
            return false;
        }

        // ⚠ `int.TryParse` with NumberStyles.None, so `+8`, ` 8` and `0x8` are all refused. A prefix
        // length is a bare run of digits and nothing else.
        if (!int.TryParse(
                text[(slash + 1)..],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var prefix
            )) {
            return false;
        }

        var bits = address.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32;
        if (prefix < 0 || prefix > bits) {
            return false;
        }

        cidr = new() { Network = Mask(address, prefix), PrefixLength = prefix };
        return true;
    }

    /// <summary>
    ///     Whether two prefixes share any address at all.
    /// </summary>
    /// <param name="other">The other prefix.</param>
    /// <remarks>
    ///     ⚠ <b>Two prefixes overlap exactly when one contains the other's network address</b>, which
    ///     is why this is the shorter prefix's containment test rather than a range intersection. A
    ///     range intersection over 128-bit integers is the same answer arrived at more expensively and
    ///     with more places to be wrong.
    ///     <para>
    ///         ⚠ Two prefixes of different families never overlap. That is not a special case bolted
    ///         on — an IPv4 and an IPv6 prefix describe disjoint address spaces — and it is the line
    ///         that keeps dual-stack from reporting a conflict between a subnet's two halves.
    ///     </para>
    /// </remarks>
    public bool Overlaps(Cidr other) {
        if (IsV6 != other.IsV6) {
            return false;
        }

        var shorter = PrefixLength <= other.PrefixLength ? this : other;
        var longer = PrefixLength <= other.PrefixLength ? other : this;

        return Mask(longer.Network, shorter.PrefixLength).Equals(shorter.Network);
    }

    /// <summary>Whether this prefix wholly contains <paramref name="other" />.</summary>
    /// <param name="other">The candidate sub-prefix.</param>
    public bool Contains(Cidr other) =>
        IsV6 == other.IsV6
        && PrefixLength <= other.PrefixLength
        && Mask(other.Network, PrefixLength).Equals(Network);

    /// <inheritdoc />
    public override string ToString() => Canonical;

    /// <summary>Clears every bit below <paramref name="prefix" />.</summary>
    static IPAddress Mask(IPAddress address, int prefix) {
        var bytes = address.GetAddressBytes();

        for (var index = 0; index < bytes.Length; index++) {
            var remaining = prefix - (index * 8);

            bytes[index] = remaining switch {
                >= 8 => bytes[index],
                <= 0 => 0,
                _ => (byte)(bytes[index] & (0xFF << (8 - remaining)))
            };
        }

        return new(bytes);
    }

    /// <summary>
    ///     The shape of an IPv4 prefix, for <see cref="SchemaProperty.Pattern" />.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Shape only, and deliberately looser than <see cref="TryParse" />.</b> It refuses
    ///     <c>hello</c>, <c>10.0.0.0</c> and <c>10.0.0.0/</c>; it accepts <c>999.1.1.1/8</c>, which
    ///     <see cref="TryParse" /> then refuses. The division of labour is on purpose: a
    ///     <c>Pattern</c> runs on the request path against a caller-supplied string with a 100 ms
    ///     budget, so it must be linear and boring, and every alternative to <c>\d{1,3}</c> that
    ///     enforces 0–255 is a longer expression with more backtracking in it. The exact refusal comes
    ///     from <see cref="TryParse" /> — see <see cref="NetworkAddressing.ProblemWith" />.
    /// </remarks>
    public const string V4Pattern = @"(\d{1,3}\.){3}\d{1,3}/\d{1,2}";

    /// <summary>
    ///     The shape of an IPv6 prefix, for <see cref="SchemaProperty.Pattern" />.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Deliberately permissive, and more so than the v4 one.</b> A complete IPv6 grammar in
    ///     one regular expression is long, unreadable and — with <c>::</c> compression and embedded
    ///     v4 — a well-known source of catastrophic backtracking, which on the request path is a
    ///     denial of service a tenant can trigger with one string
    ///     (<c>SchemaProperty.PatternTimeout</c> exists because of that class of bug). So this
    ///     admits the character set and the slash, and <see cref="TryParse" /> decides.
    /// </remarks>
    public const string V6Pattern = "[0-9A-Fa-f:.]+/[0-9]{1,3}";

    /// <summary>
    ///     <see cref="V4Pattern" />, or the empty string.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>THE WHOLE PATTERN IS WRAPPED IN <c>(…)?</c> AND THE REASON IS A PLATFORM RULE THAT IS
    ///     EASY TO MEET BY ACCIDENT AND IMPOSSIBLE TO MEET BY GUESSING.</b>
    ///     <c>SchemaProperty.Incoherences</c> runs a declared <c>DefaultJson</c> through the
    ///     property's <i>own</i> constraints — <i>"a default the schema would reject is a bug that
    ///     ships as a form nobody can submit"</i> — and it does so at <b>class initialisation</b>,
    ///     which is silo start. So an optional property whose default is <c>""</c> and whose
    ///     <c>Pattern</c> does not admit <c>""</c> is not a validation that never fires: it is a
    ///     <c>TypeInitializationException</c> that takes down the process that would have served the
    ///     type. Found by writing the obvious thing and watching the whole family fail to construct.
    ///     <para>
    ///         ⚠ <b>The alternative — dropping the pattern on the optional half — is the one that must
    ///         not be taken</b>, because it is precisely the IPv6 prefix, and docs/plan/14 § IPv6 makes
    ///         dual-stack a day-one requirement rather than an afterthought. An unpatterned v6 field
    ///         would be the <c>cidr-shape-is-unenforced</c> gap re-entering the family through the
    ///         optional door. <c>charts/managed/kube-ovn-vpc</c>'s own precedent for the shape is
    ///         <c>StorageAccounts.OptionalQuantityPattern</c>, which solves the same problem the same
    ///         way.
    ///     </para>
    /// </remarks>
    public const string OptionalV4Pattern = "(" + V4Pattern + ")?";

    /// <summary><see cref="V6Pattern" />, or the empty string. See <see cref="OptionalV4Pattern" />.</summary>
    public const string OptionalV6Pattern = "(" + V6Pattern + ")?";
}

/// <summary>
///     Where this family's address-space rules live, and the reason they live here rather than in a
///     <see cref="ResourceSchema" />.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THE DECISION THIS CLASS EXISTS TO RECORD, MADE BEFORE ANY CODE WAS WRITTEN AND STATED
///         HERE BECAUSE THE ALTERNATIVE IS THE DEFECT THIS PLATFORM HAS JUST SHIPPED TWICE.</b>
///         docs/plan/14 § Virtual networks requires that <i>"the API validates against a per-region
///         reserved list and rejects with the conflicting range named"</i>. <b>ResourceSchema cannot
///         express that rule, and this was established rather than assumed.</b>
///         <see cref="SchemaProperty" /> carries <c>AllowedValues</c>, <c>Pattern</c>, <c>Format</c>,
///         <c>Minimum</c>/<c>Maximum</c> and the two lengths — every one of which compares <b>one
///         value against a constant</b>. A CIDR-overlap check compares one value against a
///         <i>list</i>, selected by <i>another property</i> (the region), using a
///         <i>relation</i> (overlap) that is not equality. <c>ResourceSchema.Validate</c> walks one
///         property at a time and never sees a second one; there is no cross-property seam and no
///         external-state seam anywhere on it.
///     </para>
///     <para>
///         ⚠ <b>AND THERE IS NO PROVIDER SEAM ANYWHERE ELSE ON THE WRITE PATH EITHER, WHICH IS THE
///         HALF THAT COST THE MOST TO ESTABLISH.</b> <c>ResourceManagerService</c>'s twelve steps run
///         parse → <b>schema validate</b> → ReBAC → locks → policy → quota → index claim → parent edge
///         → durable write → <c>202</c>. <c>IResourceTypeBuilder</c> declares
///         <c>ApiVersion</c>, <c>Reconciler</c>, <c>Meter</c>, <c>Permissions</c>, <c>Action</c>,
///         <c>Display</c>, <c>Chart</c>, <c>SupportsTags</c>, <c>SupportsSoftDelete</c> and
///         <c>RequiresCluster</c> — <b>and no predicate</b>. <c>IPolicyEvaluator</c> is the one hook
///         that can refuse a body before the <c>202</c>, and it is a <i>platform</i> singleton
///         (docs/plan/08 step 5, M3) rather than something a provider registers: a provider cannot
///         reach it, and a provider that could would be writing tenant policy.
///     </para>
///     <para>
///         ⚠ <b>SO THE RULE RUNS IN THE RECONCILER, AFTER THE <c>202</c>, AND THAT IS A DEFECT
///         RATHER THAN A DESIGN.</b> It is the same shape as the one docs/plan/12's Postgres row just
///         shipped — a body the API accepts and the substrate refuses — and naming it as such is the
///         point of this paragraph. What is done about it here, in descending order of how much it
///         helps:
///     </para>
///     <list type="number">
///         <item>
///             <b>Everything expressible in the schema IS in the schema.</b> A malformed prefix —
///             <c>10.0.0.0</c>, <c>hello/24</c>, <c>10.0.0.0/99</c> — is refused by
///             <c>ResourceSchema.Validate</c> with a <c>400</c> and a JSON Pointer, before the write
///             path answers anything, because <see cref="Cidr.V4Pattern" /> is declared as a
///             <see cref="SchemaProperty.Pattern" /> on every single-valued prefix property in this
///             family. That closes the <i>common</i> case at the API.
///         </item>
///         <item>
///             <b>The reserved list is data a test can walk, not prose</b> —
///             <see cref="ReservedRanges" />, on the model of <c>MariaDbServers.SupportedSubset</c>.
///             <c>NetworkAddressTests</c> walks every row against every other row and against every
///             schema example in the family.
///         </item>
///         <item>
///             <b>The reconciler's refusal is terminal and names the conflicting range</b> —
///             <see cref="ProblemWith" /> produces docs/plan/14's <i>"rejects with the conflicting
///             range named"</i> sentence, and the reconciler returns
///             <c>ReconcileOutcome.Failed</c> rather than <c>InProgress</c>, so the resource reaches
///             <c>Failed</c> with an actionable message instead of retrying a body that can never
///             converge.
///         </item>
///     </list>
///     <para>
///         ⚠ <b>What would close it properly</b> is one seam:
///         <c>IResourceTypeBuilder.Validates(Func&lt;JsonElement, string, Result&gt;)</c> — a pure
///         predicate over the body and the region, run as step 2b next to the schema, whose failure is
///         the same <c>InvalidRequestBody</c> with a <c>Target</c> that the schema's own is. It is a
///         change to <c>CyberCloud.ResourceManager</c> and to <c>IProviderBuilder</c>, which is
///         platform surface this provider may not take unilaterally — three other families have
///         recorded a cross-property rule they could not express
///         (<c>charts/managed/kafka</c>'s <c>replication-factor-versus-node-count</c>,
///         <c>charts/managed/seaweedfs</c>'s <c>replication-versus-topology</c>,
///         <c>charts/managed/seaweedfs-bucket</c>'s
///         <c>bucket-cluster-may-differ-from-its-accounts</c>) and this is the fourth and the first
///         where the rule is the <i>subject</i> of the resource type rather than a refinement of it.
///         <c>charts/managed/kube-ovn-vpc/conformance.yaml § owed</c>,
///         <c>address-space-is-validated-after-202</c>.
///     </para>
/// </remarks>
public static class NetworkAddressing {
    /// <summary>
    ///     One range the platform reserves, which no tenant network may overlap.
    /// </summary>
    /// <param name="Id">A stable identifier, for the refusal message and for tests.</param>
    /// <param name="Region">
    ///     The region it applies in, or <c>""</c> for every region. ⚠ See
    ///     <see cref="ReservedRanges" /> for why most rows are global.
    /// </param>
    /// <param name="Prefix">The reserved prefix.</param>
    /// <param name="Because">Why it is reserved, in a sentence a tenant can act on.</param>
    public sealed record ReservedRange(string Id, string Region, string Prefix, string Because) {
        /// <summary>The prefix, parsed. ⚠ Throws at class initialisation if the literal is malformed.</summary>
        public Cidr Cidr { get; } =
            Cidr.TryParse(Prefix, out var parsed)
                ? parsed
                : throw new ArgumentException(
                    $"The reserved range '{Id}' declares the prefix '{Prefix}', which is not a CIDR. A "
                    + "malformed row here would silently reserve nothing, so it fails at class "
                    + "initialisation — which is silo start — rather than at the first create that "
                    + "should have been refused.",
                    nameof(Prefix)
                );

        /// <summary>Whether this row applies in <paramref name="region" />.</summary>
        /// <param name="region">The resource's region.</param>
        public bool AppliesIn(string region) =>
            Region.Length == 0 || string.Equals(Region, region, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The ranges a tenant network may not overlap — docs/plan/14's <i>"per-region reserved
    ///     list"</i>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>OVERLAPPING WITH ANOTHER TENANT — OR WITH YOUR OWN OTHER VPC — IS NOT ON THIS
    ///         LIST AND MUST NOT BE.</b> docs/plan/14: <i>"Overlapping CIDRs between a tenant's VPCs
    ///         is fine; overlapping with the platform's underlay is not."</i> That is the whole point
    ///         of a VPC and it is what Kube-OVN's per-VPC routing tables deliver. A list that also
    ///         refused tenant-to-tenant overlap would make <c>10.0.0.0/16</c> allocatable exactly once
    ///         across the platform, which is not a cloud.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>MOST ROWS ARE GLOBAL AND THE per-region MEMBER IS STILL THE RIGHT SHAPE.</b> The
    ///         ranges below are properties of the <i>software</i> — Kubernetes' own service and pod
    ///         CIDRs, the link-local block, the loopback block — and are the same in every region this
    ///         platform will ever run. What is genuinely per-region is the <b>underlay</b>: the
    ///         physical network a region's nodes sit on, which differs per datacentre and is not
    ///         knowable from this repository. <see cref="ReservedRange.Region" /> exists so that those
    ///         rows have somewhere to go, and the table ships with the rows that are true everywhere
    ///         plus one worked example.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>AND THAT IS THIS TABLE'S REAL LIMIT, STATED RATHER THAN IMPLIED: it is
    ///         COMPILED-IN.</b> A reserved list that a region's operator cannot edit without a
    ///         release is a list that will be wrong in the first region whose underlay is not
    ///         <c>10.0.0.0/8</c>. It is a constant here because the alternative — configuration
    ///         reaching a provider — has no seam either (<c>ReconcileContext</c> carries a cluster
    ///         connection, an <c>ISecretResolver</c> and nothing else), and because a wrong-but-loud
    ///         refusal a tenant can read is better than no check at all. <c>§ owed</c>,
    ///         <c>reserved-list-is-compiled-in</c>.
    ///     </para>
    /// </remarks>
    public static ImmutableArray<ReservedRange> ReservedRanges { get; } = [
        new(
            "kubernetes-services",
            "",
            "10.96.0.0/12",
            "the Kubernetes service CIDR. A tenant network overlapping it would make every in-cluster "
            + "service address ambiguous for workloads in that network, and the symptom is DNS that "
            + "resolves and connections that reach the wrong process."
        ),
        new(
            "kube-ovn-default-subnet",
            "",
            "10.16.0.0/16",
            "Kube-OVN's own default subnet — the one ovn-default that every pod outside a tenant VPC "
            + "sits on. It is the platform's own pod network and it is not shareable."
        ),
        new(
            "kube-ovn-join-subnet",
            "",
            "100.64.0.0/16",
            "Kube-OVN's join subnet, which carries traffic between the node and the OVN gateway. It is "
            + "also RFC 6598 carrier-grade NAT space, which is why it was chosen and why it must not "
            + "be handed to a tenant."
        ),
        new(
            "loopback",
            "",
            "127.0.0.0/8",
            "the loopback block. An address here never leaves the host, so a route to it is a route to "
            + "nowhere."
        ),
        new(
            "link-local",
            "",
            "169.254.0.0/16",
            "the IPv4 link-local block, which carries cloud metadata services and ARP-level "
            + "autoconfiguration. Routing it inside a VPC is how a workload reaches another tenant's "
            + "metadata endpoint."
        ),
        new(
            "multicast",
            "",
            "224.0.0.0/4",
            "the multicast range, which is not unicast address space and cannot be a subnet."
        ),
        new(
            "ipv6-link-local",
            "",
            "fe80::/10",
            "the IPv6 link-local block. Every interface has one already, and a subnet here would "
            + "collide with the address the interface configures for itself."
        ),
        // ⚠ THE ONE ROW THAT IS ACTUALLY per-region, AND IT IS AN EXAMPLE RATHER THAN A FACT. It is
        // here so that the Region member is exercised by the table it belongs to rather than only by
        // a test — an unexercised column is the column that rots — and so that the first operator to
        // add a real underlay row finds the shape already filled in once. The region name is the one
        // every schema in this tree uses as its example.
        new(
            "eu-central-underlay",
            "eu-central",
            "10.0.0.0/16",
            "the physical underlay this region's nodes are addressed on. A tenant network overlapping "
            + "it would make a node unreachable from inside that network, which takes the tenant's own "
            + "workloads down with it."
        )
    ];

    /// <summary>
    ///     What is wrong with a prefix, or <see langword="null" /> when nothing is.
    /// </summary>
    /// <param name="prefix">The prefix as the body spells it.</param>
    /// <param name="region">The resource's region.</param>
    /// <param name="jsonPointer">The JSON Pointer to report, for a message a caller can act on.</param>
    /// <returns>
    ///     docs/plan/14's <i>"rejects with the conflicting range named"</i> sentence, or
    ///     <see langword="null" />.
    /// </returns>
    /// <remarks>
    ///     ⚠ <b>A pure function of its three arguments, and it must stay one.</b> It is called from a
    ///     reconciler, which is a singleton serving every tenant in the process — so a cache, a clock
    ///     or a field would be the hidden state <c>ReconcilerConformance.CheckNoHiddenState</c> is
    ///     structurally blind to. It is <c>static</c> on a <c>static</c> class for that reason and not
    ///     for tidiness.
    /// </remarks>
    public static string? ProblemWith(string prefix, string region, string jsonPointer) {
        if (!Cidr.TryParse(prefix, out var cidr)) {
            return $"'{jsonPointer}' is '{prefix}', which is not a CIDR prefix. A prefix is an address "
                + "and a length, for example '10.20.0.0/16' or 'fd00:20::/64'.";
        }

        foreach (var reserved in ReservedRanges) {
            if (reserved.AppliesIn(region) && cidr.Overlaps(reserved.Cidr)) {
                // ⚠ THE CONFLICTING RANGE IS NAMED, WHICH IS docs/plan/14'S OWN REQUIREMENT AND NOT A
                // NICETY. "Your address space is not allowed" is a message whose only next step is to
                // guess. This one gives the tenant the range, the reason and their own value, which
                // is the same standard docs/plan/08 § Errors sets when it requires a message that
                // "names the actual numbers".
                return $"'{jsonPointer}' is '{prefix}', which overlaps the reserved range "
                    + $"'{reserved.Prefix}' ({reserved.Id}) — {reserved.Because} Choose an address "
                    + "space that does not overlap it. Overlapping another of your own virtual "
                    + "networks is fine and is not what this refuses.";
            }
        }

        return null;
    }

    /// <summary>
    ///     Every reserved range a prefix conflicts with, by id. Empty when it conflicts with none.
    /// </summary>
    /// <param name="prefix">The parsed prefix.</param>
    /// <param name="region">The resource's region.</param>
    /// <remarks>
    ///     ⚠ Exists for <c>NetworkAddressTests</c>, which walks the whole table rather than asserting
    ///     on the first conflict <see cref="ProblemWith" /> happens to report. A rule that is right
    ///     about the first row and wrong about the eighth is the one a single-case test misses.
    /// </remarks>
    public static ImmutableArray<string> ConflictsWith(Cidr prefix, string region) =>
        [
            .. ReservedRanges
                .Where(x => x.AppliesIn(region) && prefix.Overlaps(x.Cidr))
                .Select(x => x.Id)
        ];
}
