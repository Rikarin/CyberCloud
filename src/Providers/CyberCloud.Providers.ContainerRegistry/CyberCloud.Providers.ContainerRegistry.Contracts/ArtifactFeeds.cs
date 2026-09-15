using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.ContainerRegistry.Contracts;

/// <summary>
///     Everything addressable about <c>CyberCloud.ContainerRegistry/feeds</c>: the type, its
///     api-version, its body shape, and where a feed's artefacts live. docs/plan/13 § Artifact feeds.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             The first type in the catalogue whose data plane is a platform host and not an
///             object in a tenant's cluster, and that is the fact every other line here follows from.
///         </b> docs/plan/13 § Artifact feeds: "Harbor does OCI only" and NuGet, npm and Maven are
///         "a single .NET service backed by SeaweedFS". That service is
///         <c>CyberCloud.Registry.Feeds.Host</c>. So this type declares no <c>RequiresCluster</c>, no
///         <c>clusterId</c> pointer and no chart objects; what a reconciler does with it is open its
///         catalogue on create and empty its storage prefix on delete, and what the feeds host does
///         with it is everything else.
///     </para>
///     <para>
///         <b>Three protocols, one resource type, one <c>kind</c>.</b> A feed is NuGet <i>or</i> npm
///         <i>or</i> Maven, and the kind is immutable, because the three have three versioning
///         models and a catalogue that held two of them would answer one client's listing with
///         another client's package. A tenant who wants all three creates three feeds, and the
///         portal's resource list shows three rows that say which is which.
///     </para>
///     <para>
///         ⚠
///         <b>
///             What docs/plan/13's scope names and this api-version does not declare: proxy and
///             retention.
///         </b> "Proxy + host + retention" is the row's stated scope, and this is the host third.
///         An <c>upstream</c> property whose only honest value today is empty, or a
///         <c>retention</c> block nothing enforces, would publish a feature nobody can have — the
///         same reason <c>charts/managed/harbor</c> renders <c>WITH_TRIVY</c> as false and no
///         property turns it on. Both are recorded in
///         <c>charts/managed/feeds/conformance.yaml § owed</c>, and each arrives as a new date.
///     </para>
/// </remarks>
public static class ArtifactFeeds {
    /// <summary>The resource type's path under <see cref="ContainerRegistries.ProviderNamespace" />.</summary>
    public const string TypePath = "feeds";

    /// <summary>The one api-version. ⚠ Immutable — adding a field is a new date.</summary>
    public const string V2026 = "2026-08-01";

    /// <summary>
    ///     The chart, under <c>charts/</c>. ⚠ It renders no Kubernetes object — see the chart's own
    ///     <c>NOTES.txt</c> — and exists to carry the type's configuration surface and conformance
    ///     manifest in the catalogue's format.
    /// </summary>
    public const string ChartName = "managed/feeds";

    /// <summary>The pointer to the feed's protocol, one of <see cref="KindNames" />.</summary>
    public const string KindPointer = "/properties/kind";

    /// <summary>The type, namespace and path together.</summary>
    public static ResourceTypeName Type { get; } = new(ContainerRegistries.ProviderNamespace, TypePath);

    /// <summary>
    ///     The <c>kind</c> values, spelled as the body carries them. ⚠ Lower case and matched
    ///     ordinally, because they are also path segments in the feeds host's URLs.
    /// </summary>
    public static ImmutableArray<string> KindNames { get; } = ["nuget", "npm", "maven"];

    /// <summary>
    ///     The body shape at <see cref="V2026" />.
    /// </summary>
    /// <remarks>
    ///     A location, a kind and a description. There is deliberately no <c>clusterId</c>: nothing
    ///     about a feed is placed in a cluster, and a required pointer to one would make every feed
    ///     name a cluster it never touches.
    /// </remarks>
    public static ResourceSchema Schema2026 { get; } =
        ResourceSchema.Of(
            [
                new(
                    "/location",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The region the feed is billed in and served from."
                ) {
                    Format = SchemaFormat.Region,
                    Widget = WidgetHint.Region,
                    Immutable = true,
                    ExampleJson = "\"eu-central\""
                },
                new("/properties", SchemaKind.Nested, Description: "The feed's own settings."),
                new(
                    KindPointer,
                    SchemaKind.Text,
                    Required: true,
                    Description: "Which protocol the feed speaks: nuget (the v3 API), npm (the registry "
                    + "API) or maven (the repository layout). Immutable, because the three have three "
                    + "versioning models."
                ) {
                    AllowedValues = [.. KindNames],
                    Immutable = true,
                    // ⚠ Required AND defaulted, and the default is for the chart. A body without a
                    // kind is refused at the write path; charts/managed/feeds/values.yaml carries
                    // this row, and Build.Charts refuses a required enum whose chart default is a
                    // value the enum does not list — which `""` is. `nuget` is the row docs/plan/13
                    // names first.
                    DefaultJson = "\"nuget\"",
                    ExampleJson = "\"nuget\""
                },
                new(
                    "/properties/description",
                    SchemaKind.Text,
                    Description: "What the feed is for, shown in the portal beside its name."
                ) { MaxLength = 256 }
            ]
        );

    /// <summary>The pointers <see cref="Schema2026" /> declares, in declaration order.</summary>
    public static ImmutableArray<string> Pointers2026 { get; } = [.. Schema2026.Properties.Select(x => x.JsonPointer)];

    /// <summary>Builds a body that satisfies <see cref="Schema2026" />.</summary>
    /// <param name="kind">One of <see cref="KindNames" />.</param>
    /// <param name="description">The optional description.</param>
    /// <param name="location">The region.</param>
    public static string Body(string kind = "nuget", string? description = null, string location = "eu-central") {
        var properties = new JsonObject { ["kind"] = kind };

        if (description is not null) {
            properties["description"] = description;
        }

        return new JsonObject { ["location"] = location, ["properties"] = properties }.ToJsonString();
    }

    /// <summary>Reads the kind out of a validated desired body.</summary>
    /// <param name="desired">The body.</param>
    /// <returns>The kind, or <see cref="FeedKind.Unknown" /> when the body does not carry one.</returns>
    public static FeedKind KindOf(JsonElement desired) {
        if (desired.ValueKind != JsonValueKind.Object
            || !desired.TryGetProperty("properties", out var properties)
            || properties.ValueKind != JsonValueKind.Object
            || !properties.TryGetProperty("kind", out var kind)
            || kind.ValueKind != JsonValueKind.String) {
            return FeedKind.Unknown;
        }

        return ParseKind(kind.GetString());
    }

    /// <summary>Parses a kind name.</summary>
    /// <param name="name">One of <see cref="KindNames" />, or anything else.</param>
    /// <returns>The kind, or <see cref="FeedKind.Unknown" />. ⚠ Ordinal: <c>NuGet</c> is not <c>nuget</c>.</returns>
    public static FeedKind ParseKind(string? name) =>
        name switch {
            "nuget" => FeedKind.NuGet,
            "npm" => FeedKind.Npm,
            "maven" => FeedKind.Maven,
            _ => FeedKind.Unknown
        };

    /// <summary>The kind's name, as the body and the URL spell it.</summary>
    /// <param name="kind">The kind.</param>
    public static string NameOf(FeedKind kind) =>
        kind switch {
            FeedKind.NuGet => "nuget",
            FeedKind.Npm => "npm",
            FeedKind.Maven => "maven",
            _ => "unknown"
        };

    /// <summary>
    ///     The object-store prefix under which every artefact of one feed lives, and nothing else
    ///     does — <c>{tenantId:N}/{feedId:N}/</c>.
    /// </summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="feedId">The feed resource's GUID.</param>
    /// <remarks>
    ///     ⚠ <b>This string is the whole contract between the host that writes and the reconciler
    ///     that deletes.</b> The host stores every artefact at <c>{prefix}{entry.Path}</c>; the
    ///     teardown lists this prefix and removes what it finds. A host that wrote outside the prefix
    ///     would leave bytes no teardown reaches, and a prefix that did not start with the tenant
    ///     would let one tenant's teardown list another's. The tenant is first for that reason and
    ///     <c>ArtifactFeedDeclarationTests.TheStoragePrefixStartsWithTheTenantAndEndsWithASlash</c> pins it.
    /// </remarks>
    public static string StoragePrefix(Guid tenantId, Guid feedId) =>
        tenantId.ToString("N", CultureInfo.InvariantCulture) + "/" + feedId.ToString("N", CultureInfo.InvariantCulture) + "/";
}
