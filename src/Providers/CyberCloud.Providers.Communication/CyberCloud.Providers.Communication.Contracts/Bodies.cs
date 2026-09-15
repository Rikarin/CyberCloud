using System.Globalization;
using System.Text.Json;

namespace CyberCloud.Providers.Communication.Contracts;

/// <summary>
///     Reads a validated desired body. Every reader tolerates absence and returns the fallback,
///     because the schema — not this class — is what decides what a body must carry.
/// </summary>
static class Bodies {
    /// <summary><c>/properties/{name}</c>, or <see langword="null" /> when absent.</summary>
    public static JsonElement? Property(JsonElement desired, string name) =>
        desired.ValueKind is JsonValueKind.Object
        && desired.TryGetProperty("properties", out var properties)
        && properties.ValueKind is JsonValueKind.Object
        && properties.TryGetProperty(name, out var value)
            ? value
            : null;

    /// <summary><c>/properties/{parent}/{name}</c>, or <see langword="null" /> when absent.</summary>
    public static JsonElement? Member(JsonElement desired, string parent, string name) =>
        Property(desired, parent) is { ValueKind: JsonValueKind.Object } section
        && section.TryGetProperty(name, out var value)
            ? value
            : null;

    public static string Text(JsonElement? element, string fallback) =>
        element is { ValueKind: JsonValueKind.String } value ? value.GetString() ?? fallback : fallback;

    public static bool Flag(JsonElement? element, bool fallback) =>
        element switch {
            { ValueKind: JsonValueKind.True } => true,
            { ValueKind: JsonValueKind.False } => false,
            _ => fallback
        };

    public static long Whole(JsonElement? element, long fallback) =>
        element is { ValueKind: JsonValueKind.Number } value && value.TryGetInt64(out var found) ? found : fallback;

    public static decimal Amount(JsonElement? element, decimal fallback) =>
        element is { ValueKind: JsonValueKind.Number } value && value.TryGetDecimal(out var found) ? found : fallback;

    /// <summary>The strings in an array property, skipping anything that is not a string.</summary>
    public static IEnumerable<string> Strings(JsonElement? element) {
        if (element is not { ValueKind: JsonValueKind.Array } array) {
            yield break;
        }

        foreach (var item in array.EnumerateArray()) {
            if (item.ValueKind is JsonValueKind.String && item.GetString() is { } text) {
                yield return text;
            }
        }
    }

    /// <summary>A root-level member of an action body — actions have no <c>properties</c> wrapper.</summary>
    public static JsonElement? Root(JsonElement body, string name) =>
        body.ValueKind is JsonValueKind.Object && body.TryGetProperty(name, out var value) ? value : null;

    public static string Stamp(DateTimeOffset at) => at.ToString("O", CultureInfo.InvariantCulture);

    public static string Number(decimal value) => value.ToString(CultureInfo.InvariantCulture);
}
