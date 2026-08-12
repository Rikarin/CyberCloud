using System.Collections.Immutable;
using System.Globalization;

namespace CyberCloud.ResourceManager.Contracts;

// ⚠ SecretRef LIVED HERE AND NOW LIVES IN CyberCloud.Core.Contracts, WITH ITS ALIAS UNCHANGED.
// docs/plan/00 § Non-negotiables writes "secrets are SecretRef handles resolved at the data plane"
// platform-wide, and a platform-wide concept in one module's contracts is a concept every other
// module either takes a dependency for or re-declares — CyberCloud.Identity.Contracts re-declared it.
// The alias is still "CyberCloud.ResourceManager.SecretRef" because an [Alias] is a wire identifier
// (docs/plan/04 § Failure and upgrade) and re-spelling one on a move is a data-loss bug wearing a
// refactor's clothes. It is imported through this assembly's GlobalUsings, so every provider that
// named it still does.

/// <summary>
///     What an observation saw. The hot-tier half of a resource's state — docs/plan/05 § Hot lists
///     "observed state" among what the hot tier holds.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b><see cref="Json" /> is a string rather than a <c>JsonElement</c> and that is forced.</b>
///         This type is grain state and travels between silos, and <c>JsonElement</c> is a view over a
///         pooled buffer with no Orleans codec. Storing the text and re-parsing on use costs a parse
///         per pass and removes an entire class of use-after-dispose bug, which on the observed path —
///         where the buffer's owner is a response body that was disposed three awaits ago — is not
///         hypothetical.
///     </para>
///     <para>
///         <b><see cref="Exists" /> is separate from an empty <see cref="Json" /></b> because they
///         mean different things to the drift scan: an object that is absent is a
///         <see cref="DriftKind.Stray" />, while an object that is present and empty is a resource
///         somebody hollowed out.
///     </para>
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.ResourceManager.ObservedState")]
public sealed record ObservedState {
    /// <summary>An observation that found nothing, at the epoch.</summary>
    public static ObservedState Absent { get; } = new();

    /// <summary>Whether anything was there at all.</summary>
    [Id(0)]
    public bool Exists { get; init; }

    /// <summary>The observed shape, as JSON text. <c>{}</c> when nothing was observed.</summary>
    [Id(1)]
    public string Json { get; init; } = "{}";

    /// <summary>When the observation was taken. ⚠ An old observation is not a read-back — clause 4.</summary>
    [Id(2)]
    public DateTimeOffset ObservedAt { get; init; }

    /// <summary>
    ///     The cluster's own revision for the observed object, so two observations can be compared
    ///     without diffing their bodies. Empty for a provider with no such concept.
    /// </summary>
    [Id(3)]
    public string Revision { get; init; } = string.Empty;

    /// <summary>
    ///     The <c>cybercloud.io/reconcile-hash</c> the observed objects carry — docs/plan/09 § The
    ///     command builder, "of the desired body — cheap no-op detection". Empty when unknown.
    /// </summary>
    [Id(4)]
    public string ReconcileHash { get; init; } = string.Empty;

    /// <summary>One line for a human: "2 of 3 replicas ready". Empty when there is nothing to say.</summary>
    [Id(5)]
    public string Summary { get; init; } = string.Empty;
}

/// <summary>
///     Who is asking. The gateway builds one per request from the token — docs/plan/08 § The write
///     path, end to end's <c>CallerContext (user or service principal, tenant, scopes)</c>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The subject is a type and an id, not a <c>SubjectRef</c>, and that is an assembly-graph
///         decision.</b> A <c>SubjectRef</c> would drag <c>CyberCloud.Authorization.Contracts</c> into
///         every provider's reference set, and with it <c>ICheckGrain</c> — the one type
///         docs/plan/07 § The enforcement seam says providers must never reach. The manager builds the
///         <c>SubjectRef</c> at the seam, from these two strings.
///     </para>
///     <para>
///         <see cref="ImpersonatedBy" /> is the <c>X-CyberCloud-Impersonated-By</c> of
///         docs/plan/06 § Platform administration. It is on the caller rather than on the request so
///         that no write path can carry a caller without carrying it.
///     </para>
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.ResourceManager.CallerContext")]
public sealed record CallerContext {
    /// <summary>The tenant the request is <i>for</i>. ⚠ Not necessarily the caller's own — see <see cref="ImpersonatedBy" />.</summary>
    [Id(0)]
    public Guid TenantId { get; init; }

    /// <summary>
    ///     The ReBAC subject type — one of <c>user</c>, <c>servicePrincipal</c> or
    ///     <c>managedIdentity</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The spelling is <c>servicePrincipal</c>, and it is matched <i>ordinally</i>.</b> This
    ///     summary said <c>serviceprincipal</c> until 2026-08-11, which is not a typo with no
    ///     consequence: it names a subject no tuple mentions, so every <c>Check</c> denies and the
    ///     symptom presents as a permissions bug rather than as a bad value. The closed set is
    ///     <c>SubjectTypes</c> in <c>CyberCloud.Authorization.Contracts</c>, which refuses anything
    ///     outside it rather than correcting a known-bad spelling. It lived in
    ///     <c>CyberCloud.Identity.Contracts</c> until 2026-08-12, which was itself the symptom — the
    ///     vocabulary was declared where one caller could reach it rather than where both could.
    /// </remarks>
    [Id(1)]
    public string SubjectType { get; init; } = "user";

    /// <summary>The ReBAC subject id.</summary>
    [Id(2)]
    public string SubjectId { get; init; } = string.Empty;

    /// <summary>The operator behind an impersonated request, or empty. Always audited.</summary>
    [Id(3)]
    public string ImpersonatedBy { get; init; } = string.Empty;

    /// <summary>The request's correlation id, which goes in the response header and the trace.</summary>
    [Id(4)]
    public string CorrelationId { get; init; } = string.Empty;

    /// <inheritdoc />
    public override string ToString() => $"{SubjectType}:{SubjectId}@{TenantId:D}";
}

/// <summary>
///     Which steps of docs/plan/08 § The write path, end to end a request actually entered, in order.
/// </summary>
/// <remarks>
///     ⚠ <b>This exists to make step order a test rather than a review item.</b> The ordering
///     <i>is</i> the security property — <c>Check</c> before quota, quota before the index claim, all
///     three before a provider is called. A trace turns "we believe the order is right" into an
///     assertion that fails when someone moves a step, including when they move it for a good reason.
///     <para>
///         <see cref="Reached" /> is append-only and never reordered, so
///         <see cref="IsCanonicalPrefix" /> is the whole check: strictly increasing, and every entry a
///         member of the canonical sequence.
///     </para>
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.ResourceManager.WriteTrace")]
public sealed record WriteTrace {
    /// <summary>The canonical order — the twelve steps as docs/plan/08 numbers them.</summary>
    public static ImmutableArray<WriteStep> Canonical { get; } = [
        WriteStep.ResolveRegistration,
        WriteStep.ValidateBody,
        WriteStep.AuthorizationCheck,
        WriteStep.Locks,
        WriteStep.Policy,
        WriteStep.Quota,
        WriteStep.IndexClaim,
        WriteStep.LinkParent,
        WriteStep.SubmitDesired,
        WriteStep.StartOperation,
        WriteStep.EmitChanged,
        WriteStep.Accepted
    ];

    /// <summary>The steps entered, in the order they were entered.</summary>
    [Id(0)]
    public ImmutableArray<WriteStep> Reached { get; init; } = [];

    /// <summary>The step the request stopped at, or <see cref="WriteStep.Accepted" /> when it did not.</summary>
    public WriteStep StoppedAt => Reached.IsDefaultOrEmpty ? WriteStep.None : Reached[^1];

    /// <summary>
    ///     Whether this trace is a strictly increasing prefix of <see cref="Canonical" /> — which is
    ///     what "no step moved" means.
    /// </summary>
    /// <returns>
    ///     <c>true</c> when every recorded step is greater than the one before it and the recorded
    ///     sequence matches <see cref="Canonical" /> from the start with no gaps.
    /// </returns>
    public bool IsCanonicalPrefix() {
        if (Reached.IsDefaultOrEmpty) {
            return true;
        }

        if (Reached.Length > Canonical.Length) {
            return false;
        }

        for (var i = 0; i < Reached.Length; i++) {
            if (Reached[i] != Canonical[i]) {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public override string ToString() =>
        Reached.IsDefaultOrEmpty ? "(no steps)" : string.Join(" → ", Reached);
}

/// <summary>
///     A write request as it reaches the manager, after the gateway has authenticated and resolved
///     the region — docs/plan/08 § The write path, end to end.
/// </summary>
/// <remarks>
///     ⚠ <b><see cref="Body" /> is JSON text, not a parsed document.</b> The manager parses it once,
///     inside step 2, and a parse failure is an <see cref="ErrorCode.InvalidRequestBody" /> like any
///     other schema failure rather than an exception from the deserializer. Handing the manager a
///     pre-parsed body would put the parse before step 1, which is the one place a malformed request
///     could reach code that has not yet decided whether the caller may see this resource at all.
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.ResourceManager.WriteRequest")]
public sealed record WriteRequest {
    /// <summary>The resource id path from the URL.</summary>
    [Id(0)]
    public string Path { get; init; } = string.Empty;

    /// <summary>The <c>api-version</c> query parameter. ⚠ Required — there is no "latest".</summary>
    [Id(1)]
    public string ApiVersion { get; init; } = string.Empty;

    /// <summary>The verb. <see cref="WriteVerb.Put" /> replaces; <see cref="WriteVerb.Patch" /> merges.</summary>
    [Id(2)]
    public WriteVerb Verb { get; init; } = WriteVerb.Unknown;

    /// <summary>The request body, as JSON text.</summary>
    [Id(3)]
    public string Body { get; init; } = "{}";

    /// <summary>Who is asking.</summary>
    [Id(4)]
    public CallerContext Caller { get; init; } = new();

    /// <summary>
    ///     The <c>If-Match</c> etag, or empty. docs/plan/06 § Tags, locks: <i>"the only way to make
    ///     concurrent portal edits safe"</i>.
    /// </summary>
    [Id(5)]
    public string IfMatch { get; init; } = string.Empty;

    /// <summary>
    ///     The action name, for <see cref="WriteVerb.Post" />. ⚠ An action is on an <i>existing</i>
    ///     resource; a <see cref="WriteVerb.Post" /> to a name that does not exist is a
    ///     <c>404</c> and never a create.
    /// </summary>
    [Id(6)]
    public string Action { get; init; } = string.Empty;
}

/// <summary>
///     The <c>202 Accepted</c> of step 12, with everything the response needs.
/// </summary>
[GenerateSerializer]
[Alias("CyberCloud.ResourceManager.WriteAccepted")]
public sealed record WriteAccepted {
    /// <summary>The operation to poll. Rendered into <c>Azure-Async-Operation</c>.</summary>
    [Id(0)]
    public Guid OperationId { get; init; }

    /// <summary>The operation's URL — <c>/operations/{opId}</c>.</summary>
    [Id(1)]
    public string OperationUri { get; init; } = string.Empty;

    /// <summary>The <c>Retry-After</c>, in seconds. docs/plan/08 gives 10 for the first poll.</summary>
    [Id(2)]
    public int RetryAfterSeconds { get; init; } = 10;

    /// <summary>The resource as it now stands, in <c>Creating</c> or <c>Updating</c>.</summary>
    [Id(3)]
    public ResourceSnapshot Resource { get; init; } = new();

    /// <summary>
    ///     Which steps ran. ⚠ Present on the <i>success</i> path too, so a test can assert the order
    ///     of a request that worked rather than only of one that was refused.
    /// </summary>
    [Id(4)]
    public WriteTrace Trace { get; init; } = new();

    /// <summary>
    ///     Whether this write changed nothing — a repeated identical <c>PUT</c>. ⚠ The response is
    ///     still a success; that is what "idempotent" means and it is why the API is <c>PUT</c>.
    /// </summary>
    [Id(5)]
    public bool NoOp { get; init; }
}

/// <summary>
///     A resource as the API renders it, at one api-version.
/// </summary>
/// <remarks>
///     ⚠ <b><see cref="Properties" /> is <i>projected</i> to <see cref="ApiVersion" /> and is not the
///     grain's whole state.</b> docs/plan/08 § The provider registry: <i>"the grain's state is a
///     <b>superset</b> and a read at an old version projects down"</i>. A snapshot that leaked a
///     newer version's field would break the SDK that was generated against the older one, which is
///     the failure the immutable-date rule exists to prevent.
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.ResourceManager.ResourceSnapshot")]
public sealed record ResourceSnapshot {
    /// <summary>The resource GUID.</summary>
    [Id(0)]
    public Guid Id { get; init; }

    /// <summary>The address, as docs/plan/06 § Identifiers spells it.</summary>
    [Id(1)]
    public string Path { get; init; } = string.Empty;

    /// <summary>The type, as <c>{namespace}/{type}</c>.</summary>
    [Id(2)]
    public string Type { get; init; } = string.Empty;

    /// <summary>The name within the group.</summary>
    [Id(3)]
    public string Name { get; init; } = string.Empty;

    /// <summary>The api-version this snapshot is projected to.</summary>
    [Id(4)]
    public string ApiVersion { get; init; } = string.Empty;

    /// <summary>The Azure provisioning vocabulary. docs/plan/06 § Tags, locks.</summary>
    [Id(5)]
    public ProvisioningState ProvisioningState { get; init; } = ProvisioningState.Unknown;

    /// <summary>The projected body, as JSON text.</summary>
    [Id(6)]
    public string Properties { get; init; } = "{}";

    /// <summary>Key/value tags, at most 50 pairs. docs/plan/06 § Tags, locks.</summary>
    [Id(7)]
    public ImmutableDictionary<string, string> Tags { get; init; } =
        ImmutableDictionary<string, string>.Empty;

    /// <summary>The etag, for <c>If-Match</c>.</summary>
    [Id(8)]
    public string Etag { get; init; } = string.Empty;

    /// <summary>Where the resource lives.</summary>
    [Id(9)]
    public string Location { get; init; } = string.Empty;

    /// <summary>
    ///     Which cluster the resource is placed into, or <see cref="Guid.Empty" /> for a provider with
    ///     no cluster. docs/plan/08 § What the resource manager deliberately does not do: <i>"the
    ///     manager just carries the id"</i>.
    /// </summary>
    [Id(10)]
    public Guid ClusterId { get; init; }

    /// <summary>Who created it, as a subject string.</summary>
    [Id(11)]
    public string CreatedBy { get; init; } = string.Empty;

    /// <summary>When.</summary>
    [Id(12)]
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Who last changed it.</summary>
    [Id(13)]
    public string ModifiedBy { get; init; } = string.Empty;

    /// <summary>When.</summary>
    [Id(14)]
    public DateTimeOffset ModifiedAt { get; init; }

    /// <summary>
    ///     Why the last attempt failed, or empty. ⚠ Present on a resource stuck in
    ///     <see cref="ProvisioningState.Deleting" />, which stays visible on purpose.
    /// </summary>
    [Id(15)]
    public string LastFailure { get; init; } = string.Empty;

    /// <summary>The operation currently driving this resource, or <see cref="Guid.Empty" />.</summary>
    [Id(16)]
    public Guid OperationId { get; init; }

    /// <summary>The lock in force at this scope, inherited included.</summary>
    [Id(17)]
    public LockLevel Lock { get; init; } = LockLevel.None;
}

/// <summary>
///     Everything an operation needs to re-drive itself after a silo loss.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/08 § Long-running operations:
///         <i>
///             "State is <b>durable</b> (ADR-003) and includes everything needed to re-drive: the
///             resource id, the desired body, the quota lease, the index claim, the step cursor. On
///             activation after a silo loss the grain re-registers its reminder and continues."
///         </i>
///     </para>
///     <para>
///         ⚠ <b>Every field here is one the resume path cannot recompute.</b> That is the test for
///         whether something belongs: the quota lease id cannot be re-derived (a new reservation would
///         double-count), and <see cref="IndexClaimed" /> cannot be re-derived (a claim that looks
///         free might be a claim this operation made and lost). Anything that <i>can</i> be
///         recomputed — the projected snapshot, the reconciler instance — deliberately is not here.
///     </para>
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.ResourceManager.OperationSpec")]
public sealed record OperationSpec {
    /// <summary>The operation's own id. Also its grain key.</summary>
    [Id(0)]
    public Guid OperationId { get; init; }

    /// <summary>What this operation is doing.</summary>
    [Id(1)]
    public OperationKind Kind { get; init; } = OperationKind.Unknown;

    /// <summary>The resource's address.</summary>
    [Id(2)]
    public string ResourcePath { get; init; } = string.Empty;

    /// <summary>The resource's GUID.</summary>
    [Id(3)]
    public Guid ResourceId { get; init; }

    /// <summary>The tenant.</summary>
    [Id(4)]
    public Guid TenantId { get; init; }

    /// <summary>The subscription, which is where the quota lease lives.</summary>
    [Id(5)]
    public Guid SubscriptionId { get; init; }

    /// <summary>The api-version the desired body was written at.</summary>
    [Id(6)]
    public string ApiVersion { get; init; } = string.Empty;

    /// <summary>The desired body, as JSON text. ⚠ What a resume re-submits, unchanged.</summary>
    [Id(7)]
    public string Desired { get; init; } = "{}";

    /// <summary>
    ///     The quota leases this operation holds. ⚠ Released on failure and on cancellation; expire on
    ///     their own if this grain dies — docs/plan/06 § Quota.
    /// </summary>
    [Id(8)]
    public ImmutableArray<Guid> QuotaLeaseIds { get; init; } = [];

    /// <summary>
    ///     Whether step 7 claimed the name. ⚠ A resume must know this: releasing a claim this
    ///     operation never made would free somebody else's name.
    /// </summary>
    [Id(9)]
    public bool IndexClaimed { get; init; }

    /// <summary>Who asked.</summary>
    [Id(10)]
    public CallerContext Caller { get; init; } = new();

    /// <summary>The action name, for <see cref="OperationKind.Action" />.</summary>
    [Id(11)]
    public string Action { get; init; } = string.Empty;

    /// <summary>
    ///     The parent operation, or <see cref="Guid.Empty" />. docs/plan/08 § Long-running operations:
    ///     deleting a resource group is one operation with N children.
    /// </summary>
    [Id(12)]
    public Guid ParentOperationId { get; init; }

    /// <summary>
    ///     The committed usage this operation is accountable for: what a create's leases turn into,
    ///     and what a delete gives back.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This exists because a delete has no leases and the amounts were committed.</b>
    ///         <c>OperationGrain</c> gave committed quota back by calling
    ///         <c>IQuotaGrain.ReleaseAsync</c>, which releases a <i>reservation</i>; a delete holds
    ///         none, so the call did nothing and a subscription's committed usage drifted upward on
    ///         every delete — allowance the tenant paid for and never got back.
    ///         <c>IQuotaGrain.ReturnAsync</c> is the method that unwinds committed usage, and it needs
    ///         a meter and an amount, which nothing carried. Now the spec does.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Empty means "as before", which is what makes appending it safe.</b>
    ///         docs/plan/05 § Serialization and schema evolution: a new member is optional and its
    ///         default means what the absence used to. An operation started by a peer that predates
    ///         this member carries nothing here and returns nothing — the old behaviour, not a wrong
    ///         new one. ⚠ And the durable tier is JSON, where the <b>property name</b> is the
    ///         persisted contract rather than the <c>[Id(n)]</c>: renaming this member breaks
    ///         re-drives of in-flight operations even though the number is untouched.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Numbered 13, never by renumbering.</b> The twelve above keep the numbers they were
    ///         published under.
    ///     </para>
    /// </remarks>
    [Id(13)]
    public ImmutableArray<QuotaCommitment> CommittedQuota { get; init; } = [];

    /// <summary>
    ///     The GUID of the resource this one's address names as its parent, or <see cref="Guid.Empty" />
    ///     for a top-level resource.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This exists because the unlink cannot re-derive it, and a tuple you cannot rebuild
    ///         is a tuple you cannot delete.</b> <c>OperationGrain</c> reconstructs the address with
    ///         <c>ResourceId.ParsePath(spec.ResourcePath).WithId(spec.ResourceId)</c>, and a parsed path
    ///         carries no GUIDs at all — docs/plan/06 § Identifiers keeps them out — so the parent's is
    ///         <see cref="Guid.Empty" /> exactly where <c>UnlinkFromParentAsync</c> needs it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And re-resolving it through <c>IResourceIndexGrain</c> at unlink time is the thing
    ///         that must not be done.</b> The unlink runs after <c>CompleteDeleteAsync</c> — the
    ///         resource is already gone — and is retried from a reminder to
    ///         <c>ReconcileSchedule</c>'s sixty-minute ceiling. docs/plan/08 § Deleting a parent
    ///         resource that has children records that the 409 which would stop a parent being deleted
    ///         out from under its children is decided and <i>not built</i>, so the parent genuinely may
    ///         be gone by then. A lookup would fail every retry, converge never, and leave the dangling
    ///         tuple <c>ParentEdgeTests.DeleteLeavesNoDanglingTuple</c> exists to catch. Persisted at
    ///         create, read at delete, never resolved twice.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Empty means "as before", which is what makes appending it safe.</b> docs/plan/05
    ///         § Serialization and schema evolution — an operation started by a peer that predates this
    ///         member carries nothing here and unlinks a group-subject edge, which is what every
    ///         resource had when that peer was written. ⚠ Numbered 14, never by renumbering, and the
    ///         durable tier is JSON so the property NAME is the persisted contract.
    ///     </para>
    /// </remarks>
    [Id(14)]
    public Guid ParentResourceId { get; init; }
}

/// <summary>
///     One meter's committed amount, as an operation records it.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Not a <c>QuotaLease</c> and not a <c>QuotaUsage</c>, because it is neither.</b> A lease
///         is a reservation that has not landed and is gone the moment it commits; a usage is the
///         subscription's running totals. This is the amount <i>one operation</i> put on <i>one
///         meter</i> — the only thing a delete needs in order to give back exactly what the create
///         took, rather than an approximation re-derived from a body that may have moved.
///     </para>
///     <para>
///         ⚠ <b>A record rather than a tuple or two parallel arrays.</b> Two arrays that must stay the
///         same length is a wire type that can be internally inconsistent, and the first re-drive after
///         a partial write is where that shows up.
///     </para>
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.ResourceManager.QuotaCommitment")]
public sealed record QuotaCommitment {
    /// <summary>Which limit. ⚠ <see cref="QuotaMeter.Unknown" /> is not a meter and is not returned.</summary>
    [Id(0)]
    public QuotaMeter Meter { get; init; } = QuotaMeter.Unknown;

    /// <summary>How much. ⚠ Must be positive; <c>IQuotaGrain.ReturnAsync</c> refuses anything else.</summary>
    [Id(1)]
    public decimal Amount { get; init; }

    /// <inheritdoc />
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Meter}={Amount}");
}

/// <summary>One entry in an operation's progress array. docs/plan/08 § Long-running operations.</summary>
[GenerateSerializer]
[Alias("CyberCloud.ResourceManager.OperationProgress")]
public sealed record OperationProgress {
    /// <summary>When.</summary>
    [Id(0)]
    public DateTimeOffset At { get; init; }

    /// <summary>The phase name a reconciler reported — <c>applying</c>, <c>waiting-for-ready</c>.</summary>
    [Id(1)]
    public string Step { get; init; } = string.Empty;

    /// <summary>What happened, naming the actual numbers.</summary>
    [Id(2)]
    public string Detail { get; init; } = string.Empty;

    /// <summary>How far along, 0 to 100.</summary>
    [Id(3)]
    public int PercentComplete { get; init; }

    /// <inheritdoc />
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Step}: {Detail}");
}

/// <summary>
///     An operation's current state, plus the progress array docs/plan/08 § Long-running operations
///     requires.
/// </summary>
[GenerateSerializer]
[Alias("CyberCloud.ResourceManager.OperationStatus")]
public sealed record OperationStatus {
    /// <summary>The operation.</summary>
    [Id(0)]
    public Guid OperationId { get; init; }

    /// <summary>Azure's status vocabulary. See <see cref="OperationState" /> on the naming.</summary>
    [Id(1)]
    public OperationState State { get; init; } = OperationState.Unknown;

    /// <summary>The resource's address.</summary>
    [Id(2)]
    public string ResourcePath { get; init; } = string.Empty;

    /// <summary>When the operation was accepted.</summary>
    [Id(3)]
    public DateTimeOffset StartedAt { get; init; }

    /// <summary>When it reached a terminal state, or the epoch.</summary>
    [Id(4)]
    public DateTimeOffset EndedAt { get; init; }

    /// <summary>
    ///     The progress array, oldest first. ⚠ Capped — see
    ///     <c>OperationGrain.MaxProgressEntries</c>; an unbounded array on a durable grain is a row
    ///     that grows until the write fails.
    /// </summary>
    [Id(5)]
    public ImmutableArray<OperationProgress> Progress { get; init; } = [];

    /// <summary>Why it failed, in the one error shape, or <see langword="null" />.</summary>
    [Id(6)]
    public Error? Error { get; init; }

    /// <summary>How far along, 0 to 100.</summary>
    [Id(7)]
    public int PercentComplete { get; init; }

    /// <summary>Whether a cancellation has been asked for but has not finished.</summary>
    /// <remarks>
    ///     ⚠ Distinct from <see cref="OperationState.Canceled" />, which is reached only <i>after</i>
    ///     the delete path has run for everything already applied. A caller that treats this flag as
    ///     "it stopped" is reading the wrong field — docs/plan/08 § Long-running operations,
    ///     <i>"cancellation <b>completes</b> rather than abandoning"</i>.
    /// </remarks>
    [Id(8)]
    public bool CancelRequested { get; init; }

    /// <summary>Why cancellation was asked for.</summary>
    [Id(9)]
    public string CancelReason { get; init; } = string.Empty;

    /// <summary>How many reconcile passes have run. The scheduler's backoff index.</summary>
    [Id(10)]
    public int Attempts { get; init; }

    /// <summary>
    ///     How many times this grain has activated while the operation was live. ⚠ Greater than one
    ///     means the operation was re-driven after a silo loss, which is the observable half of
    ///     docs/plan/00's <i>every LRO is resumable</i>.
    /// </summary>
    [Id(11)]
    public int Activations { get; init; }

    /// <summary>Child operations, for a nested operation. Empty otherwise.</summary>
    [Id(12)]
    public ImmutableArray<Guid> Children { get; init; } = [];

    /// <summary>
    ///     The GUID of the resource this operation is driving, or <see cref="Guid.Empty" /> for an
    ///     operation that was never started.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Added so that <see cref="IResourceManager.GetOperationAsync" /> can authorize a
    ///         poll without reading the resource.</b> <see cref="ResourcePath" /> alone is an address
    ///         with <see cref="Guid.Empty" /> for its id, and the ReBAC object of a resource is its
    ///         GUID — so a check built from the path alone would fall back to the parent resource
    ///         group, which is a <i>different</i> question. Resolving the GUID from the path costs an
    ///         index-grain call per poll; the operation grain already knows it, so it says it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Appended at 13, and the number is never reused.</b> docs/plan/05 § Serialization
    ///         and schema evolution. A peer that predates this member sends nothing for it and reads
    ///         <see cref="Guid.Empty" />, which is exactly "as before" — and
    ///         <c>GetOperationAsync</c> treats <see cref="Guid.Empty" /> the way it treats an empty
    ///         path: as an operation there is nothing to authorize against, which is a <c>404</c>.
    ///     </para>
    /// </remarks>
    [Id(13)]
    public Guid ResourceId { get; init; }

    /// <summary>Whether the operation has reached a terminal state.</summary>
    public bool IsTerminal =>
        State is OperationState.Succeeded or OperationState.Failed or OperationState.Canceled;

    /// <summary>The last progress entry, or <see langword="null" /> when there is none.</summary>
    /// <remarks>
    ///     ⚠ This is what the 60-minute timeout error names — docs/plan/08 § The reconcile loop. An
    ///     operation that timed out without saying <i>where</i> it was stuck is a support ticket, not
    ///     a diagnosis.
    /// </remarks>
    public OperationProgress? LastProgress =>
        Progress.IsDefaultOrEmpty ? null : Progress[^1];
}

/// <summary>
///     The <c>resource-changed</c> event of step 11 — the input to docs/plan/08 § The resource-graph
///     projection.
/// </summary>
/// <remarks>
///     ⚠ <b>The members are the ClickHouse columns, on purpose.</b> docs/plan/08 § The resource-graph
///     projection lists them; keeping the event and the table in the same shape means the projector is
///     a copy rather than a transformation, and a transformation is where a projection drifts from its
///     source. <b>The projector itself is out of scope here</b> — this assembly emits, and
///     <see cref="IResourceChangedSink" /> is where a projector attaches.
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.ResourceManager.ResourceChangedEvent")]
public sealed record ResourceChangedEvent {
    /// <summary>What happened.</summary>
    [Id(0)]
    public ResourceChangeKind Change { get; init; } = ResourceChangeKind.Unknown;

    /// <summary>The resource GUID — the projection's <c>resource_id</c>.</summary>
    [Id(1)]
    public Guid ResourceId { get; init; }

    /// <summary>The tenant.</summary>
    [Id(2)]
    public Guid TenantId { get; init; }

    /// <summary>The subscription.</summary>
    [Id(3)]
    public Guid SubscriptionId { get; init; }

    /// <summary>The resource group's name.</summary>
    [Id(4)]
    public string ResourceGroup { get; init; } = string.Empty;

    /// <summary>The provider namespace.</summary>
    [Id(5)]
    public string Provider { get; init; } = string.Empty;

    /// <summary>The resource type path.</summary>
    [Id(6)]
    public string Type { get; init; } = string.Empty;

    /// <summary>The resource's name.</summary>
    [Id(7)]
    public string Name { get; init; } = string.Empty;

    /// <summary>The api-version the change was written at.</summary>
    [Id(8)]
    public string ApiVersion { get; init; } = string.Empty;

    /// <summary>The provisioning state after the change.</summary>
    [Id(9)]
    public ProvisioningState ProvisioningState { get; init; } = ProvisioningState.Unknown;

    /// <summary>Where the resource lives.</summary>
    [Id(10)]
    public string Location { get; init; } = string.Empty;

    /// <summary>Which cluster, or <see cref="Guid.Empty" />.</summary>
    [Id(11)]
    public Guid ClusterId { get; init; }

    /// <summary>The tags, for the projection's <c>tags Map(String,String)</c>.</summary>
    [Id(12)]
    public ImmutableDictionary<string, string> Tags { get; init; } =
        ImmutableDictionary<string, string>.Empty;

    /// <summary>When the resource was created.</summary>
    [Id(13)]
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>When this change happened.</summary>
    [Id(14)]
    public DateTimeOffset ModifiedAt { get; init; }

    /// <summary>
    ///     A SHA-256 over the desired body — the projection's <c>desired_hash</c>, and the same value
    ///     the command builder stamps as <c>cybercloud.io/reconcile-hash</c>.
    /// </summary>
    [Id(15)]
    public string DesiredHash { get; init; } = string.Empty;

    /// <summary>The resource's monotonic version, so a projector can drop a reordered event.</summary>
    [Id(16)]
    public long Version { get; init; }

    /// <summary>The stream this event belongs on — <c>cc.{tenantId:N}.res</c>, per step 11.</summary>
    public string StreamNamespace =>
        string.Create(CultureInfo.InvariantCulture, $"cc.{TenantId:N}.res");
}

/// <summary>One thing a per-cluster drift scan found. docs/plan/08 § The reconcile loop.</summary>
[GenerateSerializer]
[Alias("CyberCloud.ResourceManager.DriftFinding")]
public sealed record DriftFinding {
    /// <summary>Which of the three kinds.</summary>
    [Id(0)]
    public DriftKind Kind { get; init; } = DriftKind.Unknown;

    /// <summary>
    ///     The <c>cybercloud.io/resource-id</c> label's value, which is what makes the scan a hash
    ///     join rather than a cluster-wide scan — ADR-013, docs/plan/09 § The command builder.
    /// </summary>
    [Id(1)]
    public Guid ResourceId { get; init; }

    /// <summary>The resource's address, from the <c>cybercloud.io/resource-path</c> annotation.</summary>
    [Id(2)]
    public string ResourcePath { get; init; } = string.Empty;

    /// <summary>The cluster objects involved. Empty for a <see cref="DriftKind.Stray" />.</summary>
    [Id(3)]
    public ImmutableArray<ObjectRef> Objects { get; init; } = [];

    /// <summary>What the difference is, in words.</summary>
    [Id(4)]
    public string Detail { get; init; } = string.Empty;
}

/// <summary>What one per-cluster drift scan concluded.</summary>
[GenerateSerializer]
[Alias("CyberCloud.ResourceManager.DriftReport")]
public sealed record DriftReport {
    /// <summary>The cluster scanned.</summary>
    [Id(0)]
    public Guid ClusterId { get; init; }

    /// <summary>When.</summary>
    [Id(1)]
    public DateTimeOffset ScannedAt { get; init; }

    /// <summary>How many labelled objects the scan saw.</summary>
    [Id(2)]
    public int ObjectsSeen { get; init; }

    /// <summary>How many resource grains the scan compared against.</summary>
    [Id(3)]
    public int ResourcesSeen { get; init; }

    /// <summary>What it found. Empty when the cluster and the grains agree.</summary>
    [Id(4)]
    public ImmutableArray<DriftFinding> Findings { get; init; } = [];

    /// <summary>The orphans — labelled objects whose resource grain is gone, <b>and billed for</b>.</summary>
    public IEnumerable<DriftFinding> Orphans => Findings.Where(x => x.Kind == DriftKind.Orphan);

    /// <summary>The strays — resources whose objects vanished.</summary>
    public IEnumerable<DriftFinding> Strays => Findings.Where(x => x.Kind == DriftKind.Stray);
}
