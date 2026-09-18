using CyberCloud.Authorization.Contracts;
using CyberCloud.Core;

namespace CyberCloud.Authorization.Evaluation;

/// <summary>
///     Where the Leopard index's slices are read and written — one <c>IMembershipIndexGrain</c>
///     per subject object in production, a dictionary in a test.
/// </summary>
/// <remarks>
///     The seam exists for the reason <see cref="IRelationReader" /> does: the closure algorithm in
///     <see cref="MembershipIndexMaintainer" /> is the one that ships, and the only way to hold it
///     to a brute-force closure over thousands of generated graphs is to run it over slices that
///     live in memory. <c>MembershipIndexPropertyTests</c> is that comparison, and the grain tests
///     are what shows the production store behind this interface persists what the algorithm wrote.
/// </remarks>
public interface IMembershipIndexStore {
    /// <summary>One subject object's slice, or an empty slice when nothing has been written.</summary>
    /// <param name="subjectObject">The subject object.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    ValueTask<Result<MembershipIndexSnapshot>> ReadAsync(ObjectRef subjectObject, CancellationToken cancellationToken);

    /// <summary>Applies one change to one slice. Idempotent — see <see cref="MembershipIndexChange" />.</summary>
    /// <param name="subjectObject">The subject object.</param>
    /// <param name="change">The change.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    ValueTask<Result> ApplyAsync(
        ObjectRef subjectObject,
        MembershipIndexChange change,
        CancellationToken cancellationToken
    );
}
