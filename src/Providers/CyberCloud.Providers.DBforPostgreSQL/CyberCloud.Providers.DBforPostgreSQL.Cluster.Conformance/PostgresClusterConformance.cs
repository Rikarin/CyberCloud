using CyberCloud.Cluster.Conformance;
using CyberCloud.Cluster.Conformance.Infrastructure;
using CyberCloud.Providers.DBforPostgreSQL.Conformance;

namespace CyberCloud.Providers.DBforPostgreSQL.ClusterConformance;

/// <summary>
///     The cluster-backed suite, run against the managed-PostgreSQL provider.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Two class declarations, and that is the entire cost.</b> The case is
///         <see cref="PostgresCase" /> — the one <c>CyberCloud.Providers.DBforPostgreSQL.Conformance</c>
///         already declares — so a provider under both halves of the suite is described exactly once.
///     </para>
///     <para>
///         ⚠ <b>Late, and the project file says why.</b> This family's Docker-free suite has promised
///         "its own <c>.Cluster.Conformance</c> project" in a skip message since the family landed,
///         and no such project existed until 2026-09-17. The <c>.csproj</c> beside this file records
///         how that stayed green and what this project does and does not prove without the operator.
///     </para>
/// </remarks>
/// <param name="fixture">The harness.</param>
public sealed class PostgresServerClusterLifecycleConformance(ClusterConformanceFixture<PostgresCase> fixture)
    : ClusterConformanceTests<PostgresCase>(fixture), IClassFixture<ClusterConformanceFixture<PostgresCase>>;

/// <summary>docs/plan/24 § Phase 1's exit criterion 3, against the managed-PostgreSQL provider.</summary>
public sealed class PostgresServerSiloKillConformance : SiloKillConformanceTests<PostgresCase>;
