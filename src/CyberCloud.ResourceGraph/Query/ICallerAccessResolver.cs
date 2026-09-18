using CyberCloud.Authorization.Contracts;
using CyberCloud.Core.Resources;
using Orleans.Multitenant;
using System.Collections.Immutable;
using System.Globalization;

namespace CyberCloud.ResourceGraph.Query;

/// <summary>
///     Answers "which subjects is this caller" for the query's access filter: the caller's own
///     subject and every userset it is closed into.
/// </summary>
/// <remarks>
///     The other half of <see cref="IResourceAccessResolver" />. The projector fills the row's
///     <c>access</c> column with grantees — <c>user:{id}</c>, <c>group:{id}#member</c> — and leaves a
///     group as a userset; this expands the <i>caller</i> into the usersets they are in, once per
///     query, so that <c>hasAny(access, [caller, …usersets])</c> is the whole filter. The seam exists
///     so the service can be driven without a cluster behind it; the real one is
///     <see cref="MembershipIndexCallerAccessResolver" />.
/// </remarks>
public interface ICallerAccessResolver {
    /// <summary>The subject strings the access column is matched against for one caller.</summary>
    /// <param name="caller">The caller — the tenant, the subject type and the subject id.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    ///     The caller's own subject first, then its closed usersets, sorted; or a failure when the
    ///     index could not be read. ⚠ Never empty on success: a caller in no group is still
    ///     themselves.
    /// </returns>
    Task<Result<ImmutableArray<string>>> SubjectsOfAsync(
        CallerContext caller,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
///     The resolver over the Leopard index — docs/plan/07 § The Leopard index — read once per query.
/// </summary>
/// <remarks>
///     <para>
///         <c>IMembershipIndexGrain.ReadAsync</c> on the caller's own slice, and
///         <c>MembershipIndexSnapshot.UsersetsOf("")</c> is every userset the concrete subject is
///         transitively in through indexed relations — <c>group:eng#member</c> for a member of a
///         member of <c>eng</c>. That is the same read <c>ListObjectsEvaluator</c> starts from, and the
///         one docs/plan/08 § The resource-graph projection puts on the list query rather than in
///         the row.
///     </para>
///     <para>
///         ⚠
///         <b>
///             A slice nothing has written is rebuilt before it is trusted, and a stale one is read
///             as written.
///         </b> <c>SchemaVersion</c> 0 means no write has touched the slice — a tenant
///         upgraded to the index, or restored without it — and reading it as an empty closure would
///         hide every group-granted resource from every member (the #37 review finding).
///         <c>RebuildAsync</c> derives it from the tuples, once, and the maintainer keeps it from
///         then on. A slice stamped with an <i>older</i> schema version this assembly cannot detect,
///         since the current version lives in <c>CyberCloud.Authorization</c> and not in its
///         contracts; it is read as it stands, which under-lists rather than over-lists — the row's
///         grantees are the truth and a missing userset hides rows, never shows one — and the next
///         write through the tuple store rebuilds it.
///     </para>
/// </remarks>
public sealed class MembershipIndexCallerAccessResolver : ICallerAccessResolver {
    readonly IGrainFactory grains;

    /// <summary>Creates a resolver over the cluster.</summary>
    /// <param name="grains">The grain factory; the tenant is applied per call.</param>
    public MembershipIndexCallerAccessResolver(IGrainFactory grains) {
        ArgumentNullException.ThrowIfNull(grains);
        this.grains = grains;
    }

    /// <inheritdoc />
    public async Task<Result<ImmutableArray<string>>> SubjectsOfAsync(
        CallerContext caller,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(caller);

        var self = SubjectRef.Create(caller.SubjectType, caller.SubjectId);

        if (self.TryGetError(out var subjectError)) {
            return Result<ImmutableArray<string>>.Failure(subjectError);
        }

        var subject = self.GetValueOrThrow();

        var index = grains
            .ForTenant(caller.TenantId.ToString("D", CultureInfo.InvariantCulture))
            .GetGrain<IMembershipIndexGrain>(GrainKeys.MembershipIndex(subject.Type, subject.Id));

        Result<MembershipIndexSnapshot> read;

        try {
            read = await index.ReadAsync().WaitAsync(cancellationToken);

            if (read.IsSuccess && read.GetValueOrThrow().SchemaVersion == 0) {
                read = await index.RebuildAsync().WaitAsync(cancellationToken);
            }
        } catch (Exception exception) when (exception is not OperationCanceledException) {
            // ⚠ The same rule ReBacResourceAccessResolver applies: a grain call that throws is the
            // engine not answering, and the contract promises a failure for that rather than an
            // Orleans exception type the caller would have to know.
            return Result<ImmutableArray<string>>.Failure(
                ErrorCode.InternalError,
                $"The membership index did not answer for {subject} in tenant {caller.TenantId:D}: "
                + $"{exception.GetType().Name}: {exception.Message}"
            );
        }

        if (read.TryGetError(out var readError)) {
            return Result<ImmutableArray<string>>.Failure(readError);
        }

        var subjects = read.GetValueOrThrow()
            .UsersetsOf("")
            .Select(static userset => userset.ToString())
            .Where(userset => !string.Equals(userset, subject.ToString(), StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Prepend(subject.ToString())
            .ToImmutableArray();

        return Result<ImmutableArray<string>>.Success(subjects);
    }
}
