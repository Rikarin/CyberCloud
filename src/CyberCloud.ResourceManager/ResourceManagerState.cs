namespace CyberCloud.ResourceManager;

/// <summary>
///     The durable state of an <c>IResourceGrain</c>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every collection member is <c>{ get; set; }</c> and that is load-bearing.</b>
///         System.Text.Json — which is one of the serializers a persisted grain state may pass
///         through — <b>silently does not populate</b> a get-only collection property on read: it
///         reads the property, finds an initialized empty collection, and adds nothing. The state
///         comes back structurally valid and empty, which is a resource that has forgotten its
///         desired body rather than a deserialization error anyone can see. The same rule as
///         <c>CyberCloud.Tenancy/TenancyState.cs</c>, for the same reason.
///     </para>
///     <para>
///         <b><see cref="Superset" /> is the whole point of the api-version machinery.</b>
///         docs/plan/08 § The provider registry:
///         <i>
///             "the grain's state is a <b>superset</b> and a read
///             at an old version projects down"
///         </i>. Every write merges its version's properties into
///         this one document, and every read filters it by the requested version's declared pointers.
///         Storing one body per version instead would make "what is the resource actually like" a
///         question with N answers.
///     </para>
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.ResourceManager.State.Resource")]
public sealed class ResourceState {
    /// <summary>The address, as written. Empty before the first submission.</summary>
    [Id(0)]
    public string Path { get; set; } = string.Empty;

    /// <summary>The api-version of the most recent write.</summary>
    [Id(1)]
    public string ApiVersion { get; set; } = string.Empty;

    /// <summary>
    ///     The union of every version's properties, as JSON text. ⚠ Never returned to a caller
    ///     unprojected.
    /// </summary>
    [Id(2)]
    public string Superset { get; set; } = "{}";

    /// <summary>The Azure provisioning vocabulary.</summary>
    [Id(3)]
    public ProvisioningState ProvisioningState { get; set; } = ProvisioningState.Unknown;

    /// <summary>Key/value tags, at most 50 pairs.</summary>
    [Id(4)]
    public Dictionary<string, string> Tags { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The etag, for <c>If-Match</c>. Bumped on every write that changes something.</summary>
    /// <remarks>
    ///     ⚠ <b>Not bumped by a no-op <c>PUT</c>.</b> An etag that moved on an identical write would
    ///     make the retry that docs/plan/06 § Two-phase create relies on invalidate a concurrent
    ///     reader's <c>If-Match</c> for no reason.
    /// </remarks>
    [Id(5)]
    public string Etag { get; set; } = string.Empty;

    /// <summary>A monotonic version, so a projector can drop a reordered event.</summary>
    [Id(6)]
    public long Version { get; set; }

    /// <summary>Where the resource lives.</summary>
    [Id(7)]
    public string Location { get; set; } = string.Empty;

    /// <summary>Which cluster, or <see cref="Guid.Empty" />.</summary>
    [Id(8)]
    public Guid ClusterId { get; set; }

    /// <summary>Who created it.</summary>
    [Id(9)]
    public string CreatedBy { get; set; } = string.Empty;

    /// <summary>When.</summary>
    [Id(10)]
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Who last changed it.</summary>
    [Id(11)]
    public string ModifiedBy { get; set; } = string.Empty;

    /// <summary>When.</summary>
    [Id(12)]
    public DateTimeOffset ModifiedAt { get; set; }

    /// <summary>The operation currently driving this resource.</summary>
    [Id(13)]
    public Guid OperationId { get; set; }

    /// <summary>Why the last attempt failed. ⚠ Survives on a resource stuck in <c>Deleting</c>.</summary>
    [Id(14)]
    public string LastFailure { get; set; } = string.Empty;

    /// <summary>The lock at this scope. Inheritance is resolved by the write path, not here.</summary>
    [Id(15)]
    public LockLevel Lock { get; set; } = LockLevel.None;

    /// <summary>What the last observation saw.</summary>
    [Id(16)]
    public ObservedState? Observed { get; set; }

    /// <summary>
    ///     The <c>resource-changed</c> events handed to this resource by the watch fan-out and not yet
    ///     acknowledged by a converged pass — <see cref="IResourceGrain.NotifyChangedAsync" />. Bounded
    ///     at <see cref="ReconcileInput.MaxPendingChanges" />; the oldest is dropped past that and
    ///     <see cref="ChangesDropped" /> counts it.
    /// </summary>
    [Id(17)]
    public List<PendingChange> PendingChanges { get; set; } = [];

    /// <summary>
    ///     The sequence number the last delivered event was given. Monotonic for the life of the
    ///     resource, so an acknowledgement can name "everything up to what I read" and leave what
    ///     arrived after the read.
    /// </summary>
    [Id(18)]
    public long ChangeSequence { get; set; }

    /// <summary>Events dropped since the last acknowledgement because the list was full.</summary>
    [Id(19)]
    public int ChangesDropped { get; set; }

    /// <summary>Whether anything has ever been written here.</summary>
    public bool Exists => Path.Length > 0;
}

/// <summary>One delivered event and the sequence number that lets a pass acknowledge up to it.</summary>
[GenerateSerializer]
[Alias("CyberCloud.ResourceManager.State.PendingChange")]
public sealed class PendingChange {
    /// <summary>Its position in this resource's delivery order.</summary>
    [Id(0)]
    public long Sequence { get; set; }

    /// <summary>The event as the fan-out handed it over.</summary>
    [Id(1)]
    public ResourceChangedEvent Change { get; set; } = new();
}

/// <summary>
///     The durable state of an <c>IOperationGrain</c> — everything a re-drive needs.
/// </summary>
/// <remarks>
///     ⚠ <b>Durable, per docs/plan/08 § Long-running operations and ADR-003.</b> This is the state
///     behind docs/plan/00's non-negotiable <i>every LRO is resumable</i>: on activation after a silo
///     loss the grain reads this, re-registers its reminder and continues from
///     <see cref="Attempts" />, holding the same quota lease and the same index claim it held before.
///     <para>
///         Collections are <c>{ get; set; }</c> for the reason given on <see cref="ResourceState" />.
///     </para>
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.ResourceManager.State.Operation")]
public sealed class OperationGrainState {
    /// <summary>What the operation is for. <see langword="null" /> before <c>StartAsync</c>.</summary>
    [Id(0)]
    public OperationSpec? Spec { get; set; }

    /// <summary>Azure's status vocabulary.</summary>
    [Id(1)]
    public OperationState Status { get; set; } = OperationState.Unknown;

    /// <summary>The progress array, oldest first. Capped — see <c>OperationGrain.MaxProgressEntries</c>.</summary>
    [Id(2)]
    public List<OperationProgress> Progress { get; set; } = [];

    /// <summary>When the operation was accepted.</summary>
    [Id(3)]
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>When it reached a terminal state.</summary>
    [Id(4)]
    public DateTimeOffset EndedAt { get; set; }

    /// <summary>Why it failed.</summary>
    [Id(5)]
    public Error? Failure { get; set; }

    /// <summary>How far along, 0 to 100.</summary>
    [Id(6)]
    public int PercentComplete { get; set; }

    /// <summary>Whether cancellation has been asked for. ⚠ Not the same as being cancelled.</summary>
    [Id(7)]
    public bool CancelRequested { get; set; }

    /// <summary>Why.</summary>
    [Id(8)]
    public string CancelReason { get; set; } = string.Empty;

    /// <summary>
    ///     How many reconcile passes have run — the scheduler's backoff index and the step cursor
    ///     docs/plan/08 § Long-running operations names.
    /// </summary>
    [Id(9)]
    public int Attempts { get; set; }

    /// <summary>
    ///     How many times this grain has activated while the operation was live. ⚠ Greater than one
    ///     means it was re-driven, which is what makes resumability observable rather than asserted.
    /// </summary>
    [Id(10)]
    public int Activations { get; set; }

    /// <summary>
    ///     Whether anything has been applied to the data plane yet.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This is what makes cancellation complete rather than abandon.</b>
    ///     docs/plan/08 § Long-running operations:
    ///     <i>
    ///         "for anything already applied it runs the delete
    ///         path. A 'cancelled' create that leaves resources running is a billing dispute waiting to
    ///         happen."
    ///     </i> Set the moment the first reconcile pass returns anything other than an
    ///     immediate failure, because a pass that got far enough to be interrupted may have applied
    ///     something the grain cannot see.
    /// </remarks>
    [Id(11)]
    public bool Applied { get; set; }

    /// <summary>How many progress entries were dropped by the cap.</summary>
    /// <remarks>
    ///     ⚠ Recorded so a reader can tell a truncated history from a short one. A capped array that
    ///     did not say it was capped would make "the operation reported nothing before this" and "the
    ///     operation reported 900 things before this" look identical.
    /// </remarks>
    [Id(12)]
    public int ProgressDropped { get; set; }

    /// <summary>Child operations, for a nested operation.</summary>
    [Id(13)]
    public List<Guid> Children { get; set; } = [];

    /// <summary>Whether the delete path has finished running for a cancellation.</summary>
    [Id(14)]
    public bool CancelTeardownDone { get; set; }

    /// <summary>
    ///     Whether this operation has seen the resource's index claim confirmed — by the write path's
    ///     step 3, or by its own first pass when that step never ran.
    /// </summary>
    /// <remarks>
    ///     ⚠ Set by <c>OperationGrain.DriveAsync</c> and not by the write path, because the write path
    ///     cannot reach this state after the silo it was running on has died — which is the one case
    ///     the field exists for. See <c>OperationGrain.ConfirmClaimAsync</c>; issue #44's first storm
    ///     is where the gap it closes was found.
    /// </remarks>
    [Id(15)]
    public bool IndexConfirmed { get; set; }
}

/// <summary>
///     The durable state of an <c>IParkedResourceRegistryGrain</c> — one resource group's
///     soft-deleted resources, docs/plan/08 § Soft delete.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Durable, and the argument is that nothing else holds this at all.</b> Every other
///         fact about a parked resource has a second home — the name is held by
///         <c>IResourceIndexGrain</c>, the body by <see cref="ResourceState" />, the quota by the
///         subscription. The <i>enumeration</i> has none: the index is one grain per path and one-way,
///         the group's membership deliberately no longer holds the resource, and no scan can find
///         what no listing names. So docs/plan/05 § Choosing a tier's question — "can this be
///         rebuilt" — answers no, and losing it puts every parked resource in the group back where
///         issue #71 found them: recoverable in principle and reachable only by somebody who already
///         remembers the exact path.
///     </para>
///     <para>
///         Keyed by resource GUID rather than by path, for <c>GrainKeys.Resource</c>'s reason: a
///         restore puts the resource back at its old address, but nothing here should have to be
///         rewritten if it were ever moved or renamed, and the GUID is what both clears take.
///     </para>
///     <para>
///         The collection is <c>{ get; set; }</c> for the reason given on <see cref="ResourceState" />.
///     </para>
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.ResourceManager.State.ParkedResourceRegistry")]
public sealed class ParkedResourceRegistryState {
    /// <summary>The parked resources, by resource id.</summary>
    [Id(0)]
    public Dictionary<Guid, ParkedResource> Entries { get; set; } = [];
}

/// <summary>
///     The durable state of an <c>IResourceWatchGrain</c> — the resources in one subscription that
///     asked to hear when resources of one type change. docs/plan/08 § What the resource manager
///     deliberately does not do.
/// </summary>
/// <remarks>
///     ⚠ <b>Durable for the parked registry's reason: the enumeration has no second home.</b> The
///     reconciler that registered a watch holds nothing between passes, and the passes in between
///     are exactly when the fan-out needs to find it. <c>durable-grains.txt</c> carries the argument
///     beside the line. Keyed by resource GUID so a watcher that is renamed or restored keeps its
///     watch, and so the fan-out can hand the event to <c>IResourceGrain</c> by the key it already
///     has.
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.ResourceManager.State.ResourceWatch")]
public sealed class ResourceWatchState {
    /// <summary>The watchers, by resource id.</summary>
    [Id(0)]
    public Dictionary<Guid, ResourceWatcher> Watchers { get; set; } = [];
}
