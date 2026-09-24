using CyberCloud.Core.Time;

namespace CyberCloud.Providers.Compute;

/// <summary>
///     Converges one scale set onto its KubeVirt <c>VirtualMachinePool</c> and, when the body names
///     cloud-init, the one Secret every machine in it mounts.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>IT READS THE POOL BEFORE IT RENDERS, FOR THE MACHINE RECONCILER'S REASON.</b> The replica
///         count is on the object, written by the <c>scale</c> action and never by a body —
///         <see cref="VirtualMachineScaleSets" />' class remarks say why — so every pass begins with a
///         <c>GetAsync</c> of the pool: found, the render carries its replica count clamped to the body's
///         capacity; absent, the render says the capacity. And the apply is conditional on the version
///         the count was read at (<see cref="IKubeCommandBuilder.IfResourceVersion" />), so a scale that
///         lands between the read and the apply is not undone: the pass loses as
///         <see cref="ApplyResult.Stale" /> and reads again. That is <c>power-state-can-lose-a-race</c>'s
///         fix, taken by this type from its first line rather than added after a review.
///     </para>
///     <para>
///         ⚠ <b>The machine half is the machine reconciler's, by construction.</b> The image gate is
///         <see cref="ImageGate" />, the cloud-init handle is parsed by
///         <see cref="VirtualMachines.ParseCloudInitRef" /> under the same tenant prefix and resolved into
///         the same document under the set's own name (<see cref="VirtualMachineScaleSets.CloudInitSecretName" />
///         says why it is not the machine's), and the pool's machine template is
///         <see cref="VirtualMachines.VirtualMachineJson" />'s spec. What is this type's own: the name
///         bound (<see cref="VirtualMachineScaleSets.NameProblem" />), the replica clamp, and a
///         <c>Converged</c> that follows the pool's <c>readyReplicas</c>.
///     </para>
///     <para>
///         The four clauses of docs/plan/08 § The reconcile loop:
///     </para>
///     <list type="number">
///         <item>
///             <b>Idempotent.</b> Both documents are pure functions of the address, the body, the vault
///             value and the replica count read at the top of the pass.
///         </item>
///         <item>
///             <b>No hidden state.</b> The only field is the primary constructor's <see cref="IClock" />.
///         </item>
///         <item>
///             <b>Bounded.</b> At most one vault read, two cluster reads before the applies, two applies
///             and two reads after.
///         </item>
///         <item>
///             <b>Observes, never assumes.</b> <see cref="ReconcileOutcome.Converged" /> follows a
///             <c>GetAsync</c> of the pool and a reading of its status.
///         </item>
///     </list>
/// </remarks>
/// <param name="clock">Stamps <see cref="ObservedState.ObservedAt" />.</param>
public sealed class VirtualMachineScaleSetReconciler(IClock clock) : IResourceReconciler {
    /// <inheritdoc />
    public ResourceTypeName Type => VirtualMachineScaleSets.Type;

    /// <inheritdoc />
    public async Task<ReconcileOutcome> ReconcileAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            return ReconcileOutcome.Failed(
                ErrorCode.InternalError,
                $"'{context.Id.Path}' has no cluster connection, and a scale set is a KubeVirt pool in a "
                + "cluster. CyberCloud.Compute/virtualMachineScaleSets declares RequiresCluster, so the "
                + "driver should have refused this pass — see ReconcileDriver."
            );
        }

        var name = context.Id.Name;
        var ns = context.Namespace;

        // ── A name no instance can carry is refused before anything is read ────────────────────
        if (VirtualMachineScaleSets.NameProblem(name) is { Length: > 0 } nameProblem) {
            return ReconcileOutcome.Failed(new Error(ErrorCode.InvalidRequestBody, nameProblem));
        }

        if (await ImageGate.WaitForAsync(context, cluster, VirtualMachines.Image(context.Desired), "set", cancellationToken)
            is { } waiting) {
            return waiting;
        }

        // ── The replica count, read off the pool before anything is rendered ───────────────────
        var existing = await cluster.GetAsync(VirtualMachineScaleSets.PoolRef(ns, name), cancellationToken);

        if (existing.TryGetError(out var existingError) && existingError.Code != ErrorCode.ResourceNotFound) {
            return ReconcileOutcome.FromFailure(existingError);
        }

        var replicas = VirtualMachineScaleSets.ReplicasToRender(
            existing.IsSuccess ? VirtualMachineScaleSets.ReplicasOf(existing.GetValueOrThrow().Json) : null,
            context.Desired
        );

        var readVersion = existing.IsSuccess ? existing.GetValueOrThrow().ResourceVersion : string.Empty;

        // ── Cloud-init: the machine's rule, under the set's own pointer ────────────────────────
        string? userData = null;

        if (VirtualMachines.HasCloudInit(context.Desired)) {
            var handle = VirtualMachines.ParseCloudInitRef(
                VirtualMachines.CloudInitRef(context.Desired),
                context.Id.TenantId
            );

            if (handle.TryGetError(out var handleError)) {
                return ReconcileOutcome.FromFailure(handleError);
            }

            var resolved = await context.Secrets.ResolveAsync(handle.GetValueOrThrow(), cancellationToken);

            if (resolved.TryGetError(out var resolveError)) {
                return ReconcileOutcome.FromFailure(resolveError);
            }

            userData = resolved.GetValueOrThrow();

            context.Log.Report("applying-cloud-init", $"writing the cloud-init user data of '{name}' into its Secret", 30);

            if (await VirtualMachineReconciler.ApplyAsync(
                    context,
                    cluster,
                    KubeSecret.Kind,
                    VirtualMachineScaleSets.CloudInitSecretJson(name, userData),
                    [],
                    string.Empty,
                    cancellationToken
                ) is { } secretProblem) {
                return secretProblem;
            }
        }

        context.Log.Report("applying-pool", $"applying the VirtualMachinePool of '{name}' at {replicas} replicas", 50);

        if (await VirtualMachineReconciler.ApplyAsync(
                context,
                cluster,
                VirtualMachineScaleSets.PoolKind,
                VirtualMachineScaleSets.PoolJson(ns, name, context.Desired, replicas),
                [.. VirtualMachineScaleSets.TemplatePaths],
                readVersion,
                cancellationToken
            ) is { } problem) {
            return problem;
        }

        // ── Clause 4. Everything above this line is a claim; this is the reading. ──────────────
        if (userData is not null) {
            var secret = await cluster.GetAsync(VirtualMachineScaleSets.CloudInitSecretRef(ns, name), cancellationToken);

            if (secret.TryGetError(out var secretReadError)) {
                return secretReadError.Code == ErrorCode.ResourceNotFound
                    ? ReconcileOutcome.InProgress("the cloud-init Secret was applied and is not readable back yet", TimeSpan.FromSeconds(5))
                    : ReconcileOutcome.FromFailure(secretReadError);
            }

            var carried = KubeSecret.Value(secret.GetValueOrThrow(), VirtualMachines.CloudInitKey);

            if (!carried.IsSuccess || carried.GetValueOrThrow() != userData) {
                return ReconcileOutcome.InProgress(
                    "the cloud-init Secret is readable and does not yet carry the vault's value",
                    TimeSpan.FromSeconds(5)
                );
            }
        }

        var read = await cluster.GetAsync(VirtualMachineScaleSets.PoolRef(ns, name), cancellationToken);

        if (read.TryGetError(out var readError)) {
            return readError.Code == ErrorCode.ResourceNotFound
                ? ReconcileOutcome.InProgress("the VirtualMachinePool was applied and is not readable back yet", TimeSpan.FromSeconds(5))
                : ReconcileOutcome.FromFailure(readError);
        }

        var json = read.GetValueOrThrow().Json;

        if (!VirtualMachineScaleSets.Matches(json, ns, context.Desired, replicas)) {
            return ReconcileOutcome.InProgress(
                "the VirtualMachinePool is readable and does not yet carry the desired spec",
                TimeSpan.FromSeconds(5)
            );
        }

        var readiness = VirtualMachineScaleSets.ReadinessOf(json);

        if (readiness.Kind == VirtualMachines.ReadinessKind.NotReady) {
            context.Log.Report("waiting-for-kubevirt", readiness.Detail, 80);

            return ReconcileOutcome.InProgress(readiness.Detail, TimeSpan.FromSeconds(15));
        }

        context.Log.Report(
            "ready",
            readiness.Kind == VirtualMachines.ReadinessKind.Ready
                ? readiness.Detail
                // ⚠ Said in the tenant's own progress log — conformance.yaml § owed, `converged-is-not-ready`.
                : $"the VirtualMachinePool of '{name}' reads back as desired; KubeVirt has not reported on it yet",
            100
        );

        return ReconcileOutcome.Converged;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Foreground, and the reason is the machines.</b> A background delete returns once the
    ///     pool is marked and the read-back below would report it gone while its machines — owned by it,
    ///     garbage-collected after it — were still running and still billing. Foreground keeps the pool
    ///     readable until every machine it owns is gone, so <c>Converged</c> here means the machines too,
    ///     which is docs/plan/06 § Two-phase create's "never silently gone while its pods still run".
    /// </remarks>
    public async Task<ReconcileOutcome> DeleteAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            return ReconcileOutcome.Converged;
        }

        var name = context.Id.Name;
        var ns = context.Namespace;

        context.Log.Report("deleting", $"deleting the VirtualMachinePool of '{name}' and its machines");

        foreach (var (kind, json, policy) in new[] {
                     (VirtualMachineScaleSets.PoolKind,
                         VirtualMachineScaleSets.PoolJson(ns, name, context.Desired, 0), CascadePolicy.Foreground),
                     (KubeSecret.Kind, VirtualMachineScaleSets.CloudInitSecretJson(name, string.Empty), CascadePolicy.Background)
                 }) {
            var deleted = await KubeCommand.For(cluster)
                .WithTenantId(context.Id.TenantId)
                .WithResourceId(context.Id)
                .InNamespace(ns)
                .WithKind(kind)
                .WithApiVersion(context.ApiVersion)
                .ObjectJson(json)
                .DeleteAsync(policy, cancellationToken);

            if (deleted.TryGetError(out var deleteError) && deleteError.Code != ErrorCode.ResourceNotFound) {
                return ReconcileOutcome.FromFailure(deleteError);
            }
        }

        foreach (var target in new[] {
                     VirtualMachineScaleSets.PoolRef(ns, name), VirtualMachineScaleSets.CloudInitSecretRef(ns, name)
                 }) {
            var read = await cluster.GetAsync(target, cancellationToken);

            if (read.IsSuccess) {
                return ReconcileOutcome.InProgress($"'{target}' is still readable", TimeSpan.FromSeconds(5));
            }

            if (read.Error!.Code != ErrorCode.ResourceNotFound) {
                return ReconcileOutcome.FromFailure(read.Error);
            }
        }

        context.Log.Report("deleted", $"the VirtualMachinePool of '{name}' and its machines are gone", 100);
        return ReconcileOutcome.Converged;
    }

    /// <inheritdoc />
    public async Task<ObservedState> ObserveAsync(
        ObserveContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            return ObservedState.Absent;
        }

        var read = await cluster.GetAsync(
            VirtualMachineScaleSets.PoolRef(context.Namespace, context.Id.Name),
            cancellationToken
        );

        if (read.TryGetError(out _)) {
            return new() { Exists = false, ObservedAt = clock.UtcNow, Summary = "the scale set is absent" };
        }

        var found = read.GetValueOrThrow();
        var replicas = VirtualMachineScaleSets.ReplicasOf(found.Json) ?? 0;
        var matches = VirtualMachineScaleSets.Matches(found.Json, context.Namespace, context.Desired, replicas);

        return new() {
            Exists = true,
            Json = found.Json,
            ObservedAt = clock.UtcNow,
            Revision = found.ResourceVersion,
            Summary = (matches ? "the scale set carries the desired spec" : "the scale set has drifted")
                + "; " + VirtualMachineScaleSets.ReadinessOf(found.Json).Detail
        };
    }
}
