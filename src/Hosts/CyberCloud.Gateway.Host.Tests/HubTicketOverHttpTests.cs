using CyberCloud.Gateway.Host.Hubs;
using CyberCloud.Gateway.Host.Tests.Infrastructure;
using NSubstitute;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json;

namespace CyberCloud.Gateway.Host.Tests;

/// <summary>
///     The hub ticket over a real socket: Kestrel, a WebSocket upgrade with no header, SignalR's
///     handshake, and a hub method — <c>HubTickets</c>, docs/plan/10 § SignalR.
/// </summary>
/// <remarks>
///     <para>
///         <c>HubTicketTests</c> proves what the pipeline decides. This proves what the listener does
///         with the decision: that an upgrade carrying nothing but <c>?ticket=</c> is admitted by the
///         composed host, completes the JSON protocol's handshake, and reaches
///         <see cref="TerminalHub" /> — whose <c>Attach</c> refuses a session id that is not a pod UID
///         before any grain is addressed, so the completion that comes back is the hub's own sentence,
///         which is the assertion. And that the same socket
///         opened again with the same ticket, or with the bearer token where the ticket goes, is a
///         <c>401</c> at the upgrade.
///     </para>
///     <para>
///         ⚠ <b>Why a raw <c>ClientWebSocket</c> and not <c>Microsoft.AspNetCore.SignalR.Client</c>.</b>
///         The portal is the client this exists for and it is TypeScript; what is being pinned is the
///         bytes on the wire — a handshake frame, an invocation frame, a completion frame — not a .NET
///         client's behaviour around them. Three frames is less to keep in step than a package.
///     </para>
/// </remarks>
public sealed class HubTicketOverHttpTests {
    static async Task<HttpStatusCode> UpgradeStatusAsync(OverHttpGateway gateway, string pathAndQuery) {
        var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;

        try {
            await socket.ConnectAsync(gateway.WebSocketUri(pathAndQuery), CancellationToken.None);
            return HttpStatusCode.SwitchingProtocols;
        } catch (WebSocketException) {
            return socket.HttpStatusCode;
        } finally {
            socket.Dispose();
        }
    }

    [Fact]
    public async Task ATicketOpensTheTerminalHubAndReachesAHubMethod() {
        await using var gateway = await OverHttpGateway.StartAsync();
        var ticket = await gateway.MintTicketAsync(HubNames.Terminal, gateway.Harness.Token(GatewayHarness.TenantA));

        using var socket = await OverHttpGateway.ConnectAsync(
            gateway.WebSocketUri($"/hubs/{HubNames.Terminal}?{HubTickets.QueryParameter}={ticket}")
        );
        socket.State.ShouldBe(WebSocketState.Open);

        // SignalR's handshake: the client names the protocol, the server answers `{}` for "fine".
        await OverHttpGateway.SendFrameAsync(socket, """{"protocol":"json","version":1}""");
        (await OverHttpGateway.ReceiveFrameAsync(socket)).ShouldBe("{}");

        // The portal's first call, exactly as it sends it.
        await OverHttpGateway.SendFrameAsync(
            socket,
            $"{{\"type\":1,\"invocationId\":\"1\",\"target\":\"{TerminalProtocol.Attach}\",\"arguments\":[\"sess-1\",80,24]}}"
        );

        JsonElement completion;

        while (true) {
            completion = JsonDocument.Parse(await OverHttpGateway.ReceiveFrameAsync(socket)).RootElement;

            if (completion.GetProperty("type").GetInt32() == 3) {
                break; // 6 is a ping; 3 is the completion
            }
        }

        completion.GetProperty("invocationId").GetString().ShouldBe("1");
        // A HubException's message travels to the client, which is the point of throwing one. `sess-1`
        // is not a pod UID, so the hub refuses it on its shape — before any grain is addressed, which
        // this harness's grain factory (a substitute nothing may call) makes a hard requirement.
        completion.GetProperty("error").GetString()!.ShouldContain("is not a session id");
        gateway.Harness.Grains.ReceivedCalls().ShouldBeEmpty();

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    [Fact]
    public async Task TheSameTicketDoesNotOpenASecondSocket() {
        await using var gateway = await OverHttpGateway.StartAsync();
        var ticket = await gateway.MintTicketAsync(HubNames.Terminal, gateway.Harness.Token(GatewayHarness.TenantA));
        var path = $"/hubs/{HubNames.Terminal}?{HubTickets.QueryParameter}={ticket}";

        using var first = await OverHttpGateway.ConnectAsync(gateway.WebSocketUri(path));
        first.State.ShouldBe(WebSocketState.Open);

        (await UpgradeStatusAsync(gateway, path)).ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AnUpgradeWithNoTicketOrWithTheBearerTokenInTheUrlIs401() {
        await using var gateway = await OverHttpGateway.StartAsync();
        var token = gateway.Harness.Token(GatewayHarness.TenantA);

        (await UpgradeStatusAsync(gateway, $"/hubs/{HubNames.Terminal}")).ShouldBe(HttpStatusCode.Unauthorized);
        (await UpgradeStatusAsync(gateway, $"/hubs/{HubNames.Terminal}?access_token={token}")).ShouldBe(
            HttpStatusCode.Unauthorized
        );
        (await UpgradeStatusAsync(gateway, $"/hubs/{HubNames.Terminal}?{HubTickets.QueryParameter}={token}"))
            .ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ATicketForAnotherHubIs401AtTheUpgrade() {
        await using var gateway = await OverHttpGateway.StartAsync();
        var ticket = await gateway.MintTicketAsync(HubNames.Resources, gateway.Harness.Token(GatewayHarness.TenantA));

        (await UpgradeStatusAsync(gateway, $"/hubs/{HubNames.Terminal}?{HubTickets.QueryParameter}={ticket}"))
            .ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task MintingOverHttpNeedsTheBearerHeader() {
        await using var gateway = await OverHttpGateway.StartAsync();

        using var response = await gateway.Http.PostAsync(
            $"/hubs/{HubNames.Terminal}/ticket",
            null,
            TestContext.Current.CancellationToken
        );

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ToString().ShouldBe("Bearer");
    }
}
