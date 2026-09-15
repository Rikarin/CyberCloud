using CyberCloud.Kubernetes.Apply;
using CyberCloud.Kubernetes.Contracts.Tunnel;
using CyberCloud.Kubernetes.Tunnel;
using Microsoft.Extensions.Options;
using System.Net.WebSockets;
using System.Reflection;

namespace CyberCloud.Agent.Host;

/// <summary>
///     The reverse-tunnel client of docs/plan/09 § Cluster connections: dials the gateway, hands the
///     socket to a <see cref="TunnelAgent" />, and dials again when the socket goes away.
/// </summary>
/// <remarks>
///     <para>
///         <b>Which credential to present, in order.</b> The credential Secret in the agent's own
///         namespace, if one has been stored; otherwise the enrollment token the chart mounted. The
///         first successful enrollment hands back a credential in the welcome, which is stored
///         before anything else happens — so a pod that restarts presents the credential, and the
///         token it was installed with is never needed again. ⚠ A pod with neither is not an error
///         to retry through: it logs what is missing and waits, because no amount of reconnecting
///         produces a token.
///     </para>
///     <para>
///         <b>Backoff.</b> One second, doubling to <see cref="AgentOptions.MaxReconnectSeconds" />,
///         reset after any session that lasted a minute. A NAT that drops idle mappings, a gateway
///         rolling deploy and a revoked cluster all look the same from here — the socket closes —
///         and the difference is only how long the next attempt takes to fail.
///     </para>
///     <para>
///         ⚠ <b>A <c>401</c> on the upgrade is retried at the maximum backoff, not abandoned.</b>
///         It is what a spent token, a revoked cluster and a not-yet-armed one all answer, and the
///         tenant's next <c>listInstallCommand</c> plus a re-install is the fix for all three. An
///         agent that exited on it would be a pod in <c>CrashLoopBackOff</c>, which reads as the
///         agent being broken rather than the credential.
///     </para>
///     <para>
///         ⚠ <b>A credential the Secret write refused is kept in memory and the write is
///         retried, because by then the token is spent.</b> The welcome that carries the credential
///         is the platform saying the enrollment token has been used; a <c>403</c> from the
///         <c>Role</c> on the Secret write, or a transient API server error, at that moment used
///         to escape the session, end the process, and restart a pod that had neither a stored
///         credential nor a working token. Now the write's failure is logged, the credential stays
///         in <see cref="unstoredCredential" />, every redial presents it from there, and
///         <see cref="RetryStoreAsync" /> keeps writing every
///         <see cref="AgentOptions.CredentialStoreRetrySeconds" /> until the Secret holds it. What
///         a pod restart before that costs is a fresh <c>listInstallCommand</c>; what it used to
///         cost was the same, on every failure, immediately.
///         <c>AgentServiceTests.ACredentialTheSecretRefusesIsPresentedFromMemoryAndStoredWhenTheWriteRecovers</c>
///         pins both halves.
///     </para>
/// </remarks>
public sealed class AgentService : BackgroundService {
    readonly IOptions<AgentOptions> options;
    readonly IAgentEndpoints endpoints;
    readonly ILogger<AgentService> logger;
    readonly SemaphoreSlim storeGate = new(1, 1);
    string? unstoredCredential;
    Task? storeRetry;

    /// <summary>Creates the service.</summary>
    /// <param name="options">The chart's settings.</param>
    /// <param name="endpoints">The in-cluster API client and credential store.</param>
    /// <param name="logger">Where sessions are logged.</param>
    public AgentService(IOptions<AgentOptions> options, IAgentEndpoints endpoints, ILogger<AgentService> logger) {
        this.options = options;
        this.endpoints = endpoints;
        this.logger = logger;
    }

    /// <inheritdoc />
    public override void Dispose() {
        storeGate.Dispose();
        base.Dispose();
    }

    /// <summary>The version reported in every heartbeat — the assembly's informational version.</summary>
    public static string Version { get; } =
        typeof(AgentService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "0.0.0";

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        var settings = options.Value;

        if (!settings.IsConfigured) {
            logger.LogCritical(
                "The agent is not configured: {Section}:TunnelEndpoint and {Section}:ClusterId are both "
                + "required, and the install command from listInstallCommand sets them. Nothing will be "
                + "dialled.",
                AgentOptions.SectionName,
                AgentOptions.SectionName
            );

            return;
        }

        var backoff = TimeSpan.FromSeconds(1);
        var ceiling = TimeSpan.FromSeconds(Math.Max(1, settings.MaxReconnectSeconds));

        while (!stoppingToken.IsCancellationRequested) {
            var started = DateTimeOffset.UtcNow;
            var outcome = await SessionAsync(settings, stoppingToken);

            if (stoppingToken.IsCancellationRequested) {
                return;
            }

            if (DateTimeOffset.UtcNow - started > TimeSpan.FromMinutes(1)) {
                backoff = TimeSpan.FromSeconds(1);
            }

            var wait = outcome == SessionOutcome.Refused ? ceiling : backoff;
            logger.LogInformation("Reconnecting in {Seconds} s.", wait.TotalSeconds);

            try {
                await Task.Delay(wait, stoppingToken);
            } catch (OperationCanceledException) {
                return;
            }

            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, ceiling.Ticks));
        }
    }

    enum SessionOutcome {
        Ended,
        Refused,
        NoCredential
    }

    async Task<SessionOutcome> SessionAsync(AgentOptions settings, CancellationToken stoppingToken) {
        var credential = await CredentialAsync(settings, stoppingToken);

        if (credential is null) {
            return SessionOutcome.NoCredential;
        }

        using var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol(TunnelCodec.SubProtocol);
        socket.Options.SetRequestHeader("Authorization", "Bearer " + credential);
        socket.Options.SetRequestHeader(TunnelCodec.ClusterHeader, settings.ClusterId);
        socket.Options.SetRequestHeader(TunnelCodec.AgentVersionHeader, Version);
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

        try {
            await socket.ConnectAsync(new(settings.TunnelEndpoint), stoppingToken);
        } catch (WebSocketException ex) {
            // ⚠ The one line an operator reads when the token is wrong. The gateway answers 401 with
            // no upgrade, which ClientWebSocket reports as a failed handshake.
            logger.LogWarning(
                "The gateway at {Endpoint} did not admit this agent: {Message}. If the install token was "
                + "spent or has expired, run listInstallCommand on the connected cluster again and "
                + "re-install.",
                settings.TunnelEndpoint,
                ex.Message
            );

            return SessionOutcome.Refused;
        } catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException
                                     && !stoppingToken.IsCancellationRequested) {
            logger.LogWarning(ex, "The gateway at {Endpoint} could not be reached.", settings.TunnelEndpoint);
            return SessionOutcome.Ended;
        }

        await using var transport = new WebSocketTunnelTransport(socket);

        using var agent = new TunnelAgent(
            transport,
            endpoints.Api,
            new() {
                HeartbeatInterval = TimeSpan.FromSeconds(Math.Max(1, settings.HeartbeatSeconds)),
                AgentVersion = Version,
                OnWelcome = async (welcome, _) => {
                    if (welcome.Credential is { Length: > 0 } issued) {
                        await StoreCredentialAsync(issued, settings, stoppingToken);
                    }
                }
            },
            logger
        );

        logger.LogInformation("Connected to {Endpoint} as cluster {Cluster}.", settings.TunnelEndpoint, settings.ClusterId);

        var reason = await agent.RunAsync(stoppingToken);

        logger.LogInformation("Session ended: {Reason}", reason);
        return SessionOutcome.Ended;
    }

    /// <summary>
    ///     Stores the credential the welcome handed over, and keeps it in memory with a retry
    ///     running when the Secret write fails.
    /// </summary>
    async Task StoreCredentialAsync(string issued, AgentOptions settings, CancellationToken stoppingToken) {
        // Remembered before the write is attempted: from here on, whatever the Secret does, this
        // process presents the credential and not the spent token.
        unstoredCredential = issued;

        if (await TryWriteAsync(issued, stoppingToken)) {
            return;
        }

        await storeGate.WaitAsync(stoppingToken);

        try {
            if (storeRetry is null || storeRetry.IsCompleted) {
                storeRetry = RetryStoreAsync(settings, stoppingToken);
            }
        } finally {
            storeGate.Release();
        }
    }

    async Task<bool> TryWriteAsync(string issued, CancellationToken cancellationToken) {
        try {
            await endpoints.Credentials.WriteAsync(issued, cancellationToken);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            // ⚠ The one line an operator reads when the Role is wrong. A 403 here is rbac.yaml's
            // credential Role not matching cluster.credentialSecretName, or not applied at all.
            logger.LogError(
                ex,
                "The credential could not be stored in the Secret; it is held in memory and the write "
                + "will be retried every {Seconds} s. The install token is already spent, so if this pod "
                + "restarts before the write succeeds, run listInstallCommand again and re-install. A "
                + "403 means the agent's Role does not grant the Secret named by "
                + "cluster.credentialSecretName.",
                options.Value.CredentialStoreRetrySeconds
            );

            return false;
        }

        unstoredCredential = null;
        logger.LogInformation("Enrolled: the credential is stored and the install token is spent.");
        return true;
    }

    async Task RetryStoreAsync(AgentOptions settings, CancellationToken stoppingToken) {
        var interval = TimeSpan.FromSeconds(Math.Max(1, settings.CredentialStoreRetrySeconds));

        try {
            while (!stoppingToken.IsCancellationRequested && unstoredCredential is { } pending) {
                await Task.Delay(interval, stoppingToken);

                if (await TryWriteAsync(pending, stoppingToken)) {
                    logger.LogInformation("The credential Secret write recovered.");
                    return;
                }
            }
        } catch (OperationCanceledException) {
            // The host is stopping; the credential dies with the process, which the error line
            // that started this loop already said.
        }
    }

    async Task<string?> CredentialAsync(AgentOptions settings, CancellationToken cancellationToken) {
        try {
            var stored = await endpoints.Credentials.ReadAsync(cancellationToken);

            if (stored is { Length: > 0 }) {
                return stored;
            }
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            logger.LogWarning(ex, "The credential Secret could not be read; falling back to the enrollment token.");
        }

        if (unstoredCredential is { Length: > 0 } held) {
            // ⚠ Ahead of the token, which is spent: the platform issued this credential and the only
            // thing that failed was writing it down.
            logger.LogWarning("Presenting the credential from memory; the Secret does not hold it yet.");
            return held;
        }

        if (File.Exists(settings.EnrollmentTokenFile)) {
            var token = (await File.ReadAllTextAsync(settings.EnrollmentTokenFile, cancellationToken)).Trim();

            if (token.Length > 0) {
                return token;
            }
        }

        logger.LogError(
            "No credential is stored and no enrollment token is mounted at {File}. Install the chart with "
            + "the command from listInstallCommand.",
            settings.EnrollmentTokenFile
        );

        return null;
    }
}

/// <summary>What the agent needs from the cluster it runs in.</summary>
/// <remarks>
///     A seam so <c>AgentComposition</c> can be built in a test without a service account, and so
///     the host binds none of <c>k8s.*</c> itself — <see cref="InClusterAgent" /> builds both.
/// </remarks>
public interface IAgentEndpoints {
    /// <summary>The API server, through the pod's service account.</summary>
    IKubeApiClient Api { get; }

    /// <summary>Where the credential is kept between restarts.</summary>
    IAgentCredentialStore Credentials { get; }
}

/// <summary>The production endpoints: the pod's own service account and namespace.</summary>
/// <param name="api">The client.</param>
/// <param name="credentials">The store.</param>
public sealed class PodEndpoints(IKubeApiClient api, IAgentCredentialStore credentials) : IAgentEndpoints {
    /// <inheritdoc />
    public IKubeApiClient Api => api;

    /// <inheritdoc />
    public IAgentCredentialStore Credentials => credentials;
}
