using CyberCloud.Authorization.Contracts;
using CyberCloud.Authorization.Evaluation;
using CyberCloud.Core;
using CyberCloud.Core.Resources;
using Orleans.Multitenant;
using System.Globalization;

namespace CyberCloud.Authorization.Grains;

/// <summary>
///     The production <see cref="IRelationReader" />: one <c>IObjectRelationsGrain</c> read per
///     object the walk visits, inside one tenant.
/// </summary>
/// <remarks>
///     <para>
///         Constructed per check, because <c>forceDurable</c> is per check —
///         <c>ConsistencyMode.FullyConsistent</c> is the only mode that pays for a durable re-read
///         (docs/plan/07 § Consistency, row 3).
///     </para>
///     <para>
///         ⚠ <b>The tenant is captured, not taken per call.</b> Every object a walk reaches is in
///         the same tenant as the object it started from, because a tuple cannot name an object in
///         another tenant — there is no field for one. Capturing the tenant means the walk has no
///         expression in it that could produce a cross-tenant read even if a tuple were corrupt.
///     </para>
/// </remarks>
sealed class GrainRelationReader(IGrainFactory grains, Guid tenantId, bool forceDurable)
    : IRelationReader {
    readonly string tenant = tenantId.ToString("D", CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public async ValueTask<Result<ObjectRelationsSnapshot>> ReadAsync(
        ObjectRef @object,
        CancellationToken cancellationToken
    ) {
        var grain = grains.ForTenant(tenant)
            .GetGrain<IObjectRelationsGrain>(GrainKeys.ObjectRelations(@object.Type, @object.Id));

        return forceDurable
            ? await grain.ReadDurableAsync()
            : await grain.ReadAsync();
    }
}
