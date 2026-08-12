using CyberCloud.Cluster.Conformance;
using CyberCloud.Cluster.Conformance.Infrastructure;
using CyberCloud.Providers.Analytics.Conformance;

namespace CyberCloud.Providers.Analytics.ClusterConformance;

/// <summary>
///     The cluster-backed suite, run against the managed ClickHouse provider.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Two class declarations over the case
///         <c>CyberCloud.Providers.Analytics.Conformance</c> already declares.</b> One provider, one
///         <c>ProviderConformanceCase</c>: a second copy here would be a second description of the
///         same provider, and the two would disagree the first time either changed.
///     </para>
///     <para>
///         ⚠ <b>Two CRD stubs, derived, in two API groups — and the second group is what this
///         provider adds to the harness's evidence.</b>
///         <c>ClusterConformanceHarness.EnsureCustomResourceDefinitionsAsync</c> reads group, version,
///         kind and plural off the case's own <c>Objects</c> and waits for <c>Established</c>. Every
///         earlier custom-resource provider needed one or two stubs in <i>one</i> group; this needs
///         one each in <c>clickhouse.altinity.com</c> and <c>clickhouse-keeper.altinity.com</c>, which
///         is the first time the derivation has had to key on the group as well as the kind.
///     </para>
///     <para>
///         ⚠ <b>A stub makes the REST path exist and nothing more.</b> No Altinity operator runs here,
///         so no <c>ClickHouseInstallation</c> in this suite has ever started a server, elected a
///         Keeper leader or answered a query. What the suite asserts is that the platform applied the
///         objects it said it applied, with the seven labels, into the right namespaces — the
///         operator's half is <c>charts/managed/clickhouse/conformance.yaml</c>'s assertions, which
///         have no runner yet.
///     </para>
/// </remarks>
/// <param name="fixture">The harness.</param>
public sealed class ClickHouseLifecycleConformance(ClusterConformanceFixture<AnalyticsCase> fixture)
    : ClusterConformanceTests<AnalyticsCase>(fixture), IClassFixture<ClusterConformanceFixture<AnalyticsCase>>;

/// <summary>docs/plan/24 § Phase 1's exit criterion 3, against the managed ClickHouse provider.</summary>
public sealed class ClickHouseSiloKillConformance : SiloKillConformanceTests<AnalyticsCase>;
