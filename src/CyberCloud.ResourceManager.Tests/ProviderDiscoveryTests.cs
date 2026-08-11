using CyberCloud.ResourceManager.Contracts.Generation;
using CyberCloud.ResourceManager.Registry;
using System.Text.Json;

namespace CyberCloud.ResourceManager.Tests;

/// <summary>
///     The seam ADR-012's build step reaches the registry through: a provider assembly, loaded, its
///     <c>Describe</c> run, and the same <see cref="ProviderRegistry" /> the silo builds.
/// </summary>
/// <remarks>
///     ⚠ <b>This is the identity docs/plan/08 § The provider registry is about.</b> <i>"the same
///     registry that generates the CLI is the one that validates the request body"</i> — so the test
///     that matters here is not "discovery finds a type", it is that the object the emitter reads is
///     the object <c>ResourceSchema.Validate</c> reads. Both assertions are below, on the same
///     instance.
/// </remarks>
public sealed class ProviderDiscoveryTests {
    [Fact]
    public void AProviderInAnAssemblyIsFoundAndDescribed() {
        var providers = ProviderDiscovery.FromAssembly(typeof(DiscoverableProvider).Assembly);

        providers.ShouldContain(x => x is DiscoverableProvider);
    }

    [Fact]
    public void AProviderWithNoParameterlessConstructorFailsRatherThanBeingSkipped() {
        // A provider silently missing from the generated document has the same symptom as a provider
        // nobody wrote, and only one of the two has anybody looking for it.
        var failure = Should.Throw<InvalidOperationException>(
            () => ProviderDiscovery.FromTypes([typeof(UnconstructableProvider)])
        );

        failure.Message.ShouldContain(nameof(UnconstructableProvider));
        failure.Message.ShouldContain("parameterless constructor");
    }

    [Fact]
    public void TheRegistryTheEmitterReadsIsTheRegistryTheWritePathValidatesAgainst() {
        var registry = ProviderRegistry.Build([new DiscoverableProvider()]);

        // Half one: it generates.
        var document = OpenApiEmitter.Emit(registry, ApiVersion.Parse(DiscoverableProvider.Version));

        OpenApiStructure.Validate(document).ShouldBeEmpty();

        document["components"]!["schemas"]!["CyberCloud.Discovery.things"]!
            ["properties"]!["size"]!["type"]!
            .GetValue<string>()
            .ShouldBe("integer");

        // Half two: the very same object validates a body, and disagrees with nothing.
        registry.TryGetType(new(DiscoverableProvider.Namespace, "things"), out var registration).ShouldBeTrue();

        var schema = registration.SchemaFor(ApiVersion.Parse(DiscoverableProvider.Version)).GetValueOrThrow();

        schema.Validate(JsonDocument.Parse("""{"size":3}""").RootElement).IsSuccess.ShouldBeTrue();

        // The document says integer; the validator refuses a string. One source, one answer.
        schema.Validate(JsonDocument.Parse("""{"size":"3"}""").RootElement)
            .IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void EmittingFromAnAssemblyWithNoProviderIsAnEmptyRegistryRatherThanAFailure() =>
        ProviderDiscovery.FromAssembly(typeof(ProviderRegistry).Assembly).ShouldBeEmpty();
}

/// <summary>A provider that exists only to be discovered.</summary>
public sealed class DiscoverableProvider : IResourceProvider {
    public const string Namespace = "CyberCloud.Discovery";
    public const string Version = "2026-08-01";

    /// <inheritdoc />
    public string ProviderNamespace => Namespace;

    /// <inheritdoc />
    public void Describe(IProviderBuilder builder) {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ResourceType("things")
            .ApiVersion(
                Version,
                ResourceSchema.Of([new("/size", SchemaKind.WholeNumber, Description: "How big.")])
            );
    }
}

/// <summary>
///     A provider the generation step cannot construct. ⚠ <c>internal</c> on purpose: it must not be
///     found by <see cref="ProviderDiscovery.FromAssembly" />, which scans this same test assembly in
///     the test above, so it is reached through <see cref="ProviderDiscovery.FromTypes" /> instead.
/// </summary>
sealed class UnconstructableProvider(string configured) : IResourceProvider {
    /// <inheritdoc />
    public string ProviderNamespace => configured;

    /// <inheritdoc />
    public void Describe(IProviderBuilder builder) => throw new NotSupportedException();
}
