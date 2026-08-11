using CyberCloud.ResourceManager.Contracts.Registry;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CyberCloud.ResourceManager.Contracts.Generation;

/// <summary>
///     Checks that an emitted document is structurally a legal OpenAPI 3.1 document.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This is not the OpenAPI meta-schema, and pretending otherwise would be the useful
///         lie.</b> It checks the structural rules this emitter can actually break — a
///         <c>$ref</c> that resolves nowhere, a component key that is not a legal component key, a
///         path template whose <c>{placeholders}</c> and declared parameters disagree, a duplicate
///         <c>operationId</c>, a <c>required</c> naming a property that is not there. Validating
///         against the published meta-schema would need a JSON Schema 2020-12 evaluator and a
///         vendored copy of the schema, and would still not catch the last three, which are
///         <i>semantic</i> rules the meta-schema does not express.
///     </para>
///     <para>
///         It runs on every emitted document on every <c>Generate</c>, not only in a test. A
///         validator that is a test asserts about the documents somebody thought to write a test for;
///         one that is part of the pipeline asserts about the document that is being checked in.
///     </para>
///     <para>
///         ⚠ Every problem is reported, not the first — the same reason
///         <see cref="ResourceSchema.Validate" /> gives. A generator you fix one message at a time is
///         a generator nobody runs twice.
///     </para>
/// </remarks>
public static partial class OpenApiStructure {
    /// <summary>The HTTP methods a Path Item Object may carry, per the specification.</summary>
    static readonly ImmutableArray<string> Methods =
        ["get", "put", "post", "delete", "options", "head", "patch", "trace"];

    /// <summary>The values a Schema Object's <c>type</c> may take, per JSON Schema 2020-12.</summary>
    static readonly ImmutableArray<string> JsonTypes =
        ["array", "boolean", "integer", "null", "number", "object", "string"];

    [GeneratedRegex(@"^[a-zA-Z0-9\.\-_]+$")]
    private static partial Regex ComponentKey { get; }

    [GeneratedRegex(@"\{([^{}]*)\}")]
    private static partial Regex Placeholder { get; }

    /// <summary>
    ///     Validates one document.
    /// </summary>
    /// <param name="document">The emitted document.</param>
    /// <returns>
    ///     One line per problem, each naming the JSON Pointer at fault and the rule. Empty means the
    ///     document is structurally sound.
    /// </returns>
    public static ImmutableArray<string> Validate(JsonNode document) {
        ArgumentNullException.ThrowIfNull(document);

        var problems = new List<string>();

        if (document is not JsonObject root) {
            return ["/ — the document is a " + Describe(document) + " and an OpenAPI document is an object."];
        }

        CheckPreamble(root, problems);
        CheckComponentKeys(root, problems);
        CheckPaths(root, problems);
        CheckSchemas(root, problems);
        CheckReferences(root, root, "", problems);

        problems.Sort(StringComparer.Ordinal);
        return [.. problems];
    }

    static void CheckPreamble(JsonObject root, List<string> problems) {
        var declared = Text(root["openapi"]);

        if (declared is null) {
            problems.Add("/openapi — missing. The document must declare the specification version.");
        } else if (!declared.StartsWith("3.1.", StringComparison.Ordinal)) {
            problems.Add(
                $"/openapi — '{declared}' is not a 3.1 version. docs/plan/02 § ADR-012 specifies "
                + "OpenAPI 3.1, and the field is the exact specification version rather than the "
                + "minor series."
            );
        }

        if (root["info"] is not JsonObject info) {
            problems.Add("/info — missing. Title and version are required by the specification.");
            return;
        }

        if (string.IsNullOrEmpty(Text(info["title"]))) {
            problems.Add("/info/title — missing or empty, and the specification requires it.");
        }

        if (string.IsNullOrEmpty(Text(info["version"]))) {
            problems.Add("/info/version — missing or empty, and the specification requires it.");
        }
    }

    /// <summary>
    ///     A node's string value, or <see langword="null" /> when it is absent or is not a string.
    /// </summary>
    /// <remarks>
    ///     ⚠ <see cref="JsonNode.GetValue{T}" /> throws when the node is the wrong kind, and this
    ///     validator is pointed at documents that came off disk as well as at ones this process just
    ///     built. A validator that throws on a malformed file reports nothing about it.
    /// </remarks>
    static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    static void CheckComponentKeys(JsonObject root, List<string> problems) {
        if (root["components"] is not JsonObject components) {
            return;
        }

        foreach (var section in components) {
            if (section.Value is not JsonObject entries) {
                continue;
            }

            foreach (var entry in entries) {
                if (!ComponentKey.IsMatch(entry.Key)) {
                    problems.Add(
                        $"/components/{section.Key}/{entry.Key} — '{entry.Key}' is not a legal component "
                        + @"key. The specification fixes the key to ^[a-zA-Z0-9\.\-_]+$, so a resource "
                        + "type's '/' has to be folded before it becomes one."
                    );
                }
            }
        }
    }

    static void CheckPaths(JsonObject root, List<string> problems) {
        if (root["paths"] is not JsonObject paths) {
            problems.Add("/paths — missing. An empty object is legal; an absent one is not.");
            return;
        }

        var operationIds = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var path in paths) {
            if (!path.Key.StartsWith('/')) {
                problems.Add($"/paths/{path.Key} — a path template must begin with '/'.");
            }

            if (path.Value is not JsonObject item) {
                problems.Add($"/paths/{path.Key} — is a {Describe(path.Value)} and a Path Item is an object.");
                continue;
            }

            CheckPathParameters(root, path.Key, item, problems);

            foreach (var method in Methods) {
                if (item[method] is not JsonObject operation) {
                    continue;
                }

                if (operation["responses"] is not JsonObject responses || responses.Count == 0) {
                    problems.Add(
                        $"/paths/{path.Key}/{method}/responses — an operation with no response declares "
                        + "nothing a client can handle."
                    );
                } else {
                    foreach (var response in responses) {
                        if (!string.Equals(response.Key, "default", StringComparison.Ordinal)
                            && !(response.Key.Length == 3 && response.Key.All(char.IsAsciiDigit))) {
                            problems.Add(
                                $"/paths/{path.Key}/{method}/responses/{response.Key} — a response key is "
                                + "a three-digit status code or 'default'."
                            );
                        }
                    }
                }

                if (Text(operation["operationId"]) is not { Length: > 0 } operationId) {
                    continue;
                }

                if (operationIds.TryGetValue(operationId, out var owner)) {
                    problems.Add(
                        $"/paths/{path.Key}/{method}/operationId — '{operationId}' is already used by "
                        + $"{owner}. The specification requires it to be unique, and every SDK generator "
                        + "turns it into a method name."
                    );
                    continue;
                }

                operationIds[operationId] = $"/paths/{path.Key}/{method}";
            }
        }
    }

    /// <summary>
    ///     Every <c>{placeholder}</c> in a path template must be a declared path parameter, and every
    ///     declared path parameter must appear in the template.
    /// </summary>
    /// <remarks>
    ///     ⚠ The rule the emitter is most likely to break, because the template is built by string
    ///     concatenation and the parameter list by <c>$ref</c>, and nothing but this connects them.
    ///     Parameters may sit on the Path Item or on the operation; both are collected.
    /// </remarks>
    static void CheckPathParameters(JsonObject root, string template, JsonObject item, List<string> problems) {
        var declared = new HashSet<string>(StringComparer.Ordinal);

        Collect(item["parameters"]);

        foreach (var method in Methods) {
            if (item[method] is JsonObject operation) {
                Collect(operation["parameters"]);
            }
        }

        var placeholders = Placeholder.Matches(template)
            .Select(x => x.Groups[1].Value)
            .ToList();

        foreach (var placeholder in placeholders.Where(x => !declared.Contains(x)).Distinct(StringComparer.Ordinal)) {
            problems.Add(
                $"/paths/{template} — '{{{placeholder}}}' appears in the template and no path parameter "
                + "declares it."
            );
        }

        foreach (var orphan in declared.Where(x => !placeholders.Contains(x, StringComparer.Ordinal))
                     .OrderBy(x => x, StringComparer.Ordinal)) {
            problems.Add(
                $"/paths/{template} — path parameter '{orphan}' is declared and does not appear in the "
                + "template."
            );
        }

        void Collect(JsonNode? parameters) {
            if (parameters is not JsonArray array) {
                return;
            }

            foreach (var entry in array) {
                var parameter = Resolve(root, entry) as JsonObject;

                if (parameter is null
                    || !string.Equals(Text(parameter["in"]), "path", StringComparison.Ordinal)) {
                    continue;
                }

                if (Text(parameter["name"]) is { Length: > 0 } name) {
                    declared.Add(name);
                }
            }
        }
    }

    static void CheckSchemas(JsonObject root, List<string> problems) {
        if (root["components"]?["schemas"] is not JsonObject schemas) {
            return;
        }

        foreach (var schema in schemas) {
            Walk(schema.Value, "/components/schemas/" + schema.Key, problems);
        }
    }

    static void Walk(JsonNode? node, string pointer, List<string> problems) {
        if (node is not JsonObject schema) {
            return;
        }

        if (Text(schema["type"]) is { } type && !JsonTypes.Contains(type, StringComparer.Ordinal)) {
            problems.Add(
                $"{pointer}/type — '{type}' is not a JSON Schema type. The seven are "
                + string.Join(", ", JsonTypes) + "."
            );
        }

        if (schema["required"] is JsonArray required) {
            var properties = schema["properties"] as JsonObject;

            foreach (var name in required.Select(x => Text(x)).Where(x => x is not null)) {
                if (properties?[name!] is null) {
                    problems.Add(
                        $"{pointer}/required — names '{name}', which is not in this schema's properties. "
                        + "A required property that is not declared can never be supplied."
                    );
                }
            }
        }

        if (schema["properties"] is JsonObject members) {
            foreach (var member in members) {
                Walk(member.Value, pointer + "/properties/" + member.Key, problems);
            }
        }

        Walk(schema["items"], pointer + "/items", problems);
    }

    /// <summary>
    ///     Every <c>$ref</c> in the document must be a local pointer that resolves.
    /// </summary>
    /// <remarks>
    ///     ⚠ An external <c>$ref</c> is refused rather than followed. docs/plan/21 § OpenAPI makes the
    ///     document "the contract", and a contract that is only complete if a second file is fetched
    ///     is not one. It is also what keeps the compatibility diff honest: a diff of one file cannot
    ///     see a breaking change made in another.
    /// </remarks>
    static void CheckReferences(JsonObject root, JsonNode? node, string pointer, List<string> problems) {
        switch (node) {
            case JsonObject obj:
                if (Text(obj["$ref"]) is { } reference) {
                    if (!reference.StartsWith("#/", StringComparison.Ordinal)) {
                        problems.Add(
                            $"{pointer}/$ref — '{reference}' is not a local reference. The document is the "
                            + "contract and must be complete on its own."
                        );
                    } else if (Resolve(root, obj) is null) {
                        problems.Add($"{pointer}/$ref — '{reference}' resolves to nothing in this document.");
                    }
                }

                foreach (var member in obj) {
                    CheckReferences(root, member.Value, pointer + "/" + member.Key, problems);
                }

                break;

            case JsonArray array:
                for (var i = 0; i < array.Count; i++) {
                    CheckReferences(
                        root,
                        array[i],
                        pointer + "/" + i.ToString(CultureInfo.InvariantCulture),
                        problems
                    );
                }

                break;

            default:
                break;
        }
    }

    /// <summary>Follows a one-level local <c>$ref</c>, or returns the node unchanged.</summary>
    static JsonNode? Resolve(JsonObject root, JsonNode? node) {
        if (node is not JsonObject obj || Text(obj["$ref"]) is not { } reference) {
            return node;
        }

        if (!reference.StartsWith("#/", StringComparison.Ordinal)) {
            return null;
        }

        JsonNode? current = root;

        foreach (var token in reference[2..].Split('/')) {
            if (current is not JsonObject step) {
                return null;
            }

            // RFC 6901 escaping, which a component key cannot contain but a path template can.
            current = step[token.Replace("~1", "/", StringComparison.Ordinal)
                .Replace("~0", "~", StringComparison.Ordinal)];
        }

        return current;
    }

    static string Describe(JsonNode? node) =>
        node switch {
            null => "null",
            JsonObject => "object",
            JsonArray => "array",
            _ => "scalar"
        };
}
