namespace CyberCloud.ResourceManager.Contracts;

/// <summary>
///     One soft-deleted resource, as the registry records it.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>It carries no <c>RecoverableUntil</c>, and that absence is the decision.</b>
///         <c>IResourceIndexGrain.SoftDeleteAsync</c> takes a <i>duration</i> rather than a deadline
///         precisely so that one activation both stamps and reads the window — see the remarks on
///         <c>ResourceManagerService.PurgeExpiredAsync</c>, which refuse to compare
///         <c>RecoverableUntil</c> against this process's clock on "the single path where being early
///         destroys a resource somebody could still have restored". A copy of the deadline here would
///         be a second opinion about it, held by a grain that does not own it and cannot notice when
///         it changes. A listing that wants to show the deadline reads it from each entry's own index
///         grain, which is what <c>IResourceGroupGrain.ReapOrphansAsync</c> already does with each
///         candidate's index and for the same reason: age is not evidence, the index is.
///     </para>
///     <para>
///         <see cref="ParkedAt" /> is <i>this</i> grain's own fact and not a copy of anybody's — the
///         moment the registry recorded the entry, stamped by the registry's clock. It is what orders
///         a listing and what tells an operator how long ago the delete happened; it is not the
///         window and must never be arithmetic'd into one.
///     </para>
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.ResourceManager.ParkedResource")]
public sealed record ParkedResource {
    /// <summary>
    ///     The resource's address, as <c>ResourceId.Path</c> renders it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A PATH AND A GUID RATHER THAN A <c>ResourceId</c>, AND THAT IS A STORAGE
    ///         CONSTRAINT BEFORE IT IS A STYLE.</b> This record is the registry's grain <i>state</i>
    ///         as well as its wire type. A <c>ResourceId</c> crosses a grain <i>call</i> perfectly
    ///         well — <c>ResourceIdSurrogate</c> is what makes that true — but grain state goes
    ///         through <c>IGrainStorageSerializer</c>, which is JSON, and JSON serialises every public
    ///         getter it can see. A <c>ResourceId</c> reachable from this record, whether as an
    ///         <c>[Id]</c> member or merely as a computed property, therefore ends up being walked by
    ///         a serializer that cannot do anything sensible with it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Measured rather than reasoned about, and both shapes were tried.</b> With
    ///         <c>[Id(0)] ResourceId Address</c> the first state write threw
    ///         <c>JsonSerializationException</c>; replacing the member with a path and a GUID but
    ///         keeping an <c>Address</c> <i>property</i> threw exactly the same thing, because the
    ///         getter is still a getter. Removing the property fixed it. The failure surfaced as
    ///         twenty-four soft-delete cases at once and named a codec rather than a property, because
    ///         the exception could not itself be serialized back across the grain call — so the shape
    ///         of the report says nothing about the shape of the cause, which is the reason it is
    ///         written down here.
    ///     </para>
    ///     <para>
    ///         The whole tree already had the answer and this is the rule rather than the exception:
    ///         no shipping durable state holds a <c>ResourceId</c>. <c>OperationSpec</c> carries
    ///         <c>ResourcePath</c> and <c>ResourceId</c> as a string and a GUID, and
    ///         <c>ResourceGroupMember</c> carries a canonical path and a GUID. <c>OperationGrain</c>'s
    ///         own <c>Address</c> helper says why that is right and not merely necessary: the path is
    ///         what the record durably holds, and a second copy of the resource group beside it would
    ///         be a second thing that can disagree with it.
    ///     </para>
    /// </remarks>
    [Id(0)]
    public string Path { get; init; } = string.Empty;

    /// <summary>The resource's GUID.</summary>
    /// <remarks>
    ///     ⚠ It is what makes the entry actionable: what
    ///     <c>IResourceIndexGrain.ResolveSoftDeletedAsync</c> answers with, what a restore and a purge
    ///     address the resource grain by, and what
    ///     <see cref="IParkedResourceRegistryGrain.UnparkAsync" /> takes. An entry holding a name and
    ///     no GUID would be a listing with nothing to act on, which is the state the registry exists
    ///     to end.
    /// </remarks>
    [Id(1)]
    public Guid ResourceId { get; init; }

    /// <summary>When the registry recorded this entry.</summary>
    [Id(2)]
    public DateTimeOffset ParkedAt { get; init; }

    /// <summary>
    ///     The address as a parsed <c>ResourceId</c>, with <see cref="ResourceId" /> filled in.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A METHOD AND NOT A PROPERTY, WHICH IS THE WHOLE OF WHY THIS RECORD PERSISTS.</b>
    ///         The JSON grain-storage serializer walks public getters and does not call methods, so
    ///         the derivation is reachable to every caller and invisible to storage. It reads a
    ///         little worse than <c>entry.Address</c> and it is the difference between a registry that
    ///         writes and one that throws — see the remarks on <see cref="Path" /> for the two shapes
    ///         that were tried before this one.
    ///     </para>
    ///     <para>
    ///         ⚠ Re-parsed rather than stored, with the failure mode <c>OperationGrain.Address</c>
    ///         documents: <see cref="Path" /> was produced by <c>ResourceId.Path</c> and is parsed by
    ///         its inverse, so a failure is unreachable — and if it ever were reachable,
    ///         <c>default</c> carries <see cref="Guid.Empty" />, which every writer in this codebase
    ///         refuses rather than acts on.
    ///     </para>
    /// </remarks>
    public ResourceId AddressOf() {
        var parsed = Core.Resources.ResourceId.ParsePath(Path);
        return parsed.IsSuccess ? parsed.GetValueOrThrow().WithId(ResourceId) : default;
    }
}

/// <summary>
///     A resource group's registry of the resources that are inside their soft-delete recovery
///     window — the second place to look, which docs/plan/08 § Soft delete says the platform has and
///     did not.
/// </summary>
/// <remarks>
///     <para>
///         <b>Kind</b> Entity · <b>Tier</b> Durable · <b>Key</b>
///         <c>parked/{subscriptionId:N}/rg/{name}</c>, tenant-qualified. Build it with
///         <c>GrainKeys.ParkedResourceRegistry</c>.
///     </para>
///     <para>
///         ⚠ <b>THIS EXISTS BECAUSE THE FILTER HAD AN EMPTY INPUT, NOT BECAUSE THE FILTER WAS
///         MISSING.</b> docs/plan/08 § Soft delete predicted that "the soft-deleted collection is one
///         filter over whatever answers" a listing, and <c>IResourceManager.ListAsync</c> then landed
///         and made that testable — and false. A parked resource is <b>not</b> a member of its
///         resource group: <c>OperationGrain.ParkAsync</c> calls the <i>group's</i>
///         <c>CompleteDeleteAsync</c> deliberately, because a member left behind would put a name into
///         a listing whose every read is the canonical <c>404</c>, handing a caller who may list the
///         group but may not read the resource the "something is held here" signal § Soft delete
///         refuses a <c>410 Gone</c> over. ⚠ <b>That decision is right and is not the one this
///         reverses.</b> What was missing is a second collection, and this is it.
///     </para>
///     <para>
///         ⚠ <b>Three writes, at three call sites that already existed — and two more that re-ask
///         the index.</b> Written by <c>OperationGrain.ParkAsync</c> where the soft delete unlists the
///         member; cleared by <c>ResourceManagerService.RestoreAsync</c> where the restore puts it
///         back; cleared by <c>ResourceManagerService.PurgeCoreAsync</c> where the purge releases the
///         name. The other two are <c>ResourceManagerService.RepairParkedRegistryAsync</c>, which
///         re-parks, and <c>ExpirySweeperGrain.SweepAsync</c>, which unparks — see the paragraph
///         below for what makes both legitimate.
///     </para>
///     <para>
///         ⚠ <b>THE RULE IS NOT "NO FOURTH WRITER". IT IS "NO WRITER THAT DOES NOT RE-ASK THE
///         INDEX".</b> This paragraph said the first of those until 2026-09-05, and it forbade the
///         code that had to be written: a restore can be refused <i>permanently</i> — by a recovery
///         window that has passed — at a point below the clear, and an entry cleared for a restore
///         that then never happens is gone for good, because the delete operation that would re-park
///         it has long since terminated. So <c>ResourceManagerService.RepairParkedRegistryAsync</c> is
///         a fourth caller of <see cref="ParkAsync" />, and it is legitimate for exactly the reason
///         the old rule was groping at: it re-parks <i>only</i> while the index still says
///         <see cref="IndexEntryState.SoftDeleted" /> of the same resource GUID, so it makes no claim
///         of its own. An entry that appeared from anywhere else — from a writer that did not ask —
///         would be a claim about a recovery window made by something that does not hold one.
///     </para>
///     <para>
///         ⚠ <b>And the rule reads the same way in the other direction, which is what
///         <c>ExpirySweeperGrain.SweepAsync</c> is (2026-09-05, issue #12).</b> It is a <i>fifth</i>
///         writer and it only ever <see cref="UnparkAsync" />s, and it does so only for an entry the
///         index has just told it is false — not <see cref="IndexEntryState.SoftDeleted" />, or
///         soft-deleted as a <i>different</i> resource GUID. That is this type's invariant read as a
///         removal rule rather than as an ordering rule, and it is the first thing in the tree that
///         can correct a registry that has gone <b>long</b>. Before it, a long entry was permanent,
///         because nothing could address the resource again;
///         <c>ResourceManagerService.RepairParkedRegistryAsync</c>'s "NARROWED, NOT CLOSED" remarks
///         are where that mattered and where it is now written down.
///     </para>
///     <para>
///         ⚠ <b>THE INVARIANT, WHICH IS WHAT FIXES THE ORDER OF ALL THREE:
///         <i>an entry exists only while the index says <see cref="IndexEntryState.SoftDeleted" /></i>.</b>
///         So the park is written <i>after</i> the index was parked and <i>before</i> the group's
///         member is removed, and both clears run <i>before</i> the index write that stops the entry
///         being true. Every crash window therefore leaves the registry <b>under</b>-reporting rather
///         than over-reporting, and the direction matters: under-reporting hides a resource that is
///         still restorable by its own path and is repaired by re-driving the operation that was
///         interrupted, while over-reporting offers a caller a restore that answers <c>404</c> and —
///         worse — tells a caller who may list this collection but may not read the resource that the
///         name is held, which is the enumeration oracle the whole design is built around refusing.
///     </para>
///     <para>
///         ⚠ <b>It is not the group's membership and must never be merged into it.</b> docs/plan/08
///         § Soft delete: the two collections "answer different questions to different callers, and
///         merging them is exactly the <c>410 Gone</c> the decision above refuses". They are two
///         grains with two keys for that reason and for no other.
///     </para>
///     <para>
///         ⚠ <b>Nothing here authorizes anything.</b> This grain answers what is parked; who may see
///         it is the caller's question, decided at docs/plan/07 § The enforcement seam by a
///         <c>Check</c> per entry, exactly as <c>IResourceManager.ListAsync</c> does per member. A
///         registry that filtered would be a second, weaker copy of that seam in a grain that cannot
///         see a caller.
///     </para>
///     <para>
///         <b>Cardinality and growth.</b> One activation per resource group, holding at most the
///         resources that group has ever had, which is bounded by the subscription's quota
///         (docs/plan/06 § Quota) exactly as the group's own membership is. Entries leave at the
///         restore or at the purge; an entry whose window has passed and whose purge has not run
///         used to stay indefinitely, and that is the state <c>IExpirySweeperGrain</c> now ends —
///         armed by <see cref="ParkAsync" />'s two callers and reading <see cref="ListAsync" /> on a
///         clock, which this listing is what made possible.
///     </para>
/// </remarks>
[Alias("CyberCloud.ResourceManager.IParkedResourceRegistryGrain")]
public interface IParkedResourceRegistryGrain : IGrainWithStringKey {
    /// <summary>
    ///     Records that <paramref name="address" /> is inside its recovery window.
    /// </summary>
    /// <param name="address">
    ///     The resource, with its GUID resolved and its resource group matching this grain's key.
    /// </param>
    /// <returns>
    ///     Success, or <see cref="ErrorCode.InvalidResourceId" /> when the address is unresolved or
    ///     names a different resource group.
    /// </returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Idempotent, and a re-park does <i>not</i> restamp <see cref="ParkedResource.ParkedAt" />.</b>
    ///         The caller is <c>OperationGrain.ParkAsync</c>, which is re-driven from a durable
    ///         reminder, so a second call is the ordinary path rather than an error. Restamping would
    ///         make "when was this deleted" move every time a retry ran — the same rule, for the same
    ///         reason, as <c>IResourceIndexGrain.SoftDeleteAsync</c> not restamping its deadline on a
    ///         re-drive.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The clock is this grain's, and the caller does not get to supply the time.</b> One
    ///         activation stamps and reads, so two entries in one group are always comparable; a
    ///         caller-supplied timestamp would make the ordering of a listing depend on which silo
    ///         happened to run each delete.
    ///     </para>
    /// </remarks>
    Task<Result> ParkAsync(ResourceId address);

    /// <summary>
    ///     Removes the entry for <paramref name="resourceId" />, because its window has ended one way
    ///     or the other.
    /// </summary>
    /// <param name="resourceId">The resource's GUID.</param>
    /// <returns>Success, including when there was no entry.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Absence is the goal, so removing what is not there succeeds.</b> Both callers can
    ///         be retried — a restore that failed after this point is repeated by the caller, and a
    ///         purge is re-driven — and a second call that failed would turn a converging operation
    ///         into a stuck one over a row that is already in the state it was asked to reach.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>One method for both endings, and the ending is deliberately not recorded here.</b>
    ///         A restore and a purge leave the same registry — nothing recoverable at this address —
    ///         and which of them happened is on the operation record and in the audit log, where a
    ///         reader can also see who asked and when. A "reason" column on a row that is being
    ///         deleted would be written and never read.
    ///     </para>
    /// </remarks>
    Task<Result> UnparkAsync(Guid resourceId);

    /// <summary>
    ///     Everything parked in this resource group, ordered by canonical path, ordinally.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Unfiltered and unpaged, like <c>IResourceGroupGrain.ListAsync</c> beside it, and for
    ///     the same reason: this is the grain's own inventory rather than an endpoint.</b> The paging
    ///     and the per-entry <c>Check</c> belong to whatever serves a collection <c>GET</c> over it —
    ///     <c>ListRequest.MaxPageSize</c> is where the cost of one request stops being a number the
    ///     caller chooses.
    /// </remarks>
    Task<Result<IReadOnlyList<ParkedResource>>> ListAsync();

    /// <summary>
    ///     What is recoverable in this group, <b>of one type</b> — the question docs/plan/08 § Soft
    ///     delete says is "expressible today".
    /// </summary>
    /// <param name="collection">
    ///     The collection to filter to. Its tenant, subscription and resource group must be this
    ///     grain's.
    /// </param>
    /// <returns>
    ///     The matching entries, ordered as <see cref="ListAsync" /> orders them, or
    ///     <see cref="ErrorCode.InvalidResourceId" /> when the collection names a different group.
    /// </returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A <c>ResourceCollectionId</c> and not a <c>ResourceTypeName</c>, because a nested
    ///         type is not a type on its own.</b> A collection of <c>servers/databases</c> is
    ///         addressed <c>…/servers/{serverName}/databases</c> and its ancestor's name is part of
    ///         the question — two servers in one group have two database collections. Taking the type
    ///         alone would answer both at once, and the caller would have no way to say which it
    ///         meant.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Resource-group-scoped and nothing wider, which is a boundary rather than a
    ///         limitation to be lifted in passing.</b> docs/plan/08 § Soft delete: anything
    ///         subscription-wide "is still the addressing question, because <c>ResourceId.ParsePath</c>
    ///         has <c>const int fixedPrefix = 8</c> and no subscription-scoped shape". A registry
    ///         method that took a subscription would have no address a caller could ask for and would
    ///         settle that question by implication.
    ///     </para>
    /// </remarks>
    Task<Result<IReadOnlyList<ParkedResource>>> ListOfTypeAsync(ResourceCollectionId collection);

    /// <summary>Drops this activation — see <c>ITenantGrain.DeactivateAsync</c>.</summary>
    Task DeactivateAsync();
}
