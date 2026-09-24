using System.Globalization;
using System.Text.Json.Nodes;

namespace CyberCloud.Core.Policy;

/// <summary>
///     What a condition or a modify operation reads or writes: one of four facts about the request,
///     or an RFC 6901 pointer into the resource body.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Pointers, not Azure's dotted aliases.</b> Azure Policy spells a field
///         <c>Microsoft.Storage/storageAccounts/sku.name</c> and keeps a per-provider alias table to
///         map that onto a body. This platform's registry already addresses every property by pointer
///         — <c>SchemaProperty.JsonPointer</c>, <c>Error.Target</c>, the generated document — so a
///         condition that named <c>/properties/sku</c> needs no second vocabulary and no table that
///         could drift from the schema. A dotted name is refused with a sentence that shows the
///         pointer form, rather than guessed at.
///     </para>
///     <para>
///         ⚠ <b>The four facts are closed, like the operators.</b> <see cref="Type" /> and
///         <see cref="Name" /> are the resource's; <see cref="Operation" /> and <see cref="Action" />
///         are the request's. A deny rule that reads either request fact is the one kind that reaches
///         a <c>DELETE</c> or an action, and an audit or modify rule may not test for one —
///         <see cref="PolicyRule" />'s remarks say why.
///     </para>
/// </remarks>
public readonly record struct PolicyField {
    /// <summary>The resource type, <c>CyberCloud.Sample/widgets</c>.</summary>
    public const string Type = "type";

    /// <summary>The resource's own name, the last segment of its path.</summary>
    public const string Name = "name";

    /// <summary>
    ///     What the request does to the resource — one of <see cref="PolicyOperations" />' four
    ///     spellings.
    /// </summary>
    public const string Operation = "operation";

    /// <summary>The action's name on a <c>POST</c>, and empty on every other request.</summary>
    public const string Action = "action";

    /// <summary>The four facts, in the order a refusal lists them.</summary>
    public static IReadOnlyList<string> Facts { get; } = [Type, Name, Operation, Action];

    PolicyField(string value) => Value = value;

    /// <summary>The field as the rule spells it — a fact's name, or a pointer.</summary>
    public string Value { get; }

    /// <summary>Whether this is a pointer into the body rather than one of the four facts.</summary>
    public bool IsPointer => Value.Length > 0 && Value[0] == '/';

    /// <summary>
    ///     Parses a field. A fact's name is matched ordinally; anything else must be a pointer.
    /// </summary>
    /// <param name="value">The <c>field</c> member's string.</param>
    /// <param name="target">Where the member sits in the definition, for the refusal's <c>target</c>.</param>
    public static Result<PolicyField> Parse(string? value, string target) {
        if (string.IsNullOrEmpty(value)) {
            return Invalid("A field is required: one of 'type', 'name', 'operation', 'action', or a pointer such as '/properties/sku'.", target);
        }

        if (Facts.Contains(value, StringComparer.Ordinal)) {
            return Result<PolicyField>.Success(new(value));
        }

        if (value[0] != '/') {
            return Invalid(
                $"'{value}' is not a field. A field is one of 'type', 'name', 'operation' or 'action', or an "
                + "RFC 6901 pointer into the resource body such as '/properties/sku' or '/tags/env' — the "
                + "spelling the registry, the generated document and every error target already use.",
                target
            );
        }

        if (value.Length == 1) {
            return Invalid(
                "'/' names the whole body, and a condition or an operation names one property of it. "
                + "Use a pointer to a member, such as '/properties/sku'.",
                target
            );
        }

        foreach (var token in value[1..].Split('/')) {
            if (token.Length == 0) {
                return Invalid($"'{value}' has an empty segment (a doubled or trailing '/').", target);
            }

            if (!EscapesAreValid(token)) {
                return Invalid(
                    $"'{value}' has a '~' that is not '~0' or '~1', which RFC 6901 reserves for escaping "
                    + "'~' and '/'.",
                    target
                );
            }
        }

        return Result<PolicyField>.Success(new(value));
    }

    /// <summary>The reference tokens of a pointer field, unescaped.</summary>
    public IEnumerable<string> Tokens() {
        if (!IsPointer) {
            yield break;
        }

        foreach (var raw in Value[1..].Split('/')) {
            yield return raw.Contains('~', StringComparison.Ordinal)
                ? raw.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)
                : raw;
        }
    }

    /// <summary>
    ///     Reads the pointer out of a document. <see langword="null" /> for a member that is absent, and
    ///     for one that is present and JSON <c>null</c> — the two are the same to a condition.
    /// </summary>
    /// <param name="document">The body.</param>
    /// <remarks>
    ///     A numeric token indexes an array, so <c>/properties/rules/0/port</c> reads the first rule's
    ///     port. A token that does not fit the node it meets — a name into an array, an index past the
    ///     end — reads as absent rather than failing, because "the body does not have that" is exactly
    ///     what <c>exists: false</c> is for.
    /// </remarks>
    public JsonNode? Read(JsonNode? document) {
        var current = document;

        foreach (var token in Tokens()) {
            current = current switch {
                JsonObject obj => obj.TryGetPropertyValue(token, out var next) ? next : null,
                JsonArray array when int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                    && index < array.Count => array[index],
                _ => null
            };

            if (current is null) {
                return null;
            }
        }

        return current;
    }

    /// <summary>
    ///     Writes a value at the pointer, creating the objects above it that do not exist.
    /// </summary>
    /// <param name="document">The body to write into.</param>
    /// <param name="value">The value. It is deep-cloned, so one rule's value is never shared between two bodies.</param>
    /// <returns>
    ///     Success, or <see cref="ErrorCode.PolicyViolation" /> when a member on the way is not an
    ///     object — a policy cannot turn a string into an object by writing under it.
    /// </returns>
    public Result Write(JsonObject document, JsonNode? value) {
        ArgumentNullException.ThrowIfNull(document);

        var tokens = Tokens().ToArray();
        var current = document;

        for (var i = 0; i < tokens.Length - 1; i++) {
            if (!current.TryGetPropertyValue(tokens[i], out var next) || next is null) {
                var created = new JsonObject();
                current[tokens[i]] = created;
                current = created;
                continue;
            }

            if (next is not JsonObject nested) {
                return Result.Failure(
                    ErrorCode.PolicyViolation,
                    $"The operation writes '{Value}', and the body's '{string.Join('/', tokens[..(i + 1)].Prepend(""))}' is "
                    + "not an object, so there is nowhere to write it. A modify operation adds or replaces "
                    + "one member of an object; it does not change the shape of the body above it."
                );
            }

            current = nested;
        }

        current[tokens[^1]] = value?.DeepClone();
        return Result.Success;
    }

    /// <inheritdoc />
    public override string ToString() => Value;

    /// <summary>
    ///     A pointer one member deeper, with the member's name escaped — so a refusal can name a
    ///     member the caller spelled with a <c>/</c> or a <c>~</c> in it and still carry a valid
    ///     <see cref="Error.Target" />.
    /// </summary>
    /// <param name="parent">The pointer to the containing object.</param>
    /// <param name="member">The member's name, as the body spells it.</param>
    public static string Append(string parent, string member) {
        ArgumentNullException.ThrowIfNull(member);

        return parent
            + "/"
            + member.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
    }

    static bool EscapesAreValid(string token) {
        for (var i = 0; i < token.Length; i++) {
            if (token[i] != '~') {
                continue;
            }

            if (i + 1 >= token.Length || token[i + 1] is not ('0' or '1')) {
                return false;
            }
        }

        return true;
    }

    static Result<PolicyField> Invalid(string message, string target) =>
        Result<PolicyField>.Failure(ErrorCode.InvalidRequestBody, message, target);
}

/// <summary>
///     The four values <see cref="PolicyField.Operation" /> takes, spelled as a condition compares
///     them.
/// </summary>
public static class PolicyOperations {
    /// <summary>A <c>PUT</c> on a name that was free.</summary>
    public const string Create = "create";

    /// <summary>A <c>PUT</c> or <c>PATCH</c> on a resource that exists.</summary>
    public const string Update = "update";

    /// <summary>A <c>DELETE</c>.</summary>
    public const string Delete = "delete";

    /// <summary>A <c>POST</c> action on an existing resource.</summary>
    public const string Action = "action";

    /// <summary>The four, in the order a message lists them.</summary>
    public static IReadOnlyList<string> All { get; } = [Create, Update, Delete, Action];

    /// <summary>Whether the operation writes a body — a create or an update.</summary>
    /// <param name="operation">One of <see cref="All" />.</param>
    public static bool WritesBody(string operation) =>
        string.Equals(operation, Create, StringComparison.Ordinal)
        || string.Equals(operation, Update, StringComparison.Ordinal);
}

/// <summary>
///     What a condition is evaluated against: the resource body as the write would leave it, and the
///     four facts <see cref="PolicyField" /> names.
/// </summary>
/// <param name="Type">The resource type.</param>
/// <param name="Name">The resource's own name.</param>
/// <param name="Operation">One of <see cref="PolicyOperations.All" />.</param>
/// <param name="Action">The action's name, or empty.</param>
/// <param name="Document">
///     The body. ⚠ Mutable, because a modify rule rewrites it before the deny and audit rules read it
///     — the order <see cref="PolicyRule" />'s remarks set out.
/// </param>
public sealed record PolicyFacts(string Type, string Name, string Operation, string Action, JsonObject Document) {
    /// <summary>The value a field names, or <see langword="null" /> when it is absent.</summary>
    /// <param name="field">The field.</param>
    public JsonNode? Resolve(PolicyField field) =>
        field.Value switch {
            PolicyField.Type => JsonValue.Create(Type),
            PolicyField.Name => JsonValue.Create(Name),
            PolicyField.Operation => JsonValue.Create(Operation),
            PolicyField.Action => Action.Length == 0 ? null : JsonValue.Create(Action),
            _ => field.Read(Document)
        };
}
