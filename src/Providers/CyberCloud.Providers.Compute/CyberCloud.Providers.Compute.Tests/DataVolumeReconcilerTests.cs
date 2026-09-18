using CyberCloud.ResourceManager.Reconcile;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Compute.Tests;

/// <summary>
///     The image and disk reconcilers: one pass, two readiness rules, and the renders each owns.
/// </summary>
public sealed class DataVolumeReconcilerTests {
    [Fact]
    public async Task AnImageIsConvergedOnlyWhenCdiSaysSucceededAndADiskWhenProvisioned() {
        // ⚠ THE TWO RULES, SIDE BY SIDE. An image without its bytes is useless; a disk without a
        // consumer is inventory. Cdi.IsPopulated and Cdi.IsProvisioned are the two spellings.
        var image = new ImageReconciler(new FixedClock());
        var disk = new DiskReconciler(new FixedClock());

        foreach (var (phase, imageConverges, diskConverges) in new[] {
                     ("", true, true),
                     ("Pending", false, false),
                     ("ImportInProgress", false, false),
                     (Cdi.WaitForFirstConsumer, false, true),
                     (Cdi.PendingPopulation, false, true),
                     (Cdi.Succeeded, true, true)
                 }) {
            (await PassWithPhase(image, Compute.Image("ubuntu"), Images.Body(Compute.ClusterId), Images.DataVolumeRef, phase)).IsConverged
                .ShouldBe(imageConverges, $"image at phase '{phase}'");

            (await PassWithPhase(disk, Compute.Disk("data"), Disks.Body(Compute.ClusterId), Disks.DataVolumeRef, phase)).IsConverged
                .ShouldBe(diskConverges, $"disk at phase '{phase}'");
        }

        var failed = await PassWithPhase(image, Compute.Image("ubuntu"), Images.Body(Compute.ClusterId), Images.DataVolumeRef, Cdi.Failed);
        failed.Kind.ShouldBe(ReconcileOutcomeKind.Failed);
        failed.Retryable.ShouldBeFalse("a failed import is terminal: the source is immutable, so a retry would ask the same registry for the same bytes forever");
    }

    [Fact]
    public async Task CdisOwnReasonReachesTheInProgressMessage() {
        var reconciler = new DiskReconciler(new FixedClock());
        var connection = new RecordingConnection();
        var address = Compute.Disk("data");
        using var body = JsonDocument.Parse(Disks.Body(Compute.ClusterId));

        await reconciler.ReconcileAsync(Compute.Context(connection, address, body.RootElement), TestContext.Current.CancellationToken);

        Compute.Report(
            connection,
            Disks.DataVolumeRef(ReconcileDriver.NamespaceFor(address), "data"),
            new JsonObject {
                ["phase"] = "Pending",
                ["conditions"] = new JsonArray(
                    new JsonObject { ["type"] = "Bound", ["status"] = "False", ["message"] = "no storage class found" }
                )
            }
        );

        var outcome = await reconciler.ReconcileAsync(Compute.Context(connection, address, body.RootElement), TestContext.Current.CancellationToken);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.InProgress);
        outcome.Reason.ShouldContain("no storage class found", Case.Sensitive, "CDI's sentence is the one the tenant needs, not the phase alone");
    }

    [Fact]
    public async Task AnImageBindsImmediatelyAndADiskDoesNot() {
        // ⚠ THE ONE ANNOTATION THAT MAKES AN IMAGE IMPORTABLE ON A WaitForFirstConsumer CLASS, and
        // that would pin a disk to whichever node CDI's helper picked — Cdi.ImmediateBindAnnotation.
        var imageConnection = new RecordingConnection();
        using var imageBody = JsonDocument.Parse(Images.Body(Compute.ClusterId));
        await new ImageReconciler(new FixedClock()).ReconcileAsync(Compute.Context(imageConnection, Compute.Image("ubuntu"), imageBody.RootElement), TestContext.Current.CancellationToken);

        Annotations(imageConnection.Applied.Single().Body)[Cdi.ImmediateBindAnnotation]!.GetValue<string>().ShouldBe("true");

        var diskConnection = new RecordingConnection();
        using var diskBody = JsonDocument.Parse(Disks.Body(Compute.ClusterId));
        await new DiskReconciler(new FixedClock()).ReconcileAsync(Compute.Context(diskConnection, Compute.Disk("data"), diskBody.RootElement), TestContext.Current.CancellationToken);

        Annotations(diskConnection.Applied.Single().Body).ContainsKey(Cdi.ImmediateBindAnnotation).ShouldBeFalse();
        Compute.Spec(diskConnection.Applied.Single().Body)["source"]!["blank"].ShouldNotBeNull();

        // ⚠ The access mode is WRITTEN, on both. CDI 1.66 has no StorageProfile entry for the bundle's
        // provisioner and refuses a claim it cannot fill one for — the first real run's finding, and
        // the line that turned it green. Cdi.Storage carries the reading.
        foreach (var body in new[] { imageConnection.Applied.Single().Body, diskConnection.Applied.Single().Body }) {
            Compute.Spec(body)["storage"]!["accessModes"]!.AsArray().Select(x => x!.GetValue<string>()).ShouldBe([Cdi.ReadWriteOnce]);
        }
    }

    [Fact]
    public async Task ACatalogueImageRendersItsDigestAndAUrlImageItsScheme() {
        using var catalogue = JsonDocument.Parse(Images.Body(Compute.ClusterId, name: "debian-13"));
        var rendered = Images.DataVolumeJson("debian", catalogue.RootElement);

        Compute.Spec(rendered)["source"]!["registry"]!["url"]!.GetValue<string>()
            .ShouldBe(Images.Catalogue["debian-13"].Url);
        Images.Catalogue["debian-13"].Url.ShouldContain("@sha256:", Case.Sensitive, "the catalogue pins by digest, never by tag");

        using var http = JsonDocument.Parse(Images.Body(Compute.ClusterId, kind: Images.UrlSource, url: "https://cloud-images.ubuntu.com/noble/current/noble-server-cloudimg-amd64.img"));
        Compute.Spec(Images.DataVolumeJson("noble", http.RootElement))["source"]!["http"]!["url"]!.GetValue<string>().ShouldStartWith("https://");

        using var registry = JsonDocument.Parse(Images.Body(Compute.ClusterId, kind: Images.UrlSource, url: "docker://quay.io/kubevirt/cirros-container-disk-demo:v1.9.0"));
        Compute.Spec(Images.DataVolumeJson("cirros", registry.RootElement))["source"]!["registry"].ShouldNotBeNull();

        // A url body with no address is refused before anything is applied.
        var connection = new RecordingConnection();
        using var empty = JsonDocument.Parse(Images.Body(Compute.ClusterId, kind: Images.UrlSource));
        var outcome = await new ImageReconciler(new FixedClock()).ReconcileAsync(Compute.Context(connection, Compute.Image("x"), empty.RootElement), TestContext.Current.CancellationToken);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.Failed);
        outcome.Error!.Message.ShouldContain("source.url");
        connection.Applied.ShouldBeEmpty();
    }

    [Fact]
    public void MatchesIsContainmentOverTheFieldsATenantChose() {
        using var body = JsonDocument.Parse(Disks.Body(Compute.ClusterId, size: "64Gi", storageClass: "openebs-hostpath"));
        var rendered = JsonNode.Parse(Disks.DataVolumeJson("data", body.RootElement))!.AsObject();

        // CDI's mutating path fills what the StorageProfile knows; that is not drift.
        rendered["spec"]!["storage"]!["accessModes"] = new JsonArray("ReadWriteOnce");
        rendered["spec"]!["storage"]!["volumeMode"] = "Filesystem";
        Disks.Matches(rendered.ToJsonString(), body.RootElement).ShouldBeTrue();

        rendered["spec"]!["storage"]!["resources"]!["requests"]!["storage"] = "32Gi";
        Disks.Matches(rendered.ToJsonString(), body.RootElement).ShouldBeFalse("a rewritten size is drift");

        Disks.Matches("{\"kind\":\"PersistentVolumeClaim\",\"spec\":{}}", body.RootElement).ShouldBeFalse();
        Disks.Matches("not json", body.RootElement).ShouldBeFalse();
    }

    [Fact]
    public async Task DeleteRemovesTheDataVolumeAndIsConvergedOnceItIsGone() {
        var reconciler = new ImageReconciler(new FixedClock());
        var connection = new RecordingConnection();
        var address = Compute.Image("ubuntu");
        using var body = JsonDocument.Parse(Images.Body(Compute.ClusterId));

        await reconciler.ReconcileAsync(Compute.Context(connection, address, body.RootElement), TestContext.Current.CancellationToken);
        var outcome = await reconciler.DeleteAsync(Compute.Context(connection, address, body.RootElement), TestContext.Current.CancellationToken);

        outcome.ShouldBe(ReconcileOutcome.Converged);
        connection.Deleted.Select(x => x.Kind.Kind).ShouldBe(["DataVolume"]);
        connection.Objects.ShouldBeEmpty();
    }

    [Fact]
    public async Task ObserveCarriesCdisPhase() {
        var reconciler = new DiskReconciler(new FixedClock());
        var connection = new RecordingConnection();
        var address = Compute.Disk("data");
        using var body = JsonDocument.Parse(Disks.Body(Compute.ClusterId));

        await reconciler.ReconcileAsync(Compute.Context(connection, address, body.RootElement), TestContext.Current.CancellationToken);
        Compute.Report(connection, Disks.DataVolumeRef(ReconcileDriver.NamespaceFor(address), "data"), new JsonObject { ["phase"] = Cdi.WaitForFirstConsumer });

        var observed = await reconciler.ObserveAsync(Compute.Observe(connection, address, body.RootElement), TestContext.Current.CancellationToken);

        observed.Exists.ShouldBeTrue();
        observed.Summary.ShouldContain("CDI reports WaitForFirstConsumer");
    }

    // ── Harness ─────────────────────────────────────────────────────────────────────────────────

    static async Task<ReconcileOutcome> PassWithPhase(
        IResourceReconciler reconciler,
        ResourceId address,
        string body,
        Func<string, string, ObjectRef> target,
        string phase
    ) {
        var connection = new RecordingConnection();
        using var desired = JsonDocument.Parse(body);

        await reconciler.ReconcileAsync(Compute.Context(connection, address, desired.RootElement), TestContext.Current.CancellationToken);

        if (phase.Length > 0) {
            Compute.Report(connection, target(ReconcileDriver.NamespaceFor(address), address.Name), new JsonObject { ["phase"] = phase });
        }

        return await reconciler.ReconcileAsync(Compute.Context(connection, address, desired.RootElement), TestContext.Current.CancellationToken);
    }

    static JsonObject Annotations(string objectJson) =>
        JsonNode.Parse(objectJson)!["metadata"]!["annotations"]?.AsObject() ?? [];
}
