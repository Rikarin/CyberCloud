using CyberCloud.Core.Time;
using CyberCloud.ResourceManager;
using CyberCloud.ResourceManager.Conformance;
using CyberCloud.ResourceManager.Reconcile;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Storage.Tests;

/// <summary>
///     The object-storage reconciler against a connection that misbehaves in the ways a real cluster
///     does.
/// </summary>
/// <remarks>
///     ⚠ <b>The harness below is a fresh copy and it has to be.</b>
///     <c>src/Providers/README.md § Hard rule</c> forbids a <c>Providers.*</c> assembly referencing
///     another, so <c>RecordingConnection</c>, <c>FixedClock</c> and <c>NullLog</c> cannot be shared
///     with <c>CyberCloud.Providers.Messaging.Tests</c> however identical they look. That duplication
///     is the price of the rule and is worth naming rather than apologising for: the alternative is a
///     line in <c>module-layering.txt</c> between two providers, which rule 2 refuses.
/// </remarks>
public sealed class StorageReconcilerTests {
    // ── Failure class (b): a reconciler with a field ─────────────────────────────────────────────

    [Fact]
    public void TheReconcilerHoldsNoMutableState() {
        // Clause 2, checked structurally, in the cheap place. The conformance run checks it too; this
        // is the one that catches the field somebody adds in a hurry, before a suite has to boot.
        ReconcilerConformance.CheckNoHiddenState(new StorageAccountReconciler(new FixedClock()))
            .ShouldBeEmpty();
    }

    [Fact]
    public void TheStructuralCheckCatchesAReadonlyMutableCache() {
        // ⚠ CALIBRATION, AND IT NOW POINTS THE OTHER WAY. This test used to assert that
        // CheckNoHiddenState MISSED the counter-example below, because it skipped every
        // `field.IsInitOnly` — and `readonly` stops the FIELD being reassigned while stopping
        // nothing about the dictionary, so a per-tenant cache passed clause 2 while accumulating
        // state on a singleton every tenant shares. Seven families each pinned that blind spot
        // and it is now closed; this is what holds it closed.
        //
        // ⚠ THE CROSS-TENANT TEST BELOW STAYS, AND IS NOT MADE REDUNDANT BY THIS. This one reads
        // a field's declared TYPE. That one drives ONE reconciler instance through TWO tenants and
        // compares what each got, which is the only way to catch mixing no field type could show.
        var findings = ReconcilerConformance.CheckNoHiddenState(new ReconcilerWithAReadonlyCache());

        findings.ShouldContain(
            x => x.Clause == ReconcilerClause.NoHiddenState,
            "a readonly field holding a mutable Dictionary is state on a shared singleton, and the "
            + "structural check is what catches it before the behavioural test has to"
        );

        findings.ShouldContain(x => x.Detail.Contains("lastRendered", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OneReconcilerInstanceServesTwoTenantsWithoutMixingThem() {
        // ⚠ THE TEST A SINGLE-TENANT TEST CANNOT BE, AND THE ONLY ONE THAT CATCHES THE CACHE ABOVE.
        // AddCyberCloudProvider registers a reconciler as a SINGLETON BY CONCRETE TYPE, so in a real
        // silo ONE instance serves every tenant in the process.
        var reconciler = new StorageAccountReconciler(new FixedClock());

        // ⚠ THE SAME RESOURCE NAME IN BOTH TENANTS. Two tenants naming an account `assets` is the
        // ordinary case. ⚠ Each brings its OWN subscription, because ReconcileDriver.NamespaceFor is
        // `{subscriptionId:N}-{resourceGroup}` and the TENANT ID IS NOT IN IT — two tenants sharing a
        // subscription id would share a namespace and this test would fail for the harness's reason.
        var alice = Address("assets", TenantA, SubscriptionA);
        var bob = Address("assets", TenantB, SubscriptionB);

        var connection = new RecordingConnection();

        using var aliceBody = JsonDocument.Parse(
            StorageAccounts.Body(ClusterId, volumeServers: 3, storageSize: "100Gi")
        );

        using var bobBody = JsonDocument.Parse(
            StorageAccounts.Body(ClusterId, volumeServers: 6, storageSize: "500Gi")
        );

        // Interleaved, so a cache written on the first pass is read on the third.
        await Pass(reconciler, connection, alice, aliceBody.RootElement);
        await Pass(reconciler, connection, bob, bobBody.RootElement);
        await Pass(reconciler, connection, alice, aliceBody.RootElement);
        await Pass(reconciler, connection, bob, bobBody.RootElement);

        // ⚠ THE SEAWEEDS ONLY, IN PASS ORDER. Each pass now applies two objects — the identities
        // Secret then the Seaweed — so an index into the raw list would land on a Secret half the
        // time. Filtering by kind keeps this test about what it has always been about: whether one
        // singleton instance carries one tenant's body into another tenant's pass.
        var applied = connection.Applied.Where(x => x.Target.Kind.Kind == "Seaweed").ToList();
        applied.Count.ShouldBe(4);

        Volume(applied[0].Body)["replicas"]!.GetValue<int>().ShouldBe(3);
        Volume(applied[1].Body)["replicas"]!.GetValue<int>().ShouldBe(6);
        Volume(applied[2].Body)["replicas"]!.GetValue<int>()
            .ShouldBe(3, "tenant A's volume-server count came back as tenant B's");

        Volume(applied[3].Body)["requests"]!["storage"]!.GetValue<string>()
            .ShouldBe("500Gi", "tenant B's volume size came back as tenant A's");

        applied[0].Labels[KubeLabels.TenantId].ShouldBe(KubeLabels.GuidValue(TenantA));
        applied[1].Labels[KubeLabels.TenantId].ShouldBe(KubeLabels.GuidValue(TenantB));

        // ⚠ And the two tenants' objects are in different namespaces, so the third pass read back
        // Alice's account rather than Bob's. Without this the assertions above would hold for a
        // reconciler that wrote both tenants into one namespace and let the second overwrite the
        // first.
        applied[0].Target.Namespace.ShouldNotBe(applied[1].Target.Namespace);
    }

    // ── The four clauses, isolated ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task ConvergedFollowsTheReadAndNotTheApply() {
        // Clause 4. An apply that reports success and stores nothing is what a swallowing admission
        // webhook looks like from here — and a reconciler that trusted the apply's own result would
        // report Converged for a cluster with nothing in it.
        var connection = new RecordingConnection { SwallowApplies = true };
        using var body = JsonDocument.Parse(StorageAccounts.Body(ClusterId));

        var outcome = await Reconcile(connection, body.RootElement);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.InProgress);
        outcome.ShouldNotBe(ReconcileOutcome.Converged);
    }

    [Fact]
    public async Task EveryAppliedObjectIsAlsoReadBack() {
        // ⚠ ONE OBJECT MAKES THIS LOOK TRIVIAL AND IT IS NOT. A reconciler that applied the Seaweed
        // and returned Converged without reading anything would pass every other test in this file:
        // the apply happened, the body was right, the labels were right. Clause 4 is the claim that
        // the platform OBSERVED what it applied, and set equality in both directions is the only form
        // that catches either half of it going missing.
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(StorageAccounts.Body(ClusterId));

        (await Reconcile(connection, body.RootElement)).ShouldBe(ReconcileOutcome.Converged);

        var applied = connection.Applied.Select(x => RecordingConnection.Key(x.Target))
            .ToHashSet(StringComparer.Ordinal);

        var read = connection.Read.Select(RecordingConnection.Key).ToHashSet(StringComparer.Ordinal);

        read.ShouldBe(
            applied,
            "the reconciler applied " + applied.Count + " object(s) and read back " + read.Count
            + ". An object applied and not read back is one the loop reports Converged without ever "
            + "having observed."
        );
    }

    [Fact]
    public async Task ASecondPassWithTheSameBodyChangesNothing() {
        // Clause 1. Nothing in SeaweedJson counts, appends or timestamps, and this is what says so.
        //
        // ⚠ FAILURE CLASS (d), AND IT IS NOT AN AUXILIARY ASSERTION — IT IS THIS ONE. The reconciler
        // generates a FRESH key pair on every pass and hands it to the vault, so byte-stability here
        // is a statement about mint-once: the second pass's candidate must be discarded and the
        // rendered identities Secret must still carry the FIRST pass's pair. A reconciler that
        // overwrote on mint, or that rendered its candidate instead of what it resolved back, fails
        // exactly here — with the identities Secret's body differing and the Seaweed's identical.
        //
        // ⚠ One vault across both passes, which is what production is. A fresh store per pass would
        // let a mint-every-time reconciler pass, because each pass would then be the first.
        var connection = new RecordingConnection();
        var vault = new InMemorySecretVault();
        using var body = JsonDocument.Parse(StorageAccounts.Body(ClusterId));

        await Reconcile(connection, body.RootElement, vault);
        var first = connection.Applied.Select(x => x.Body).ToArray();

        await Reconcile(connection, body.RootElement, vault);
        var second = connection.Applied.Skip(first.Length).Select(x => x.Body).ToArray();

        second.ShouldBe(first);

        vault.Writes.ShouldBe(
            1,
            "the second pass minted a second credential. Mint-once is what stops a reconcile loop "
            + "from rotating a tenant's key pair out from under them on every reminder."
        );
    }

    [Fact]
    public async Task AnUnreachableClusterSuspendsRatherThanFails() {
        // docs/plan/09 § Cluster connections. A tenant whose cluster is down has a resource that is
        // still coming, not one that broke.
        var connection = new RecordingConnection { Suspend = true };
        using var body = JsonDocument.Parse(StorageAccounts.Body(ClusterId));

        (await Reconcile(connection, body.RootElement)).Kind.ShouldBe(ReconcileOutcomeKind.InProgress);
    }

    [Fact]
    public async Task AConflictBecomesProgressRatherThanAFailureOrAForcedOverwrite() {
        // ADR-013 makes a conflict "a drift event with a name". ⚠ `.spec.volume.replicas` is the
        // plausible one on this type: it is the capacity axis, and a tenant running any autoscaler
        // over their own cluster can end up owning it. Forcing would silently undo them every pass.
        var connection = new RecordingConnection { ConflictField = ".spec.volume.replicas" };
        using var body = JsonDocument.Parse(StorageAccounts.Body(ClusterId));

        var outcome = await Reconcile(connection, body.RootElement);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.InProgress);
        outcome.Reason.ShouldContain("replicas");
    }

    [Fact]
    public async Task DeleteReportsConvergedOnlyOnceTheObjectIsGone() {
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(StorageAccounts.Body(ClusterId));

        await Reconcile(connection, body.RootElement);

        var reconciler = new StorageAccountReconciler(new FixedClock());
        var deleted = await reconciler.DeleteAsync(
            Context(connection, body.RootElement),
            TestContext.Current.CancellationToken
        );

        deleted.ShouldBe(ReconcileOutcome.Converged);
        connection.Deleted.Count.ShouldBe(2);

        // ⚠ THE SEAWEED FIRST AND THE IDENTITIES SECOND, WHICH IS THE APPLY ORDER REVERSED. Removing
        // the identities file from under a gateway that is still serving would restart it with no
        // identities — and SeaweedFS answers every unauthenticated caller as ACTION_ADMIN when it has
        // none. A teardown interrupted between the two leaves a Secret nobody mounts, which is inert.
        connection.Deleted[0].Kind.Kind.ShouldBe("Seaweed");
        connection.Deleted[1].Kind.Kind.ShouldBe("Secret");
    }

    // ── Failure class (f), at the object: the credential reference is never optional ─────────────

    [Fact]
    public async Task TheGatewaysConfigSecretReferenceIsRenderedOnEveryPass() {
        // ⚠ THE ONE FIELD WHOSE ABSENCE MAKES THE SERVICE WORK, WHICH IS WHY NOTHING ELSE WOULD EVER
        // REPORT IT. weed/s3api/auth_credentials.go sets `isAuthEnabled = len(identities) > 0` and
        // AuthenticateRequest returns an ADMIN identity when it is false — so a Seaweed rendered
        // without spec.s3.configSecret comes up, passes every readiness probe, converges, and serves
        // every anonymous request as an administrator. A "temporarily drop the reference so the
        // account can finish" change would look like a fix.
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(StorageAccounts.Body(ClusterId));

        await Reconcile(connection, body.RootElement);

        // ⚠ BY KIND AND NOT BY INDEX. This provider applies two objects now — the identities Secret
        // first, then the Seaweed that mounts it — and Applied[0] was the Seaweed when it applied
        // one. An index here would silently start asserting about the wrong document.
        var s3 = JsonNode.Parse(Seaweed(connection).Body)!["spec"]!["s3"]!.AsObject();

        s3["configSecret"].ShouldNotBeNull(
            "the S3 gateway was rendered with no identities file. SeaweedFS treats that as "
            + "\"authentication disabled\" and grants ACTION_ADMIN to every anonymous caller."
        );

        s3["configSecret"]!["name"]!.GetValue<string>().ShouldBe("observed-s3-config");
        s3["configSecret"]!["key"]!.GetValue<string>().ShouldBe("s3.json");
    }

    [Fact]
    public async Task TheRenderedObjectCarriesNoSecretValueAnywhere() {
        // docs/plan/05: credentials never in grain state, and never in a rendered object either. The
        // CR names a Secret; it never carries one.
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(StorageAccounts.Body(ClusterId));

        await Reconcile(connection, body.RootElement);

        // ⚠ THE SEAWEED, AND ONLY THE SEAWEED — because the identities Secret DOES carry the
        // credential and has to. That is the whole shape of piece 5: exactly one rendered object
        // holds the value, it is the one the gateway mounts, and it is built from what the vault
        // returned rather than from desired state. Asserting this over every applied object would
        // fail on the object that is supposed to carry it; asserting it over none would let the
        // credential drift into the CR unnoticed.
        foreach (var forbidden in new[] { "accessKey", "secretKey", "secretAccessKey", "stringData" }) {
            Seaweed(connection).Body.ShouldNotContain(forbidden, Case.Sensitive, forbidden);
        }
    }

    [Fact]
    public async Task TheCredentialReachesTheIdentitiesSecretAndNothingElse() {
        // ⚠ THE OTHER HALF OF THE TEST ABOVE, AND WITHOUT IT THAT ONE IS SATISFIED BY A RECONCILER
        // THAT MINTS AND THEN RENDERS NOTHING. A Secret applied with an empty identities list is a
        // gateway that comes up with `isAuthEnabled = false` — the exact failure the reference in
        // the CR exists to prevent, reached from the other side.
        var connection = new RecordingConnection();
        var vault = new InMemorySecretVault();
        using var body = JsonDocument.Parse(StorageAccounts.Body(ClusterId));

        (await Reconcile(connection, body.RootElement, vault)).ShouldBe(ReconcileOutcome.Converged);

        var path = StorageAccounts.SecretPath(Address("observed", TenantA, SubscriptionA));
        var secretAccessKey = vault.Peek(path, StorageAccounts.SecretAccessKeyField);

        secretAccessKey.ShouldNotBeNull("the reconciler did not mint a key pair at all");

        var rendered = connection.Applied.Single(x => x.Target.Kind.Kind == "Secret").Body;

        // ⚠ Decoded, because the Secret carries `data` rather than `stringData` — see
        // StorageAccounts.ConfigSecretJson for why. A test that searched the base64 for a plain
        // string would pass whatever the reconciler wrote.
        var identities = Encoding.UTF8.GetString(
            Convert.FromBase64String(
                JsonNode.Parse(rendered)!["data"]![StorageAccounts.ConfigSecretKey]!.GetValue<string>()
            )
        );

        identities.ShouldContain(
            secretAccessKey,
            customMessage: "the identities Secret does not carry the secret access key the vault "
            + "holds, so the gateway authenticates nobody — and SeaweedFS answers an unauthenticated "
            + "caller as ACTION_ADMIN when it has no identities."
        );

        identities.ShouldContain(
            vault.Peek(path, StorageAccounts.AccessKeyIdField)!,
            customMessage: "the identities file names a different access key id than the vault holds, "
            + "so listKeys hands out a pair the gateway will not accept"
        );
    }

    // ── Failure class (e): a partial failure, in the order that survives one ──────────────────

    [Fact]
    public async Task AVaultThatRefusesLeavesNOTHINGAppliedToTheCluster() {
        // ⚠ THIS IS THE ORDER ARGUMENT, AS AN ASSERTION.
        //
        // Two orders and two partial failures. Mint-then-apply leaves an orphaned KV document: inert,
        // nothing running, nothing billed, and the next pass reuses it because mint-once makes the
        // retry converge on the same pair. Apply-then-mint leaves a Seaweed whose gateway has no
        // identities file — and `weed/s3api/auth_credentials.go` sets
        // `isAuthEnabled = len(identities) > 0`, so `AuthenticateRequest` answers every
        // unauthenticated caller as ACTION_ADMIN. A leaked KV entry is housekeeping; an S3 endpoint
        // open to the cluster network is an incident.
        //
        // So the credential goes first, and this asserts that a vault failure stops the pass BEFORE
        // anything reaches the API server.
        var connection = new RecordingConnection();
        var vault = new InMemorySecretVault { RefuseMint = true };
        using var body = JsonDocument.Parse(StorageAccounts.Body(ClusterId));

        var outcome = await Reconcile(connection, body.RootElement, vault);

        outcome.Kind.ShouldNotBe(ReconcileOutcomeKind.Converged);

        connection.Applied.ShouldBeEmpty(
            "the reconciler applied to the cluster before the credential existed. A Seaweed whose "
            + "identities Secret is never written comes up authenticating nobody, and SeaweedFS "
            + "grants ACTION_ADMIN to every anonymous caller when it has no identities."
        );
    }

    [Fact]
    public async Task AVaultFailureIsRetryableSoTheNextPassCanConverge() {
        // The other half of the order argument: refusing must not be terminal. A sealed, unreachable
        // or unwired vault is a resource that has not started, not one that broke — and the sixty
        // minute ceiling in ReconcileSchedule is what eventually makes it actionable.
        var connection = new RecordingConnection();
        var vault = new InMemorySecretVault { RefuseMint = true };
        using var body = JsonDocument.Parse(StorageAccounts.Body(ClusterId));

        var refused = await Reconcile(connection, body.RootElement, vault);
        refused.Retryable.ShouldBeTrue(refused.Error?.Message);

        vault.RefuseMint = false;

        (await Reconcile(connection, body.RootElement, vault)).ShouldBe(
            ReconcileOutcome.Converged,
            "a pass that failed on the vault left something behind that stops the next one"
        );

        vault.Writes.ShouldBe(1);
    }

    [Fact]
    public async Task AnOrphanedMintIsReusedRatherThanReplacedWhenTheClusterComesBack() {
        // The surviving failure, driven: the vault write lands, the cluster refuses, and the pass
        // that succeeds afterwards hands out the SAME pair. That is what makes the orphan harmless —
        // it is not litter, it is the credential this resource was always going to have.
        var failing = new RecordingConnection { FailApplies = true };
        var vault = new InMemorySecretVault();
        using var body = JsonDocument.Parse(StorageAccounts.Body(ClusterId));

        await Reconcile(failing, body.RootElement, vault);

        var path = StorageAccounts.SecretPath(Address("observed", TenantA, SubscriptionA));
        var minted = vault.Peek(path, StorageAccounts.SecretAccessKeyField);

        minted.ShouldNotBeNull("the credential was not minted before the cluster was touched");

        var working = new RecordingConnection();
        (await Reconcile(working, body.RootElement, vault)).ShouldBe(ReconcileOutcome.Converged);

        vault.Peek(path, StorageAccounts.SecretAccessKeyField).ShouldBe(
            minted,
            "the recovery pass minted a second credential, so a tenant who had already read the "
            + "first one holds a key the gateway no longer accepts"
        );

        vault.Writes.ShouldBe(1);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void TheKeptClaimsFollowTheRenderedVolumeServerCountAndTheRenderedFiler(int volumeServers) {
        // ⚠ THE THREE INPUTS THAT ARE OURS ARE READ BACK OUT OF THE RENDERED Seaweed, because
        // everything else about these names belongs to the operator. `spec.volume.replicas` is how
        // many `mount0-{name}-volume-{i}` exist; the ABSENCE of `spec.volume.hostPath` is why they
        // are claims at all rather than node-local disks — volumeServerDisksFor branches on that
        // field and not on storageClassName; and `spec.filer.persistence.enabled` is why the filer
        // has a claim to keep.
        using var desired = JsonDocument.Parse(StorageAccounts.Body(ClusterId, volumeServers: volumeServers));

        var spec = JsonNode.Parse(StorageAccounts.SeaweedJson("observed", desired.RootElement))!["spec"]!.AsObject();

        spec["volume"]!["replicas"]!.GetValue<int>().ShouldBe(volumeServers);
        (spec["volume"] as JsonObject)!.ContainsKey("hostPath").ShouldBeFalse();
        spec["filer"]!["persistence"]!["enabled"]!.GetValue<bool>().ShouldBeTrue();
        spec["filer"]!["replicas"]!.GetValue<int>().ShouldBe(1);

        var claims = StorageAccounts.RetainedClaims("ns", "observed", desired.RootElement);

        claims.Select(x => x.Claim.Name).ShouldBe(
            [
                .. Enumerable.Range(0, volumeServers).Select(i => $"mount0-observed-volume-{i}"),
                // ⚠ The doubled name is real: the operator uses `m.Name + "-filer"` for the
                // StatefulSet AND for its claim template, and Kubernetes composes
                // {template}-{set}-{ordinal}.
                "observed-filer-observed-filer-0"
            ]
        );

        foreach (var claim in claims) {
            claim.Claim.Kind.ShouldBe(RetainedVolume.ClaimKind);
            claim.Claim.Namespace.ShouldBe("ns");
            claim.OwnedBy["app.kubernetes.io/managed-by"].ShouldBe("seaweedfs-operator");
            claim.OwnedBy["app.kubernetes.io/name"].ShouldBe("seaweedfs");
            claim.OwnedBy["app.kubernetes.io/instance"].ShouldBe("observed");
        }

        // ⚠ The two sets carry DIFFERENT component labels, and a single list would have hidden it: a
        // filer claim wearing `component=volume` is refused by VolumeReclaimer's guard and the
        // metadata store survives every purge.
        claims[0].OwnedBy["app.kubernetes.io/component"].ShouldBe("volume");
        claims[^1].OwnedBy["app.kubernetes.io/component"].ShouldBe("filer");
    }

    [Fact]
    public async Task TheFinalTeardownRemovesTheObjectDisksAndTheFilersMetadataStore() {
        // ⚠ THE ONE THAT WOULD HAVE CAUGHT THE LEAK, and here upstream retention is a DECISION rather
        // than an omission: internal/controller/pv_reclaim.go pins the claim retention policy's
        // `whenDeleted` to Retain as a constant, with a comment saying deleting on cluster delete
        // "would be an unpleasant surprise", and its own unit test asserts it for every input. So the
        // disks were always going to survive the teardown — which is what makes this type's seven-day
        // window worth having — and until RetainedVolumesAsync they survived the purge too.
        var connection = new RecordingConnection();
        using var desired = JsonDocument.Parse(StorageAccounts.Body(ClusterId));
        var context = Context(connection, desired.RootElement);

        var planted = StorageAccounts.RetainedClaims(context.Namespace, "observed", desired.RootElement);
        planted.Length.ShouldBe(4);

        foreach (var claim in planted) {
            connection.Objects[RecordingConnection.Key(claim.Claim)] = new JsonObject {
                ["metadata"] = new JsonObject {
                    ["name"] = claim.Claim.Name,
                    ["namespace"] = claim.Claim.Namespace,
                    ["labels"] = new JsonObject(
                        claim.OwnedBy.Select(x => KeyValuePair.Create(x.Key, (JsonNode?)JsonValue.Create(x.Value)))
                    )
                }
            }.ToJsonString();
        }

        var outcome = await VolumeReclaimer.ReclaimAsync(
            new StorageAccountReconciler(new FixedClock()),
            context,
            TestContext.Current.CancellationToken
        );

        outcome.IsConverged.ShouldBeTrue(outcome.ToString());

        foreach (var claim in planted) {
            connection.Objects.ContainsKey(RecordingConnection.Key(claim.Claim)).ShouldBeFalse(
                $"'{claim.Claim}' survived the final teardown, so a purged account returned its quota "
                + "and left a tenant's objects on disk."
            );
        }
    }

    /// <summary>The Seaweed command, found by kind rather than by position.</summary>
    static KubeCommand Seaweed(RecordingConnection connection) =>
        connection.Applied.Single(x => x.Target.Kind.Kind == "Seaweed");

    // ── Harness ───────────────────────────────────────────────────────────────────────────────

    static readonly Guid ClusterId = Guid.Parse("eeeeeeee-0000-4000-8000-000000000005");
    static readonly Guid TenantA = Guid.Parse("11111111-1111-4111-8111-111111111111");
    static readonly Guid TenantB = Guid.Parse("44444444-4444-4444-8444-444444444444");
    static readonly Guid SubscriptionA = Guid.Parse("22222222-2222-4222-8222-222222222222");
    static readonly Guid SubscriptionB = Guid.Parse("55555555-5555-4555-8555-555555555555");

    static async Task<ReconcileOutcome> Reconcile(
        RecordingConnection connection,
        JsonElement desired,
        InMemorySecretVault? vault = null
    ) =>
        await new StorageAccountReconciler(new FixedClock())
            .ReconcileAsync(Context(connection, desired, vault), TestContext.Current.CancellationToken);

    static async Task<ReconcileOutcome> Pass(
        StorageAccountReconciler reconciler,
        RecordingConnection connection,
        ResourceId address,
        JsonElement desired,
        InMemorySecretVault? vault = null
    ) {
        // ⚠ One vault per ADDRESS by default, which is what the cross-tenant test needs: two tenants
        // minting at two paths must not be able to read each other back, and a shared store would
        // pass that test for free by holding both.
        var store = vault ?? new InMemorySecretVault();

        return await reconciler.ReconcileAsync(
            new(
                address,
                StorageAccounts.V2026,
                desired,
                null,
                ReconcileDriver.NamespaceFor(address),
                connection,
                store,
                new NullLog()
            ) {
                SecretWriter = store
            },
            TestContext.Current.CancellationToken
        );
    }

    /// <summary>
    ///     A pass over a working vault, which every clause assertion in this file needs.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The default here used to be <c>UnavailableSecretResolver</c> and could not stay one.</b>
    ///     This reconciler now mints before it applies — see its own remarks on why the credential
    ///     goes first — so a context with a refusing writer makes every pass fail for a wiring reason
    ///     and no clause is exercised at all. <c>InMemorySecretVault</c> implements mint-once for
    ///     real, so the idempotence assertion is still measuring the reconciler.
    /// </remarks>
    static ReconcileContext Context(
        IKubeClusterConnection? connection,
        JsonElement desired,
        InMemorySecretVault? vault = null
    ) {
        var address = Address("observed", TenantA, SubscriptionA);
        var store = vault ?? new InMemorySecretVault();

        return new(
            address,
            StorageAccounts.V2026,
            desired,
            null,
            ReconcileDriver.NamespaceFor(address),
            connection,
            store,
            new NullLog()
        ) {
            SecretWriter = store
        };
    }

    /// <summary>An address in a named tenant and its own subscription.</summary>
    static ResourceId Address(string name, Guid tenant, Guid subscription) =>
        new(
            tenant,
            subscription,
            "prod",
            StorageAccounts.Type,
            name,
            Guid.Parse("33333333-3333-4333-8333-333333333333")
        );

    static JsonObject Volume(string objectJson) =>
        JsonNode.Parse(objectJson)!["spec"]!["volume"]!.AsObject();
}

/// <summary>
///     A reconciler that <c>CheckNoHiddenState</c> reports and that is not stateless.
/// </summary>
/// <remarks>
///     The field is <see langword="readonly" />, which stops it being reassigned and stops nothing
///     about the dictionary it holds. That is the shape a per-tenant cache takes when somebody adds
///     one for performance. <c>CheckNoHiddenState</c> used to skip it for being
///     <see langword="readonly" /> and now reports it; the cross-tenant test in the sibling file is
///     what still catches the mixing a field's declared type cannot show.
/// </remarks>
sealed class ReconcilerWithAReadonlyCache : IResourceReconciler {
    readonly Dictionary<string, string> lastRendered = new(StringComparer.Ordinal);

    public ResourceTypeName Type => StorageAccounts.Type;

    public Task<ReconcileOutcome> ReconcileAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        lastRendered[context.Id.Name] = context.Desired.GetRawText();
        return Task.FromResult(ReconcileOutcome.Converged);
    }

    public Task<ReconcileOutcome> DeleteAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) => Task.FromResult(ReconcileOutcome.Converged);

    public Task<ObservedState> ObserveAsync(
        ObserveContext context,
        CancellationToken cancellationToken = default
    ) => Task.FromResult(ObservedState.Absent);
}

/// <summary>A connection that records what it was asked to do and can be made to misbehave.</summary>
sealed class RecordingConnection : IKubeClusterConnection {
    /// <summary>What is in the "cluster", keyed by kind, namespace and name.</summary>
    public ConcurrentDictionary<string, string> Objects { get; } = new(StringComparer.Ordinal);

    /// <summary>Every command applied, in order.</summary>
    public List<KubeCommand> Applied { get; } = [];

    /// <summary>Every object deleted, in order.</summary>
    public List<ObjectRef> Deleted { get; } = [];

    /// <summary>Every object <i>read</i>, in order — clause 4's evidence.</summary>
    public List<ObjectRef> Read { get; } = [];

    /// <summary>Whether every apply answers <c>Suspended</c>.</summary>
    public bool Suspend { get; init; }

    /// <summary>The field another manager owns, or empty.</summary>
    public string ConflictField { get; init; } = string.Empty;

    /// <summary>Whether an apply reports success and stores nothing — the clause-4 trap.</summary>
    public bool SwallowApplies { get; init; }

    /// <summary>
    ///     Whether every apply fails outright, for the half of a partial failure the cluster owns.
    /// </summary>
    /// <remarks>
    ///     ⚠ Distinct from <see cref="Suspend" />, which is "we cannot reach your cluster" and is
    ///     survivable by definition. This is the API server refusing, which is the case that leaves a
    ///     credential minted and nothing running.
    /// </remarks>
    public bool FailApplies { get; set; }

    public Guid ClusterId => Guid.Parse("eeeeeeee-0000-4000-8000-000000000005");

    public Task<Result<ApplyOutcome>> ApplyAsync(KubeCommand command, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(command);

        if (FailApplies) {
            // ⚠ Not recorded in Applied: a refused apply changed nothing, and the ordering assertion
            // reads that list to prove the cluster was never touched before the vault was.
            return Task.FromResult(
                Result<ApplyOutcome>.Failure(ErrorCode.ProvisioningFailed, "the API server refused.")
            );
        }

        Applied.Add(command);

        if (Suspend) {
            return Task.FromResult(
                Result<ApplyOutcome>.Success(
                    new() {
                        Result = ApplyResult.Suspended,
                        Target = command.Target,
                        Message = "We cannot reach your cluster; this will resume automatically."
                    }
                )
            );
        }

        if (ConflictField.Length > 0) {
            return Task.FromResult(
                Result<ApplyOutcome>.Success(
                    new() {
                        Result = ApplyResult.Conflict,
                        Target = command.Target,
                        Drift = new() {
                            Target = command.Target,
                            FieldManager = command.FieldManager,
                            Conflicts = [new() { Field = ConflictField, OwnedBy = "kubectl-edit" }]
                        }
                    }
                )
            );
        }

        if (!SwallowApplies) {
            Objects[Key(command.Target)] = command.Body;
        }

        return Task.FromResult(
            Result<ApplyOutcome>.Success(new() { Result = ApplyResult.Created, Target = command.Target })
        );
    }

    public Task<Result<KubeObject>> GetAsync(ObjectRef target, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(target);
        Read.Add(target);

        return Task.FromResult(
            Objects.TryGetValue(Key(target), out var json)
                ? Result<KubeObject>.Success(new() { Ref = target, Json = json })
                : Result<KubeObject>.Failure(ErrorCode.ResourceNotFound, $"'{target}' is not here.")
        );
    }

    public Task<Result> DeleteAsync(
        KubeCommand command,
        CascadePolicy policy = CascadePolicy.Background,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(command);

        var removed = Objects.TryRemove(Key(command.Target), out _);

        if (removed) {
            Deleted.Add(command.Target);
        }

        return Task.FromResult(
            removed
                ? Result.Success
                : Result.Failure(ErrorCode.ResourceNotFound, $"'{command.Target}' is not here.")
        );
    }

    /// <summary>
    ///     ⚠ Keyed by kind, namespace AND name. The namespace is in it because the cross-tenant test
    ///     puts the same resource name in two tenants, which is the only shape in which one singleton
    ///     reconciler serving both can be caught mixing them.
    /// </summary>
    internal static string Key(ObjectRef target) =>
        target.Kind.Kind + "/" + target.Namespace + "/" + target.Name;
}

/// <summary>A clock that does not move. Nothing here depends on time passing.</summary>
sealed class FixedClock : IClock {
    public DateTimeOffset UtcNow => new(2026, 8, 12, 12, 0, 0, TimeSpan.Zero);
}

/// <summary>A log that drops everything. These tests assert outcomes, not progress.</summary>
sealed class NullLog : IReconcileLog {
    public void Report(string phase, string detail) { }

    public void Report(string phase, string detail, int percent) { }
}
