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
    ///     <see cref="ApplyResult.Updated" />. On success the body includes
    ///     <c>metadata.managedFields</c>, which is how the apply path then tells
    ///     <see cref="ApplyResult.Updated" /> from <see cref="ApplyResult.Unchanged" />.
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

    /// <summary>
    ///     Replaces an object's <c>metadata.ownerReferences</c> with one controller, or with nothing.
    /// </summary>
    /// <param name="target">The dependent.</param>
    /// <param name="owner">The controller to write, or <see langword="null" /> to clear the list.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>
    ///     Success, or <see cref="ErrorCode.ResourceNotFound" /> when there is no such object. ⚠ A
    ///     JSON merge patch, never an apply — <c>IKubeClusterConnection.SetOwnerAsync</c> says why.
    /// </returns>
    Task<Result> SetOwnerAsync(
        ObjectRef target,
        OwnerRef? owner,
        CancellationToken cancellationToken = default
    );

    /// <summary>The last lines one container of a pod wrote — the pod's <c>log</c> subresource.</summary>
    /// <param name="pod">The pod.</param>
    /// <param name="container">The container, or empty for the pod's only one.</param>
    /// <param name="tailLines">How many lines from the end.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>
    ///     The text; <see cref="ErrorCode.ResourceNotFound" /> for a pod that is not there;
    ///     <see cref="ErrorCode.OperationInProgress" /> for a container that has not started.
    /// </returns>
    /// <remarks>
    ///     ⚠ Fails by default, so a client that cannot reach a kubelet's log says so rather than
    ///     answering an empty log. <c>KubeApiClient</c> reads the subresource; the agent tunnel carries
    ///     the call as <c>TunnelOperations.ReadLogs</c>. <c>IKubeClusterConnection.ReadLogsAsync</c>.
    /// </remarks>
    Task<Result<string>> ReadLogsAsync(
        ObjectRef pod,
        string container,
        int tailLines,
        CancellationToken cancellationToken = default
    ) =>
        Task.FromResult(
            // InternalError, as IKubeClusterConnection's default says: a client that cannot read a log
            // is the platform's gap and not the caller's body (#28's review; it read InvalidRequestBody).
            Result<string>.Failure(
                ErrorCode.InternalError,
                $"This client ({GetType().Name}) cannot read a pod's log, so the logs of '{pod}' were not "
                + "read. It fails rather than answering an empty log, which would say the container wrote "
                + "nothing."
            )
        );

    /// <summary>
    ///     Every namespaced kind this cluster serves, one version per group — API discovery.
    /// </summary>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>
    ///     The kinds, or the failure that stopped discovery. ⚠ <b>Never a partial answer.</b> The one
    ///     caller enumerates a namespace in order to decide whether it is empty, and a kind missing
    ///     from this list is a kind whose objects are invisible to that decision.
    /// </returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>ONE VERSION PER GROUP, AND IT IS THE PREFERRED ONE.</b> A group that serves
    ///         <c>v1</c> and <c>v1beta1</c> stores each object once and serves it under both, so
    ///         listing every advertised version would count every object as many times as it has
    ///         versions. That inflates a refusal harmlessly and would corrupt any count built on top
    ///         of it, so the ambiguity is removed here rather than left to each caller.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             Subresources and kinds that cannot be listed are excluded, and both exclusions
    ///             are on evidence the API server gives rather than on a name.
    ///         </b> A subresource is
    ///         discovered as <c>pods/log</c> — a name with a slash — and addressing it as a
    ///         collection is a <c>404</c>. A resource whose <c>verbs</c> omit <c>list</c>
    ///         (<c>bindings</c>, the <c>*accessreviews</c>) is create-only and answers <c>405</c>.
    ///         Neither is a thing that can be <i>in</i> a namespace, so neither is a thing whose
    ///         absence could be mistaken for emptiness.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             Two kinds ARE listed twice on every cluster and that is not this method's to
    ///             fix
    ///         </b>: <c>v1 Event</c> and <c>events.k8s.io/v1 Event</c> are different groups over
    ///         the same storage. They are different <see cref="GroupVersionKind" />s by every rule
    ///         available here, and the caller that cares is the one that knows an Event is not
    ///         evidence of occupancy.
    ///     </para>
    /// </remarks>
    Task<Result<IReadOnlyList<GroupVersionKind>>> DiscoverNamespacedKindsAsync(
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

    /// <summary>
    ///     Opens the <c>pods/attach</c> stream to a container's terminal: standard input, standard
    ///     output and the resize channel.
    /// </summary>
    /// <param name="pod">The pod.</param>
    /// <param name="container">The container whose process to join.</param>
    /// <param name="cancellationToken">Stops the handshake.</param>
    /// <returns>The open terminal, which the caller disposes, or the API server's refusal.</returns>
    /// <remarks>
    ///     ⚠ <b>Refuses by default.</b> Only a client with a socket to an API server can attach; the
    ///     agent tunnel carries request frames and has no stream frame yet, and the recording doubles
    ///     have nothing to attach to. A default that answered would be a terminal wired to nothing.
    /// </remarks>
    Task<Result<IKubeTerminal>> AttachAsync(
        ObjectRef pod,
        string container,
        CancellationToken cancellationToken = default
    ) =>
        Task.FromResult(
            Result<IKubeTerminal>.Failure(
                ErrorCode.InternalError,
                $"{GetType().Name} cannot attach to '{pod}': it has no stream to an API server."
            )
        );
}
