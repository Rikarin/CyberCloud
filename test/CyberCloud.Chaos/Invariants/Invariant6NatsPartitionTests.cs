using CyberCloud.Chaos.Topology;

namespace CyberCloud.Chaos.Invariants;

/// <summary>
///     docs/plan/23 § The chaos invariants, 6: <i>partition the NATS cluster → streams recover,
///     consumers resume from their cursor, no duplicate billing after dedup.</i>
/// </summary>
/// <remarks>
///     ⚠ <b>Vacuous, and the row says exactly why rather than skipping quietly.</b> No silo in this
///     repository speaks NATS: <c>Microsoft.Orleans.Streaming.NATS</c> is a prerelease no project
///     references (<c>OrleansApplication.cs</c> carries the commented-out
///     <c>AddMultitenantStreams</c> line), <c>CyberCloud.AppHost</c> adds a <c>nats</c> container
///     nothing connects to, and the metering pipeline of docs/plan/22 § The pipeline — the consumer
///     whose cursor and dedup this invariant is about — is not built. There is no stream to
///     partition and no billing to duplicate. The test exists so that
///     <c>build/Build.Chaos.cs</c> § AssertEveryInvariantIsCovered has a row to find, and so the
///     ○ in the report is a sentence rather than an absence; the day a silo binds JetStream, this
///     is where the partition goes.
/// </remarks>
[Collection(ChaosSuite.Name)]
public sealed class Invariant6NatsPartitionTests(ChaosTopology topology) {
    const string Reason =
        "no silo in this repository speaks NATS — Microsoft.Orleans.Streaming.NATS is a prerelease no project "
        + "references (OrleansApplication.cs), the AppHost's nats container has no client, and the metering "
        + "consumer of docs/plan/22 § The pipeline whose cursor and dedup this invariant is about is not built. "
        + "There is no stream to partition and no billing to duplicate.";

    [Fact]
    public void Invariant6_PartitioningTheNatsClusterHasNothingToPartitionYet() {
        topology.Report.Vacuous(6, Reason, new Dictionary<string, double>(StringComparer.Ordinal) { ["natsClientsInAnySilo"] = 0 });
        Assert.Skip("VACUOUS — " + Reason);
    }
}
