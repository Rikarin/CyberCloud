using CyberCloud.ResourceManager.Contracts.Generation;
using CyberCloud.ResourceManager.Registry;
using System.Collections.Immutable;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Search.Tests;

/// <summary>
///     Failure class (a): every property name of the search type, spelled the same in all three places
///     a caller meets it.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>One character of casing.</b> A <c>resourcegroup</c>/<c>resourceGroup</c> mismatch was
///         once failing every create in the platform and surfaced as a <c>404</c> whose reason was
///         only in a log line.
///     </para>
///     <para>
///         ⚠ <b>EVERY EXPECTATION BELOW IS A LITERAL, AND A PREVIOUS PROVIDER'S CASING SABOTAGE
///         STAYED GREEN BECAUSE IT WAS NOT.</b> That version built the expected path from the same two
///         constants the emitter reads, so re-casing the constant left the whole suite green — two
///         things derived from one constant agree however that constant is spelled. The strings here
///         are typed out by hand, and they are the fourth independent copy after
///         docs/plan/12 § The catalogue, <c>charts/managed/opensearch/Chart.yaml</c>'s
///         <c>cybercloud.io/resource-type</c> and <c>charts/managed/opensearch/conformance.yaml</c>.
///     </para>
///     <para>
///         ⚠ <b>This type's casing risk is concentrated in three names, and the dangerous one is
///         <c>masterNodes</c>.</b> The API property is <c>masterNodes</c>; the <i>role</i> the rendered
///         object carries is <c>cluster_manager</c>; the operator's own filter accepts <c>master</c>
///         as well. Three vocabularies for one concept, two of them snake-cased, one of them the API —
///         and the property that would read most naturally in the document, <c>clusterManagerNodes</c>,
///         is not the one that is declared. <c>dataNodes</c> and <c>coordinatingNodes</c> are the other
///         two, and their lower-cased spellings are ordinary English words.
///     </para>
///     <para>
///         The document is <b>emitted here, in-process</b>, rather than read off disk:
///         <c>openapi/2026-08-01.json</c> is written by a build step that runs <i>after</i>
///         compilation, so a test embedding it would compare this build's schema against the previous
///         build's document and go green on a stale file.
///     </para>
/// </remarks>
public sealed class OpenSearchOpenApiCasingTests {
    /// <summary>The spelling, written out. ⚠ <b>A literal, and it has to be.</b></summary>
    const string QualifiedType = "CyberCloud.Search/services";

    [Fact]
    public void EveryDeclaredPropertyNameReachesTheOpenApiDocumentWithItsExactCasing() {
        var names = new HashSet<string>(StringComparer.Ordinal);
        Collect(Document(), names);

        foreach (var property in OpenSearchServices.Schema2026.Properties) {
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
    public void TheCamelCasedPropertyNamesAreSpelledOutHereRatherThanDerived() {
        // ⚠ THE LITERALS ARE THE TEST. Reading these off Schema2026 would compare the schema to
        // itself.
        var names = new HashSet<string>(StringComparer.Ordinal);
        Collect(Document(), names);

        foreach (var expected in new[] { "dataNodes", "masterNodes", "coordinatingNodes" }) {
            names.ShouldContain(expected, $"'{expected}' is not in the document under that casing");
            names.ShouldNotContain(
                expected.ToLowerInvariant(),
                $"the document carries '{expected.ToLowerInvariant()}' as well as '{expected}'. Two "
                + "spellings of one property is a body half the generated clients send to the wrong "
                + "key."
            );
        }
    }

    [Fact]
    public void TheApiSpellsMasterNodesAndTheCustomResourceSpellsClusterManager() {
        // ⚠ THREE VOCABULARIES FOR ONE CONCEPT AND ONLY ONE OF THEM IS THE API. `cluster_manager` is
        // the OpenSearch ROLE this provider renders into spec.nodePools[].roles; `master` is the
        // deprecated spelling the operator's `availableRoles` still accepts; `masterNodes` is the
        // property a tenant sets. A document carrying either snake-cased form would be a body every
        // generated client sends to a key the write path refuses as unknown — and the mistake reads as
        // correct, because the value it carries genuinely is the number of cluster managers.
        var names = new HashSet<string>(StringComparer.Ordinal);
        Collect(Document(), names);

        names.ShouldContain("masterNodes");

        names.ShouldNotContain(
            "cluster_manager",
            "the OpenSearch role name reached the API document. It is the CR's spelling, not this "
            + "platform's, and the two must not be the same string in the same surface."
        );

        names.ShouldNotContain("clusterManagerNodes", "two spellings of one property");
        names.ShouldNotContain("nodePools", "the operator's own array name reached the API document");
    }

    [Fact]
    public void TheTypePathSurvivesIntoThePathsExactly() {
        // ⚠ The path is what the gateway routes on, and `ResourceId.TryParsePath` compares the type
        // path through ResourceTypeName — which is case-INSENSITIVE. So a mis-cased document would
        // still route, and this test is not about routing: it is about what a generated client, a
        // portal breadcrumb and docs/plan/12's prose all copy.
        OpenSearchServices.Type.ToString().ShouldBe(
            QualifiedType,
            "the declared type is spelled differently from docs/plan/12 § The catalogue and from "
            + "charts/managed/opensearch/Chart.yaml's cybercloud.io/resource-type annotation."
        );

        var paths = Paths();

        paths.ShouldContain(
            x => x.Contains("/providers/" + QualifiedType + "/", StringComparison.Ordinal),
            "no path carries the provider namespace and type as docs/plan/12 spells them"
        );

        foreach (var path in paths.Where(
                     x => x.Contains("search/services", StringComparison.OrdinalIgnoreCase)
                 )) {
            path.Contains(QualifiedType, StringComparison.Ordinal).ShouldBeTrue(
                $"'{path}' spells the type in a casing other than '{QualifiedType}'."
            );
        }
    }

    [Fact]
    public void TheActionIsRoutedUnderItsDeclaredCasingRatherThanLowerCased() {
        Paths().ShouldContain(
            x => x.EndsWith("/" + OpenSearchServices.ListKeysAction, StringComparison.Ordinal),
            $"no path ends with '/{OpenSearchServices.ListKeysAction}'"
        );

        Paths().ShouldNotContain(
            x => x.EndsWith("/listkeys", StringComparison.Ordinal),
            "the action was lower-cased on the way into the document, so every generated client would "
            + "POST to a path the gateway does not serve."
        );
    }

    [Fact]
    public void TheKubeLabelValueIsTheLowerCasedTypeAndIsLegalLabelSyntax() {
        // ⚠ The one place the casing MUST change, and the one place it must change EXACTLY. A `/` is
        // not a legal Kubernetes label value character, so the type is lower-cased with `/` replaced
        // by `_`. charts/managed/opensearch/conformance.yaml pins the same string independently.
        var value = KubeLabels.ResourceTypeValue(OpenSearchServices.Type);

        value.ShouldBe("cybercloud.search_services");
        LabelSyntax.ValidateValue(value, KubeLabels.ResourceType).IsSuccess.ShouldBeTrue(
            "the resource-type label value is not legal Kubernetes label syntax, so every object this "
            + "provider applies would be refused at admission rather than at build time."
        );
    }

    [Fact]
    public void TheDocumentHasPathsInItRatherThanBeingAnEmptyDocumentThatDiffsCleanly() {
        // ⚠ Failure class (e) at the emitter rather than at the registry. A provider whose Describe
        // chain broke emits a document with a `paths` object and nothing in it; the generator writes
        // it, diffs it against itself and exits 0. The count is the check, not the exit code.
        Paths().Length.ShouldBeGreaterThanOrEqualTo(
            3,
            "the emitted document carries fewer paths than a single resource type produces — a "
            + "collection, an item and the listKeys action. An emitter that produced nothing would "
            + "still round-trip and still exit 0."
        );
    }

    /// <summary>The document the generator would write for this provider alone.</summary>
    static JsonObject Document() {
        var registry = ProviderRegistry.Build([new SearchProvider()]);
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
        }
    }
}
