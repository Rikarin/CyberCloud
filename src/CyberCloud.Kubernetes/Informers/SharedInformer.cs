using CyberCloud.Kubernetes.Apply;
using Microsoft.Extensions.Logging;

namespace CyberCloud.Kubernetes.Informers;

/// <summary>How an informer's list phase went — what a test and an operator both want to know.</summary>
/// <param name="Resumed">
///     Whether the list resumed from a held <c>resourceVersion</c> rather than starting cold.
/// </param>
/// <param name="FellBackToFullList">
///     Whether a resume was attempted and refused with HTTP 410 Gone, forcing a full list. This is
///     the bounded case docs/plan/09 § Observing allows for — "where the API server still has it".
/// </param>
/// <param name="StaggerDelay">How long establishment waited before listing.</param>
/// <param name="ResourceVersion">The cursor the informer is now at.</param>
/// <param name="ItemCount">How many objects the list returned. Zero on a resume that found nothing new.</param>
/// <param name="Pages">How many pages the list took.</param>
public sealed record InformerEstablishment(
    bool Resumed,
    bool FellBackToFullList,
    TimeSpan StaggerDelay,
    string ResourceVersion,
    int ItemCount,
    int Pages
);

/// <summary>
///     One shared informer: a list-then-watch over one kind in one cluster, filtered by
///     <see cref="KubeLabels.ManagedBySelector" />, shared by every caller that wants that kind.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/09 § Observing:
///         <i>
///             "Each connection grain runs shared informers for the GVKs its
///             tenant's resources use, filtered by <c>cybercloud.io/managed-by=cybercloud</c> … Why
///             informers rather than each reconciler polling: N resources polling is N × rate API calls
///             against a cluster we do not own and may be rate-limited by. One watch per kind is
///             O(kinds)."
///         </i>
///         "Shared" is the load-bearing word — <see cref="Subscribers" /> counts
///         holders and the watch is established once.
///     </para>
///     <para>
///         ⚠ <b>The failure this class is shaped around</b>, quoting docs/plan/09 § Observing:
///         <i>
///             "The informer cache is in one silo's memory and is lost when that silo dies.
///             Re-establishing it is a full list + watch, which for a large cluster is seconds and a
///             burst of API load. Mitigations: resume from the last <c>resourceVersion</c> where the API
///             server still has it, and stagger re-establishment across clusters so a silo restart does
///             not stampede every tenant's API server at once. The second one matters more than it
///             sounds — a 30-silo rolling deploy without staggering is a synchronized list storm."
///         </i>
///         Both mitigations are here: <see cref="InformerStagger" /> before the list, and
///         <see cref="ResourceVersion" /> carried into it.
///     </para>
///     <para>
///         ⚠ <b>The cursor outlives the cache, and that is the point.</b>
///         <see cref="ResourceVersion" /> is small enough to persist in the connection grain's
///         durable state, so a silo that dies takes the <i>cache</i> with it but not the
///         <i>cursor</i> — which is what turns re-establishment from a full list into a delta. The
///         cache is the expensive thing to rebuild and the cursor is the cheap thing that avoids
///         rebuilding it.
///     </para>
/// </remarks>
public sealed class SharedInformer {
    readonly Guid clusterId;
    readonly IKubeApiClient api;
    readonly ILogger logger;
    readonly TimeSpan staggerWindow;
    readonly string ns;

    /// <summary>The kind being watched.</summary>
    public GroupVersionKind Kind { get; }

    /// <summary>
    ///     The selector in force. ⚠ <b>Always</b> contains
    ///     <see cref="KubeLabels.ManagedBySelector" /> — see <see cref="CombineSelector" />.
    /// </summary>
    public string LabelSelector { get; }

    /// <summary>The cursor. Survives the cache; see the remarks on the type.</summary>
    public string ResourceVersion { get; private set; } = string.Empty;

    /// <summary>How many callers hold this informer.</summary>
    public int Subscribers { get; private set; }

    /// <summary>How long the last establishment was staggered by.</summary>
    public TimeSpan LastStaggerDelay { get; private set; }

    /// <summary>The lease describing this informer's current state.</summary>
    public InformerLease Lease =>
        new() {
            ClusterId = clusterId,
            Kind = Kind,
            LabelSelector = LabelSelector,
            ResourceVersion = ResourceVersion,
            StreamNamespace = string.Empty,
            Subscribers = Subscribers,
            StaggerDelay = LastStaggerDelay
        };

    /// <summary>Creates an informer for one kind.</summary>
    /// <param name="clusterId">The cluster. Also the stagger's seed.</param>
    /// <param name="kind">The kind to watch.</param>
    /// <param name="ns">The namespace, or empty to watch cluster-wide.</param>
    /// <param name="extraSelector">
    ///     An additional label selector, ANDed with <see cref="KubeLabels.ManagedBySelector" />.
    /// </param>
    /// <param name="api">The cluster's API client.</param>
    /// <param name="logger">Where establishment is reported.</param>
    /// <param name="staggerWindow">
    ///     The spread. Defaults to <see cref="InformerStagger.DefaultWindow" />; pass
    ///     <see cref="TimeSpan.Zero" /> in a test that does not want to wait.
    /// </param>
    public SharedInformer(
        Guid clusterId,
        GroupVersionKind kind,
        string ns,
        string extraSelector,
        IKubeApiClient api,
        ILogger logger,
        TimeSpan? staggerWindow = null
    ) {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(logger);

        this.clusterId = clusterId;
        this.api = api;
        this.logger = logger;
        this.ns = ns ?? string.Empty;
        this.staggerWindow = staggerWindow ?? InformerStagger.DefaultWindow;

        Kind = kind;
        LabelSelector = CombineSelector(extraSelector);
    }

    /// <summary>Registers a holder. The watch is established once regardless of how many there are.</summary>
    public int Subscribe() => ++Subscribers;

    /// <summary>Releases a holder.</summary>
    public int Unsubscribe() => Subscribers = Math.Max(0, Subscribers - 1);

    /// <summary>Restores a cursor persisted by a previous activation.</summary>
    /// <param name="resourceVersion">The cursor.</param>
    public void ResumeFrom(string? resourceVersion) => ResourceVersion = resourceVersion ?? string.Empty;

    /// <summary>
    ///     Establishes (or re-establishes) the informer: stagger, then list, then hold the cursor.
    /// </summary>
    /// <param name="delay">
    ///     How to wait. Defaults to <see cref="Task.Delay(TimeSpan, CancellationToken)" />; a test
    ///     substitutes a recorder so that the stagger's <i>value</i> is asserted without the suite
    ///     actually sleeping for it.
    /// </param>
    /// <param name="cancellationToken">The activation's token.</param>
    public async Task<Result<InformerEstablishment>> EstablishAsync(
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        CancellationToken cancellationToken = default
    ) {
        var stagger = InformerStagger.DelayFor(clusterId, staggerWindow);
        LastStaggerDelay = stagger;

        if (stagger > TimeSpan.Zero) {
            logger.LogDebug(
                "Informer for {Kind} on cluster {Cluster} is staggering re-establishment by "
                + "{Delay} ms. docs/plan/09 § Observing — a rolling deploy without this is a "
                + "synchronized list storm.",
                Kind,
                clusterId,
                stagger.TotalMilliseconds
            );

            await (delay ?? Task.Delay)(stagger, cancellationToken).ConfigureAwait(false);
        }

        var wanted = ResourceVersion;
        var resumed = wanted.Length > 0;

        var listed = await ListAsync(resumed ? wanted : null, cancellationToken).ConfigureAwait(false);

        if (listed.TryGetError(out var error)) {
            if (!resumed || error.Code != ErrorCode.PreconditionFailed) {
                return Result<InformerEstablishment>.Failure(error);
            }

            // ⚠ HTTP 410 Gone. The cursor is older than the API server's retained history, so the
            // delta we asked for no longer exists. docs/plan/09 § Observing bounds the resume with
            // exactly this caveat, and the only correct response is the expensive one: drop the
            // cursor and list from scratch. Resuming from a compacted cursor would silently skip
            // every change in between, which is worse than the burst of API load.
            logger.LogInformation(
                "Informer for {Kind} on cluster {Cluster} could not resume from resourceVersion "
                + "{ResourceVersion} — the API server has compacted it. Falling back to a full "
                + "list. docs/plan/09 § Observing.",
                Kind,
                clusterId,
                wanted
            );

            ResourceVersion = string.Empty;

            var full = await ListAsync(null, cancellationToken).ConfigureAwait(false);
            if (full.TryGetError(out var fullError)) {
                return Result<InformerEstablishment>.Failure(fullError);
            }

            var afterFallback = full.GetValueOrThrow();
            ResourceVersion = afterFallback.ResourceVersion;

            return Result<InformerEstablishment>.Success(
                new(
                    true,
                    true,
                    stagger,
                    afterFallback.ResourceVersion,
                    afterFallback.ItemCount,
                    afterFallback.Pages
                )
            );
        }

        var outcome = listed.GetValueOrThrow();
        ResourceVersion = outcome.ResourceVersion;

        return Result<InformerEstablishment>.Success(
            new(
                resumed,
                false,
                stagger,
                outcome.ResourceVersion,
                outcome.ItemCount,
                outcome.Pages
            )
        );
    }

    /// <summary>
    ///     Consumes the watch, advancing <see cref="ResourceVersion" /> as events arrive.
    /// </summary>
    /// <param name="onEvent">What to do with each event.</param>
    /// <param name="cancellationToken">Stops the watch.</param>
    /// <remarks>
    ///     Returns when the API server closes the stream, which it does routinely. The caller
    ///     re-establishes; that is why <see cref="EstablishAsync" /> is idempotent and why the
    ///     cursor is advanced on every event rather than at the end.
    /// </remarks>
    public async Task<Result> PumpAsync(
        Func<KubeWatchEvent, Task> onEvent,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(onEvent);

        try {
            await foreach (var change in api
                               .WatchAsync(Kind, ns, LabelSelector, ResourceVersion, cancellationToken)
                               .ConfigureAwait(false)) {
                if (change.Kind == KubeWatchEventKind.Error) {
                    // An ERROR frame is nearly always a 410 wearing a different hat. Dropping the
                    // cursor here means the next EstablishAsync does a full list rather than
                    // re-asking for a delta the server has already said it cannot produce.
                    ResourceVersion = string.Empty;
                    return Result.Failure(
                        ErrorCode.PreconditionFailed,
                        $"The watch on {Kind} in cluster {clusterId:D} returned an error frame; the "
                        + "cursor has been dropped so the next establishment lists in full."
                    );
                }

                if (change.ResourceVersion.Length > 0) {
                    ResourceVersion = change.ResourceVersion;
                }

                if (change.Kind != KubeWatchEventKind.Bookmark) {
                    await onEvent(change).ConfigureAwait(false);
                }
            }

            return Result.Success;
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            return Result.Success;
        }
    }

    /// <summary>
    ///     ANDs the caller's selector with the mandatory one.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The managed-by term is prepended and cannot be removed by a caller.</b>
    ///     docs/plan/09 § Observing filters every informer by
    ///     <c>cybercloud.io/managed-by=cybercloud</c>, and an informer that dropped it would stream
    ///     the tenant's own objects into our resource grains — a cluster we manage may also be
    ///     written to by the tenant (ADR-013 says so explicitly, which is why the admission policy
    ///     exists). Passing a selector that also mentions <c>managed-by</c> is harmless: Kubernetes
    ///     ANDs comma-separated terms, and two identical terms select the same set.
    /// </remarks>
    static string CombineSelector(string? extra) =>
        string.IsNullOrWhiteSpace(extra)
            ? KubeLabels.ManagedBySelector
            : KubeLabels.ManagedBySelector + "," + extra.Trim();

    async Task<Result<ListOutcome>> ListAsync(string? resourceVersion, CancellationToken cancellationToken) {
        var items = 0;
        var pages = 0;
        var cursor = string.Empty;
        string? continueToken = null;

        do {
            var page = await api.ListAsync(
                    Kind,
                    ns,
                    LabelSelector,
                    resourceVersion,
                    continueToken,
                    cancellationToken: cancellationToken
                )
                .ConfigureAwait(false);

            if (page.TryGetError(out var error)) {
                return Result<ListOutcome>.Failure(error);
            }

            var body = page.GetValueOrThrow();
            pages++;
            items += body.Items.Count;

            if (body.ResourceVersion.Length > 0) {
                cursor = body.ResourceVersion;
            }

            continueToken = body.ContinueToken.Length > 0 ? body.ContinueToken : null;
        } while (continueToken is not null && !cancellationToken.IsCancellationRequested);

        return Result<ListOutcome>.Success(new(cursor, items, pages));
    }

    sealed record ListOutcome(string ResourceVersion, int ItemCount, int Pages);
}
