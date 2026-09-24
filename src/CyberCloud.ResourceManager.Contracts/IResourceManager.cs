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
///         ⚠ <b>Every method that runs the twelve steps returns the trace.</b> The ordering of the
///         steps <i>is</i> the security property, and a trace turns it into an assertion rather than
///         a review item — see <see cref="WriteTrace" />. This summary read "every method" until
///         2026-09-02 and that was never true: <see cref="WriteTrace" /> lives on
///         <see cref="WriteAccepted" /> and on nothing else, so <see cref="ReadAsync" />,
///         <see cref="ListAsync" /> and <see cref="GetOperationAsync" /> return none — correctly,
///         because none of them runs a step that writes. The sentence mattered because a new read
///         method written to satisfy it would either invent a trace nobody asserts on or carry a
///         wire type through a path with nothing to record.
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

    /// <summary>
    ///     The write path entered by a parent operation on behalf of the caller it recorded — all
    ///     twelve steps, with that caller's subject in step 3, and the new operation made a child of
    ///     the parent. <c>PUT</c> only.
    /// </summary>
    /// <param name="parentOperationId">
    ///     The parent operation. Recorded as <see cref="OperationSpec.ParentOperationId" /> on the
    ///     child, which is what lets the child tell the parent when it ends.
    /// </param>
    /// <param name="request">
    ///     The child's write. <see cref="WriteRequest.Caller" /> is the parent's
    ///     <see cref="OperationSpec.Caller" />, unchanged — the one identity the gateway authenticated
    ///     when the parent was accepted.
    /// </param>
    /// <param name="cancellationToken">Cancels the request. ⚠ Not the operation it starts.</param>
    /// <returns>
    ///     <see cref="WriteAccepted" />, or the first step's refusal exactly as
    ///     <see cref="WriteAsync" /> would give it to that caller — so a child the caller may not
    ///     write is <see cref="ErrorCode.ResourceNotFound" /> or <see cref="ErrorCode.AuthorizationFailed" />,
    ///     never a success. Refused outright for an empty parent, an empty subject, a verb other
    ///     than <see cref="WriteVerb.Put" />, and a child that is itself a deployment; and, before
    ///     step 1, with <see cref="ErrorCode.TenantSuspended" /> for a tenant that no longer takes
    ///     control-plane writes and <see cref="ErrorCode.AuthorizationFailed" /> for an impersonated
    ///     caller or a principal that may no longer act.
    /// </returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>THE ARGUMENT FOR WRITING AS A RECORDED CALLER, WRITTEN DOWN.</b> A deployment's
    ///         children are written long after the request that asked for them has returned, by a
    ///         grain, from a reminder. Nothing on that path carries a token. What it carries is the
    ///         <see cref="CallerContext" /> the gateway built from one when the deployment's own
    ///         <c>PUT</c> passed step 3 at the resource group — persisted in the parent's
    ///         <see cref="OperationSpec.Caller" /> and never re-derived. Replaying that identity is
    ///         safe for four reasons, and each is a property of this method rather than of its
    ///         caller's care:
    ///     </para>
    ///     <list type="number">
    ///         <item>
    ///             <b>What the gateway would have refused before step 3 is asked again, now.</b> A
    ///             direct request meets the gateway's stages before it reaches this service, and a
    ///             child meets none of them, so this argument used to cover ReBAC alone. Three gates
    ///             sat in those stages and nowhere else, and a child now meets each before step 1:
    ///             <list type="bullet">
    ///                 <item>
    ///                     The tenant's status, read from <c>ITenantDirectoryGrain</c> — the record
    ///                     <c>ResolveTenantStage</c> reads a mirror of — and refused with
    ///                     <see cref="ErrorCode.TenantSuspended" /> unless <c>Active</c> or
    ///                     <c>Warned</c> (docs/plan/06 § Tenant lifecycle).
    ///                 </item>
    ///                 <item>
    ///                     The principal's own status, through <c>IPrincipalStanding</c>: a user must be
    ///                     active, a service principal enabled, and a managed identity bound. A
    ///                     suspension revokes the user's sessions, so they can't renew a token for the
    ///                     gateway, but leaves their role tuples, so step 3 alone still allowed them.
    ///                 </item>
    ///                 <item>
    ///                     Impersonation, refused outright. The sixty-minute box
    ///                     (docs/plan/06 § Platform administration) is the operator's grant, which no
    ///                     spec records, so a child can't tell a live one from one that has run out.
    ///                 </item>
    ///             </list>
    ///             <para>
    ///                 ⚠ <b>Before the review of #39 none of these was asked.</b> A tenant suspended
    ///                 by billing, or a compromised account an administrator suspended, mid-way
    ///                 through a hundred-resource template had every remaining child created as
    ///                 them. <c>DeploymentTests.ATenantSuspendedBetweenTwoChildrenStopsTheDeploymentAtTheSecond</c>
    ///                 and
    ///                 <c>DeploymentAuthorizationTests.ACreatorSuspendedBetweenTwoChildrenIsRefusedAtTheSecond</c>
    ///                 hold the line.
    ///             </para>
    ///             <para>
    ///                 ⚠ <b>The rate limiter is the one stage that isn't repeated, deliberately.</b> It
    ///                 charged the deployment's own <c>PUT</c>, and a child can't multiply that
    ///                 charge without bound: <c>DeploymentLimits.MaxResources</c> caps a template at a
    ///                 hundred children, they're written one at a time, and each waits for its
    ///                 predecessor's operation to end. Quota, the budget that matters for what a
    ///                 child creates, is step 6 of every child. The counters are
    ///                 <c>CyberCloud.ServiceDefaults</c>' and this module has no edge to it;
    ///                 docs/plan/08 § Long-running operations records charging children as owed.
    ///             </para>
    ///         </item>
    ///         <item>
    ///             <b>Every child is checked at its own scope, now, against the durable rows.</b> This is
    ///             <see cref="WriteAsync" />'s body: step 3 runs the caller's subject against the child's
    ///             address — its group, or the child itself on an update — at the moment the child is
    ///             written, and for a child it runs <c>FullyConsistent</c>. A deployment therefore grants
    ///             nothing its creator does not hold at each child, and a right revoked between two
    ///             children is honoured at the second.
    ///             <para>
    ///                 ⚠ <b>"Now" was not true at <c>MinimizeLatency</c>, which every other write uses.</b>
    ///                 <c>CheckGrain</c> answers that mode from any cached entry with no TTL, and the
    ///                 deployment's own <c>PUT</c> has just cached an allow for its creator at the group —
    ///                 so a revoked creator went on writing children as themselves, from a reminder, until
    ///                 the template ran out (the review of #39 saw exactly that, and
    ///                 <c>DeploymentAuthorizationTests.ARightRevokedBetweenTwoChildrenIsHonouredAtTheSecond</c>
    ///                 now holds the line). The same bypass lets a grant made after a refused child reach
    ///                 the rerun, where a cached deny used to answer instead.
    ///             </para>
    ///         </item>
    ///         <item>
    ///             <b>There is no platform identity to fall back to.</b> The platform has no system
    ///             principal (see <see cref="ExpiredPurgeRequest" />), so the only subject a write
    ///             can carry is a tenant's; an empty subject is refused here rather than handed to
    ///             step 3 to deny, so a spec that lost its caller fails loudly instead of looking like
    ///             a permissions problem.
    ///         </item>
    ///         <item>
    ///             <b>The gateway cannot reach it.</b> A request's caller is always the token's; this
    ///             method is the one way to name a caller that is not the current request's, and it is
    ///             not in the gateway's dispatch (<c>GatewayIsolationTests</c> reads the source for
    ///             it). Its one production caller is <c>DeploymentDriver</c>, passing its own spec's
    ///             caller.
    ///         </item>
    ///     </list>
    ///     <para>
    ///         ⚠ <b>Impersonation doesn't travel with it any more.</b> This paragraph used to say
    ///         <see cref="CallerContext.ImpersonatedBy" /> reached every child and its audit line, which
    ///         was true and was the problem: a deployment made the operator's time-boxed session last
    ///         as long as the template did. A deployment created under impersonation is accepted, and
    ///         its first child is refused naming the operator, which fails it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It does not ask the parent whether it exists.</b> The parent is the grain calling
    ///         this, inside its own turn, and a call back into a non-reentrant grain from within its
    ///         turn is a deadlock rather than a check. The parent id is recorded, and a child whose
    ///         parent is gone ends normally and tells nobody.
    ///     </para>
    /// </remarks>
    Task<Result<WriteAccepted>> WriteChildAsync(
        Guid parentOperationId,
        WriteRequest request,
        CancellationToken cancellationToken = default
    );

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
    ///     Lists one resource type inside one resource group — the collection <c>GET</c>.
    /// </summary>
    /// <param name="request">The request, carrying a <c>ResourceCollectionId</c> path.</param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>
    ///     A page of the resources the caller may read. ⚠ An empty page and a page short of
    ///     <see cref="ListRequest.PageSize" /> both mean "that is what you may see", never "that is
    ///     all there is" — see <see cref="ResourceListPage" />.
    /// </returns>
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             The filter is not optional, and it is one <c>ListObjects</c> per page with a
    ///             <c>Check</c> per member as the fallback.
    ///         </b> Without it a listing is a way to read the
    ///         names of resources the caller has no permission on — which is precisely the
    ///         enumeration oracle § The enforcement seam answers <c>404</c> to prevent one resource
    ///         at a time, handed back wholesale. docs/plan/07 § ListObjects is built (issue #37):
    ///         <see cref="IResourceAuthorizer.ListReadableAsync" /> asks the engine which resources
    ///         under the object the page's members hang off this caller may read, and the page is
    ///         intersected with the answer.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What it used to cost, and what the fallback still costs.</b> One <c>ICheckGrain</c>
    ///         call per member examined, each to a distinct activation keyed on that resource's GUID,
    ///         plus one resource read per member that survives the filter — a cold group paid an
    ///         activation and a hot-tier state read per member. The walk reads the caller's own
    ///         index, the groups they are in and the chain down to the group, and touches no
    ///         member's grain. That path is taken again whenever the engine declines to answer —
    ///         a walk past its object cap, a store that is down — because "nothing readable" is the
    ///         one answer a listing may never fake. <see cref="ListRequest.MaxPageSize" /> stays a
    ///         cap rather than a hint for the same reason it was one: it bounds the fallback, and
    ///         it bounds the resource reads that follow the filter either way.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The group's membership is the enumeration source, and nothing else can be.</b>
    ///         <c>IResourceIndexGrain</c> is path→GUID and one-way; the resource-graph projection is
    ///         eventually consistent by design (docs/plan/08 § The resource-graph projection) and a
    ///         listing built on it would answer for a state that has not happened yet.
    ///         <c>IResourceGroupGrain.ListAsync</c> is the one activation that already serialises
    ///         "what is in this group", and it deliberately includes members in
    ///         <c>ProvisioningState.Deleting</c> — docs/plan/06 § Two-phase create keeps a resource
    ///         whose teardown failed visible <i>because</i> its pods still run and its meter still
    ///         ticks.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             It does not run the twelve steps and does not carry a <see cref="WriteTrace" />,
    ///             which is the same shape <see cref="ReadAsync" /> has.
    ///         </b> Steps 4 to 11 write, and a
    ///         listing writes nothing: there is no body to validate, no lock to resolve (a
    ///         <c>ReadOnly</c> lock does not hide a resource), no policy to evaluate, no quota, no
    ///         index claim and no operation. What it does keep is the part of step 1 that is a
    ///         security property — the tenant and subscription ownership checks, in that order,
    ///         before the registry is consulted — and step 3, once per member.
    ///     </para>
    /// </remarks>
    Task<Result<ResourceListPage>> ListAsync(ListRequest request, CancellationToken cancellationToken = default);

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
    ///     Brings a soft-deleted resource back to its old address — docs/plan/08 § Soft delete.
    /// </summary>
    /// <param name="request">The request. <see cref="WriteRequest.Body" /> is ignored.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>
    ///     The accepted operation, or <see cref="ErrorCode.ResourceNotFound" /> — for a name that holds
    ///     no soft-deleted resource, for one whose recovery window has passed, and for one the caller
    ///     may not see, which are the same answer on purpose.
    /// </returns>
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             A long-running operation, and it used to answer synchronously because it did
    ///             nothing.
    ///         </b> The old contract said a restore "does no data-plane work at all: the
    ///         volumes, the PVCs and the memory were never released" — which was true, and was true
    ///         because a soft delete ran no teardown and left the tenant's pods running behind a name
    ///         that answered <c>404</c>. A soft delete now tears the data plane down like any other
    ///         delete, so a restore applies the stored desired state again through
    ///         <see cref="OperationKind.Restore" />, and applying fifteen Kubernetes objects is not
    ///         something to hold a request open for.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What the window preserves is everything a teardown does not remove</b>: the name,
    ///         the resource's stored body, the committed quota, and the volumes — deleting a
    ///         <c>StatefulSet</c> leaves the claims its <c>volumeClaimTemplate</c> made. That is why a
    ///         restore reserves nothing and re-derives nothing: docs/plan/08 § Soft delete keeps the
    ///         quota committed exactly so a restore "cannot fail against an allowance the tenant has
    ///         spent in the meantime".
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Direct role assignments do not come back</b>, and that is the decision rather than a
    ///         limitation — see <see cref="IResourceRelationWriter.DropDirectRoleAssignmentsAsync" />.
    ///     </para>
    /// </remarks>
    Task<Result<WriteAccepted>> RestoreAsync(WriteRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Ends a recovery window early: tears the resource down, releases its name and returns its
    ///     committed quota. Irreversible.
    /// </summary>
    /// <param name="request">The request. <see cref="WriteRequest.Body" /> is ignored.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The accepted operation, or the refusal.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             It checks <c>ResourceTypeRegistration.PurgePermission</c> and not the delete
    ///             permission.
    ///         </b> docs/plan/08 § Soft delete: Azure puts
    ///         <c>deletedVaults/purge/action</c> in Key Vault Contributor's <c>notActions</c>, so "may
    ///         delete" and "may destroy permanently" are separable rights. Checking the delete
    ///         permission here would mean the window protected against nobody who could already delete
    ///         — which is everybody it exists to protect against.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>This is the half of the delete that a soft-deletable type deferred</b>: the
    ///         data-plane teardown, the index release and the committed-quota return all happen here
    ///         and none of them happened at the <c>DELETE</c>.
    ///     </para>
    /// </remarks>
    Task<Result<WriteAccepted>> PurgeAsync(WriteRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Ends a recovery window that has already ended. The clock-driven half of purge, with no
    ///     caller and no <c>Check</c>.
    /// </summary>
    /// <param name="request">The parked resource's path and the api-version it is stored under.</param>
    /// <param name="cancellationToken">Cancels the request. ⚠ Not the operation it starts.</param>
    /// <returns>
    ///     The accepted operation, or the canonical absence for a path that holds no parked resource
    ///     <i>and</i> for one whose window has not ended yet — which are deliberately the same
    ///     answer.
    /// </returns>
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             THIS IS THE DECISION docs/plan/08 § Soft delete DEFERRED TO
    ///             docs/plan/07 § Azure RBAC, AND IT IS THE SECOND OF THE TWO SHAPES THAT SECTION
    ///             OFFERED.
    ///         </b>
    ///         <i>
    ///             "An expiry is not a request, so there is nobody to authorize it, and
    ///             <see cref="PurgeAsync" /> checks <c>PurgePermission</c> against a caller. Either the
    ///             platform gains a system principal, or the purge splits into an authorized front and a
    ///             mechanism the clock may drive."
    ///         </i> This is the mechanism. There is no system
    ///         principal, and the reason is on <see cref="ExpiredPurgeRequest" />.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What it does NOT inherit from the front, and why each one.</b> The
    ///         <b>permission</b> is gone because there is no subject to check it against — its place
    ///         is taken by <c>IResourceIndexGrain.ResolveExpiredAsync</c>, a precondition nothing can
    ///         be granted. <b>Purge protection</b> is gone because the flag does not mean what
    ///         refusing here would make it mean: <see cref="PurgeAsync" />'s own refusal says the
    ///         resource <i>"cannot be purged before its recovery window ends"</i> and the write
    ///         path's says <i>"wait for the recovery window to end"</i>, so a protected resource
    ///         whose window has ended is exactly the case the flag was always going to release.
    ///         Refusing it here would turn an opt-in that cannot be turned off into a resource that
    ///         can never be destroyed, which is not a protection anybody chose.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The lock check IS inherited, and that asymmetry is the point.</b> A
    ///         <c>CanNotDelete</c> lock is a tenant's standing refusal of destruction, written
    ///         deliberately and visible in their own portal. A clock must not overrule it: the window
    ///         ends, the mechanism refuses, and the resource stays parked until somebody removes the
    ///         lock. That is a resource held past its window — which is what this member exists to
    ///         stop — and it is held by a decision its owner made and can see, which is the
    ///         difference.
    ///     </para>
    /// </remarks>
    Task<Result<WriteAccepted>> PurgeExpiredAsync(
        ExpiredPurgeRequest request,
        CancellationToken cancellationToken = default
    );

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
    ///     which are the same answer on purpose. docs/plan/07 § The enforcement seam:
    ///     <b>
    ///         404, never
    ///         403
    ///     </b>.
    /// </returns>
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             This exists because polling is the hot path and the alternative cost a full
    ///             resource read per poll.
    ///         </b> docs/plan/10 § Long-running operations gives the endpoint;
    ///         this interface had no method for it, so the gateway built its own reader — correctly
    ///         refusing to decide authorization itself, and therefore asking the only question it
    ///         could: <see cref="ReadAsync" /> on the operation's resource. That is an index-grain
    ///         resolve, a check, a resource-grain read and an api-version projection, on every poll of
    ///         a nine-minute cluster create that <c>cyc --wait</c> polls continuously.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             The gateway still decides nothing, and that property is the constraint this method
    ///             was written under.
    ///         </b> The check happens here, in the one seam docs/plan/07 § The
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

/// <summary>What policy evaluation decided about one request. Step 5.</summary>
/// <remarks>
///     ⚠ <b>Not a wire type.</b> It lives in the process that runs the write path — the gateway — and
///     is built from the <see cref="PolicyEvaluation" /> the catalog grain returned, which is.
/// </remarks>
public sealed record PolicyDecision {
    /// <summary>The decision a platform with no policy engine makes.</summary>
    public static PolicyDecision NotSupported { get; } = new() { Effect = PolicyEffect.NotSupported };

    /// <summary>
    ///     The strongest thing that happened: <see cref="PolicyEffect.Deny" />, else
    ///     <see cref="PolicyEffect.Modify" /> when a rewrite was made, else
    ///     <see cref="PolicyEffect.Audit" /> when an audit recorded a non-compliant verdict, else
    ///     <see cref="PolicyEffect.Allow" /> — or <see cref="PolicyEffect.NotSupported" /> when no engine
    ///     ran.
    /// </summary>
    public PolicyEffect Effect { get; init; } = PolicyEffect.NotSupported;

    /// <summary>
    ///     Why, for <see cref="PolicyEffect.Deny" />. ⚠ <see cref="ErrorCode.PolicyViolation" />, naming
    ///     the assignment and the definition in its message and again in its two details, with the
    ///     rule's first body pointer as its <c>target</c>.
    /// </summary>
    public Error? Error { get; init; }

    /// <summary>
    ///     The rewrites to make on the body the write sends, in order. ⚠ The write path makes them and
    ///     then validates the result against the schema again — a policy that produced an invalid body
    ///     would otherwise reach the provider unchecked.
    /// </summary>
    public ImmutableArray<PolicyModificationRecord> Modifications { get; init; } = [];

    /// <summary>What the write's trace records at step 5 — every assignment that applied.</summary>
    public ImmutableArray<PolicyTraceEntry> Trace { get; init; } = [];

    /// <summary>The audit verdicts, to record once the write is accepted.</summary>
    public ImmutableArray<PolicyStateRecord> States { get; init; } = [];

    /// <summary>
    ///     Whether <see cref="IPolicyEvaluator.RecordComplianceAsync" /> has anything to do: verdicts to
    ///     record, or old ones an empty set must clear.
    /// </summary>
    public bool RecordsCompliance { get; init; }

    /// <summary>Whether the write may proceed.</summary>
    public bool Permits => Effect != PolicyEffect.Deny;
}

/// <summary>One request, as step 5 hands it to <see cref="IPolicyEvaluator" />.</summary>
/// <remarks>
///     ⚠ <b><see cref="Document" /> is the body as the write would leave the resource</b> — a
///     <c>PUT</c>'s body, a <c>PATCH</c> merged onto what is stored, the stored body for a
///     <c>DELETE</c> or an action — with the type's secret properties removed. A condition over a
///     password would be an oracle for it: an audit's verdict is readable by anyone with
///     <c>read</c> on the scope, one bit per rule, and the rule is the owner's to write.
/// </remarks>
public sealed record PolicyEvaluationRequest {
    /// <summary>The resource, with its GUID once it has one.</summary>
    public ResourceId Id { get; init; }

    /// <summary>One of <c>create</c>, <c>update</c>, <c>delete</c>, <c>action</c> — <c>PolicyOperations</c>.</summary>
    public string Operation { get; init; } = string.Empty;

    /// <summary>The action's name, for an action.</summary>
    public string Action { get; init; } = string.Empty;

    /// <summary>The body as the write would leave it, secrets removed, as JSON text — see the remarks.</summary>
    public string Document { get; init; } = "{}";

    /// <summary>The subscription's management group as step 1 read it, or empty.</summary>
    public string ManagementGroup { get; init; } = string.Empty;

    /// <summary>Who is asking.</summary>
    public CallerContext Caller { get; init; } = new();
}

/// <summary>
///     Step 5 of docs/plan/08 § The write path, end to end — deny, modify, audit.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The seam that stood here from the start now has an engine behind it</b> — the tenant's
///         <see cref="IPolicyCatalogGrain" />, through <c>CatalogPolicyEvaluator</c> (issue #46). The
///         step did not move to receive it: it was placed between the locks and the quota when it did
///         nothing, precisely so that the day it did something the order would already be right.
///     </para>
///     <para>
///         ⚠ <b>Every write kind enters it</b> — a <c>PUT</c>, a <c>PATCH</c>, a <c>DELETE</c> and an
///         action — and a provider cannot skip it, because no provider is reached before it: the
///         reconciler runs from the operation grain, which step 10 starts, and a synchronous action's
///         handler runs after it. <c>PolicyEnforcementTests.EveryWriteKindEntersStepFiveAndADenyStopsEachOne</c>
///         drives all five shapes through real grains.
///     </para>
///     <para>
///         ⚠ <b>Every write kind the issue names, not every write method.</b> <c>RestoreAsync</c>,
///         <c>PurgeAsync</c> and <c>PurgeExpiredAsync</c> don't enter step 5, so a restore brings back
///         a body that a deny assigned since would refuse. docs/plan/08 § Policy records that as a
///         decision still owed rather than a default.
///     </para>
/// </remarks>
public interface IPolicyEvaluator {
    /// <summary>Evaluates every assignment that applies to one request.</summary>
    /// <param name="request">The request and the body it would leave.</param>
    /// <param name="cancellationToken">Cancels the evaluation.</param>
    /// <returns>
    ///     The decision. ⚠ An evaluator that <i>cannot</i> answer — the catalog unreachable — denies,
    ///     naming the reason: a write that slipped past a deny rule because the rule's store was down
    ///     is the enforcement failing open.
    /// </returns>
    Task<PolicyDecision> EvaluateAsync(PolicyEvaluationRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Records the audit verdicts of a write that was accepted. Called after step 9 and only when
    ///     <see cref="PolicyDecision.RecordsCompliance" /> is set.
    /// </summary>
    /// <param name="id">The resource, with its GUID.</param>
    /// <param name="decision">The decision step 5 returned for this write.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>
    ///     Success or the failure. ⚠ The write path logs a failure and does not fail the request: the
    ///     resource is already durable, and a <c>500</c> for a create that succeeded is the worse lie.
    /// </returns>
    Task<Result> RecordComplianceAsync(ResourceId id, PolicyDecision decision, CancellationToken cancellationToken = default);

    /// <summary>Forgets a resource's verdicts. Called when its delete is accepted.</summary>
    /// <param name="id">The resource.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task<Result> ForgetAsync(ResourceId id, CancellationToken cancellationToken = default);
}

/// <summary>
///     Where step 11's <c>resource-changed</c> events go.
/// </summary>
/// <remarks>
///     ⚠ <b>The seam the transport attaches to, and nothing here writes to ClickHouse or should.</b>
///     docs/plan/08 § The resource-graph projection routes these to a per-tenant ClickHouse table via
///     a projector. This assembly emits — the event is built with the projection's columns and
///     published at step 11 of the write path and at the terminal transitions <c>OperationGrain</c>
///     drives — and <c>CyberCloud.ResourceGraph</c> implements this interface over NATS JetStream and
///     consumes the stream on the silos (#54). A host with no stream configured keeps
///     <c>LoggingResourceChangedSink</c>.
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
///         ⚠
///         <b>
///             An interface here rather than a direct <c>ICheckGrain</c> call, and the reason is the
///             assembly graph.
///         </b> <c>CyberCloud.ResourceManager.Contracts</c> deliberately does not
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
    ///     <i>
    ///         "deletion, key export, billing changes, anything where a stale allow is a real
    ///         incident"
    ///     </i>.
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

    /// <summary>
    ///     Which of a collection's members the caller may read, asked once for the collection rather
    ///     than once per member — docs/plan/07 § ListObjects, the half of the model that lets
    ///     <see cref="IResourceManager.ListAsync" /> stop running a check per member.
    /// </summary>
    /// <param name="collection">The collection being listed.</param>
    /// <param name="parentResourceId">
    ///     The GUID of the parent resource for a nested collection, or <see cref="Guid.Empty" /> for
    ///     a top-level one, whose members hang off the resource group. It is what the engine scopes
    ///     the walk to, so a wrong value here does not leak — it lists nothing.
    /// </param>
    /// <param name="candidates">The members on the page, by GUID. The answer is a subset of these.</param>
    /// <param name="readPermission">The permission a read needs, from the registry.</param>
    /// <param name="caller">Who is asking.</param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>
    ///     <see cref="CollectionVisibility.IsAnswered" /> with the readable subset, or
    ///     <see cref="CollectionVisibility.Unanswered" /> when the engine could not answer within its
    ///     bounds — ⚠ in which case the caller asks <see cref="AuthorizeAsync" /> once per member,
    ///     which is the path this method exists to replace and remains the fallback. Never a
    ///     refusal: a member the caller may not read is one that is not in the answer.
    /// </returns>
    Task<CollectionVisibility> ListReadableAsync(
        ResourceCollectionId collection,
        Guid parentResourceId,
        IReadOnlyCollection<Guid> candidates,
        string readPermission,
        CallerContext caller,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
///     What <see cref="IResourceAuthorizer.ListReadableAsync" /> concluded: the readable subset, or
///     that the question was not answered and has to be asked per member.
/// </summary>
/// <remarks>
///     ⚠ <b>Two states rather than a nullable set</b>, so that "answered, and nothing is readable"
///     and "not answered" cannot be confused — the first is an empty page and the second is a
///     fallback, and a caller that treated one as the other would either leak a whole group or
///     hide one.
/// </remarks>
public sealed record CollectionVisibility {
    /// <summary>The engine did not answer; ask per member.</summary>
    public static CollectionVisibility Unanswered { get; } = new();

    /// <summary>Whether <see cref="Visible" /> is an answer.</summary>
    public bool IsAnswered { get; private init; }

    /// <summary>The readable members. Meaningless unless <see cref="IsAnswered" />.</summary>
    public IReadOnlySet<Guid> Visible { get; private init; } = ImmutableHashSet<Guid>.Empty;

    /// <summary>An answer: exactly these members are readable.</summary>
    /// <param name="visible">The readable members' GUIDs.</param>
    public static CollectionVisibility Of(IEnumerable<Guid> visible) {
        ArgumentNullException.ThrowIfNull(visible);
        return new() { IsAnswered = true, Visible = visible.ToImmutableHashSet() };
    }
}

/// <summary>
///     Where the resource manager records a resource's place in the authorization hierarchy —
///     the <c>parent</c> edge docs/plan/07 § The model's <c>From("parent", …)</c> rewrites follow.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             This exists because the write path used to have no step that wrote a tuple, and a
///             create therefore produced a resource its own creator could not read.
///         </b>
///         <c>CyberCloudSchema</c> gives a resource
///         <c>Role(owner, This | From(parent, owner))</c>, so the only thing that makes a resource
///         reachable from the role assignments on its group is a
///         <c>resource:{id}#parent@resourceGroup:{sub}-{rg}</c> tuple. Nobody wrote one. The
///         isolation suite wrote its own and said so in its remarks; docs/plan/08 § The write path,
///         end to end now has the step, and this is the seam it calls.
///     </para>
///     <para>
///         ⚠
///         <b>
///             An interface here rather than an <c>ITupleStoreGrain</c> call, for the same reason
///             <see cref="IResourceAuthorizer" /> is an interface.
///         </b>
///         <c>CyberCloud.ResourceManager.Contracts</c> deliberately does not reference
///         <c>CyberCloud.Authorization.Contracts</c>, so a provider referencing this assembly cannot
///         name a tuple type — docs/plan/07 § The enforcement seam,
///         <i>
///             "Providers never call the
///             engine."
///         </i> A provider that could <i>write</i> tuples would be worse than one that could
///         read them.
///     </para>
///     <para>
///         ⚠
///         <b>
///             For a LIVE resource the parent is the resource GROUP and not the subscription, and
///             <c>CyberCloudSchema</c> is what decides that.
///         </b> Its rewrite chain is
///         resource → resourceGroup → subscription → tenant: pointing a live resource's <c>parent</c>
///         at the subscription would skip the group, and every <c>resourceGroup:…#contributor</c>
///         assignment — the second row of docs/plan/07 § Azure RBAC, expressed in it — would grant
///         nothing on the resources inside it.
///     </para>
///     <para>
///         ⚠
///         <b>
///             A SOFT-DELETED resource is the one exception, and it is an exception because it is no
///             longer in the group.
///         </b> docs/plan/08 § Soft delete: a soft-deleted resource leaves its
///         resource group, so
///         <i>
///             "a tuple naming the resource group as its parent asserts a containment
///             that is no longer true. Preserving it is not the conservative choice, it is the wrong
///             one."
///         </i> The edge moves to <c>#parent@subscription:{sub}</c> and back on restore — see
///         <see cref="ReparentToSubscriptionAsync" />. The paragraph above is unaffected: skipping the
///         group is precisely the intent while the resource is not in one.
///     </para>
/// </remarks>
public interface IResourceRelationWriter {
    /// <summary>
    ///     Records <c>resource:{id}#parent@resource:{parentId}</c> for a child, or
    ///     <c>resource:{id}#parent@resourceGroup:{subscription}-{group}</c> for a top-level resource.
    ///     Idempotent.
    /// </summary>
    /// <param name="id">
    ///     The resource, with <see cref="ResourceId.Id" /> set. ⚠ The GUID is minted at the quota step
    ///     and the name is claimed at the index step, both <i>before</i> durable state exists — which
    ///     is what lets this run before the resource does.
    /// </param>
    /// <param name="parentId">
    ///     The GUID of the resource <see cref="ResourceId.Parent" /> names, or <see cref="Guid.Empty" />
    ///     when it is <see langword="null" />.
    ///     <para>
    ///         ⚠ <b>Passed in rather than resolved here, and the delete path is why.</b>
    ///         <see cref="ResourceId.Parent" /> is an <i>address</i>: docs/plan/06 § Identifiers keeps
    ///         the GUID out of the path, so turning it into a subject needs
    ///         <c>IResourceIndexGrain</c>. An implementation that did that lookup itself would do it on
    ///         <see cref="UnlinkFromParentAsync" /> too — which runs when the resource is gone, retried
    ///         from a reminder, at a point where the parent may also be gone (docs/plan/08 § Deleting a
    ///         parent resource that has children records that the refusal which would prevent that is
    ///         decided and not built). The unlink would then fail on every retry and the tuple would
    ///         leak. The caller resolves once, on the write path, and persists it on the operation.
    ///     </para>
    ///     <para>
    ///         ⚠ <see cref="Guid.Empty" /> for an id whose <see cref="ResourceId.Parent" /> is
    ///         <i>not</i> null is a caller error and is refused, not quietly aimed at the resource
    ///         group: that fallback is what made a child inherit from its group instead of its parent.
    ///     </para>
    /// </param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>
    ///     Success, or the failure that stopped it. ⚠ The write path <b>fails the request</b> on a
    ///     failure here, which it can only do honestly because the call happens before
    ///     <c>SubmitDesiredAsync</c> — see the write path's step 8.
    /// </returns>
    Task<Result> LinkToParentAsync(ResourceId id, Guid parentId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Removes the <c>parent</c> tuple. Idempotent — removing one that is not there succeeds.
    /// </summary>
    /// <param name="id">The resource, with <see cref="ResourceId.Id" /> set.</param>
    /// <param name="parentId">
    ///     The same GUID <see cref="LinkToParentAsync" /> was given. ⚠ It must be the same one: a
    ///     delete removes the tuple it wrote, so a different subject here removes nothing and leaves
    ///     the real edge behind. <c>OperationSpec</c> persists it for exactly this call.
    /// </param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <remarks>
    ///     ⚠ <b>Called when the resource is <i>gone</i>, not when a delete is requested.</b> A
    ///     resource being torn down stays visible in <c>Deleting</c> (docs/plan/06 § Two-phase create)
    ///     and its owner has to be able to watch that happen, so unlinking at the request would blind
    ///     them to their own delete. <c>OperationGrain</c> calls this after
    ///     <c>CompleteDeleteAsync</c>, and retries it from its reminder if it fails — a tuple pointing
    ///     at an object that no longer exists is a slow leak in the tenant's tuple store.
    /// </remarks>
    Task<Result> UnlinkFromParentAsync(ResourceId id, Guid parentId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Moves the <c>parent</c> edge to <c>subscription:{sub}</c> — the resource has been
    ///     soft-deleted and has left its resource group. Idempotent.
    /// </summary>
    /// <param name="id">The resource, with <see cref="ResourceId.Id" /> set.</param>
    /// <param name="parentId">
    ///     The GUID of the parent this resource is leaving, so the old tuple can be removed. Same value
    ///     <see cref="LinkToParentAsync" /> was given, and <see cref="Guid.Empty" /> for a top-level
    ///     resource whose old parent is its group.
    /// </param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Success, or the failure that stopped it.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             Re-parented rather than preserved and rather than dropped, and the two rejected
    ///             options fail differently.
    ///         </b> docs/plan/08 § Soft delete: preserving the group edge
    ///         asserts a containment that is no longer true; dropping it leaves the resource
    ///         <i>parentless</i>, which is the failure that made this seam necessary in the first place
    ///         — a resource nobody can see, and a silo lost in that window leaving it that way. Moving
    ///         it means the window has no such state at all.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Who can see a deleted resource becomes who holds subscription-scoped rights</b>,
    ///         which is deliberate rather than incidental:
    ///         <i>
    ///             "exactly who Azure gives
    ///             <c>deletedVaults/read</c> and <c>purge/action</c> to. A restore is a subscription-scoped
    ///             operation; the visibility should match."
    ///         </i>
    ///     </para>
    ///     <para>
    ///         ⚠ <b>This does not drop direct role assignments</b> — that is
    ///         <see cref="DropDirectRoleAssignmentsAsync" />, and the two are separate calls because
    ///         docs/plan/08 makes them separate decisions with different reasons: this one is a
    ///         modelling answer, that one is a security answer, and
    ///         <i>
    ///             "running them together is how
    ///             this gets decided wrongly"
    ///         </i>.
    ///     </para>
    /// </remarks>
    Task<Result> ReparentToSubscriptionAsync(
        ResourceId id,
        Guid parentId,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Moves the <c>parent</c> edge back off the subscription and onto the resource's ordinary
    ///     parent — the restore. Idempotent.
    /// </summary>
    /// <param name="id">The resource, with <see cref="ResourceId.Id" /> set.</param>
    /// <param name="parentId">
    ///     The GUID of the parent resource the address names, or <see cref="Guid.Empty" /> for a
    ///     top-level resource going back to its group.
    /// </param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Success, or the failure that stopped it.</returns>
    Task<Result> ReparentFromSubscriptionAsync(
        ResourceId id,
        Guid parentId,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Removes every role assignment written <b>directly</b> on this resource, leaving its
    ///     <c>parent</c> edge and everything it inherits untouched.
    /// </summary>
    /// <param name="id">The resource, with <see cref="ResourceId.Id" /> set.</param>
    /// <param name="cancellationToken">Cancels the writes.</param>
    /// <returns>How many assignments were dropped, or the failure that stopped it.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             A security answer and not a modelling one, and Azure's behaviour is the right one
    ///             to copy: assignments go with the resource and <i>"must be recreated"</i> on
    ///             recovery.
    ///         </b> docs/plan/08 § Soft delete:
    ///         <i>
    ///             "The recovery window is used after a
    ///             compromise or after a decommission somebody wants to undo, and those are the cases that
    ///             decide it. Silently restoring a grant an administrator deliberately removed is an error
    ///             nobody observes. Making somebody re-grant after a restore is an error everybody observes
    ///             and can fix in a minute. Take the visible failure."
    ///         </i>
    ///     </para>
    ///     <para>
    ///         ⚠ <b>There is no inverse, and there deliberately is not one.</b> A restore does not put
    ///         these back — that is the decision, not an omission — so nothing persists them and nothing
    ///         could. An implementation that stashed them somewhere recoverable would be building the
    ///         option the document rejected.
    ///     </para>
    /// </remarks>
    Task<Result<int>> DropDirectRoleAssignmentsAsync(
        ResourceId id,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Removes the <c>parent</c> tuple a soft-deleted resource holds on its subscription. The purge
    ///     counterpart of <see cref="UnlinkFromParentAsync" />. Idempotent.
    /// </summary>
    /// <param name="id">The resource, with <see cref="ResourceId.Id" /> set.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         A separate method because a purge must delete the edge the resource ACTUALLY holds, and
    ///         by then that is the subscription one.
    ///     </b> Calling <see cref="UnlinkFromParentAsync" /> would
    ///     build the ordinary subject — the resource group, or the parent resource — delete a tuple that
    ///     is not there, report success, and leave the real edge behind: one row per purged resource,
    ///     forever, pointing at a GUID that names nothing. The failure is silent in both directions,
    ///     which is why it is a different call rather than a parameter.
    /// </remarks>
    Task<Result> UnlinkFromSubscriptionAsync(ResourceId id, CancellationToken cancellationToken = default);
}

/// <summary>
///     The labelled objects a cluster currently holds, for the per-cluster drift scan.
/// </summary>
/// <remarks>
///     ⚠ <b>This is the seam the drift scan needs and cannot have yet.</b>
///     docs/plan/08 § The reconcile loop makes drift detection per-cluster and sources it from the
///     connection grain's live informer bridge —
///     <i>
///         "an hourly per-cluster reminder diffs labelled
///         objects against the resource grains that own them"
///     </i>. The <i>diff</i> is pure and is
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
/// <remarks>
///     ⚠ <b>Every member is <see langword="required" />, and this type used to be positional.</b> The
///     change was forced by <see cref="ResourceType" /> below: it was added as a fifth positional
///     parameter with no default, on the theory that omitting it would then be a compile error — and
///     it was not, because both real construction sites use an object initializer, which runs a
///     struct's parameterless constructor and leaves anything unset at its default. An unset
///     <see cref="ResourceType" /> reads as "belongs to a resource", which is the exact wrong answer
///     and produces a silent orphan storm rather than a build failure. <see langword="required" /> is
///     what makes the omission fail the way it was supposed to.
/// </remarks>
public readonly record struct ClusterObjectRecord {
    /// <summary>
    ///     The <c>cybercloud.io/resource-id</c> label — ADR-013's hash-join key
    ///     (docs/plan/09 § The command builder).
    /// </summary>
    public required Guid ResourceId { get; init; }

    /// <summary>The <c>cybercloud.io/resource-path</c> annotation.</summary>
    public required string ResourcePath { get; init; }

    /// <summary>The <c>cybercloud.io/reconcile-hash</c> annotation.</summary>
    public required string ReconcileHash { get; init; }

    /// <summary>Which object.</summary>
    public required ObjectRef Target { get; init; }

    /// <summary>
    ///     The <c>cybercloud.io/resource-type</c> label.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Carried for one reason: not every labelled object belongs to a resource.</b> The
    ///     platform writes a resource group's namespace itself (<c>NamespaceEnsurer</c>), and its
    ///     <see cref="ResourceId" /> is a GUID derived from the group — deliberately, so that
    ///     deleting the resource that happened to create it does not orphan the namespace everything
    ///     else in the group lives in. Without this member the scan cannot tell that object from a
    ///     real one and reports every namespace on the cluster as an orphan, forever.
    ///     <c>KubeLabels.IsGroupScoped</c> is the test.
    /// </remarks>
    public required string ResourceType { get; init; }

    /// <summary>
    ///     The fragments other resources have co-written onto this object — one per
    ///     <c>cybercloud.io/fragment.{writer}</c> annotation — with the hash and path recorded beside
    ///     each. Empty for the object every resource owned alone before issue #89.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Not <see langword="required" />, unlike everything above, and that is right:</b> an
    ///     unset value reads as "nobody co-writes this object", which is true of every object but a
    ///     peered <c>Vpc</c>, whereas an unset <see cref="ResourceType" /> read as the wrong answer.
    ///     What the scan does with it: a co-writing resource owns no object carrying its resource-id
    ///     label, so without this the scan would report every converged peering as a stray forever;
    ///     with it, the co-writer joins to the objects carrying its fragment, its hash is compared
    ///     against the fragment's, and a fragment whose writer no grain owns is an orphan the scan
    ///     names — the one way a fragment left by a co-writer that vanished without withdrawing is
    ///     ever found. <c>DriftScanner</c>'s remarks say what it does with each.
    /// </remarks>
    public ImmutableArray<FragmentRecord> Fragments { get; init; } = [];

    // A struct with a member initializer has to declare a constructor for it to run, and this one
    // is what makes an unset Fragments an EMPTY array rather than a default one. The `required`
    // members above are still enforced at every object initializer.
    /// <summary>Starts an object initializer; every <see langword="required" /> member must follow.</summary>
    public ClusterObjectRecord() { }
}

/// <summary>One co-writer's fragment as the drift scan sees it on an object it does not own.</summary>
/// <param name="Writer">The co-writing resource's GUID, from the annotation key.</param>
/// <param name="Hash">The <c>cybercloud.io/fragment-hash.{writer}</c> annotation, or empty when it is missing.</param>
/// <param name="Path">The <c>cybercloud.io/fragment-path.{writer}</c> annotation, or empty when it is missing.</param>
public readonly record struct FragmentRecord(Guid Writer, string Hash, string Path);
