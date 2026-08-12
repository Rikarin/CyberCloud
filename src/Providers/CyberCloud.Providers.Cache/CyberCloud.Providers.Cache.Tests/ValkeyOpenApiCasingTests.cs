using CyberCloud.ResourceManager.Contracts.Generation;
using CyberCloud.ResourceManager.Registry;
using System.Collections.Immutable;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Cache.Tests;

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
///         ⚠ <b>This type's exposure is the opposite of the first provider's and is not smaller.</b>
///         <c>CyberCloud.DBforPostgreSQL</c> is dangerous because it has three internal case changes.
///         <c>CyberCloud.Cache/redis</c> is dangerous because it reads like prose: the namespace is one
///         ordinary word, the type is an all-lower-case one, and the product is called <b>Valkey</b> —
///         so <c>CyberCloud.Cache/valkey</c>, <c>CyberCloud.Caches/redis</c> and
///         <c>CyberCloud.cache/redis</c> are all things a helpful person writes, and every one of them
///         routes to nothing.
///     </para>
///     <para>
///         The three places are the registry (<c>ValkeyCaches.Schema2026</c>), the generated OpenAPI
///         document (this file) and the chart's generated schema
///         (<c>ChartRegistryPairTests</c>). The document is <b>emitted here, in-process</b>, rather
///         than read off disk: <c>openapi/2026-08-01.json</c> is written by a build step that runs
///         <i>after</i> compilation, so a test embedding it would compare this build's schema against
///         the previous build's document and go green on a stale file.
///     </para>
/// </remarks>
public sealed class ValkeyOpenApiCasingTests {
    [Fact]
    public void EveryDeclaredPropertyNameReachesTheOpenApiDocumentWithItsExactCasing() {
        var names = new HashSet<string>(StringComparer.Ordinal);
        Collect(Document(), names);

        foreach (var property in ValkeyCaches.Schema2026.Properties) {
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
        // about the provider namespace — so a document advertising `cybercloud.cache` would generate a
        // CLI and an SDK calling an endpoint the parser resolves to nothing.
        var paths = Paths();

        paths.ShouldContain(
            x => x.Contains("/providers/CyberCloud.Cache/redis/", StringComparison.Ordinal),
            "no path carries the provider namespace and type as declared"
        );

        foreach (var path in paths.Where(x => x.Contains("cybercloud.cache", StringComparison.OrdinalIgnoreCase))) {
            path.Contains("CyberCloud.Cache", StringComparison.Ordinal).ShouldBeTrue(path);
        }
    }

    [Fact]
    public void TheTypeSegmentIsRedisAndNotTheProductName() {
        // ⚠ THE RENAME NOBODY WOULD CALL A BUG. ADR-011 says "say Valkey on the product page", and
        // every display name, description and chart in this provider does. The PATH is the Azure-parity
        // one — docs/plan/01 maps this row onto Microsoft.Cache/redis — and a path is what a tenant's
        // existing tooling addresses by string. Somebody making the API "consistent" with the product
        // name would break every caller and would be able to point at ADR-011 while doing it.
        Paths().ShouldContain(
            x => x.Contains("/providers/CyberCloud.Cache/redis/", StringComparison.Ordinal),
            "the type segment is no longer `redis`"
        );

        Paths().ShouldNotContain(
            x => x.Contains("/valkey/", StringComparison.OrdinalIgnoreCase),
            "a path advertises the product name as the resource type. ADR-011 asks for `Valkey` on the "
            + "product page, not in the route — docs/plan/01 § Azure parity maps this row onto "
            + "Microsoft.Cache/redis."
        );
    }

    [Fact]
    public void TheActionIsRoutedUnderItsDeclaredCasingRatherThanLowerCased() {
        // ⚠ `listKeys` is the one camel-cased URL SEGMENT this type has. ResourceManagerService matches
        // an action name case-insensitively — its own duplicate check says so — but the document is
        // what a generated client copies, and a lower-cased `listkeys` in the document would be an SDK
        // method whose name no longer matches docs/plan/12's prose or the portal's blade.
        Paths().ShouldContain(
            x => x.EndsWith("/" + ValkeyCaches.ListKeysAction, StringComparison.Ordinal),
            $"no path ends with '/{ValkeyCaches.ListKeysAction}'"
        );
    }

    /// <summary>The document the generator would write for this provider alone.</summary>
    /// <remarks>
    ///     ⚠ <b>This provider alone.</b> Rule 2 of docs/plan/03 § Assembly graph rules is "no
    ///     <c>Providers.*</c> assembly references another <c>Providers.*</c> assembly, not even
    ///     <c>.Contracts</c>", and a test project taking such a reference would put the edge in the
    ///     graph the gate inspects. That three namespaces share <c>openapi/2026-08-01.json</c> without
    ///     any of them swallowing another is <c>./build.sh Generate</c>'s assertion, byte-for-byte,
    ///     against the checked-in file.
    /// </remarks>
    static JsonObject Document() {
        var registry = ProviderRegistry.Build([new ValkeyCacheProvider()]);
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
