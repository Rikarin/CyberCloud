using CyberCloud.Core;

namespace CyberCloud.Authorization.Contracts;

/// <summary>
///     The Leopard index — docs/plan/07 § The Leopard index — for one subject object: the
///     transitively closed membership of every userset formed on it, and every userset it is
///     transitively in.
/// </summary>
/// <remarks>
///     <para>
///         <b>Kind</b> Index · <b>Tier</b> Durable · <b>Key</b> <c>rel/idx/{type}/{id}</c>,
///         tenant-qualified. Build it with <c>GrainKeys.MembershipIndex</c>. The object in the key
///         is a <b>subject object</b> — <c>group:eng</c>, <c>user:alice</c> — and one activation
///         holds both directions for it, which is the shape docs/plan/07 § The Leopard index's
///         status paragraph found the walk needs: "per subject, every userset it is in, closed" is
///         what <c>ListObjects</c> starts from, and "per userset, its members, closed" is what a
///         write against a userset has to consult to find the subjects it reaches.
///     </para>
///     <para>
///         ⚠ <b>Written by <see cref="ITupleStoreGrain" /> on every write and every delete, in the
///         same journalled sequence as the two indexes docs/plan/07 § Storage lists, and by nothing
///         else.</b> The document's index is fed by a stream and lags a write by "tens of
///         milliseconds"; this one is a step of the write itself, before the tenant's relation
///         version moves, so a token covers the index the way it covers the reverse half and a
///         check never has to compare versions to trust it. What that costs is stated on
///         <c>TupleStoreGrain</c>: an edge between two groups is propagated to every userset above
///         it and every member below it, one grain write each, in the write path.
///     </para>
///     <para>
///         ⚠ <b>The store orders the index differently for a write and for a delete, and the
///         difference is what keeps a crash fail-closed.</b> A write lands the index <i>last</i>, so a
///         crash before it leaves a grant the forward walk sees and the index does not — a deny
///         until the sweeper replays it. A delete lands the index <i>first</i>, so a crash after it
///         leaves a revoke the index honours and the forward half has not yet applied. Either way
///         the index is never more permissive than the tuples, which is the property that lets
///         <c>Check</c> take a <c>false</c> from it without walking.
///     </para>
///     <para>
///         ⚠ <b>Not in <c>durable-grains.txt</c>, by rationale rather than by oversight.</b> The
///         closure is derived from the two reviewed indexes and <see cref="RebuildAsync" /> derives
///         it again, so its loss tolerance is not the zero that list is reserved for. It binds
///         Durable rather than Hot because the store's journal guarantee — a token covers a write
///         that landed — is only true of state that persists with the halves it is journalled
///         beside; a Redis flush would leave the closure behind every token with nothing to replay.
///     </para>
/// </remarks>
[Alias("CyberCloud.Authorization.IMembershipIndexGrain")]
public interface IMembershipIndexGrain : IGrainWithStringKey {
    /// <summary>Both closures, as stored.</summary>
    Task<Result<MembershipIndexSnapshot>> ReadAsync();

    /// <summary>
    ///     Applies one write's or delete's share of the closure update to this slice. Idempotent.
    /// </summary>
    /// <param name="change">The unions, replacements and subtractions to apply.</param>
    /// <returns>Whether anything stored actually changed.</returns>
    Task<Result<bool>> ApplyAsync(MembershipIndexChange change);

    /// <summary>
    ///     Recomputes both closures from the tuples — the forward index for <see cref="MembershipIndexSnapshot.Members" />,
    ///     the reverse index for <see cref="MembershipIndexSnapshot.Usersets" /> — and replaces
    ///     what is stored.
    /// </summary>
    /// <remarks>
    ///     ⚠ Per slice only. Nothing enumerates a tenant's subject objects, so a schema change
    ///     that alters which relations are direct-only has no tenant-wide rebuild yet; docs/plan/07
    ///     § The Leopard index records that as owed.
    /// </remarks>
    Task<Result<MembershipIndexSnapshot>> RebuildAsync();

    /// <summary>Drops this activation.</summary>
    Task DeactivateAsync();
}
