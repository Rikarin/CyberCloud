using System.Collections.Immutable;

namespace CyberCloud.ResourceManager.Contracts;

/// <summary>
///     The one place a tenant's intent enters the platform. docs/plan/08 § The write path, end to end.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/08:
///         <i>
///             "Everything a tenant does passes through it exactly once. … Steps 3-7 are the entire
///             reason this is one component rather than a shared library each provider calls. A
///             provider that could skip step 3 is a provider that eventually will."
///         </i>
///     </para>
///     <para>
///         ⚠ <b>A service and not a grain, deliberately.</b> Every step it performs is a call to some
///         other grain, so making it a grain would add an activation, a hop and a serialization to
///         each request while owning no state of its own. It is held by the gateway, which is an
///         Orleans <i>client</i> (docs/plan/03, docs/plan/10) and therefore permanently outside
///         <c>Orleans.Multitenant</c>'s call filter — so every grain reference it takes goes through
///         <c>ForTenant</c>, which is what <c>CC1006</c> checks.
///     </para>
///     <para>
///         ⚠ <b>Every method returns the trace.</b> The ordering of the steps <i>is</i> the security
///         property, and a trace turns it into an assertion rather than a review item — see
///         <see cref="WriteTrace" />.
///     </para>
/// </remarks>
public interface IResourceManager {
    /// <summary>
    ///     The write path, all twelve steps. <c>PUT</c> and <c>PATCH</c>.
    /// </summary>
    /// <param name="request">The request, as the gateway parsed it off the URL and the body.</param>
    /// <param name="cancellationToken">Cancels the request. ⚠ Not the operation it starts.</param>
    /// <returns>
    ///     <see cref="WriteAccepted" /> — the <c>202</c> of step 12 — or the first step's failure.
    ///     ⚠ A failure at step 3 is <see cref="ErrorCode.ResourceNotFound" /> and never
    ///     <see cref="ErrorCode.AuthorizationFailed" /> when the caller cannot read the resource:
    ///     docs/plan/07 § The enforcement seam, <b>404, never 403</b>.
    /// </returns>
    Task<Result<WriteAccepted>> WriteAsync(WriteRequest request, CancellationToken cancellationToken = default);

    /// <summary>Reads a resource, projected to the requested api-version.</summary>
    /// <param name="request">
    ///     The request. <see cref="WriteRequest.Verb" /> and <see cref="WriteRequest.Body" /> are
    ///     ignored.
    /// </param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    ///     The snapshot, or <see cref="ErrorCode.ResourceNotFound" /> — for a resource that does not
    ///     exist <i>and</i> for one the caller may not see, which is the same answer on purpose.
    /// </returns>
    Task<Result<ResourceSnapshot>> ReadAsync(WriteRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    ///     The delete path — the reverse of the write path, and docs/plan/06 § Two-phase create calls
    ///     it the harder half.
    /// </summary>
    /// <param name="request">The request. <see cref="WriteRequest.Body" /> is ignored.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>
    ///     The accepted operation. ⚠ The resource stays visible in <c>Deleting</c> until teardown
    ///     converges, and stays visible <i>indefinitely</i> if teardown keeps failing.
    /// </returns>
    Task<Result<WriteAccepted>> DeleteAsync(WriteRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    ///     A <c>POST</c> action on an existing resource — <c>restart</c>, <c>rotateKeys</c>,
    ///     <c>listKeys</c>.
    /// </summary>
    /// <param name="request">
    ///     The request, with <see cref="WriteRequest.Action" /> naming the action.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>
    ///     The accepted operation, or <see cref="ErrorCode.ResourceNotFound" /> when the resource does
    ///     not exist. ⚠ <b>Never a create.</b> docs/plan/08 § The write path, end to end: <c>POST</c>
    ///     "appears only for actions on an existing resource … never for creation".
    /// </returns>
    Task<Result<WriteAccepted>> ActionAsync(WriteRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Reads a long-running operation's status. The <c>GET /operations/{opId}</c> of
    ///     docs/plan/10 § Long-running operations and docs/plan/08 § Long-running operations.
    /// </summary>
    /// <param name="operationId">The operation.</param>
    /// <param name="caller">
    ///     Who is asking. ⚠ Its tenant came from the token and selects the grain — the gateway is an
    ///     Orleans client, so <c>ForTenant</c> is the <i>only</i> thing closing the cross-tenant read
    ///     (docs/plan/00 § The tenant-separation row, corrected).
    /// </param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    ///     The status, or <see cref="ErrorCode.ResourceNotFound" /> — for an operation that does not
    ///     exist, one belonging to another tenant, and one whose resource the caller may not read,
    ///     which are the same answer on purpose. docs/plan/07 § The enforcement seam: <b>404, never
    ///     403</b>.
    /// </returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This exists because polling is the hot path and the alternative cost a full
    ///         resource read per poll.</b> docs/plan/10 § Long-running operations gives the endpoint;
    ///         this interface had no method for it, so the gateway built its own reader — correctly
    ///         refusing to decide authorization itself, and therefore asking the only question it
    ///         could: <see cref="ReadAsync" /> on the operation's resource. That is an index-grain
    ///         resolve, a check, a resource-grain read and an api-version projection, on every poll of
    ///         a nine-minute cluster create that <c>cyc --wait</c> polls continuously.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The gateway still decides nothing, and that property is the constraint this method
    ///         was written under.</b> The check happens here, in the one seam docs/plan/07 § The
    ///         enforcement seam names, against the same <see cref="IResourceAuthorizer" /> and the same
    ///         read permission a <c>GET</c> of the resource would use. What the gateway gains is one
    ///         call instead of two; what it does not gain is a decision.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Not a <see cref="WriteRequest" />, because there is no path and no api-version.</b>
    ///         An operation is addressed by its GUID alone — docs/plan/10's URL is
    ///         <c>/operations/{opId}</c> — and the api-version on that URL selects the response
    ///         <i>rendering</i>, which is the gateway's job, not a projection of stored state.
    ///     </para>
    /// </remarks>
    Task<Result<OperationStatus>> GetOperationAsync(
        Guid operationId,
        CallerContext caller,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
///     Resolves the effective lock at a scope — step 4 of docs/plan/08 § The write path, end to end.
/// </summary>
/// <remarks>
///     ⚠ <b>A seam rather than a walk, because the walk crosses assemblies.</b> The inheritance chain
///     is resource → resource group → subscription → management group. The shipped
///     <c>ResourceScopeLockResolver</c> walks the first three and takes the strongest lock found;
///     <b>the management group is not walked because there is nothing to walk</b> — docs/plan/06
///     § The hierarchy makes that tree optional and docs/plan/01 puts it at M2, so no grain, no key
///     and no parent pointer exist. A lock at that level cannot be set, let alone missed.
/// </remarks>
public interface ILockResolver {
    /// <summary>The strongest lock in force at a resource's scope, inherited included.</summary>
    /// <param name="id">The resource, or the address of one that does not exist yet.</param>
    /// <param name="cancellationToken">Cancels the walk.</param>
    /// <returns>
    ///     The strongest of the locks set on the resource, its group and its subscription. ⚠
    ///     "Strongest" is <c>LockLevels.Strongest</c> and is <b>not</b> the enum's numeric maximum:
    ///     <see cref="LockLevel.ReadOnly" /> outranks <see cref="LockLevel.CanNotDelete" /> while
    ///     carrying the smaller value.
    /// </returns>
    Task<Result<LockLevel>> ResolveAsync(ResourceId id, CancellationToken cancellationToken = default);
}

/// <summary>What policy evaluation decided about one write. Step 5.</summary>
/// <param name="Effect">Allow, deny, modify, audit — or <see cref="PolicyEffect.NotSupported" />.</param>
/// <param name="Error">
///     Why, for <see cref="PolicyEffect.Deny" />. ⚠ Carries <see cref="ErrorCode.PolicyViolation" />
///     and a <c>target</c> pointing at the offending field.
/// </param>
/// <param name="ModifiedBody">
///     The rewritten body, for <see cref="PolicyEffect.Modify" />, as JSON text. ⚠ Re-validated
///     against the schema before step 6 — a policy that produced an invalid body would otherwise reach
///     the provider unchecked.
/// </param>
public readonly record struct PolicyDecision(
    PolicyEffect Effect,
    Error? Error = null,
    string? ModifiedBody = null
) {
    /// <summary>The decision a platform with no policy engine makes.</summary>
    public static PolicyDecision NotSupported { get; } = new(PolicyEffect.NotSupported);

    /// <summary>Whether the write may proceed.</summary>
    public bool Permits => Effect != PolicyEffect.Deny;
}

/// <summary>
///     Step 5 of docs/plan/08 § The write path, end to end — deny, modify, audit.
/// </summary>
/// <remarks>
///     ⚠ <b>The seam exists and the engine does not.</b> Policy is M3. The step is in the write path,
///     in the right place, from the start, and the registered default returns
///     <see cref="PolicyEffect.NotSupported" /> — which the write path treats as "carry on" and
///     records in the trace. That way the day a real evaluator lands, nothing about the ordering has
///     to move, and the ordering is the thing that must not move.
/// </remarks>
public interface IPolicyEvaluator {
    /// <summary>Evaluates policy for one write.</summary>
    /// <param name="id">The resource being written.</param>
    /// <param name="apiVersion">The api-version the body is at.</param>
    /// <param name="body">The body, as JSON text.</param>
    /// <param name="caller">Who is asking.</param>
    /// <param name="cancellationToken">Cancels the evaluation.</param>
    Task<PolicyDecision> EvaluateAsync(
        ResourceId id,
        string apiVersion,
        string body,
        CallerContext caller,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
///     Where step 11's <c>resource-changed</c> events go.
/// </summary>
/// <remarks>
///     ⚠ <b>The projector is out of scope and this is the seam it attaches to.</b>
///     docs/plan/08 § The resource-graph projection routes these to a per-tenant ClickHouse table via
///     a projector; nothing here writes to ClickHouse and nothing here should. What is implemented is
///     the <i>emission</i> — the event is built with the projection's columns and published at step
///     10, so a projector is a consumer rather than a change to the write path.
/// </remarks>
public interface IResourceChangedSink {
    /// <summary>Publishes one change.</summary>
    /// <param name="change">The event, carrying the projection's columns.</param>
    /// <param name="cancellationToken">Cancels the publish.</param>
    /// <returns>
    ///     Success, or a failure. ⚠ The write path <b>does not fail a request</b> when this fails: the
    ///     projection is eventually consistent by design (docs/plan/08 § The resource-graph projection)
    ///     and refusing a create because a list view will lag would trade a correct write for a
    ///     cosmetic one.
    /// </returns>
    Task<Result> PublishAsync(ResourceChangedEvent change, CancellationToken cancellationToken = default);
}

/// <summary>
///     Where the resource manager asks whether a caller may do something. docs/plan/07 § The
///     enforcement seam.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>An interface here rather than a direct <c>ICheckGrain</c> call, and the reason is the
///         assembly graph.</b> <c>CyberCloud.ResourceManager.Contracts</c> deliberately does not
///         reference <c>CyberCloud.Authorization.Contracts</c>, so that a provider referencing this
///         assembly cannot name <c>ICheckGrain</c> — docs/plan/07 § The enforcement seam,
///         <i>"Providers never call the engine."</i> The implementation lives in
///         <c>CyberCloud.ResourceManager</c>, which does reference it, and is the <b>one</b>
///         implementation in the platform.
///     </para>
///     <para>
///         ⚠ <b>The two-permission shape is what makes 404-versus-403 decidable.</b> A single
///         "allowed?" answer cannot distinguish "no such resource" from "you may look but not touch",
///         and docs/plan/07 § The enforcement seam requires exactly that distinction: 404 when the
///         caller cannot read, 403 only when they can read but not act, because a 403 on an unreadable
///         resource is an enumeration oracle.
///     </para>
/// </remarks>
public interface IResourceAuthorizer {
    /// <summary>Whether a caller may perform an action on a resource, and what to say if not.</summary>
    /// <param name="id">The resource, or the address of one that does not exist yet.</param>
    /// <param name="actionPermission">The permission the verb needs, from the registry.</param>
    /// <param name="readPermission">
    ///     The permission a read needs. ⚠ Consulted only when the action is refused, to choose between
    ///     <c>404</c> and <c>403</c>.
    /// </param>
    /// <param name="caller">Who is asking.</param>
    /// <param name="fullyConsistent">
    ///     Whether to bypass every cache. docs/plan/07 § Consistency: <c>true</c> for
    ///     <i>"deletion, key export, billing changes, anything where a stale allow is a real
    ///     incident"</i>.
    /// </param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>
    ///     Success when allowed. On refusal, <see cref="ErrorCode.ResourceNotFound" /> when the caller
    ///     cannot read, and <see cref="ErrorCode.AuthorizationFailed" /> when they can.
    /// </returns>
    Task<Result> AuthorizeAsync(
        ResourceId id,
        string actionPermission,
        string readPermission,
        CallerContext caller,
        bool fullyConsistent = false,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
///     Where the resource manager records a resource's place in the authorization hierarchy —
///     the <c>parent</c> edge docs/plan/07 § The model's <c>From("parent", …)</c> rewrites follow.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This exists because the write path used to have no step that wrote a tuple, and a
///         create therefore produced a resource its own creator could not read.</b>
///         <c>CyberCloudSchema</c> gives a resource
///         <c>Role(owner, This | From(parent, owner))</c>, so the only thing that makes a resource
///         reachable from the role assignments on its group is a
///         <c>resource:{id}#parent@resourceGroup:{sub}-{rg}</c> tuple. Nobody wrote one. The
///         isolation suite wrote its own and said so in its remarks; docs/plan/08 § The write path,
///         end to end now has the step, and this is the seam it calls.
///     </para>
///     <para>
///         ⚠ <b>An interface here rather than an <c>ITupleStoreGrain</c> call, for the same reason
///         <see cref="IResourceAuthorizer" /> is an interface.</b>
///         <c>CyberCloud.ResourceManager.Contracts</c> deliberately does not reference
///         <c>CyberCloud.Authorization.Contracts</c>, so a provider referencing this assembly cannot
///         name a tuple type — docs/plan/07 § The enforcement seam, <i>"Providers never call the
///         engine."</i> A provider that could <i>write</i> tuples would be worse than one that could
///         read them.
///     </para>
///     <para>
///         ⚠ <b>The parent is the resource GROUP and not the subscription, and
///         <c>CyberCloudSchema</c> is what decides that.</b> Its rewrite chain is
///         resource → resourceGroup → subscription → tenant: pointing a resource's <c>parent</c> at
///         the subscription would skip the group, and every <c>resourceGroup:…#contributor</c>
///         assignment — the second row of docs/plan/07 § Azure RBAC, expressed in it — would grant
///         nothing on the resources inside it.
///     </para>
/// </remarks>
public interface IResourceRelationWriter {
    /// <summary>
    ///     Records <c>resource:{id}#parent@resourceGroup:{subscription}-{group}</c>. Idempotent.
    /// </summary>
    /// <param name="id">
    ///     The resource, with <see cref="ResourceId.Id" /> set. ⚠ The GUID is minted at the quota step
    ///     and the name is claimed at the index step, both <i>before</i> durable state exists — which
    ///     is what lets this run before the resource does.
    /// </param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>
    ///     Success, or the failure that stopped it. ⚠ The write path <b>fails the request</b> on a
    ///     failure here, which it can only do honestly because the call happens before
    ///     <c>SubmitDesiredAsync</c> — see the write path's step 8.
    /// </returns>
    Task<Result> LinkToParentAsync(ResourceId id, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Removes the <c>parent</c> tuple. Idempotent — removing one that is not there succeeds.
    /// </summary>
    /// <param name="id">The resource, with <see cref="ResourceId.Id" /> set.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <remarks>
    ///     ⚠ <b>Called when the resource is <i>gone</i>, not when a delete is requested.</b> A
    ///     resource being torn down stays visible in <c>Deleting</c> (docs/plan/06 § Two-phase create)
    ///     and its owner has to be able to watch that happen, so unlinking at the request would blind
    ///     them to their own delete. <c>OperationGrain</c> calls this after
    ///     <c>CompleteDeleteAsync</c>, and retries it from its reminder if it fails — a tuple pointing
    ///     at an object that no longer exists is a slow leak in the tenant's tuple store.
    /// </remarks>
    Task<Result> UnlinkFromParentAsync(ResourceId id, CancellationToken cancellationToken = default);
}

/// <summary>
///     The labelled objects a cluster currently holds, for the per-cluster drift scan.
/// </summary>
/// <remarks>
///     ⚠ <b>This is the seam the drift scan needs and cannot have yet.</b>
///     docs/plan/08 § The reconcile loop makes drift detection per-cluster and sources it from the
///     connection grain's live informer bridge — <i>"an hourly per-cluster reminder diffs labelled
///     objects against the resource grains that own them"</i>. The <i>diff</i> is pure and is
///     implemented and tested; the <i>inventory</i> needs a live informer against a real API server,
///     which no in-process test has. So the inventory is this interface, the shipped implementation
///     is one that reports nothing and says so, and a cluster-backed one is owed.
/// </remarks>
public interface IClusterObjectInventory {
    /// <summary>
    ///     Every object in a cluster carrying <c>cybercloud.io/managed-by=cybercloud</c>, keyed by the
    ///     <c>cybercloud.io/resource-id</c> label.
    /// </summary>
    /// <param name="clusterId">The cluster to inventory.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    ///     The labelled objects. ⚠ An <i>empty</i> result and a <i>failed</i> result mean very
    ///     different things to the scan: empty says every resource is a stray, failed says do not
    ///     conclude anything. A stub therefore fails rather than returning empty.
    /// </returns>
    Task<Result<ImmutableArray<ClusterObjectRecord>>> ListManagedAsync(
        Guid clusterId,
        CancellationToken cancellationToken = default
    );
}

/// <summary>One labelled object as the drift scan sees it.</summary>
/// <param name="ResourceId">
///     The <c>cybercloud.io/resource-id</c> label — ADR-013's hash-join key
///     (docs/plan/09 § The command builder).
/// </param>
/// <param name="ResourcePath">The <c>cybercloud.io/resource-path</c> annotation.</param>
/// <param name="ReconcileHash">The <c>cybercloud.io/reconcile-hash</c> annotation.</param>
/// <param name="Target">Which object.</param>
public readonly record struct ClusterObjectRecord(
    Guid ResourceId,
    string ResourcePath,
    string ReconcileHash,
    ObjectRef Target
);
