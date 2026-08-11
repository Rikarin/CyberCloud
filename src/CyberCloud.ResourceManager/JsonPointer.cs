using System.Text.Json.Nodes;

namespace CyberCloud.ResourceManager;

/// <summary>
///     Reads, writes and removes a value at an RFC 6901 JSON Pointer.
/// </summary>
/// <remarks>
///     <para>
///         The same pointer string does three jobs in this subsystem, and that is why one helper owns
///         all of them: it addresses a schema property
///         (<c>SchemaProperty.Pointer</c>), it is the <c>target</c> of an error
///         (docs/plan/08 § Errors), and it is where the api-version projection reads and writes.
///         Three implementations would be three chances for the projection to disagree with the
///         validation about what <c>/properties/sku/name</c> means.
///     </para>
///     <para>
///         ⚠ <b>Objects only — array indices are not addressed.</b> RFC 6901 allows a numeric token to
///         index an array, and this deliberately does not: <see cref="SchemaKind.Array" /> does not
///         model element shape, so a pointer into an array would address something the schema cannot
///         describe. A numeric token here reads as an object member name, which is what a JSON object
///         with a <c>"0"</c> key actually is.
///     </para>
/// </remarks>
static class JsonPointer {
    /// <summary>Reads the node at a pointer, or <see langword="null" /> when nothing is there.</summary>
    /// <param name="root">The document.</param>
    /// <param name="pointer">An RFC 6901 pointer beginning with <c>/</c>.</param>
    public static JsonNode? Read(JsonObject root, string pointer) {
        JsonNode? current = root;

        foreach (var token in Tokens(pointer)) {
            if (current is not JsonObject obj || !obj.TryGetPropertyValue(token, out var next)) {
                return null;
            }

            current = next;
        }

        return current;
    }

    /// <summary>Writes a value at a pointer, creating the objects on the way.</summary>
    /// <param name="root">The document to write into.</param>
    /// <param name="pointer">An RFC 6901 pointer beginning with <c>/</c>.</param>
    /// <param name="value">The value. Already cloned by the caller — this does not clone.</param>
    /// <remarks>
    ///     ⚠ A non-object standing where a container is needed is <b>replaced</b>. That happens when
    ///     one api-version declares <c>/properties/sku</c> as a string and a later one declares
    ///     <c>/properties/sku/name</c>, which is a schema change the immutable-date rule permits and
    ///     the superset has to survive. Replacing keeps the newer, deeper shape; the older version's
    ///     read then finds no string at <c>/properties/sku</c> and projects nothing, which is the
    ///     honest answer.
    /// </remarks>
    public static void Write(JsonObject root, string pointer, JsonNode value) {
        var tokens = Tokens(pointer).ToArray();
        if (tokens.Length == 0) {
            return;
        }

        var current = root;

        for (var i = 0; i < tokens.Length - 1; i++) {
            if (current[tokens[i]] is JsonObject existing) {
                current = existing;
                continue;
            }

            var created = new JsonObject();
            current[tokens[i]] = created;
            current = created;
        }

        current[tokens[^1]] = value;
    }

    /// <summary>Removes whatever is at a pointer. A pointer that addresses nothing is a no-op.</summary>
    /// <param name="root">The document.</param>
    /// <param name="pointer">An RFC 6901 pointer beginning with <c>/</c>.</param>
    /// <remarks>
    ///     ⚠ A container left empty by the removal is <b>not</b> pruned. Pruning would make an
    ///     old-version <c>PUT</c> that cleared its own fields also delete the object a newer version's
    ///     fields live in, which is the data loss <c>ReplaceSlice</c>'s scoping exists to avoid.
    /// </remarks>
    public static void Remove(JsonObject root, string pointer) {
        var tokens = Tokens(pointer).ToArray();
        if (tokens.Length == 0) {
            return;
        }

        var current = root;

        for (var i = 0; i < tokens.Length - 1; i++) {
            if (current[tokens[i]] is not JsonObject next) {
                return;
            }

            current = next;
        }

        current.Remove(tokens[^1]);
    }

    /// <summary>The reference tokens, with <c>~1</c> and <c>~0</c> unescaped in that order.</summary>
    /// <remarks>
    ///     ⚠ <b>The order of the two replacements matters and is fixed by RFC 6901 § 4.</b> Unescaping
    ///     <c>~0</c> first would turn <c>~01</c> into <c>~1</c> and then into <c>/</c>, when it means
    ///     the two characters <c>~1</c>. Doing <c>~1</c> first is the only order that round-trips.
    /// </remarks>
    static IEnumerable<string> Tokens(string pointer) {
        foreach (var raw in pointer.Split('/', StringSplitOptions.RemoveEmptyEntries)) {
            yield return raw.Contains('~', StringComparison.Ordinal)
                ? raw.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)
                : raw;
        }
    }
}
