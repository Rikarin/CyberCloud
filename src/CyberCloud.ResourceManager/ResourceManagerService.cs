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
        var released = await Index(target).ReleaseAsync(target.Id.Id);
        if (released.TryGetError(out var releaseError)) {
            return Result<WriteAccepted>.Failure(releaseError);
        }

        trace.Enter(WriteStep.SubmitDesired);

        var operationId = Guid.NewGuid();
        var beginning = await Resource(target).BeginDeleteAsync(operationId, request.IfMatch);
        if (beginning.TryGetError(out var beginError)) {
            return Result<WriteAccepted>.Failure(beginError);
        }

        var snapshot = beginning.GetValueOrThrow();

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
                    CommittedQuota = CommittedBy(target.Registration, snapshot.Properties),
                    IndexClaimed = false,
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
        trace.Enter(WriteStep.LinkParent);

        var linked = await relations.LinkToParentAsync(addressed, cancellationToken);
        if (linked.TryGetError(out var linkError)) {
            await ReleaseAsync(resolvedTarget, leases);
            return Result<WriteAccepted>.Failure(linkError);
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
                _ = await relations.UnlinkFromParentAsync(addressed, cancellationToken);
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
    ///     index, and looks the type and version up.
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

        return Result<WriteTarget>.Success(
            new(
                existing.IsSuccess ? address.WithId(existing.GetValueOrThrow()) : address,
                found.Registration,
                found.ApiVersion,
                found.Schema,
                existing.IsSuccess
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
    readonly record struct WriteTarget(
        ResourceId Id,
        ResourceTypeRegistration Registration,
        ApiVersion ApiVersion,
        ResourceSchema Schema,
        bool Exists
    );
}
