namespace CyberCloud.Kubernetes.Apply;

/// <summary>One page of a list, plus the cursor an informer resumes from.</summary>
/// <param name="Items">The objects, as JSON.</param>
/// <param name="ResourceVersion">The list's <c>metadata.resourceVersion</c> — the watch cursor.</param>
/// <param name="ContinueToken">The pagination cursor, or empty when the list is complete.</param>
public sealed record ListPage(
    IReadOnlyList<string> Items,
    string ResourceVersion,
    string ContinueToken
);

/// <summary>What kind of change a watch reported.</summary>
public enum KubeWatchEventKind {
    /// <summary>Not an event.</summary>
    Unknown = 0,

    /// <summary>The object appeared, or was seen for the first time in this watch.</summary>
    Added = 1,

    /// <summary>The object changed.</summary>
    Modified = 2,

    /// <summary>The object went away.</summary>
    Deleted = 3,

    /// <summary>
    ///     A bookmark: no object changed, but the API server is telling us a newer
    ///     <c>resourceVersion</c> is safe to resume from.
    /// </summary>
    /// <remarks>
    ///     ⚠ This is what makes a resume survive a quiet cluster. Without bookmarks, an informer
    ///     watching a kind that has not changed for an hour still holds an hour-old
    ///     <c>resourceVersion</c>, which is exactly the cursor most likely to have been compacted
    ///     when the silo restarts — so the quietest clusters would be the ones that always fall back
    ///     to a full list. docs/plan/09 § Observing wants the resume to work "where the API server
    ///     still has it", and bookmarks are how the window stays open.
    /// </remarks>
    Bookmark = 4,

    /// <summary>The API server sent an error frame — usually a 410 Gone in disguise.</summary>
    Error = 5
}

/// <summary>One event from a watch.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="Json">The object, as JSON.</param>
/// <param name="ResourceVersion">The object's <c>resourceVersion</c> — the cursor to advance to.</param>
public sealed record KubeWatchEvent(KubeWatchEventKind Kind, string Json, string ResourceVersion);

/// <summary>
///     The narrow surface of a live Kubernetes API server that the rest of this assembly needs.
/// </summary>
/// <remarks>
///     ⚠ <b>This interface is where <c>k8s</c> stops.</b> Nothing it exposes is a
///     <c>k8s.Models</c> type, so <c>ClusterConnectionGrain</c>, the informers and the health tracker
///     are all testable without an API server and none of them re-exports Kubernetes types upward.
///     The <c>KubernetesClient</c> reference is confined to <see cref="KubeApiClient" />.
/// </remarks>
public interface IKubeApiClient : IDisposable {
    /// <summary>Checks the API server answers, for <c>PingAsync</c>.</summary>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>The server's version string on success.</returns>
    Task<Result<string>> PingAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads one object.</summary>
    /// <param name="target">What to read.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>
    ///     A failure carrying <see cref="ErrorCode.ResourceNotFound" /> when the object is absent —
    ///     which the apply path uses to tell <see cref="ApplyResult.Created" /> from
    ///     <see cref="ApplyResult.Updated" />.
    /// </returns>
    Task<Result<KubeObject>> GetAsync(ObjectRef target, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Server-side applies <paramref name="command" /> under its field manager.
    /// </summary>
    /// <param name="command">The built command.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    Task<Result<ApplyOutcome>> ApplyAsync(KubeCommand command, CancellationToken cancellationToken = default);

    /// <summary>Deletes an object.</summary>
    /// <param name="target">What to delete.</param>
    /// <param name="policy">How to cascade.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    Task<Result> DeleteAsync(
        ObjectRef target,
        CascadePolicy policy,
        CancellationToken cancellationToken = default
    );

    /// <summary>Lists a kind, for an informer's initial or resumed list.</summary>
    /// <param name="kind">The kind.</param>
    /// <param name="ns">The namespace, or empty for all namespaces / cluster scope.</param>
    /// <param name="labelSelector">The selector. Always includes <c>managed-by</c> upstream of here.</param>
    /// <param name="resourceVersion">
    ///     The cursor to resume from, or <see langword="null" /> for a full list. ⚠ A resume whose
    ///     cursor the API server has already compacted fails with
    ///     <see cref="ErrorCode.PreconditionFailed" /> — HTTP 410 Gone — which is the caller's signal
    ///     to fall back to a full list. docs/plan/09 § Observing: "resume from the last
    ///     resourceVersion <i>where the API server still has it</i>".
    /// </param>
    /// <param name="continueToken">The pagination cursor from a previous page.</param>
    /// <param name="limit">The page size.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    Task<Result<ListPage>> ListAsync(
        GroupVersionKind kind,
        string ns,
        string labelSelector,
        string? resourceVersion = null,
        string? continueToken = null,
        int? limit = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Watches a kind from <paramref name="resourceVersion" /> onwards.
    /// </summary>
    /// <param name="kind">The kind.</param>
    /// <param name="ns">The namespace, or empty for cluster scope.</param>
    /// <param name="labelSelector">The selector.</param>
    /// <param name="resourceVersion">The cursor the preceding list ended at.</param>
    /// <param name="cancellationToken">Stops the watch.</param>
    /// <remarks>
    ///     The sequence ends when the API server closes the connection, which it does routinely — a
    ///     watch is not a permanent subscription and a caller must re-establish. That is why
    ///     <c>SharedInformer</c> loops rather than awaits.
    /// </remarks>
    IAsyncEnumerable<KubeWatchEvent> WatchAsync(
        GroupVersionKind kind,
        string ns,
        string labelSelector,
        string resourceVersion,
        CancellationToken cancellationToken = default
    );
}
