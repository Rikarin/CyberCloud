using CyberCloud.Kubernetes.Apply;
using CyberCloud.Kubernetes.Contracts.Tunnel;
using CyberCloud.Kubernetes.Tests.Infrastructure;
using CyberCloud.Kubernetes.Tunnel;
using System.IO.Pipes;

namespace CyberCloud.Kubernetes.Tests.Tunnel;

/// <summary>
///     A tunnel with both ends in this process: the platform's <see cref="TunnelExchange" /> on one
///     side of a pair of anonymous pipes, a real <see cref="TunnelAgent" /> on the other.
/// </summary>
/// <remarks>
///     ⚠ <b>Nothing here is a double except the transport and the API server.</b> The frames, the
///     codec, the multiplexer, the agent's dispatcher and the tunnel-backed
///     <see cref="IKubeApiClient" /> are the shipping types, byte for byte. What a real deployment
///     adds is a WebSocket where the pipes are and a kubelet-issued service account where the
///     <see cref="RecordingApiClient" /> is.
/// </remarks>
public sealed class InProcessTunnel : IAsyncDisposable {
    readonly AnonymousPipeServerStream platformToAgent = new(PipeDirection.Out);
    readonly AnonymousPipeServerStream agentToPlatform = new(PipeDirection.Out);
    readonly AnonymousPipeClientStream agentReads;
    readonly AnonymousPipeClientStream platformReads;
    readonly CancellationTokenSource stop = new();
    Task<string>? pump;
    Task<string>? agentRun;

    /// <summary>The platform side's transport, before it is wrapped.</summary>
    public StreamTunnelTransport PlatformTransport { get; }

    /// <summary>The agent side's transport.</summary>
    public StreamTunnelTransport AgentTransport { get; }

    /// <summary>The platform side's multiplexer — what a grain holds.</summary>
    public TunnelExchange Exchange { get; }

    /// <summary>The API server the agent serves — a fake, so a test can script and inspect it.</summary>
    public RecordingApiClient Api { get; } = new();

    /// <summary>The real agent, over the real transport.</summary>
    public TunnelAgent Agent { get; }

    /// <summary>Every heartbeat frame the platform side received.</summary>
    public List<TunnelFrame> Heartbeats { get; } = [];

    /// <summary>The <see cref="IKubeApiClient" /> a connection grain would hold.</summary>
    public TunnelKubeApiClient Client { get; }

    /// <summary>Why the platform pump stopped, once it has. <see langword="null" /> while it runs.</summary>
    public string? PumpEnded { get; private set; }

    /// <summary>Creates the pair. Nothing runs until <see cref="Start" />.</summary>
    /// <param name="requestTimeout">The exchange's timeout.</param>
    /// <param name="heartbeat">The agent's heartbeat interval.</param>
    /// <param name="onWelcome">What the agent does with a welcome — the host's credential store, in production.</param>
    public InProcessTunnel(
        TimeSpan? requestTimeout = null,
        TimeSpan? heartbeat = null,
        Func<WelcomeBody, CancellationToken, Task>? onWelcome = null
    ) {
        agentReads = new(PipeDirection.In, platformToAgent.ClientSafePipeHandle);
        platformReads = new(PipeDirection.In, agentToPlatform.ClientSafePipeHandle);

        PlatformTransport = new(platformReads, platformToAgent);
        AgentTransport = new(agentReads, agentToPlatform);

        Exchange = new(PlatformTransport.SendAsync, requestTimeout ?? TimeSpan.FromSeconds(10));
        Agent = new(
            AgentTransport,
            Api,
            new() {
                HeartbeatInterval = heartbeat ?? TimeSpan.FromMilliseconds(200),
                AgentVersion = "test-agent",
                OnWelcome = onWelcome
            }
        );

        Client = new(Guid.NewGuid(), Exchange);
    }

    /// <summary>Starts the platform pump and the agent.</summary>
    public void Start() {
        pump = PumpAsync();
        agentRun = Agent.RunAsync(stop.Token);
    }

    /// <summary>
    ///     The platform's pump, and what production does when it ends.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Pending requests are failed here when the transport goes, not by the test.</b> In
    ///     production the relay's pump ending is <c>AgentSession.RunAsync</c> →
    ///     <c>IAgentTunnelGrain.DisconnectedAsync</c> → <c>DropSession</c> → <c>FailAll</c>; a test
    ///     that called <c>FailAll</c> itself after killing the agent was pinning <c>FailAll</c>, not
    ///     that a killed agent fails requests. This is the same step, in the same place.
    /// </remarks>
    async Task<string> PumpAsync() {
        var reason = await TunnelPump.RunAsync(
            PlatformTransport,
            frame => {
                if (frame.Kind == TunnelFrameKind.Response) {
                    Exchange.Complete(frame);
                } else if (frame.Kind == TunnelFrameKind.Heartbeat) {
                    lock (Heartbeats) {
                        Heartbeats.Add(frame);
                    }
                }

                return Task.CompletedTask;
            },
            stop.Token
        );

        PumpEnded = reason;
        Exchange.FailAll(reason);
        return reason;
    }

    /// <summary>Sends the welcome the gateway would, so the agent picks up its heartbeat interval.</summary>
    /// <param name="welcome">The body.</param>
    /// <param name="cancellationToken">The test's token.</param>
    public Task WelcomeAsync(WelcomeBody welcome, CancellationToken cancellationToken = default) =>
        PlatformTransport.SendAsync(
            new() { Kind = TunnelFrameKind.Welcome, Payload = TunnelCodec.Serialize(welcome) },
            cancellationToken
        );

    /// <summary>Closes the agent's end abruptly — a pod killed mid-session.</summary>
    public async Task KillAgentAsync() {
        await AgentTransport.DisposeAsync();
    }

    /// <summary>Waits until the platform side has seen at least one heartbeat.</summary>
    public async Task<TunnelFrame> FirstHeartbeatAsync() {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);

        while (DateTime.UtcNow < deadline) {
            lock (Heartbeats) {
                if (Heartbeats.Count > 0) {
                    return Heartbeats[0];
                }
            }

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException("No heartbeat arrived within ten seconds.");
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() {
        await stop.CancelAsync();

        if (agentRun is not null) {
            try {
                await agentRun;
            } catch (OperationCanceledException) {
                // Stopped.
            }
        }

        if (pump is not null) {
            await pump;
        }

        Exchange.Dispose();
        Agent.Dispose();
        await PlatformTransport.DisposeAsync();
        await AgentTransport.DisposeAsync();

        // The transports own these and have disposed them; disposing a stream twice is a no-op, and
        // CA2213 cannot see through the wrapping.
        await platformToAgent.DisposeAsync();
        await agentToPlatform.DisposeAsync();
        await agentReads.DisposeAsync();
        await platformReads.DisposeAsync();
        stop.Dispose();
    }
}
