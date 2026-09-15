using CyberCloud.Core.Contracts;
using CyberCloud.Core.Resources;
using CyberCloud.Core.Time;
using CyberCloud.Kubernetes.Connections;
using CyberCloud.Kubernetes.Contracts.Tunnel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Concurrency;
using System.Diagnostics.CodeAnalysis;

namespace CyberCloud.Kubernetes.Tunnel;

/// <summary>
///     One cluster's agent tunnel — the credential that admits its agent, the session the agent
///     holds, and the requests in flight down it. <see cref="IAgentTunnelGrain" /> says what each
///     method is for; this says how, and why the caller policy is shaped the way it is.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b><c>[Reentrant]</c>, and the tunnel does not work without it.</b>
///         <see cref="ExchangeAsync" /> awaits a response that arrives through
///         <see cref="DeliverAsync" /> — a second call into this same grain. A non-reentrant grain
///         would queue the delivery behind the exchange that is waiting for it, forever. Reentrancy
///         also lets two reconcilers' requests be in flight down one tunnel at once, which is what
///         the correlation id on every frame is for. What reentrancy costs is that every await in
///         this class is a point where another turn may have run; the state writes below are
///         arranged so that none of them straddles one.
///     </para>
///     <para>
///         <b>The caller policy, per method.</b> <c>ClusterConnectionTenantFilter</c> stamps every
///         incoming call with who made it, silo-wide, so it is readable here too.
///     </para>
///     <list type="table">
///         <item>
///             <term><see cref="AcceptAsync" />, <see cref="DeliverAsync" />, <see cref="DisconnectedAsync" /></term>
///             <description>
///                 The gateway's — a <see cref="CallerKind.Client" /> — or a null-tenant platform
///                 grain. The credential is the check. A tenant grain is refused: a tenant has no
///                 socket to relay.
///             </description>
///         </item>
///         <item>
///             <term><see cref="ExchangeAsync" /></term>
///             <description>
///                 A null-tenant platform grain only — in production, <c>ClusterConnectionGrain</c>,
///                 after its owner check. ⚠ Neither a client nor a tenant, because either could
///                 otherwise drive tenant A's cluster from tenant B's reconciler or from any process
///                 holding a cluster client.
///             </description>
///         </item>
///         <item>
///             <term><see cref="ArmAsync" />, <see cref="RevokeAsync" />, <see cref="GetStatusAsync" /></term>
///             <description>
///                 The owning tenant, a client (the gateway serving an action the dispatcher has
///                 already authorised, or the resource manager on a silo), or a platform grain. Any
///                 other tenant gets the canonical <see cref="ErrorCode.ResourceNotFound" /> —
///                 docs/plan/00 § Non-negotiables' <c>404</c>, so that a cluster id is not an
///                 existence oracle.
///             </description>
///         </item>
///     </list>
/// </remarks>
[Reentrant]
[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "The exchange is a session's, not the grain's: DropSession disposes it on replacement, revocation and deactivation, and a grain's lifetime is Orleans' to end."
)]
public sealed class AgentTunnelGrain : Grain, IAgentTunnelGrain {
    readonly IPersistentState<AgentTunnelState> state;
    readonly IClock clock;
    readonly KubernetesOptions options;
    readonly ILogger<AgentTunnelGrain> logger;

    Guid clusterId;
    Guid sessionId;
    IAgentTunnelObserver? observer;
    TunnelExchange? exchange;
    DateTimeOffset lastPersistedHeartbeat;

    /// <summary>Creates the grain.</summary>
    /// <param name="state">The durable half — see <see cref="AgentTunnelState" />.</param>
    /// <param name="clock">What heartbeats and expiries are measured against.</param>
    /// <param name="options">The request timeout and the heartbeat interval.</param>
    /// <param name="logger">Where sessions and refusals are written.</param>
    public AgentTunnelGrain(
        [PersistentState("agentTunnel", StorageTiers.Durable)] IPersistentState<AgentTunnelState> state,
        IClock clock,
        IOptions<KubernetesOptions> options,
        ILogger<AgentTunnelGrain> logger
    ) {
        ArgumentNullException.ThrowIfNull(options);

        this.state = state;
        this.clock = clock;
        this.options = options.Value;
        this.logger = logger;
    }

    /// <inheritdoc />
    public override Task OnActivateAsync(CancellationToken cancellationToken) {
        var key = this.GetPrimaryKeyString();
        var parsed = GrainKeys.Parse(key);

        if (parsed.TryGetError(out var error) || parsed.GetValueOrThrow().Kind != GrainKeyKind.ClusterConnection) {
            throw new InvalidOperationException(
                $"'{key}' is not a cluster key. An agent tunnel shares IClusterConnectionGrain's key, "
                + $"'{GrainKeys.ClusterConnectionPrefix}{{clusterId:N}}' — docs/plan/06 § Grain keys. "
                + (error is null ? string.Empty : error.Message)
            );
        }

        clusterId = parsed.GetValueOrThrow().Id;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken) {
        // ⚠ The socket outlives this activation and the gateway does not know the grain moved. The
        // next DeliverAsync reactivates the grain somewhere with no session, drops the frame, and
        // the agent's next request times out at the exchange — which fails the connection's health
        // and, through the gateway's disconnect, makes the agent redial. Slow, but it converges.
        DropSession("the grain deactivated");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<Result> ArmAsync(AgentArmRequest request) {
        ArgumentNullException.ThrowIfNull(request);

        var allowed = EnsureOwnerOrPlatform(nameof(ArmAsync));
        if (allowed.TryGetError(out var refusal)) {
            return Result.Failure(refusal);
        }

        if (request.EnrollmentHash.Length == 0 || request.OwningTenantId == Guid.Empty) {
            return Result.Failure(
                ErrorCode.InvalidRequestBody,
                $"Arming the tunnel for cluster {clusterId:D} needs an enrollment hash and an owning tenant."
            );
        }

        if (request.ExpiresAt <= clock.UtcNow) {
            return Result.Failure(
                ErrorCode.InvalidRequestBody,
                $"The enrollment token for cluster {clusterId:D} would already be expired at {request.ExpiresAt:O}."
            );
        }

        if (state.State.Armed && state.State.OwningTenantId != request.OwningTenantId) {
            // The owner is a fact the resource manager stamped when the resource was created and
            // it does not change. A second owner is a second tenant trying to take the cluster.
            logger.LogError(
                "DENIED: tunnel for cluster {Cluster} is owned by tenant {Owner}; an arm named tenant {Other}.",
                clusterId,
                state.State.OwningTenantId,
                request.OwningTenantId
            );

            return Refused();
        }

        state.State.OwningTenantId = request.OwningTenantId;
        state.State.Armed = true;
        state.State.Revoked = false;
        state.State.EnrollmentHash = request.EnrollmentHash;
        state.State.EnrollmentExpiresAt = request.ExpiresAt;
        state.State.HeartbeatInterval = request.HeartbeatInterval > TimeSpan.Zero ? request.HeartbeatInterval : TimeSpan.Zero;

        await state.WriteStateAsync();

        logger.LogInformation(
            "Tunnel for cluster {Cluster} armed for tenant {Owner}; the enrollment token expires at {ExpiresAt}.",
            clusterId,
            request.OwningTenantId,
            request.ExpiresAt
        );

        return Result.Success;
    }

    /// <inheritdoc />
    public async Task<Result<AgentAcceptance>> AcceptAsync(AgentHello hello, IAgentTunnelObserver observer) {
        ArgumentNullException.ThrowIfNull(hello);
        ArgumentNullException.ThrowIfNull(observer);

        if (!CallerIsRelay(nameof(AcceptAsync))) {
            return Result<AgentAcceptance>.Failure(NotAdmitted());
        }

        var now = clock.UtcNow;
        var consumed = false;

        if (!state.State.Armed || state.State.Revoked || hello.PresentedHash.Length == 0) {
            return Result<AgentAcceptance>.Failure(NotAdmitted());
        }

        if (hello.PresentedEnrollment) {
            if (state.State.EnrollmentHash.Length == 0
                || !AgentCredentials.HashesMatch(state.State.EnrollmentHash, hello.PresentedHash)
                || now > state.State.EnrollmentExpiresAt) {
                logger.LogWarning("Tunnel for cluster {Cluster}: an enrollment token was refused.", clusterId);
                return Result<AgentAcceptance>.Failure(NotAdmitted());
            }

            if (hello.ReplacementHash.Length == 0) {
                return Result<AgentAcceptance>.Failure(
                    ErrorCode.InvalidRequestBody,
                    "An enrollment needs the hash of the credential the gateway minted to replace the token."
                );
            }

            // ⚠ Spent here, before anything can await. The token admits exactly one connection.
            state.State.EnrollmentHash = string.Empty;
            state.State.CredentialHash = hello.ReplacementHash;
            state.State.CredentialIssuedAt = now;
            consumed = true;
        } else if (state.State.CredentialHash.Length == 0
                   || !AgentCredentials.HashesMatch(state.State.CredentialHash, hello.PresentedHash)) {
            logger.LogWarning("Tunnel for cluster {Cluster}: a credential was refused.", clusterId);
            return Result<AgentAcceptance>.Failure(NotAdmitted());
        }

        DropSession("a new agent session replaced this one");

        sessionId = Guid.NewGuid();
        this.observer = observer;
        exchange = new(SendAsync, options.TunnelRequestTimeout);
        state.State.AgentVersion = hello.AgentVersion;

        await state.WriteStateAsync();

        logger.LogInformation(
            "Tunnel for cluster {Cluster}: agent {Version} connected as session {Session} (enrollment consumed: {Consumed}).",
            clusterId,
            hello.AgentVersion,
            sessionId,
            consumed
        );

        // ⚠ The interval the resource asked for, not the platform-wide default. The install command
        // put the same number in the chart, but the agent adopts the welcome —
        // TunnelEndToEndTests.TheAgentHeartbeatsWithoutBeingAskedAndAdoptsTheWelcomedInterval —
        // so a welcome that ignored the arm made the resource's heartbeatSeconds dead after the
        // first frame. The default is for a grain armed before the arm carried one.
        return Result<AgentAcceptance>.Success(
            new() {
                SessionId = sessionId,
                EnrollmentConsumed = consumed,
                HeartbeatInterval = state.State.HeartbeatInterval > TimeSpan.Zero
                    ? state.State.HeartbeatInterval
                    : options.AgentHeartbeatInterval
            }
        );
    }

    /// <inheritdoc />
    public async Task DeliverAsync(Guid sessionId, TunnelFrame frame) {
        ArgumentNullException.ThrowIfNull(frame);

        if (!CallerIsRelay(nameof(DeliverAsync)) || sessionId != this.sessionId || exchange is null) {
            return;
        }

        switch (frame.Kind) {
            case TunnelFrameKind.Response:
                if (!exchange.Complete(frame)) {
                    logger.LogDebug("Tunnel for cluster {Cluster}: response #{Id} had no request waiting.", clusterId, frame.Id);
                }

                return;

            case TunnelFrameKind.Heartbeat:
                await RecordHeartbeatAsync(frame);
                return;

            case TunnelFrameKind.Welcome:
            case TunnelFrameKind.Request:
            case TunnelFrameKind.Goodbye:
            case TunnelFrameKind.Unknown:
            default:
                logger.LogWarning("Tunnel for cluster {Cluster}: the agent sent a {Kind} frame. Dropped.", clusterId, frame.Kind);
                return;
        }
    }

    /// <inheritdoc />
    public Task DisconnectedAsync(Guid sessionId, string reason) {
        if (CallerIsRelay(nameof(DisconnectedAsync)) && sessionId == this.sessionId) {
            logger.LogInformation("Tunnel for cluster {Cluster}: session {Session} ended: {Reason}", clusterId, sessionId, reason);
            DropSession(reason);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<Result<TunnelFrame>> ExchangeAsync(TunnelFrame request) {
        ArgumentNullException.ThrowIfNull(request);

        var caller = CallerTenant.Current;

        if (caller.Kind != CallerKind.NullTenant) {
            // ⚠ THE CHECK THAT MAKES THE TUNNEL TENANT-SAFE. The connection grain has already
            // decided whether its caller may reach this cluster; this grain only ever takes the
            // connection grain's word for it, and the connection grain is null-tenant.
            logger.LogError(
                "DENIED: {Caller} tried to exchange down the tunnel for cluster {Cluster}. Only a "
                + "null-tenant platform grain may — the connection grain, after its owner check.",
                caller,
                clusterId
            );

            return Task.FromResult(Refused<TunnelFrame>());
        }

        return exchange is null
            ? Task.FromResult(Result<TunnelFrame>.Failure(TunnelExchange.NoSession()))
            : exchange.ExchangeAsync(request, CancellationToken.None);
    }

    /// <inheritdoc />
    public Task<Result<AgentTunnelStatus>> GetStatusAsync() {
        if (state.State.Armed) {
            var allowed = EnsureOwnerOrPlatform(nameof(GetStatusAsync));
            if (allowed.TryGetError(out var refusal)) {
                return Task.FromResult(Result<AgentTunnelStatus>.Failure(refusal));
            }
        }

        var s = state.State;

        return Task.FromResult(
            Result<AgentTunnelStatus>.Success(
                new() {
                    ClusterId = clusterId,
                    Armed = s.Armed,
                    EnrollmentOpen = s.EnrollmentHash.Length > 0 && clock.UtcNow <= s.EnrollmentExpiresAt && !s.Revoked,
                    Connected = observer is not null,
                    LastHeartbeatAt = s.LastHeartbeatAt,
                    FirstHeartbeatAt = s.FirstHeartbeatAt,
                    AgentVersion = s.AgentVersion,
                    KubernetesVersion = s.KubernetesVersion,
                    Revoked = s.Revoked
                }
            )
        );
    }

    /// <inheritdoc />
    public async Task<Result> RevokeAsync() {
        if (state.State.Armed) {
            var allowed = EnsureOwnerOrPlatform(nameof(RevokeAsync));
            if (allowed.TryGetError(out var refusal)) {
                return Result.Failure(refusal);
            }
        }

        if (!state.State.Armed && !state.State.Revoked) {
            return Result.Success;
        }

        var previous = observer;
        DropSession("the cluster was revoked");

        state.State.Revoked = true;
        state.State.EnrollmentHash = string.Empty;
        state.State.CredentialHash = string.Empty;

        await state.WriteStateAsync();

        if (previous is not null) {
            try {
                await previous.SendAsync(TunnelFrame.Goodbye("the cluster was revoked"));
            } catch (Exception ex) when (ex is not OperationCanceledException) {
                logger.LogDebug(ex, "Tunnel for cluster {Cluster}: the revoked agent could not be told.", clusterId);
            }
        }

        logger.LogInformation("Tunnel for cluster {Cluster} revoked.", clusterId);
        return Result.Success;
    }

    // ── Internals ──────────────────────────────────────────────────────────────────────────────

    Task SendAsync(TunnelFrame frame, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();

        return observer is { } current
            ? current.SendAsync(frame)
            : throw new InvalidOperationException("No agent session is open.");
    }

    async Task RecordHeartbeatAsync(TunnelFrame frame) {
        var now = clock.UtcNow;
        var first = state.State.FirstHeartbeatAt == default;

        state.State.LastHeartbeatAt = now;

        if (first) {
            state.State.FirstHeartbeatAt = now;
        }

        var body = TunnelCodec.Deserialize<HeartbeatBody>(frame.Payload);
        if (body.IsSuccess) {
            var report = body.GetValueOrThrow();

            if (report.AgentVersion.Length > 0) {
                state.State.AgentVersion = report.AgentVersion;
            }

            if (report.KubernetesVersion.Length > 0) {
                state.State.KubernetesVersion = report.KubernetesVersion;
            }
        }

        // ⚠ The first heartbeat is written immediately — it is what makes the resource Succeeded,
        // and a reconcile pass on another silo reads it from storage. Later ones are written at
        // most once a minute, for the reason ClusterConnectionGrain gives about LastSuccessAt.
        if (first || now - lastPersistedHeartbeat >= TimeSpan.FromMinutes(1)) {
            lastPersistedHeartbeat = now;
            await state.WriteStateAsync();
        }
    }

    void DropSession(string reason) {
        var closing = exchange;
        exchange = null;
        observer = null;
        sessionId = Guid.Empty;

        if (closing is not null) {
            closing.FailAll(reason);
            closing.Dispose();
        }
    }

    bool CallerIsRelay(string operation) {
        var caller = CallerTenant.Current;

        if (caller.Kind is CallerKind.Client or CallerKind.NullTenant) {
            return true;
        }

        logger.LogError(
            "DENIED: {Caller} tried to {Operation} on the tunnel for cluster {Cluster}. Only the gateway relays a socket.",
            caller,
            operation,
            clusterId
        );

        return false;
    }

    Result EnsureOwnerOrPlatform(string operation) {
        var caller = CallerTenant.Current;

        if (caller.Kind is CallerKind.Client or CallerKind.NullTenant) {
            return Result.Success;
        }

        if (caller.Kind == CallerKind.Tenant && caller.TenantId == state.State.OwningTenantId) {
            return Result.Success;
        }

        if (caller.Kind == CallerKind.Tenant && caller.TenantId == ClusterConnectionGrain.PlatformTenantId) {
            return Result.Success;
        }

        logger.LogError(
            "DENIED: {Caller} tried to {Operation} on the tunnel for cluster {Cluster}, owned by tenant {Owner}.",
            caller,
            operation,
            clusterId,
            state.State.OwningTenantId
        );

        return Refused();
    }

    Result Refused() =>
        Result.Failure(ErrorCode.ResourceNotFound, $"Cluster {clusterId:D} does not exist, or you may not reach it.");

    Result<T> Refused<T>()
        where T : notnull => Result<T>.Failure(Refused().Error!);

    /// <summary>The one refusal every credential failure shares — see <see cref="IAgentTunnelGrain.AcceptAsync" />.</summary>
    Error NotAdmitted() =>
        new(
            ErrorCode.AuthorizationFailed,
            $"The agent was not admitted to cluster {clusterId:D}. The credential is unknown, spent, "
            + "expired, or the cluster is not accepting agents. Run listInstallCommand on the "
            + "connected cluster for a fresh install command."
        );
}
