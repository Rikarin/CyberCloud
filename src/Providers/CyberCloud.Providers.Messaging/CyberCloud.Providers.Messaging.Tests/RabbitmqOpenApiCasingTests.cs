using CyberCloud.ResourceManager.Contracts.Generation;
using CyberCloud.ResourceManager.Registry;
using System.Collections.Immutable;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Messaging.Tests;

/// <summary>
///     Failure class (a): every property name of the RabbitMQ type, spelled the same in all three
///     places a caller meets it.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>One character of casing.</b> A <c>resourcegroup</c>/<c>resourceGroup</c> mismatch was
///         once failing every create in the platform and surfaced as a <c>404</c> whose reason was
///         only in a log line.
///     </para>
///     <para>
///         ⚠ <b>Every expectation below is a LITERAL, and the two sibling files record why in the
///         strongest possible terms: an earlier version of the Kafka one built the expected path from
///         the same two constants the emitter reads, and re-casing the constant left the whole suite
///         green.</b> Two things derived from one constant agree however that constant is spelled. So
///         the strings here are typed out by hand, and they are the fourth independent copy after
///         docs/plan/12 § The catalogue, <c>charts/managed/rabbitmq/Chart.yaml</c>'s
///         <c>cybercloud.io/resource-type</c> and <c>charts/managed/rabbitmq/conformance.yaml</c>.
///     </para>
///     <para>
///         ⚠ <b>THIS TYPE CARRIES A CASING RISK NEITHER SIBLING HAS, AND IT IS IN THE TYPE PATH
///         ITSELF.</b> The product is written <c>RabbitMQ</c> everywhere — in this type's own
///         <c>Display</c> name, in docs/plan/12's prose, in the chart description — and the type path
///         is <c>rabbitmqClusters</c>, all lower case through the acronym. <c>rabbitMqClusters</c> or
///         <c>rabbitMQClusters</c> would compile, would route (<c>ResourceTypeName</c> compares
///         case-insensitively), and would give <c>CliEmitter.CommandOf</c> — which kebab-cases on
///         case transitions — the verb <c>rabbit-mq-clusters</c>. ⚠ And a <b>third</b> spelling is in
///         play in the same file: the Kubernetes kind is <c>RabbitmqCluster</c>, capital R and lower
///         <c>mq</c>, which is neither the product's spelling nor the type path's.
///     </para>
///     <para>
///         The document is <b>emitted here, in-process</b>, rather than read off disk:
///         <c>openapi/2026-08-01.json</c> is written by a build step that runs <i>after</i>
///         compilation, so a test embedding it would compare this build's schema against the previous
///         build's document and go green on a stale file.
///     </para>
/// </remarks>
public sealed class RabbitmqOpenApiCasingTests {
    /// <summary>The spelling, written out. ⚠ <b>A literal, and it has to be.</b></summary>
    const string QualifiedType = "CyberCloud.Messaging/rabbitmqClusters";

    [Fact]
    public void EveryDeclaredPropertyNameReachesTheOpenApiDocumentWithItsExactCasing() {
        var names = new HashSet<string>(StringComparer.Ordinal);
        Collect(Document(), names);

        foreach (var property in RabbitmqClusters.Schema2026.Properties) {
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
    public void TheThreeCamelCasedPropertyNamesAreSpelledOutHereRatherThanDerived() {
        // ⚠ THE LITERALS ARE THE TEST. Reading these off Schema2026 would compare the schema to
        // itself. Each is a name whose lower-cased spelling is plausible enough that a hand-written
        // portal form or CLI flag would carry it — and `defaultType` is the dangerous one, because
        // the WRONG spelling is also correct one line away: the rabbitmq.conf key this property
        // becomes is `default_queue_type`, and this provider writes that string itself.
        var names = new HashSet<string>(StringComparer.Ordinal);
        Collect(Document(), names);

        foreach (var expected in new[] { "defaultType", "maxMessageSize", "clusterId" }) {
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
    public void TheTypePathIsAllLowerCaseThroughTheAcronymAndTheKindIsNot() {
        // ⚠ THREE SPELLINGS OF ONE WORD, PINNED AGAINST EACH OTHER. The product is `RabbitMQ`, the
        // type path is `rabbitmqClusters`, and the Kubernetes kind is `RabbitmqCluster`. Each is
        // correct in its own place and none is derivable from another, so all three are literals.
        RabbitmqClusters.Type.ToString().ShouldBe(
            QualifiedType,
            "the declared type is spelled differently from docs/plan/12 § The catalogue and from "
            + "charts/managed/rabbitmq/Chart.yaml's cybercloud.io/resource-type annotation."
        );

        RabbitmqClusters.TypePath.ShouldBe("rabbitmqClusters");

        // ⚠ Capital R, lower-case `mq`. Verified against the CRD's spec.names.kind. A kind the API
        // server does not know is a 404 at apply time, per object, with the reason in the response
        // body rather than anywhere this repository looks.
        RabbitmqClusters.ClusterKind.Kind.ShouldBe("RabbitmqCluster");
        RabbitmqClusters.ClusterKind.Plural.ShouldBe("rabbitmqclusters");
        RabbitmqClusters.ClusterKind.Group.ShouldBe("rabbitmq.com");
        RabbitmqClusters.ClusterKind.Version.ShouldBe("v1beta1");

        var paths = Paths();

        paths.ShouldContain(
            x => x.Contains("/providers/" + QualifiedType + "/", StringComparison.Ordinal),
            "no path carries the provider namespace and type as the catalogue spells them"
        );

        foreach (var path in paths.Where(
                     x => x.Contains("rabbitmqclusters", StringComparison.OrdinalIgnoreCase)
                 )) {
            path.Contains(QualifiedType, StringComparison.Ordinal).ShouldBeTrue(
                $"'{path}' spells the type in a casing other than '{QualifiedType}'."
            );
        }
    }

    [Fact]
    public void AllThreeTypesInThisNamespaceKeepTheirOwnPathsAndNoneSwallowsTheOthers() {
        // ⚠ FAILURE CLASS (e), AT THE DOCUMENT. This is the first provider namespace with THREE types
        // in it, and every emitter that disambiguates by name — SdkEmitter's three-tier ladder,
        // CliEmitter's command map — reads this document. A path group that lost one of the three
        // would take the CLI verb and the SDK client with it, silently.
        var paths = Paths();

        foreach (var qualified in new[] {
                     "CyberCloud.Messaging/rabbitmqClusters",
                     "CyberCloud.Messaging/kafkaClusters",
                     "CyberCloud.Messaging/natsClusters"
                 }) {
            paths.ShouldContain(
                x => x.Contains("/providers/" + qualified + "/", StringComparison.Ordinal),
                qualified
            );
        }
    }

    [Fact]
    public void TheActionIsRoutedUnderItsDeclaredCasingRatherThanLowerCased() {
        Paths().ShouldContain(
            x => x.EndsWith("/" + RabbitmqClusters.ListKeysAction, StringComparison.Ordinal),
            $"no path ends with '/{RabbitmqClusters.ListKeysAction}'"
        );
    }

    [Fact]
    public void TheKubeLabelValueIsTheLowerCasedTypeAndIsLegalLabelSyntax() {
        // ⚠ The one place the casing MUST change, and the one place it must change EXACTLY. A `/` is
        // not a legal Kubernetes label value character, so the type is lower-cased with `/` replaced
        // by `_`. charts/managed/rabbitmq/conformance.yaml pins the same string independently, and
        // the cluster-backed suite proves a real API server accepts it.
        var value = KubeLabels.ResourceTypeValue(RabbitmqClusters.Type);

        value.ShouldBe("cybercloud.messaging_rabbitmqclusters");
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
