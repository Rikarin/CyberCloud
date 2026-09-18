using CyberCloud.ResourceManager;
using CyberCloud.ResourceManager.Conformance;
using CyberCloud.ResourceManager.Reconcile;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Storage.Tests;

/// <summary>
///     The file-share reconciler, and the thing about a <b>shared</b> object that no shared suite can
///     assert: what happens to the account's CSI driver when one share of several goes.
/// </summary>
/// <remarks>
///     ⚠ <b>THIS FILE EXISTS BECAUSE THE SHARED SUITE BRINGS UP ONE SHARE PER RUN.</b> With one share
///     the driver's "last one out removes it" is indistinguishable from "every delete removes it", and
///     the second is the defect that unmounts every sibling's data. The sibling cases are here, against
///     a connection that lists the way a real one does.
/// </remarks>
public sealed class StorageFileShareReconcilerTests {
    [Fact]
    public void TheReconcilerHoldsNoMutableState() {
        ReconcilerConformance.CheckNoHiddenState(new StorageFileShareReconciler(new FixedClock())).ShouldBeEmpty();
    }

    [Fact]
    public async Task OneReconcilerInstanceServesTwoTenantsWithoutMixingThem() {
        // ⚠ A singleton by concrete type serves every tenant in the process — same account name, same
        // share name, two tenants, two subscriptions, interleaved.
        var reconciler = new StorageFileShareReconciler(new FixedClock());
        var connection = new RecordingConnection();

        var alice = Address("home", "media", TenantA, SubscriptionA);
        var bob = Address("home", "media", TenantB, SubscriptionB);

        using var aliceBody = JsonDocument.Parse(StorageFileShares.Body(ClusterId, "10Gi"));
        using var bobBody = JsonDocument.Parse(StorageFileShares.Body(ClusterId, "999Gi"));

        await Pass(reconciler, connection, alice, aliceBody.RootElement);
        await Pass(reconciler, connection, bob, bobBody.RootElement);
        await Pass(reconciler, connection, alice, aliceBody.RootElement);
        await Pass(reconciler, connection, bob, bobBody.RootElement);

        var claims = connection.Applied.Where(static x => x.Target.Kind.Kind == "PersistentVolumeClaim").ToList();
        claims.Count.ShouldBe(4);

        Storage(claims[2].Body).ShouldBe("10Gi", "tenant A's size came back as tenant B's");
        Storage(claims[3].Body).ShouldBe("999Gi", "tenant B's size came back as tenant A's");

        claims[0].Labels[KubeLabels.TenantId].ShouldBe(KubeLabels.GuidValue(TenantA));
        claims[1].Labels[KubeLabels.TenantId].ShouldBe(KubeLabels.GuidValue(TenantB));
        claims[0].Target.Namespace.ShouldNotBe(claims[1].Target.Namespace);

        // ⚠ And the two drivers differ by NAME as well as by namespace. The driverName is a
        // cluster-scoped singleton — the operator writes a CSIDriver and a StorageClass under it — so
        // two accounts called `media` in two namespaces must not fold to one name, or the newer
        // account's driver is refused with DriverNameConflict and its claims never bind.
        var drivers = connection.Applied.Where(static x => x.Target.Kind.Kind == "SeaweedCSIDriver").ToList();
        DriverName(drivers[0].Body).ShouldNotBe(DriverName(drivers[1].Body));
    }

    // ── The shape ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheClaimIsReadWriteManyAgainstTheAccountsOwnDriverWithTheAskedForSize() {
        var connection = new RecordingConnection();
        var address = Address("home", "media", TenantA, SubscriptionA);

        using var body = JsonDocument.Parse(StorageFileShares.Body(ClusterId, "25Gi"));

        (await Pass(new StorageFileShareReconciler(new FixedClock()), connection, address, body.RootElement))
            .ShouldBe(ReconcileOutcome.Converged);

        // ⚠ THE DRIVER FIRST. A claim applied before its class exists is not refused, not retried and
        // not reported — the external-provisioner only watches claims of classes it serves — so the
        // order is what keeps that window one pass long.
        connection.Applied[0].Target.Kind.Kind.ShouldBe("SeaweedCSIDriver");
        connection.Applied[1].Target.Kind.Kind.ShouldBe("PersistentVolumeClaim");

        var claim = Spec(connection.Applied[1].Body);

        // ⚠ Literals, not StorageFileShares.AccessMode. Deriving the expectation from the constant the
        // renderer reads would compare the renderer to itself.
        claim["accessModes"]!.AsArray().Select(static x => x!.GetValue<string>()).ShouldBe(["ReadWriteMany"]);
        claim["resources"]!["requests"]!["storage"]!.GetValue<string>().ShouldBe("25Gi");

        var driver = Spec(connection.Applied[0].Body);
        driver["seaweedRef"]!["name"]!.GetValue<string>()
            .ShouldBe("media", "the driver points at a filer that is not the account's");
        driver["storageClass"]!["parameters"]!["parentDir"]!.GetValue<string>()
            .ShouldBe(
                "/fileshares",
                "a share provisioned under the driver's default /buckets shows up in every S3 client as a bucket"
            );
        driver["storageClass"]!["reclaimPolicy"]!.GetValue<string>().ShouldBe("Delete");

        // The claim names the class the driver creates, which is the driver's name.
        claim["storageClassName"]!.GetValue<string>().ShouldBe(driver["driverName"]!.GetValue<string>());
        driver["driverName"]!.GetValue<string>().Length.ShouldBeLessThanOrEqualTo(63, "the CRD caps driverName at 63");
        driver["driverName"]!.GetValue<string>().ShouldMatch("^[a-z0-9]([-a-z0-9.]*[a-z0-9])?$");

        connection.Applied[0].Target.Name.ShouldBe("media-csi");
        connection.Applied[1].Target.Name.ShouldBe("media-home");
    }

    [Fact]
    public async Task TwoAccountsInOneResourceGroupHoldTwoDifferentSharesOfTheSameName() {
        var reconciler = new StorageFileShareReconciler(new FixedClock());
        var connection = new RecordingConnection();

        var media = Address("home", "media", TenantA, SubscriptionA);
        var logs = Address("home", "logs", TenantA, SubscriptionA);

        using var body = JsonDocument.Parse(StorageFileShares.Body(ClusterId));

        (await Pass(reconciler, connection, media, body.RootElement)).ShouldBe(ReconcileOutcome.Converged);
        (await Pass(reconciler, connection, logs, body.RootElement)).ShouldBe(ReconcileOutcome.Converged);

        var claims = connection.Applied.Where(static x => x.Target.Kind.Kind == "PersistentVolumeClaim").ToList();

        claims[0].Target.Namespace.ShouldBe(claims[1].Target.Namespace);
        claims[0].Target.Name.ShouldBe("media-home");
        claims[1].Target.Name.ShouldBe("logs-home");

        // ⚠ And two DRIVERS, one per account, each on its own filer.
        var drivers = connection.Applied.Where(static x => x.Target.Kind.Kind == "SeaweedCSIDriver")
            .Select(static x => x.Target.Name)
            .ToList();
        drivers.ShouldBe(["media-csi", "logs-csi"]);
    }

    [Fact]
    public async Task EveryObjectCarriesTheAccountLabelThroughTheBuilder() {
        // ⚠ The eighth label is what the delete lists on. A reconciler that wrote it into the rendered
        // document instead of through WithLabels would skip the syntax check the seven go through — and
        // one that forgot it would list nothing and remove the driver from under every sibling.
        var connection = new RecordingConnection();
        var address = Address("home", "media", TenantA, SubscriptionA);

        using var body = JsonDocument.Parse(StorageFileShares.Body(ClusterId));

        await Pass(new StorageFileShareReconciler(new FixedClock()), connection, address, body.RootElement);

        foreach (var command in connection.Applied) {
            command.Labels.ShouldContainKeyAndValue("storage.cybercloud.io/account", "media");
        }
    }

    [Fact]
    public async Task NothingInAPassEverReadsTheAccountsOwnObject() {
        var connection = new RecordingConnection();
        var address = Address("home", "media", TenantA, SubscriptionA);

        using var body = JsonDocument.Parse(StorageFileShares.Body(ClusterId));

        await Pass(new StorageFileShareReconciler(new FixedClock()), connection, address, body.RootElement);

        connection.Read.ShouldAllBe(
            x => x.Kind.Kind == "PersistentVolumeClaim" || x.Kind.Kind == "SeaweedCSIDriver",
            "a pass read an object that is not this share's own. Reading the account's Seaweed would "
            + "be the parent re-check docs/plan/08 forbids."
        );
    }

    // ── The shared object, and who removes it ─────────────────────────────────────────────────

    [Fact]
    public async Task DeletingTheLastShareOfAnAccountRemovesItsDriver() {
        var connection = new RecordingConnection();
        var address = Address("home", "media", TenantA, SubscriptionA);
        var reconciler = new StorageFileShareReconciler(new FixedClock());

        using var body = JsonDocument.Parse(StorageFileShares.Body(ClusterId));

        await Pass(reconciler, connection, address, body.RootElement);

        var deleted = await reconciler.DeleteAsync(
            Context(connection, address, body.RootElement),
            TestContext.Current.CancellationToken
        );

        deleted.ShouldBe(ReconcileOutcome.Converged);
        connection.Deleted.Select(static x => x.Kind.Kind)
            .ShouldBe(
                ["PersistentVolumeClaim", "SeaweedCSIDriver"],
                "the claim goes first — it is the data — and the driver second, once nothing is behind it"
            );
        connection.Objects.ShouldBeEmpty();
    }

    [Fact]
    public async Task DeletingOneShareOfTwoLeavesTheDriverForTheOther() {
        // ⚠ THE ASSERTION THE SHARED SUITE CANNOT MAKE, AND THE ONE THIS TYPE IS MOST DANGEROUS
        // WITHOUT. Removing the SeaweedCSIDriver tears down the node plugin DaemonSet, which is what
        // holds every FUSE mount in the account; a reconciler that removed it on every delete would
        // unmount the sibling's data under a running pod.
        var connection = new RecordingConnection();
        var reconciler = new StorageFileShareReconciler(new FixedClock());

        var home = Address("home", "media", TenantA, SubscriptionA);
        var scratch = Address("scratch", "media", TenantA, SubscriptionA);

        using var body = JsonDocument.Parse(StorageFileShares.Body(ClusterId));

        await Pass(reconciler, connection, home, body.RootElement);
        await Pass(reconciler, connection, scratch, body.RootElement);

        var deleted = await reconciler.DeleteAsync(
            Context(connection, home, body.RootElement),
            TestContext.Current.CancellationToken
        );

        deleted.ShouldBe(ReconcileOutcome.Converged);
        connection.Deleted.Select(static x => x.Kind.Kind).ShouldBe(["PersistentVolumeClaim"]);

        var ns = ReconcileDriver.NamespaceFor(home);
        connection.Objects.ShouldContainKey(
            RecordingConnection.Key(StorageFileShares.DriverRef(ns, scratch)),
            "the account's CSI driver was removed while another share of the account still had a "
            + "claim against it — every mount of that share is gone with it"
        );

        // And the listing asked for THIS account's claims, by the label, not for every claim in the
        // namespace — another account's share must not keep this account's driver alive.
        connection.Listed.Single().ShouldContain("storage.cybercloud.io/account=media");
        connection.Listed.Single().ShouldContain("cybercloud.io/resource-type=cybercloud.storage_accounts_fileshares");
    }

    [Fact]
    public async Task AnotherAccountsShareDoesNotKeepThisAccountsDriverAlive() {
        var connection = new RecordingConnection();
        var reconciler = new StorageFileShareReconciler(new FixedClock());

        var media = Address("home", "media", TenantA, SubscriptionA);
        var logs = Address("home", "logs", TenantA, SubscriptionA);

        using var body = JsonDocument.Parse(StorageFileShares.Body(ClusterId));

        await Pass(reconciler, connection, media, body.RootElement);
        await Pass(reconciler, connection, logs, body.RootElement);

        (await reconciler.DeleteAsync(
                Context(connection, media, body.RootElement),
                TestContext.Current.CancellationToken
            ))
            .ShouldBe(ReconcileOutcome.Converged);

        var ns = ReconcileDriver.NamespaceFor(media);
        connection.Objects.ShouldNotContainKey(RecordingConnection.Key(StorageFileShares.DriverRef(ns, media)));
        connection.Objects.ShouldContainKey(RecordingConnection.Key(StorageFileShares.DriverRef(ns, logs)));
    }

    [Fact]
    public async Task AConnectionThatCannotListLeavesTheDriverStandingAndFails() {
        // ⚠ FAIL CLOSED. Every IKubeClusterConnection double inherits a refusing ListAsync, and a
        // reconciler that read that refusal as "no siblings" would be the one that removes a driver
        // with bound claims behind it. The claim is gone — that half is this share's own — and the
        // outcome is a failure naming the listing, not Converged.
        var connection = new RecordingConnection { RefuseListing = true };
        var address = Address("home", "media", TenantA, SubscriptionA);
        var reconciler = new StorageFileShareReconciler(new FixedClock());

        using var body = JsonDocument.Parse(StorageFileShares.Body(ClusterId));

        await Pass(reconciler, connection, address, body.RootElement);

        var deleted = await reconciler.DeleteAsync(
            Context(connection, address, body.RootElement),
            TestContext.Current.CancellationToken
        );

        deleted.Kind.ShouldBe(ReconcileOutcomeKind.Failed);
        connection.Deleted.Select(static x => x.Kind.Kind).ShouldBe(["PersistentVolumeClaim"]);
        connection.Objects.Keys.ShouldContain(x => x.StartsWith("SeaweedCSIDriver/", StringComparison.Ordinal));
    }

    // ── The volume, which outlives the claim ──────────────────────────────────────────────────

    [Fact]
    public async Task TheDriverWaitsForTheReleasedVolumeToBeReclaimed() {
        // ⚠ THE ORPHAN THE REVIEW FOUND. With reclaimPolicy Delete the CSI external-provisioner — a
        // container in the driver's controller Deployment — is what removes a released volume and its
        // filer directory. A delete that removed the driver the moment the claim read NotFound would,
        // whenever the volume was still Released, take away the one process able to reclaim it, and
        // /fileshares/pvc-… would stay forever: untracked, unbilled, removable only by hand.
        var connection = new RecordingConnection();
        var address = Address("home", "media", TenantA, SubscriptionA);
        var reconciler = new StorageFileShareReconciler(new FixedClock());

        using var body = JsonDocument.Parse(StorageFileShares.Body(ClusterId));

        await Pass(reconciler, connection, address, body.RootElement);

        // The provisioner's work, done by hand: bind the claim to a volume that exists.
        var ns = ReconcileDriver.NamespaceFor(address);
        var claimKey = RecordingConnection.Key(StorageFileShares.ClaimRef(ns, address));
        connection.Objects[claimKey] = StorageFileShares.WithBoundVolume(connection.Objects[claimKey], "pvc-0f7d2c1e");

        var volumeKey = RecordingConnection.Key(StorageFileShares.VolumeRef("pvc-0f7d2c1e"));
        connection.Objects[volumeKey] =
            """{"kind":"PersistentVolume","metadata":{"name":"pvc-0f7d2c1e"},"spec":{"claimRef":{"name":"media-home"}}}""";

        // Three passes with the volume never reclaimed: the claim goes, the driver does not.
        for (var pass = 0; pass < 3; pass++) {
            var outcome = await reconciler.DeleteAsync(
                Context(connection, address, body.RootElement),
                TestContext.Current.CancellationToken
            );

            outcome.Kind.ShouldBe(ReconcileOutcomeKind.InProgress, $"pass {pass} did not wait for the released volume");
        }

        connection.Deleted.Select(static x => x.Kind.Kind).ShouldBe(["PersistentVolumeClaim"]);
        connection.Objects.ShouldContainKey(
            RecordingConnection.Key(StorageFileShares.DriverRef(ns, address)),
            "the driver was removed while its volume was still Released — the volume and its filer "
            + "directory are now nobody's, because the controller that would have reclaimed them went "
            + "with the driver"
        );

        // ⚠ And the volume carries the labels now, which is the ONLY reason a later pass — a
        // different silo, a different process, no memory of pass one — could still find it.
        var volume = JsonNode.Parse(connection.Objects[volumeKey])!.AsObject();
        volume["metadata"]!["labels"]!["storage.cybercloud.io/account"]!.GetValue<string>().ShouldBe("media");
        volume["metadata"]!["labels"]![KubeLabels.ResourceType]!.GetValue<string>()
            .ShouldBe("cybercloud.storage_accounts_fileshares");
        connection.Applied.Single(static x => x.Target.Kind.Kind == "PersistentVolume")
            .Target.Namespace.ShouldBeEmpty("a volume is cluster-scoped");

        // The provisioner finishes. The next pass finds no volume and removes the driver.
        connection.Objects.TryRemove(volumeKey, out _).ShouldBeTrue();

        (await reconciler.DeleteAsync(
                Context(connection, address, body.RootElement),
                TestContext.Current.CancellationToken
            ))
            .ShouldBe(ReconcileOutcome.Converged);

        connection.Deleted.Select(static x => x.Kind.Kind).ShouldBe(["PersistentVolumeClaim", "SeaweedCSIDriver"]);
        connection.Objects.ShouldBeEmpty();
    }

    [Fact]
    public async Task TheVolumeIsLabelledBeforeTheClaimIsDeletedAndAFailedLabelKeepsTheClaim() {
        // ⚠ ORDER. Once the claim is gone nothing on the cluster names the volume, so a label write
        // that failed AFTER the delete would leave exactly the orphan the write exists to prevent.
        // A refused label fails the pass with the claim still standing, and the next pass tries again.
        var connection = new RecordingConnection();
        var address = Address("home", "media", TenantA, SubscriptionA);
        var reconciler = new StorageFileShareReconciler(new FixedClock());

        using var body = JsonDocument.Parse(StorageFileShares.Body(ClusterId));

        await Pass(reconciler, connection, address, body.RootElement);

        var ns = ReconcileDriver.NamespaceFor(address);
        var claimKey = RecordingConnection.Key(StorageFileShares.ClaimRef(ns, address));
        connection.Objects[claimKey] = StorageFileShares.WithBoundVolume(connection.Objects[claimKey], "pvc-0f7d2c1e");
        connection.Objects[RecordingConnection.Key(StorageFileShares.VolumeRef("pvc-0f7d2c1e"))] =
            """{"kind":"PersistentVolume"}""";

        connection.FailApplies = true;

        var outcome = await reconciler.DeleteAsync(
            Context(connection, address, body.RootElement),
            TestContext.Current.CancellationToken
        );

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.Failed);
        connection.Deleted.ShouldBeEmpty("the claim was deleted with its volume unlabelled");
        connection.Objects.ShouldContainKey(claimKey);
    }

    [Fact]
    public async Task AClaimThatNeverBoundHasNoVolumeToWaitFor() {
        // A Pending claim — the shared suite's shape, and the cluster suite's, where no driver runs —
        // deletes in one pass: nothing to label, nothing listed, driver gone.
        var connection = new RecordingConnection();
        var address = Address("home", "media", TenantA, SubscriptionA);
        var reconciler = new StorageFileShareReconciler(new FixedClock());

        using var body = JsonDocument.Parse(StorageFileShares.Body(ClusterId));

        await Pass(reconciler, connection, address, body.RootElement);

        (await reconciler.DeleteAsync(
                Context(connection, address, body.RootElement),
                TestContext.Current.CancellationToken
            ))
            .ShouldBe(ReconcileOutcome.Converged);

        connection.Applied.ShouldAllBe(x => x.Target.Kind.Kind != "PersistentVolume");
        connection.Listed.Count.ShouldBe(2, "claims, then volumes");
        connection.Listed[1].ShouldContain("storage.cybercloud.io/account=media");
        connection.Listed[1].ShouldContain(KubeLabels.SubscriptionId + "=" + KubeLabels.GuidValue(SubscriptionA));
        connection.Listed[1].ShouldContain(KubeLabels.ResourceGroup + "=prod");
    }

    [Fact]
    public async Task DeletingAShareLeavesItsAccountAlone() {
        var connection = new RecordingConnection();
        var address = Address("home", "media", TenantA, SubscriptionA);
        var reconciler = new StorageFileShareReconciler(new FixedClock());

        using var body = JsonDocument.Parse(StorageFileShares.Body(ClusterId));

        var ns = ReconcileDriver.NamespaceFor(address);
        var seaweed = StorageAccounts.SeaweedRef(ns, "media");
        connection.Objects[RecordingConnection.Key(seaweed)] = """{"kind":"Seaweed"}""";

        await Pass(reconciler, connection, address, body.RootElement);
        await reconciler.DeleteAsync(
            Context(connection, address, body.RootElement),
            TestContext.Current.CancellationToken
        );

        connection.Objects.ShouldContainKey(RecordingConnection.Key(seaweed));
    }

    // ── The four clauses, isolated ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ConvergedFollowsTheReadAndNotTheApply() {
        var connection = new RecordingConnection { SwallowApplies = true };
        var address = Address("home", "media", TenantA, SubscriptionA);

        using var body = JsonDocument.Parse(StorageFileShares.Body(ClusterId));

        var outcome = await Pass(
            new StorageFileShareReconciler(new FixedClock()),
            connection,
            address,
            body.RootElement
        );

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.InProgress);
    }

    [Fact]
    public async Task ASecondPassWithTheSameBodyChangesNothing() {
        var connection = new RecordingConnection();
        var address = Address("home", "media", TenantA, SubscriptionA);
        var reconciler = new StorageFileShareReconciler(new FixedClock());

        using var body = JsonDocument.Parse(StorageFileShares.Body(ClusterId));

        await Pass(reconciler, connection, address, body.RootElement);
        var first = connection.Applied.Select(static x => x.Body).ToArray();

        await Pass(reconciler, connection, address, body.RootElement);
        connection.Applied.Skip(first.Length).Select(static x => x.Body).ToArray().ShouldBe(first);
    }

    [Fact]
    public async Task TheDriverIsReappliedOnEveryPassAndCarriesTheLastApplyingSharesId() {
        // ⚠ THE CHURN, PINNED RATHER THAN HIDDEN. Two shares of one account rewrite the driver's
        // labels on every alternating pass, because the builder stamps the applying share's resource
        // id. Reading first and applying only on drift was tried and the k3s-backed suite refused it:
        // a rival field manager's edit of the driver's tenant-id label must CONFLICT on the next pass
        // (ADR-013), and a reconciler that left a spec-matching driver alone never re-asserted its
        // labels, so the tenant's edit of billing attribution became silence. The write is the price
        // of the re-assertion; conformance.yaml § owed `the-driver-carries-one-shares-labels` records
        // what it costs.
        var connection = new RecordingConnection();
        var reconciler = new StorageFileShareReconciler(new FixedClock());

        var home = Address("home", "media", TenantA, SubscriptionA);
        var scratch = Address("scratch", "media", TenantA, SubscriptionA);

        using var body = JsonDocument.Parse(StorageFileShares.Body(ClusterId));

        await Pass(reconciler, connection, home, body.RootElement);
        await Pass(reconciler, connection, scratch, body.RootElement);
        await Pass(reconciler, connection, home, body.RootElement);

        connection.Applied.Count(static x => x.Target.Kind.Kind == "SeaweedCSIDriver").ShouldBe(3);

        var ns = ReconcileDriver.NamespaceFor(home);
        var stored = JsonNode.Parse(
            connection.Objects[RecordingConnection.Key(StorageFileShares.DriverRef(ns, home))]
        )!;
        stored["metadata"]!["labels"]![KubeLabels.ResourceId]!.GetValue<string>()
            .ShouldBe(
                KubeLabels.GuidValue(home.Id),
                "the driver names a share other than the one that applied it last"
            );
    }

    [Fact]
    public async Task AClaimWhoseClassWasRewrittenIsReportedAsDrift() {
        // ⚠ The field that most needs comparing: a claim on another account's class is a share
        // provisioned on another account's filer under this share's resource id.
        var connection = new RecordingConnection();
        var address = Address("home", "media", TenantA, SubscriptionA);
        var reconciler = new StorageFileShareReconciler(new FixedClock());

        using var body = JsonDocument.Parse(StorageFileShares.Body(ClusterId));

        await Pass(reconciler, connection, address, body.RootElement);

        var ns = ReconcileDriver.NamespaceFor(address);
        var key = RecordingConnection.Key(StorageFileShares.ClaimRef(ns, address));
        var tampered = JsonNode.Parse(connection.Objects[key])!.AsObject();
        tampered["spec"]!["storageClassName"] = "somebody-elses-class";
        connection.Objects[key] = tampered.ToJsonString();

        var observed = await reconciler.ObserveAsync(
            new(address, StorageFileShares.V2026, body.RootElement, ns, connection),
            TestContext.Current.CancellationToken
        );

        observed.Exists.ShouldBeTrue();
        observed.Summary.ShouldContain("drifted");
    }

    // ── Harness ───────────────────────────────────────────────────────────────────────────────

    static readonly Guid ClusterId = Guid.Parse("eeeeeeee-0000-4000-8000-000000000005");
    static readonly Guid TenantA = Guid.Parse("11111111-1111-4111-8111-111111111111");
    static readonly Guid TenantB = Guid.Parse("44444444-4444-4444-8444-444444444444");
    static readonly Guid SubscriptionA = Guid.Parse("22222222-2222-4222-8222-222222222222");
    static readonly Guid SubscriptionB = Guid.Parse("55555555-5555-4555-8555-555555555555");

    static async Task<ReconcileOutcome> Pass(
        StorageFileShareReconciler reconciler,
        RecordingConnection connection,
        ResourceId address,
        JsonElement desired
    ) =>
        await reconciler.ReconcileAsync(Context(connection, address, desired), TestContext.Current.CancellationToken);

    static ReconcileContext Context(IKubeClusterConnection? connection, ResourceId address, JsonElement desired) =>
        new(
            address,
            StorageFileShares.V2026,
            desired,
            null,
            ReconcileDriver.NamespaceFor(address),
            connection,
            new UnavailableSecretResolver(),
            new NullLog()
        );

    /// <summary>A share's address inside a named account.</summary>
    static ResourceId Address(string name, string account, Guid tenant, Guid subscription) =>
        new(
            tenant,
            subscription,
            "prod",
            StorageFileShares.Type,
            name,
            Guid.Parse("33333333-3333-4333-8333-333333333333"),
            account
        );

    static JsonObject Spec(string objectJson) => JsonNode.Parse(objectJson)!["spec"]!.AsObject();

    static string Storage(string claimJson) =>
        Spec(claimJson)["resources"]!["requests"]!["storage"]!.GetValue<string>();

    static string DriverName(string driverJson) => Spec(driverJson)["driverName"]!.GetValue<string>();
}
