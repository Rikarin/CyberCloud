using CyberCloud.Core.Time;

namespace CyberCloud.Providers.Terminal;

/// <summary>
///     Converges the durable half of a cloud terminal: a home volume, an identity and a constraint.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THIS RECONCILER NEVER STARTS A SHELL, AND THAT IS THE DESIGN RATHER THAN A GAP.</b>
///         The pod is the session and the session is started by <c>CloudConsoleSessionHandler</c>;
///         see <see cref="CloudConsoles" />' remarks, § What is a resource and what is a session. A
///         reconciler that applied the pod would fight the idle reclaim on every reminder — reclaim
///         deletes it, the next pass re-creates it, and a console nobody has touched for a week costs
///         exactly what docs/plan/19 § The pod says the whole design exists to avoid.
///     </para>
///     <para>
///         <b>The four clauses of docs/plan/08 § The reconcile loop, and where each is satisfied:</b>
///     </para>
///     <list type="number">
///         <item>
///             <b>Idempotent.</b> Three server-side applies, so a second pass with the same body is
///             three <c>Unchanged</c>es. ⚠ Nothing here reads a clock into a rendered object — the
///             only use of <see cref="IClock" /> is stamping
///             <see cref="ObservedState.ObservedAt" /> — which matters more on this type than on most,
///             because the two numbers it renders (an idle timeout and a deadline) are DURATIONS and
///             the obvious mistake is to render them as instants.
///         </item>
///         <item>
///             <b>No hidden state.</b> The only field is the primary constructor's
///             <see cref="IClock" />. ⚠ A reconciler is registered <b>as a singleton, by concrete
///             type</b>, so one instance serves every tenant in the process, and a
///             <c>readonly</c> mutable field would pass <c>ReconcilerConformance.CheckNoHiddenState</c>
///             — which skips <c>readonly</c> — while handing tenant B tenant A's egress posture.
///             <c>ConsoleReconcilerTests</c> holds both checks and a demonstration of the blind spot.
///         </item>
///         <item>
///             <b>Bounded.</b> Three applies and three reads, on the caller's token. ⚠ There is no
///             wait for the home volume to bind, and the reason is a property of the substrate rather
///             than of the budget — see <see cref="CloudConsoles" />' remarks.
///         </item>
///         <item>
///             <b>Observes, never assumes.</b> <see cref="ReconcileOutcome.Converged" /> follows a
///             <c>GetAsync</c> of <b>all three</b> objects, never any apply's own result.
///         </item>
///     </list>
///     <para>
///         ⚠ <b>THE ORDER IS THE SECURITY ARGUMENT.</b> Volume, then identity, then constraint. The
///         constraint is last because a pass that dies half way should leave a console that cannot be
///         attached to rather than one that can be attached to and reaches everything — and
///         <c>CloudConsoleSessionHandler</c> refuses to start a pod until all three read back, so the
///         window in which an unconstrained shell is startable does not exist rather than being
///         short.
///     </para>
///     <para>
///         ⚠ <b>An <see cref="ApplyResult.Conflict" /> is reported and retried rather than failed and
///         never forced</b> — ADR-013 makes a conflict "a drift event with a name". On this type the
///         plausible rival is a mutating admission policy: a cluster running a Pod Security or
///         Kyverno policy that edits security contexts and network policies is exactly the kind of
///         cluster this row is deployed into, and forcing would take a field back from it once per
///         reminder, forever.
///     </para>
/// </remarks>
/// <param name="clock">Stamps <see cref="ObservedState.ObservedAt" />.</param>
public sealed class CloudConsoleReconciler(IClock clock) : IResourceReconciler {
    /// <inheritdoc />
    public ResourceTypeName Type => CloudConsoles.Type;

    /// <inheritdoc />
    public async Task<ReconcileOutcome> ReconcileAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            // Unreachable through the driver, which refuses a RequiresCluster type with no connection
            // and names the type. Kept because a reconciler is also callable directly.
            return ReconcileOutcome.Failed(
                ErrorCode.InternalError,
                $"'{context.Id.Path}' has no cluster connection, and a cloud terminal is a home "
                + "volume, a service account and a network policy in a cluster. "
                + "CyberCloud.Terminal/consoles declares RequiresCluster, so the driver should have "
                + "refused this pass — see ReconcileDriver."
            );
        }

        var name = context.Id.Name;

        context.Log.Report("applying-home", $"applying the home volume of '{name}'", 20);

        if (await Apply(
                context,
                cluster,
                CloudConsoles.ClaimKind,
                CloudConsoles.HomeClaimJson(name, context.Desired),
                "the home volume",
                cancellationToken
            ) is { } claimProblem) {
            return claimProblem;
        }

        context.Log.Report("applying-identity", $"applying the service account of '{name}'", 40);

        if (await Apply(
                context,
                cluster,
                CloudConsoles.ServiceAccountKind,
                CloudConsoles.ServiceAccountJson(name, context.Desired),
                "the service account",
                cancellationToken
            ) is { } accountProblem) {
            return accountProblem;
        }

        context.Log.Report("applying-network-policy", $"applying the network policy of '{name}'", 60);

        if (await Apply(
                context,
                cluster,
                CloudConsoles.NetworkPolicyKind,
                CloudConsoles.NetworkPolicyJson(
                    name,
                    context.Id.TenantId,
                    context.Namespace,
                    context.Desired
                ),
                "the network policy",
                cancellationToken
            ) is { } policyProblem) {
            return policyProblem;
        }

        // ── Clause 4. Everything above this line is a claim; this is the reading. ──────────────
        //
        // ⚠ ALL THREE, AND THE SERVICE ACCOUNT IS THE ONE MOST LIKELY TO BE SKIPPED. It carries no
        // tenant-facing setting at all, so a reconciler that read back only the two objects with
        // fields in them would report Converged for a console whose pod has no identity to run under
        // — which the API server accepts, and which presents as a shell that starts and can do
        // nothing, or worse, as a shell that fell back to the namespace's `default` account.
        foreach (var target in CloudConsoles.Objects(context.Namespace, name)) {
            var read = await cluster.GetAsync(target, cancellationToken);

            if (read.TryGetError(out var readError)) {
                return readError.Code == ErrorCode.ResourceNotFound
                    ? ReconcileOutcome.InProgress(
                        $"'{target}' was applied and is not readable back yet",
                        TimeSpan.FromSeconds(5)
                    )
                    : ReconcileOutcome.FromFailure(readError);
            }

            if (!CloudConsoles.Matches(read.GetValueOrThrow().Json, context.Desired)) {
                return ReconcileOutcome.InProgress(
                    $"'{target}' is readable and does not yet carry the desired spec",
                    TimeSpan.FromSeconds(5)
                );
            }
        }

        // ⚠ THE WORD "ATTACHABLE" RATHER THAN "READY", SAID OUT LOUD IN THE TENANT'S OWN PROGRESS
        // LOG. A console that has converged has no shell running and never did; a tenant reading
        // "ready" would reasonably expect one, and the difference is the whole of this row's design.
        context.Log.Report(
            "attachable",
            $"'{name}' has a home volume, an identity and a network policy; no shell is running "
            + "until somebody connects",
            100
        );

        return ReconcileOutcome.Converged;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>THE POD IS DELETED HERE EVEN THOUGH IT IS NEVER APPLIED HERE, AND THE ASYMMETRY IS
    ///     DELIBERATE.</b> A teardown's job is that nothing of the resource is left running. The
    ///     session handler may have started a shell seconds ago; leaving it would be a pod holding a
    ///     deleted console's identity, mounting a volume that is about to go, with nothing left in the
    ///     platform that knows it exists. So the delete path knows about an object the create path
    ///     does not, and the read-back below covers all four.
    /// </remarks>
    public async Task<ReconcileOutcome> DeleteAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            // ⚠ Converged, not Failed, and the asymmetry with ReconcileAsync is deliberate — a
            // teardown with no cluster to reach has nothing left to remove, and failing would park the
            // resource in Deleting: visible, billed and permanent, for a wiring reason.
            return ReconcileOutcome.Converged;
        }

        var name = context.Id.Name;

        context.Log.Report("deleting", $"stopping any shell of '{name}' and removing its objects");

        // ⚠ THE POD FIRST AND THE VOLUME LAST, WHICH IS THE REVERSE OF THE APPLY ORDER AND IS NOT
        // COSMETIC. Deleting a PersistentVolumeClaim that a running pod has mounted does not delete
        // it: the claim gets a deletionTimestamp and sits there behind kubernetes.io/pvc-protection
        // until the pod goes, so a teardown that started with the volume would return InProgress
        // forever while a shell nobody could see kept it alive.
        //
        // ⚠ AND THIS DELETE TAKES THE TENANT'S HOME DIRECTORY WITH IT, IRREVERSIBLY. That is the
        // consequence of this type declining soft delete and of a claim being unable to ask for its
        // bytes to outlive it — TerminalProvider's remarks and CloudConsoles.HomeClaimJson carry both
        // halves. charts/managed/cloud-shell/conformance.yaml § owed, `delete-takes-the-home-directory`.
        var targets = new[] {
            CloudConsoles.PodRef(context.Namespace, name),
            CloudConsoles.NetworkPolicyRef(context.Namespace, name),
            CloudConsoles.ServiceAccountRef(context.Namespace, name),
            CloudConsoles.HomeClaimRef(context.Namespace, name)
        };

        foreach (var target in targets) {
            var deleted = await KubeCommand.For(cluster)
                .WithTenantId(context.Id.TenantId)
                .WithResourceId(context.Id)
                .InNamespace(context.Namespace)
                .WithKind(target.Kind)
                .WithApiVersion(context.ApiVersion)
                .ObjectJson(Placeholder(target.Name))
                // ⚠ Foreground, and the reason is the pod rather than the other three. A background
                // cascade returns as soon as the object is marked, so the read-back below could report
                // "not found" for a shell whose container was still running — and a console that stops
                // being billed while somebody is still typing into it is the failure the read-back
                // exists to prevent. docs/plan/06 § Two-phase create: "never silently gone while its
                // pods still run and its meter still ticks."
                .DeleteAsync(CascadePolicy.Foreground, cancellationToken);

            if (deleted.TryGetError(out var deleteError) && deleteError.Code != ErrorCode.ResourceNotFound) {
                return ReconcileOutcome.FromFailure(deleteError);
            }
        }

        foreach (var target in targets) {
            var read = await cluster.GetAsync(target, cancellationToken);

            if (read.IsSuccess) {
                return ReconcileOutcome.InProgress($"'{target}' is still readable", TimeSpan.FromSeconds(5));
            }

            if (read.Error!.Code != ErrorCode.ResourceNotFound) {
                return ReconcileOutcome.FromFailure(read.Error);
            }
        }

        context.Log.Report("deleted", $"the objects of '{name}' are gone, home directory included", 100);

        return ReconcileOutcome.Converged;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>IT READS THE THREE DURABLE OBJECTS AND, SEPARATELY, THE POD — AND ONLY THE FIRST
    ///         THREE DECIDE <see cref="ObservedState.Exists" />.</b> This is the whole of what
    ///         "observing a session" can mean. The pod's absence is the ordinary state of a console
    ///         nobody is using, so an observer that folded it into existence would report every idle
    ///         console as gone, and the drift scanner — which reads this — would repair a resource
    ///         that is working exactly as designed.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The pod is reported in <see cref="ObservedState.Summary" /> and nowhere else,
    ///         which is a deliberate downgrade from a fact to a sentence.</b> There is no field on
    ///         <see cref="ObservedState" /> for "a session is attached", and adding one to a
    ///         platform-wide wire type for one provider would be this row spending the manager's
    ///         budget. The summary is what a person sees in the portal, and for a terminal "a shell
    ///         is running" is the thing a person most wants to know.
    ///         <c>conformance.yaml § owed</c>, <c>session-state-is-a-sentence</c>.
    ///     </para>
    /// </remarks>
    public async Task<ObservedState> ObserveAsync(
        ObserveContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            return ObservedState.Absent;
        }

        var claim = await cluster.GetAsync(
            CloudConsoles.HomeClaimRef(context.Namespace, context.Id.Name),
            cancellationToken
        );

        if (claim.TryGetError(out _)) {
            return new() {
                Exists = false, ObservedAt = clock.UtcNow, Summary = "the home volume is absent"
            };
        }

        var found = claim.GetValueOrThrow();

        var others = await Task.WhenAll(
            cluster.GetAsync(
                CloudConsoles.ServiceAccountRef(context.Namespace, context.Id.Name),
                cancellationToken
            ),
            cluster.GetAsync(
                CloudConsoles.NetworkPolicyRef(context.Namespace, context.Id.Name),
                cancellationToken
            )
        );

        var matches = CloudConsoles.Matches(found.Json, context.Desired)
            && others.All(x => x.IsSuccess && CloudConsoles.Matches(x.GetValueOrThrow().Json, context.Desired));

        // ⚠ A READ AND NEVER AN APPLY. docs/plan/08: ObserveAsync "must not apply anything — this runs
        // on the drift path too, where a write would turn a diff into a change." On this type that
        // rule has teeth: an observer that ensured the pod existed would start a shell, with an
        // identity, on the drift scanner's schedule, for a console nobody had opened.
        var pod = await cluster.GetAsync(
            CloudConsoles.PodRef(context.Namespace, context.Id.Name),
            cancellationToken
        );

        return new() {
            Exists = true,
            Json = found.Json,
            ObservedAt = clock.UtcNow,
            Revision = found.ResourceVersion,
            Summary = matches
                ? (pod.IsSuccess
                    ? "the console is attachable and a shell is running"
                    : "the console is attachable and no shell is running")
                : "the console has drifted"
        };
    }

    /// <summary>
    ///     Applies one object, returning the outcome that ends the pass or <see langword="null" /> to
    ///     carry on.
    /// </summary>
    /// <remarks>
    ///     ⚠ Shared by all three applies rather than written three times, because the branches below
    ///     are a policy — retryable, refused, owned by somebody else — and three copies of a policy is
    ///     two copies that get forgotten.
    /// </remarks>
    static async Task<ReconcileOutcome?> Apply(
        ReconcileContext context,
        IKubeClusterConnection cluster,
        GroupVersionKind kind,
        string objectJson,
        string what,
        CancellationToken cancellationToken
    ) {
        var applied = await KubeCommand.For(cluster)
            .WithTenantId(context.Id.TenantId)
            .WithResourceId(context.Id)
            .InNamespace(context.Namespace)
            .WithKind(kind)
            .WithApiVersion(context.ApiVersion)
            .ObjectJson(objectJson)
            .ApplyAsync(cancellationToken);

        if (applied.TryGetError(out var applyError)) {
            // ⚠ The code decides, not this call site. ReconcileOutcome.FromFailure reads the error's
            // own code: the four refusals end the operation on this pass and everything else comes
            // back. On this type PolicyViolation is the one to expect — a Pod Security admission
            // policy refusing the pod, or a cluster with no NetworkPolicy CRD served at all — and
            // retrying an admission refusal for the full hour would replace the policy's own message,
            // available on the first pass, with an OperationTimeout.
            return ReconcileOutcome.FromFailure(applyError);
        }

        var outcome = applied.GetValueOrThrow();

        switch (outcome.Result) {
            case ApplyResult.Suspended:
                // docs/plan/09 § Cluster connections: an unreachable cluster suspends reconciles
                // rather than failing them. A tenant whose cluster is down has a console that is still
                // coming, not one that broke.
                context.Log.Report("waiting-for-cluster", outcome.Message);

                return ReconcileOutcome.InProgress(
                    outcome.Message.Length > 0 ? outcome.Message : "the cluster is unreachable",
                    TimeSpan.FromSeconds(30)
                );

            case ApplyResult.Conflict:
                context.Log.Report("conflict", outcome.Drift?.Describe() ?? outcome.Message);

                return ReconcileOutcome.InProgress(
                    outcome.Drift?.Describe()
                    ?? $"another field manager owns part of {what} and it was not overwritten",
                    TimeSpan.FromSeconds(30)
                );

            default:
                return null;
        }
    }

    /// <summary>The smallest object a delete needs: a name.</summary>
    static string Placeholder(string name) =>
        new System.Text.Json.Nodes.JsonObject {
            ["metadata"] = new System.Text.Json.Nodes.JsonObject { ["name"] = name }
        }.ToJsonString();
}
