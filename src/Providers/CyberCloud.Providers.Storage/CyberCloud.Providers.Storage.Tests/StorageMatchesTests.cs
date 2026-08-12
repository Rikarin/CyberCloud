using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Storage.Tests;

/// <summary>
///     Failure class (c): <c>Matches</c> must be containment, not equality.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Checked against the CRD and the webhook rather than against the README, and the two
///         answers are different.</b> <c>api/v1/seaweed_types.go</c> carries
///         <c>+kubebuilder:default</c> on eight fields — <c>s3.replicas=1</c>, <c>s3.iam=true</c>,
///         <c>filer.iam=true</c>, <c>volume.kind=StatefulSet</c>, <c>persistence.enabled=false</c>,
///         <c>persistence.mountPath="/data"</c>, <c>persistence.subPath=""</c>,
///         <c>worker.jobType="all"</c> — so the API server's structural defaulting adds fields to a
///         <c>Seaweed</c> on write. That is the ordinary reason and it is true here, where
///         <c>charts/managed/kafka</c>'s equivalent check found the opposite about Strimzi.
///     </para>
///     <para>
///         ⚠ <b>The second reason has no precedent in the tree: the operator installs a MUTATING
///         webhook whose defaulter is a scaffold.</b> <c>api/v1/seaweed_webhook.go</c> registers
///         <c>/mutate-seaweed-seaweedfs-com-v1-seaweed</c> on <c>create;update</c>, and the body of
///         <c>SeaweedCustomDefaulter.Default</c> is a log line and a <c>TODO</c>. It mutates nothing
///         today — so an equality comparison would pass against this release and break silently
///         against whichever one fills the TODO in, with the symptom being every account stuck in
///         <c>InProgress</c> while its cluster is perfectly correct. <c>spotahome</c>'s validator
///         mutates <i>now</i>, which is easier to find; a webhook that will mutate later is the one an
///         author who opened the file and saw an empty function would have talked themselves out of.
///     </para>
/// </remarks>
public sealed class StorageMatchesTests {
    static readonly Guid ClusterId = Guid.Parse("eeeeeeee-0000-4000-8000-000000000005");

    [Fact]
    public void ASeaweedCarryingEveryFieldTheCrdDefaultsStillMatches() {
        using var desired = JsonDocument.Parse(StorageAccounts.Body(ClusterId));

        var read = JsonNode.Parse(StorageAccounts.SeaweedJson("assets", desired.RootElement))!.AsObject();
        read["apiVersion"] = "seaweed.seaweedfs.com/v1";
        read["kind"] = "Seaweed";

        var spec = read["spec"]!.AsObject();

        // Exactly what the CRD's own `+kubebuilder:default` markers add and this provider never sent.
        spec["s3"]!["iam"] = true;
        spec["filer"]!["iam"] = true;
        spec["filer"]!["persistence"]!["subPath"] = "";
        spec["volume"]!["kind"] = "StatefulSet";

        // And what the API server adds to every object.
        read["metadata"]!.AsObject()["generation"] = 1;
        read["metadata"]!.AsObject()["managedFields"] = new JsonArray {
            new JsonObject { ["manager"] = "cybercloud/cybercloud.storage" }
        };

        // And what the operator writes back into `.status`, which is a subresource this provider must
        // never compare against.
        read["status"] = new JsonObject {
            ["observedGeneration"] = 1,
            ["master"] = new JsonObject { ["replicas"] = 3, ["readyReplicas"] = 0 },
            ["s3"] = new JsonObject { ["replicas"] = 2, ["readyReplicas"] = 0 }
        };

        StorageAccounts.Matches(read.ToJsonString(), desired.RootElement).ShouldBeTrue(
            "a Seaweed read back with the fields its own CRD defaults was reported as drifted. That "
            + "account would never leave InProgress while being perfectly correct."
        );
    }

    [Fact]
    public void ASeaweedTheMutatingWebhookHasEditedStillMatches() {
        // ⚠ THE SCAFFOLD FILLED IN. SeaweedCustomDefaulter.Default is a TODO at this commit, so this
        // is the only test in the tree asserting against a mutation that has not happened yet — which
        // is the point: an equality comparison passes today and fails on an operator upgrade, in
        // production, as "every account is stuck in InProgress".
        using var desired = JsonDocument.Parse(StorageAccounts.Body(ClusterId));

        var read = JsonNode.Parse(StorageAccounts.SeaweedJson("assets", desired.RootElement))!.AsObject();
        read["kind"] = "Seaweed";

        var spec = read["spec"]!.AsObject();
        spec["schedulerName"] = "default-scheduler";
        spec["imagePullPolicy"] = "IfNotPresent";
        spec["volumeServerDiskCount"] = 1;
        spec["master"]!["concurrentStart"] = false;

        StorageAccounts.Matches(read.ToJsonString(), desired.RootElement).ShouldBeTrue();
    }

    [Fact]
    public void DriftOnAFieldThisProviderOWNSIsStillReported() {
        // ⚠ THE OTHER HALF, AND THE ONE A CONTAINMENT CHECK IS EASY TO GET WRONG. Containment that
        // tolerated everything would be `return true`, which passes every test above and converges an
        // account whose cluster never received the update.
        using var desired = JsonDocument.Parse(StorageAccounts.Body(ClusterId, volumeServers: 6));

        var read = JsonNode.Parse(StorageAccounts.SeaweedJson("assets", desired.RootElement))!.AsObject();
        read["kind"] = "Seaweed";
        read["spec"]!["volume"]!["replicas"] = 3;

        StorageAccounts.Matches(read.ToJsonString(), desired.RootElement).ShouldBeFalse(
            "a Seaweed scaled away from the declared volume-server count read back as matching."
        );
    }

    [Fact]
    public void AShrunkenDataVolumeIsReportedRatherThanAccepted() {
        using var desired = JsonDocument.Parse(StorageAccounts.Body(ClusterId, storageSize: "500Gi"));

        var read = JsonNode.Parse(StorageAccounts.SeaweedJson("assets", desired.RootElement))!.AsObject();
        read["kind"] = "Seaweed";
        read["spec"]!["volume"]!["requests"]!["storage"] = "100Gi";

        StorageAccounts.Matches(read.ToJsonString(), desired.RootElement).ShouldBeFalse();
    }

    [Fact]
    public void ASeaweedWhoseIdentitiesReferenceWasStrippedIsReportedAsDrifted() {
        // ⚠ THE COMPARISON THAT EXISTS BECAUSE ITS ABSENCE WOULD MAKE THE ACCOUNT WORK. A Seaweed
        // whose spec.s3.configSecret was removed by an admission policy, a merge or a `kubectl edit`
        // comes up, passes its probes, and serves every anonymous request as an administrator —
        // weed/s3api/auth_credentials.go returns an ACTION_ADMIN identity when no identities are
        // configured. Every other signal about that resource is green.
        using var desired = JsonDocument.Parse(StorageAccounts.Body(ClusterId));

        var read = JsonNode.Parse(StorageAccounts.SeaweedJson("assets", desired.RootElement))!.AsObject();
        read["kind"] = "Seaweed";
        read["spec"]!["s3"]!.AsObject().Remove("configSecret");

        StorageAccounts.Matches(read.ToJsonString(), desired.RootElement).ShouldBeFalse(
            "a gateway with no identities file read back as matching. That is a publicly-spoken S3 "
            + "endpoint on which every anonymous caller is an administrator, and it is the one drift "
            + "that makes the resource look healthier rather than worse."
        );
    }

    [Fact]
    public void AReplicationCodeThatMovedIsReported() {
        // The account's durability promise lives on spec.master.defaultReplication and nowhere else.
        // An operator upgrade, a merge or a hand edit that reset it to "000" leaves a cluster that is
        // running, healthy, and keeping one copy of everything.
        using var desired = JsonDocument.Parse(StorageAccounts.Body(ClusterId));

        var read = JsonNode.Parse(StorageAccounts.SeaweedJson("assets", desired.RootElement))!.AsObject();
        read["kind"] = "Seaweed";
        read["spec"]!["master"]!["defaultReplication"] = "000";

        StorageAccounts.Matches(read.ToJsonString(), desired.RootElement).ShouldBeFalse();
    }

    [Fact]
    public void GarbageIsFalseRatherThanAnException() {
        using var desired = JsonDocument.Parse(StorageAccounts.Body(ClusterId));

        // A reconciler that threw here would fail the resource on a malformed read rather than
        // reporting it as not-yet-converged and coming back.
        StorageAccounts.Matches("{not json", desired.RootElement).ShouldBeFalse();
        StorageAccounts.Matches("[]", desired.RootElement).ShouldBeFalse();
        StorageAccounts.Matches("{}", desired.RootElement).ShouldBeFalse();
        StorageAccounts.Matches("{\"kind\":\"ConfigMap\",\"spec\":{}}", desired.RootElement).ShouldBeFalse();
    }
}
