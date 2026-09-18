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
    ///     rather than an error. A co-owned command (<see cref="KubeCommand.IsCoOwned" />) can also
    ///     come back <see cref="ApplyResult.Stale" /> — the object moved since the read it was built
    ///     from; read again — and is a <b>failure</b> carrying <see cref="ErrorCode.ResourceNotFound" />
    ///     when the owner's object is absent, because a co-writer never creates it. A failed
    ///     <see cref="Result" /> otherwise means <i>we</i> got it wrong.
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
    ///     ⚠
    ///     <b>
    ///         This member is what makes clause 4 of the reconciler contract satisfiable, and it was
    ///         added by the first provider that tried.
    ///     </b> docs/plan/08 § The reconcile loop:
    ///     <i>
    ///         "Observes,
    ///         never assumes. <c>Converged</c> means it read back the desired shape, not that the apply
    ///         returned 200."
    ///     </i> Before this, the only members here were
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
    ///     Every object, or the failure that stopped the enumeration. ⚠
    ///     <b>
    ///         Never a partial
    ///         listing.
    ///     </b> An implementation that returned what it managed to read would report the
    ///     kinds it could not reach as absent, and absence here is what authorises deleting the
    ///     namespace.
    /// </returns>
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             THIS IS THE FIRST READ MEMBER HERE THAT IS NOT KEYED BY AN OBJECT, AND IT WAS
    ///             ADDED BY THE ONE CALLER THAT CANNOT BE EXPRESSED WITHOUT IT.
    ///         </b> Until it existed the
    ///         interface had <see cref="ApplyAsync" />, <see cref="GetAsync" /> and
    ///         <see cref="DeleteAsync" /> — two writes and a read of a name you already know — so a
    ///         component asking "what is in this namespace" had nowhere to ask. That is why
    ///         <c>RetainedVolume</c> is shaped "name, then verify" rather than "select, then delete",
    ///         and why <c>INamespaceInventory</c> had no implementation.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             Not <c>IClusterObjectInventory</c>, and the difference is the whole safety of a
    ///             namespace delete.
    ///         </b> That seam selects on
    ///         <c>cybercloud.io/managed-by=cybercloud</c> because a drift scan compares what the
    ///         platform wrote against what it meant to write. This one must find the objects that
    ///         selector excludes — a tenant's own <c>PersistentVolumeClaim</c>, a <c>Secret</c> an
    ///         operator added, a <c>StatefulSet</c> from a chart nobody registered — so it applies no
    ///         selector at all.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             The default implementation FAILS, and every connection that cannot really
    ///             enumerate a namespace inherits it on purpose.
    ///         </b> The alternative — an abstract member
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

    /// <summary>
    ///     The objects of one kind in one namespace that carry a label selector — the
    ///     "select" that <c>RetainedVolume</c>'s remarks said no member could do.
    /// </summary>
    /// <param name="kind">The kind to list. One REST path, so one round trip per page.</param>
    /// <param name="ns">
    ///     The namespace to list in, or empty for a cluster-scoped kind — the convention
    ///     <see cref="ObjectRef.IsClusterScoped" /> states. ⚠ A <c>PersistentVolume</c> is the first
    ///     kind listed this way: a file share's teardown selects the released volumes of its account
    ///     before it removes the driver that reclaims them.
    /// </param>
    /// <param name="labelSelector">
    ///     A Kubernetes label selector, for example <c>cnpg.io/cluster=main</c>. ⚠ Never empty:
    ///     an empty selector is <see cref="ListNamespaceAsync" />'s job for one kind, and a caller
    ///     that wants everything of a kind should say so there rather than here, where every caller
    ///     is looking for <i>its own</i> objects.
    /// </param>
    /// <param name="cancellationToken">The caller's budget.</param>
    /// <returns>Every matching object, or the failure that stopped the listing. Never a partial page.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             Added by the first provider whose operator names the claims it creates, and
    ///             not before.
    ///         </b> A <c>StatefulSet</c>'s claims are computable from the desired body —
    ///         <c>{volume}-{set}-{ordinal}</c> — so <c>RetainedVolumesAsync</c> could name them
    ///         without asking the cluster. CloudNativePG's are not: an instance replaced after a
    ///         failover takes the next serial, so a two-instance server may own <c>main-1</c> and
    ///         <c>main-4</c>, and a provider that predicted <c>main-1</c> and <c>main-2</c> would
    ///         detach one claim and let the other go. What every one of those claims carries is the
    ///         operator's own <c>cnpg.io/cluster</c> label, and this is how a provider asks for
    ///         them.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Fails by default, for the reason <see cref="ListNamespaceAsync" /> does.</b> A
    ///         connection that answered an empty list here would tell a teardown "there are no claims
    ///         to detach", and the operator's garbage collector would then remove the ones that were
    ///         there. Every double that does not really list inherits a refusal; the production
    ///         handle and the conformance fake override it.
    ///     </para>
    /// </remarks>
    Task<Result<IReadOnlyList<KubeObjectSummary>>> ListAsync(
        GroupVersionKind kind,
        string ns,
        string labelSelector,
        CancellationToken cancellationToken = default
    ) =>
        Task.FromResult(
            Result<IReadOnlyList<KubeObjectSummary>>.Failure(
                ErrorCode.InternalError,
                $"This cluster connection ({GetType().Name}) cannot list {kind?.Kind} by selector "
                + $"'{labelSelector}' in namespace '{ns}' on cluster {ClusterId:D}. It fails rather "
                + "than answering an empty list: an empty list is the answer that tells a teardown "
                + "there is nothing to detach, and the claims it did not see are then "
                + "garbage-collected with their owner."
            )
        );

    /// <summary>
    ///     Makes <paramref name="owner" /> the one controller of <paramref name="target" />, or —
    ///     with <see langword="null" /> — leaves <paramref name="target" /> with no owner at all.
    /// </summary>
    /// <param name="target">The dependent. A namespaced object in the owner's namespace.</param>
    /// <param name="owner">
    ///     The controller to write, read back from the API server so that its
    ///     <see cref="OwnerRef.Uid" /> is real; or <see langword="null" /> to clear every owner
    ///     reference the object carries.
    /// </param>
    /// <param name="cancellationToken">The caller's budget.</param>
    /// <returns>
    ///     Success once the API server holds the new ownership, or
    ///     <see cref="ErrorCode.ResourceNotFound" /> when there is no such object.
    /// </returns>
    /// <remarks>
    ///     <para>
    ///         <b>
    ///             This is <c>kubectl cnpg destroy --keep-pvc</c>'s primitive, made available to a
    ///             reconciler.
    ///         </b> An operator that creates its own claims stamps a controller reference
    ///         onto each, and the garbage collector removes the claim the moment the controller
    ///         goes — before a recovery window has started. Clearing the reference first is what
    ///         lets the claim outlive its <c>Cluster</c>; writing a fresh one on the restore is what
    ///         lets the operator find it again, because it indexes its claims by controller.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             A metadata patch and not a server-side apply, because apply cannot do this.
    ///         </b> <c>ownerReferences</c> is a map-typed list keyed on <c>uid</c>, and server-side
    ///         apply removes an entry only when the manager that owns it applies without it. The
    ///         entry an operator wrote belongs to the operator's manager, so this platform's manager
    ///         can add entries beside it and can never take it away. A JSON merge patch replaces
    ///         the whole list, under whichever manager sends it, which is the only spelling that
    ///         reaches an entry somebody else wrote.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Fails by default.</b> A double that reported success and changed nothing would
    ///         let a teardown believe its claims were detached while the garbage collector took them,
    ///         which is the defect this member exists to close.
    ///     </para>
    /// </remarks>
    Task<Result> SetOwnerAsync(
        ObjectRef target,
        OwnerRef? owner,
        CancellationToken cancellationToken = default
    ) =>
        Task.FromResult(
            Result.Failure(
                ErrorCode.InternalError,
                $"This cluster connection ({GetType().Name}) cannot change who owns '{target}' on "
                + $"cluster {ClusterId:D}. It fails rather than pretending: a claim that was "
                + "reported detached and was not is garbage-collected with its owner."
            )
        );
}
