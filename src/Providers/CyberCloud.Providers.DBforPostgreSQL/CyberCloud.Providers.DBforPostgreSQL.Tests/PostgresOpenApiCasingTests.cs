using CyberCloud.ResourceManager.Contracts.Generation;
using CyberCloud.ResourceManager.Registry;
using System.Collections.Immutable;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.DBforPostgreSQL.Tests;

/// <summary>
///     Every property name, spelled the same in all three places a caller meets it.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>One character of casing.</b> A <c>resourcegroup</c>/<c>resourceGroup</c> mismatch was
///         once failing every create in the platform and surfaced as a <c>404</c> whose reason was
///         only in a log line — nothing compared the two spellings, because both were plausible and
///         each was written in a different file. This type's namespace is
///         <c>CyberCloud.DBforPostgreSQL</c>, which has three internal case changes and is therefore
///         the worst case the whole docs/plan/12 catalogue has: <c>dbforpostgresql</c>,
///         <c>DbForPostgreSql</c> and <c>DBForPostgreSQL</c> are all plausible and all route to
///         nothing.
///     </para>
///     <para>
///         The three places are the registry (<c>PostgresServers.Schema2026</c>), the chart's
///         <c>@param</c> block (<c>ChartRegistryPairTests</c>, compared ordinally) and the generated
///         OpenAPI document, which is this file. The document is <b>emitted here, in-process</b>,
///         rather than read off disk: <c>openapi/2026-08-01.json</c> is written by a build step that
///         runs <i>after</i> compilation, so a test embedding it would compare this build's schema
///         against the previous build's document and go green on a stale file.
///     </para>
/// </remarks>
public sealed class PostgresOpenApiCasingTests {
    [Fact]
    public void EveryDeclaredPropertyNameReachesTheOpenApiDocumentWithItsExactCasing() {
        var names = new HashSet<string>(StringComparer.Ordinal);
        Collect(Document(), names);

        foreach (var property in PostgresServers.Schema2026.Properties) {
            names.ShouldContain(
                property.Name,
                $"'{property.JsonPointer}' does not appear in the emitted OpenAPI document under that "
                + "exact spelling. A property whose document name differs from its registry name by "
                + "one character is a property every generated client sends to the wrong key, and the "
                + "write path then refuses it as unknown."
            );
        }
    }

    [Fact]
    public void TheProviderNamespaceAndTypeAppearInThePathsWithTheirExactCasing() {
        // ⚠ The path is what the gateway routes on, and `ResourceId.TryParsePath` is case-sensitive
        // about the provider namespace — so a document advertising `cybercloud.dbforpostgresql` would
        // generate a CLI and an SDK calling an endpoint the parser resolves to nothing.
        var paths = Paths();

        paths.ShouldContain(
            x => x.Contains("/providers/CyberCloud.DBforPostgreSQL/servers/", StringComparison.Ordinal),
            "no path carries the provider namespace and type as declared"
        );

        foreach (var path in paths.Where(x => x.Contains("dbforpostgresql", StringComparison.OrdinalIgnoreCase))) {
            path.Contains("CyberCloud.DBforPostgreSQL", StringComparison.Ordinal).ShouldBeTrue(path);
        }
    }

    [Fact]
    public void TheActionIsRoutedUnderItsDeclaredCasingRatherThanLowerCased() {
        // ⚠ `listKeys` is the one camel-cased URL SEGMENT this type has. ResourceManagerService matches
        // an action name case-insensitively — its own duplicate check says so — but the document is
        // what a generated client copies, and a lower-cased `listkeys` in the document would be an
        // SDK method whose name no longer matches docs/plan/12's prose or the portal's blade.
        Paths().ShouldContain(
            x => x.EndsWith("/" + PostgresServers.ListKeysAction, StringComparison.Ordinal),
            $"no path ends with '/{PostgresServers.ListKeysAction}'"
        );
    }

    /// <summary>The document the generator would write for this provider alone.</summary>
    /// <remarks>
    ///     ⚠ <b>This provider alone, and not alongside the sample.</b> Rule 2 of docs/plan/03
    ///     § Assembly graph rules is "no <c>Providers.*</c> assembly references another
    ///     <c>Providers.*</c> assembly, not even <c>.Contracts</c>" — this is the first provider in
    ///     the tree with a sibling it could have broken that rule against, and a test project taking
    ///     the reference would put the edge in the graph the gate inspects. That both namespaces share
    ///     <c>openapi/2026-08-01.json</c> without either swallowing the other is
    ///     <c>./build.sh Generate</c>'s assertion, byte-for-byte, against the checked-in file.
    /// </remarks>
    static JsonObject Document() {
        var registry = ProviderRegistry.Build([new PostgresProvider()]);
        return OpenApiEmitter.Emit(registry, OpenApiEmitter.ApiVersionsOf(registry).Single());
    }

    static ImmutableArray<string> Paths() => [.. Document()["paths"]!.AsObject().Select(x => x.Key)];

    static void Collect(JsonNode? node, HashSet<string> names) {
        switch (node) {
            case JsonObject map:
                foreach (var (key, value) in map) {
                    names.Add(key);
                    Collect(value, names);
                }

                break;

            case JsonArray array:
                foreach (var item in array) {
                    Collect(item, names);
                }

                break;

            default:
                break;
        }
    }
}
