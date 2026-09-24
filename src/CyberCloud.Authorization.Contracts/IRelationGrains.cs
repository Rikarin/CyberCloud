using CyberCloud.Core;
using Orleans.Concurrency;

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
public interface IObjectRelationsGrain : IGrainWithStringKey {
    /// <summary>Records <c>this#relation@subject</c>, with its expiry. Idempotent.</summary>
    /// <param name="relation">The relation.</param>
    /// <param name="subject">The subject.</param>
    /// <param name="expiresOn">
    ///     When the tuple stops granting, or <see langword="null" /> for a permanent one. A tuple
    ///     already present takes this expiry, whatever it had before — including losing one.
    /// </param>
    /// <returns>Whether anything stored changed: the tuple was new, or its expiry moved.</returns>
    Task<Result<bool>> WriteAsync(string relation, SubjectRef subject, DateTimeOffset? expiresOn);

    /// <summary>Removes <c>this#relation@subject</c>. Idempotent.</summary>
    /// <param name="relation">The relation.</param>
    /// <param name="subject">The subject.</param>
    /// <returns>Whether a tuple was removed.</returns>
    Task<Result<bool>> DeleteAsync(string relation, SubjectRef subject);

    /// <summary>Every live tuple on this object, with the expiry of each one that has one.</summary>
    /// <remarks>
    ///     ⚠ <b>A tuple whose expiry has passed isn't returned</b>, by this method or by any other
    ///     read here, from the instant it expires — whether or not the sweep has deleted it yet.
    ///     This is the one place the check path applies the clock, which is what lets
    ///     <c>Check</c> deny an expired grant with no write having happened. docs/plan/07
    ///     § Time-bounded relations.
    /// </remarks>
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
///         ⚠
///         <b>
///             This is what <c>ListObjects</c> reads, and the only thing on that path that
///             reads it is the walk.
///         </b> docs/plan/07 § Effort and sequencing put <c>ListObjects</c> at
///         M2 and this grain was built in M1 because the write path was two-grain from the start —
///         retrofitting a reverse index over tuples written without one means a backfill.
///         <see cref="IListObjectsGrain" /> starts from <see cref="ListAsync" /> for the subject —
///         and, since issue #37, from the subject's closed usersets in
///         <see cref="IMembershipIndexGrain" /> — and reads the same method for every userset and
///         every parent it reaches. So an entry missing here is exactly the miss the paragraph
///         above describes: the object is hidden from a listing until the sweeper replays the
///         write, and never shown to a caller who may not see it.
///     </para>
/// </remarks>
[Alias("CyberCloud.Authorization.ISubjectRelationsGrain")]
public interface ISubjectRelationsGrain : IGrainWithStringKey {
    /// <summary>Records that this subject appears in a tuple. Idempotent.</summary>
    /// <param name="entry">The entry.</param>
    /// <returns>Whether the entry was new.</returns>
    Task<Result<bool>> AddAsync(SubjectIndexEntry entry);

    /// <summary>Removes an entry. Idempotent.</summary>
    /// <param name="entry">The entry.</param>
    /// <returns>Whether an entry was removed.</returns>
    Task<Result<bool>> RemoveAsync(SubjectIndexEntry entry);

    /// <summary>Every entry whose tuple is still live.</summary>
    /// <remarks>
    ///     An entry whose <see cref="SubjectIndexEntry.ExpiresOn" /> has passed is left out, so a
    ///     listing drops an expired grant at the same instant a check does.
    /// </remarks>
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
///         ("tuple writes are rare (role assignments), checks are constant").
///         <b>
///             No check ever goes
///             through it
///         </b>
///         except to read the version on a cache miss and the cache fences at most once per
///         <see cref="TupleExpiry.ShorteningNotice" /> per check grain, and both are reads.
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
public interface ITupleStoreGrain : IGrainWithStringKey {
    /// <summary>
    ///     Writes a tuple to both grains, in order, and bumps the tenant's relation version.
    /// </summary>
    /// <param name="tuple">
    ///     The tuple. A <see cref="RelationTuple.ExpiresOn" /> must be later than now; writing a
    ///     tuple that's already present replaces its expiry, so a repeated write is how a grant is
    ///     extended, shortened, or made permanent. A shortening must end at least
    ///     <see cref="TupleExpiry.ShorteningNotice" /> from now, and writes a <see cref="CacheFence" />.
    /// </param>
    /// <returns>The token that covers the write — docs/plan/07 § Consistency.</returns>
    /// <remarks>
    ///     ⚠ A write with an expiry arms the store's sweep reminder before anything is journalled,
    ///     and fails if it can't: an expiring grant nothing will ever sweep or audit is refused
    ///     rather than written. docs/plan/07 § Time-bounded relations.
    /// </remarks>
    Task<Result<ConsistencyToken>> WriteAsync(RelationTuple tuple);

    /// <summary>Removes a tuple from both grains, in the same order, and bumps the version.</summary>
    /// <param name="tuple">The tuple. Its <see cref="RelationTuple.ExpiresOn" /> is ignored: a delete removes the tuple whatever expiry it has.</param>
    /// <returns>The token that covers the revoke.</returns>
    Task<Result<ConsistencyToken>> DeleteAsync(RelationTuple tuple);

    /// <summary>The tenant's current relation version, as a token.</summary>
    Task<Result<ConsistencyToken>> GetTokenAsync();

    /// <summary>
    ///     Returns every fence the check cache must still honour — the ends of grants that a rewrite
    ///     brought closer. docs/plan/07 § Time-bounded relations.
    /// </summary>
    /// <remarks>
    ///     ⚠ <see cref="AlwaysInterleaveAttribute" />, because a check grain calls this on a request
    ///     path and a tuple write is several durable writes long. It reads one list and writes
    ///     nothing. A rewrite adds its fence in the same turn that checks the notice, with no await
    ///     between, so a read that interleaves with the write either sees the fence or was served
    ///     before the notice began.
    /// </remarks>
    [AlwaysInterleave]
    Task<Result<IReadOnlyList<CacheFence>>> GetCacheFencesAsync();

    /// <summary>
    ///     Replays every journalled write whose second half did not land — docs/plan/07 § Storage's
    ///     sweeper.
    /// </summary>
    Task<Result<SweepReport>> SweepAsync();

    /// <summary>How many writes are journalled but not yet reconciled.</summary>
    Task<Result<int>> PendingCountAsync();

    /// <summary>
    ///     Replays the journal, then deletes every registered tuple whose expiry has passed and
    ///     audits each deletion — what the store's reminder does on every tick, run by hand.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Housekeeping, not enforcement.</b> An expired tuple has granted nothing since the
    ///         instant it expired, because every read drops it. The sweep takes it out of storage,
    ///         out of the reverse index and out of the membership index's unclosed marks, bumps the
    ///         relation version as any delete does, and writes the audit event that says when the
    ///         grant ended. A sweep that runs an hour late changes no check's answer.
    ///     </para>
    ///     <para>
    ///         The store registers every tuple written with an expiry — it's the tenant's single
    ///         writer, so the register is complete by construction — and holds a reminder while the
    ///         register or the journal is non-empty. docs/plan/07 § Time-bounded relations.
    ///     </para>
    /// </remarks>
    Task<Result<ExpirySweepReport>> SweepExpiredAsync();

    /// <summary>Drops this activation.</summary>
    Task DeactivateAsync();
}
