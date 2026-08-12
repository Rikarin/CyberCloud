// ⚠ ONE `using`, AND IT USED TO BE TWO. `ObjectTypes` and `Relations` were in the authorization
// IMPLEMENTATION assembly, so this file's second `using CyberCloud.Authorization;` was the whole
// reason CyberCloud.Identity.csproj referenced it — the only module-to-implementation edge in the
// tree outside providers. The vocabulary moved to .Contracts; see
// CyberCloud.Authorization.Contracts/AuthorizationVocabulary.cs.
using CyberCloud.Authorization.Contracts;
using CyberCloud.Core.Contracts;
using CyberCloud.Core.Time;
using Orleans.Multitenant;
using System.Globalization;

namespace CyberCloud.Identity.Grains;

/// <summary>
///     <see cref="IGroupGrain" /> — Entity, Durable, key <c>group/{groupId:N}</c>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THE STATE HOLDS NO MEMBERS. Look at <see cref="GroupGrainState" /> — there is nowhere
///         to put one.</b> docs/plan/11 § The object model: membership is <c>group:X#member@user:Y</c>
///         in ReBAC, "so nesting, inheritance and <c>ListObjects</c> come free", and a member list
///         here "would be a second source of truth and a hot spot for large groups".
///     </para>
///     <para>
///         What that buys, concretely, and it is worth spelling out because the membership methods
///         below look like they ought to be maintaining something:
///     </para>
///     <list type="bullet">
///         <item>
///             <b>Nesting costs nothing.</b> <see cref="AddMemberAsync" /> takes a
///             <see cref="SubjectRef" />, so <c>group:platform#member</c> is as valid a member as
///             <c>user:alice</c>, and the evaluator walks the nesting with no code in this file.
///         </item>
///         <item>
///             <b>Revoking is one tuple delete</b> that takes effect at the next check, rather than a
///             rewrite of a list some cache may still be holding.
///         </item>
///         <item>
///             <b>"Who is in this group" scales</b>, because it is an <c>Expand</c> against the
///             reverse index rather than a field that has to be small enough to load.
///         </item>
///         <item>
///             <b>A role assignment to a group is the same mechanism.</b>
///             <c>resourceGroup:R#contributor@group:G#member</c> is a tuple, and nothing in this
///             module has to know it exists.
///         </item>
///     </list>
///     <para>
///         ⚠ <b>Writing a tuple is not enforcing a policy.</b> docs/plan/07 § The enforcement seam
///         keeps <i>checks</i> in one place, in the resource manager. This grain writes membership,
///         which is the same kind of act as a role assignment, and reads it back through
///         <see cref="ICheckGrain" /> — the one supported way to ask.
///     </para>
/// </remarks>
public sealed class GroupGrain(
    [PersistentState("group", StorageTiers.Durable)] IPersistentState<GroupGrainState> state,
    IGrainFactory grains,
    IClock clock
)
    : Grain, IGroupGrain {
    Guid groupId;
    Guid tenantId;

    /// <inheritdoc />
    public override Task OnActivateAsync(CancellationToken cancellationToken) {
        tenantId = IdentityGrainKeys.TenantOf(this);
        groupId = IdentityGrainKeys.Decode(this, GrainKeyKind.Group).Id;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<Result<GroupDescriptor>> CreateAsync(string name, string description) {
        if (string.IsNullOrWhiteSpace(name)) {
            return Result<GroupDescriptor>.Failure(ErrorCode.InvalidRequestBody, "A group needs a name.");
        }

        if (state.State.CreatedAt != default && !state.State.Deleted) {
            return Result<GroupDescriptor>.Success(Descriptor());
        }

        state.State.Name = name;
        state.State.Description = description ?? string.Empty;
        state.State.CreatedAt = clock.UtcNow;
        state.State.Deleted = false;

        await state.WriteStateAsync();
        return Result<GroupDescriptor>.Success(Descriptor());
    }

    /// <inheritdoc />
    public Task<Result<GroupDescriptor>> GetAsync() =>
        Task.FromResult(Exists() ? Result<GroupDescriptor>.Success(Descriptor()) : NotFound<GroupDescriptor>());

    /// <inheritdoc />
    public async Task<Result<GroupDescriptor>> RenameAsync(string name, string description) {
        if (!Exists()) {
            return NotFound<GroupDescriptor>();
        }

        if (string.IsNullOrWhiteSpace(name)) {
            return Result<GroupDescriptor>.Failure(ErrorCode.InvalidRequestBody, "A group needs a name.");
        }

        state.State.Name = name;
        state.State.Description = description ?? string.Empty;

        await state.WriteStateAsync();
        return Result<GroupDescriptor>.Success(Descriptor());
    }

    /// <inheritdoc />
    public async Task<Result> AddMemberAsync(SubjectRef subject) {
        ArgumentNullException.ThrowIfNull(subject);

        if (!Exists()) {
            return NotFound();
        }

        var written = await TupleStore().WriteAsync(MembershipTuple(subject));
        return written.ToResult();
    }

    /// <inheritdoc />
    public async Task<Result> RemoveMemberAsync(SubjectRef subject) {
        ArgumentNullException.ThrowIfNull(subject);

        if (!Exists()) {
            return NotFound();
        }

        var deleted = await TupleStore().DeleteAsync(MembershipTuple(subject));
        return deleted.ToResult();
    }

    /// <inheritdoc />
    public async Task<Result<bool>> IsMemberAsync(SubjectRef subject, Consistency? consistency) {
        ArgumentNullException.ThrowIfNull(subject);

        if (!Exists()) {
            return NotFound<bool>();
        }

        // ⚠ A Check, not a lookup, and that is what makes nesting work. `group:eng#member@group:all#member`
        // plus `group:all#member@user:alice` means Alice is in Eng, and nothing here had to know.
        //
        // The consistency mode is the caller's and is passed through untouched. Defaulting it here
        // would be choosing on their behalf between a stale list view and a full walk, and the two
        // callers who care most — a portal listing and an access decision — want opposite answers.
        var check = await grains
            .ForTenant(TenantKey())
            .GetGrain<ICheckGrain>(GrainKeys.CheckCache(ObjectTypes.Group, GroupId()))
            .CheckAsync(Relations.Member, subject, consistency);

        return check.TryGetError(out var error)
            ? Result<bool>.Failure(error)
            : Result<bool>.Success(check.GetValueOrThrow().Allowed);
    }

    /// <inheritdoc />
    public async Task<Result> DeleteAsync() {
        if (!Exists()) {
            return NotFound();
        }

        state.State.Deleted = true;
        await state.WriteStateAsync();

        // ⚠ The tuples naming this group as an OBJECT are its membership; the ones naming it as a
        // SUBJECT are its grants elsewhere, and this does not touch those. Deleting a group whose
        // `member` userset holds role assignments silently removes those grants at the next check,
        // which is correct and is why the portal has to show what a group grants before offering to
        // delete it — docs/plan/11 § The object model.
        return Result.Success;
    }

    /// <inheritdoc />
    public Task DeactivateAsync() {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }

    // ── Internals ──────────────────────────────────────────────────────────────────────────────

    RelationTuple MembershipTuple(SubjectRef subject) =>
        new() {
            Object = ObjectRef.Of(ObjectTypes.Group, GroupId()),
            Relation = Relations.Member,
            Subject = subject
        };

    ITupleStoreGrain TupleStore() =>
        grains.ForTenant(TenantKey()).GetGrain<ITupleStoreGrain>(GrainKeys.TupleStore(tenantId));

    string TenantKey() => tenantId.ToString("D", CultureInfo.InvariantCulture);

    string GroupId() => groupId.ToString("N", CultureInfo.InvariantCulture);

    bool Exists() => state.State.CreatedAt != default && !state.State.Deleted;

    GroupDescriptor Descriptor() =>
        new() {
            GroupId = groupId,
            TenantId = tenantId,
            Name = state.State.Name,
            Description = state.State.Description,
            CreatedAt = state.State.CreatedAt
        };

    Result<T> NotFound<T>()
        where T : notnull =>
        Result<T>.Failure(ErrorCode.ResourceNotFound, $"Group {groupId:D} does not exist.");

    Result NotFound() =>
        Result.Failure(ErrorCode.ResourceNotFound, $"Group {groupId:D} does not exist.");
}
