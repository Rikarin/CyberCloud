using CyberCloud.Conformance;
using CyberCloud.Core.Resources;
using CyberCloud.Conformance.Harness;
using CyberCloud.Providers.ContainerRegistry.Contracts;
using Shouldly;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.ContainerRegistry.Conformance;

/// <summary>
///     <c>CyberCloud.ContainerRegistry/registries</c>, registered into the shared provider suite.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>One case object and two class declarations, for the twelfth family in a row — over the
///         largest rendered object set in the catalogue.</b> The case's <c>Objects</c> member returns
///         <b>fifteen</b> references where the widest before it returned five, and
///         <c>test/CyberCloud.Conformance</c> needed no change. That is the claim the shape has been
///         making since the third provider, tested at three times the previous width.
///     </para>
///     <para>
///         ⚠ <b>AND IT IS THE FIRST CASE WHOSE TYPE DECLARES A RECOVERY WINDOW, WHICH CHANGES WHAT ONE
///         ASSERTION MEANS RATHER THAN ADDING ONE.</b>
///         <c>ProviderConformanceTests.DeleteTearsDownTheDataPlaneAndTheResourceIsGone</c> asserts that
///         a <c>DELETE</c> removes every object in <c>Objects</c> and that the resource is no longer
///         addressable. Both still hold — a soft delete tears the data plane down and parks the
///         <i>record</i> — so the suite passes unchanged, and what it now proves is strictly less than
///         its name suggests: <b>gone from its address</b> rather than <b>gone</b>. The name being held,
///         the quota staying committed and the ReBAC parent moving to the subscription are
///         <c>SoftDeletePathTests</c>' assertions in <c>CyberCloud.ResourceManager.Tests</c>, over a
///         test provider, and nothing in a per-provider suite reaches them.
///     </para>
///     <para>
///         ⚠ <b>The harness supplies a vault, and without one this whole case is red.</b>
///         <c>ProviderTestCluster</c> registers an <c>InMemorySecretVault</c> as both
///         <c>ISecretResolver</c> and <c>ISecretWriter</c>, shared between the silo and the client,
///         because the mint happens inside the silo and <c>listCredentials</c> resolves outside it. It
///         implements mint-once for real, so the idempotence assertions still measure the reconciler.
///     </para>
///     <para>
///         ⚠ <b>What this case cannot exercise, said out loud.</b> The suite asserts that the objects
///         this provider applied are in the cluster and carry what the body asked for. It does not
///         start a Harbor: no image is pulled, no database migration runs, no <c>docker login</c> is
///         attempted, and the fifteen documents' <i>contents</i> are checked only against
///         <see cref="ContainerRegistries.Matches" />, which is this provider's own opinion of them.
///         <c>charts/managed/harbor/conformance.yaml § owed</c> is where the assertions that would need
///         a running Harbor live.
///     </para>
/// </remarks>
public sealed class ContainerRegistryCase : IProviderCaseSource {
    /// <inheritdoc />
    public static ProviderConformanceCase ProviderCase { get; } =
        new() {
            DisplayName = "CyberCloud.ContainerRegistry/registries",
            CreateProvider = () => new ContainerRegistryProvider(),
            ReconcilerType = typeof(ContainerRegistryReconciler),
            CreateReconciler = clock => new ContainerRegistryReconciler(clock),
            Type = ContainerRegistries.Type,
            ApiVersion = ContainerRegistries.V2026,
            Body = cluster => ContainerRegistries.Body(cluster),
            // ⚠ Changes `replicas`, which the rendered objects carry in THREE places — core's, the
            // portal's and the job service's `spec.replicas` — and which the meters read as well. A
            // body that differed only where the reconciler ignores it (`purgeProtection`, say, which
            // reaches no object at all) would pass the update test while proving the update never left
            // the grain.
            ChangedBody = cluster => ContainerRegistries.Body(cluster, replicas: 3),
            // Drops the required `/properties/storage/size`.
            // ⚠ Built from a valid body with one required property removed rather than hand-written: a
            // hand-written invalid body drifts out of date the day the schema gains a property and then
            // tests "invalid for the wrong reason" while still going green.
            InvalidBody = cluster => WithoutStorageSize(ContainerRegistries.Body(cluster)),
            InvalidBodyTarget = "/properties/storage/size",
            ActionName = ContainerRegistries.ListCredentialsAction,
            // ⚠ ALL FIFTEEN, IN APPLY ORDER, AND THE ORDER IS PART OF THE ASSERTION. The suite's delete
            // assertion only proves what is listed here is gone, and a credentials Secret surviving its
            // registry is exactly the thing that must not — so the object applied first and deleted
            // last is listed first.
            Objects = (id, ns) => [
                ContainerRegistries.CredentialsSecretRef(ns, id.Name),
                ContainerRegistries.ConfigMapRef(ns, id.Name),
                ContainerRegistries.DatabaseServiceRef(ns, id.Name),
                ContainerRegistries.RedisServiceRef(ns, id.Name),
                ContainerRegistries.RegistryServiceRef(ns, id.Name),
                ContainerRegistries.CoreServiceRef(ns, id.Name),
                ContainerRegistries.PortalServiceRef(ns, id.Name),
                ContainerRegistries.JobServiceServiceRef(ns, id.Name),
                ContainerRegistries.DatabaseSetRef(ns, id.Name),
                ContainerRegistries.RedisSetRef(ns, id.Name),
                ContainerRegistries.RegistrySetRef(ns, id.Name),
                ContainerRegistries.CoreDeploymentRef(ns, id.Name),
                ContainerRegistries.PortalDeploymentRef(ns, id.Name),
                ContainerRegistries.JobServiceDeploymentRef(ns, id.Name),
                ContainerRegistries.PodMonitorRef(ns, id.Name)
            ],
            // This platform mints or computes everything this type's actions hand back, so no operator
            // writes an object any action reads. Stated rather than defaulted — see
            // ProviderConformanceCase.OperatorWritten.
            OperatorWritten = static (_, _) => [],
            ObjectMatchesDesired = (objectJson, desiredJson) => {
                using var desired = JsonDocument.Parse(desiredJson);
                return ContainerRegistries.Matches(objectJson, desired.RootElement);
            }
        };

    /// <summary>A valid body with the required image-storage size removed.</summary>
    /// <param name="body">A valid body.</param>
    static string WithoutStorageSize(string body) {
        var node = JsonNode.Parse(body)!.AsObject();
        node["properties"]!.AsObject()["storage"]!.AsObject().Remove("size");
        return node.ToJsonString();
    }
}

/// <summary>The shared suite, run against the managed container-registry provider.</summary>
/// <param name="cluster">The harness.</param>
public sealed class ContainerRegistryConformance(ProviderTestCluster<ContainerRegistryCase> cluster)
    : ProviderConformanceTests<ContainerRegistryCase>(cluster),
        IClassFixture<ProviderTestCluster<ContainerRegistryCase>>;

/// <summary>The container-backed half, skipped loudly, against the managed container-registry provider.</summary>
public sealed class ContainerRegistryClusterBackedConformance()
    : ClusterBackedConformanceTests(ContainerRegistryCase.ProviderCase);

/// <summary>
///     What this provider's registration into the shared suite is <b>shaped</b> like.
/// </summary>
/// <remarks>
///     ⚠ Every assertion here is about the CASE, not about the provider. It lives in this project
///     rather than in <c>CyberCloud.Providers.ContainerRegistry.Tests</c> because that project
///     deliberately does not reference this one.
/// </remarks>
public sealed class ContainerRegistrySuiteShapeTests {
    [Fact]
    public void TheCaseListsEveryObjectTheReconcilerApplies() {
        // ⚠ THE ASSERTION A FIFTEEN-OBJECT TYPE MOST NEEDS, and the one nothing in the shared suite
        // can make. `ProviderConformanceCase.Objects` is a hand-written list; the reconciler has its
        // own. The suite reads only the first, so an object the reconciler applies and the case does
        // not list is an object nothing ever checks — it is applied, never read back by the suite,
        // never asserted gone after a delete, and its absence from a torn-down namespace would be
        // invisible.
        //
        // ⚠ Reached through ContainerRegistryReconciler.Targets, which is the same function the
        // reconciler's own read-back loop walks, so this compares the case against the shipping list
        // rather than against a second copy of it.
        using var body = JsonDocument.Parse(ContainerRegistries.Body(ClusterId));

        var applied = ContainerRegistryReconciler
            .Targets("ns", "reg", body.RootElement)
            .Select(x => x.ToString())
            .ToArray();

        var listed = ContainerRegistryCase.ProviderCase
            .Objects(Address, "ns")
            .Select(x => x.ToString())
            .ToArray();

        listed.ShouldBe(
            applied,
            "the conformance case and the reconciler disagree about which objects a registry owns. "
            + "The suite asserts against the case's list only, so an object in one and not the other "
            + "is either never checked or never applied."
        );
    }

    static readonly Guid ClusterId = Guid.Parse("eeeeeeee-0000-4000-8000-00000000000c");

    static readonly ResourceId Address = new(
        Guid.Parse("11111111-1111-4111-8111-11111111111c"),
        Guid.Parse("22222222-2222-4222-8222-22222222222c"),
        "prod",
        ContainerRegistries.Type,
        "reg",
        Guid.Parse("33333333-3333-4333-8333-33333333333c")
    );
}
