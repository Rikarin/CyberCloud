using CyberCloud.ResourceManager.Contracts.Generation;
using CyberCloud.ResourceManager.Registry;
using System.Collections.Immutable;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Messaging.Tests;

/// <summary>
///     Every property name of the NATS type, spelled the same in all three places a caller meets it.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>One character of casing.</b> A <c>resourcegroup</c>/<c>resourceGroup</c> mismatch was
///         once failing every create in the platform and surfaced as a <c>404</c> whose reason was
///         only in a log line.
///     </para>
///     <para>
///         ⚠ <b>Every expectation below is a LITERAL, and the sibling Kafka file records why in the
///         strongest possible terms: an earlier version of it built the expected path from the same
///         two constants the emitter reads, and re-casing the constant left the whole suite
///         green.</b> Two things derived from one constant agree however that constant is spelled.
///         So the strings here are typed out by hand, and they are the fourth independent copy after
///         docs/plan/12 § The catalogue, <c>charts/managed/nats/Chart.yaml</c>'s
///         <c>cybercloud.io/resource-type</c> and <c>charts/managed/nats/conformance.yaml</c>.
///     </para>
///     <para>
///         ⚠ <b>This type carries a risk the Kafka one does not: a property name with an INTERNAL
///         capital in the middle of a nested path.</b> <c>maxMemoryStore</c>, <c>maxPayload</c>,
///         <c>maxConnections</c>, <c>leafNodes</c> and <c>allowedCidrs</c> are five chances to write
///         <c>maxmemorystore</c> or <c>leafnodes</c> — and <c>leafNodes</c> is the dangerous one,
///         because <c>leafnodes</c> is the correct spelling of the NATS <i>configuration block</i>
///         four lines away in the same provider. The two are different vocabularies and only one of
///         them is the API.
///     </para>
///     <para>
///         The document is <b>emitted here, in-process</b>, rather than read off disk:
///         <c>openapi/2026-08-01.json</c> is written by a build step that runs <i>after</i>
///         compilation, so a test embedding it would compare this build's schema against the previous
///         build's document and go green on a stale file.
///     </para>
/// </remarks>
public sealed class NatsOpenApiCasingTests {
    /// <summary>The spelling, written out. ⚠ <b>A literal, and it has to be.</b></summary>
    const string QualifiedType = "CyberCloud.Messaging/natsClusters";

    [Fact]
    public void EveryDeclaredPropertyNameReachesTheOpenApiDocumentWithItsExactCasing() {
        var names = new HashSet<string>(StringComparer.Ordinal);
        Collect(Document(), names);

        foreach (var property in NatsClusters.Schema2026.Properties) {
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
    public void TheFiveCamelCasedPropertyNamesAreSpelledOutHereRatherThanDerived() {
        // ⚠ THE LITERALS ARE THE TEST. Reading these off Schema2026 would compare the schema to
        // itself. Each of the five below is a name whose lower-cased spelling is plausible enough
        // that a hand-written portal form or a hand-written CLI flag would carry it, and
        // `leafNodes` is the one where the WRONG spelling is also correct four lines away — NATS'
        // own configuration block is `leafnodes`, all lower case, and it is written by this very
        // provider in nats.conf.
        var names = new HashSet<string>(StringComparer.Ordinal);
        Collect(Document(), names);

        foreach (var expected in new[] {
                     "maxMemoryStore", "maxPayload", "maxConnections", "leafNodes", "allowedCidrs"
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
    public void TheCamelCasedTypePathSurvivesIntoThePathsExactly() {
        // ⚠ The path is what the gateway routes on, and `ResourceId.TryParsePath` compares the type
        // path through ResourceTypeName — which is case-INSENSITIVE. So a mis-cased document would
        // still route, and this test is not about routing: it is about what a generated client, a
        // portal breadcrumb and docs/plan/12's prose all copy.
        NatsClusters.Type.ToString().ShouldBe(
            QualifiedType,
            "the declared type is spelled differently from docs/plan/12 § The catalogue and from "
            + "charts/managed/nats/Chart.yaml's cybercloud.io/resource-type annotation."
        );

        var paths = Paths();

        paths.ShouldContain(
            x => x.Contains("/providers/" + QualifiedType + "/", StringComparison.Ordinal),
            "no path carries the provider namespace and type as the catalogue spells them"
        );

        foreach (var path in paths.Where(
                     x => x.Contains("natsclusters", StringComparison.OrdinalIgnoreCase)
                 )) {
            path.Contains(QualifiedType, StringComparison.Ordinal).ShouldBeTrue(
                $"'{path}' spells the type in a casing other than '{QualifiedType}'."
            );
        }
    }

    [Fact]
    public void BothTypesInThisNamespaceKeepTheirOwnPathsAndNeitherSwallowsTheOther() {
        // ⚠ FAILURE CLASS (d), AT THE DOCUMENT. This is the first provider namespace with two types
        // in it, and the emitters that disambiguate by name — SdkEmitter's three-tier ladder,
        // CliEmitter's command map — all read this document. A path group that lost one of the two
        // would take the CLI verb and the SDK client with it, silently.
        var paths = Paths();

        paths.ShouldContain(x => x.Contains("/providers/" + QualifiedType + "/", StringComparison.Ordinal));
        paths.ShouldContain(
            x => x.Contains("/providers/CyberCloud.Messaging/kafkaClusters/", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void TheActionIsRoutedUnderItsDeclaredCasingRatherThanLowerCased() {
        Paths().ShouldContain(
            x => x.EndsWith("/" + NatsClusters.ListKeysAction, StringComparison.Ordinal),
            $"no path ends with '/{NatsClusters.ListKeysAction}'"
        );
    }

    [Fact]
    public void TheKubeLabelValueIsTheLowerCasedTypeAndIsLegalLabelSyntax() {
        // ⚠ The one place the casing MUST change, and the one place it must change EXACTLY. A `/` is
        // not a legal Kubernetes label value character, so the type is lower-cased with `/` replaced
        // by `_`. charts/managed/nats/conformance.yaml pins the same string independently.
        var value = KubeLabels.ResourceTypeValue(NatsClusters.Type);

        value.ShouldBe("cybercloud.messaging_natsclusters");
        LabelSyntax.ValidateValue(value, KubeLabels.ResourceType).IsSuccess.ShouldBeTrue(
            "the resource-type label value is not legal Kubernetes label syntax, so every object this "
            + "provider applies would be refused at admission rather than at build time."
        );
    }

    /// <summary>The document the generator would write for this provider alone.</summary>
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
