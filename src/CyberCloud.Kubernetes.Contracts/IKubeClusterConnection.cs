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
public interface IKubeClusterConnection
{
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

    /// <summary>Deletes the object a command addresses.</summary>
    /// <param name="command">The built command; only its target and identity are used.</param>
    /// <param name="policy">How to cascade.</param>
    /// <param name="cancellationToken">The reconcile's token.</param>
    Task<Result> DeleteAsync(
        KubeCommand command,
        CascadePolicy policy = CascadePolicy.Background,
        CancellationToken cancellationToken = default);
}
