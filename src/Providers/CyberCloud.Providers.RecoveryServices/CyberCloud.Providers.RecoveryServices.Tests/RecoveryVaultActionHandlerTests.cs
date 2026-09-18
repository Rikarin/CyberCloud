using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.RecoveryServices.Tests;

/// <summary>
///     The two handlers, against a cluster the vault's reconciler has already written into and an
///     operator has left recovery points in.
/// </summary>
public sealed class RecoveryVaultActionHandlerTests {
    static readonly DateTimeOffset Now = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ListRecoveryPointsAnswersEveryItemsPointsNewestFirstWithTheCompletedCount() {
        var (vault, connection, ns) = await ProtectedAsync("main", "reports");
        var main = RecoveryVaults.ScheduledBackupNameOf("nightly", "main");
        var reports = RecoveryVaults.ScheduledBackupNameOf("nightly", "reports");

        connection.Plant(
            RecoveryVaults.BackupRef(ns, main + "-1"),
            RecoveryVaults.OperatorBackupJson(
                ns,
                main,
                "main",
                main + "-1",
                "completed",
                Now.AddDays(-2),
                Now.AddDays(-2).AddMinutes(3)
            )
        );
        connection.Plant(
            RecoveryVaults.BackupRef(ns, main + "-2"),
            RecoveryVaults.OperatorBackupJson(
                ns,
                main,
                "main",
                main + "-2",
                "failed",
                Now.AddDays(-1),
                Now.AddDays(-1).AddMinutes(1),
                error: "invalid destination: no bucket"
            )
        );
        connection.Plant(
            RecoveryVaults.BackupRef(ns, reports + "-1"),
            RecoveryVaults.OperatorBackupJson(
                ns,
                reports,
                "reports",
                reports + "-1",
                "completed",
                Now.AddHours(-3),
                Now.AddHours(-3).AddMinutes(2)
            )
        );
        // Somebody else's, under a schedule this vault does not own.
        connection.Plant(
            RecoveryVaults.BackupRef(ns, "weekly-main-1"),
            RecoveryVaults.OperatorBackupJson(ns, "weekly-main", "main", "weekly-main-1", "completed", Now, Now)
        );

        var answer = await new RecoveryVaultListRecoveryPointsHandler().InvokeAsync(
            Context(vault, connection, ns, RecoveryVaults.ListRecoveryPointsAction, "{}"),
            TestContext.Current.CancellationToken
        );

        answer.IsSuccess.ShouldBeTrue(answer.Error?.Message);
        var body = JsonNode.Parse(answer.GetValueOrThrow())!.AsObject();

        body["count"]!.GetValue<int>().ShouldBe(3);
        body["completed"]!.GetValue<int>().ShouldBe(2);

        var lines = body["recoveryPoints"]!.AsArray().Select(static x => x!.GetValue<string>()).ToList();
        lines.Count.ShouldBe(3);
        lines[0].ShouldStartWith("reports " + reports + "-1 completed", Case.Sensitive, "newest first");
        lines[1].ShouldStartWith("main " + main + "-2 failed");
        lines[1].ShouldEndWith(
            ": invalid destination: no bucket",
            customMessage: "the operator's error text is the line a tenant needs to read"
        );
        lines[2].ShouldStartWith("main " + main + "-1 completed");
        lines.ShouldAllBe(x => !x.Contains("weekly", StringComparison.Ordinal));

        // And the response validates against the shape the provider publishes.
        using var parsed = JsonDocument.Parse(answer.GetValueOrThrow());
        RecoveryVaults.ListRecoveryPointsResponse.Validate(parsed.RootElement).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task ListRecoveryPointsFailsRatherThanAnsweringZeroWhenTheClusterCannotBeListed() {
        var vault = Ids.Vault("nightly");
        var answer = await new RecoveryVaultListRecoveryPointsHandler()
            .InvokeAsync(
                Context(
                    vault,
                    new RecordingConnection { RefuseListing = true },
                    Ids.Namespace(vault),
                    RecoveryVaults.ListRecoveryPointsAction,
                    "{}"
                ),
                TestContext.Current.CancellationToken
            );

        answer.IsFailure.ShouldBeTrue("a vault that could not list answered a count, and zero reads as 'no backups'");
    }

    [Fact]
    public async Task RecoverBootstrapsANewClusterFromACompletedPointSizedLikeTheSource() {
        var (vault, connection, ns) = await ProtectedAsync("main");
        var schedule = RecoveryVaults.ScheduledBackupNameOf("nightly", "main");
        var point = schedule + "-20260917020000";

        connection.Plant(
            RecoveryVaults.BackupRef(ns, point),
            RecoveryVaults.OperatorBackupJson(
                ns,
                schedule,
                "main",
                point,
                "completed",
                Now.AddDays(-1),
                Now.AddDays(-1).AddMinutes(4)
            )
        );
        connection.Plant(
            RecoveryVaults.ClusterRef(ns, "main"),
            """{"apiVersion":"postgresql.cnpg.io/v1","kind":"Cluster","metadata":{"name":"main"},"spec":{"instances":2,"imageName":"ghcr.io/cloudnative-pg/postgresql:17.2","storage":{"size":"50Gi","storageClass":"openebs-hostpath"},"backup":{"barmanObjectStore":{"destinationPath":"s3://b/p"}}}}"""
        );

        var request = new JsonObject { ["recoveryPoint"] = point, ["targetName"] = "main-restored" }.ToJsonString();
        var answer = await new RecoveryVaultRecoverHandler().InvokeAsync(
            Context(vault, connection, ns, RecoveryVaults.RecoverAction, request),
            TestContext.Current.CancellationToken
        );

        answer.IsSuccess.ShouldBeTrue(answer.Error?.Message);

        var response = JsonNode.Parse(answer.GetValueOrThrow())!.AsObject();
        response["kind"]!.GetValue<string>().ShouldBe("Cluster");
        response["name"]!.GetValue<string>().ShouldBe("main-restored");
        response["namespace"]!.GetValue<string>().ShouldBe(ns);
        response["recoveryPoint"]!.GetValue<string>().ShouldBe(point);
        response["source"]!.GetValue<string>().ShouldBe(Ids.Server("main").Path);

        using var parsed = JsonDocument.Parse(answer.GetValueOrThrow());
        RecoveryVaults.RecoverResponse.Validate(parsed.RootElement).IsSuccess.ShouldBeTrue();

        var restored = connection.Applied.Single(static x => x.Target.Kind.Kind == "Cluster");
        var spec = JsonNode.Parse(restored.Body)!["spec"]!.AsObject();

        spec["bootstrap"]!["recovery"]!["backup"]!["name"]!.GetValue<string>().ShouldBe(point);
        spec["instances"]!.GetValue<int>().ShouldBe(1);
        spec["storage"]!["size"]!.GetValue<string>()
            .ShouldBe("50Gi", "a recovery needs a volume at least as large as the one it came from");
        spec["storage"]!["storageClass"]!.GetValue<string>().ShouldBe("openebs-hostpath");
        spec["imageName"]!.GetValue<string>().ShouldBe("ghcr.io/cloudnative-pg/postgresql:17.2");
        spec.ContainsKey("backup")
            .ShouldBeFalse(
                "a restored cluster that archived into the source's store under the source's name would overwrite its WAL"
            );

        restored.Labels[RecoveryVaults.RestoreRoleLabel].ShouldBe("restore");
        restored.Labels[RecoveryVaults.ProtectedItemLabel].ShouldBe("main");
        restored.Labels[KubeLabels.ResourceId].ShouldBe(KubeLabels.GuidValue(vault.Id));
    }

    [Fact]
    public async Task RecoverProceedsWithDefaultsWhenTheSourceClusterIsGone() {
        // ⚠ The disaster the vault was bought against: the server is gone and the point is all there is.
        var (vault, connection, ns) = await ProtectedAsync("main");
        var schedule = RecoveryVaults.ScheduledBackupNameOf("nightly", "main");
        var point = schedule + "-1";

        connection.Plant(
            RecoveryVaults.BackupRef(ns, point),
            RecoveryVaults.OperatorBackupJson(
                ns,
                schedule,
                "main",
                point,
                "completed",
                Now.AddDays(-1),
                Now.AddDays(-1)
            )
        );

        var request = new JsonObject { ["recoveryPoint"] = point, ["targetName"] = "main-again" }.ToJsonString();
        var answer = await new RecoveryVaultRecoverHandler().InvokeAsync(
            Context(vault, connection, ns, RecoveryVaults.RecoverAction, request),
            TestContext.Current.CancellationToken
        );

        answer.IsSuccess.ShouldBeTrue(answer.Error?.Message);
        var spec = JsonNode.Parse(connection.Applied.Single(static x => x.Target.Kind.Kind == "Cluster").Body)!["spec"]!
            .AsObject();
        spec["storage"]!["size"]!.GetValue<string>().ShouldBe("20Gi");
        spec.ContainsKey("imageName").ShouldBeFalse();
    }

    [Fact]
    public async Task RecoverRefusesAPointThatIsNotThisVaultsWithTheSameAnswerAsAnAbsentOne() {
        var (vault, connection, ns) = await ProtectedAsync("main");

        // Another vault's completed point, and a name nothing has.
        connection.Plant(
            RecoveryVaults.BackupRef(ns, "weekly-main-1"),
            RecoveryVaults.OperatorBackupJson(ns, "weekly-main", "main", "weekly-main-1", "completed", Now, Now)
        );

        var theirs = await Recover(vault, connection, ns, "weekly-main-1", "x");
        var absent = await Recover(vault, connection, ns, "nothing-here", "x");

        theirs.IsFailure.ShouldBeTrue("a vault restored a point another vault's schedule made");
        theirs.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
        absent.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);

        theirs.Error.Message.Replace("weekly-main-1", "POINT", StringComparison.Ordinal)
            .ShouldBe(
                absent.Error.Message.Replace("nothing-here", "POINT", StringComparison.Ordinal),
                "the two answers differ, which tells a caller the other vault's point exists"
            );

        connection.Applied.ShouldNotContain(x => x.Target.Kind.Kind == "Cluster");
    }

    [Fact]
    public async Task RecoverRefusesAFailedPointAndAnExistingTargetByName() {
        var (vault, connection, ns) = await ProtectedAsync("main");
        var schedule = RecoveryVaults.ScheduledBackupNameOf("nightly", "main");

        connection.Plant(
            RecoveryVaults.BackupRef(ns, schedule + "-bad"),
            RecoveryVaults.OperatorBackupJson(
                ns,
                schedule,
                "main",
                schedule + "-bad",
                "failed",
                Now,
                Now,
                error: "invalid destination"
            )
        );
        connection.Plant(
            RecoveryVaults.BackupRef(ns, schedule + "-good"),
            RecoveryVaults.OperatorBackupJson(ns, schedule, "main", schedule + "-good", "completed", Now, Now)
        );
        connection.Plant(
            RecoveryVaults.ClusterRef(ns, "taken"),
            """{"kind":"Cluster","metadata":{"name":"taken"},"spec":{}}"""
        );

        var failedPoint = await Recover(vault, connection, ns, schedule + "-bad", "fresh");
        failedPoint.IsFailure.ShouldBeTrue();
        failedPoint.Error!.Code.ShouldBe(ErrorCode.PreconditionFailed);
        failedPoint.Error.Message.ShouldContain("failed");
        failedPoint.Error.Message.ShouldContain("invalid destination");

        var taken = await Recover(vault, connection, ns, schedule + "-good", "taken");
        taken.IsFailure.ShouldBeTrue("a restore wrote into a cluster that was already there");
        taken.Error!.Code.ShouldBe(ErrorCode.ResourceAlreadyExists);
        taken.Error.Target.ShouldBe("/targetName");

        connection.Applied.ShouldNotContain(x => x.Target.Kind.Kind == "Cluster");
    }

    // ── A vault the reconciler has converged ──────────────────────────────────────────────────

    static async Task<(ResourceId Vault, RecordingConnection Connection, string Namespace)> ProtectedAsync(
        params string[] servers
    ) {
        var vault = Ids.Vault("nightly");
        var ns = Ids.Namespace(vault);
        var connection = new RecordingConnection();
        var view = new ScriptedView();

        foreach (var server in servers) {
            view.Showing(
                Ids.Server(server),
                Ids.ServerBody(),
                Ids.Cluster,
                ProvisioningState.Succeeded,
                RecoveryVaults.ClusterRef(ns, server)
            );
        }

        using var body = JsonDocument.Parse(
            RecoveryVaults.Body(Ids.Cluster, [.. servers.Select(static x => Ids.Server(x).Path)])
        );

        (await new RecoveryVaultReconciler(new FixedClock()).ReconcileAsync(
                Ids.Context(connection, vault, body.RootElement, view),
                TestContext.Current.CancellationToken
            ))
            .ShouldBe(ReconcileOutcome.Converged);

        return (vault, connection, ns);
    }

    static async Task<Result<string>> Recover(
        ResourceId vault,
        RecordingConnection connection,
        string ns,
        string point,
        string target
    ) =>
        await new RecoveryVaultRecoverHandler().InvokeAsync(
            Context(
                vault,
                connection,
                ns,
                RecoveryVaults.RecoverAction,
                new JsonObject { ["recoveryPoint"] = point, ["targetName"] = target }.ToJsonString()
            ),
            TestContext.Current.CancellationToken
        );

    static ActionContext Context(
        ResourceId vault,
        RecordingConnection connection,
        string ns,
        string action,
        string requestJson
    ) {
        using var request = JsonDocument.Parse(requestJson);
        using var desired = JsonDocument.Parse(RecoveryVaults.Body(Ids.Cluster, []));

        return new(
            vault,
            RecoveryVaults.V2026,
            action,
            request.RootElement.Clone(),
            desired.RootElement.Clone(),
            ns,
            connection,
            new UnavailableSecretResolver()
        );
    }
}
