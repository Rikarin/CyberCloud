using CyberCloud.Core.Resources;
using CyberCloud.Kubernetes.Contracts.Tunnel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Threading.Channels;

namespace CyberCloud.Kubernetes.Tunnel;

/// <summary>
///     The gateway's half of an agent session: admits an agent through <see cref="IAgentTunnelGrain" />
///     and then relays frames between its socket and the grain until one of them goes away.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/09 § Cluster connections: <i>"the tunnel identity is bound to the cluster
///         resource id at the gateway"</i>. <see cref="AdmitAsync" /> is where that happens — the
///         cluster id comes from the request, the credential is hashed here and never leaves this
///         process in plaintext, and the grain for exactly that cluster says yes or no.
///     </para>
///     <para>
///         ⚠ <b>Here and not in the gateway, because the gateway may not call
///         <c>IGrainFactory.GetGrain</c> without <c>ForTenant</c></b> —
///         <c>GatewayIsolationTests.NoGatewaySourceFileTakesAGrainReferenceWithoutForTenant</c> reads
///         its source for exactly that. A tunnel grain is null-tenant and <c>ForTenant</c> would fork
///         it per tenant, so the one unqualified reference lives in this assembly, where CC1006's
///         documented second form covers it and the gateway's source rule does not reach. The same
///         class is what <c>AgentTunnelGrainTests</c> stands in for a gateway with, so the relay in
///         the test is the relay in production.
///     </para>
///     <para>
///         ⚠ <b>Admission happens before the socket is upgraded, on purpose.</b> A refused agent gets
///         an HTTP <c>401</c> and no WebSocket, which is what an operator running <c>curl -v</c>
///         against the tunnel URL can read. So frames the grain sends between admission and the
///         socket coming up are held in the session's outbox rather than lost.
///     </para>
/// </remarks>
/// <param name="grains">The grain factory — an Orleans client's, in the gateway.</param>
/// <param name="logger">Where sessions are logged.</param>
public sealed class AgentTunnelRelay(IGrainFactory grains, ILogger? logger = null) {
    /// <summary>Admits an agent, or refuses it, without opening a socket.</summary>
    /// <param name="clusterId">The cluster the agent claims to be. From the request header.</param>
    /// <param name="presented">The bearer value the agent sent. Hashed here; never stored.</param>
    /// <param name="agentVersion">The agent's version header, for status.</param>
    /// <returns>A session to run once the socket is up, or the grain's refusal.</returns>
    public async Task<Result<AgentSession>> AdmitAsync(Guid clusterId, string presented, string agentVersion) {
        ArgumentNullException.ThrowIfNull(presented);

        if (!AgentCredentials.IsWellFormed(presented)) {
            return Result<AgentSession>.Failure(
                ErrorCode.AuthorizationFailed,
                "The bearer value is not an agent credential. An agent presents the enrollment token "
                + "from listInstallCommand, or the credential it was handed in exchange."
            );
        }

        var enrollment = AgentCredentials.IsEnrollment(presented);
        var replacement = enrollment ? AgentCredentials.MintCredential() : default;

        var session = new AgentSession(grains, clusterId, logger ?? NullLogger.Instance);

        var accepted = await session.Grain.AcceptAsync(
            new() {
                PresentedHash = AgentCredentials.Hash(presented),
                PresentedEnrollment = enrollment,
                ReplacementHash = enrollment ? replacement.Hash : string.Empty,
                AgentVersion = agentVersion
            },
            session.ObserverReference
        );

        if (accepted.TryGetError(out var refusal)) {
            session.Abandon();
            return Result<AgentSession>.Failure(refusal);
        }

        session.Accepted(accepted.GetValueOrThrow(), enrollment ? replacement.Plaintext : null);
        return Result<AgentSession>.Success(session);
    }
}

/// <summary>
///     One admitted agent's session: the observer the grain writes to, the outbox it fills, and the
///     pump that moves frames between the socket and the grain.
/// </summary>
public sealed class AgentSession : IAgentTunnelObserver {
    readonly IGrainFactory grains;
    readonly ILogger logger;
    readonly Channel<TunnelFrame> outbox = Channel.CreateUnbounded<TunnelFrame>(new() { SingleReader = true });
    AgentAcceptance acceptance = new();
    string? credentialToHandOver;

    internal AgentSession(IGrainFactory grains, Guid clusterId, ILogger logger) {
        this.grains = grains;
        this.logger = logger;
        ClusterId = clusterId;
        ObserverReference = grains.CreateObjectReference<IAgentTunnelObserver>(this);
    }

    /// <summary>The cluster.</summary>
    public Guid ClusterId { get; }

    /// <summary>The session id the grain assigned.</summary>
    public Guid SessionId => acceptance.SessionId;

    /// <summary>Whether this connection spent the enrollment token — the agent will be handed its credential.</summary>
    public bool EnrollmentConsumed => acceptance.EnrollmentConsumed;

    /// <summary>The observer reference the grain holds — see <see cref="IAgentTunnelObserver" />.</summary>
    internal IAgentTunnelObserver ObserverReference { get; }

    /// <summary>The grain for this cluster.</summary>
    internal IAgentTunnelGrain Grain => grains.GetGrain<IAgentTunnelGrain>(GrainKeys.ClusterConnection(ClusterId));

    /// <inheritdoc />
    public Task SendAsync(TunnelFrame frame) {
        ArgumentNullException.ThrowIfNull(frame);
        outbox.Writer.TryWrite(frame);
        return Task.CompletedTask;
    }

    /// <summary>Relays until the socket closes, the grain says goodbye, or the token fires.</summary>
    /// <param name="transport">The agent's socket, upgraded.</param>
    /// <param name="cancellationToken">Stops the relay — the gateway shutting down.</param>
    /// <returns>Why the session ended, for the log.</returns>
    /// <remarks>
    ///     The welcome goes first, carrying the credential when the enrollment token was spent on
    ///     this connection. Then two loops: the outbox to the socket, the socket to
    ///     <see cref="IAgentTunnelGrain.DeliverAsync" />. When either ends the other is stopped, the
    ///     grain is told, and the observer reference is deleted so the grain cannot write into a
    ///     session that is gone.
    /// </remarks>
    public async Task<string> RunAsync(ITunnelTransport transport, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(transport);

        await transport.SendAsync(
            new() {
                Kind = TunnelFrameKind.Welcome,
                Payload = TunnelCodec.Serialize(
                    new WelcomeBody {
                        SessionId = SessionId,
                        HeartbeatSeconds = (int)acceptance.HeartbeatInterval.TotalSeconds,
                        Credential = credentialToHandOver
                    }
                )
            },
            cancellationToken
        ).ConfigureAwait(false);

        // ⚠ Handed over once. The plaintext is not kept on this object after the welcome went out.
        credentialToHandOver = null;

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var outbound = DrainAsync(transport, stop.Token);

        string reason;
        try {
            reason = await TunnelPump.RunAsync(
                transport,
                frame => Grain.DeliverAsync(SessionId, frame),
                stop.Token
            ).ConfigureAwait(false);
        } finally {
            await stop.CancelAsync().ConfigureAwait(false);

            try {
                await outbound.ConfigureAwait(false);
            } catch (OperationCanceledException) {
                // The drain's own exit.
            }
        }

        try {
            await Grain.DisconnectedAsync(SessionId, reason).ConfigureAwait(false);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            logger.LogWarning(ex, "The tunnel grain for cluster {Cluster} could not be told session {Session} ended.", ClusterId, SessionId);
        }

        Abandon();
        await transport.CloseAsync(reason, CancellationToken.None).ConfigureAwait(false);

        logger.LogInformation("Agent session {Session} for cluster {Cluster} ended: {Reason}", SessionId, ClusterId, reason);
        return reason;
    }

    internal void Accepted(AgentAcceptance accepted, string? credential) {
        acceptance = accepted;
        credentialToHandOver = credential;
    }

    internal void Abandon() {
        outbox.Writer.TryComplete();

        try {
            // ⚠ The REFERENCE, not this object: Orleans indexes registrations by the reference it
            // handed out, and passing the observer itself is an ArgumentException.
            grains.DeleteObjectReference<IAgentTunnelObserver>(ObserverReference);
        } catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) {
            // Already deleted, or never registered — either way there is nothing to hold.
        }
    }

    async Task DrainAsync(ITunnelTransport transport, CancellationToken cancellationToken) {
        await foreach (var frame in outbox.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false)) {
            await transport.SendAsync(frame, cancellationToken).ConfigureAwait(false);

            if (frame.Kind == TunnelFrameKind.Goodbye) {
                // The grain closed the session — a revocation. The socket is closed by the pump
                // seeing the transport go away, which CloseAsync makes happen.
                await transport.CloseAsync("the platform closed the session", cancellationToken).ConfigureAwait(false);
                return;
            }
        }
    }
}
