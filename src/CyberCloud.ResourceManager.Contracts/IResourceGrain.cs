using System.Collections.Immutable;

namespace CyberCloud.ResourceManager.Contracts;

/// <summary>
///     What step 8 writes: a desired body at an api-version, and who wrote it.
/// </summary>
/// <remarks>
///     ⚠ <b><see cref="Verb" /> is carried into the grain rather than resolved in the manager, and
///     that is what makes <c>PUT</c> and <c>PATCH</c> different at the one place the difference
///     matters.</b> A <c>PUT</c> replaces the version's slice of the superset outright; a
///     <c>PATCH</c> merges into it. Deciding that in the manager and handing the grain a finished
///     body would work, and would put the merge on the far side of the etag check — so two concurrent
///     patches could each read the same base, merge, and one would win silently. The grain is the
///     single writer; the merge belongs where the single writer is.
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.ResourceManager.DesiredSubmission")]
public sealed record DesiredSubmission {
    /// <summary>The resource's address, with its GUID resolved.</summary>
    [Id(0)]
    public string Path { get; init; } = string.Empty;

    /// <summary>The api-version the body is written at.</summary>
    [Id(1)]
    public string ApiVersion { get; init; } = string.Empty;

    /// <summary>The body, as JSON text, already validated against that version's schema.</summary>
    [Id(2)]
    public string Body { get; init; } = "{}";

    /// <summary><see cref="WriteVerb.Put" /> replaces; <see cref="WriteVerb.Patch" /> merges.</summary>
    [Id(3)]
    public WriteVerb Verb { get; init; } = WriteVerb.Unknown;

    /// <summary>The operation that will drive this to convergence.</summary>
    [Id(4)]
    public Guid OperationId { get; init; }

    /// <summary>The <c>If-Match</c> etag, or empty for an unconditional write.</summary>
    [Id(5)]
    public string IfMatch { get; init; } = string.Empty;

    /// <summary>Who wrote it.</summary>
    [Id(6)]
    public CallerContext Caller { get; init; } = new();

    /// <summary>Tags to set, when the type supports them.</summary>
    [Id(7)]
    public ImmutableDictionary<string, string> Tags { get; init; } =
        ImmutableDictionary<string, string>.Empty;

    /// <summary>Where the resource lives.</summary>
    [Id(8)]
    public string Location { get; init; } = string.Empty;

    /// <summary>Which cluster, or <see cref="Guid.Empty" /> for a clusterless provider.</summary>
    [Id(9)]
    public Guid ClusterId { get; init; }

    /// <summary>The type's declared properties at this version, so the grain can project on read.</summary>
    /// <remarks>
    ///     ⚠ <b>The grain is handed the pointer set rather than looking the registry up itself.</b> A
    ///     grain that resolved the registry would need a reference to it, and the registry is built
    ///     from providers — which would make every resource grain depend on every provider assembly.
    ///     Carrying the pointers on the submission keeps the grain a store and leaves the registry
    ///     where the manager is.
    /// </remarks>
    [Id(10)]
    public ImmutableArray<string> DeclaredPointers { get; init; } = [];
}

/// <summary>
///     One resource's desired state, its observed state, and its lifecycle. docs/plan/08.
/// </summary>
/// <remarks>
///     <para>
///         <b>Kind</b> Entity · <b>Tier</b> Durable · <b>Key</b> <c>res/{resourceId:N}</c>,
///         tenant-qualified (docs/plan/06 § Grain keys). Build it with <c>GrainKeys.Resource</c>.
///     </para>
///     <para>
///         ⚠ <b>Keyed by the GUID and not by the path</b> — docs/plan/06 § Grain keys, so a rename is
///         a metadata update rather than a grain migration. The path lives in the state and in
///         <c>IResourceIndexGrain</c>, and the two are updated in one flow with the index claimed
///         first (docs/plan/06 § Two-phase create).
///     </para>
///     <para>
///         ⚠ <b>This grain never provisions inline.</b> docs/plan/08 § The reconcile loop: <i>"The
///         resource grain records intent and returns; a reminder drives convergence."</i>
///         <see cref="SubmitDesiredAsync" /> writes and returns; the work happens in the operation
///         grain's <c>DriveAsync</c>.
///     </para>
///     <para>
///         ⚠ <b>The state is a superset across api-versions.</b> docs/plan/08 § The provider registry:
///         <i>"the grain's state is a <b>superset</b> and a read at an old version projects down"</i>.
///         <see cref="GetAsync" /> takes the version to project to, and there is deliberately no
///         overload that omits it — a read with no version is a read that would get whatever the
///         newest writer happened to store.
///     </para>
/// </remarks>
[Alias("Rm.Resource")]
public interface IResourceGrain : IGrainWithStringKey {
    /// <summary>
    ///     Step 8: writes durable desired state and moves to <c>Creating</c> or <c>Updating</c>.
    /// </summary>
    /// <param name="submission">The body, the version, the verb and the operation.</param>
    /// <returns>
    ///     The resource as it now stands, or a failure —
    ///     <see cref="ErrorCode.PreconditionFailed" /> when <see cref="DesiredSubmission.IfMatch" />
    ///     does not match, <see cref="ErrorCode.ScopeLocked" /> when a <see cref="LockLevel.ReadOnly" />
    ///     lock is in force, or <see cref="ErrorCode.OperationInProgress" /> when another operation is
    ///     already driving this resource.
    /// </returns>
    /// <remarks>
    ///     ⚠ <b>An identical <c>PUT</c> is a no-op and reports success.</b>
    ///     docs/plan/06 § Two-phase create: <i>"the caller retries the <c>PUT</c> — which is idempotent
    ///     because <c>PUT</c> with the same body on an existing resource is a no-op, which is exactly
    ///     why the API is <c>PUT</c> and not <c>POST</c>."</i> "Identical" means the projected slice
    ///     for that api-version is byte-equal after canonical ordering, so a body whose properties
    ///     arrived in a different order is still a no-op. The returned snapshot's
    ///     <c>ProvisioningState</c> and <c>Etag</c> are unchanged, which is how a caller tells.
    /// </remarks>
    Task<Result<ResourceSnapshot>> SubmitDesiredAsync(DesiredSubmission submission);

    /// <summary>Reads the resource, projected down to one api-version.</summary>
    /// <param name="apiVersion">
    ///     The version to project to. ⚠ Required. A read at an old version gets exactly the shape it
    ///     was written against — docs/plan/08 § The provider registry.
    /// </param>
    /// <param name="declaredPointers">
    ///     The pointers that version declares, from the registry. Empty projects nothing, which is
    ///     what an unknown version deserves.
    /// </param>
    /// <returns>
    ///     The snapshot, or <see cref="ErrorCode.ResourceNotFound" />. ⚠ A resource in
    ///     <see cref="ProvisioningState.Deleting" /> is <i>found</i> — docs/plan/06 § Two-phase create
    ///     keeps it visible on purpose.
    /// </returns>
    Task<Result<ResourceSnapshot>> GetAsync(string apiVersion, ImmutableArray<string> declaredPointers);

    /// <summary>Begins a delete: moves to <c>Deleting</c> and records the operation.</summary>
    /// <param name="operationId">The operation that will drive the teardown.</param>
    /// <param name="ifMatch">The <c>If-Match</c> etag, or empty.</param>
    /// <returns>
    ///     The snapshot in <see cref="ProvisioningState.Deleting" />;
    ///     <see cref="ErrorCode.ScopeLocked" /> when a <see cref="LockLevel.CanNotDelete" /> or
    ///     <see cref="LockLevel.ReadOnly" /> lock is in force; or
    ///     <see cref="ErrorCode.OperationInProgress" /> when a <i>different</i> operation is already
    ///     driving the resource.
    /// </returns>
    /// <remarks>
    ///     ⚠ <b>The single-writer guard is the same one <see cref="SubmitDesiredAsync" /> applies, and
    ///     it is here because docs/plan/03 § Providers requires it.</b> That section's conformance list
    ///     includes <i>"delete while an operation is running → 409"</i>. A delete that raced a live
    ///     create would tear down objects the create is still applying, and would leave the create's
    ///     quota lease and index claim owned by an operation whose resource is on its way out. A
    ///     re-drive of the <i>same</i> operation is not a conflict — that is the resumable path.
    /// </remarks>
    Task<Result<ResourceSnapshot>> BeginDeleteAsync(Guid operationId, string ifMatch);

    /// <summary>
    ///     Records that teardown finished and deletes the grain's state.
    /// </summary>
    /// <returns>Success, or <see cref="ErrorCode.Conflict" /> when the resource is not <c>Deleting</c>.</returns>
    /// <remarks>
    ///     ⚠ Called only after the reconciler's <c>DeleteAsync</c> <b>converged</b> — read the objects
    ///     back as gone, not issued a delete. A teardown that failed leaves the resource in
    ///     <see cref="ProvisioningState.Deleting" /> and visible, per docs/plan/06 § Two-phase create,
    ///     <i>"never silently gone while its pods still run and its meter still ticks"</i>.
    /// </remarks>
    Task<Result> CompleteDeleteAsync();

    /// <summary>Records a terminal provisioning state after a reconcile pass ended.</summary>
    /// <param name="terminal">
    ///     <see cref="ProvisioningState.Succeeded" />, <see cref="ProvisioningState.Failed" /> or
    ///     <see cref="ProvisioningState.Canceled" />.
    /// </param>
    /// <param name="failure">
    ///     Why, for <see cref="ProvisioningState.Failed" />, or <see langword="null" />. Surfaced as
    ///     <see cref="ResourceSnapshot.LastFailure" />.
    /// </param>
    Task<Result<ResourceSnapshot>> CompleteAsync(ProvisioningState terminal, Error? failure);

    /// <summary>Stores what the last observation saw.</summary>
    /// <param name="observed">
    ///     The observation. ⚠ Hot-tier by nature (docs/plan/05 § Hot) but stored with the durable
    ///     record here, because a resource whose desired state survived a restart and whose observed
    ///     state did not would report drift against every one of its own objects on the next scan.
    /// </param>
    Task<Result> ReportObservedAsync(ObservedState observed);

    /// <summary>Sets or clears the lock at this scope.</summary>
    /// <param name="level">The lock. <see cref="LockLevel.None" /> clears it.</param>
    /// <remarks>
    ///     ⚠ <b>Only the lock <i>at this scope</i>.</b> docs/plan/06 § Tags, locks makes locks
    ///     inherited down the hierarchy, so the effective lock is the strongest of this one and those
    ///     on the group, the subscription and the management group. Step 4 of the write path resolves
    ///     the inheritance; this grain holds one link of it.
    /// </remarks>
    Task<Result> SetLockAsync(LockLevel level);

    /// <summary>Everything the manager needs to build a <see cref="ReconcileContext" />.</summary>
    /// <returns>
    ///     The desired body as stored, the last observation, the api-version, and the placement —
    ///     or <see cref="ErrorCode.ResourceNotFound" />.
    /// </returns>
    Task<Result<ReconcileInput>> GetReconcileInputAsync();

    /// <summary>Drops this activation — see <c>ITenantGrain.DeactivateAsync</c>.</summary>
    Task DeactivateAsync();
}

/// <summary>What a reconcile pass reads out of the resource grain.</summary>
/// <remarks>
///     ⚠ <b>One call rather than four.</b> A driver that fetched the desired body, the observation,
///     the version and the placement separately would see four different points in time, and the pass
///     it built from them would converge towards a state that never existed. The grain is
///     single-threaded, so one call is one consistent read.
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.ResourceManager.ReconcileInput")]
public sealed record ReconcileInput {
    /// <summary>The address, with the GUID resolved.</summary>
    [Id(0)]
    public string Path { get; init; } = string.Empty;

    /// <summary>The resource GUID.</summary>
    [Id(1)]
    public Guid ResourceId { get; init; }

    /// <summary>The api-version the desired body was written at.</summary>
    [Id(2)]
    public string ApiVersion { get; init; } = string.Empty;

    /// <summary>The desired body as stored, as JSON text.</summary>
    [Id(3)]
    public string Desired { get; init; } = "{}";

    /// <summary>The last observation, or <see langword="null" /> when nothing has been observed.</summary>
    [Id(4)]
    public ObservedState? Observed { get; init; }

    /// <summary>Which cluster, or <see cref="Guid.Empty" />.</summary>
    [Id(5)]
    public Guid ClusterId { get; init; }

    /// <summary>The current provisioning state.</summary>
    [Id(6)]
    public ProvisioningState ProvisioningState { get; init; } = ProvisioningState.Unknown;

    /// <summary>The operation currently driving this resource.</summary>
    [Id(7)]
    public Guid OperationId { get; init; }
}
