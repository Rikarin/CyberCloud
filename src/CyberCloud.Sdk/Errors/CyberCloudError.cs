using System.Text.Json;

namespace CyberCloud.Sdk;

/// <summary>
///     The one error shape, parsed off the wire — docs/plan/08 § Errors:
///     <code>
///     { "error": { "code": "QuotaExceeded",
///                  "message": "Subscription quota for 'vcpu' in region 'eu-central' would be exceeded (requested 8, available 2).",
///                  "target": "properties.sku",
///                  "details": [ … ] } }
///     </code>
/// </summary>
/// <remarks>
///     ⚠ <b>No exception detail is ever parsed, because none is ever sent</b> — docs/plan/08 § Errors,
///     read from the client side. This type has no member that could carry a stack trace, and adding
///     one would give the service somewhere to put it.
/// </remarks>
public sealed class CyberCloudError {
    /// <summary>Creates an error.</summary>
    /// <param name="code">The stable, greppable identifier.</param>
    /// <param name="message">The human-readable message, naming the actual numbers.</param>
    /// <param name="target">An RFC 6901 JSON Pointer into the request body, or <see langword="null" />.</param>
    /// <param name="details">Every other problem found. Never <see langword="null" />; possibly empty.</param>
    public CyberCloudError(string code, string message, string? target = null, IReadOnlyList<CyberCloudError>? details = null) {
        Code = code;
        Message = message;
        Target = target;
        Details = details ?? [];
    }

    /// <summary>
    ///     The stable, documented, greppable identifier — <c>QuotaExceeded</c>. docs/plan/08 § Errors
    ///     makes it part of the API contract, so branching on it is supported and changing one is a
    ///     breaking change on the service's side.
    /// </summary>
    public string Code { get; }

    /// <summary>The message, for a human, naming the actual numbers.</summary>
    public string Message { get; }

    /// <summary>
    ///     An RFC 6901 JSON Pointer into the <b>request body</b> — <c>/properties/sku</c> — or
    ///     <see langword="null" /> when the error is not about one field.
    /// </summary>
    /// <remarks>
    ///     ⚠ It points into what the caller <i>sent</i>, which is what lets a form highlight the
    ///     offending input rather than merely reporting that something was wrong with it. docs/plan/08
    ///     § Errors: <i>"so the portal can highlight the field"</i>.
    /// </remarks>
    public string? Target { get; }

    /// <summary>
    ///     Every other problem found in the same request. docs/plan/08 § Errors: <i>"A form that has to
    ///     be fixed one field per round trip is a form nobody finishes."</i>
    /// </summary>
    public IReadOnlyList<CyberCloudError> Details { get; }

    /// <summary>
    ///     Parses the body of an error response. Returns <see langword="null" /> when the body is
    ///     absent, is not JSON, or does not carry the shape.
    /// </summary>
    /// <remarks>
    ///     ⚠ Returns rather than throws. A gateway that fell over before it reached the error shaper
    ///     (docs/plan/10 § Request pipeline, stage 9) sends an HTML error page, and a parser that threw
    ///     on it would replace the status code the caller needs with a <see cref="JsonException" />
    ///     nobody can act on.
    /// </remarks>
    /// <param name="content">The response body.</param>
    public static CyberCloudError? TryParse(ReadOnlyMemory<byte>? content) {
        if (content is not { } body || body.IsEmpty)
            return null;

        try {
            using var document = JsonDocument.Parse(body);

            // The envelope is `{ "error": { … } }` — openapi/2026-08-01.json § ErrorResponse. The bare
            // object is accepted too: it is the shape inside an OperationStatus's `error` member, so
            // reading both here means CyberCloudOperation needs no second parser.
            var element = document.RootElement;

            if (element.ValueKind is JsonValueKind.Object
                && element.TryGetProperty("error", out var wrapped)
                && wrapped.ValueKind is JsonValueKind.Object)
                element = wrapped;

            return Read(element);
        } catch (JsonException) {
            return null;
        }
    }

    static CyberCloudError? Read(JsonElement element) {
        if (element.ValueKind is not JsonValueKind.Object)
            return null;

        // `code` and `message` are the two required members — openapi/2026-08-01.json § Error. A body
        // missing either is not the one error shape, and reporting "an error with no code" is worse
        // than reporting the raw status.
        if (!TryReadString(element, "code", out var code) || !TryReadString(element, "message", out var message))
            return null;

        // Absent and empty are both "no field to point at". `null` is the one spelling callers branch
        // on, so an empty string never survives to CyberCloudRequestFailedException.Target.
        var target = TryReadString(element, "target", out var pointer) && pointer.Length > 0 ? pointer : null;

        List<CyberCloudError>? details = null;

        if (element.TryGetProperty("details", out var array) && array.ValueKind is JsonValueKind.Array) {
            foreach (var item in array.EnumerateArray()) {
                if (Read(item) is not { } detail)
                    continue;

                details ??= [];
                details.Add(detail);
            }
        }

        return new CyberCloudError(code, message, target, details);
    }

    static bool TryReadString(JsonElement element, string name, out string value) {
        if (element.TryGetProperty(name, out var property) && property.ValueKind is JsonValueKind.String) {
            value = property.GetString()!;

            return true;
        }

        value = string.Empty;

        return false;
    }

    /// <inheritdoc />
    public override string ToString() => Target is null ? $"{Code}: {Message}" : $"{Code} at '{Target}': {Message}";
}
