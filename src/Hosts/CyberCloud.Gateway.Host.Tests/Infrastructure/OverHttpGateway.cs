using CyberCloud.Gateway.Host.Pipeline;
using CyberCloud.Gateway.Host.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net.WebSockets;
using System.Text;

namespace CyberCloud.Gateway.Host.Tests.Infrastructure;

/// <summary>
///     The harness's pipeline behind a real listener: Kestrel on a loopback port, the four hubs
///     mapped by <c>GatewayComposition.MapGateway</c>, and nothing substituted between the socket and
///     stage 1.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             The project header says no <c>TestServer</c> and no <c>WebApplicationFactory</c>, and
///             this is neither — and the reason it exists is the one thing a <c>DefaultHttpContext</c>
///             cannot represent.
///         </b> A WebSocket upgrade is decided by Kestrel and SignalR's own middleware after the
///         pipeline has run; the harness's <c>SendAsync</c> stops where the pipeline stops and can say
///         only that the request <i>would</i> have been handed on. Whether a browser's upgrade with
///         nothing but <c>?ticket=</c> actually opens a hub, completes SignalR's handshake and reaches
///         a hub method is a property of the composed listener, so that is what this class starts.
///         Kestrel is in the shared framework; no package was added.
///     </para>
///     <para>
///         The stages are the <see cref="GatewayHarness" />'s own instances, so a token issued through
///         <see cref="GatewayHarness.Token" /> is honoured here and a ticket minted here is visible to
///         <see cref="GatewayHarness.Tickets" />. Port 0, so thirteen suites on one machine cannot
///         collide.
///     </para>
/// </remarks>
sealed class OverHttpGateway : IAsyncDisposable {
    /// <summary>SignalR's record separator — every JSON-protocol frame ends with it.</summary>
    public const char RecordSeparator = '\u001E';

    readonly WebApplication app;

    OverHttpGateway(GatewayHarness harness, WebApplication app, Uri baseUri) {
        Harness = harness;
        this.app = app;
        BaseUri = baseUri;
        Http = new() { BaseAddress = baseUri };
    }

    /// <summary>The fakes behind the listener.</summary>
    public GatewayHarness Harness { get; }

    /// <summary>Where the listener answers, <c>http://127.0.0.1:{port}/</c>.</summary>
    public Uri BaseUri { get; }

    /// <summary>A client for the ordinary requests.</summary>
    public HttpClient Http { get; }

    /// <summary>Starts the listener.</summary>
    public static Task<OverHttpGateway> StartAsync() => StartAsync(new GatewayHarness());

    /// <summary>Starts the listener over a harness the caller composed — one over a real manager, for instance.</summary>
    /// <param name="harness">The stages to put behind the socket.</param>
    public static async Task<OverHttpGateway> StartAsync(GatewayHarness harness) {
        ArgumentNullException.ThrowIfNull(harness);

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        foreach (var stage in harness.Stages) {
            builder.Services.AddSingleton(stage);
        }

        builder.Services.AddSingleton<GatewayPipeline>();
        builder.Services.AddSingleton(harness.Grains);
        builder.Services.AddSingleton<IConcurrencyLimiter>(harness.Concurrency);
        builder.Services.AddSignalR();

        var app = builder.Build();
        app.MapGateway();
        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses.First();

        return new(harness, app, new Uri(address + "/"));
    }

    /// <summary>The <c>ws://</c> form of <see cref="BaseUri" /> plus a path and query.</summary>
    public Uri WebSocketUri(string pathAndQuery) => new("ws://" + BaseUri.Authority + pathAndQuery);

    /// <summary>Mints a ticket for a hub the way the portal does: an authenticated <c>POST</c>.</summary>
    public async Task<string> MintTicketAsync(string hub, string token) {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/hubs/{hub}/ticket");
        request.Headers.Authorization = new("Bearer", token);

        using var response = await Http.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync();
        return System.Text.Json.JsonDocument.Parse(body).RootElement.GetProperty("ticket").GetString() ?? "";
    }

    /// <summary>Opens a WebSocket, collecting the upgrade's status for the refusals.</summary>
    public static async Task<ClientWebSocket> ConnectAsync(Uri uri) {
        var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;
        await socket.ConnectAsync(uri, CancellationToken.None);
        return socket;
    }

    /// <summary>Sends one SignalR JSON-protocol frame.</summary>
    public static Task SendFrameAsync(WebSocket socket, string json) =>
        socket.SendAsync(
            Encoding.UTF8.GetBytes(json + RecordSeparator),
            WebSocketMessageType.Text,
            true,
            CancellationToken.None
        );

    /// <summary>Reads one frame, up to and including its record separator.</summary>
    public static async Task<string> ReceiveFrameAsync(WebSocket socket) {
        var buffer = new byte[16 * 1024];
        var text = new StringBuilder();

        while (true) {
            var result = await socket.ReceiveAsync(buffer, CancellationToken.None);

            if (result.MessageType == WebSocketMessageType.Close) {
                throw new InvalidOperationException(
                    $"The hub closed the socket: {result.CloseStatus} {result.CloseStatusDescription}"
                );
            }

            text.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

            if (result.EndOfMessage) {
                var frame = text.ToString();
                return frame.EndsWith(RecordSeparator) ? frame[..^1] : frame;
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() {
        Http.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
    }
}
