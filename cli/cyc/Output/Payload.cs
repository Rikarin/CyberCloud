namespace CyberCloud.Cli.Output;

/// <summary>
///     One JSON value on its way to the terminal: what came back from the platform, what
///     <c>--query</c> made of it, and what a renderer walks.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Why not <c>JsonNode</c>.</b> A <c>--query</c> builds values that were never in the
///         response — a multiselect list, a projection, the result of <c>length()</c> — so the payload
///         has to be constructible, and building a <c>JsonNode</c> tree means
///         <c>JsonValue.Create&lt;T&gt;</c>, which wants a <c>JsonTypeInfo</c> this project cannot
///         hand it without a serializer context per shape. This type composes an existing
///         <see cref="JsonElement" /> with lists and maps built in memory, and writes the whole thing
///         through <see cref="Utf8JsonWriter" /> — no reflection anywhere, which is what
///         <c>IsAotCompatible</c> demands.
///     </para>
///     <para>
///         ⚠ <b><see cref="Missing" /> is not <see cref="Null" />, and JMESPath needs both.</b> A
///         field that was absent and a field that was <c>null</c> are the same thing to a caller
///         reading the result, but they are not the same thing to a filter: <c>[?tier]</c> drops both
///         and <c>[?tier == `null`]</c> keeps only the second.
///     </para>
/// </remarks>
sealed class Payload {
    readonly JsonElement element;
    readonly List<Payload>? items;
    readonly List<KeyValuePair<string, Payload>>? members;
    readonly PayloadKind kind;

    Payload(PayloadKind kind, JsonElement element, List<Payload>? items, List<KeyValuePair<string, Payload>>? members) {
        this.kind = kind;
        this.element = element;
        this.items = items;
        this.members = members;
    }

    /// <summary>A value that was not there. Renders as nothing and is false in a filter.</summary>
    public static Payload Missing { get; } = new(PayloadKind.Missing, default, null, null);

    /// <summary>An explicit JSON <c>null</c>.</summary>
    public static Payload Null { get; } = new(PayloadKind.Null, default, null, null);

    /// <summary>Wraps a parsed response body or any part of one.</summary>
    /// <param name="element">The element. Its owning <see cref="JsonDocument" /> must outlive the payload.</param>
    public static Payload Of(JsonElement element)
        => element.ValueKind switch {
            JsonValueKind.Null or JsonValueKind.Undefined => Null,
            _ => new Payload(PayloadKind.Element, element, null, null),
        };

    /// <summary>An array built in memory — a projection, a multiselect list, a filtered sequence.</summary>
    /// <param name="values">The elements, in order.</param>
    public static Payload Array(List<Payload> values) => new(PayloadKind.Array, default, values, null);

    /// <summary>An object built in memory — a multiselect hash.</summary>
    /// <param name="values">The members, in declaration order.</param>
    public static Payload Object(List<KeyValuePair<string, Payload>> values) => new(PayloadKind.Object, default, null, values);

    /// <summary>A string that was not in the response — the result of <c>join()</c> or <c>to_string()</c>.</summary>
    /// <param name="value">The text.</param>
    public static Payload Text(string value) => new(PayloadKind.String, default, null, null) { Literal = value };

    /// <summary>A number that was not in the response — the result of <c>length()</c>.</summary>
    /// <param name="value">The number.</param>
    public static Payload Number(double value) => new(PayloadKind.Number, default, null, null) { Numeric = value };

    /// <summary>A boolean that was not in the response — the result of a comparison.</summary>
    /// <param name="value">The value.</param>
    public static Payload Boolean(bool value) => value ? True : False;

    static Payload True { get; } = new(PayloadKind.True, default, null, null);

    static Payload False { get; } = new(PayloadKind.False, default, null, null);

    string? Literal { get; init; }

    double Numeric { get; init; }

    /// <summary>What this value is, in <see cref="JsonValueKind" />'s vocabulary.</summary>
    public JsonValueKind ValueKind
        => kind switch {
            PayloadKind.Element => element.ValueKind,
            PayloadKind.Array => JsonValueKind.Array,
            PayloadKind.Object => JsonValueKind.Object,
            PayloadKind.String => JsonValueKind.String,
            PayloadKind.Number => JsonValueKind.Number,
            PayloadKind.True => JsonValueKind.True,
            PayloadKind.False => JsonValueKind.False,
            PayloadKind.Null => JsonValueKind.Null,
            _ => JsonValueKind.Undefined,
        };

    /// <summary>Whether the value was absent — distinct from a JSON <c>null</c>.</summary>
    public bool IsMissing => kind == PayloadKind.Missing;

    /// <summary>Whether this is an array, however it was built.</summary>
    public bool IsArray => ValueKind == JsonValueKind.Array;

    /// <summary>Whether this is an object, however it was built.</summary>
    public bool IsObject => ValueKind == JsonValueKind.Object;

    /// <summary>The elements of an array, or nothing.</summary>
    public IEnumerable<Payload> Items {
        get {
            if (items is not null)
                return items;

            if (kind == PayloadKind.Element && element.ValueKind == JsonValueKind.Array)
                return element.EnumerateArray().Select(Of);

            return [];
        }
    }

    /// <summary>The members of an object, in order, or nothing.</summary>
    public IEnumerable<KeyValuePair<string, Payload>> Members {
        get {
            if (members is not null)
                return members;

            if (kind == PayloadKind.Element && element.ValueKind == JsonValueKind.Object)
                return element.EnumerateObject().Select(x => new KeyValuePair<string, Payload>(x.Name, Of(x.Value)));

            return [];
        }
    }

    /// <summary>How many elements or members there are; <c>0</c> for anything else.</summary>
    public int Count
        => items?.Count
            ?? members?.Count
            ?? (kind == PayloadKind.Element
                ? element.ValueKind switch {
                    JsonValueKind.Array => element.GetArrayLength(),
                    JsonValueKind.Object => element.EnumerateObject().Count(),
                    JsonValueKind.String => element.GetString()!.Length,
                    _ => 0,
                }
                : Literal?.Length ?? 0);

    /// <summary>One member of an object, or <see cref="Missing" />.</summary>
    /// <param name="name">The member name, matched exactly — JSON keys are case-sensitive.</param>
    public Payload Member(string name) {
        if (members is not null) {
            foreach (var member in members) {
                if (string.Equals(member.Key, name, StringComparison.Ordinal))
                    return member.Value;
            }

            return Missing;
        }

        if (kind == PayloadKind.Element && element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value))
            return Of(value);

        return Missing;
    }

    /// <summary>One element of an array by index, negative counting from the end, or <see cref="Missing" />.</summary>
    /// <param name="index">The index.</param>
    public Payload At(int index) {
        var all = items ?? (kind == PayloadKind.Element && element.ValueKind == JsonValueKind.Array
            ? [.. element.EnumerateArray().Select(Of)]
            : null);

        if (all is null)
            return Missing;

        var resolved = index < 0 ? all.Count + index : index;

        return resolved >= 0 && resolved < all.Count ? all[resolved] : Missing;
    }

    /// <summary>The text of a JSON string, or <c>null</c> when this is not one.</summary>
    public string? AsString()
        => kind switch {
            PayloadKind.String => Literal,
            PayloadKind.Element when element.ValueKind == JsonValueKind.String => element.GetString(),
            _ => null,
        };

    /// <summary>The value of a JSON number, or <c>null</c> when this is not one.</summary>
    public double? AsNumber()
        => kind switch {
            PayloadKind.Number => Numeric,
            PayloadKind.Element when element.ValueKind == JsonValueKind.Number => element.GetDouble(),
            _ => null,
        };

    /// <summary>
    ///     Whether the value is truthy, as JMESPath defines it: <c>false</c>, <c>null</c>, an empty
    ///     string, an empty array, an empty object and an absent value are false; everything else,
    ///     including <c>0</c>, is true.
    /// </summary>
    public bool IsTruthy
        => ValueKind switch {
            JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.False => false,
            JsonValueKind.True => true,
            JsonValueKind.String => AsString() is { Length: > 0 },
            JsonValueKind.Array or JsonValueKind.Object => Count > 0,
            _ => true,
        };

    /// <summary>Whether two values are the same JSON value.</summary>
    /// <param name="other">The value to compare with.</param>
    public bool SameAs(Payload other) {
        ArgumentNullException.ThrowIfNull(other);

        if (ValueKind != other.ValueKind)
            return false;

        return ValueKind switch {
            JsonValueKind.String => string.Equals(AsString(), other.AsString(), StringComparison.Ordinal),
            JsonValueKind.Number => AsNumber() == other.AsNumber(),
            JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null or JsonValueKind.Undefined => true,
            _ => string.Equals(ToJson(indented: false), other.ToJson(indented: false), StringComparison.Ordinal),
        };
    }

    /// <summary>Writes the value.</summary>
    /// <param name="writer">The writer. A <see cref="Missing" /> value writes <c>null</c>.</param>
    public void WriteTo(Utf8JsonWriter writer) {
        ArgumentNullException.ThrowIfNull(writer);

        switch (kind) {
            case PayloadKind.Element:
                element.WriteTo(writer);

                break;

            case PayloadKind.Array:
                writer.WriteStartArray();

                foreach (var item in items!)
                    item.WriteTo(writer);

                writer.WriteEndArray();

                break;

            case PayloadKind.Object:
                writer.WriteStartObject();

                foreach (var member in members!) {
                    writer.WritePropertyName(member.Key);
                    member.Value.WriteTo(writer);
                }

                writer.WriteEndObject();

                break;

            case PayloadKind.String:
                writer.WriteStringValue(Literal);

                break;

            case PayloadKind.Number:
                writer.WriteNumberValue(Numeric);

                break;

            case PayloadKind.True:
                writer.WriteBooleanValue(value: true);

                break;

            case PayloadKind.False:
                writer.WriteBooleanValue(value: false);

                break;

            default:
                writer.WriteNullValue();

                break;
        }
    }

    /// <summary>The value as JSON text.</summary>
    /// <param name="indented">Whether to pretty-print. Scripts do not care; humans do.</param>
    public string ToJson(bool indented) {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = indented })) {
            WriteTo(writer);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    ///     The value as one cell of a table or a TSV row: a string as itself, a scalar as its literal
    ///     text, and anything structured as compact JSON.
    /// </summary>
    public string ToCell()
        => ValueKind switch {
            JsonValueKind.Undefined or JsonValueKind.Null => string.Empty,
            JsonValueKind.String => AsString() ?? string.Empty,
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => AsNumber()?.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty,
            _ => ToJson(indented: false),
        };

    enum PayloadKind {
        Missing,
        Element,
        Array,
        Object,
        String,
        Number,
        True,
        False,
        Null,
    }
}
