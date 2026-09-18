using CyberCloud.Cluster.Conformance;
using CyberCloud.Cluster.Conformance.Infrastructure;
using CyberCloud.Providers.RecoveryServices.Conformance;

namespace CyberCloud.Providers.RecoveryServices.ClusterConformance;

/// <summary>
///     The cluster-backed suite, run against the backup-vault provider — over a k3s with the CRD
///     stubs the harness derives, like every other family.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             Two class declarations over the case <c>CyberCloud.Providers.RecoveryServices.Conformance</c>
///             already declares
///         </b>, plus the companion PostgreSQL server the harness now creates before
///         the vault's first assertion — against the same real API server, so the vault's view lists
///         a namespace k3s holds and finds the companion's <c>Cluster</c> by ADR-013's label on an
///         object the API server really admitted.
///     </para>
///     <para>
///         ⚠ <b>Stubs, not the operator, and the reason is the companion.</b> A stub admits the
///         PostgreSQL family's default body; CloudNativePG's real definition refuses it twice over —
///         an empty <c>destinationPath</c>, then <i>"missing credentials"</i> — so a lane with the real
///         operator installed cannot create the very server the vault protects
///         (<c>charts/managed/postgres/conformance.yaml § owed</c>, <c>the-default-bucket-is-not-filled-in</c>).
///         What the real operator does with the vault's schedule is a different question with a
///         different answer, and it lives in its own process:
///         <c>CyberCloud.Providers.RecoveryServices.Cnpg.Cluster.Conformance</c>. One k3s per
///         process is what keeps the two from installing over each other's definitions.
///     </para>
/// </remarks>
/// <param name="fixture">The harness.</param>
public sealed class RecoveryVaultLifecycleConformance(ClusterConformanceFixture<RecoveryVaultCase> fixture)
    : ClusterConformanceTests<RecoveryVaultCase>(fixture), IClassFixture<ClusterConformanceFixture<RecoveryVaultCase>>;

/// <summary>docs/plan/24 § Phase 1's exit criterion 3, against the backup-vault provider.</summary>
public sealed class RecoveryVaultSiloKillConformance : SiloKillConformanceTests<RecoveryVaultCase>;
