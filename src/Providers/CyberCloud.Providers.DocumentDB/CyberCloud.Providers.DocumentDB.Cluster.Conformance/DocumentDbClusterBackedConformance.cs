using CyberCloud.Cluster.Conformance;
using CyberCloud.Cluster.Conformance.Infrastructure;
using CyberCloud.Providers.DocumentDB.Conformance;

namespace CyberCloud.Providers.DocumentDB.ClusterConformance;

/// <summary>
///     The cluster-backed suite, run against the managed document-database provider.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Two class declarations over the case
///         <c>CyberCloud.Providers.DocumentDB.Conformance</c> already declares.</b> One provider, one
///         <c>ProviderConformanceCase</c>: a second copy here would be a second description of the
///         same provider, and the two would disagree the first time either changed.
///     </para>
///     <para>
///         ⚠ <b>Two CRD stubs and two kinds that need none, and all four are derived rather than
///         declared.</b> <c>ClusterConformanceHarness.EnsureCustomResourceDefinitionsAsync</c> reads
///         group, version, kind and plural off the case's own <c>Objects</c> and waits for
///         <c>Established</c>. A bare k3s already serves <c>Deployment</c> and <c>Service</c>; it
///         serves neither <c>postgresql.cnpg.io/v1</c> nor <c>monitoring.coreos.com/v1</c>. This is
///         the first provider whose object set is mixed — <c>charts/managed/kafka</c> needed stubs for
///         both of its kinds and <c>charts/managed/nats</c> for one of five — and the harness works it
///         out from <c>Objects</c> without being told which is which.
///     </para>
///     <para>
///         ⚠ <b>A stub makes the REST path exist and nothing more.</b> It does not run CloudNativePG,
///         so nothing here observes a PostgreSQL cluster, an extension being installed or a FerretDB
///         pod reaching readiness. What the suite asserts is that the platform applied the objects it
///         said it applied; the operator's half is
///         <c>charts/managed/ferretdb/conformance.yaml</c>'s assertions, which have no runner yet.
///     </para>
/// </remarks>
/// <param name="fixture">The harness.</param>
public sealed class DocumentDbAccountLifecycleConformance(ClusterConformanceFixture<DocumentDbCase> fixture)
    : ClusterConformanceTests<DocumentDbCase>(fixture),
        IClassFixture<ClusterConformanceFixture<DocumentDbCase>>;

/// <summary>docs/plan/24 § Phase 1's exit criterion 3, against the managed document-database provider.</summary>
public sealed class DocumentDbAccountSiloKillConformance : SiloKillConformanceTests<DocumentDbCase>;
