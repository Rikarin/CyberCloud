using CyberCloud.Kubernetes.Apply;
using CyberCloud.Kubernetes.Contracts.Tunnel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;

namespace CyberCloud.Kubernetes.Tunnel;

/// <summary>What one agent session is told about itself.</summary>
public sealed record TunnelAgentOptions {
    /// <summary>How often to heartbeat. The welcome frame says; this is the value before one arrives.</summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>The version to report in every heartbeat.</summary>
    public string AgentVersion { get; init; } = "dev";

    /// <summary>
    ///     How many requests may run against the API server at once. The platform's reconcilers are
    ///     bounded per pass, so this is a ceiling against a runaway rather than a tuning knob.
    /// </summary>
    public int MaxConcurrentRequests { get; init; } = 32;

    /// <summary>
    ///     Called once with the welcome the platform sends — the host's chance to store the
    ///     credential an enrollment was exchanged for before the enrollment token stops working.
    /// </summary>
    /// <remarks>
    ///     ⚠ A throw here is logged and the session goes on; it does not end the pump. By the time
    ///     the welcome arrives the enrollment token is spent, so a session that died on a failed
    ///     Secret write would leave the host redialling with a token the platform no longer admits.
    ///     Keeping the session up is what gives the host time to retry the write — see
    ///     <c>AgentService</c>, which does, and holds the credential in memory meanwhile.
    /// </remarks>
    public Func<WelcomeBody, CancellationToken, Task>? OnWelcome { get; init; }
}

/// <summary>
///     The agent's half of the tunnel: reads requests off the transport, runs each against the local
///     <see cref="IKubeApiClient" />, writes the response back, and heartbeats on a timer.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/09 § Cluster connections: <i>"a reverse-tunnel client and a scoped proxy"</i>.
///         This is the scoped proxy. The reverse-tunnel client — the dial, the retry, the credential
///         Secret — is the agent host's, one layer up, because it is the half that knows about a
///         gateway URL and a Kubernetes Secret and this half knows about neither.
///     </para>
///     <para>
///         ⚠ <b>Requests are served concurrently and answered out of order, on purpose.</b> A
///         reconciler's <c>GetAsync</c> should not queue behind another reconciler's namespace
///         listing; the correlation id is what makes that safe, and <c>TunnelEndToEndTests</c>
///         proves a slow request does not hold a fast one.
///     </para>
///     <para>
///         ⚠ <b>Runs against whatever <see cref="IKubeApiClient" /> it is given.</b> In the agent
///         host that is a <see cref="KubeApiClient" /> over the pod's in-cluster service account;
///         in a test it is a fake. That substitution is what "both sides real" means — this class
///         is the same bytes in both.
///     </para>
/// </remarks>
public sealed class TunnelAgent : IDisposable {
    readonly ITunnelTransport transport;
    readonly IKubeApiClient api;
    readonly TunnelAgentOptions options;
    readonly ILogger logger;
    readonly ConcurrentDictionary<long, Task> inFlight = new();
    readonly SemaphoreSlim concurrency;

    /// <summary>The last welcome received, once one has.</summary>
    public WelcomeBody? Welcome { get; private set; }

    /// <summary>How many heartbeats this session has sent. For tests.</summary>
    public int HeartbeatsSent { get; private set; }

    /// <summary>Creates an agent over a transport.</summary>
    /// <param name="transport">The carrier, already connected.</param>
    /// <param name="api">The local API server.</param>
    /// <param name="options">Heartbeat and concurrency.</param>
    /// <param name="logger">Where refusals and faults are written.</param>
    public TunnelAgent(
        ITunnelTransport transport,
        IKubeApiClient api,
        TunnelAgentOptions? options = null,
        ILogger? logger = null
    ) {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(api);

        this.transport = transport;
        this.api = api;
        this.options = options ?? new();
        this.logger = logger ?? NullLogger.Instance;
        concurrency = new(this.options.MaxConcurrentRequests, this.options.MaxConcurrentRequests);
    }

    /// <summary>Serves the session until the peer closes it or the token fires.</summary>
    /// <param name="cancellationToken">Stops the agent; a goodbye is sent first.</param>
    /// <returns>Why the session ended, for the log and for the reconnect loop.</returns>
    public async Task<string> RunAsync(CancellationToken cancellationToken = default) {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeats = HeartbeatLoopAsync(stop.Token);

        string reason;
        try {
            reason = await TunnelPump.RunAsync(transport, HandleAsync, stop.Token).ConfigureAwait(false);
        } finally {
            await stop.CancelAsync().ConfigureAwait(false);

            try {
                await heartbeats.ConfigureAwait(false);
            } catch (OperationCanceledException) {
                // The loop's own exit.
            }

            await Task.WhenAll(inFlight.Values).ConfigureAwait(false);
        }

        // ⚠ Always closed from this side too, whoever went first. A peer that said goodbye is still
        // reading until it sees this end close, and a relay that never sees it holds its session —
        // and the grain's — open for a socket nobody is on.
        await transport.CloseAsync(
            cancellationToken.IsCancellationRequested ? "the agent is shutting down" : reason,
            CancellationToken.None
        )
            .ConfigureAwait(false);

        return reason;
    }

    Task HandleAsync(TunnelFrame frame) {
        switch (frame.Kind) {
            case TunnelFrameKind.Welcome:
                var welcome = TunnelCodec.Deserialize<WelcomeBody>(frame.Payload);

                if (!welcome.IsSuccess) {
                    return Task.CompletedTask;
                }

                Welcome = welcome.GetValueOrThrow();
                logger.LogInformation(
                    "Agent session {Session} welcomed; heartbeat every {Seconds} s.",
                    Welcome.SessionId,
                    Welcome.HeartbeatSeconds
                );

                // ⚠ Awaited inline, before the next frame is read: a credential that is not yet
                // stored when the pod dies is an enrollment token spent for nothing.
                return options.OnWelcome is { } onWelcome ? WelcomedAsync(onWelcome, Welcome) : Task.CompletedTask;

            case TunnelFrameKind.Request:
                var work = ServeAsync(frame);
                inFlight[frame.Id] = work;
                _ = work.ContinueWith(
                    _ => inFlight.TryRemove(frame.Id, out var _),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default
                );

                return Task.CompletedTask;

            default:
                logger.LogWarning(
                    "The platform sent a {Kind} frame, which an agent does not serve. Dropped.",
                    frame.Kind
                );
                return Task.CompletedTask;
        }
    }

    async Task WelcomedAsync(Func<WelcomeBody, CancellationToken, Task> onWelcome, WelcomeBody welcome) {
        try {
            await onWelcome(welcome, CancellationToken.None).ConfigureAwait(false);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            // ⚠ CAUGHT, AND THE SESSION GOES ON. TunnelPump.RunAsync catches transport faults only,
            // so a throw from here — a 403 from the Role on the Secret write, a transient API
            // server error — used to propagate out of RunAsync, through the host's session loop,
            // and take the process down: a pod restart with the enrollment token already spent and
            // no credential stored, which is the one state the agent cannot recover from. The
            // session is worth more than the write; the host retries the write while it lives.
            logger.LogError(
                ex,
                "The welcome handler faulted for session {Session}; the session continues. If this was "
                + "the credential store, the enrollment token is already spent — the host retries the "
                + "write and presents the credential from memory until it lands.",
                welcome.SessionId
            );
        }
    }

    async Task ServeAsync(TunnelFrame request) {
        await concurrency.WaitAsync().ConfigureAwait(false);

        string payload;
        try {
            payload = await DispatchAsync(request).ConfigureAwait(false);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            // ⚠ Caught and answered rather than allowed to end the session. A reconciler's request
            // that makes the local client throw is that reconciler's problem; the other tenants'
            // — the other reconcilers' — requests on this socket are not.
            logger.LogError(ex, "Request #{Id} ({Operation}) faulted in the agent.", request.Id, request.Operation);

            payload = TunnelOperations.Seal(
                Result.Failure(
                    ErrorCode.InternalError,
                    $"The agent faulted serving {request.Operation}: {ex.GetType().Name}: {ex.Message}"
                )
            );
        } finally {
            concurrency.Release();
        }

        try {
            await transport.SendAsync(
                TunnelFrame.Response(request.Id, request.Operation, payload),
                CancellationToken.None
            )
                .ConfigureAwait(false);
        } catch (Exception ex) when (ex is IOException
                                         or ObjectDisposedException
                                         or InvalidOperationException
                                         or System.Net.WebSockets.WebSocketException) {
            logger.LogWarning(ex, "Response #{Id} could not be sent; the session is closing.", request.Id);
        }
    }

    async Task<string> DispatchAsync(TunnelFrame request) {
        var cancellationToken = CancellationToken.None;

        switch (request.Operation) {
            case TunnelOperations.Ping: {
                var pinged = await api.PingAsync(cancellationToken).ConfigureAwait(false);

                return TunnelOperations.Seal(
                    pinged.TryGetError(out var error)
                        ? Result<TunnelOperations.PingAnswer>.Failure(error)
                        : Result<TunnelOperations.PingAnswer>.Success(new() { Version = pinged.GetValueOrThrow() })
                );
            }

            case TunnelOperations.Get: {
                var target = TunnelCodec.Deserialize<ObjectRef>(request.Payload);

                return target.TryGetError(out var error)
                    ? TunnelOperations.Seal(Result<KubeObject>.Failure(error))
                    : TunnelOperations.Seal(
                        await api.GetAsync(target.GetValueOrThrow(), cancellationToken).ConfigureAwait(false)
                    );
            }

            case TunnelOperations.Apply: {
                var command = KubeCommandJson.FromJson(request.Payload);

                return command.TryGetError(out var error)
                    ? TunnelOperations.Seal(Result<ApplyOutcome>.Failure(error))
                    : TunnelOperations.Seal(
                        await api.ApplyAsync(command.GetValueOrThrow(), cancellationToken).ConfigureAwait(false)
                    );
            }

            case TunnelOperations.Delete: {
                var arguments = TunnelCodec.Deserialize<TunnelOperations.DeleteArguments>(request.Payload);

                if (arguments.TryGetError(out var error)) {
                    return TunnelOperations.Seal(Result.Failure(error));
                }

                var value = arguments.GetValueOrThrow();
                return TunnelOperations.Seal(
                    await api.DeleteAsync(value.Target, value.Policy, cancellationToken).ConfigureAwait(false)
                );
            }

            case TunnelOperations.SetOwner: {
                var arguments = TunnelCodec.Deserialize<TunnelOperations.SetOwnerArguments>(request.Payload);

                if (arguments.TryGetError(out var error)) {
                    return TunnelOperations.Seal(Result.Failure(error));
                }

                var value = arguments.GetValueOrThrow();
                return TunnelOperations.Seal(
                    await api.SetOwnerAsync(value.Target, value.Owner, cancellationToken).ConfigureAwait(false)
                );
            }

            case TunnelOperations.Discover: {
                var kinds = await api.DiscoverNamespacedKindsAsync(cancellationToken).ConfigureAwait(false);

                return TunnelOperations.Seal(
                    kinds.TryGetError(out var error)
                        ? Result<TunnelOperations.DiscoverAnswer>.Failure(error)
                        : Result<TunnelOperations.DiscoverAnswer>.Success(
                            new() { Kinds = [.. kinds.GetValueOrThrow()] }
                        )
                );
            }

            case TunnelOperations.List: {
                var arguments = TunnelCodec.Deserialize<TunnelOperations.ListArguments>(request.Payload);

                if (arguments.TryGetError(out var error)) {
                    return TunnelOperations.Seal(Result<TunnelOperations.ListAnswer>.Failure(error));
                }

                var value = arguments.GetValueOrThrow();

                var page = await api.ListAsync(
                    value.Kind,
                    value.Namespace,
                    value.LabelSelector,
                    value.ResourceVersion,
                    value.ContinueToken,
                    value.Limit,
                    cancellationToken
                )
                    .ConfigureAwait(false);

                return TunnelOperations.Seal(
                    page.TryGetError(out var listError)
                        ? Result<TunnelOperations.ListAnswer>.Failure(listError)
                        : Result<TunnelOperations.ListAnswer>.Success(
                            new() {
                                Items = [.. page.GetValueOrThrow().Items],
                                ResourceVersion = page.GetValueOrThrow().ResourceVersion,
                                ContinueToken = page.GetValueOrThrow().ContinueToken
                            }
                        )
                );
            }

            default:
                // ⚠ Refused by name, and never "try it as HTTP". The list of operations IS the
                // agent's authorization scope — see TunnelOperations.
                return TunnelOperations.Seal(
                    Result.Failure(
                        ErrorCode.InvalidRequestBody,
                        $"'{request.Operation}' is not an operation this agent serves. The tunnel carries "
                        + string.Join(", ", TunnelOperations.All)
                        + " and nothing else."
                    )
                );
        }
    }

    /// <inheritdoc />
    public void Dispose() => concurrency.Dispose();

    async Task HeartbeatLoopAsync(CancellationToken cancellationToken) {
        var kubernetesVersion = string.Empty;

        while (!cancellationToken.IsCancellationRequested) {
            var interval = Welcome is { HeartbeatSeconds: > 0 } welcome
                ? TimeSpan.FromSeconds(welcome.HeartbeatSeconds)
                : options.HeartbeatInterval;

            if (kubernetesVersion.Length == 0) {
                // Asked once and remembered: the version is the one fact a portal wants about a
                // cluster it cannot otherwise see, and it does not change between restarts.
                var pinged = await api.PingAsync(cancellationToken).ConfigureAwait(false);
                kubernetesVersion = pinged.IsSuccess ? pinged.GetValueOrThrow() : string.Empty;
            }

            try {
                await transport.SendAsync(
                    TunnelFrame.Heartbeat(
                        TunnelCodec.Serialize(
                            new HeartbeatBody {
                                AgentVersion = options.AgentVersion, KubernetesVersion = kubernetesVersion
                            }
                        )
                    ),
                    cancellationToken
                )
                    .ConfigureAwait(false);

                HeartbeatsSent++;
            } catch (Exception ex) when (ex is IOException
                                             or ObjectDisposedException
                                             or InvalidOperationException
                                             or System.Net.WebSockets.WebSocketException) {
                logger.LogWarning(ex, "A heartbeat could not be sent; the session is closing.");
                return;
            }

            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
        }
    }
}
