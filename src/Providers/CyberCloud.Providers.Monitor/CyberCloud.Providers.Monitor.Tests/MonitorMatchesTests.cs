using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Monitor.Tests;

/// <summary>
///     <c>MonitorWorkspaces.Matches</c> — containment, and the mechanism that forces it here is not
///     the one any earlier family records.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Failure class (b), and the CRD is not where the answer is on this type.</b> Three
///         families argue containment from CRD defaulting, one from a mutating webhook, one from a
///         controller writing back into <c>.spec</c>. Two of this type's three objects are <b>core
///         kinds</b> — there is no CRD and no operator — so what forces containment is the <b>API
///         server itself</b>: <c>metadata.creationTimestamp</c>, <c>uid</c>, <c>resourceVersion</c>,
///         <c>managedFields</c> and the seven labels <c>KubeCommandBuilder</c> injects all come back
///         on a read that the render never wrote.
///     </para>
///     <para>
///         ⚠ <b>And unlike every earlier sighting, the conformance harness is NOT blind to this
///         one.</b> The equality mistake was measured against both halves rather than argued: the
///         cluster-backed suite fails on it because a real API server adds all five metadata fields,
///         and the Docker-free suite fails on it too because <c>KubeCommandBuilder</c> has already
///         added the seven labels by the time <c>FakeKubeCluster</c> echoes the apply back. Every
///         family whose operator's CRD carries defaults has the hole
///         <c>CyberCloud.Providers.Search</c> records; this one does not.
///     </para>
/// </remarks>
public sealed class MonitorMatchesTests {
    static readonly Guid ClusterId = Guid.Parse("eeeeeeee-0000-4000-8000-00000000000c");

    static readonly ResourceId Address = new(
        Guid.Parse("11111111-1111-4111-8111-111111111111"),
        Guid.Parse("22222222-2222-4222-8222-222222222222"),
        "prod",
        MonitorWorkspaces.Type,
        "prod",
        Guid.Parse("33333333-3333-4333-8333-333333333333")
    );

    [Fact]
    public void ARenderedRowMatchesItself() {
        using var body = JsonDocument.Parse(MonitorWorkspaces.Body(ClusterId));

        MonitorWorkspaces.Matches(
            MonitorWorkspaces.RowJson(Address, body.RootElement),
            Address,
            body.RootElement
        ).ShouldBeTrue();
    }

    [Fact]
    public void AnObjectCarryingWhatAnApiServerAddsStillMatches() {
        // ⚠ THE EQUALITY MISTAKE, RUN. Everything added below is added by a real API server or by
        // KubeCommandBuilder on the way in; none of it is written by the render. An equality
        // comparison fails here on the FIRST read-back, so the resource never converges — and the
        // operation times out with a message that says the object "does not yet carry the desired
        // spec", which is exactly wrong.
        using var body = JsonDocument.Parse(MonitorWorkspaces.Body(ClusterId));

        var document = JsonNode.Parse(MonitorWorkspaces.RowJson(Address, body.RootElement))!.AsObject();
        var metadata = document["metadata"]!.AsObject();

        metadata["uid"] = "0e6ca9d1-1f4e-4a2e-9a52-0e2a1b3c4d5e";
        metadata["resourceVersion"] = "44821";
        metadata["creationTimestamp"] = "2026-08-18T12:00:00Z";
        metadata["namespace"] = "22222222222242228222222222222222-prod";
        metadata["managedFields"] = new JsonArray {
            new JsonObject { ["manager"] = "cybercloud", ["operation"] = "Apply" }
        };

        var labels = metadata["labels"]!.AsObject();
        labels["cybercloud.io/tenant-id"] = Address.TenantId.ToString("N");
        labels["cybercloud.io/managed-by"] = "cybercloud";

        MonitorWorkspaces.Matches(document.ToJsonString(), Address, body.RootElement).ShouldBeTrue(
            "an object carrying only what an API server and the command builder add no longer "
            + "matches. Matches is CONTAINMENT: an equality comparison here never matches on the "
            + "first read-back, and the workspace never converges."
        );
    }

    [Fact]
    public void ARowWhoseRetentionDriftedDoesNotMatch() {
        // The other direction. Containment that accepted anything would report a workspace whose
        // retention somebody edited by hand as converged, which is drift detection reporting nothing.
        using var body = JsonDocument.Parse(MonitorWorkspaces.Body(ClusterId));

        var document = JsonNode.Parse(MonitorWorkspaces.RowJson(Address, body.RootElement))!.AsObject();
        document["data"]!["retentionLogsDays"] = "1";

        MonitorWorkspaces.Matches(document.ToJsonString(), Address, body.RootElement).ShouldBeFalse();
    }

    [Fact]
    public void AVmUserRoutedToAnotherAccountDoesNotMatch() {
        // ⚠ THE ONE READ-BACK THAT MUST NOT BE LENIENT. A VMUser whose suffix names a different
        // account is a workspace writing into somebody else's metrics, and it is the exact drift a
        // hand edit or a half-applied update produces.
        using var body = JsonDocument.Parse(MonitorWorkspaces.Body(ClusterId));

        var document = JsonNode.Parse(MonitorWorkspaces.VmUserJson(Address, body.RootElement))!.AsObject();
        document["spec"]!["targetRefs"]!.AsArray()[0]!["target_path_suffix"] = "/insert/1/prometheus";

        MonitorWorkspaces.Matches(document.ToJsonString(), Address, body.RootElement).ShouldBeFalse();
    }

    [Fact]
    public void AVmUserRoutedToAnotherRetentionTierDoesNotMatch() {
        // Because the tier IS the routing on the metrics half, a VMUser pointing at the wrong
        // VMCluster is a workspace billed for one retention and given another.
        using var body = JsonDocument.Parse(MonitorWorkspaces.Body(ClusterId, metricsTier: "extended"));

        var document = JsonNode.Parse(MonitorWorkspaces.VmUserJson(Address, body.RootElement))!.AsObject();
        document["spec"]!["targetRefs"]!.AsArray()[0]!["crd"]!["name"] = "telemetry-short";

        MonitorWorkspaces.Matches(document.ToJsonString(), Address, body.RootElement).ShouldBeFalse();
    }

    [Fact]
    public void AVmUserPointedAtAnotherWorkspacesSecretDoesNotMatch() {
        using var body = JsonDocument.Parse(MonitorWorkspaces.Body(ClusterId));

        var document = JsonNode.Parse(MonitorWorkspaces.VmUserJson(Address, body.RootElement))!.AsObject();
        document["spec"]!["passwordRef"]!["name"] = "monitor-somebody-else-ingest";

        MonitorWorkspaces.Matches(document.ToJsonString(), Address, body.RootElement).ShouldBeFalse();
    }

    [Fact]
    public void ASecretMatchesOnPresenceAndNotOnValue() {
        // ⚠ The key is minted once and read back from the vault on every pass, so its value is stable
        // — but comparing it here would mean this method taking a credential as an argument, and a
        // comparison is not a reason to move one. What a wrong Secret looks like from here is an
        // ABSENT field, and that is what is checked.
        using var body = JsonDocument.Parse(MonitorWorkspaces.Body(ClusterId));

        MonitorWorkspaces.Matches(
            MonitorWorkspaces.KeySecretJson("prod", "AbCdEf123456"),
            Address,
            body.RootElement
        ).ShouldBeTrue();

        var empty = JsonNode.Parse(MonitorWorkspaces.KeySecretJson("prod", "AbCdEf123456"))!.AsObject();
        empty["data"]!.AsObject().Remove("ingestKey");

        MonitorWorkspaces.Matches(empty.ToJsonString(), Address, body.RootElement).ShouldBeFalse();
    }

    [Fact]
    public void ADocumentOfAnUnknownKindDoesNotMatch() {
        // ⚠ ONE Matches OVER THREE KINDS, so a default of `true` for an unrecognised document would
        // report an object that was never applied as converged. There is no "and anything else is
        // fine" branch, and this is what says so.
        using var body = JsonDocument.Parse(MonitorWorkspaces.Body(ClusterId));

        MonitorWorkspaces.Matches(
            new JsonObject { ["kind"] = "Deployment", ["spec"] = new JsonObject() }.ToJsonString(),
            Address,
            body.RootElement
        ).ShouldBeFalse();

        // Including a document with no kind at all: all three renders write their own, so a body
        // without one has been neither rendered here nor returned by an API server.
        MonitorWorkspaces.Matches(
            new JsonObject { ["data"] = new JsonObject() }.ToJsonString(),
            Address,
            body.RootElement
        ).ShouldBeFalse();
    }

    [Fact]
    public void TheSecretIsNotJudgedAsARow() {
        // ⚠ The ConfigMap and the Secret both carry a `data` object, and both are core kinds. A
        // Matches that dispatched on shape rather than on `kind` would judge the Secret against the
        // row's expected keys and report the workspace as permanently drifted.
        using var body = JsonDocument.Parse(MonitorWorkspaces.Body(ClusterId));

        var secret = JsonNode.Parse(MonitorWorkspaces.KeySecretJson("prod", "AbCdEf123456"))!.AsObject();
        secret["kind"]!.ShouldNotBeNull();
        secret["kind"]!.GetValue<string>().ShouldBe("Secret");

        MonitorWorkspaces.Matches(secret.ToJsonString(), Address, body.RootElement).ShouldBeTrue();
    }
}
