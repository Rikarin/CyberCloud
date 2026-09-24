using CyberCloud.ResourceManager.Terminals;
using CyberCloud.ServiceDefaults.Storage;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;

namespace CyberCloud.ResourceManager.Tests;

/// <summary>
///     A terminal session's owner record through the serializers a real silo could put it through,
///     rather than through in-memory storage.
/// </summary>
/// <remarks>
///     ⚠ <b>What <c>TerminalSessionGrainTests.ALostActivationKeepsTheSessionsOwner</c> can't show.</b>
///     <c>ResourceManagerCluster</c> runs on in-memory grain storage, which keeps the object graph, so
///     that test would pass with a record no serializer reads back. A record that read back without
///     its owner would be the defect it exists to close: a fresh activation binding whoever connects
///     next. The hot tier leaves the serializer to Orleans' default; the JSON one is here as well
///     because <see cref="ResourceState" />'s remarks count it among the serializers a state may pass
///     through.
/// </remarks>
public sealed class TerminalSessionStateSerializationTests : IDisposable {
    readonly ServiceProvider provider;
    readonly Serializer serializer;

    /// <summary>Builds an Orleans serializer over the assemblies the state's types come from.</summary>
    public TerminalSessionStateSerializationTests() {
        var services = new ServiceCollection();
        services.AddSerializer(static builder => builder
            .AddAssembly(typeof(TerminalSessionState).Assembly)
            .AddAssembly(typeof(CallerContext).Assembly)
        );

        provider = services.BuildServiceProvider();
        serializer = provider.GetRequiredService<Serializer>();
    }

    /// <inheritdoc />
    public void Dispose() => provider.Dispose();

    static TerminalSessionState Recorded() =>
        new() {
            Owner = new() {
                TenantId = Guid.Parse("a0a0a0a0-0000-4000-8000-000000000022"),
                SubjectType = "user",
                SubjectId = "alice",
                CorrelationId = "c-22"
            },
            Ended = true,
            EndedBecause = "the shell exited"
        };

    [Fact]
    public void AnOwnerRecordRoundTripsThroughOrleans() =>
        AssertSame(Recorded(), serializer.Deserialize<TerminalSessionState>(serializer.SerializeToArray(Recorded())));

    [Fact]
    public void AnOwnerRecordRoundTripsThroughJson() {
        var json = new SystemTextJsonGrainStorageSerializer();

        AssertSame(Recorded(), json.Deserialize<TerminalSessionState>(json.Serialize(Recorded())));
    }

    static void AssertSame(TerminalSessionState expected, TerminalSessionState actual) {
        actual.Owner.ShouldBe(expected.Owner, "an owner that doesn't read back is a session anybody can take");
        actual.Ended.ShouldBe(expected.Ended);
        actual.EndedBecause.ShouldBe(expected.EndedBecause);
    }
}
