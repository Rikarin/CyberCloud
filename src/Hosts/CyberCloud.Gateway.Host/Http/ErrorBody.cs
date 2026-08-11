using System.Buffers;
using System.Text.Json;

namespace CyberCloud.Gateway.Host.Http;

/// <summary>
///     Renders an <see cref="Error" /> as docs/plan/08 § Errors' one body.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Hand-written with <see cref="Utf8JsonWriter" />, and that is the safety property.</b>
///         docs/plan/08 § Errors: <i>"No exception details, ever. A stack trace in an error body is
///         an information leak and a support-cost multiplier."</i> A serializer pointed at an object
///         graph writes whatever the graph grows next; this writer emits four names and cannot emit a
///         fifth. <see cref="Error" /> itself has no member that could hold a stack trace, so the two
///         defences are independent — and this one survives someone adding a member.
///     </para>
///     <para>
///         The correlation id is not in the body either. It goes in
///         <see cref="GatewayHeaders.RequestId" />, which is where docs/plan/08 § Errors puts it and
///         where a caller can read it off a response they could not parse.
///     </para>
/// </remarks>
static class ErrorBody {
    /// <summary>The media type, with the charset every Azure SDK expects.</summary>
    public const string ContentType = "application/json; charset=utf-8";

    /// <summary>Renders one error to UTF-8 JSON.</summary>
    /// <param name="error">The error. Its <see cref="Error.Details" /> nest to any depth.</param>
    /// <returns>The bytes of <c>{"error":{…}}</c>.</returns>
    public static byte[] Render(Error error) {
        ArgumentNullException.ThrowIfNull(error);

        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer)) {
            writer.WriteStartObject();
            writer.WritePropertyName("error");
            WriteError(writer, error);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    static void WriteError(Utf8JsonWriter writer, Error error) {
        writer.WriteStartObject();
        writer.WriteString("code", error.Code.Value);
        writer.WriteString("message", error.Message);

        if (error.Target is not null) {
            writer.WriteString("target", error.Target);
        }

        if (error.Details.Length > 0) {
            writer.WritePropertyName("details");
            writer.WriteStartArray();

            foreach (var detail in error.Details) {
                WriteError(writer, detail);
            }

            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }
}
