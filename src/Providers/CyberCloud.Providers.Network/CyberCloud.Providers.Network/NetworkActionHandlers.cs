// ⚠ For `Result<string>`. `CyberCloud.Core.Resources` is global in this assembly and
// `CyberCloud.Core` itself is not; the `ErrorCode` alias in GlobalUsings still wins over the
// `Orleans.ErrorCode` this import would otherwise put back in play — the same note StorageProvider
// carries.
using CyberCloud.Core;
using CyberCloud.Core.Time;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Network;

/// <summary>
///     Serves <c>POST …/virtualNetworks/{name}/showIsolation</c>: what this network's tenant
///     separation does and does not guarantee.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THIS ACTION WAS DECLARED WITH NO HANDLER AND THEREFORE ANSWERED A <c>500</c>, AND
///         THAT IS WORSE HERE THAN IT WOULD BE ANYWHERE ELSE IN THE CATALOGUE.</b> When this family
///         shipped, no provider in the tree had a handler and there was nowhere to put one — the
///         declaration reached the OpenAPI document, the SDK and the CLI, and nothing could run it.
///         The seam exists now (<c>IResourceActionHandler</c>, <c>ActionDispatcher</c>), and a
///         synchronous action with no handler is <b>refused by name</b>:
///         <i>"declares the action '…' and no handler for it, so it cannot be run"</i>, as an
///         <c>InternalError</c>. So the one action in the catalogue whose <i>content</i> was ready
///         was the one publishing a <c>500</c> — and the content in question is the platform's
///         statement of what it does <b>not</b> protect a tenant from.
///     </para>
///     <para>
///         ⚠ <b>It reaches nothing.</b> The answer is a pure function of
///         <see cref="VirtualNetworks.IsolationClaim" /> and
///         <see cref="VirtualNetworks.IsolationLimits" />, both compile-time constants — no cluster
///         read, no secret, no usage pipeline. It is the same sentence for every virtual network on
///         the platform, which is exactly the property that makes it safe to hand out under
///         <c>read</c>.
///     </para>
///     <para>
///         ⚠ <b>The limits are flattened to sentences on the way out</b>, because
///         <c>SchemaProperty.ElementKind</c> refuses an array of objects on a response schema. The
///         four columns are joined in a fixed order — what is not claimed, why, what to ask for
///         instead — so a client that wants them back can split, and a human reading the CLI output
///         gets a paragraph rather than a table with no headers.
///         <c>charts/managed/kube-ovn-vpc/conformance.yaml § owed</c>,
///         <c>an-array-of-objects-is-not-expressible</c>.
///     </para>
/// </remarks>
public sealed class ShowIsolationHandler : IResourceActionHandler {
    /// <summary>
    ///     The substrate named in every response.
    /// </summary>
    /// <remarks>
    ///     ⚠ Named because docs/plan/14 requires that <i>"the marketing must not claim more than the
    ///     substrate delivers"</i>, and a tenant's own security review cannot assess a claim whose
    ///     enforcement mechanism is unnamed. The parenthetical is the part that matters: the
    ///     separation is enforced in a userspace-and-kernel datapath on shared nodes.
    /// </remarks>
    public const string Substrate = "Kube-OVN (Open vSwitch)";

    /// <inheritdoc />
    public ResourceTypeName Type => VirtualNetworks.Type;

    /// <inheritdoc />
    public string Action => VirtualNetworks.ShowIsolationAction;

    /// <inheritdoc />
    public Task<Result<string>> InvokeAsync(
        ActionContext context,
        CancellationToken cancellationToken = default
    ) {
        var limits = new JsonArray();

        foreach (var limit in VirtualNetworks.IsolationLimits) {
            limits.Add(
                $"Not claimed: {limit.NotClaimed} Why: {limit.Because} Instead: {limit.Instead}"
            );
        }

        return Task.FromResult(
            Result<string>.Success(
                new JsonObject {
                    ["claim"] = VirtualNetworks.IsolationClaim,
                    ["limits"] = limits,
                    ["substrate"] = Substrate
                }.ToJsonString()
            )
        );
    }
}

/// <summary>
///     Serves <c>POST …/subnets/{name}/listAddressUsage</c>: how much of a subnet's range is gone.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THE FIGURES COME OFF THE OBJECT'S <c>status</c>, WHICH IS THE ONLY PLACE THEY
///         EXIST.</b> Kube-OVN's <c>SubnetStatus</c> carries <c>v4availableIPs</c>,
///         <c>v4usingIPs</c> and their v6 counterparts, maintained by the controller as ports come
///         and go. This handler reads the <c>Subnet</c> and reports them; it does not count anything
///         itself, because a second opinion about how many addresses are free is a second opinion
///         that will disagree with the allocator.
///     </para>
///     <para>
///         ⚠ <b><c>total</c> IS <c>using + available</c> AND NOT AN ARITHMETIC FUNCTION OF THE
///         PREFIX, AND THAT IS THE DECISION IN THIS FILE.</b> A <c>/24</c> holds 256 addresses, of
///         which the network address, the broadcast address and the gateway are not allocatable — and
///         so is every entry the controller appended to <c>excludeIps</c>, which it does silently and
///         which this platform never sees. Computing <c>2^(32-prefix) - 3</c> would produce a number
///         that is right for a fresh subnet, drifts as the fabric excludes more, and is confidently
///         wrong in exactly the situation the tenant is asking about. Summing the two figures the
///         allocator itself publishes cannot drift from the allocator.
///     </para>
///     <para>
///         ⚠ <b>THE v6 FIGURES ARE READ AS RAW JSON TEXT AND NEVER THROUGH AN INTEGER, WHICH IS A
///         CORRECTNESS REQUIREMENT RATHER THAN A STYLE.</b> Kube-OVN types these counts as
///         <c>internal.BigInt</c>, which marshals as an unbounded JSON number: a <c>/64</c> reports
///         <c>18446744073709551616</c>, which does not fit in <c>Int64</c>, and a <c>/63</c> is twice
///         that. <c>NetworkSubnets.AddressUsageResponse</c> declares the v6 figures as
///         <see cref="SchemaKind.Text" /> for that reason, and this handler carries the literal
///         across rather than parsing it. The v4 figures are bounded by 2^32 and go through
///         <c>Int64</c> as the schema's <see cref="SchemaKind.WholeNumber" /> requires.
///     </para>
///     <para>
///         ⚠ <b><c>sampledAt</c> IS WHEN THE PLATFORM READ THE OBJECT, NOT WHEN THE FABRIC WROTE
///         IT.</b> <c>SubnetStatus</c> carries no timestamp on the counts — only
///         <c>status.conditions[]</c> are stamped, and those are about readiness rather than about
///         these numbers. So the value here is the read time, which is an upper bound on the age of
///         the figures rather than their age. The field exists at all because a count with no
///         timestamp is a count a caller reads as live; it is honest about being a read time in
///         <c>charts/managed/kube-ovn-subnet/conformance.yaml § owed</c>,
///         <c>address-usage-has-no-substrate-timestamp</c>.
///     </para>
///     <para>
///         ⚠ <b>A subnet that has never been reconciled reports zeros rather than refusing.</b> A
///         <c>Subnet</c> whose controller has not run yet has no <c>status</c> at all, and "this
///         subnet has no addresses left" would be a dangerously wrong reading of that. What is
///         returned is <c>total: 0, used: 0, available: 0</c>, which is the arithmetic identity of
///         "nothing is known yet" and is distinguishable from a full subnet by the total being zero.
///     </para>
/// </remarks>
/// <param name="clock">Stamps <c>sampledAt</c>. ⚠ The handler's only field, and it is not mutable.</param>
public sealed class ListAddressUsageHandler(IClock clock) : IResourceActionHandler {
    /// <inheritdoc />
    public ResourceTypeName Type => NetworkSubnets.Type;

    /// <inheritdoc />
    public string Action => NetworkSubnets.AddressUsageAction;

    /// <inheritdoc />
    public async Task<Result<string>> InvokeAsync(
        ActionContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            // ⚠ Unreachable in production — the type declares RequiresCluster and ActionDispatcher
            // refuses before a handler is reached. It is here because "unreachable" is a claim about
            // a call site rather than about this method, and a null dereference would be the symptom.
            return Result<string>.Failure(
                ErrorCode.InternalError,
                $"'{context.Id.Path}' has no cluster connection, and a subnet's address usage is read "
                + "from the Subnet object in a cluster."
            );
        }

        var target = NetworkSubnets.SubnetRef(context.Namespace, context.Id);
        var read = await cluster.GetAsync(target, cancellationToken);

        if (read.TryGetError(out var error)) {
            return Result<string>.Failure(error);
        }

        var status = StatusOf(read.GetValueOrThrow().Json);

        var v4Used = Whole(status, "v4usingIPs");
        var v4Available = Whole(status, "v4availableIPs");

        var v6Used = Literal(status, "v6usingIPs");
        var v6Available = Literal(status, "v6availableIPs");

        return Result<string>.Success(
            new JsonObject {
                ["v4"] = new JsonObject {
                    ["total"] = v4Used + v4Available,
                    ["used"] = v4Used,
                    ["available"] = v4Available
                },
                ["v6"] = new JsonObject {
                    // ⚠ The v6 total is the SUM OF TWO ARBITRARY-PRECISION DECIMALS, which is why it
                    // is not computed. There is no BigInteger in this assembly's reference set and
                    // adding one to produce a number nobody can act on would be the wrong trade — a
                    // subnet with 2^64 addresses is not one a tenant is about to exhaust. What the
                    // schema promises for /v6/total is "how many the prefix contains", and the honest
                    // answer this handler has is the available count; the total is reported only when
                    // nothing is used, where the two are equal.
                    ["total"] = v6Used == "0" ? v6Available : string.Empty,
                    ["available"] = v6Available
                },
                ["sampledAt"] = clock.UtcNow.ToString("O", CultureInfo.InvariantCulture)
            }.ToJsonString()
        );
    }

    /// <summary>The <c>status</c> of a <c>Subnet</c> document, or <see langword="null" />.</summary>
    /// <param name="objectJson">The object's JSON.</param>
    static JsonObject? StatusOf(string objectJson) {
        JsonNode? parsed;
        try {
            parsed = JsonNode.Parse(objectJson);
        } catch (JsonException) {
            return null;
        }

        return parsed is JsonObject document && document["status"] is JsonObject status ? status : null;
    }

    /// <summary>One bounded count, or zero when the controller has not written one.</summary>
    /// <param name="status">The subnet's status.</param>
    /// <param name="name">The status field.</param>
    static long Whole(JsonObject? status, string name) =>
        status?[name] is JsonValue value && value.TryGetValue<long>(out var number) ? number : 0;

    /// <summary>
    ///     One count as it is written on the object, for a figure that may not fit in
    ///     <see cref="long" />.
    /// </summary>
    /// <param name="status">The subnet's status.</param>
    /// <param name="name">The status field.</param>
    /// <remarks>
    ///     ⚠ <c>ToJsonString()</c> on the value, so <c>18446744073709551616</c> survives. Anything
    ///     that is not a number is reported as the empty string, which the response schema declares as
    ///     "or empty for an IPv4-only subnet".
    /// </remarks>
    static string Literal(JsonObject? status, string name) =>
        status?[name] is JsonValue value && value.GetValueKind() is JsonValueKind.Number
            ? value.ToJsonString()
            : string.Empty;
}

/// <summary>
///     Serves <c>POST …/securityGroups/{name}/showEffectiveRules</c>: the rules a compact body
///     actually becomes.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>IT EXISTS BECAUSE OF THE RESHAPE, AND WITHOUT IT THE RESHAPE WOULD BE A
///         DOCUMENTATION PROMISE.</b> <c>NetworkSecurityGroups</c> expresses a rule list as six
///         scalars per direction, because <c>SchemaProperty.ElementKind</c> refuses an array of
///         objects. The cost is that the mapping from those scalars to Kube-OVN's rules — a cross
///         product of remotes against port entries — is arithmetic a tenant has to do in their head
///         to answer "what did I just open". This returns the answer, in the order the fabric gets it.
///     </para>
///     <para>
///         ⚠ <b>IT REPORTS WHAT THE PLATFORM ASKS FOR, NOT WHAT OVN HOLDS, AND EVERY RESPONSE SAYS
///         SO.</b> The rules are derived from <c>ActionContext.Desired</c> — the resource's stored
///         body — so a group whose reconcile has not converged reports the rules it is converging
///         towards. The alternative, reading the <c>SecurityGroup</c> back, would report the same
///         thing one round trip later for a converged group and would report <i>nothing</i> for a
///         group that is still being applied, which is the moment somebody is most likely to ask.
///         <c>/note</c> carries the distinction on every response rather than leaving it to a reader
///         of the schema.
///     </para>
///     <para>
///         ⚠ <b><c>defaultAction</c> IS A CONSTANT AND IT IS THE MOST IMPORTANT FIELD IN THE
///         RESPONSE.</b> A list of allow rules with no statement of what happens to everything else is
///         ambiguous in the direction that gets people hurt. It is <c>drop</c> because Kube-OVN's
///         <c>CreateSgDenyAllACL</c> installs <c>outport == @{pg} &amp;&amp; ip</c> and
///         <c>inport == @{pg} &amp;&amp; ip</c> at <c>SecurityGroupDropPriority</c> beneath every
///         rule this type writes, and no property on this resource can change that.
///     </para>
/// </remarks>
public sealed class ShowEffectiveRulesHandler : IResourceActionHandler {
    /// <summary>What happens to traffic no rule matches.</summary>
    public const string DefaultAction = "drop";

    /// <summary>What the answer is, and is not, on every response.</summary>
    public const string Note =
        "These are the rules the platform writes to the fabric for this security group's current "
        + "body. They are not a reading of the ACLs the fabric currently holds, so a group whose last "
        + "write has not converged reports the rules it is converging towards. Anything not listed is "
        + "dropped.";

    /// <inheritdoc />
    public ResourceTypeName Type => NetworkSecurityGroups.Type;

    /// <inheritdoc />
    public string Action => NetworkSecurityGroups.EffectiveRulesAction;

    /// <inheritdoc />
    public Task<Result<string>> InvokeAsync(
        ActionContext context,
        CancellationToken cancellationToken = default
    ) {
        var rules = NetworkSecurityGroups.AllRules(context.Desired);
        var described = new JsonArray();

        foreach (var rule in rules) {
            described.Add(rule.Describe());
        }

        return Task.FromResult(
            Result<string>.Success(
                new JsonObject {
                    ["rules"] = described,
                    ["count"] = rules.Length,
                    ["defaultAction"] = DefaultAction,
                    ["allowSameGroupTraffic"] =
                        NetworkSecurityGroups.AllowSameGroupTraffic(context.Desired),
                    ["note"] = Note
                }.ToJsonString()
            )
        );
    }
}

/// <summary>
///     Serves <c>POST …/publicIpAddresses/{name}/showAllocation</c>: the address the fabric actually
///     handed out.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THIS IS THE ONLY ACTION IN THE CATALOGUE THAT RETURNS THE RESOURCE'S OWN REASON FOR
///         EXISTING.</b> Every other one reports a refinement — how full a subnet is, what a rule set
///         expands to, what the isolation claim does not cover. A public address <i>is</i> the value
///         the fabric picked: it is not in the body, because the body is what was asked for, and it is
///         derivable from nothing. Without this action a tenant would have to read
///         <c>OvnEip.status.v4Ip</c>, on an object they have no access to, in a cluster they cannot
///         reach.
///     </para>
///     <para>
///         ⚠ <b><c>ready</c> IS RETURNED WITH THE ADDRESS AND NOT INSTEAD OF IT.</b> The controller
///         writes <c>status.v4Ip</c> as soon as IPAM allocates and <c>status.ready</c> only after the
///         fabric has finished — <c>patchOvnEipStatus(key, true)</c> is a separate call in
///         <c>handleAddOvnEip</c>. An address without the flag is a value a tenant would point DNS at
///         a few seconds too early; the flag without the address is a spinner. Both, always.
///     </para>
///     <para>
///         ⚠ <b><c>attachedTo</c> IS <c>status.nat</c> AND IT IS EMPTY FOR EVERY ADDRESS IN THIS
///         API-VERSION.</b> It names the NAT rule using the address — an <c>OvnFip</c>,
///         <c>OvnDnatRule</c> or <c>OvnSnatRule</c> — and nothing in this platform creates one yet, so
///         "I allocated an address and nothing happens" is the question this type will be asked most
///         often. Returning the field empty answers it honestly rather than leaving the tenant to
///         infer it. It is also what the delete path waits on: the fabric will not release an address
///         a rule still names.
///     </para>
///     <para>
///         ⚠ <b><c>sampledAt</c> IS WHEN THE PLATFORM READ THE OBJECT, NOT WHEN THE FABRIC WROTE
///         IT</b>, for <see cref="ListAddressUsageHandler" />'s reason: <c>OvnEipStatus</c> carries no
///         timestamp on the allocation, only <c>conditions[]</c>, and those are about readiness rather
///         than about these values.
///     </para>
///     <para>
///         ⚠ <b>An address whose controller has not run yet reports empties and <c>ready: false</c>
///         rather than refusing.</b> An <c>OvnEip</c> that was applied a moment ago has no
///         <c>status</c> at all, and a <c>404</c> or an error there would read as "your address is
///         gone" at the one moment it is most likely to be asked for.
///     </para>
/// </remarks>
/// <param name="clock">Stamps <c>sampledAt</c>. ⚠ The handler's only field, and it is not mutable.</param>
public sealed class ShowAllocationHandler(IClock clock) : IResourceActionHandler {
    /// <inheritdoc />
    public ResourceTypeName Type => PublicIpAddresses.Type;

    /// <inheritdoc />
    public string Action => PublicIpAddresses.AllocationAction;

    /// <inheritdoc />
    public async Task<Result<string>> InvokeAsync(
        ActionContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            // ⚠ Unreachable in production — the type declares RequiresCluster and ActionDispatcher
            // refuses before a handler is reached. It is here because "unreachable" is a claim about a
            // call site rather than about this method.
            return Result<string>.Failure(
                ErrorCode.InternalError,
                $"'{context.Id.Path}' has no cluster connection, and an allocated address is read from "
                + "the OvnEip object in a cluster."
            );
        }

        var target = PublicIpAddresses.OvnEipRef(context.Namespace, context.Id.Name);
        var read = await cluster.GetAsync(target, cancellationToken);

        if (read.TryGetError(out var error)) {
            return Result<string>.Failure(error);
        }

        var status = StatusOf(read.GetValueOrThrow().Json);

        return Result<string>.Success(
            new JsonObject {
                ["v4"] = Text(status, "v4Ip"),
                ["v6"] = Text(status, "v6Ip"),
                ["macAddress"] = Text(status, "macAddress"),
                ["ready"] = status?["ready"] is JsonValue ready
                    && ready.TryGetValue<bool>(out var isReady)
                    && isReady,
                ["attachedTo"] = Text(status, "nat"),
                ["sampledAt"] = clock.UtcNow.ToString("O", CultureInfo.InvariantCulture)
            }.ToJsonString()
        );
    }

    /// <summary>The <c>status</c> of an <c>OvnEip</c> document, or <see langword="null" />.</summary>
    /// <param name="objectJson">The object's JSON.</param>
    static JsonObject? StatusOf(string objectJson) {
        JsonNode? parsed;
        try {
            parsed = JsonNode.Parse(objectJson);
        } catch (JsonException) {
            return null;
        }

        return parsed is JsonObject document && document["status"] is JsonObject status ? status : null;
    }

    /// <summary>One status string, or empty when the controller has not written one.</summary>
    /// <param name="status">The address's status.</param>
    /// <param name="name">The status field.</param>
    static string Text(JsonObject? status, string name) =>
        status?[name] is JsonValue value && value.GetValueKind() is JsonValueKind.String
            ? value.GetValue<string>()
            : string.Empty;
}
