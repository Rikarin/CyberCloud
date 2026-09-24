using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace CyberCloud.Kubernetes.Contracts;

/// <summary>
///     One fully-labelled write to one cluster: what object, where, under which field manager, and
///     with the seven mandatory labels already injected.
/// </summary>
/// <remarks>
///     <para>
///         <b>This type is only reachable through the builder</b> — <see cref="For" /> returns
///         <see cref="IKubeCommandNeedsTenant" /> and <c>Build()</c> exists only on
///         <see cref="IKubeCommandBuilder" />, which is three method calls away. That is ADR-013's
///         "the builder makes omission impossible": there is no public constructor and no object
///         initializer, so a <see cref="KubeCommand" /> that reached a cluster without the labels is
///         not a thing that can be typed.
///     </para>
///     <para>
///         ⚠
///         <b>
///             docs/plan/09 § The command builder declares <c>public static class KubeCommand</c>
///             and, four lines later, <c>KubeCommand Build();</c>.
///         </b>
///         Those cannot both be true — a
///         static class cannot be a return type. The repair, which preserves both spellings the
///         document uses, is for <c>KubeCommand</c> to be an ordinary sealed record carrying a
///         <see langword="static" /> <see cref="For" />: <c>KubeCommand.For(connection)</c> reads
///         exactly as written, and <c>Build()</c> has something to return.
///     </para>
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Kubernetes.KubeCommand")]
public sealed record KubeCommand {
    /// <summary>The tenant that owns the resource being written.</summary>
    [Id(0)]
    public Guid TenantId { get; init; }

    /// <summary>The subscription the resource bills to.</summary>
    [Id(1)]
    public Guid SubscriptionId { get; init; }

    /// <summary>The resource this object belongs to.</summary>
    [Id(2)]
    public Guid ResourceId { get; init; }

    /// <summary>The object being written.</summary>
    [Id(3)]
    public ObjectRef Target { get; init; } = new();

    /// <summary>
    ///     The complete object JSON, <b>with</b> the seven labels and two annotations already in
    ///     <c>metadata</c>. This is the body sent as an apply patch.
    /// </summary>
    [Id(4)]
    public string Body { get; init; } = "{}";

    /// <summary>
    ///     The field manager — <c>cybercloud/{provider}</c>. ADR-013: "a stable field manager per
    ///     provider … that gives us conflict detection for free".
    /// </summary>
    [Id(5)]
    public string FieldManager { get; init; } = string.Empty;

    /// <summary>The seven mandatory labels plus any extras, as emitted.</summary>
    [Id(6)]
    public IReadOnlyDictionary<string, string> Labels { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The two mandatory annotations plus any extras, as emitted.</summary>
    [Id(7)]
    public IReadOnlyDictionary<string, string> Annotations { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The <c>sha256:…</c> of the body before injection — the no-op detector.</summary>
    [Id(8)]
    public string ReconcileHash { get; init; } = string.Empty;

    /// <summary>
    ///     Whether the apply may take ownership of fields another manager holds.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Always <see langword="false" /> and there is no builder method to set it.</b> ADR-013
    ///     wants a tenant's hand-edit to produce "a conflict rather than silently reverting"; the
    ///     API server's <c>force=true</c> is precisely the switch that turns that conflict back into
    ///     a silent revert. It exists as a field because the wire shape has one and because a
    ///     deliberate, audited override may one day be a supported repair action — but it is not
    ///     reachable from the builder, so no reconciler can quietly opt into stomping a tenant.
    /// </remarks>
    [Id(9)]
    public bool Force { get; init; }

    /// <summary>The resource's address, carried for the drift event and the audit line.</summary>
    [Id(10)]
    public string ResourcePath { get; init; } = string.Empty;

    /// <summary>
    ///     The GUID of the resource that <b>owns</b> the object, when this command is a co-writer's
    ///     fragment onto somebody else's object — <see cref="IKubeCommandBuilder.CoWriting" />.
    ///     <see cref="Guid.Empty" /> for the ordinary case, where <see cref="ResourceId" /> is the owner.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             Read off the live object's <c>cybercloud.io/resource-id</c> label, never
    ///             supplied.
    ///         </b> It is what a cluster connection keys two refusals on: a co-owned apply
    ///         against an object that is not there is refused rather than creating an unlabelled
    ///         object under the owner's name, and a co-owned <c>DeleteAsync</c> withdraws the fragment
    ///         rather than deleting the owner's object.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A claim, and it is checked twice rather than trusted.</b> The ordinary command's
    ///         guard is the seven labels, re-checked by the tunnel agent; a co-owned command carries
    ///         none, so setting this field on a command switches that guard off, and a command with
    ///         this set and nothing else right would be an unlabelled body applied under any manager
    ///         onto any object. <see cref="CheckCoOwnedShape" /> is what replaces the guard — the
    ///         agent runs it before applying — and <see cref="CheckCoOwnedAgainst" /> is what the
    ///         client runs on the object it read a moment before writing.
    ///     </para>
    /// </remarks>
    [Id(11)]
    public Guid OwnerResourceId { get; init; }

    /// <summary>Whether this command writes a fragment onto an object another resource owns.</summary>
    public bool IsCoOwned => OwnerResourceId != Guid.Empty;

    /// <summary>The resource group the writing resource is in.</summary>
    /// <remarks>
    ///     ⚠ <b>Carried for the co-owned check, where it is a boundary.</b> An ordinary command
    ///     writes the group as the <c>cybercloud.io/resource-group</c> label and this member repeats
    ///     it; a co-owned command writes no labels, and this is what
    ///     <see cref="CheckCoOwnedAgainst" /> holds the live object's label against. The write path
    ///     authorized the caller on the co-writer's own address and nothing else, and roles are
    ///     granted on subscriptions and groups (docs/plan/07), so one group is the smallest scope on
    ///     which write on the co-writer implies write on the owner's object. Without this, two groups
    ///     that differ by a hyphen — <c>prod</c>'s network <c>a-b</c> and <c>prod-a</c>'s network
    ///     <c>b</c> render one <c>Vpc</c> name — would let a peering in one group write a route into
    ///     a router in the other.
    /// </remarks>
    [Id(12)]
    public string ResourceGroup { get; init; } = string.Empty;

    /// <summary>
    ///     Checks that a co-owned command has the shape <see cref="IKubeCommandBuilder.CoWriting" />
    ///     builds and nothing else: no labels, the manager derived from the owner it names, the live
    ///     <c>resourceVersion</c> in the body, and the fragment bookkeeping for its own resource
    ///     present on an apply and absent on a withdrawal.
    /// </summary>
    /// <returns>
    ///     Success for an ordinary command and for a co-owned one that holds every rule; otherwise
    ///     <see cref="ErrorCode.InvalidRequestBody" /> naming the rule that failed.
    /// </returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is the co-owned mode's ADR-013 check, and the tunnel agent runs it.</b> The
    ///         seven-label check is switched off for a co-owned command by design — the labels are the
    ///         owner's and stay on the object — so without this the agent, which is the last thing
    ///         between a frame and a tenant's API server, would apply any body under any manager as
    ///         long as <c>ownerResourceId</c> was set. Each rule here is one the builder holds by
    ///         construction, restated as a check on the built command so that a frame the platform did
    ///         not build, or a control-plane bug that set the field on an ordinary command, is refused
    ///         by name rather than waved through.
    ///     </para>
    ///     <para>
    ///         The rules: <see cref="Labels" /> is empty and the body carries no <c>metadata.labels</c>,
    ///         <c>ownerReferences</c> or <c>status</c>; <see cref="FieldManager" /> is
    ///         <see cref="KubeLabels.CoWriterFieldManager" /> for <see cref="OwnerResourceId" />; the
    ///         body carries a non-empty <c>metadata.resourceVersion</c>; every annotation is one of the
    ///         three per-fragment keys; an apply — <see cref="ReconcileHash" /> set — carries its own
    ///         fragment, hash and path with the hash annotation equal to <see cref="ReconcileHash" />,
    ///         and a withdrawal carries none of its own; <see cref="Force" /> is off;
    ///         <see cref="ResourceGroup" /> is set; and the owner is not the applying resource.
    ///     </para>
    /// </remarks>
    public Result CheckCoOwnedShape() {
        if (!IsCoOwned) {
            return Result.Success;
        }

        if (Labels.Count > 0) {
            return Refuse(
                $"carries {Labels.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)} label(s). "
                + "A co-writer applies a fragment under the owner's labels and writes none of its own "
                + "(IKubeCommandBuilder.CoWriting); the labelled shape is a fragment claiming the owner's identity."
            );
        }

        if (OwnerResourceId == ResourceId) {
            return Refuse(
                "names itself as the owner. The co-owned mode is for a second writer; the owner applies with no CoWriting call."
            );
        }

        if (Force) {
            return Refuse(
                "has Force set. A co-writer never forces: a conflict with the owner's fields is drift with a name."
            );
        }

        if (ResourceGroup.Length == 0) {
            return Refuse(
                "names no resource group. A co-writer writes no labels, so the group it is in rides on the "
                + "command (KubeCommand.ResourceGroup) for the live check to hold the owner's label against; "
                + "a command without one was not built by the builder."
            );
        }

        if (!KubeLabels.TryReadCoWriterFieldManager(FieldManager, out _, out var managerOwner)
            || managerOwner != OwnerResourceId) {
            return Refuse(
                $"applies under field manager '{FieldManager}', and a co-owned apply goes under "
                + $"'cybercloud/{{ownerType}}/{OwnerResourceId:D}' — KubeLabels.CoWriterFieldManager, named for "
                + "the owner the command claims and shared by every co-writer of that object."
            );
        }

        foreach (var key in Annotations.Keys) {
            if (!KubeLabels.IsFragmentAnnotation(key)) {
                return Refuse(
                    $"carries the annotation '{key}'. A co-writer's annotations are the three per-fragment "
                    + "keys and nothing else; the rest of an object's metadata is its owner's."
                );
            }
        }

        var hasFragment = Annotations.ContainsKey(KubeLabels.FragmentAnnotation(ResourceId));
        var hasHash = Annotations.TryGetValue(KubeLabels.FragmentHashAnnotation(ResourceId), out var hash);
        var hasPath = Annotations.ContainsKey(KubeLabels.FragmentPathAnnotation(ResourceId));

        if (ReconcileHash.Length > 0) {
            if (!hasFragment || !hasHash || !hasPath) {
                return Refuse(
                    "carries a reconcile hash and not the fragment bookkeeping that goes with it. An apply in "
                    + "the co-owned mode writes its fragment, its hash and its path under cybercloud.io/fragment.*, "
                    + "fragment-hash.* and fragment-path.* keyed by its own resource id; without them the next "
                    + "co-writer's apply prunes this slice."
                );
            }

            if (!string.Equals(hash, ReconcileHash, StringComparison.Ordinal)) {
                return Refuse(
                    $"carries reconcile hash '{ReconcileHash}' and a fragment-hash annotation of '{hash}'. "
                    + "The two are one value — the hash of this co-writer's fragment — and a command where "
                    + "they differ was not built by the builder."
                );
            }
        } else if (hasFragment || hasHash || hasPath) {
            return Refuse(
                "carries no reconcile hash — the withdrawal shape — and still carries its own fragment "
                + "bookkeeping. A withdrawal applies what the other co-writers hold and drops this "
                + "co-writer's three annotations; one that keeps them withdraws nothing."
            );
        }

        JsonElement root;
        try {
            using var document = JsonDocument.Parse(Body);
            root = document.RootElement.Clone();
        } catch (JsonException ex) {
            return Refuse($"has a body that is not valid JSON: {ex.Message}");
        }

        if (root.ValueKind != JsonValueKind.Object) {
            return Refuse("has a body that is not a JSON object.");
        }

        if (root.TryGetProperty("status", out _)) {
            return Refuse(
                "carries 'status'. Status is the controller's report on the owner's object, and a co-writer applies desired state only."
            );
        }

        if (!root.TryGetProperty("metadata", out var metadata) || metadata.ValueKind != JsonValueKind.Object) {
            return Refuse(
                "has a body without 'metadata'. A co-owned apply carries metadata.resourceVersion as its optimistic lock."
            );
        }

        if (!metadata.TryGetProperty("resourceVersion", out var version)
            || version.ValueKind != JsonValueKind.String
            || string.IsNullOrEmpty(version.GetString())) {
            return Refuse(
                "carries no metadata.resourceVersion. A co-owned apply carries the version it was computed "
                + "from, so that two co-writers racing onto one object lose loudly (ApplyResult.Stale) rather "
                + "than one applying a union computed from a version the other has already replaced."
            );
        }

        foreach (var forbidden in new[] { "labels", "ownerReferences", "finalizers" }) {
            if (metadata.TryGetProperty(forbidden, out _)) {
                return Refuse(
                    $"carries 'metadata.{forbidden}' in its body. Labels, owner references and finalizers say "
                    + "whose the object is, and that is its owner's to say."
                );
            }
        }

        return Result.Success;

        Result Refuse(string what) =>
            Result.Failure(
                ErrorCode.InvalidRequestBody,
                $"A co-owned command for resource {ResourceId:D} onto resource {OwnerResourceId:D}'s object "
                + $"'{Target}' {what}"
            );
    }

    /// <summary>
    ///     Checks a co-owned command against the object it is about to be applied onto: the object
    ///     is owned by the resource the command claims, in the command's tenant, subscription and
    ///     resource group, by this platform, and the command's manager is the one derived from that
    ///     owner.
    /// </summary>
    /// <param name="live">The object, as the cluster connection read it a moment before the write.</param>
    /// <returns>
    ///     Success for an ordinary command and for a co-owned one whose owner the object confirms;
    ///     otherwise <see cref="ErrorCode.Conflict" /> naming the owner the command claims and the
    ///     one the object carries.
    /// </returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The second half of the check, and the one the shape alone cannot make.</b> A
    ///         command whose shape is right still names its owner as a claim. The object is the only
    ///         thing that can confirm it, and <c>KubeApiClient</c> has read the object a moment before
    ///         the <c>PATCH</c> — for the Created/Updated/Unchanged distinction — so the comparison
    ///         costs no extra request. What it catches: an owner's object deleted and the name taken
    ///         by another resource between a co-writer's read and its apply, and a command whose
    ///         <see cref="OwnerResourceId" /> was set by anything other than the builder reading the
    ///         live labels. The answer is <see cref="ErrorCode.Conflict" /> rather than
    ///         <see cref="ApplyResult.Stale" /> because reading again does not repair it: a
    ///         co-writer's next pass reads the new owner and decides, in its own reconciler, whether
    ///         that is the object it means.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             The subscription and the group are boundaries, and the object's labels draw
    ///             them.
    ///         </b> A co-writer's caller was authorized on the co-writer's address alone, so what
    ///         lets it change the owner's object is that write on one implies write on the other —
    ///         which holds inside one resource group and nowhere wider (docs/plan/07 grants roles on
    ///         subscriptions and groups). The builder held the same labels against the writer when it
    ///         read the object; this is the read a moment before the <c>PATCH</c>. What made it a
    ///         check rather than a naming argument: a <c>Vpc</c>'s name is
    ///         <c>{sub}-{group}-{network}</c> and both halves admit hyphens, so <c>prod</c>'s
    ///         <c>a-b</c> and <c>prod-a</c>'s <c>b</c> are one object name, and a peering in
    ///         <c>prod</c> naming <c>a-b</c> would otherwise write a route into <c>prod-a</c>'s router.
    ///     </para>
    /// </remarks>
    public Result CheckCoOwnedAgainst(KubeObject live) {
        ArgumentNullException.ThrowIfNull(live);

        if (!IsCoOwned) {
            return Result.Success;
        }

        JsonElement labels;
        try {
            using var document = JsonDocument.Parse(live.Json);

            labels = document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("metadata", out var metadata)
                && metadata.ValueKind == JsonValueKind.Object
                && metadata.TryGetProperty("labels", out var found)
                && found.ValueKind == JsonValueKind.Object
                    ? found.Clone()
                    : default;
        } catch (JsonException ex) {
            return Result.Failure(ErrorCode.Conflict, $"'{Target}' as read is not valid JSON: {ex.Message}");
        }

        var managedBy = Label(KubeLabels.ManagedBy);
        if (!string.Equals(managedBy, KubeLabels.ManagedByValue, StringComparison.Ordinal)) {
            return Refuse(
                $"is managed by '{managedBy}', not by this platform. A co-writer writes only onto an object a Cyber Cloud resource rendered."
            );
        }

        var ownerValue = Label(KubeLabels.ResourceId);
        if (!Guid.TryParseExact(ownerValue, "D", out var owner) || owner != OwnerResourceId) {
            return Refuse(
                $"carries '{ownerValue}' as its resource-id, and the command was built against resource "
                + $"{OwnerResourceId:D}'s object. The owner's object went and the name was taken by another "
                + "resource between the read and the apply, or the command's owner was not read off the "
                + "object. Nothing was written; the next pass reads the object that is there."
            );
        }

        var tenant = Label(KubeLabels.TenantId);
        if (!string.Equals(tenant, KubeLabels.GuidValue(TenantId), StringComparison.Ordinal)) {
            return Refuse(
                $"belongs to tenant {tenant} and the co-writer is in tenant {TenantId:D}. A co-writer never reaches across a tenant."
            );
        }

        var subscription = Label(KubeLabels.SubscriptionId);
        if (!string.Equals(subscription, KubeLabels.GuidValue(SubscriptionId), StringComparison.Ordinal)) {
            return Refuse(
                $"belongs to subscription {subscription} and the co-writer is in subscription {SubscriptionId:D}. "
                + "A co-writer never reaches across a subscription: write on the co-writer was checked on its own "
                + "address, and it implies write on the owner's object inside one resource group only."
            );
        }

        var group = Label(KubeLabels.ResourceGroup);
        if (!string.Equals(group, ResourceGroup, StringComparison.Ordinal)) {
            return Refuse(
                $"belongs to resource group '{group}' and the co-writer is in resource group '{ResourceGroup}'. "
                + "A co-writer never reaches across a resource group: write on the co-writer was checked on its "
                + "own address, and one group is the smallest scope on which that implies write on the owner's "
                + "object. Two groups that differ by a hyphen can render one object name, which is how a "
                + "co-writer arrives here."
            );
        }

        var expectedManager = KubeLabels.CoWriterFieldManager(Label(KubeLabels.ResourceType), ownerValue);
        if (!string.Equals(FieldManager, expectedManager, StringComparison.Ordinal)) {
            return Refuse(
                $"is co-written under '{expectedManager}' — the one manager every co-writer of it shares — "
                + $"and the command applies under '{FieldManager}'. A second manager on the object's atomic "
                + "lists conflicts with the first forever, so the manager is derived from the object, not chosen."
            );
        }

        return Result.Success;

        string Label(string key) =>
            labels.ValueKind == JsonValueKind.Object
            && labels.TryGetProperty(key, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;

        Result Refuse(string what) =>
            Result.Failure(
                ErrorCode.Conflict,
                $"'{Target}' {what} The command is resource {ResourceId:D}'s fragment onto resource "
                + $"{OwnerResourceId:D}'s object."
            );
    }

    // Internal so that only KubeCommandBuilder can mint one. A record's positional/init surface
    // would otherwise let a caller construct an unlabelled command directly, which is the exact
    // hole the type-state chain exists to close.
    internal KubeCommand() { }

    /// <summary>
    ///     Starts the type-state chain — ADR-013 and docs/plan/09 § The command builder.
    /// </summary>
    /// <param name="connection">The cluster to write to.</param>
    /// <param name="charts">
    ///     The chart renderer, when one is registered. <see langword="null" /> — the default — makes
    ///     <see cref="IKubeCommandBuilder.Chart" /> fail with a message naming
    ///     <c>CyberCloud.Kubernetes.Charts</c>. See the remarks on <see cref="IChartRenderer" />.
    /// </param>
    /// <returns>
    ///     A builder on which the <b>only</b> available method is
    ///     <see cref="IKubeCommandNeedsTenant.WithTenantId" />. There is no <c>Build()</c>, no
    ///     <c>ApplyAsync()</c> and no <c>DeleteAsync()</c> here, and that is the point: the unlabelled
    ///     case does not compile.
    /// </returns>
    public static IKubeCommandNeedsTenant For(
        IKubeClusterConnection connection,
        IChartRenderer? charts = null
    ) {
        ArgumentNullException.ThrowIfNull(connection);
        return new KubeCommandBuilder(connection, charts);
    }
}

/// <summary>
///     Stage 1 of the type-state chain. The only thing you can do is name the tenant.
/// </summary>
/// <remarks>
///     ⚠ This interface deliberately has <b>one</b> member. Anything else on it — a convenience
///     <c>Build()</c>, an <c>InNamespace</c>, a <c>WithLabels</c> — would make some unlabelled
///     command expressible, and ADR-013's whole claim is that none is.
/// </remarks>
public interface IKubeCommandNeedsTenant {
    /// <summary>Names the owning tenant and advances to stage 2.</summary>
    /// <param name="tenantId">The tenant. Becomes <c>cybercloud.io/tenant-id</c>.</param>
    IKubeCommandNeedsResource WithTenantId(Guid tenantId);
}

/// <summary>
///     Stage 2 of the type-state chain. The only thing you can do is name the resource.
/// </summary>
public interface IKubeCommandNeedsResource {
    /// <summary>Names the resource and advances to the fully-qualified stage.</summary>
    /// <param name="resourceId">
    ///     The resource. Supplies <c>resource-id</c>, and by inference <c>subscription-id</c>,
    ///     <c>resource-group</c> and <c>resource-type</c>.
    /// </param>
    IKubeCommandBuilder WithResourceId(ResourceId resourceId);
}

/// <summary>
///     The fully-qualified stage — and the <b>only</b> place <c>Build</c>, <c>ApplyAsync</c> and
///     <c>DeleteAsync</c> exist.
/// </summary>
/// <remarks>
///     ADR-013:
///     <i>
///         "<c>Build()</c> and <c>Apply()</c> exist only on the fully-qualified interface, so
///         an unlabelled object does not compile."
///     </i>
///     That is a compile-time claim and it is proved as
///     one — <c>CompileFailureTests</c> compiles the illegal call with Roslyn and asserts the
///     diagnostic, rather than asserting an exception at run time.
/// </remarks>
public interface IKubeCommandBuilder {
    /// <summary>Overrides the inferred subscription — for platform objects. docs/plan/09.</summary>
    /// <param name="id">The subscription to bill to.</param>
    IKubeCommandBuilder WithSubscriptionId(Guid id);

    /// <summary>Sets the namespace. Defaults to the resource's namespace.</summary>
    /// <param name="ns">The Kubernetes namespace.</param>
    IKubeCommandBuilder InNamespace(string ns);

    /// <summary>
    ///     Names the kind, including the <b>plural</b> the REST path needs.
    /// </summary>
    /// <param name="kind">The group, version, kind and plural.</param>
    /// <remarks>
    ///     ⚠ <b>Not in docs/plan/09's list, and required.</b> See the remarks on
    ///     <see cref="GroupVersionKind" />: every Kubernetes REST path is keyed by the plural
    ///     resource name, which is not derivable from the kind. Without this the builder would have
    ///     to guess, and a guess that works for ninety kinds and 404s on the ninety-first is worse
    ///     than a required argument.
    /// </remarks>
    IKubeCommandBuilder WithKind(GroupVersionKind kind);

    /// <summary>Adds labels. May not replace one of the seven — see the remarks.</summary>
    /// <param name="extra">The additional labels.</param>
    /// <exception cref="ArgumentException">
    ///     A key is one of <see cref="KubeLabels.Mandatory" />, or a key or value is not legal
    ///     Kubernetes label syntax. ADR-013 makes the seven "injected and non-overridable"; silently
    ///     ignoring an attempted override would leave the caller believing it took effect, so it is
    ///     loud. A mandatory-label override in code is a bug, and docs/plan/00 § Coding standards
    ///     puts bugs on the exception path.
    /// </exception>
    IKubeCommandBuilder WithLabels(params (string Key, string Value)[] extra);

    /// <summary>
    ///     Declares nested object templates inside this body whose <c>metadata.labels</c> must also
    ///     carry the platform's labels — a <c>volumeClaimTemplate</c> and nothing else, so far.
    /// </summary>
    /// <param name="paths">
    ///     <c>/</c>-separated field paths from the root of the body to a template object or to an
    ///     array of them — for example <c>spec/volumeClaimTemplates</c>. A path that does not resolve
    ///     in this body is a no-op, which is what lets one render function serve a kind that
    ///     sometimes has the field and sometimes does not.
    /// </param>
    /// <exception cref="ArgumentException">A path is empty or has an empty segment.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Why the caller declares the paths instead of the builder discovering them.</b> The
    ///         objects this platform applies carry at least three nested-template shapes and they do
    ///         not want the same treatment. A <c>PodTemplateSpec</c> already gets its labels a
    ///         different way — from the selector the workload was rendered with — and stamping the
    ///         platform's labels there would change the pod template, which is a rolling restart. A
    ///         Cluster API infrastructure machine template's <c>dataVolumeTemplates</c> lives inside an
    ///         object the CAPI contract treats as immutable and rotates rather than edits. And several
    ///         fields whose names end in <c>Template</c> hold a <b>string</b>: a
    ///         <c>ClickHouseInstallation</c>'s <c>defaults.templates.podTemplate</c> and
    ///         <c>dataVolumeClaimTemplate</c> both name a template rather than being one. A rule of
    ///         the form "descend into anything called <c>*Template</c>" hits all three. The knowledge
    ///         of which nested field is a claim template belongs to whoever knows the kind's schema,
    ///         which is the reconciler, so it is passed rather than guessed.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Only <see cref="KubeLabels.LifetimeStable" /> is written here, never the seventh.</b>
    ///         See that member: a live <c>StatefulSet</c>'s <c>spec.volumeClaimTemplates</c> cannot be
    ///         changed at all, so a template carrying the per-request <c>api-version</c> label would
    ///         make the resource unreconcilable the first time a tenant called at a newer version.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             This changes the applied body of an object that may already exist, and for a
    ///             <c>StatefulSet</c> that apply is refused.
    ///         </b> Adopting it on a cluster that already
    ///         runs one needs the set deleted with <c>--cascade=orphan</c> — measured to leave the
    ///         pods and the claims in place — and the next reconcile re-creates it. The claims that
    ///         already exist stay unlabelled either way: the StatefulSet controller stamps a claim
    ///         when it creates it and never revisits one. <c>src/Providers/README.md § Namespaces</c>
    ///         carries both halves.
    ///     </para>
    /// </remarks>
    IKubeCommandBuilder WithTemplateLabels(params string[] paths);

    /// <summary>Adds annotations. May not replace the two mandatory ones.</summary>
    /// <param name="extra">The additional annotations.</param>
    /// <exception cref="ArgumentException">A key is mandatory, or the key is not legal syntax.</exception>
    IKubeCommandBuilder WithAnnotations(params (string Key, string Value)[] extra);

    /// <summary>Adds an owner reference, so deletion cascades. docs/plan/09 § The command builder.</summary>
    /// <param name="parent">The owning resource.</param>
    /// <param name="ownerKind">The owner's kind and plural.</param>
    /// <param name="ownerName">The owner object's name in the cluster.</param>
    /// <param name="ownerUid">The owner object's <c>metadata.uid</c>, which Kubernetes requires.</param>
    IKubeCommandBuilder WithOwner(
        ResourceId parent,
        GroupVersionKind ownerKind,
        string ownerName,
        string ownerUid
    );

    /// <summary>
    ///     Switches the command to the <b>co-owned</b> mode: the body is a fragment written onto an
    ///     object <i>another</i> resource owns, and the owner keeps everything that says whose the
    ///     object is.
    /// </summary>
    /// <param name="live">
    ///     The owner's object, as <see cref="IKubeClusterConnection.GetAsync" /> returned it a moment
    ///     ago. Its labels name the owner, its <c>metadata.resourceVersion</c> is carried into the
    ///     apply as the optimistic lock, and its fragment annotations are how the builder learns what
    ///     the other co-writers have applied. ⚠ Read it on every pass; a cached copy is a stale
    ///     version, and the apply is refused as <see cref="ApplyResult.Stale" />.
    /// </param>
    /// <remarks>
    ///     <para>
    ///         <b>Why the ordinary mode cannot do this, and what this mode changes.</b> Issue #89: a
    ///         VNet peering is two entries on two <c>Vpc</c> objects, each already owned by the
    ///         <c>virtualNetworks</c> resource that rendered it. The ordinary <c>Build()</c> injects the
    ///         applying resource's seven labels and two annotations, non-overridably, so a peering
    ///         applying its parent's <c>Vpc</c> would claim <c>resource-id</c>, <c>resource-type</c>
    ///         and <c>reconcile-hash</c> at values that differ from the owner's — a
    ///         <c>FieldManagerConflict</c> on every one, which is the conflict ADR-013 exists to
    ///         produce. In this mode the rules are:
    ///     </para>
    ///     <list type="bullet">
    ///         <item>
    ///             <b>Labels are the owner's, and so is the rest of the metadata.</b> The builder
    ///             injects none, and refuses <see cref="WithLabels" />, <see cref="WithTemplateLabels" />,
    ///             <see cref="WithOwner" /> and <see cref="WithAnnotations" />: a co-writer may add
    ///             only its own fragment, and an annotation applied beside a fragment would ride under
    ///             the shared manager without being in the fragment the next co-writer merges, which
    ///             prunes it. The live object must carry the seven — a co-writer writes only onto
    ///             objects this platform owns — and its <c>tenant-id</c>, <c>subscription-id</c>
    ///             and <c>resource-group</c> must be the co-writer's own: the caller was authorized
    ///             on the co-writer's address alone, and one resource group is the smallest scope on
    ///             which write there implies write on the owner's object
    ///             (<see cref="KubeCommand.CheckCoOwnedAgainst" /> holds the same three on the read
    ///             before the <c>PATCH</c>).
    ///         </item>
    ///         <item>
    ///             <b>One field manager per co-owned object</b>,
    ///             <see cref="KubeLabels.CoWriterFieldManager" />, named for the owner and shared by
    ///             every co-writer of that object; <see cref="WithFieldManager" /> is refused. The
    ///             reason is the atomic list — see that member.
    ///         </item>
    ///         <item>
    ///             <b>A hash and a path per fragment</b>,
    ///             <see cref="KubeLabels.FragmentHashAnnotationPrefix" /> and
    ///             <see cref="KubeLabels.FragmentPathAnnotationPrefix" /> keyed by the co-writer's
    ///             GUID, beside the owner's two annotations rather than over them; and the fragment
    ///             itself under <see cref="KubeLabels.FragmentAnnotationPrefix" />, which is what lets
    ///             the next co-writer merge without asking.
    ///         </item>
    ///         <item>
    ///             <b>The union is applied.</b> The body is merged with every other co-writer's stored
    ///             fragment — objects recursively, arrays by concatenation in co-writer order, and a
    ///             scalar two fragments set differently is a refusal naming the path — so no
    ///             co-writer's apply prunes another's.
    ///         </item>
    ///         <item>
    ///             <b><c>metadata.resourceVersion</c> is carried</b> from <paramref name="live" />, so
    ///             two co-writers racing onto one object lose loudly: the second is
    ///             <see cref="ApplyResult.Stale" /> and reads again, rather than applying a union
    ///             computed from a version that no longer exists.
    ///         </item>
    ///         <item>
    ///             <b>Teardown withdraws.</b> <see cref="DeleteAsync" /> in this mode applies the
    ///             other co-writers' fragments without this one's and drops only this co-writer's
    ///             three annotations; it never deletes the owner's object. The owner's delete wins:
    ///             once the object is gone, a co-owned apply is refused rather than re-creating it.
    ///         </item>
    ///     </list>
    ///     <para>
    ///         ⚠ <b>Never for an object the resource itself owns.</b> A resource co-writing its own
    ///         object is refused by name; the ordinary mode is one <see cref="Object{T}" /> call away.
    ///     </para>
    /// </remarks>
    IKubeCommandBuilder CoWriting(KubeObject live);

    /// <summary>
    ///     Makes the apply conditional on the object still being at the version the caller read: the
    ///     body carries <c>metadata.resourceVersion</c>, and an object that moved in between is
    ///     refused as <see cref="ApplyResult.Stale" /> with nothing written.
    /// </summary>
    /// <param name="resourceVersion">
    ///     The <see cref="KubeObject.ResourceVersion" /> of the read the body was computed from, or
    ///     empty for no precondition — what a caller passes when its read found no object.
    /// </param>
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             For a render that carries a value it READ off the object, and for nothing
    ///             else.
    ///         </b> The ordinary apply is last-writer-wins by design, because a reconciler's
    ///         document is a pure function of the desired body. A virtual machine's run strategy and
    ///         a scale set's replica count are not: the reconciler reads them back and writes them
    ///         again, and an action that moved them between the read and the write was silently
    ///         undone — <c>charts/managed/virtual-machine/conformance.yaml</c>'s
    ///         <c>power-state-can-lose-a-race</c>, closed by this member. With the precondition the
    ///         pass loses loudly and reads again, as a co-writer does.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The version rides in the body, after the reconcile hash is taken</b>, so the hash
    ///         still describes the desired document and two passes over one body still hash alike.
    ///         <c>KubeApiClient</c> already reads an optimistic-lock <c>409</c> as
    ///         <see cref="ApplyResult.Stale" /> rather than as drift. ⚠ Against an object that is
    ///         absent the API server does not hold the lock — its create-on-update path clears the
    ///         version and creates (<c>CoOwnedApplyTests</c> measured it) — so the precondition
    ///         guards a read that found something, and an empty version is the honest spelling of
    ///         one that did not.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Refused in the co-owned mode</b>, which carries the live object's version on its
    ///         own; two sources for one field would be two answers to which read the apply is from.
    ///     </para>
    /// </remarks>
    IKubeCommandBuilder IfResourceVersion(string resourceVersion);

    /// <summary>Overrides the field manager. Defaults to <c>cybercloud/{provider}</c>.</summary>
    /// <param name="manager">The field manager name.</param>
    IKubeCommandBuilder WithFieldManager(string manager);

    /// <summary>Sets the api-version label — the version the desired state was written at.</summary>
    /// <param name="apiVersion">For example <c>2026-08-01</c>.</param>
    IKubeCommandBuilder WithApiVersion(string apiVersion);

    /// <summary>
    ///     Renders a chart and applies the result. <b>The seam only</b> — see the remarks.
    /// </summary>
    /// <param name="chart">The chart name, for example <c>managed/postgres</c>.</param>
    /// <param name="values">The values document.</param>
    /// <remarks>
    ///     ⚠ <b>Not implemented here, on purpose.</b> docs/plan/03 § src puts Helm rendering in its
    ///     own assembly, <c>CyberCloud.Kubernetes.Charts</c>, which does not exist yet. This records
    ///     the request; if no <see cref="IChartRenderer" /> was supplied to <see cref="KubeCommand.For" />
    ///     the command fails to build with a message naming that assembly, rather than silently
    ///     applying nothing.
    /// </remarks>
    IKubeCommandBuilder Chart(string chart, JsonElement values);

    /// <summary>
    ///     Sets the object to apply, by serializing <paramref name="obj" /> to JSON.
    /// </summary>
    /// <typeparam name="T">
    ///     Anything serializable. ⚠ <b>Not</b> constrained to
    ///     <c>IKubernetesObject&lt;V1ObjectMeta&gt;</c> as docs/plan/09 § The command builder writes
    ///     it — that constraint would put <c>k8s.Models</c> in every caller's compile-time closure,
    ///     which docs/plan/03 § Assembly graph rules rule 3 forbids. See the remarks on
    ///     <see cref="KubeObject" />.
    /// </typeparam>
    /// <param name="obj">The object.</param>
    // ⚠ CA1720/CA1716 suppressed rather than obeyed: both want this renamed because `Object` is a
    // type name and a Visual Basic keyword. docs/plan/09 § The command builder spells the method
    // `Object<T>(T obj)`, and the value of a builder whose call sites read exactly as the design
    // document writes them is worth more here than cross-language override ergonomics on an
    // interface no other language will implement. The neighbouring ObjectJson(string) gives anyone
    // who dislikes the name an alternative.
    [SuppressMessage(
        "Naming",
        "CA1720:Identifier contains type name",
        Justification = "The name is docs/plan/09 § The command builder's, verbatim."
    )]
    [SuppressMessage(
        "Naming",
        "CA1716:Identifiers should not match keywords",
        Justification = "The name is docs/plan/09 § The command builder's, verbatim."
    )]
    IKubeCommandBuilder Object<T>(T obj)
        where T : notnull;

    /// <summary>Sets the object to apply, from JSON that is already built.</summary>
    /// <param name="json">A complete Kubernetes object document.</param>
    IKubeCommandBuilder ObjectJson(string json);

    /// <summary>
    ///     Builds the command, injecting the seven labels and two annotations.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///     The command is not buildable — see <see cref="TryBuild" /> for the same check without the
    ///     throw. This throws because reaching it means a reconciler was written wrong, and
    ///     docs/plan/00 § Coding standards keeps bugs on the exception path.
    /// </exception>
    KubeCommand Build();

    /// <summary>
    ///     <see cref="Build" /> as a <see cref="Result" />, for the paths that must not throw.
    /// </summary>
    Result<KubeCommand> TryBuild();

    /// <summary>Builds and applies, server-side, under the field manager.</summary>
    /// <param name="cancellationToken">The reconcile's token.</param>
    Task<Result<ApplyOutcome>> ApplyAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Builds and deletes — or, after <see cref="CoWriting" />, withdraws this co-writer's
    ///     fragment and leaves the owner's object standing.
    /// </summary>
    /// <param name="policy">
    ///     How to cascade. Ignored in the co-owned mode, where nothing is deleted: the object is the
    ///     owner's, and how its dependents go is the owner's call.
    /// </param>
    /// <param name="cancellationToken">The reconcile's token.</param>
    Task<Result> DeleteAsync(
        CascadePolicy policy = CascadePolicy.Background,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
///     The seam <c>CyberCloud.Kubernetes.Charts</c> fills — docs/plan/03 § src.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/09 § The command builder: charts are rendered <b>in-process</b> with a Helm
///         library and the resulting objects applied with server-side apply — no <c>HelmRelease</c>,
///         no Flux, because desired state must not live in the target cluster's etcd (ADR-001) and
///         the reconcile loop must be ours.
///     </para>
///     <para>
///         The interface is declared here, in the assembly the builder lives in, so that the
///         builder's shape is settled before the renderer exists. Nothing implements it yet.
///     </para>
/// </remarks>
public interface IChartRenderer {
    /// <summary>Renders a chart to Kubernetes object documents.</summary>
    /// <param name="chart">The chart name.</param>
    /// <param name="values">The values document.</param>
    /// <param name="releaseNamespace">The namespace the release targets.</param>
    /// <param name="releaseName">The release name.</param>
    Result<IReadOnlyList<string>> Render(
        string chart,
        JsonElement values,
        string releaseNamespace,
        string releaseName
    );
}
