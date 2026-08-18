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
///         ⚠ <b><c>/properties/clusterId</c> is declared here and required, and
///         <c>RequiresCluster()</c> now names it.</b> This provider is where that gap was found: the
///         reconcile driver refused a pass with no cluster connection, the manager resolved one by
///         reading a hard-coded <c>/properties/clusterId</c> out of the body, and nothing checked that
///         a type declaring the flag also declared the property — so forgetting it was a per-resource
///         runtime failure, after the caller had been told <c>202</c>, rather than a silo-start one.
///         <c>ProviderBuilder.CheckClusterPlacement</c> now refuses such a type at silo start and
///         <c>ResourceManagerService.ClusterFrom</c> reads the registered pointer, so the flag and the
///         shape can no longer disagree.
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
    ///     <para>
    ///         A location, the cluster to land in, a message and a switch. That is the whole surface,
    ///         and it is enough to exercise every kind the schema model has a case for.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The constraints below are declarations, not capability.</b> This provider's own
    ///         remarks warn that cleverness here degrades the measurement docs/plan/25 § R1 asks for —
    ///         and none of this is cleverness: not one line of <c>WidgetReconciler</c> changed. Every
    ///         one is a fact the registry could not previously express, declared here so that the
    ///         published <c>openapi/2026-08-01.json</c> shows it. A registry field no provider declares
    ///         is a field no generated surface proves, and "it works in a unit test" is exactly the
    ///         evidence R1 says not to accept.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Every property added here is optional.</b> A published api-version is immutable
    ///         (docs/plan/08 § The provider registry) and <c>OpenApiCompatibility.RequiredAdded</c>
    ///         refuses a property that was optional and became required — so a <i>new</i> required
    ///         property would need a new date, not an edit to this one.
    ///     </para>
    /// </remarks>
    public static ResourceSchema Schema2026 { get; } =
        ResourceSchema.Of(
            [
                new(
                    "/location",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The region the widget is billed in."
                ) {
                    // A platform format: checked with the same rule every other platform name obeys,
                    // and rendered by docs/plan/20's region picker rather than by a text box.
                    Format = SchemaFormat.Region,
                    Widget = WidgetHint.Region,
                    Immutable = true,
                    ExampleJson = "\"eu-central\""
                },
                new("/properties", SchemaKind.Nested, Description: "The widget's own settings."),
                new(
                    "/properties/clusterId",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The cluster whose namespace holds the widget's ConfigMap."
                ) {
                    // ⚠ This is the property RequiresCluster names. ProviderBuilder now refuses the
                    // type if this pointer is missing or is not a required string, so the flag and the
                    // shape can no longer disagree.
                    Format = SchemaFormat.Uuid,
                    Widget = WidgetHint.Cluster,
                    Immutable = true
                },
                new(
                    "/properties/message",
                    SchemaKind.Text,
                    Required: true,
                    Description: "What the ConfigMap's 'message' key says."
                ) {
                    // A ConfigMap key's value is a string of any length; the cap is ours, so that a
                    // caller learns the limit from the document rather than from the API server.
                    MinLength = 1,
                    MaxLength = 1024,
                    ExampleJson = "\"hello\""
                },
                new(
                    "/properties/enabled",
                    SchemaKind.Boolean,
                    Description: "Whether the widget is switched on. Defaults to off when absent."
                ) {
                    // The sentence above was already in the description and was not machine-readable.
                    DefaultJson = "false"
                },
                new(
                    "/properties/tier",
                    SchemaKind.Text,
                    Description: "How much the widget's ConfigMap is allowed to say."
                ) {
                    // The sku.name case, on the one type the platform has. Four values, refused at the
                    // write path and offered by every generated surface.
                    AllowedValues = ["free", "basic", "standard", "premium"],
                    Widget = WidgetHint.Sku,
                    DefaultJson = "\"free\""
                },
                new(
                    "/properties/replicas",
                    SchemaKind.WholeNumber,
                    Description: "How many copies of the ConfigMap's message the widget claims to hold."
                ) {
                    Minimum = 1,
                    Maximum = 9,
                    DefaultJson = "1"
                },
                new(
                    "/properties/allowedCidrs",
                    SchemaKind.Array,
                    Description: "The ranges the widget claims to serve. Shape only; nothing enforces it."
                ) {
                    // The array gap: an element kind, and the element's own constraints. Before this
                    // an SDK saw object[] and a CLI could not type a repeated flag.
                    ElementKind = SchemaKind.Text,
                    Pattern = @"\d{1,3}(\.\d{1,3}){3}/\d{1,2}",
                    Widget = WidgetHint.Cidr,
                    ExampleJson = "[\"10.0.0.0/8\"]"
                },
                new(
                    "/properties/retiredOn",
                    SchemaKind.Text,
                    Description: "When the widget was retired, or null while it is live."
                ) {
                    // ⚠ The one nullable property in the platform, and it is here to make the decision
                    // visible in a published document rather than only in a doc comment. Note what it
                    // costs: on a PATCH this field's null and "remove this field" are the same bytes —
                    // see the remarks on SchemaProperty.Nullable.
                    Nullable = true,
                    Format = SchemaFormat.DateTime
                }
            ]
        );

    /// <summary>The action a widget declares.</summary>
    /// <remarks>
    ///     ⚠ A constant rather than the literal the provider used to pass, because
    ///     <c>WidgetPingHandler.Action</c> has to report the same string and the dispatcher refuses
    ///     the two if they disagree. A literal in two files is a refusal waiting for the first typo.
    /// </remarks>
    public const string PingAction = "ping";

    /// <summary>
    ///     What a <c>POST …/ping</c> carries.
    /// </summary>
    /// <remarks>
    ///     ⚠ Declared so that the action's parameters are refused by the write path on the same terms
    ///     a resource's properties are. Before <c>ActionRegistration.Request</c> existed, the emitted
    ///     document said <c>schema: {}</c> and the manager checked nothing — an action was the one
    ///     part of the API surface with no contract at all.
    /// </remarks>
    public static ResourceSchema PingRequest { get; } =
        ResourceSchema.Of(
            [
                new("/echo", SchemaKind.Text, Description: "Text the response repeats back.") {
                    MaxLength = 64,
                    DefaultJson = "\"pong\""
                }
            ]
        );

    /// <summary>What a <c>POST …/ping</c> returns.</summary>
    public static ResourceSchema PingResponse { get; } =
        ResourceSchema.Of(
            [
                new("/echo", SchemaKind.Text, Required: true, Description: "What was echoed."),
                new("/at", SchemaKind.Text, Required: true, Description: "When the ping was served.") {
                    Format = SchemaFormat.DateTime
                }
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
