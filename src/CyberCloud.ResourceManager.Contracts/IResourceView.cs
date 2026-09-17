using System.Collections.Immutable;

namespace CyberCloud.ResourceManager.Contracts;

/// <summary>
///     A reconciler's read-only, authorization-checked view of resources it does not own.
///     docs/plan/08 § What the resource manager deliberately does not do, the cross-resource seam.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why this exists.</b> src/Providers/README.md § Hard rule forbids one <c>Providers.*</c>
///         assembly referencing another, and the Assembly graph gate fails the build on it. A backup
///         vault protects <i>other providers'</i> resources and a customer-managed key is resolved by
///         every provider that persists, so both need to read a resource another provider rendered —
///         and until this seam nothing in <see cref="ReconcileContext" /> let a reconciler see a
///         resource it did not own (issue #90). The choice was between a seam above the provider and
///         a hole in the rule, and the rule has held for sixteen families.
///     </para>
///     <para>
///         <b>The authorization rule, written down.</b> A reconciler for the <i>owning</i> resource
///         <c>O</c> may view a <i>target</i> resource <c>T</c> exactly when the gateway would answer a
///         <c>GET</c> on <c>T</c> for a caller whose subject is <c>O</c> itself:
///         <c>IResourceAuthorizer.AuthorizeAsync(T, T.read, T.read, caller)</c> with
///         <c>caller = { TenantId = O.TenantId, SubjectType = "resource", SubjectId = O.Id }</c>. Same
///         seam, same engine, same <c>404</c> on refusal (docs/plan/07 § The enforcement seam —
///         existence is not disclosed to a resource any more than to a user). One difference, stated:
///         the check is <c>FullyConsistent</c> where a <c>GET</c>'s is <c>MinimizeLatency</c>, because
///         the check cache has no TTL and a reconciler has no token to pass — a vault denied once
///         before its grant would otherwise be denied forever. For the check to say
///         yes, a tuple has to grant <c>resource:O</c> the <c>reader</c> role on <c>T</c> or on a
///         scope above it, and the tenant writes that tuple through the ordinary role-assignment path
///         with <c>principalType: "resource"</c> — the way Azure grants a vault's system-assigned
///         identity a role on what it protects. Nothing is granted implicitly: the user who
///         <c>PUT</c> the vault may not be able to read the file share it names, and a seam that let
///         the vault read it anyway would be the confused deputy.
///     </para>
///     <para>
///         <b>Two gates run before the engine is asked, both fail-closed.</b> A target in another
///         tenant is refused before any grain of that tenant is touched, with the same <c>404</c> the
///         gateway gives (docs/plan/08 § The write path, end to end, step 1) — the view resolves the
///         target through the <i>owner's</i> tenant-qualified grain factory, so another tenant's path
///         has no grain to resolve to. A target whose type this silo does not serve is refused too,
///         because its read permission and its readable pointers come from the registration.
///     </para>
///     <para>
///         ⚠ <b>Read-only, and the argument is the rule above.</b> A write is authorized against the
///         <i>caller</i> and a reconciler has none — it runs on a reminder, under no user, with no
///         correlation id and nobody to bill or audit the change to. Everything in the write path
///         that makes a write a tenant's write (steps 3 to 11: locks, policy, quota, the index claim,
///         the parent edge, the operation record, <c>resource-changed</c>) hangs off that caller. A
///         resource that wrote another resource would be a write with none of them, from a component
///         clause 2 of the reconciler contract forbids to hold state, running twice when the reminder
///         fires twice. So this interface has no member that mutates, and
///         <c>CrossResourceViewTests.TheViewHasNoMemberThatCouldWrite</c> keeps it that way. A
///         provider that needs another resource to <i>change</i> asks the tenant to <c>PUT</c> it, or
///         publishes an action on its own type — which is what the vault does: it writes snapshots
///         <i>beside</i> the protected resource, under its own id, never into it.
///     </para>
///     <para>
///         ⚠ <b>What comes back is the other provider's public contract and nothing more.</b> The
///         body is the same projection the gateway returns at the type's newest api-version, with the
///         secret-marked pointers dropped the same way — a <c>SecretRef</c> handle, never a value. The
///         rendered objects are addresses, not contents: a vault that wants to snapshot a PVC needs
///         its name and namespace, and reads the object itself through
///         <see cref="ReconcileContext.Cluster" /> like any other object on the cluster it was placed
///         on. Neither result names a type from the other provider's assembly, which is how a provider
///         reaches another's <i>contract</i> — its schema — without reaching its implementation.
///     </para>
///     <para>
///         ⚠ <b>Costs, stated.</b> Every call is a grain hop to the index, a fully consistent ReBAC
///         walk over durable rows, and a grain hop to the resource, inside the reconciler's 30-second
///         budget (docs/plan/08 § The reconcile loop, clause 3); a reconciler viewing forty resources
///         in one pass spends its budget on this
///         and should return <see cref="ReconcileOutcome.InProgress" /> between batches.
///         <see cref="RenderedObjectsAsync" /> adds a namespace listing on the target's cluster. And
///         the view sees the target's <i>current</i> snapshot — a target mid-update reports
///         <see cref="ProvisioningState.Updating" />, and a reconciler that must act on a settled
///         shape checks the state before it does.
///     </para>
/// </remarks>
public interface IResourceView {
    /// <summary>
    ///     Reads another resource's snapshot, as the gateway would return it to a caller allowed to
    ///     read it.
    /// </summary>
    /// <param name="target">
    ///     The resource to read, by address. <see cref="ResourceId.Id" /> may be empty; the view
    ///     resolves the path through the tenant's index the way a <c>GET</c> does, on
    ///     <see cref="ResourceId.CanonicalPath" />, so either spelling of the provider namespace
    ///     finds the same resource.
    /// </param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    ///     The snapshot at the target type's newest api-version with its secret pointers dropped, or
    ///     <see cref="ErrorCode.ResourceNotFound" /> when the target is in another tenant, does not
    ///     exist, or the owning resource has not been granted <c>read</c> on it — one code for all
    ///     three, deliberately.
    /// </returns>
    Task<Result<ResourceSnapshot>> ReadAsync(ResourceId target, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Lists the addresses of the Kubernetes objects another resource's provider rendered for it.
    /// </summary>
    /// <param name="target">The resource whose objects are wanted, addressed as for <see cref="ReadAsync" />.</param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>
    ///     Every object in the target's namespace on the target's cluster that carries the target's
    ///     <c>cybercloud.io/resource-id</c> label (ADR-013), as addresses. The same
    ///     <see cref="ErrorCode.ResourceNotFound" /> as <see cref="ReadAsync" /> when the read would
    ///     be refused, and a failure naming the cluster when it cannot be listed — never an empty
    ///     list standing in for "could not look", because a vault that believed it would snapshot
    ///     nothing and report a backup.
    /// </returns>
    /// <remarks>
    ///     A clusterless target — one whose type does not declare <c>RequiresCluster</c> and carries no
    ///     cluster id — has no rendered objects, and the answer is an empty list rather than a
    ///     failure: that is a true statement about a DNS zone.
    /// </remarks>
    Task<Result<ImmutableArray<ObjectRef>>> RenderedObjectsAsync(
        ResourceId target,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
///     The <see cref="IResourceView" /> a context built by hand carries: it refuses.
/// </summary>
/// <remarks>
///     ⚠ Refuses rather than answering "not found", because the two mean different things to the
///     reconciler reading the answer. Not found is the authorization rule speaking and a reconciler
///     may act on it — a vault drops a protected item that is gone. This is a wiring fact, and a
///     reconciler that treated it as absence would tear down protection on a test harness's
///     omission. <c>ReconcileDriver</c> always supplies the real view.
/// </remarks>
public sealed class RefusingResourceView : IResourceView {
    const string Because =
        "This reconcile context carries no cross-resource view. A pass driven by ReconcileDriver "
        + "always carries the host's view; a context built by hand has to supply one through "
        + "ReconcileContext.View.";

    /// <inheritdoc />
    public Task<Result<ResourceSnapshot>> ReadAsync(ResourceId target, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result<ResourceSnapshot>.Failure(ErrorCode.InternalError, Because));

    /// <inheritdoc />
    public Task<Result<ImmutableArray<ObjectRef>>> RenderedObjectsAsync(
        ResourceId target,
        CancellationToken cancellationToken = default
    ) =>
        Task.FromResult(Result<ImmutableArray<ObjectRef>>.Failure(ErrorCode.InternalError, Because));
}

/// <summary>
///     Where a reconciler says "tell me when resources of this type in my subscription change".
///     docs/plan/08 § What the resource manager deliberately does not do, the event half of the seam.
/// </summary>
/// <remarks>
///     <para>
///         <b>How a change reaches the watcher.</b> <c>resource-changed</c> is emitted at step 11 of
///         every accepted write (docs/plan/08 § The write path, end to end). The manager fans each
///         event out to the resources that subscribed to its type in its subscription, and each
///         watcher's grain keeps the events it was handed until a pass acknowledges them. The next
///         pass over the watcher finds them in <see cref="ReconcileContext.Changes" />, and when it
///         converges the driver acknowledges what it read. Nothing here runs provider code outside a
///         pass: clause 2 of the reconciler contract (no hidden state) means the only place a
///         provider can receive anything is the context of its own pass.
///     </para>
///     <para>
///         <b>Each delivery is authorized by the same rule as a read.</b> Before an event is handed
///         to a watcher the manager checks that the watcher could <see cref="IResourceView.ReadAsync" />
///         the changed resource — the event carries its path, name and tags, and a watcher that had
///         not been granted <c>read</c> on the resource learns nothing about it, the way an
///         unauthorized <c>GET</c> learns nothing. A subscription is therefore not a grant; it is a
///         request to be told about what one is already allowed to see.
///     </para>
///     <para>
///         ⚠ <b>Bounded, and the bound is visible.</b> A watcher's grain keeps at most
///         <see cref="ReconcileInput.MaxPendingChanges" /> events; past that the oldest is dropped
///         and <see cref="ReconcileContext.ChangesDropped" /> says how many. A reconciler that sees a
///         nonzero count must rescan rather than trust the list — a vault re-lists what it protects.
///         The list is a hint about where to look, never the only record of what happened.
///     </para>
///     <para>
///         ⚠ <b>Subscribing is idempotent and every pass may do it.</b> A reconciler is called over
///         and over on a converged resource and has no memory between passes, so
///         <see cref="SubscribeAsync" /> on every pass is the shape that keeps clause 2 — the watch
///         index records a set, and re-adding a member is a no-op. Unsubscribe when the desired state
///         stops naming the type; a watcher that is deleted is dropped from the index the next time
///         a fan-out finds its grain gone.
///     </para>
///     <para>
///         ⚠ <b>What is owed, precisely.</b> Delivery is durable and the pass that runs next sees it.
///         What does not exist yet is the pass itself: a converged resource has no operation and no
///         reminder, so a notification waits for the next pass something else starts — a
///         <c>PUT</c>, a restore, or the drift scan (docs/plan/08 § The reconcile loop). The hook for
///         a manager-started pass is <see cref="IResourceGrain.NotifyChangedAsync" />, and the kind
///         it would start is a seventh <see cref="OperationKind" /> that converges without a body
///         change and tears nothing down on cancel. docs/plan/08 records it beside the seam.
///     </para>
/// </remarks>
public interface IResourceWatch {
    /// <summary>Asks to be told when resources of <paramref name="type" /> in the owner's subscription change.</summary>
    /// <param name="type">
    ///     The type to watch, in any spelling of its namespace. Watching one's own type is allowed and
    ///     is how a family with a coordinating resource hears about its siblings.
    /// </param>
    /// <param name="cancellationToken">Cancels the registration.</param>
    /// <returns>Success once the owner is in the watch list, whether or not it already was.</returns>
    Task<Result> SubscribeAsync(ResourceTypeName type, CancellationToken cancellationToken = default);

    /// <summary>Stops the owner hearing about <paramref name="type" />.</summary>
    /// <param name="type">The type to stop watching.</param>
    /// <param name="cancellationToken">Cancels the removal.</param>
    /// <returns>Success once the owner is out of the watch list, whether or not it was ever in it.</returns>
    Task<Result> UnsubscribeAsync(ResourceTypeName type, CancellationToken cancellationToken = default);
}

/// <summary>
///     The <see cref="IResourceWatch" /> a context built by hand carries: it refuses, for the reason
///     <see cref="RefusingResourceView" /> gives.
/// </summary>
public sealed class RefusingResourceWatch : IResourceWatch {
    const string Because =
        "This reconcile context carries no resource watch. A pass driven by ReconcileDriver always "
        + "carries the host's; a context built by hand has to supply one through ReconcileContext.Watch.";

    /// <inheritdoc />
    public Task<Result> SubscribeAsync(ResourceTypeName type, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Failure(ErrorCode.InternalError, Because));

    /// <inheritdoc />
    public Task<Result> UnsubscribeAsync(ResourceTypeName type, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Failure(ErrorCode.InternalError, Because));
}
