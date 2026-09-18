namespace CyberCloud.Load.Scenarios;

/// <summary>
///     docs/plan/23 § The load scenarios, rows 5 and 6 — <i>1 000 concurrent terminal sessions</i>
///     and <i>500 000 spans/s ingest</i> — which this machine cannot host, written into the results
///     file as vacuous with the reason rather than left out.
/// </summary>
/// <remarks>
///     ⚠ A metric absent from the results file is, to <c>build/Build.Load.cs</c>, a scenario that
///     did not run, and the target fails on it — correctly. These two rows are not that: they are
///     scenarios whose infrastructure does not exist here, and the difference between "did not run"
///     and "cannot run here, because" is the whole point of a ○ row. Each reason names what would
///     change it.
/// </remarks>
[Collection(LoadSuite.Name)]
public sealed class UnhostedScenariosTests(LoadTopology topology) {
    const string TerminalReason =
        "a thousand concurrent terminal sessions are a thousand exec streams into a thousand cloud-shell pods "
        + "(docs/plan/19 § Cloud terminal), and the k3s in Docker that hosts this run schedules pods on one node "
        + "with a laptop's share of it; CyberCloud.Providers.Terminal's conformance suite proves one session, "
        + "and a hundred pods here would measure the laptop. The row needs the staging cluster.";

    const string SpansReason =
        "there is no span-ingest path in this repository to drive: the collector and VictoriaMetrics are "
        + "charts/bundle/ components (docs/plan/16 § Observability) that nothing in a silo or gateway feeds "
        + "spans into, so 'no drops below quota' and 'ingest pods scale linearly' have no pipeline to be true of. "
        + "The row needs the observability stack installed and an ingest client written.";

    [Fact]
    public void TerminalStreamsAreNotHostedHere() {
        topology.Report.Vacuous("terminal-stream-p99-ms", TerminalReason);
        Assert.Skip("VACUOUS — " + TerminalReason);
    }

    [Fact]
    public void SpanIngestIsNotHostedHere() {
        topology.Report.Vacuous("span-ingest-drops", SpansReason);
        Assert.Skip("VACUOUS — " + SpansReason);
    }
}
