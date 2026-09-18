using CyberCloud.Gateway.Host.Http;
using CyberCloud.Kubernetes.Contracts.Tunnel;
using CyberCloud.Kubernetes.Tunnel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;

namespace CyberCloud.Gateway.Host.Agent;

/// <summary>
///     <c>GET /agent/v1/tunnel</c>: where a connected cluster's agent dials in — docs/plan/09
///     § Cluster connections, the <c>AgentInitiated</c> row.
/// </summary>
/// <remarks>
///     <para>
///         The request carries three things: <c>X-CyberCloud-Cluster</c>, the cluster resource id
///         in <c>D</c> form; <c>Authorization: Bearer</c>, the enrollment token from
///         <c>listInstallCommand</c> or the credential the agent was handed in exchange for it; and
///         <c>X-CyberCloud-Agent-Version</c>. <see cref="AgentTunnelRelay" /> hashes the bearer value
///         and asks the tunnel grain for exactly that cluster. Only then is the socket upgraded.
///     </para>
///     <para>
///         ⚠ <b>Every refusal is <c>401</c> with the same body</b> — a missing header, a malformed
///         id, a token that was spent, a cluster that was revoked. The agent's operator gets "check
///         the install command" and nothing that would tell a stolen token which of those it is;
///         the gateway's log has the detail, keyed by the request id in the response.
///     </para>
///     <para>
///         ⚠
///         <b>
///             The pipeline's nine stages ran before this — stages 1 and 5 for real, the rest as
///             pass-through
///         </b> — because <c>GatewayComposition.MapGateway</c> maps the endpoint behind
///         the one middleware. A tunnel endpoint mapped anywhere else would be a listener with no
///         per-IP rate limit, which is the shape docs/plan/10 § Rate limiting exists to refuse.
///     </para>
/// </remarks>
static class AgentTunnelEndpoint {
    /// <summary>Serves one agent for the life of its socket.</summary>
    /// <param name="http">The request, already through the pipeline.</param>
    public static async Task HandleAsync(HttpContext http) {
        ArgumentNullException.ThrowIfNull(http);

        var logger = http.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("CyberCloud.Gateway.Host.Agent");
        var relay = http.RequestServices.GetRequiredService<AgentTunnelRelay>();
        var requestId = http.Response.Headers[GatewayHeaders.RequestId].ToString();

        if (!http.WebSockets.IsWebSocketRequest) {
            await Refuse(
                http,
                StatusCodes.Status426UpgradeRequired,
                "The agent tunnel is a WebSocket. Send an upgrade request with the Sec-WebSocket-* headers."
            );

            return;
        }

        var clusterHeader = http.Request.Headers[TunnelCodec.ClusterHeader].ToString();
        var bearer = Bearer(http.Request.Headers.Authorization.ToString());
        var agentVersion = http.Request.Headers[TunnelCodec.AgentVersionHeader].ToString();

        if (!GuidFormat.TryParseD(clusterHeader, out var clusterId) || bearer is null) {
            logger.LogWarning(
                "Agent upgrade {RequestId} refused: the request did not carry a cluster id and a bearer credential.",
                requestId
            );

            await Refuse(http, StatusCodes.Status401Unauthorized, NotAdmitted);
            return;
        }

        var admitted = await relay.AdmitAsync(clusterId, bearer, agentVersion);

        if (admitted.TryGetError(out var refusal)) {
            logger.LogWarning(
                "Agent upgrade {RequestId} for cluster {Cluster} refused: {Reason}",
                requestId,
                clusterId,
                refusal.Message
            );

            await Refuse(http, StatusCodes.Status401Unauthorized, NotAdmitted);
            return;
        }

        var session = admitted.GetValueOrThrow();

        using var socket = await http.WebSockets.AcceptWebSocketAsync(TunnelCodec.SubProtocol);
        await using var transport = new WebSocketTunnelTransport(socket);

        logger.LogInformation(
            "Agent {Version} for cluster {Cluster} connected as session {Session} (request {RequestId}).",
            agentVersion,
            clusterId,
            session.SessionId,
            requestId
        );

        var reason = await session.RunAsync(transport, http.RequestAborted);

        logger.LogInformation(
            "Agent session {Session} for cluster {Cluster} ended: {Reason}",
            session.SessionId,
            clusterId,
            reason
        );
    }

    const string NotAdmitted =
        "The agent was not admitted. Check the cluster id and the credential against the install "
        + "command from listInstallCommand, and run it again for a fresh token if this one was used "
        + "or has expired.";

    static string? Bearer(string authorization) {
        if (!AuthenticationHeaderValue.TryParse(authorization, out var parsed)
            || !string.Equals(parsed.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(parsed.Parameter)) {
            return null;
        }

        return parsed.Parameter.Trim();
    }

    static Task Refuse(HttpContext http, int status, string message) {
        http.Response.StatusCode = status;

        if (status == StatusCodes.Status401Unauthorized) {
            http.Response.Headers.WWWAuthenticate = "Bearer realm=\"cybercloud-agent\"";
        }

        http.Response.ContentType = ErrorBody.ContentType;

        var body = ErrorBody.Render(new(ErrorCode.AuthorizationFailed, message));
        http.Response.ContentLength = body.Length;

        return http.Response.Body.WriteAsync(body, 0, body.Length, http.RequestAborted);
    }
}
