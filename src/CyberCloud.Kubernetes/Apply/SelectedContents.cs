namespace CyberCloud.Kubernetes.Apply;

/// <summary>
///     Lists one kind in one namespace under a label selector, paging until the API server says the
///     list is complete.
/// </summary>
/// <remarks>
///     <para>
///         The narrow sibling of <see cref="NamespaceContents" />: one REST path instead of a
///         discovery plus one per kind, which is what makes it affordable on the reconcile path. A
///         teardown asks this for the claims an operator gave the resource it is about to remove,
///         and a purge asks it again to name what the window kept.
///     </para>
///     <para>
///         ⚠ <b>Never a partial answer, for the same reason as the wide one.</b> A caller reads
///         "these are the claims" as "detach exactly these"; a page that failed halfway would leave
///         the claims it did not reach owned by an object about to be deleted, and the garbage
///         collector would take them. So a failed page fails the listing.
///     </para>
/// </remarks>
public static class SelectedContents {
    /// <summary>
    ///     Every object of <paramref name="kind" /> in <paramref name="ns" /> that
    ///     <paramref name="labelSelector" /> matches.
    /// </summary>
    /// <param name="api">The cluster's client.</param>
    /// <param name="clusterId">The cluster, for the messages.</param>
    /// <param name="kind">The kind to list.</param>
    /// <param name="ns">The namespace to list in.</param>
    /// <param name="labelSelector">The selector. ⚠ Refused when empty — see the remarks.</param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>Every match, or the failure that stopped the listing.</returns>
    /// <remarks>
    ///     ⚠ <b>An empty selector is refused rather than read as "everything".</b> Every caller of
    ///     this is looking for its own objects, and a selector that drifted to empty — a name that
    ///     was never filled in — would hand a teardown every claim in the namespace, including the
    ///     ones that belong to a different resource. <see cref="NamespaceContents" /> is the member
    ///     for "everything", and it says so in its own remarks.
    /// </remarks>
    public static async Task<Result<IReadOnlyList<KubeObjectSummary>>> ListAsync(
        IKubeApiClient api,
        Guid clusterId,
        GroupVersionKind kind,
        string ns,
        string labelSelector,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(kind);

        if (string.IsNullOrEmpty(ns) || string.IsNullOrEmpty(labelSelector)) {
            return Result<IReadOnlyList<KubeObjectSummary>>.Failure(
                ErrorCode.InvalidRequestBody,
                $"A selected listing of {kind} on cluster {clusterId:D} was asked for with "
                + (string.IsNullOrEmpty(ns) ? "no namespace" : "no label selector")
                + ". Every caller of this listing is looking for its own objects, so an empty "
                + "selector is a name nobody filled in rather than a request for everything."
            );
        }

        var found = new List<KubeObjectSummary>();
        var continueToken = string.Empty;

        do {
            cancellationToken.ThrowIfCancellationRequested();

            var page = await api.ListAsync(
                kind,
                ns,
                labelSelector,
                continueToken: string.IsNullOrEmpty(continueToken) ? null : continueToken,
                cancellationToken: cancellationToken
            )
                .ConfigureAwait(false);

            if (page.TryGetError(out var listError)) {
                return Result<IReadOnlyList<KubeObjectSummary>>.Failure(
                    listError.Code,
                    $"Listing {kind} matching '{labelSelector}' in namespace '{ns}' on cluster "
                    + $"{clusterId:D} failed: {listError.Message} A listing with a hole in it "
                    + "would leave the objects it did not reach unaccounted for, so nothing is "
                    + "concluded from it."
                );
            }

            var value = page.GetValueOrThrow();

            foreach (var item in value.Items) {
                if (NamespaceContents.Summarize(item, kind, ns) is { } summary) {
                    found.Add(summary);
                }
            }

            continueToken = value.ContinueToken;
        } while (!string.IsNullOrEmpty(continueToken));

        return Result<IReadOnlyList<KubeObjectSummary>>.Success(found);
    }
}
