using CyberCloud.Bundle.Cluster.Conformance;
using CyberCloud.Cluster.Conformance.Infrastructure;
using CyberCloud.Conformance;
using CyberCloud.Core;
using CyberCloud.Core.Resources;
using CyberCloud.Core.Time;
using CyberCloud.Kubernetes.Contracts;
using CyberCloud.ObjectStorage;
using CyberCloud.Providers.DBforPostgreSQL.Contracts;
using CyberCloud.Providers.RecoveryServices.Conformance;
using CyberCloud.Providers.RecoveryServices.Contracts;
using CyberCloud.ResourceManager.Contracts;
using CyberCloud.Tenancy.Contracts;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Shouldly;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using k8s;
using k8s.Autorest;

namespace CyberCloud.Providers.RecoveryServices.CnpgConformance;

/// <summary>
///     The platform's object store, for this process: a SeaweedFS with its IAM API, on the Docker
///     bridge the k3s node is on, so a CloudNativePG pod reaches it by address and the silo by a
///     mapped port.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Two endpoints, and the difference is the one <see cref="ObjectStorageOptions.DataPlaneEndpoint" />
///         exists for.</b> The silo runs on the host and reaches the store through Docker's port
///         mapping; a pod in k3s cannot, and reaches the container at its bridge address instead.
///         That is the production shape too — a management network for the platform, a data network
///         for the tenant's workloads — measured here rather than assumed.
///     </para>
///     <para>
///         <c>chrislusf/seaweedfs:3.80</c>, <c>weed server -s3 -iam</c>, no identities file: the IAM
///         API writes the identities, and a <c>-s3.config</c> file would override them (see
///         <see cref="SeaweedFsObjectStoreGrants" />). The administrator is created by
///         <see cref="SeaweedFsObjectStoreGrants.BootstrapAdministratorAsync" />, the install step.
///     </para>
/// </remarks>
public static class PlatformObjectStore {
    /// <summary>The image, pinned where <c>CyberCloud.ObjectStorage.Tests</c> pins it.</summary>
    public const string Image = "chrislusf/seaweedfs:3.80";

    /// <summary>The store's section, once it is up; <see langword="null" /> before.</summary>
    public static ObjectStorageOptions? Options { get; set; }

    /// <summary>The grants the silo is given, over <see cref="Options" />.</summary>
    public static IObjectStoreGrants Grants() =>
        new SeaweedFsObjectStoreGrants(
            new HttpClient { Timeout = TimeSpan.FromSeconds(30) },
            Options ?? throw new InvalidOperationException("the platform object store was not started"),
            new SystemClock()
        );
}

/// <summary>
///     Installs CloudNativePG onto the shared k3s once, before the one class in this assembly runs —
///     through <c>charts/bundle/install.sh</c>, the way a real cluster gets it — and starts the
///     platform's object store beside it.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The whole point of this process is the operator.</b> Every other k3s lane applies its
///         custom resource against a stub the harness derives. A vault's schedule and a server's WAL
///         archive are worth nothing until a controller acts on them, so this fixture runs the
///         bundle's cloudnative-pg row — and the storage row it needs, in the roster's order — against
///         the same k3s <c>ClusterInfrastructure</c> hands the harness.
///     </para>
///     <para>
///         ⚠ <b>No cert-manager, and the reason is the archiver.</b> The pinned CloudNativePG 1.30.0
///         serves the in-tree <c>barmanObjectStore</c>, which needs nothing but the operator; the
///         Barman Cloud plugin that replaces it in 1.31 needs cert-manager for its TLS. The day the
///         bundle moves, this install gains the cert-manager row —
///         charts/managed/postgres/conformance.yaml § owed, <c>the-in-tree-archiver-goes-in-1-31</c>.
///     </para>
///     <para>
///         ⚠ <b>Before the harness, or not at all.</b> A CRD stub created by the harness first would
///         make <c>helm install</c> refuse the real definitions as existing resources it does not own,
///         and the harness's silo reads <see cref="PlatformObjectStore.Options" /> when it composes.
///     </para>
///     <para>
///         ⚠ <b>Never throws when the daemon is absent, and never quietly succeeds either.</b> The
///         failure is remembered and the class turns it into its own loud skip.
///     </para>
/// </remarks>
public sealed class CloudNativePgInstalled : IAsyncLifetime {
    string? kubeconfigPath;
    IContainer? store;

    /// <summary>Why the install did not happen, or <see langword="null" /> when it did.</summary>
    public string? Failure { get; private set; }

    /// <summary>The installer's output, for a failure message.</summary>
    public string Output { get; private set; } = string.Empty;

    /// <summary>How long the install took, for the budget line in the test's output.</summary>
    public TimeSpan Took { get; private set; }

    /// <inheritdoc />
    public async ValueTask InitializeAsync() {
        var token = TestContext.Current.CancellationToken;

        if (!BundleInstaller.OnPath("bash") || !BundleInstaller.OnPath("helm")) {
            Failure =
                "install.sh is a bash script whose cloudnative-pg row is one `helm upgrade --install`; one of `bash` or `helm` is not on PATH.";
            return;
        }

        var endpoints = await ClusterInfrastructure.TryStartAsync(token);
        if (endpoints is null) {
            Failure = "the shared k3s did not come up: "
                + ClusterInfrastructure.SkipMessage("CyberCloud.RecoveryServices/vaults", "nothing yet");
            return;
        }

        var clock = Stopwatch.StartNew();

        try {
            using var yaml = new MemoryStream(Encoding.UTF8.GetBytes(endpoints.Kubeconfig));
            var raw = new k8s.Kubernetes(await KubernetesClientConfiguration.BuildConfigFromConfigFileAsync(yaml));

            if (!await IsServedAsync(raw, token)) {
                // ⚠ A FILE, because install.sh is a separate process and helm reads $KUBECONFIG. Deleted
                // in DisposeAsync — it holds a working client certificate for the container.
                kubeconfigPath = Path.Combine(
                    Path.GetTempPath(),
                    "cybercloud-vault-" + Guid.NewGuid().ToString("N") + ".kubeconfig"
                );
                await File.WriteAllTextAsync(kubeconfigPath, endpoints.Kubeconfig, token);

                // ⚠ Both components, the storage row first by the roster's order and not the command
                // line's — CloudNativePgOnAnEmptyCluster asserts that ordering; here it is relied on.
                // Budgeted by BudgetFor, which reads each row's own install and waitFor lines, rather
                // than by one helm row's default.
                var run = await BundleInstaller.RunAsync(
                    "--component "
                    + BundleInstaller.CloudNativePgComponent
                    + " --component "
                    + BundleInstaller.OpenEbsLocalPvComponent,
                    kubeconfigPath,
                    token,
                    BundleInstaller.BudgetFor([BundleInstaller.CloudNativePgComponent, BundleInstaller.OpenEbsLocalPvComponent])
                );

                Output = run.Output;

                if (run.ExitCode != 0) {
                    Failure =
                        $"charts/bundle/install.sh exited {run.ExitCode} installing cloudnative-pg and openebs-localpv onto the shared k3s.";
                    return;
                }

                if (!await IsServedAsync(raw, token)) {
                    Failure =
                        "install.sh succeeded and postgresql.cnpg.io/v1 is still not served, so the component's `serves:` line is not true of the cluster it installed.";
                    return;
                }
            }

            await StartStoreAsync(token);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            Failure = ex.GetType().Name + ": " + ex.Message;
        } finally {
            Took = clock.Elapsed;
        }
    }

    /// <summary>Starts SeaweedFS, waits for its IAM API, and creates the platform's administrator.</summary>
    async Task StartStoreAsync(CancellationToken token) {
        store = new ContainerBuilder(PlatformObjectStore.Image)
            .WithPortBinding(8333, true)
            .WithPortBinding(8111, true)
            // ⚠ NO PREALLOCATION. The image's entrypoint passes -master.volumePreallocate with a
            // 1 GiB volume limit, and each bucket is a collection that grows several volumes at once —
            // gigabytes of Docker VM disk per bucket, measured on 2026-09-24 as the k3s node going to
            // DiskPressure mid-run. The later flags win.
            .WithCommand(
                "server",
                "-s3",
                "-iam",
                "-dir=/data",
                "-ip.bind=0.0.0.0",
                "-master.volumePreallocate=false",
                "-master.volumeSizeLimitMB=64"
            )
            // ⚠ The IAM port: weed iam waits for the filer and binds last, about thirty seconds after
            // the S3 gateway on this image.
            .WithWaitStrategy(
                Wait.ForUnixContainer()
                    .UntilHttpRequestIsSucceeded(static x => x.ForPort(8111).ForPath("/").ForStatusCodeMatching(static _ => true))
            )
            .Build();

        await store.StartAsync(token);

        var s3 = $"http://{store.Hostname}:{store.GetMappedPublicPort(8333)}";
        var iam = $"http://{store.Hostname}:{store.GetMappedPublicPort(8111)}";

        var admin = await SeaweedFsObjectStoreGrants.BootstrapAdministratorAsync(
            new HttpClient { Timeout = TimeSpan.FromSeconds(30) },
            new Uri(iam),
            "platform",
            new SystemClock(),
            token
        );

        if (admin.TryGetError(out var adminError)) {
            throw new InvalidOperationException("the platform store's administrator could not be created: " + adminError.Message);
        }

        PlatformObjectStore.Options = new() {
            Endpoint = s3,
            IamEndpoint = iam,
            // ⚠ The bridge address and the container port: what a pod in the k3s node reaches.
            DataPlaneEndpoint = $"http://{store.IpAddress}:8333",
            Bucket = "platform",
            AccessKeyId = admin.GetValueOrThrow().AccessKeyId,
            SecretAccessKey = admin.GetValueOrThrow().SecretAccessKey,
            AllowInsecureTransport = true
        };

        // The administrator's key takes effect through a filer subscription. ⚠ Until it has, the S3
        // gateway authenticates nobody, so the install step waits for a stranger to be refused before
        // anything is issued (SeaweedFsObjectStoreGrants § remarks); the first bucket the key makes is
        // the proof that the key itself works.
        var grants = PlatformObjectStore.Grants();
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60);
        Result made;

        do {
            var closed = await SeaweedFsObjectStoreGrants.RefusesStrangersAsync(
                new HttpClient { Timeout = TimeSpan.FromSeconds(30) },
                new Uri(s3),
                PlatformObjectStore.Options.Region,
                new SystemClock(),
                token
            );

            made = closed is { IsSuccess: true } && closed.GetValueOrThrow()
                ? await grants.EnsureBucketAsync("platform", token)
                : Result.Failure(CyberCloud.Core.ErrorCode.InternalError, "the store still answers a key nobody issued");
            if (made.IsSuccess) {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), token);
        } while (DateTimeOffset.UtcNow < deadline);

        throw new InvalidOperationException("the platform store never honoured its administrator's key: " + made.Error?.Message);
    }

    /// <summary>A loud skip naming what the calling test would have proved.</summary>
    /// <param name="wouldProve">The sentence.</param>
    public void Require(string wouldProve) {
        if (Failure is null) {
            return;
        }

        Assert.Skip(
            "SKIPPED — CyberCloud.RecoveryServices/vaults against a real CloudNativePG: the operator or the platform "
            + "store did not come up, so nothing was checked. "
            + ClusterInfrastructure.PrerequisiteMarker
            + " a Docker daemon able to run "
            + ClusterInfrastructure.K3sImage
            + " and "
            + PlatformObjectStore.Image
            + ", and `bash` and `helm` on PATH. WOULD PROVE: "
            + wouldProve
            + " What went wrong: "
            + Failure
            + (Output.Length > 0 ? " Installer output:\n" + Output : string.Empty)
        );
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() {
        if (kubeconfigPath is not null) {
            try {
                File.Delete(kubeconfigPath);
            } catch (IOException) {
                // A kubeconfig for a container Ryuk is about to remove. Not a test result.
            }
        }

        if (store is not null) {
            await store.DisposeAsync();
        }
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
///     The vault's case with a companion the real operator runs: one instance, no pooler, a
///     one-gibibyte volume, backups ON — to the platform's store.
/// </summary>
/// <remarks>
///     ⚠ <b>Backups on is the default body since #30, and it is the whole of what changed here.</b>
///     The first cut of this lane could only give the operator a server with backups off, because the
///     chart rendered a store with no credentials and CloudNativePG's webhook refused it; the vault's
///     refusal of that server was the honest statement of the platform. Now the server gets a bucket
///     and a key, and the lane asserts the protection end to end instead.
/// </remarks>
public sealed class RecoveryVaultOnOperatorCase : IProviderCaseSource {
    /// <summary>The companion: small, poolerless, with the default backup section.</summary>
    public static CompanionCase ProtectedServer { get; } =
        RecoveryVaultCase.ProtectedServer with {
            Body = static cluster => PostgresServers.Body(cluster, replicas: 1, storageSize: "1Gi", pooling: false)
        };

    /// <inheritdoc />
    public static ProviderConformanceCase ProviderCase => RecoveryVaultCase.ProviderCase;

    /// <inheritdoc />
    public static ImmutableArray<CompanionCase> Companions { get; } = [ProtectedServer];

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ The real store's grants, after the harness's in-memory ones — the later registration wins,
    ///     so the PostgreSQL reconciler in this silo issues real keys to a real SeaweedFS.
    /// </remarks>
    public static void ConfigureSilo(ISiloBuilder silo) =>
        silo.ConfigureServices(static services => services.AddSingleton(PlatformObjectStore.Grants()));
}

/// <summary>
///     #30 end to end, against a real CloudNativePG and a real SeaweedFS: a server is created with a
///     row in it, a vault protects it, an on-demand backup completes into the server's own bucket, and
///     a restore creates a NEW server resource whose database has the row — and, once the source is
///     deleted and purged with its key Secret, a second restore of the same point still does.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every step goes through the platform, and the only thing the test does by hand is SQL.</b>
///         The server and the vault are PUTs through the manager; the first point is the one
///         CloudNativePG's ScheduledBackup controller takes when the vault's schedule appears, found by
///         the controller's own label; the second is the vault's <c>backupNow</c>; each point's phase
///         is read through <c>listRecoveryPoints</c>; the restore is <c>recover</c> from the
///         controller's point, which creates the server resource through the caller's write path; the row
///         is written and read with <c>psql</c> inside the instance pods, because the platform has no
///         data-plane API for SQL and should not grow one for a test.
///     </para>
///     <para>
///         ⚠ <b>The time is budgeted per phase, and each budget is a measured ceiling rather than a
///         hope.</b> <see cref="ServerReady" /> covers pulling the PostgreSQL image into k3s, an initdb,
///         and the first WAL archived to the store — CloudNativePG does not report the cluster healthy
///         before archiving works; <see cref="PointCompleted" /> a base backup of an almost empty
///         database; <see cref="RestoreReady" /> the recovery job reading that backup back and the new
///         primary's own first archive. The install is <see cref="BundleInstaller.BudgetFor" />'s. A phase
///         that runs out fails naming itself and the last status it read.
///     </para>
/// </remarks>
/// <param name="cnpg">The install, which must have run first.</param>
/// <param name="fixture">The harness.</param>
public sealed class RecoveryVaultAgainstCloudNativePg(
    CloudNativePgInstalled cnpg,
    ClusterConformanceFixture<RecoveryVaultOnOperatorCase> fixture)
    : IClassFixture<ClusterConformanceFixture<RecoveryVaultOnOperatorCase>> {
    static readonly TimeSpan ServerReady = TimeSpan.FromMinutes(8);
    static readonly TimeSpan PointCompleted = TimeSpan.FromMinutes(6);
    static readonly TimeSpan RestoreReady = TimeSpan.FromMinutes(10);
    static readonly TimeSpan BetweenPolls = TimeSpan.FromSeconds(5);
    const string VaultName = "nightly";
    const string Restored = "protected-server-restored";
    const string RestoredAfterPurge = "protected-server-after-purge";
    const string Row = "written-before-the-backup";

    [Fact]
    public async Task AProtectedServerIsBackedUpAndRestoredIntoANewServerWithItsRow() {
        cnpg.Require(
            "that a PostgreSQL server archives to a bucket of its own on the platform's store with a vault-held key, "
            + "that a vault's backupNow produces a completed recovery point there, and that recover creates a new "
            + "server resource whose database holds the row written before the backup."
        );

        var harness = fixture.Require("the same, once the harness is up");
        var token = TestContext.Current.CancellationToken;
        var output = TestContext.Current.TestOutputHelper;
        var ns = ClusterConformanceHarness<RecoveryVaultOnOperatorCase>.Namespace;
        var item = RecoveryVaultOnOperatorCase.ProtectedServer.Name;
        var clock = Stopwatch.StartNew();

        output?.WriteLine($"install.sh (cloudnative-pg, openebs-localpv) and the store took {cnpg.Took.TotalSeconds:F0}s");

        // ── 1. The server the harness created is really running, and archiving ──────────────────
        await ClusterReadyAsync(harness, ns, item, ServerReady, token);
        output?.WriteLine($"server ready after {clock.Elapsed.TotalSeconds:F0}s");

        var serverId = (await harness.For(ConformanceIds.Tenant)
                .GetGrain<IResourceIndexGrain>(GrainKeys.PathIndex(RecoveryVaultOnOperatorCase.ProtectedServer.Address()))
                .ResolveAsync())
            .GetValueOrThrow();

        var secret = JsonNode.Parse((await harness.Connection.GetAsync(PostgresServers.BackupSecretRef(ns, item), token)).GetValueOrThrow().Json)!;
        var keyId = Encoding.UTF8.GetString(Convert.FromBase64String(secret["data"]![PostgresServers.AccessKeyIdKey]!.GetValue<string>()));
        keyId.ShouldNotBe(
            PlatformObjectStore.Options!.AccessKeyId,
            "the server's Secret holds the platform's own key, which opens every bucket on the store"
        );

        // ── 2. A row, before any backup ─────────────────────────────────────────────────────────
        var written = await SqlAsync(
            harness,
            ns,
            item + "-1",
            $"create table cc30 (v text); insert into cc30 values ('{Row}'); select count(*) from cc30;",
            token
        );
        written.ShouldContain("1", Case.Sensitive, "the row was not written");

        // ── 3. A vault protecting it ────────────────────────────────────────────────────────────
        var accepted = await harness.Manager.WriteAsync(
            new() {
                Path = ClusterConformanceHarness<RecoveryVaultOnOperatorCase>.Address(VaultName).Path,
                ApiVersion = RecoveryVaults.V2026,
                Verb = WriteVerb.Put,
                Body = RecoveryVaultCase.ProviderCase.Body(ClusterConformanceHarness<RecoveryVaultOnOperatorCase>.ClusterId),
                Caller = ClusterConformanceHarness<RecoveryVaultOnOperatorCase>.Caller()
            },
            token
        );

        accepted.IsSuccess.ShouldBeTrue(accepted.Error?.Message);
        var vault = ClusterConformanceHarness<RecoveryVaultOnOperatorCase>.Address(VaultName)
            .WithId(accepted.GetValueOrThrow().Resource.Id);

        var protectedStatus = await DriveAsync(harness, accepted.GetValueOrThrow().OperationId, token);
        protectedStatus.State.ShouldBe(OperationState.Succeeded, protectedStatus.Error?.Message);

        // ⚠ The manager-started pass is armed on the real reminder table — the vault's retention runs
        // with nobody writing to it.
        (await harness.ReminderRowsAsync(harness.For(ConformanceIds.Tenant).GetGrain<IResourceGrain>(GrainKeys.Resource(vault.Id))))
            .ShouldBe(1, "the vault declares PassEvery and its converged create armed no reminder: " + await harness.AllRemindersAsync());

        // ── 4. The CONTROLLER's point: what the vault's schedule makes, found by its label ──────
        //
        // ⚠ #30'S REVIEW: the first rewrite of this lane took its only point from backupNow, which the
        // platform labels by hand, and dropped the one assertion that CloudNativePG's ScheduledBackup
        // controller labels its Backups the way listRecoveryPoints, recover and retention all join on.
        // `immediate: true` makes the controller take one as soon as the schedule exists, after the
        // row above was committed, so the restore below is from this point and not from backupNow's.
        var schedule = RecoveryVaults.ScheduledBackupNameOf(VaultName, item);
        var scheduled = await ControllerPointAsync(harness, ns, schedule, token);
        var scheduledPoint = scheduled["metadata"]!["name"]!.GetValue<string>();

        var labels = scheduled["metadata"]!["labels"]!.AsObject();
        labels[RecoveryVaults.ParentScheduledBackupLabel]!.GetValue<string>().ShouldBe(schedule);
        labels[RecoveryVaults.ClusterLabel]!.GetValue<string>().ShouldBe(item);
        labels.Select(static x => x.Key)
            .ShouldNotContain(static x => x.StartsWith(KubeLabels.Prefix + "/", StringComparison.Ordinal), "the platform wrote this Backup, so it proves nothing about the controller");
        scheduled["metadata"]!["ownerReferences"]!.AsArray()
            .Select(static x => x!.AsObject())
            .ShouldContain(x => x["kind"]!.GetValue<string>() == "ScheduledBackup" && x["name"]!.GetValue<string>() == schedule);

        var scheduledLine = await PointSettledAsync(harness, vault, scheduledPoint, token);
        scheduledLine.ShouldStartWith(
            item + " " + scheduledPoint + " completed",
            Case.Sensitive,
            $"the controller's point is not listed as completed within {PointCompleted.TotalMinutes:F0} minutes: {scheduledLine}"
        );
        output?.WriteLine($"controller's point completed after {clock.Elapsed.TotalSeconds:F0}s: {scheduledLine}");

        // ⚠ The major the operator records, which a restore whose source is gone reads
        // (RecoveryVaults.RestoredMajorVersion) — measured here rather than taken from the source file.
        var recorded = JsonNode.Parse(
            (await harness.Connection.GetAsync(RecoveryVaults.BackupRef(ns, scheduledPoint), token)).GetValueOrThrow().Json
        )!;
        recorded["status"]?["majorVersion"]?.GetValue<int>()
            .ShouldBe(17, "CloudNativePG 1.30 did not record the major on the Backup: " + recorded["status"]?.ToJsonString());

        // ── 5. An on-demand backup, to completion ───────────────────────────────────────────────
        var now = await ActionAsync(harness, vault, RecoveryVaults.BackupNowAction, new JsonObject { ["item"] = item }, token);
        var point = JsonNode.Parse(now)!["recoveryPoint"]!.GetValue<string>();

        var line = await PointSettledAsync(harness, vault, point, token);
        line.ShouldStartWith(
            item + " " + point + " completed",
            Case.Sensitive,
            $"the on-demand point did not complete within {PointCompleted.TotalMinutes:F0} minutes: {line}"
        );
        output?.WriteLine($"recovery point completed after {clock.Elapsed.TotalSeconds:F0}s: {line}");

        // The bytes are in the server's own bucket on the platform's store.
        var bucket = ObjectStoreCredentials.BucketFor(PostgresServers.BucketPrefix, serverId);
        var listed = await new S3ObjectStore(
                new HttpClient { Timeout = TimeSpan.FromSeconds(30) },
                new() {
                    Endpoint = PlatformObjectStore.Options.Endpoint,
                    Bucket = bucket,
                    AccessKeyId = PlatformObjectStore.Options.AccessKeyId,
                    SecretAccessKey = PlatformObjectStore.Options.SecretAccessKey,
                    AllowInsecureTransport = true
                },
                new SystemClock()
            )
            .ListAsync(item + "/", token);

        listed.IsSuccess.ShouldBeTrue(listed.Error?.Message);
        listed.GetValueOrThrow().ShouldContain(x => x.Contains("/base/", StringComparison.Ordinal), $"no base backup in '{bucket}'");
        listed.GetValueOrThrow().ShouldContain(x => x.Contains("/wals/", StringComparison.Ordinal), $"no archived WAL in '{bucket}'");

        // ── 6. The restore, from the CONTROLLER's point: a new server resource ─────────────────
        //
        // ⚠ #30'S REVIEW, FIRST: a caller's own PUT of a server naming the point is refused by the write
        // path, because only recover checks that the point is the vault's and that the caller may use
        // the vault. It used to be accepted and bootstrap the copy.
        var stolen = await harness.Manager.WriteAsync(
            new() {
                Path = (ClusterConformanceHarness<RecoveryVaultOnOperatorCase>.Address("protected-server-stolen")
                    with { Type = RecoveryVaults.PostgresServerType }).Path,
                ApiVersion = RecoveryVaults.PostgresServerApiVersion,
                Verb = WriteVerb.Put,
                Body = PostgresServers.Body(
                    ClusterConformanceHarness<RecoveryVaultOnOperatorCase>.ClusterId,
                    replicas: 1,
                    storageSize: "1Gi",
                    pooling: false,
                    recoveryPoint: scheduledPoint
                ),
                Caller = ClusterConformanceHarness<RecoveryVaultOnOperatorCase>.Caller()
            },
            token
        );

        stolen.IsFailure.ShouldBeTrue("a PUT of a server restored another server's point without the vault");
        stolen.Error!.Target.ShouldBe(PostgresServers.RecoveryPointPointer);

        var recovered = JsonNode.Parse(
            await ActionAsync(
                harness,
                vault,
                RecoveryVaults.RecoverAction,
                new JsonObject { ["recoveryPoint"] = scheduledPoint, ["targetName"] = Restored },
                token
            )
        )!;

        recovered["kind"]!.GetValue<string>().ShouldBe(RecoveryVaults.PostgresServerType.ToString());

        var restore = await DriveAsync(harness, Guid.Parse(recovered["operationId"]!.GetValue<string>()), token);
        restore.State.ShouldBe(OperationState.Succeeded, restore.Error?.Message);

        await ClusterReadyAsync(harness, ns, Restored, RestoreReady, token);
        output?.WriteLine($"restored server ready after {clock.Elapsed.TotalSeconds:F0}s");

        // ── 7. The row is there ─────────────────────────────────────────────────────────────────
        var read = await SqlAsync(harness, ns, Restored + "-1", "select v from cc30;", token);
        read.Trim().ShouldBe(Row, "the restored server's database does not hold the row written before the backup");

        // ⚠ #30's reclaim: the copy read the source's bucket through a Secret of its OWN, and names the
        // source's {name}-backup-s3 nowhere — so nothing the copy needs is lost when the source goes.
        var restoredSpec = JsonNode.Parse(
            (await harness.Connection.GetAsync(PostgresServers.ClusterRef(ns, Restored), token)).GetValueOrThrow().Json
        )!["spec"]!;
        restoredSpec["externalClusters"]![0]!["barmanObjectStore"]!["s3Credentials"]!["accessKeyId"]!["name"]!
            .GetValue<string>()
            .ShouldBe(PostgresServers.RestoreSecretName(Restored));
        restoredSpec.ToJsonString().ShouldNotContain(PostgresServers.BackupSecretName(item));

        output?.WriteLine($"first restore (source alive) in {clock.Elapsed.TotalSeconds:F0}s");

        // ── 8. The source, deleted and purged — its key Secret with it ─────────────────────────
        //
        // ⚠ THE RESTORE A VAULT EXISTS FOR, AND #30'S RECLAIM. The source's teardown used to leave
        // {name}-backup-s3 standing because a restore read it through the point's status, and
        // NamespaceReclaim then refused the resource group forever over that platform-written Secret.
        // Now the purge removes it, and the restore below must work without it.
        var sourceWrite = new WriteRequest {
            Path = RecoveryVaultOnOperatorCase.ProtectedServer.Address().Path,
            ApiVersion = RecoveryVaults.PostgresServerApiVersion,
            Caller = ClusterConformanceHarness<RecoveryVaultOnOperatorCase>.Caller()
        };

        var deleted = await harness.Manager.DeleteAsync(sourceWrite, token);
        deleted.IsSuccess.ShouldBeTrue(deleted.Error?.Message);
        var deletion = await DriveAsync(harness, deleted.GetValueOrThrow().OperationId, token);
        deletion.State.ShouldBe(OperationState.Succeeded, deletion.Error?.Message);

        var purged = await harness.Manager.PurgeAsync(sourceWrite, token);
        purged.IsSuccess.ShouldBeTrue(purged.Error?.Message);
        var purge = await DriveAsync(harness, purged.GetValueOrThrow().OperationId, token);
        purge.State.ShouldBe(OperationState.Succeeded, purge.Error?.Message);

        var gone = await harness.Connection.GetAsync(PostgresServers.BackupSecretRef(ns, item), token);
        gone.IsSuccess.ShouldBeFalse($"the source was purged and '{PostgresServers.BackupSecretName(item)}' is still in '{ns}'");
        gone.Error!.Code.ShouldBe(CyberCloud.Core.ErrorCode.ResourceNotFound, gone.Error.Message);
        output?.WriteLine($"source deleted and purged after {clock.Elapsed.TotalSeconds:F0}s");

        // ── 9. The same point, restored with its source gone ───────────────────────────────────
        var afterPurge = JsonNode.Parse(
            await ActionAsync(
                harness,
                vault,
                RecoveryVaults.RecoverAction,
                new JsonObject { ["recoveryPoint"] = scheduledPoint, ["targetName"] = RestoredAfterPurge },
                token
            )
        )!;

        var restoreAfterPurge = await DriveAsync(harness, Guid.Parse(afterPurge["operationId"]!.GetValue<string>()), token);
        restoreAfterPurge.State.ShouldBe(OperationState.Succeeded, restoreAfterPurge.Error?.Message);

        await ClusterReadyAsync(harness, ns, RestoredAfterPurge, RestoreReady, token);
        output?.WriteLine($"restored server (source purged) ready after {clock.Elapsed.TotalSeconds:F0}s");

        var readAfterPurge = await SqlAsync(harness, ns, RestoredAfterPurge + "-1", "select v from cc30;", token);
        readAfterPurge.Trim().ShouldBe(Row, "a restore of a purged server's point does not hold the row written before the backup");

        output?.WriteLine($"end to end in {clock.Elapsed.TotalSeconds:F0}s");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Waits for a <c>Backup</c> CloudNativePG's ScheduledBackup controller made for
    ///     <paramref name="schedule" />, found the way <c>listRecoveryPoints</c> finds one — by the
    ///     controller's own label — and not named like a <c>backupNow</c> point.
    /// </summary>
    static async Task<JsonObject> ControllerPointAsync(
        ClusterConformanceHarness<RecoveryVaultOnOperatorCase> harness,
        string ns,
        string schedule,
        CancellationToken token
    ) {
        var deadline = DateTimeOffset.UtcNow + PointCompleted;

        while (DateTimeOffset.UtcNow < deadline) {
            var listed = await harness.Raw.CustomObjects.ListNamespacedCustomObjectAsync(
                RecoveryVaults.BackupKind.Group,
                RecoveryVaults.BackupKind.Version,
                ns,
                RecoveryVaults.BackupKind.Plural,
                labelSelector: RecoveryVaults.RecoveryPointSelector(schedule),
                cancellationToken: token
            );

            var made = JsonNode.Parse(listed.ToString()!)?["items"]?.AsArray()
                .OfType<JsonObject>()
                .FirstOrDefault(static x => !x["metadata"]!["name"]!.GetValue<string>().Contains("-now-", StringComparison.Ordinal));

            if (made is not null) {
                return made;
            }

            await Task.Delay(BetweenPolls, token);
        }

        throw new ShouldAssertException(
            $"no Backup labelled {RecoveryVaults.RecoveryPointSelector(schedule)} appeared in `{ns}` within "
            + $"{PointCompleted.TotalMinutes:F0} minutes of the vault converging a ScheduledBackup with immediate: true. "
            + "The controller is not acting on the schedule, or labels its Backups differently from what "
            + "listRecoveryPoints, recover and retention join on. " + await EventsAsync(harness, ns)
        );
    }

    /// <summary>Waits until CloudNativePG reports every instance of a cluster ready.</summary>
    static async Task ClusterReadyAsync(
        ClusterConformanceHarness<RecoveryVaultOnOperatorCase> harness,
        string ns,
        string name,
        TimeSpan budget,
        CancellationToken token
    ) {
        var deadline = DateTimeOffset.UtcNow + budget;
        var last = "never read";

        while (DateTimeOffset.UtcNow < deadline) {
            try {
                var found = await harness.Raw.CustomObjects.GetNamespacedCustomObjectAsync(
                    PostgresServers.ClusterKind.Group,
                    PostgresServers.ClusterKind.Version,
                    ns,
                    PostgresServers.ClusterKind.Plural,
                    name,
                    token
                );

                var status = JsonNode.Parse(found.ToString()!)?["status"];
                last = status?["phase"]?.GetValue<string>() + " / " + status?["phaseReason"]?.GetValue<string>();

                if (status?["readyInstances"]?.GetValue<int>() is >= 1
                    && status["phase"]?.GetValue<string>() == "Cluster in healthy state") {
                    return;
                }
            } catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound) {
                last = "the Cluster does not exist yet";
            }

            await Task.Delay(BetweenPolls, token);
        }

        throw new ShouldAssertException(
            $"CloudNativePG did not report '{name}' healthy within {budget.TotalMinutes:F0} minutes; last: {last}. "
            + await EventsAsync(harness, ns)
        );
    }

    /// <summary>The namespace's most recent warnings, for a failure message that says why.</summary>
    static async Task<string> EventsAsync(ClusterConformanceHarness<RecoveryVaultOnOperatorCase> harness, string ns) {
        try {
            var events = await harness.Raw.CoreV1.ListNamespacedEventAsync(ns);
            return "Warnings: " + string.Join(
                " | ",
                events.Items.Where(static x => x.Type == "Warning")
                    .TakeLast(8)
                    .Select(static x => $"{x.InvolvedObject.Kind}/{x.InvolvedObject.Name}: {x.Reason} {x.Message}")
            );
        } catch (HttpOperationException) {
            return string.Empty;
        }
    }

    /// <summary>Runs SQL in an instance pod's own <c>psql</c>, over the local socket, and returns its output.</summary>
    static async Task<string> SqlAsync(
        ClusterConformanceHarness<RecoveryVaultOnOperatorCase> harness,
        string ns,
        string pod,
        string sql,
        CancellationToken token
    ) {
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        var exit = await ((k8s.Kubernetes)harness.Raw).NamespacedPodExecAsync(
            pod,
            ns,
            "postgres",
            ["psql", "-d", "app", "-v", "ON_ERROR_STOP=1", "-tAc", sql],
            false,
            async (_, output, error) => {
                using var outReader = new StreamReader(output);
                using var errReader = new StreamReader(error);
                stdout.Append(await outReader.ReadToEndAsync(token));
                stderr.Append(await errReader.ReadToEndAsync(token));
            },
            token
        );

        exit.ShouldBe(0, $"psql in '{pod}' failed: {stderr}");
        return stdout.ToString();
    }

    /// <summary>Invokes a vault action through the manager, as the harness's caller, and returns its body.</summary>
    static async Task<string> ActionAsync(
        ClusterConformanceHarness<RecoveryVaultOnOperatorCase> harness,
        ResourceId vault,
        string action,
        JsonObject body,
        CancellationToken token
    ) {
        var answered = await harness.Manager.ActionAsync(
            new() {
                Path = vault.Path,
                ApiVersion = RecoveryVaults.V2026,
                Verb = WriteVerb.Post,
                Action = action,
                Body = body.ToJsonString(),
                Caller = ClusterConformanceHarness<RecoveryVaultOnOperatorCase>.Caller()
            },
            token
        );

        answered.IsSuccess.ShouldBeTrue($"{action}: {answered.Error?.Message}");
        return answered.GetValueOrThrow().ActionResponse;
    }

    /// <summary>Polls <c>listRecoveryPoints</c> until the point reports a terminal phase.</summary>
    static async Task<string> PointSettledAsync(
        ClusterConformanceHarness<RecoveryVaultOnOperatorCase> harness,
        ResourceId vault,
        string point,
        CancellationToken token
    ) {
        var deadline = DateTimeOffset.UtcNow + PointCompleted;
        var last = "not listed";

        while (DateTimeOffset.UtcNow < deadline) {
            var listed = JsonNode.Parse(
                await ActionAsync(harness, vault, RecoveryVaults.ListRecoveryPointsAction, [], token)
            )!["recoveryPoints"]!.AsArray();

            last = listed.Select(static x => x!.GetValue<string>())
                    .FirstOrDefault(x => x.Contains(" " + point + " ", StringComparison.Ordinal))
                ?? "not listed";

            if (last.Contains(" " + point + " completed", StringComparison.Ordinal)
                || last.Contains(" " + point + " failed", StringComparison.Ordinal)) {
                return last;
            }

            await Task.Delay(BetweenPolls, token);
        }

        return last;
    }

    static async Task<OperationStatus> DriveAsync(
        ClusterConformanceHarness<RecoveryVaultOnOperatorCase> harness,
        Guid operationId,
        CancellationToken token
    ) {
        var operation = harness.Operation(ConformanceIds.Tenant, operationId);
        OperationStatus? last = null;

        for (var i = 0; i < 90; i++) {
            last = (await operation.DriveAsync()).GetValueOrThrow();
            if (last.IsTerminal) {
                return last;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), token);
        }

        last.ShouldNotBeNull();
        return last;
    }
}
