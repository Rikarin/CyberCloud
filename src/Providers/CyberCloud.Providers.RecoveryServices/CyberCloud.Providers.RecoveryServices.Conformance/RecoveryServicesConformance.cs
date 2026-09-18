using CyberCloud.Conformance;
using CyberCloud.Conformance.Harness;
using CyberCloud.Core.Resources;
using CyberCloud.Providers.DBforPostgreSQL.Conformance;
using CyberCloud.Providers.RecoveryServices.Contracts;
using Shouldly;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.RecoveryServices.Conformance;

/// <summary>
///     <c>CyberCloud.RecoveryServices/vaults</c>, registered into the shared provider suite — with a
///     PostgreSQL server as its companion.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THE FIRST CASE WITH A <see cref="Companions" /> ENTRY, AND WHY ONE OBJECT PLANTED
///         THROUGH <see cref="ProviderConformanceCase.OperatorWritten" /> WOULD NOT HAVE DONE.</b>
///         The vault's reconciler finds the server's <c>Cluster</c> through
///         <c>ReconcileContext.View</c>, which resolves the item's path through the tenant's index and
///         its type through the silo's registry. A planted <c>Cluster</c> object has neither: no
///         index binding names it and no provider serves its type, so the view answers
///         <c>ResourceNotFound</c> and the vault refuses the item at its pointer — correctly. The
///         companion is a real <c>CyberCloud.DBforPostgreSQL/servers</c>, created through the write
///         path by the PostgreSQL family's own case object, whose reconciler applied the
///         <c>Cluster</c> the vault then reads the address of.
///     </para>
///     <para>
///         ⚠ <b><see cref="ProviderConformanceCase.Objects" /> is the ScheduledBackup and nothing
///         else.</b> The Backup objects a schedule produces are the operator's — the controller
///         creates them on each tick — and the suite asserts what the <i>reconciler</i> applied.
///         Listing a Backup would fail against every cluster with no CloudNativePG, which is every
///         cluster the Docker-free half runs against. What the recovery-point half of the type does
///         is proved by <see cref="ProviderConformanceCase.OperatorWritten" /> placing one completed
///         Backup the way the controller would — labelled with the schedule's name, owned by the
///         schedule — and by <c>listRecoveryPoints</c> reading it back in the action assertion.
///     </para>
///     <para>
///         ⚠ <b>The planted Backup names its owner with an empty uid, and the harness fills it in.</b>
///         <c>backupOwnerReference: self</c> is how the vault's teardown reaches its recovery points,
///         and the fake's garbage collector follows uids, never names — so the owner reference is
///         written the way <c>PostgresCase</c> writes CloudNativePG's claims, and the shared suite
///         resolves it against the ScheduledBackup the reconciler applied.
///     </para>
/// </remarks>
public sealed class RecoveryVaultCase : IProviderCaseSource {
    /// <summary>The PostgreSQL server every vault in the run protects.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <c>PostgresCase.ProviderCase</c>'s own body: backups enabled and fourteen days of
    ///         retention by that type's published defaults, which is what lets a vault with the default
    ///         policy accept it. A companion body with backups off would be refused by the vault at its
    ///         pointer — which <c>RecoveryVaultReconcilerTests</c> asserts and this suite must not.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The fake and the harness's CRD stub admit that body; CloudNativePG's real definition
    ///         does not.</b> An empty <c>destinationPath</c> is refused, and one that is filled in is
    ///         refused for <i>"missing credentials"</i>, because the PostgreSQL family renders no
    ///         <c>s3Credentials</c> — found by this type's operator lane on 2026-09-18 and recorded at
    ///         <c>charts/managed/postgres/conformance.yaml § owed</c>, <c>the-default-bucket-is-not-filled-in</c>.
    ///         So this companion runs against stubs, and the lane with the real operator
    ///         (<c>CyberCloud.Providers.RecoveryServices.Cnpg.Cluster.Conformance</c>) protects a
    ///         server with backups <i>off</i> and asserts the refusal the vault gives it.
    ///     </para>
    /// </remarks>
    public static CompanionCase ProtectedServer { get; } =
        new() { ProviderCase = PostgresCase.ProviderCase, Name = "protected-server" };

    /// <inheritdoc />
    public static ProviderConformanceCase ProviderCase { get; } =
        new() {
            DisplayName = "CyberCloud.RecoveryServices/vaults",
            CreateProvider = () => new RecoveryServicesProvider(),
            ReconcilerType = typeof(RecoveryVaultReconciler),
            CreateReconciler = clock => new RecoveryVaultReconciler(clock),
            Type = RecoveryVaults.Type,
            ApiVersion = RecoveryVaults.V2026,
            Body = cluster => RecoveryVaults.Body(cluster, [ProtectedServer.Address().Path]),
            // ⚠ Changes the SCHEDULE, which the rendered ScheduledBackup carries as spec.schedule —
            // the one field a hand edit would most plausibly rewrite. Retention would be a change the
            // object never shows, and a body that differed only where the reconciler renders nothing
            // would pass the update test while proving the update never left the grain.
            ChangedBody = cluster => RecoveryVaults.Body(cluster, [ProtectedServer.Address().Path], schedule: "30 3 * * *"),
            // Drops the required `/properties/policy/schedule`.
            InvalidBody = cluster => WithoutSchedule(RecoveryVaults.Body(cluster, [ProtectedServer.Address().Path])),
            InvalidBodyTarget = "/properties/policy/schedule",
            ActionName = RecoveryVaults.ListRecoveryPointsAction,
            Objects = (id, ns) => [RecoveryVaults.ScheduledBackupRef(ns, id.Name, ProtectedServer.Name)],
            // A cluster data plane, which the harness breaks and reads itself — see ProviderConformanceCase.DataPlane.
            DataPlane = null,
            StoragePrefix = null,
            OperatorWritten = (id, ns) => [
                (
                    RecoveryVaults.BackupRef(ns, RecoveryVaults.ScheduledBackupNameOf(id.Name, ProtectedServer.Name) + "-20260918020000"),
                    RecoveryVaults.OperatorBackupJson(
                        ns,
                        RecoveryVaults.ScheduledBackupNameOf(id.Name, ProtectedServer.Name),
                        ProtectedServer.Name,
                        RecoveryVaults.ScheduledBackupNameOf(id.Name, ProtectedServer.Name) + "-20260918020000",
                        RecoveryVaults.CompletedPhase,
                        new DateTimeOffset(2026, 9, 18, 2, 0, 0, TimeSpan.Zero),
                        new DateTimeOffset(2026, 9, 18, 2, 4, 12, TimeSpan.Zero),
                        // ⚠ Empty on purpose: the harness fills the uid in from the ScheduledBackup the
                        // reconciler applied, the way the operator's SetAsOwnedBy would.
                        ownerUid: string.Empty
                    )
                )
            ],
            ObjectMatchesDesired = match => {
                using var desired = JsonDocument.Parse(match.DesiredJson);
                return RecoveryVaults.Matches(match.ObjectJson, ProtectedServer.Name, desired.RootElement);
            }
        };

    /// <inheritdoc />
    public static ImmutableArray<CompanionCase> Companions { get; } = [ProtectedServer];

    /// <summary>A valid body with the required schedule removed.</summary>
    /// <param name="body">A valid body.</param>
    static string WithoutSchedule(string body) {
        var node = JsonNode.Parse(body)!.AsObject();
        node["properties"]!.AsObject()["policy"]!.AsObject().Remove("schedule");
        return node.ToJsonString();
    }
}

/// <summary>The shared suite, run against the backup-vault provider.</summary>
/// <param name="cluster">The harness.</param>
public sealed class RecoveryVaultConformance(ProviderTestCluster<RecoveryVaultCase> cluster)
    : ProviderConformanceTests<RecoveryVaultCase>(cluster), IClassFixture<ProviderTestCluster<RecoveryVaultCase>>;

/// <summary>The container-backed half, skipped loudly, against the backup-vault provider.</summary>
public sealed class RecoveryVaultClusterBackedConformance() : ClusterBackedConformanceTests(RecoveryVaultCase.ProviderCase);

/// <summary>
///     What this provider's registration into the shared suite is <b>shaped</b> like — the
///     companion half, which no earlier case has.
/// </summary>
public sealed class RecoveryVaultSuiteShapeTests {
    [Fact]
    public void TheCompanionIsThePostgresFamilysOwnCaseObjectAndTheBodyNamesItsPath() {
        // ⚠ Reached through a type PARAMETER, for the reason StorageSuiteShapeTests gives: a
        // `static virtual` interface member is only accessible through a constrained generic.
        var companions = CompanionsOf<RecoveryVaultCase>();

        companions.Length.ShouldBe(1);
        companions[0].ProviderCase.ShouldBeSameAs(
            PostgresCase.ProviderCase,
            "the companion is a SECOND DESCRIPTION of the PostgreSQL server rather than that family's own "
            + "case object, so the two can disagree the first time either changes"
        );

        companions[0].ProviderCase.Type.Depth.ShouldBe(1, "the harness builds no ancestor chain for a companion");

        // And the vault's own body protects exactly that companion, by the path the harness will bind.
        using var body = JsonDocument.Parse(RecoveryVaultCase.ProviderCase.Body(ConformanceIds.Cluster));
        RecoveryVaults.ProtectedItemPaths(body.RootElement).ShouldBe([companions[0].Address().Path]);

        // ⚠ Both bodies differ where the object shows it — the changed schedule reaches spec.schedule.
        using var changed = JsonDocument.Parse(RecoveryVaultCase.ProviderCase.ChangedBody(ConformanceIds.Cluster));
        RecoveryVaults.Schedule(changed.RootElement).ShouldNotBe(RecoveryVaults.Schedule(body.RootElement));
    }

    [Fact]
    public void ThePlantedRecoveryPointIsLabelledTheWayTheOperatorLabelsAndOwnedByTheSchedule() {
        var id = ProviderTestCluster<RecoveryVaultCase>.Address("shape").WithId(Guid.NewGuid());
        var (target, json) = RecoveryVaultCase.ProviderCase.OperatorWritten(id, "ns").Single();

        target.Kind.Kind.ShouldBe("Backup");

        var schedule = RecoveryVaults.ScheduledBackupNameOf(id.Name, RecoveryVaultCase.ProtectedServer.Name);
        RecoveryVaults.BackupParentOf(json).ShouldBe(schedule, "the operator's own label is the join listRecoveryPoints selects on");
        RecoveryVaults.BackupClusterOf(json).ShouldBe(RecoveryVaultCase.ProtectedServer.Name);

        var owner = JsonNode.Parse(json)!["metadata"]!["ownerReferences"]!.AsArray().Single()!.AsObject();
        owner["kind"]!.GetValue<string>().ShouldBe("ScheduledBackup");
        owner["name"]!.GetValue<string>().ShouldBe(schedule);
        owner["uid"]!.GetValue<string>().ShouldBeEmpty("an empty uid is what tells the harness to fill in the applied object's");

        // ⚠ And NONE of the platform's seven — the real shape of a Backup the controller created.
        var labels = JsonNode.Parse(json)!["metadata"]!["labels"]!.AsObject();
        labels.Select(x => x.Key).ShouldNotContain(x => x.StartsWith("cybercloud.io/", StringComparison.Ordinal));
    }

    static ImmutableArray<CompanionCase> CompanionsOf<TSource>()
        where TSource : IProviderCaseSource => TSource.Companions;
}
