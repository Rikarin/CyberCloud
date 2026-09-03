namespace CyberCloud.Kubernetes.Contracts;

/// <summary>
///     What the command builder writes to: a cluster the platform can reach.
/// </summary>
/// <remarks>
///     <para>
///         This is the <c>connection</c> in <c>KubeCommand.For(connection)</c> (docs/plan/09 § The
///         command builder). It is deliberately smaller than <c>IClusterConnectionGrain</c> and
///         deliberately free of <c>k8s.Models</c>: a reconciler holds one of these, and a reconciler
///         lives in a <c>Providers.*</c> assembly, which docs/plan/03 § Assembly graph rules rule 3
///         forbids from seeing Kubernetes types.
///     </para>
///     <para>
///         The production implementation is a thin handle over the grain
///         (<c>ClusterConnectionHandle</c> in <c>CyberCloud.Kubernetes</c>). Keeping the two apart
///         means a reconciler test can substitute a connection without an Orleans cluster, while the
///         real path still goes through the one activation per cluster that docs/plan/06 § Grain keys
///         requires.
///     </para>
/// </remarks>
public interface IKubeClusterConnection {
    /// <summary>The cluster's resource GUID.</summary>
    Guid ClusterId { get; }

    /// <summary>
    ///     Applies a command server-side.
    /// </summary>
    /// <param name="command">The built, fully-labelled command.</param>
    /// <param name="cancellationToken">The reconcile's token.</param>
    /// <returns>
    ///     ⚠ A <b>successful</b> <see cref="Result{T}" /> carrying
    ///     <see cref="ApplyResult.Suspended" /> when the cluster is
    ///     <see cref="ClusterHealthState.Degraded" />, and a successful one carrying
    ///     <see cref="ApplyResult.Conflict" /> when another field manager owns a field. Neither is a
    ///     failure: docs/plan/09 § Cluster connections requires an unreachable cluster to suspend
    ///     reconciles rather than fail them, and ADR-013 requires a conflict to become a drift event
    ///     rather than an error. A failed <see cref="Result" /> means <i>we</i> got it wrong.
    /// </returns>
    Task<Result<ApplyOutcome>> ApplyAsync(KubeCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Reads one object back, as JSON.
    /// </summary>
    /// <param name="target">Which object. A reconciler builds this from its own resource id.</param>
    /// <param name="cancellationToken">The reconcile's token.</param>
    /// <returns>
    ///     The object, or <see cref="ErrorCode.ResourceNotFound" /> when the API server has no such
    ///     object. ⚠ Absence is a <b>failure</b> carrying that code rather than a successful
    ///     <see langword="null" />, so that "the object is gone" and "the read did not happen" stay
    ///     distinguishable — the first converges a delete, the second must not.
    /// </returns>
    /// <remarks>
    ///     ⚠ <b>This member is what makes clause 4 of the reconciler contract satisfiable, and it was
    ///     added by the first provider that tried.</b> docs/plan/08 § The reconcile loop: <i>"Observes,
    ///     never assumes. <c>Converged</c> means it read back the desired shape, not that the apply
    ///     returned 200."</i> Before this, the only members here were
    ///     <see cref="ApplyAsync" /> and <see cref="DeleteAsync" /> — both writes — so a reconciler
    ///     given nothing but this connection could only report <c>Converged</c> by remembering that an
    ///     apply had succeeded, which is the exact violation the clause names and which
    ///     <c>ReconcilerConformance</c> exists to reject. <c>IClusterConnectionGrain.GetAsync</c>
    ///     already existed; a reconciler is not allowed to reach the grain
    ///     (docs/plan/03 § Assembly graph rules, rule 3 is why the handle exists at all), so the read
    ///     had to appear here.
    /// </remarks>
    Task<Result<KubeObject>> GetAsync(ObjectRef target, CancellationToken cancellationToken = default);

    /// <summary>Deletes the object a command addresses.</summary>
    /// <param name="command">The built command; only its target and identity are used.</param>
    /// <param name="policy">How to cascade.</param>
    /// <param name="cancellationToken">The reconcile's token.</param>
    Task<Result> DeleteAsync(
        KubeCommand command,
        CascadePolicy policy = CascadePolicy.Background,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Everything in one namespace, of every kind the cluster serves, whether or not this
    ///     platform wrote it.
    /// </summary>
    /// <param name="ns">The namespace to enumerate.</param>
    /// <param name="cancellationToken">The caller's budget.</param>
    /// <returns>
    ///     Every object, or the failure that stopped the enumeration. ⚠ <b>Never a partial
    ///     listing.</b> An implementation that returned what it managed to read would report the
    ///     kinds it could not reach as absent, and absence here is what authorises deleting the
    ///     namespace.
    /// </returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>THIS IS THE FIRST READ MEMBER HERE THAT IS NOT KEYED BY AN OBJECT, AND IT WAS
    ///         ADDED BY THE ONE CALLER THAT CANNOT BE EXPRESSED WITHOUT IT.</b> Until it existed the
    ///         interface had <see cref="ApplyAsync" />, <see cref="GetAsync" /> and
    ///         <see cref="DeleteAsync" /> — two writes and a read of a name you already know — so a
    ///         component asking "what is in this namespace" had nowhere to ask. That is why
    ///         <c>RetainedVolume</c> is shaped "name, then verify" rather than "select, then delete",
    ///         and why <c>INamespaceInventory</c> had no implementation.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Not <c>IClusterObjectInventory</c>, and the difference is the whole safety of a
    ///         namespace delete.</b> That seam selects on
    ///         <c>cybercloud.io/managed-by=cybercloud</c> because a drift scan compares what the
    ///         platform wrote against what it meant to write. This one must find the objects that
    ///         selector excludes — a tenant's own <c>PersistentVolumeClaim</c>, a <c>Secret</c> an
    ///         operator added, a <c>StatefulSet</c> from a chart nobody registered — so it applies no
    ///         selector at all.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The default implementation FAILS, and every connection that cannot really
    ///         enumerate a namespace inherits it on purpose.</b> The alternative — an abstract member
    ///         — would have forced twenty reconciler test doubles to write a body, and the body
    ///         everybody writes is <c>return []</c>, which is the one answer that authorises a
    ///         recursive delete of a tenant's live data. Fail-closed by default is the only default
    ///         whose worst case is a refused reclaim rather than a destroyed namespace. The
    ///         production path overrides it; see <c>ClusterConnectionHandle</c>.
    ///     </para>
    /// </remarks>
    Task<Result<IReadOnlyList<KubeObjectSummary>>> ListNamespaceAsync(
        string ns,
        CancellationToken cancellationToken = default
    ) =>
        Task.FromResult(
            Result<IReadOnlyList<KubeObjectSummary>>.Failure(
                ErrorCode.InternalError,
                $"This cluster connection ({GetType().Name}) cannot enumerate namespace '{ns}' on "
                + $"cluster {ClusterId:D}, so nothing can say what is in it. It fails rather than "
                + "reporting an empty namespace: an empty namespace is the one answer that "
                + "authorises deleting it, and deleting a namespace is a recursive delete of every "
                + "object inside — including the volume claims a soft-deleted resource is restored "
                + "from."
            )
        );
}
