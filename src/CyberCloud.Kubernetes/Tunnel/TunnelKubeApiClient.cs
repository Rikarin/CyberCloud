using CyberCloud.Kubernetes.Apply;
using CyberCloud.Kubernetes.Contracts.Tunnel;

namespace CyberCloud.Kubernetes.Tunnel;

/// <summary>
///     <see cref="IKubeApiClient" /> over an agent tunnel: every call becomes one request frame down
///     the tunnel and one response frame back, and the API server at the far end is the agent's.
/// </summary>
/// <remarks>
///     <para>
///         This is what <c>KubeApiClientFactory</c> hands <c>ClusterConnectionGrain</c> for a
///         <see cref="ClusterConnectionKind.AgentInitiated" /> descriptor, in place of a
///         <see cref="KubeApiClient" /> over a kubeconfig. The grain does not know the difference,
///         which is the point: its tenancy check, its health window, its suspend-on-Degraded rule
///         all run exactly as they do for a kubeconfig, above this seam.
///     </para>
///     <para>
///         ⚠
///         <b>
///             A tunnel failure and an API server failure are told apart by the error code, and the
///             health tracker depends on it.
///         </b> An answer the agent relayed — a <c>404</c>, a
///         <c>409</c>, an admission refusal — comes back as the code the agent's own
///         <see cref="KubeApiClient" /> produced, and <c>KubeFailures.MeansTheClusterAnswered</c>
///         says the cluster is up. A request that never got an answer comes back as
///         <see cref="ErrorCode.OperationTimeout" /> or <see cref="ErrorCode.ProvisioningFailed" />
///         from the route, neither of which is on that list — so a dead tunnel drives the
///         connection toward <c>Degraded</c> and a refused apply does not.
///     </para>
/// </remarks>
/// <param name="clusterId">The cluster, for messages.</param>
/// <param name="route">Where requests go.</param>
public sealed class TunnelKubeApiClient(Guid clusterId, ITunnelRoute route) : IKubeApiClient {
    /// <summary>The route this client sends down. Exposed so a test can prove which one the factory built.</summary>
    public ITunnelRoute Route => route;

    /// <inheritdoc />
    public async Task<Result<string>> PingAsync(CancellationToken cancellationToken = default) {
        var answer = await CallAsync<TunnelOperations.PingAnswer>(TunnelOperations.Ping, "{}", cancellationToken)
            .ConfigureAwait(false);

        return answer.TryGetError(out var error)
            ? Result<string>.Failure(error)
            : Result<string>.Success(answer.GetValueOrThrow().Version);
    }

    /// <inheritdoc />
    public Task<Result<KubeObject>> GetAsync(ObjectRef target, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(target);
        return CallAsync<KubeObject>(TunnelOperations.Get, TunnelCodec.Serialize(target), cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result<ApplyOutcome>> ApplyAsync(KubeCommand command, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(command);
        return CallAsync<ApplyOutcome>(TunnelOperations.Apply, KubeCommandJson.ToJson(command), cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result> DeleteAsync(
        ObjectRef target,
        CascadePolicy policy,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(target);

        return CallAsync(
            TunnelOperations.Delete,
            TunnelCodec.Serialize(new TunnelOperations.DeleteArguments { Target = target, Policy = policy }),
            cancellationToken
        );
    }

    /// <inheritdoc />
    public Task<Result> SetOwnerAsync(
        ObjectRef target,
        OwnerRef? owner,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(target);

        return CallAsync(
            TunnelOperations.SetOwner,
            TunnelCodec.Serialize(new TunnelOperations.SetOwnerArguments { Target = target, Owner = owner }),
            cancellationToken
        );
    }

    /// <inheritdoc />
    public async Task<Result<string>> ReadLogsAsync(
        ObjectRef pod,
        string container,
        int tailLines,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(pod);

        var answer = await CallAsync<TunnelOperations.ReadLogsAnswer>(
            TunnelOperations.ReadLogs,
            TunnelCodec.Serialize(
                new TunnelOperations.ReadLogsArguments { Pod = pod, Container = container ?? string.Empty, TailLines = tailLines }
            ),
            cancellationToken
        )
            .ConfigureAwait(false);

        return answer.TryGetError(out var error)
            ? Result<string>.Failure(error)
            : Result<string>.Success(answer.GetValueOrThrow().Log);
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<GroupVersionKind>>> DiscoverNamespacedKindsAsync(
        CancellationToken cancellationToken = default
    ) {
        var answer = await CallAsync<TunnelOperations.DiscoverAnswer>(
            TunnelOperations.Discover,
            "{}",
            cancellationToken
        )
                .ConfigureAwait(false);

        return answer.TryGetError(out var error)
            ? Result<IReadOnlyList<GroupVersionKind>>.Failure(error)
            : Result<IReadOnlyList<GroupVersionKind>>.Success(answer.GetValueOrThrow().Kinds);
    }

    /// <inheritdoc />
    public async Task<Result<ListPage>> ListAsync(
        GroupVersionKind kind,
        string ns,
        string labelSelector,
        string? resourceVersion = null,
        string? continueToken = null,
        int? limit = null,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(kind);

        var answer = await CallAsync<TunnelOperations.ListAnswer>(
            TunnelOperations.List,
            TunnelCodec.Serialize(
                new TunnelOperations.ListArguments {
                    Kind = kind,
                    Namespace = ns,
                    LabelSelector = labelSelector,
                    ResourceVersion = resourceVersion,
                    ContinueToken = continueToken,
                    Limit = limit
                }
            ),
            cancellationToken
        ).ConfigureAwait(false);

        if (answer.TryGetError(out var error)) {
            return Result<ListPage>.Failure(error);
        }

        var page = answer.GetValueOrThrow();
        return Result<ListPage>.Success(new(page.Items, page.ResourceVersion, page.ContinueToken));
    }

    /// <inheritdoc />
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Refuses, by name, on the first move.</b> A watch is a stream and the tunnel's
    ///         frames have one answer each. Yielding nothing would let <c>SharedInformer</c> believe
    ///         a cluster is quiet, so a caller that gets this far is told why instead.
    ///         <c>charts/agent/conformance.yaml § owed</c>, <c>informers-do-not-cross-the-tunnel</c>.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             Nothing in production reaches this throw, and the refusal that matters is the
    ///             connection grain's.
    ///         </b> Establishing an informer is a <i>list</i> —
    ///         <c>SharedInformer.EstablishAsync</c> lists and holds the cursor; the watch is
    ///         <c>SharedInformer.PumpAsync</c>, which only its own tests call — and a list crosses
    ///         the tunnel like any other request. So <c>ClusterConnectionGrain.WatchAsync</c> refuses
    ///         an <see cref="ClusterConnectionKind.AgentInitiated" /> descriptor before the list is
    ///         sent, which
    ///         <c>AgentTunnelGrainTests.AWatchOnAConnectedClusterIsRefusedBeforeAnyListCrossesTheTunnel</c>
    ///         pins; this throw is the backstop for a caller holding the client directly, which
    ///         <c>TunnelEndToEndTests.WatchIsRefusedByNameOnTheTunnelClient</c> pins.
    ///     </para>
    /// </remarks>
    public IAsyncEnumerable<KubeWatchEvent> WatchAsync(
        GroupVersionKind kind,
        string ns,
        string labelSelector,
        string resourceVersion,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(kind);

        throw new NotSupportedException(
            $"Cluster {clusterId:D} is reached through an agent tunnel, and a watch on {kind} cannot "
            + "cross it: the tunnel carries one response per request and a watch is a stream. "
            + "charts/agent/conformance.yaml § owed, informers-do-not-cross-the-tunnel."
        );
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Refused by name, for the watch's reason.</b> An attach is a stream both ways for as
    ///     long as somebody types, and the tunnel carries one response per request. A cloud terminal
    ///     on an agent-connected cluster needs a stream frame in <c>TunnelFrame</c> and a pump in the
    ///     agent — <c>charts/managed/cloud-shell/conformance.yaml § owed</c>,
    ///     <c>no-terminal-over-the-agent-tunnel</c>.
    /// </remarks>
    public Task<Result<IKubeTerminal>> AttachAsync(
        ObjectRef pod,
        string container,
        CancellationToken cancellationToken = default
    ) =>
        Task.FromResult(
            Result<IKubeTerminal>.Failure(
                ErrorCode.PreconditionFailed,
                $"Cluster {clusterId:D} is reached through an agent tunnel, and a terminal on '{pod}' "
                + "cannot cross it: the tunnel carries one response per request and a terminal is a "
                + "stream both ways. charts/managed/cloud-shell/conformance.yaml § owed, "
                + "no-terminal-over-the-agent-tunnel."
            )
        );

    /// <inheritdoc />
    public void Dispose() {
        // The route is the grain's or the test's; nothing here owns a socket.
    }

    async Task<Result<T>> CallAsync<T>(string operation, string payload, CancellationToken cancellationToken)
        where T : notnull {
        var response = await route
            .ExchangeAsync(TunnelFrame.Request(0, operation, payload), cancellationToken)
            .ConfigureAwait(false);

        return response.TryGetError(out var error)
            ? Result<T>.Failure(error)
            : TunnelOperations.Open<T>(response.GetValueOrThrow().Payload);
    }

    async Task<Result> CallAsync(string operation, string payload, CancellationToken cancellationToken) {
        var response = await route
            .ExchangeAsync(TunnelFrame.Request(0, operation, payload), cancellationToken)
            .ConfigureAwait(false);

        return response.TryGetError(out var error)
            ? Result.Failure(error)
            : TunnelOperations.Open(response.GetValueOrThrow().Payload);
    }
}
