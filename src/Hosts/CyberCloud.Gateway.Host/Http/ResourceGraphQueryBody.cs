using System.Globalization;
using System.Text.Json;

namespace CyberCloud.Gateway.Host.Http;

/// <summary>
///     The body of a resource graph query — <c>{ "query": "resources | …", "$top": n,
///     "$skipToken": "…" }</c> — read together with the page parameters a <c>nextLink</c> put in
///     the query string. docs/plan/08 § The resource-graph projection.
/// </summary>
/// <param name="Query">The KQL text. Required and non-empty.</param>
/// <param name="Top">The page size the caller asked for, or 0 for none. Clamped by the service.</param>
/// <param name="Continuation">The <c>$skipToken</c>, or empty for the first page.</param>
/// <remarks>
///     <para>
///         ⚠ <b>The body wins over the query string for both page parameters, and the query string
///         is read at all only because a <c>nextLink</c> is a URL.</b> <c>DispatchStage</c>'s
///         remarks carry the argument. <c>$top</c> is parsed leniently in both places, as every
///         collection of this API parses it: a value that is not a number is ignored, because the
///         page size is a hint the platform clamps anyway.
///     </para>
///     <para>
///         The three names are spelled as Azure spells the page parameters — with the sigil — so
///         a body and a query string carry the same words; <c>query</c> is Azure Resource Graph's
///         own name for the text.
///     </para>
/// </remarks>
sealed record ResourceGraphQueryBody(string Query, int Top, string Continuation) {
    /// <summary>The body member that holds the KQL.</summary>
    public const string QueryMember = "query";

    /// <summary>The page-size member and query parameter, <c>$top</c>.</summary>
    public const string TopMember = "$top";

    /// <summary>The continuation member and query parameter, <c>$skipToken</c>.</summary>
    public const string SkipTokenMember = "$skipToken";

    /// <summary>Reads the body and the query string.</summary>
    /// <param name="body">The request body as text. Empty is refused: a query needs its text.</param>
    /// <param name="query">The request's query string, for a <c>nextLink</c>'s page parameters.</param>
    /// <returns>The parsed body, or an <see cref="ErrorCode.InvalidRequestBody" /> failure naming what is missing or malformed.</returns>
    public static Result<ResourceGraphQueryBody> Parse(string body, IQueryCollection query) {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(query);

        var top = int.TryParse(query[TopMember], NumberStyles.Integer, CultureInfo.InvariantCulture, out var fromUrl) ? fromUrl : 0;
        var continuation = query[SkipTokenMember].ToString();

        if (body.Length == 0) {
            return Missing();
        }

        JsonDocument document;

        try {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException exception) {
            return Result<ResourceGraphQueryBody>.Failure(ErrorCode.InvalidRequestBody, $"The request body is not valid JSON: {exception.Message}");
        }

        using (document) {
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object) {
                return Missing();
            }

            if (!root.TryGetProperty(QueryMember, out var text) || text.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(text.GetString())) {
                return Missing();
            }

            if (root.TryGetProperty(TopMember, out var topMember)) {
                if (topMember.ValueKind == JsonValueKind.Number && topMember.TryGetInt32(out var asNumber)) {
                    top = asNumber;
                }
                else if (topMember.ValueKind == JsonValueKind.String
                         && int.TryParse(topMember.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var asText)) {
                    top = asText;
                }
            }

            if (root.TryGetProperty(SkipTokenMember, out var tokenMember) && tokenMember.ValueKind == JsonValueKind.String) {
                continuation = tokenMember.GetString() ?? "";
            }

            return Result<ResourceGraphQueryBody>.Success(new(text.GetString()!, top, continuation));
        }
    }

    static Result<ResourceGraphQueryBody> Missing() =>
        Result<ResourceGraphQueryBody>.Failure(
            ErrorCode.InvalidRequestBody,
            "A resource graph query is a JSON object with a non-empty \"query\" member holding the KQL — "
            + "{ \"query\": \"resources | where type =~ '…' | project name\" } — and optionally \"$top\" "
            + "and \"$skipToken\". docs/plan/08 § The resource-graph projection."
        );
}
