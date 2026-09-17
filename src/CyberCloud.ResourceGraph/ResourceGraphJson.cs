using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CyberCloud.ResourceGraph;

/// <summary>
///     The JSON that goes into and comes out of ClickHouse's <c>JSONEachRow</c> format for a
///     <see cref="ResourceGraphRow" />: snake-case names, and timestamps in the form ClickHouse
///     writes them.
/// </summary>
/// <remarks>
///     ⚠ <b>ClickHouse writes a <c>DateTime64</c> as <c>2026-09-17 10:00:00.123</c>, with a space
///     and no offset, and <see cref="DateTimeOffset" />'s default converter refuses that.</b> The
///     converter below reads both that form and ISO 8601, and writes ISO 8601 in UTC — which
///     <c>date_time_input_format=best_effort</c> accepts on the way in. A column declared
///     <c>DateTime64(3, 'UTC')</c> is what makes the space-form unambiguous on the way out.
/// </remarks>
public static class ResourceGraphJson {
    /// <summary>The options both directions use.</summary>
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.General) {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters = { new ClickHouseDateTimeOffsetConverter() },
        WriteIndented = false
    };

    /// <summary>One row as a <c>JSONEachRow</c> line.</summary>
    /// <param name="row">The row.</param>
    public static string EncodeRow(ResourceGraphRow row) {
        ArgumentNullException.ThrowIfNull(row);
        return JsonSerializer.Serialize(row, Options);
    }

    /// <summary>The first line of a <c>JSONEachRow</c> result as a row, or <c>null</c> for an empty result.</summary>
    /// <param name="body">The response body.</param>
    public static ResourceGraphRow? DecodeFirstRow(string body) {
        ArgumentNullException.ThrowIfNull(body);

        var line = FirstLine(body);
        return line.Length == 0 ? null : JsonSerializer.Deserialize<ResourceGraphRow>(line, Options);
    }

    /// <summary>One numeric column out of the first line of a <c>JSONEachRow</c> result.</summary>
    /// <param name="body">The response body.</param>
    /// <param name="column">The column's alias in the <c>SELECT</c>.</param>
    /// <returns>The number, or <c>null</c> when the result is empty or the column is <c>null</c>.</returns>
    public static long? DecodeNumber(string body, string column) {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(column);

        var line = FirstLine(body);

        if (line.Length == 0) {
            return null;
        }

        using var document = JsonDocument.Parse(line);

        if (!document.RootElement.TryGetProperty(column, out var value)) {
            return null;
        }

        return value.ValueKind switch {
            JsonValueKind.Number => value.GetInt64(),
            // ⚠ A `max()` over no rows is 0 for UInt64 in ClickHouse rather than null, and a
            // 64-bit integer comes back quoted unless the request turned quoting off; both are
            // handled so a reader does not depend on which the server did.
            JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }

    static string FirstLine(string body) {
        var newline = body.IndexOf('\n', StringComparison.Ordinal);
        return (newline < 0 ? body : body[..newline]).Trim();
    }

    sealed class ClickHouseDateTimeOffsetConverter : JsonConverter<DateTimeOffset> {
        // ⚠ Seven F's, not nine. .NET's custom format parser stops at seven fractional places and
        // treats a longer run as an invalid format — TryParseExact then fails silently, the code
        // falls through to Parse, and Parse assumes LOCAL time for a string with no offset. The
        // first version had nine, for DateTime64(9), and read every UTC row two hours off on a
        // laptop in Prague. The table is DateTime64(3), so three places arrive; seven leaves room.
        const string ClickHouseForm = "yyyy-MM-dd HH:mm:ss.FFFFFFF";

        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
            var text = reader.GetString() ?? string.Empty;

            // ⚠ AssumeUniversal alone hands back the machine's LOCAL offset — the value is right
            // and the offset is +02:00 on a laptop in Prague. AdjustToUniversal is what keeps it at
            // +00:00, which is what a column declared DateTime64(3, 'UTC') means.
            const DateTimeStyles utc = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;

            if (DateTimeOffset.TryParseExact(text, ClickHouseForm, CultureInfo.InvariantCulture, utc, out var clickHouse)) {
                return clickHouse;
            }

            if (DateTimeOffset.TryParseExact(text, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, utc, out var seconds)) {
                return seconds;
            }

            // ISO 8601 with an offset or a Z — what this converter itself writes.
            return DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, utc);
        }

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
    }
}
