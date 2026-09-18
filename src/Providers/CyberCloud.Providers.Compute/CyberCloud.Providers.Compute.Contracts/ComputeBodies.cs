using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Compute.Contracts;

/// <summary>
///     Reads one pointer out of a validated body, and one field out of an object read back from a
///     cluster. Shared by the three types so a body is read the same way everywhere.
/// </summary>
/// <remarks>
///     ⚠ Every reader takes a fallback and never throws. A body reaching a reconciler has passed
///     <see cref="ResourceSchema.Validate" />, so a missing property is a property the schema
///     defaults, and the fallback is that default spelled once more in C#. The two spellings are what
///     the declaration tests compare.
/// </remarks>
static class ComputeBodies {
    public static JsonElement? Property(JsonElement desired, string name) =>
        desired.ValueKind is JsonValueKind.Object
        && desired.TryGetProperty("properties", out var properties)
        && properties.ValueKind is JsonValueKind.Object
        && properties.TryGetProperty(name, out var value)
            ? value
            : null;

    public static JsonElement? Member(JsonElement desired, string parent, string name) =>
        Property(desired, parent) is { ValueKind: JsonValueKind.Object } section
        && section.TryGetProperty(name, out var value)
            ? value
            : null;

    public static string Text(JsonElement? element, string fallback) =>
        element is { ValueKind: JsonValueKind.String } value ? value.GetString() ?? fallback : fallback;

    public static IEnumerable<string> Strings(JsonElement? element) {
        if (element is not { ValueKind: JsonValueKind.Array } array) {
            yield break;
        }

        foreach (var item in array.EnumerateArray()) {
            if (item.ValueKind is JsonValueKind.String && item.GetString() is { Length: > 0 } text) {
                yield return text;
            }
        }
    }

    /// <summary>The object's JSON as a tree, or <see langword="null" /> when it is not JSON.</summary>
    public static JsonObject? Document(string objectJson) {
        try {
            return JsonNode.Parse(objectJson) as JsonObject;
        } catch (JsonException) {
            return null;
        }
    }

    public static string Kind(string objectJson) => Document(objectJson)?["kind"]?.GetValue<string>() ?? string.Empty;

    public static JsonObject? Spec(string objectJson) => Document(objectJson)?["spec"] as JsonObject;

    public static JsonObject? Status(string objectJson) => Document(objectJson)?["status"] as JsonObject;

    public static string TextOf(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : string.Empty;

    public static int WholeOf(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<int>(out var number) ? number : -1;

    /// <summary>
    ///     One entry of a status's <c>conditions</c> array by type, or <see langword="null" />.
    /// </summary>
    public static JsonObject? Condition(JsonObject? status, string type) =>
        status?["conditions"] is JsonArray conditions
            ? conditions.OfType<JsonObject>().FirstOrDefault(x => TextOf(x["type"]) == type)
            : null;
}
