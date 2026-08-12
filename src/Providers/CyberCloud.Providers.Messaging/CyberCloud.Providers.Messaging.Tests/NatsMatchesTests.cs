using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Messaging.Tests;

/// <summary>
///     Failure class (c): <c>Matches</c> must be containment, not equality.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The usual argument for containment is "the operator edits the spec it is given", and
///         for this type the argument that actually holds is a DIFFERENT one — which is worth pinning
///         because the two providers before this one reached opposite findings.</b>
///         <c>charts/managed/kafka/SOURCE</c> records that Strimzi's CRD declares <b>no
///         <c>default:</c> anywhere</b>, so the API server's structural defaulting adds nothing on
///         write and the premise is false there. Every object this type renders is a <b>built-in</b>
///         kind, and built-in kinds are the most heavily defaulted objects in Kubernetes. So the
///         premise is not merely true here, it is true on every single object.
///     </para>
///     <para>
///         The tests below feed the round trip a real API server performs — every field this provider
///         never sent — and assert the comparison survives it. An equality comparison would report
///         drift on the first read of every resource, forever, and the symptom is a resource that
///         never leaves <c>InProgress</c> while the cluster is perfectly correct.
///     </para>
/// </remarks>
public sealed class NatsMatchesTests {
    static readonly Guid ClusterId = Guid.Parse("eeeeeeee-0000-4000-8000-000000000005");

    [Fact]
    public void AStatefulSetCarryingEveryFieldTheApiServerAddsStillMatches() {
        using var desired = JsonDocument.Parse(NatsClusters.Body(ClusterId));

        var read = JsonNode.Parse(NatsClusters.StatefulSetJson("events", desired.RootElement))!.AsObject();
        read["kind"] = "StatefulSet";

        // Exactly what a real API server hands back and this provider never sent.
        var spec = read["spec"]!.AsObject();
        spec["revisionHistoryLimit"] = 10;
        spec["updateStrategy"] = new JsonObject {
            ["type"] = "RollingUpdate",
            ["rollingUpdate"] = new JsonObject { ["partition"] = 0 }
        };
        spec["persistentVolumeClaimRetentionPolicy"] = new JsonObject {
            ["whenDeleted"] = "Retain", ["whenScaled"] = "Retain"
        };

        var podSpec = spec["template"]!["spec"]!.AsObject();
        podSpec["dnsPolicy"] = "ClusterFirst";
        podSpec["restartPolicy"] = "Always";
        podSpec["schedulerName"] = "default-scheduler";
        podSpec["securityContext"] = new JsonObject();

        var container = podSpec["containers"]!.AsArray()[0]!.AsObject();
        container["imagePullPolicy"] = "IfNotPresent";
        container["terminationMessagePath"] = "/dev/termination-log";
        container["terminationMessagePolicy"] = "File";

        read["status"] = new JsonObject { ["replicas"] = 0, ["observedGeneration"] = 1 };
        read["metadata"]!.AsObject()["managedFields"] = new JsonArray {
            new JsonObject { ["manager"] = "cybercloud/cybercloud.messaging" }
        };

        NatsClusters.Matches(read.ToJsonString(), desired.RootElement).ShouldBeTrue(
            "a StatefulSet read back with the fields the API server defaults was reported as drifted. "
            + "That resource would never leave InProgress while being perfectly correct."
        );
    }

    [Fact]
    public void AServiceCarryingTheClusterIpTheApiServerAllocatedStillMatches() {
        using var desired = JsonDocument.Parse(NatsClusters.Body(ClusterId));

        var read = JsonNode.Parse(NatsClusters.ClientServiceJson("events", desired.RootElement))!.AsObject();
        read["kind"] = "Service";

        var spec = read["spec"]!.AsObject();
        spec["clusterIP"] = "10.43.12.7";
        spec["clusterIPs"] = new JsonArray { "10.43.12.7" };
        spec["ipFamilies"] = new JsonArray { "IPv4" };
        spec["ipFamilyPolicy"] = "SingleStack";
        spec["sessionAffinity"] = "None";
        spec["internalTrafficPolicy"] = "Cluster";

        NatsClusters.Matches(read.ToJsonString(), desired.RootElement).ShouldBeTrue();
    }

    [Fact]
    public void DriftOnAFieldThisProviderOWNSIsStillReported() {
        // ⚠ THE OTHER HALF, AND THE ONE A CONTAINMENT CHECK IS EASY TO GET WRONG. Containment that
        // tolerated everything would be `return true`, which passes every test above and converges a
        // resource whose cluster never received the update.
        using var desired = JsonDocument.Parse(NatsClusters.Body(ClusterId, servers: 5));

        var read = JsonNode.Parse(NatsClusters.StatefulSetJson("events", desired.RootElement))!.AsObject();
        read["kind"] = "StatefulSet";

        // What `kubectl scale` or an autoscaler writes through the scale subresource.
        read["spec"]!["replicas"] = 3;

        NatsClusters.Matches(read.ToJsonString(), desired.RootElement).ShouldBeFalse(
            "a StatefulSet scaled away from the declared server count read back as matching."
        );
    }

    [Fact]
    public void AShrunkenVolumeClaimIsReportedRatherThanAccepted() {
        using var desired = JsonDocument.Parse(NatsClusters.Body(ClusterId, storageSize: "50Gi"));

        var read = JsonNode.Parse(NatsClusters.StatefulSetJson("events", desired.RootElement))!.AsObject();
        read["kind"] = "StatefulSet";
        read["spec"]!["volumeClaimTemplates"]![0]!["spec"]!["resources"]!["requests"]!["storage"] = "10Gi";

        NatsClusters.Matches(read.ToJsonString(), desired.RootElement).ShouldBeFalse();
    }

    [Fact]
    public void AConfigMapWhoseRouteListDoesNotFitTheServerCountIsReported() {
        // ⚠ THE ONE COMPARISON THAT LOOKS INSIDE A STRING, AND THE MOST IMPORTANT ONE ON THIS TYPE.
        // A StatefulSet scaled from 3 to 5 whose ConfigMap still lists three routes gives a cluster
        // where two servers are unreachable to the other three — and every object reads back as
        // healthy. Nothing about the objects' shapes catches it, because the route list is a line in
        // a text file.
        using var three = JsonDocument.Parse(NatsClusters.Body(ClusterId, servers: 3));
        using var five = JsonDocument.Parse(NatsClusters.Body(ClusterId, servers: 5));

        var read = JsonNode.Parse(NatsClusters.ConfigMapJson("events", three.RootElement))!.AsObject();
        read["kind"] = "ConfigMap";

        NatsClusters.Matches(read.ToJsonString(), three.RootElement).ShouldBeTrue();
        NatsClusters.Matches(read.ToJsonString(), five.RootElement).ShouldBeFalse(
            "a three-route configuration read back as satisfying a five-server body."
        );
    }

    [Fact]
    public void GarbageIsFalseRatherThanAnException() {
        using var desired = JsonDocument.Parse(NatsClusters.Body(ClusterId));

        // A reconciler that threw here would fail the resource on a malformed read rather than
        // reporting it as not-yet-converged and coming back.
        NatsClusters.Matches("{not json", desired.RootElement).ShouldBeFalse();
        NatsClusters.Matches("[]", desired.RootElement).ShouldBeFalse();
        NatsClusters.Matches("{}", desired.RootElement).ShouldBeFalse();
    }
}
