using CyberCloud.Core.Time;
using CyberCloud.ResourceManager.Reconcile;
using Microsoft.Extensions.Logging;
using Orleans.Multitenant;
using System.Globalization;

namespace CyberCloud.ResourceManager.Grains;

/// <summary>
///     <see cref="IOperationGrain" /> — Coordinator, Durable, key <c>op/{operationId:N}</c>.
/// </summary>
/// <remarks>
///     <para>
///         This grain is docs/plan/00's non-negotiable <i>every LRO is resumable</i>. Everything it
///         needs to continue after a silo loss is in <see cref="OperationGrainState" />, and
///         <see cref="OnActivateAsync" /> re-registers the reminder and re-drives without being asked.
///     </para>
///     <para>
///         ⚠ <b>The reminder and <see cref="DriveAsync" /> are separate on purpose.</b> Orleans'
///         minimum reminder period is one minute, and the backoff ladder starts at ten seconds — so a
///         reminder alone cannot express the schedule, and a test that waited for one would take an
///         hour. The reminder is the <i>safety net</i> that survives a silo loss; the scheduled
///         in-activation timer is what honours the ladder; and <see cref="DriveAsync" /> is the body
///         both call and a test drives directly.
///     </para>
/// </remarks>
public sealed class OperationGrain(
    [PersistentState("operation", StorageTiers.Durable)] IPersistentState<OperationGrainState> state,
    ReconcileDriver driver,
    IResourceRelationWriter relations,
    IGrainFactory grains,
    IClock clock,
    ILogger<OperationGrain> logger
)
    : Grain, IOperationGrain, IRemindable {
    /// <summary>
    ///     How many progress entries the array keeps.
    /// </summary>
    /// <remarks>
    ///     ⚠ An unbounded array on a durable grain is a row that grows until the write fails. A
    ///     four-hour run reporting once a second would produce 14 400 entries, and nobody reads past
    ///     the last few. The oldest go first and <see cref="OperationGrainState.ProgressDropped" />
    ///     records how many, so a reader can tell a truncated history from a short one.
    /// </remarks>
    public const int MaxProgressEntries = 200;

    /// <summary>The reminder's name. One reminder per operation.</summary>
    public const string ReminderName = "reconcile";

    /// <summary>
    ///     The reminder's period. ⚠ Orleans' floor is one minute; the ladder is honoured by the
    ///     in-activation schedule, and this exists so an operation whose silo died is still picked up.
    /// </summary>
    public static TimeSpan ReminderPeriod { get; } = TimeSpan.FromMinutes(1);

    Guid operationId;

    /// <inheritdoc />
    public override async Task OnActivateAsync(CancellationToken cancellationToken) {
        _ = ResourceManagerGrainKeys.TenantOf(this);
        operationId = ResourceManagerGrainKeys.Decode(this, GrainKeyKind.Operation).Id;

        // ⚠ THE RESUME PATH. docs/plan/08 § Long-running operations: "On activation after a silo loss
        // the grain re-registers its reminder and continues."
        //
        // The Activations counter is what makes that observable: a test deactivates mid-operation,
        // touches the grain, and sees the count go up and the operation carry on from its recorded
        // attempt rather than from zero. Without a counter, "it resumed" and "it never stopped" look
        // identical from outside.
        if (state.State.Spec is not null && !IsTerminal(state.State.Status)) {
            state.State.Activations++;
            await state.WriteStateAsync(cancellationToken);
            await EnsureReminderAsync();
        }
    }

    /// <inheritdoc />
    public async Task<Result> StartAsync(OperationSpec spec) {
        ArgumentNullException.ThrowIfNull(spec);

        if (state.State.Spec is not null) {
            // ⚠ Starting the SAME spec twice succeeds and changes nothing. That is what makes a
            // retried PUT a no-op all the way down (docs/plan/06 § Two-phase create) — the gateway
            // reuses the operation id it derived from the request, so the retry lands here.
            return string.Equals(state.State.Spec.Desired, spec.Desired, StringComparison.Ordinal)
                && state.State.Spec.ResourceId == spec.ResourceId
                && state.State.Spec.Kind == spec.Kind
                    ? Result.Success
                    : Result.Failure(
                        ErrorCode.Conflict,
                        $"Operation {operationId:D} was already started for "
                        + $"'{state.State.Spec.ResourcePath}' ({state.State.Spec.Kind}) and cannot be "
                        + "restarted with a different spec. Start a new operation."
                    );
        }

        state.State.Spec = spec;
        state.State.Status = Contracts.OperationState.NotStarted;
        state.State.StartedAt = clock.UtcNow;

        await state.WriteStateAsync();
        await EnsureReminderAsync();

        return Result.Success;
    }

    /// <inheritdoc />
    public Task<Result<OperationStatus>> GetAsync() =>
        Task.FromResult(
            state.State.Spec is null
                ? Result<OperationStatus>.Failure(
                    ErrorCode.ResourceNotFound,
                    $"Operation {operationId:D} does not exist."
                )
                : Result<OperationStatus>.Success(Status())
        );

    /// <inheritdoc />
    public async Task<Result> ReportAsync(OperationProgress progress) {
        ArgumentNullException.ThrowIfNull(progress);

        if (state.State.Spec is null) {
            return Result.Failure(
                ErrorCode.ResourceNotFound,
                $"Operation {operationId:D} does not exist, so there is nothing to report against."
            );
        }

        Append(progress);
        await state.WriteStateAsync();
        return Result.Success;
    }

    /// <inheritdoc />
    public async Task<Result> CancelAsync(string reason) {
        if (state.State.Spec is null) {
            return Result.Failure(
                ErrorCode.ResourceNotFound,
                $"Operation {operationId:D} does not exist."
            );
        }

        if (IsTerminal(state.State.Status)) {
            return Result.Failure(
                ErrorCode.Conflict,
                $"Operation {operationId:D} is already {state.State.Status} and cannot be cancelled. "
                + "Cancelling a finished operation would promise a teardown that will never run."
            );
        }

        state.State.CancelRequested = true;
        state.State.CancelReason = string.IsNullOrWhiteSpace(reason) ? "No reason given." : reason;

        Append(
            Progress(
                "cancelling",
                // ⚠ Says explicitly that this is not the end, because the difference is the whole
                // point: docs/plan/08 § Long-running operations, "cancellation completes rather than
                // abandoning".
                $"Cancellation requested: {state.State.CancelReason} Tearing down anything already "
                + "applied before the operation reports Canceled."
            )
        );

        await state.WriteStateAsync();

        // Not awaited to completion here: CancelAsync returns as soon as the flag is set, and the
        // teardown runs on the next drive. A caller wanting "it has stopped" polls for
        // OperationState.Canceled.
        await EnsureReminderAsync();
        return Result.Success;
    }

    /// <inheritdoc />
    public async Task<Result<OperationStatus>> DriveAsync() {
        if (state.State.Spec is null) {
            return Result<OperationStatus>.Failure(
                ErrorCode.ResourceNotFound,
                $"Operation {operationId:D} does not exist."
            );
        }

        if (IsTerminal(state.State.Status)) {
            return Result<OperationStatus>.Success(Status());
        }

        var spec = state.State.Spec;
        var now = clock.UtcNow;

        // ⚠ THE SIXTY-MINUTE CEILING, CHECKED BEFORE THE PASS RATHER THAN AFTER.
        // docs/plan/08 § The reconcile loop: "a resource stuck forever is worse than a resource that
        // failed, because a failure is actionable." Checking first means the ceiling holds even if
        // every pass hangs for its full budget; checking after would let one more 30-second pass run
        // past the hour every time.
        if (ReconcileSchedule.HasTimedOut(state.State.StartedAt, now)) {
            var timeout = ReconcileSchedule.TimedOut(operationId, spec.ResourcePath, LastProgress());
            await FailAsync(timeout);
            return Result<OperationStatus>.Success(Status());
        }

        state.State.Status = Contracts.OperationState.Running;
        state.State.Attempts++;

        // ── A SOFT DELETE TEARS THE DATA PLANE DOWN, AND EVERY OTHER PART OF IT IS WHAT MAKES THE
        //    WINDOW A WINDOW ──────────────────────────────────────────────────────────────────────
        //
        // ⚠ THIS BRANCH USED TO RETURN HERE WITHOUT RUNNING A PASS, AND THAT WAS THE DEFECT TWO
        // PROVIDERS WITHDREW THEIR RECOVERY WINDOWS OVER. Its argument was that a resource in its
        // window "consumes plenty, because handing the data back is the entire feature: the volumes,
        // the PVCs and the memory are all still allocated", so there was nothing to tear down. The
        // first clause is true and the conclusion does not follow from it. What a restore needs back
        // is the DATA, and a Kubernetes teardown does not remove the data: deleting a StatefulSet
        // leaves the PersistentVolumeClaims its volumeClaimTemplate created, which is Kubernetes' own
        // behaviour and is why ContainerRegistryReconciler.DeleteAsync can say in as many words that
        // a soft-deleted registry's layers, database and job queue are all still on disk. What a
        // teardown DOES remove is the running half — the pods, the Services, the credentials Secret
        // and, on CyberCloud.Monitor/workspaces, the VMUser that vmauth authorises tenant writes
        // against. Leaving those standing is what docs/plan/06 § Two-phase create forbids in as many
        // words: a resource must never be "silently gone while its pods still run and its meter still
        // ticks", and a parked resource is silently gone by construction, because ResolveAsync
        // refuses it and the tenant cannot see it in order to delete it again.
        //
        // ⚠ SO THE PASS RUNS WITH tearingDown TRUE, EXACTLY AS A HARD DELETE'S DOES, and the four
        // things a soft delete does differently all happen AFTER it converges — see ConvergedAsync:
        // the name is held rather than released (ResourceManagerService.DeleteAsync parked the index
        // on the request path), the committed quota is kept rather than returned, the resource grain
        // keeps its desired state rather than being cleared, and the ReBAC parent edge moves to the
        // subscription. Those four are the recovery window. The teardown is not one of them.
        //
        // ⚠ AND A RESTORE IS THE OTHER HALF: OperationKind.Restore drives ReconcileAsync over the
        // body the grain still holds, which is why nothing above may clear it.
        //
        // ⚠ A PURGE TEARS DOWN TOO, and by then there is usually nothing left to remove — the soft
        // delete already did it and the reconciler is idempotent, so the pass converges on the first
        // read-back. It is still driven rather than skipped, because a purge must also converge for a
        // resource whose soft-delete teardown never finished.
        var tearingDown = spec.Kind is OperationKind.Delete or OperationKind.Purge
            || state.State.CancelRequested;

        var pass = await driver.RunAsync(spec, tearingDown);

        foreach (var entry in pass.Progress) {
            Append(entry);
        }

        state.State.Applied |= pass.Applied;

        if (pass.Progress.Length > 0) {
            state.State.PercentComplete = pass.Progress[^1].PercentComplete;
        }

        switch (pass.Outcome.Kind) {
            case ReconcileOutcomeKind.Converged:
                await ConvergedAsync(spec, tearingDown);
                break;

            case ReconcileOutcomeKind.Failed when !pass.Outcome.Retryable:
                await FailAsync(pass.Outcome.Error!);
                break;

            case ReconcileOutcomeKind.Failed when spec.Kind == OperationKind.Delete:
                // ⚠ A TEARDOWN PASS THAT FAILED AND WILL BE RETRIED, RECORDED AGAINST THE GROUP'S
                // LISTING RATHER THAN ONLY AGAINST THE OPERATION.
                //
                // docs/plan/06 § Two-phase create: a resource whose teardown fails "is left in
                // Deleting with a retry reminder and is *visible* in listings with that state".
                // FailDeleteAsync is the method that says so — it deliberately cannot remove the
                // member and deliberately cannot move it to Failed, both of which would make the
                // resource look finished while its pods still run and its meter still ticks — and
                // ResourceGroupMember.TeardownAttempts is a COUNT, which only means anything if this
                // is recorded per failed pass rather than once at the end.
                //
                // ⚠ ON THE RETRYABLE BRANCH RATHER THAN THE TERMINAL ONE, because the terminal one
                // is FailAsync and it records the same thing on its way out. Between them the member
                // carries the latest reason and the number of attempts behind it, which is what an
                // operator looking at a stuck group listing needs and what the operation's own
                // progress array — capped, and reachable only if you know the operation id — does not
                // give them.
                //
                // ⚠ Best effort. This is the retry path: the pass is coming back, and failing the
                // delete over a bookkeeping write would turn a recoverable teardown into a stuck one.
                _ = await Group(spec).FailDeleteAsync(spec.ResourceId, pass.Outcome.Error!.Message);
                await ScheduleAsync(pass.Outcome);
                break;

            case ReconcileOutcomeKind.Failed:
            case ReconcileOutcomeKind.InProgress:
            default:
                await ScheduleAsync(pass.Outcome);
                break;
        }

        return Result<OperationStatus>.Success(Status());
    }

    /// <inheritdoc />
    public async Task ReceiveReminder(string reminderName, TickStatus status) {
        if (!string.Equals(reminderName, ReminderName, StringComparison.Ordinal)) {
            return;
        }

        _ = await DriveAsync();
    }

    /// <inheritdoc />
    public Task DeactivateAsync() {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }

    // ── The three endings ──────────────────────────────────────────────────────────────────────

    /// <summary>The pass converged. Which ending that is depends on why we were tearing down.</summary>
    async Task ConvergedAsync(OperationSpec spec, bool tearingDown) {
        if (tearingDown && state.State.CancelRequested && spec.Kind != OperationKind.Delete) {
            // ⚠ A CANCELLED CREATE, COMPLETED. The teardown converged, so everything this operation
            // applied is gone, and only now does the operation report Canceled. docs/plan/08
            // § Long-running operations: "A 'cancelled' create that leaves resources running is a
            // billing dispute waiting to happen."
            state.State.CancelTeardownDone = true;
            await ReleaseAsync(spec);
            await FinishResourceAsync(spec, ProvisioningState.Canceled, null);

            // ⚠ AND THE MEMBER IS STAMPED Canceled RATHER THAN REMOVED. A cancelled create leaves a
            // resource grain behind in Canceled — FinishResourceAsync above is what puts it there —
            // so the group still contains it and a listing that dropped it would hide a resource that
            // is still addressable. CompleteCreateAsync takes Canceled for exactly this ending.
            await StampMemberAsync(spec, ProvisioningState.Canceled);
            await TerminateAsync(Contracts.OperationState.Canceled, null);
            return;
        }

        // ⚠ A SOFT DELETE'S TEARDOWN CONVERGED, AND NOW EVERYTHING THE HARD DELETE DOES NEXT IS
        // SKIPPED. That list is the recovery window itself: CompleteDeleteAsync would throw away the
        // desired state a restore re-applies, the unlink would remove the edge ParkAsync is about to
        // move, the child-count decrement would let a parent be deleted out from under a resource
        // that can still come back, and ReturnCommittedQuotaAsync would hand back an allowance the
        // purge is going to hand back again — failure class (a), quota returned twice.
        if (spec.Kind == OperationKind.Delete && spec.SoftDelete) {
            await ParkAsync(spec);
            return;
        }

        if (spec.Kind is OperationKind.Delete or OperationKind.Purge) {
            // ── AND THE VOLUMES THE TEARDOWN DELIBERATELY KEPT GO NOW, FIRST OF EVERYTHING BELOW ──
            //
            // ⚠ THIS BRANCH IS REACHED BY A HARD DELETE AND BY A PURGE AND BY NOTHING ELSE, WHICH IS
            // WHY THE RECLAIM BELONGS ON IT RATHER THAN INSIDE A RECONCILER. A soft delete returned
            // one branch up, at ParkAsync, and the claims it left standing are what
            // docs/plan/08 § Soft delete calls "the half a teardown never touches" — the disks a
            // restore restores from. Ending the window is the only moment they may go, and this line
            // is that moment for the purge and the equivalent one for a type that never had a window.
            //
            // ⚠ BEFORE IResourceGrain.CompleteDeleteAsync, AND THAT ORDER IS FORCED RATHER THAN
            // CHOSEN. The claim names are derived from the DESIRED BODY — the replica count of each
            // StatefulSet is what says how many ordinals exist — and CompleteDeleteAsync is the call
            // that throws that body away. Reclaiming afterwards would ask a provider to name claims
            // out of an empty grain, which is the same "purge leaves the volumes" defect with an
            // extra step in front of it.
            //
            // ⚠ AND BEFORE ReturnCommittedQuotaAsync FOR THE OPPOSITE REASON: THIS ONE CAN FAIL, AND
            // A PURGE THAT HALF-SUCCEEDS MUST LEAVE SOMETHING AN OPERATOR CAN FINISH. Returning the
            // quota first and failing here would report an allowance the tenant may spend while their
            // disks are still allocated against it. Failing first leaves the operation Deleting, with
            // the reason on the resource, its name still released (the request path did that, for the
            // hard delete's reason) and its quota still held — and the retry re-reads every claim, so
            // a reclaim interrupted halfway costs a pass rather than correctness.
            //
            // ⚠ THE CANCELLED-CREATE TEARDOWN ABOVE DELIBERATELY DOES NOT REACH THIS. That branch is
            // shared by a cancelled create and a cancelled UPDATE, and an update's resource existed
            // before the operation did — removing its disks because a change to it was cancelled is
            // the one mistake on this path that cannot be undone. A cancelled create's claims are
            // recorded as owed in docs/plan/08 § Soft delete instead of guessed at here.
            var reclaimed = await driver.ReclaimVolumesAsync(spec);

            foreach (var entry in reclaimed.Progress) {
                Append(entry);
            }

            if (reclaimed.Outcome.Kind != ReconcileOutcomeKind.Converged) {
                // ⚠ A REFUSAL IS TERMINAL AND A WAIT IS NOT, and VolumeReclaimer is what distinguishes
                // them. A claim that is not this resource's will not become this resource's on the
                // next attempt, so retrying it is thirty-nine more chances to destroy it; that comes
                // back non-retryable and fails the operation with the claim and the label named. A
                // claim still held by a terminating pod comes back InProgress and is re-driven.
                if (reclaimed.Outcome.Kind == ReconcileOutcomeKind.Failed && !reclaimed.Outcome.Retryable) {
                    await FailAsync(reclaimed.Outcome.Error!);
                    return;
                }

                await ScheduleAsync(reclaimed.Outcome);
                return;
            }

            // ⚠ Only now is the grain state removed — the reconciler READ THE OBJECTS BACK AS GONE.
            // docs/plan/06 § Two-phase create's harder half: the index was released first so the name
            // was immediately reusable, the data plane came down second, and the grain goes last.
            var completed = await Resource(spec).CompleteDeleteAsync();
            if (completed.TryGetError(out var completeError)) {
                await ScheduleAsync(ReconcileOutcome.Failed(completeError, true));
                return;
            }

            // ⚠ AND THE ReBAC PARENT EDGE GOES WITH IT. THIS IS THE OTHER HALF OF THE WRITE PATH'S
            // STEP 8, AND IT LIVES HERE RATHER THAN IN ResourceManagerService.DeleteAsync.
            //
            // Two reasons, and both are about *when* the resource stops existing:
            //
            //   • A delete is accepted long before it converges. The resource stays visible in
            //     Deleting the whole time — docs/plan/06 § Two-phase create insists on that, calling
            //     it "a billing-dispute prevention measure as much as a correctness one" — and a
            //     resource its owner cannot READ is a resource they cannot watch being deleted.
            //     Unlinking at the request would blind them to their own teardown, and a teardown
            //     that then failed would leave a live, billed, invisible resource. So the edge
            //     survives until the resource does not, which is this line.
            //   • It has to be able to fail and be retried. A dangling tuple pointing at a GUID that
            //     no longer names anything is a slow leak in the tenant's tuple store, so "best
            //     effort, log and move on" is not good enough. This grain is durable and is
            //     re-driven from a reminder, which is exactly the machinery that leak needs.
            //
            // ⚠ THE RETRY CONVERGES, AND THAT IS CHECKABLE RATHER THAN HOPED FOR. On the next drive
            // ReconcileDriver reads a resource grain that is now empty and — because tearingDown is
            // true — reports Converged rather than Failed ("a resource that is gone during a teardown
            // is a teardown that succeeded"). Control reaches this branch again, CompleteDeleteAsync
            // returns Success because it is idempotent on an already-cleared grain, and the unlink is
            // attempted again. TupleStoreGrain.DeleteAsync is idempotent too, so a partially applied
            // previous attempt is not a problem either. The loop ends the way every other one does:
            // at ReconcileSchedule's sixty-minute ceiling, with a Failed operation that names the
            // reason — which is the actionable outcome, and is what a stuck operation is for.
            // ⚠ From the SPEC, not re-resolved. Address(spec) reparses a path, and a parsed path
            // carries no GUIDs at all — so the parent's id has to have been written down at create
            // time. See OperationSpec.ParentResourceId: a lookup here would be a lookup made after
            // the resource is gone, on a retry loop, against a parent that may be gone too.
            // ⚠ AND A PURGE UNLINKS A DIFFERENT EDGE, BECAUSE BY THEN THE RESOURCE HANGS OFF ITS
            // SUBSCRIPTION. docs/plan/08 § Soft delete moved it there at the delete —
            // `#parent@subscription:{sub}` — so building the ordinary subject here would delete a tuple
            // that is not present, report success, and leave the real edge behind: one inert row per
            // purged resource, forever, and nothing to notice it. See
            // IResourceRelationWriter.UnlinkFromSubscriptionAsync.
            var unlinked = spec.Kind == OperationKind.Purge
                ? await relations.UnlinkFromSubscriptionAsync(Address(spec))
                : await relations.UnlinkFromParentAsync(Address(spec), spec.ParentResourceId);

            if (unlinked.TryGetError(out var unlinkError)) {
                await ScheduleAsync(ReconcileOutcome.Failed(unlinkError, true));
                return;
            }

            // ⚠ AND THE PARENT'S CHILD COUNT GOES DOWN, HERE AND NOT AT THE ACCEPT.
            //
            // ResourceManagerService.DeleteAsync refuses a delete while the resource still has
            // children (docs/plan/08 § Deleting a parent resource that has children). This is the other
            // side of that counter, and it belongs on this line for the two reasons the unlink above
            // belongs here — and one more that is specific to it.
            //
            //   • A child that is still tearing down STILL EXISTS. docs/plan/06 § Two-phase create
            //     keeps it visible, in Deleting, with its meter ticking, precisely so a failed teardown
            //     is not mistaken for a completed one. Decrementing at the accept would let the parent
            //     be deleted while such a child was still running — the orphan the counter exists to
            //     prevent, reintroduced in the window where it is most likely, because the child's
            //     teardown is exactly what tends to get stuck.
            //   • It has to be able to fail and be retried, and this grain is the durable, reminder-
            //     driven machinery for that. A count left high is worse than a dangling tuple: the
            //     parent answers 409 to its own delete until somebody clears it by hand.
            //
            // ⚠ IDEMPOTENT ON BOTH SIDES, WHICH IS WHAT MAKES THE RETRY SAFE. RemoveChildAsync clamps
            // at zero and succeeds for a type it is not holding, so a second drive after a partial
            // success cannot push the count negative — and a negative count would make the parent
            // deletable while another child was still live, which is the failure with the sign flipped.
            //
            // ⚠ From the SPEC's path, like the unlink: Address(spec) reparses ResourcePath, and
            // ResourceId.Parent is a pure function of it. Nothing here reads the parent's GUID, so
            // unlike the unlink this converges even for a parent that is already gone.
            var uncounted = await UncountFromParentAsync(spec);
            if (uncounted.TryGetError(out var uncountError)) {
                await ScheduleAsync(ReconcileOutcome.Failed(uncountError, true));
                return;
            }

            // ── AND THE RESOURCE LEAVES ITS GROUP'S MEMBERSHIP, LAST OF ALL ─────────────────────
            //
            // ⚠ THE CLOSING HALF OF ResourceManagerService.DeleteAsync'S BeginDeleteAsync, AND IT IS
            // HERE FOR THE REASON THE UNLINK AND THE UNCOUNT ABOVE ARE. docs/plan/06 § Two-phase
            // create: a resource whose teardown fails "is left in Deleting … and is *visible* in
            // listings with that state — never silently gone while its pods still run and its meter
            // still ticks. That last clause is a billing-dispute prevention measure as much as a
            // correctness one." The member is what a listing reads, so removing it at the accept
            // would be the silent disappearance that sentence forbids — the listing would go quiet
            // while the pods came down, and a teardown that then failed would leave a billed
            // resource nothing lists.
            //
            // ⚠ AND IT IS RETRIED RATHER THAN BEST-EFFORT, LIKE ITS TWO NEIGHBOURS. A member left
            // behind for a resource that no longer exists is a listing that names something whose
            // every read is a 404 — and ResourceGroupGrain refuses to delete a group that still has
            // members, so the leak is not merely cosmetic. CompleteDeleteAsync is idempotent on a
            // member that is already gone, which is what makes the retry safe.
            //
            // ⚠ A PURGE FINDS NOTHING TO REMOVE AND SUCCEEDS, WHICH IS CORRECT RATHER THAN A GAP.
            // ParkAsync took the member out when the soft delete converged — the resource stopped
            // being in the group at that moment, not at the purge — so by the time a purge runs the
            // group has not held it for the length of the recovery window.
            var unlisted = await Group(spec).CompleteDeleteAsync(spec.ResourceId);
            if (unlisted.TryGetError(out var unlistError)) {
                await ScheduleAsync(ReconcileOutcome.Failed(unlistError, true));
                return;
            }

            await ReturnCommittedQuotaAsync(spec);
            await TerminateAsync(Contracts.OperationState.Succeeded, null);
            return;
        }

        await CommitAsync(spec);
        await FinishResourceAsync(spec, ProvisioningState.Succeeded, null);

        // ⚠ THE MEMBER REACHES THE SAME TERMINAL STATE THE RESOURCE JUST DID, AND THIS IS WHAT KEEPS
        // THE REAPER OFF A LIVE RESOURCE. ResourceManagerService's step 7b records the member in
        // Creating; IResourceGroupGrain.ListOrphansAsync enumerates members that have been Creating
        // for longer than a threshold, and docs/plan/06 § Two-phase create has a per-subscription
        // reaper reminder sweep them. A create that converged and never stamped its member would be
        // swept as an orphan — a live, billed resource torn down by a reaper because nothing told the
        // group it had finished.
        //
        // ⚠ ON THE UPDATE AND THE RESTORE TOO, not only the create. An update's member is already
        // terminal and this rewrites the same value, which costs one call and buys the property that
        // the listed state and the resource's state cannot drift; a restore's member was put back in
        // Creating by RestoreAsync and this is what finishes it.
        await StampMemberAsync(spec, ProvisioningState.Succeeded);
        await TerminateAsync(Contracts.OperationState.Succeeded, null);
    }

    /// <summary>
    ///     What a soft delete does once its teardown has converged: re-parent the resource, drop its
    ///     direct role assignments, move it out of its group's listing and into its group's registry
    ///     of what is recoverable, and stop short of everything that would make the delete
    ///     irreversible.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Reached from <see cref="ConvergedAsync" /> and therefore only after the data plane
    ///         is down, read back.</b> Parking before the teardown converged would be the defect this
    ///         path was built to close, one step later: the resource would stop being addressable while
    ///         its pods were still running, and nothing would be driving them down.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Here rather than in <c>ResourceManagerService.DeleteAsync</c>, and for the two
    ///         reasons the unlink beside it lives here.</b> Both writes can fail, and this grain is the
    ///         durable, reminder-driven machinery that re-drives them; and a delete is accepted long
    ///         before it settles, so the request path should not be holding a caller while two tuple
    ///         stores are written. The index park stays on the request path because THAT is what makes
    ///         the resource stop being addressable, and a caller who is told <c>202</c> must not be able
    ///         to read the resource a moment later.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Neither call returns quota, and neither clears the resource.</b> docs/plan/08
    ///         § Soft delete moves the committed-quota return to the purge, whole —
    ///         <c>QuotaMeter.Resources</c> included, because a per-meter split reintroduces the partial
    ///         restore — and the resource grain keeps its state because that stored body is what
    ///         <see cref="OperationKind.Restore" /> applies again. A <c>CompleteDeleteAsync</c> here
    ///         would be the delete this operation deliberately did not do, and it would leave a restore
    ///         with nothing to restore <i>from</i>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The re-parent goes first and the assignment drop second, and both are retried as a
    ///         unit.</b> The re-parent is what keeps the resource visible to somebody — a subscription
    ///         role holder — so running it first means the drop can never leave the resource
    ///         unreachable even for an instant. A failure in either schedules a retry, so the pair
    ///         converges or the operation fails at <c>ReconcileSchedule</c>'s ceiling with a reason,
    ///         which is the actionable outcome.
    ///     </para>
    /// </remarks>
    async Task ParkAsync(OperationSpec spec) {
        var address = Address(spec);

        var reparented = await relations.ReparentToSubscriptionAsync(address, spec.ParentResourceId);
        if (reparented.TryGetError(out var reparentError)) {
            await ScheduleAsync(ReconcileOutcome.Failed(reparentError, true));
            return;
        }

        // ⚠ THE SECURITY HALF, AND IT IS SEPARATE FROM THE MODELLING HALF ON PURPOSE. docs/plan/08
        // § Soft delete: the parent edge is a modelling question and direct role assignments are "a
        // separate question with a security answer rather than a modelling one … running them together
        // is how this gets decided wrongly". Azure's behaviour is copied: the assignments go with the
        // resource and must be recreated on recovery. The window is used after a compromise, and
        // silently restoring a grant an administrator deliberately removed is an error nobody observes.
        var dropped = await relations.DropDirectRoleAssignmentsAsync(address);
        if (dropped.TryGetError(out var dropError)) {
            await ScheduleAsync(ReconcileOutcome.Failed(dropError, true));
            return;
        }

        // ── AND IT JOINS THE REGISTRY OF WHAT IS RECOVERABLE, ONE LINE BEFORE IT LEAVES THE OTHER ─
        //
        // ⚠ THIS IS THE SECOND PLACE TO LOOK, AND WITHOUT IT THE LINE BELOW PUTS THE RESOURCE IN NO
        // LISTING AT ALL. docs/plan/08 § Soft delete: "listing what is recoverable needs an
        // enumeration source, and the platform has none anywhere … the shape that fits is a
        // per-resource-group registry of parked resources, written where ParkAsync unlists the member
        // and cleared where the restore relists it and the purge releases the name". This is the
        // first of those three, and issue #71 is the finding that the filter over ListAsync it was
        // supposed to be one of has an EMPTY INPUT: the index is one grain per path and one-way, so
        // ResolveSoftDeletedAsync answers a question you can only ask if you already know the name.
        //
        // ⚠ BEFORE THE UNLIST AND NOT AFTER, WHICH IS THE ORDER THE REGISTRY'S OWN INVARIANT FIXES:
        // an entry exists only while the index says SoftDeleted. The index was parked on the delete's
        // REQUEST path, long before this line, so the entry is true the moment it is written; and
        // writing it first means there is no crash window — not even a one-call-wide one — in which
        // the resource is in neither collection. The state a crash leaves instead is the resource in
        // the registry AND still a Deleting member of its group, which is the state it has been in
        // for the whole teardown already (ResourceManagerService.DeleteAsync marked the member
        // Deleting at the accept and docs/plan/06 § Two-phase create keeps it listed), so the
        // overlap is one grain call longer rather than new in kind. The opposite order would make the
        // crash window the exact defect this registry exists to close.
        //
        // ⚠ AND IT IS RETRIED RATHER THAN BEST-EFFORT, like both of its neighbours here. A park that
        // silently failed would leave a resource holding its name and its committed quota for a whole
        // recovery window with nothing anywhere naming it — which is worse than the failure it would
        // be hiding, because a failed operation is at least actionable.
        var registered = await Parked(spec).ParkAsync(address);
        if (registered.TryGetError(out var registerError)) {
            await ScheduleAsync(ReconcileOutcome.Failed(registerError, true));
            return;
        }

        // ── AND THE THING THAT WILL END THE WINDOW IS TOLD THERE IS ONE ─────────────────────────
        //
        // ⚠ THE WINDOW OPENS HERE, SO THIS IS WHERE THE CLOCK IS ARMED — issue #12, and the item
        // docs/plan/07 § Azure RBAC left owed after it built both fronts of purge: "nothing yet
        // drives PurgeExpiredAsync on a clock". IExpirySweeperGrain is that clock. It is armed off
        // "this group has something parked" rather than off a deadline, so this call carries no time
        // and makes no claim about one — see the grain's remarks for why the deadline stays with the
        // one activation that stamped it.
        //
        // ⚠ AFTER THE PARK AND NOT BEFORE, because the sweeper disarms itself on a registry it finds
        // empty. Arming first would leave a window in which a tick could run against a group whose
        // entry had not been written yet, cancel the reminder it had just been given, and leave the
        // resource with nothing driving it.
        //
        // ⚠ NOT RETRIED, AND NOT GUARDED ON A RESULT, WHICH IS THE OPPOSITE OF WHAT THIS SAID
        // (2026-09-05, #12 review). It read "RETRIED LIKE ITS NEIGHBOURS" and guarded on
        // `armed.TryGetError`, and that guard was DEAD CODE: IExpirySweeperGrain.ArmAsync returns
        // success on both of its paths — the register and the caught "this silo has no reminder
        // service" — so the branch could never be taken and the retry it described could never
        // happen. What could happen was the thing neither the guard nor the comment covered: a
        // THROWN call. The callee is a non-reentrant activation this design deliberately makes
        // long-running (MaxPerSweep purge choreographies in one turn) and the tree configures no
        // ResponseTimeout, so Orleans' 30-second default applies; a timeout has no Result to
        // inspect and OperationGrain has no try/catch, so it escaped the pass. ArmAsync is now
        // [AlwaysInterleave], which is what actually removes the queueing, and this catch is the
        // belt: a park is finished work and an arm is a schedule, so the delete converges either
        // way.
        //
        // ⚠ AND SWALLOWING IT IS THE SAME DECISION ArmAsync ITSELF TAKES ONE LEVEL DOWN, for the
        // reason its remarks give: failing the delete because the platform could not arrange to
        // purge the resource in seven days' time turns an absent sweeper into an absent platform.
        // What is lost is exactly what master loses today — nothing ends this group's windows on
        // the clock's account — and three things put it back: the group's next park, a hand
        // SweepAsync, and ExpirySweeperBackfill at the next silo start.
        try {
            _ = await Sweeper(spec).ArmAsync();
        }
        catch (Exception error) when (error is not OperationCanceledException) {
            logger.LogWarning(
                error,
                "'{Path}' is parked but its resource group's expiry sweeper could not be armed, so "
                + "nothing in this group is ending recovery windows on a clock until its next park, "
                + "a hand IExpirySweeperGrain.SweepAsync, or the next silo start's backfill. The "
                + "soft delete itself is complete — issue #12.",
                spec.ResourcePath
            );
        }

        // ── AND IT LEAVES THE GROUP'S MEMBERSHIP, WHICH IS NOT THE DELETE THIS PATH REFUSED TO DO ─
        //
        // ⚠ THE GROUP'S CompleteDeleteAsync, NOT THE RESOURCE'S — and the remarks above forbid only
        // the second. IResourceGrain.CompleteDeleteAsync throws away the stored desired state a
        // restore re-applies, which is why this path must not call it; IResourceGroupGrain's removes
        // one entry from a listing and destroys nothing. The two share a name and nothing else.
        //
        // ⚠ AND THE RESOURCE REALLY HAS LEFT THE GROUP BY THIS LINE. The reparent one call up moved
        // its ReBAC parent edge to the SUBSCRIPTION, which is docs/plan/08 § Soft delete's whole
        // model of the recovery window: "the people who can see a deleted resource become the people
        // who hold subscription-scoped rights". Its old path is unaddressable, so a member left
        // behind would put a name into the group's listing whose every read is the canonical 404 —
        // handing a caller who may list the group but may not read the resource exactly the
        // "something is held here" signal that document refuses a 410 Gone over. RestoreAsync's
        // BeginCreateAsync is the exact reverse and puts it back.
        var unlisted = await Group(spec).CompleteDeleteAsync(spec.ResourceId);
        if (unlisted.TryGetError(out var unlistError)) {
            await ScheduleAsync(ReconcileOutcome.Failed(unlistError, true));
            return;
        }

        Append(
            Progress(
                "parked",
                $"'{spec.ResourcePath}' is soft-deleted: its data plane is down, it is no longer "
                + "addressable, its name is held for its recovery window, its parent edge names its "
                + "subscription and "
                + dropped.GetValueOrThrow().ToString(CultureInfo.InvariantCulture)
                + " direct role assignment(s) were dropped. It has left its resource group's listing "
                + "and joined its resource group's registry of parked resources, which is where it "
                + "can be found while the window lasts. Its desired state and its committed quota "
                + "are kept so that a restore can apply it again; both end at the purge, which the "
                + "group's expiry sweeper will run once the window closes if nobody restores or "
                + "purges it first."
            )
        );

        await TerminateAsync(Contracts.OperationState.Succeeded, null);
    }

    /// <summary>The pass failed terminally, or the clock ran out.</summary>
    async Task FailAsync(Error error) {
        var spec = state.State.Spec!;

        await ReleaseAsync(spec);

        // ⚠ A FAILED TEARDOWN LEAVES THE RESOURCE IN Deleting AND VISIBLE.
        // docs/plan/06 § Two-phase create: "A resource whose data plane teardown fails is left in
        // Deleting with a retry reminder and is *visible* in listings with that state — never silently
        // gone while its pods still run and its meter still ticks." The resource grain enforces that
        // itself; passing Failed for a delete records the reason without moving the state.
        await FinishResourceAsync(spec, ProvisioningState.Failed, error);

        // ── AND THE GROUP'S LISTING IS TOLD, IN THE ONE SHAPE THAT DOES NOT LIE ─────────────────
        //
        // ⚠ A FAILED TEARDOWN GOES THROUGH FailDeleteAsync, WHICH DELIBERATELY CANNOT CLEAR THE
        // MEMBER AND DELIBERATELY CANNOT MOVE IT TO Failed. Both would make the resource look
        // finished while its pods still run and its meter still ticks — the exact sentence
        // docs/plan/06 § Two-phase create calls "a billing-dispute prevention measure as much as a
        // correctness one". It records the reason and the attempt count against a member that stays
        // Deleting and stays listed, which is what an operator needs in order to find it.
        //
        // ⚠ EVERY OTHER KIND STAMPS Failed, WHICH IS THE STATE THE RESOURCE ITSELF JUST REACHED. A
        // create that failed terminally leaves a resource in Failed that its owner can still see and
        // delete; a listing showing it as Creating would be a listing that says a dead create is
        // still running, and — worse — ListOrphansAsync would hand it to the reaper.
        //
        // ⚠ BEST EFFORT, BECAUSE THIS IS A TERMINAL ENDING WITH NO RETRY BEHIND IT. FinishResourceAsync
        // one line up discards its result for the same reason: the operation is already reporting a
        // failure with a reason, and there is no later drive in which to converge a bookkeeping write.
        // What is lost is a stale label on one listing entry, against an operation that already failed.
        if (spec.Kind == OperationKind.Delete) {
            _ = await Group(spec).FailDeleteAsync(spec.ResourceId, error.Message);
        }
        else {
            await StampMemberAsync(spec, ProvisioningState.Failed);
        }

        await TerminateAsync(Contracts.OperationState.Failed, error);
    }

    /// <summary>The pass is not finished. Back off and come back.</summary>
    async Task ScheduleAsync(ReconcileOutcome outcome) {
        var delay = ReconcileSchedule.DelayFor(
            state.State.Attempts,
            // The one place non-determinism belongs. ReconcileSchedule.DelayFor takes the sample so
            // the ±20 % property is testable over the whole interval rather than over one seed.
            Random.Shared.NextDouble(),
            outcome.RetryAfter
        );

        Append(
            Progress(
                outcome.Kind == ReconcileOutcomeKind.InProgress ? "waiting" : "retrying",
                outcome.Kind == ReconcileOutcomeKind.InProgress
                    ? $"{outcome.Reason} Next attempt in "
                    + $"{delay.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)}s."
                    : $"Attempt {state.State.Attempts.ToString(CultureInfo.InvariantCulture)} failed "
                    + $"and is retryable: {outcome.Error?.Message} Next attempt in "
                    + $"{delay.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)}s."
            )
        );

        // ⚠ A RETRYABLE FAILURE STILL RECORDS WHY, ON THE RESOURCE.
        //
        // docs/plan/06 § Two-phase create leaves a resource whose teardown failed "in Deleting with a
        // retry reminder and VISIBLE in listings with that state". Visible with no reason is half the
        // measure — an operator looking at a resource that has been Deleting for an hour needs the
        // reason on the resource, not only in an operation they would have to know to poll. The
        // resource grain refuses to move a Deleting resource to Failed, so this records the reason
        // and leaves the state alone.
        if (outcome.Kind == ReconcileOutcomeKind.Failed && outcome.Error is not null) {
            _ = await Resource(state.State.Spec!).CompleteAsync(ProvisioningState.Failed, outcome.Error);
        }

        await state.WriteStateAsync();
        await EnsureReminderAsync();
    }

    async Task TerminateAsync(Contracts.OperationState status, Error? error) {
        state.State.Status = status;
        state.State.EndedAt = clock.UtcNow;
        state.State.Failure = error;

        if (status == Contracts.OperationState.Succeeded) {
            state.State.PercentComplete = 100;
        }

        Append(
            Progress(
                status.ToString().ToLowerInvariant(),
                error is null
                    ? $"The operation finished: {status}."
                    : $"The operation finished: {status}. {error.Message}"
            )
        );

        await state.WriteStateAsync();

        // The reminder is unregistered rather than left to fire on a terminal grain: a reminder with
        // nothing to do is a wakeup per minute per finished operation, forever.
        var reminder = await this.GetReminder(ReminderName);
        if (reminder is not null) {
            await this.UnregisterReminder(reminder);
        }
    }

    // ── Quota, which the operation owns because only it knows whether the work landed ───────────

    /// <summary>
    ///     Releases every lease this operation holds. Called on failure and on cancellation.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Uses <c>IQuotaGrain.ReleaseAsync</c> rather than reimplementing a release.</b>
    ///     docs/plan/06 § Quota already has the semantics: releasing a lease that has already gone
    ///     succeeds rather than failing, <i>"so a failure path re-driven from a reminder is safe to
    ///     run twice"</i>, and a lease nobody released expires on its own because expiry is evaluated
    ///     on read. Both are exactly what this path needs and neither is restated here.
    /// </remarks>
    async Task ReleaseAsync(OperationSpec spec) {
        if (spec.QuotaLeaseIds.IsDefaultOrEmpty) {
            return;
        }

        var quota = Quota(spec);
        foreach (var leaseId in spec.QuotaLeaseIds) {
            _ = await quota.ReleaseAsync(leaseId);
        }
    }

    /// <summary>Turns every lease into committed usage. The resource now exists.</summary>
    async Task CommitAsync(OperationSpec spec) {
        if (spec.QuotaLeaseIds.IsDefaultOrEmpty) {
            return;
        }

        var quota = Quota(spec);
        foreach (var leaseId in spec.QuotaLeaseIds) {
            _ = await quota.CommitAsync(leaseId);
        }
    }

    /// <summary>
    ///     Gives committed quota back after a delete converged.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><c>ReturnAsync</c>, not <c>ReleaseAsync</c>, and the difference was a real defect
    ///         rather than a naming preference.</b> This method used to delegate to
    ///         <see cref="ReleaseAsync" />, which releases <i>leases</i> — and a delete holds none:
    ///         <see cref="OperationSpec.QuotaLeaseIds" /> is empty on every delete spec, because the
    ///         amounts were <b>committed</b> by the create that made the resource. So the call was a
    ///         no-op, a subscription's committed usage climbed by one resource's worth on every
    ///         delete, and the allowance a tenant had paid for never came back.
    ///         <c>IQuotaGrain.ReturnAsync</c> is the method that unwinds committed usage; it existed,
    ///         worked, and had no callers.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Exactly what the create committed, and exactly once.</b> The amounts come off
    ///         <see cref="OperationSpec.CommittedQuota" />, which the delete path derived from the
    ///         resource's stored body with the same function step 6 reserved with — not from the
    ///         leases, which are gone, and not re-derived here, where the resource has already been
    ///         cleared by <c>CompleteDeleteAsync</c>. No lease is released alongside it: a delete has
    ///         none, and doing both would be the double credit.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Safe to run twice, because the delete path is re-driven from a reminder.</b> It is
    ///         called after <c>CompleteDeleteAsync</c> and the unlink have both succeeded, and the
    ///         very next line terminates the operation and unregisters the reminder — so the
    ///         re-drivable window is between them. <c>IQuotaGrain.ReturnAsync</c> is not idempotent
    ///         (it is an unconditional credit), which is why nothing above it may fail after it runs
    ///         and why it is the last quota call the operation makes.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><see cref="QuotaMeter.Unknown" /> and non-positive amounts are skipped rather than
    ///         sent.</b> Both are refused by the grain, and a refusal logged per delete is noise that
    ///         hides a real one. <see cref="QuotaMeter.Unknown" /> is the zero value a
    ///         default-constructed wire type carries, which is what an operation started by a peer
    ///         that predates <see cref="OperationSpec.CommittedQuota" /> would produce.
    ///     </para>
    /// </remarks>
    async Task ReturnCommittedQuotaAsync(OperationSpec spec) {
        if (spec.CommittedQuota.IsDefaultOrEmpty) {
            return;
        }

        var quota = Quota(spec);

        foreach (var commitment in spec.CommittedQuota) {
            if (commitment.Meter == QuotaMeter.Unknown || commitment.Amount <= 0) {
                continue;
            }

            var returned = await quota.ReturnAsync(commitment.Meter, commitment.Amount);
            if (returned.TryGetError(out var error)) {
                // Not fatal to the delete: the resource is gone and refusing to finish the operation
                // would leave it Deleting forever over an accounting entry. It IS worth a line — an
                // unreturned credit is quota a tenant paid for and did not get back.
                Append(
                    Progress(
                        "quota",
                        $"Returning {commitment.Amount} of {commitment.Meter} to subscription "
                        + $"{spec.SubscriptionId:D} failed: {error.Message}"
                    )
                );
            }
        }
    }

    /// <summary>
    ///     Takes this resource off its parent's child counter, now that it is gone.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A top-level resource has nothing to do here and says so by succeeding.</b> Its parent
    ///     is the resource <i>group</i>, which cascades by design (docs/plan/06 § The hierarchy) and
    ///     carries no counter — so the absence of a parent address is the ordinary case rather than a
    ///     fault, and returning a failure for it would stall every top-level delete on a retry loop.
    /// </remarks>
    async Task<Result> UncountFromParentAsync(OperationSpec spec) {
        var address = Address(spec);

        if (address.Parent is not { } parent) {
            return Result.Success;
        }

        var removed = await Tenant(spec)
            .GetGrain<IResourceIndexGrain>(GrainKeys.PathIndex(parent))
            .RemoveChildAsync(address.Type);

        return removed.ToResult();
    }

    /// <summary>
    ///     Stamps the group's membership record with the terminal state the resource just reached.
    /// </summary>
    /// <param name="spec">The operation's spec.</param>
    /// <param name="terminal">The terminal state.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A member that is not there is not an error, and this is the one place that
    ///         tolerance lives.</b> <c>ResourceManagerService</c>'s step 7b records a member for every
    ///         create, so every resource created after it exists in some group's membership. A
    ///         resource created <i>before</i> it has none, and an update or a delete of one reaches
    ///         here with nothing to stamp. Refusing would fail an operation whose work landed, and
    ///         retrying would fail it an hour later at <c>ReconcileSchedule</c>'s ceiling — both of
    ///         them reporting a failure for a write that succeeded. There is nothing to stamp, so not
    ///         stamping it is the whole of the correct behaviour.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Best effort beyond that too, and deliberately not retried.</b> Every caller has
    ///         already written the resource's own terminal state, which is what a read returns; this
    ///         is the listing's copy of it. Scheduling a retry would keep an operation whose work is
    ///         finished in <c>Running</c> over a label, and a poller would see a create that had
    ///         converged still reporting progress. A stale label loses to a stuck operation.
    ///     </para>
    /// </remarks>
    async Task StampMemberAsync(OperationSpec spec, ProvisioningState terminal) {
        var stamped = await Group(spec).CompleteCreateAsync(spec.ResourceId, terminal);

        if (stamped.TryGetError(out var stampError) && stampError.Code != ErrorCode.ResourceNotFound) {
            Append(
                Progress(
                    "membership",
                    $"'{spec.ResourcePath}' reached {terminal} and its resource group's listing could "
                    + $"not be stamped with it: {stampError.Message}. The resource is correct; what is "
                    + "stale is the state the group's listing shows for it."
                )
            );
        }
    }

    // ── Internals ──────────────────────────────────────────────────────────────────────────────

    async Task FinishResourceAsync(OperationSpec spec, ProvisioningState terminal, Error? error) {
        _ = await Resource(spec).CompleteAsync(terminal, error);
    }

    async Task EnsureReminderAsync() =>
        await this.RegisterOrUpdateReminder(ReminderName, ReminderPeriod, ReminderPeriod);

    void Append(OperationProgress progress) {
        state.State.Progress.Add(progress);

        while (state.State.Progress.Count > MaxProgressEntries) {
            state.State.Progress.RemoveAt(0);
            state.State.ProgressDropped++;
        }
    }

    OperationProgress Progress(string step, string detail) =>
        new() { At = clock.UtcNow, Step = step, Detail = detail, PercentComplete = state.State.PercentComplete };

    OperationProgress? LastProgress() =>
        state.State.Progress.Count == 0 ? null : state.State.Progress[^1];

    OperationStatus Status() =>
        new() {
            OperationId = operationId,
            State = state.State.Status,
            ResourcePath = state.State.Spec?.ResourcePath ?? string.Empty,
            ResourceId = state.State.Spec?.ResourceId ?? Guid.Empty,
            StartedAt = state.State.StartedAt,
            EndedAt = state.State.EndedAt,
            Progress = [.. state.State.Progress],
            Error = state.State.Failure,
            PercentComplete = state.State.PercentComplete,
            CancelRequested = state.State.CancelRequested,
            CancelReason = state.State.CancelReason,
            Attempts = state.State.Attempts,
            Activations = state.State.Activations,
            Children = [.. state.State.Children]
        };

    IResourceGrain Resource(OperationSpec spec) => Tenant(spec).GetGrain<IResourceGrain>(GrainKeys.Resource(spec.ResourceId));

    /// <summary>
    ///     The operation's resource as an address, for the ReBAC unlink.
    /// </summary>
    /// <remarks>
    ///     ⚠ Re-parsed from <see cref="OperationSpec.ResourcePath" /> rather than carried as a field:
    ///     the path is what the spec durably records, and a second copy of the resource group on the
    ///     spec would be a second thing that can disagree with it. The path was produced by
    ///     <c>ResourceId.Path</c> and is parsed by its inverse, so a failure here is unreachable — and
    ///     if it ever were reachable, an address of <c>default</c> carries <see cref="Guid.Empty" />
    ///     as its id, which <c>IResourceRelationWriter</c> refuses rather than acting on.
    /// </remarks>
    static ResourceId Address(OperationSpec spec) {
        var parsed = ResourceId.ParsePath(spec.ResourcePath);
        return parsed.IsSuccess ? parsed.GetValueOrThrow().WithId(spec.ResourceId) : default;
    }

    IQuotaGrain Quota(OperationSpec spec) =>
        Tenant(spec).GetGrain<IQuotaGrain>(GrainKeys.Subscription(spec.SubscriptionId));

    /// <summary>
    ///     The resource group whose membership this operation's endings maintain.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The group NAME comes from <see cref="Address" />, which reparses
    ///     <see cref="OperationSpec.ResourcePath" />, and the subscription comes from the spec's own
    ///     field.</b> Both are what the spec durably recorded, so this converges from a reminder after
    ///     the resource itself is gone — which is exactly when the delete path's last two calls run.
    ///     A second copy of the group name on the spec would be a second thing that can disagree with
    ///     the path, which is the argument <see cref="Address" /> already makes.
    /// </remarks>
    IResourceGroupGrain Group(OperationSpec spec) =>
        Tenant(spec)
            .GetGrain<IResourceGroupGrain>(
                GrainKeys.ResourceGroup(spec.SubscriptionId, Address(spec).ResourceGroup)
            );

    /// <summary>
    ///     The same resource group's registry of parked resources — docs/plan/08 § Soft delete.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Built from exactly the two values <see cref="Group" /> is built from</b>, so the two
    ///     grains this path writes in the same breath cannot be addressed at two different groups.
    ///     They are two grain <i>types</i> behind two key shapes rather than one grain with two
    ///     collections, which is docs/plan/08 § Soft delete's refusal to merge them: they answer
    ///     different questions to different callers.
    /// </remarks>
    IParkedResourceRegistryGrain Parked(OperationSpec spec) =>
        Tenant(spec)
            .GetGrain<IParkedResourceRegistryGrain>(
                GrainKeys.ParkedResourceRegistry(spec.SubscriptionId, Address(spec).ResourceGroup)
            );

    /// <summary>
    ///     The same resource group's expiry sweeper — issue #12, docs/plan/07 § Azure RBAC.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Built from the same two values <see cref="Parked" /> is</b>, because it exists to read
    ///     exactly that registry: a sweeper addressed at a different group than the entry just written
    ///     would arm a clock over somebody else's windows and leave this one with none. It is a third
    ///     grain type behind a third key shape over one resource group, which
    ///     <c>GrainKeys.ExpirySweeper</c> argues for at length — the short version is that a purge
    ///     calls back into the registry, so the driver cannot live there.
    /// </remarks>
    IExpirySweeperGrain Sweeper(OperationSpec spec) =>
        Tenant(spec)
            .GetGrain<IExpirySweeperGrain>(
                GrainKeys.ExpirySweeper(spec.SubscriptionId, Address(spec).ResourceGroup)
            );

    TenantGrainFactory Tenant(OperationSpec spec) =>
        grains.ForTenant(spec.TenantId.ToString("D", CultureInfo.InvariantCulture));

    static bool IsTerminal(Contracts.OperationState status) =>
        status is Contracts.OperationState.Succeeded
            or Contracts.OperationState.Failed
            or Contracts.OperationState.Canceled;
}
