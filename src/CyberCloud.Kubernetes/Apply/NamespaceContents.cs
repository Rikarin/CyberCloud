using System.Globalization;
using System.Text.Json;

namespace CyberCloud.Kubernetes.Apply;

/// <summary>
///     Enumerates everything one namespace holds: API discovery, then a list per kind, with no label
///     selector.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THE ANSWER THIS PRODUCES AUTHORISES A RECURSIVE DELETE OF A TENANT'S LIVE DATA, SO
///         THE ONLY ACCEPTABLE FAILURE MODE IS A REFUSAL.</b> Every path that cannot finish returns a
///         failure rather than what it managed to collect. A partial listing is not a smaller true
///         answer — it is a wrong one, because the caller reads a kind's absence as "the namespace
///         holds none of those" and an empty result as "delete it".
///     </para>
///     <para>
///         ⚠ <b>It is a discovery plus one list per served kind, which on a busy cluster is a hundred
///         round trips.</b> That cost is why it is not on the reconcile path: the reconcile driver
///         asks <c>NamespaceEnsurer</c> whether the namespace exists, which is one apply an hour.
///         This runs once, when a resource group is deleted.
///     </para>
///     <para>
///         ⚠ <b>What it does not do is watch.</b> docs/plan/09 § Observing's informer bridge answers
///         a different question — one named kind under
///         <see cref="KubeLabels.ManagedBySelector" /> — and growing it into this would mean an
///         informer per served kind per cluster, held open forever, to serve a call that happens once
///         per resource group.
///     </para>
/// </remarks>
public static class NamespaceContents {
    /// <summary>
    ///     How many objects a namespace may hold before the enumeration refuses to finish.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A ceiling and not a page size, and exceeding it is a failure rather than a
    ///     truncation.</b> The caller decides whether a namespace is empty; a truncated listing is
    ///     still evidence of "not empty", but returning one would put a value in circulation whose
    ///     count is wrong, and the count is quoted to an operator. A namespace with more than this
    ///     many objects in it is not one anything here should be reasoning about deleting.
    /// </remarks>
    public const int MaxObjects = 20_000;

    /// <summary>
    ///     Every object in <paramref name="ns" />, of every namespaced kind
    ///     <paramref name="api" />'s cluster serves.
    /// </summary>
    /// <param name="api">The cluster's client.</param>
    /// <param name="clusterId">The cluster, for the messages.</param>
    /// <param name="ns">The namespace.</param>
    /// <param name="cancellationToken">Cancels the enumeration.</param>
    /// <returns>Everything that is in there, or the failure that stopped it. Never a partial list.</returns>
    public static async Task<Result<IReadOnlyList<KubeObjectSummary>>> ListAsync(
        IKubeApiClient api,
        Guid clusterId,
        string ns,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentException.ThrowIfNullOrEmpty(ns);

        var discovered = await api.DiscoverNamespacedKindsAsync(cancellationToken).ConfigureAwait(false);

        if (discovered.TryGetError(out var discoveryError)) {
            return Result<IReadOnlyList<KubeObjectSummary>>.Failure(
                discoveryError.Code,
                $"Cluster {clusterId:D} could not say which kinds it serves, so nothing can say what "
                + $"namespace '{ns}' holds: {discoveryError.Message}"
            );
        }

        var found = new List<KubeObjectSummary>();

        foreach (var kind in discovered.GetValueOrThrow()) {
            var continueToken = string.Empty;

            do {
                cancellationToken.ThrowIfCancellationRequested();

                var page = await api.ListAsync(
                        kind,
                        ns,
                        // ⚠ NO SELECTOR. The drift scan's inventory filters on
                        // `managed-by=cybercloud`; this one must find the objects that filter hides,
                        // because those are the ones a namespace delete would destroy.
                        string.Empty,
                        continueToken: string.IsNullOrEmpty(continueToken) ? null : continueToken,
                        cancellationToken: cancellationToken
                    )
                    .ConfigureAwait(false);

                if (page.TryGetError(out var listError)) {
                    // ⚠ THE WHOLE ENUMERATION FAILS ON ONE KIND. Returning what was read so far
                    // would report this kind's objects as absent, and absence is the answer that
                    // authorises the delete.
                    return Result<IReadOnlyList<KubeObjectSummary>>.Failure(
                        listError.Code,
                        $"Namespace '{ns}' on cluster {clusterId:D} could not be enumerated: listing "
                        + $"{kind} failed with {listError.Message} A listing with a hole in it is not "
                        + "evidence that the namespace is empty, so nothing is concluded from it."
                    );
                }

                var value = page.GetValueOrThrow();

                foreach (var item in value.Items) {
                    if (Read(item, kind, ns) is { } summary) {
                        found.Add(summary);
                    }

                    if (found.Count > MaxObjects) {
                        return Result<IReadOnlyList<KubeObjectSummary>>.Failure(
                            ErrorCode.Conflict,
                            $"Namespace '{ns}' on cluster {clusterId:D} holds more than "
                            + MaxObjects.ToString(CultureInfo.InvariantCulture)
                            + " objects, which is past what this enumeration will carry. It is "
                            + "plainly not empty, so nothing that depends on this listing may "
                            + "proceed anyway."
                        );
                    }
                }

                continueToken = value.ContinueToken;
            } while (!string.IsNullOrEmpty(continueToken));
        }

        return Result<IReadOnlyList<KubeObjectSummary>>.Success(found);
    }

    /// <summary>
    ///     One listed item, reduced to kind, name and labels, or <see langword="null" /> when it has
    ///     no name.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>An item with no <c>metadata.name</c> is skipped and that is the one safe direction to
    ///     be wrong in here — it removes an occupant from the listing.</b> It is also not reachable
    ///     from a real API server: every object in etcd has a name. The alternative, synthesising a
    ///     placeholder name, would put a refusal message in front of an operator naming an object
    ///     that does not exist.
    /// </remarks>
    static KubeObjectSummary? Read(string json, GroupVersionKind kind, string ns) {
        JsonDocument document;

        try {
            document = JsonDocument.Parse(json);
        } catch (JsonException) {
            return null;
        }

        using (document) {
            if (!document.RootElement.TryGetProperty("metadata", out var metadata)
                || metadata.ValueKind != JsonValueKind.Object) {
                return null;
            }

            if (!metadata.TryGetProperty("name", out var name)
                || name.ValueKind != JsonValueKind.String
                || name.GetString() is not { Length: > 0 } text) {
                return null;
            }

            var labels = new Dictionary<string, string>(StringComparer.Ordinal);

            // ⚠ ABSENT IS NOT NULL AND NOT EMPTY-BUT-OURS. An object with no `metadata.labels`
            // carries no `managed-by`, which reads as "not this platform's" — the conservative
            // answer, and the only one that cannot turn into a delete.
            if (metadata.TryGetProperty("labels", out var written) && written.ValueKind == JsonValueKind.Object) {
                foreach (var label in written.EnumerateObject()) {
                    if (label.Value.ValueKind == JsonValueKind.String) {
                        labels[label.Name] = label.Value.GetString() ?? string.Empty;
                    }
                }
            }

            return new() { Kind = kind, Namespace = ns, Name = text, Labels = labels };
        }
    }
}
