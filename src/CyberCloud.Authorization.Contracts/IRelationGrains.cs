using CyberCloud.Core;

namespace CyberCloud.Authorization.Contracts;

/// <summary>
///     Every tuple whose <b>object</b> is this one — docs/plan/07 § Storage, row 1.
/// </summary>
/// <remarks>
///     <para>
///         <b>Kind</b> Entity · <b>Tier</b> Durable · <b>Key</b> <c>rel/obj/{type}/{id}</c>,
///         tenant-qualified. Build it with <c>GrainKeys.ObjectRelations</c>.
///     </para>
///     <para>
///         ⚠ <b>This grain is the authority and the forward direction.</b> docs/plan/07 § Storage:
///         "A subject index missing an entry costs a <c>ListObjects</c> a miss, not a <c>Check</c> an
///         incorrect answer, because <c>Check</c> walks forward from the object." Every read on the
///         check path lands here and nowhere else, which is what makes that sentence true — and
///         <c>TwoGrainWriteTests</c> is what makes it <i>tested</i> rather than asserted.
///     </para>
///     <para>
///         ⚠ <b>Write through <see cref="ITupleStoreGrain" />, not through this.</b> The methods here
///         are the primitive halves of the two-grain write and are public because the sweeper and
///         the store grain both need them — a caller that uses them directly writes a tuple the
///         reverse index will never learn about and does not bump the tenant's relation version, so
///         no token covers it and no cache is invalidated.
///     </para>
/// </remarks>
[Alias("CyberCloud.Authorization.IObjectRelationsGrain")]
public interface IObjectRelationsGrain : IGrainWithStringKey
{
    /// <summary>Records <c>this#relation@subject</c>. Idempotent.</summary>
    /// <param name="relation">The relation.</param>
    /// <param name="subject">The subject.</param>
    /// <returns>Whether the tuple was new.</returns>
    Task<Result<bool>> WriteAsync(string relation, SubjectRef subject);

    /// <summary>Removes <c>this#relation@subject</c>. Idempotent.</summary>
    /// <param name="relation">The relation.</param>
    /// <param name="subject">The subject.</param>
    /// <returns>Whether a tuple was removed.</returns>
    Task<Result<bool>> DeleteAsync(string relation, SubjectRef subject);

    /// <summary>Every tuple on this object.</summary>
    Task<Result<ObjectRelationsSnapshot>> ReadAsync();

    /// <summary>
    ///     Every tuple on this object, re-read from the durable row first.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This is what <c>ConsistencyMode.FullyConsistent</c> means by "read durable"</b>
    ///     (docs/plan/07 § Consistency, row 3). An activation's in-memory state is normally
    ///     authoritative — it is the single writer — so this call is not about correctness against
    ///     our own writes. It is about the case the row was changed by something that is not this
    ///     activation: a restore, a repair tool, a migration, or a split brain during a silo
    ///     handover. On the destructive paths that ask for this mode, paying a durable read to rule
    ///     that out is the trade the document makes.
    /// </remarks>
    Task<Result<ObjectRelationsSnapshot>> ReadDurableAsync();

    /// <summary>
    ///     The Azure-shaped role assignments written <b>at this scope</b> — docs/plan/07 § Azure
    ///     RBAC, expressed in it.
    /// </summary>
    /// <param name="roles">The relation names that are roles.</param>
    /// <remarks>
    ///     Direct only. The inherited half is <see cref="ICheckGrain.ListRoleAssignmentsAsync" />,
    ///     because inheritance is a walk and a walk belongs where the walk lives.
    /// </remarks>
    Task<Result<IReadOnlyList<RoleAssignment>>> ListRoleAssignmentsAsync(IReadOnlyList<string> roles);

    /// <summary>Drops this activation — see <c>ITenantGrain.DeactivateAsync</c>.</summary>
    Task DeactivateAsync();
}

/// <summary>
///     Every tuple whose <b>subject</b> is this one — the reverse index, docs/plan/07 § Storage,
///     row 2.
/// </summary>
/// <remarks>
///     <para>
///         <b>Kind</b> Index · <b>Tier</b> Durable · <b>Key</b> <c>rel/sub/{type}/{id}</c>,
///         tenant-qualified.
///     </para>
///     <para>
///         ⚠ <b>This is the half that is allowed to be stale, and that asymmetry is the point.</b>
///         docs/plan/07 § Storage: "the direction that can be stale is the one where staleness is a
///         performance bug, not a security bug". Nothing on the check path reads this grain — see
///         <c>TwoGrainWriteTests</c>, which interrupts a write between the two and asserts
///         <c>Check</c> is still correct.
///     </para>
///     <para>
///         ⚠ <b>M1 has no <c>ListObjects</c>.</b> docs/plan/07 § Effort and sequencing puts it at
///         M2. This grain is built now because the write path is two-grain now, and retrofitting a
///         reverse index over tuples written without one means a backfill. <see cref="ListAsync" />
///         is the seam <c>ListObjects</c> will start from; there is no reverse rewrite walk yet.
///     </para>
/// </remarks>
[Alias("CyberCloud.Authorization.ISubjectRelationsGrain")]
public interface ISubjectRelationsGrain : IGrainWithStringKey
{
    /// <summary>Records that this subject appears in a tuple. Idempotent.</summary>
    /// <param name="entry">The entry.</param>
    /// <returns>Whether the entry was new.</returns>
    Task<Result<bool>> AddAsync(SubjectIndexEntry entry);

    /// <summary>Removes an entry. Idempotent.</summary>
    /// <param name="entry">The entry.</param>
    /// <returns>Whether an entry was removed.</returns>
    Task<Result<bool>> RemoveAsync(SubjectIndexEntry entry);

    /// <summary>Every entry.</summary>
    Task<Result<IReadOnlyList<SubjectIndexEntry>>> ListAsync();

    /// <summary>Drops this activation — the seam the interruption test uses.</summary>
    Task DeactivateAsync();
}

/// <summary>
///     The tenant's tuple writer, relation version and reconciliation sweeper.
/// </summary>
/// <remarks>
///     <para>
///         <b>Kind</b> Coordinator · <b>Tier</b> Durable · <b>Key</b>
///         <c>rel/store/{tenantId:N}</c>, tenant-qualified. ⚠ <b>Not a row in docs/plan/07</b> — see
///         <c>GrainKeys.TupleStore</c> for why it exists.
///     </para>
///     <para>
///         <b>Why one grain per tenant.</b> Two of the document's requirements are per tenant and
///         need a single writer: "a per-tenant monotonic version returned by every tuple write"
///         (§ Consistency) and the ordering of the two-grain write (§ Storage). docs/plan/04 § Grain
///         taxonomy's cardinality question applies and the answer is the same as for quota: this
///         grain serialises <i>tuple writes</i>, which § Caching across requests itself calls rare
///         ("tuple writes are rare (role assignments), checks are constant"). <b>No check ever goes
///         through it</b> except to read the version, which is a read.
///     </para>
///     <para>
///         ⚠ <b>The write is not transactional and the journal is what makes the sweeper possible.</b>
///         docs/plan/07 § Storage names the ordering and the sweeper and does not say how the
///         sweeper knows what to sweep — grains cannot be scanned. So a write is journalled durably
///         here first, then applied object-first and subject-second, then cleared. A crash anywhere
///         leaves an entry in the journal and <see cref="SweepAsync" /> replays it; replay is
///         idempotent because both halves are.
///     </para>
/// </remarks>
[Alias("CyberCloud.Authorization.ITupleStoreGrain")]
public interface ITupleStoreGrain : IGrainWithStringKey
{
    /// <summary>
    ///     Writes a tuple to both grains, in order, and bumps the tenant's relation version.
    /// </summary>
    /// <param name="tuple">The tuple.</param>
    /// <returns>The token that covers the write — docs/plan/07 § Consistency.</returns>
    Task<Result<ConsistencyToken>> WriteAsync(RelationTuple tuple);

    /// <summary>Removes a tuple from both grains, in the same order, and bumps the version.</summary>
    /// <param name="tuple">The tuple.</param>
    /// <returns>The token that covers the revoke.</returns>
    Task<Result<ConsistencyToken>> DeleteAsync(RelationTuple tuple);

    /// <summary>The tenant's current relation version, as a token.</summary>
    Task<Result<ConsistencyToken>> GetTokenAsync();

    /// <summary>
    ///     Replays every journalled write whose second half did not land — docs/plan/07 § Storage's
    ///     sweeper.
    /// </summary>
    Task<Result<SweepReport>> SweepAsync();

    /// <summary>How many writes are journalled but not yet reconciled.</summary>
    Task<Result<int>> PendingCountAsync();

    /// <summary>Drops this activation.</summary>
    Task DeactivateAsync();
}
