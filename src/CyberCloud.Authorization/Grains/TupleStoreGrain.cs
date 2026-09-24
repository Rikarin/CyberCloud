using CyberCloud.Authorization.Contracts;
using CyberCloud.Authorization.Evaluation;
using CyberCloud.Core;
using CyberCloud.Core.Contracts;
using CyberCloud.Core.Resources;
using CyberCloud.Core.Time;
using Microsoft.Extensions.Logging;
using Orleans.Multitenant;
using System.Globalization;

namespace CyberCloud.Authorization.Grains;

/// <summary>
///     <see cref="ITupleStoreGrain" /> — Coordinator, Durable, key <c>rel/store/{tenantId:N}</c>.
/// </summary>
/// <remarks>
///     <para>
///         <b>
///             The write, in the order docs/plan/07 § Storage requires, with the two additions that
///             make the sweeper possible and the Leopard index a step rather than a stream:
///         </b>
///     </para>
///     <list type="number">
///         <item>
///             journal the tuple durably, here, with a <see cref="CacheFence" /> in the same write
///             when it brings a live grant's end closer;
///         </item>
///         <item>
///             <b>for a delete</b>, and <b>for a write that changes an existing edge's expiry</b>,
///             update <c>IMembershipIndexGrain</c> — every slice the edge could have reached,
///             recomputed as if the tuple were already gone;
///         </item>
///         <item>write <c>IObjectRelationsGrain</c> — <b>the half <c>Check</c> reads</b>;</item>
///         <item>
///             <see cref="IRelationWriteInterceptor" /> — the seam a test uses to kill the write
///             exactly here;
///         </item>
///         <item>write <c>ISubjectRelationsGrain</c> — the half only <c>ListObjects</c> reads;</item>
///         <item>
///             <b>for a write only</b>, update <c>IMembershipIndexGrain</c> — the two unions of
///             <see cref="MembershipIndexMaintainer" />;
///         </item>
///         <item>
///             clear the journal entry, update the expiry register, and bump the tenant relation
///             version, durably and in one write.
///         </item>
///     </list>
///     <para>
///         ⚠ <b>The version is bumped last, and that ordering is the point of the token.</b> A crash
///         anywhere before step 7 means no token was ever handed out, so nothing can be waiting on
///         a version that covers a write which did not finish. A token, once returned, always
///         covers a write that landed in every grain, the index included — which is why the index
///         needs no version of its own for docs/plan/07 § The Leopard index's staleness rule.
///     </para>
///     <para>
///         ⚠
///         <b>
///             The index lands last on a write and first on a delete, and the asymmetry is the
///             whole safety argument for letting <c>Check</c> read it.
///         </b> docs/plan/07 § Storage lets
///         the reverse index be stale because nothing on the check path reads it; the Leopard index
///         <i>is</i> read on the check path, and a <c>false</c> from it is taken without a walk. So
///         at every point a crash can leave the tenant, the index must be no more permissive than
///         the forward half. A write that dies before step 6 leaves a grant the walk sees and the
///         index does not — a deny, until <see cref="SweepAsync" /> replays it. A delete that dies
///         after step 2 leaves a revoke the index honours and the forward half has not applied —
///         also a deny. The order that would break it, forward delete before index update, is the
///         one crash that could leave a revoked membership answering <c>true</c>. A rewrite that
///         turns a permanent edge into an expiring one is the same crash in a new shape, which is
///         why it pays step 2 as well: the closure loses the edge's members before the forward half
///         says the edge will stop granting.
///     </para>
///     <para>
///         ⚠
///         <b>
///             Four durable writes per tuple, plus one per index slice the tuple reaches, and that
///             is affordable exactly because docs/plan/07 § Caching across requests says so:
///         </b>
///         "tuple writes are rare (role assignments), checks
///         are constant". If that ever stops being true, the fix is batching here, not dropping the
///         journal — without it the sweeper has nothing to sweep, because grains cannot be scanned.
///         The index's share is the fan-out docs/plan/07 § The Leopard index's threshold paragraph
///         exists to cap: a group-to-group edge writes one slice per userset above it and one per
///         member below it, and <c>AuthorizationMetrics.IndexWrites</c> is how big that has been.
///     </para>
///     <para>
///         ⚠
///         <b>
///             The expiry sweep is this grain's reminder and not a grain of its own, and there's
///             no cycle to break.
///         </b> <c>IExpirySweeperGrain</c> is a grain of its own because a purge calls back into
///         the registry that would have held its reminder. A tuple delete calls the object, subject
///         and index grains and never this one, so the reminder can live beside the register it
///         walks, and the sweep's deletes run through the same seven steps as a revoke — the same
///         journal, the same index order, the same version bump — rather than through a second
///         implementation nobody watches. The reminder is armed off "the register or the journal
///         is non-empty" and never off a deadline, for the reason docs/plan/07 § Azure RBAC, expressed in it gives:
///         a reminder whose due time was an expiry would be a second durable copy of it.
///     </para>
/// </remarks>
public sealed class TupleStoreGrain(
    [PersistentState("tuples", StorageTiers.Durable)]
    IPersistentState<TupleStoreState> state,
    AuthorizationSchema schema,
    IRelationWriteInterceptor interceptor,
    IClock clock,
    ILogger<TupleStoreGrain> logger
)
    : Grain, ITupleStoreGrain, IRemindable {
    /// <summary>The reminder that drives <see cref="SweepExpiredAsync" />.</summary>
    public const string SweepReminderName = "sweep-expired-tuples";

    /// <summary>
    ///     How often the sweep runs while anything is registered. An expired tuple grants nothing
    ///     from the instant it expires, so this bounds how late the audit event and the storage
    ///     cleanup are, never how long a grant lasts.
    /// </summary>
    public static readonly TimeSpan SweepPeriod = TimeSpan.FromMinutes(5);

    Guid tenantId;

    /// <inheritdoc />
    public override Task OnActivateAsync(CancellationToken cancellationToken) {
        tenantId = AuthorizationGrainKeys.TenantOf(this);
        var key = AuthorizationGrainKeys.Decode(this, GrainKeyKind.TupleStore);

        if (key.Id != tenantId) {
            throw new InvalidOperationException(
                $"TupleStoreGrain was activated for tenant {tenantId:D} with the key "
                + $"'{GrainKeys.TupleStore(key.Id)}', which names tenant {key.Id:D}. The two halves "
                + "of the key must agree, or one tenant's relation version would be another "
                + "tenant's."
            );
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<Result<ConsistencyToken>> WriteAsync(RelationTuple tuple) => ApplyAsync(tuple, false);

    /// <inheritdoc />
    public Task<Result<ConsistencyToken>> DeleteAsync(RelationTuple tuple) => ApplyAsync(tuple, true);

    /// <inheritdoc />
    public Task<Result<ConsistencyToken>> GetTokenAsync() => Task.FromResult(Result<ConsistencyToken>.Success(Token()));

    /// <inheritdoc />
    public Task<Result<IReadOnlyList<CacheFence>>> GetCacheFencesAsync() =>
        Task.FromResult(Result<IReadOnlyList<CacheFence>>.Success([.. state.State.Fences]));

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Only the latest entry for a tuple is replayed, and the older ones are dropped.</b>
    ///     The journal holds intents, and a later write or delete of the same tuple is a later
    ///     intent: replaying an earlier one over it would resurrect a revoked grant, or turn a
    ///     shortened grant back into a permanent one that the register then forgets — with no
    ///     sweep and no audit event for its end. Replaying the latest entry is enough on its own,
    ///     because every replay runs steps 2 to 6 in full, and so undoes whatever an older entry's
    ///     crash left half-applied. <see cref="ApplyAsync" /> drops superseded entries the same
    ///     way when a write succeeds. <c>TimeBoundedRelationTests</c> has a test for each case.
    /// </remarks>
    public async Task<Result<SweepReport>> SweepAsync() {
        var pending = state.State.Pending.OrderBy(static x => x.Sequence).ToList();
        if (pending.Count == 0) {
            return Result<SweepReport>.Success(new());
        }

        // A tuple without its expiry is its identity (RelationTuple.IsSameTupleAs), and the walk is
        // in sequence order, so the last entry stored under a key is that tuple's latest.
        var byTuple = new Dictionary<RelationTuple, PendingWrite>();
        foreach (var entry in pending) {
            byTuple[entry.Tuple with { ExpiresOn = null }] = entry;
        }

        var latest = byTuple.Values.OrderBy(static x => x.Sequence).ToList();

        var superseded = pending.Count - latest.Count;
        if (superseded > 0) {
            var kept = latest.Select(static x => x.Sequence).ToHashSet();
            state.State.Pending.RemoveAll(x => !kept.Contains(x.Sequence));
        }

        // ⚠ A replayed expiring write may be a shortening that never landed, and the answers cached
        // since were proved by the grant it replaces, at versions past the fence its first attempt
        // wrote. So each one gets a fence at this version too, durably, before its forward half can
        // land. The notice can't be enforced here: the end was checked when the write was first
        // made, and a replay that runs later is that much closer to it.
        var fenced = false;
        foreach (var entry in latest.Where(static x => !x.IsDelete && x.Tuple.ExpiresOn is not null)) {
            AddFence(entry.Tuple.ExpiresOn!.Value);
            fenced = true;
        }

        if (fenced) {
            await state.WriteStateAsync();
        }

        var repaired = 0;

        foreach (var entry in latest) {
            var applied = await ApplyBothHalvesAsync(entry.Tuple, entry.IsDelete, false);
            if (applied.IsFailure) {
                continue;
            }

            state.State.Pending.RemoveAll(x => x.Sequence == entry.Sequence);
            Register(entry.Tuple, entry.IsDelete);
            repaired++;
        }

        if (repaired > 0) {
            // A repaired write may have landed its OBJECT half for the first time, which changes
            // what Check answers — so the version moves and every cached answer in the tenant is
            // stale from here on.
            state.State.Version++;
        }

        await state.WriteStateAsync();

        return Result<SweepReport>.Success(
            new() {
                Pending = pending.Count,
                Repaired = repaired,
                Remaining = state.State.Pending.Count,
                Superseded = superseded
            }
        );
    }

    /// <inheritdoc />
    public Task<Result<int>> PendingCountAsync() => Task.FromResult(Result<int>.Success(state.State.Pending.Count));

    /// <inheritdoc />
    public async Task<Result<ExpirySweepReport>> SweepExpiredAsync() {
        // The journal first: a write that died half-applied may be the one that registered an
        // expiring tuple, or the one that rewrote a registered tuple as permanent, and the
        // register is only complete once every journalled write has finished.
        var replayed = await SweepAsync();
        if (replayed.TryGetError(out var replayError)) {
            return Result<ExpirySweepReport>.Failure(replayError);
        }

        var now = clock.UtcNow;
        var removed = 0;
        var failed = 0;

        // ⚠ A registered tuple with a journal entry still outstanding is left for a later tick. The
        // entry is its latest intent — a rewrite as permanent, say, whose replay failed again — and
        // the register reflects the write before it. Deleting it here would act on the older intent,
        // and the delete's own step 7 would then drop the newer entry as superseded.
        var expired = state.State.Expiring
            .Where(x => !TupleExpiry.IsLive(x.ExpiresOn, now) && !state.State.Pending.Any(p => p.Tuple.IsSameTupleAs(x)))
            .ToList();

        foreach (var tuple in expired) {
            // The same seven steps as a revoke — the step-7 register update is what takes the
            // tuple out of the list this loop is walking a copy of.
            var deleted = await ApplyAsync(tuple, true);

            if (deleted.TryGetError(out var error)) {
                failed++;
                AuthorizationLog.ExpiredTupleSweepFailed(logger, tenantId, tuple.ToString(), tuple.ExpiresOn, error.Message);
                continue;
            }

            removed++;

            // ⚠ The audit event for the end of a just-in-time grant. It is written when the tuple
            // is removed and names both instants, because the grant ended at ExpiresOn — every
            // check from then on denied — and the sweep is only when storage caught up.
            AuthorizationLog.ExpiredTupleRemoved(
                logger,
                tenantId,
                tuple.ToString(),
                tuple.ExpiresOn,
                now,
                deleted.GetValueOrThrow().Version
            );
        }

        var armed = await DisarmIfIdleAsync();

        return Result<ExpirySweepReport>.Success(
            new() { Removed = removed, Remaining = state.State.Expiring.Count, Armed = armed, Failed = failed }
        );
    }

    /// <inheritdoc />
    public async Task ReceiveReminder(string reminderName, TickStatus status) {
        if (!string.Equals(reminderName, SweepReminderName, StringComparison.Ordinal)) {
            return;
        }

        var swept = await SweepExpiredAsync();
        if (swept.TryGetError(out var error)) {
            // Left armed: the next tick is the retry, and a sweep that fails has deleted nothing
            // it didn't finish, because every delete is journalled.
            AuthorizationLog.ExpirySweepFailed(logger, tenantId, error.Message);
        }
    }

    /// <inheritdoc />
    public Task DeactivateAsync() {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }

    async Task<Result<ConsistencyToken>> ApplyAsync(RelationTuple tuple, bool isDelete) {
        var validated = Validate(tuple, isDelete);
        if (validated.TryGetError(out var error)) {
            return Result<ConsistencyToken>.Failure(error);
        }

        if (!isDelete && tuple.ExpiresOn is { } expiresOn) {
            if (await ShortensAsync(tuple, expiresOn)) {
                // ⚠ THE NOTICE AND THE FENCE, IN ONE TURN. No await between the clock read and the
                // fence, so a check grain whose fence read interleaves here either sees this fence
                // or was answered before `now`, and trusts that answer for less than a notice after
                // it. That's what lets a MinimizeLatency hit skip the store. See CacheFence.
                var now = clock.UtcNow;

                if (expiresOn < now + TupleExpiry.ShorteningNotice) {
                    return Result<ConsistencyToken>.Failure(
                        ErrorCode.InvalidRequestBody,
                        $"'{tuple}' would end at {expiresOn:O}, sooner than it does now and less than "
                        + TupleExpiry.ShorteningNotice.TotalSeconds.ToString(CultureInfo.InvariantCulture)
                        + " seconds from now. A check grain trusts the fences it last read for that long, "
                        + "so a grant that has to end sooner is revoked instead — docs/plan/07 "
                        + "§ Time-bounded relations."
                    );
                }

                AddFence(expiresOn);
            }

            // ⚠ BEFORE THE JOURNAL. An expiring grant nothing will ever sweep is one nothing will
            // ever audit the end of, and a silo with no reminder service throws here — so the
            // write is refused while nothing has landed, rather than half-written.
            await ArmAsync();
        }

        // Step 1 — journal, durably, BEFORE either half. A crash between here and step 7 leaves an
        // entry the sweeper can replay; a crash before here left nothing behind to replay.
        var sequence = state.State.NextSequence++;
        state.State.Pending.Add(new() { Tuple = tuple, IsDelete = isDelete, Sequence = sequence });

        await state.WriteStateAsync();

        var applied = await ApplyBothHalvesAsync(tuple, isDelete, true);
        if (applied.TryGetError(out var applyError)) {
            return Result<ConsistencyToken>.Failure(applyError);
        }

        // Step 7 — the journal entry goes, the register follows the tuple, and the version moves,
        // in one durable write. ⚠ Older entries for the same tuple go with it: this write is the
        // latest intent and has just run every step, so a replay of an earlier write or delete
        // could only undo it. See the remarks on SweepAsync.
        state.State.Pending.RemoveAll(x => x.Sequence <= sequence && x.Tuple.IsSameTupleAs(tuple));
        Register(tuple, isDelete);
        state.State.Version++;
        await state.WriteStateAsync();

        if (!isDelete && tuple.ExpiresOn is { } written) {
            AuthorizationLog.ExpiringTupleWritten(logger, tenantId, tuple.ToString(), written, state.State.Version);
        }

        return Result<ConsistencyToken>.Success(Token());
    }

    async Task<Result> ApplyBothHalvesAsync(RelationTuple tuple, bool isDelete, bool useInterceptor) {
        var tenant = tenantId.ToString("D", CultureInfo.InvariantCulture);
        var index = Maintainer();

        var objects = GrainFactory.ForTenant(tenant)
            .GetGrain<IObjectRelationsGrain>(GrainKeys.ObjectRelations(tuple.Object.Type, tuple.Object.Id));

        // Step 2 — a delete's index update, BEFORE the forward half, so that no crash leaves the
        // index granting a membership the tuples no longer do. See the remarks on this class. A
        // rewrite that moves an indexed edge's expiry pays it too: the edge may be becoming one
        // the closure must not hold, and only a recomputation takes its members back out.
        var recompute = isDelete || await ChangesIndexedExpiryAsync(objects, tuple);

        if (recompute) {
            var revoked = await index.ApplyDeleteAsync(tuple, CancellationToken.None);
            if (revoked.TryGetError(out var revokeError)) {
                return Result.Failure(revokeError);
            }
        }

        // Step 3 — the object half. THE ONE CHECK READS.
        var forward = isDelete
            ? await objects.DeleteAsync(tuple.Relation, tuple.Subject)
            : await objects.WriteAsync(tuple.Relation, tuple.Subject, tuple.ExpiresOn);

        if (forward.TryGetError(out var forwardError)) {
            return Result.Failure(forwardError);
        }

        // Step 4 — the seam. See IRelationWriteInterceptor.
        if (useInterceptor) {
            await interceptor.AfterObjectWriteAsync(tuple, isDelete);
        }

        // Step 5 — the reverse half. Nothing on the check path reads it.
        var subjects = GrainFactory.ForTenant(tenant)
            .GetGrain<ISubjectRelationsGrain>(GrainKeys.SubjectRelations(tuple.Subject.Type, tuple.Subject.Id));

        var entry = new SubjectIndexEntry {
            Object = tuple.Object,
            Relation = tuple.Relation,
            SubjectRelation = tuple.Subject.Relation,
            ExpiresOn = isDelete ? null : tuple.ExpiresOn
        };

        var reverse = isDelete
            ? await subjects.RemoveAsync(entry)
            : await subjects.AddAsync(entry);

        if (reverse.TryGetError(out var reverseError)) {
            return Result.Failure(reverseError);
        }

        // Step 6 — a write's index update, AFTER both halves, so that no crash leaves the index
        // granting a membership the forward half has not recorded.
        if (!isDelete) {
            var granted = await index.ApplyWriteAsync(tuple, CancellationToken.None);
            if (granted.TryGetError(out var grantError)) {
                return Result.Failure(grantError);
            }
        }

        return Result.Success;
    }

    /// <summary>
    ///     Whether a write rewrites a live tuple on an indexed relation with a different expiry —
    ///     the case step 2 has to recompute for as well as a delete.
    /// </summary>
    /// <remarks>
    ///     A replay after a crash past step 3 finds the forward half already rewritten and skips
    ///     the recomputation, which is right: the crash was after step 2, so it already ran.
    /// </remarks>
    async Task<bool> ChangesIndexedExpiryAsync(IObjectRelationsGrain objects, RelationTuple tuple) {
        if (!MembershipIndexMaintainer.IsIndexed(schema, tuple.Object.Type, tuple.Relation)) {
            return false;
        }

        var read = await objects.ReadAsync();
        if (read.IsFailure) {
            // Recomputing is always safe — it is what a delete does — so a read that can't say
            // is answered by doing the safe thing.
            return true;
        }

        var snapshot = read.GetValueOrThrow();
        return snapshot.Subjects(tuple.Relation).Contains(tuple.Subject)
            && snapshot.ExpiryOf(tuple.Relation, tuple.Subject) != tuple.ExpiresOn;
    }

    /// <summary>
    ///     Whether an expiring write brings a live tuple's end closer: the tuple is permanent now, or
    ///     ends later than <paramref name="expiresOn" />.
    /// </summary>
    /// <remarks>
    ///     A new grant isn't a shortening, because no cached answer can rest on a tuple that wasn't
    ///     there. Neither is a rewrite of an expired tuple the sweep hasn't reached: the answers it
    ///     proved already stopped at its end. A read that can't say is taken as a shortening, which
    ///     is the side that fences.
    /// </remarks>
    async Task<bool> ShortensAsync(RelationTuple tuple, DateTimeOffset expiresOn) {
        var read = await GrainFactory.ForTenant(tenantId.ToString("D", CultureInfo.InvariantCulture))
            .GetGrain<IObjectRelationsGrain>(GrainKeys.ObjectRelations(tuple.Object.Type, tuple.Object.Id))
            .ReadAsync();

        if (read.IsFailure) {
            return true;
        }

        var snapshot = read.GetValueOrThrow();

        return snapshot.Subjects(tuple.Relation).Contains(tuple.Subject)
            && (snapshot.ExpiryOf(tuple.Relation, tuple.Subject) is not { } current || current > expiresOn);
    }

    /// <summary>
    ///     Adds a fence that retires every answer stamped at or before the current version from
    ///     <paramref name="at" />, and folds the fences already in effect into one.
    /// </summary>
    /// <remarks>
    ///     Folding is exact: once two fences are both in effect, the one with the higher
    ///     <see cref="CacheFence.Below" /> retires everything the other does. The folded fence takes
    ///     the earlier instant, so a check grain whose clock runs a little behind this one still
    ///     finds it in effect.
    /// </remarks>
    void AddFence(DateTimeOffset at) {
        var now = clock.UtcNow;
        var fences = state.State.Fences;
        var passed = fences.Where(x => !TupleExpiry.IsLive(x.At, now)).ToList();

        if (passed.Count > 1) {
            fences.RemoveAll(x => !TupleExpiry.IsLive(x.At, now));
            fences.Add(new() { Below = passed.Max(static x => x.Below), At = passed.Min(static x => x.At) });
        }

        fences.Add(new() { Below = state.State.Version + 1, At = at });
    }

    /// <summary>
    ///     Keeps the expiry register in step with the tuple just applied: registered while its
    ///     latest write carries an expiry, and gone once it's deleted or rewritten without one.
    /// </summary>
    void Register(RelationTuple tuple, bool isDelete) {
        state.State.Expiring.RemoveAll(x => x.IsSameTupleAs(tuple));

        if (!isDelete && tuple.ExpiresOn is not null) {
            state.State.Expiring.Add(tuple);
        }
    }

    /// <summary>
    ///     Registers the sweep reminder unless it's already registered.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The <c>GetReminder</c> guard is the whole point</b>, for the reason
    ///     <c>ExpirySweeperGrain</c> found the hard way: <c>RegisterOrUpdateReminder</c> rewrites
    ///     the due time, so arming unconditionally on every expiring write would push the next
    ///     tick out by a period each time, and a tenant granting just-in-time roles faster than
    ///     once per <see cref="SweepPeriod" /> would never be swept.
    /// </remarks>
    async Task ArmAsync() {
        if (await this.GetReminder(SweepReminderName) is not null) {
            return;
        }

        _ = await this.RegisterOrUpdateReminder(SweepReminderName, SweepPeriod, SweepPeriod);
    }

    /// <summary>Cancels the reminder once nothing is registered or journalled.</summary>
    /// <returns>Whether the reminder is still registered.</returns>
    async Task<bool> DisarmIfIdleAsync() {
        var reminder = await this.GetReminder(SweepReminderName);

        if (state.State.Expiring.Count > 0 || state.State.Pending.Count > 0) {
            if (reminder is null) {
                // Registered or journalled with no reminder — a row lost from the reminder table.
                // The sweep that noticed puts it back, which is the repair path a reminder armed
                // off a deadline would not have.
                _ = await this.RegisterOrUpdateReminder(SweepReminderName, SweepPeriod, SweepPeriod);
            }

            return true;
        }

        if (reminder is not null) {
            await this.UnregisterReminder(reminder);
        }

        return false;
    }

    /// <summary>
    ///     The closure maintainer over this tenant's grains. Built per write because the readers
    ///     are: the forward reader it recomputes from is the same one <c>Check</c> walks.
    /// </summary>
    MembershipIndexMaintainer Maintainer() {
        var store = new GrainMembershipIndexStore(GrainFactory, tenantId);

        return new(
            schema,
            new GrainRelationReader(GrainFactory, tenantId, false),
            new GrainReverseRelationReader(GrainFactory, tenantId, new MembershipIndexReader(schema, store)),
            store
        );
    }

    Result Validate(RelationTuple tuple, bool isDelete) {
        if (tuple is null || !tuple.IsValid) {
            return Result.Failure(
                ErrorCode.InvalidRequestBody,
                $"'{tuple}' is not a well-formed tuple. It is 'object#relation@subject' — "
                + "docs/plan/07 § The model."
            );
        }

        var type = schema.Type(tuple.Object.Type);
        if (type is null) {
            return Result.Failure(
                ErrorCode.SchemaInvalid,
                $"'{tuple.Object.Type}' is not an object type in schema version "
                + schema.Version.ToString(CultureInfo.InvariantCulture)
                + ". It defines ["
                + string.Join(", ", schema.TypeNames)
                + "]."
            );
        }

        var member = type.Member(tuple.Relation);
        if (member is null) {
            return Result.Failure(
                ErrorCode.SchemaInvalid,
                $"'{tuple.Object.Type}' declares no relation '{tuple.Relation}'. It declares ["
                + string.Join(", ", type.Relations)
                + "]."
            );
        }

        if (member.IsPermission) {
            return Result.Failure(
                ErrorCode.SchemaInvalid,
                $"'{tuple.Relation}' is a permission on '{tuple.Object.Type}', not a relation. "
                + "Tuples are written against relations; a permission is computed. Writing one "
                + "would create a grant nothing evaluates and nobody can find."
            );
        }

        if (!isDelete && tuple.ExpiresOn is { } expiresOn && !TupleExpiry.IsLive(expiresOn, clock.UtcNow)) {
            return Result.Failure(
                ErrorCode.InvalidRequestBody,
                $"'{tuple}' expires at {expiresOn:O}, which is not later than now. A tuple that has "
                + "already expired grants nothing and would sit in storage until a sweep removed "
                + "it, so it is refused rather than written — docs/plan/07 § Time-bounded relations."
            );
        }

        return Result.Success;
    }

    ConsistencyToken Token() => new() { TenantId = tenantId, Version = state.State.Version };
}
