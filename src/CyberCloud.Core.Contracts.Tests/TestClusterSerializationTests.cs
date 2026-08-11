using CyberCloud.Core.Resources;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;
using Orleans.TestingHost;
using Shouldly;

namespace CyberCloud.Core.Contracts.Tests;

/// <summary>
///     The same round trips again, through the <see cref="Serializer" /> a <b>real silo</b> built.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="OrleansSerializerFixture" /> builds a serializer from a bare
///         <c>ServiceCollection</c> and names this assembly explicitly. That proves the converters
///         work; it does not prove a silo <i>finds</i> them. A silo assembles its type manifest from
///         the <c>TypeManifestProvider</c> attributes the Orleans code generator emitted into every
///         loaded assembly, and an assembly that fails to emit one — because it referenced
///         <c>Microsoft.Orleans.Core.Abstractions</c> instead of <c>Microsoft.Orleans.Sdk</c>, say —
///         produces exactly this failure: everything compiles, the unit tests pass, and the cluster
///         cannot serialise a <see cref="Result" />.
///     </para>
///     <para>
///         ADR-018 (docs/plan/02 § ADR-018) makes <c>TestCluster</c> the default test host for anything
///         Orleans, and this is the narrowest thing it is worth using for here: no grains are
///         declared (that is a later task), only the silo's own serializer is taken out of its
///         service provider.
///     </para>
/// </remarks>
public sealed class TestClusterSerializationTests : IAsyncLifetime {
    TestCluster cluster = null!;
    Serializer serializer = null!;

    /// <inheritdoc />
    public async ValueTask InitializeAsync() {
        cluster = new TestClusterBuilder(1).Build();
        await cluster.DeployAsync();
        serializer = cluster.Client.ServiceProvider.GetRequiredService<Serializer>();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() {
        await cluster.StopAllSilosAsync();
        await cluster.DisposeAsync();
    }

    [Fact]
    public void TheClusterFindsTheConvertersWithoutBeingToldAboutThisAssembly() {
        // No AddAssembly call anywhere: the silo discovered CyberCloud.Core.Contracts through the
        // manifest the code generator emitted.
        var original = Result<ResourceId>.Success(
            new(
                Guid.Parse("2b4a1c66-2e70-4a9d-9d0a-1f7ec1f1a4b3"),
                Guid.Parse("6f0f1f0e-1234-4c8b-9a3d-aabbccddeeff"),
                "prod",
                new("CyberCloud.DBforPostgreSQL", "servers"),
                "orders-db",
                Guid.Parse("11112222-3333-4444-5555-666677778888")
            )
        );

        var round = serializer.Deserialize<Result<ResourceId>>(serializer.SerializeToArray(original));

        round.ShouldBe(original);
    }

    [Fact]
    public void DefaultResultIsStillAFailureInsideARealSilo() {
        var round = serializer.Deserialize<Result>(serializer.SerializeToArray(default(Result)));

        round.IsFailure.ShouldBeTrue();
        round.Error!.Code.ShouldBe(ErrorCode.InternalError);
    }

    [Fact]
    public void AFailedResultIsStillAFailureInsideARealSilo() {
        var original = Result<int>.Failure(ErrorCode.QuotaExceeded, "vcpu: requested 8, available 2.");
        var round = serializer.Deserialize<Result<int>>(serializer.SerializeToArray(original));

        round.IsFailure.ShouldBeTrue();
        round.TryGetValue(out _).ShouldBeFalse();
        round.Error.ShouldBe(original.Error);
    }

    [Fact]
    public void TheSiloCanCopyAResultWithoutSerialisingIt() {
        // Orleans short-circuits an in-cluster call to a DeepCopy rather than a serialize/deserialize
        // pair. That path goes through the surrogate's copier, not its codec, and it is the path
        // every same-silo grain call actually takes — so a surrogate that round-trips but copies
        // wrongly would be invisible to every other test in this assembly.
        var copier = cluster.Client.ServiceProvider.GetRequiredService<DeepCopier>();

        copier.Copy(default(Result)).IsFailure.ShouldBeTrue();
        copier.Copy(Result.Success).IsSuccess.ShouldBeTrue();

        var failure = Result<ResourceId>.Failure(ErrorCode.ResourceNotFound, "no such resource.");
        var copied = copier.Copy(failure);
        copied.IsFailure.ShouldBeTrue();
        copied.Error.ShouldBe(failure.Error);
    }
}
