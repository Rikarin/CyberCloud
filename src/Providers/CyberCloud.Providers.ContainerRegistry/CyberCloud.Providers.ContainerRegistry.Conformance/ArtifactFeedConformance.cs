using CyberCloud.Conformance;
using CyberCloud.Conformance.Harness;
using CyberCloud.Core.Resources;
using CyberCloud.Providers.ContainerRegistry.Contracts;
using CyberCloud.ResourceManager.Conformance;
using Orleans.Multitenant;
using Shouldly;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.ContainerRegistry.Conformance;

/// <summary>
///     <c>CyberCloud.ContainerRegistry/feeds</c>, registered into the shared provider suite.
///     docs/plan/13 § Artifact feeds.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             The first case with no <c>Objects</c>, and the first with a <c>DataPlane</c> — the two
///             halves of the same fact.
///         </b> A feed applies nothing to a cluster, so the suite's world-facing assertions have no
///         object to read around the reconciler; what they read instead is what
///         <see cref="DataPlaneOf" /> hands them — the catalogue grain, reached through
///         <c>ForTenant</c> exactly as the reconciler reaches it, broken by closing it and read by
///         describing it. That is clause 4 with a grain in the place of a ConfigMap.
///     </para>
///     <para>
///         <b>The break is a close, and a close is permanent.</b> So the drift assertion for this
///         type is "noticed" rather than "corrected": a pass after the break reports the grain's
///         refusal and never <c>Converged</c>. That is the right answer — what a feed held was the
///         tenant's, and no desired body can put it back — and
///         <c>ProviderConformanceTests.DriftIsCorrectedWhenSomebodyDeletesTheObjectsByHand</c>'s
///         clusterless branch says so in its own words.
///     </para>
/// </remarks>
public sealed class ArtifactFeedCase : IProviderCaseSource {
    /// <inheritdoc />
    public static ProviderConformanceCase ProviderCase { get; } =
        new() {
            DisplayName = "CyberCloud.ContainerRegistry/feeds",
            CreateProvider = () => new ContainerRegistryProvider(),
            ReconcilerType = typeof(ArtifactFeedReconciler),
            // ⚠ The factory takes the harness's clock and nothing else, and this reconciler needs a
            // grain factory too — the first in the tree that does. The suite drives a reconciler it
            // built this way twice, in the four-clause check and the drift repair, and both have to
            // reach the same silo the harness runs; ConformanceGrains is how the fixture's factory
            // gets here, and its remarks say why that is not the case's own member yet.
            CreateReconciler = clock => new ArtifactFeedReconciler(ConformanceGrains.Instance, clock),
            Type = ArtifactFeeds.Type,
            ApiVersion = ArtifactFeeds.V2026,
            // The cluster id the harness passes is ignored: a feed names no cluster.
            Body = _ => ArtifactFeeds.Body("nuget", "packages for the build"),
            // ⚠ Changes the description, which is the ONLY mutable property a feed has. The kind is
            // immutable and the location is immutable, so a body that changed either would be
            // refused at the write path rather than reaching the cluster — which is what the update
            // assertion needs to distinguish from an update that stopped at the grain.
            ChangedBody = _ => ArtifactFeeds.Body("nuget", "packages for the build, renamed"),
            // Drops the required kind.
            InvalidBody = _ => Without(ArtifactFeeds.Body("nuget"), "kind"),
            InvalidBodyTarget = ArtifactFeeds.KindPointer,
            // ⚠ No action, deliberately, and the suite's two POST assertions skip loudly for it. A
            // feed's endpoints — its NuGet service index, its npm registry URL — are a function of
            // the resource path and the feeds host's public origin, and an action that returned them
            // would need that origin at the silo, which is the feeds host's configuration and not
            // the silo's. Recorded at charts/managed/feeds/conformance.yaml § owed.
            ActionName = "",
            Objects = static (_, _) => [],
            OperatorWritten = static (_, _) => [],
            ObjectMatchesDesired = static _ => false,
            DataPlane = DataPlaneOf,
            StoragePrefix = address => ArtifactFeeds.StoragePrefix(address.TenantId, address.Id)
        };

    /// <summary>
    ///     The feed's world: broken by closing the catalogue, read by describing it.
    /// </summary>
    /// <param name="grains">The harness's grain factory.</param>
    /// <param name="address">The feed, with its GUID resolved.</param>
    static ConformanceWorld DataPlaneOf(IGrainFactory grains, ResourceId address) {
        var feed = grains
            .ForTenant(address.TenantId.ToString("D", CultureInfo.InvariantCulture))
            .GetGrain<IFeedGrain>(GrainKeys.Resource(address.Id));

        return new(
            BreakAsync: async () => (await feed.CloseAsync()).IsSuccess.ShouldBeTrue(),
            MatchesDesiredAsync: async () => {
                var described = await feed.DescribeAsync();
                return described.IsSuccess
                    && described.GetValueOrThrow().IsOpen
                    && described.GetValueOrThrow().Kind == FeedKind.NuGet;
            }
        );
    }

    /// <summary>A body with one property removed from <c>/properties</c>.</summary>
    static string Without(string body, string property) {
        var node = JsonNode.Parse(body)!.AsObject();
        node["properties"]!.AsObject().Remove(property);
        return node.ToJsonString();
    }
}

/// <summary>The shared suite, run against the artifact-feed type.</summary>
/// <remarks>
///     ⚠ Three lines rather than the one every other family writes, and the extra two are the
///     grain factory reaching the case's reconciler factory — see <see cref="ConformanceGrains" />.
/// </remarks>
public sealed class ArtifactFeedConformance
    : ProviderConformanceTests<ArtifactFeedCase>, IClassFixture<ProviderTestCluster<ArtifactFeedCase>> {
    /// <summary>Runs the suite over the harness, and lets the case's reconciler reach its silo.</summary>
    /// <param name="cluster">The harness.</param>
    public ArtifactFeedConformance(ProviderTestCluster<ArtifactFeedCase> cluster) : base(cluster) =>
        ConformanceGrains.Use(cluster.Grains);
}

/// <summary>The container-backed half, skipped loudly, against the artifact-feed type.</summary>
public sealed class ArtifactFeedClusterBackedConformance()
    : ClusterBackedConformanceTests(ArtifactFeedCase.ProviderCase);

/// <summary>What this case is shaped like.</summary>
public sealed class ArtifactFeedSuiteShapeTests {
    [Fact]
    public void TheCaseDeclaresNoObjectAndBothHalvesOfItsDataPlane() {
        // ⚠ Both halves, because either alone is a suite that asserts less than it says. A DataPlane
        // with no StoragePrefix is a teardown nobody checks emptied the store; a StoragePrefix with
        // no DataPlane is clause 4 checked against nothing.
        var address = new ResourceId(
            Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001"),
            Guid.Parse("cccccccc-0000-4000-8000-000000000003"),
            "prod",
            ArtifactFeeds.Type,
            "packages",
            Guid.Parse("dddddddd-0000-4000-8000-00000000000d")
        );

        ArtifactFeedCase.ProviderCase.Objects(address, "ns").ShouldBeEmpty();
        ArtifactFeedCase.ProviderCase.DataPlane.ShouldNotBeNull();
        ArtifactFeedCase.ProviderCase.StoragePrefix.ShouldNotBeNull();
        ArtifactFeedCase.ProviderCase.StoragePrefix(address)
            .ShouldBe("aaaaaaaa000040008000000000000001/dddddddd00004000800000000000000d/");
    }

    [Fact]
    public void TheChangedBodyDiffersOnlyInTheOneMutableProperty() {
        using var before = JsonDocument.Parse(ArtifactFeedCase.ProviderCase.Body(Guid.Empty));
        using var after = JsonDocument.Parse(ArtifactFeedCase.ProviderCase.ChangedBody(Guid.Empty));

        ArtifactFeeds.KindOf(before.RootElement).ShouldBe(ArtifactFeeds.KindOf(after.RootElement));
        before.RootElement.GetProperty("location").GetString().ShouldBe(after.RootElement.GetProperty("location").GetString());
        before.RootElement.GetProperty("properties").GetProperty("description").GetString()
            .ShouldNotBe(after.RootElement.GetProperty("properties").GetProperty("description").GetString());
    }
}

/// <summary>
///     The grain factory the case's reconciler factory hands out, which is the harness's own.
/// </summary>
/// <remarks>
///     ⚠ <b>Set by the suite fixture, read by the case, and refusing until it is set.</b>
///     <c>ProviderConformanceCase.CreateReconciler</c> takes a clock and nothing else, because
///     "every reconciler takes exactly an <c>IClock</c>" was the shape until this type; the feed
///     reconciler takes a grain factory too. The one direct drive that constructs a reconciler this
///     way — the four-clause check — needs it to reach the same silo the harness runs, so the
///     fixture registers its factory here at start and the refusal below is what a reconciler built
///     outside a running harness meets. A <c>required</c> member on the case would have been the
///     cleaner shape and is the one to move to when a second clusterless type arrives.
/// </remarks>
static class ConformanceGrains {
    static IGrainFactory? instance;

    /// <summary>The harness's grain factory.</summary>
    public static IGrainFactory Instance =>
        instance
        ?? throw new InvalidOperationException(
            "ArtifactFeedReconciler was built outside a running harness. ProviderTestCluster's silo "
            + "resolves the reconciler with its own IGrainFactory; a direct construction has to go "
            + "through ConformanceGrains.Use first."
        );

    /// <summary>Records the harness's grain factory for the case's reconciler factory.</summary>
    /// <param name="grains">The factory.</param>
    public static void Use(IGrainFactory grains) => instance = grains;
}
