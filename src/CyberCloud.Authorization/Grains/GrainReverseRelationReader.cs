using CyberCloud.Authorization.Contracts;
using CyberCloud.Authorization.Evaluation;
using CyberCloud.Core;
using CyberCloud.Core.Resources;
using Orleans.Multitenant;
using System.Globalization;

namespace CyberCloud.Authorization.Grains;

/// <summary>
///     The production <see cref="IReverseRelationReader" />: one <c>ISubjectRelationsGrain</c> read
///     per subject object the walk visits, and one <c>IMembershipIndexGrain</c> read per closure
///     it asks for, inside one tenant.
/// </summary>
/// <remarks>
///     ⚠
///     <b>
///         The tenant is captured, for the reason <see cref="GrainRelationReader" /> captures
///         it.
///     </b> A reverse entry names an object in the same tenant as the subject it was written
///     against — there is no field for another — so a walk that never spells the tenant per call
///     has no expression in it that could read across one. The index reader is handed in rather
///     than built here so the walk and its verifying check share one, and one cache of slices.
/// </remarks>
sealed class GrainReverseRelationReader(IGrainFactory grains, Guid tenantId, MembershipIndexReader index)
    : IReverseRelationReader {
    readonly string tenant = tenantId.ToString("D", CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public async ValueTask<Result<IReadOnlyList<SubjectIndexEntry>>> ReadAsync(
        ObjectRef subjectObject,
        CancellationToken cancellationToken
    ) =>
        await grains.ForTenant(tenant)
            .GetGrain<ISubjectRelationsGrain>(GrainKeys.SubjectRelations(subjectObject.Type, subjectObject.Id))
            .ListAsync();

    /// <inheritdoc />
    public ValueTask<Result<IReadOnlyList<SubjectRef>>> ReadUsersetsAsync(
        SubjectRef subject,
        CancellationToken cancellationToken
    ) =>
        index.UsersetsOfAsync(subject, cancellationToken);
}

/// <summary>
///     The production <see cref="IMembershipIndexStore" />: one <c>IMembershipIndexGrain</c> per
///     subject object, inside one tenant.
/// </summary>
sealed class GrainMembershipIndexStore(IGrainFactory grains, Guid tenantId) : IMembershipIndexStore {
    readonly string tenant = tenantId.ToString("D", CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public async ValueTask<Result<MembershipIndexSnapshot>> ReadAsync(
        ObjectRef subjectObject,
        CancellationToken cancellationToken
    ) =>
        await Grain(subjectObject).ReadAsync();

    /// <inheritdoc />
    public async ValueTask<Result> ApplyAsync(
        ObjectRef subjectObject,
        MembershipIndexChange change,
        CancellationToken cancellationToken
    ) {
        var applied = await Grain(subjectObject).ApplyAsync(change);
        return applied.TryGetError(out var error) ? Result.Failure(error) : Result.Success;
    }

    IMembershipIndexGrain Grain(ObjectRef subjectObject) =>
        grains.ForTenant(tenant)
            .GetGrain<IMembershipIndexGrain>(GrainKeys.MembershipIndex(subjectObject.Type, subjectObject.Id));
}
