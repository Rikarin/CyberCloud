using CyberCloud.ResourceManager;
using CyberCloud.ResourceManager.Reconcile;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Storage.Tests;

/// <summary>
///     The two handlers that read the cluster rather than the vault: a bucket's <c>stats</c> off the
///     operator's <c>status.usage</c>, and a share's <c>listMountTargets</c> off its bound claim.
/// </summary>
/// <remarks>
///     ⚠ Both are driven against a full document the operator would have left, which the shared
///     suite's fake never produces on its own — <c>OperatorWritten</c> plants one there, and these
///     tests are where the projection is checked field by field against literals.
/// </remarks>
public sealed class StorageActionHandlerTests {
    // ── stats ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task StatsHandsBackWhatTheOperatorSampledAndWhenItSampledIt() {
        var connection = new RecordingConnection();
        var address = BucketAddress("assets", "media");
        var ns = ReconcileDriver.NamespaceFor(address);

        using var body = JsonDocument.Parse(StorageBuckets.Body(ClusterId));

        // The Bucket as the reconciler applied it, then as bucket_usage.go's refresher patched it.
        connection.Objects[RecordingConnection.Key(StorageBuckets.BucketRef(ns, address))] =
            StorageBuckets.WithSampledUsage(
                StorageBuckets.BucketJson(address, body.RootElement),
                objectCount: 1234,
                sizeBytes: 987654321,
                sampledAt: "2026-09-15T10:05:00Z"
            );

        var answer = await new StorageBucketStatsHandler().InvokeAsync(
            Action(connection, address, StorageBuckets.StatsAction, body.RootElement),
            TestContext.Current.CancellationToken
        );

        answer.IsSuccess.ShouldBeTrue(answer.Error?.Message);
        var response = JsonNode.Parse(answer.GetValueOrThrow())!.AsObject();

        response["objectCount"]!.GetValue<long>().ShouldBe(1234);
        response["sizeBytes"]!.GetValue<long>().ShouldBe(987654321);

        // ⚠ THE OPERATOR'S CLOCK, NOT OURS. A handler that stamped IClock.UtcNow here would present a
        // five-minute-old sample as live, which is the exact thing the response's own description
        // warns a caller about.
        response["sampledAt"]!.GetValue<string>().ShouldBe("2026-09-15T10:05:00Z");

        StorageBuckets.StatsResponse.Validate(JsonDocument.Parse(answer.GetValueOrThrow()).RootElement)
            .IsSuccess.ShouldBeTrue("the handler's body does not satisfy the response shape the provider publishes");
    }

    [Fact]
    public async Task StatsRefusesRatherThanAnsweringZeroForABucketTheRefresherHasNotReached() {
        // ⚠ A fresh bucket has no status.usage for up to one refresh interval. Zeros with a made-up
        // timestamp would be read as "empty"; the refusal says the operator has not sampled yet.
        var connection = new RecordingConnection();
        var address = BucketAddress("assets", "media");
        var ns = ReconcileDriver.NamespaceFor(address);

        using var body = JsonDocument.Parse(StorageBuckets.Body(ClusterId));

        connection.Objects[RecordingConnection.Key(StorageBuckets.BucketRef(ns, address))] =
            StorageBuckets.BucketJson(address, body.RootElement);

        var answer = await new StorageBucketStatsHandler().InvokeAsync(
            Action(connection, address, StorageBuckets.StatsAction, body.RootElement),
            TestContext.Current.CancellationToken
        );

        answer.IsFailure.ShouldBeTrue();
        answer.Error!.Code.ShouldBe(ErrorCode.OperationInProgress);
        answer.Error.Message.ShouldContain("five minutes");
    }

    [Fact]
    public async Task StatsReadsTheBucketNamedByTheAddressAndNothingElse() {
        var connection = new RecordingConnection();
        var address = BucketAddress("assets", "media");

        using var body = JsonDocument.Parse(StorageBuckets.Body(ClusterId));

        await new StorageBucketStatsHandler().InvokeAsync(
            Action(connection, address, StorageBuckets.StatsAction, body.RootElement),
            TestContext.Current.CancellationToken
        );

        connection.Read.Single().Name.ShouldBe("media-assets");
        connection.Read.Single().Kind.Kind.ShouldBe("Bucket");
        connection.Applied.ShouldBeEmpty("an action wrote to the cluster");
    }

    // ── listMountTargets ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListMountTargetsHandsBackTheClaimTheFilerThePathAndTheCollection() {
        var connection = new RecordingConnection();
        var address = ShareAddress("home", "media");
        var ns = ReconcileDriver.NamespaceFor(address);

        using var body = JsonDocument.Parse(StorageFileShares.Body(ClusterId));

        connection.Objects[RecordingConnection.Key(StorageFileShares.ClaimRef(ns, address))] =
            StorageFileShares.WithBoundVolume(
                StorageFileShares.ClaimJson(ns, address, body.RootElement),
                "pvc-6f1c0a2e-9b3d-4c7e-8f10-2a4b6c8d0e12"
            );

        var answer = await new StorageFileShareListMountTargetsHandler().InvokeAsync(
            Action(connection, address, StorageFileShares.ListMountTargetsAction, body.RootElement),
            TestContext.Current.CancellationToken
        );

        answer.IsSuccess.ShouldBeTrue(answer.Error?.Message);
        var response = JsonNode.Parse(answer.GetValueOrThrow())!.AsObject();

        // ⚠ Literals throughout: the claim name a pod writes, the filer Service the operator names,
        // the parentDir the StorageClass carries, and the collection the CSI mounter derives.
        response["claimName"]!.GetValue<string>().ShouldBe("media-home");
        response["accessMode"]!.GetValue<string>().ShouldBe("ReadWriteMany");
        response["filer"]!.GetValue<string>().ShouldBe("media-filer." + ns + ".svc:8888");
        response["path"]!.GetValue<string>().ShouldBe("/fileshares/pvc-6f1c0a2e-9b3d-4c7e-8f10-2a4b6c8d0e12");
        response["collection"]!.GetValue<string>().ShouldBe("pvc-6f1c0a2e-9b3d-4c7e-8f10-2a4b6c8d0e12");

        StorageFileShares.ListMountTargetsResponse.Validate(JsonDocument.Parse(answer.GetValueOrThrow()).RootElement)
            .IsSuccess.ShouldBeTrue("the handler's body does not satisfy the response shape the provider publishes");
    }

    [Fact]
    public async Task ListMountTargetsRefusesWhileTheClaimIsStillPending() {
        // ⚠ An empty path would fail the response schema's own Required and — worse — read as a path.
        var connection = new RecordingConnection();
        var address = ShareAddress("home", "media");
        var ns = ReconcileDriver.NamespaceFor(address);

        using var body = JsonDocument.Parse(StorageFileShares.Body(ClusterId));

        connection.Objects[RecordingConnection.Key(StorageFileShares.ClaimRef(ns, address))] =
            StorageFileShares.ClaimJson(ns, address, body.RootElement);

        var answer = await new StorageFileShareListMountTargetsHandler().InvokeAsync(
            Action(connection, address, StorageFileShares.ListMountTargetsAction, body.RootElement),
            TestContext.Current.CancellationToken
        );

        answer.IsFailure.ShouldBeTrue();
        answer.Error!.Code.ShouldBe(ErrorCode.OperationInProgress);
        answer.Error.Message.ShouldContain("not yet bound");
    }

    [Fact]
    public async Task ListMountTargetsNeverTouchesTheVault() {
        // ⚠ Nothing in the response is a credential, and the handler is built so that it cannot become
        // one by accident: the resolver it is handed refuses by name, and the call succeeds anyway.
        var connection = new RecordingConnection();
        var address = ShareAddress("home", "media");
        var ns = ReconcileDriver.NamespaceFor(address);

        using var body = JsonDocument.Parse(StorageFileShares.Body(ClusterId));

        connection.Objects[RecordingConnection.Key(StorageFileShares.ClaimRef(ns, address))] =
            StorageFileShares.WithBoundVolume(StorageFileShares.ClaimJson(ns, address, body.RootElement), "pvc-1");

        var answer = await new StorageFileShareListMountTargetsHandler().InvokeAsync(
            Action(connection, address, StorageFileShares.ListMountTargetsAction, body.RootElement),
            TestContext.Current.CancellationToken
        );

        answer.IsSuccess.ShouldBeTrue(answer.Error?.Message);
        answer.GetValueOrThrow().ShouldNotContain("secret", Case.Insensitive);
    }

    // ── Harness ───────────────────────────────────────────────────────────────────────────────

    static readonly Guid ClusterId = Guid.Parse("eeeeeeee-0000-4000-8000-000000000005");
    static readonly Guid Tenant = Guid.Parse("11111111-1111-4111-8111-111111111111");
    static readonly Guid Subscription = Guid.Parse("22222222-2222-4222-8222-222222222222");

    static ActionContext Action(IKubeClusterConnection connection, ResourceId address, string action, JsonElement desired) =>
        new(
            address,
            StorageAccounts.V2026,
            action,
            JsonDocument.Parse("{}").RootElement,
            desired,
            ReconcileDriver.NamespaceFor(address),
            connection,
            new UnavailableSecretResolver()
        );

    static ResourceId BucketAddress(string name, string account) =>
        new(Tenant, Subscription, "prod", StorageBuckets.Type, name, Guid.Parse("33333333-3333-4333-8333-333333333333"), account);

    static ResourceId ShareAddress(string name, string account) =>
        new(Tenant, Subscription, "prod", StorageFileShares.Type, name, Guid.Parse("33333333-3333-4333-8333-333333333334"), account);
}
