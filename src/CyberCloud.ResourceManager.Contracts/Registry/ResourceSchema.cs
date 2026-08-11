using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.ResourceManager.Contracts.Registry;

/// <summary>
///     One property a resource type's body may carry, at one api-version.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b><see cref="JsonPointer" /> is an RFC 6901 JSON Pointer and doubles as
///         <see cref="Error.Target" />.</b> docs/plan/08 § Errors: <i>"<c>target</c> is a JSON Pointer
///         into the request body so the portal can highlight the field."</i> Storing the pointer
///         rather than a dotted path means a validation failure already holds the exact string the
///         error body needs, so there is no second place that could format it differently.
///     </para>
///     <para>
///         <b>Nested objects are separate properties with deeper pointers</b>, not a recursive tree.
///         <c>/properties/sku</c> and <c>/properties/sku/name</c> are two entries. That keeps the
///         model flat — a list an emitter can walk in one pass and a validator can index — and it
///         makes the api-version projection a set membership test rather than a tree walk.
///     </para>
/// </remarks>
/// <param name="JsonPointer">
///     The RFC 6901 pointer to this property, for example <c>/properties/sku/name</c>. ⚠ Must start
///     with <c>/</c>; the empty pointer would be the whole document, which is not a property.
/// </param>
/// <param name="Kind">What the value must be.</param>
/// <param name="Required">
///     Whether the body must carry it. ⚠ Required means required <i>on a <c>PUT</c></i>. A
///     <c>PATCH</c> is a merge and validates the merged result, not the patch — see
///     <see cref="ResourceSchema.Validate" />.
/// </param>
/// <param name="ReadOnly">
///     Whether the server owns it. A body that sets a read-only property is refused rather than
///     silently ignored, because "I set it and it did not take" is the bug report nobody can act on.
/// </param>
/// <param name="Secret">
///     Whether the value is a secret. ⚠ A secret property's value never reaches grain state — it is
///     replaced by a <see cref="SecretRef" /> before step 8 — so this flag is what a generated form
///     masks and what the write path redacts.
/// </param>
/// <param name="Description">
///     What the property means, for the generated OpenAPI, CLI help and portal form. ADR-012's
///     emitters read this; the registry being the single source is what makes the generated help and
///     the runtime validation impossible to disagree.
/// </param>
public readonly record struct SchemaProperty(
    string JsonPointer,
    SchemaKind Kind,
    bool Required = false,
    bool ReadOnly = false,
    bool Secret = false,
    string Description = ""
) {
    /// <summary>The pointer, validated on construction and on <c>with</c>.</summary>
    public string JsonPointer {
        get;
        init => field = EnsurePointer(value);
    } = EnsurePointer(JsonPointer);

    /// <summary>The last segment of <see cref="JsonPointer" /> — the property's own name.</summary>
    public string Name {
        get {
            var slash = JsonPointer.LastIndexOf('/');
            return Unescape(JsonPointer[(slash + 1)..]);
        }
    }

    /// <summary>The pointer to the object this property sits in, or <c>""</c> for a root property.</summary>
    public string ParentPointer {
        get {
            var slash = JsonPointer.LastIndexOf('/');
            return slash <= 0 ? string.Empty : JsonPointer[..slash];
        }
    }

    static string EnsurePointer(string jsonPointer) {
        if (string.IsNullOrEmpty(jsonPointer) || jsonPointer[0] != '/') {
            throw new ArgumentException(
                $"'{jsonPointer}' is not a property pointer. A schema property is addressed by an RFC "
                + "6901 JSON Pointer beginning with '/', for example '/properties/sku/name'. The "
                + "empty pointer addresses the whole document, which is not a property — "
                + "docs/plan/08 § Errors.",
                nameof(jsonPointer)
            );
        }

        return jsonPointer;
    }

    static string Unescape(string token) =>
        token.Contains('~', StringComparison.Ordinal)
            ? token.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)
            : token;
}

/// <summary>
///     One resource type's body shape at one api-version — the thing docs/plan/08 § The provider
///     registry calls <c>schema:</c>, and the thing ADR-012's emitters read.
/// </summary>
/// <remarks>
///     <para>
///         <b>This object validates and generates, and that identity is the point.</b>
///         docs/plan/08 § The provider registry:
///         <i>
///             "This object is the source for the four generated surfaces (ADR-012) <b>and</b> for
///             the runtime write path — the same registry that generates the CLI is the one that
///             validates the request body. That identity is what makes drift impossible rather than
///             merely detectable."
///         </i>
///         A schema stored as opaque JSON Schema text would satisfy the first half and not the
///         second: an emitter would have to re-parse and re-interpret it, and a second interpretation
///         is exactly where drift comes back.
///     </para>
///     <para>
///         ⚠ <b>The generation pipeline itself is ADR-012 and is not here.</b> This type is
///         <i>shaped</i> so the emitters are possible — every property carries its pointer, kind,
///         requiredness and description, which is what an OpenAPI schema, a CLI flag, an SDK member
///         and a portal form field each need. Writing the emitters is a separate task and none of
///         them exists yet.
///     </para>
///     <para>
///         ⚠ <b><see cref="Project" /> is the other half of the immutable-date rule.</b> The grain's
///         state is a superset of every version's properties; a read at an old version keeps exactly
///         the properties that version declared and drops the rest. That is what stops an SDK
///         generated against <c>2026-08-01</c> from receiving a field it has no member for.
///     </para>
/// </remarks>
public sealed record ResourceSchema {
    /// <summary>A schema with no properties — every body validates, nothing projects through.</summary>
    /// <remarks>
    ///     ⚠ Useful only for a type whose body is genuinely empty. A type that has not had its schema
    ///     written yet should not use this: an empty schema accepts anything and projects nothing, so
    ///     a read at that version comes back blank rather than failing loudly.
    /// </remarks>
    public static ResourceSchema Empty { get; } = new();

    /// <summary>The properties, in declaration order.</summary>
    public ImmutableArray<SchemaProperty> Properties { get; init; } = [];

    /// <summary>
    ///     Whether a property the schema does not declare is refused. Defaults to refusing.
    /// </summary>
    /// <remarks>
    ///     ⚠ Refusing is the right default and it is the opposite of what most JSON Schemas do.
    ///     An unknown property is nearly always a typo (<c>storageGB</c> for <c>storageGb</c>) or an
    ///     SDK from a newer version talking to an older one. Silently dropping it produces a resource
    ///     that is not what the caller asked for and reports success, which is the failure mode a
    ///     control plane can least afford. Set this to <c>false</c> only for a type that deliberately
    ///     carries a free-form bag.
    /// </remarks>
    public bool RejectsUnknownProperties { get; init; } = true;

    /// <summary>Builds a schema from a property list.</summary>
    /// <param name="properties">The properties. Duplicate pointers are a bug and throw.</param>
    /// <exception cref="ArgumentException">Two properties share a pointer.</exception>
    public static ResourceSchema Of(params ImmutableArray<SchemaProperty> properties) {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in properties) {
            if (!seen.Add(property.JsonPointer)) {
                throw new ArgumentException(
                    $"'{property.JsonPointer}' is declared twice. One pointer names one property, and a "
                    + "duplicate would make validation depend on which entry the validator reached "
                    + "first.",
                    nameof(properties)
                );
            }
        }

        return new() { Properties = properties };
    }

    /// <summary>
    ///     Validates a body. Step 2 of docs/plan/08 § The write path, end to end.
    /// </summary>
    /// <param name="body">The parsed request body.</param>
    /// <param name="requireRequired">
    ///     Whether missing required properties are a failure. <c>true</c> for a <c>PUT</c>, which is a
    ///     full replacement; <c>false</c> when validating a <c>PATCH</c> document on its own — a merge
    ///     patch legitimately omits everything it is not changing, and the merged result is validated
    ///     with <c>true</c> afterwards.
    /// </param>
    /// <param name="allowTags">
    ///     Whether a root-level <c>tags</c> object is accepted. Pass the type's
    ///     <c>SupportsTags</c> — see the remarks.
    /// </param>
    /// <returns>
    ///     Success, or an <see cref="ErrorCode.InvalidRequestBody" /> whose
    ///     <see cref="Error.Target" /> is the offending property's pointer and whose
    ///     <see cref="Error.Details" /> carries every other problem found.
    /// </returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Every problem is reported, not just the first.</b> A portal form that has to be
    ///         fixed one field per round trip is a form nobody finishes. The first problem is the
    ///         top-level error so the response still has one <c>target</c> to highlight; the rest are
    ///         <see cref="Error.Details" />, which is what that field is for (docs/plan/08 § Errors).
    ///     </para>
    ///     <para>
    ///         ⚠ <b><paramref name="allowTags" /> exists because <c>SupportsTags</c> was otherwise
    ///         undeliverable, and the first provider found that out.</b>
    ///         <c>IResourceTypeBuilder.SupportsTags</c> declares that a type carries tags, and the
    ///         write path reads them out of a root-level <c>tags</c> object — but a schema declares
    ///         only the type's own properties, and <see cref="RejectsUnknownProperties" /> defaults to
    ///         refusing, so <c>/tags</c> was rejected as an unknown property before the write path ever
    ///         looked at it. Every declaring provider would have had to add <c>/tags</c> to <i>every</i>
    ///         api-version's schema by hand: platform boilerplate copied twenty times, which is exactly
    ///         the failure docs/plan/25 § R1 describes.
    ///     </para>
    ///     <para>
    ///         The flag rather than an unconditional exemption, because
    ///         <c>IResourceTypeBuilder.SupportsTags</c>' own remarks require the other half: <i>"A type
    ///         that does not declare this refuses a body with tags rather than accepting and dropping
    ///         them."</i> Both branches are now real.
    ///     </para>
    /// </remarks>
    public Result Validate(JsonElement body, bool requireRequired = true, bool allowTags = false) {
        var problems = new List<Error>();

        if (body.ValueKind != JsonValueKind.Object) {
            return Result.Failure(
                ErrorCode.InvalidRequestBody,
                $"The request body is a JSON {Describe(body.ValueKind)} and a resource body is a JSON "
                + "object.",
                ""
            );
        }

        foreach (var property in Properties) {
            var found = TryResolve(body, property.JsonPointer, out var value);

            if (!found) {
                if (requireRequired && property.Required) {
                    problems.Add(
                        new(
                            ErrorCode.InvalidRequestBody,
                            $"'{property.JsonPointer}' is required and is missing.",
                            property.JsonPointer
                        )
                    );
                }

                continue;
            }

            if (property.ReadOnly) {
                problems.Add(
                    new(
                        ErrorCode.InvalidRequestBody,
                        $"'{property.JsonPointer}' is set by the server and cannot be written. It is "
                        + "refused rather than ignored, so that a caller never gets a success for a "
                        + "value that did not take.",
                        property.JsonPointer
                    )
                );

                continue;
            }

            if (!Matches(property.Kind, value)) {
                problems.Add(
                    new(
                        ErrorCode.InvalidRequestBody,
                        $"'{property.JsonPointer}' must be a {Describe(property.Kind)} and is a "
                        + $"{Describe(value.ValueKind)}.",
                        property.JsonPointer
                    )
                );
            }
        }

        if (allowTags) {
            problems.AddRange(TagProblems(body));
        }

        if (RejectsUnknownProperties) {
            problems.AddRange(UnknownProperties(body, allowTags));
        }

        if (problems.Count == 0) {
            return Result.Success;
        }

        var head = problems[0];
        return Result.Failure(
            problems.Count == 1
                ? head
                : head.WithDetails([.. problems.Skip(1)])
        );
    }

    /// <summary>
    ///     Keeps only what this version declares — the "projects down" half of
    ///     docs/plan/08 § The provider registry.
    /// </summary>
    /// <param name="superset">
    ///     The grain's whole state, which is the union of every version's properties.
    /// </param>
    /// <returns>
    ///     A new object carrying exactly the declared pointers that are present in
    ///     <paramref name="superset" />. ⚠ Secret properties are dropped rather than projected — a
    ///     projection is a read, and docs/plan/08 § The provider registry's <c>secret: true</c>
    ///     actions are the only path a secret value leaves by.
    /// </returns>
    public JsonObject Project(JsonObject superset) {
        ArgumentNullException.ThrowIfNull(superset);

        var projected = new JsonObject();

        foreach (var property in Properties) {
            if (property.Secret) {
                continue;
            }

            var node = Resolve(superset, property.JsonPointer);
            if (node is null) {
                continue;
            }

            // A container property is created by whichever of its children lands first, so an object
            // or array declared purely as a container is skipped and its leaves rebuild it.
            if (property.Kind is SchemaKind.Nested) {
                continue;
            }

            Place(projected, property.JsonPointer, node.DeepClone());
        }

        return projected;
    }

    /// <summary>Whether this schema declares <paramref name="jsonPointer" />.</summary>
    /// <param name="jsonPointer">An RFC 6901 pointer.</param>
    public bool Declares(string jsonPointer) {
        foreach (var property in Properties) {
            if (string.Equals(property.JsonPointer, jsonPointer, StringComparison.Ordinal)) {
                return true;
            }
        }

        return false;
    }

    // ── Pointer machinery ──────────────────────────────────────────────────────────────────────

    /// <summary>Reads a pointer out of a <see cref="JsonElement" />.</summary>
    static bool TryResolve(JsonElement root, string jsonPointer, out JsonElement value) {
        value = root;

        foreach (var token in Tokens(jsonPointer)) {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(token, out var next)) {
                value = default;
                return false;
            }

            value = next;
        }

        return true;
    }

    /// <summary>Reads a pointer out of a <see cref="JsonNode" />.</summary>
    static JsonNode? Resolve(JsonNode root, string jsonPointer) {
        var current = root;

        foreach (var token in Tokens(jsonPointer)) {
            if (current is not JsonObject obj || !obj.TryGetPropertyValue(token, out var next) || next is null) {
                return null;
            }

            current = next;
        }

        return current;
    }

    /// <summary>Writes a value at a pointer, creating the objects on the way.</summary>
    static void Place(JsonObject root, string jsonPointer, JsonNode value) {
        var tokens = Tokens(jsonPointer).ToArray();
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

    /// <summary>
    ///     The reference tokens of an RFC 6901 pointer, with <c>~1</c> and <c>~0</c> unescaped.
    /// </summary>
    static IEnumerable<string> Tokens(string jsonPointer) {
        foreach (var raw in jsonPointer.Split('/', StringSplitOptions.RemoveEmptyEntries)) {
            yield return raw.Contains('~', StringComparison.Ordinal)
                ? raw.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)
                : raw;
        }
    }

    /// <summary>Escapes one property name into a reference token.</summary>
    static string Escape(string name) =>
        name.Contains('~', StringComparison.Ordinal) || name.Contains('/', StringComparison.Ordinal)
            ? name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal)
            : name;

    /// <summary>
    ///     Walks the body and reports every leaf whose pointer this schema does not declare.
    /// </summary>
    /// <remarks>
    ///     ⚠ An <i>object</i> that is not declared is reported once, at the object, rather than once
    ///     per leaf inside it. A caller who sent a whole unrecognised section wants one message
    ///     naming it, not forty naming its contents.
    /// </remarks>
    List<Error> UnknownProperties(JsonElement body, bool allowTags) {
        var problems = new List<Error>();
        Walk(body, string.Empty, problems, allowTags);
        return problems;
    }

    /// <summary>The root-level <c>tags</c> object — docs/plan/06 § Tags, locks.</summary>
    const string TagsPointer = "/tags";

    /// <summary>
    ///     Checks the shape of a tag bag, for a type that declares <c>SupportsTags</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ Shape only. The cap of 50 pairs and the key and value rules are the resource grain's, and
    ///     they stay there because they apply to the <i>merged</i> tag set rather than to one request's
    ///     — a <c>PATCH</c> adding one tag to forty-nine is the request that crosses the cap.
    /// </remarks>
    static List<Error> TagProblems(JsonElement body) {
        var problems = new List<Error>();

        if (!body.TryGetProperty("tags", out var tags)) {
            return problems;
        }

        if (tags.ValueKind != JsonValueKind.Object) {
            problems.Add(
                new(
                    ErrorCode.InvalidRequestBody,
                    $"'{TagsPointer}' must be an object of string values and is a {Describe(tags.ValueKind)}.",
                    TagsPointer
                )
            );

            return problems;
        }

        foreach (var tag in tags.EnumerateObject()) {
            if (tag.Value.ValueKind != JsonValueKind.String) {
                problems.Add(
                    new(
                        ErrorCode.InvalidRequestBody,
                        // ⚠ Refused rather than coerced. The write path keeps only string values, so a
                        // number here would be silently dropped — and "I set that tag and it did not
                        // take" is the bug report nobody can act on.
                        $"'{TagsPointer}/{tag.Name}' must be a string and is a {Describe(tag.Value.ValueKind)}.",
                        TagsPointer + "/" + tag.Name
                    )
                );
            }
        }

        return problems;
    }

    void Walk(JsonElement node, string prefix, List<Error> problems, bool allowTags) {
        if (node.ValueKind != JsonValueKind.Object) {
            return;
        }

        foreach (var member in node.EnumerateObject()) {
            var jsonPointer = prefix + "/" + Escape(member.Name);

            // The tag bag is free-form by design, so neither it nor its members are walked.
            if (allowTags && string.Equals(jsonPointer, TagsPointer, StringComparison.Ordinal)) {
                continue;
            }

            if (!Declares(jsonPointer)) {
                problems.Add(
                    new(
                        ErrorCode.InvalidRequestBody,
                        $"'{jsonPointer}' is not a property of this resource type at this api-version. "
                        + "An unknown property is refused rather than dropped: silently ignoring it "
                        + "produces a resource that is not what was asked for and reports success. "
                        + "docs/plan/08 § The provider registry.",
                        jsonPointer
                    )
                );

                continue;
            }

            Walk(member.Value, jsonPointer, problems, allowTags);
        }
    }

    static bool Matches(SchemaKind kind, JsonElement value) =>
        kind switch {
            SchemaKind.Text => value.ValueKind is JsonValueKind.String,
            SchemaKind.Number => value.ValueKind is JsonValueKind.Number,
            SchemaKind.WholeNumber => value.ValueKind is JsonValueKind.Number && value.TryGetInt64(out _),
            SchemaKind.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            SchemaKind.Nested => value.ValueKind is JsonValueKind.Object,
            SchemaKind.Array => value.ValueKind is JsonValueKind.Array,
            _ => false
        };

    static string Describe(SchemaKind kind) =>
        kind switch {
            SchemaKind.Text => "string",
            SchemaKind.Number => "number",
            SchemaKind.WholeNumber => "integer",
            SchemaKind.Boolean => "boolean",
            SchemaKind.Nested => "object",
            SchemaKind.Array => "array",
            _ => kind.ToString().ToLowerInvariant()
        };

    static string Describe(JsonValueKind kind) =>
        kind switch {
            JsonValueKind.String => "string",
            JsonValueKind.Number => "number",
            JsonValueKind.True or JsonValueKind.False => "boolean",
            JsonValueKind.Object => "object",
            JsonValueKind.Array => "array",
            JsonValueKind.Null => "null",
            _ => kind.ToString().ToLowerInvariant()
        };
}
