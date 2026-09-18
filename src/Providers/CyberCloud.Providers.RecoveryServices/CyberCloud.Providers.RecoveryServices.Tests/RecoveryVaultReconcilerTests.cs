using CyberCloud.ResourceManager.Conformance;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.RecoveryServices.Tests;

/// <summary>
///     What only this reconciler can be wrong about: which answers from the view it acts on, which
///     refuse the item and at which pointer, and that the cluster it schedules a backup of is the
///     one the view named.
/// </summary>
public sealed class RecoveryVaultReconcilerTests {
    static readonly ObjectRef ServerCluster = RecoveryVaults.ClusterRef(Ids.Namespace(Ids.Vault("v")), "main");

    [Fact]
    public void TheReconcilerHoldsNoMutableState() {
        ReconcilerConformance.CheckNoHiddenState(new RecoveryVaultReconciler(new FixedClock())).ShouldBeEmpty();
    }

    // ── The shape ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AProtectedServerBecomesAScheduledBackupNamingTheClusterTheViewReturned() {
        var vault = Ids.Vault("nightly");
        var server = Ids.Server("main");
        var connection = new RecordingConnection();
        var watch = new RecordingWatch();

        // ⚠ The rendered Cluster is called `main-db`, NOT `main`: a reconciler that predicted the
        // cluster's name from the item's name would render `main` and fail here. The name is the
        // other provider's business, and the only way to it is the view.
        var view = new ScriptedView()
            .Showing(server, Ids.ServerBody(), Ids.Cluster, ProvisioningState.Succeeded, RecoveryVaults.ClusterRef(Ids.Namespace(vault), "main-db"));

        using var body = JsonDocument.Parse(RecoveryVaults.Body(Ids.Cluster, [server.Path], schedule: "15 4 * * 0", retentionDays: 7));

        var outcome = await Pass(connection, vault, body.RootElement, view, watch);

        outcome.ShouldBe(ReconcileOutcome.Converged);

        var applied = connection.Applied.ShouldHaveSingleItem();
        applied.Target.Kind.Kind.ShouldBe("ScheduledBackup");
        applied.Target.Kind.Group.ShouldBe("postgresql.cnpg.io");
        applied.Target.Namespace.ShouldBe(Ids.Namespace(vault), "the schedule goes into the protected server's namespace, which is the vault's own");
        applied.Target.Name.ShouldBe("nightly-main-19eac1a54fcd", "the vault, the item, and twelve hex digits of the pair's digest that keep `a`/`b-c` and `a-b`/`c` apart");

        var spec = Spec(applied.Body);

        // ⚠ Literals, not RecoveryVaults.SixFieldSchedule: deriving the expectation from the function
        // the renderer calls would compare the renderer to itself.
        spec["schedule"]!.GetValue<string>().ShouldBe("0 15 4 * * 0", "CloudNativePG's cron leads with a seconds field the tenant never writes");
        spec["cluster"]!["name"]!.GetValue<string>().ShouldBe("main-db");
        spec["backupOwnerReference"]!.GetValue<string>().ShouldBe("self");
        spec["method"]!.GetValue<string>().ShouldBe("barmanObjectStore");
        spec["immediate"]!.GetValue<bool>().ShouldBeTrue();

        applied.Labels[RecoveryVaults.ProtectedItemLabel].ShouldBe("main");
        applied.Labels[KubeLabels.ResourceId].ShouldBe(KubeLabels.GuidValue(vault.Id));

        // The view was asked about the server, twice — the contract and the addresses — and nothing else.
        view.Asked.ShouldBe([server.Path, server.Path]);

        // And the vault asked to hear about servers changing.
        watch.Subscribed.ShouldBe([RecoveryVaults.PostgresServerType]);
    }

    [Fact]
    public async Task TwoVaultsMayProtectOneServerWithTwoSchedulesOfTheirOwnNames() {
        var server = Ids.Server("main");
        var connection = new RecordingConnection();
        var view = new ScriptedView().Showing(server, Ids.ServerBody(), Ids.Cluster, ProvisioningState.Succeeded, ServerCluster);

        using var nightly = JsonDocument.Parse(RecoveryVaults.Body(Ids.Cluster, [server.Path], schedule: "0 2 * * *"));
        using var hourly = JsonDocument.Parse(RecoveryVaults.Body(Ids.Cluster, [server.Path], schedule: "0 * * * *"));

        (await Pass(connection, Ids.Vault("nightly"), nightly.RootElement, view)).ShouldBe(ReconcileOutcome.Converged);
        (await Pass(connection, Ids.Vault("hourly"), hourly.RootElement, view)).ShouldBe(ReconcileOutcome.Converged);

        connection.Applied.Select(x => x.Target.Name).ShouldBe(["nightly-main-19eac1a54fcd", "hourly-main-35248febfccd"]);
        Spec(connection.Applied[0].Body)["schedule"]!.GetValue<string>().ShouldBe("0 0 2 * * *");
        Spec(connection.Applied[1].Body)["schedule"]!.GetValue<string>().ShouldBe("0 0 * * * *");
    }

    [Fact]
    public async Task OneReconcilerInstanceServesTwoTenantsWithoutMixingThem() {
        var reconciler = new RecoveryVaultReconciler(new FixedClock());
        var connection = new RecordingConnection();

        var aliceVault = Ids.Vault("vault", Ids.TenantA, Ids.SubscriptionA);
        var bobVault = Ids.Vault("vault", Ids.TenantB, Ids.SubscriptionB);
        var aliceServer = Ids.Server("main", Ids.TenantA, Ids.SubscriptionA);
        var bobServer = Ids.Server("main", Ids.TenantB, Ids.SubscriptionB);

        var view = new ScriptedView()
            .Showing(aliceServer, Ids.ServerBody(), Ids.Cluster, ProvisioningState.Succeeded, RecoveryVaults.ClusterRef(Ids.Namespace(aliceVault), "main"))
            .Showing(bobServer, Ids.ServerBody(), Ids.Cluster, ProvisioningState.Succeeded, RecoveryVaults.ClusterRef(Ids.Namespace(bobVault), "main"));

        using var alice = JsonDocument.Parse(RecoveryVaults.Body(Ids.Cluster, [aliceServer.Path], schedule: "0 1 * * *"));
        using var bob = JsonDocument.Parse(RecoveryVaults.Body(Ids.Cluster, [bobServer.Path], schedule: "0 5 * * *"));

        (await reconciler.ReconcileAsync(Ids.Context(connection, aliceVault, alice.RootElement, view), TestContext.Current.CancellationToken)).ShouldBe(ReconcileOutcome.Converged);
        (await reconciler.ReconcileAsync(Ids.Context(connection, bobVault, bob.RootElement, view), TestContext.Current.CancellationToken)).ShouldBe(ReconcileOutcome.Converged);

        connection.Applied.Count.ShouldBe(2);
        connection.Applied[0].Target.Namespace.ShouldNotBe(connection.Applied[1].Target.Namespace);
        connection.Applied[0].Labels[KubeLabels.TenantId].ShouldBe(KubeLabels.GuidValue(Ids.TenantA));
        connection.Applied[1].Labels[KubeLabels.TenantId].ShouldBe(KubeLabels.GuidValue(Ids.TenantB));
        Spec(connection.Applied[0].Body)["schedule"]!.GetValue<string>().ShouldBe("0 0 1 * * *", "tenant A's schedule came back as tenant B's");
        Spec(connection.Applied[1].Body)["schedule"]!.GetValue<string>().ShouldBe("0 0 5 * * *");
    }

    // ── What refuses an item, and where it points ─────────────────────────────────────────────

    [Fact]
    public async Task AnItemTheViewCannotSeeFailsAtItsPointerAndSaysHowToGrant() {
        var vault = Ids.Vault("nightly");
        var connection = new RecordingConnection();

        // Nothing scripted: the view answers the seam's one 404 for absent, other-tenant and ungranted.
        using var body = JsonDocument.Parse(RecoveryVaults.Body(Ids.Cluster, [Ids.Server("ghost").Path]));

        var outcome = await Pass(connection, vault, body.RootElement, new ScriptedView());

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.Failed);
        outcome.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
        outcome.Error.Target.ShouldBe("/properties/protectedItems/0");
        outcome.Error.Message.ShouldContain("granted");
        outcome.Error.Message.ShouldContain("resource:" + vault.Id.ToString("N"), customMessage: "the message names the ReBAC subject a tenant has to grant reader to");

        connection.Applied.ShouldBeEmpty("a refused item must not leave a schedule behind");
    }

    [Fact]
    public async Task TheSecondItemsRefusalPointsAtTheSecondIndex() {
        var vault = Ids.Vault("nightly");
        var good = Ids.Server("main");
        var view = new ScriptedView().Showing(good, Ids.ServerBody(), Ids.Cluster, ProvisioningState.Succeeded, ServerCluster);

        using var body = JsonDocument.Parse(RecoveryVaults.Body(Ids.Cluster, [good.Path, Ids.Server("ghost").Path]));

        var outcome = await Pass(new RecordingConnection(), vault, body.RootElement, view);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.Failed);
        outcome.Error!.Target.ShouldBe("/properties/protectedItems/1");
    }

    [Fact]
    public async Task AnItemInAnotherTenantIsRefusedBeforeTheViewIsAsked() {
        var vault = Ids.Vault("nightly");
        var view = new ScriptedView();

        using var body = JsonDocument.Parse(RecoveryVaults.Body(Ids.Cluster, [Ids.Server("main", Ids.TenantB, Ids.SubscriptionB).Path]));

        var outcome = await Pass(new RecordingConnection(), vault, body.RootElement, view);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.Failed);
        outcome.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        outcome.Error.Target.ShouldBe("/properties/protectedItems/0");
        outcome.Error.Message.ShouldContain("tenant");

        // ⚠ Not even asked. The view would answer 404 anyway; refusing the spelling first is what
        // gives the tenant a message about THEIR body rather than about a resource that "does not exist".
        view.Asked.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnItemInAnotherResourceGroupIsRefusedByName() {
        var vault = Ids.Vault("nightly");

        using var body = JsonDocument.Parse(RecoveryVaults.Body(Ids.Cluster, [Ids.Server("main", group: "staging").Path]));

        var outcome = await Pass(new RecordingConnection(), vault, body.RootElement, new ScriptedView());

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.Failed);
        outcome.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        outcome.Error.Message.ShouldContain("staging");
        outcome.Error.Message.ShouldContain("items-in-other-resource-groups");
    }

    [Fact]
    public async Task AFileShareIsRefusedBecauseNothingBehindItSnapshots() {
        var vault = Ids.Vault("nightly");
        var share = new ResourceId(Ids.TenantA, Ids.SubscriptionA, "prod", new("CyberCloud.Storage", "accounts/fileShares"), "home", Guid.Empty, "media");

        using var body = JsonDocument.Parse(RecoveryVaults.Body(Ids.Cluster, [share.Path]));

        var outcome = await Pass(new RecordingConnection(), vault, body.RootElement, new ScriptedView());

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.Failed);
        outcome.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        outcome.Error.Target.ShouldBe("/properties/protectedItems/0");
        outcome.Error.Message.ShouldContain("snapshot");
    }

    [Fact]
    public async Task AServerOnAnotherClusterIsRefused() {
        var vault = Ids.Vault("nightly");
        var server = Ids.Server("main");
        var view = new ScriptedView().Showing(server, Ids.ServerBody(), Ids.OtherCluster, ProvisioningState.Succeeded, ServerCluster);

        using var body = JsonDocument.Parse(RecoveryVaults.Body(Ids.Cluster, [server.Path]));

        var outcome = await Pass(new RecordingConnection(), vault, body.RootElement, view);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.Failed);
        outcome.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        outcome.Error.Message.ShouldContain(Ids.OtherCluster.ToString("D"));
    }

    [Fact]
    public async Task AServerWithBackupsDisabledIsRefusedWithCloudNativePgsOwnReason() {
        var vault = Ids.Vault("nightly");
        var server = Ids.Server("main");
        var view = new ScriptedView().Showing(server, Ids.ServerBody(backupEnabled: false), Ids.Cluster, ProvisioningState.Succeeded, ServerCluster);

        using var body = JsonDocument.Parse(RecoveryVaults.Body(Ids.Cluster, [server.Path]));

        var outcome = await Pass(new RecordingConnection(), vault, body.RootElement, view);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.Failed);
        outcome.Error!.Target.ShouldBe("/properties/protectedItems/0");
        outcome.Error.Message.ShouldContain("no backup section");
    }

    [Fact]
    public async Task AServerWhoseRetentionIsShorterThanTheVaultsIsRefusedAndAnEqualOneIsNot() {
        var vault = Ids.Vault("nightly");
        var server = Ids.Server("main");
        var view = new ScriptedView().Showing(server, Ids.ServerBody(retentionDays: 7), Ids.Cluster, ProvisioningState.Succeeded, ServerCluster);

        using var longer = JsonDocument.Parse(RecoveryVaults.Body(Ids.Cluster, [server.Path], retentionDays: 30));
        var refused = await Pass(new RecordingConnection(), vault, longer.RootElement, view);

        refused.Kind.ShouldBe(ReconcileOutcomeKind.Failed);
        refused.Error!.Message.ShouldContain("7 day(s)");
        refused.Error.Message.ShouldContain("30");

        using var equal = JsonDocument.Parse(RecoveryVaults.Body(Ids.Cluster, [server.Path], retentionDays: 7));
        (await Pass(new RecordingConnection(), vault, equal.RootElement, view)).ShouldBe(ReconcileOutcome.Converged);
    }

    [Fact]
    public async Task ADefaultServerBodyMeansBackupsOnAndFourteenDays() {
        // ⚠ The write path stores a body as sent, so a server created from the portal's defaults has no
        // backup block at all. A reader that took absence for `false` would refuse every such server.
        var vault = Ids.Vault("nightly");
        var server = Ids.Server("main");
        var view = new ScriptedView().Showing(server, Ids.ServerBody(), Ids.Cluster, ProvisioningState.Succeeded, ServerCluster);

        using var fourteen = JsonDocument.Parse(RecoveryVaults.Body(Ids.Cluster, [server.Path], retentionDays: 14));
        (await Pass(new RecordingConnection(), vault, fourteen.RootElement, view)).ShouldBe(ReconcileOutcome.Converged);

        using var fifteen = JsonDocument.Parse(RecoveryVaults.Body(Ids.Cluster, [server.Path], retentionDays: 15));
        (await Pass(new RecordingConnection(), vault, fifteen.RootElement, view)).Kind.ShouldBe(ReconcileOutcomeKind.Failed);
    }

    [Fact]
    public async Task AServerThatHasNotRenderedItsClusterYetIsInProgressRatherThanRefused() {
        var vault = Ids.Vault("nightly");
        var server = Ids.Server("main");
        var view = new ScriptedView().Showing(server, Ids.ServerBody(), Ids.Cluster, ProvisioningState.Creating);

        using var body = JsonDocument.Parse(RecoveryVaults.Body(Ids.Cluster, [server.Path]));

        var outcome = await Pass(new RecordingConnection(), vault, body.RootElement, view);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.InProgress, outcome.ToString());
        outcome.Reason.ShouldContain("Creating");
    }

    [Fact]
    public async Task SeventeenItemsAreRefusedAtTheSeventeenthIndex() {
        var vault = Ids.Vault("nightly");
        var items = Enumerable.Range(0, 17).Select(i => Ids.Server("s" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)).Path).ToImmutableArray();

        using var body = JsonDocument.Parse(RecoveryVaults.Body(Ids.Cluster, items));

        var outcome = await Pass(new RecordingConnection(), vault, body.RootElement, new ScriptedView());

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.Failed);
        outcome.Error!.Target.ShouldBe("/properties/protectedItems/16");
    }

    [Fact]
    public async Task AVaultThatProtectsNothingConvergesAndAppliesNothing() {
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(RecoveryVaults.Body(Ids.Cluster, []));

        (await Pass(connection, Ids.Vault("empty"), body.RootElement, new ScriptedView())).ShouldBe(ReconcileOutcome.Converged);

        connection.Applied.ShouldBeEmpty();
    }

    // ── Updates, retention and the teardown ───────────────────────────────────────────────────

    [Fact]
    public async Task AnItemRemovedFromTheBodyLosesItsScheduleOnTheNextPass() {
        var vault = Ids.Vault("nightly");
        var main = Ids.Server("main");
        var reports = Ids.Server("reports");
        var connection = new RecordingConnection();
        var ns = Ids.Namespace(vault);

        var view = new ScriptedView()
            .Showing(main, Ids.ServerBody(), Ids.Cluster, ProvisioningState.Succeeded, RecoveryVaults.ClusterRef(ns, "main"))
            .Showing(reports, Ids.ServerBody(), Ids.Cluster, ProvisioningState.Succeeded, RecoveryVaults.ClusterRef(ns, "reports"));

        using var both = JsonDocument.Parse(RecoveryVaults.Body(Ids.Cluster, [main.Path, reports.Path]));
        (await Pass(connection, vault, both.RootElement, view)).ShouldBe(ReconcileOutcome.Converged);
        connection.Holds(RecoveryVaults.ScheduledBackupRef(ns, "nightly", "reports")).ShouldBeTrue();

        using var one = JsonDocument.Parse(RecoveryVaults.Body(Ids.Cluster, [main.Path]));
        (await Pass(connection, vault, one.RootElement, view)).ShouldBe(ReconcileOutcome.Converged);

        connection.Holds(RecoveryVaults.ScheduledBackupRef(ns, "nightly", "reports")).ShouldBeFalse("the schedule of an item that left the body was left standing");
        connection.Holds(RecoveryVaults.ScheduledBackupRef(ns, "nightly", "main")).ShouldBeTrue();
        connection.Deleted.ShouldHaveSingleItem().Name.ShouldBe("nightly-reports-59a7c713d1d3");
    }

    [Fact]
    public async Task RecoveryPointsOlderThanTheRetentionArePrunedAndYoungerOnesKept() {
        var vault = Ids.Vault("nightly");
        var server = Ids.Server("main");
        var connection = new RecordingConnection();
        var ns = Ids.Namespace(vault);
        var now = new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);
        var schedule = RecoveryVaults.ScheduledBackupNameOf("nightly", "main");

        // Three points the operator made: 20 days old, 6 days old, and one still running with no stop.
        connection.Plant(RecoveryVaults.BackupRef(ns, schedule + "-old"), RecoveryVaults.OperatorBackupJson(ns, schedule, "main", schedule + "-old", "completed", now.AddDays(-20), now.AddDays(-20).AddMinutes(5)));
        connection.Plant(RecoveryVaults.BackupRef(ns, schedule + "-young"), RecoveryVaults.OperatorBackupJson(ns, schedule, "main", schedule + "-young", "completed", now.AddDays(-6), now.AddDays(-6).AddMinutes(5)));
        connection.Plant(RecoveryVaults.BackupRef(ns, schedule + "-running"), RecoveryVaults.OperatorBackupJson(ns, schedule, "main", schedule + "-running", "running", now.AddDays(-30), null).Replace("\"creationTimestamp\"", "\"created\"", StringComparison.Ordinal));

        // And one of ANOTHER vault's, which the selector must not reach.
        connection.Plant(RecoveryVaults.BackupRef(ns, "weekly-main-old"), RecoveryVaults.OperatorBackupJson(ns, "weekly-main", "main", "weekly-main-old", "completed", now.AddDays(-40), now.AddDays(-40)));

        var view = new ScriptedView().Showing(server, Ids.ServerBody(retentionDays: 30), Ids.Cluster, ProvisioningState.Succeeded, ServerCluster);
        using var body = JsonDocument.Parse(RecoveryVaults.Body(Ids.Cluster, [server.Path], retentionDays: 14));

        var reconciler = new RecoveryVaultReconciler(new FixedClock { UtcNow = now });
        (await reconciler.ReconcileAsync(Ids.Context(connection, vault, body.RootElement, view), TestContext.Current.CancellationToken)).ShouldBe(ReconcileOutcome.Converged);

        connection.Deleted.Select(x => x.Name).ShouldBe([schedule + "-old"]);
        connection.Holds(RecoveryVaults.BackupRef(ns, schedule + "-young")).ShouldBeTrue();
        connection.Holds(RecoveryVaults.BackupRef(ns, schedule + "-running")).ShouldBeTrue("a point with neither a stop nor a creation stamp is one the operator is still working on");
        connection.Holds(RecoveryVaults.BackupRef(ns, "weekly-main-old")).ShouldBeTrue("another vault's point was pruned");
    }

    [Fact]
    public async Task AListingThatFailsFailsThePassRatherThanSkippingThePrune() {
        var vault = Ids.Vault("nightly");
        var server = Ids.Server("main");
        var view = new ScriptedView().Showing(server, Ids.ServerBody(), Ids.Cluster, ProvisioningState.Succeeded, ServerCluster);

        using var body = JsonDocument.Parse(RecoveryVaults.Body(Ids.Cluster, [server.Path]));

        var outcome = await Pass(new RecordingConnection { RefuseListing = true }, vault, body.RootElement, view);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.Failed);
        outcome.Error!.Message.ShouldContain("cannot list");
    }

    [Fact]
    public async Task DeleteRemovesTheSchedulesAndLeavesARestoredClusterStanding() {
        var vault = Ids.Vault("nightly");
        var server = Ids.Server("main");
        var connection = new RecordingConnection();
        var ns = Ids.Namespace(vault);
        var view = new ScriptedView().Showing(server, Ids.ServerBody(), Ids.Cluster, ProvisioningState.Succeeded, ServerCluster);

        using var body = JsonDocument.Parse(RecoveryVaults.Body(Ids.Cluster, [server.Path]));
        (await Pass(connection, vault, body.RootElement, view)).ShouldBe(ReconcileOutcome.Converged);

        // A restored cluster, as the recover handler leaves one: the vault's labels plus the role.
        var restored = RecoveryVaults.ClusterRef(ns, "main-restored");
        await KubeCommand.For(connection)
            .WithTenantId(vault.TenantId)
            .WithResourceId(vault)
            .InNamespace(ns)
            .WithKind(RecoveryVaults.ClusterKind)
            .WithApiVersion(RecoveryVaults.V2026)
            .WithLabels((RecoveryVaults.ProtectedItemLabel, "main"), (RecoveryVaults.RestoreRoleLabel, RecoveryVaults.RestoreRoleValue))
            .ObjectJson(RecoveryVaults.RestoredClusterJson("main-restored", "nightly-main-x", "{}"))
            .ApplyAsync(TestContext.Current.CancellationToken);

        var reconciler = new RecoveryVaultReconciler(new FixedClock());
        var outcome = await reconciler.DeleteAsync(Ids.Context(connection, vault, body.RootElement, view), TestContext.Current.CancellationToken);

        outcome.ShouldBe(ReconcileOutcome.Converged);
        connection.Holds(RecoveryVaults.ScheduledBackupRef(ns, "nightly", "main")).ShouldBeFalse();
        connection.Holds(restored).ShouldBeTrue("deleting the vault deleted the database the tenant had just recovered");
    }

    [Fact]
    public async Task AnUnreachableClusterSuspendsRatherThanFails() {
        var vault = Ids.Vault("nightly");
        var server = Ids.Server("main");
        var view = new ScriptedView().Showing(server, Ids.ServerBody(), Ids.Cluster, ProvisioningState.Succeeded, ServerCluster);

        using var body = JsonDocument.Parse(RecoveryVaults.Body(Ids.Cluster, [server.Path]));

        var outcome = await Pass(new RecordingConnection { Suspend = true }, vault, body.RootElement, view);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.InProgress);
        outcome.Reason.ShouldContain("cannot reach");
    }

    [Fact]
    public async Task ObserveReportsTheSchedulesOnTheClusterAgainstTheItemsInTheBody() {
        var vault = Ids.Vault("nightly");
        var server = Ids.Server("main");
        var connection = new RecordingConnection();
        var view = new ScriptedView().Showing(server, Ids.ServerBody(), Ids.Cluster, ProvisioningState.Succeeded, ServerCluster);

        using var body = JsonDocument.Parse(RecoveryVaults.Body(Ids.Cluster, [server.Path]));
        var reconciler = new RecoveryVaultReconciler(new FixedClock());

        var before = await reconciler.ObserveAsync(new(vault, RecoveryVaults.V2026, body.RootElement, Ids.Namespace(vault), connection), TestContext.Current.CancellationToken);
        before.Exists.ShouldBeFalse();

        (await reconciler.ReconcileAsync(Ids.Context(connection, vault, body.RootElement, view), TestContext.Current.CancellationToken)).ShouldBe(ReconcileOutcome.Converged);

        var after = await reconciler.ObserveAsync(new(vault, RecoveryVaults.V2026, body.RootElement, Ids.Namespace(vault), connection), TestContext.Current.CancellationToken);
        after.Exists.ShouldBeTrue();
        after.Summary.ShouldBe("1 schedule(s), one per protected item");
    }

    static async Task<ReconcileOutcome> Pass(RecordingConnection connection, ResourceId vault, JsonElement desired, IResourceView view, IResourceWatch? watch = null) =>
        await new RecoveryVaultReconciler(new FixedClock())
            .ReconcileAsync(Ids.Context(connection, vault, desired, view, watch), TestContext.Current.CancellationToken);

    static JsonObject Spec(string objectJson) => JsonNode.Parse(objectJson)!["spec"]!.AsObject();
}
