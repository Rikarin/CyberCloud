using System.Globalization;
using System.Net.WebSockets;
using System.Text;

namespace CyberCloud.Kubernetes.Apply;

/// <summary>
///     <see cref="IKubeTerminal" /> over one <c>v4.channel.k8s.io</c> WebSocket: every message's
///     first byte names a channel, and the rest is that channel's bytes.
/// </summary>
/// <remarks>
///     <para>
///         The channels are the API server's: <c>0</c> standard input, <c>1</c> standard output,
///         <c>2</c> standard error, <c>3</c> the error channel — one JSON <c>Status</c> when the
///         process ends — and <c>4</c> resize, which takes <c>{"Width":…,"Height":…}</c>.
///     </para>
///     <para>
///         ⚠ <b>Empty frames are skipped, not treated as the end.</b> The API server opens each
///         channel with a frame holding only its channel byte. Reading that as a zero-length read
///         would report a live shell as ended on its first read.
///     </para>
///     <para>
///         ⚠ <b>One sender at a time.</b> A <see cref="WebSocket" /> allows one outstanding send and one
///         outstanding receive. Keystrokes and resizes both send and can arrive together, so they take
///         turns; the reader is only ever the one pump that owns the terminal.
///     </para>
/// </remarks>
/// <param name="socket">The upgraded socket. Owned: disposing the terminal closes it.</param>
sealed class WebSocketTerminal(WebSocket socket) : IKubeTerminal {
    const byte StdIn = 0;
    const byte StdOut = 1;
    const byte StdErr = 2;
    const byte Error = 3;
    const byte Resize = 4;

    readonly SemaphoreSlim sending = new(1, 1);
    readonly byte[] frame = new byte[32 * 1024];

    // What is left of the current output message after the caller's buffer filled.
    int pendingOffset;
    int pendingCount;

    // Whether the next received chunk starts a message, and so carries a channel byte.
    bool atMessageStart = true;
    byte channel;
    bool ended;

    /// <summary>The <c>Status</c> the API server sent on the error channel, or empty.</summary>
    /// <remarks>
    ///     Set once the process has ended. A shell that exited cleanly says <c>"Success"</c>; a
    ///     container killed at its deadline says why. Kept for the log, not for a decision — the pod's
    ///     own state is what a caller reads to decide.
    /// </remarks>
    public string Status { get; private set; } = string.Empty;

    /// <inheritdoc />
    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) {
        while (true) {
            if (pendingCount > 0) {
                var take = Math.Min(pendingCount, buffer.Length);
                frame.AsMemory(pendingOffset, take).CopyTo(buffer);
                pendingOffset += take;
                pendingCount -= take;
                return take;
            }

            if (ended) {
                return 0;
            }

            ValueWebSocketReceiveResult received;

            try {
                received = await socket.ReceiveAsync(frame.AsMemory(), cancellationToken).ConfigureAwait(false);
            } catch (WebSocketException) {
                // The API server went away mid-message. Nothing more will arrive; the caller reads the
                // pod to learn whether the shell is still there.
                ended = true;
                return 0;
            }

            if (received.MessageType == WebSocketMessageType.Close) {
                ended = true;
                return 0;
            }

            var offset = 0;

            if (atMessageStart) {
                if (received.Count == 0) {
                    continue;
                }

                channel = frame[0];
                offset = 1;
            }

            atMessageStart = received.EndOfMessage;

            var count = received.Count - offset;

            if (count <= 0) {
                continue;
            }

            switch (channel) {
                case StdOut:
                case StdErr:
                    pendingOffset = offset;
                    pendingCount = count;
                    break;

                case Error:
                    Status += Encoding.UTF8.GetString(frame, offset, count);
                    break;
            }
        }
    }

    /// <inheritdoc />
    public ValueTask WriteAsync(ReadOnlyMemory<byte> input, CancellationToken cancellationToken = default) =>
        input.IsEmpty ? ValueTask.CompletedTask : SendAsync(StdIn, input, cancellationToken);

    /// <inheritdoc />
    public ValueTask ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default) {
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);

        // Spelled as the kubelet's TerminalSize struct spells its fields. Go's decoder matches field
        // names case-insensitively, so the casing is convention rather than contract — measured:
        // lower-case keys resized the k3s 1.35 terminal just the same. What IS contract is that a
        // resize that never reaches this channel leaves `stty size` at the attach's default, which
        // is what PodAttachTests fails on.
        var json = string.Create(
            CultureInfo.InvariantCulture,
            $$"""{"Width":{{columns}},"Height":{{rows}}}"""
        );

        return SendAsync(Resize, Encoding.UTF8.GetBytes(json), cancellationToken);
    }

    async ValueTask SendAsync(byte target, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken) {
        var message = new byte[payload.Length + 1];
        message[0] = target;
        payload.CopyTo(message.AsMemory(1));

        await sending.WaitAsync(cancellationToken).ConfigureAwait(false);

        try {
            await socket.SendAsync(message, WebSocketMessageType.Binary, true, cancellationToken)
                .ConfigureAwait(false);
        } finally {
            sending.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() {
        try {
            if (socket.State == WebSocketState.Open) {
                using var bounded = new CancellationTokenSource(TimeSpan.FromSeconds(2));

                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "detached", bounded.Token)
                    .ConfigureAwait(false);
            }
        } catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) {
            // Closing politely is a courtesy to the API server; a socket that is already gone has
            // nothing to be polite to.
        } finally {
            socket.Dispose();
            sending.Dispose();
        }
    }
}
