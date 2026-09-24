using CyberCloud.Core.Time;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CyberCloud.ResourceManager.Contracts.Registry;
using Orleans.Multitenant;

namespace CyberCloud.ResourceManager.Grains;

/// <summary>
///     <see cref="IResourceGrain" /> — Entity, Durable, key <c>res/{resourceId:N}</c>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This grain never provisions inline.</b> docs/plan/08 § The reconcile loop:
///         <i>
///             "The
///             resource grain never provisions inline. It records intent and returns; a reminder drives
///             convergence."
///         </i> Nothing here calls a reconciler, reaches a cluster, or awaits anything but
///         its own storage — which is what keeps a <c>PUT</c> a sub-millisecond write rather than a
///         four-minute request.
///     </para>
///     <para>
///         ⚠ <b>And it holds the one reminder a converged resource has.</b> docs/plan/08 § The
///         manager-started pass: a type that declares <c>PassEvery</c> gets a <c>periodic-pass</c>
///         reminder here, armed when a write converges and removed when a delete begins. The tick
///         does not reconcile — that would be provisioning inline — it starts an
///         <see cref="OperationKind.Refresh" /> operation and returns, exactly as a write does.
///     </para>
/// </remarks>
public sealed class ResourceGrain(
    [PersistentState("resource", StorageTiers.Durable)]
    IPersistentState<ResourceState> state,
    IClock clock,
    IProviderRegistry registry
)
    : Grain, IResourceGrain, IRemindable {
    /// <summary>
    ///     The tag cap of docs/plan/06 § Tags, locks — 50 pairs.
    /// </summary>
    /// <remarks>
    ///     ⚠ <see cref="Contracts.Registry.TagRules.MaxTags" />' value rather than a second 50. The emitted OpenAPI
    ///     document publishes the cap as <c>maxProperties</c>, so a number written twice would be a
    ///     published contract and an enforced limit that could silently disagree.
    /// </remarks>
    public const int MaxTags = Contracts.Registry.TagRules.MaxTags;

    Guid resourceId;

    /// <inheritdoc />
    public override Task OnActivateAsync(CancellationToken cancellationToken) {
        _ = ResourceManagerGrainKeys.TenantOf(this);
        resourceId = ResourceManagerGrainKeys.Decode(this, GrainKeyKind.Resource).Id;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<Result<ResourceSnapshot>> SubmitDesiredAsync(DesiredSubmission submission) {
        ArgumentNullException.ThrowIfNull(submission);

        var address = ResourceId.ParsePath(submission.Path);
        if (address.TryGetError(out var pathError)) {
            return Result<ResourceSnapshot>.Failure(pathError);
        }

        if (state.State.Lock == LockLevel.ReadOnly) {
            return Result<ResourceSnapshot>.Failure(
                ErrorCode.ScopeLocked,
                $"'{submission.Path}' carries a ReadOnly lock, so writes are refused. Remove the lock "
                + "to change it — docs/plan/06 § Tags, locks, and the small stuff that is not small."
            );
        }

        if (submission.IfMatch.Length > 0
            && !string.Equals(submission.IfMatch, state.State.Etag, StringComparison.Ordinal)) {
            return Result<ResourceSnapshot>.Failure(
                ErrorCode.PreconditionFailed,
                $"The If-Match etag '{submission.IfMatch}' does not match the resource's current etag "
                + $"'{state.State.Etag}'. Something changed since the copy you are editing was read."
            );
        }

        // Another live operation on the same resource is a conflict rather than a queue: two
        // reconcilers driving one resource towards two shapes is exactly the race a single writer
        // exists to prevent. A retry of the SAME operation is not a conflict — that is the resumable
        // path.
        if (state.State.OperationId != Guid.Empty
            && state.State.OperationId != submission.OperationId
            && state.State.ProvisioningState
            is ProvisioningState.Creating or ProvisioningState.Updating or ProvisioningState.Deleting) {
            return Result<ResourceSnapshot>.Failure(
                ErrorCode.OperationInProgress,
                $"Operation {state.State.OperationId:D} is already driving '{submission.Path}' and it "
                + $"is {state.State.ProvisioningState}. Poll that operation, or cancel it, before "
                + "submitting another change."
            );
        }

        var incoming = Parse(submission.Body);
        if (incoming.TryGetError(out var bodyError)) {
            return Result<ResourceSnapshot>.Failure(bodyError);
        }

        var tagProblem = CheckTags(submission.Tags);
        if (tagProblem is not null) {
            return Result<ResourceSnapshot>.Failure(tagProblem);
        }

        var superset = Parse(state.State.Superset).GetValueOrThrow();
        var declared = submission.DeclaredPointers;

        // ⚠ The write applies `declared` and the response projects `readable`, and the two differ by
        // exactly the Secret properties. Replacing the slice needs every pointer or a PUT would drop
        // what it did not list; the response needs the filtered one or a secret the caller just sent
        // comes straight back out. Empty means "not supplied", which falls back to declared.
        var readable = submission.ReadablePointers.IsDefaultOrEmpty ? declared : submission.ReadablePointers;

        // ⚠ The verb's whole meaning is these two branches.
        //
        // PUT is a full replacement OF THIS VERSION'S SLICE — every pointer the version declares is
        // removed first and then written from the body, so a property the caller omitted is gone.
        // It is *not* a replacement of the superset: a newer version's fields are not this caller's
        // to delete, and dropping them would make an old-version PUT a data-loss event.
        //
        // PATCH is RFC 7386 JSON Merge Patch and merges: an absent property is left alone and an
        // explicit null removes one. That asymmetry is why PUT is idempotent and PATCH is not.
        var next = submission.Verb == WriteVerb.Put
            ? ReplaceSlice(superset, declared, incoming.GetValueOrThrow())
            : MergePatch(superset, incoming.GetValueOrThrow());

        // ⚠ Canonical, so that "identical" means identical in meaning rather than in member order —
        // see JsonCanonical. The stored superset is canonical too, which is what lets the comparison
        // below be plain string equality and what keeps DesiredHash stable across writes.
        var nextJson = JsonCanonical.Of(next).ToJsonString();
        var tagsUnchanged = TagsEqual(state.State.Tags, submission.Tags);

        // ⚠ THE NO-OP BRANCH. docs/plan/06 § Two-phase create: "the caller retries the PUT — which is
        // idempotent because PUT with the same body on an existing resource is a no-op, which is
        // exactly why the API is PUT and not POST." The comparison is over the serialized superset,
        // which JsonNode writes in insertion order — and ReplaceSlice rebuilds that order from the
        // schema's pointer list rather than from the request, so a body whose properties arrived in a
        // different order still compares equal. Without that, "idempotent" would depend on the
        // client's JSON serializer.
        if (state.State.Exists
            && state.State.ProvisioningState is ProvisioningState.Succeeded
            && string.Equals(state.State.Superset, nextJson, StringComparison.Ordinal)
            && tagsUnchanged) {
            return Result<ResourceSnapshot>.Success(Snapshot(submission.ApiVersion, readable));
        }

        var now = clock.UtcNow;
        var creating = !state.State.Exists;

        state.State.Path = submission.Path;
        state.State.ApiVersion = submission.ApiVersion;
        state.State.Superset = nextJson;
        state.State.ProvisioningState = creating ? ProvisioningState.Creating : ProvisioningState.Updating;
        state.State.OperationId = submission.OperationId;
        state.State.LastFailure = string.Empty;
        state.State.Location = submission.Location.Length > 0 ? submission.Location : state.State.Location;
        state.State.ClusterId = submission.ClusterId != Guid.Empty ? submission.ClusterId : state.State.ClusterId;
        state.State.ModifiedBy = submission.Caller.ToString();
        state.State.ModifiedAt = now;
        state.State.Version++;
        state.State.Etag = NextEtag();

        if (!tagsUnchanged) {
            state.State.Tags = new(submission.Tags, StringComparer.Ordinal);
        }

        if (creating) {
            state.State.CreatedBy = state.State.ModifiedBy;
            state.State.CreatedAt = now;
        }

        await state.WriteStateAsync();
        return Result<ResourceSnapshot>.Success(Snapshot(submission.ApiVersion, readable));
    }

    /// <inheritdoc />
    public Task<Result<ResourceSnapshot>> GetAsync(string apiVersion, ImmutableArray<string> declaredPointers) {
        if (!state.State.Exists) {
            return Task.FromResult(NotFound<ResourceSnapshot>());
        }

        return Task.FromResult(Result<ResourceSnapshot>.Success(Snapshot(apiVersion, declaredPointers)));
    }

    /// <inheritdoc />
    public async Task<Result<ResourceSnapshot>> BeginDeleteAsync(Guid operationId, string ifMatch) {
        if (!state.State.Exists) {
            return NotFound<ResourceSnapshot>();
        }

        if (state.State.Lock is LockLevel.CanNotDelete or LockLevel.ReadOnly) {
            return Result<ResourceSnapshot>.Failure(
                ErrorCode.ScopeLocked,
                $"'{state.State.Path}' carries a {state.State.Lock} lock, so it cannot be deleted. "
                + "That is what the lock is for — docs/plan/06 § Tags, locks, and the small stuff "
                + "that is not small."
            );
        }

        if (ifMatch.Length > 0 && !string.Equals(ifMatch, state.State.Etag, StringComparison.Ordinal)) {
            return Result<ResourceSnapshot>.Failure(
                ErrorCode.PreconditionFailed,
                $"The If-Match etag '{ifMatch}' does not match '{state.State.Etag}'."
            );
        }

        // ⚠ ADDED BY THE CONFORMANCE SUITE. docs/plan/03 § Providers lists "delete while an operation
        // is running → 409" among the things every provider must pass, and this grain was accepting
        // the delete instead: SubmitDesiredAsync had the single-writer guard and BeginDeleteAsync did
        // not, so a DELETE arriving mid-create flipped a Creating resource straight to Deleting while
        // the create's reconcile pass was still applying objects. The teardown and the create then
        // raced on the same objects, which is the exact race the guard on the write side exists to
        // prevent — and it left the create's quota lease and index claim owned by an operation whose
        // resource was on its way out.
        //
        // A re-drive of the SAME operation is not a conflict: that is the resumable path
        // (docs/plan/08 § Long-running operations), and refusing it would strand every delete that
        // outlived a silo.
        if (state.State.OperationId != Guid.Empty
            && state.State.OperationId != operationId
            && state.State.ProvisioningState
            is ProvisioningState.Creating or ProvisioningState.Updating or ProvisioningState.Deleting) {
            return Result<ResourceSnapshot>.Failure(
                ErrorCode.OperationInProgress,
                $"Operation {state.State.OperationId:D} is already driving '{state.State.Path}' and it "
                + $"is {state.State.ProvisioningState}. Poll that operation, or cancel it, before "
                + "deleting — a delete that raced a live create would tear down objects the create is "
                + "still applying."
            );
        }

        state.State.ProvisioningState = ProvisioningState.Deleting;
        state.State.OperationId = operationId;
        state.State.ModifiedAt = clock.UtcNow;
        state.State.Version++;
        state.State.Etag = NextEtag();

        await state.WriteStateAsync();

        // ⚠ Cancelled at the delete's START, not its end: a pass that fired during the teardown
        // would find the resource Deleting and skip, but a reminder row left for a resource that is
        // on its way out is a wakeup per period for as long as the recovery window lasts.
        await DisarmPeriodicPassAsync(PassPeriod());
        return Result<ResourceSnapshot>.Success(Snapshot(state.State.ApiVersion, []));
    }

    /// <inheritdoc />
    public async Task<Result> CompleteDeleteAsync() {
        if (!state.State.Exists) {
            // Already gone. Idempotent, because the delete path is re-driven from a reminder and a
            // second completion must not fail the operation that already succeeded.
            return Result.Success;
        }

        if (state.State.ProvisioningState != ProvisioningState.Deleting) {
            return Result.Failure(
                ErrorCode.Conflict,
                $"'{state.State.Path}' is {state.State.ProvisioningState} and only a Deleting "
                + "resource can complete a delete. Call BeginDeleteAsync first — the ordering is what "
                + "keeps a resource visible while its data plane is still up."
            );
        }

        var period = PassPeriod();
        await state.ClearStateAsync();
        state.State = new();
        await DisarmPeriodicPassAsync(period);
        return Result.Success;
    }

    /// <inheritdoc />
    public async Task<Result<ResourceSnapshot>> ParkAsync() {
        if (!state.State.Exists) {
            return NotFound<ResourceSnapshot>();
        }

        if (state.State.ProvisioningState != ProvisioningState.Deleting) {
            return Result<ResourceSnapshot>.Failure(
                ErrorCode.Conflict,
                $"'{state.State.Path}' is {state.State.ProvisioningState} and only a Deleting resource "
                + "can be parked. The park follows a soft delete's teardown — docs/plan/08 § Soft delete."
            );
        }

        // ⚠ THE STATE, THE OPERATION AND THE ETAG ALL STAY. The resource is still Deleting with the
        // delete's OperationId on it, which is what BeginRestoreAsync reads and what its remarks
        // explain the absence of a single-writer guard by. What this write is FOR is the count: the
        // park is a transition of the resource — its data plane is down and its address is gone —
        // and the projection orders transitions by this number. CompleteAsync leaves the etag alone
        // for a silo-side transition too; nothing a caller holds an If-Match against changed.
        state.State.ModifiedAt = clock.UtcNow;
        state.State.Version++;

        await state.WriteStateAsync();
        return Result<ResourceSnapshot>.Success(Snapshot(state.State.ApiVersion, []));
    }

    /// <inheritdoc />
    public async Task<Result<ResourceSnapshot>> BeginRestoreAsync(Guid operationId) {
        if (!state.State.Exists) {
            // ⚠ The canonical NotFound, and it is reachable rather than defensive: a restore is
            // authorized against the index's soft-deleted side, and a resource whose purge cleared
            // this grain between that read and this call is exactly a resource that is gone.
            return NotFound<ResourceSnapshot>();
        }

        if (state.State.ProvisioningState != ProvisioningState.Deleting) {
            return Result<ResourceSnapshot>.Failure(
                ErrorCode.Conflict,
                $"'{state.State.Path}' is {state.State.ProvisioningState} and only a Deleting resource "
                + "can be restored. A resource that is not parked has nothing to come back from — "
                + "docs/plan/08 § Soft delete."
            );
        }

        // ⚠ NO SINGLE-WRITER GUARD HERE, AND THE ABSENCE IS DELIBERATE RATHER THAN AN OVERSIGHT.
        // A parked resource is Deleting and its OperationId names the delete that parked it, so a
        // guard reading "another operation holds this resource" would refuse EVERY first restore.
        // The guard that actually matters — "that delete is still tearing the data plane down" —
        // needs the operation's status, which this grain cannot see, so
        // ResourceManagerService.RestoreAsync applies it before calling here. Same shape as the
        // OperationInProgress read the delete path performs before releasing the index, and for the
        // same reason: the refusal has to happen before anything irreversible.
        state.State.ProvisioningState = ProvisioningState.Updating;
        state.State.OperationId = operationId;
        state.State.LastFailure = string.Empty;
        state.State.ModifiedAt = clock.UtcNow;
        state.State.Version++;
        state.State.Etag = NextEtag();

        await state.WriteStateAsync();
        return Result<ResourceSnapshot>.Success(Snapshot(state.State.ApiVersion, []));
    }

    /// <inheritdoc />
    public async Task<Result<ResourceSnapshot>> CompleteAsync(ProvisioningState terminal, Error? failure) {
        if (!state.State.Exists) {
            return NotFound<ResourceSnapshot>();
        }

        if (terminal is not (ProvisioningState.Succeeded or ProvisioningState.Failed or ProvisioningState.Canceled)) {
            return Result<ResourceSnapshot>.Failure(
                ErrorCode.InvalidRequestBody,
                $"{terminal} is not a terminal provisioning state. The terminal states are Succeeded, "
                + "Failed and Canceled — docs/plan/06 § Tags, locks, and the small stuff that is not "
                + "small."
            );
        }

        // ⚠ A resource whose teardown failed stays Deleting. docs/plan/06 § Two-phase create: it is
        // "left in Deleting with a retry reminder and is *visible* in listings with that state — never
        // silently gone while its pods still run and its meter still ticks." Moving it to Failed would
        // make it look like a resource that exists and is broken, which is a different and less
        // actionable thing than a resource that is on its way out and stuck.
        if (state.State.ProvisioningState == ProvisioningState.Deleting && terminal == ProvisioningState.Failed) {
            state.State.LastFailure = failure?.Message ?? string.Empty;
            state.State.ModifiedAt = clock.UtcNow;
            state.State.Version++;
            await state.WriteStateAsync();
            return Result<ResourceSnapshot>.Success(Snapshot(state.State.ApiVersion, []));
        }

        state.State.ProvisioningState = terminal;
        state.State.LastFailure = failure?.Message ?? string.Empty;
        state.State.ModifiedAt = clock.UtcNow;
        state.State.Version++;

        if (terminal is ProvisioningState.Succeeded) {
            state.State.OperationId = Guid.Empty;
        }

        await state.WriteStateAsync();

        // ⚠ Armed on every converged write, and a restore is one: the delete that parked the resource
        // removed the reminder, and the restore's CompleteAsync is what puts it back.
        if (terminal is ProvisioningState.Succeeded) {
            await ArmPeriodicPassCoreAsync();
        }

        return Result<ResourceSnapshot>.Success(Snapshot(state.State.ApiVersion, []));
    }

    /// <inheritdoc />
    public async Task<Result<Guid>> RunPeriodicPassAsync() {
        if (!state.State.Exists) {
            await DisarmPeriodicPassAsync(PeriodicPass.MinimumPeriod);
            return NotFound<Guid>();
        }

        if (PassPeriod() == TimeSpan.Zero) {
            // A type that stopped declaring a period since the reminder was armed.
            await DisarmPeriodicPassAsync(PeriodicPass.MinimumPeriod);
            return Result<Guid>.Success(Guid.Empty);
        }

        // ⚠ ONLY A RESOURCE AT REST. Creating, Updating and Deleting belong to the operation that
        // put them there, and a Failed or Canceled resource is waiting for its owner rather than for
        // a pass — re-running a failed create every hour would be a retry nobody asked for.
        if (state.State.ProvisioningState != ProvisioningState.Succeeded || state.State.OperationId != Guid.Empty) {
            return Result<Guid>.Success(Guid.Empty);
        }

        // ⚠ One pass at a time. A pass that is still backing off — a vault whose cluster is
        // unreachable — is the pass; starting a second beside it would be two drivers of one
        // resource, which is the race the single-writer guard exists to prevent.
        //
        // ⚠ DECIDED FROM THIS GRAIN'S OWN STATE, AND IT USED TO ASK THE OPERATION — #30's review. The
        // running pass calls this grain mid-drive, so a tick that awaited that operation's GetAsync
        // while it drove was a cycle of two non-reentrant grains, broken only by Orleans' 30-second
        // response timeout. The pass says when it ends (EndPeriodicPassAsync); one that never said so
        // has been failed by ReconcileSchedule.Timeout's ceiling by the time this lets another start.
        if (state.State.PassOperationId != Guid.Empty
            && clock.UtcNow - state.State.PassStartedAt < ReconcileSchedule.Timeout + PeriodicPass.MinimumPeriod) {
            return Result<Guid>.Success(Guid.Empty);
        }

        var tenant = GrainFactory.ForTenant(ResourceManagerGrainKeys.TenantOf(this).ToString("D", CultureInfo.InvariantCulture));

        var address = ResourceId.ParsePath(state.State.Path).GetValueOrThrow();
        var operationId = Guid.NewGuid();

        var started = await tenant.GetGrain<IOperationGrain>(GrainKeys.Operation(operationId))
            .StartAsync(
                new() {
                    OperationId = operationId,
                    Kind = OperationKind.Refresh,
                    ResourcePath = state.State.Path,
                    ResourceId = resourceId,
                    TenantId = address.TenantId,
                    SubscriptionId = address.SubscriptionId,
                    ApiVersion = state.State.ApiVersion,
                    Desired = state.State.Superset
                }
            );

        if (started.TryGetError(out var startError)) {
            return Result<Guid>.Failure(startError);
        }

        state.State.PassOperationId = operationId;
        state.State.PassStartedAt = clock.UtcNow;
        await state.WriteStateAsync();
        return Result<Guid>.Success(operationId);
    }

    /// <inheritdoc />
    public async Task<Result> EndPeriodicPassAsync(Guid operationId) {
        if (operationId == Guid.Empty || state.State.PassOperationId != operationId) {
            return Result.Success;
        }

        state.State.PassOperationId = Guid.Empty;
        await state.WriteStateAsync();
        return Result.Success;
    }

    /// <inheritdoc />
    public async Task<Result<bool>> ArmPeriodicPassAsync() {
        if (!state.State.Exists) {
            return NotFound<bool>();
        }

        // ⚠ Only a converged resource. Anything else has an operation that arms the reminder when it
        // converges, or is waiting for its owner — a Failed create gets no hourly retry from here.
        if (state.State.ProvisioningState != ProvisioningState.Succeeded || PassPeriod() == TimeSpan.Zero) {
            return Result<bool>.Success(false);
        }

        return Result<bool>.Success(await ArmPeriodicPassCoreAsync());
    }

    /// <inheritdoc />
    public async Task ReceiveReminder(string reminderName, TickStatus status) {
        if (string.Equals(reminderName, PeriodicPass.ReminderName, StringComparison.Ordinal)) {
            _ = await RunPeriodicPassAsync();
        }
    }

    /// <inheritdoc />
    public async Task<Result> ReportObservedAsync(ObservedState observed) {
        ArgumentNullException.ThrowIfNull(observed);

        if (!state.State.Exists) {
            return NotFound();
        }

        state.State.Observed = observed;
        await state.WriteStateAsync();
        return Result.Success;
    }

    /// <inheritdoc />
    public async Task<Result> RecordReadOnlyAsync(ImmutableDictionary<string, string> values) {
        ArgumentNullException.ThrowIfNull(values);

        if (!state.State.Exists) {
            return NotFound();
        }

        var superset = Parse(state.State.Superset).GetValueOrThrow();

        foreach (var (pointer, json) in values.OrderBy(static x => x.Key, StringComparer.Ordinal)) {
            JsonNode? value;

            try {
                value = JsonNode.Parse(json);
            } catch (JsonException exception) {
                return Result.Failure(
                    ErrorCode.InvalidRequestBody,
                    $"The value recorded at '{pointer}' is not JSON: {exception.Message}",
                    pointer
                );
            }

            if (value is null) {
                JsonPointer.Remove(superset, pointer);
            } else {
                JsonPointer.Write(superset, pointer, value);
            }
        }

        var next = JsonCanonical.Of(superset).ToJsonString();

        if (string.Equals(next, state.State.Superset, StringComparison.Ordinal)) {
            return Result.Success;
        }

        // ⚠ The body changed, so the etag and the version move — a client holding the etag from the
        // PUT that started the run is holding a representation that no longer exists. ModifiedBy
        // stays: the platform recording a run is not somebody modifying the resource.
        state.State.Superset = next;
        state.State.Version++;
        state.State.Etag = NextEtag();
        await state.WriteStateAsync();
        return Result.Success;
    }

    /// <inheritdoc />
    public async Task<Result> SetLockAsync(LockLevel level) {
        if (!state.State.Exists) {
            return NotFound();
        }

        state.State.Lock = level;
        state.State.ModifiedAt = clock.UtcNow;
        state.State.Version++;
        await state.WriteStateAsync();
        return Result.Success;
    }

    /// <inheritdoc />
    public Task<Result<ReconcileInput>> GetReconcileInputAsync() {
        if (!state.State.Exists) {
            return Task.FromResult(NotFound<ReconcileInput>());
        }

        return Task.FromResult(
            Result<ReconcileInput>.Success(
                new() {
                    Path = state.State.Path,
                    ResourceId = resourceId,
                    ApiVersion = state.State.ApiVersion,
                    Desired = state.State.Superset,
                    Observed = state.State.Observed,
                    ClusterId = state.State.ClusterId,
                    ProvisioningState = state.State.ProvisioningState,
                    OperationId = state.State.OperationId,
                    PendingChanges = [.. state.State.PendingChanges.Select(static x => x.Change)],
                    ChangeSequence = state.State.PendingChanges.Count == 0
                        ? 0
                        : state.State.PendingChanges[^1].Sequence,
                    ChangesDropped = state.State.ChangesDropped,
                    PassOperationId = state.State.PassOperationId
                }
            )
        );
    }

    /// <inheritdoc />
    public async Task<Result> NotifyChangedAsync(ResourceChangedEvent change) {
        ArgumentNullException.ThrowIfNull(change);

        if (!state.State.Exists) {
            return NotFound();
        }

        // ⚠ THE SEQUENCE ADVANCES EVEN WHEN THE EVENT IS DROPPED. An acknowledgement names "up to the
        // number I read", so a dropped event that did not consume a number would let a later event
        // reuse it, and a pass that read the earlier list would acknowledge the newcomer unseen.
        state.State.ChangeSequence++;
        state.State.PendingChanges.Add(new() { Sequence = state.State.ChangeSequence, Change = change });

        while (state.State.PendingChanges.Count > ReconcileInput.MaxPendingChanges) {
            state.State.PendingChanges.RemoveAt(0);
            state.State.ChangesDropped++;
        }

        await state.WriteStateAsync();
        return Result.Success;
    }

    /// <inheritdoc />
    public async Task<Result> AcknowledgeChangesAsync(long throughSequence) {
        if (!state.State.Exists) {
            return NotFound();
        }

        var before = state.State.PendingChanges.Count;
        state.State.PendingChanges.RemoveAll(x => x.Sequence <= throughSequence);

        // ⚠ The drop count is reset only when the acknowledgement reaches the newest event. A pass
        // that read a list with drops in it and converged has rescanned, so the drops it saw are
        // dealt with — but if more arrived after its read, those may have been dropped too, and the
        // next pass has to be told.
        var caughtUp = state.State.PendingChanges.Count == 0;
        if (caughtUp) {
            state.State.ChangesDropped = 0;
        }

        if (before != state.State.PendingChanges.Count || caughtUp) {
            await state.WriteStateAsync();
        }

        return Result.Success;
    }

    /// <inheritdoc />
    public Task DeactivateAsync() {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }

    // ── Internals ──────────────────────────────────────────────────────────────────────────────

    /// <summary>The period this resource's type declared, or zero.</summary>
    TimeSpan PassPeriod() {
        var address = ResourceId.ParsePath(state.State.Path);
        return address.IsSuccess && registry.TryGetType(address.GetValueOrThrow().Type, out var registration)
            ? registration.PassPeriod
            : TimeSpan.Zero;
    }

    /// <summary>Registers the <c>periodic-pass</c> reminder, once, for a type that declares a period.</summary>
    /// <remarks>
    ///     ⚠ <b>Only when <c>GetReminder</c> answers nothing</b> — the lesson docs/plan/08 § Two-phase
    ///     create records from #83. Re-registering on every converged write would push the first due
    ///     time out again each time, and a resource written more often than its period would never
    ///     get a pass at all.
    /// </remarks>
    /// <returns><c>true</c> if this call registered the reminder.</returns>
    async Task<bool> ArmPeriodicPassCoreAsync() {
        var period = PassPeriod();

        if (period == TimeSpan.Zero || await this.GetReminder(PeriodicPass.ReminderName) is not null) {
            return false;
        }

        await this.RegisterOrUpdateReminder(PeriodicPass.ReminderName, PeriodicPass.FirstDue(resourceId, period), period);
        return true;
    }

    /// <summary>Removes the <c>periodic-pass</c> reminder if there is one.</summary>
    /// <param name="period">
    ///     The type's period, read before a clear took the path away. ⚠ Zero skips the reminder
    ///     table entirely, which is what keeps every type without a period — all but one — from
    ///     paying a reminder read on every delete.
    /// </param>
    async Task DisarmPeriodicPassAsync(TimeSpan period) {
        if (period == TimeSpan.Zero) {
            return;
        }

        if (await this.GetReminder(PeriodicPass.ReminderName) is { } reminder) {
            await this.UnregisterReminder(reminder);
        }
    }

    /// <summary>The resource as the API renders it, projected to one api-version.</summary>
    /// <remarks>
    ///     ⚠ <see cref="ResourceSnapshot.Body" /> is <see cref="ResourceProjection.Project" /> over
    ///     the stored superset and nothing else — the whole projected document, <c>location</c> and
    ///     <c>properties</c> included. The gateway's writer splices that document into the response
    ///     envelope; the projection is shared rather than private so the gateway suite's substitute
    ///     manager can produce the same shape from the same function.
    /// </remarks>
    ResourceSnapshot Snapshot(string apiVersion, ImmutableArray<string> declaredPointers) {
        var superset = Parse(state.State.Superset).GetValueOrThrow();
        var projected = ResourceProjection.Project(superset, declaredPointers);

        var type = ResourceTypeName.TryParse(TypeOf(state.State.Path), out var parsedType) ? parsedType : default;

        return new() {
            Id = resourceId,
            Path = state.State.Path,
            Type = type.ToString(),
            Name = NameOf(state.State.Path),
            ApiVersion = apiVersion,
            ProvisioningState = state.State.ProvisioningState,
            Body = projected.ToJsonString(),
            Tags = state.State.Tags.ToImmutableDictionary(StringComparer.Ordinal),
            Etag = state.State.Etag,
            Location = state.State.Location,
            ClusterId = state.State.ClusterId,
            CreatedBy = state.State.CreatedBy,
            CreatedAt = state.State.CreatedAt,
            ModifiedBy = state.State.ModifiedBy,
            ModifiedAt = state.State.ModifiedAt,
            LastFailure = state.State.LastFailure,
            OperationId = state.State.OperationId,
            Lock = state.State.Lock,
            Version = state.State.Version
        };
    }

    /// <summary>
    ///     Removes every pointer this version declares, then writes the ones the body carries.
    /// </summary>
    /// <remarks>
    ///     ⚠ This is what makes <c>PUT</c> a <i>replacement</i>: a property the caller left out is
    ///     removed rather than kept. It replaces only the declared slice, so a newer api-version's
    ///     fields survive an old-version <c>PUT</c> — the superset is shared and an old client must
    ///     not be able to delete what it cannot see.
    /// </remarks>
    static JsonObject ReplaceSlice(JsonObject superset, ImmutableArray<string> declared, JsonObject body) {
        var next = (JsonObject)superset.DeepClone();

        if (declared.IsDefaultOrEmpty) {
            // No registry slice to scope the replacement to: replace outright, which is what a type
            // with no declared properties means.
            return (JsonObject)body.DeepClone();
        }

        foreach (var pointer in declared) {
            // ⚠ A CONTAINER IS NEVER REMOVED, AND SKIPPING IT IS LOAD-BEARING.
            //
            // The schema models a nested object as its own property plus deeper ones — /properties
            // and /properties/size are two entries. Removing /properties would take every member
            // inside it, INCLUDING the ones a newer api-version declares and this version has never
            // heard of. That is exactly the data loss this method's scoping exists to prevent, and it
            // is not hypothetical: without this guard, a 2026 PUT wiped the 2027-only field, and
            // WritePathTests.AnOldVersionPutDoesNotDeleteANewerVersionsField is what caught it.
            //
            // The leaves inside the container are removed individually by their own pointers, so the
            // replacement semantics of PUT are unchanged for everything this version declares.
            if (JsonPointer.Read(next, pointer) is JsonObject) {
                continue;
            }

            JsonPointer.Remove(next, pointer);
        }

        foreach (var pointer in declared) {
            var value = JsonPointer.Read(body, pointer);
            if (value is null or JsonObject) {
                continue;
            }

            JsonPointer.Write(next, pointer, value.DeepClone());
        }

        return next;
    }

    /// <summary>RFC 7386 JSON Merge Patch: an absent member is left alone, an explicit null removes.</summary>
    static JsonObject MergePatch(JsonObject target, JsonObject patch) {
        var next = (JsonObject)target.DeepClone();

        foreach (var member in patch) {
            if (member.Value is null) {
                next.Remove(member.Key);
                continue;
            }

            if (member.Value is JsonObject nested && next[member.Key] is JsonObject existing) {
                next[member.Key] = MergePatch(existing, nested);
                continue;
            }

            next[member.Key] = member.Value.DeepClone();
        }

        return next;
    }

    static Result<JsonObject> Parse(string json) {
        try {
            var node = JsonNode.Parse(json);
            return node is JsonObject obj
                ? Result<JsonObject>.Success(obj)
                : Result<JsonObject>.Failure(
                    ErrorCode.InvalidRequestBody,
                    "A resource body is a JSON object.",
                    ""
                );
        } catch (JsonException exception) {
            return Result<JsonObject>.Failure(
                ErrorCode.InvalidRequestBody,
                // ⚠ The parser's message names the offset and the token and nothing about our stack —
                // docs/plan/08 § Errors bans exception details in an error body, and a JSON syntax
                // message is data about the caller's own input rather than about us.
                $"The request body is not valid JSON: {exception.Message}",
                ""
            );
        }
    }

    static Error? CheckTags(ImmutableDictionary<string, string> tags) =>
        tags.Count <= MaxTags
            ? null
            : new Error(
                ErrorCode.InvalidRequestBody,
                $"A resource carries at most {MaxTags.ToString(CultureInfo.InvariantCulture)} tags "
                + $"and {tags.Count.ToString(CultureInfo.InvariantCulture)} were supplied — "
                + "docs/plan/06 § Tags, locks, and the small stuff that is not small.",
                "/tags"
            );

    static bool TagsEqual(Dictionary<string, string> left, ImmutableDictionary<string, string> right) {
        if (left.Count != right.Count) {
            return false;
        }

        foreach (var pair in left) {
            if (!right.TryGetValue(pair.Key, out var value)
                || !string.Equals(value, pair.Value, StringComparison.Ordinal)) {
                return false;
            }
        }

        return true;
    }

    /// <summary>A fresh etag. Opaque by construction, so nobody parses it.</summary>
    static string NextEtag() => Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

    static string TypeOf(string path) {
        var parsed = ResourceId.ParsePath(path);
        return parsed.IsSuccess ? parsed.GetValueOrThrow().Type.ToString() : string.Empty;
    }

    static string NameOf(string path) {
        var parsed = ResourceId.ParsePath(path);
        return parsed.IsSuccess ? parsed.GetValueOrThrow().Name : string.Empty;
    }

    Result<T> NotFound<T>()
        where T : notnull =>
        Result<T>.Failure(
            ErrorCode.ResourceNotFound,
            $"Resource {resourceId:D} does not exist."
        );

    Result NotFound() => Result.Failure(ErrorCode.ResourceNotFound, $"Resource {resourceId:D} does not exist.");
}
