using System.Buffers.Binary;
using System.Net.WebSockets;

namespace CyberCloud.Kubernetes.Contracts.Tunnel;

/// <summary>
///     A message-oriented, full-duplex carrier for <see cref="TunnelFrame" />s: a WebSocket in
///     production, a pair of pipes in a test.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             This seam is the whole reason the tunnel can be tested end to end without a
///             network.
///         </b> Both sides of the tunnel — the platform's exchange and the agent's dispatcher
///         — are written against this and nothing else, so <c>TunnelEndToEndTests</c> runs the real
///         code at both ends over <see cref="StreamTunnelTransport" /> and a duplex stream, and the
///         only thing production substitutes is <see cref="WebSocketTunnelTransport" />.
///     </para>
///     <para>
///         <b>One reader and one writer at a time.</b> A transport is owned by one pump; concurrent
///         <see cref="SendAsync" /> calls are serialised by the implementation and concurrent
///         <see cref="ReceiveAsync" /> calls are a caller bug.
///     </para>
/// </remarks>
public interface ITunnelTransport : IAsyncDisposable {
    /// <summary>Sends one frame, whole.</summary>
    /// <param name="frame">The frame.</param>
    /// <param name="cancellationToken">Abandons the send.</param>
    Task SendAsync(TunnelFrame frame, CancellationToken cancellationToken = default);

    /// <summary>Receives the next frame, whole.</summary>
    /// <param name="cancellationToken">Abandons the receive.</param>
    /// <returns>The frame, or <see langword="null" /> once the peer has closed.</returns>
    Task<TunnelFrame?> ReceiveAsync(CancellationToken cancellationToken = default);

    /// <summary>Closes the carrier politely, telling the peer why.</summary>
    /// <param name="reason">Why. Reaches the peer where the carrier can carry it.</param>
    /// <param name="cancellationToken">Abandons the close.</param>
    Task CloseAsync(string reason, CancellationToken cancellationToken = default);
}

/// <summary>
///     <see cref="ITunnelTransport" /> over a byte <see cref="Stream" />: each frame is a four-byte
///     big-endian length followed by that many bytes of <see cref="TunnelCodec" /> JSON.
/// </summary>
/// <remarks>
///     ⚠ <b>Two streams, not one.</b> A duplex carrier in .NET is usually one object, but a test
///     wants to hand each side its own pair of pipes, and a real byte carrier — a TCP socket
///     wrapped in a <see cref="Stream" /> — is one object used for both directions. Taking a reader
///     and a writer separately serves both: pass the same stream twice for the second case.
/// </remarks>
public sealed class StreamTunnelTransport : ITunnelTransport {
    readonly Stream reader;
    readonly Stream writer;
    readonly SemaphoreSlim sendGate = new(1, 1);
    bool closed;

    /// <summary>Wraps a reader and a writer.</summary>
    /// <param name="reader">Where the peer's frames arrive.</param>
    /// <param name="writer">Where this side's frames go.</param>
    public StreamTunnelTransport(Stream reader, Stream writer) {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(writer);

        this.reader = reader;
        this.writer = writer;
    }

    /// <inheritdoc />
    public async Task SendAsync(TunnelFrame frame, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(frame);

        var payload = TunnelCodec.Encode(frame);

        if (payload.Length > TunnelCodec.MaxFrameBytes) {
            throw new InvalidOperationException(
                $"A {frame} frame is {payload.Length} bytes and the tunnel's frame cap is "
                + $"{TunnelCodec.MaxFrameBytes}."
            );
        }

        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);

        await sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            if (closed) {
                throw new InvalidOperationException("The tunnel transport is closed.");
            }

            await writer.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await writer.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        } finally {
            sendGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<TunnelFrame?> ReceiveAsync(CancellationToken cancellationToken = default) {
        var header = new byte[4];

        if (!await FillAsync(header, cancellationToken).ConfigureAwait(false)) {
            return null;
        }

        var length = BinaryPrimitives.ReadInt32BigEndian(header);

        if (length is <= 0 or > TunnelCodec.MaxFrameBytes) {
            throw new InvalidDataException(
                $"A tunnel frame announced {length} bytes; the cap is {TunnelCodec.MaxFrameBytes}."
            );
        }

        var payload = new byte[length];

        if (!await FillAsync(payload, cancellationToken).ConfigureAwait(false)) {
            return null;
        }

        var decoded = TunnelCodec.Decode(payload);

        if (decoded.TryGetError(out var error)) {
            throw new InvalidDataException(error.Message);
        }

        return decoded.GetValueOrThrow();
    }

    /// <inheritdoc />
    public async Task CloseAsync(string reason, CancellationToken cancellationToken = default) {
        if (closed) {
            return;
        }

        try {
            await SendAsync(TunnelFrame.Goodbye(reason), cancellationToken).ConfigureAwait(false);
        } catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) {
            // The peer went first. There is nobody to say goodbye to, which is fine.
        }

        closed = true;
        await writer.DisposeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() {
        closed = true;
        sendGate.Dispose();
        await reader.DisposeAsync().ConfigureAwait(false);
        await writer.DisposeAsync().ConfigureAwait(false);
    }

    /// <returns><c>false</c> when the stream ended before the buffer was full.</returns>
    async Task<bool> FillAsync(Memory<byte> buffer, CancellationToken cancellationToken) {
        var filled = 0;

        while (filled < buffer.Length) {
            var read = await reader.ReadAsync(buffer[filled..], cancellationToken).ConfigureAwait(false);

            if (read == 0) {
                return false;
            }

            filled += read;
        }

        return true;
    }
}

/// <summary>
///     <see cref="ITunnelTransport" /> over a <see cref="WebSocket" />: one text message per frame.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>A WebSocket and not gRPC, which is what docs/plan/09 § Cluster connections named.</b>
///         Three reasons, recorded in that section's own correction. The gateway already terminates
///         WebSockets for its four hubs, so this adds no listener, no package, and no ADR — and
///         docs/plan/02's register admits no package without one. A bidirectional gRPC stream needs
///         HTTP/2 end to end, and the corporate egress proxies a NAT'd on-prem cluster sits behind
///         routinely downgrade to HTTP/1.1, which is exactly the network this connection kind exists
///         for. And the frame protocol above is carrier-agnostic, so a gRPC carrier is one more
///         implementation of <see cref="ITunnelTransport" /> rather than a rewrite.
///     </para>
///     <para>
///         A message larger than <see cref="TunnelCodec.MaxFrameBytes" /> is refused on receive by
///         closing the socket with <see cref="WebSocketCloseStatus.MessageTooBig" />, which is the
///         status a peer can act on.
///     </para>
/// </remarks>
public sealed class WebSocketTunnelTransport : ITunnelTransport {
    readonly WebSocket socket;
    readonly SemaphoreSlim sendGate = new(1, 1);

    /// <summary>Wraps an open socket.</summary>
    /// <param name="socket">The socket, already upgraded.</param>
    public WebSocketTunnelTransport(WebSocket socket) {
        ArgumentNullException.ThrowIfNull(socket);
        this.socket = socket;
    }

    /// <inheritdoc />
    public async Task SendAsync(TunnelFrame frame, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(frame);

        var payload = TunnelCodec.Encode(frame);

        await sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            await socket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
        } finally {
            sendGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<TunnelFrame?> ReceiveAsync(CancellationToken cancellationToken = default) {
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];

        while (true) {
            ValueWebSocketReceiveResult result;
            try {
                result = await socket.ReceiveAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
            } catch (WebSocketException) {
                // The peer vanished without a close handshake — a NAT timeout, a pod restart. To
                // the pump that is the same event as a clean close.
                return null;
            }

            if (result.MessageType == WebSocketMessageType.Close) {
                return null;
            }

            await buffer.WriteAsync(chunk.AsMemory(0, result.Count), cancellationToken).ConfigureAwait(false);

            if (buffer.Length > TunnelCodec.MaxFrameBytes) {
                await socket.CloseAsync(
                    WebSocketCloseStatus.MessageTooBig,
                    $"a frame exceeded {TunnelCodec.MaxFrameBytes} bytes",
                    CancellationToken.None
                )
                    .ConfigureAwait(false);

                return null;
            }

            if (result.EndOfMessage) {
                break;
            }
        }

        var decoded = TunnelCodec.Decode(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));

        if (decoded.TryGetError(out var error)) {
            throw new InvalidDataException(error.Message);
        }

        return decoded.GetValueOrThrow();
    }

    /// <inheritdoc />
    public async Task CloseAsync(string reason, CancellationToken cancellationToken = default) {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) {
            return;
        }

        try {
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, cancellationToken)
                .ConfigureAwait(false);
        } catch (WebSocketException) {
            // Already gone.
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() {
        sendGate.Dispose();
        socket.Dispose();
        return ValueTask.CompletedTask;
    }
}
