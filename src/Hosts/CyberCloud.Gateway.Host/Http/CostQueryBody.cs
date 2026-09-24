using CyberCloud.Billing.Contracts;
using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CyberCloud.Gateway.Host.Http;

/// <summary>
///     The cost query's body — <c>{ "from": "…", "to": "…", "groupBy": "resourceGroup" }</c>, with an
///     optional <c>"granularity": "daily"</c> (#41) — and its answer. docs/plan/22 § Cost visibility, issue #38.
/// </summary>
/// <remarks>
///     ⚠ <b>Parsed here and judged behind the seam.</b> This type turns JSON into a
///     <see cref="CostQueryRequest" /> and refuses what is not JSON or not the shape; the period's
///     bounds, the grouping's meaning and every visibility question are the cost grain's, so a rule
///     about them has one home. The grammar words are the body's own spelling, the way a
///     resource body spells its enums.
/// </remarks>
/// <param name="From">The start of the period.</param>
/// <param name="To">The end of the period, exclusive.</param>
/// <param name="Grouping">How the rows are keyed.</param>
/// <param name="Granularity">Whether each row is also one day's.</param>
sealed record CostQueryBody(DateTimeOffset From, DateTimeOffset To, CostGrouping Grouping, CostGranularity Granularity = CostGranularity.None) {
    /// <summary>The body spellings of <see cref="CostGrouping" />, in enum order.</summary>
    public static ImmutableArray<string> GroupingValues { get; } = ["resource", "resourceGroup", "resourceType", "meter", "day"];

    /// <summary>The body spellings of <see cref="CostGranularity" />, in enum order from <c>None</c>.</summary>
    public static ImmutableArray<string> GranularityValues { get; } = ["none", "daily"];

    /// <summary>Parses a body.</summary>
    /// <param name="body">The request body.</param>
    /// <returns>The query, or <see cref="ErrorCode.InvalidRequestBody" /> naming the member and the shape.</returns>
    public static Result<CostQueryBody> Parse(string body) {
        ArgumentNullException.ThrowIfNull(body);

        if (body.Length == 0) {
            return Missing("/");
        }

        JsonDocument document;

        try {
            document = JsonDocument.Parse(body);
        } catch (JsonException exception) {
            return Result<CostQueryBody>.Failure(ErrorCode.InvalidRequestBody, $"The request body is not valid JSON: {exception.Message}");
        }

        using (document) {
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object) {
                return Missing("/");
            }

            if (!Instant(root, "from", out var from)) {
                return Missing("/from");
            }

            if (!Instant(root, "to", out var to)) {
                return Missing("/to");
            }

            var grouping = root.TryGetProperty("groupBy", out var groupBy) && groupBy.ValueKind == JsonValueKind.String
                ? GroupingValues.IndexOf(groupBy.GetString() ?? string.Empty) + 1
                : 0;

            if (grouping == 0) {
                return Missing("/groupBy");
            }

            // Optional, and absent is none: a body written before #41 means what it meant.
            var granularity = 0;

            if (root.TryGetProperty("granularity", out var granular)) {
                granularity = granular.ValueKind == JsonValueKind.String ? GranularityValues.IndexOf(granular.GetString() ?? string.Empty) : -1;

                if (granularity < 0) {
                    return Missing("/granularity");
                }
            }

            return Result<CostQueryBody>.Success(new(from, to, (CostGrouping)grouping, (CostGranularity)granularity));
        }
    }

    /// <summary>The answer, as the collection-envelope-free object a cost query returns.</summary>
    /// <param name="answer">What the cost grain answered.</param>
    public static string Render(CostQueryResult answer) {
        ArgumentNullException.ThrowIfNull(answer);

        var buffer = new ArrayBufferWriter<byte>(512);

        using (var writer = new Utf8JsonWriter(buffer)) {
            writer.WriteStartObject();
            writer.WriteString("currency", answer.Currency);
            writer.WriteString("from", answer.From.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteString("to", answer.To.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteString("groupBy", GroupingValues[(int)answer.Grouping - 1]);
            writer.WriteString("granularity", GranularityValues[(int)answer.Granularity]);
            writer.WriteNumber("total", answer.Total);
            writer.WriteBoolean("filtered", answer.Filtered);
            writer.WriteBoolean("pricedAlone", answer.PricedAlone);
            writer.WritePropertyName("rows");
            writer.WriteStartArray();

            foreach (var row in answer.Rows) {
                writer.WriteStartObject();

                if (answer.Granularity == CostGranularity.Daily) {
                    writer.WriteString("day", row.Day);
                }

                writer.WriteString("name", row.Name);
                writer.WriteNumber("amount", row.Amount);

                if (answer.Grouping == CostGrouping.Meter) {
                    writer.WriteNumber("quantity", row.Quantity);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    static bool Instant(JsonElement root, string name, out DateTimeOffset instant) {
        instant = default;

        return root.TryGetProperty(name, out var member)
            && member.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(
                member.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out instant
            );
    }

    static Result<CostQueryBody> Missing(string target) =>
        Result<CostQueryBody>.Failure(
            ErrorCode.InvalidRequestBody,
            """A cost query is a JSON object with "from" and "to" as ISO 8601 instants — "to" exclusive — """
            + $"""and "groupBy" as one of {string.Join(", ", GroupingValues)}, with an optional "granularity" of """
            + $"""{string.Join(" or ", GranularityValues)}: """
            + """{ "from": "2026-08-01T00:00:00Z", "to": "2026-09-01T00:00:00Z", "groupBy": "resourceGroup" }. """
            + "docs/plan/22 § Cost visibility.",
            target
        );
}
