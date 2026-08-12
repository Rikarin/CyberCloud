using CyberCloud.Core.Time;
using CyberCloud.ResourceManager.Reconcile;
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
    IClock clock
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

        // ── A SOFT DELETE RUNS NO RECONCILE PASS, AND THAT IS THE POINT OF IT ────────────────────
        //
        // ⚠ docs/plan/08 § Soft delete: a resource in its recovery window "consumes plenty, because
        // handing the data back is the entire feature: the volumes, the PVCs and the memory are all
        // still allocated". So there is nothing to tear down — and running the driver anyway would do
        // the opposite of nothing in both directions: with tearingDown TRUE it would destroy exactly
        // the data the window exists to hand back, and with it FALSE it would RE-APPLY the desired
        // state of a resource the caller has just deleted. Neither is a pass worth taking, so the
        // teardown-and-return half is skipped and the parking half runs on its own.
        if (spec.Kind == OperationKind.Delete && spec.SoftDelete) {
            await ParkAsync(spec);
            return Result<OperationStatus>.Success(Status());
        }

        // ⚠ A PURGE TEARS DOWN. It is the half of the delete this type deferred — see
        // OperationKind.Purge — so it is the one kind other than Delete that drives the reconciler's
        // DeleteAsync rather than its ReconcileAsync.
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
            await TerminateAsync(Contracts.OperationState.Canceled, null);
            return;
        }

        if (spec.Kind is OperationKind.Delete or OperationKind.Purge) {
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

            await ReturnCommittedQuotaAsync(spec);
            await TerminateAsync(Contracts.OperationState.Succeeded, null);
            return;
        }

        await CommitAsync(spec);
        await FinishResourceAsync(spec, ProvisioningState.Succeeded, null);
        await TerminateAsync(Contracts.OperationState.Succeeded, null);
    }

    /// <summary>
    ///     The soft delete's whole convergence: re-parent the resource, drop its direct role
    ///     assignments, and stop.
    /// </summary>
    /// <remarks>
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
    ///         restore — and the resource grain keeps its state because the restore hands it back. A
    ///         <c>CompleteDeleteAsync</c> here would be the delete this operation deliberately did not
    ///         do.
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

        Append(
            Progress(
                "parked",
                $"'{spec.ResourcePath}' is soft-deleted: it is no longer addressable, its name is held "
                + "for its recovery window, its parent edge names its subscription and "
                + dropped.GetValueOrThrow().ToString(CultureInfo.InvariantCulture)
                + " direct role assignment(s) were dropped. Its quota stays committed until it is "
                + "purged."
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

    TenantGrainFactory Tenant(OperationSpec spec) =>
        grains.ForTenant(spec.TenantId.ToString("D", CultureInfo.InvariantCulture));

    static bool IsTerminal(Contracts.OperationState status) =>
        status is Contracts.OperationState.Succeeded
            or Contracts.OperationState.Failed
            or Contracts.OperationState.Canceled;
}
