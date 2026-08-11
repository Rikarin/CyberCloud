using System.CommandLine;

namespace CyberCloud.Cli.Execution;

/// <summary>
///     Builds a request body out of the flags the user gave, at the RFC 6901 pointers the generator
///     put on them.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The pointer is the whole point of the seam.</b> <c>CliEmitter</c>'s note on
///         <c>jsonPointer</c> says it is <i>"what the host builds the request body at and what an
///         error's <c>target</c> comes back as, so a failed flag highlights itself"</i> — and
///         <see cref="FlagFor" /> is the other half: a <c>400</c> whose <c>target</c> is
///         <c>/properties/tier</c> is reported as <c>--tier</c>, because nobody typed a JSON pointer.
///     </para>
///     <para>
///         ⚠ <b>Only what was typed is sent.</b> See <see cref="FlagBinding" />: a <c>PATCH</c> is a
///         merge patch, so filling in defaults would rewrite fields nobody mentioned.
///     </para>
/// </remarks>
static class RequestBody {
    /// <summary>
    ///     Serialises the body, or returns <c>null</c> when no body flag was given.
    /// </summary>
    /// <param name="bindings">The verb's flags.</param>
    /// <param name="parse">The parse result.</param>
    public static byte[]? Build(IReadOnlyList<FlagBinding> bindings, ParseResult parse) {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(parse);

        var given = bindings
            .Where(x => x.Flag.JsonPointer is { Length: > 0 } && x.Provided(parse))
            .ToList();

        if (given.Count == 0)
            return null;

        var root = new Branch();

        foreach (var binding in given)
            root.Add(Segments(binding.Flag.JsonPointer!), binding, parse);

        using var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer)) {
            root.WriteTo(writer);
        }

        return buffer.ToArray();
    }

    /// <summary>
    ///     The flag that set a JSON pointer, so an error's <c>target</c> can be reported as something
    ///     the user typed.
    /// </summary>
    /// <param name="bindings">The verb's flags.</param>
    /// <param name="target">The pointer from the error body — docs/plan/08 § Errors.</param>
    /// <returns>The flag name, or <c>null</c> when no flag maps to that pointer.</returns>
    public static string? FlagFor(IReadOnlyList<FlagBinding> bindings, string? target) {
        if (string.IsNullOrEmpty(target))
            return null;

        ArgumentNullException.ThrowIfNull(bindings);

        return bindings
            .FirstOrDefault(x => string.Equals(x.Flag.JsonPointer, target, StringComparison.Ordinal))
            ?.Flag.Name;
    }

    /// <summary>
    ///     Splits a JSON pointer into its unescaped segments — RFC 6901 § 3, where <c>~1</c> is
    ///     <c>/</c> and <c>~0</c> is <c>~</c>. The order matters: unescaping <c>~0</c> first would
    ///     turn <c>~01</c> into <c>/</c>.
    /// </summary>
    static IReadOnlyList<string> Segments(string pointer)
        => [.. pointer
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal))];

    /// <summary>One node of the body under construction: either a leaf a flag writes, or a branch.</summary>
    sealed class Branch {
        readonly List<KeyValuePair<string, Branch>> children = [];

        FlagBinding? leaf;
        ParseResult? parse;

        public void Add(IReadOnlyList<string> segments, FlagBinding binding, ParseResult result) {
            if (segments.Count == 0) {
                leaf = binding;
                parse = result;

                return;
            }

            var name = segments[0];
            var child = children.FirstOrDefault(x => string.Equals(x.Key, name, StringComparison.Ordinal)).Value;

            if (child is null) {
                child = new Branch();
                children.Add(new KeyValuePair<string, Branch>(name, child));
            }

            child.Add([.. segments.Skip(1)], binding, result);
        }

        public void WriteTo(Utf8JsonWriter writer) {
            if (leaf is not null) {
                leaf.WriteJson(parse!, writer);

                return;
            }

            writer.WriteStartObject();

            foreach (var child in children) {
                writer.WritePropertyName(child.Key);
                child.Value.WriteTo(writer);
            }

            writer.WriteEndObject();
        }
    }
}
