using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Conformance.Harness;

/// <summary>
///     One body that differs from a case's <see cref="ProviderConformanceCase.Body" /> in exactly one
///     property, and what was set where.
/// </summary>
/// <param name="JsonPointer">The property's JSON Pointer, as the type's schema declares it.</param>
/// <param name="Value">The value the variant carries there, as JSON, for a message.</param>
/// <param name="Body">The whole body, for a <c>PUT</c>.</param>
public sealed record PropertyVariant(string JsonPointer, string Value, string Body);

/// <summary>
///     Derives, from a type's own schema, one body per property value the default body does not
///     carry — the bodies a tenant can send that no suite converged before the review of issue #91.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Why a derivation and not a member on the case.</b> The sixteen <c>.Conformance</c>
///         suites each converge one <c>Body</c>, and a reconciler's other branches — the block it
///         renders only when a flag is on, the field it adds for the other enum value — reached the
///         committed definition never. <c>PostgresServers.ClusterJson</c> rendered
///         <c>spec.postgresql_synchronous</c>, a key CloudNativePG's definition does not declare, for
///         every server with <c>synchronousReplication: true</c>, and it was green through #91 because
///         the default is <c>false</c>. A <c>Variants</c> member on the case would be a list a provider
///         can under-declare — the objection <c>ProviderConformanceCase</c> records against
///         <c>RequiredCrds</c> — so the set is read off the schema the registry validates requests
///         against, which a provider cannot leave a property out of without also refusing it at the API.
///     </para>
///     <para>
///         <b>What is derived, per property.</b> A boolean is flipped. A closed set contributes every
///         value the body does not already carry. A number contributes its declared minimum and
///         maximum when they differ from the body's value, and one more than the body's value when
///         neither bound is declared — the bounds are where a type's schema and an operator's
///         definition most often disagree, and <c>0</c> replicas is the shape of that disagreement. An
///         open string contributes the schema's own example when it has one and differs from the
///         body, and a fixed word otherwise, when no pattern or format constrains it — a value the
///         type's schema would refuse is a variant that proves nothing, and the suite counts those
///         separately. An array of a closed set contributes every allowed value at once, which is the
///         body <c>PostgresReconcilerTests.AnExtensionNameIsNotALibraryName</c> pins for the same
///         reason.
///     </para>
///     <para>
///         ⚠ <b>What is left alone, and why each is a rule rather than an omission.</b> A read-only
///         property is refused on any write. A secret reaches the renderer through the resolver, not
///         the body. The cluster-id pointer names the harness's one cluster and any other value is
///         the driver's own refusal. A nested object is a container whose leaves are properties in
///         their own right. A property whose parent is absent from the body is still varied — the
///         parent is created around it, because "the block the reconciler renders only when this
///         section is present" is exactly the branch a default body never reaches.
///     </para>
///     <para>
///         ⚠ <b>One property at a time, deliberately.</b> A body varied in two places that a definition
///         refuses names neither, and the point of the row is a message that names the property to
///         look at. Pairs a reconciler renders jointly — a size and a class — are the next gap, and it
///         is recorded rather than closed here.
///     </para>
/// </remarks>
public static class PropertyVariants {
    const string Word = "variant";

    /// <summary>Every single-property variant of one body the schema declares a value for.</summary>
    /// <param name="schema">The type's schema at the api-version the case writes.</param>
    /// <param name="body">The case's default body, which is not modified.</param>
    /// <param name="clusterIdPointer">
    ///     The registration's <c>ClusterIdPointer</c>, which is skipped — empty for a type with none.
    /// </param>
    public static IReadOnlyList<PropertyVariant> Of(ResourceSchema schema, JsonObject body, string clusterIdPointer) {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(body);

        var variants = new List<PropertyVariant>();

        foreach (var property in schema.Properties) {
            if (property.ReadOnly
                || property.Secret
                || property.Kind is SchemaKind.Nested or SchemaKind.Unknown
                || string.Equals(property.JsonPointer, clusterIdPointer, StringComparison.Ordinal)) {
                continue;
            }

            var current = Read(body, property.JsonPointer) ?? Parse(property.DefaultJson);

            foreach (var value in ValuesFor(property, current)) {
                if (current is not null && JsonNode.DeepEquals(current, value)) {
                    continue;
                }

                var variant = body.DeepClone().AsObject();
                Write(variant, property.JsonPointer, value.DeepClone());
                variants.Add(new(property.JsonPointer, value.ToJsonString(), variant.ToJsonString()));
            }
        }

        return variants;
    }

    static IEnumerable<JsonNode> ValuesFor(SchemaProperty property, JsonNode? current) {
        switch (property.Kind) {
            case SchemaKind.Boolean:
                yield return JsonValue.Create(!(current is JsonValue flag && flag.TryGetValue<bool>(out var on) && on));
                break;

            case SchemaKind.Text when property.AllowedValues.Length > 0:
                foreach (var allowed in property.AllowedValues) {
                    yield return JsonValue.Create(allowed);
                }

                break;

            case SchemaKind.Text:
                if (Parse(property.ExampleJson) is { } example) {
                    yield return example;
                } else if (property.Pattern.Length == 0
                    && property.Format == SchemaFormat.None
                    && (property.MaxLength is null || property.MaxLength >= Word.Length)
                    && (property.MinLength is null || property.MinLength <= Word.Length)) {
                    yield return JsonValue.Create(Word);
                }

                break;

            case SchemaKind.WholeNumber or SchemaKind.Number: {
                var now = current is JsonValue number && number.TryGetValue<double>(out var parsed) ? parsed : 0;

                if (property.Minimum is { } least) {
                    yield return Numeric(property.Kind, least);
                }

                if (property.Maximum is { } most) {
                    yield return Numeric(property.Kind, most);
                }

                if (property.Minimum is null && property.Maximum is null) {
                    yield return Numeric(property.Kind, now + 1);
                }

                break;
            }

            case SchemaKind.Array when property.AllowedValues.Length > 0: {
                var all = new JsonArray();
                foreach (var allowed in property.AllowedValues) {
                    all.Add(JsonValue.Create(allowed));
                }

                yield return all;
                break;
            }

            case SchemaKind.Array:
                if (Parse(property.ExampleJson) is { } listed) {
                    yield return listed;
                }

                break;
        }
    }

    static JsonValue Numeric(SchemaKind kind, double value) =>
        kind == SchemaKind.WholeNumber
            ? JsonValue.Create((long)Math.Round(value, MidpointRounding.AwayFromZero))
            : JsonValue.Create(value);

    static JsonNode? Parse(string json) {
        if (json.Length == 0) {
            return null;
        }

        try {
            return JsonNode.Parse(json);
        } catch (JsonException) {
            return null;
        }
    }

    static IEnumerable<string> Segments(string pointer) =>
        pointer.Split('/').Skip(1).Select(x => x.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal));

    static JsonNode? Read(JsonObject body, string pointer) {
        JsonNode? node = body;

        foreach (var segment in Segments(pointer)) {
            if (node is JsonObject map && map.TryGetPropertyValue(segment, out var child)) {
                node = child;
            } else if (node is JsonArray array
                && int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                && index < array.Count) {
                node = array[index];
            } else {
                return null;
            }
        }

        return node;
    }

    /// <summary>Sets one pointer, creating each absent object on the way — never an array.</summary>
    static void Write(JsonObject body, string pointer, JsonNode value) {
        var segments = Segments(pointer).ToList();
        var map = body;

        for (var i = 0; i < segments.Count - 1; i++) {
            if (map[segments[i]] is not JsonObject next) {
                next = [];
                map[segments[i]] = next;
            }

            map = next;
        }

        map[segments[^1]] = value;
    }
}
