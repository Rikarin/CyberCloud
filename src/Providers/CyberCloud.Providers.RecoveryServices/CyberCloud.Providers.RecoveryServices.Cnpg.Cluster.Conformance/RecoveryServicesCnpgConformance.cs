using CyberCloud.Bundle.Cluster.Conformance;
using CyberCloud.Cluster.Conformance.Infrastructure;
using CyberCloud.Conformance;
using CyberCloud.Core;
using CyberCloud.Kubernetes.Contracts;
using CyberCloud.Providers.RecoveryServices.Conformance;
using CyberCloud.Providers.RecoveryServices.Contracts;
using CyberCloud.ResourceManager;
using CyberCloud.ResourceManager.Contracts;
using k8s;
using k8s.Autorest;
using Shouldly;
using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.RecoveryServices.CnpgConformance;

/// <summary>
///     Installs CloudNativePG onto the shared k3s once, before the one class in this assembly runs —
///     through <c>charts/bundle/install.sh</c>, the way a real cluster gets it.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The whole point of this process is the operator.</b> Every other k3s lane applies its
///         custom resource against a stub the harness derives, and what it proves is that the
///         platform applied what it said it applied. A vault's schedule is worth nothing until a
///         controller acts on it: creates a <c>Backup</c>, labels it with the schedule's name, owns
///         it. So this fixture runs the bundle's cloudnative-pg row — and the storage row it needs, in
///         the roster's order — against the same k3s <c>ClusterInfrastructure</c> hands the harness,
///         and the harness then finds <c>postgresql.cnpg.io/v1</c> served and derives no stub for it.
///     </para>
///     <para>
///         ⚠ <b>Before the harness, or not at all.</b> A CRD stub created by the harness first would
///         make <c>helm install</c> refuse the real definitions as existing resources it does not own.
///         The class takes this fixture in its constructor, which is what orders it; the assembly
///         attribute alone would not.
///     </para>
///     <para>
///         ⚠ <b>Never throws when the daemon is absent, and never quietly succeeds either.</b> The
///         failure is remembered and the class turns it into its own loud skip, the same shape
///         <c>ClusterConformanceFixture</c> has.
///     </para>
/// </remarks>
public sealed class CloudNativePgInstalled : IAsyncLifetime {
    string? kubeconfigPath;

    /// <summary>Why the install did not happen, or <see langword="null" /> when it did.</summary>
    public string? Failure { get; private set; }

    /// <summary>The installer's output, for a failure message.</summary>
    public string Output { get; private set; } = string.Empty;

    /// <inheritdoc />
    public async ValueTask InitializeAsync() {
        var token = TestContext.Current.CancellationToken;

        if (!BundleInstaller.OnPath("bash") || !BundleInstaller.OnPath("helm")) {
            Failure = "install.sh is a bash script whose cloudnative-pg row is one `helm upgrade --install`; one of `bash` or `helm` is not on PATH.";
            return;
        }

        var endpoints = await ClusterInfrastructure.TryStartAsync(token);
        if (endpoints is null) {
            Failure = "the shared k3s did not come up: " + ClusterInfrastructure.SkipMessage("CyberCloud.RecoveryServices/vaults", "nothing yet");
            return;
        }

        try {
            using var yaml = new MemoryStream(Encoding.UTF8.GetBytes(endpoints.Kubeconfig));
            var raw = new k8s.Kubernetes(await KubernetesClientConfiguration.BuildConfigFromConfigFileAsync(yaml));

            if (await IsServedAsync(raw, token)) {
                return;
            }

            // ⚠ A FILE, because install.sh is a separate process and helm reads $KUBECONFIG. Deleted in
            // DisposeAsync — it holds a working client certificate for the container.
            kubeconfigPath = Path.Combine(Path.GetTempPath(), "cybercloud-vault-" + Guid.NewGuid().ToString("N") + ".kubeconfig");
            await File.WriteAllTextAsync(kubeconfigPath, endpoints.Kubeconfig, token);

            // ⚠ Both components, the storage row first by the roster's order and not the command
            // line's — CloudNativePgOnAnEmptyCluster asserts that ordering; here it is relied on.
            var run = await BundleInstaller.RunAsync(
                "--component " + BundleInstaller.CloudNativePgComponent + " --component " + BundleInstaller.OpenEbsLocalPvComponent,
                kubeconfigPath,
                token
            );

            Output = run.Output;

            if (run.ExitCode != 0) {
                Failure = $"charts/bundle/install.sh exited {run.ExitCode} installing cloudnative-pg and openebs-localpv onto the shared k3s.";
                return;
            }

            if (!await IsServedAsync(raw, token)) {
                Failure = "install.sh succeeded and postgresql.cnpg.io/v1 is still not served, so the component's `serves:` line is not true of the cluster it installed.";
            }
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            Failure = ex.GetType().Name + ": " + ex.Message;
        }
    }

    /// <summary>A loud skip naming what the calling test would have proved.</summary>
    /// <param name="wouldProve">The sentence.</param>
    public void Require(string wouldProve) {
        if (Failure is null) {
            return;
        }

        Assert.Skip(
            "SKIPPED — CyberCloud.RecoveryServices/vaults against a real CloudNativePG: the operator was not "
            + "installed, so nothing was checked. NEEDS: a Docker daemon able to run "
            + ClusterInfrastructure.K3sImage
            + ", and `bash` and `helm` on PATH. WOULD PROVE: "
            + wouldProve
            + " What went wrong: "
            + Failure
            + (Output.Length > 0 ? " Installer output:\n" + Output : string.Empty)
        );
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() {
        if (kubeconfigPath is not null) {
            try {
                File.Delete(kubeconfigPath);
            } catch (IOException) {
                // A kubeconfig for a container Ryuk is about to remove. Not a test result.
            }
        }

        return ValueTask.CompletedTask;
    }

    static async Task<bool> IsServedAsync(k8s.Kubernetes client, CancellationToken token) {
        try {
            await client.CustomObjects.ListClusterCustomObjectAsync(
                RecoveryVaults.ScheduledBackupKind.Group,
                RecoveryVaults.ScheduledBackupKind.Version,
                RecoveryVaults.ScheduledBackupKind.Plural,
                cancellationToken: token
            );

            return true;
        } catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound) {
            return false;
        }
    }
}

/// <summary>
///     The vault's case with a companion the real operator admits: the same PostgreSQL server, backups
///     <b>off</b>.
/// </summary>
/// <remarks>
///     ⚠ The only body CloudNativePG 1.30.0 admits from the PostgreSQL family today is one with no
///     backup section: with the default <c>backup.enabled: true</c> the real definition refuses an
///     empty <c>destinationPath</c>, and a filled-in one for <i>"missing credentials"</i>. So the
///     server this lane creates is one the vault must refuse — and asserting that refusal, against
///     the operator the bundle installs, is the honest statement of what the platform can protect.
/// </remarks>
public sealed class RecoveryVaultOnOperatorCase : IProviderCaseSource {
    /// <summary>The companion, with backups off.</summary>
    public static CompanionCase ProtectedServer { get; } =
        RecoveryVaultCase.ProtectedServer with { Body = cluster => WithBackupsOff(RecoveryVaultCase.ProtectedServer.BodyFor(cluster)) };

    /// <inheritdoc />
    public static ProviderConformanceCase ProviderCase => RecoveryVaultCase.ProviderCase;

    /// <inheritdoc />
    public static ImmutableArray<CompanionCase> Companions { get; } = [ProtectedServer];

    static string WithBackupsOff(string body) {
        var node = JsonNode.Parse(body)!.AsObject();
        node["properties"]!.AsObject()["backup"] = new JsonObject { ["enabled"] = false };
        return node.ToJsonString();
    }
}

/// <summary>
///     What only a real operator can show — in two halves, because the operator refuses the server the
///     first half would need.
/// </summary>
/// <remarks>
///     <para>
///         <b>The vault's half.</b> A vault naming the companion — the only PostgreSQL server the real
///         operator admits, backups off — fails at <c>/properties/protectedItems/0</c> with the
///         backups-off refusal, against the real API server, through the real view. That is the
///         platform today: until <c>charts/managed/postgres</c> renders a destination and credentials
///         the operator accepts, there is no server a vault can protect, and this lane says so rather
///         than passing over a stub.
///     </para>
///     <para>
///         <b>The operator's half.</b> The vault's own rendering — <c>RecoveryVaults.ScheduledBackupJson</c>,
///         applied by this test through the same <c>KubeCommand</c> with the same labels — is admitted
///         by CloudNativePG's ScheduledBackup webhook (six-field cron, <c>backupOwnerReference: self</c>),
///         the controller creates a <c>Backup</c> the platform never wrote, labels it
///         <c>cnpg.io/scheduled-backup</c> with the schedule's name and makes the schedule its owner,
///         the Backup controller fails it with <i>"cannot proceed with the backup as the cluster has no
///         backup section"</i>, and <c>RecoveryVaultListRecoveryPointsHandler</c> over the real
///         connection lists it with that phase and that reason. Every claim <c>SOURCE</c> makes about
///         the operator's labelling is measured here.
///     </para>
///     <para>
///         ⚠ <b>Budgeted at three minutes, measured at under one.</b> <c>immediate: true</c> makes the
///         controller create the first Backup on its first reconcile of the schedule, and the Backup
///         controller's prerequisite check fails it before any pod is involved.
///     </para>
/// </remarks>
/// <param name="cnpg">The install, which must have run first.</param>
/// <param name="fixture">The harness.</param>
public sealed class RecoveryVaultAgainstCloudNativePg(CloudNativePgInstalled cnpg, ClusterConformanceFixture<RecoveryVaultOnOperatorCase> fixture)
    : IClassFixture<ClusterConformanceFixture<RecoveryVaultOnOperatorCase>> {
    static readonly TimeSpan PointBudget = TimeSpan.FromMinutes(3);
    static readonly TimeSpan BetweenDrives = TimeSpan.FromSeconds(1);
    const int MaxDrives = 60;
    const string Name = "real-points";

    [Fact]
    public async Task TheVaultRefusesTheOnlyServerTheOperatorAdmitsAndTheOperatorActsOnTheVaultsRendering() {
        cnpg.Require(
            "that a vault naming a backups-off server is refused at its pointer against the real operator, that "
            + "CloudNativePG's webhook admits the vault's ScheduledBackup rendering, that its controller creates a "
            + "Backup labelled cnpg.io/scheduled-backup with the schedule's name and owned by the schedule, and that "
            + "listRecoveryPoints finds it through that label and reports the operator's own reason."
        );

        var harness = fixture.Require("the same, once the harness is up");
        var token = TestContext.Current.CancellationToken;
        var ns = ClusterConformanceHarness<RecoveryVaultOnOperatorCase>.Namespace;
        var item = RecoveryVaultOnOperatorCase.ProtectedServer.Name;

        // ── The vault's half: the honest refusal ────────────────────────────────────────────────
        var accepted = await harness.Manager.WriteAsync(
            new() {
                Path = ClusterConformanceHarness<RecoveryVaultOnOperatorCase>.Address(Name).Path,
                ApiVersion = RecoveryVaults.V2026,
                Verb = WriteVerb.Put,
                Body = RecoveryVaultCase.ProviderCase.Body(ClusterConformanceHarness<RecoveryVaultOnOperatorCase>.ClusterId),
                Caller = ClusterConformanceHarness<RecoveryVaultOnOperatorCase>.Caller()
            },
            token
        );

        accepted.IsSuccess.ShouldBeTrue(accepted.Error?.Message);
        var vault = ClusterConformanceHarness<RecoveryVaultOnOperatorCase>.Address(Name).WithId(accepted.GetValueOrThrow().Resource.Id);

        var status = await ConvergeAsync(harness, accepted.GetValueOrThrow().OperationId);

        status.State.ShouldBe(
            OperationState.Failed,
            "a vault protecting a server with backups off reported " + status.State + " against the real operator; "
            + "CloudNativePG fails every Backup of such a cluster, and the vault is supposed to say so before it schedules one"
        );
        status.Error.ShouldNotBeNull();
        status.Error.Target.ShouldBe("/properties/protectedItems/0");
        status.Error.Message.ShouldContain("backups disabled");

        // ── The operator's half: the vault's rendering, applied by hand, under the vault's labels ─
        using var desired = JsonDocument.Parse(RecoveryVaultCase.ProviderCase.Body(ClusterConformanceHarness<RecoveryVaultOnOperatorCase>.ClusterId));
        var schedule = RecoveryVaults.ScheduledBackupNameOf(Name, item);

        var applied = await KubeCommand.For(harness.Connection)
            .WithTenantId(vault.TenantId)
            .WithResourceId(vault)
            .InNamespace(ns)
            .WithKind(RecoveryVaults.ScheduledBackupKind)
            .WithApiVersion(RecoveryVaults.V2026)
            .WithLabels((RecoveryVaults.ProtectedItemLabel, item))
            .ObjectJson(RecoveryVaults.ScheduledBackupJson(Name, item, item, desired.RootElement))
            .ApplyAsync(token);

        applied.IsSuccess.ShouldBeTrue(
            "CloudNativePG's ScheduledBackup webhook refused the vault's own rendering: " + applied.Error?.Message
        );
        applied.GetValueOrThrow().Result.ShouldBe(ApplyResult.Created, applied.GetValueOrThrow().Message);

        try {
            JsonObject? backup = null;
            var deadline = DateTimeOffset.UtcNow + PointBudget;

            while (DateTimeOffset.UtcNow < deadline) {
                var listed = await harness.Raw.CustomObjects.ListNamespacedCustomObjectAsync(
                    RecoveryVaults.BackupKind.Group,
                    RecoveryVaults.BackupKind.Version,
                    ns,
                    RecoveryVaults.BackupKind.Plural,
                    labelSelector: RecoveryVaults.RecoveryPointSelector(schedule),
                    cancellationToken: token
                );

                var items = JsonNode.Parse(listed.ToString()!)?["items"]?.AsArray();
                var settled = items?.OfType<JsonObject>().FirstOrDefault(x => x["status"]?["phase"]?.GetValue<string>() is { Length: > 0 });

                if (settled is not null) {
                    backup = settled;
                    break;
                }

                await Task.Delay(TimeSpan.FromSeconds(3), token);
            }

            backup.ShouldNotBeNull(
                $"no Backup labelled {RecoveryVaults.RecoveryPointSelector(schedule)} reached a phase in `{ns}` within "
                + $"{PointBudget.TotalSeconds:F0} seconds of the ScheduledBackup being applied with immediate: true. The API "
                + "server admitted the schedule — the definition and the webhook are installed — so this is the controller "
                + "not acting on it, or labelling it differently from what the SOURCE file records."
            );

            // ⚠ THE ASSERTION THE CLASS IS FOR: the platform never wrote this object. It carries none of
            // ADR-013's seven, and it carries the operator's own label naming the vault's schedule.
            var labels = backup["metadata"]!["labels"]!.AsObject();
            labels[RecoveryVaults.ParentScheduledBackupLabel]!.GetValue<string>().ShouldBe(schedule);
            labels[RecoveryVaults.ClusterLabel]!.GetValue<string>().ShouldBe(item);
            labels.Select(x => x.Key).ShouldNotContain(x => x.StartsWith(KubeLabels.Prefix + "/", StringComparison.Ordinal));

            var owner = backup["metadata"]!["ownerReferences"]?.AsArray().Select(x => x!.AsObject()).SingleOrDefault(x => x["kind"]?.GetValue<string>() == "ScheduledBackup");
            owner.ShouldNotBeNull("backupOwnerReference: self did not make the schedule the Backup's owner, so deleting the vault would orphan its points");
            owner["name"]!.GetValue<string>().ShouldBe(schedule);

            var phase = backup["status"]!["phase"]!.GetValue<string>();
            phase.ShouldBe("failed", "a Backup of a cluster with no backup section is supposed to fail on the controller's prerequisite check; it reported " + phase);
            backup["status"]!["error"]!.GetValue<string>().ShouldContain("no backup section");

            // ── The listing, through the vault's own handler over the real connection ────────────
            var answer = await new RecoveryVaultListRecoveryPointsHandler().InvokeAsync(
                new(vault, RecoveryVaults.V2026, RecoveryVaults.ListRecoveryPointsAction, desired.RootElement, desired.RootElement, ns, harness.Connection, new UnavailableSecretResolver()),
                token
            );

            answer.IsSuccess.ShouldBeTrue(answer.Error?.Message);

            var response = JsonNode.Parse(answer.GetValueOrThrow())!.AsObject();
            response["count"]!.GetValue<int>().ShouldBeGreaterThanOrEqualTo(1);
            response["completed"]!.GetValue<int>().ShouldBe(0);

            var pointName = backup["metadata"]!["name"]!.GetValue<string>();
            var line = response["recoveryPoints"]!.AsArray().Select(x => x!.GetValue<string>())
                .SingleOrDefault(x => x.Contains(" " + pointName + " ", StringComparison.Ordinal));

            line.ShouldNotBeNull($"listRecoveryPoints did not list '{pointName}', which the controller labelled with the vault's schedule");
            line.ShouldStartWith(item + " " + pointName + " failed ");
            line.ShouldContain("no backup section");

            TestContext.Current.TestOutputHelper?.WriteLine("recovery point as listed: " + line);
        } finally {
            // The vault's own teardown, against the real API server: it lists its schedules by label
            // and deletes them, and the operator's Backups follow by owner reference.
            var deleted = await harness.Manager.DeleteAsync(
                new() { Path = vault.Path, ApiVersion = RecoveryVaults.V2026, Caller = ClusterConformanceHarness<RecoveryVaultOnOperatorCase>.Caller() },
                token
            );

            if (deleted.IsSuccess) {
                await ConvergeAsync(harness, deleted.GetValueOrThrow().OperationId);
            }
        }

        (await harness.Connection.GetAsync(RecoveryVaults.ScheduledBackupRef(ns, Name, item), token)).IsFailure
            .ShouldBeTrue("the vault's teardown left its schedule standing on the real API server");
    }

    static async Task<OperationStatus> ConvergeAsync(ClusterConformanceHarness<RecoveryVaultOnOperatorCase> harness, Guid operationId) {
        var operation = harness.Operation(ConformanceIds.Tenant, operationId);
        OperationStatus? last = null;

        for (var i = 0; i < MaxDrives; i++) {
            last = (await operation.DriveAsync()).GetValueOrThrow();
            if (last.IsTerminal) {
                return last;
            }

            await Task.Delay(BetweenDrives, TestContext.Current.CancellationToken);
        }

        last.ShouldNotBeNull();
        return last;
    }
}
