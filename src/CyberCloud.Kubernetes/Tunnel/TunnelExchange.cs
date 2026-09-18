using CyberCloud.Kubernetes.Contracts.Tunnel;
using System.Collections.Concurrent;
using System.Globalization;

namespace CyberCloud.Kubernetes.Tunnel;

/// <summary>
///     Where a request goes to become a response: the one call <see cref="TunnelKubeApiClient" />
///     makes.
/// </summary>
/// <remarks>
///     Two implementations. <see cref="TunnelExchange" /> multiplexes directly onto a transport and
///     is what a test — and the grain, internally — use. <c>GrainTunnelRoute</c> forwards to
///     <c>IAgentTunnelGrain.ExchangeAsync</c> and is what the connection grain's client gets in
///     production, because the socket is in another process.
/// </remarks>
public interface ITunnelRoute {
    /// <summary>Sends one request and waits for its response.</summary>
    /// <param name="request">A <see cref="TunnelFrameKind.Request" />; the id is assigned by the route.</param>
    /// <param name="cancellationToken">The caller's budget.</param>
    /// <returns>The response frame, or why there is none.</returns>
    Task<Result<TunnelFrame>> ExchangeAsync(TunnelFrame request, CancellationToken cancellationToken = default);
}

/// <summary>
///     The platform side's request multiplexer: assigns correlation ids, sends, and holds each
///     request open until <see cref="Complete" /> is handed its response — or the timeout is.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every pending request has a timeout, and it is the tunnel's, not the caller's.</b> A
///         caller's token is honoured too, but a reconcile pass passes
///         <c>CancellationToken.None</c> through the grain (see <c>ClusterConnectionGrain</c>), so
///         without a timeout of its own a request to an agent that stopped answering — a laptop
///         closed, a pod OOM-killed between frames — would hold a grain turn forever. The default
///         is <see cref="KubernetesOptions.TunnelRequestTimeout" />.
///     </para>
///     <para>
///         <b>Ids are per exchange, not per session.</b> A new session gets a new exchange, so a
///         late response from a dead socket can never complete a request on the socket that
///         replaced it — <see cref="Complete" /> answers <c>false</c> for an id it does not hold.
///     </para>
/// </remarks>
public sealed class TunnelExchange : ITunnelRoute, IDisposable {
    readonly Func<TunnelFrame, CancellationToken, Task> send;
    readonly TimeSpan timeout;
    readonly ConcurrentDictionary<long, TaskCompletionSource<Result<TunnelFrame>>> pending = new();
    long nextId;
    bool disposed;

    /// <summary>Creates an exchange over a way to send.</summary>
    /// <param name="send">Writes one frame toward the agent — a transport, or a grain observer.</param>
    /// <param name="timeout">How long a request may wait for its response.</param>
    public TunnelExchange(Func<TunnelFrame, CancellationToken, Task> send, TimeSpan timeout) {
        ArgumentNullException.ThrowIfNull(send);

        if (timeout <= TimeSpan.Zero) {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                timeout,
                "A tunnel request needs a positive timeout."
            );
        }

        this.send = send;
        this.timeout = timeout;
    }

    /// <summary>How many requests are waiting for a response right now.</summary>
    public int Pending => pending.Count;

    /// <inheritdoc />
    public async Task<Result<TunnelFrame>> ExchangeAsync(
        TunnelFrame request,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Kind != TunnelFrameKind.Request) {
            return Result<TunnelFrame>.Failure(
                ErrorCode.InvalidRequestBody,
                $"Only a request can be exchanged; this frame is a {request.Kind}."
            );
        }

        if (disposed) {
            return Result<TunnelFrame>.Failure(NoSession());
        }

        var id = Interlocked.Increment(ref nextId);
        var sent = request with { Id = id };
        var completion = new TaskCompletionSource<Result<TunnelFrame>>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        pending[id] = completion;

        try {
            await send(sent, cancellationToken).ConfigureAwait(false);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            pending.TryRemove(id, out _);

            return Result<TunnelFrame>.Failure(
                ErrorCode.ProvisioningFailed,
                $"Request #{id} ({sent.Operation}) could not be sent down the tunnel: {ex.GetType().Name}: {ex.Message}"
            );
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(timeout);

        try {
            return await completion.Task.WaitAsync(budget.Token).ConfigureAwait(false);
        } catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) {
            pending.TryRemove(id, out _);

            return Result<TunnelFrame>.Failure(
                ErrorCode.OperationTimeout,
                $"The agent did not answer request #{id} ({sent.Operation}) within "
                + $"{timeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} seconds. The "
                + "socket is open and nothing is coming back on it, which is what a suspended "
                + "laptop or a NAT mapping that silently expired looks like from here."
            );
        } catch (OperationCanceledException) {
            pending.TryRemove(id, out _);
            throw;
        }
    }

    /// <summary>Hands a response to the request waiting for it.</summary>
    /// <param name="response">A <see cref="TunnelFrameKind.Response" /> off the wire.</param>
    /// <returns><c>true</c> when a request was waiting for that id.</returns>
    public bool Complete(TunnelFrame response) {
        ArgumentNullException.ThrowIfNull(response);

        return pending.TryRemove(response.Id, out var completion)
            && completion.TrySetResult(Result<TunnelFrame>.Success(response));
    }

    /// <summary>Fails every pending request — the session is gone.</summary>
    /// <param name="reason">Why, in the failure's message.</param>
    public void FailAll(string reason) {
        foreach (var (id, completion) in pending) {
            if (pending.TryRemove(id, out _)) {
                completion.TrySetResult(
                    Result<TunnelFrame>.Failure(
                        ErrorCode.ProvisioningFailed,
                        $"Request #{id} was abandoned because the agent's session ended: {reason}"
                    )
                );
            }
        }
    }

    /// <inheritdoc />
    public void Dispose() {
        disposed = true;
        FailAll("the exchange was disposed");
    }

    /// <summary>The failure a request gets when there is no agent to send it to.</summary>
    public static Error NoSession() =>
        new(
            ErrorCode.ProvisioningFailed,
            "No agent is connected for this cluster. The request was not sent. A connected "
            + "cluster's agent dials the platform; until it does — or after it stops — the cluster "
            + "is unreachable, and docs/plan/09 § Cluster connections makes that Degraded rather "
            + "than failed."
        );
}

/// <summary>
///     Reads a transport until it closes and hands every frame to one callback.
/// </summary>
/// <remarks>
///     Used by the gateway (frames → the grain) and by the in-process test (frames → an exchange).
///     The agent has its own loop in <see cref="TunnelAgent" />, because it also answers.
/// </remarks>
public static class TunnelPump {
    /// <summary>Pumps until the peer closes, the token fires, or the transport faults.</summary>
    /// <param name="transport">The carrier.</param>
    /// <param name="onFrame">What to do with each frame. A throw here ends the pump.</param>
    /// <param name="cancellationToken">Stops the pump.</param>
    /// <returns>Why the pump stopped, for the log.</returns>
    public static async Task<string> RunAsync(
        ITunnelTransport transport,
        Func<TunnelFrame, Task> onFrame,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(onFrame);

        try {
            while (!cancellationToken.IsCancellationRequested) {
                var frame = await transport.ReceiveAsync(cancellationToken).ConfigureAwait(false);

                if (frame is null) {
                    return "the peer closed the connection";
                }

                if (frame.Kind == TunnelFrameKind.Goodbye) {
                    var body = TunnelCodec.Deserialize<GoodbyeBody>(frame.Payload);
                    return "the peer said goodbye: " + (body.IsSuccess ? body.GetValueOrThrow().Reason : frame.Payload);
                }

                await onFrame(frame).ConfigureAwait(false);
            }

            return "the pump was cancelled";
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            return "the pump was cancelled";
        } catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException) {
            return $"the transport faulted: {ex.GetType().Name}: {ex.Message}";
        }
    }
}
