using CyberCloud.Core.Time;
using CyberCloud.ResourceManager.Reconcile;
using Orleans.Multitenant;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.ResourceManager.Orchestration;

/// <summary>
///     One pass of a deployment's parent operation: plan the template, write the next resource through
///     the write path as the deployment's creator, and wait for its child operation. docs/plan/08
///     § Long-running operations, "Nested operations".
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The parent's pass in place of a reconciler's, and it returns the same shape.</b>
///         <c>OperationGrain</c> hands its endings a <see cref="ReconcilePass" />; this produces one, so
///         a deployment converges, fails, retries, times out and cancels through the endings every
///         other operation already has — the quota release, the member stamp, the change event and the
///         reminder all come for free and cannot drift from the ordinary ones.
///     </para>
///     <para>
///         ⚠ <b>Strictly one child at a time, in dependency order.</b> A resource is written only when
///         every resource before it in the plan has succeeded, which is stronger than
///         <c>dependsOn</c> requires and is the order a person reading the history can follow. Two
///         independent resources could be written together; they are not, because the first failure
///         would then have to stop a sibling already in flight, and "the deployment stopped at the first
///         failure and wrote nothing after it" is a sentence worth being able to say.
///     </para>
///     <para>
///         ⚠ <b>Failure stops, and nothing is rolled back.</b> Every resource created before the failing
///         one is left in place, and the run's record says which — see <see cref="History" />. Deleting
///         them would be a second set of writes made as the caller after the caller's deployment
///         failed, each able to fail in turn, and a rollback that half-succeeds is worse than a record
///         of what to remove. Azure's deployments do not roll back by default either.
///     </para>
///     <para>
///         ⚠ <b>Every child is written with <see cref="IResourceManager.WriteChildAsync" /></b>, whose
///         remarks carry the argument for writing as a recorded caller. The caller passed is the spec's,
///         unchanged except for the correlation id, which names the parent so a child's audit line leads
///         back to the deployment that wrote it.
///     </para>
/// </remarks>
public sealed class DeploymentDriver(IResourceManager manager, IGrainFactory grains, IClock clock) {
    /// <summary>How soon a waiting parent asks again. The child's own reminder decides how soon it moves.</summary>
    public static TimeSpan PollInterval { get; } = TimeSpan.FromSeconds(10);

    /// <summary>
    ///     How long one pass may keep writing before it yields and lets the reminder bring it back —
    ///     <see cref="ReconcileDriver.PassBudget" /> unless a test says otherwise.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>docs/plan/08 § The reconcile loop's thirty seconds applies to a parent's pass too.</b> A
    ///     pass runs inside the operation grain's turn, and a rerun of a hundred-resource template whose
    ///     every child is a no-op made a hundred full write-path calls in that one turn. The budget is
    ///     checked between steps, never inside one: a step is a single write, and stopping it half way
    ///     would lose a child the write path had accepted. The cursor is persisted when the pass
    ///     returns, so the next pass resumes at the step this one did not reach.
    /// </remarks>
    public TimeSpan Budget { get; init; } = ReconcileDriver.PassBudget;

    /// <summary>Runs one pass.</summary>
    /// <param name="spec">The parent operation's spec — the deployment's desired body and its creator.</param>
    /// <param name="run">The run so far, mutated in place; the caller persists it.</param>
    /// <param name="cancelReason">
    ///     Why the parent is being cancelled, or <see langword="null" /> when it is not — a cancelling
    ///     pass stops the child in flight and starts nothing.
    /// </param>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <returns>
    ///     <see cref="ReconcileOutcome.Converged" /> when every resource is deployed (or, cancelling,
    ///     when nothing is left running); <c>InProgress</c> while a child runs;
    ///     a non-retryable <c>Failed</c> naming the resource that stopped the deployment.
    /// </returns>
    public async Task<ReconcilePass> RunAsync(
        OperationSpec spec,
        DeploymentRunState run,
        string? cancelReason,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(run);

        var progress = ImmutableArray.CreateBuilder<OperationProgress>();

        if (run.Steps.Count == 0) {
            if (cancelReason is not null) {
                progress.Add(Progress("cancelled", "Cancelled before anything was planned or written.", 0));
                return new(ReconcileOutcome.Converged, progress.ToImmutable(), false);
            }

            var planned = Plan(spec);

            if (planned.TryGetError(out var planError)) {
                progress.Add(Progress("planning", $"The template does not deploy: {planError.Message}", 0));
                return new(ReconcileOutcome.Failed(planError), progress.ToImmutable(), false);
            }

            var plan = planned.GetValueOrThrow();

            run.Steps = [
                .. plan.Resources.Select(static x => new DeploymentStepState {
                        ResourcePath = x.Id.Path, ApiVersion = x.ApiVersion, Body = x.Body
                    }
                )
            ];
            run.Cursor = 0;
            run.StepStartedAt = clock.UtcNow;

            progress.Add(
                Progress(
                    "planned",
                    $"{plan.Resources.Length.ToString(CultureInfo.InvariantCulture)} resource(s), in dependency "
                    + $"order: {string.Join(", ", plan.Resources.Select(static x => $"'{x.Id.Name}'"))}.",
                    0
                )
            );
        }

        if (cancelReason is not null) {
            return await CancelAsync(spec, run, cancelReason, progress);
        }

        var passStarted = Stopwatch.GetTimestamp();
        var cursorAtStart = run.Cursor;

        while (run.Cursor < run.Steps.Count) {
            var step = run.Steps[run.Cursor];
            var label = Label(run);

            // At least one step per pass, so a budget smaller than one write still makes progress.
            if (run.Cursor > cursorAtStart && Stopwatch.GetElapsedTime(passStarted) >= Budget) {
                var done = (run.Cursor - cursorAtStart).ToString(CultureInfo.InvariantCulture);

                progress.Add(
                    Progress(
                        "yielding",
                        $"{done} step(s) done this pass, which used its budget; {label} is next.",
                        Percent(run, 0)
                    )
                );

                return new(ReconcileOutcome.InProgress($"Yielded before {label}.", TimeSpan.Zero), progress.ToImmutable(), true);
            }

            if (step.Status == DeploymentStepStatus.Pending) {
                // ⚠ The ceiling for a step whose child was never accepted. A child that WAS accepted
                // has a ceiling of its own and fails through it; this one only has the parent's
                // patience, measured from when the step began rather than from the deployment's start
                // — a twenty-resource template is not a sixty-minute operation.
                if (ReconcileSchedule.HasTimedOut(run.StepStartedAt, clock.UtcNow)) {
                    step.Status = DeploymentStepStatus.Failed;
                    step.Detail = "never accepted by the write path within the sixty-minute ceiling";

                    return new(
                        ReconcileOutcome.Failed(
                            new Error(
                                ErrorCode.OperationTimeout,
                                $"The deployment stopped at {label}: the write path did not accept it within "
                                + "sixty minutes of the step beginning.",
                                step.ResourcePath
                            )
                        ),
                        progress.ToImmutable(),
                        true
                    );
                }

                var written = await manager.WriteChildAsync(
                    spec.OperationId,
                    new() {
                        Path = step.ResourcePath,
                        ApiVersion = step.ApiVersion,
                        Verb = WriteVerb.Put,
                        Body = step.Body,
                        Caller = spec.Caller with {
                            CorrelationId = $"{spec.Caller.CorrelationId}/deployment/{spec.OperationId:N}/{run.Cursor + 1}"
                        }
                    },
                    cancellationToken
                );

                if (written.TryGetError(out var refusal)) {
                    // ⚠ Another operation is driving this resource — somebody else's update, or this
                    // deployment's own child from a pass whose record a silo loss took. Either way the
                    // PUT is retried once that operation ends rather than failing the deployment: a
                    // resource busy for a minute is not a template that cannot deploy. A lost child of
                    // our own re-PUT with its own body is then a no-op, which is recorded as one.
                    if (refusal.Code == ErrorCode.OperationInProgress) {
                        progress.Add(Progress("waiting", $"{label} is being changed by another operation: {refusal.Message}", Percent(run, 0)));
                        return new(ReconcileOutcome.InProgress($"{label} is busy.", PollInterval), progress.ToImmutable(), true);
                    }

                    step.Status = DeploymentStepStatus.Failed;
                    step.Detail = $"refused by the write path — {refusal.Code}: {refusal.Message}";

                    progress.Add(Progress("failed", $"{label} was refused: {refusal.Code}: {refusal.Message}", Percent(run, 0)));

                    return new(
                        ReconcileOutcome.Failed(
                            new Error(
                                ErrorCode.ProvisioningFailed,
                                $"The deployment stopped at {label}: the write path refused it — {refusal.Code}: "
                                + $"{refusal.Message} Nothing after it was written.",
                                step.ResourcePath
                            )
                        ),
                        progress.ToImmutable(),
                        true
                    );
                }

                var accepted = written.GetValueOrThrow();

                if (accepted.NoOp || accepted.OperationId == Guid.Empty) {
                    step.Status = DeploymentStepStatus.Succeeded;
                    step.Detail = "no change";
                    progress.Add(Progress("deployed", $"{label} already matches the template; nothing was written.", Percent(run, 100)));
                    Advance(run);
                    continue;
                }

                step.ChildOperationId = accepted.OperationId;
                step.Created = accepted.Resource.ProvisioningState == ProvisioningState.Creating;
                step.Status = DeploymentStepStatus.Running;

                progress.Add(
                    Progress(
                        "deploying",
                        $"{label} accepted as operation {accepted.OperationId:D} ({(step.Created ? "create" : "update")}).",
                        Percent(run, 0)
                    )
                );
            }

            if (step.Status == DeploymentStepStatus.Running) {
                var read = await Operation(spec, step.ChildOperationId).GetAsync();

                if (read.TryGetError(out var readError)) {
                    step.Status = DeploymentStepStatus.Failed;
                    step.Detail = $"its operation {step.ChildOperationId:D} cannot be read: {readError.Message}";

                    return new(
                        ReconcileOutcome.Failed(
                            new Error(
                                ErrorCode.ProvisioningFailed,
                                $"The deployment stopped at {label}: its operation {step.ChildOperationId:D} cannot "
                                + $"be read — {readError.Message}",
                                step.ResourcePath
                            )
                        ),
                        progress.ToImmutable(),
                        true
                    );
                }

                var child = read.GetValueOrThrow();

                switch (child.State) {
                    case OperationState.Succeeded:
                        step.Status = DeploymentStepStatus.Succeeded;
                        step.Detail = step.Created ? "created" : "updated";
                        progress.Add(Progress("deployed", $"{label} succeeded (operation {child.OperationId:D}).", Percent(run, 100)));
                        Advance(run);
                        continue;

                    case OperationState.Failed:
                        step.Status = DeploymentStepStatus.Failed;
                        step.Detail = child.Error?.Message ?? "failed without a reason";

                        return ChildEnded(
                            run,
                            progress,
                            $"The deployment stopped at {label}: its operation {child.OperationId:D} failed — "
                            + $"{child.Error?.Code.ToString() ?? "ProvisioningFailed"}: {step.Detail}",
                            step
                        );

                    case OperationState.Canceled:
                        step.Status = DeploymentStepStatus.Canceled;
                        step.Detail = child.CancelReason.Length > 0 ? child.CancelReason : "cancelled";

                        return ChildEnded(
                            run,
                            progress,
                            $"The deployment stopped at {label}: its operation {child.OperationId:D} was cancelled "
                            + $"— {step.Detail}",
                            step
                        );

                    default:
                        progress.Add(
                            Progress(
                                "waiting",
                                $"{label}: operation {child.OperationId:D} is {child.State}"
                                + (child.LastProgress is { } last ? $" — {last}" : "."),
                                Percent(run, child.PercentComplete)
                            )
                        );

                        return new(
                            ReconcileOutcome.InProgress(
                                $"Waiting for {label} (operation {child.OperationId:D}).",
                                PollInterval
                            ),
                            progress.ToImmutable(),
                            true
                        );
                }
            }

            // A step already Succeeded on an earlier pass whose cursor move was not persisted.
            if (step.Status == DeploymentStepStatus.Succeeded) {
                Advance(run);
                continue;
            }

            // Failed or Canceled with the cursor still on it: the ending was not persisted. Report it
            // again rather than writing the resource a second time.
            return ChildEnded(run, progress, $"The deployment stopped at {label}: {step.Detail}", step);
        }

        progress.Add(
            Progress(
                "deployed",
                $"All {run.Steps.Count.ToString(CultureInfo.InvariantCulture)} resource(s) are deployed.",
                100
            )
        );

        return new(ReconcileOutcome.Converged, progress.ToImmutable(), true);
    }

    /// <summary>
    ///     The run's record, as the read-only properties of the deployment's body — written by the parent
    ///     operation when it ends.
    /// </summary>
    /// <param name="run">The run, or <see langword="null" /> for one that never planned.</param>
    /// <param name="ending">How the parent operation ended.</param>
    /// <param name="error">Its failure, or <see langword="null" />.</param>
    /// <returns>Each read-only pointer against its value as JSON text.</returns>
    /// <remarks>
    ///     ⚠ <b>The rollback line says it was not performed, every time there was something to roll
    ///     back.</b> A reader of a failed deployment's body needs two facts — what it left behind and
    ///     that nobody removed it — and a field that was merely empty on failure would let them assume
    ///     the second.
    /// </remarks>
    public static ImmutableDictionary<string, string> History(
        DeploymentRunState? run,
        OperationState ending,
        Error? error
    ) {
        var steps = run?.Steps ?? [];

        var output = new JsonArray();

        foreach (var step in steps.Where(static x => x.Status == DeploymentStepStatus.Succeeded)) {
            output.Add(step.ResourcePath);
        }

        var lines = new JsonArray();

        foreach (var step in steps) {
            var state = step.Status == DeploymentStepStatus.Pending ? "NotStarted" : step.Status.ToString();
            var line = $"{state} {step.ResourcePath}";

            if (step.ChildOperationId != Guid.Empty) {
                line += $" — operation {step.ChildOperationId:D}";
            }

            if (step.Detail.Length > 0) {
                line += $" — {step.Detail}";
            }

            lines.Add(line);
        }

        var created = steps
            .Where(static x => x.Created && x.Status is DeploymentStepStatus.Succeeded)
            .Select(static x => x.ResourcePath)
            .ToList();

        // The resource the run stopped at, when its create had been accepted: its grain exists, in
        // Failed or Canceled, and it is as much "left behind" as the ones before it.
        var stopped = steps.FirstOrDefault(static x => x.Created
            && x.Status is DeploymentStepStatus.Failed or DeploymentStepStatus.Canceled
        );

        var verb = ending.ToString().ToLowerInvariant();
        var rollback = new StringBuilder();

        if (ending != OperationState.Succeeded) {
            if (created.Count == 0 && stopped is null) {
                rollback.Append(CultureInfo.InvariantCulture, $"Not performed, and not needed: the deployment {verb} before it created anything.");
            } else {
                rollback.Append(CultureInfo.InvariantCulture, $"Not performed. The deployment {verb}");

                if (created.Count > 0) {
                    rollback.Append(
                        CultureInfo.InvariantCulture,
                        $" after creating {created.Count} resource(s), which were left in place: {string.Join(", ", created)}"
                    );
                }

                if (stopped is not null) {
                    rollback.Append(created.Count > 0 ? "; and " : " at ")
                        .Append(stopped.ResourcePath)
                        .Append(", whose create ")
                        .Append(stopped.Status == DeploymentStepStatus.Failed ? "failed and which is left in Failed" : "was cancelled and which is left in Canceled");
                }

                rollback.Append(
                    ". Delete them to undo the deployment, or fix the failure and PUT it again — a rerun leaves "
                    + "every resource that already matches the template unchanged."
                );
            }
        }

        return ImmutableDictionary<string, string>.Empty
            .Add(Deployments.OutputResourcesPointer, output.ToJsonString())
            .Add(Deployments.StepsPointer, lines.ToJsonString())
            .Add(Deployments.ErrorPointer, JsonValue.Create(error?.Message ?? "").ToJsonString())
            .Add(Deployments.RollbackPointer, JsonValue.Create(rollback.ToString()).ToJsonString());
    }

    /// <summary>
    ///     A cancelling pass: stop the child in flight, start nothing, and converge once nothing is
    ///     running — which is when the parent may report <see cref="OperationState.Canceled" />.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Cancellation reaches the child and stops there.</b> The child's own cancellation
    ///     completes rather than abandons — it tears down what it applied — which is docs/plan/08's rule
    ///     for one resource. Resources earlier steps already finished are not torn down: that would be a
    ///     rollback, and a rollback is recorded rather than performed (see <see cref="History" />).
    /// </remarks>
    async Task<ReconcilePass> CancelAsync(
        OperationSpec spec,
        DeploymentRunState run,
        string reason,
        ImmutableArray<OperationProgress>.Builder progress
    ) {
        if (run.Cursor < run.Steps.Count && run.Steps[run.Cursor] is { Status: DeploymentStepStatus.Running } step) {
            var child = Operation(spec, step.ChildOperationId);

            // Conflict means the child already ended, which the read below will say.
            _ = await child.CancelAsync($"Its deployment's operation {spec.OperationId:D} was cancelled: {reason}");

            var read = await child.GetAsync();

            if (read.IsSuccess && !read.GetValueOrThrow().IsTerminal) {
                progress.Add(Progress("cancelling", $"Waiting for {Label(run)} to finish cancelling.", Percent(run, 0)));
                return new(ReconcileOutcome.InProgress($"Waiting for {Label(run)} to cancel.", PollInterval), progress.ToImmutable(), true);
            }

            if (read.IsSuccess) {
                var ended = read.GetValueOrThrow();
                step.Status = ended.State switch {
                    OperationState.Succeeded => DeploymentStepStatus.Succeeded,
                    OperationState.Failed => DeploymentStepStatus.Failed,
                    _ => DeploymentStepStatus.Canceled
                };
                step.Detail = ended.State == OperationState.Succeeded
                    ? step.Created ? "created before the cancellation reached it" : "updated before the cancellation reached it"
                    : ended.Error?.Message ?? ended.CancelReason;
            } else {
                step.Status = DeploymentStepStatus.Canceled;
                step.Detail = "cancelled";
            }
        }

        var notStarted = run.Steps.Count(static x => x.Status == DeploymentStepStatus.Pending);

        progress.Add(
            Progress(
                "cancelled",
                $"{notStarted.ToString(CultureInfo.InvariantCulture)} resource(s) were not started. Nothing an "
                + "earlier step deployed is removed — rollback is recorded, not performed.",
                Percent(run, 0)
            )
        );

        return new(ReconcileOutcome.Converged, progress.ToImmutable(), true);
    }

    ReconcilePass ChildEnded(
        DeploymentRunState run,
        ImmutableArray<OperationProgress>.Builder progress,
        string message,
        DeploymentStepState step
    ) {
        progress.Add(Progress("failed", message, Percent(run, 0)));

        return new(
            ReconcileOutcome.Failed(new Error(ErrorCode.ProvisioningFailed, message, step.ResourcePath)),
            progress.ToImmutable(),
            true
        );
    }

    /// <summary>The plan, from the deployment's desired body as the resource grain stored it.</summary>
    static Result<DeploymentPlan> Plan(OperationSpec spec) {
        var deployment = ResourceId.ParsePath(spec.ResourcePath);

        if (deployment.TryGetError(out var pathError)) {
            return Result<DeploymentPlan>.Failure(pathError);
        }

        try {
            using var body = JsonDocument.Parse(spec.Desired);
            return DeploymentTemplate.EvaluateBody(deployment.GetValueOrThrow(), body.RootElement);
        } catch (JsonException exception) {
            return Result<DeploymentPlan>.Failure(
                ErrorCode.InvalidRequestBody,
                $"The deployment's stored body is not JSON: {exception.Message}"
            );
        }
    }

    void Advance(DeploymentRunState run) {
        run.Cursor++;
        run.StepStartedAt = clock.UtcNow;
    }

    static string Label(DeploymentRunState run) =>
        $"step {(run.Cursor + 1).ToString(CultureInfo.InvariantCulture)} of "
        + $"{run.Steps.Count.ToString(CultureInfo.InvariantCulture)}, '{run.Steps[Math.Min(run.Cursor, run.Steps.Count - 1)].ResourcePath}'";

    /// <summary>
    ///     The roll-up: finished steps count whole, the step in flight counts for its child's own
    ///     percentage.
    /// </summary>
    static int Percent(DeploymentRunState run, int current) =>
        run.Steps.Count == 0
            ? 0
            : Math.Clamp((run.Cursor * 100 + Math.Clamp(current, 0, 100)) / run.Steps.Count, 0, 100);

    OperationProgress Progress(string step, string detail, int percent) =>
        new() { At = clock.UtcNow, Step = step, Detail = detail, PercentComplete = percent };

    IOperationGrain Operation(OperationSpec spec, Guid operationId) =>
        grains.ForTenant(spec.TenantId.ToString("D", CultureInfo.InvariantCulture))
            .GetGrain<IOperationGrain>(GrainKeys.Operation(operationId));
}
