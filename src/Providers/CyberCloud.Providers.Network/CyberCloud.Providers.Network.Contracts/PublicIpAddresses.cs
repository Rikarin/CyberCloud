using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Network.Contracts;

/// <summary>
///     One IP address, parsed — the host form, where <see cref="Cidr" /> is the prefix form.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>A separate type from <see cref="Cidr" /> rather than a prefix with an implied
///         <c>/32</c>, because the two are asked different questions.</b> Everything in this family
///         before this type describes a <i>range</i> — an address space, a subnet prefix, a security
///         group's remote — and every one of them ends in a slash and a length. A public address is a
///         single host, <c>OvnEip.spec.v4Ip</c> is a bare address with no length, and
///         <c>10.100.0.7/32</c> would be refused by the substrate rather than read as the same thing.
///     </para>
///     <para>
///         ⚠ <b>The division of labour is <see cref="Cidr" />'s and is copied deliberately.</b>
///         <see cref="V4Pattern" /> and <see cref="V6Pattern" /> exist to be declared as a
///         <see cref="SchemaProperty.Pattern" /> so that a malformed address is refused with a
///         <c>400</c> and a JSON Pointer <i>before</i> the write path answers <c>202</c>; they are a
///         shape check and nothing more. <see cref="TryParse" /> delegates to
///         <see cref="IPAddress" /> for the meaning. A second regular-expression opinion about what an
///         address <i>is</i> would be a second opinion this platform then has to keep in step with the
///         first.
///     </para>
/// </remarks>
public static class IpAddresses {
    /// <summary>
    ///     The shape of an IPv4 address, for <see cref="SchemaProperty.Pattern" />.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b><see cref="Cidr.V4Pattern" /> without the length, and loose for the same reason.</b> It
    ///     accepts <c>999.1.1.1</c>, which <see cref="TryParse" /> then refuses; every alternative to
    ///     <c>\d{1,3}</c> that enforces 0–255 is a longer expression with more backtracking in it, and
    ///     this one runs on the request path against a caller-supplied string.
    /// </remarks>
    public const string V4Pattern = @"(\d{1,3}\.){3}\d{1,3}";

    /// <summary>
    ///     The shape of an IPv6 address, for <see cref="SchemaProperty.Pattern" />.
    /// </summary>
    /// <remarks>
    ///     ⚠ Deliberately permissive, for <see cref="Cidr.V6Pattern" />'s reason: a complete IPv6
    ///     grammar in one expression is a known catastrophic-backtracking hazard on a request path with
    ///     a 100 ms budget. ⚠ <b>It admits upper case and the substrate does not</b> —
    ///     <c>handleAddOvnEip</c> refuses an EIP whose <c>spec.v6Ip</c> contains an upper-case
    ///     character, by name — so <see cref="ProblemWith" /> refuses it here instead, where the caller
    ///     still gets a pointer.
    /// </remarks>
    public const string V6Pattern = "[0-9A-Fa-f:.]+";

    /// <summary><see cref="V4Pattern" />, or the empty string.</summary>
    /// <remarks>
    ///     ⚠ Wrapped in <c>(…)?</c> for the reason <see cref="Cidr.OptionalV4Pattern" /> records:
    ///     <c>SchemaProperty.Incoherences</c> runs a declared <c>DefaultJson</c> through the property's
    ///     own constraints at <b>class initialisation</b>, so an optional property defaulting to
    ///     <c>""</c> under a pattern that refuses <c>""</c> is a <c>TypeInitializationException</c> at
    ///     silo start rather than a validation that never fires.
    /// </remarks>
    public const string OptionalV4Pattern = "(" + V4Pattern + ")?";

    /// <summary><see cref="V6Pattern" />, or the empty string.</summary>
    public const string OptionalV6Pattern = "(" + V6Pattern + ")?";

    /// <summary>
    ///     Parses an address of an expected family.
    /// </summary>
    /// <param name="text">The address.</param>
    /// <param name="v6">Whether an IPv6 address is expected.</param>
    /// <param name="address">The parsed address.</param>
    /// <returns>Whether <paramref name="text" /> is a well-formed address of that family.</returns>
    /// <remarks>
    ///     ⚠ <b>The family is checked rather than inferred</b>, because the two properties this backs
    ///     are separate slots and a v6 address typed into the v4 slot has to be refused <i>at that
    ///     slot's pointer</i> — otherwise the caller is told their v6 address is fine and the fabric
    ///     later allocates from a pool that has no such range.
    /// </remarks>
    public static bool TryParse(string? text, bool v6, out IPAddress address) {
        address = IPAddress.None;

        if (string.IsNullOrEmpty(text) || !IPAddress.TryParse(text, out var parsed)) {
            return false;
        }

        var isV6 = parsed.AddressFamily == AddressFamily.InterNetworkV6;

        if (isV6 != v6) {
            return false;
        }

        address = parsed;
        return true;
    }

    /// <summary>
    ///     What is wrong with a requested address, or <see langword="null" /> when nothing is.
    /// </summary>
    /// <param name="text">The address as the body spells it. Empty is not a problem.</param>
    /// <param name="v6">Whether an IPv6 address is expected.</param>
    /// <param name="jsonPointer">The JSON Pointer to report.</param>
    /// <remarks>
    ///     ⚠ <b>A pure function of its three arguments and it must stay one</b> — it is called from a
    ///     reconciler, which is a singleton serving every tenant in the process. Same rule, same
    ///     reason, as <see cref="NetworkAddressing.ProblemWith" />.
    /// </remarks>
    public static string? ProblemWith(string text, bool v6, string jsonPointer) {
        if (text.Length == 0) {
            return null;
        }

        var family = v6 ? "IPv6" : "IPv4";

        if (!TryParse(text, v6, out _)) {
            return $"'{jsonPointer}' is '{text}', which is not an {family} address. It is a bare "
                + $"address with no prefix length — for example {(v6 ? "'fd00:ff::7'" : "'10.100.0.7'")} "
                + "— because a public address is one host rather than a range.";
        }

        // ⚠ THE ONE RULE THAT IS NOT A SHAPE, AND IT IS THE SUBSTRATE'S. handleAddOvnEip refuses an
        // EIP whose spec.v6Ip contains an upper-case character — `util.ContainsUppercase` — and does
        // so in the controller, which is after this platform has answered 202. Refusing it here costs
        // nothing and turns a silent non-convergence into a message with a pointer in it.
        return v6 && text.Any(char.IsAsciiLetterUpper)
            ? $"'{jsonPointer}' is '{text}', which contains an upper-case letter. The fabric refuses "
            + "an IPv6 address that does, so write it in lower case."
            : null;
    }
}

/// <summary>
///     Everything addressable about <c>CyberCloud.Network/publicIpAddresses</c> — one allocated
///     public address, as a Kube-OVN <c>OvnEip</c>.
/// </summary>
/// <remarks>
///     <para>
///         <b>The authority is docs/plan/14 § Everything else</b>, whose row reads
///         <i>"<c>publicIpAddresses</c> · M1 · ⊂ the VPC provider; a metered, quota'd, allocatable
///         resource in its own right — because IPv4 is scarce and must be accounted"</i>, and
///         § Load balancing, which is where the allocator question is asked.
///     </para>
///     <para>
///         ⚠ <b>THE POINT OF THIS TYPE IS THE METER, AND IT IS THE FIRST TYPE IN THE PLATFORM THAT CAN
///         DRAW <c>QuotaMeter.PublicIps</c> AT ALL.</b> That meter has existed since
///         <c>QuotaGrain</c>'s defaults — 20 per subscription — and no shipping type has ever drawn it,
///         because every provider that reached for it wanted to draw it <i>conditionally</i> (an
///         address only when a body asked for external exposure) and <c>QuotaGrain.TryReserveAsync</c>
///         refuses a non-positive amount by name: <i>"A reservation must be positive; 0 is not."</i>
///         <b>An address resource does not have that problem</b>: it draws exactly one,
///         unconditionally, for its whole life. ⚠ And the shape is confirmed at the code rather than
///         argued — <c>ResourceManagerService.AmountFor</c> answers <c>meter.Fallback ?? 1m</c> for a
///         meter with an empty <c>AmountPointer</c>, so <c>.Meters(QuotaMeter.PublicIps, …)</c> is a
///         <b>flat</b> meter of one and needs no pointer, no fallback and no <c>MeterDerivation</c>.
///     </para>
///     <para>
///         ⚠ <b>A TOP-LEVEL TYPE AND NOT A CHILD OF <c>virtualNetworks</c>, WHICH IS THE OPPOSITE OF
///         WHERE A READER OF THIS FAMILY WOULD PUT IT.</b> Its two siblings are children because a
///         <c>Subnet</c> binds to a <c>Vpc</c> and a security group is scoped to one network by name.
///         An <c>OvnEip</c> binds to <b>neither</b>: it is allocated out of the <i>external</i> subnet
///         — the operator's underlay — and it is attached to a tenant's routing domain later, by a
///         separate <c>OvnFip</c>, <c>OvnDnatRule</c> or <c>OvnSnatRule</c> object that names it.
///         docs/plan/14 spells it <c>CyberCloud.Network/publicIpAddresses</c>, with no network segment
///         in the path, and the substrate agrees. ⚠ <b>An unattached address is therefore inert</b>,
///         which is the safety answer for this type — see
///         <c>charts/managed/kube-ovn-eip/conformance.yaml</c>,
///         <c>an-unattached-address-carries-no-traffic</c>.
///     </para>
///     <para>
///         ⚠ <b>EVERY FIELD OF AN <c>OvnEip</c> IS IMMUTABLE ONCE IT IS READY, AND THAT IS A FACT
///         ABOUT THE CONTROLLER RATHER THAN A CONVENTION.</b> Read firsthand in
///         <c>pkg/controller/ovn_eip.go</c> at <c>v1.16.2</c>: <c>handleAddOvnEip</c> returns
///         immediately when <c>status.macAddress</c> is already set — <i>"already ok"</i> — and
///         <c>handleUpdateOvnEip</c> refuses four fields <b>by name</b>, one error each: <i>"not
///         support change v4 ip"</i>, <i>"not support change v6 ip"</i>, <i>"not support change mac
///         address"</i>, <i>"not support change type"</i>. So this is the first type in the tree with
///         <b>no mutable property at all</b>. What that costs, and why the shared conformance suite
///         cannot see it, is
///         <c>charts/managed/kube-ovn-eip/conformance.yaml § owed</c>,
///         <c>an-allocated-address-cannot-be-changed</c> — stated as a defect rather than dressed up.
///     </para>
///     <para>
///         ⚠ <b>THE RENDERER OMITS A KEY IT WOULD OTHERWISE SEND EMPTY, AND SENDING IT WOULD DEADLOCK
///         THE RECONCILE LOOP RATHER THAN MERELY BE UNTIDY.</b> <c>createOrUpdateOvnEipCR</c> writes
///         the address the fabric allocated back into <c>spec.v4Ip</c>, <c>spec.v6Ip</c> and
///         <c>spec.macAddress</c> through a full <c>OvnEips().Update(...)</c> — so those fields become
///         owned by the controller's field manager. A provider that applied <c>v4Ip: ""</c> would own
///         the same field at a <i>different</i> value, and every subsequent apply would answer
///         <c>ApplyResult.Conflict</c>: the resource would sit in <c>InProgress</c> forever, reporting
///         a field manager conflict on a resource that was allocated correctly the first time.
///         <see cref="OvnEipJson" /> therefore emits <c>v4Ip</c> and <c>v6Ip</c> <b>only when the body
///         asks for a specific address</b>. ⚠ It is <c>CyberCloud.Terminal/consoles</c>' finding —
///         <i>an empty value is not an absent one</i> — arriving on a scalar instead of a list, and
///         with a worse symptom.
///     </para>
///     <para>
///         ⚠ <b>AND <see cref="Matches" /> COMPARES AN ADDRESS ONLY WHEN THE BODY REQUESTED ONE, WHICH
///         IS THIS FAMILY'S CANONICAL BUG IN A NEW SHAPE.</b> <c>NetworkSubnets.Matches</c> compares
///         parsed networks because the controller <i>canonicalizes</i> what it was sent. Here the
///         controller <i>fills in what was not sent</i>: an address the tenant left to the fabric
///         arrives in <c>spec.v4Ip</c> one pass later. A comparison against the empty string would
///         report drift on an address that was allocated exactly as asked, forever.
///         <c>NetworkPublicIpTests</c> runs that mistake red.
///     </para>
///     <para>
///         ⚠ <b>NO <c>ipVersion</c> PROPERTY, AND ITS ABSENCE IS LOAD-BEARING RATHER THAN AN
///         OVERSIGHT — <c>NetworkSubnets</c>' <c>protocol</c> argument, exactly.</b> Which families an
///         EIP gets is decided by the <b>external subnet</b> it is allocated from: <c>acquireIPAddress</c>
///         hands back whatever that subnet carries, and <c>spec.v4Ip</c>/<c>spec.v6Ip</c> are requests
///         for a <i>particular</i> address rather than a choice of family. A tenant-facing
///         <c>ipVersion</c> would be a control the substrate ignores, which is the worst of the three
///         possible outcomes. ⚠ <b>It is also what keeps the meter honest</b>: because a tenant cannot
///         ask for an IPv6-only address, this type never has to draw <c>PublicIps</c> conditionally —
///         and a conditional draw is the exact thing <c>TryReserveAsync</c> refuses. An IPv6 address
///         rides along with the IPv4 one on a dual-stack pool and costs nothing, which is right:
///         <c>PublicIps</c> counts the scarce half.
///     </para>
///     <para>
///         ⚠ <b>NO <c>externalSubnet</c> PROPERTY EITHER, AND HERE THE FAMILY'S USUAL RULE INVERTS.</b>
///         <c>NetworkSubnets.SubnetJson</c> sends <c>spec.vpc</c> explicitly, because letting it
///         default binds the subnet to the platform's own VPC. This one deliberately does <b>not</b>
///         send <c>spec.externalSubnet</c>, because <c>handleAddOvnEip</c> falls back to
///         <c>c.config.ExternalGatewaySwitch</c> — the operator's <c>--external-gateway-switch</c>
///         flag — and that name is a property of the <i>deployment</i> which this repository cannot
///         know. A compiled-in guess would be a pool that does not exist in the first region whose
///         operator named theirs something else, and the symptom would be an EIP that never becomes
///         ready. <b>The difference from the subnet case is that there the default is wrong and here
///         the default is the only right answer available.</b> ⚠ The cost — a tenant cannot choose
///         between two pools — is <c>§ owed</c>, <c>the-external-pool-is-the-operators-default</c>.
///     </para>
///     <para>
///         ⚠ <b>NO <c>SupportsSoftDelete</c>, AND ON THIS TYPE THE OBVIOUS READING IS BACKWARDS.</b>
///         "Releasing a scarce address is exactly what a recovery window is for" is the tempting
///         sentence and it is wrong today. What <c>SupportsSoftDelete</c> does is park the name in the
///         index <b>and withhold the committed quota until a purge</b> — <c>OperationGrain</c> returns
///         <c>CommittedQuota</c> only for a delete that is not soft — while <c>RestoreAsync</c> and
///         <c>PurgeAsync</c> have <b>no HTTP route</b>. A window here would hold a tenant's
///         <c>PublicIps</c> allowance, 20 by default, against addresses they deleted, for the whole
///         window, with no way to recover one and no way to release one early. That is a one-way leak
///         on the platform's scarcest meter. <b>The window becomes right the day a purge route
///         exists</b>, and not before.
///     </para>
/// </remarks>
public static class PublicIpAddresses {
    /// <summary>The provider namespace — the family's.</summary>
    public const string ProviderNamespace = VirtualNetworks.ProviderNamespace;

    /// <summary>
    ///     The type path. ⚠ <b>Top level, with no <c>virtualNetworks/</c> in front of it.</b>
    /// </summary>
    /// <remarks>
    ///     docs/plan/14 § Load balancing spells it <c>CyberCloud.Network/publicIpAddresses</c> and the
    ///     substrate agrees: an <c>OvnEip</c> names no VPC. See this class's remarks.
    /// </remarks>
    public const string TypePath = "publicIpAddresses";

    /// <summary>The one api-version. ⚠ Equal to the rest of the family's.</summary>
    public const string V2026 = VirtualNetworks.V2026;

    /// <summary>The chart this type is the configuration surface of.</summary>
    public const string ChartName = "managed/kube-ovn-eip";

    /// <summary>The pointer <c>RequiresCluster</c> names.</summary>
    public const string ClusterIdPointer = ClusterPlacement.DefaultPointer;

    /// <summary>The type, namespace and path together.</summary>
    public static ResourceTypeName Type { get; } = new(ProviderNamespace, TypePath);

    /// <summary>
    ///     The <c>spec.type</c> every address this platform allocates carries.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Sent explicitly even though the controller supplies it, on the rule
    ///     <c>NetworkSecurityGroups</c> established</b>: <c>handleAddOvnEip</c> reads an empty
    ///     <c>spec.type</c> as <c>nat</c>, but "the substrate's zero value happens to be safe" is a
    ///     fact about a version of Go source rather than a property of this resource — and
    ///     <see cref="Matches" /> can only compare a field that was sent.
    ///     <para>
    ///         ⚠ <b>Its two siblings are the reason it is a constant rather than a property.</b>
    ///         <c>lsp</c> makes the controller create a bare logical switch port <i>on a node</i>, and
    ///         <c>lrp</c> a logical router port for a VPC's external gateway. Both are an operator's
    ///         layering decision in the same shape as <c>SecurityGroupSpec.Tier</c>, and a tenant body
    ///         that could elect one would be reaching into the platform's own fabric.
    ///     </para>
    /// </remarks>
    public const string UsageTypeNat = "nat";

    /// <summary>The action that reports the address the fabric actually allocated.</summary>
    /// <remarks>
    ///     ⚠ <b>THE ONE THING A TENANT ASKS THIS RESOURCE AND CANNOT OTHERWISE LEARN.</b> A public
    ///     address is the only resource in the catalogue whose <i>whole value</i> is a fact the tenant
    ///     did not supply: they ask for an address and the fabric picks one. It is not in the body,
    ///     because the body is what was asked for, and it is not derivable from anything — it lives on
    ///     <c>OvnEip.status.v4Ip</c> and nowhere else. ⚠ Without this action the only way to read it is
    ///     a Kubernetes object the tenant has no access to, which is
    ///     <c>NetworkSubnets.AddressUsageAction</c>'s argument arriving on a sharper case.
    /// </remarks>
    public const string AllocationAction = "showAllocation";

    /// <summary>The permission <see cref="AllocationAction" /> checks.</summary>
    /// <remarks>
    ///     ⚠ <c>read</c>. A public address is public by construction — it is announced to the internet
    ///     — so it is neither a credential nor a capability, and a permission of its own would be a
    ///     role nobody grants. The contrast is <c>StorageAccounts.ListKeysAction</c>, where what leaves
    ///     is a key.
    /// </remarks>
    public const string AllocationPermission = "read";

    // ── The object an address IS ──────────────────────────────────────────────────────────────

    /// <summary>
    ///     The Kube-OVN <c>OvnEip</c> custom resource.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The plural is <c>ovn-eips</c>, HYPHENATED</b>, read firsthand from
    ///     <c>pkg/apis/kubeovn/v1/ovn-eip.go</c> —
    ///     <c>+kubebuilder:resource:scope="Cluster",shortName="oeip",path="ovn-eips",singular="ovn-eip"</c>
    ///     — and confirmed in the CRD the chart installs. It is the same hyphenation
    ///     <c>NetworkSecurityGroups</c> found, and the same trap: <c>ClusterConformanceHarness</c>
    ///     derives its CRD stub's path from <see cref="GroupVersionKind.Plural" />, so
    ///     <c>ovneips</c> would install a definition at a path the apply never reaches and the symptom
    ///     is a discovery error naming a missing operator rather than a wrong plural.
    ///     <para>
    ///         ⚠ <b>Cluster-scoped, like everything else in this family.</b> <c>OvnEip</c> is
    ///         <c>scope="Cluster"</c>, so <see cref="ObjectRef.Namespace" /> is empty and the
    ///         separation a namespace would have given is inside <see cref="ObjectNameOf" />.
    ///     </para>
    /// </remarks>
    public static GroupVersionKind OvnEipKind { get; } =
        new() { Group = "kubeovn.io", Version = "v1", Kind = "OvnEip", Plural = "ovn-eips" };

    /// <summary>
    ///     The name of the <c>OvnEip</c> an address renders: its namespace and its own name, joined.
    /// </summary>
    /// <param name="ns">The resource's namespace, used as a name component.</param>
    /// <param name="name">The address resource's name.</param>
    /// <remarks>
    ///     ⚠ <b>Two components rather than the three a subnet needs, because this type has no
    ///     parent</b> — and the namespace is still mandatory for <see cref="VirtualNetworks.ObjectNameOf" />'s
    ///     reason: an <c>OvnEip</c> is cluster-scoped, so two subscriptions each creating an address
    ///     called <c>web</c> would render one object, each converging by overwriting the other, with
    ///     nothing reporting an error. ⚠ <b>On this type the collision costs more than on any other in
    ///     the family</b>, because the object is an <i>allocation</i>: two tenants would be handed the
    ///     same address, and the second tenant's traffic would arrive at the first tenant's NAT rule.
    /// </remarks>
    public static string ObjectNameOf(string ns, string name) => ns + "-" + name;

    /// <summary>The <c>OvnEip</c> an address owns.</summary>
    /// <param name="ns">The resource's namespace, used as a name component.</param>
    /// <param name="name">The address resource's name.</param>
    /// <remarks>⚠ Cluster-scoped: <c>Namespace</c> is empty. See <see cref="VirtualNetworks.VpcRef" />.</remarks>
    public static ObjectRef OvnEipRef(string ns, string name) =>
        new() { Kind = OvnEipKind, Namespace = string.Empty, Name = ObjectNameOf(ns, name) };

    // ── The body shape ────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     The body shape at <see cref="V2026" />.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>TWO ADDRESS SLOTS RATHER THAN ONE, WHICH IS <c>addressSpace</c>'S SHAPE AND IS
    ///         CHOSEN FOR THE SAME REASON.</b> Each is individually patterned, individually reportable
    ///         through its own JSON Pointer when it is wrong, and models docs/plan/14 § IPv6's
    ///         <i>"a v4 prefix, a v6 prefix, or both"</i> exactly. A single field would have to guess
    ///         the family from the text, and would put the ordering rule in a description where nothing
    ///         enforces it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>BOTH ARE OPTIONAL AND THE EMPTY BODY IS THE ORDINARY ONE.</b> An address the fabric
    ///         picks is what almost every tenant wants; naming one is for reclaiming an address you
    ///         held before, which <c>acquireStaticIPAddress</c> supports and which fails loudly at the
    ///         fabric when the address is already taken. ⚠ The empty string is what <b>does not</b>
    ///         reach the object — see <see cref="OvnEipJson" />.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>THERE IS NO REQUIRED PROPERTY IN <c>/properties</c> BEYOND THE CLUSTER, WHICH IS
    ///         WHY <c>InvalidBody</c> FOR THIS TYPE IS A MALFORMED ADDRESS RATHER THAN A MISSING
    ///         FIELD.</b> Its two siblings drop a required property because that is all their schemas
    ///         can refuse beyond a shape; this one refuses <c>10.0.0</c> at
    ///         <c>/properties/address/v4</c> by <see cref="IpAddresses.OptionalV4Pattern" />, with a
    ///         <c>400</c> and that pointer, before the write path answers.
    ///     </para>
    /// </remarks>
    public static ResourceSchema Schema2026 { get; } =
        ResourceSchema.Of(
            [
                new(
                    "/location",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The region the address is allocated in. ⚠ It must be a region whose "
                    + "operator has an external pool — nothing checks that, and an address in a region "
                    + "with none never becomes ready."
                ) {
                    Format = SchemaFormat.Region,
                    Widget = WidgetHint.Region,
                    Immutable = true,
                    ExampleJson = "\"eu-central\""
                },
                new("/properties", SchemaKind.Nested, Description: "The address's own settings."),
                new(
                    ClusterIdPointer,
                    SchemaKind.Text,
                    Required: true,
                    Description: "The cluster whose external pool the address is allocated from. ⚠ An "
                    + "address is only reachable from the fabric that announces it, so a load balancer "
                    + "in another cluster cannot use it."
                ) {
                    Format = SchemaFormat.Uuid,
                    Widget = WidgetHint.Cluster,
                    Immutable = true
                },
                new(
                    "/properties/address",
                    SchemaKind.Nested,
                    Description: "A particular address to ask for, rather than whichever one is free."
                ),
                new(
                    "/properties/address/v4",
                    SchemaKind.Text,
                    Description: "The IPv4 address to reclaim, or empty to be given whichever one is "
                    + "free. ⚠ A bare address and not a prefix — 10.100.0.7, never 10.100.0.7/32. An "
                    + "address that is already taken is refused by the fabric rather than by the API."
                ) {
                    Pattern = IpAddresses.OptionalV4Pattern,
                    Immutable = true,
                    DefaultJson = "\"\"",
                    ExampleJson = "\"10.100.0.7\""
                },
                new(
                    "/properties/address/v6",
                    SchemaKind.Text,
                    Description: "The IPv6 address to reclaim, or empty to be given whichever one is "
                    + "free. ⚠ Lower case only — the fabric refuses an address with an upper-case "
                    + "letter in it. Whether an address has a v6 half at all is decided by the pool it "
                    + "comes from, not here."
                ) {
                    Pattern = IpAddresses.OptionalV6Pattern,
                    Immutable = true,
                    DefaultJson = "\"\"",
                    ExampleJson = "\"fd00:ff::7\""
                }
            ]
        );

    /// <summary>The pointers <see cref="Schema2026" /> declares, in declaration order.</summary>
    public static ImmutableArray<string> Pointers2026 { get; } =
        [.. Schema2026.Properties.Select(x => x.JsonPointer)];

    /// <summary>
    ///     What a <c>POST …/showAllocation</c> returns.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b><c>ready</c> IS THE FIELD THAT STOPS THE OTHERS FROM BEING READ AS PROMISES.</b> An
    ///     <c>OvnEip</c> carries an address in <c>status.v4Ip</c> from the moment the controller
    ///     allocates it, and <c>status.ready</c> only once the fabric has finished with it. Reporting
    ///     the address without the flag would tell a tenant to point DNS at an address that is not yet
    ///     announced.
    ///     <para>
    ///         ⚠ <c>attachedTo</c> is <c>status.nat</c>, which is the name of the NAT rule using this
    ///         address, or empty. It is reported because <b>an address with nothing attached carries no
    ///         traffic</b>, and "I allocated an address and nothing happens" is the question this type
    ///         will be asked most often in M1 — where nothing can attach one yet.
    ///     </para>
    /// </remarks>
    public static ResourceSchema AllocationResponse { get; } =
        ResourceSchema.Of(
            [
                new(
                    "/v4",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The IPv4 address the fabric allocated, or empty when it has not "
                    + "allocated one yet."
                ),
                new(
                    "/v6",
                    SchemaKind.Text,
                    Description: "The IPv6 address the fabric allocated, or empty for an IPv4-only "
                    + "pool."
                ),
                new(
                    "/macAddress",
                    SchemaKind.Text,
                    Description: "The MAC address the fabric bound to it. ⚠ Reported because it is what "
                    + "an operator needs to find this address in an ARP table when it is unreachable."
                ),
                new(
                    "/ready",
                    SchemaKind.Boolean,
                    Required: true,
                    Description: "Whether the fabric has finished announcing the address. ⚠ False with "
                    + "an address already reported is the ordinary state for a few seconds after "
                    + "create."
                ),
                new(
                    "/attachedTo",
                    SchemaKind.Text,
                    Description: "The NAT rule currently using this address, or empty. ⚠ Empty means "
                    + "the address is allocated and carries no traffic, which in this api-version is "
                    + "every address — nothing can attach one yet."
                ),
                new(
                    "/sampledAt",
                    SchemaKind.Text,
                    Required: true,
                    Description: "When the platform read the object, RFC 3339. ⚠ The read time rather "
                    + "than the time the fabric wrote the figures, because the object carries no "
                    + "timestamp on them."
                ) {
                    Format = SchemaFormat.DateTime
                }
            ]
        );

    // ── The desired body, read ────────────────────────────────────────────────────────────────

    /// <summary>The IPv4 address a body asks for, or empty.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string RequestedV4(JsonElement desired) => Text(desired, "address", "v4");

    /// <summary>The IPv6 address a body asks for, or empty.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string RequestedV6(JsonElement desired) => Text(desired, "address", "v6");

    /// <summary>The region a body declares.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string Location(JsonElement desired) => VirtualNetworks.Location(desired);

    /// <summary>
    ///     What is wrong with a body's requested addresses, or <see langword="null" /> when nothing is.
    /// </summary>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     ⚠ <b>Much smaller than <c>NetworkSubnets.AddressProblem</c>, and the difference is the
    ///     point.</b> A subnet's prefix is checked against a whole reserved table after the <c>202</c>,
    ///     because the rule compares one value against a list selected by another property and
    ///     <c>ResourceSchema</c> cannot express that. Here the pattern reaches almost everything and
    ///     what is left is two facts about one string — is it the right family, and is it lower case —
    ///     which is the narrowest this family's recurring defect has ever been.
    ///     <para>
    ///         ⚠ <b><see cref="NetworkAddressing.ReservedRanges" /> is deliberately NOT consulted.</b>
    ///         Every row there is a range a <i>tenant network</i> may not overlap; a public address
    ///         comes out of the operator's own external pool, which is underlay space by construction
    ///         and would fail most of those rows for being exactly what it is.
    ///     </para>
    /// </remarks>
    public static string? AddressProblem(JsonElement desired) =>
        IpAddresses.ProblemWith(RequestedV4(desired), false, "/properties/address/v4")
        ?? IpAddresses.ProblemWith(RequestedV6(desired), true, "/properties/address/v6");

    // ── The object a desired body becomes ─────────────────────────────────────────────────────

    /// <summary>The <c>OvnEip</c> document a desired body becomes.</summary>
    /// <param name="ns">The resource's namespace, used as a name component.</param>
    /// <param name="name">The address resource's name.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A REQUESTED ADDRESS IS EMITTED AND AN UNREQUESTED ONE IS ABSENT, NOT EMPTY, AND
    ///         THE DIFFERENCE IS A DEADLOCK.</b> <c>createOrUpdateOvnEipCR</c> writes the allocated
    ///         address into <c>spec.v4Ip</c> with a full <c>Update()</c>, taking field-manager
    ///         ownership of it. An apply that carried <c>v4Ip: ""</c> would claim the same field at a
    ///         different value and every later apply would answer <c>ApplyResult.Conflict</c> — the
    ///         resource would sit in <c>InProgress</c> forever on an address that was allocated
    ///         correctly the first time. Omitting the key leaves the field to the controller entirely.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>NO <c>externalSubnet</c>, NO <c>macAddress</c>.</b> The pool is the operator's
    ///         <c>--external-gateway-switch</c> and this repository cannot know its name — see this
    ///         class's remarks. The MAC is the fabric's to choose and is written back to the spec, so
    ///         sending one would be a value overwritten one pass later while the resource reported it
    ///         as desired.
    ///     </para>
    ///     <para>
    ///         ⚠ No labels, no annotations and no namespace here — ADR-013's seven labels and two
    ///         annotations are injected by <c>KubeCommand</c> non-overridably, and this object is
    ///         cluster-scoped so there is no namespace to write.
    ///     </para>
    /// </remarks>
    public static string OvnEipJson(string ns, string name, JsonElement desired) {
        var spec = new JsonObject { ["type"] = UsageTypeNat };

        if (RequestedV4(desired) is { Length: > 0 } v4) {
            spec["v4Ip"] = v4;
        }

        if (RequestedV6(desired) is { Length: > 0 } v6) {
            spec["v6Ip"] = v6;
        }

        return new JsonObject {
            ["kind"] = OvnEipKind.Kind,
            ["metadata"] = new JsonObject { ["name"] = ObjectNameOf(ns, name) },
            ["spec"] = spec
        }.ToJsonString();
    }

    /// <summary>
    ///     Whether an <c>OvnEip</c> read back from a cluster carries what the desired body asks for.
    /// </summary>
    /// <param name="objectJson">The object's JSON, exactly as the API server returned it.</param>
    /// <param name="desired">The desired body.</param>
    /// <remarks>
    ///     ⚠ <b>AN ADDRESS IS COMPARED ONLY WHEN ONE WAS ASKED FOR, AND THE OTHER READING IS THE BUG
    ///     THIS FAMILY KEEPS FINDING.</b> The controller fills <c>spec.v4Ip</c> with whatever it
    ///     allocated, so a body that asked for no particular address will always disagree with the
    ///     object on that field — and a comparison that insisted would report drift on an address that
    ///     was allocated exactly as requested, forever, with the resource never leaving
    ///     <c>InProgress</c>. It is <c>NetworkSubnets.Matches</c>' canonicalisation trap in its second
    ///     shape: there the controller <i>rewrote</i> what it was sent, here it <i>fills in what it was
    ///     not</i>.
    ///     <para>
    ///         ⚠ Containment rather than equality, as everywhere in this family, and here the
    ///         controller's write-back is emphatic: <c>createOrUpdateOvnEipCR</c> issues a full
    ///         <c>OvnEips().Update(...)</c> that sets <c>spec.macAddress</c>, <c>spec.v4Ip</c>,
    ///         <c>spec.v6Ip</c> and <c>spec.type</c>, and <c>handleAddOvnEip</c> fills
    ///         <c>spec.externalSubnet</c> from the operator's flag. An equality comparison would never
    ///         converge on any of them.
    ///     </para>
    /// </remarks>
    public static bool Matches(string objectJson, JsonElement desired) {
        if (Spec(objectJson) is not { } spec) {
            return false;
        }

        if (spec["type"]?.GetValue<string>() != UsageTypeNat) {
            return false;
        }

        return Carries(spec, "v4Ip", RequestedV4(desired))
            && Carries(spec, "v6Ip", RequestedV6(desired));
    }

    /// <summary>Whether the object carries a requested address, ignoring one that was not requested.</summary>
    /// <param name="spec">The object's spec.</param>
    /// <param name="field">The spec field.</param>
    /// <param name="requested">What the body asked for, or empty.</param>
    static bool Carries(JsonObject spec, string field, string requested) =>
        requested.Length == 0 || spec[field]?.GetValue<string>() == requested;

    /// <summary>The <c>spec</c> of an <c>OvnEip</c> document, or <see langword="null" />.</summary>
    /// <param name="objectJson">The object's JSON.</param>
    static JsonObject? Spec(string objectJson) {
        JsonNode? parsed;
        try {
            parsed = JsonNode.Parse(objectJson);
        } catch (JsonException) {
            return null;
        }

        return parsed is JsonObject document
            && document["kind"]?.GetValue<string>() is (null or "OvnEip")
            && document["spec"] is JsonObject spec
                ? spec
                : null;
    }

    // ── A body, for tests, fixtures and the conformance case ──────────────────────────────────

    /// <summary>Builds a body that satisfies <see cref="Schema2026" />.</summary>
    /// <param name="clusterId">The cluster whose external pool the address comes from.</param>
    /// <param name="addressV4">A particular IPv4 address to ask for, or empty.</param>
    /// <param name="addressV6">A particular IPv6 address to ask for, or empty.</param>
    /// <param name="location">The region.</param>
    /// <remarks>
    ///     ⚠ The default asks for <b>no particular address</b>, because that is the ordinary request
    ///     and because it is the one whose rendered object exercises
    ///     <see cref="OvnEipJson" />'s omission — the case that deadlocks if the key is emitted empty.
    /// </remarks>
    public static string Body(
        Guid clusterId,
        string addressV4 = "",
        string addressV6 = "",
        string location = "eu-central"
    ) =>
        new JsonObject {
            ["location"] = location,
            ["properties"] = new JsonObject {
                ["clusterId"] = clusterId.ToString("D", CultureInfo.InvariantCulture),
                ["address"] = new JsonObject { ["v4"] = addressV4, ["v6"] = addressV6 }
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
