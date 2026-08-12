using CyberCloud.Core.Time;

namespace CyberCloud.Providers.Storage;

/// <summary>
///     Converges one bucket onto the single <c>Bucket</c> custom resource it is.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>IT NEVER LOOKS ITS ACCOUNT UP, AND THAT IS THE MOST IMPORTANT LINE IN THIS FILE.</b>
///         docs/plan/12 § Child resources makes the parent a pure function of the address, and
///         docs/plan/08 § Deleting a parent resource that has children says the platform
///         <i>"must not re-check the parent on every write to a child"</i> — the check belongs on the
///         create, in <c>ResourceManagerService.ResolveAsync</c>, where it runs before the enforcement
///         seam and answers the same 404 as an unauthorized read.
///         <para>
///             ⚠ <b>And on THIS type there is a second reason, sharper than the first: the account
///             never reports <c>Succeeded</c>.</b> Its S3 gateway mounts a <c>Secret</c> nothing writes
///             — <see cref="StorageAccounts.ConfigSecretName" /> — so it stays <c>InProgress</c>
///             indefinitely and deliberately. A bucket reconciler that read its parent's <c>Seaweed</c>
///             back and waited for it would therefore never converge for anybody, and the symptom
///             would read as a bug in the bucket. The only thing this reconciler takes from the parent
///             is its <i>name</i>, off the address, in <see cref="StorageBuckets.ClusterRefOf" />.
///         </para>
///     </para>
///     <para>
///         The four clauses of docs/plan/08 § The reconcile loop:
///     </para>
///     <list type="number">
///         <item>
///             <b>Idempotent.</b> <see cref="StorageBuckets.BucketJson" /> is a pure function of the
///             address and the body. Nothing counts, appends or timestamps.
///         </item>
///         <item>
///             <b>No hidden state.</b> The only field is the primary constructor's
///             <see cref="IClock" />. ⚠ A reconciler is a <b>singleton by concrete type</b>, so one
///             instance serves every tenant in the process, and a <c>readonly</c> field holding a
///             mutable dictionary passes the structural check forever —
///             <c>StorageBucketReconcilerTests</c> asserts both halves, because only the cross-tenant
///             one catches that shape.
///         </item>
///         <item>
///             <b>Bounded.</b> One apply and one read, on the caller's token.
///         </item>
///         <item>
///             <b>Observes, never assumes.</b> <see cref="ReconcileOutcome.Converged" /> follows a
///             <c>GetAsync</c> of the object, never the apply's own result.
///         </item>
///     </list>
///     <para>
///         ⚠ <b>Its own <c>Type</c> is a separate class from <see cref="StorageAccountReconciler" />
///         even though the work rhymes, and that is forced rather than chosen.</b>
///         <c>ProviderRegistry</c> stores each type's reconciler by CONCRETE TYPE and
///         <c>ReconcileDriver</c> resolves it from the container by that type, so one class cannot
///         serve two registrations — its <see cref="Type" /> can only name one of them, and
///         <c>ProviderRegistry.Build</c> refuses exactly that. Unlike the reference provider, there is
///         no shared base here: an account renders a whole cluster and a bucket renders a name inside
///         one, and the only lines they would share are the ones every reconciler shares.
///     </para>
/// </remarks>
/// <param name="clock">Stamps <see cref="ObservedState.ObservedAt" />.</param>
public sealed class StorageBucketReconciler(IClock clock) : IResourceReconciler {
    /// <inheritdoc />
    public ResourceTypeName Type => StorageBuckets.Type;

    /// <inheritdoc />
    public async Task<ReconcileOutcome> ReconcileAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            return ReconcileOutcome.Failed(
                ErrorCode.InternalError,
                $"'{context.Id.Path}' has no cluster connection, and a bucket is a Bucket custom "
                + "resource in a cluster. CyberCloud.Storage/accounts/buckets declares "
                + "RequiresCluster, so the driver should have refused this pass — see ReconcileDriver."
            );
        }

        var name = StorageBuckets.ObjectNameOf(context.Id);

        context.Log.Report(
            "applying",
            $"applying the Bucket of '{context.Id.Name}' in account "
            + $"'{StorageBuckets.ClusterRefOf(context.Id)}'",
            20
        );

        var applied = await KubeCommand.For(cluster)
            .WithTenantId(context.Id.TenantId)
            .WithResourceId(context.Id)
            .InNamespace(context.Namespace)
            .WithKind(StorageBuckets.BucketKind)
            .WithApiVersion(context.ApiVersion)
            .ObjectJson(StorageBuckets.BucketJson(context.Id, context.Desired))
            .ApplyAsync(cancellationToken);

        if (applied.TryGetError(out var applyError)) {
            return ReconcileOutcome.FromFailure(applyError);
        }

        var outcome = applied.GetValueOrThrow();

        switch (outcome.Result) {
            case ApplyResult.Suspended:
                context.Log.Report("waiting-for-cluster", outcome.Message);

                return ReconcileOutcome.InProgress(
                    outcome.Message.Length > 0 ? outcome.Message : "the cluster is unreachable",
                    TimeSpan.FromSeconds(30)
                );

            case ApplyResult.Conflict:
                // ⚠ `.spec.quota` is the plausible one here: it is the field a tenant's own
                // cost-control tooling would reach for, and forcing would undo them every pass.
                context.Log.Report("conflict", outcome.Drift?.Describe() ?? outcome.Message);

                return ReconcileOutcome.InProgress(
                    outcome.Drift?.Describe()
                    ?? "another field manager owns part of the Bucket and it was not overwritten",
                    TimeSpan.FromSeconds(30)
                );
        }

        // ── Clause 4. Everything above this line is a claim; this is the reading. ───────────────
        var target = StorageBuckets.BucketRef(context.Namespace, context.Id);
        var read = await cluster.GetAsync(target, cancellationToken);

        if (read.TryGetError(out var readError)) {
            return readError.Code == ErrorCode.ResourceNotFound
                ? ReconcileOutcome.InProgress(
                    $"'{target}' was applied and is not readable back yet",
                    TimeSpan.FromSeconds(5)
                )
                : ReconcileOutcome.FromFailure(readError);
        }

        if (!StorageBuckets.Matches(read.GetValueOrThrow().Json, context.Id, context.Desired)) {
            return ReconcileOutcome.InProgress(
                $"'{target}' is readable and does not yet carry the desired spec",
                TimeSpan.FromSeconds(5)
            );
        }

        context.Log.Report("ready", $"the Bucket of '{name}' reads back as desired", 100);

        return ReconcileOutcome.Converged;
    }

    /// <inheritdoc />
    public async Task<ReconcileOutcome> DeleteAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            return ReconcileOutcome.Converged;
        }

        var name = StorageBuckets.ObjectNameOf(context.Id);

        context.Log.Report("deleting", $"deleting the Bucket of '{name}'");

        var deleted = await KubeCommand.For(cluster)
            .WithTenantId(context.Id.TenantId)
            .WithResourceId(context.Id)
            .InNamespace(context.Namespace)
            .WithKind(StorageBuckets.BucketKind)
            .WithApiVersion(context.ApiVersion)
            .ObjectJson(StorageBuckets.BucketJson(context.Id, context.Desired))
            // ⚠ THE ACCOUNT'S Seaweed IS NOT TOUCHED, AND IT WOULD BE EASY TO REACH FOR. Deleting a
            // bucket removes one object; a delete that also tidied up the parent — or that waited for
            // it — would be this type reaching outside its own resource. The whole of what a bucket
            // owns is the Bucket named here.
            .DeleteAsync(CascadePolicy.Background, cancellationToken);

        if (deleted.TryGetError(out var deleteError) && deleteError.Code != ErrorCode.ResourceNotFound) {
            return ReconcileOutcome.FromFailure(deleteError);
        }

        var target = StorageBuckets.BucketRef(context.Namespace, context.Id);
        var read = await cluster.GetAsync(target, cancellationToken);

        if (read.IsSuccess) {
            return ReconcileOutcome.InProgress($"'{target}' is still readable", TimeSpan.FromSeconds(5));
        }

        if (read.Error!.Code != ErrorCode.ResourceNotFound) {
            return ReconcileOutcome.FromFailure(read.Error);
        }

        context.Log.Report("deleted", $"the Bucket of '{name}' is gone", 100);
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
            StorageBuckets.BucketRef(context.Namespace, context.Id),
            cancellationToken
        );

        if (read.TryGetError(out _)) {
            return new() { Exists = false, ObservedAt = clock.UtcNow, Summary = "the bucket is absent" };
        }

        var found = read.GetValueOrThrow();
        var matches = StorageBuckets.Matches(found.Json, context.Id, context.Desired);

        return new() {
            Exists = true,
            Json = found.Json,
            ObservedAt = clock.UtcNow,
            Revision = found.ResourceVersion,
            Summary = matches ? "the bucket carries the desired spec" : "the bucket has drifted"
        };
    }
}
