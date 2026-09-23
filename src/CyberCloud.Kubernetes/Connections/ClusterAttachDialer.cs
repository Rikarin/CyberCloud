using CyberCloud.Core.Resources;

namespace CyberCloud.Kubernetes.Connections;

/// <summary>
///     The production <see cref="IKubeAttachDialer" />: the connection grain decides, and this process
///     dials.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The grain call comes first and nothing is awaited before it.</b> The connection grain's
///         tenancy check reads the calling grain's tenant from the message's source
///         (<see cref="ClusterConnectionTenantFilter" />). A call made from inside a session grain's
///         turn carries that grain as its source; the same call made after a
///         <c>ConfigureAwait(false)</c> would leave the activation's context and arrive as a client,
///         which the check refuses. So the call is the first thing this method does.
///     </para>
///     <para>
///         ⚠ <b>One API client per terminal, owned by the terminal.</b> The connection grain's client
///         lives in that grain's activation and cannot be lent across a message, so an attach builds
///         its own from the same descriptor through the same <see cref="IKubeApiClientFactory" /> —
///         the same credential resolver, and for an agent-connected cluster the same tunnel route,
///         which refuses by name. Disposing the terminal disposes the client.
///     </para>
/// </remarks>
/// <param name="grains">Where the connection grain is reached. Unqualified: it is a null-tenant grain.</param>
/// <param name="clients">How a descriptor becomes a live client — the silo's own factory.</param>
public sealed class ClusterAttachDialer(IGrainFactory grains, IKubeApiClientFactory clients) : IKubeAttachDialer {
    /// <inheritdoc />
    public async Task<Result<IKubeTerminal>> AttachAsync(
        Guid clusterId,
        ObjectRef pod,
        string container,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(pod);
        ArgumentException.ThrowIfNullOrEmpty(container);

        var authorized = await grains
            .GetGrain<IClusterConnectionGrain>(GrainKeys.ClusterConnection(clusterId))
            .AuthorizeAttachAsync(pod);

        if (authorized.TryGetError(out var refusal)) {
            return Result<IKubeTerminal>.Failure(refusal);
        }

        var connected = await clients.ConnectAsync(authorized.GetValueOrThrow(), cancellationToken)
            .ConfigureAwait(false);

        if (connected.TryGetError(out var connectError)) {
            return Result<IKubeTerminal>.Failure(connectError);
        }

        var api = connected.GetValueOrThrow();
        var attached = await api.AttachAsync(pod, container, cancellationToken).ConfigureAwait(false);

        if (attached.TryGetError(out var attachError)) {
            api.Dispose();
            return Result<IKubeTerminal>.Failure(attachError);
        }

        return Result<IKubeTerminal>.Success(new OwningTerminal(attached.GetValueOrThrow(), api));
    }

    /// <summary>A terminal that disposes the client it was opened through.</summary>
    sealed class OwningTerminal(IKubeTerminal inner, IDisposable client) : IKubeTerminal {
        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        public ValueTask WriteAsync(ReadOnlyMemory<byte> input, CancellationToken cancellationToken = default) =>
            inner.WriteAsync(input, cancellationToken);

        public ValueTask ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default) =>
            inner.ResizeAsync(columns, rows, cancellationToken);

        public async ValueTask DisposeAsync() {
            try {
                await inner.DisposeAsync().ConfigureAwait(false);
            } finally {
                client.Dispose();
            }
        }
    }
}
