using CyberCloud.Authorization.Contracts;
using CyberCloud.Authorization.Evaluation;
using CyberCloud.Core;
using CyberCloud.Core.Resources;
using Orleans.Multitenant;
using System.Globalization;

namespace CyberCloud.Authorization.Grains;

/// <summary>
///     The production <see cref="IReverseRelationReader" />: one <c>ISubjectRelationsGrain</c> read
///     per subject object the walk visits, inside one tenant.
/// </summary>
/// <remarks>
///     ⚠ <b>The tenant is captured, for the reason <see cref="GrainRelationReader" /> captures
///     it.</b> A reverse entry names an object in the same tenant as the subject it was written
///     against — there is no field for another — so a walk that never spells the tenant per call
///     has no expression in it that could read across one.
/// </remarks>
sealed class GrainReverseRelationReader(IGrainFactory grains, Guid tenantId) : IReverseRelationReader {
    readonly string tenant = tenantId.ToString("D", CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public async ValueTask<Result<IReadOnlyList<SubjectIndexEntry>>> ReadAsync(
        ObjectRef subjectObject,
        CancellationToken cancellationToken
    ) =>
        await grains.ForTenant(tenant)
            .GetGrain<ISubjectRelationsGrain>(GrainKeys.SubjectRelations(subjectObject.Type, subjectObject.Id))
            .ListAsync();
}
