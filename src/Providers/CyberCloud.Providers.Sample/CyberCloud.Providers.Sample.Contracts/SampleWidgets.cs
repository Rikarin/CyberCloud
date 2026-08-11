using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Sample.Contracts;

/// <summary>
///     Everything addressable about <c>CyberCloud.Sample/widgets</c>: the type, its api-version, its
///     body shape, and the one Kubernetes object it becomes.
/// </summary>
/// <remarks>
///     <para>
///         <b>A widget is a <c>ConfigMap</c> with two fields in it, and it is meant to stay that
///         way.</b> docs/plan/24 § Phase 1's exit criterion 1 names this provider by name;
///         docs/plan/25 § R1 explains why it is first and why it is trivial — <i>"so its friction is
///         unmistakably the platform's"</i>. Every line of cleverness added here is a line of
///         measurement lost, because a provider that solves a problem itself stops reporting that the
///         platform has one.
///     </para>
///     <para>
///         ⚠ <b><c>/properties/clusterId</c> is declared here and required, and nothing links it to
///         <c>RequiresCluster()</c>.</b> <c>IResourceTypeBuilder.RequiresCluster</c> makes the reconcile
///         driver refuse a pass with no connection, but the connection is resolved from
///         <c>ClusterFrom(body)</c>, which reads this exact pointer out of the body. The two live in
///         different files and nothing checks that a type declaring <c>RequiresCluster</c> also
///         declares the property that supplies it — so forgetting the property is a per-resource
///         runtime failure rather than a silo-start one.
///     </para>
/// </remarks>
public static class SampleWidgets {
    /// <summary>The provider namespace, as docs/plan/24 § Phase 1 spells it.</summary>
    public const string ProviderNamespace = "CyberCloud.Sample";

    /// <summary>The one resource type.</summary>
    public const string TypePath = "widgets";

    /// <summary>The one api-version. ⚠ Immutable — adding a field is a new date.</summary>
    public const string V2026 = "2026-08-01";

    /// <summary>The field manager the apply runs under — ADR-013's stable per-provider name.</summary>
    public const string FieldManager = "cybercloud/cybercloud.sample";

    /// <summary>The type, namespace and path together.</summary>
    public static ResourceTypeName Type { get; } = new(ProviderNamespace, TypePath);

    /// <summary>
    ///     The core-group <c>v1 ConfigMap</c>, with the plural the REST path needs.
    /// </summary>
    /// <remarks>
    ///     ⚠ <see cref="GroupVersionKind.Plural" /> is carried rather than derived, for the reason that
    ///     type's own remarks give: a Kubernetes REST path is keyed by the plural resource name, and
    ///     English pluralisation is not computable.
    /// </remarks>
    public static GroupVersionKind ConfigMapKind { get; } =
        new() { Group = "", Version = "v1", Kind = "ConfigMap", Plural = "configmaps" };

    /// <summary>
    ///     The body shape at <see cref="V2026" />.
    /// </summary>
    /// <remarks>
    ///     A location, the cluster to land in, a message and a switch. That is the whole surface, and
    ///     it is enough to exercise every kind the schema model has a case for except
    ///     <c>SchemaKind.Number</c> and <c>SchemaKind.Array</c>.
    /// </remarks>
    public static ResourceSchema Schema2026 { get; } =
        ResourceSchema.Of(
            [
                new(
                    "/location",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The region the widget is billed in."
                ),
                new("/properties", SchemaKind.Nested, Description: "The widget's own settings."),
                new(
                    "/properties/clusterId",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The cluster whose namespace holds the widget's ConfigMap."
                ),
                new(
                    "/properties/message",
                    SchemaKind.Text,
                    Required: true,
                    Description: "What the ConfigMap's 'message' key says."
                ),
                new(
                    "/properties/enabled",
                    SchemaKind.Boolean,
                    Description: "Whether the widget is switched on. Defaults to off when absent."
                )
            ]
        );

    /// <summary>The pointers <see cref="Schema2026" /> declares, in declaration order.</summary>
    public static ImmutableArray<string> Pointers2026 { get; } =
        [.. Schema2026.Properties.Select(x => x.JsonPointer)];

    /// <summary>Builds a body that satisfies <see cref="Schema2026" />.</summary>
    /// <param name="clusterId">The cluster to place the ConfigMap in.</param>
    /// <param name="message">The message the ConfigMap carries.</param>
    /// <param name="enabled">Whether the widget is on.</param>
    /// <param name="location">The region.</param>
    public static string Body(
        Guid clusterId,
        string message = "hello",
        bool enabled = true,
        string location = "eu-central"
    ) =>
        new JsonObject {
            ["location"] = location,
            ["properties"] = new JsonObject {
                ["clusterId"] = clusterId.ToString("D", CultureInfo.InvariantCulture),
                ["message"] = message,
                ["enabled"] = enabled
            }
        }.ToJsonString();

    /// <summary>
    ///     The <c>data</c> map a desired body becomes.
    /// </summary>
    /// <param name="desired">The validated desired body.</param>
    /// <returns>
    ///     The keys and values, both strings. ⚠ A <c>ConfigMap</c>'s <c>data</c> values are strings and
    ///     nothing else, so the boolean is spelled <c>true</c>/<c>false</c> rather than emitted as a
    ///     JSON boolean — the API server rejects the latter with a type error that names
    ///     <c>data</c> and not the field.
    /// </returns>
    public static ImmutableDictionary<string, string> DataFor(JsonElement desired) {
        var properties = desired.ValueKind == JsonValueKind.Object
                         && desired.TryGetProperty("properties", out var found)
                         && found.ValueKind == JsonValueKind.Object
            ? found
            : default;

        var message = properties.ValueKind == JsonValueKind.Object
                      && properties.TryGetProperty("message", out var text)
                      && text.ValueKind == JsonValueKind.String
            ? text.GetString() ?? string.Empty
            : string.Empty;

        var enabled = properties.ValueKind == JsonValueKind.Object
                      && properties.TryGetProperty("enabled", out var flag)
                      && flag.ValueKind == JsonValueKind.True;

        return ImmutableDictionary<string, string>.Empty
            .Add("message", message)
            .Add("enabled", enabled ? "true" : "false");
    }

    /// <summary>The <c>ConfigMap</c> document a desired body becomes, ready for server-side apply.</summary>
    /// <param name="name">The object's <c>metadata.name</c> — the resource's own name.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     ⚠ No labels, no annotations and no namespace here. Those are ADR-013's seven mandatory
    ///     labels and two annotations, and <c>KubeCommand</c> injects them non-overridably —
    ///     docs/plan/09 § The command builder. A provider that set them itself would be a provider
    ///     that could get them wrong.
    /// </remarks>
    public static string ConfigMapJson(string name, JsonElement desired) {
        var data = new JsonObject();
        foreach (var (key, value) in DataFor(desired)) {
            data[key] = value;
        }

        return new JsonObject { ["metadata"] = new JsonObject { ["name"] = name }, ["data"] = data }
            .ToJsonString();
    }

    /// <summary>Whether an object read back from a cluster carries the data the desired body asks for.</summary>
    /// <param name="objectJson">The object's JSON, exactly as the API server returned it.</param>
    /// <param name="desired">The desired body.</param>
    /// <returns>
    ///     <c>true</c> when every key the desired body implies is present with the right value.
    ///     ⚠ Subset, not equality: server-side apply leaves other managers' keys in place, and
    ///     demanding an exact match would make another controller's annotation look like drift.
    /// </returns>
    public static bool Matches(string objectJson, JsonElement desired) {
        JsonNode? parsed;
        try {
            parsed = JsonNode.Parse(objectJson);
        }
        catch (JsonException) {
            return false;
        }

        if (parsed is not JsonObject document || document["data"] is not JsonObject data) {
            return false;
        }

        foreach (var (key, expected) in DataFor(desired)) {
            if (data[key]?.GetValue<string>() != expected) {
                return false;
            }
        }

        return true;
    }
}
