using CyberCloud.Cluster.Conformance;
using CyberCloud.Cluster.Conformance.Infrastructure;
using CyberCloud.Providers.Monitor.Conformance;

namespace CyberCloud.Providers.Monitor.ClusterConformance;

/// <summary>
///     The cluster-backed suite, run against the monitor-workspace provider.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Two class declarations over the case
///         <c>CyberCloud.Providers.Monitor.Conformance</c> already declares.</b> One provider, one
///         <c>ProviderConformanceCase</c>: a second copy here would be a second description of the
///         same provider, and the two would disagree the first time either changed.
///     </para>
///     <para>
///         ⚠ <b>ONE CRD STUB AND TWO KINDS THAT NEED NONE, WHICH IS A MIX NO EARLIER FAMILY HAS.</b>
///         <c>ClusterConformanceHarness.EnsureCustomResourceDefinitionsAsync</c> reads group,
///         version, kind and plural off the case's own <c>Objects</c> and waits for
///         <c>Established</c>. It derives one definition here — <c>VMUser</c> in
///         <c>operator.victoriametrics.com</c> — and derives none for the <c>ConfigMap</c> and the
///         <c>Secret</c>, whose REST paths a bare k3s serves without being told. That is the whole
///         range of the derivation exercised in one case.
///     </para>
///     <para>
///         ⚠⚠ <b>WHAT THIS SUITE PROVES, AND WHAT IT DOES NOT — AND ON THIS TYPE THE GAP IS WIDER
///         THAN ON ANY OTHER FAMILY.</b> The k3s has no VictoriaMetrics, no vmauth, no ClickHouse and
///         no <c>CyberCloud.Ingest.Host</c>, so nothing in this suite has ever authenticated an
///         ingest key, routed a sample to an accountID, refused a cardinality bomb or expired a
///         partition. What it proves is the platform's half: the three manifests are ones a real API
///         server accepts, server-side apply under our field manager behaves as ADR-013 assumes, the
///         seven labels survive admission, the plural in each <c>GroupVersionKind</c> addresses a
///         real REST path, two tenants' workspaces land in different namespaces, and a silo killed
///         mid-provision still converges. ⚠ <b>And it proves less than usual even about the
///         manifests</b>: the derived CRD stub has an <i>open</i> schema, so a <c>VMUser</c> whose
///         <c>target_path_suffix</c> were spelled the way the operator's prose spells it would be
///         accepted here and ignored by vmauth in production. The one test that catches that is
///         <c>MonitorReconcilerTests.TheTargetPathSuffixIsSpelledTheWayTheGoTagSpellsIt</c>, which
///         is a unit test against a literal, because nothing in this repository validates a rendered
///         document against an operator's real schema.
///     </para>
/// </remarks>
/// <param name="fixture">The harness.</param>
public sealed class MonitorWorkspaceLifecycleConformance(ClusterConformanceFixture<MonitorCase> fixture)
    : ClusterConformanceTests<MonitorCase>(fixture), IClassFixture<ClusterConformanceFixture<MonitorCase>>;

/// <summary>docs/plan/24 § Phase 1's exit criterion 3, against the monitor-workspace provider.</summary>
public sealed class MonitorWorkspaceSiloKillConformance : SiloKillConformanceTests<MonitorCase>;
