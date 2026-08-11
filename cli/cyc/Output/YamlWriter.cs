namespace CyberCloud.Cli.Output;

/// <summary>
///     Renders a value as YAML 1.2.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Written here rather than taken from a package, and the reason is the publish.</b>
///         docs/plan/21 § `cyc` requires a single-file AOT artifact per RID, and every general-purpose
///         YAML library on NuGet serializes through reflection — which is an IL2026 this project's
///         <c>IsAotCompatible</c> turns into a build failure. What is needed here is one direction
///         only: emit a document that came from JSON. That is a hundred lines, and the alternative was
///         a trim-hostile dependency in a CLI meant to be one file.
///     </para>
///     <para>
///         ⚠ <b>Quoting is conservative on purpose.</b> A YAML scalar that <i>looks</i> like
///         something else changes meaning silently — the Norway problem (<c>no</c> parsing as
///         <c>false</c>), a version number parsing as a float, a resource name of digits parsing as an
///         integer. Anything that is not unambiguously a plain scalar is quoted.
///     </para>
/// </remarks>
static class YamlWriter {
    /// <summary>Writes a value as a YAML document.</summary>
    /// <param name="writer">The stream to write to.</param>
    /// <param name="value">The value.</param>
    public static void Write(TextWriter writer, Payload value) {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        if (value.IsMissing) {
            writer.WriteLine("null");

            return;
        }

        WriteNode(writer, value, indent: 0, inline: false);
    }

    static void WriteNode(TextWriter writer, Payload value, int indent, bool inline) {
        var pad = new string(' ', indent);

        if (value.IsObject) {
            var members = value.Members.ToList();

            if (members.Count == 0) {
                writer.WriteLine(inline ? " {}" : pad + "{}");

                return;
            }

            if (inline)
                writer.WriteLine();

            foreach (var member in members) {
                writer.Write(pad);
                writer.Write(Key(member.Key));
                writer.Write(':');
                WriteChild(writer, member.Value, indent);
            }

            return;
        }

        if (value.IsArray) {
            var items = value.Items.ToList();

            if (items.Count == 0) {
                writer.WriteLine(inline ? " []" : pad + "[]");

                return;
            }

            if (inline)
                writer.WriteLine();

            foreach (var item in items) {
                writer.Write(pad);
                writer.Write('-');

                if (item.IsObject || item.IsArray) {
                    // A nested collection under a `-` starts on the next line, indented two: the
                    // compact `- key: value` form is legal and reads worse the moment the object has
                    // a second member.
                    writer.WriteLine();
                    WriteNode(writer, item, indent + 2, inline: false);
                } else {
                    writer.Write(' ');
                    writer.WriteLine(Scalar(item));
                }
            }

            return;
        }

        if (inline) {
            writer.Write(' ');
            writer.WriteLine(Scalar(value));
        } else {
            writer.WriteLine(pad + Scalar(value));
        }
    }

    static void WriteChild(TextWriter writer, Payload value, int indent) {
        if (value.IsObject || value.IsArray) {
            WriteNode(writer, value, indent + 2, inline: true);

            return;
        }

        writer.Write(' ');
        writer.WriteLine(Scalar(value));
    }

    static string Key(string name) => NeedsQuotes(name) ? Quote(name) : name;

    static string Scalar(Payload value)
        => value.ValueKind switch {
            JsonValueKind.Null or JsonValueKind.Undefined => "null",
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => value.ToCell(),
            _ => value.AsString() is { } text ? (NeedsQuotes(text) ? Quote(text) : text) : value.ToCell(),
        };

    /// <summary>
    ///     Whether a string has to be quoted to survive a round trip.
    /// </summary>
    /// <remarks>
    ///     The list is the union of "would parse as another type" and "would break the syntax". Empty
    ///     strings, leading or trailing space, anything starting with a YAML indicator character, and
    ///     the boolean- and number-shaped words all qualify.
    /// </remarks>
    static bool NeedsQuotes(string value) {
        if (value.Length == 0)
            return true;

        if (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]))
            return true;

        if ("-?:,[]{}#&*!|>'\"%@`".Contains(value[0], StringComparison.Ordinal))
            return true;

        if (value.Contains(": ", StringComparison.Ordinal) || value.Contains(" #", StringComparison.Ordinal))
            return true;

        if (value.AsSpan().ContainsAny(['\n', '\r', '\t']))
            return true;

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            return true;

        // ⚠ YAML 1.1's boolean spellings. `no` is a Norwegian country code and a false, which is the
        // reason this list exists rather than a `true`/`false` check.
        return BooleanLookalikes.Contains(value, StringComparer.OrdinalIgnoreCase);
    }

    static readonly string[] BooleanLookalikes = ["true", "false", "yes", "no", "on", "off", "null", "~"];

    static string Quote(string value) {
        var quoted = new StringBuilder(value.Length + 2);
        quoted.Append('"');

        foreach (var character in value) {
            switch (character) {
                case '"':
                    quoted.Append("\\\"");

                    break;

                case '\\':
                    quoted.Append("\\\\");

                    break;

                case '\n':
                    quoted.Append("\\n");

                    break;

                case '\r':
                    quoted.Append("\\r");

                    break;

                case '\t':
                    quoted.Append("\\t");

                    break;

                default:
                    quoted.Append(character);

                    break;
            }
        }

        return quoted.Append('"').ToString();
    }
}
