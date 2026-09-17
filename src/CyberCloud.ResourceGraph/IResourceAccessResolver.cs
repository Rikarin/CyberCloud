using CyberCloud.Authorization.Contracts;
using CyberCloud.Core.Resources;
using Orleans.Multitenant;
using System.Collections.Immutable;
using System.Globalization;

namespace CyberCloud.ResourceGraph;

/// <summary>
///     Answers "who may read this resource" for the projection's <c>access</c> column.
/// </summary>
/// <remarks>
///     The seam exists so the projector can be driven without an authorization cluster behind it,
///     and so a host with a different engine can fill the column its own way. The real one is
///     <see cref="ReBacResourceAccessResolver" />.
/// </remarks>
public interface IResourceAccessResolver {
    /// <summary>The subjects that hold <c>read</c> on the resource, as subject strings, sorted.</summary>
    /// <param name="tenantId">The tenant the resource belongs to.</param>
    /// <param name="resourceId">The resource.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    ///     The grantees — users, service principals and usersets — or a failure when the engine could
    ///     not answer. ⚠ An empty list is a success: a resource nobody has been granted is one nobody
    ///     lists, and that is not the same as not knowing.
    /// </returns>
    Task<Result<ImmutableArray<string>>> ReadersOfAsync(Guid tenantId, Guid resourceId, CancellationToken cancellationToken = default);
}

/// <summary>
///     The resolver over the ReBAC engine: the role-assignment view at the resource, direct and
///     inherited, which on <c>CyberCloudSchema</c> is exactly the set that can <c>read</c>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b><c>ICheckGrain.ListRoleAssignmentsAsync(includeInherited: true)</c> and not a second
///         walk written here, for the reason <c>ReBacRoleAssignmentStore</c> gives.</b> That view
///         reports an inherited role only where the resource's own rewrite inherits it — a walk of
///         <c>parent</c> edges written beside it would be a second opinion about which roles inherit,
///         and the one nobody re-reads when the schema changes. Every role on the schema
///         (<c>owner</c>, <c>contributor</c>, <c>reader</c>) rewrites down to <c>reader</c>, and
///         <c>read</c> is <c>Rel(reader)</c>, so a principal in the view is a principal that can read.
///         The <c>suspended</c> relation gates <c>assignRole</c> and <c>purge</c> and does not reach
///         <c>read</c>, which is why it is not consulted.
///     </para>
///     <para>
///         ⚠ <b>Why this and not <c>ListObjects</c>.</b> docs/plan/07 § ListObjects says the column is
///         "recomputed from <c>ListObjects</c>", and <c>ListObjects</c> answers the other direction:
///         given a subject, which objects. Filling one row from it would mean running it for every
///         subject in the tenant. The role-assignment view is the object-to-subjects direction the
///         column actually needs, over the same tuples and the same rewrites, and the membership
///         index's subject-to-usersets read happens where the plan puts it — on the list query, once
///         per caller.
///     </para>
/// </remarks>
public sealed class ReBacResourceAccessResolver : IResourceAccessResolver {
    readonly IGrainFactory grains;

    /// <summary>Creates a resolver over the cluster.</summary>
    /// <param name="grains">The grain factory; the tenant is applied per call.</param>
    public ReBacResourceAccessResolver(IGrainFactory grains) {
        ArgumentNullException.ThrowIfNull(grains);
        this.grains = grains;
    }

    /// <inheritdoc />
    public async Task<Result<ImmutableArray<string>>> ReadersOfAsync(Guid tenantId, Guid resourceId, CancellationToken cancellationToken = default) {
        var check = grains
            .ForTenant(tenantId.ToString("D", CultureInfo.InvariantCulture))
            .GetGrain<ICheckGrain>(GrainKeys.CheckCache(ObjectTypes.Resource, resourceId.ToString("N", CultureInfo.InvariantCulture)));

        var listed = await check.ListRoleAssignmentsAsync(true).WaitAsync(cancellationToken);

        if (listed.TryGetError(out var error)) {
            return Result<ImmutableArray<string>>.Failure(error);
        }

        var readers = listed.GetValueOrThrow()
            .Select(assignment => assignment.Principal.ToString())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToImmutableArray();

        return Result<ImmutableArray<string>>.Success(readers);
    }
}
