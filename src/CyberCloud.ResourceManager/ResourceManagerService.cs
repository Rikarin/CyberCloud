using CyberCloud.ResourceManager.Actions;
using CyberCloud.ResourceManager.Contracts.Registry;
using Microsoft.Extensions.Logging;
using Orleans.Multitenant;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;

namespace CyberCloud.ResourceManager;

/// <summary>
///     The write path. docs/plan/08 § The write path, end to end, all twelve steps, in order.
/// </summary>
/// <remarks>
///     <para>
///         <b>The order is the component.</b> docs/plan/08:
///         <i>
///             "Steps 3-7 are the entire reason this is one component rather than a shared library
///             each provider calls. A provider that could skip step 3 is a provider that eventually
///             will."
///         </i>
///         Every step records itself into a <see cref="WriteTraceBuilder" />, which refuses to go
///         backwards — so a reordering fails at the call site rather than in production.
///     </para>
///     <para>
///         ⚠ <b>Step 1 resolves the index, and that is a read rather than the claim.</b>
///         docs/plan/08's step 1 is <i>"parse path → ResourceId; look up the provider + type +
///         api-version in the registry"</i>, and docs/plan/06 § Identifiers says a parsed path yields
///         <c>Guid.Empty</c> and <i>"resolving it to a real identity is a lookup through
///         <c>IResourceIndexGrain</c>"</i>. So the resolve belongs to step 1. It matters that it is
///         there and not later, because step 3 has to know <b>whether the resource exists</b> to know
///         whether to check the resource or its parent group — and because step 6 has to know whether
///         this is a create (which draws quota) or an update (which does not, the resource is already
///         counted). Neither the reservation at 6 nor the claim at 7 moves.
///     </para>
///     <para>
///         ⚠ <b>Step 8 writes a ReBAC tuple, and it is the only step that writes to the authorization
///         store.</b> docs/plan/07 § The model makes a resource's permissions inherit through
///         <c>From("parent", …)</c>, which follows a <c>resource:X#parent@resourceGroup:Y</c> edge —
///         and nothing wrote one, so a create used to succeed and the creator then got <c>404</c> on
///         what they had just made. The step sits between the index claim and the durable write for
///         reasons set out at the call site; the short version is that before the durable write a
///         failure is a clean refusal, and after it a failure is a resource nobody can see.
///     </para>
///     <para>
///         ⚠ <b>Every grain reference goes through <c>ForTenant</c>.</b> This is a plain service held
///         by the gateway, which is an Orleans <i>client</i>, and
///         <c>Orleans.Multitenant</c>'s call filter never sees a caller that is not a grain — see
///         <c>CyberCloud.Tenancy/TenancySiloBuilderExtensions.cs</c>. <c>CC1006</c> is what keeps that
///         true after the next edit.
///     </para>
/// </remarks>
public sealed class ResourceManagerService(
    IProviderRegistry registry,
    IResourceAuthorizer authorizer,
    IResourceRelationWriter relations,
    ILockResolver locks,
    IPolicyEvaluator policy,
    IResourceChangedSink changes,
    IGrainFactory grains,
    ActionDispatcher actions,
    ILogger<ResourceManagerService> logger
)
    : IResourceManager {
    /// <inheritdoc />
    public async Task<Result<WriteAccepted>> WriteAsync(
        WriteRequest request,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Verb is not (WriteVerb.Put or WriteVerb.Patch)) {
            return Result<WriteAccepted>.Failure(
                ErrorCode.InvalidRequestBody,
                $"{request.Verb} is not a write verb. A resource is created and replaced with PUT and "
                + "amended with PATCH; POST is an action on an existing resource and never creates — "
                + "docs/plan/08 § The write path, end to end."
            );
        }

        var trace = new WriteTraceBuilder();

        // ── 1. Parse the path, resolve the identity, look the type and version up ───────────────
        trace.Enter(WriteStep.ResolveRegistration);

        var resolved = await ResolveAsync(request, trace);
        if (resolved.TryGetError(out var resolveError)) {
            return Result<WriteAccepted>.Failure(resolveError);
        }

        var target = resolved.GetValueOrThrow();

        // ── 2. Validate the body against this api-version's schema ──────────────────────────────
        trace.Enter(WriteStep.ValidateBody);

        JsonDocument body;
        try {
            body = JsonDocument.Parse(request.Body);
        }
        catch (JsonException exception) {
            return Result<WriteAccepted>.Failure(
                ErrorCode.InvalidRequestBody,
                // The parser's message describes the caller's own input, not our stack —
                // docs/plan/08 § Errors bans exception detail, and this is not any.
                $"The request body is not valid JSON: {exception.Message}",
                ""
            );
        }

        using (body) {
            // ⚠ A PATCH document validates without its required properties — a merge patch omits
            // everything it is not changing. The MERGED result is what must satisfy them, and the
            // resource grain is where the merge happens, so this validates shape and leaves
            // requiredness to the PUT case. That asymmetry is the whole difference between the verbs.
            // ⚠ SupportsTags is passed through, and it has to be. The schema declares the type's own
            // properties; the tag bag is the platform's envelope and is not any type's property, so a
            // validator that did not know about the declaration would refuse '/tags' as unknown —
            // which it did, until the first provider that declared SupportsTags tried to use it.
            var validated = target.Schema.Validate(
                body.RootElement,
                request.Verb == WriteVerb.Put,
                target.Registration.SupportsTags
            );
            if (validated.TryGetError(out var schemaError)) {
                return Result<WriteAccepted>.Failure(schemaError);
            }

            return await ContinueWriteAsync(request, target, body.RootElement, trace, cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task<Result<ResourceSnapshot>> ReadAsync(
        WriteRequest request,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(request);

        var trace = new WriteTraceBuilder();
        trace.Enter(WriteStep.ResolveRegistration);

        var resolved = await ResolveAsync(request, trace);
        if (resolved.TryGetError(out var resolveError)) {
            return Result<ResourceSnapshot>.Failure(resolveError);
        }

        var target = resolved.GetValueOrThrow();

        // A read skips step 2 — there is no body — and goes straight to the enforcement seam.
        trace.Enter(WriteStep.AuthorizationCheck);

        // ⚠ Both permissions are the read permission here, which makes the seam answer 404 for every
        // refusal on this path. That is correct and is not a shortcut: a caller who cannot read gets
        // 404 because 403 would confirm the resource exists, and a caller who *can* read is not being
        // refused. There is no third case for a GET.
        var authorized = await authorizer.AuthorizeAsync(
            target.Id,
            target.Registration.ReadPermission,
            target.Registration.ReadPermission,
            request.Caller,
            false,
            cancellationToken
        );

        if (authorized.TryGetError(out var authError)) {
            return Result<ResourceSnapshot>.Failure(authError);
        }

        if (!target.Exists) {
            return NotFound<ResourceSnapshot>(request.Path);
        }

        return await Resource(target).GetAsync(target.ApiVersion.Value, ReadablePointers(target.Schema));
    }

    /// <inheritdoc />
    public async Task<Result<WriteAccepted>> DeleteAsync(
        WriteRequest request,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(request);

        var trace = new WriteTraceBuilder();
        trace.Enter(WriteStep.ResolveRegistration);

        var resolved = await ResolveAsync(request, trace);
        if (resolved.TryGetError(out var resolveError)) {
            return Result<WriteAccepted>.Failure(resolveError);
        }

        var target = resolved.GetValueOrThrow();

        trace.Enter(WriteStep.AuthorizationCheck);

        // ⚠ FullyConsistent. docs/plan/07 § Consistency: "Bypass all caches, read durable … Deletion,
        // key export, billing changes, anything where a stale allow is a real incident." A revoked
        // operator whose next request deletes production is the incident that table is about.
        var authorized = await authorizer.AuthorizeAsync(
            target.Id,
            target.Registration.DeletePermission,
            target.Registration.ReadPermission,
            request.Caller,
            true,
            cancellationToken
        );

        if (authorized.TryGetError(out var authError)) {
            return Result<WriteAccepted>.Failure(authError);
        }

        if (!target.Exists) {
            return NotFound<WriteAccepted>(request.Path);
        }

        // ── THE CHILDREN, ONE STEP BEFORE THE LOCK CHECK ────────────────────────────────────────
        //
        // ⚠ THE OTHER END OF STEP 1'S PARENT CHECK, AND IT REFUSES RATHER THAN CASCADING.
        //
        // docs/plan/08 § Deleting a parent resource that has children: "a delete is refused while the
        // resource still has children — 409, not a cascade, and not a silent orphan." A resource group
        // is a declared lifecycle boundary and a parent resource is not: nobody who types DELETE on a
        // single-resource URL has said anything about the databases on that server, and a cascade
        // would tear down an unknown number of resources they never named, with the data in them,
        // returning their quota under an operation that says it deleted something else.
        //
        // ⚠ WHY THIS IS ANSWERABLE NOW WHEN THE DOCUMENT RECORDED IT AS UNANSWERABLE. The blocker was
        // that IResourceIndexGrain is path→GUID and one-way, so the only enumeration available was the
        // eventually-consistent resource-graph projection — "a delete gate reading a stale index either
        // orphans a child it did not see or refuses over a child that is already gone". The counter the
        // document then asks for lives ON THE PARENT'S OWN INDEX GRAIN, which is the one activation
        // that already serialises "is this name taken": the read below and the ReleaseAsync further
        // down address the same grain, so there is no window in which the answer changes between them
        // and no second entity to disagree with.
        //
        // ⚠ IT COUNTS RESOURCES, WHICH INCLUDES CHILDREN THAT ARE STILL TEARING DOWN, AND THAT IS
        // CORRECT. docs/plan/06 § Two-phase create keeps a child whose teardown failed visible, in
        // Deleting, still metered — "never silently gone while its pods still run and its meter still
        // ticks". A child in that state exists, so it holds its parent. The count is decremented by the
        // child's own operation grain once CompleteDeleteAsync has cleared it, which is the moment it
        // stops existing.
        //
        // ⚠ BEFORE THE LOCK CHECK, WHICH IS THE ORDER THE DOCUMENT GIVES AND ALSO THE CHEAPER ONE: a
        // caller who has to delete three databases first should be told that on the first call rather
        // than after removing a lock they did not need to remove.
        //
        // ⚠ AND AFTER THE ENFORCEMENT SEAM, WHICH IS WHAT KEEPS THE COUNT FROM BEING AN ORACLE. The
        // authorization check above has already answered 404 for a caller who cannot read this
        // resource, so the only callers who reach this line are ones the child count tells nothing new.
        //
        // ⚠ What must NOT be done instead is re-checking the parent on every write to a child: that
        // turns a deleted parent into a frozen child which answers 404 to a GET for a resource that
        // plainly exists — worse than the orphan. ParentExistenceTests pins against it.
        var children = await Index(target).ChildrenAsync();
        if (children.TryGetError(out var childrenError)) {
            return Result<WriteAccepted>.Failure(childrenError);
        }

        if (children.GetValueOrThrow() is { Length: > 0 } held) {
            return Result<WriteAccepted>.Failure(ErrorCode.ResourceHasChildren, ChildRefusal(request.Path, held));
        }

        trace.Enter(WriteStep.Locks);

        var lockLevel = await locks.ResolveAsync(target.Id, cancellationToken);
        if (lockLevel.TryGetError(out var lockError)) {
            return Result<WriteAccepted>.Failure(lockError);
        }

        if (lockLevel.GetValueOrThrow() is LockLevel.CanNotDelete or LockLevel.ReadOnly) {
            return Result<WriteAccepted>.Failure(
                ErrorCode.ScopeLocked,
                $"'{request.Path}' is covered by a {lockLevel.GetValueOrThrow()} lock, so it cannot be "
                + "deleted. Locks are inherited from the resource group, the subscription and the "
                + "management group — docs/plan/06 § Tags, locks, and the small stuff that is not "
                + "small."
            );
        }

        // ── The single-writer refusal, BEFORE anything is released ──────────────────────────────
        //
        // ⚠ THIS READ EXISTS BECAUSE THE INDEX RELEASE BELOW IS IRREVERSIBLE, AND THE ORDER IS FIXED.
        // docs/plan/06 § Two-phase create: "release the index first (so the name is immediately
        // reusable), then tear down the data plane, then delete the grain state." So by the time
        // BeginDeleteAsync could answer OperationInProgress — docs/plan/03 § Providers' "delete while
        // an operation is running → 409" — the name would already be free, and a refused delete would
        // have handed somebody else the right to claim a name that is still in use. The grain keeps
        // the guard as well, because it is the single writer and a direct caller must not be able to
        // walk past it; this is the same rule read one step earlier so that a 409 costs nothing.
        var live = await Resource(target).GetAsync(target.ApiVersion.Value, []);
        if (live.IsSuccess
            && live.GetValueOrThrow().ProvisioningState
                is ProvisioningState.Creating or ProvisioningState.Updating or ProvisioningState.Deleting) {
            return Result<WriteAccepted>.Failure(
                ErrorCode.OperationInProgress,
                $"Operation {live.GetValueOrThrow().OperationId:D} is already driving "
                + $"'{request.Path}' and it is {live.GetValueOrThrow().ProvisioningState}. Poll that "
                + "operation, or cancel it, before deleting — a delete that raced a live create would "
                + "tear down objects the create is still applying."
            );
        }

        // Steps 5, 6 and 7 read differently on the delete path and the trace says so rather than
        // pretending they ran: policy is not evaluated for a delete in this build, quota is returned
        // rather than reserved (and only once teardown converges), and the index is RELEASED rather
        // than claimed.
        trace.Enter(WriteStep.IndexClaim);

        // ⚠ THE INDEX GOES FIRST, AND THAT IS THE ORDER docs/plan/06 § Two-phase create GIVES:
        // "Deletion is the same in reverse and it is the harder half: release the index first (so the
        // name is immediately reusable), then tear down the data plane, then delete the grain state."
        //
        // ── AND FOR A SOFT-DELETABLE TYPE IT IS PARKED RATHER THAN RELEASED ─────────────────────
        //
        // ⚠ THIS ONE BRANCH IS WHY docs/plan/08 § Soft delete NEEDS NO "unless deleted" CLAUSE
        // ANYWHERE ELSE. The document weighs moving the resource out of the tree against leaving it in
        // place with a flag, and rejects the flag because it "puts an 'unless deleted' clause on every
        // read path, every list, every ReBAC check and the index claim, and the feature is then only as
        // good as the least-remembered of them". IndexEntryState.SoftDeleted is how the move is
        // spelled: ResolveAsync refuses the entry, so step 1 of every later request reads Exists =
        // false and answers the CANONICAL 404 — the same bytes from the same NotFound helper that a
        // name nobody ever claimed gets. Nothing downstream learns that soft delete exists.
        //
        // ⚠ AND IT IS A 404 RATHER THAN A 410, WHICH IS THE POINT THE DOCUMENT MAKES TWICE. A 410 Gone
        // would tell a caller who may not read the resource that the name was taken — the enumeration
        // oracle docs/plan/07 § The enforcement seam exists to close, handed back by the one status
        // code that would have felt more informative.
        //
        // ⚠ THE RETENTION COMES FROM THE REGISTRY AND NEVER FROM THE BODY, which is how docs/plan/08's
        // "retention is set at creation and immutable afterwards" is honoured: there is no per-resource
        // retention property, so there is nothing a caller can shorten under their own resource. The
        // grain stamps the deadline with its own clock (see IResourceIndexGrain.SoftDeleteAsync) and
        // does not restamp it on a re-drive.
        var softDelete = target.Registration.SoftDeleteDays > 0;

        if (softDelete) {
            var parked = await Index(target)
                .SoftDeleteAsync(target.Id.Id, TimeSpan.FromDays(target.Registration.SoftDeleteDays));

            if (parked.TryGetError(out var parkError)) {
                return Result<WriteAccepted>.Failure(parkError);
            }
        }
        else {
            var released = await Index(target).ReleaseAsync(target.Id.Id);
            if (released.TryGetError(out var releaseError)) {
                return Result<WriteAccepted>.Failure(releaseError);
            }
        }

        trace.Enter(WriteStep.SubmitDesired);

        var operationId = Guid.NewGuid();
        var beginning = await Resource(target).BeginDeleteAsync(operationId, request.IfMatch);
        if (beginning.TryGetError(out var beginError)) {
            return Result<WriteAccepted>.Failure(beginError);
        }

        var snapshot = beginning.GetValueOrThrow();

        // ⚠ THE PARENT'S GUID IS RESOLVED HERE, ONCE, WHILE SOMEONE IS STILL WAITING FOR AN ANSWER.
        //
        // The unlink runs from OperationGrain's reminder after CompleteDeleteAsync, off the request
        // path, and must remove the tuple the create wrote — `resource:{child}#parent@resource:{parent}`
        // for a child. Neither half of that is recoverable there: the spec carries a PATH, and
        // docs/plan/06 § Identifiers keeps GUIDs out of paths.
        //
        // ⚠ ResolveAsync (step 1) is not where this goes, even though it does the same lookup for a
        // create. It is shared with ReadAsync and ActionAsync, so resolving there would put an index
        // read on every GET of every child to serve a value only a create and a delete ever use.
        //
        // A parent that no longer resolves leaves this Guid.Empty rather than failing the delete: the
        // child is an orphan, refusing would strand it undeletable, and the writer's remarks explain
        // what an empty id costs on the unlink side.
        var parentResourceId = Guid.Empty;

        if (target.Id.Parent is { } parentAddress) {
            var bound = await Tenant(target)
                .GetGrain<IResourceIndexGrain>(GrainKeys.PathIndex(parentAddress))
                .ResolveAsync();

            if (bound.IsSuccess) {
                parentResourceId = bound.GetValueOrThrow();
            }
        }

        trace.Enter(WriteStep.StartOperation);

        var started = await Operation(target, operationId)
            .StartAsync(
                new() {
                    OperationId = operationId,
                    Kind = OperationKind.Delete,
                    ResourcePath = target.Id.Path,
                    ResourceId = target.Id.Id,
                    TenantId = target.Id.TenantId,
                    SubscriptionId = target.Id.SubscriptionId,
                    ApiVersion = target.ApiVersion.Value,
                    Desired = "{}",
                    // ⚠ A DELETE HOLDS NO LEASE, AND THAT IS THE WHOLE DEFECT THIS LINE CLOSES.
                    // The operation grain used to "return committed quota" by releasing leases; this
                    // array is empty and always was, so nothing came back and a subscription's
                    // committed usage climbed by one resource's worth on every delete.
                    QuotaLeaseIds = [],
                    // ⚠ …and this is what it actually has to give back. Derived from the resource's
                    // STORED body — BeginDeleteAsync above projected the whole superset, not an
                    // api-version's view of it — through the same AmountFor the create reserved with,
                    // so a delete returns the number the create committed rather than one near it.
                    // Recorded on the spec because the grain is re-driven from a reminder after
                    // CompleteDeleteAsync has cleared the resource, at which point there is nothing
                    // left to derive it from.
                    // ⚠ …AND FOR A SOFT-DELETABLE TYPE IT IS NOT GIVEN BACK HERE. The amounts are
                    // still recorded — the purge that ends the window needs exactly them and by then
                    // there is nothing to derive them from — but `SoftDelete` below is what stops the
                    // operation returning them on convergence. docs/plan/08 § Soft delete: a resource
                    // in its recovery window "consumes plenty, because handing the data back is the
                    // entire feature: the volumes, the PVCs and the memory are all still allocated".
                    CommittedQuota = CommittedBy(target.Registration, snapshot.Properties),
                    IndexClaimed = false,
                    ParentResourceId = parentResourceId,
                    SoftDelete = softDelete,
                    Caller = request.Caller
                }
            );

        if (started.TryGetError(out var startError)) {
            return Result<WriteAccepted>.Failure(startError);
        }

        trace.Enter(WriteStep.EmitChanged);
        await EmitAsync(ResourceChangeKind.Deleting, target, snapshot, cancellationToken);

        trace.Enter(WriteStep.Accepted);

        return Result<WriteAccepted>.Success(
            new() {
                OperationId = operationId,
                OperationUri = OperationUri(operationId),
                RetryAfterSeconds = ReconcileSchedule.InitialRetryAfterSeconds,
                Resource = snapshot,
                Trace = trace.Build()
            }
        );
    }

    /// <inheritdoc />
    public async Task<Result<WriteAccepted>> RestoreAsync(
        WriteRequest request,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(request);

        var trace = new WriteTraceBuilder();
        trace.Enter(WriteStep.ResolveRegistration);

        var resolved = await ResolveAsync(request, trace);
        if (resolved.TryGetError(out var resolveError)) {
            return Result<WriteAccepted>.Failure(resolveError);
        }

        var target = resolved.GetValueOrThrow();

        // ⚠ THE GUID COMES FROM THE INDEX'S SOFT-DELETED SIDE, AND STEP 1 CANNOT SUPPLY IT.
        //
        // ResolveAsync reads IResourceIndexGrain.ResolveAsync, which refuses a soft-deleted binding —
        // that refusal is the whole of the canonical 404 and must not be relaxed. So `target.Id.Id` is
        // Guid.Empty here for exactly the resources this method exists to act on, and the entitled
        // question is asked by a different method: ResolveSoftDeletedAsync. See its remarks for why
        // that is a second method rather than a flag on the first.
        var parked = await RestorableAsync(target);
        if (parked.TryGetError(out var parkedError)) {
            return Result<WriteAccepted>.Failure(parkedError);
        }

        var addressed = target with { Id = target.Id.WithId(parked.GetValueOrThrow()) };

        trace.Enter(WriteStep.AuthorizationCheck);

        // ⚠ CHECKED AGAINST THE RESOURCE, WHICH BY NOW HANGS OFF THE SUBSCRIPTION — and that is the
        // visibility docs/plan/08 § Soft delete chose deliberately: "the people who can see a deleted
        // resource become the people who hold subscription-scoped rights, which is exactly who Azure
        // gives deletedVaults/read and purge/action to. A restore is a subscription-scoped operation;
        // the visibility should match."
        //
        // ⚠ The GUID above is what makes that true rather than accidental. Authorizing the address
        // BEFORE resolving it would carry Guid.Empty, and ReBacResourceAuthorizer.CheckedObject reads
        // that as "check the parent resource group" — the scope the resource has just left, and a
        // different question with a different answer.
        //
        // ⚠ FullyConsistent, for the reason the delete path gives: docs/plan/07 § Consistency puts
        // deletion in the row where "a stale allow is a real incident", and a restore is the verb that
        // undoes one.
        var authorized = await authorizer.AuthorizeAsync(
            addressed.Id,
            addressed.Registration.WritePermission,
            addressed.Registration.ReadPermission,
            request.Caller,
            true,
            cancellationToken
        );

        if (authorized.TryGetError(out var authError)) {
            return Result<WriteAccepted>.Failure(authError);
        }

        trace.Enter(WriteStep.Locks);

        var lockLevel = await locks.ResolveAsync(addressed.Id, cancellationToken);
        if (lockLevel.TryGetError(out var lockError)) {
            return Result<WriteAccepted>.Failure(lockError);
        }

        if (lockLevel.GetValueOrThrow() == LockLevel.ReadOnly) {
            return Result<WriteAccepted>.Failure(
                ErrorCode.ScopeLocked,
                $"'{request.Path}' is covered by a ReadOnly lock, so it cannot be restored — a restore "
                + "puts a resource back into the scope the lock covers. docs/plan/06 § Tags, locks."
            );
        }

        // ⚠ A DELETE THAT IS STILL TEARING THE DATA PLANE DOWN IS NOT A RESOURCE THAT CAN BE
        // RESTORED YET, AND THIS READ IS WHERE THAT IS REFUSED.
        //
        // The index is parked on the delete's REQUEST path, before its operation has run a single
        // pass — so a resource can be soft-deleted, unaddressable and still have pods coming down.
        // Restoring in that window would put two operations on one resource driving it towards
        // opposite shapes, which is the race the single-writer guard exists for; and the resource
        // grain cannot apply that guard itself, because a parked resource's OperationId always names
        // the delete and a guard reading "somebody else holds this" would refuse every first restore.
        // So it is asked here, of the operation, exactly as the delete path asks the resource before
        // releasing the index: the refusal has to come before anything irreversible.
        var deleting = await Resource(addressed).GetAsync(addressed.ApiVersion.Value, []);
        if (deleting.TryGetError(out var deletingError)) {
            return Result<WriteAccepted>.Failure(deletingError);
        }

        var parkedBy = deleting.GetValueOrThrow().OperationId;
        if (parkedBy != Guid.Empty) {
            var driving = await Operation(addressed, parkedBy).GetAsync();

            if (driving.IsSuccess
                && driving.GetValueOrThrow().State is OperationState.NotStarted or OperationState.Running) {
                return Result<WriteAccepted>.Failure(
                    ErrorCode.OperationInProgress,
                    $"Operation {parkedBy:D} is still tearing '{request.Path}' down and it is "
                    + $"{driving.GetValueOrThrow().State}. Poll that operation before restoring — a "
                    + "restore that raced its own delete would re-apply objects the teardown is still "
                    + "removing."
                );
            }
        }

        trace.Enter(WriteStep.IndexClaim);

        // ⚠ THE INDEX GOES FIRST HERE TOO, AND FOR THE MIRROR OF THE DELETE'S REASON. The index is
        // what makes the resource addressable; until it is Confirmed again a caller cannot reach the
        // resource whatever else has been written, so a failure after this line leaves a resource that
        // IS reachable and whose parent edge may still name the subscription — visible to a
        // subscription role holder, which is who asked for the restore. The other order would leave it
        // addressable to nobody after a partial failure, and nothing would notice.
        //
        // ⚠ The window is checked HERE and not above, because the index is what holds it:
        // RestoreAsync refuses a binding past IndexEntry.RecoverableUntil with the same
        // ResourceNotFound a name that holds nothing gets. Reading the deadline first and acting on it
        // second would be two reads of a value that can change between them.
        var restored = await Index(addressed).RestoreAsync(addressed.Id.Id);
        if (restored.TryGetError(out var restoreError)) {
            return Result<WriteAccepted>.Failure(restoreError);
        }

        trace.Enter(WriteStep.LinkParent);

        // ⚠ AND THE PARENT EDGE COMES BACK TO THE RESOURCE GROUP. docs/plan/08 § Soft delete: the
        // edge "moves with the resource … and moves back on restore". The parent GUID is re-resolved
        // rather than remembered because a restore runs on the request path with the caller waiting,
        // which is the same reason the delete resolves it there — and unlike the unlink it is not
        // driven from a reminder against a resource that is already gone.
        var reparented = await relations.ReparentFromSubscriptionAsync(
            addressed.Id,
            await ParentIdOf(addressed),
            cancellationToken
        );

        if (reparented.TryGetError(out var reparentError)) {
            return Result<WriteAccepted>.Failure(reparentError);
        }

        trace.Enter(WriteStep.SubmitDesired);

        // ── AND NOW THE DATA PLANE COMES BACK, WHICH IS THE HALF THIS METHOD USED TO SKIP ────────
        //
        // ⚠ THIS WAS `CompleteAsync(Succeeded)` — one label move — AND THE COMMENT ABOVE IT SAID
        // "there was never anything to re-apply", WHICH WAS TRUE ONLY BECAUSE THE SOFT DELETE RAN NO
        // TEARDOWN. That is the defect two providers withdrew their recovery windows over: a resource
        // whose pods kept running after a delete the tenant was told had converged. A soft delete now
        // tears the data plane down like any other delete — see OperationGrain.DriveAsync — so a
        // restore has work to do, and this is it.
        //
        // ⚠ NO BODY IS SUBMITTED AND NONE IS ASKED FOR. The resource grain still holds the superset
        // the delete did not clear, and ReconcileDriver reads desired state from the grain rather than
        // from the operation spec, so `Desired: "{}"` below is not a body — it is the same placeholder
        // every delete and purge spec carries. What comes back is byte for byte what was there, which
        // is the property a recovery window is FOR: a restore that re-derived a body from anywhere
        // else would be handing the tenant a resource they did not have.
        //
        // ⚠ AND NO QUOTA IS RESERVED. docs/plan/08 § Soft delete: the committed amounts stayed
        // committed through the whole window precisely so "a restore that re-reserved would fail
        // against an allowance the tenant has spent in the meantime, which is a restore that works
        // only when it is not needed". So this operation carries no leases and returns nothing — the
        // arithmetic is untouched from create to purge, and failure class (a) has no way in.
        var operationId = Guid.NewGuid();

        var beginning = await Resource(addressed).BeginRestoreAsync(operationId);
        if (beginning.TryGetError(out var beginError)) {
            return Result<WriteAccepted>.Failure(beginError);
        }

        var snapshot = beginning.GetValueOrThrow();

        trace.Enter(WriteStep.StartOperation);

        var started = await Operation(addressed, operationId)
            .StartAsync(
                new() {
                    OperationId = operationId,
                    Kind = OperationKind.Restore,
                    ResourcePath = addressed.Id.Path,
                    ResourceId = addressed.Id.Id,
                    TenantId = addressed.Id.TenantId,
                    SubscriptionId = addressed.Id.SubscriptionId,
                    ApiVersion = addressed.ApiVersion.Value,
                    Desired = "{}",
                    QuotaLeaseIds = [],
                    CommittedQuota = [],
                    IndexClaimed = false,
                    ParentResourceId = await ParentIdOf(addressed),
                    Caller = request.Caller
                }
            );

        if (started.TryGetError(out var startError)) {
            return Result<WriteAccepted>.Failure(startError);
        }

        trace.Enter(WriteStep.EmitChanged);
        await EmitAsync(ResourceChangeKind.Updated, addressed, snapshot, cancellationToken);

        trace.Enter(WriteStep.Accepted);

        return Result<WriteAccepted>.Success(
            new() {
                OperationId = operationId,
                OperationUri = OperationUri(operationId),
                RetryAfterSeconds = ReconcileSchedule.InitialRetryAfterSeconds,
                Resource = snapshot,
                Trace = trace.Build()
            }
        );
    }

    /// <inheritdoc />
    public async Task<Result<WriteAccepted>> PurgeAsync(
        WriteRequest request,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(request);

        var trace = new WriteTraceBuilder();
        trace.Enter(WriteStep.ResolveRegistration);

        var resolved = await ResolveAsync(request, trace);
        if (resolved.TryGetError(out var resolveError)) {
            return Result<WriteAccepted>.Failure(resolveError);
        }

        var target = resolved.GetValueOrThrow();

        var parked = await RestorableAsync(target);
        if (parked.TryGetError(out var parkedError)) {
            return Result<WriteAccepted>.Failure(parkedError);
        }

        var addressed = target with { Id = target.Id.WithId(parked.GetValueOrThrow()) };

        trace.Enter(WriteStep.AuthorizationCheck);

        // ⚠ THE PURGE PERMISSION, NOT THE DELETE PERMISSION, AND THAT IS THE WHOLE SEPARATION.
        //
        // docs/plan/08 § Soft delete: Azure's
        // `Microsoft.KeyVault/locations/deletedVaults/purge/action` is in Key Vault Contributor's
        // notActions, "so 'may delete' and 'may destroy permanently' are genuinely separable rights and
        // a role can hold the first without the second". Checking DeletePermission here would make the
        // recovery window protect against nobody who could already delete — which is precisely the
        // caller it is there to protect against, because they are the one whose delete put the resource
        // here.
        //
        // ⚠ The read permission is still the second argument, so a caller who cannot see the
        // resource gets the same 404 as one for whom it does not exist. docs/plan/07 § The enforcement
        // seam has no third case, and "you may not purge this" would confirm there is something to
        // purge.
        var authorized = await authorizer.AuthorizeAsync(
            addressed.Id,
            addressed.Registration.PurgePermission,
            addressed.Registration.ReadPermission,
            request.Caller,
            true,
            cancellationToken
        );

        if (authorized.TryGetError(out var authError)) {
            return Result<WriteAccepted>.Failure(authError);
        }

        trace.Enter(WriteStep.Locks);

        var lockLevel = await locks.ResolveAsync(addressed.Id, cancellationToken);
        if (lockLevel.TryGetError(out var lockError)) {
            return Result<WriteAccepted>.Failure(lockError);
        }

        if (lockLevel.GetValueOrThrow() is LockLevel.CanNotDelete or LockLevel.ReadOnly) {
            return Result<WriteAccepted>.Failure(
                ErrorCode.ScopeLocked,
                $"'{request.Path}' is covered by a {lockLevel.GetValueOrThrow()} lock, so it cannot be "
                + "purged. A purge destroys more than a delete does, so a lock that refuses the delete "
                + "refuses this too — docs/plan/06 § Tags, locks."
            );
        }

        // The stored superset, which is both what purge protection is read out of and what the quota
        // return is derived from. Empty pointers is the whole stored shape rather than one
        // api-version's view of it — see CommittedBy.
        var stored = await Resource(addressed).GetAsync(addressed.ApiVersion.Value, []);
        if (stored.TryGetError(out var storedError)) {
            return Result<WriteAccepted>.Failure(storedError);
        }

        var snapshot = stored.GetValueOrThrow();

        // ── Purge protection, which is the one refusal that outranks the permission ──────────────
        //
        // ⚠ docs/plan/08 § Soft delete: "Purge protection is a further opt-in flag that cannot be
        // turned off once on, which is the only version of it that is worth anything." A flag whose
        // holder can clear it and then purge is one round-trip of protection, so the write path refuses
        // to clear it and this refuses to purge past it. Both halves, or neither is worth having.
        //
        // ⚠ It is checked AFTER the enforcement seam, so it tells an unauthorized caller nothing:
        // they were answered 404 above. To an authorized one it is a 409 that names what to do, which
        // is nothing — that is what the flag means.
        if (IsPurgeProtected(addressed.Registration, snapshot.Properties)) {
            return Result<WriteAccepted>.Failure(
                ErrorCode.Conflict,
                $"'{request.Path}' has purge protection enabled, so it cannot be purged before its "
                + "recovery window ends. The flag cannot be turned off once on — that is what makes it "
                + "worth anything — so there is no request that changes this answer. docs/plan/08 "
                + "§ Soft delete."
            );
        }

        trace.Enter(WriteStep.IndexClaim);

        // ⚠ THE NAME COMES BACK NOW, WHICH IS THE HARD DELETE'S ORDER AND FOR THE HARD DELETE'S
        // REASON. docs/plan/06 § Two-phase create: "release the index first (so the name is
        // immediately reusable), then tear down the data plane, then delete the grain state." A purge
        // is the delete this type did not do at the accept, so it does it in the same order — a tenant
        // who purged in order to re-create should not wait for their own teardown, which is the whole
        // argument for releasing first.
        var released = await Index(addressed).ReleaseAsync(addressed.Id.Id);
        if (released.TryGetError(out var releaseError)) {
            return Result<WriteAccepted>.Failure(releaseError);
        }

        trace.Enter(WriteStep.StartOperation);

        var operationId = Guid.NewGuid();

        var started = await Operation(addressed, operationId)
            .StartAsync(
                new() {
                    OperationId = operationId,
                    Kind = OperationKind.Purge,
                    ResourcePath = addressed.Id.Path,
                    ResourceId = addressed.Id.Id,
                    TenantId = addressed.Id.TenantId,
                    SubscriptionId = addressed.Id.SubscriptionId,
                    ApiVersion = addressed.ApiVersion.Value,
                    Desired = "{}",
                    QuotaLeaseIds = [],
                    // ⚠ THE AMOUNTS THE DELETE DID NOT GIVE BACK, DERIVED THE WAY IT DERIVED THEM.
                    // docs/plan/08 § Soft delete moves the return "from the delete's convergence to the
                    // purge", and it moves the WHOLE of it — QuotaMeter.Resources included — because a
                    // per-meter split reintroduces the partial restore. Re-derived here rather than
                    // copied off the delete's spec: that is a different grain which may be long gone,
                    // and the resource's stored body is still exactly what the create wrote, so the
                    // same AmountFor over the same JSON gives the same numbers.
                    CommittedQuota = CommittedBy(addressed.Registration, snapshot.Properties),
                    IndexClaimed = false,
                    ParentResourceId = await ParentIdOf(addressed),
                    Caller = request.Caller
                }
            );

        if (started.TryGetError(out var startError)) {
            return Result<WriteAccepted>.Failure(startError);
        }

        trace.Enter(WriteStep.EmitChanged);
        await EmitAsync(ResourceChangeKind.Deleting, addressed, snapshot, cancellationToken);

        trace.Enter(WriteStep.Accepted);

        return Result<WriteAccepted>.Success(
            new() {
                OperationId = operationId,
                OperationUri = OperationUri(operationId),
                RetryAfterSeconds = ReconcileSchedule.InitialRetryAfterSeconds,
                Resource = snapshot,
                Trace = trace.Build()
            }
        );
    }

    /// <summary>
    ///     The GUID of the soft-deleted resource at this address, or the canonical <c>404</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The failure is <c>NotFound</c> from the same helper every other absence uses, and
    ///     that identity is the property.</b> A name that never existed, a live resource, a soft-deleted
    ///     resource in another tenant and one whose window has passed all answer the same sentence.
    ///     Anything else here is the enumeration oracle docs/plan/07 § The enforcement seam closes,
    ///     reopened by the two verbs that know soft delete exists.
    /// </remarks>
    async Task<Result<Guid>> RestorableAsync(WriteTarget target) {
        if (target.Registration.SoftDeleteDays <= 0) {
            // ⚠ Not "this type has no recovery window". That is a fact a caller can read out of the
            // generated document, but as an ANSWER HERE it separates "wrong type" from "wrong name" —
            // and the pair of them is how a name gets enumerated.
            return NotFound<Guid>(target.Id.Path);
        }

        var parked = await Index(target).ResolveSoftDeletedAsync();

        return parked.IsSuccess ? parked : NotFound<Guid>(target.Id.Path);
    }

    /// <summary>
    ///     The refusal a write gets when it would turn purge protection off, or
    ///     <see langword="null" />.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Turning it ON is always allowed and turning it off never is</b>, which is what "opt-in,
    ///     and cannot be turned off once on" means as a state machine: one edge, no inverse. There is
    ///     deliberately no administrative override — an override is the bypass with a nicer name, and
    ///     the resource becomes purgeable on its own when the window ends anyway.
    /// </remarks>
    async Task<Error?> PurgeProtectionRefusalAsync(WriteRequest request, WriteTarget target, JsonElement body) {
        var stored = await Resource(target).GetAsync(target.ApiVersion.Value, []);

        if (stored.IsFailure || !IsPurgeProtected(target.Registration, stored.GetValueOrThrow().Properties)) {
            return null;
        }

        var incoming = MeterDerivation.Resolve(body, target.Registration.PurgeProtectionPointer);

        var clearing = request.Verb == WriteVerb.Put
            ? incoming is not { ValueKind: JsonValueKind.True }
            : incoming is { ValueKind: JsonValueKind.False };

        return clearing
            ? new Error(
                ErrorCode.Conflict,
                $"'{request.Path}' has purge protection enabled and it cannot be turned off — "
                + "docs/plan/08 § Soft delete: a flag whose holder can clear it and then purge is not a "
                + "protection. Send it as true, or wait for the recovery window to end.",
                target.Registration.PurgeProtectionPointer
            )
            : null;
    }

    /// <summary>Whether this resource has purge protection turned on.</summary>
    /// <remarks>
    ///     ⚠ <b>Absent, unparseable and non-boolean all read as OFF, and that is the only safe
    ///     direction even though it is the failing-open one.</b> The failing-closed reading is worse: a
    ///     type whose pointer named nothing would refuse every purge of every resource forever, and
    ///     there is no request that clears it. What keeps the fail-open from being silent is
    ///     <c>ProviderBuilder.CheckPurgeProtection</c>, which refuses at silo start a type whose
    ///     api-versions do not declare the pointer as a boolean — so a pointer that reads as absent
    ///     means the caller did not set the flag, which is what off means.
    /// </remarks>
    static bool IsPurgeProtected(ResourceTypeRegistration registration, string properties) {
        if (registration.PurgeProtectionPointer.Length == 0 || properties.Length == 0) {
            return false;
        }

        JsonDocument body;
        try {
            body = JsonDocument.Parse(properties);
        }
        catch (JsonException) {
            return false;
        }

        using (body) {
            return MeterDerivation.Resolve(body.RootElement, registration.PurgeProtectionPointer)
                is { ValueKind: JsonValueKind.True };
        }
    }

    /// <summary>
    ///     The GUID of the resource this address names as its parent, or <see cref="Guid.Empty" />.
    /// </summary>
    /// <remarks>
    ///     ⚠ Resolved on the request path, where somebody is still waiting, for the reason
    ///     <c>OperationSpec.ParentResourceId</c> gives: the unlink runs from a reminder after the
    ///     resource is gone, and a lookup there would fail every retry. A parent that no longer resolves
    ///     leaves this <see cref="Guid.Empty" /> rather than failing the request — the child is an
    ///     orphan, and refusing would strand it unpurgeable.
    /// </remarks>
    async Task<Guid> ParentIdOf(WriteTarget target) {
        if (target.Id.Parent is not { } parentAddress) {
            return Guid.Empty;
        }

        var bound = await Tenant(target)
            .GetGrain<IResourceIndexGrain>(GrainKeys.PathIndex(parentAddress))
            .ResolveAsync();

        return bound.IsSuccess ? bound.GetValueOrThrow() : Guid.Empty;
    }

    /// <inheritdoc />
    public async Task<Result<WriteAccepted>> ActionAsync(
        WriteRequest request,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(request);

        var trace = new WriteTraceBuilder();
        trace.Enter(WriteStep.ResolveRegistration);

        var resolved = await ResolveAsync(request, trace);
        if (resolved.TryGetError(out var resolveError)) {
            return Result<WriteAccepted>.Failure(resolveError);
        }

        var target = resolved.GetValueOrThrow();

        if (!target.Registration.TryGetAction(request.Action, out var action)) {
            return Result<WriteAccepted>.Failure(
                ErrorCode.InvalidRequestBody,
                $"'{request.Action}' is not an action on '{target.Id.Type}'. The declared actions are "
                + $"[{string.Join(", ", target.Registration.Actions.Select(x => x.Name))}]."
            );
        }

        // ── 2. Validate the action's body against the shape the action declares ─────────────────
        //
        // ⚠ An action's parameters go through the same validator a resource body does, and that is
        // what makes ActionRegistration.Request a contract rather than documentation. An action whose
        // request shape reached only the generated document would be an API that published a body it
        // did not check — the exact inversion of the tag defect, and just as invisible.
        //
        // ⚠ Only when the action declares one. An action with no declared request takes whatever it
        // is given, which is what every action did before the registry could say otherwise; refusing
        // an undeclared body here would break every action that already works.
        if (action.Request is { } requestSchema) {
            trace.Enter(WriteStep.ValidateBody);

            var problem = ValidateActionBody(request.Body, requestSchema, target.Id.Type, action.Name);
            if (problem is { } refusal) {
                return Result<WriteAccepted>.Failure(refusal);
            }
        }

        trace.Enter(WriteStep.AuthorizationCheck);

        var authorized = await authorizer.AuthorizeAsync(
            target.Id,
            action.Permission,
            target.Registration.ReadPermission,
            request.Caller,
            // A secret-returning action is a key export, which docs/plan/07 § Consistency puts in the
            // FullyConsistent row by name.
            action.Secret,
            cancellationToken
        );

        if (authorized.TryGetError(out var authError)) {
            return Result<WriteAccepted>.Failure(authError);
        }

        // ⚠ POST NEVER CREATES. docs/plan/08 § The write path, end to end: POST "appears only for
        // actions on an existing resource … never for creation." This check is here, in the manager,
        // rather than in each action's handler — twenty handlers is twenty chances to forget, and the
        // one that forgets is a create through a verb with no idempotency guarantee.
        if (!target.Exists) {
            return NotFound<WriteAccepted>(request.Path);
        }

        // ── An action that does no work answers here, and never starts an operation ──────────────
        //
        // ⚠ THE SECRET MUST NOT REACH THE OPERATION RECORD, AND NOT STARTING ONE IS HOW THAT IS
        // GUARANTEED RATHER THAN REMEMBERED.
        //
        // OperationSpec and OperationStatus are DURABLE and are readable by anyone holding `read` on
        // the resource — OperationGrain writes both to StorageTiers.Durable and IOperationReader
        // serves the status to any caller who can read the resource. `listKeys` checks its OWN
        // permission (docs/plan/07 § The enforcement seam puts a key export in the fully-consistent
        // row precisely because `read` is not enough for it), so a handler that returned the
        // credential through the operation's public status would hand it to every reader of the
        // resource AND write it into a backup of the durable tier — defeating the permission split
        // twice over, without touching the permission itself.
        //
        // A handler's result therefore travels on WriteAccepted.ActionResponse, which is the reply to
        // one caller on one request and is persisted nowhere. `ActionDispatchTests` asserts the value is in
        // exactly one of the two, and a sabotage run that routed listKeys through the operation path
        // turned that suite red.
        //
        // ⚠ And it is the right ANSWER as well as the safe one: ActionRegistration.LongRunning
        // exists because `restart` takes a minute and `listKeys` reads two strings. An action that
        // does no work answering 202-and-poll is a generated client that polls for a value it was
        // already entitled to have.
        if (!action.LongRunning) {
            return await CompleteActionAsync(target, action, request, trace, cancellationToken);
        }

        trace.Enter(WriteStep.StartOperation);

        var operationId = Guid.NewGuid();
        var started = await Operation(target, operationId)
            .StartAsync(
                new() {
                    OperationId = operationId,
                    Kind = OperationKind.Action,
                    ResourcePath = target.Id.Path,
                    ResourceId = target.Id.Id,
                    TenantId = target.Id.TenantId,
                    SubscriptionId = target.Id.SubscriptionId,
                    ApiVersion = target.ApiVersion.Value,
                    Desired = request.Body,
                    Action = action.Name,
                    Caller = request.Caller
                }
            );

        if (started.TryGetError(out var startError)) {
            return Result<WriteAccepted>.Failure(startError);
        }

        trace.Enter(WriteStep.Accepted);

        var snapshot = await Resource(target).GetAsync(target.ApiVersion.Value, ReadablePointers(target.Schema));

        return Result<WriteAccepted>.Success(
            new() {
                OperationId = operationId,
                OperationUri = OperationUri(operationId),
                RetryAfterSeconds = ReconcileSchedule.InitialRetryAfterSeconds,
                Resource = snapshot.ValueOrDefault ?? new(),
                Trace = trace.Build()
            }
        );
    }

    /// <summary>
    ///     Runs a synchronous action's handler and shapes the <c>200</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The resource is read for the handler and again for the reply, and the two reads are
    ///         different things.</b> <c>GetReconcileInputAsync</c> gives the stored <i>superset</i> —
    ///         which is what a handler wants, because it reads facts about the resource rather than
    ///         rendering it — and <c>GetAsync</c> gives the api-version's projection, which is what the
    ///         caller gets back. Handing the handler the projection would hide a property from it the
    ///         moment somebody added one at a newer date.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>No <c>ResourceChanged</c> is emitted.</b> An action that does no work changed
    ///         nothing, and a change notification for a read would wake every portal blade watching the
    ///         resource every time somebody looked at its keys.
    ///     </para>
    /// </remarks>
    async Task<Result<WriteAccepted>> CompleteActionAsync(
        WriteTarget target,
        ActionRegistration action,
        WriteRequest request,
        WriteTraceBuilder trace,
        CancellationToken cancellationToken
    ) {
        var input = await Resource(target).GetReconcileInputAsync();
        if (input.TryGetError(out var inputError)) {
            return Result<WriteAccepted>.Failure(inputError);
        }

        using var body = JsonDocument.Parse(
            string.IsNullOrWhiteSpace(request.Body) ? "{}" : request.Body
        );

        var invoked = await actions.InvokeAsync(
            target.Id,
            target.Registration,
            action,
            input.GetValueOrThrow(),
            body.RootElement,
            cancellationToken
        );

        if (invoked.TryGetError(out var invokeError)) {
            return Result<WriteAccepted>.Failure(invokeError);
        }

        trace.Enter(WriteStep.Accepted);

        var snapshot = await Resource(target).GetAsync(target.ApiVersion.Value, ReadablePointers(target.Schema));

        return Result<WriteAccepted>.Success(
            new() {
                // ⚠ Guid.Empty and no OperationUri, because there is no operation. The gateway keys
                // its 200-versus-202 on Completed rather than on this, so an action that answered
                // directly cannot also advertise something to poll.
                OperationId = Guid.Empty,
                RetryAfterSeconds = 0,
                Resource = snapshot.ValueOrDefault ?? new(),
                ActionResponse = invoked.GetValueOrThrow(),
                Completed = true,
                Trace = trace.Build()
            }
        );
    }

    /// <summary>
    ///     The refusal a delete gets while the resource still has children.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The count and the type are the whole point of the message.</b> docs/plan/08
    ///         § Deleting a parent resource that has children: <i>"The refusal names how many children
    ///         there are and their type, so the caller can go and delete them"</i>, and the ⚠ beside it
    ///         says why a bare refusal is not enough — refusing creates a real failure mode where a
    ///         child whose own delete is stuck holds its parent undeletable, and the only thing that
    ///         makes that recoverable is being able to see what is holding it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Types, not names.</b> The counter is per type and deliberately not a list (see
    ///         <see cref="ChildTypeCount" />), so this cannot name the children. It does not need to:
    ///         the caller owns the parent, the children live under the parent's own address, and a
    ///         <c>GET</c> on that address lists them. What the caller cannot work out for themselves is
    ///         <i>whether</i> anything is there and roughly how much, which is exactly what this says.
    ///     </para>
    /// </remarks>
    static string ChildRefusal(string path, ImmutableArray<ChildTypeCount> children) {
        var parts = children.Select(
            x => string.Create(
                CultureInfo.InvariantCulture,
                $"{x.Count} of type '{x.Type}'"
            )
        );

        return $"'{path}' still has child resources — {string.Join(", ", parts)} — so it cannot be "
            + "deleted. Delete the children first and retry. A resource group is a declared lifecycle "
            + "boundary and a parent resource is not, so this refuses rather than cascading: a cascade "
            + "would tear down resources the caller never named, with the data in them — docs/plan/08 "
            + "§ Deleting a parent resource that has children.";
    }

    /// <summary>
    ///     Checks an action's <c>POST</c> body against the shape the action declares.
    /// </summary>
    /// <returns>The failure, or <see langword="null" /> when the body is acceptable.</returns>
    /// <remarks>
    ///     ⚠ <b>Full requiredness, and no tag bag.</b> A <c>POST</c> to an action is not a merge —
    ///     there is nothing to merge it into — so every required parameter must be present, exactly as
    ///     on a <c>PUT</c>. Tags belong to a resource and not to an invocation, so the bag is not
    ///     allowed here even for a type that carries tags.
    /// </remarks>
    static Error? ValidateActionBody(string requestBody, ResourceSchema schema, ResourceTypeName type, string action) {
        // An action with a declared request and an empty body is validated as `{}`, so a missing
        // required parameter is reported as missing rather than as "the body is not JSON".
        var text = string.IsNullOrWhiteSpace(requestBody) ? "{}" : requestBody;

        JsonDocument parsed;
        try {
            parsed = JsonDocument.Parse(text);
        }
        catch (JsonException exception) {
            return new(
                ErrorCode.InvalidRequestBody,
                $"The body of '{type}/{action}' is not valid JSON: {exception.Message}",
                ""
            );
        }

        using (parsed) {
            var validated = schema.Validate(parsed.RootElement);
            return validated.TryGetError(out var error) ? error : null;
        }
    }

    // ── Steps 3 through 12 of the write path ───────────────────────────────────────────────────

    async Task<Result<WriteAccepted>> ContinueWriteAsync(
        WriteRequest request,
        WriteTarget target,
        JsonElement body,
        WriteTraceBuilder trace,
        CancellationToken cancellationToken
    ) {
        // ── 3. ReBAC Check — BEFORE quota, BEFORE the index claim, BEFORE any provider ──────────
        //
        // ⚠ docs/plan/07 § The enforcement seam: "404, never 403, on a resource the caller cannot
        // read. A 403 confirms the resource exists, which is an enumeration oracle: a competitor can
        // discover a customer's resource names by probing. 403 is returned only when the caller can
        // read the object but not perform the action." IResourceAuthorizer takes both permissions for
        // exactly that reason, and nothing below this line runs when it refuses.
        trace.Enter(WriteStep.AuthorizationCheck);

        var authorized = await authorizer.AuthorizeAsync(
            target.Id,
            target.Registration.WritePermission,
            target.Registration.ReadPermission,
            request.Caller,
            false,
            cancellationToken
        );

        if (authorized.TryGetError(out var authError)) {
            return Result<WriteAccepted>.Failure(authError);
        }

        // ── 4. Locks, inherited from the group, the subscription and the management group ───────
        trace.Enter(WriteStep.Locks);

        var lockLevel = await locks.ResolveAsync(target.Id, cancellationToken);
        if (lockLevel.TryGetError(out var lockError)) {
            return Result<WriteAccepted>.Failure(lockError);
        }

        if (lockLevel.GetValueOrThrow() == LockLevel.ReadOnly) {
            return Result<WriteAccepted>.Failure(
                ErrorCode.ScopeLocked,
                $"'{request.Path}' is covered by a ReadOnly lock, so writes are refused — "
                + "docs/plan/06 § Tags, locks, and the small stuff that is not small."
            );
        }

        // ── 4b. Purge protection, once on, may not be turned off ────────────────────────────────
        //
        // ⚠ THE OTHER HALF OF THE FLAG, AND WITHOUT IT THE FLAG IS WORTH NOTHING.
        //
        // docs/plan/08 § Soft delete: "Purge protection is a further opt-in flag that cannot be turned
        // off once on, which is the only version of it that is worth anything." PurgeAsync refuses a
        // protected resource; that refusal is one PATCH away from being bypassed unless this line
        // exists, and an attacker who can write can already do the delete. One round-trip of protection
        // is not protection.
        //
        // ⚠ AGAINST THE STORED VALUE AND NOT AGAINST THE REQUEST'S OWN PREVIOUS STATE. The question is
        // "is it on right now", which only the resource grain knows; a caller who sends a full PUT
        // omitting the flag is asking for it to be cleared just as plainly as one who sends `false`,
        // which is why a PUT is checked for the presence of `true` rather than the absence of `false`.
        // A PATCH is the opposite — a merge patch omits everything it is not changing — so only an
        // explicit `false` is a refusal there. That asymmetry is the whole difference between the
        // verbs, and it is the same one step 2 makes about requiredness.
        if (target.Exists && target.Registration.PurgeProtectionPointer.Length > 0) {
            var refusal = await PurgeProtectionRefusalAsync(request, target, body);
            if (refusal is { } locked) {
                return Result<WriteAccepted>.Failure(locked);
            }
        }

        // ── 5. Policy — deny, modify, audit ─────────────────────────────────────────────────────
        trace.Enter(WriteStep.Policy);

        var decision = await policy.EvaluateAsync(
            target.Id,
            target.ApiVersion.Value,
            request.Body,
            request.Caller,
            cancellationToken
        );

        if (!decision.Permits) {
            return Result<WriteAccepted>.Failure(
                decision.Error
                ?? new Error(
                    ErrorCode.PolicyViolation,
                    $"Policy denied this write to '{request.Path}' and gave no reason. A denial "
                    + "without a reason is unactionable; the evaluator should name the policy."
                )
            );
        }

        var effectiveBody = decision.ModifiedBody ?? request.Body;

        if (decision.Effect == PolicyEffect.Modify) {
            // ⚠ A modified body is re-validated. A policy that produced an invalid body would
            // otherwise reach the provider unchecked, which is a policy engine granting itself the
            // right to bypass the schema.
            using var modified = JsonDocument.Parse(effectiveBody);
            var revalidated = target.Schema.Validate(
                modified.RootElement,
                request.Verb == WriteVerb.Put,
                target.Registration.SupportsTags
            );
            if (revalidated.TryGetError(out var modifiedError)) {
                return Result<WriteAccepted>.Failure(
                    ErrorCode.PolicyViolation,
                    $"Policy rewrote the body of '{request.Path}' into something the "
                    + $"'{target.ApiVersion}' schema refuses: {modifiedError.Message}",
                    modifiedError.Target
                );
            }
        }

        // ── 6. Quota ────────────────────────────────────────────────────────────────────────────
        //
        // ⚠ Reserved only for a CREATE. An update does not draw new quota — the resource is already
        // counted — and reserving on every PUT would make a tenant's own idempotent retry consume
        // their allowance. docs/plan/06 § Quota: "Reservation, not a counter increment."
        trace.Enter(WriteStep.Quota);

        var leases = ImmutableArray<Guid>.Empty;
        var committed = ImmutableArray<QuotaCommitment>.Empty;
        var operationId = Guid.NewGuid();
        var resourceId = target.Exists ? target.Id.Id : Guid.NewGuid();

        if (!target.Exists) {
            var reserved = await ReserveAsync(target, body, operationId);
            if (reserved.TryGetError(out var quotaError)) {
                return Result<WriteAccepted>.Failure(quotaError);
            }

            (leases, committed) = reserved.GetValueOrThrow();
        }

        var addressed = target.Id.WithId(resourceId);
        var resolvedTarget = target with { Id = addressed };

        // ── 7. Claim the name. Two-phase create, step 1 ─────────────────────────────────────────
        trace.Enter(WriteStep.IndexClaim);

        var claimed = await Index(resolvedTarget).TryClaimAsync(addressed, resourceId);
        if (claimed.TryGetError(out var claimError)) {
            // ⚠ The lease is released on the way out. docs/plan/06 § Quota: "the lease is released if
            // the operation fails". Losing the name race and keeping the quota would let a burst of
            // colliding creates consume a subscription's allowance with nothing to show for it.
            await ReleaseAsync(resolvedTarget, leases);
            return Result<WriteAccepted>.Failure(claimError);
        }

        // ── 8. The ReBAC parent edge — BEFORE the resource is durable ───────────────────────────
        //
        // ⚠ THE STEP AND ITS POSITION ARE THE DECISION, AND THE POSITION IS THE HALF THAT MATTERS.
        //
        // CyberCloudSchema gives a resource `Role(owner, This | From(parent, owner))`, so a resource
        // is reachable from the role assignments on its group only through a
        // `resource:{id}#parent@resourceGroup:{sub}-{rg}` tuple. Nothing wrote one, so a create
        // succeeded and the creator then got 404 on what they had just created.
        //
        // It goes HERE, between the index claim and the durable write, because:
        //
        //   • the resource GUID already exists — step 6 minted it — and the name is already ours,
        //     so there is nothing left to wait for;
        //   • AFTER SubmitDesiredAsync there is a window in which the resource is durable and
        //     unreadable. A silo lost inside that window leaves it durable and unreadable FOREVER,
        //     and no reminder exists to notice: the operation grain has not been started yet either;
        //   • before it, a failure is a clean refusal. Nothing durable was written, the quota lease
        //     is released below, the index claim expires on its own, and the caller gets an error
        //     rather than a 202 for a resource they cannot see.
        //
        // The cost is the mirror-image leak: a silo lost between this line and the durable write
        // leaves a parent tuple pointing at a GUID no path will ever resolve to. That tuple grants
        // nothing — `resource:{unreachable}` is not addressable, the index claim expires and the next
        // create of the same path mints a different GUID — so it is inert storage rather than an
        // authorization defect. Trading a permanent correctness failure for an inert row is the whole
        // argument, and the row is swept on the failure paths below.
        //
        // ⚠ NOT DEFERRED TO THE OPERATION GRAIN, which is the other candidate and is a worse one.
        // The operation grain is durable and re-drivable — genuinely the right home for work that has
        // to converge — but it does not exist until step 10, which is after step 9. Putting the edge
        // there is putting it after the durable write by construction, which is the window this step
        // exists to close. The operation grain does own the DELETE side, where there is no such
        // ordering problem and where re-drivability is exactly what is needed.
        //
        // ⚠ WRITTEN ONLY FOR A CREATE, in the shape step 6 above already uses: the step is entered
        // either way and the work inside it is create-only. A resource's parent cannot change — the
        // address is what the index binds and a different address is a different resource — so the
        // edge is a create-time fact and re-writing it on every PUT only ever rewrote a tuple that
        // was already there.
        //
        // ⚠ FOR A CHILD IT WOULD BE WORSE THAN REDUNDANT. `target.ParentId` is resolved by step 1's
        // parent check, which runs on the create and deliberately not on an update — re-checking every
        // write would turn a deleted parent into a frozen child answering 404 for a resource that
        // plainly exists, which ParentExistenceTests pins against. So on an update a child's parent
        // GUID is not known here, and linking anyway would fall back to the resource group and leave
        // the child with TWO `parent` tuples: the correct one from its create and a group one beside
        // it. CheckGrain.WalkAncestorsAsync takes the first of them, so which grant applied would
        // depend on store order.
        trace.Enter(WriteStep.LinkParent);

        if (!target.Exists) {
            var linked = await relations.LinkToParentAsync(addressed, resolvedTarget.ParentId, cancellationToken);
            if (linked.TryGetError(out var linkError)) {
                await ReleaseAsync(resolvedTarget, leases);
                return Result<WriteAccepted>.Failure(linkError);
            }
        }

        // ── 9. Write durable desired state ──────────────────────────────────────────────────────
        trace.Enter(WriteStep.SubmitDesired);

        var submitted = await Resource(resolvedTarget)
            .SubmitDesiredAsync(
                new() {
                    Path = addressed.Path,
                    ApiVersion = resolvedTarget.ApiVersion.Value,
                    Body = effectiveBody,
                    Verb = request.Verb,
                    OperationId = operationId,
                    IfMatch = request.IfMatch,
                    Caller = request.Caller,
                    Tags = TagsFrom(body, resolvedTarget.Registration),
                    Location = LocationFrom(body),
                    ClusterId = ClusterFrom(body, resolvedTarget.Registration),
                    DeclaredPointers = Pointers(resolvedTarget.Schema),
                    ReadablePointers = ReadablePointers(resolvedTarget.Schema)
                }
            );

        if (submitted.TryGetError(out var submitError)) {
            await ReleaseAsync(resolvedTarget, leases);

            // ⚠ THE ONLY FAILURE THAT UNWRITES THE EDGE, AND THE RULE IS "did a resource survive?".
            //
            // This branch is the one place after step 8 where nothing durable exists: the desired
            // write is what creates the resource, and it did not happen. So the tuple written a
            // moment ago points at a GUID that will never be a resource, and it goes.
            //
            // Every LATER failure — StartAsync, the index confirm — is past the durable write, and
            // there the edge STAYS. A resource that exists with a dangling-looking parent is a
            // resource its owner can still see, delete and clean up; a resource that exists with no
            // parent is invisible to everyone including the reaper's operator, which is the defect
            // this step was added to fix. Inert tuple over invisible resource, at every fork.
            //
            // ⚠ Only for a CREATE. On an update the edge was already there and belongs to a resource
            // that is still alive; removing it because this write failed would take an existing
            // resource away from its owner.
            if (!target.Exists) {
                _ = await relations.UnlinkFromParentAsync(addressed, resolvedTarget.ParentId, cancellationToken);
            }

            return Result<WriteAccepted>.Failure(submitError);
        }

        var snapshot = submitted.GetValueOrThrow();

        // ⚠ THE NO-OP. docs/plan/06 § Two-phase create: "the caller retries the PUT — which is
        // idempotent because PUT with the same body on an existing resource is a no-op, which is
        // exactly why the API is PUT and not POST."
        //
        // The grain reports it by leaving the resource Succeeded rather than moving it to Updating.
        // No operation is started, nothing is emitted, and the response carries NoOp so the gateway
        // can answer 200 rather than 202. Starting an operation that had nothing to do would leave a
        // Succeeded operation per retry, and a poller watching one of them would see a resource
        // "provisioning" that never changed.
        if (snapshot.ProvisioningState == ProvisioningState.Succeeded && target.Exists) {
            await ReleaseAsync(resolvedTarget, leases);
            return Result<WriteAccepted>.Success(
                new() {
                    OperationId = Guid.Empty,
                    OperationUri = string.Empty,
                    RetryAfterSeconds = 0,
                    Resource = snapshot,
                    Trace = trace.Build(),
                    NoOp = true
                }
            );
        }

        // ── 10. Start the operation, then confirm the claim ─────────────────────────────────────
        trace.Enter(WriteStep.StartOperation);

        var started = await Operation(resolvedTarget, operationId)
            .StartAsync(
                new() {
                    OperationId = operationId,
                    Kind = target.Exists ? OperationKind.Update : OperationKind.Create,
                    ResourcePath = addressed.Path,
                    ResourceId = resourceId,
                    TenantId = addressed.TenantId,
                    SubscriptionId = addressed.SubscriptionId,
                    ApiVersion = resolvedTarget.ApiVersion.Value,
                    Desired = effectiveBody,
                    QuotaLeaseIds = leases,
                    // ⚠ What the leases above will COMMIT to, recorded at the moment the amounts are
                    // known. A create does not read this back — CommitAsync works off the lease ids —
                    // but recording it is what makes the spec a complete account of the quota this
                    // operation moved, which is the property the delete path needs on its own side.
                    CommittedQuota = committed,
                    IndexClaimed = true,
                    // ⚠ What the delete will unlink with. Empty for a top-level resource and for an
                    // update, which is correct in both cases: the first has no parent resource, and
                    // the second did not write an edge to begin with.
                    ParentResourceId = resolvedTarget.ParentId,
                    Caller = request.Caller
                }
            );

        if (started.TryGetError(out var startError)) {
            await ReleaseAsync(resolvedTarget, leases);
            return Result<WriteAccepted>.Failure(startError);
        }

        // ⚠ Two-phase create, step 3: "Confirm the claim … converts the lease into a permanent
        // binding." It goes HERE — after the resource grain and the operation exist — because that is
        // the order docs/plan/06 § Two-phase create fixes, and because the failure mode it buys is
        // the good one: a silo that dies before this leaves a claim that expires and frees the name,
        // and an orphaned resource grain the per-subscription reaper sweeps.
        var confirmed = await Index(resolvedTarget).ConfirmAsync(resourceId);
        if (confirmed.TryGetError(out var confirmError)) {
            logger.LogError(
                "Confirming the index claim for {Path} failed after the resource and operation were "
                + "created: {Message}. The claim will expire and the resource grain will be swept.",
                addressed.Path,
                confirmError.Message
            );

            await ReleaseAsync(resolvedTarget, leases);
            return Result<WriteAccepted>.Failure(confirmError);
        }

        // ── The parent's child counter, which is what makes its delete refusable ────────────────
        //
        // ⚠ AFTER THE CONFIRM, ON THE CREATE ONLY, AND NOT ALLOWED TO FAIL THE WRITE.
        //
        // The counter read by DeleteAsync's gate is maintained here and released by the child's own
        // operation grain once CompleteDeleteAsync has run — docs/plan/08 § Deleting a parent resource
        // that has children asks for exactly that, "a per-parent child counter maintained
        // transactionally where the index claim and release already happen".
        //
        // ⚠ THE POSITION IS A CHOICE BETWEEN TWO LEAKS AND THIS IS THE RECOVERABLE ONE.
        // Incrementing BEFORE the durable write means a create that then fails leaves the parent's
        // count high with no child behind it — and a count that is high for a child that never existed
        // is never decremented by anything, so the parent answers 409 to its own delete forever and
        // only an operator can clear it. Incrementing after the confirm means a silo lost in the
        // microseconds between leaves the count low, which is the orphan this gate is closing —
        // undesirable, but no worse than the behaviour that existed before the gate, and it heals the
        // moment the child is deleted and re-created. Unrecoverable-forever loses to
        // as-bad-as-yesterday, which is the same trade step 8 makes one screen up.
        //
        // ⚠ For the same reason it does not fail the write. Everything durable already exists; a 500
        // here would tell the caller their create failed when it plainly succeeded, and the only thing
        // actually lost is the parent's protection against being deleted out from under this child.
        //
        // ⚠ Only when there IS a parent resource. A top-level type's parent is the resource GROUP,
        // whose delete cascades by design (docs/plan/06 § The hierarchy) and needs no counter.
        if (!target.Exists && addressed.Parent is { } parentAddress) {
            var registered = await Tenant(resolvedTarget)
                .GetGrain<IResourceIndexGrain>(GrainKeys.PathIndex(parentAddress))
                .AddChildAsync(addressed.Type);

            if (registered.TryGetError(out var registerError)) {
                logger.LogError(
                    "Registering {Path} against its parent's child counter failed: {Message}. The "
                    + "resource exists and is usable; what is lost is that its parent can now be "
                    + "deleted without the 409 docs/plan/08 § Deleting a parent resource that has "
                    + "children requires, leaving this resource an orphan.",
                    addressed.Path,
                    registerError.Message
                );
            }
        }

        // ── 11. Emit resource-changed ───────────────────────────────────────────────────────────
        trace.Enter(WriteStep.EmitChanged);

        await EmitAsync(
            target.Exists ? ResourceChangeKind.Updated : ResourceChangeKind.Created,
            resolvedTarget,
            snapshot,
            cancellationToken
        );

        // ── 12. 202 Accepted ────────────────────────────────────────────────────────────────────
        trace.Enter(WriteStep.Accepted);

        return Result<WriteAccepted>.Success(
            new() {
                OperationId = operationId,
                OperationUri = OperationUri(operationId),
                RetryAfterSeconds = ReconcileSchedule.InitialRetryAfterSeconds,
                Resource = snapshot,
                Trace = trace.Build()
            }
        );
    }

    // ── The operation poll ─────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Two grain calls, and the second one is the enforcement seam.</b> The operation
    ///         grain is reached through <c>ForTenant</c> with the tenant off the token, which is the
    ///         only thing closing the cross-tenant read; then the resource the operation names is put
    ///         to <see cref="IResourceAuthorizer" /> with the read permission on both arguments, which
    ///         makes every refusal on this path a <c>404</c> — docs/plan/07 § The enforcement seam has
    ///         no third case for a <c>GET</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What this deliberately does NOT do is read the resource.</b> The question a poll
    ///         asks is "may this caller see this operation?", and the answer is "may they read its
    ///         resource?" — which the check answers on its own. Reading the resource to obtain the
    ///         answer, which is what the gateway's own reader had to do while this method did not
    ///         exist, adds an index resolve, a resource-grain call and an api-version projection to
    ///         every poll of every operation. <c>cyc --wait</c> polls a nine-minute cluster create
    ///         continuously.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The resource GUID comes off the operation, not out of the index.</b>
    ///         <see cref="OperationStatus.ResourceId" /> exists for this: a ReBAC check built from the
    ///         path alone would carry <see cref="Guid.Empty" /> and
    ///         <c>ReBacResourceAuthorizer</c> would fall back to the parent resource group, which is a
    ///         different question with a different answer. An operation that names no resource — one
    ///         that was never started, or one from a peer that predates the member — is a <c>404</c>
    ///         rather than a check against something weaker.
    ///     </para>
    /// </remarks>
    public async Task<Result<OperationStatus>> GetOperationAsync(
        Guid operationId,
        CallerContext caller,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(caller);

        var status = await grains
            .ForTenant(caller.TenantId.ToString("D", CultureInfo.InvariantCulture))
            .GetGrain<IOperationGrain>(GrainKeys.Operation(operationId))
            .GetAsync();

        if (status.TryGetError(out var statusError)) {
            return Result<OperationStatus>.Failure(statusError);
        }

        var value = status.GetValueOrThrow();

        if (value.ResourceId == Guid.Empty || value.ResourcePath.Length == 0) {
            return OperationNotFound(operationId);
        }

        var parsed = ResourceId.ParsePath(value.ResourcePath);
        if (parsed.TryGetError(out _)) {
            // Unreachable: the path was produced by ResourceId.Path and is parsed by its inverse. If
            // it ever were reachable, the honest answer is the one an unanswerable check gets.
            return OperationNotFound(operationId);
        }

        var address = parsed.GetValueOrThrow().WithId(value.ResourceId);

        // ⚠ TryGetType rather than Resolve, because there is no api-version to resolve against.
        // docs/plan/10 § API versioning makes the parameter required on every request, but on
        // /operations/{opId} it selects the response RENDERING; nothing here projects a body, so
        // asking the registry for a schema would be asking a question this method does not have an
        // input for. ReadPermission is declared per type and not per version, which is the whole
        // reason this is decidable without one.
        if (!registry.TryGetType(address.Type, out var registration)) {
            // A resource type the registry no longer serves. The operation is real and its resource
            // may be too, but there is no declared read permission to check against — and inventing
            // one, in either direction, is worse than the 404.
            return OperationNotFound(operationId);
        }

        var authorized = await authorizer.AuthorizeAsync(
            address,
            registration.ReadPermission,
            registration.ReadPermission,
            caller,
            false,
            cancellationToken
        );

        return authorized.TryGetError(out var authError)
            ? Result<OperationStatus>.Failure(authError)
            : Result<OperationStatus>.Success(value);
    }

    /// <summary>
    ///     The <c>404</c> an operation gets, worded like every other absence.
    /// </summary>
    /// <remarks>
    ///     ⚠ Identical for "no such operation", "another tenant's operation" and "an operation whose
    ///     resource you cannot read". Three different messages would be the enumeration oracle the
    ///     status code closed.
    /// </remarks>
    static Result<OperationStatus> OperationNotFound(Guid operationId) =>
        Result<OperationStatus>.Failure(
            ErrorCode.ResourceNotFound,
            $"Operation {operationId:D} does not exist."
        );

    // ── Step 1's parts ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Parses the path, checks the tenant and the subscription, resolves the identity through the
    ///     index, looks the type and version up, and — for a child type being created — checks that
    ///     the parent its address names is a resource that exists.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The two ownership checks are the first two things that happen and they are in this
    ///     order on purpose.</b> Everything below them describes the platform to the caller — the
    ///     registry names api-versions, the index says whether a name is taken — and a description
    ///     handed out through a path the caller does not own is an oracle even when no data comes
    ///     with it. The isolation suite's <c>OracleTests</c> drive exactly that.
    /// </remarks>
    async Task<Result<WriteTarget>> ResolveAsync(WriteRequest request, WriteTraceBuilder trace) {
        _ = trace;

        var parsed = ResourceId.ParsePath(request.Path);
        if (parsed.TryGetError(out var pathError)) {
            return Result<WriteTarget>.Failure(pathError);
        }

        var address = parsed.GetValueOrThrow();

        if (address.TenantId != request.Caller.TenantId) {
            // ⚠ 404 rather than 403, for the same enumeration-oracle reason as the ReBAC seam: a
            // cross-tenant path that answered "forbidden" would confirm the other tenant's resource
            // exists. The isolation suite (docs/plan/03) drives exactly this.
            return NotFound<WriteTarget>(request.Path);
        }

        // ── The subscription is real, and it is one of THIS tenant's ────────────────────────────
        //
        // ⚠ THE PATH'S SUBSCRIPTION USED TO BE ACCEPTED WITHOUT BEING LOOKED AT.
        // /tenants/{mine}/subscriptions/{theirs}/… parsed, passed the tenant comparison, and ran the
        // whole write path against a subscription GUID that was somebody else's label. It leaked
        // nothing — every grain below here is reached through ForTenant(caller), so the quota, the
        // index and the resource all landed in the CALLER's tenant under a foreign-looking id, and
        // the isolation suite proved that. What it meant is that subscription ids were never
        // validated at all, which stops being survivable the moment billing or quota reporting reads
        // one: an invoice line, a usage report or a quota dashboard keyed on an id nobody checked is
        // a number attributed to a subscription that does not exist.
        //
        // ⚠ THE ANSWER IS 404 AND IS THE SAME 404 AS "no such resource" — byte for byte, from the
        // same helper. docs/plan/07 § The enforcement seam: "404, never 403 … A 403 confirms the
        // resource exists, which is an enumeration oracle." A subscription is exactly as enumerable
        // as a resource: SubscriptionNotFound here would tell an attacker which GUIDs are live
        // subscriptions in someone else's tenant, one probe at a time. A subscription in another
        // tenant and one that never existed are indistinguishable, and that identity is the property.
        //
        // ⚠ The grain is reached through ForTenant(caller's tenant), which is what makes "belongs to
        // this tenant" and "exists" the same question — ADR-002 puts the tenant in the key, so the
        // victim's subscription grain is simply not addressable from here and reads as absent.
        var subscription = await grains
            .ForTenant(address.TenantId.ToString("D", CultureInfo.InvariantCulture))
            .GetGrain<ISubscriptionGrain>(GrainKeys.Subscription(address.SubscriptionId))
            .GetAsync();

        if (subscription.IsFailure) {
            return NotFound<WriteTarget>(request.Path);
        }

        var resolution = registry.Resolve(address.Type, request.ApiVersion);
        if (resolution.TryGetError(out var registryError)) {
            return Result<WriteTarget>.Failure(registryError);
        }

        var found = resolution.GetValueOrThrow();

        // docs/plan/06 § Identifiers: a parsed path yields Guid.Empty, and "resolving it to a real
        // identity is a lookup through IResourceIndexGrain". Only a CONFIRMED binding resolves, so a
        // name under an unexpired claim reads as "does not exist" — which is what it is.
        var index = grains
            .ForTenant(address.TenantId.ToString("D", CultureInfo.InvariantCulture))
            .GetGrain<IResourceIndexGrain>(GrainKeys.PathIndex(address));

        var existing = await index.ResolveAsync();

        // ── The parent resource is real ─────────────────────────────────────────────────────────
        //
        // ⚠ A CHILD'S PARENT USED TO BE A SEGMENT NOBODY LOOKED AT. This method validated the tenant,
        // the subscription, the type and the api-version, and never the parent — so
        // …/widgets/{gone}/gadgets/{name} was created without complaint, and every layer below here
        // agreed: the name was free, the quota was there, the claim succeeded, the reconciler ran.
        //
        // ⚠ AND THE EDGE STEP 8 WRITES MAKES IT WORSE THAN A DANGLING REFERENCE.
        // IResourceRelationWriter.LinkToParentAsync derives the ReBAC `parent` edge from this address
        // and nothing else, so an orphan's edge points at a resource that does not exist and the
        // child inherits permission from nothing. docs/plan/12 § Child resources chose the
        // interleaved grammar *because* the flattened one could not express the parent — leaving the
        // parent unchecked spends that decision and keeps the failure it was meant to remove.
        //
        // ⚠ THE ANSWER IS 404 AND IS THE SAME 404 AS "no such resource" — byte for byte, from the
        // same helper, for the reason the subscription check above gives and one more besides. This
        // runs BEFORE the enforcement seam, so a caller who may write in a resource group but may not
        // read a particular widget would otherwise learn, one probe at a time, which widget names are
        // live: ParentNotFound would confirm the parent's absence and its silence would confirm the
        // parent's existence. docs/plan/07 § The enforcement seam. The message names the CHILD's path
        // — the one the caller supplied — and never the parent's, which would hand back the string
        // they were guessing at.
        //
        // ⚠ ONLY A CONFIRMED BINDING IS A PARENT, which is what IResourceIndexGrain.ResolveAsync
        // already means: a name under an unexpired two-phase-create claim is a lease and not yet a
        // resource, and a child hung off one would outlive its parent's failure to exist.
        //
        // ⚠ THE IMMEDIATE PARENT, NOT THE WHOLE CHAIN. At depth 3 the grandparent is not re-read,
        // because it was checked when the parent was created and the invariant carries down
        // inductively. The one thing that can break it is a delete, which is exactly what
        // docs/plan/08 § Deleting a parent resource that has children is about.
        //
        // ⚠ ON THE CREATE AND ONLY ON THE CREATE — hence `existing.IsFailure`, and why this sits
        // after the index read rather than up beside the subscription check where it otherwise
        // belongs: "is this a create" is that read's answer. Re-checking every write would mean that
        // deleting a parent silently froze every child — a GET or a PATCH that had nothing to do with
        // the parent would start answering 404 for a resource that plainly exists, which is a worse
        // failure than the one being closed.
        // ⚠ AND THE GUID IT RESOLVES IS KEPT. It was read and thrown away when all this step owed was
        // a yes/no, and step 8 needs the same value to aim the child's `parent` edge at the parent
        // RESOURCE — see WriteTarget.ParentId for why it is carried from here rather than re-read
        // there.
        var parentId = Guid.Empty;

        if (existing.IsFailure && address.Parent is { } parent) {
            var bound = await grains
                .ForTenant(address.TenantId.ToString("D", CultureInfo.InvariantCulture))
                .GetGrain<IResourceIndexGrain>(GrainKeys.PathIndex(parent))
                .ResolveAsync();

            if (bound.IsFailure) {
                return NotFound<WriteTarget>(request.Path);
            }

            parentId = bound.GetValueOrThrow();
        }

        return Result<WriteTarget>.Success(
            new(
                existing.IsSuccess ? address.WithId(existing.GetValueOrThrow()) : address,
                found.Registration,
                found.ApiVersion,
                found.Schema,
                existing.IsSuccess,
                parentId
            )
        );
    }

    // ── Step 6's parts ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     The leases step 6 took, and the amounts they will commit to.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Both halves, because the delete path cannot recompute the second one and the create
    ///     path can.</b> The lease ids are what step 9 needs in order to commit or release; the
    ///     amounts are what a <i>delete</i> needs in order to give back exactly what was committed
    ///     (<see cref="QuotaCommitment" />). They are produced by the same loop, from the same body,
    ///     so they cannot disagree.
    /// </remarks>
    readonly record struct Reservation(ImmutableArray<Guid> Leases, ImmutableArray<QuotaCommitment> Committed);

    async Task<Result<Reservation>> ReserveAsync(WriteTarget target, JsonElement body, Guid operationId) {
        if (target.Registration.Meters.IsDefaultOrEmpty) {
            return Result<Reservation>.Success(new([], []));
        }

        var quota = grains
            .ForTenant(target.Id.TenantId.ToString("D", CultureInfo.InvariantCulture))
            .GetGrain<IQuotaGrain>(GrainKeys.Subscription(target.Id.SubscriptionId));

        var taken = ImmutableArray.CreateBuilder<Guid>(target.Registration.Meters.Length);
        var amounts = ImmutableArray.CreateBuilder<QuotaCommitment>(target.Registration.Meters.Length);

        foreach (var meter in target.Registration.Meters) {
            var derived = AmountFor(meter, body);

            // ⚠ A METER THAT CANNOT SAY HOW MUCH REFUSES THE WRITE, AND THAT IS THE WHOLE POINT.
            // The alternative — which is what a `decimal Fallback = 1m` did — is that a pointer which
            // stopped resolving reserved one unit, quota passed, the resource provisioned at whatever
            // size it liked, and the meter that was supposed to bound it recorded a number nobody
            // chose. Zero and one are both wrong and both succeed; only a refusal is visible.
            //
            // Nothing has been reserved yet on the first iteration and everything taken so far is
            // released below on a later one, so this leaves no lease behind either way.
            if (derived.TryGetError(out var amountError)) {
                foreach (var leaseId in taken) {
                    _ = await quota.ReleaseAsync(leaseId);
                }

                logger.LogError(
                    "The meter {Meter} on {Type} could not determine an amount for this body: "
                    + "{Message}",
                    meter.Meter,
                    target.Registration.Type,
                    amountError.Message
                );

                return Result<Reservation>.Failure(amountError);
            }

            var amount = derived.GetValueOrThrow();
            var reserved = await quota.TryReserveAsync(meter.Meter, amount, operationId);

            if (reserved.TryGetError(out var quotaError)) {
                // Everything taken so far comes back before the refusal is reported. A partial
                // reservation left behind would hold a subscription's capacity against a request that
                // was refused, for the whole lease duration.
                foreach (var leaseId in taken) {
                    _ = await quota.ReleaseAsync(leaseId);
                }

                return Result<Reservation>.Failure(quotaError);
            }

            taken.Add(reserved.GetValueOrThrow().LeaseId);
            amounts.Add(new() { Meter = meter.Meter, Amount = amount });
        }

        return Result<Reservation>.Success(new(taken.DrainToImmutable(), amounts.DrainToImmutable()));
    }

    /// <summary>
    ///     What a resource of this type committed, re-derived for the delete path.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The same function over the same body the create used, which is what makes it
    ///         exact rather than an approximation.</b> <see cref="AmountFor" /> reads the meter's
    ///         declared pointer, and the body handed in here is the resource grain's stored
    ///         <i>superset</i> — <c>BeginDeleteAsync</c> projects with an empty pointer list, which is
    ///         the whole stored shape rather than one api-version's view of it. For a resource that
    ///         was created and not updated the two are the same JSON, so the delete returns the exact
    ///         number the create reserved and committed.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A <see cref="MeterRegistration.Derivation" /> keeps that symmetry only because it is
    ///         a pure function of the body.</b> The seam lets a provider compute an amount the body does
    ///         not spell — <c>replicas × sizing.cpu</c>, a preset resolved to a quantity — and the
    ///         create and the delete both run that same function over the same stored JSON, so the two
    ///         cannot disagree. A derivation that consulted a clock, configuration, or anything outside
    ///         its argument would reintroduce exactly the drift this method exists to stop, which is why
    ///         <see cref="MeterDerivation" /> says so at length and declares its own read set.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An update that moves a metered value is a gap, and it is an older one than this.</b>
    ///         Step 6 reserves only for a create — <i>"an update does not draw new quota"</i> — so
    ///         committed usage already does not track a body that grew. Closing that needs quota to be
    ///         re-evaluated on update, which is a change to step 6 and not to the delete path; what
    ///         this method must not do is invent a different number, so it derives the same way step 6
    ///         does and inherits step 6's answer. <b>The seam makes that closable rather than closing
    ///         it</b>: <see cref="AmountFor" /> is now a total function from a body to an amount or a
    ///         stated refusal, so re-reserving on a <c>PATCH</c> is running it over the new body and
    ///         moving the difference — which is a change to step 6, deliberately not made here.
    ///     </para>
    /// </remarks>
    ImmutableArray<QuotaCommitment> CommittedBy(ResourceTypeRegistration registration, string properties) {
        if (registration.Meters.IsDefaultOrEmpty) {
            return [];
        }

        JsonDocument body;
        try {
            body = JsonDocument.Parse(properties.Length == 0 ? "{}" : properties);
        }
        catch (JsonException) {
            // Grain state that is not JSON is a platform fault. Returning nothing means the delete
            // gives nothing back, which is the behaviour that existed before this method — a drift
            // upward — rather than a wrong credit, which would be worse.
            return [];
        }

        using (body) {
            var amounts = ImmutableArray.CreateBuilder<QuotaCommitment>(registration.Meters.Length);

            foreach (var meter in registration.Meters) {
                var derived = AmountFor(meter, body.RootElement);

                // ⚠ A delete does not fail because a meter stopped deriving, and it does not guess
                // either. The create refused every body this could not measure, so a stored resource
                // whose amount no longer derives means the DECLARATION moved under it — a provider
                // renamed a property, or an api-version changed the shape — between the create and the
                // delete. Crediting a number this run computed would credit a different number than
                // the one that was committed, which is the drift with the sign flipped. Skipping it
                // leaves that meter high by one resource's worth, logged, and lets the tear-down
                // finish; a delete that refused would leave the resource undeletable instead.
                if (derived.TryGetError(out var amountError)) {
                    logger.LogError(
                        "The meter {Meter} on {Type} no longer derives an amount from the stored body, "
                        + "so a delete cannot return what the create committed and that meter will "
                        + "read high: {Message}",
                        meter.Meter,
                        registration.Type,
                        amountError.Message
                    );

                    continue;
                }

                amounts.Add(new() { Meter = meter.Meter, Amount = derived.GetValueOrThrow() });
            }

            return amounts.DrainToImmutable();
        }
    }

    /// <summary>
    ///     How much of a meter this body draws — the derivation, the declared pointer, or a refusal.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The one step both a create and a delete go through, which is what makes them
    ///         symmetric.</b> Anything that changes how an amount is derived changes it for both at
    ///         once; a second derivation on either side is the bug <c>DeletePathTests</c>'
    ///         <c>ADeleteReturnsExactlyWhatTheCreateCommittedOnEveryMeter</c> pins.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Refusal is the default and zero is never the answer.</b> A pointer that resolves to
    ///         nothing, a quantity that does not parse, a preset a provider does not know — each one
    ///         used to mean "reserve the fallback", which defaulted to one unit. That is the failure
    ///         nobody sees: the write succeeds, the resource provisions, and the meter carries a number
    ///         nobody chose. A fallback now has to be declared to exist.
    ///     </para>
    /// </remarks>
    /// <param name="meter">The declared meter.</param>
    /// <param name="body">The body — the request's on a create, the stored superset on a delete.</param>
    /// <returns>The amount, or the refusal that names why it could not be determined.</returns>
    static Result<decimal> AmountFor(MeterRegistration meter, JsonElement body) {
        if (meter.Derivation is { } derivation) {
            try {
                return derivation.Amount(body);
            }
            // ⚠ A provider's lambda runs here, on the write path, holding no lease yet. A throw would
            // escape into the gateway as a 500 with a stack trace on a create, and on a DELETE it would
            // escape AFTER the index was released — leaving a name freed and a resource that never
            // tears down. Turning it into the same refusal a returned failure produces keeps both paths
            // on one behaviour. OperationCanceledException is excluded because cancellation is not a
            // derivation failure and must keep propagating.
            catch (Exception exception) when (exception is not OperationCanceledException) {
                return Result<decimal>.Failure(
                    ErrorCode.InternalError,
                    $"The derivation '{derivation.Expression}' for {meter.Meter} threw "
                    + $"{exception.GetType().Name}, so the amount this resource draws is unknown and "
                    + "the write is refused. A derivation must be a total, pure function of the body — "
                    + "see MeterDerivation."
                );
            }
        }

        if (meter.AmountPointer.Length == 0) {
            // A flat meter. `Meters(meter)` always supplies 1m and the builder refuses a pointerless
            // meter with no fallback, so the coalesce is unreachable rather than a second default.
            return Result<decimal>.Success(meter.Fallback ?? 1m);
        }

        if (MeterDerivation.Resolve(body, meter.AmountPointer) is { } found
            && found.ValueKind == JsonValueKind.Number
            && found.TryGetDecimal(out var amount)
            && amount > 0) {
            return Result<decimal>.Success(amount);
        }

        return meter.Fallback is { } fallback
            ? Result<decimal>.Success(fallback)
            : Result<decimal>.Failure(
                ErrorCode.InternalError,
                $"'{meter.AmountPointer}' does not hold a positive number, so the {meter.Meter} this "
                + "resource draws cannot be determined and the write is refused. The meter and the "
                + "schema have drifted apart; declare a fallback on the meter if absent genuinely means "
                + "a server default — docs/plan/06 § Quota."
            );
    }

    async Task ReleaseAsync(WriteTarget target, ImmutableArray<Guid> leases) {
        if (leases.IsDefaultOrEmpty) {
            return;
        }

        var quota = grains
            .ForTenant(target.Id.TenantId.ToString("D", CultureInfo.InvariantCulture))
            .GetGrain<IQuotaGrain>(GrainKeys.Subscription(target.Id.SubscriptionId));

        foreach (var leaseId in leases) {
            _ = await quota.ReleaseAsync(leaseId);
        }
    }

    // ── Step 11's parts ────────────────────────────────────────────────────────────────────────

    async Task EmitAsync(
        ResourceChangeKind change,
        WriteTarget target,
        ResourceSnapshot snapshot,
        CancellationToken cancellationToken
    ) {
        var published = await changes.PublishAsync(
            new() {
                Change = change,
                ResourceId = target.Id.Id,
                TenantId = target.Id.TenantId,
                SubscriptionId = target.Id.SubscriptionId,
                ResourceGroup = target.Id.ResourceGroup,
                Provider = target.Id.Type.Namespace,
                Type = target.Id.Type.Type,
                Name = target.Id.Name,
                ApiVersion = target.ApiVersion.Value,
                ProvisioningState = snapshot.ProvisioningState,
                Location = snapshot.Location,
                ClusterId = snapshot.ClusterId,
                Tags = snapshot.Tags,
                CreatedAt = snapshot.CreatedAt,
                ModifiedAt = snapshot.ModifiedAt,
                DesiredHash = DesiredHash.Of(snapshot.Properties),
                Version = 0
            },
            cancellationToken
        );

        // ⚠ A failed publish does NOT fail the request. docs/plan/08 § The resource-graph projection
        // makes the projection eventually consistent by design; refusing a create because a list view
        // will lag would trade a correct write for a cosmetic one.
        if (published.TryGetError(out var publishError)) {
            logger.LogWarning(
                "Publishing {Change} for {Path} failed: {Message}. The write stands; the resource-graph "
                + "projection will be behind until the next change.",
                change,
                target.Id.Path,
                publishError.Message
            );
        }
    }

    // ── Small shared pieces ────────────────────────────────────────────────────────────────────

    // ⚠ TWO LISTS, AND THE DIFFERENCE IS THE WHOLE OF THE SECRET DROP.
    //
    // Pointers is the WRITE slice and carries every declared property. ResourceGrain.ReplaceSlice
    // writes only the pointers it is handed, so dropping one here would make a PUT swallow that
    // property in silence — which is the failure docs/plan/08 § The provider registry cares about
    // most: the caller is told the write took and it did not.
    //
    // ReadablePointers is what a caller is allowed to see back, and it omits every
    // SchemaProperty.Secret. It has to reach every snapshot that leaves this class, and there are two
    // routes: ReadAsync and the accepted-write re-read pass it to GetAsync, while the no-op and 202
    // branches answer from the snapshot SubmitDesiredAsync returns — which is why the submission
    // carries it too. Miss either route and the secret comes back on that one.
    //
    // ⚠ It is a projection filter and NOT confidentiality. The value is still stored in the grain's
    // superset in plaintext, and OperationSpec.Desired holds another copy, because nothing replaces a
    // secret with a SecretRef on the way in — docs/plan/02 § ADR-010, and the remarks on
    // SchemaProperty. This stops a secret round-tripping through the API; it does not keep one out of
    // Postgres or out of a backup.

    static ImmutableArray<string> Pointers(ResourceSchema schema) => [.. schema.Properties.Select(x => x.JsonPointer)];

    static ImmutableArray<string> ReadablePointers(ResourceSchema schema) =>
        [.. schema.Properties.Where(x => !x.Secret).Select(x => x.JsonPointer)];

    static ImmutableDictionary<string, string> TagsFrom(JsonElement body, ResourceTypeRegistration registration) {
        if (!registration.SupportsTags
            || !body.TryGetProperty("tags", out var tags)
            || tags.ValueKind != JsonValueKind.Object) {
            return ImmutableDictionary<string, string>.Empty;
        }

        var built = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var member in tags.EnumerateObject()) {
            if (member.Value.ValueKind == JsonValueKind.String) {
                built[member.Name] = member.Value.GetString() ?? string.Empty;
            }
        }

        return built.ToImmutable();
    }

    static string LocationFrom(JsonElement body) =>
        body.TryGetProperty("location", out var location) && location.ValueKind == JsonValueKind.String
            ? location.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>
    ///     Reads the cluster id out of the body, at the pointer the type's own registration names.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The pointer used to be written here, and that is what made <c>RequiresCluster()</c> a
    ///     flag with no schema consequence.</b> The manager looked for <c>/properties/clusterId</c>
    ///     whatever the type's schema declared, and nothing checked that a type declaring the flag
    ///     declared the property — so a provider that forgot it got <c>Guid.Empty</c> here, a
    ///     <c>202</c> to the caller, and a reconcile failure per resource.
    ///     <c>ProviderBuilder.CheckClusterPlacement</c> now refuses such a type at silo start, and this
    ///     reads the pointer that check verified.
    /// </remarks>
    static Guid ClusterFrom(JsonElement body, ResourceTypeRegistration registration) {
        if (!registration.RequiresCluster || registration.ClusterIdPointer.Length == 0) {
            return Guid.Empty;
        }

        var current = body;

        foreach (var token in registration.ClusterIdPointer.Split('/', StringSplitOptions.RemoveEmptyEntries)) {
            var name = token.Contains('~', StringComparison.Ordinal)
                ? token.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)
                : token;

            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out var next)) {
                return Guid.Empty;
            }

            current = next;
        }

        return current.ValueKind == JsonValueKind.String && Guid.TryParse(current.GetString(), out var parsed)
            ? parsed
            : Guid.Empty;
    }

    static string OperationUri(Guid operationId) =>
        string.Create(CultureInfo.InvariantCulture, $"/operations/{operationId:D}");

    static Result<T> NotFound<T>(string path)
        where T : notnull =>
        Result<T>.Failure(
            ErrorCode.ResourceNotFound,
            // ⚠ The same message whether the resource is absent or merely invisible. That identity is
            // the point — docs/plan/07 § The enforcement seam.
            $"'{path}' does not exist."
        );

    IResourceGrain Resource(WriteTarget target) =>
        Tenant(target).GetGrain<IResourceGrain>(GrainKeys.Resource(target.Id.Id));

    IResourceIndexGrain Index(WriteTarget target) =>
        Tenant(target).GetGrain<IResourceIndexGrain>(GrainKeys.PathIndex(target.Id));

    IOperationGrain Operation(WriteTarget target, Guid operationId) =>
        Tenant(target).GetGrain<IOperationGrain>(GrainKeys.Operation(operationId));

    TenantGrainFactory Tenant(WriteTarget target) =>
        grains.ForTenant(target.Id.TenantId.ToString("D", CultureInfo.InvariantCulture));

    /// <summary>What step 1 resolved a request to.</summary>
    /// <param name="Id">
    ///     The address, with <see cref="ResourceId.Id" /> filled in when the index resolved it and
    ///     <see cref="Guid.Empty" /> when it did not.
    /// </param>
    /// <param name="Registration">The type's registration.</param>
    /// <param name="ApiVersion">The version asked for.</param>
    /// <param name="Schema">That version's body shape.</param>
    /// <param name="Exists">
    ///     Whether the index holds a <b>confirmed</b> binding. ⚠ A name under an unexpired claim reads
    ///     as not existing, which is correct: <c>IResourceIndexGrain.ResolveAsync</c> resolves only a
    ///     confirmed binding, because returning a claimed one would let a caller address a resource
    ///     that may never exist.
    /// </param>
    /// <param name="ParentId">
    ///     The GUID of the resource <see cref="ResourceId.Parent" /> names, or <see cref="Guid.Empty" />
    ///     when the address is top-level or this is not a create.
    ///     <para>
    ///         ⚠ <b>Resolved here and carried, rather than looked up again at step 8, and the reason is
    ///         the DELETE path.</b> <c>ResolveAsync</c> already reads this binding to answer the child's
    ///         404, so a second read would be a second chance to disagree with the first. That is the
    ///         small reason. The large one is that <c>IResourceRelationWriter</c> must be able to
    ///         reconstruct the tuple it wrote when the resource is <i>gone</i> — <c>OperationGrain</c>
    ///         unlinks after <c>CompleteDeleteAsync</c>, retrying from a reminder — and by then the
    ///         parent may be gone too: docs/plan/08 § Deleting a parent resource that has children
    ///         records that the refusal which would prevent that is decided and not built. A writer
    ///         that resolved the parent itself would fail every retry, converge never, and leave
    ///         exactly the dangling tuple <c>ParentEdgeTests.DeleteLeavesNoDanglingTuple</c> exists to
    ///         catch. So the GUID is resolved once, on the path that already has it, and persisted on
    ///         <c>OperationSpec</c> for the unlink.
    ///     </para>
    /// </param>
    readonly record struct WriteTarget(
        ResourceId Id,
        ResourceTypeRegistration Registration,
        ApiVersion ApiVersion,
        ResourceSchema Schema,
        bool Exists,
        Guid ParentId
    );
}
