using CyberCloud.Gateway.Host.Hubs;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace CyberCloud.Gateway.Host.Cluster.Conformance;

/// <summary>
///     One terminal pane's socket, speaking SignalR's JSON protocol by hand — what the portal's
///     <c>@microsoft/signalr</c> client sends, and nothing it does not.
/// </summary>
/// <remarks>
///     ⚠ <b>By hand rather than through the .NET SignalR client, for the reason the portal skips
///     negotiate.</b> A ticket is single-use and the WebSocket upgrade is the request that must spend
///     it; this opens the socket directly with <c>?ticket=</c>, completes the handshake, and reads
///     frames — so what reaches the hub is byte-for-byte the portal's traffic, with <c>byte[]</c> as
///     base64 on the JSON protocol.
/// </remarks>
public sealed class HubPane : IAsyncDisposable {
    const char RecordSeparator = '\u001E';

    readonly ClientWebSocket socket;
    readonly ConcurrentDictionary<string, TaskCompletionSource<string?>> completions = new(StringComparer.Ordinal);
    readonly StringBuilder screen = new();
    readonly Lock gate = new();
    readonly CancellationTokenSource stopping = new();
    readonly Task reading;
    int nextInvocation;

    HubPane(ClientWebSocket socket) {
        this.socket = socket;
        reading = ReadLoopAsync();
    }

    /// <summary>The <see cref="TerminalProtocol.Ended" /> sentence, once the hub has said the session is over.</summary>
    public string? Ended { get; private set; }

    /// <summary>Everything the shell has printed to this pane, decoded as UTF-8.</summary>
    public string Screen {
        get {
            lock (gate) {
                return screen.ToString();
            }
        }
    }

    /// <summary>How many <see cref="TerminalProtocol.Output" /> frames arrived.</summary>
    public int Frames { get; private set; }

    /// <summary>Opens the socket and completes SignalR's handshake.</summary>
    /// <param name="uri">The hub's address, ticket included.</param>
    public static async Task<HubPane> OpenAsync(Uri uri) {
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(uri, TestContext.Current.CancellationToken);

        await SendRawAsync(socket, """{"protocol":"json","version":1}""");

        // The handshake answer is `{}` before anything else can arrive.
        var handshake = await ReceiveRawAsync(socket, CancellationToken.None);
        handshake.ShouldStartWith("{}");

        return new(socket);
    }

    /// <summary>Invokes a hub method and waits for its completion.</summary>
    /// <param name="method">The wire name — one of <see cref="TerminalProtocol" />.</param>
    /// <param name="arguments">The arguments, serialised as the JSON protocol does.</param>
    /// <returns>The completion's error, or <see langword="null" /> when the method succeeded.</returns>
    public async Task<string?> InvokeAsync(string method, params object[] arguments) {
        var id = Interlocked.Increment(ref nextInvocation).ToString(CultureInfo.InvariantCulture);
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        completions[id] = completion;

        await SendRawAsync(
            socket,
            JsonSerializer.Serialize(new { type = 1, invocationId = id, target = method, arguments })
        );

        return await completion.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
    }

    /// <summary>Types into the shell — <see cref="TerminalProtocol.Send" /> with the bytes as base64.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="keys">What to type. A carriage return is Enter.</param>
    public Task<string?> TypeAsync(string sessionId, string keys) =>
        InvokeAsync(TerminalProtocol.Send, sessionId, Convert.ToBase64String(Encoding.UTF8.GetBytes(keys)));

    /// <summary>Waits until the screen shows some text, or fails with what it did show.</summary>
    /// <param name="expected">The text.</param>
    /// <param name="within">How long to wait.</param>
    public async Task WaitForScreenAsync(string expected, TimeSpan within) {
        var deadline = DateTimeOffset.UtcNow + within;

        while (!Screen.Contains(expected, StringComparison.Ordinal)) {
            if (DateTimeOffset.UtcNow > deadline) {
                throw new TimeoutException($"'{expected}' never appeared. Screen: {Screen}");
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Waits for <see cref="TerminalProtocol.Ended" />.</summary>
    /// <param name="within">How long to wait.</param>
    public async Task<string> WaitForEndedAsync(TimeSpan within) {
        var deadline = DateTimeOffset.UtcNow + within;

        while (Ended is null) {
            if (DateTimeOffset.UtcNow > deadline) {
                throw new TimeoutException($"the hub never said the session ended. Screen: {Screen}");
            }

            await Task.Delay(200, TestContext.Current.CancellationToken);
        }

        return Ended;
    }

    async Task ReadLoopAsync() {
        var pending = new StringBuilder();

        try {
            while (!stopping.IsCancellationRequested) {
                var text = await ReceiveRawAsync(socket, stopping.Token);
                pending.Append(text);

                var all = pending.ToString();
                var last = all.LastIndexOf(RecordSeparator);

                if (last < 0) {
                    continue;
                }

                pending.Clear().Append(all[(last + 1)..]);

                foreach (var record in all[..last].Split(RecordSeparator, StringSplitOptions.RemoveEmptyEntries)) {
                    Dispatch(JsonDocument.Parse(record).RootElement);
                }
            }
        } catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or InvalidOperationException) {
            // Closed — by the hub after Ended, or by DisposeAsync.
        } finally {
            foreach (var waiting in completions.Values) {
                waiting.TrySetResult("the socket closed before the completion arrived");
            }
        }
    }

    void Dispatch(JsonElement message) {
        switch (message.GetProperty("type").GetInt32()) {
            case 1 when message.GetProperty("target").GetString() == TerminalProtocol.Output:
                var bytes = Convert.FromBase64String(message.GetProperty("arguments")[0].GetString()!);

                lock (gate) {
                    screen.Append(Encoding.UTF8.GetString(bytes));
                    Frames++;
                }

                break;

            case 1 when message.GetProperty("target").GetString() == TerminalProtocol.Ended:
                Ended = message.GetProperty("arguments")[0].GetString();
                break;

            case 3:
                var id = message.GetProperty("invocationId").GetString()!;
                var error = message.TryGetProperty("error", out var e) ? e.GetString() : null;

                if (completions.TryRemove(id, out var completion)) {
                    completion.TrySetResult(error);
                }

                break;
        }
    }

    static Task SendRawAsync(WebSocket socket, string json) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(json + RecordSeparator), WebSocketMessageType.Text, true, CancellationToken.None);

    static async Task<string> ReceiveRawAsync(WebSocket socket, CancellationToken cancellationToken) {
        var buffer = new byte[64 * 1024];
        var text = new StringBuilder();

        while (true) {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);

            if (result.MessageType == WebSocketMessageType.Close) {
                throw new InvalidOperationException("the hub closed the socket");
            }

            text.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

            if (result.EndOfMessage) {
                return text.ToString();
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() {
        await stopping.CancelAsync();

        try {
            if (socket.State == WebSocketState.Open) {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
            }
        } catch (WebSocketException) {
            // Already gone.
        }

        await reading.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        socket.Dispose();
        stopping.Dispose();
    }
}
