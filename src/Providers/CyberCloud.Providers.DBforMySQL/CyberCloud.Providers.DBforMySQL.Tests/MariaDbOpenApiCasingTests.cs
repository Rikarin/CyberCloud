using CyberCloud.ResourceManager.Contracts.Generation;
using CyberCloud.ResourceManager.Registry;
using System.Collections.Immutable;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.DBforMySQL.Tests;

/// <summary>
///     Every property name, spelled the same in all three places a caller meets it.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>One character of casing.</b> A <c>resourcegroup</c>/<c>resourceGroup</c> mismatch was
///         once failing every create in the platform and surfaced as a <c>404</c> whose reason was only
///         in a log line — nothing compared the two spellings, because both were plausible and each was
///         written in a different file.
///     </para>
///     <para>
///         ⚠ <b><c>CyberCloud.DBforMySQL</c> is the most dangerous namespace in the tree, and the
///         reason is arithmetic rather than opinion.</b> It carries four internal case transitions in
///         ten characters, where its nearest sibling <c>CyberCloud.DBforPostgreSQL</c> carries three
///         and <c>CyberCloud.Cache</c> carries none. <c>DbForMySql</c> is what an IDE's normalise
///         refactor produces; <c>DBForMySQL</c> is what somebody who capitalises acronyms consistently
///         writes; <c>DBforMysql</c> is what somebody following MySQL's own branding writes. Every one
///         of them is a plausible good-faith edit, and every one routes to nothing.
///     </para>
///     <para>
///         The three places are the registry (<c>MariaDbServers.Schema2026</c>), the generated OpenAPI
///         document (this file) and the chart's generated schema
///         (<c>ChartRegistryPairTests</c>). The document is <b>emitted here, in-process</b>, rather
///         than read off disk: <c>openapi/2026-08-01.json</c> is written by a build step that runs
///         <i>after</i> compilation, so a test embedding it would compare this build's schema against
///         the previous build's document and go green on a stale file.
///     </para>
/// </remarks>
public sealed class MariaDbOpenApiCasingTests {
    [Fact]
    public void EveryDeclaredPropertyNameReachesTheOpenApiDocumentWithItsExactCasing() {
        var names = new HashSet<string>(StringComparer.Ordinal);
        Collect(Document(), names);

        foreach (var property in MariaDbServers.Schema2026.Properties) {
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
        // about the provider namespace — so a document advertising `cybercloud.dbformysql` would
        // generate a CLI and an SDK calling an endpoint the parser resolves to nothing.
        //
        // ⚠ A LITERAL, not `MariaDbServers.ProviderNamespace`. Both sides reading the same constant is
        // how a casing sabotage stayed green on an earlier provider.
        var paths = Paths();

        paths.ShouldContain(
            x => x.Contains("/providers/CyberCloud.DBforMySQL/servers/", StringComparison.Ordinal),
            "no path carries the provider namespace and type as declared"
        );

        foreach (var path in paths.Where(x => x.Contains("cybercloud.dbformysql", StringComparison.OrdinalIgnoreCase))) {
            path.Contains("CyberCloud.DBforMySQL", StringComparison.Ordinal).ShouldBeTrue(path);
        }
    }

    [Fact]
    public void TheTypeSegmentIsServersAndTheNamespaceIsNotTheEngineName() {
        // ⚠ THE RENAME NOBODY WOULD CALL A BUG, IN BOTH DIRECTIONS. docs/plan/12 line 310 makes the
        // product page say MariaDB, and this provider's display name, summary and CLI alias all do —
        // so somebody making the API "consistent" with the product would rename the namespace to
        // CyberCloud.DBforMariaDB and break every caller while pointing at line 310 to justify it. The
        // PATH is the Azure-parity one (docs/plan/01 maps this row onto Microsoft.DBforMySQL/servers)
        // and a path is what a tenant's existing tooling addresses by string. Valkey made the same
        // split and its own casing test pins the same shape.
        Paths().ShouldContain(
            x => x.Contains("/providers/CyberCloud.DBforMySQL/servers/", StringComparison.Ordinal),
            "the type segment is no longer `servers` under `CyberCloud.DBforMySQL`"
        );

        Paths().ShouldNotContain(
            x => x.Contains("mariadb", StringComparison.OrdinalIgnoreCase),
            "a path advertises the engine name as part of the resource type. docs/plan/12 asks for "
            + "MariaDB on the product page, not in the route — docs/plan/01 § Azure parity maps this "
            + "row onto Microsoft.DBforMySQL/servers."
        );
    }

    [Fact]
    public void TheActionIsRoutedUnderItsDeclaredCasingRatherThanLowerCased() {
        // ⚠ `listKeys` is the one camel-cased URL SEGMENT this type has. ResourceManagerService matches
        // an action name case-insensitively — its own duplicate check says so — but the document is
        // what a generated client copies, and a lower-cased `listkeys` in the document would be an SDK
        // method whose name no longer matches docs/plan/12's prose or the portal's blade.
        Paths().ShouldContain(
            x => x.EndsWith("/listKeys", StringComparison.Ordinal),
            "no path ends with '/listKeys'"
        );
    }

    static JsonObject Document() {
        var registry = ProviderRegistry.Build([new MariaDbProvider()]);
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
