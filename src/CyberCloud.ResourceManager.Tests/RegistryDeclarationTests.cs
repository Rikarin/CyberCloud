using CyberCloud.ResourceManager.Registry;
using CyberCloud.ResourceManager.Tests.Infrastructure;

namespace CyberCloud.ResourceManager.Tests;

/// <summary>
///     The three flags that had no schema consequence, and now have one.
/// </summary>
/// <remarks>
///     ⚠ <b><c>SupportsTags</c>, <c>RequiresCluster</c> and <c>SupportsSoftDelete</c> were booleans
///     the registry recorded and nothing checked</b>, which is the shape of every "the code and the
///     document disagree" bug. Two of them now fail loudly; the third is documented as undelivered
///     rather than implied. Each case below is the failure that used to be silent.
/// </remarks>
public sealed class RegistryDeclarationTests {
    sealed class Provider(Action<IProviderBuilder> describe) : IResourceProvider {
        public string ProviderNamespace => "CyberCloud.Declaring";

        public void Describe(IProviderBuilder builder) => describe(builder);
    }

    static ProviderRegistry Build(Action<IProviderBuilder> describe) =>
        ProviderRegistry.Build([new Provider(describe)]);

    static ResourceSchema WithCluster() =>
        ResourceSchema.Of([
            new("/properties", SchemaKind.Nested),
            new("/properties/clusterId", SchemaKind.Text, Required: true) { Format = SchemaFormat.Uuid }
        ]);

    // ── RequiresCluster ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ATypeThatRequiresAClusterAndDoesNotDeclareTheIdIsRefusedAtSiloStart() {
        // ⚠ THE FAILURE THAT USED TO BE PER-RESOURCE AND AFTER THE 202. Before this check, such a
        // type started cleanly, accepted every PUT, answered 202, and then failed at reconcile time
        // for each resource in turn — the worst place to find out, because the caller has already
        // been told the write was accepted.
        var thrown = Should.Throw<InvalidOperationException>(
            () => Build(b => b
                .ResourceType("orphans")
                .ApiVersion("2026-08-01", ResourceSchema.Of([new("/location", SchemaKind.Text)]))
                .RequiresCluster())
        );

        thrown.Message.ShouldContain("/properties/clusterId");
        thrown.Message.ShouldContain("fail every reconcile");
    }

    [Fact]
    public void AnOptionalClusterIdIsRefusedBecauseItIsTheSameFailureOneStepLater() {
        var optional = ResourceSchema.Of([
            new("/properties", SchemaKind.Nested),
            new("/properties/clusterId", SchemaKind.Text)
        ]);

        Should.Throw<InvalidOperationException>(
                () => Build(b => b.ResourceType("loose").ApiVersion("2026-08-01", optional).RequiresCluster())
            )
            .Message.ShouldContain("required string");
    }

    [Fact]
    public void EveryApiVersionIsChecked() {
        // An api-version is served forever, so a type that dropped the property in a later version
        // would still be reachable at the earlier one — and the manager reads one pointer for both.
        Should.Throw<InvalidOperationException>(
                () => Build(b => b
                    .ResourceType("drifting")
                    .ApiVersion("2026-08-01", WithCluster())
                    .ApiVersion("2027-01-01", ResourceSchema.Of([new("/location", SchemaKind.Text)]))
                    .RequiresCluster())
            )
            .Message.ShouldContain("2027-01-01");
    }

    [Fact]
    public void ADeclaredPointerIsCarriedOntoTheRegistrationSoTheManagerReadsItRatherThanAConstant() {
        var registry = Build(b => b
            .ResourceType("placed")
            .ApiVersion("2026-08-01", WithCluster())
            .RequiresCluster());

        registry.TryGetType(new("CyberCloud.Declaring", "placed"), out var registration).ShouldBeTrue();
        registration.RequiresCluster.ShouldBeTrue();
        registration.ClusterIdPointer.ShouldBe(ClusterPlacement.DefaultPointer);
    }

    [Fact]
    public void ATypeWithNoClusterCarriesNoPointerAtAll() {
        var registry = Build(b => b
            .ResourceType("clusterless")
            .ApiVersion("2026-08-01", ResourceSchema.Of([new("/location", SchemaKind.Text)])));

        registry.TryGetType(new("CyberCloud.Declaring", "clusterless"), out var registration).ShouldBeTrue();
        // A DNS zone, a mail domain, a role assignment: empty rather than the default, so that
        // "declared nothing" and "declared the default" are different states in the document.
        registration.ClusterIdPointer.ShouldBeEmpty();
    }

    [Fact]
    public void AProviderMayPutTheClusterIdSomewhereElseAndTheCheckFollowsIt() {
        var elsewhere = ResourceSchema.Of([new("/clusterRef", SchemaKind.Text, Required: true)]);

        var registry = Build(b => b
            .ResourceType("elsewhere")
            .ApiVersion("2026-08-01", elsewhere)
            .RequiresCluster("/clusterRef"));

        registry.TryGetType(new("CyberCloud.Declaring", "elsewhere"), out var registration).ShouldBeTrue();
        registration.ClusterIdPointer.ShouldBe("/clusterRef");
    }

    // ── Display metadata ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void ATypeCarriesTheNameAHumanSees() {
        var registry = ProviderRegistry.Build([new TestingProvider()]);

        registry.TryGetType(new("CyberCloud.Testing", "widgets"), out var registration).ShouldBeTrue();
        registration.Display.Name.ShouldBe("Testing widget");
        registration.Display.Plural.ShouldBe("Testing widgets");
        registration.Display.Alias.ShouldBe("twidget");
        registration.Display.IsEmpty.ShouldBeFalse();
    }

    [Fact]
    public void AnUndeclaredDisplayIsEmptyRatherThanNull() {
        // ⚠ `default(DisplayMetadata)` skips the constructor's defaults, so every string would be
        // null and IsEmpty — the first thing anything asks — would throw. It is normalised on read.
        var registry = Build(b => b
            .ResourceType("anonymous")
            .ApiVersion("2026-08-01", ResourceSchema.Of([new("/location", SchemaKind.Text)])));

        registry.TryGetType(new("CyberCloud.Declaring", "anonymous"), out var registration).ShouldBeTrue();
        registration.Display.IsEmpty.ShouldBeTrue();
        registration.Display.Name.ShouldBeEmpty();
    }

    [Fact]
    public void ATypeMayNotBeNamedTwice() =>
        Should.Throw<InvalidOperationException>(
                () => Build(b => b
                    .ResourceType("twice")
                    .ApiVersion("2026-08-01", ResourceSchema.Of([new("/location", SchemaKind.Text)]))
                    .Display("One", "Ones")
                    .Display("Two", "Twos"))
            )
            .Message.ShouldContain("named twice");

    // ── Action schemas ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AnActionCarriesItsRequestResponseAndWhetherItIsLongRunning() {
        var registry = ProviderRegistry.Build([new TestingProvider()]);

        registry.TryGetType(new("CyberCloud.Testing", "widgets"), out var registration).ShouldBeTrue();

        registration.TryGetAction("resize", out var resize).ShouldBeTrue();
        resize.Request.ShouldNotBeNull();
        resize.Response.ShouldNotBeNull();
        resize.LongRunning.ShouldBeTrue();

        // ⚠ And the other branch stays real: an action that declares nothing is null rather than an
        // empty schema, because "takes no parameters" and "we have not said" are different claims and
        // the emitted document renders them differently.
        registration.TryGetAction("restart", out var restart).ShouldBeTrue();
        restart.Request.ShouldBeNull();
        restart.LongRunning.ShouldBeFalse();
    }
}
