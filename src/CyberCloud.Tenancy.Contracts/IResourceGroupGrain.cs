using CyberCloud.Core;
using CyberCloud.Core.Resources;

namespace CyberCloud.Tenancy.Contracts;

/// <summary>
///     A resource group — <b>a lifecycle unit, not a folder</b> (docs/plan/06 § The hierarchy).
/// </summary>
/// <remarks>
///     <para>
///         <b>Kind</b> Entity · <b>Tier</b> Durable · <b>Key</b>
///         <c>sub/{subscriptionId:N}/rg/{name}</c>, tenant-qualified (docs/plan/06 § Grain keys).
///         Build it with <c>GrainKeys.ResourceGroup</c>.
///     </para>
///     <para>
///         <b>What this grain owns and what it does not.</b> It owns <i>membership</i> and the
///         lifecycle of the group. It does <b>not</b> own the resources: <c>IResourceGrain</c>, its
///         desired/observed split, the providers and the reconcilers are the resource manager's
///         (docs/plan/08) and are deliberately not built here. The four methods below are the
///         resource-group half of the create and delete choreographies in docs/plan/06 § Two-phase
///         create; the orchestrator that calls them, in order, alongside the index grain and the
///         resource grain, is the resource manager.
///     </para>
///     <para>
///         ⚠ <b>The delete order is the reverse of the create order and it is the harder half.</b>
///         docs/plan/06 § Two-phase create: "release the index first (so the name is immediately
///         reusable), then tear down the data plane, then delete the grain state. A resource whose
///         data plane teardown fails is left in <c>Deleting</c> with a retry reminder and is
///         <i>visible</i> in listings with that state — never silently gone while its pods still run
///         and its meter still ticks."
///     </para>
/// </remarks>
[Alias("CyberCloud.Tenancy.IResourceGroupGrain")]
public interface IResourceGroupGrain : IGrainWithStringKey {
    /// <summary>
    ///     How long a member must have been <see cref="ProvisioningState.Creating" /> before the
    ///     reaper of docs/plan/06 § Two-phase create will consider it an orphan.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Three times the index lease, and the margin is what stops the reaper's evidence being
    ///     circular.</b> Step 1's claim carries a five-minute lease, so a create that is merely slow
    ///     has lost its name well before this — which is what makes "old, and the index is free" mean
    ///     the same thing as "died between steps 1 and 3". A tighter figure would have the reaper
    ///     reading an index whose lease might still be alive and calling that "no confirmed index".
    ///     <para>
    ///         ⚠ On the interface rather than on the grain, because <b>both</b> callers need it and
    ///         one of them cannot see the other: <c>ResourceGroupReclaimer</c> sweeps before it seals
    ///         a group, and it lives in <c>CyberCloud.ResourceManager</c>, which references these
    ///         contracts and not <c>CyberCloud.Tenancy</c>. Two spellings of the threshold would be
    ///         two definitions of what an orphan is.
    ///     </para>
    /// </remarks>
    static TimeSpan OrphanAge { get; } = TimeSpan.FromMinutes(15);

    /// <summary>Creates the group. Idempotent on the same region.</summary>
    /// <param name="tenantId">The owning tenant.</param>
    /// <param name="region">The region its resources default to.</param>
    Task<Result<ResourceGroupDescriptor>> CreateAsync(Guid tenantId, string region);

    /// <summary>The group's record, or <c>ResourceGroupNotFound</c>.</summary>
    Task<Result<ResourceGroupDescriptor>> GetAsync();

    /// <summary>Sets or clears the lock at this scope.</summary>
    /// <param name="level">The lock. <see cref="LockLevel.None" /> clears it.</param>
    /// <remarks>
    ///     ⚠ <b>Only the lock <i>at this scope</i>, and it is the middle link of the chain.</b>
    ///     docs/plan/06 § Tags, locks makes a lock "inherited down the hierarchy", so a lock set here
    ///     covers every resource in this group and is itself covered by the subscription's.
    ///     <c>ILockResolver</c> in the resource manager is what walks the three and takes the
    ///     strongest; this grain holds one link and knows nothing about the others.
    ///     <para>
    ///         ⚠ <b>Setting a lock does not refuse the delete of the group itself.</b> That is the
    ///         group lifecycle's business, and a lock read by the write path only covers the resources
    ///         inside it.
    ///     </para>
    /// </remarks>
    Task<Result> SetLockAsync(LockLevel level);

    /// <summary>
    ///     Step 2 of docs/plan/06 § Two-phase create, from the group's side: records the resource as
    ///     a member in <see cref="ProvisioningState.Creating" />.
    /// </summary>
    /// <param name="address">The resource's address. Its <c>Id</c> is the GUID being created.</param>
    /// <remarks>
    ///     Called <b>after</b> <c>IResourceIndexGrain.TryClaimAsync</c> and <b>before</b>
    ///     <c>ConfirmAsync</c>. A member left in <see cref="ProvisioningState.Creating" /> with no
    ///     confirmed index is the orphan docs/plan/06 § Two-phase create says "is swept by a
    ///     per-subscription reaper reminder" — <see cref="ListOrphansAsync" /> is what the reaper
    ///     reads.
    /// </remarks>
    Task<Result> BeginCreateAsync(ResourceId address);

    /// <summary>Step 4: the resource reached a terminal state.</summary>
    /// <param name="resourceId">The resource.</param>
    /// <param name="terminal">The terminal state.</param>
    Task<Result> CompleteCreateAsync(Guid resourceId, ProvisioningState terminal);

    /// <summary>
    ///     Delete, step 1: marks the resource <see cref="ProvisioningState.Deleting" />. It stays
    ///     listed.
    /// </summary>
    /// <param name="resourceId">The resource.</param>
    Task<Result> BeginDeleteAsync(Guid resourceId);

    /// <summary>
    ///     Delete, the failure path: teardown failed, so the member <b>stays</b>
    ///     <see cref="ProvisioningState.Deleting" /> and stays listed.
    /// </summary>
    /// <param name="resourceId">The resource.</param>
    /// <param name="failure">Why teardown failed.</param>
    /// <remarks>
    ///     ⚠ This method deliberately cannot remove the member and deliberately cannot move it to
    ///     <see cref="ProvisioningState.Failed" />. Both would make the resource look finished while
    ///     its pods still run and its meter still ticks, which docs/plan/06 § Two-phase create names
    ///     as "a billing-dispute prevention measure as much as a correctness one".
    /// </remarks>
    Task<Result> FailDeleteAsync(Guid resourceId, string failure);

    /// <summary>Delete, the last step: teardown succeeded, so the member goes.</summary>
    /// <param name="resourceId">The resource.</param>
    Task<Result> CompleteDeleteAsync(Guid resourceId);

    /// <summary>
    ///     Every member, <b>including</b> the ones in <see cref="ProvisioningState.Deleting" />.
    /// </summary>
    Task<Result<IReadOnlyList<ResourceGroupMember>>> ListAsync();

    /// <summary>
    ///     Members that have been <see cref="ProvisioningState.Creating" /> for longer than
    ///     <paramref name="olderThan" /> — what the reaper reminder of docs/plan/06 § Two-phase
    ///     create sweeps.
    /// </summary>
    /// <param name="olderThan">The age threshold.</param>
    Task<Result<IReadOnlyList<ResourceGroupMember>>> ListOrphansAsync(TimeSpan olderThan);

    /// <summary>
    ///     Removes the members that are orphans <b>and can be proved to be</b> — the reaper of
    ///     docs/plan/06 § Two-phase create, doing the sweeping rather than the listing.
    /// </summary>
    /// <param name="olderThan">
    ///     How long a member must have been <see cref="ProvisioningState.Creating" /> to be
    ///     considered. ⚠ Must comfortably exceed the index lease, or a create that is simply slow is
    ///     indistinguishable from one that died.
    /// </param>
    /// <returns>The members that were removed. Empty is the ordinary answer.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>AGE IS NOT EVIDENCE, AND THIS IS THE WHOLE DIFFERENCE BETWEEN THIS AND
    ///         <see cref="ListOrphansAsync" />.</b> That one answers "which members have been
    ///         <see cref="ProvisioningState.Creating" /> for a while", which is a question about a
    ///         clock. Removing a member is a claim that the resource does not exist, and the only
    ///         thing that can support it is the index: docs/plan/06 § Two-phase create defines the
    ///         orphan as <i>"durable state, no confirmed index"</i>. So each candidate's own
    ///         <c>IResourceIndexGrain</c> is read, and the member is removed <b>only</b> when the
    ///         name is free or bound to a different GUID. A <see cref="IndexEntryState.Confirmed" />
    ///         or <see cref="IndexEntryState.SoftDeleted" /> binding to this member's id means the
    ///         create got through step 3 and the resource is real — that member stays, however old
    ///         it is.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An index that cannot be read is a member that stays.</b> Removing on a failed
    ///         read would make an unreachable shard look like an empty platform, and the mistake is
    ///         the one docs/plan/06 § Two-phase create calls a billing-dispute prevention measure:
    ///         a resource that is silently gone from listings while its pods still run.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What it does NOT do, recorded rather than assumed: it does not clear the resource
    ///         grain's durable state.</b> This grain cannot see <c>IResourceGrain</c> —
    ///         <c>CyberCloud.Tenancy</c> holds no reference to the resource manager's contracts, and
    ///         reversing that edge is a module cycle. In practice the commonest orphan has no
    ///         resource-grain state at all, because step 7b of docs/plan/08 § The write path records
    ///         membership <i>before</i> step 9 writes the resource; what is left when it does exist
    ///         is state under a GUID no name resolves to. Clearing that belongs to the resource
    ///         manager and is owed.
    ///     </para>
    /// </remarks>
    Task<Result<IReadOnlyList<ResourceGroupMember>>> ReapOrphansAsync(TimeSpan olderThan);

    /// <summary>
    ///     Records that this group's objects have been placed on <paramref name="clusterId" />, so
    ///     that the group's own delete knows which namespaces are its.
    /// </summary>
    /// <param name="clusterId">The cluster a namespace was just written to.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>WITHOUT THIS THE GROUP DELETE HAS NOTHING TO ENUMERATE, AND THAT IS NOT OBVIOUS
    ///         UNTIL THE DELETE IS WRITTEN.</b> A namespace is keyed by (group, cluster) and a group
    ///         may hold resources on several clusters, so a group delete has to reclaim one namespace
    ///         per cluster the group ever touched. By the time the delete runs, every member is gone
    ///         — that is the precondition — so the members cannot say which clusters those were, and
    ///         nothing else in the control plane knows either. <c>NamespaceEnsurer</c>'s own remarks
    ///         rule out a platform-level controller for the same reason: the set of (group × cluster)
    ///         pairs "is not knowable from the control plane's own state".
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Written from the reconcile driver, and only when the namespace apply really
    ///         happened.</b> The driver is the one place that holds the group and a live connection
    ///         at once, and <c>NamespaceEnsurer</c>'s memo already bounds that to once per (cluster,
    ///         namespace) per hour per silo — so this costs one grain call an hour rather than one
    ///         per pass. A failure to record is <b>not</b> a failed pass: the consequence is a
    ///         namespace a later delete does not reclaim, which is the leak that existed anyway,
    ///         and refusing to place a tenant's resource over a bookkeeping write would be the worse
    ///         trade.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It is a set and it is never pruned.</b> A cluster that once held this group's
    ///         objects may hold its namespace still, so forgetting one is how a namespace outlives
    ///         every record of itself. The cardinality is the number of clusters a tenant has, which
    ///         is small and is not tenant-controllable at scale.
    ///     </para>
    /// </remarks>
    Task<Result> RecordClusterAsync(Guid clusterId);

    /// <summary>Every cluster this group is known to have placed objects on.</summary>
    /// <returns>
    ///     The clusters, in no particular order. ⚠ An <b>empty</b> list means "nothing was ever
    ///     recorded", which is not the same as "no namespaces exist" — a group whose resources were
    ///     placed before <see cref="RecordClusterAsync" /> existed reports empty and still has
    ///     namespaces. The group delete says so rather than reporting a clean reclaim.
    /// </returns>
    Task<Result<IReadOnlyList<Guid>>> ListClustersAsync();

    /// <summary>
    ///     Group delete, step 1: <b>seals</b> the group, so that nothing new can join it, and
    ///     refuses outright if it still holds members.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>SEALING IS THE ONLY THING THAT CLOSES THE CREATE-DURING-DELETE RACE, AND NOTHING
    ///         BELOW THE GROUP CAN DO IT.</b> <c>NamespaceReclaim</c> weighs evidence and then a
    ///         namespace is deleted; a resource created in between has its objects destroyed by a
    ///         verdict that was true when it was reached. The window cannot be closed by looking
    ///         harder — only by the group refusing to accept a member first. That is why this is a
    ///         method on this grain and not a flag the caller keeps.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The check and the seal are one grain turn, which is what makes the race actually
    ///         closed rather than narrowed.</b> An Orleans grain is single-threaded, so "no members,
    ///         therefore sealed" cannot be interleaved with a <see cref="BeginCreateAsync" />. A
    ///         caller that listed first and sealed second would have reopened exactly the window this
    ///         exists to shut.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>REFUSE, NOT CASCADE, and this was the open question.</b> Azure cascades: deleting
    ///         a resource group deletes everything in it. That is the better end state and it is not
    ///         what this does, because a cascade is a per-resource delete — twelve steps each,
    ///         including the resource's own lock, its own authorization, its own soft-delete window
    ///         and its own teardown that can fail — driven as one long-running operation with partial
    ///         failure to report. A cascade that skipped any of those would be a way to delete a
    ///         locked resource by deleting its group, which is a lock that does not hold. Refusing
    ///         costs a tenant one extra step and is reversible: the cascade can be built on top of
    ///         this, and it cannot be built on top of a group that has already been sealed
    ///         wrongly.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A sealed group STAYS sealed, including when the reclaim below it then refuses.</b>
    ///         It is the same rule <see cref="FailDeleteAsync" /> applies to a member: a delete that
    ///         began and did not finish stays visible and stays in
    ///         <see cref="ProvisioningState.Deleting" /> rather than being quietly returned to
    ///         service. The group is re-drivable — this method is idempotent — and a namespace whose
    ///         reclaim refuses is reported to an operator by
    ///         <c>NamespaceReclaim.OperatorReclaimable</c>.
    ///     </para>
    /// </remarks>
    Task<Result> BeginGroupDeleteAsync();

    /// <summary>
    ///     Group delete, the last step: removes the group's own record. Requires the group to be
    ///     sealed and empty.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>It refuses a group that was never sealed, and that refusal is the ordering.</b>
    ///     docs/plan/06 § Two-phase create in reverse is: seal the group, then the members, then the
    ///     namespace last. A caller that reached this without <see cref="BeginGroupDeleteAsync" />
    ///     has skipped the only step that closes the race, and it is refused rather than obeyed.
    ///     Idempotent on a group that is already gone: absence is the goal.
    /// </remarks>
    Task<Result> CompleteGroupDeleteAsync();

    /// <summary>Drops this activation — see <c>ITenantGrain.DeactivateAsync</c>.</summary>
    Task DeactivateAsync();
}
