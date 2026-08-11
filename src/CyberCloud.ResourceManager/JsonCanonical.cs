using System.Text.Json.Nodes;

namespace CyberCloud.ResourceManager;

/// <summary>
///     Rewrites a JSON object with its members in a fixed order, so two documents that mean the same
///     thing serialize to the same string.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This is what makes <c>PUT</c> idempotent, and it is not a tidiness measure.</b>
///         docs/plan/06 § Two-phase create: <i>"the caller retries the <c>PUT</c> — which is idempotent
///         because <c>PUT</c> with the same body on an existing resource is a no-op, which is exactly
///         why the API is <c>PUT</c> and not <c>POST</c>."</i> The resource grain decides "no-op" by
///         comparing the new superset against the stored one, and <c>JsonNode</c> serializes in
///         <i>insertion</i> order — so without this, whether a retry was a no-op depended on the order
///         the client's serializer happened to emit properties in, and on the order the write path
///         happened to rebuild them in.
///     </para>
///     <para>
///         That is not hypothetical: <c>WritePathTests.ARepeatedIdenticalPutIsANoOp</c> caught it. The
///         replacement removes each declared leaf and writes it back, which moves a root-level property
///         to the end of the document on the second write — so a byte-identical retry produced a
///         different string and stopped being a no-op.
///     </para>
///     <para>
///         ⚠ <b>Arrays are left alone.</b> Sorting them would change what they mean — an ordered list
///         is data, not a bag — so two bodies whose arrays differ only in order are genuinely
///         different bodies and a retry of one is not a no-op for the other. That is the correct
///         answer, and it is the conservative direction: it can only cost a spurious update, never a
///         missed one.
///     </para>
/// </remarks>
static class JsonCanonical {
    /// <summary>Returns a copy with every object's members in ordinal order, recursively.</summary>
    /// <param name="value">The document. Not modified.</param>
    public static JsonObject Of(JsonObject value) {
        var sorted = new JsonObject();

        foreach (var member in value.OrderBy(x => x.Key, StringComparer.Ordinal)) {
            sorted[member.Key] = member.Value switch {
                JsonObject nested => Of(nested),
                null => null,
                var other => other.DeepClone()
            };
        }

        return sorted;
    }
}
