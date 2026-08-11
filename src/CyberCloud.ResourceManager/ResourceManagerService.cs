using CyberCloud.ResourceManager.Contracts.Registry;
using Microsoft.Extensions.Logging;
using Orleans.Multitenant;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;

namespace CyberCloud.ResourceManager;

/// <summary>
///     The write path. docs/plan/08 § The write path, end to end, all eleven steps, in order.
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
            var validated = target.Schema.Validate(body.RootElement, request.Verb == WriteVerb.Put);
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

        return await Resource(target).GetAsync(target.ApiVersion.Value, Pointers(target.Schema));
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
                    QuotaLeaseIds = [],
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

        var snapshot = await Resource(target).GetAsync(target.ApiVersion.Value, Pointers(target.Schema));

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

    // ── Steps 3 through 11 of the write path ───────────────────────────────────────────────────

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
            var revalidated = target.Schema.Validate(modified.RootElement, request.Verb == WriteVerb.Put);
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
        var operationId = Guid.NewGuid();
        var resourceId = target.Exists ? target.Id.Id : Guid.NewGuid();

        if (!target.Exists) {
            var reserved = await ReserveAsync(target, body, operationId);
            if (reserved.TryGetError(out var quotaError)) {
                return Result<WriteAccepted>.Failure(quotaError);
            }

            leases = reserved.GetValueOrThrow();
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

        // ── 8. Write durable desired state ──────────────────────────────────────────────────────
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
                    ClusterId = ClusterFrom(body),
                    DeclaredPointers = Pointers(resolvedTarget.Schema)
                }
            );

        if (submitted.TryGetError(out var submitError)) {
            await ReleaseAsync(resolvedTarget, leases);
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

        // ── 9. Start the operation, then confirm the claim ──────────────────────────────────────
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

        // ── 10. Emit resource-changed ───────────────────────────────────────────────────────────
        trace.Enter(WriteStep.EmitChanged);

        await EmitAsync(
            target.Exists ? ResourceChangeKind.Updated : ResourceChangeKind.Created,
            resolvedTarget,
            snapshot,
            cancellationToken
        );

        // ── 11. 202 Accepted ────────────────────────────────────────────────────────────────────
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

    // ── Step 1's parts ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Parses the path, resolves the identity through the index, and looks the type and version up.
    /// </summary>
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

    async Task<Result<ImmutableArray<Guid>>> ReserveAsync(WriteTarget target, JsonElement body, Guid operationId) {
        if (target.Registration.Meters.IsDefaultOrEmpty) {
            return Result<ImmutableArray<Guid>>.Success([]);
        }

        var quota = grains
            .ForTenant(target.Id.TenantId.ToString("D", CultureInfo.InvariantCulture))
            .GetGrain<IQuotaGrain>(GrainKeys.Subscription(target.Id.SubscriptionId));

        var taken = ImmutableArray.CreateBuilder<Guid>(target.Registration.Meters.Length);

        foreach (var meter in target.Registration.Meters) {
            var amount = AmountFor(meter, body);
            var reserved = await quota.TryReserveAsync(meter.Meter, amount, operationId);

            if (reserved.TryGetError(out var quotaError)) {
                // Everything taken so far comes back before the refusal is reported. A partial
                // reservation left behind would hold a subscription's capacity against a request that
                // was refused, for the whole lease duration.
                foreach (var leaseId in taken) {
                    _ = await quota.ReleaseAsync(leaseId);
                }

                return Result<ImmutableArray<Guid>>.Failure(quotaError);
            }

            taken.Add(reserved.GetValueOrThrow().LeaseId);
        }

        return Result<ImmutableArray<Guid>>.Success(taken.DrainToImmutable());
    }

    /// <summary>How much of a meter this body draws — from the declared pointer, or the fallback.</summary>
    static decimal AmountFor(MeterRegistration meter, JsonElement body) {
        if (meter.AmountPointer.Length == 0) {
            return meter.Fallback;
        }

        var current = body;
        foreach (var token in meter.AmountPointer.Split('/', StringSplitOptions.RemoveEmptyEntries)) {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(token, out var next)) {
                return meter.Fallback;
            }

            current = next;
        }

        return current.ValueKind == JsonValueKind.Number && current.TryGetDecimal(out var amount) && amount > 0
            ? amount
            : meter.Fallback;
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

    // ── Step 10's parts ────────────────────────────────────────────────────────────────────────

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

    static ImmutableArray<string> Pointers(ResourceSchema schema) => [.. schema.Properties.Select(x => x.JsonPointer)];

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

    static Guid ClusterFrom(JsonElement body) {
        if (!body.TryGetProperty("properties", out var properties)
            || properties.ValueKind != JsonValueKind.Object
            || !properties.TryGetProperty("clusterId", out var clusterId)
            || clusterId.ValueKind != JsonValueKind.String) {
            return Guid.Empty;
        }

        return Guid.TryParse(clusterId.GetString(), out var parsed) ? parsed : Guid.Empty;
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
