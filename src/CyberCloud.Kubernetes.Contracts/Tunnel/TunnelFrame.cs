using System.Text.Json;
using System.Text.Json.Serialization;

namespace CyberCloud.Kubernetes.Contracts.Tunnel;

/// <summary>What one frame on an agent tunnel is for.</summary>
/// <remarks>
///     docs/plan/09 § Cluster connections, the <c>AgentInitiated</c> row. The agent dials out once
///     and everything after that is one of these, in either direction. There is no <i>open a
///     stream</i> kind: every request has exactly one response, and an operation that needs more
///     than one — a watch — is refused rather than half-carried. See
///     <c>charts/agent/conformance.yaml § owed</c>, <c>informers-do-not-cross-the-tunnel</c>.
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Kubernetes.Tunnel.TunnelFrameKind")]
public enum TunnelFrameKind {
    /// <summary><c>default</c>. Not a frame.</summary>
    Unknown = 0,

    /// <summary>
    ///     Platform → agent, once, right after the socket opens. Carries the session id, the
    ///     heartbeat interval, and — on an enrollment — the long-lived credential the agent keeps.
    /// </summary>
    Welcome = 1,

    /// <summary>Platform → agent. One Kubernetes API call to make; <see cref="TunnelFrame.Operation" /> says which.</summary>
    Request = 2,

    /// <summary>Agent → platform. The answer to the request with the same <see cref="TunnelFrame.Id" />.</summary>
    Response = 3,

    /// <summary>
    ///     Agent → platform, on a timer. What makes a connected cluster <c>Succeeded</c> and what
    ///     keeps it <c>Healthy</c> — docs/plan/09 § Cluster connections.
    /// </summary>
    Heartbeat = 4,

    /// <summary>Either direction. The sender is closing and says why.</summary>
    Goodbye = 5
}

/// <summary>
///     One message on an agent tunnel. The same shape crosses the WebSocket between agent and gateway
///     and the Orleans wire between gateway and silo.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The payload is a JSON string and not a typed member, and that is what lets the
///         gateway relay without understanding.</b> The gateway is an Orleans client above
///         <c>CyberCloud.Kubernetes</c> (docs/plan/03 § Assembly graph rules, rule 3) and it must not
///         bind the types a request carries — an <c>ObjectRef</c> is fine, a <c>ListPage</c> is
///         not. So the frame is the envelope and the envelope is opaque: the gateway hashes a
///         credential, hands frames to the grain, and hands the grain's frames to the socket.
///         <c>TunnelOperations</c> in <c>CyberCloud.Kubernetes</c> is the only reader of
///         <see cref="Payload" />, at both ends.
///     </para>
///     <para>
///         ⚠ <b>No member here ends in <c>Token</c>, <c>Secret</c> or <c>Key</c>, and that is CC1005
///         rather than taste.</b> A credential never rides in a frame's typed members: the
///         enrollment token is an HTTP header on the upgrade request and the long-lived credential
///         is inside the <see cref="TunnelFrameKind.Welcome" /> payload, which is a string.
///     </para>
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Kubernetes.Tunnel.TunnelFrame")]
public sealed record TunnelFrame {
    /// <summary>What the frame is.</summary>
    [Id(0)]
    [JsonPropertyName("kind")]
    public TunnelFrameKind Kind { get; init; } = TunnelFrameKind.Unknown;

    /// <summary>
    ///     Correlates a <see cref="TunnelFrameKind.Response" /> with its
    ///     <see cref="TunnelFrameKind.Request" />. Zero for the kinds that have no pair.
    /// </summary>
    [Id(1)]
    [JsonPropertyName("id")]
    public long Id { get; init; }

    /// <summary>The operation a request names — <c>ping</c>, <c>get</c>, <c>apply</c>, … Empty otherwise.</summary>
    [Id(2)]
    [JsonPropertyName("op")]
    public string Operation { get; init; } = string.Empty;

    /// <summary>The operation's arguments, or its answer, as JSON. <c>{}</c> when there is nothing to say.</summary>
    [Id(3)]
    [JsonPropertyName("payload")]
    public string Payload { get; init; } = "{}";

    /// <summary>A request for <paramref name="operation" />.</summary>
    /// <param name="id">The correlation id. Unique within one session.</param>
    /// <param name="operation">The operation name.</param>
    /// <param name="payload">The arguments, as JSON.</param>
    public static TunnelFrame Request(long id, string operation, string payload) =>
        new() { Kind = TunnelFrameKind.Request, Id = id, Operation = operation, Payload = payload };

    /// <summary>The answer to the request carrying <paramref name="id" />.</summary>
    /// <param name="id">The request's correlation id.</param>
    /// <param name="operation">The operation answered, echoed so a log line can name it.</param>
    /// <param name="payload">The answer, as JSON.</param>
    public static TunnelFrame Response(long id, string operation, string payload) =>
        new() { Kind = TunnelFrameKind.Response, Id = id, Operation = operation, Payload = payload };

    /// <summary>A heartbeat carrying whatever the agent wants to report about itself.</summary>
    /// <param name="payload">The report, as JSON.</param>
    public static TunnelFrame Heartbeat(string payload = "{}") => new() { Kind = TunnelFrameKind.Heartbeat, Payload = payload };

    /// <summary>A goodbye carrying the reason.</summary>
    /// <param name="reason">Why the sender is closing.</param>
    public static TunnelFrame Goodbye(string reason) =>
        new() { Kind = TunnelFrameKind.Goodbye, Payload = TunnelCodec.Serialize(new GoodbyeBody { Reason = reason }) };

    /// <inheritdoc />
    public override string ToString() =>
        Kind == TunnelFrameKind.Request || Kind == TunnelFrameKind.Response
            ? $"{Kind} #{Id} {Operation}"
            : Kind.ToString();
}

/// <summary>The payload of a <see cref="TunnelFrameKind.Goodbye" />.</summary>
public sealed record GoodbyeBody {
    /// <summary>Why the sender is closing.</summary>
    [JsonPropertyName("reason")]
    public string Reason { get; init; } = string.Empty;
}

/// <summary>
///     The payload of a <see cref="TunnelFrameKind.Welcome" /> — the first frame the platform sends.
/// </summary>
public sealed record WelcomeBody {
    /// <summary>The session, as the platform will name it in every log line.</summary>
    [JsonPropertyName("sessionId")]
    public Guid SessionId { get; init; }

    /// <summary>How often the agent is expected to send a <see cref="TunnelFrameKind.Heartbeat" />.</summary>
    [JsonPropertyName("heartbeatSeconds")]
    public int HeartbeatSeconds { get; init; }

    /// <summary>
    ///     The long-lived credential the agent keeps, present only when the socket was opened with
    ///     the one-time enrollment token. <see langword="null" /> on every later connection.
    /// </summary>
    /// <remarks>
    ///     ⚠ Named <c>credential</c> and not <c>token</c> on purpose: the install token is spent the
    ///     moment this arrives, and an agent that stored this under the token's name would present
    ///     it as one on the next restart. See <see cref="AgentCredentials" />.
    /// </remarks>
    [JsonPropertyName("credential")]
    public string? Credential { get; init; }
}

/// <summary>The payload of a <see cref="TunnelFrameKind.Heartbeat" />.</summary>
public sealed record HeartbeatBody {
    /// <summary>The agent's own version, so the portal can say which agent a cluster runs.</summary>
    [JsonPropertyName("agentVersion")]
    public string AgentVersion { get; init; } = string.Empty;

    /// <summary>The API server's version string, when the agent has asked it. Empty until then.</summary>
    [JsonPropertyName("kubernetesVersion")]
    public string KubernetesVersion { get; init; } = string.Empty;
}

/// <summary>
///     The one encoding of a <see cref="TunnelFrame" /> on the wire: a UTF-8 JSON object.
/// </summary>
/// <remarks>
///     ⚠ <b>Text, not binary, and no schema registry.</b> The agent is a process the tenant runs and
///     may run for months without an upgrade, so the wire has to be readable in a packet capture and
///     tolerant of members it does not know. JSON with unknown members ignored is both; a binary
///     framing with a version byte is neither, until somebody writes the tooling.
/// </remarks>
public static class TunnelCodec {
    /// <summary>The protocol version the <c>Sec-WebSocket-Protocol</c> header names.</summary>
    public const string SubProtocol = "cybercloud.agent.v1";

    /// <summary>The path on the gateway an agent dials.</summary>
    public const string TunnelPath = "/agent/v1/tunnel";

    /// <summary>The request header the agent puts its cluster resource id in, <c>D</c> form.</summary>
    public const string ClusterHeader = "X-CyberCloud-Cluster";

    /// <summary>The request header the agent puts its own version in.</summary>
    public const string AgentVersionHeader = "X-CyberCloud-Agent-Version";

    /// <summary>
    ///     The largest frame either side accepts, in bytes. A Kubernetes object is capped at 1.5 MiB
    ///     by etcd, so a frame carrying one — plus a list page of them — fits with room to spare.
    /// </summary>
    public const int MaxFrameBytes = 16 * 1024 * 1024;

    static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Encodes a frame as UTF-8 JSON.</summary>
    /// <param name="frame">The frame.</param>
    public static byte[] Encode(TunnelFrame frame) {
        ArgumentNullException.ThrowIfNull(frame);
        return JsonSerializer.SerializeToUtf8Bytes(frame, Options);
    }

    /// <summary>Decodes a frame from UTF-8 JSON.</summary>
    /// <param name="bytes">The bytes of one whole message.</param>
    /// <returns>The frame, or a failure naming what was wrong with the bytes.</returns>
    /// <remarks>
    ///     ⚠ A frame with no <see cref="TunnelFrame.Kind" /> is refused rather than passed on as
    ///     <see cref="TunnelFrameKind.Unknown" />, because every consumer switches on the kind and
    ///     an unknown one would fall through to "ignore" — which is the wrong default for a message
    ///     a peer went to the trouble of sending.
    /// </remarks>
    public static Result<TunnelFrame> Decode(ReadOnlySpan<byte> bytes) {
        try {
            var frame = JsonSerializer.Deserialize<TunnelFrame>(bytes, Options);

            if (frame is null || frame.Kind == TunnelFrameKind.Unknown) {
                return Result<TunnelFrame>.Failure(
                    ErrorCode.InvalidRequestBody,
                    "A tunnel frame arrived with no kind. Every frame carries one of welcome, "
                    + "request, response, heartbeat, or goodbye."
                );
            }

            return Result<TunnelFrame>.Success(frame);
        } catch (JsonException ex) {
            return Result<TunnelFrame>.Failure(
                ErrorCode.InvalidRequestBody,
                $"A tunnel frame could not be read as JSON: {ex.Message}"
            );
        }
    }

    /// <summary>Serializes a payload body with the codec's options.</summary>
    /// <typeparam name="T">The body type.</typeparam>
    /// <param name="body">The body.</param>
    public static string Serialize<T>(T body) => JsonSerializer.Serialize(body, Options);

    /// <summary>Deserializes a payload body with the codec's options.</summary>
    /// <typeparam name="T">The body type.</typeparam>
    /// <param name="json">The payload.</param>
    /// <returns>The body, or a failure when the payload is not one.</returns>
    public static Result<T> Deserialize<T>(string json)
        where T : notnull {
        try {
            var body = JsonSerializer.Deserialize<T>(json, Options);

            return body is null
                ? Result<T>.Failure(ErrorCode.InvalidRequestBody, $"A tunnel payload of {typeof(T).Name} was null.")
                : Result<T>.Success(body);
        } catch (JsonException ex) {
            return Result<T>.Failure(
                ErrorCode.InvalidRequestBody,
                $"A tunnel payload could not be read as {typeof(T).Name}: {ex.Message}"
            );
        }
    }
}
