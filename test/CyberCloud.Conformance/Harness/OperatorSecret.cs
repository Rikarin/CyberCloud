using System.Text;
using System.Text.Json.Nodes;

namespace CyberCloud.Conformance.Harness;

/// <summary>
///     Builds the JSON of a <c>core/v1</c> <c>Secret</c> an operator would have written, for a
///     provider's <see cref="ProviderConformanceCase.OperatorWritten" />.
/// </summary>
/// <remarks>
///     <para>
///         <b>Shared because five providers need the same three lines and the base64 is the part that
///         is easy to get wrong in a way that still looks right.</b> A fixture that put the plaintext
///         straight into <c>data</c> would satisfy a handler that forgot to decode and fail one that
///         did — the exact inversion of what the test is for.
///     </para>
///     <para>
///         ⚠ <b><c>data</c> and never <c>stringData</c>, which is what a live cluster returns.</b>
///         <c>stringData</c> is write-only: the API server folds it into <c>data</c> and it is absent
///         from every read. A fixture using it would be testing a shape no handler will ever meet.
///     </para>
/// </remarks>
public static class OperatorSecret {
    /// <summary>One <c>Secret</c>, as the API server would return it.</summary>
    /// <param name="target">Where it lives. Its <c>Kind</c> is not read — the JSON says <c>Secret</c>.</param>
    /// <param name="values">The keys and their plaintext values.</param>
    public static string Json(ObjectRef target, IReadOnlyList<(string Key, string Value)> values) {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(values);

        var data = new JsonObject();

        foreach (var (key, value) in values) {
            data[key] = Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        }

        return new JsonObject {
            ["apiVersion"] = "v1",
            ["kind"] = "Secret",
            ["metadata"] = new JsonObject {
                ["name"] = target.Name,
                ["namespace"] = target.Namespace
            },
            ["type"] = "Opaque",
            ["data"] = data
        }.ToJsonString();
    }
}
