using CyberCloud.ResourceManager.Contracts.Generation;
using CyberCloud.ResourceManager.Registry;
using System.Collections.Immutable;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Messaging.Tests;

/// <summary>
///     Every property name, spelled the same in all three places a caller meets it.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>One character of casing.</b> A <c>resourcegroup</c>/<c>resourceGroup</c> mismatch was
///         once failing every create in the platform and surfaced as a <c>404</c> whose reason was
///         only in a log line — nothing compared the two spellings, because both were plausible and
///         each was written in a different file.
///     </para>
///     <para>
///         ⚠ <b>This type's exposure is different from the PostgreSQL provider's and needs its own
///         test rather than inheriting the argument.</b> That one's risk is the <i>namespace</i>:
///         <c>CyberCloud.DBforPostgreSQL</c> has three internal case changes. This namespace,
///         <c>CyberCloud.Messaging</c>, has none — and the risk moved to the <b>type path</b>.
///         <c>kafkaClusters</c> is camel-cased where <c>servers</c> and <c>widgets</c> are one lower
///         word, so it is the first type in the tree whose path segment can be mis-cased at all:
///         <c>kafkaclusters</c>, <c>KafkaClusters</c> and <c>kafka-clusters</c> are each what one of
///         the four generated surfaces might plausibly have written, and only one of them routes.
///     </para>
///     <para>
///         The document is <b>emitted here, in-process</b>, rather than read off disk:
///         <c>openapi/2026-08-01.json</c> is written by a build step that runs <i>after</i>
///         compilation, so a test embedding it would compare this build's schema against the previous
///         build's document and go green on a stale file.
///     </para>
/// </remarks>
public sealed class KafkaOpenApiCasingTests {
    [Fact]
    public void EveryDeclaredPropertyNameReachesTheOpenApiDocumentWithItsExactCasing() {
        var names = new HashSet<string>(StringComparer.Ordinal);
        Collect(Document(), names);

        foreach (var property in KafkaClusters.Schema2026.Properties) {
            names.ShouldContain(
                property.Name,
                $"'{property.JsonPointer}' does not appear in the emitted OpenAPI document under that "
                + "exact spelling. A property whose document name differs from its registry name by "
                + "one character is a property every generated client sends to the wrong key, and the "
                + "write path then refuses it as unknown."
            );
        }
    }

    /// <summary>
    ///     The spelling, written out. ⚠ <b>A literal, and it has to be.</b>
    /// </summary>
    /// <remarks>
    ///     The first version of the test below built the expected path from
    ///     <c>KafkaClusters.ProviderNamespace</c> and <c>KafkaClusters.TypePath</c> — the same two
    ///     constants the emitter reads. Changing <c>TypePath</c> to <c>kafkaclusters</c> and running
    ///     the suite left it <b>green</b>, along with every other test in this assembly: two things
    ///     derived from one constant agree however that constant is spelled. A casing test whose
    ///     expectation moves with the thing it is testing cannot fail, which is worse than no test
    ///     because it reads as coverage.
    ///     <para>
    ///         docs/plan/12 § The catalogue, <c>charts/managed/kafka/Chart.yaml</c>'s
    ///         <c>cybercloud.io/resource-type</c> annotation, and this line are the three places the
    ///         string is written independently. <c>./build.sh Charts</c> compares the middle one to
    ///         the registry; this is the one that fails in a second rather than in a build.
    ///     </para>
    /// </remarks>
    const string QualifiedType = "CyberCloud.Messaging/kafkaClusters";

    [Fact]
    public void TheCamelCasedTypePathSurvivesIntoThePathsExactly() {
        // ⚠ The path is what the gateway routes on, and `ResourceId.TryParsePath` compares the type
        // path through ResourceTypeName — which is case-INSENSITIVE. So a mis-cased document would
        // still route, and this test is not about routing: it is about what a generated client, a
        // portal breadcrumb and docs/plan/12's prose all copy. Three surfaces are generated FROM this
        // document, so a `kafkaclusters` here is `kafkaclusters` in the CLI, the SDK and the form.
        KafkaClusters.Type.ToString().ShouldBe(
            QualifiedType,
            "the declared type is spelled differently from docs/plan/12 § The catalogue and from "
            + "charts/managed/kafka/Chart.yaml's cybercloud.io/resource-type annotation."
        );

        var paths = Paths();

        paths.ShouldContain(
            x => x.Contains("/providers/" + QualifiedType + "/", StringComparison.Ordinal),
            "no path carries the provider namespace and type as the catalogue spells them"
        );

        foreach (var path in paths.Where(
                     x => x.Contains("kafkaclusters", StringComparison.OrdinalIgnoreCase)
                 )) {
            path.Contains(QualifiedType, StringComparison.Ordinal).ShouldBeTrue(
                $"'{path}' spells the type in a casing other than '{QualifiedType}'."
            );
        }
    }

    [Fact]
    public void TheActionIsRoutedUnderItsDeclaredCasingRatherThanLowerCased() {
        // ⚠ `listKeys` is one of the two camel-cased URL segments this type has, and the only one
        // that is not the type itself. ResourceManagerService matches an action name
        // case-insensitively, but the document is what a generated client copies.
        Paths().ShouldContain(
            x => x.EndsWith("/" + KafkaClusters.ListKeysAction, StringComparison.Ordinal),
            $"no path ends with '/{KafkaClusters.ListKeysAction}'"
        );
    }

    [Fact]
    public void TheKubeLabelValueIsTheLowerCasedTypeAndIsLegalLabelSyntax() {
        // ⚠ The one place the casing MUST change, and the one place it must change EXACTLY. A `/` is
        // not a legal Kubernetes label value character, so the type is lower-cased with `/` replaced
        // by `_`. charts/managed/kafka/conformance.yaml pins the same string; the two are compared by
        // a human today and by the conformance-manifest reader when one exists, so a test that only
        // asserted "it is lower case" would let the two drift apart while both stayed plausible.
        var value = KubeLabels.ResourceTypeValue(KafkaClusters.Type);

        value.ShouldBe("cybercloud.messaging_kafkaclusters");
        LabelSyntax.ValidateValue(value, KubeLabels.ResourceType).IsSuccess.ShouldBeTrue(
            "the resource-type label value is not legal Kubernetes label syntax, so every object this "
            + "provider applies would be refused at admission rather than at build time."
        );
    }

    /// <summary>The document the generator would write for this provider alone.</summary>
    /// <remarks>
    ///     ⚠ <b>This provider alone.</b> Rule 2 of docs/plan/03 § Assembly graph rules is "no
    ///     <c>Providers.*</c> assembly references another <c>Providers.*</c> assembly, not even
    ///     <c>.Contracts</c>", and a test project taking such a reference would put the edge in the
    ///     graph the gate inspects. That all three namespaces share <c>openapi/2026-08-01.json</c>
    ///     without any of them swallowing another is <c>./build.sh Generate</c>'s assertion,
    ///     byte-for-byte, against the checked-in file.
    /// </remarks>
    static JsonObject Document() {
        var registry = ProviderRegistry.Build([new MessagingProvider()]);
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
