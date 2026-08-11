using System.Threading.Channels;

namespace CyberCloud.Sdk;

/// <summary>
///     The polling machinery behind <see cref="Operation" /> and <see cref="Operation{T}" />: it owns
///     the poll URL, the state, the progress feed and the delay, and it is the only thing in this SDK
///     that issues an operation poll.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>There is no background task and no timer of our own, and that is a design choice
///         rather than an omission.</b> Every poll is driven by a caller that is awaiting —
///         <c>WaitForCompletionAsync</c>, <c>UpdateStatusAsync</c> or the <c>GetProgressAsync</c>
///         enumerator. The only timer that ever exists is inside
///         <c>Task.Delay(interval, cancellationToken)</c>, which that token cancels and disposes. So a
///         cancelled wait leaves nothing running: no orphaned loop keeps polling a nine-minute cluster
///         creation after the caller has walked away, and there is no <c>Dispose</c> to forget. The
///         alternative — a self-driving loop feeding a channel — has to be started, stopped and
///         disposed, and gets all three wrong the first time somebody enumerates progress inside a
///         block that returns early.
///     </para>
///     <para>
///         Concurrent callers are deduplicated rather than turned into two round trips: a caller that
///         finds a poll already in flight waits for it, re-checks the poll counter, and skips its own
///         when the answer it was about to ask for has just arrived.
///     </para>
/// </remarks>
sealed class OperationPoller {
    /// <summary>
    ///     ⚠ Never disposed, and that is safe. <see cref="SemaphoreSlim" /> allocates a kernel handle
    ///     only when <c>AvailableWaitHandle</c> is read, which nothing here does. Making it disposable
    ///     would push <c>IDisposable</c> onto <see cref="Operation{T}" />, which callers would not
    ///     call.
    /// </summary>
    readonly SemaphoreSlim gate = new(1, 1);

    readonly OperationProgressFeed feed = new();
    readonly CyberCloudClientContext context;
    readonly Uri pollUri;
    readonly Uri? resourceUri;
    readonly string operationName;

    long pollCount;

    internal OperationPoller(
        CyberCloudClientContext context,
        Uri requestUri,
        Response initialResponse,
        string operationName,
        bool fetchResourceOnSuccess) {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requestUri);
        ArgumentNullException.ThrowIfNull(initialResponse);

        this.context = context;
        this.operationName = operationName;

        // ⚠ `Azure-AsyncOperation` is required on the 202 — openapi/2026-08-01.json, the 202 response
        // of every write. Without it there is no operation to poll and no way to tell the caller what
        // happened, so this is a hard failure at construction rather than a null that surfaces later
        // as a NullReferenceException inside WaitForCompletionAsync.
        if (!initialResponse.TryGetHeader(CyberCloudHeaders.AsyncOperation, out var location)
            || !Uri.TryCreate(location, UriKind.Absolute, out var uri))
            throw new CyberCloudRequestFailedException(
                $"The response to {operationName} has no usable {CyberCloudHeaders.AsyncOperation} header, so the "
                + "operation cannot be polled — docs/plan/10 § Long-running operations, over HTTP.");

        pollUri = uri;

        // docs/plan/10 § Long-running operations, over HTTP: "→ 200 { \"status\": \"Succeeded\" } →
        // then GET the resource". The resource's URL is the URL of the request that started the
        // operation. A DELETE has no resource left to read, which is what fetchResourceOnSuccess is
        // false for.
        resourceUri = fetchResourceOnSuccess ? requestUri : null;

        RawResponse = initialResponse;
        NextDelay = initialResponse.RetryAfter;
    }

    /// <summary>The most recent response — the <c>202</c> until the first poll, then each poll's.</summary>
    public Response RawResponse { get; private set; }

    /// <summary>The most recent parsed status, or <see langword="null" /> before the first poll.</summary>
    public OperationStatus? Status { get; private set; }

    /// <summary>Whether the operation has reached a terminal state.</summary>
    public bool HasCompleted { get; private set; }

    /// <summary>The <c>GET</c> of the resource, once the operation has succeeded and it has been fetched.</summary>
    public Response? ResourceResponse { get; private set; }

    /// <summary>The operation id — the last segment of the poll URL.</summary>
    public string Id => pollUri.Segments[^1].TrimEnd('/');

    /// <summary>How long to wait before the next poll, from the last response's <c>Retry-After</c>.</summary>
    TimeSpan? NextDelay { get; set; }

    /// <summary>
    ///     The delay before the next poll: the client's override when one is configured, otherwise the
    ///     service's <c>Retry-After</c>, otherwise a floor.
    /// </summary>
    /// <remarks>
    ///     ⚠ The service's number wins by default because docs/plan/10 § Long-running operations, over
    ///     HTTP sends it as back-pressure. A client that polls a nine-minute operation every 100 ms
    ///     spends its rate-limit budget (docs/plan/10 § Rate limiting) finding out nothing changed.
    /// </remarks>
    public TimeSpan CurrentDelay => context.PollingInterval ?? NextDelay ?? TimeSpan.FromSeconds(1);

    /// <summary>Opens a subscription to the progress array.</summary>
    public Channel<OperationProgress> Subscribe() => feed.Subscribe();

    /// <summary>Closes a subscription.</summary>
    public void Unsubscribe(Channel<OperationProgress> channel) => feed.Unsubscribe(channel);

    /// <summary>Throws when the operation reached a terminal failure.</summary>
    /// <exception cref="CyberCloudRequestFailedException">The operation failed or was cancelled.</exception>
    public void ThrowIfFailed() {
        if (Status is not { } status || status.State is not (OperationState.Failed or OperationState.Canceled))
            return;

        // ⚠ The error is carried by the poll response, which is a 200 — the poll worked and reported a
        // failure. CyberCloudRequestFailedException's own doc comment says so, because a caller who
        // branched on Status alone would otherwise conclude the operation succeeded.
        throw new CyberCloudRequestFailedException(
            RawResponse,
            status.Error ?? new CyberCloudError(
                status.State is OperationState.Canceled ? "OperationCanceled" : "ProvisioningFailed",
                $"{operationName} ended in state {status.State} without an error body."));
    }

    /// <summary>Waits the current delay, then polls once. The step the enumerating and waiting callers repeat.</summary>
    public async ValueTask PollAfterDelayAsync(CancellationToken cancellationToken) {
        var delay = CurrentDelay;

        if (delay > TimeSpan.Zero)
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

        await PollAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Polls once, unless another caller's poll landed while this one was waiting for the gate.</summary>
    public async ValueTask PollAsync(CancellationToken cancellationToken) {
        var observed = Interlocked.Read(ref pollCount);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try {
            if (HasCompleted || Interlocked.Read(ref pollCount) != observed)
                return;

            using var request = context.CreateRequest(HttpMethod.Get, pollUri);
            Apply(await context.Pipeline.SendAsync(request, cancellationToken).ConfigureAwait(false));

            if (!HasCompleted || Status?.State is not OperationState.Succeeded || resourceUri is null || ResourceResponse is not null)
                return;

            using var resource = context.CreateRequest(HttpMethod.Get, resourceUri);
            var response = await context.Pipeline.SendAsync(resource, cancellationToken).ConfigureAwait(false);

            if (response.IsError)
                throw new CyberCloudRequestFailedException(response);

            ResourceResponse = response;
        } finally {
            Interlocked.Increment(ref pollCount);
            gate.Release();
        }
    }

    /// <summary>Folds one poll response into the state. Called under <see cref="gate" />.</summary>
    void Apply(Response response) {
        RawResponse = response;

        // A poll that fails is not an operation that fails: a 404 on the operation URL means the id is
        // wrong or has aged out, and a 429 means we polled too fast. Both are request failures and
        // carry the one error shape, so they are reported as themselves.
        if (response.IsError)
            throw new CyberCloudRequestFailedException(response);

        var status = OperationStatus.Parse(response.Content);

        Status = status;
        NextDelay = response.RetryAfter;

        // ⚠ Published before HasCompleted is set, so the entries a terminal poll carries are readable
        // by an enumerator about to see the operation finish.
        feed.Publish(status.Progress);

        if (!status.IsTerminal)
            return;

        HasCompleted = true;
        feed.Complete();
    }
}
