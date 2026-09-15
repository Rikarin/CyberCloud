using CyberCloud.Authorization.Contracts;
using CyberCloud.Authorization.Evaluation;
using CyberCloud.Core;
using CyberCloud.Core.Contracts;
using CyberCloud.Core.Resources;
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
///         <item>journal the tuple durably, here;</item>
///         <item>
///             <b>for a delete only</b>, update <c>IMembershipIndexGrain</c> — every slice the
///             edge could have reached, recomputed as if the tuple were already gone;
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
///         <item>clear the journal entry and bump the tenant relation version, durably.</item>
///     </list>
///     <para>
///         ⚠ <b>The version is bumped last, and that ordering is the point of the token.</b> A crash
///         anywhere before step 7 means no token was ever handed out, so nothing can be waiting on
///         a version that covers a write which did not finish. A token, once returned, always
///         covers a write that landed in every grain, the index included — which is why the index
///         needs no version of its own for docs/plan/07 § The Leopard index's staleness rule.
///     </para>
///     <para>
///         ⚠ <b>The index lands last on a write and first on a delete, and the asymmetry is the
///         whole safety argument for letting <c>Check</c> read it.</b> docs/plan/07 § Storage lets
///         the reverse index be stale because nothing on the check path reads it; the Leopard index
///         <i>is</i> read on the check path, and a <c>false</c> from it is taken without a walk. So
///         at every point a crash can leave the tenant, the index must be no more permissive than
///         the forward half. A write that dies before step 6 leaves a grant the walk sees and the
///         index does not — a deny, until <see cref="SweepAsync" /> replays it. A delete that dies
///         after step 2 leaves a revoke the index honours and the forward half has not applied —
///         also a deny. The order that would break it, forward delete before index update, is the
///         one crash that could leave a revoked membership answering <c>true</c>.
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
/// </remarks>
public sealed class TupleStoreGrain(
    [PersistentState("tuples", StorageTiers.Durable)]
    IPersistentState<TupleStoreState> state,
    AuthorizationSchema schema,
    IRelationWriteInterceptor interceptor
)
    : Grain, ITupleStoreGrain {
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
    public async Task<Result<SweepReport>> SweepAsync() {
        var pending = state.State.Pending.OrderBy(x => x.Sequence).ToList();
        if (pending.Count == 0) {
            return Result<SweepReport>.Success(new());
        }

        var repaired = 0;

        foreach (var entry in pending) {
            var applied = await ApplyBothHalvesAsync(entry.Tuple, entry.IsDelete, false);
            if (applied.IsFailure) {
                continue;
            }

            state.State.Pending.RemoveAll(x => x.Sequence == entry.Sequence);
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
            new() { Pending = pending.Count, Repaired = repaired, Remaining = state.State.Pending.Count }
        );
    }

    /// <inheritdoc />
    public Task<Result<int>> PendingCountAsync() => Task.FromResult(Result<int>.Success(state.State.Pending.Count));

    /// <inheritdoc />
    public Task DeactivateAsync() {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }

    async Task<Result<ConsistencyToken>> ApplyAsync(RelationTuple tuple, bool isDelete) {
        var validated = Validate(tuple);
        if (validated.TryGetError(out var error)) {
            return Result<ConsistencyToken>.Failure(error);
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

        // Step 7 — the journal entry goes and the version moves, in one durable write.
        state.State.Pending.RemoveAll(x => x.Sequence == sequence);
        state.State.Version++;
        await state.WriteStateAsync();

        return Result<ConsistencyToken>.Success(Token());
    }

    async Task<Result> ApplyBothHalvesAsync(RelationTuple tuple, bool isDelete, bool useInterceptor) {
        var tenant = tenantId.ToString("D", CultureInfo.InvariantCulture);
        var index = Maintainer();

        // Step 2 — a delete's index update, BEFORE the forward half, so that no crash leaves the
        // index granting a membership the tuples no longer do. See the remarks on this class.
        if (isDelete) {
            var revoked = await index.ApplyDeleteAsync(tuple, CancellationToken.None);
            if (revoked.TryGetError(out var revokeError)) {
                return Result.Failure(revokeError);
            }
        }

        // Step 3 — the object half. THE ONE CHECK READS.
        var objects = GrainFactory.ForTenant(tenant)
            .GetGrain<IObjectRelationsGrain>(GrainKeys.ObjectRelations(tuple.Object.Type, tuple.Object.Id));

        var forward = isDelete
            ? await objects.DeleteAsync(tuple.Relation, tuple.Subject)
            : await objects.WriteAsync(tuple.Relation, tuple.Subject);

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
            Object = tuple.Object, Relation = tuple.Relation, SubjectRelation = tuple.Subject.Relation
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

    Result Validate(RelationTuple tuple) {
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

        return Result.Success;
    }

    ConsistencyToken Token() => new() { TenantId = tenantId, Version = state.State.Version };
}
