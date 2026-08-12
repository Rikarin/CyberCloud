using CyberCloud.ResourceManager.Contracts.Generation;
using CyberCloud.ResourceManager.Registry;
using System.Collections.Immutable;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.ContainerService.Tests;

/// <summary>
///     Every name of these two types, spelled the same in all three places a caller meets it.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>ONE CHARACTER OF CASING.</b> A <c>resourcegroup</c>/<c>resourceGroup</c> mismatch was
///         once failing every create in the platform and surfaced as a <c>404</c> whose reason was only
///         in a log line.
///     </para>
///     <para>
///         ⚠ <b>EVERY EXPECTATION BELOW IS A LITERAL, AND A PREVIOUS PROVIDER'S CASING SABOTAGE STAYED
///         GREEN BECAUSE IT WAS NOT.</b> That version built the expected path from the same constants
///         the emitter reads, so re-casing a constant left the whole suite green — two things derived
///         from one constant agree however that constant is spelled.
///     </para>
/// </remarks>
public sealed class ManagedClusterOpenApiCasingTests {
    const string QualifiedCluster = "CyberCloud.ContainerService/managedClusters";
    const string QualifiedPool = "CyberCloud.ContainerService/managedClusters/agentPools";

    [Fact]
    public void EveryDeclaredPropertyNameReachesTheOpenApiDocumentWithItsExactCasing() {
        var names = new HashSet<string>(StringComparer.Ordinal);
        Collect(Document(), names);

        foreach (var property in ManagedClusters.Schema2026.Properties.Concat(AgentPools.Schema2026.Properties)) {
            names.ShouldContain(
                property.Name,
                $"'{property.JsonPointer}' does not appear in the emitted OpenAPI document under that "
                + "exact spelling. A property whose document name differs from its registry name by one "
                + "character is a property every generated client sends to the wrong key, and the write "
                + "path then refuses it as unknown."
            );
        }
    }

    [Fact]
    public void TheCamelCasedNamesAreSpelledOutHereRatherThanDerived() {
        // ⚠ THE LITERALS ARE THE TEST. Reading these off the schemas would compare each to itself.
        // Every name here has a capital in it, deliberately: a lower-cased name compared against its
        // own ToLowerInvariant() is a test that can only fail.
        var names = new HashSet<string>(StringComparer.Ordinal);
        Collect(Document(), names);

        foreach (var expected in new[] {
                     "clusterId",
                     "controlPlane",
                     "podCidr",
                     "serviceCidr",
                     "osDiskSize",
                     "minCount",
                     "maxCount",
                     "maxSurge",
                     "maxUnavailable",
                     "apiServerEndpoint",
                     "expiresAt"
                 }) {
            expected.ShouldNotBe(expected.ToLowerInvariant(), "this list is for camelCased names only");

            names.ShouldContain(expected, $"'{expected}' is not in the document under that casing");

            names.ShouldNotContain(
                expected.ToLowerInvariant(),
                $"the document carries '{expected.ToLowerInvariant()}' as well as '{expected}'. Two "
                + "spellings of one property is a body half the generated clients send to the wrong key."
            );
        }

        foreach (var expected in new[] { "version", "replicas", "count", "size", "kubeconfig" }) {
            names.ShouldContain(expected);
        }
    }

    [Fact]
    public void TheApiSpellsCountAndTheCustomResourceSpellsReplicas() {
        // ⚠ TWO VOCABULARIES, AND ONLY ONE OF THEM IS THE API. A MachineDeployment's field is
        // `spec.replicas`; the resource body says `count`, because "replicas" in a Kubernetes context
        // means copies of one thing and a node pool's machines are not copies of each other in the way
        // a Deployment's pods are. ⚠ The cluster's own control plane DOES say `replicas`, because there
        // they genuinely are copies — so the document carries both words and they mean different things.
        var names = new HashSet<string>(StringComparer.Ordinal);
        Collect(Document(), names);

        names.ShouldContain("count");
        names.ShouldContain("replicas");

        ManagedClusters.Schema2026.Declares("/properties/controlPlane/replicas").ShouldBeTrue();
        AgentPools.Schema2026.Declares("/properties/count").ShouldBeTrue();
        AgentPools.Schema2026.Declares("/properties/replicas").ShouldBeFalse();
    }

    [Fact]
    public void TheTypePathsSurviveIntoThePathsExactlyAndTheChildInterleaves() {
        ManagedClusters.Type.ToString().ShouldBe(QualifiedCluster);
        AgentPools.Type.ToString().ShouldBe(QualifiedPool);

        var paths = Paths();

        paths.ShouldContain(
            x => x.EndsWith(
                "/providers/CyberCloud.ContainerService/managedClusters/{resourceName}",
                StringComparison.Ordinal
            ),
            "no path addresses a managed cluster. Every path in the document: " + string.Join(", ", paths)
        );

        // ⚠ THE WHOLE SEGMENT, WRITTEN OUT. `Contains("agentPools")` would pass on a flattened
        // `/managedClusters/agentPools/{name}` — a legal-looking path that addresses no parent and
        // whose ReBAC edge would name the resource group.
        paths.ShouldContain(
            x => x.EndsWith(
                "/providers/CyberCloud.ContainerService/managedClusters/{managedClustersName}/agentPools/{resourceName}",
                StringComparison.Ordinal
            ),
            "no path interleaves the cluster's name between the two type segments. Every path in the "
            + "document: " + string.Join(", ", paths)
        );

        paths.ShouldNotContain(
            x => x.EndsWith(
                "/providers/CyberCloud.ContainerService/managedClusters/agentPools/{resourceName}",
                StringComparison.Ordinal
            ),
            "the type path was flattened on the way into the document, so a node pool is addressed with "
            + "no cluster in its URL at all."
        );
    }

    [Fact]
    public void BothActionsAreRoutedUnderTheirDeclaredCasingRatherThanLowerCased() {
        var paths = Paths();

        paths.ShouldContain(
            x => x.EndsWith("/managedClusters/{resourceName}/listCredentials", StringComparison.Ordinal),
            "listCredentials is not routed under a cluster's own address, or was lower-cased"
        );

        // ⚠ And the pool's action hangs off the CHILD's address. An action routed one level up would be
        // a POST that names no pool.
        paths.ShouldContain(
            x => x.EndsWith("/agentPools/{resourceName}/upgradeNodeImage", StringComparison.Ordinal),
            "upgradeNodeImage is not routed under a node pool's own address"
        );
    }

    [Fact]
    public void TheKubeLabelValuesFoldEverySlashAndAreLegalLabelSyntax() {
        // ⚠ The child has TWO slashes to fold and a single-replacement implementation would pass every
        // top-level type's test. A `/` is not a legal Kubernetes label VALUE character, and the failure
        // arrives at admission, per object, rather than at build time.
        KubeLabels.ResourceTypeValue(ManagedClusters.Type)
            .ShouldBe("cybercloud.containerservice_managedclusters");

        var child = KubeLabels.ResourceTypeValue(AgentPools.Type);

        child.ShouldBe("cybercloud.containerservice_managedclusters_agentpools");

        child.ShouldNotContain(
            "/",
            Case.Sensitive,
            "the resource-type label value still carries a slash, so only the first of the child type's "
            + "two was folded."
        );

        foreach (var value in new[] { KubeLabels.ResourceTypeValue(ManagedClusters.Type), child }) {
            LabelSyntax.ValidateValue(value, KubeLabels.ResourceType).IsSuccess.ShouldBeTrue(value);
        }
    }

    /// <summary>The document the generator would write for this provider alone.</summary>
    static JsonObject Document() {
        var registry = ProviderRegistry.Build([new ContainerServiceProvider()]);
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
