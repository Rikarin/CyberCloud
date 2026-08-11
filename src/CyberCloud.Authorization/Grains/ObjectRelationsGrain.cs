using CyberCloud.Authorization.Contracts;
using CyberCloud.Core;
using CyberCloud.Core.Contracts;
using CyberCloud.Core.Resources;

namespace CyberCloud.Authorization.Grains;

/// <summary>
///     <see cref="IObjectRelationsGrain" /> — Entity, Durable, key <c>rel/obj/{type}/{id}</c>.
/// </summary>
public sealed class ObjectRelationsGrain(
    [PersistentState("relations", StorageTiers.Durable)] IPersistentState<ObjectRelationsState> state
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
    public async Task<Result<bool>> WriteAsync(string relation, SubjectRef subject) {
        var validated = Validate(relation, subject);
        if (validated.TryGetError(out var error)) {
            return Result<bool>.Failure(error);
        }

        if (!state.State.ByRelation.TryGetValue(relation, out var subjects)) {
            subjects = [];
            state.State.ByRelation[relation] = subjects;
        }

        if (subjects.Contains(subject)) {
            // Idempotent: writing the same tuple twice is one tuple. A role assignment retried
            // after a timeout must not create a second grant that a single revoke fails to remove.
            return Result<bool>.Success(false);
        }

        subjects.Add(subject);
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

        List<RoleAssignment> assignments = [];

        foreach (var role in roles) {
            if (!state.State.ByRelation.TryGetValue(role, out var subjects)) {
                continue;
            }

            assignments.AddRange(
                subjects.Select(subject => new RoleAssignment {
                        Scope = self,
                        RoleName = role,
                        Principal = subject,
                        Inherited = false,
                        InheritedFrom = self
                    }
                )
            );
        }

        return Task.FromResult(
            Result<IReadOnlyList<RoleAssignment>>.Success(
                [
                    .. assignments.OrderBy(x => x.RoleName, StringComparer.Ordinal)
                        .ThenBy(x => x.Principal.ToString(), StringComparer.Ordinal)
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

    ObjectRelationsSnapshot Snapshot() {
        Dictionary<string, IReadOnlyList<SubjectRef>> byRelation = new(StringComparer.Ordinal);
        var count = 0;

        foreach (var (relation, subjects) in state.State.ByRelation) {
            byRelation[relation] = [.. subjects];
            count += subjects.Count;
        }

        return new() { Object = self, ByRelation = byRelation, Count = count };
    }
}
