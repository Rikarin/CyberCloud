using System.Collections.Immutable;

namespace CyberCloud.ResourceManager.Reconcile;

/// <summary>
///     <see cref="INamespaceInventory" /> over a live cluster connection — the implementation
///     <c>UnavailableNamespaceInventory</c> stood in for.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The work is <c>IKubeClusterConnection.ListNamespaceAsync</c> and this type is the
///         translation, which is the whole of what belongs above the fabric.</b> Discovery of every
///         served namespaced kind and a list apiece is a Kubernetes concern and lives in
///         <c>CyberCloud.Kubernetes</c> behind the connection grain; what happens here is
///         <c>KubeObjectSummary</c> → <see cref="NamespaceOccupant" />, which is the one thing that
///         cannot happen down there — <c>NamespaceOccupant</c> is declared in this assembly's
///         contracts and the fabric may not see them.
///     </para>
///     <para>
///         ⚠ <b>It refuses rather than reporting an empty namespace, in both of the two ways it can
///         fail.</b> A cluster with no connection is a refusal and not "nothing is there"; a listing
///         that stopped part way is a refusal and not a shorter list. That is the same rule
///         <c>UnavailableNamespaceInventory</c> ships and it does not relax because a real
///         implementation exists — an empty listing is the answer that authorises a recursive delete
///         of a tenant's live data.
///     </para>
///     <para>
///         ⚠ <b>Not <c>IClusterObjectInventory</c>, and reusing it would have been the obvious
///         move.</b> That seam selects on <c>cybercloud.io/managed-by=cybercloud</c> because a drift
///         scan compares what the platform wrote against what it meant to write. This one has to find
///         the objects that selector excludes.
///     </para>
/// </remarks>
/// <param name="connections">Where a cluster id becomes something that can be read from.</param>
public sealed class ConnectionNamespaceInventory(IClusterConnectionFactory connections) : INamespaceInventory {
    /// <inheritdoc />
    public async Task<Result<ImmutableArray<NamespaceOccupant>>> ListAllAsync(
        Guid clusterId,
        string ns,
        CancellationToken cancellationToken = default
    ) {
        ArgumentException.ThrowIfNullOrEmpty(ns);

        if (connections.Connect(clusterId) is not { } connection) {
            // ⚠ A FAILURE AND NOT AN EMPTY ARRAY, and this is the branch a wrong cluster id lands
            // on. "There is no connection to this cluster" and "this cluster's namespace is empty"
            // are one keystroke apart at the call site and one recursive delete apart in effect.
            return Result<ImmutableArray<NamespaceOccupant>>.Failure(
                ErrorCode.ResourceNotFound,
                $"There is no connection to cluster {clusterId:D}, so nothing can say what namespace "
                + $"'{ns}' holds. Nothing may be reclaimed on a guess."
            );
        }

        var listed = await connection.ListNamespaceAsync(ns, cancellationToken).ConfigureAwait(false);

        if (listed.TryGetError(out var error)) {
            return Result<ImmutableArray<NamespaceOccupant>>.Failure(error);
        }

        var occupants = ImmutableArray.CreateBuilder<NamespaceOccupant>();

        foreach (var found in listed.GetValueOrThrow()) {
            occupants.Add(
                new() {
                    Kind = found.Kind.Kind,
                    Name = found.Name,
                    Labels = found.Labels.ToImmutableDictionary(StringComparer.Ordinal)
                }
            );
        }

        return Result<ImmutableArray<NamespaceOccupant>>.Success(occupants.ToImmutable());
    }
}
