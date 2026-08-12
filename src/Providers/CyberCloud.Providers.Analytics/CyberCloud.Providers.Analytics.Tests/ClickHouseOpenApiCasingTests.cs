using CyberCloud.ResourceManager.Contracts.Generation;
using CyberCloud.ResourceManager.Registry;
using System.Collections.Immutable;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Analytics.Tests;

/// <summary>
///     Every property name of the ClickHouse type, spelled the same in all three places a caller meets
///     it.
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
///         are typed out by hand, and they are the fourth independent copy after docs/plan/12 § The
///         catalogue, <c>charts/managed/clickhouse/Chart.yaml</c>'s
///         <c>cybercloud.io/resource-type</c> and <c>charts/managed/clickhouse/conformance.yaml</c>.
///     </para>
///     <para>
///         ⚠ <b>This type's casing risk is concentrated in the type path itself and in one property.</b>
///         <c>clickhouseClusters</c> has a lower-case <c>c</c> in the middle of a word a human reads as
///         two — <c>ClickHouse</c> — so <c>clickHouseClusters</c> is the plausible typo, and it is
///         plausible in the <i>right</i> direction: it is how the C# class is spelled. And
///         <c>keeperNodes</c> is the one property name whose lower-cased form reads as ordinary
///         English.
///     </para>
///     <para>
///         The document is <b>emitted here, in-process</b>, rather than read off disk:
///         <c>openapi/2026-08-01.json</c> is written by a build step that runs <i>after</i>
///         compilation, so a test embedding it would compare this build's schema against the previous
///         build's document and go green on a stale file.
///     </para>
/// </remarks>
public sealed class ClickHouseOpenApiCasingTests {
    /// <summary>The spelling, written out. ⚠ <b>A literal, and it has to be.</b></summary>
    const string QualifiedType = "CyberCloud.Analytics/clickhouseClusters";

    [Fact]
    public void EveryDeclaredPropertyNameReachesTheOpenApiDocumentWithItsExactCasing() {
        var names = new HashSet<string>(StringComparer.Ordinal);
        Collect(Document(), names);

        foreach (var property in ClickHouseClusters.Schema2026.Properties) {
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

        foreach (var expected in new[] {
                     "keeperNodes", "clusterId", "httpEndpoint", "nativeEndpoint", "clusterName"
                 }) {
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
    public void TheApiSpellsShardsAndReplicasAndTheCustomResourceSpellsShardsCountAndReplicasCount() {
        // ⚠ TWO VOCABULARIES IN ONE PROVIDER, AND ONLY ONE OF THEM IS THE API. `shardsCount` and
        // `replicasCount` are Altinity's own field names on
        // spec.configuration.clusters[].layout and are written by ClickHouseClusters.ClickHouseJson;
        // `shards` and `replicas` are what a tenant sets. A document carrying the operator's spelling
        // would be a body every generated client sends to a key the write path refuses as unknown —
        // and the mistake reads as correct, because the value it carries genuinely is the shard count.
        var names = new HashSet<string>(StringComparer.Ordinal);
        Collect(Document(), names);

        names.ShouldContain("shards");
        names.ShouldContain("replicas");

        foreach (var operatorSpelling in new[] { "shardsCount", "replicasCount", "zookeeper", "layout" }) {
            names.ShouldNotContain(
                operatorSpelling,
                $"the operator's own field name '{operatorSpelling}' reached the API document. It is "
                + "the Altinity CR's spelling, not this platform's, and the two must not be the same "
                + "string in the same surface."
            );
        }
    }

    [Fact]
    public void TheTypePathSurvivesIntoThePathsExactly() {
        // ⚠ The path is what the gateway routes on, and `ResourceId.TryParsePath` compares the type
        // path through ResourceTypeName — which is case-INSENSITIVE. So a mis-cased document would
        // still route, and this test is not about routing: it is about what a generated client, a
        // portal breadcrumb and docs/plan/12's prose all copy.
        ClickHouseClusters.Type.ToString().ShouldBe(
            QualifiedType,
            "the declared type is spelled differently from docs/plan/12 § The catalogue and from "
            + "charts/managed/clickhouse/Chart.yaml's cybercloud.io/resource-type annotation."
        );

        var paths = Paths();

        paths.ShouldContain(
            x => x.Contains("/providers/" + QualifiedType + "/", StringComparison.Ordinal),
            "no path carries the provider namespace and type as docs/plan/12 spells them"
        );

        foreach (var path in paths.Where(
                     x => x.Contains("analytics/clickhouseclusters", StringComparison.OrdinalIgnoreCase)
                 )) {
            path.Contains(QualifiedType, StringComparison.Ordinal).ShouldBeTrue(
                $"'{path}' spells the type in a casing other than '{QualifiedType}'."
            );
        }

        // ⚠ THE PLAUSIBLE TYPO, NAMED. `clickHouseClusters` is how the C# class is spelled and is what
        // an author copying the type name from the file it lives in would write.
        paths.ShouldNotContain(
            x => x.Contains("clickHouseClusters", StringComparison.Ordinal),
            "the type path is spelled with a capital H, which is the C# class's casing rather than the "
            + "catalogue's."
        );
    }

    [Fact]
    public void TheActionIsRoutedUnderItsDeclaredCasingRatherThanLowerCased() {
        Paths().ShouldContain(
            x => x.EndsWith("/" + ClickHouseClusters.ListKeysAction, StringComparison.Ordinal),
            $"no path ends with '/{ClickHouseClusters.ListKeysAction}'"
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
        // by `_`. charts/managed/clickhouse/conformance.yaml pins the same string independently.
        var value = KubeLabels.ResourceTypeValue(ClickHouseClusters.Type);

        value.ShouldBe("cybercloud.analytics_clickhouseclusters");
        LabelSyntax.ValidateValue(value, KubeLabels.ResourceType).IsSuccess.ShouldBeTrue(
            "the resource-type label value is not legal Kubernetes label syntax, so every object this "
            + "provider applies would be refused at admission rather than at build time."
        );
    }

    /// <summary>The document the generator would write for this provider alone.</summary>
    static JsonObject Document() {
        var registry = ProviderRegistry.Build([new AnalyticsProvider()]);
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
