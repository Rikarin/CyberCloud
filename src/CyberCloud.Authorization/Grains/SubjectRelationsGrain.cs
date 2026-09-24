using CyberCloud.Authorization.Contracts;
using CyberCloud.Core;
using CyberCloud.Core.Contracts;
using CyberCloud.Core.Resources;
using CyberCloud.Core.Time;

namespace CyberCloud.Authorization.Grains;

/// <summary>
///     <see cref="ISubjectRelationsGrain" /> — Index, Durable, key <c>rel/sub/{type}/{id}</c>.
/// </summary>
/// <remarks>
///     ⚠ <b>Nothing on the check path reads this grain.</b> That is not an implementation detail —
///     it is the property docs/plan/07 § Storage asserts and <c>TwoGrainWriteTests</c> proves. If a
///     future change makes <c>CheckGrain</c> read it, the asymmetry that makes the non-transactional
///     write safe is gone and the sweeper stops being a performance repair and becomes a security
///     one.
/// </remarks>
public sealed class SubjectRelationsGrain(
    [PersistentState("subjects", StorageTiers.Durable)]
    IPersistentState<SubjectRelationsState> state,
    IClock clock
)
    : Grain, ISubjectRelationsGrain {
    /// <inheritdoc />
    public override Task OnActivateAsync(CancellationToken cancellationToken) {
        // The key is decoded and discarded: this grain's identity IS the subject, and every entry
        // it holds is about some other object. Decoding is still done, because a key of the wrong
        // shape must fail on activation rather than quietly index the wrong subject.
        _ = AuthorizationGrainKeys.TenantOf(this);
        _ = AuthorizationGrainKeys.DecodeObject(this, GrainKeyKind.SubjectRelations);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<Result<bool>> AddAsync(SubjectIndexEntry entry) {
        var validated = Validate(entry);
        if (validated.TryGetError(out var error)) {
            return Result<bool>.Failure(error);
        }

        // Matched by object, relation and subject relation, never by record equality: a rewrite
        // with a new expiry is the same tuple and replaces the entry rather than adding a second.
        var index = state.State.Entries.FindIndex(x => x.IsSameEntryAs(entry));

        if (index >= 0) {
            if (state.State.Entries[index].ExpiresOn == entry.ExpiresOn) {
                return Result<bool>.Success(false);
            }

            state.State.Entries[index] = entry;
        } else {
            state.State.Entries.Add(entry);
        }

        await state.WriteStateAsync();
        return Result<bool>.Success(true);
    }

    /// <inheritdoc />
    public async Task<Result<bool>> RemoveAsync(SubjectIndexEntry entry) {
        var validated = Validate(entry);
        if (validated.TryGetError(out var error)) {
            return Result<bool>.Failure(error);
        }

        if (state.State.Entries.RemoveAll(x => x.IsSameEntryAs(entry)) == 0) {
            return Result<bool>.Success(false);
        }

        await state.WriteStateAsync();
        return Result<bool>.Success(true);
    }

    /// <inheritdoc />
    public Task<Result<IReadOnlyList<SubjectIndexEntry>>> ListAsync() {
        var now = clock.UtcNow;

        return Task.FromResult(
            Result<IReadOnlyList<SubjectIndexEntry>>.Success(
                [
                    .. state.State.Entries
                        .Where(x => TupleExpiry.IsLive(x.ExpiresOn, now))
                        .OrderBy(static x => x.Object.ToString(), StringComparer.Ordinal)
                        .ThenBy(static x => x.Relation, StringComparer.Ordinal)
                        .ThenBy(static x => x.SubjectRelation, StringComparer.Ordinal)
                ]
            )
        );
    }

    /// <inheritdoc />
    public Task DeactivateAsync() {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }

    static Result Validate(SubjectIndexEntry entry) {
        if (entry is null || !entry.Object.IsValid) {
            return Result.Failure(
                ErrorCode.InvalidRequestBody,
                $"'{entry?.Object}' is not a well-formed object reference."
            );
        }

        if (entry.SubjectRelation.Length > 0
            && !RelationNaming.IsName(entry.SubjectRelation)) {
            return Result.Failure(
                ErrorCode.InvalidRequestBody,
                $"'{entry.SubjectRelation}' is not a userset relation name."
            );
        }

        return RelationNaming.ValidateName(entry.Relation, "relation");
    }
}
