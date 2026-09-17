using System.Text.Json;
using System.Text.Json.Serialization;

namespace CyberCloud.ResourceGraph;

/// <summary>
///     The wire form of a <see cref="ResourceChangedEvent" /> on NATS: JSON, camel-cased, enums as
///     their names.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>JSON and not the Orleans serializer, because the stream has consumers that are not
///         Orleans.</b> docs/plan/04 § Streams lists the portal's SignalR fan-out, the audit sink and
///         billing beside the projection, and docs/plan/03 § Hosts makes the ingest host "not an
///         Orleans client at all". An Orleans-serialized payload is readable by exactly one runtime;
///         a JSON one by anything that can subscribe. The cost is a second serializer for one type,
///         and the type was designed as a flat row so the second one has nothing hard to do.
///     </para>
///     <para>
///         Enums travel as names so that a consumer reading the subject <c>Created</c> does not have
///         to know <c>ResourceChangeKind</c>'s numbering, and so a renumbering — which the Orleans
///         side would survive by alias and this side would not — is a visible break rather than a
///         silent one. <c>StreamNamespace</c> and <c>Subject</c> are computed from the fields and
///         are not written; a reader recomputes them.
///     </para>
/// </remarks>
public static class ResourceChangedJson {
    /// <summary>The one options instance both directions use.</summary>
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web) {
        Converters = { new JsonStringEnumConverter() },
        // Init-only members have setters and are written; the two computed properties do not and
        // are the ones this flag leaves out.
        IgnoreReadOnlyProperties = true,
        WriteIndented = false
    };

    /// <summary>Encodes one event as UTF-8 JSON.</summary>
    /// <param name="change">The event.</param>
    public static byte[] Encode(ResourceChangedEvent change) {
        ArgumentNullException.ThrowIfNull(change);
        return JsonSerializer.SerializeToUtf8Bytes(change, Options);
    }

    /// <summary>Decodes one event, or says why the bytes are not one.</summary>
    /// <param name="payload">The message body.</param>
    public static Result<ResourceChangedEvent> Decode(ReadOnlyMemory<byte> payload) {
        try {
            var decoded = JsonSerializer.Deserialize<ResourceChangedEvent>(payload.Span, Options);

            return decoded is null
                ? Result<ResourceChangedEvent>.Failure(ErrorCode.InvalidRequestBody, "The payload is the JSON literal null, which is not a resource-changed event.")
                : Result<ResourceChangedEvent>.Success(decoded);
        }
        catch (JsonException exception) {
            return Result<ResourceChangedEvent>.Failure(
                ErrorCode.InvalidRequestBody,
                $"The payload is not a resource-changed event: {exception.Message}"
            );
        }
    }
}
