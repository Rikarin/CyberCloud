using CyberCloud.Authorization.Contracts;
using CyberCloud.Authorization.Evaluation;
using CyberCloud.Core;
using CyberCloud.Core.Contracts;
using CyberCloud.Core.Resources;

namespace CyberCloud.Authorization.Grains;

/// <summary>
///     <see cref="IMembershipIndexGrain" /> — Index, Durable, key <c>rel/idx/{type}/{id}</c>.
/// </summary>
/// <remarks>
///     <para>
///         The grain is the slice and nothing more: it applies the unions, replacements and
///         subtractions <c>TupleStoreGrain</c> hands it and keeps them in a row. Which slices a
///         tuple touches, and with what, is <see cref="MembershipIndexMaintainer" />'s to decide —
///         the same split as <c>ObjectRelationsGrain</c> holding tuples while the store decides
///         the order they land in — so the closure algorithm is one class, testable in memory,
///         rather than half here and half there.
///     </para>
///     <para>
///         ⚠ <b>An incremental change never lands on a slice that is not yet a closure under this
///         schema.</b> A slice with <see cref="MembershipIndexState.SchemaVersion" /> <c>0</c> has
///         never been written, and the tuples it should close over may be older than the index —
///         an upgrade, or a restore of the forward rows without these — so a union applied to it
///         and stamped with this version would be a closure that omits every one of them, for good:
///         the review of issue #37 wrote a nesting edge over such a slice and the group's existing
///         members were denied permanently. A slice stamped with another version is not a closure
///         under this schema either. <see cref="ApplyAsync" /> therefore rebuilds the slice from
///         the forward and reverse indexes first — the same recomputation <see cref="RebuildAsync" />
///         offers by hand — and applies the change on top. A rebuild is a whole-slice replacement
///         (<see cref="MembershipIndexChange.Reset" />) and is exempt, or it would rebuild itself
///         forever. <c>MembershipIndexGrainTests.RowsThatPredateTheIndexAreWalkedAndThenBackfilledByTheFirstWriteThatTouchesThem</c>
///         drives it over rows written without the index.
///     </para>
///     <para>
///         ⚠ <b>Nothing on the write path reads this grain's neighbours from inside it.</b>
///         <see cref="RebuildAsync" /> reads the forward and reverse indexes and writes its own
///         state only; an index grain that reached into other index grains would be a call into
///         an activation the store may be writing at that moment, which is the re-entrancy shape
///         every grain in this assembly avoids. The reverse reader a rebuild is handed carries an
///         index reader, but <see cref="MembershipIndexMaintainer.RebuildAsync" /> reads entries
///         only and never asks it for a closure — which is what keeps the rebuild inside
///         <see cref="ApplyAsync" /> from calling this very activation.
///     </para>
///     <para>
///         ⚠ <b>A change computed under another schema version is refused, and during a rolling
///         upgrade that bumps the version this is a write that fails at step 6.</b> docs/plan/04
///         § Failure and upgrade has silos of version N and N+1 coexisting; a tuple write whose
///         <c>TupleStoreGrain</c> is on an N silo computes an N change, and an index grain on an
///         N+1 silo cannot apply it, so the caller gets this refusal after the forward and reverse
///         halves have landed. The journal entry stays pending and <c>ITupleStoreGrain.SweepAsync</c>
///         replays it once the fleet has converged; until then the index is behind the forward
///         half for that tuple, which is the deny direction. The check cache rides the same window
///         by keying on the schema version; the index has no per-version copy to key on, and
///         docs/plan/07 § The Leopard index records the window as owed.
///     </para>
/// </remarks>
[DurableStateRationale(
    "Derived state, not the record: both closures are recomputed from the reviewed forward and "
    + "reverse indexes by RebuildAsync, so its loss tolerance is not the zero durable-grains.txt is "
    + "reserved for. Durable rather than Hot because the tuple store writes it under its journal, "
    + "before the relation version moves, and that guarantee — a token covers a write that landed — "
    + "holds only for state that persists with the halves it is journalled beside. A Redis flush "
    + "would leave the closure behind every outstanding token with nothing to replay. docs/plan/07 "
    + "§ The Leopard index."
)]
public sealed class MembershipIndexGrain(
    [PersistentState("membership", StorageTiers.Durable)]
    IPersistentState<MembershipIndexState> state,
    AuthorizationSchema schema
)
    : Grain, IMembershipIndexGrain {
    Guid tenantId;
    ObjectRef self = new();

    /// <inheritdoc />
    public override Task OnActivateAsync(CancellationToken cancellationToken) {
        tenantId = AuthorizationGrainKeys.TenantOf(this);
        self = AuthorizationGrainKeys.DecodeObject(this, GrainKeyKind.MembershipIndex);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<Result<MembershipIndexSnapshot>> ReadAsync() => Task.FromResult(Result<MembershipIndexSnapshot>.Success(Snapshot()));

    /// <inheritdoc />
    public async Task<Result<bool>> ApplyAsync(MembershipIndexChange change) {
        if (change is null) {
            return Result<bool>.Failure(ErrorCode.InvalidRequestBody, "An index change is required.");
        }

        var validated = Validate(change);
        if (validated.TryGetError(out var error)) {
            return Result<bool>.Failure(error);
        }

        var changed = false;

        if (!change.Reset && state.State.SchemaVersion != change.SchemaVersion) {
            // ⚠ Unwritten or stale — not a closure under this schema. Rebuild first; see the
            // remarks on this class. The stamp alone makes this a change worth persisting.
            AuthorizationMetrics.RecordIndexRebuild();

            var rebuilt = await RebuildChangeAsync();
            if (rebuilt.TryGetError(out var rebuildError)) {
                return Result<bool>.Failure(rebuildError);
            }

            changed = Apply(rebuilt.GetValueOrThrow());
        }

        changed |= Apply(change);

        if (changed) {
            await state.WriteStateAsync();
        }

        return Result<bool>.Success(changed);
    }

    /// <inheritdoc />
    public async Task<Result<MembershipIndexSnapshot>> RebuildAsync() {
        var rebuilt = await RebuildChangeAsync();
        if (rebuilt.TryGetError(out var error)) {
            return Result<MembershipIndexSnapshot>.Failure(error);
        }

        var applied = await ApplyAsync(rebuilt.GetValueOrThrow());
        return applied.TryGetError(out var applyError)
            ? Result<MembershipIndexSnapshot>.Failure(applyError)
            : Result<MembershipIndexSnapshot>.Success(Snapshot());
    }

    /// <inheritdoc />
    public Task DeactivateAsync() {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }

    /// <summary>Both closures recomputed from the forward and reverse indexes, as a whole-slice replacement.</summary>
    async Task<Result<MembershipIndexChange>> RebuildChangeAsync() {
        var maintainer = new MembershipIndexMaintainer(
            schema,
            new GrainRelationReader(GrainFactory, tenantId, false),
            new GrainReverseRelationReader(
                GrainFactory,
                tenantId,
                new MembershipIndexReader(schema, new GrainMembershipIndexStore(GrainFactory, tenantId))
            ),
            new GrainMembershipIndexStore(GrainFactory, tenantId)
        );

        return await maintainer.RebuildAsync(self, CancellationToken.None);
    }

    /// <summary>Applies a validated change to the state in memory and reports whether anything moved.</summary>
    bool Apply(MembershipIndexChange change) {
        var changed = false;

        if (change.Reset) {
            changed = state.State.Members.Count > 0 || state.State.Usersets.Count > 0;
            state.State.Members.Clear();
            state.State.Usersets.Clear();
        }

        foreach (var (relation, members) in change.ReplaceMembers) {
            changed |= Replace(state.State.Members, relation, members);
        }

        foreach (var (relation, members) in change.AddMembers) {
            changed |= Add(state.State.Members, relation, members);
        }

        foreach (var (subjectRelation, usersets) in change.AddUsersets) {
            changed |= Add(state.State.Usersets, subjectRelation, usersets);
        }

        foreach (var (subjectRelation, usersets) in change.RemoveUsersets) {
            changed |= Remove(state.State.Usersets, subjectRelation, usersets);
        }

        if (state.State.SchemaVersion != change.SchemaVersion) {
            state.State.SchemaVersion = change.SchemaVersion;
            changed = true;
        }

        return changed;
    }

    MembershipIndexSnapshot Snapshot() =>
        new() {
            Object = self,
            SchemaVersion = state.State.SchemaVersion,
            Members = Freeze(state.State.Members),
            Usersets = Freeze(state.State.Usersets)
        };

    static Dictionary<string, IReadOnlyList<SubjectRef>> Freeze(Dictionary<string, List<SubjectRef>> lists) {
        Dictionary<string, IReadOnlyList<SubjectRef>> frozen = new(StringComparer.Ordinal);
        foreach (var (key, list) in lists) {
            frozen[key] = [.. list];
        }

        return frozen;
    }

    static bool Add(Dictionary<string, List<SubjectRef>> into, string key, IReadOnlyList<SubjectRef> values) {
        if (values.Count == 0) {
            return false;
        }

        if (!into.TryGetValue(key, out var list)) {
            list = [];
            into[key] = list;
        }

        var changed = false;
        foreach (var value in values) {
            if (!list.Contains(value)) {
                list.Add(value);
                changed = true;
            }
        }

        return changed;
    }

    static bool Replace(Dictionary<string, List<SubjectRef>> into, string key, IReadOnlyList<SubjectRef> values) {
        var existing = into.TryGetValue(key, out var list) ? list : null;

        if (values.Count == 0) {
            return existing is not null && into.Remove(key) && existing.Count > 0;
        }

        if (existing is not null && existing.Count == values.Count && values.All(existing.Contains)) {
            return false;
        }

        into[key] = [.. values];
        return true;
    }

    static bool Remove(Dictionary<string, List<SubjectRef>> from, string key, IReadOnlyList<SubjectRef> values) {
        if (!from.TryGetValue(key, out var list)) {
            return false;
        }

        var removed = list.RemoveAll(values.Contains) > 0;
        if (list.Count == 0) {
            from.Remove(key);
        }

        return removed;
    }

    Result Validate(MembershipIndexChange change) {
        if (change.SchemaVersion != schema.Version) {
            return Result.Failure(
                ErrorCode.SchemaInvalid,
                $"The change was computed under schema version {change.SchemaVersion} and this silo runs "
                + $"version {schema.Version}. A closure is only a closure under the schema that decided "
                + "which relations are direct-only, so the two must agree — docs/plan/07 § The Leopard index. "
                + "During a rolling upgrade the tuple stays journalled and the sweeper applies it once "
                + "every silo runs one version."
            );
        }

        foreach (var (key, values) in change.AddMembers.Concat(change.ReplaceMembers)) {
            var validated = ValidateRelation(key, values, "userset relation");
            if (validated.IsFailure) {
                return validated;
            }
        }

        foreach (var (key, values) in change.AddUsersets.Concat(change.RemoveUsersets)) {
            if (key.Length > 0 && !RelationNaming.IsName(key)) {
                return Result.Failure(ErrorCode.InvalidRequestBody, $"'{key}' is not a subject relation name.");
            }

            foreach (var value in values) {
                if (!value.IsUserset || !value.IsValid) {
                    return Result.Failure(
                        ErrorCode.InvalidRequestBody,
                        $"'{value}' is not a userset. Only a userset can contain a subject."
                    );
                }
            }
        }

        return Result.Success;
    }

    static Result ValidateRelation(string relation, IReadOnlyList<SubjectRef> values, string what) {
        var validated = RelationNaming.ValidateName(relation, what);
        if (validated.IsFailure) {
            return validated;
        }

        foreach (var value in values) {
            if (!value.IsValid) {
                return Result.Failure(ErrorCode.InvalidRequestBody, $"'{value}' is not a well-formed subject.");
            }
        }

        return Result.Success;
    }
}
