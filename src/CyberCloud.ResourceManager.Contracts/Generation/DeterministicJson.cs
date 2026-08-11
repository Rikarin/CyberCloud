using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.ResourceManager.Contracts.Generation;

/// <summary>
///     The one way a generated surface is turned into bytes.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every choice here exists because a generated file is <i>diffed</i>, not read.</b>
///         docs/plan/21 § OpenAPI: <i>"it is a build artifact that is diffed"</i>, and
///         docs/plan/23 § The architecture gates asks that it <i>"regenerate byte-identically"</i>.
///         Byte-identical is a property of the writer as much as of the emitter, and every one of the
///         four settings below is a way CI goes red on somebody else's machine and green on yours:
///     </para>
///     <list type="bullet">
///         <item>
///             <b>No BOM.</b> <see cref="Encoding.UTF8" />'s default constructor emits one;
///             <see cref="File.WriteAllText(string,string)" /> does not, and a file written by one and
///             compared against a file written by the other differs in three bytes nobody can see.
///             Bytes are produced here and written with <c>WriteAllBytes</c> so no encoder is
///             consulted twice.
///         </item>
///         <item>
///             <b><c>\n</c>, always.</b> <see cref="JsonWriterOptions.NewLine" /> defaults to
///             <see cref="Environment.NewLine" />, so the same document is two different files on
///             Windows and on Linux. The repository's <c>.editorconfig</c> already says
///             <c>end_of_line = lf</c>; this makes that true for a file no editor ever touches.
///         </item>
///         <item>
///             <b>Two-space indent, spaces not tabs.</b> Stated rather than defaulted, because the
///             default is a framework decision that may change under a runtime upgrade, and a runtime
///             upgrade that reformats every checked-in document reads as a breaking API change in the
///             diff.
///         </item>
///         <item>
///             <b>A trailing newline.</b> Not JSON's business and entirely git's: a file without one
///             makes the next change show as two modified lines instead of one, and <c>git diff</c>
///             prints <c>\ No newline at end of file</c> into every review of it.
///         </item>
///     </list>
///     <para>
///         ⚠ <b>Nothing here sorts.</b> <see cref="JsonObject" /> preserves insertion order and that
///         is deliberate — ordering is the emitter's decision, made once per member so that the
///         reason for each order is next to the thing being ordered. A sort here would hide an
///         emitter that had stopped being deterministic behind a writer that always was.
///     </para>
/// </remarks>
public static class DeterministicJson {
    /// <summary>The indent width, in spaces.</summary>
    public const int IndentSize = 2;

    /// <summary>Renders a document to the exact bytes that go on disk.</summary>
    /// <param name="document">The document.</param>
    /// <returns>UTF-8 with no BOM, LF line endings, and a trailing newline.</returns>
    public static byte[] ToBytes(JsonNode document) {
        ArgumentNullException.ThrowIfNull(document);

        using var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(
                   buffer,
                   new JsonWriterOptions {
                       Indented = true,
                       IndentCharacter = ' ',
                       IndentSize = IndentSize,
                       NewLine = "\n",
                       // The document is machine-generated from identifiers the registry validated;
                       // there is no user-controlled string in it that a relaxed encoder would let
                       // through as markup. Relaxed is chosen so a description containing '<' or '+'
                       // is readable in the diff rather than <.
                       Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                   }
               )) {
            document.WriteTo(writer);
        }

        buffer.WriteByte((byte)'\n');
        return buffer.ToArray();
    }

    /// <summary>Renders a document to a string, for a message or an assertion.</summary>
    /// <param name="document">The document.</param>
    public static string ToText(JsonNode document) => Encoding.UTF8.GetString(ToBytes(document));

    /// <summary>
    ///     Parses a document that was written by <see cref="ToBytes" />.
    /// </summary>
    /// <param name="bytes">The file's bytes.</param>
    /// <returns>The parsed document, or <see langword="null" /> when the bytes are not JSON.</returns>
    /// <remarks>
    ///     ⚠ Returns <see langword="null" /> rather than throwing because the caller is the
    ///     compatibility diff, and "the previously published document does not parse" is a finding it
    ///     has to report with the file's name attached — not an exception from three frames down.
    /// </remarks>
    public static JsonNode? TryParse(ReadOnlySpan<byte> bytes) {
        try {
            var reader = new Utf8JsonReader(bytes);
            return JsonNode.Parse(ref reader);
        } catch (JsonException) {
            return null;
        }
    }
}
