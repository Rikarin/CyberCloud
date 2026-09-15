using CyberCloud.ResourceManager.Registry;
using System.Text.Json;

namespace CyberCloud.Providers.ContainerRegistry.Tests;

/// <summary>
///     What <c>CyberCloud.ContainerRegistry/feeds</c> declares, and — because it is the first type
///     of its shape — what it deliberately does not.
/// </summary>
public sealed class ArtifactFeedDeclarationTests {
    static ResourceTypeRegistration Registration() {
        var registry = ProviderRegistry.Build([new ContainerRegistryProvider()]);

        registry.TryGetType(ArtifactFeeds.Type, out var registration)
            .ShouldBeTrue("the feeds type is not in the registry ContainerRegistryProvider.Describe builds");

        return registration;
    }

    [Fact]
    public void TheProviderBuildsIntoARegistryTheSiloWouldAcceptWithBothTypes() {
        var registration = Registration();

        registration.ReconcilerType.ShouldBe(typeof(ArtifactFeedReconciler));
        registration.ReadPermission.ShouldBe("read");
        registration.WritePermission.ShouldBe("write");
        registration.DeletePermission.ShouldBe("delete");
        registration.SupportsTags.ShouldBeTrue();
        registration.Display.Alias.ShouldBe(ContainerRegistryProvider.FeedShortName);
    }

    [Fact]
    public void AFeedRequiresNoClusterAndBindsTheChartThatRendersNothing() {
        // ⚠ The absence and the binding the provider's Describe argues for. A RequiresCluster here
        // would make every feed name a cluster it never touches; the chart binding is what generates
        // the type's configuration surface under charts/managed/feeds, whose only template is a
        // NOTES.txt. Both are decisions and both are pinned.
        var registration = Registration();

        registration.RequiresCluster.ShouldBeFalse();
        registration.ClusterIdPointer.ShouldBeEmpty();
        registration.Chart.ShouldBe(ArtifactFeeds.ChartName);
        registration.Actions.ShouldBeEmpty();
        registration.SoftDeleteDays.ShouldBe(0);
    }

    [Fact]
    public void TheBodyHasNoClusterIdAndTheKindIsImmutableAndClosed() {
        ArtifactFeeds.Pointers2026.ShouldNotContain(ClusterPlacement.DefaultPointer);

        var kind = ArtifactFeeds.Schema2026.Properties.Single(x => x.JsonPointer == ArtifactFeeds.KindPointer);

        kind.Required.ShouldBeTrue();
        kind.Immutable.ShouldBeTrue();
        kind.AllowedValues.ShouldBe(ArtifactFeeds.KindNames);
    }

    [Theory]
    [InlineData("nuget", FeedKind.NuGet)]
    [InlineData("npm", FeedKind.Npm)]
    [InlineData("maven", FeedKind.Maven)]
    public void EveryKindNameRoundTripsThroughTheBody(string name, FeedKind kind) {
        using var body = JsonDocument.Parse(ArtifactFeeds.Body(name));

        ArtifactFeeds.KindOf(body.RootElement).ShouldBe(kind);
        ArtifactFeeds.NameOf(kind).ShouldBe(name);
        ArtifactFeeds.ParseKind(name).ShouldBe(kind);
    }

    [Theory]
    [InlineData("NuGet")]
    [InlineData("")]
    [InlineData("oci")]
    [InlineData(null)]
    public void AKindTheSchemaDoesNotAllowIsUnknown(string? name) => ArtifactFeeds.ParseKind(name).ShouldBe(FeedKind.Unknown);

    [Fact]
    public void ABodyWithNoKindIsUnknownRatherThanAThrow() {
        using var empty = JsonDocument.Parse("{}");
        using var noKind = JsonDocument.Parse("""{"properties":{}}""");
        using var wrongShape = JsonDocument.Parse("""{"properties":{"kind":7}}""");

        ArtifactFeeds.KindOf(empty.RootElement).ShouldBe(FeedKind.Unknown);
        ArtifactFeeds.KindOf(noKind.RootElement).ShouldBe(FeedKind.Unknown);
        ArtifactFeeds.KindOf(wrongShape.RootElement).ShouldBe(FeedKind.Unknown);
    }

    [Fact]
    public void TheSchemaRefusesAKindItDoesNotName() {
        using var body = JsonDocument.Parse(ArtifactFeeds.Body("oci"));

        var refused = ArtifactFeeds.Schema2026.Validate(body.RootElement, true, true);

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Target.ShouldBe(ArtifactFeeds.KindPointer);
    }

    [Fact]
    public void TheStoragePrefixStartsWithTheTenantAndEndsWithASlash() {
        // ⚠ The contract between the host that writes and the reconciler that deletes. The tenant is
        // first so one tenant's teardown can never list another's; the trailing slash is what keeps
        // feed `…0a` from matching feed `…0ab` on a prefix listing.
        var tenant = Guid.Parse("aaaaaaaa-0000-4000-8000-00000000000a");
        var feed = Guid.Parse("bbbbbbbb-0000-4000-8000-00000000000b");

        ArtifactFeeds.StoragePrefix(tenant, feed)
            .ShouldBe("aaaaaaaa00004000800000000000000a/bbbbbbbb00004000800000000000000b/");
    }

    [Fact]
    public void TheSiblingTypeIsUnchangedByTheAddition() {
        // The registry holds both, the registry's registrations differ, and the short names do not
        // collide — ProviderRegistry.Build would have thrown on a duplicate.
        var registry = ProviderRegistry.Build([new ContainerRegistryProvider()]);

        registry.Types.Select(x => x.Type.ToString())
            .Order(StringComparer.Ordinal)
            .ShouldBe(["CyberCloud.ContainerRegistry/feeds", "CyberCloud.ContainerRegistry/registries"]);
    }
}
