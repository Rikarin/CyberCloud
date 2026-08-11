using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace CyberCloud.Sdk;

/// <summary>
///     Turns the final response of a long-running operation into the typed value the caller asked
///     for. Implemented by generated code, one per resource type.
/// </summary>
/// <typeparam name="T">The value — a generated <c>{Type}Resource</c>.</typeparam>
/// <remarks>
///     ⚠ <b>The final response is a <c>GET</c> of the resource, not the last poll.</b> docs/plan/10
///     § Long-running operations, over HTTP: <c>200 { "status": "Succeeded" }</c> <i>"→ then GET the
///     resource"</i>. The operation body says whether it worked; it does not contain the resource.
///     <see cref="Operation{T}" /> issues that <c>GET</c> against the URL of the request that started
///     the operation and hands the response here.
/// </remarks>
public interface IOperationSource<T> {
    /// <summary>Builds the value from the resource's response.</summary>
    /// <param name="response">The <c>GET</c> of the resource, after the operation succeeded.</param>
    /// <param name="cancellationToken">The token.</param>
    ValueTask<T> CreateResultAsync(Response response, CancellationToken cancellationToken);
}

/// <summary>
///     A long-running operation with no value — a delete. docs/plan/10 § Long-running operations,
///     over HTTP.
/// </summary>
/// <remarks>
///     Everything <see cref="Operation{T}" />'s remarks say about polling, progress and cancellation
///     applies here; the only difference is that there is no resource left to <c>GET</c> when it
///     succeeds.
/// </remarks>
public class Operation {
    readonly OperationPoller poller;

    /// <summary>Starts tracking an operation from the <c>202</c> that began it.</summary>
    /// <param name="context">The client context.</param>
    /// <param name="requestUri">The URL of the request that started the operation.</param>
    /// <param name="initialResponse">The <c>202</c>, carrying <c>Azure-AsyncOperation</c> and <c>Retry-After</c>.</param>
    /// <param name="operationName"><c>{Type}.{Verb}</c> — <c>Widgets.Delete</c>.</param>
    public Operation(CyberCloudClientContext context, Uri requestUri, Response initialResponse, string operationName) {
        poller = new OperationPoller(context, requestUri, initialResponse, operationName, fetchResourceOnSuccess: false);
    }

    /// <summary>Constructor for mocking.</summary>
    protected Operation() => poller = null!;

    /// <summary>The operation id — the last segment of the poll URL.</summary>
    public virtual string Id => poller.Id;

    /// <summary>Whether the operation has reached a terminal state.</summary>
    public virtual bool HasCompleted => poller.HasCompleted;

    /// <summary>The last poll's parsed status, or <see langword="null" /> before the first poll.</summary>
    public virtual OperationStatus? Status => poller.Status;

    /// <summary>How far along, 0–100, or <see langword="null" />.</summary>
    public virtual int? PercentComplete => poller.Status?.PercentComplete;

    /// <summary>The most recent response — the <c>202</c> until the first poll, then each poll's.</summary>
    public virtual Response GetRawResponse() => poller.RawResponse;

    /// <inheritdoc cref="Operation{T}.GetProgressAsync" />
    public virtual IAsyncEnumerable<OperationProgress> GetProgressAsync(CancellationToken cancellationToken = default)
        => OperationProgressEnumerator.Enumerate(poller, cancellationToken);

    /// <summary>Polls once.</summary>
    /// <param name="cancellationToken">The token.</param>
    public virtual async ValueTask<Response> UpdateStatusAsync(CancellationToken cancellationToken = default) {
        await poller.PollAsync(cancellationToken).ConfigureAwait(false);
        poller.ThrowIfFailed();

        return poller.RawResponse;
    }

    /// <summary>Polls until the operation reaches a terminal state.</summary>
    /// <param name="cancellationToken">The token.</param>
    public virtual async ValueTask<Response> WaitForCompletionResponseAsync(CancellationToken cancellationToken = default) {
        while (!poller.HasCompleted)
            await poller.PollAfterDelayAsync(cancellationToken).ConfigureAwait(false);

        poller.ThrowIfFailed();

        return poller.RawResponse;
    }
}

/// <summary>
///     A long-running operation that produces a value — docs/plan/21 § The .NET SDK's worked example:
///     <code>
///     Operation&lt;PostgresServerResource&gt; op =
///         await rg.GetPostgresServers().CreateOrUpdateAsync(WaitUntil.Started, "main", data);
///
///     await foreach (var progress in op.GetProgressAsync())      // ← ours; Azure's SDK has no equivalent
///         Console.WriteLine($"{progress.PercentComplete}% {progress.Message}");
///
///     PostgresServerResource server = await op.WaitForCompletionAsync();
///     </code>
/// </summary>
/// <typeparam name="T">The value — a generated <c>{Type}Resource</c>.</typeparam>
/// <remarks>
///     <para>
///         ⚠ <b>A short token does not end a long operation.</b> Polls go through the client's
///         pipeline, whose <see cref="BearerTokenHandler" /> sits inside the retry handler and
///         re-reads the token cache on every attempt. A nine-minute provision therefore survives
///         docs/plan/11 § Protocol's 10-minute tokens without the caller doing anything, and
///         <c>TokenRefreshedMidPollTests</c> is the test that says so.
///     </para>
///     <para>
///         ⚠ <b>Cancelling a wait stops the polling and leaves nothing behind.</b> See
///         <see cref="OperationPoller" /> — no background task, no timer of ours, and the only delay
///         is a <c>Task.Delay</c> the caller's token cancels.
///     </para>
/// </remarks>
public class Operation<T> : Operation {
    readonly OperationPoller poller;
    readonly IOperationSource<T> source;

    T? value;

    /// <summary>Starts tracking an operation from the <c>202</c> that began it.</summary>
    /// <param name="source">Builds the value from the resource's response once the operation succeeds.</param>
    /// <param name="context">The client context.</param>
    /// <param name="requestUri">
    ///     The URL of the request that started the operation. ⚠ It is where the resource is read from
    ///     when the operation succeeds — docs/plan/10 § Long-running operations, over HTTP: <i>"then
    ///     GET the resource"</i>.
    /// </param>
    /// <param name="initialResponse">The <c>202</c>, carrying <c>Azure-AsyncOperation</c> and <c>Retry-After</c>.</param>
    /// <param name="operationName"><c>{Type}.{Verb}</c> — <c>Widgets.CreateOrUpdate</c>.</param>
    public Operation(
        IOperationSource<T> source,
        CyberCloudClientContext context,
        Uri requestUri,
        Response initialResponse,
        string operationName) {
        ArgumentNullException.ThrowIfNull(source);

        this.source = source;
        poller = new OperationPoller(context, requestUri, initialResponse, operationName, fetchResourceOnSuccess: true);
    }

    /// <summary>Constructor for mocking.</summary>
    protected Operation() {
        poller = null!;
        source = null!;
    }

    /// <inheritdoc />
    public override string Id => poller.Id;

    /// <inheritdoc />
    public override bool HasCompleted => poller.HasCompleted;

    /// <inheritdoc />
    public override OperationStatus? Status => poller.Status;

    /// <inheritdoc />
    public override int? PercentComplete => poller.Status?.PercentComplete;

    /// <summary>Whether the value has been produced.</summary>
    public virtual bool HasValue { get; private set; }

    /// <summary>The value.</summary>
    /// <exception cref="InvalidOperationException"><see cref="HasValue" /> is <see langword="false" />.</exception>
    public virtual T Value => HasValue
        ? value!
        : throw new InvalidOperationException(
            "The operation has not produced a value yet. Await WaitForCompletionAsync() first, or check HasValue.");

    /// <inheritdoc />
    public override Response GetRawResponse() => poller.RawResponse;

    /// <summary>
    ///     The operation's progress entries, streamed as each poll returns them. <b>Ours; Azure's SDK
    ///     has no equivalent</b> — docs/plan/21 § The .NET SDK.
    /// </summary>
    /// <param name="cancellationToken">
    ///     Stops the enumeration and the polling it drives. Cancelling leaves no timer and no
    ///     background work.
    /// </param>
    /// <remarks>
    ///     <para>
    ///         Enumerating <b>drives the polling</b>: each turn of the loop waits the current interval,
    ///         polls once, and yields whatever that poll added. So entries appear as they happen —
    ///         which is the whole point, and what <c>ProgressStreamsIncrementallyTests</c> asserts by
    ///         counting round trips between yields.
    ///     </para>
    ///     <para>
    ///         The enumeration ends when the operation reaches a terminal state, <b>including a
    ///         failure</b>. ⚠ It does not throw: a progress stream is a narration, and a caller who
    ///         wrote the loop in docs/plan/21's example expects the failure from
    ///         <see cref="WaitForCompletionAsync" /> on the next line, not from the
    ///         <c>await foreach</c>. Check <see cref="Operation.Status" /> or await the completion.
    ///     </para>
    ///     <para>
    ///         Enumerating a second time, or after the operation has finished, replays the whole
    ///         history — the feed remembers what it published.
    ///     </para>
    /// </remarks>
    public override IAsyncEnumerable<OperationProgress> GetProgressAsync(CancellationToken cancellationToken = default)
        => OperationProgressEnumerator.Enumerate(poller, cancellationToken);

    /// <inheritdoc />
    public override async ValueTask<Response> UpdateStatusAsync(CancellationToken cancellationToken = default) {
        await poller.PollAsync(cancellationToken).ConfigureAwait(false);
        poller.ThrowIfFailed();
        await EnsureValueAsync(cancellationToken).ConfigureAwait(false);

        return poller.RawResponse;
    }

    /// <summary>Polls until the operation finishes, then returns the resource.</summary>
    /// <param name="cancellationToken">The token. Cancelling stops the polling promptly.</param>
    /// <exception cref="CyberCloudRequestFailedException">The operation failed or was cancelled.</exception>
    public virtual async ValueTask<Response<T>> WaitForCompletionAsync(CancellationToken cancellationToken = default) {
        while (!poller.HasCompleted)
            await poller.PollAfterDelayAsync(cancellationToken).ConfigureAwait(false);

        poller.ThrowIfFailed();
        await EnsureValueAsync(cancellationToken).ConfigureAwait(false);

        return Response.FromValue(Value, poller.RawResponse);
    }

    async ValueTask EnsureValueAsync(CancellationToken cancellationToken) {
        if (HasValue || poller.ResourceResponse is null)
            return;

        value = await source.CreateResultAsync(poller.ResourceResponse, cancellationToken).ConfigureAwait(false);
        HasValue = true;
    }
}

/// <summary>
///     The one enumerator behind both operation types' <c>GetProgressAsync</c>. A separate type
///     because an iterator cannot be inherited and the loop is the part that has to be identical.
/// </summary>
static class OperationProgressEnumerator {
    public static async IAsyncEnumerable<OperationProgress> Enumerate(
        OperationPoller poller,
        [EnumeratorCancellation] CancellationToken cancellationToken) {
        Channel<OperationProgress> subscription = poller.Subscribe();

        try {
            while (true) {
                // Drain first, so everything the previous poll published is yielded before this turn
                // decides whether to poll again — and so a terminal poll's entries are delivered
                // before the loop notices the operation has finished. Getting this order wrong loses
                // the last progress line of every operation, which is the one saying what it finished.
                while (subscription.Reader.TryRead(out var entry))
                    yield return entry;

                if (poller.HasCompleted)
                    yield break;

                await poller.PollAfterDelayAsync(cancellationToken).ConfigureAwait(false);
            }
        } finally {
            poller.Unsubscribe(subscription);
        }
    }
}
