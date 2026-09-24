using CyberCloud.Authorization.Contracts;
using CyberCloud.Core;
using CyberCloud.Core.Contracts;
using CyberCloud.Core.Resources;
using CyberCloud.Core.Time;

namespace CyberCloud.Authorization.Grains;

/// <summary>
///     <see cref="IObjectRelationsGrain" /> — Entity, Durable, key <c>rel/obj/{type}/{id}</c>.
/// </summary>
/// <remarks>
///     ⚠ <b>Expiry is applied here, at read time, against the silo's <see cref="IClock" />.</b> A
///     tuple whose expiry has passed is dropped from every snapshot and every role-assignment row
///     this grain hands out, from that instant on, while its row stays until the store's sweep
///     deletes it. Because <c>Check</c> reads tuples from here and nowhere else, that one filter is
///     what makes an expired grant deny with no write — and it's why the evaluator itself never
///     reads a clock. docs/plan/07 § Time-bounded relations.
/// </remarks>
public sealed class ObjectRelationsGrain(
    [PersistentState("relations", StorageTiers.Durable)]
    IPersistentState<ObjectRelationsState> state,
    IClock clock
)
    : Grain, IObjectRelationsGrain {
    ObjectRef self = new();

    /// <inheritdoc />
    public override Task OnActivateAsync(CancellationToken cancellationToken) {
        _ = AuthorizationGrainKeys.TenantOf(this);
        self = AuthorizationGrainKeys.DecodeObject(this, GrainKeyKind.ObjectRelations);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<Result<bool>> WriteAsync(string relation, SubjectRef subject, DateTimeOffset? expiresOn) {
        var validated = Validate(relation, subject);
        if (validated.TryGetError(out var error)) {
            return Result<bool>.Failure(error);
        }

        if (!state.State.ByRelation.TryGetValue(relation, out var subjects)) {
            subjects = [];
            state.State.ByRelation[relation] = subjects;
        }

        var present = subjects.Contains(subject);

        if (present && ExpiryOf(relation, subject) == expiresOn) {
            // Idempotent: writing the same tuple twice is one tuple. A role assignment retried
            // after a timeout must not create a second grant that a single revoke fails to remove.
            return Result<bool>.Success(false);
        }

        if (!present) {
            subjects.Add(subject);
        }

        // A rewrite replaces the expiry outright — extending it, shortening it, or dropping it.
        // The tuple's identity is object, relation and subject; its expiry is a property of it.
        var key = TupleExpiry.Key(relation, subject);
        if (expiresOn is { } expiry) {
            state.State.Expiries[key] = expiry;
        } else {
            state.State.Expiries.Remove(key);
        }

        await state.WriteStateAsync();
        return Result<bool>.Success(true);
    }

    /// <inheritdoc />
    public async Task<Result<bool>> DeleteAsync(string relation, SubjectRef subject) {
        var validated = Validate(relation, subject);
        if (validated.TryGetError(out var error)) {
            return Result<bool>.Failure(error);
        }

        if (!state.State.ByRelation.TryGetValue(relation, out var subjects)
            || !subjects.Remove(subject)) {
            return Result<bool>.Success(false);
        }

        if (subjects.Count == 0) {
            state.State.ByRelation.Remove(relation);
        }

        state.State.Expiries.Remove(TupleExpiry.Key(relation, subject));

        await state.WriteStateAsync();
        return Result<bool>.Success(true);
    }

    /// <inheritdoc />
    public Task<Result<ObjectRelationsSnapshot>> ReadAsync() =>
        Task.FromResult(Result<ObjectRelationsSnapshot>.Success(Snapshot()));

    /// <inheritdoc />
    public async Task<Result<ObjectRelationsSnapshot>> ReadDurableAsync() {
        await state.ReadStateAsync();
        return Result<ObjectRelationsSnapshot>.Success(Snapshot());
    }

    /// <inheritdoc />
    public Task<Result<IReadOnlyList<RoleAssignment>>> ListRoleAssignmentsAsync(
        IReadOnlyList<string> roles
    ) {
        ArgumentNullException.ThrowIfNull(roles);

        var now = clock.UtcNow;
        List<RoleAssignment> assignments = [];

        foreach (var role in roles) {
            if (!state.State.ByRelation.TryGetValue(role, out var subjects)) {
                continue;
            }

            foreach (var subject in subjects) {
                var expiresOn = ExpiryOf(role, subject);
                if (!TupleExpiry.IsLive(expiresOn, now)) {
                    continue;
                }

                assignments.Add(
                    new() {
                        Scope = self,
                        RoleName = role,
                        Principal = subject,
                        Inherited = false,
                        InheritedFrom = self,
                        ExpiresOn = expiresOn
                    }
                );
            }
        }

        return Task.FromResult(
            Result<IReadOnlyList<RoleAssignment>>.Success(
                [
                    .. assignments.OrderBy(static x => x.RoleName, StringComparer.Ordinal)
                        .ThenBy(static x => x.Principal.ToString(), StringComparer.Ordinal)
                ]
            )
        );
    }

    /// <inheritdoc />
    public Task DeactivateAsync() {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }

    static Result Validate(string relation, SubjectRef subject) {
        if (subject is null || !subject.IsValid) {
            return Result.Failure(
                ErrorCode.InvalidRequestBody,
                $"'{subject}' is not a well-formed subject. It is 'type:id' or "
                + "'type:id#relation' — docs/plan/07 § The model."
            );
        }

        return RelationNaming.ValidateName(relation, "relation");
    }

    DateTimeOffset? ExpiryOf(string relation, SubjectRef subject) =>
        state.State.Expiries.Count > 0
        && state.State.Expiries.TryGetValue(TupleExpiry.Key(relation, subject), out var expiry)
            ? expiry
            : null;

    ObjectRelationsSnapshot Snapshot() {
        var now = clock.UtcNow;
        Dictionary<string, IReadOnlyList<SubjectRef>> byRelation = new(StringComparer.Ordinal);
        Dictionary<string, DateTimeOffset> expiries = new(StringComparer.Ordinal);
        var count = 0;

        foreach (var (relation, subjects) in state.State.ByRelation) {
            List<SubjectRef> live = new(subjects.Count);

            foreach (var subject in subjects) {
                var expiresOn = ExpiryOf(relation, subject);
                if (!TupleExpiry.IsLive(expiresOn, now)) {
                    continue;
                }

                live.Add(subject);
                if (expiresOn is { } expiry) {
                    expiries[TupleExpiry.Key(relation, subject)] = expiry;
                }
            }

            if (live.Count == 0) {
                continue;
            }

            byRelation[relation] = live;
            count += live.Count;
        }

        return new() { Object = self, ByRelation = byRelation, Count = count, Expiries = expiries };
    }
}
