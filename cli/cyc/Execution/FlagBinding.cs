using System.CommandLine;
using CyberCloud.Cli.VerbTree;

namespace CyberCloud.Cli.Execution;

/// <summary>
///     One generated flag, wired to the <c>System.CommandLine</c> option that carries it.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>"Provided" means the user typed it, not that it has a value.</b> Every write in this
///         CLI sends only the flags that were given: <c>PATCH</c> is a merge patch (docs/plan/08
///         § The write path), so a host that helpfully filled in the tree's <c>default</c> for
///         everything absent would rewrite fields the user never mentioned — on <c>update</c>, that is
///         data loss with a friendly face. Defaults are shown in help and never sent.
///     </para>
///     <para>
///         ⚠ <b>An environment fallback is read here, not in the parser.</b> The tree marks
///         <c>--subscription</c> and <c>--tenant</c> with an <c>env</c>, and docs/plan/21 § Decisions
///         wants that for CI. A <c>DefaultValueFactory</c> reading the environment would make the flag
///         look "provided" to the body builder even when nobody set it.
///     </para>
/// </remarks>
sealed class FlagBinding {
    FlagBinding(VerbTreeFlag flag, Option option, Func<ParseResult, bool> provided, Func<ParseResult, string?> text, Action<ParseResult, Utf8JsonWriter> writeJson) {
        Flag = flag;
        Option = option;
        Provided = provided;
        Text = text;
        WriteJson = writeJson;
    }

    /// <summary>The flag as the generator described it.</summary>
    public VerbTreeFlag Flag { get; }

    /// <summary>The parser option.</summary>
    public Option Option { get; }

    /// <summary>Whether the user gave the flag on this command line.</summary>
    public Func<ParseResult, bool> Provided { get; }

    /// <summary>The value as text, for an address flag.</summary>
    public Func<ParseResult, string?> Text { get; }

    /// <summary>Writes the value as JSON, for a body flag.</summary>
    public Action<ParseResult, Utf8JsonWriter> WriteJson { get; }

    /// <summary>Builds the option for one generated flag.</summary>
    /// <param name="flag">The flag.</param>
    /// <exception cref="CycUsageException">The flag's <c>type</c> is not one this build knows.</exception>
    public static FlagBinding Create(VerbTreeFlag flag) {
        ArgumentNullException.ThrowIfNull(flag);

        return flag.Type switch {
            "switch" => Switch(flag),
            "integer" when flag.Repeated => Repeated(flag, JsonValueKind.Number),
            "integer" => Scalar<long>(flag, (writer, value) => writer.WriteNumberValue(value)),
            "number" when flag.Repeated => Repeated(flag, JsonValueKind.Number),
            "number" => Scalar<double>(flag, (writer, value) => writer.WriteNumberValue(value)),
            "keyValue" => KeyValue(flag),
            "string" when flag.Repeated => Repeated(flag, JsonValueKind.String),
            "string" => TextFlag(flag),
            _ => throw new CycUsageException(
                $"The verb tree describes {flag.Name} with type '{flag.Type}', which this build of cyc "
                + "does not know how to accept. Upgrade cyc."),
        };
    }

    static Option<T> Declare<T>(VerbTreeFlag flag) {
        var option = flag.Alias is { Length: > 0 } alias
            ? new Option<T>(flag.Name, alias)
            : new Option<T>(flag.Name);

        option.Description = Describe(flag);
        option.Required = flag.Required;

        if (flag.Choices.Count > 0 && option is Option<string> closed)
            closed.AcceptOnlyFromAmong([.. flag.Choices]);

        return option;
    }

    /// <summary>
    ///     The help line: the schema's description, then what the generator knows and a person would
    ///     otherwise find out from a <c>400</c>.
    /// </summary>
    static string Describe(VerbTreeFlag flag) {
        var text = new StringBuilder(flag.Summary);

        if (flag.Immutable)
            text.Append(text.Length > 0 ? " " : string.Empty).Append("⚠ Cannot be changed after create.");

        if (flag.Repeated)
            text.Append(text.Length > 0 ? " " : string.Empty).Append("Repeatable.");

        if (flag.Env is { Length: > 0 } variable)
            text.Append(text.Length > 0 ? " " : string.Empty).Append(CultureInfo.InvariantCulture, $"Also {variable}.");

        if (flag.Default.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
            text.Append(text.Length > 0 ? " " : string.Empty).Append(CultureInfo.InvariantCulture, $"Default: {Compact(flag.Default)}.");

        if (flag.Example.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
            text.Append(text.Length > 0 ? " " : string.Empty).Append(CultureInfo.InvariantCulture, $"For example: {Compact(flag.Example)}.");

        return text.ToString();
    }

    /// <summary>
    ///     Renders a <c>default</c> or <c>example</c> for one line of help.
    /// </summary>
    /// <remarks>
    ///     ⚠ Neither <see cref="JsonElement.ToString" /> nor <see cref="JsonElement.GetRawText" /> is
    ///     right here. The first spells a boolean <c>False</c>, with .NET's capital, in help for a JSON
    ///     API whose own document says <c>false</c>; the second preserves the source document's line
    ///     breaks, so the emitter's pretty-printed <c>["10.0.0.0/8"]</c> arrives as three lines in the
    ///     middle of a help table.
    /// </remarks>
    static string Compact(JsonElement value) {
        using var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer)) {
            value.WriteTo(writer);
        }

        var text = Encoding.UTF8.GetString(buffer.ToArray());

        return value.ValueKind == JsonValueKind.String ? text.Trim('"') : text;
    }

    static FlagBinding TextFlag(VerbTreeFlag flag) {
        var option = Declare<string>(flag);

        return new FlagBinding(
            flag,
            option,
            parse => Given(parse, option),
            parse => parse.GetValue(option),
            (parse, writer) => WriteNullable(writer, flag, parse.GetValue(option), (w, v) => w.WriteStringValue(v)));
    }

    static FlagBinding Switch(VerbTreeFlag flag) {
        var option = Declare<bool>(flag);

        return new FlagBinding(
            flag,
            option,
            parse => Given(parse, option),
            parse => parse.GetValue(option) ? "true" : "false",
            (parse, writer) => writer.WriteBooleanValue(parse.GetValue(option)));
    }

    static FlagBinding Scalar<T>(VerbTreeFlag flag, Action<Utf8JsonWriter, T> write) where T : struct {
        var option = Declare<T>(flag);

        return new FlagBinding(
            flag,
            option,
            parse => Given(parse, option),
            parse => Convert.ToString(parse.GetValue(option), CultureInfo.InvariantCulture),
            (parse, writer) => write(writer, parse.GetValue(option)));
    }

    static FlagBinding Repeated(VerbTreeFlag flag, JsonValueKind element) {
        var option = Declare<string[]>(flag);
        option.AllowMultipleArgumentsPerToken = true;

        return new FlagBinding(
            flag,
            option,
            parse => Given(parse, option),
            parse => string.Join(',', parse.GetValue(option) ?? []),
            (parse, writer) => {
                writer.WriteStartArray();

                foreach (var value in parse.GetValue(option) ?? []) {
                    if (element == JsonValueKind.Number && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                        writer.WriteNumberValue(number);
                    else
                        writer.WriteStringValue(value);
                }

                writer.WriteEndArray();
            });
    }

    /// <summary>
    ///     A <c>keyValue</c> flag — <c>--tags env=prod --tags owner=platform</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ The first <c>=</c> splits, so <c>--tags note=a=b</c> is the value <c>a=b</c> rather than a
    ///     parse error. Tag values legitimately contain <c>=</c>; tag keys do not.
    /// </remarks>
    static FlagBinding KeyValue(VerbTreeFlag flag) {
        var option = Declare<string[]>(flag);
        option.AllowMultipleArgumentsPerToken = true;

        return new FlagBinding(
            flag,
            option,
            parse => Given(parse, option),
            parse => string.Join(' ', parse.GetValue(option) ?? []),
            (parse, writer) => {
                writer.WriteStartObject();

                foreach (var pair in parse.GetValue(option) ?? []) {
                    var separator = pair.IndexOf('=', StringComparison.Ordinal);

                    if (separator <= 0)
                        throw new CycUsageException($"{flag.Name} takes key=value pairs; '{pair}' has no '='.");

                    writer.WriteString(pair[..separator], pair[(separator + 1)..]);
                }

                writer.WriteEndObject();
            });
    }

    static void WriteNullable<T>(Utf8JsonWriter writer, VerbTreeFlag flag, T? value, Action<Utf8JsonWriter, T> write) {
        // ⚠ The literal `null` is how a nullable field is cleared, and it is only accepted where the
        // schema says the field is nullable — otherwise a resource legitimately named "null" could
        // never be referred to.
        if (value is null || (flag.Nullable && value is string text && string.Equals(text, "null", StringComparison.Ordinal))) {
            writer.WriteNullValue();

            return;
        }

        write(writer, value);
    }

    static bool Given<T>(ParseResult parse, Option<T> option) => parse.GetResult(option) is { Implicit: false };
}
