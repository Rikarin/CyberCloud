using CyberCloud.ResourceManager;
using Microsoft.Extensions.DependencyInjection;

namespace CyberCloud.ObjectStorage.Tests;

/// <summary>
///     What a host ends up with, asserted on the real registrations in both composition orders —
///     the shape <c>VaultSeamWiringTests</c> established for the secret seams.
/// </summary>
public sealed class ObjectStorageWiringTests {
    static ObjectStorageOptions Configured() =>
        new() {
            Endpoint = "https://s3.platform.internal",
            Bucket = "cybercloud",
            AccessKeyId = "AKIACYBERCLOUD",
            SecretAccessKey = "not-a-real-secret"
        };

    [Fact]
    public async Task AnUnwiredHostGetsTheRefusingDefaultAndItsMessageNamesTheWiring() {
        var services = new ServiceCollection();
        services.AddCyberCloudResourceManager();

        using var provider = services.BuildServiceProvider();

        var store = provider.GetRequiredService<IObjectStore>();
        store.ShouldBeOfType<UnavailableObjectStore>();

        var refused = await store.ListAsync("anything", TestContext.Current.CancellationToken);
        refused.Error!.Code.ShouldBe(ErrorCode.InternalError);
        refused.Error.Message.ShouldContain("AddS3ObjectStore");
        refused.Error.Message.ShouldContain(ObjectStorageOptions.SectionName);
    }

    [Fact]
    public void TheS3StoreWinsWhenRegisteredBeforeTheManager() {
        var services = new ServiceCollection();
        services.AddS3ObjectStore(Configured());
        services.AddCyberCloudResourceManager();

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IObjectStore>().ShouldBeOfType<S3ObjectStore>();
    }

    [Fact]
    public void TheS3StoreWinsWhenRegisteredAfterTheManager() {
        // ⚠ The order a host is most likely to write, and the one a TryAdd on this side would lose.
        var services = new ServiceCollection();
        services.AddCyberCloudResourceManager();
        services.AddS3ObjectStore(Configured());

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IObjectStore>().ShouldBeOfType<S3ObjectStore>();
    }

    [Fact]
    public void AWrongSectionFailsAtCompositionNamingTheKey() {
        var services = new ServiceCollection();

        var thrown = Should.Throw<ArgumentException>(() =>
            services.AddS3ObjectStore(new() { Endpoint = "https://ok", Bucket = "b", AccessKeyId = "a" })
        );

        thrown.Message.ShouldContain("SecretAccessKey");
        services.ShouldNotContain(x => x.ServiceType == typeof(IObjectStore));
    }

    [Fact]
    public void IsConfiguredNeedsAllFourKeys() {
        Configured().IsConfigured.ShouldBeTrue();
        new ObjectStorageOptions().IsConfigured.ShouldBeFalse();
        Configured().With(x => x.Bucket = "").IsConfigured.ShouldBeFalse();
    }
}
