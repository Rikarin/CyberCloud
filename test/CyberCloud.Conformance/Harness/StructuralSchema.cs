using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CyberCloud.Conformance.Harness;

/// <summary>
///     Validates one applied object against a <c>CustomResourceDefinition</c>'s structural schema the
///     way the API server does on a server-side apply, and applies the schema's defaults the way it
///     does on admission.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>What it refuses is what the API server refuses, and each rule names the measurement or
///         the source it was taken from.</b> Issue #91's finding was three fields in one chart that
///         no test could see were wrong; the value of this class is that the next one is red on the
///         first run. The rules, in the order the server applies them:
///     </para>
///     <list type="bullet">
///         <item>
///             <b>A field the schema does not declare is refused</b>, unless an ancestor carries
///             <c>x-kubernetes-preserve-unknown-fields: true</c> or is an embedded resource. On an
///             apply patch the API server builds a typed object from the schema before it prunes
///             anything, and an undeclared field fails that step:
///             <c>failed to create typed patch object (…): .spec.foo: field not declared in schema</c>
///             — the sentence <c>KubeFailures.TypedPatchFailurePrefix</c> was written for, measured on
///             this repository's own k3s. A <c>kubectl create</c> would prune the field silently; the
///             platform never uses that path.
///         </item>
///         <item>
///             <b>Type</b> (<c>object</c>, <c>array</c>, <c>string</c>, <c>integer</c>, <c>number</c>,
///             <c>boolean</c>), with <c>x-kubernetes-int-or-string</c> honoured. The message is the
///             one apiextensions-apiserver builds from kube-openapi's failure, which quotes the TYPE
///             found where a value would go:
///             <c>spec.clusterRef: Invalid value: "string": spec.clusterRef in body must be of type object: "string"</c>.
///         </item>
///         <item>
///             <b>Nullable</b>: a <c>null</c> on a non-nullable declared property is pruned before
///             defaulting — and then defaulted, when the property has a default — rather than refused,
///             which is the CRD reference's § Defaulting and Nullable. A <c>nullable: true</c> field
///             keeps its null. Only a null the schema has no property for — an array item, a member
///             of an ungoverned object — is a type error.
///         </item>
///         <item><b>Required</b> properties: <c>spec.name: Required value</c>.</item>
///         <item>
///             <b>Enum</b>: <c>Unsupported value: "true": supported values: "Off", "Enabled", "Suspended"</c>.
///         </item>
///         <item>
///             <b>Bounds</b>: <c>minimum</c>/<c>maximum</c> (and the exclusive pair), <c>minLength</c>/
///             <c>maxLength</c>, <c>minItems</c>/<c>maxItems</c>, <c>minProperties</c>/<c>maxProperties</c>,
///             <c>pattern</c>, <c>multipleOf</c>, <c>uniqueItems</c>, and <c>allOf</c>/<c>anyOf</c>/
///             <c>oneOf</c>/<c>not</c>, which a structural schema may carry as value constraints.
///         </item>
///         <item>
///             <b>Associative lists</b>: an item of an <c>x-kubernetes-list-type: map</c> list that
///             omits one of its <c>x-kubernetes-list-map-keys</c> is refused, as structured-merge-diff
///             refuses it, and a <c>set</c> list with a duplicate is refused too.
///         </item>
///         <item>
///             <b>Defaults</b> are applied before validation, so a required field with a default is
///             satisfied by it, and the stored object carries what a real one would.
///         </item>
///     </list>
///     <para>
///         ⚠ <b>What it does not model, stated so a green run is read for what it is.</b>
///         <c>x-kubernetes-validations</c> — CEL rules such as the Bucket's "versioning cannot return
///         to Off" — need a CEL evaluator this repository does not have; a transition rule also needs
///         the old object, which an apply-time check does not see. <c>format</c> is not checked
///         beyond the type. <c>metadata</c> is checked as <c>ObjectMeta</c>'s fields and not as the
///         schema's, which is what the API server does too — a definition may describe only
///         <c>name</c> and <c>generateName</c> under it. The webhook an operator may install to
///         validate further is not here and cannot be; a refusal from one belongs to the
///         cluster-backed lane.
///     </para>
///     <para>
///         ⚠ <b><c>status</c> is dropped, not validated, when the definition has the status
///         subresource</b> — the main resource's endpoint ignores it, and a reconciler that rendered
///         one would find it absent on the read-back, here as on a cluster.
///     </para>
/// </remarks>
public static class StructuralSchema {
    /// <summary>
    ///     Applies the version's defaults to <paramref name="body" />, drops a <c>status</c> the
    ///     subresource owns, and validates the result.
    /// </summary>
    /// <param name="definition">The definition serving the kind.</param>
    /// <param name="version">The version the object is addressed under.</param>
    /// <param name="body">The applied body, which is mutated in place by defaulting.</param>
    /// <returns>Every cause the API server would list, in document order; empty when the body is valid.</returns>
    public static IReadOnlyList<string> Admit(CustomResourceDefinition definition, DefinitionVersion version, JsonObject body) {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(body);

        if (version.HasStatusSubresource) {
            body.Remove("status");
        }

        ApplyDefaults(version.Schema, body);

        var causes = new List<string>();
        ValidateRoot(version.Schema, body, causes);

        return causes;
    }

    /// <summary>
    ///     The sentence the API server puts on a <c>422 Invalid</c>:
    ///     <c>Kind.group "name" is invalid: cause</c>, or the causes in brackets when there are several.
    /// </summary>
    /// <param name="definition">The definition, for the kind and group.</param>
    /// <param name="name">The object's name.</param>
    /// <param name="causes">What <see cref="Admit" /> returned.</param>
    public static string Describe(CustomResourceDefinition definition, string name, IReadOnlyList<string> causes) {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(causes);

        var subject = $"{definition.Kind}.{definition.Group} \"{name}\" is invalid: ";

        return causes.Count == 1
            ? subject + causes[0]
            : subject + "[" + string.Join(", ", causes) + "]";
    }

    // ── Defaults ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Sets every <c>default</c> the schema declares for a property the body lacks, at every depth
    ///     the body reaches.
    /// </summary>
    /// <remarks>
    ///     ⚠ Only where an object already exists. A default inside <c>spec.quota</c> is applied when
    ///     the body carries a <c>quota</c>, and not by inventing one — which is the API server's
    ///     rule, and the reason "the CRD's own default stands" is true only for a field whose parent
    ///     was rendered. <c>metadata</c> is never defaulted.
    /// </remarks>
    static void ApplyDefaults(JsonObject schema, JsonNode? value) {
        switch (value) {
            case JsonObject map when schema["properties"] is JsonObject properties:
                foreach (var (name, propertySchema) in properties) {
                    if (propertySchema is not JsonObject property || name == "metadata") {
                        continue;
                    }

                    // ⚠ A null on a non-nullable field is PRUNED, not refused — and a null on one
                    // with a default is defaulted. That is the API server's order (the CRD reference,
                    // § Defaulting and Nullable: "null values for fields that either don't specify the
                    // nullable flag, or give it a false value, will be pruned before defaulting
                    // happens. If a default is present, it will be applied."). The first version of
                    // this refused the null as a type error, which is stricter than the real thing;
                    // the review of #91 measured the gap. A nullable field keeps its null.
                    if (map.TryGetPropertyValue(name, out var present) && present is null && !IsTrue(property["nullable"])) {
                        map.Remove(name);
                    }

                    if (!map.ContainsKey(name) && property["default"] is { } fallback) {
                        map[name] = fallback.DeepClone();
                    }

                    if (map.TryGetPropertyValue(name, out var child)) {
                        ApplyDefaults(property, child);
                    }
                }

                if (schema["additionalProperties"] is JsonObject additional) {
                    PruneNulls(map, additional, name => properties[name] is null);

                    foreach (var (name, child) in map) {
                        if (properties[name] is null) {
                            ApplyDefaults(additional, child);
                        }
                    }
                }

                break;

            case JsonObject map when schema["additionalProperties"] is JsonObject additionalOnly:
                PruneNulls(map, additionalOnly, _ => true);

                foreach (var (_, child) in map) {
                    ApplyDefaults(additionalOnly, child);
                }

                break;

            case JsonArray array when schema["items"] is JsonObject items:
                foreach (var child in array) {
                    ApplyDefaults(items, child);
                }

                break;
        }
    }

    /// <summary>
    ///     Removes every null-valued entry of a map whose value schema is not nullable — the members
    ///     an <c>additionalProperties</c> schema governs, which have no per-name default to fall back to.
    /// </summary>
    /// <param name="map">The object to prune in place.</param>
    /// <param name="valueSchema">The schema every governed value shares.</param>
    /// <param name="governed">Which names the value schema governs — every name, or those no <c>properties</c> entry declares.</param>
    static void PruneNulls(JsonObject map, JsonObject valueSchema, Func<string, bool> governed) {
        if (IsTrue(valueSchema["nullable"])) {
            return;
        }

        foreach (var name in map.Where(x => x.Value is null && governed(x.Key)).Select(x => x.Key).ToList()) {
            map.Remove(name);
        }
    }

    // ── Validation ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     The <c>ObjectMeta</c> fields an apply may carry. Anything else under <c>metadata</c> is
    ///     "field not declared in schema", as it is for a built-in.
    /// </summary>
    static readonly HashSet<string> ObjectMetaFields = new(StringComparer.Ordinal) {
        "name", "generateName", "namespace", "labels", "annotations", "uid", "resourceVersion", "generation",
        "creationTimestamp", "deletionTimestamp", "deletionGracePeriodSeconds", "ownerReferences", "finalizers",
        "managedFields", "selfLink"
    };

    static void ValidateRoot(JsonObject schema, JsonObject body, List<string> causes) {
        // apiVersion, kind and metadata are the API server's own and are always declared, whether or
        // not the schema lists them — every controller-gen schema lists the first two as strings and
        // the third as a bare object, and a schema that omitted them would still admit them.
        foreach (var (name, child) in body) {
            switch (name) {
                case "apiVersion" or "kind":
                    if (child is not JsonValue text || !text.TryGetValue<string>(out _)) {
                        causes.Add($"{name}: Invalid value: {Describe(child)}: {name} in body must be of type string: {TypeName(child)}");
                    }

                    break;

                case "metadata":
                    ValidateMetadata(child, causes);
                    break;
            }
        }

        var rest = new JsonObject();

        foreach (var (name, child) in body) {
            if (name is not ("apiVersion" or "kind" or "metadata")) {
                rest[name] = child?.DeepClone();
            }
        }

        var restSchema = new JsonObject();

        foreach (var (name, child) in schema) {
            restSchema[name] = child?.DeepClone();
        }

        if (restSchema["properties"] is JsonObject properties) {
            properties.Remove("apiVersion");
            properties.Remove("kind");
            properties.Remove("metadata");
        }

        // A root schema always has type object. The three server-owned fields have been checked
        // above and are removed here so that `required: [apiVersion, kind]`, which some definitions
        // carry, is not reported against a body that has both.
        if (restSchema["required"] is JsonArray required) {
            restSchema["required"] = new JsonArray([
                .. required
                    .OfType<JsonValue>()
                    .Where(x => x.GetValue<string>() is not ("apiVersion" or "kind" or "metadata"))
                    .Select(x => (JsonNode)x.DeepClone())
            ]);
        }

        Validate(restSchema, rest, string.Empty, causes, preserveUnknown: false);
    }

    static void ValidateMetadata(JsonNode? metadata, List<string> causes) {
        if (metadata is not JsonObject map) {
            causes.Add($"metadata: Invalid value: {Describe(metadata)}: metadata in body must be of type object: {TypeName(metadata)}");

            return;
        }

        foreach (var (name, child) in map) {
            if (!ObjectMetaFields.Contains(name)) {
                causes.Add(Undeclared("metadata." + name));

                continue;
            }

            if (name is "labels" or "annotations") {
                if (child is not JsonObject strings) {
                    causes.Add($"metadata.{name}: Invalid value: {Describe(child)}: metadata.{name} in body must be of type object: {TypeName(child)}");

                    continue;
                }

                foreach (var (key, entry) in strings) {
                    if (entry is not JsonValue text || !text.TryGetValue<string>(out _)) {
                        causes.Add($"metadata.{name}.{key}: Invalid value: {Describe(entry)}: metadata.{name}.{key} in body must be of type string: {TypeName(entry)}");
                    }
                }
            }
        }
    }

    /// <summary>The apply-time refusal for a field the schema does not know.</summary>
    static string Undeclared(string path) => $".{path}: field not declared in schema";

    static void Validate(JsonObject schema, JsonNode? value, string path, List<string> causes, bool preserveUnknown) {
        var here = path.Length == 0 ? "body" : path;
        var preserves = preserveUnknown
            || IsTrue(schema["x-kubernetes-preserve-unknown-fields"])
            || IsTrue(schema["x-kubernetes-embedded-resource"]);

        if (value is null) {
            // ⚠ Reached only for a null ApplyDefaults could not prune: an array item, or a member
            // of an object no schema governs. A null on a declared non-nullable property was removed
            // (or defaulted) before validation, as the API server removes it.
            if (!IsTrue(schema["nullable"])) {
                var declared = schema["type"]?.GetValue<string>();

                if (declared is not null) {
                    causes.Add($"{here}: Invalid value: \"null\": {here} in body must be of type {declared}: \"null\"");
                }
            }

            return;
        }

        if (!TypeMatches(schema, value, out var declaredType)) {
            // ⚠ The TYPE found is what the API server quotes as the invalid value, not the value:
            // `spec.clusterRef: Invalid value: "string": spec.clusterRef in body must be of type
            // object: "string"` — apiextensions-apiserver turns kube-openapi's type failure into a
            // field.Invalid over the type name. The first version of this quoted the value, which
            // named the same field with a sentence no real cluster ever produces.
            causes.Add($"{here}: Invalid value: {TypeName(value)}: {here} in body must be of type {declaredType}: {TypeName(value)}");

            // The type is wrong, so nothing below it is worth reporting: a string where an object was
            // expected has no properties to be missing, and the server stops here too.
            return;
        }

        if (schema["enum"] is JsonArray allowed && !allowed.Any(x => JsonNode.DeepEquals(x, value))) {
            causes.Add(
                $"{here}: Unsupported value: {Describe(value)}: supported values: "
                + string.Join(", ", allowed.Select(Describe))
            );
        }

        switch (value) {
            case JsonObject map:
                ValidateObject(schema, map, path, causes, preserves);
                break;

            case JsonArray array:
                ValidateArray(schema, array, path, causes, preserves);
                break;

            case JsonValue scalar:
                ValidateScalar(schema, scalar, here, causes);
                break;
        }

        ValidateComposition(schema, value, path, causes, preserves);
    }

    static void ValidateObject(JsonObject schema, JsonObject map, string path, List<string> causes, bool preserves) {
        var here = path.Length == 0 ? "body" : path;
        var properties = schema["properties"] as JsonObject;
        var additional = schema["additionalProperties"];

        if (schema["required"] is JsonArray required) {
            foreach (var name in required.OfType<JsonValue>().Select(x => x.GetValue<string>())) {
                if (!map.ContainsKey(name)) {
                    causes.Add($"{Join(path, name)}: Required value");
                }
            }
        }

        if (schema["minProperties"] is JsonValue minimum && minimum.TryGetValue<long>(out var least) && map.Count < least) {
            causes.Add($"{here}: Invalid value: {Describe(map)}: {here} in body should have at least {least} properties");
        }

        if (schema["maxProperties"] is JsonValue maximum && maximum.TryGetValue<long>(out var most) && map.Count > most) {
            causes.Add($"{here}: Invalid value: {Describe(map)}: {here} in body should have at most {most} properties");
        }

        foreach (var (name, child) in map) {
            var childPath = Join(path, name);

            if (properties?[name] is JsonObject property) {
                Validate(property, child, childPath, causes, preserves);
            } else if (additional is JsonObject additionalSchema) {
                Validate(additionalSchema, child, childPath, causes, preserves);
            } else if (additional is JsonValue flag && flag.TryGetValue<bool>(out var allowsAny) && allowsAny) {
                // `additionalProperties: true` — anything goes underneath.
            } else if (!preserves) {
                causes.Add(Undeclared(childPath));
            }
        }
    }

    static void ValidateArray(JsonObject schema, JsonArray array, string path, List<string> causes, bool preserves) {
        var here = path.Length == 0 ? "body" : path;

        if (schema["minItems"] is JsonValue minimum && minimum.TryGetValue<long>(out var least) && array.Count < least) {
            causes.Add($"{here}: Invalid value: {Describe(array)}: {here} in body should have at least {least} items");
        }

        if (schema["maxItems"] is JsonValue maximum && maximum.TryGetValue<long>(out var most) && array.Count > most) {
            causes.Add($"{here}: Invalid value: {Describe(array)}: {here} in body should have at most {most} items");
        }

        var listType = schema["x-kubernetes-list-type"]?.GetValue<string>();

        if (IsTrue(schema["uniqueItems"]) || listType == "set") {
            for (var i = 0; i < array.Count; i++) {
                for (var j = i + 1; j < array.Count; j++) {
                    if (JsonNode.DeepEquals(array[i], array[j])) {
                        causes.Add($"{here}: Duplicate value: {Describe(array[j])}");
                    }
                }
            }
        }

        if (listType == "map" && schema["x-kubernetes-list-map-keys"] is JsonArray keys) {
            for (var i = 0; i < array.Count; i++) {
                foreach (var key in keys.OfType<JsonValue>().Select(x => x.GetValue<string>())) {
                    if (array[i] is not JsonObject item || !item.ContainsKey(key)) {
                        causes.Add(
                            $"{here}: element {i}: associative list with keys has an element that omits key field \"{key}\" (and doesn't have default value)"
                        );
                    }
                }
            }
        }

        if (schema["items"] is JsonObject items) {
            for (var i = 0; i < array.Count; i++) {
                Validate(items, array[i], $"{here}[{i}]", causes, preserves);
            }
        } else if (!preserves && array.Count > 0 && !IsTrue(schema["x-kubernetes-preserve-unknown-fields"])) {
            // A structural schema's array always declares items; an array without them and without
            // the preserve flag is not structural and the API server would have refused the
            // definition. Reported rather than silently accepted, so a hand-edited definition shows.
            causes.Add($"{here}: items: Required value: must be specified");
        }
    }

    static void ValidateScalar(JsonObject schema, JsonValue scalar, string here, List<string> causes) {
        if (scalar.TryGetValue<string>(out var text)) {
            if (schema["minLength"] is JsonValue minimum && minimum.TryGetValue<long>(out var least) && text.Length < least) {
                causes.Add($"{here}: Invalid value: {Describe(scalar)}: {here} in body should be at least {least} chars long");
            }

            if (schema["maxLength"] is JsonValue maximum && maximum.TryGetValue<long>(out var most) && text.Length > most) {
                causes.Add($"{here}: Invalid value: {Describe(scalar)}: {here} in body should be at most {most} chars long");
            }

            if (schema["pattern"]?.GetValue<string>() is { } pattern && !Regex.IsMatch(text, pattern, RegexOptions.None, TimeSpan.FromSeconds(1))) {
                causes.Add($"{here}: Invalid value: {Describe(scalar)}: {here} in body should match '{pattern}'");
            }

            return;
        }

        if (!TryNumber(scalar, out var number)) {
            return;
        }

        if (schema["minimum"] is JsonValue low && TryNumber(low, out var lowest)) {
            var exclusive = IsTrue(schema["exclusiveMinimum"]);

            if (exclusive ? number <= lowest : number < lowest) {
                causes.Add(
                    $"{here}: Invalid value: {Describe(scalar)}: {here} in body should be greater than "
                    + (exclusive ? string.Empty : "or equal to ") + Describe(low)
                );
            }
        }

        if (schema["maximum"] is JsonValue high && TryNumber(high, out var highest)) {
            var exclusive = IsTrue(schema["exclusiveMaximum"]);

            if (exclusive ? number >= highest : number > highest) {
                causes.Add(
                    $"{here}: Invalid value: {Describe(scalar)}: {here} in body should be less than "
                    + (exclusive ? string.Empty : "or equal to ") + Describe(high)
                );
            }
        }

        if (schema["multipleOf"] is JsonValue step && TryNumber(step, out var multiple) && multiple != 0 && Math.Abs(Math.IEEERemainder(number, multiple)) > 1e-9) {
            causes.Add($"{here}: Invalid value: {Describe(scalar)}: {here} in body should be a multiple of {Describe(step)}");
        }
    }

    /// <summary>
    ///     <c>allOf</c>, <c>anyOf</c>, <c>oneOf</c> and <c>not</c>, which a structural schema may carry
    ///     only as value constraints — so each branch is validated with the same preserve flag and
    ///     no branch is allowed to declare fields the parent does not.
    /// </summary>
    static void ValidateComposition(JsonObject schema, JsonNode value, string path, List<string> causes, bool preserves) {
        var here = path.Length == 0 ? "body" : path;

        if (schema["allOf"] is JsonArray all) {
            foreach (var branch in all.OfType<JsonObject>()) {
                Validate(branch, value, path, causes, preserveUnknown: true);
            }
        }

        if (schema["anyOf"] is JsonArray any && any.Count > 0) {
            var matched = any.OfType<JsonObject>().Any(branch => Passes(branch, value, path, preserves));

            if (!matched) {
                causes.Add($"{here}: Invalid value: {Describe(value)}: {here} in body must validate at least one schema (anyOf)");
            }
        }

        if (schema["oneOf"] is JsonArray one && one.Count > 0) {
            var matched = one.OfType<JsonObject>().Count(branch => Passes(branch, value, path, preserves));

            if (matched != 1) {
                causes.Add($"{here}: Invalid value: {Describe(value)}: {here} in body must validate one and only one schema (oneOf). Found {matched} valid alternatives");
            }
        }

        if (schema["not"] is JsonObject forbidden && Passes(forbidden, value, path, preserves)) {
            causes.Add($"{here}: Invalid value: {Describe(value)}: {here} in body must not validate the schema (not)");
        }
    }

    static bool Passes(JsonObject branch, JsonNode value, string path, bool preserves) {
        var scratch = new List<string>();
        Validate(branch, value, path, scratch, preserveUnknown: true);

        return scratch.Count == 0;
    }

    // ── Types ─────────────────────────────────────────────────────────────────────────────────

    static bool TypeMatches(JsonObject schema, JsonNode value, out string declared) {
        declared = schema["type"]?.GetValue<string>() ?? string.Empty;

        if (IsTrue(schema["x-kubernetes-int-or-string"])) {
            declared = "integer|string";

            return value is JsonValue scalar && (scalar.TryGetValue<string>(out _) || IsInteger(scalar));
        }

        if (declared.Length == 0) {
            // No type and no int-or-string: only legal under preserve-unknown-fields or as a
            // composition branch, both of which accept any shape.
            return true;
        }

        return declared switch {
            "object" => value is JsonObject,
            "array" => value is JsonArray,
            "string" => value is JsonValue s && s.TryGetValue<string>(out _),
            "boolean" => value is JsonValue b && b.TryGetValue<bool>(out _),
            "integer" => value is JsonValue i && IsInteger(i),
            "number" => value is JsonValue n && TryNumber(n, out _),
            _ => true
        };
    }

    static bool IsInteger(JsonValue scalar) {
        if (scalar.TryGetValue<long>(out _)) {
            return true;
        }

        if (scalar.TryGetValue<double>(out var real)) {
            return Math.Floor(real) == real && !double.IsInfinity(real);
        }

        // A value deserialised from text arrives as a JsonElement.
        return scalar.TryGetValue<JsonElement>(out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt64(out _);
    }

    static bool TryNumber(JsonValue scalar, out double number) {
        if (scalar.TryGetValue<double>(out number)) {
            return true;
        }

        if (scalar.TryGetValue<long>(out var integer)) {
            number = integer;

            return true;
        }

        if (scalar.TryGetValue<JsonElement>(out var element) && element.ValueKind == JsonValueKind.Number) {
            return element.TryGetDouble(out number);
        }

        number = 0;

        return false;
    }

    static bool IsTrue(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    static string Join(string path, string name) => path.Length == 0 ? name : path + "." + name;

    /// <summary>The value as the API server quotes it: a string in quotes, anything else as JSON.</summary>
    static string Describe(JsonNode? value) =>
        value switch {
            null => "\"null\"",
            JsonValue scalar when scalar.TryGetValue<string>(out var text) => "\"" + text + "\"",
            JsonValue scalar => scalar.ToJsonString(),
            JsonObject or JsonArray => value.ToJsonString(),
            _ => value.ToString()
        };

    /// <summary>The JSON type of a value, quoted as the API server's type error quotes it.</summary>
    static string TypeName(JsonNode? value) =>
        value switch {
            null => "\"null\"",
            JsonObject => "\"object\"",
            JsonArray => "\"array\"",
            JsonValue scalar when scalar.TryGetValue<string>(out _) => "\"string\"",
            JsonValue scalar when scalar.TryGetValue<bool>(out _) => "\"boolean\"",
            JsonValue scalar when IsInteger(scalar) => "\"integer\"",
            _ => "\"number\""
        };
}
