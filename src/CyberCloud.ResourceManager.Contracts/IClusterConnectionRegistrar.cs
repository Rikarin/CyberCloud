namespace CyberCloud.ResourceManager.Contracts;

/// <summary>
///     Registers a cluster the platform just created, so that later resources can be placed in it.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This seam exists because a reconciler may not do this and the refusal is right.</b>
///         <c>module-layering.txt</c> records it under
///         <c>CyberCloud.Providers.ContainerService</c>: a provider may reference
///         <c>CyberCloud.Kubernetes.Contracts</c> only (docs/plan/03 § The .Contracts split), so a
///         reconciler cannot reach <c>IClusterConnectionGrain.AttachAsync</c> — and <i>"attaching a
///         connection is the resource manager's job rather than a reconciler's"</i>. The consequence
///         was written down rather than worked around, and the consequence was that
///         <c>AttachAsync</c> was called by tests and by nothing else: a cluster
///         <c>CyberCloud.ContainerService/managedClusters</c> created converged and was then
///         unreachable as a <c>clusterId</c> forever.
///     </para>
///     <para>
///         ⚠ <b>So the seam is above the provider rather than inside it.</b> A reconciler that
///         produced a cluster reports the fact through
///         <see cref="ReconcileContext.ClusterConnections" /> — a sink, like
///         <see cref="IReconcileLog" /> — and <c>ReconcileDriver</c> is what calls this. The provider
///         supplies the facts, which only it knows; the manager performs the write, which only it may.
///     </para>
///     <para>
///         ⚠ <b>The driver attaches only after a pass that returned
///         <see cref="ReconcileOutcomeKind.Converged" />, and that ordering is the whole safety
///         property.</b> A connection registered while a control plane is still coming up is a
///         connection every later placement fails against: the resource manager would hand a
///         reconciler a handle to an API server that answers nothing, the reconcile would fail, back
///         off, and report a cluster problem rather than a timing one. docs/plan/09 § Kubernetes in
///         Kubernetes budgets six to eight minutes before there is an API server at all, so the window
///         is minutes wide rather than theoretical.
///     </para>
/// </remarks>
public interface IClusterConnectionRegistrar {
    /// <summary>Registers, or re-registers, one cluster's connection.</summary>
    /// <param name="descriptor">
    ///     The cluster, its owning tenant, and where its credential lives. ⚠ Never the credential
    ///     itself — see <c>ClusterConnectionDescriptor.CredentialRef</c>.
    /// </param>
    /// <param name="cancellationToken">Cancels the registration.</param>
    /// <returns>
    ///     Success once the connection is registered. ⚠ Idempotent: a reconcile pass runs over and
    ///     over on a converged resource, so every pass after the first re-attaches the same
    ///     descriptor, and that has to be a success rather than a conflict.
    /// </returns>
    Task<Result> AttachAsync(
        ClusterConnectionDescriptor descriptor,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
///     Where a reconciler says "this pass produced a cluster the platform can now reach".
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Synchronous and returning nothing, for the reasons <see cref="IReconcileLog" />
///         gives.</b> A reconciler is bounded at 30 seconds and runs inside a single-threaded grain
///         turn, so a seam it can await is one more thing that can spend the budget. The driver
///         collects what was reported and performs the attach once, after the pass — which is also
///         what lets it refuse to attach a pass that did not converge.
///     </para>
///     <para>
///         ⚠ <b>Reporting is not attaching, and a reconciler must not treat it as though it were.</b>
///         A descriptor reported on a pass that returns
///         <see cref="ReconcileOutcomeKind.InProgress" /> is discarded, deliberately. Report it on the
///         pass that converges.
///     </para>
/// </remarks>
public interface IClusterConnectionSink {
    /// <summary>Records that this pass produced a reachable cluster.</summary>
    /// <param name="descriptor">
    ///     What the connection points at and who owns it. Reported once per pass; a second report in
    ///     one pass replaces the first, because a pass converges one resource and a resource is one
    ///     cluster.
    /// </param>
    void Produced(ClusterConnectionDescriptor descriptor);
}

/// <summary>
///     The <see cref="IClusterConnectionSink" /> a <see cref="ReconcileContext" /> carries when nobody
///     supplied one.
/// </summary>
/// <remarks>
///     ⚠ <b>It throws, and a discarding no-op was the wrong default.</b> The failure this whole seam
///     exists to close is a cluster that converges and is never registered, and a sink that quietly
///     dropped the report would reproduce it exactly — with the reconciler's own code saying it had
///     been handled. A pass driven by <c>ReconcileDriver</c> always carries the collecting sink; a
///     context built by hand that reaches this has to supply one through
///     <see cref="ReconcileContext.ClusterConnections" />.
/// </remarks>
public sealed class RefusingClusterConnectionSink : IClusterConnectionSink {
    /// <inheritdoc />
    public void Produced(ClusterConnectionDescriptor descriptor) {
        ArgumentNullException.ThrowIfNull(descriptor);

        throw new InvalidOperationException(
            $"This reconcile context carries no cluster-connection sink, so {descriptor} cannot be "
            + "registered and the cluster would converge unreachable. A pass driven by "
            + "ReconcileDriver always carries one; a context built by hand has to supply it through "
            + "ReconcileContext.ClusterConnections."
        );
    }
}
