using CyberCloud.Kubernetes.Apply;
using CyberCloud.Kubernetes.Contracts.Tunnel;
using System.Text.Json.Serialization;

namespace CyberCloud.Kubernetes.Tunnel;

/// <summary>
///     The operations an agent tunnel carries — one per unary member of <see cref="IKubeApiClient" />
///     — and how each one's arguments and answer are spelled in a frame's payload.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             The tunnel carries <see cref="IKubeApiClient" /> calls and not HTTP, and that is a
///             security decision before it is a convenience.
///         </b> docs/plan/09 § Cluster connections
///         calls the agent "a reverse-tunnel client and a scoped proxy". A proxy that forwarded raw
///         HTTP would let a compromised platform — or a platform bug — send <i>any</i> request to
///         the tenant's API server with the agent's service account. A proxy that understands
///         seven operations forwards seven operations: a get by reference, a server-side apply of
///         a command the labels are checked on, a delete, an owner patch, discovery, a paged
///         list, and a ping. Nothing else has a spelling on this wire.
///     </para>
///     <para>
///         It is also what makes the whole thing testable in one process: the agent end dispatches
///         to an <see cref="IKubeApiClient" />, and <c>RecordingApiClient</c> is one.
///     </para>
///     <para>
///         ⚠ <b><see cref="IKubeApiClient.WatchAsync" /> is not here.</b> A watch is a stream and
///         every frame here has one answer. <c>TunnelKubeApiClient.WatchAsync</c> refuses by name —
///         <c>charts/agent/conformance.yaml § owed</c>, <c>informers-do-not-cross-the-tunnel</c>.
///     </para>
/// </remarks>
public static class TunnelOperations {
    /// <summary><see cref="IKubeApiClient.PingAsync" />.</summary>
    public const string Ping = "ping";

    /// <summary><see cref="IKubeApiClient.GetAsync" />.</summary>
    public const string Get = "get";

    /// <summary><see cref="IKubeApiClient.ApplyAsync" />.</summary>
    public const string Apply = "apply";

    /// <summary><see cref="IKubeApiClient.DeleteAsync" />.</summary>
    public const string Delete = "delete";

    /// <summary><see cref="IKubeApiClient.SetOwnerAsync" />.</summary>
    public const string SetOwner = "setOwner";

    /// <summary><see cref="IKubeApiClient.DiscoverNamespacedKindsAsync" />.</summary>
    public const string Discover = "discover";

    /// <summary><see cref="IKubeApiClient.ListAsync" />.</summary>
    public const string List = "list";

    /// <summary>Every operation the wire has a spelling for, so a test can prove the agent serves each.</summary>
    public static IReadOnlyList<string> All { get; } = [Ping, Get, Apply, Delete, SetOwner, Discover, List];

    // ── Payloads ───────────────────────────────────────────────────────────────────────────────

    /// <summary>The arguments of a <see cref="Delete" />.</summary>
    public sealed record DeleteArguments {
        /// <summary>What to delete.</summary>
        [JsonPropertyName("target")]
        public ObjectRef Target { get; init; } = new();

        /// <summary>How to cascade.</summary>
        [JsonPropertyName("policy")]
        public CascadePolicy Policy { get; init; } = CascadePolicy.Background;
    }

    /// <summary>The arguments of a <see cref="SetOwner" />.</summary>
    public sealed record SetOwnerArguments {
        /// <summary>The dependent.</summary>
        [JsonPropertyName("target")]
        public ObjectRef Target { get; init; } = new();

        /// <summary>The controller to write, or <see langword="null" /> to clear.</summary>
        [JsonPropertyName("owner")]
        public OwnerRef? Owner { get; init; }
    }

    /// <summary>The arguments of a <see cref="List" />.</summary>
    public sealed record ListArguments {
        /// <summary>The kind.</summary>
        [JsonPropertyName("kind")]
        public GroupVersionKind Kind { get; init; } = new();

        /// <summary>The namespace, or empty.</summary>
        [JsonPropertyName("namespace")]
        public string Namespace { get; init; } = string.Empty;

        /// <summary>The selector.</summary>
        [JsonPropertyName("labelSelector")]
        public string LabelSelector { get; init; } = string.Empty;

        /// <summary>The resume cursor, or <see langword="null" />.</summary>
        [JsonPropertyName("resourceVersion")]
        public string? ResourceVersion { get; init; }

        /// <summary>The pagination cursor, or <see langword="null" />.</summary>
        [JsonPropertyName("continueToken")]
        public string? ContinueToken { get; init; }

        /// <summary>The page size, or <see langword="null" />.</summary>
        [JsonPropertyName("limit")]
        public int? Limit { get; init; }
    }

    /// <summary>The answer of a <see cref="List" /> — <see cref="ListPage" /> with wire names.</summary>
    public sealed record ListAnswer {
        /// <summary>The objects, as JSON.</summary>
        [JsonPropertyName("items")]
        public List<string> Items { get; init; } = [];

        /// <summary>The list's <c>resourceVersion</c>.</summary>
        [JsonPropertyName("resourceVersion")]
        public string ResourceVersion { get; init; } = string.Empty;

        /// <summary>The pagination cursor, or empty.</summary>
        [JsonPropertyName("continueToken")]
        public string ContinueToken { get; init; } = string.Empty;
    }

    /// <summary>The answer of a <see cref="Ping" />.</summary>
    public sealed record PingAnswer {
        /// <summary>The server's version string.</summary>
        [JsonPropertyName("version")]
        public string Version { get; init; } = string.Empty;
    }

    /// <summary>The answer of a <see cref="Discover" />.</summary>
    public sealed record DiscoverAnswer {
        /// <summary>The kinds.</summary>
        [JsonPropertyName("kinds")]
        public List<GroupVersionKind> Kinds { get; init; } = [];
    }

    // ── The result envelope ────────────────────────────────────────────────────────────────────

    /// <summary>A failure, as the wire spells it.</summary>
    public sealed record WireError {
        /// <summary><see cref="ErrorCode.Value" />.</summary>
        [JsonPropertyName("code")]
        public string Code { get; init; } = string.Empty;

        /// <summary>The message, verbatim.</summary>
        [JsonPropertyName("message")]
        public string Message { get; init; } = string.Empty;

        /// <summary>The JSON pointer, when there was one.</summary>
        [JsonPropertyName("target")]
        public string? Target { get; init; }
    }

    /// <summary>
    ///     A <see cref="Result{T}" /> as the wire spells it: <c>{ok, value}</c> or <c>{ok, error}</c>.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    public sealed record Envelope<T> {
        /// <summary>Whether <see cref="Value" /> is present.</summary>
        [JsonPropertyName("ok")]
        public bool Ok { get; init; }

        /// <summary>The value, when <see cref="Ok" />.</summary>
        [JsonPropertyName("value")]
        public T? Value { get; init; }

        /// <summary>The failure, when not <see cref="Ok" />.</summary>
        [JsonPropertyName("error")]
        public WireError? Error { get; init; }
    }

    /// <summary>The value type of an envelope that carries no value.</summary>
    public sealed record NoValue;

    /// <summary>Spells a result as an envelope payload.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="result">The result.</param>
    public static string Seal<T>(Result<T> result)
        where T : notnull =>
        result.TryGetError(out var error)
            ? TunnelCodec.Serialize(new Envelope<T> { Ok = false, Error = Wire(error) })
            : TunnelCodec.Serialize(new Envelope<T> { Ok = true, Value = result.GetValueOrThrow() });

    /// <summary>Spells a valueless result as an envelope payload.</summary>
    /// <param name="result">The result.</param>
    public static string Seal(Result result) =>
        result.TryGetError(out var error)
            ? TunnelCodec.Serialize(new Envelope<NoValue> { Ok = false, Error = Wire(error) })
            : TunnelCodec.Serialize(new Envelope<NoValue> { Ok = true, Value = new() });

    /// <summary>Reads a result back out of an envelope payload.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="payload">The response frame's payload.</param>
    /// <returns>
    ///     The result the agent sealed, or a failure about the payload itself. ⚠ An error code the
    ///     platform does not know — an agent newer than this silo — is read as
    ///     <see cref="ErrorCode.InternalError" /> with the message kept, rather than dropped.
    /// </returns>
    public static Result<T> Open<T>(string payload)
        where T : notnull {
        var envelope = TunnelCodec.Deserialize<Envelope<T>>(payload);

        if (envelope.TryGetError(out var malformed)) {
            return Result<T>.Failure(malformed);
        }

        var value = envelope.GetValueOrThrow();

        if (!value.Ok) {
            return Result<T>.Failure(Unwire(value.Error));
        }

        return value.Value is null
            ? Result<T>.Failure(ErrorCode.InternalError, $"The agent answered ok with no {typeof(T).Name}.")
            : Result<T>.Success(value.Value);
    }

    /// <summary>Reads a valueless result back out of an envelope payload.</summary>
    /// <param name="payload">The response frame's payload.</param>
    public static Result Open(string payload) {
        var envelope = TunnelCodec.Deserialize<Envelope<NoValue>>(payload);

        if (envelope.TryGetError(out var malformed)) {
            return Result.Failure(malformed);
        }

        var value = envelope.GetValueOrThrow();
        return value.Ok ? Result.Success : Result.Failure(Unwire(value.Error));
    }

    static WireError Wire(Error error) =>
        new() { Code = error.Code.Value, Message = error.Message, Target = error.Target };

    static Error Unwire(WireError? error) {
        if (error is null) {
            return new(ErrorCode.InternalError, "The agent answered with a failure that carried no error.");
        }

        var code = ErrorCode.TryFromValue(error.Code, out var known) ? known : ErrorCode.InternalError;
        var message = error.Message.Length > 0 ? error.Message : $"The agent reported {error.Code} with no message.";

        try {
            return new(code, message, error.Target);
        } catch (ArgumentException) {
            // A target that is not a JSON pointer came from a peer that is not this build. The
            // message is the useful half; the pointer is dropped rather than the whole answer.
            return new(code, message);
        }
    }
}
