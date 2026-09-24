using CyberCloud.Conformance;
using CyberCloud.Core.Resources;
using CyberCloud.Kubernetes.Contracts;
using CyberCloud.Providers.DBforPostgreSQL.Contracts;
using CyberCloud.Providers.RecoveryServices.Contracts;
using CyberCloud.ResourceManager.Contracts;
using CyberCloud.ResourceManager.Reconcile;
using CyberCloud.Tenancy.Contracts;
using Shouldly;
using System.Collections.Immutable;
using System.Text;
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
    const string TargetAfterPurge = "protected-server-after-purge";

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
        //
        // ⚠ With the status CloudNativePG copies off the source Cluster — its bucket, its endpoint — and
        // barman's id of the base backup, which is everything a restore reads its origin from.
        var sourceStore = JsonNode.Parse(cluster.World.Read(RecoveryVaults.ClusterRef(ns, item))!)!
            ["spec"]!["backup"]!["barmanObjectStore"]!;
        var sourceBucket = sourceStore["destinationPath"]!.GetValue<string>();

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
                cluster.World.UidOf(RecoveryVaults.ScheduledBackupRef(ns, VaultName, item)),
                destinationPath: sourceBucket,
                backupId: "20260924T010000",
                endpointUrl: sourceStore["endpointURL"]!.GetValue<string>()
            )
        );

        // ── The restore, as the caller ─────────────────────────────────────────────────────────
        var (serverPath, operationId) = await RecoverAsync(vault, point, Target, token);

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
        spec["bootstrap"]!.AsObject().ContainsKey("initdb").ShouldBeFalse();

        // ⚠ #30's reclaim: from the source's BUCKET, through the restored server's OWN Secret — never
        // the Backup form, which reads the source's {source}-backup-s3 off the point's status and so
        // kept that Secret, and the source's resource group, from ever being reclaimed.
        spec["bootstrap"]!["recovery"]!["source"]!.GetValue<string>().ShouldBe(PostgresServers.RestoreSourceName);
        spec["bootstrap"]!["recovery"]!["recoveryTarget"]!["backupID"]!.GetValue<string>().ShouldBe("20260924T010000");
        var origin = spec["externalClusters"]![0]!["barmanObjectStore"]!;
        origin["destinationPath"]!.GetValue<string>().ShouldBe(sourceBucket);
        origin["s3Credentials"]!["accessKeyId"]!["name"]!.GetValue<string>().ShouldBe(PostgresServers.RestoreSecretName(Target));
        spec.ToJsonString().ShouldNotContain(PostgresServers.BackupSecretName(item));

        var copyBucket = spec["backup"]!["barmanObjectStore"]!["destinationPath"]!.GetValue<string>();

        copyBucket.ShouldStartWith("s3://pg-" + server.Id.ToString("N"));
        copyBucket.ShouldNotBe(sourceBucket, "a copy archiving into its source's bucket would overwrite the source's WAL");

        // ⚠ The vault applied no Cluster of its own — the first cut's shape, gone.
        JsonNode.Parse(cluster2)!["metadata"]!["labels"]![KubeLabels.ResourceId]!.GetValue<string>()
            .ShouldBe(KubeLabels.GuidValue(server.Id), "the restored Cluster is the SERVER's object, attributed to it by the drift scan");

        var sourceKeyId = KeyIdIn(PostgresServers.RestoreSecretRef(ns, Target));
        sourceKeyId.ShouldBe(KeyIdIn(PostgresServers.BackupSecretRef(ns, item)), "the restore's Secret does not hold the SOURCE's key");

        // ── THE RESTORE A VAULT EXISTS FOR: the source deleted and purged, then restored from ──────
        //
        // ⚠ #30's reclaim. The source's teardown used to leave {source}-backup-s3 because this is the
        // restore that read it; NamespaceReclaim then refused the group forever. Now the purge takes
        // it, and the second restore reads the source's key from the vault, which still holds it.
        var sourcePath = RecoveryVaultCase.ProtectedServer.Address().Path;
        var sourceWrite = new WriteRequest {
            Path = sourcePath,
            ApiVersion = RecoveryVaults.PostgresServerApiVersion,
            Caller = ProviderTestCluster<RecoverCase>.Caller()
        };

        var deleted = await cluster.Manager.DeleteAsync(sourceWrite, token);
        deleted.IsSuccess.ShouldBeTrue(deleted.Error?.Message);
        (await DriveAsync(deleted.GetValueOrThrow().OperationId)).State.ShouldBe(OperationState.Succeeded);

        var purged = await cluster.Manager.PurgeAsync(sourceWrite, token);
        purged.IsSuccess.ShouldBeTrue(purged.Error?.Message);
        (await DriveAsync(purged.GetValueOrThrow().OperationId)).State.ShouldBe(OperationState.Succeeded);

        cluster.World.Read(PostgresServers.BackupSecretRef(ns, item))
            .ShouldBeNull("the source was purged and its key Secret is still in the namespace, so the group can never be reclaimed");
        cluster.World.Read(RecoveryVaults.ClusterRef(ns, item)).ShouldBeNull();

        var (afterPath, afterOperation) = await RecoverAsync(vault, point, TargetAfterPurge, token);
        var after = await DriveAsync(afterOperation);
        after.State.ShouldBe(OperationState.Succeeded, $"a restore of a purged server's point failed: {after.Error?.Message}");
        afterPath.ShouldEndWith("/servers/" + TargetAfterPurge);

        var afterSpec = JsonNode.Parse(cluster.World.Read(RecoveryVaults.ClusterRef(ns, TargetAfterPurge))!)!["spec"]!;
        afterSpec["externalClusters"]![0]!["barmanObjectStore"]!["destinationPath"]!.GetValue<string>().ShouldBe(sourceBucket);
        KeyIdIn(PostgresServers.RestoreSecretRef(ns, TargetAfterPurge))
            .ShouldBe(sourceKeyId, "the vault's copy of the source's key did not reach the restore once the source was gone");
    }

    async Task<(string Path, Guid OperationId)> RecoverAsync(ResourceId vault, string point, string target, CancellationToken token) {
        var answer = await cluster.Manager.ActionAsync(
            new() {
                Path = vault.Path,
                ApiVersion = RecoveryVaults.V2026,
                Verb = WriteVerb.Post,
                Action = RecoveryVaults.RecoverAction,
                Body = new JsonObject { ["recoveryPoint"] = point, ["targetName"] = target }.ToJsonString(),
                Caller = ProviderTestCluster<RecoverCase>.Caller(subject: "restorer")
            },
            token
        );

        answer.IsSuccess.ShouldBeTrue(answer.Error?.Message);

        using var response = JsonDocument.Parse(answer.GetValueOrThrow().ActionResponse);
        return (
            response.RootElement.GetProperty("resourceId").GetString()!,
            Guid.Parse(response.RootElement.GetProperty("operationId").GetString()!)
        );
    }

    /// <summary>The access key id a key Secret in the fake cluster holds.</summary>
    string KeyIdIn(ObjectRef secret) {
        var json = cluster.World.Read(secret);
        json.ShouldNotBeNull($"'{secret}' is not in the cluster");

        return Encoding.UTF8.GetString(
            Convert.FromBase64String(JsonNode.Parse(json)!["data"]![PostgresServers.AccessKeyIdKey]!.GetValue<string>())
        );
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
