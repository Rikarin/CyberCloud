using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Nodes;

namespace CyberCloud.ResourceManager.Contracts.Generation;

/// <summary>
///     One resource type, read back out of an emitted OpenAPI document.
/// </summary>
/// <param name="ResourceType">The fully qualified type, from <c>x-cybercloud-resource-type</c>.</param>
/// <param name="Path">The URL template.</param>
/// <param name="Component">The component key of the body schema.</param>
/// <param name="Body">The body schema itself.</param>
/// <param name="Display">The <c>x-cybercloud-display</c> object.</param>
/// <param name="SupportsTags">Whether the body carries the platform's tag bag.</param>
/// <param name="RequiresCluster">Whether the type is placed into a cluster.</param>
/// <param name="ClusterIdPointer">Where the cluster id is, or <c>""</c>.</param>
/// <param name="SoftDeleteDays">The recovery window, or 0.</param>
/// <param name="Actions">The declared actions.</param>
/// <param name="Deprecated">Whether this api-version is under a retirement notice.</param>
public sealed record DocumentType(
    string ResourceType,
    string Path,
    string Component,
    JsonObject Body,
    JsonObject Display,
    bool SupportsTags,
    bool RequiresCluster,
    string ClusterIdPointer,
    int SoftDeleteDays,
    ImmutableArray<DocumentAction> Actions,
    bool Deprecated
) {
    /// <summary>The provider namespace — everything before the <c>/</c>.</summary>
    public string ProviderNamespace {
        get {
            var slash = ResourceType.IndexOf('/', StringComparison.Ordinal);
            return slash < 0 ? ResourceType : ResourceType[..slash];
        }
    }

    /// <summary>The type path within the provider — <c>servers/databases</c>.</summary>
    public string TypePath {
        get {
            var slash = ResourceType.IndexOf('/', StringComparison.Ordinal);
            return slash < 0 ? string.Empty : ResourceType[(slash + 1)..];
        }
    }

    /// <summary>The display name, which the emitter guarantees is present.</summary>
    public string DisplayName => Text(Display["name"]);

    /// <summary>The plural display name.</summary>
    public string DisplayPlural => Text(Display["plural"]);

    /// <summary>The short form, or <c>""</c>.</summary>
    public string Alias => Text(Display["alias"]);

    /// <summary>The one-sentence summary, or <c>""</c>.</summary>
    public string Summary => Text(Display["summary"]);

    static string Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : string.Empty;
}

/// <summary>One action, read back out of an emitted document.</summary>
/// <param name="Name">The action name.</param>
/// <param name="Permission">The ReBAC permission.</param>
/// <param name="Secret">Whether the response carries secret material.</param>
/// <param name="LongRunning">Whether it answers <c>202</c>.</param>
/// <param name="Request">The request schema, or <see langword="null" />.</param>
/// <param name="Response">The response schema, or <see langword="null" />.</param>
public sealed record DocumentAction(
    string Name,
    string Permission,
    bool Secret,
    bool LongRunning,
    JsonObject? Request,
    JsonObject? Response
);

/// <summary>
///     Reads an emitted OpenAPI document back into the shape the three derived emitters walk.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The derived surfaces read the <i>document</i>, not the registry, and that is
///         docs/plan/21 § Generation's rule rather than a convenience.</b> That document says
///         <c>Build.Generate</c> walks "the provider registry → OpenAPI 3.1 → the SDK's models,
///         clients and pollers" — one hop, so the compatibility gate that protects the published
///         OpenAPI protects those four surfaces at once. A CLI generated straight from the registry
///         could describe a flag the published contract does not have, and no gate would notice.
///     </para>
///     <para>
///         ⚠ <b>ADR-012's fifth surface does not come through here, and that is stated on it rather
///         than left to be discovered.</b> <see cref="ChartAnnotationEmitter" /> reads the registry,
///         because the chart a type renders is <c>ResourceTypeRegistration.Chart</c> and no emitted
///         document carries it — there is nothing here to read a pairing back from. It also keeps
///         declaration order, which every document deliberately destroys by sorting
///         <c>properties</c> ordinally.
///     </para>
///     <para>
///         ⚠ <b>This reader is the price of that rule and it is worth naming.</b> Reading a document
///         back is a second interpretation of it, which is the thing ADR-012 is otherwise at pains to
///         avoid. It is acceptable here and only here because the document was written by
///         <see cref="OpenApiEmitter" /> in this same process seconds earlier — the pair is a
///         serialize/deserialize round trip within one build step, not two components agreeing about
///         a format. What it must never become is a reader for a <i>hand-edited</i> document.
///     </para>
/// </remarks>
public static class DocumentReader {
    /// <summary>Every resource type in a document, ordered by type.</summary>
    /// <param name="document">An emitted per-version document.</param>
    public static ImmutableArray<DocumentType> TypesOf(JsonObject document) {
        ArgumentNullException.ThrowIfNull(document);

        if (document["paths"] is not JsonObject paths) {
            return [];
        }

        var schemas = document["components"]?["schemas"] as JsonObject;
        var found = new List<DocumentType>();

        foreach (var path in paths) {
            if (path.Value is not JsonObject item
                || item["x-cybercloud-action"] is not null
                || Text(item["x-cybercloud-resource-type"]) is not { Length: > 0 } resourceType) {
                continue;
            }

            var component = ComponentOf(item);

            found.Add(new(
                resourceType,
                path.Key,
                component,
                (component.Length > 0 ? schemas?[component] as JsonObject : null) ?? [],
                item["x-cybercloud-display"] as JsonObject ?? [],
                Flag(item["x-cybercloud-supports-tags"]),
                Flag(item["x-cybercloud-requires-cluster"]),
                Text(item["x-cybercloud-cluster-id-pointer"]),
                Number(item["x-cybercloud-soft-delete-days"]),
                ActionsOf(paths, schemas, path.Key, resourceType),
                Flag(item["get"]?["deprecated"])
            ));
        }

        return [.. found.OrderBy(x => x.ResourceType, StringComparer.Ordinal)];
    }

    static ImmutableArray<DocumentAction> ActionsOf(
        JsonObject paths,
        JsonObject? schemas,
        string resourcePath,
        string resourceType
    ) {
        var found = new List<DocumentAction>();

        foreach (var path in paths) {
            if (path.Value is not JsonObject item
                || Text(item["x-cybercloud-action"]) is not { Length: > 0 } name
                || !string.Equals(Text(item["x-cybercloud-resource-type"]), resourceType, StringComparison.Ordinal)
                || !path.Key.StartsWith(resourcePath + "/", StringComparison.Ordinal)
                || item["post"] is not JsonObject post) {
                continue;
            }

            found.Add(new(
                name,
                Text(post["x-cybercloud-permission"]),
                Flag(post["x-cybercloud-secret"]),
                Flag(post["x-cybercloud-long-running"]),
                Resolve(schemas, post["requestBody"]?["content"]?["application/json"]?["schema"]),
                Resolve(schemas, post["responses"]?["200"]?["content"]?["application/json"]?["schema"])
            ));
        }

        return [.. found.OrderBy(x => x.Name, StringComparer.Ordinal)];
    }

    /// <summary>The component key a <c>$ref</c> points at, or <c>""</c>.</summary>
    static string ComponentOf(JsonObject item) {
        var reference = Text(item["get"]?["responses"]?["200"]?["content"]?["application/json"]?["schema"]?["$ref"]);
        const string Prefix = "#/components/schemas/";

        return reference.StartsWith(Prefix, StringComparison.Ordinal) ? reference[Prefix.Length..] : string.Empty;
    }

    /// <summary>
    ///     Follows a local schema <c>$ref</c>, or returns an inline schema. ⚠ An empty inline schema
    ///     reads as <see langword="null" />: "unconstrained" and "not declared" are the same fact here
    ///     and both mean a generated surface cannot type it.
    /// </summary>
    static JsonObject? Resolve(JsonObject? schemas, JsonNode? node) {
        if (node is not JsonObject schema) {
            return null;
        }

        const string Prefix = "#/components/schemas/";
        var reference = Text(schema["$ref"]);

        if (reference.StartsWith(Prefix, StringComparison.Ordinal)) {
            return schemas?[reference[Prefix.Length..]] as JsonObject;
        }

        return schema.Count == 0 ? null : schema;
    }

    /// <summary>A node's string value, or <c>""</c>.</summary>
    public static string Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : string.Empty;

    /// <summary>A node's boolean value, or <see langword="false" />.</summary>
    public static bool Flag(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    /// <summary>A node's integer value, or 0.</summary>
    public static int Number(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<int>(out var number) ? number : 0;

    /// <summary>
    ///     Every leaf of a body schema, flattened to <c>(pointer, schema, required)</c> in
    ///     depth-first, name-sorted order.
    /// </summary>
    /// <param name="schema">A body schema.</param>
    /// <remarks>
    ///     ⚠ Sorted by name at each level, because the emitted document already sorts its
    ///     <c>properties</c> and the derived surfaces must be as deterministic as the document is.
    ///     A CLI whose flag order depended on a dictionary's iteration would diff differently on two
    ///     machines.
    /// </remarks>
    public static ImmutableArray<SchemaLeaf> LeavesOf(JsonObject schema) {
        ArgumentNullException.ThrowIfNull(schema);

        var found = ImmutableArray.CreateBuilder<SchemaLeaf>();
        Walk(schema, string.Empty, found);
        return found.ToImmutable();
    }

    static void Walk(JsonObject node, string pointer, ImmutableArray<SchemaLeaf>.Builder found) {
        if (node["properties"] is not JsonObject properties) {
            return;
        }

        var required = node["required"] is JsonArray names
            ? names.Select(Text).ToHashSet(StringComparer.Ordinal)
            : [];

        foreach (var member in properties.ToList().OrderBy(x => x.Key, StringComparer.Ordinal)) {
            if (member.Value is not JsonObject child) {
                continue;
            }

            var childPointer = pointer + "/" + member.Key;
            var isObject = TypeOf(child) == "object" && child["properties"] is JsonObject;

            found.Add(new(childPointer, member.Key, child, required.Contains(member.Key), isObject));

            if (isObject) {
                Walk(child, childPointer, found);
            }
        }
    }

    /// <summary>
    ///     A schema's <c>type</c>, with a nullable union collapsed to the non-null member.
    /// </summary>
    /// <param name="schema">The schema.</param>
    /// <remarks>
    ///     ⚠ OpenAPI 3.1 spells nullability as <c>["string","null"]</c>, so every consumer has to know
    ///     the union. Collapsing it here means each derived emitter asks "what is it" and
    ///     <see cref="IsNullable" /> separately, rather than three of them re-deriving the union.
    /// </remarks>
    public static string TypeOf(JsonObject schema) {
        ArgumentNullException.ThrowIfNull(schema);

        return schema["type"] switch {
            JsonArray union => union.Select(Text).FirstOrDefault(x => x is { Length: > 0 } and not "null")
                               ?? string.Empty,
            var single => Text(single)
        };
    }

    /// <summary>Whether a schema's type union includes <c>null</c>.</summary>
    /// <param name="schema">The schema.</param>
    public static bool IsNullable(JsonObject schema) {
        ArgumentNullException.ThrowIfNull(schema);

        return schema["type"] is JsonArray union
               && union.Any(x => string.Equals(Text(x), "null", StringComparison.Ordinal));
    }

    /// <summary>A schema's <c>enum</c> values, in document order.</summary>
    /// <param name="schema">The schema.</param>
    public static ImmutableArray<string> EnumOf(JsonObject schema) {
        ArgumentNullException.ThrowIfNull(schema);

        // ⚠ An array's enum lives on its `items`, because that is where the constraint applies — see
        // the remarks on SchemaProperty.ElementKind.
        var carrier = TypeOf(schema) == "array" && schema["items"] is JsonObject items ? items : schema;

        return carrier["enum"] is not JsonArray values ? [] : [.. values.Select(Text)];
    }

    /// <summary>The api-version a document describes.</summary>
    /// <param name="document">The document.</param>
    public static string VersionOf(JsonObject document) {
        ArgumentNullException.ThrowIfNull(document);

        return Text(document["info"]?["version"]);
    }

    /// <summary>A number rendered the one way every derived surface must render it.</summary>
    /// <param name="value">The number.</param>
    public static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>One leaf of a body schema.</summary>
/// <param name="JsonPointer">Its pointer within the body.</param>
/// <param name="Name">Its own member name.</param>
/// <param name="Schema">Its schema object.</param>
/// <param name="Required">Whether its parent lists it as required.</param>
/// <param name="IsObject">Whether it is a container rather than a value.</param>
public readonly record struct SchemaLeaf(
    string JsonPointer,
    string Name,
    JsonObject Schema,
    bool Required,
    bool IsObject
);
