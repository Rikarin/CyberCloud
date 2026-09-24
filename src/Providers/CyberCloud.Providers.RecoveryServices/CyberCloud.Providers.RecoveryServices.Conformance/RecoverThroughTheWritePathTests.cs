using CyberCloud.Conformance;
using CyberCloud.Core.Resources;
using CyberCloud.Kubernetes.Contracts;
using CyberCloud.Providers.RecoveryServices.Contracts;
using CyberCloud.ResourceManager.Contracts;
using CyberCloud.ResourceManager.Reconcile;
using CyberCloud.Tenancy.Contracts;
using Shouldly;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.RecoveryServices.Conformance;

/// <summary>
///     #30's third owed row, closed: <c>recover</c> creates a <c>CyberCloud.DBforPostgreSQL/servers</c>
///     resource through the real write path, and that server's own reconciler renders the restore.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every component is the shipping one</b> — the manager, the action dispatcher, the
///         <c>CallerResourceCreator</c> it binds, both families' reconcilers in one silo, the view — and
///         the only doubles are the harness's own: the fake API server and the in-memory store and vault.
///         The CloudNativePG lane repeats this against a real operator and a real SeaweedFS.
///     </para>
///     <para>
///         ⚠ <b>The recovery point is planted the way the operator would write it</b> — owned by the
///         vault's schedule, labelled with the schedule's name — because no controller runs here.
///     </para>
/// </remarks>
/// <param name="cluster">The vault's Docker-free harness, with the PostgreSQL family as its companion.</param>
public sealed class RecoverThroughTheWritePathTests(ProviderTestCluster<RecoverCase> cluster)
    : IClassFixture<ProviderTestCluster<RecoverCase>> {
    const string VaultName = "restores";
    const string Target = "protected-server-copy";

    [Fact]
    public async Task RecoverCreatesAServerResourceThatBootstrapsFromThePointIntoABucketOfItsOwn() {
        var token = TestContext.Current.CancellationToken;
        var vault = ProviderTestCluster<RecoverCase>.Address(VaultName);
        var ns = ReconcileDriver.NamespaceFor(vault);
        var item = RecoveryVaultCase.ProtectedServer.Name;

        var created = await cluster.Manager.WriteAsync(
            new() {
                Path = vault.Path,
                ApiVersion = RecoveryVaults.V2026,
                Verb = WriteVerb.Put,
                Body = RecoveryVaultCase.ProviderCase.Body(ProviderTestCluster<RecoverCase>.ClusterId),
                Caller = ProviderTestCluster<RecoverCase>.Caller()
            },
            token
        );

        created.IsSuccess.ShouldBeTrue(created.Error?.Message);
        (await DriveAsync(created.GetValueOrThrow().OperationId)).State.ShouldBe(OperationState.Succeeded);

        // ── A completed point, as the ScheduledBackup controller would have written it ───────────
        var schedule = RecoveryVaults.ScheduledBackupNameOf(VaultName, item);
        var point = schedule + "-20260924010000";
        var at = new DateTimeOffset(2026, 9, 24, 1, 0, 0, TimeSpan.Zero);

        cluster.World.MutateBehindTheirBack(
            RecoveryVaults.BackupRef(ns, point),
            RecoveryVaults.OperatorBackupJson(
                ns,
                schedule,
                item,
                point,
                RecoveryVaults.CompletedPhase,
                at,
                at.AddMinutes(3),
                cluster.World.UidOf(RecoveryVaults.ScheduledBackupRef(ns, VaultName, item))
            )
        );

        // ── The restore, as the caller ─────────────────────────────────────────────────────────
        var answer = await cluster.Manager.ActionAsync(
            new() {
                Path = vault.Path,
                ApiVersion = RecoveryVaults.V2026,
                Verb = WriteVerb.Post,
                Action = RecoveryVaults.RecoverAction,
                Body = new JsonObject { ["recoveryPoint"] = point, ["targetName"] = Target }.ToJsonString(),
                Caller = ProviderTestCluster<RecoverCase>.Caller(subject: "restorer")
            },
            token
        );

        answer.IsSuccess.ShouldBeTrue(answer.Error?.Message);

        using var response = JsonDocument.Parse(answer.GetValueOrThrow().ActionResponse);
        var serverPath = response.RootElement.GetProperty("resourceId").GetString()!;
        var operationId = Guid.Parse(response.RootElement.GetProperty("operationId").GetString()!);

        serverPath.ShouldEndWith("/providers/CyberCloud.DBforPostgreSQL/servers/" + Target);

        var restore = await DriveAsync(operationId);
        restore.State.ShouldBe(OperationState.Succeeded, restore.Error?.Message);

        // ── It is a resource: readable, authored by the caller, carrying the point ──────────────
        var read = await cluster.Manager.ReadAsync(
            new() {
                Path = serverPath,
                ApiVersion = RecoveryVaults.PostgresServerApiVersion,
                Caller = ProviderTestCluster<RecoverCase>.Caller()
            },
            token
        );

        read.IsSuccess.ShouldBeTrue(read.Error?.Message);
        var server = read.GetValueOrThrow();
        server.ProvisioningState.ShouldBe(ProvisioningState.Succeeded);
        server.CreatedBy.ShouldContain("restorer", Case.Sensitive, "the restore is the caller's create, not the vault's");
        server.Body.ShouldContain(point);

        // ── And its own reconciler rendered the restore, into a bucket that is not the source's ─
        var cluster2 = cluster.World.Read(RecoveryVaults.ClusterRef(ns, Target));
        cluster2.ShouldNotBeNull("the server converged and no Cluster was rendered for it");

        var spec = JsonNode.Parse(cluster2)!["spec"]!.AsObject();
        spec["bootstrap"]!["recovery"]!["backup"]!["name"]!.GetValue<string>().ShouldBe(point);
        spec["bootstrap"]!.AsObject().ContainsKey("initdb").ShouldBeFalse();

        var copyBucket = spec["backup"]!["barmanObjectStore"]!["destinationPath"]!.GetValue<string>();
        var sourceBucket = JsonNode.Parse(cluster.World.Read(RecoveryVaults.ClusterRef(ns, item))!)!
            ["spec"]!["backup"]!["barmanObjectStore"]!["destinationPath"]!.GetValue<string>();

        copyBucket.ShouldStartWith("s3://pg-" + server.Id.ToString("N"));
        copyBucket.ShouldNotBe(sourceBucket, "a copy archiving into its source's bucket would overwrite the source's WAL");

        // ⚠ The vault applied no Cluster of its own — the first cut's shape, gone.
        JsonNode.Parse(cluster2)!["metadata"]!["labels"]![KubeLabels.ResourceId]!.GetValue<string>()
            .ShouldBe(KubeLabels.GuidValue(server.Id), "the restored Cluster is the SERVER's object, attributed to it by the drift scan");
    }

    async Task<OperationStatus> DriveAsync(Guid operationId) {
        var operation = cluster.Operation(ConformanceIds.Tenant, operationId);
        OperationStatus? last = null;

        for (var i = 0; i < 40; i++) {
            last = (await operation.DriveAsync()).GetValueOrThrow();

            if (last.IsTerminal) {
                return last;
            }
        }

        return last!;
    }
}

/// <summary>
///     The vault's case under a type of its own, so <see cref="RecoverThroughTheWritePathTests" /> gets
///     a harness of its own.
/// </summary>
/// <remarks>
///     ⚠ <c>ConformanceState</c> is static per case source, so a second class fixed on
///     <see cref="RecoveryVaultCase" /> would share the shared suite's fake API server — and the
///     objects this class's restore leaves behind would fail that suite's "nothing was applied"
///     assertions for a reason neither class owns.
/// </remarks>
public sealed class RecoverCase : IProviderCaseSource {
    /// <inheritdoc />
    public static ProviderConformanceCase ProviderCase => RecoveryVaultCase.ProviderCase;

    /// <inheritdoc />
    public static ImmutableArray<CompanionCase> Companions => RecoveryVaultCase.Companions;
}
