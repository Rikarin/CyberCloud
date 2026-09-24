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

        var creator = new RecordingCreator();
        var request = new JsonObject { ["recoveryPoint"] = point, ["targetName"] = "main-restored" }.ToJsonString();
        var answer = await new RecoveryVaultRecoverHandler().InvokeAsync(
            Context(vault, connection, ns, RecoveryVaults.RecoverAction, request) with { Creator = creator },
            TestContext.Current.CancellationToken
        );

        answer.IsSuccess.ShouldBeTrue(answer.Error?.Message);

        var response = JsonNode.Parse(answer.GetValueOrThrow())!.AsObject();
        response["kind"]!.GetValue<string>().ShouldBe("CyberCloud.DBforPostgreSQL/servers");
        response["name"]!.GetValue<string>().ShouldBe("main-restored");
        response["namespace"]!.GetValue<string>().ShouldBe(ns);
        response["recoveryPoint"]!.GetValue<string>().ShouldBe(point);
        response["source"]!.GetValue<string>().ShouldBe(Ids.Server("main").Path);
        response["resourceId"]!.GetValue<string>().ShouldEndWith("/providers/CyberCloud.DBforPostgreSQL/servers/main-restored");

        using var parsed = JsonDocument.Parse(answer.GetValueOrThrow());
        RecoveryVaults.RecoverResponse.Validate(parsed.RootElement).IsSuccess.ShouldBeTrue();

        // ⚠ #30: the restore is a server the caller creates, and NOTHING is applied to the cluster by the
        // vault — the server's own reconciler renders the Cluster, its bucket and its key.
        connection.Applied.ShouldNotContain(static x => x.Target.Kind.Kind == "Cluster");

        var (type, name, apiVersion, body) = creator.Created.ShouldHaveSingleItem();
        type.ShouldBe(RecoveryVaults.PostgresServerType);
        name.ShouldBe("main-restored");
        apiVersion.ShouldBe(RecoveryVaults.PostgresServerApiVersion);

        var properties = JsonNode.Parse(body)!["properties"]!.AsObject();
        properties["restore"]!["recoveryPoint"]!.GetValue<string>().ShouldBe(point);
        properties["clusterId"]!.GetValue<string>().ShouldBe(Ids.Cluster.ToString("D"));
        properties["version"]!.GetValue<string>().ShouldBe("17", "the major off the source's image, 17.2");
        properties["replicas"]!.GetValue<int>().ShouldBe(2);
        properties["storage"]!["size"]!.GetValue<string>()
            .ShouldBe("50Gi", "a recovery needs a volume at least as large as the one it came from");
        properties["storage"]!["class"]!.GetValue<string>().ShouldBe("openebs-hostpath");
        properties["pooling"]!["enabled"]!.GetValue<bool>().ShouldBeFalse("the source ran no pooler");
        properties.ContainsKey("backup")
            .ShouldBeFalse("the copy takes the server defaults — backups on, to a bucket of its own GUID");
    }

    [Fact]
    public async Task ACreateTheWritePathRefusesIsTheRestoresRefusal() {
        // The caller may use the vault and may not create a server: the create's refusal is the answer,
        // unchanged — it is the manager's, with the manager's code.
        var (vault, connection, ns) = await ProtectedAsync("main");
        var schedule = RecoveryVaults.ScheduledBackupNameOf("nightly", "main");
        var point = schedule + "-ok";

        connection.Plant(
            RecoveryVaults.BackupRef(ns, point),
            RecoveryVaults.OperatorBackupJson(ns, schedule, "main", point, "completed", Now, Now)
        );

        var creator = new RecordingCreator {
            Refuse = new(ErrorCode.AuthorizationFailed, "you may not write CyberCloud.DBforPostgreSQL/servers here")
        };

        var answer = await new RecoveryVaultRecoverHandler().InvokeAsync(
            Context(
                vault,
                connection,
                ns,
                RecoveryVaults.RecoverAction,
                new JsonObject { ["recoveryPoint"] = point, ["targetName"] = "copy" }.ToJsonString()
            ) with { Creator = creator },
            TestContext.Current.CancellationToken
        );

        answer.Error!.Code.ShouldBe(ErrorCode.AuthorizationFailed);
        answer.Error.Message.ShouldContain("may not write");
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
                Now.AddDays(-1),
                majorVersion: 16
            )
        );

        var creator = new RecordingCreator();
        var request = new JsonObject { ["recoveryPoint"] = point, ["targetName"] = "main-again" }.ToJsonString();
        var answer = await new RecoveryVaultRecoverHandler().InvokeAsync(
            Context(vault, connection, ns, RecoveryVaults.RecoverAction, request) with { Creator = creator },
            TestContext.Current.CancellationToken
        );

        answer.IsSuccess.ShouldBeTrue(answer.Error?.Message);
        var properties = JsonNode.Parse(creator.Created.ShouldHaveSingleItem().Body)!["properties"]!.AsObject();
        properties["storage"]!["size"]!.GetValue<string>().ShouldBe("20Gi");
        properties["replicas"]!.GetValue<int>().ShouldBe(1);
        properties["restore"]!["recoveryPoint"]!.GetValue<string>().ShouldBe(point);

        // ⚠ #30's review: this used to be a guess of 17, and a 16 point restored into 17 never starts.
        properties["version"]!.GetValue<string>().ShouldBe("16", "the major came from a guess and not from the point");
    }

    [Fact]
    public async Task RecoverRefusesAPointThatRecordsNoMajorWhenTheSourceIsGone() {
        var (vault, connection, ns) = await ProtectedAsync("main");
        var schedule = RecoveryVaults.ScheduledBackupNameOf("nightly", "main");
        var point = schedule + "-unversioned";

        connection.Plant(
            RecoveryVaults.BackupRef(ns, point),
            RecoveryVaults.OperatorBackupJson(ns, schedule, "main", point, "completed", Now, Now, majorVersion: 0)
        );

        var creator = new RecordingCreator();
        var answer = await new RecoveryVaultRecoverHandler().InvokeAsync(
            Context(
                vault,
                connection,
                ns,
                RecoveryVaults.RecoverAction,
                new JsonObject { ["recoveryPoint"] = point, ["targetName"] = "main-guess" }.ToJsonString()
            ) with { Creator = creator },
            TestContext.Current.CancellationToken
        );

        answer.Error!.Code.ShouldBe(ErrorCode.PreconditionFailed);
        answer.Error.Target.ShouldBe("/recoveryPoint");
        creator.Created.ShouldBeEmpty("a server was created at a guessed major");
    }

    [Fact]
    public async Task BackupNowAppliesAPointTheScheduleOwnsAndTheListingFinds() {
        var (vault, connection, ns) = await ProtectedAsync("main");
        var schedule = RecoveryVaults.ScheduledBackupNameOf("nightly", "main");
        var clock = new FixedClock();

        var answer = await new RecoveryVaultBackupNowHandler(clock).InvokeAsync(
            Context(vault, connection, ns, RecoveryVaults.BackupNowAction, """{"item":"main"}"""),
            TestContext.Current.CancellationToken
        );

        answer.IsSuccess.ShouldBeTrue(answer.Error?.Message);
        using var parsed = JsonDocument.Parse(answer.GetValueOrThrow());
        RecoveryVaults.BackupNowResponse.Validate(parsed.RootElement).IsSuccess.ShouldBeTrue();

        var name = parsed.RootElement.GetProperty("recoveryPoint").GetString()!;
        name.ShouldBe(RecoveryVaults.OnDemandBackupNameOf(schedule, clock.UtcNow));

        var applied = connection.Applied.Single(static x => x.Target.Kind.Kind == "Backup");
        applied.Labels[RecoveryVaults.ParentScheduledBackupLabel].ShouldBe(schedule, "the label every reader of a vault's points selects on");
        applied.Labels[RecoveryVaults.ProtectedItemLabel].ShouldBe("main");
        applied.Labels[KubeLabels.ResourceId].ShouldBe(KubeLabels.GuidValue(vault.Id));

        var document = JsonNode.Parse(connection.Objects[RecordingConnection.Key(RecoveryVaults.BackupRef(ns, name))])!;
        document["spec"]!["cluster"]!["name"]!.GetValue<string>().ShouldBe("main");
        document["spec"]!["method"]!.GetValue<string>().ShouldBe(RecoveryVaults.BackupMethod);

        // ⚠ Owned by the schedule, so the vault's teardown takes it with the schedule's own points.
        var owner = document["metadata"]!["ownerReferences"]!.AsArray().Single()!.AsObject();
        owner["kind"]!.GetValue<string>().ShouldBe("ScheduledBackup");
        owner["name"]!.GetValue<string>().ShouldBe(schedule);
    }

    [Fact]
    public async Task BackupNowRefusesAnItemTheVaultHasNotScheduled() {
        var (vault, connection, ns) = await ProtectedAsync("main");

        var answer = await new RecoveryVaultBackupNowHandler(new FixedClock()).InvokeAsync(
            Context(vault, connection, ns, RecoveryVaults.BackupNowAction, """{"item":"reports"}"""),
            TestContext.Current.CancellationToken
        );

        answer.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
        answer.Error.Target.ShouldBe("/item");
        connection.Applied.ShouldNotContain(static x => x.Target.Kind.Kind == "Backup");
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
