namespace CyberCloud.Kubernetes.Apply;

/// <summary>
///     Deletes the object a command names only when it is not another resource's: a read, the
///     <see cref="KubeCommand.CheckDeleteAgainst" /> comparison, and then the delete.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Here and not inside <see cref="IKubeApiClient.DeleteAsync" /></b>, because that call
///         takes the object's address and nothing else — the tunnel carries it that way to the agent
///         — and the resource a delete is made on behalf of is on the command. Every caller that holds
///         a command runs this: <c>ClusterConnectionGrain</c>, and the cluster-backed suites'
///         <c>RealClusterConnection</c>, so the rule a real k3s exercises is the one production runs.
///     </para>
///     <para>
///         ⚠ <b>An absent object answers success without a delete</b>, which is what the delete would
///         have answered: <see cref="KubeApiClient.DeleteAsync" /> maps a 404 that names the object to
///         success. A 404 for a kind the cluster does not serve is <c>InvalidResourceType</c> on the
///         read as on the delete, and is returned as it is.
///     </para>
/// </remarks>
public static class OwnedDelete {
    /// <summary>Reads, checks and deletes.</summary>
    /// <param name="api">The client of the cluster the command addresses.</param>
    /// <param name="command">The built command.</param>
    /// <param name="policy">How to cascade.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>
    ///     The delete's answer; <see cref="ErrorCode.Conflict" /> without a delete when the object is
    ///     labelled for another resource; the read's failure when the read could not be made.
    /// </returns>
    public static async Task<Result> DeleteAsync(
        IKubeApiClient api,
        KubeCommand command,
        CascadePolicy policy,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(command);

        var live = await api.GetAsync(command.Target, cancellationToken).ConfigureAwait(false);

        if (live.TryGetError(out var readError)) {
            return readError.Code == ErrorCode.ResourceNotFound ? Result.Success : Result.Failure(readError);
        }

        var owned = command.CheckDeleteAgainst(live.GetValueOrThrow());

        return owned.IsFailure
            ? owned
            : await api.DeleteAsync(command.Target, policy, cancellationToken).ConfigureAwait(false);
    }
}
