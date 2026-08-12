using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Messaging.Tests;

/// <summary>
///     Failure class (c): <see cref="RabbitmqClusters.Matches" /> is containment rather than
///     equality, checked against what the operator and the API server actually add.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THE ORDINARY REASON IS THE TRUE ONE HERE, WHICH IS THE OPPOSITE OF WHAT THE KAFKA ROW
///         FOUND — AND THAT IS WHY THE CRD WAS READ RATHER THAN THE README.</b>
///         <c>KafkaClusters.Matches</c> records that Strimzi's <c>Kafka</c> at <c>v1beta2</c> declares
///         <b>no <c>default:</c> anywhere</b>, so "the API server defaults fields on write" was false
///         for it and containment was kept for other reasons. <c>rabbitmq.com_rabbitmqclusters.yaml</c>
///         at <c>v1beta1</c> declares defaults on six spec fields, two of them at OBJECT level, and
///         the operator ships a mutating admission webhook that writes <c>spec.image</c> on top. Every
///         document below is built by taking what this provider renders and adding exactly what the
///         real cluster would.
///     </para>
///     <para>
///         ⚠ <b>Each case is written as a SABOTAGE of the desired body rather than of the read-back
///         wherever it can be</b>, because a test that only ever perturbs the object it also built
///         proves the comparison is not <c>true</c> and nothing else.
///     </para>
/// </remarks>
public sealed class RabbitmqMatchesTests {
    static readonly Guid ClusterId = Guid.Parse("eeeeeeee-0000-4000-8000-000000000009");

    [Fact]
    public void ADocumentCarryingEveryDefaultTheApiServerAddsStillMatches() {
        // ⚠ THE WHOLE POINT OF CONTAINMENT, WITH THE REAL LIST. Six CRD defaults, two of them
        // object-level, plus a webhook-written field, plus the three annotations and the finalizer
        // the controller writes back, plus managedFields and a status this provider never sent.
        // Equality would report drift on the first read of every resource, forever.
        using var body = JsonDocument.Parse(RabbitmqClusters.Body(ClusterId));

        var read = AsClusterWouldReturn(RabbitmqClusters.ClusterJson("events", body.RootElement));

        RabbitmqClusters.Matches(read, body.RootElement).ShouldBeTrue(
            "a read-back carrying only the API server's own defaults and the operator's own writes "
            + "was reported as drift, so every resource of this type would sit in InProgress forever."
        );
    }

    [Fact]
    public void TheObjectLevelDefaultsAreTheOnesThatWouldBiteAndTheyDoNot() {
        // ⚠ TWO OF THE CRD's DEFAULTS ARE ON OBJECTS RATHER THAN LEAVES — `spec.persistence` defaults
        // to {"storage": "10Gi"} and `spec.service` to {"type": "ClusterIP"}, whole. So "we did not
        // ask for a volume" and "we asked for 10Gi" are the same request, and a `spec.service` block
        // appears that this provider deliberately never wrote. A comparison that walked the object
        // graph rather than reading the four fields it owns would see both as additions.
        using var body = JsonDocument.Parse(RabbitmqClusters.Body(ClusterId, storageSize: "20Gi"));

        var document = JsonNode.Parse(RabbitmqClusters.ClusterJson("events", body.RootElement))!.AsObject();
        document["kind"] = "RabbitmqCluster";

        // The API server materialises this whole block; the provider never wrote it.
        document["spec"]!["service"] = new JsonObject { ["type"] = "ClusterIP" };
        document["spec"]!["terminationGracePeriodSeconds"] = 604800;
        document["spec"]!["delayStartSeconds"] = 30;

        RabbitmqClusters.Matches(document.ToJsonString(), body.RootElement).ShouldBeTrue(
            "an object carrying the CRD's own object-level defaults was reported as drift."
        );
    }

    [Fact]
    public void AChangedNodeCountIsDrift() {
        using var desired = JsonDocument.Parse(RabbitmqClusters.Body(ClusterId, nodes: 5));
        using var other = JsonDocument.Parse(RabbitmqClusters.Body(ClusterId, nodes: 3));

        var read = AsClusterWouldReturn(RabbitmqClusters.ClusterJson("events", other.RootElement));

        RabbitmqClusters.Matches(read, desired.RootElement).ShouldBeFalse(
            "a cluster running three nodes read back as carrying a desired five."
        );
    }

    [Fact]
    public void AChangedVersionIsDriftThroughTheImage() {
        // ⚠ THE VERSION IS ONLY VISIBLE THROUGH `spec.image`, and that is why this provider writes the
        // field the operator's webhook would otherwise fill. If the field were left out, a read-back
        // would carry whatever image the operator was compiled against and this comparison would have
        // nothing to compare — the version property would control nothing and report nothing.
        using var desired = JsonDocument.Parse(Versioned(RabbitmqClusters.Body(ClusterId), "4.1"));
        using var other = JsonDocument.Parse(Versioned(RabbitmqClusters.Body(ClusterId), "4.0"));

        var read = AsClusterWouldReturn(RabbitmqClusters.ClusterJson("events", other.RootElement));

        RabbitmqClusters.Matches(read, desired.RootElement).ShouldBeFalse(
            "a cluster running rabbitmq:4.0-management read back as carrying a desired 4.1."
        );
    }

    [Fact]
    public void AChangedStorageSizeIsDrift() {
        using var desired = JsonDocument.Parse(RabbitmqClusters.Body(ClusterId, storageSize: "80Gi"));
        using var other = JsonDocument.Parse(RabbitmqClusters.Body(ClusterId, storageSize: "20Gi"));

        var read = AsClusterWouldReturn(RabbitmqClusters.ClusterJson("events", other.RootElement));

        RabbitmqClusters.Matches(read, desired.RootElement).ShouldBeFalse();
    }

    [Fact]
    public void AChangedDefaultQueueTypeIsDrift() {
        // ⚠ THE ONE THAT MATTERS, AND THE HARDEST TO SEE. `default_queue_type` is a line inside a
        // free-text string, so a cluster still running `classic` and a body asking for `quorum` differ
        // by eleven characters buried in one spec property — and if this comparison missed it, the
        // resource would report Succeeded while replicating nothing.
        using var desired = JsonDocument.Parse(
            RabbitmqClusters.Body(ClusterId, defaultQueueType: "quorum")
        );

        using var other = JsonDocument.Parse(
            RabbitmqClusters.Body(ClusterId, defaultQueueType: "classic")
        );

        var read = AsClusterWouldReturn(RabbitmqClusters.ClusterJson("events", other.RootElement));

        RabbitmqClusters.Matches(read, desired.RootElement).ShouldBeFalse(
            "a cluster whose default queue type is `classic` — unreplicated on 4.x — read back as "
            + "carrying a desired `quorum`."
        );
    }

    [Fact]
    public void AConfigThatGAINEDALineStillMatches() {
        // ⚠ THE HALF THAT MAKES THIS CONTAINMENT RATHER THAN EQUALITY ON THE CONFIG FIELD. The
        // operator does not edit this string — it files it as its own conf.d fragment — but server-
        // side apply leaves other managers' fields in place, and a future property of this type would
        // add a line. What must stay true is that the line this row exists for is IN there.
        using var body = JsonDocument.Parse(RabbitmqClusters.Body(ClusterId));

        var document = JsonNode.Parse(RabbitmqClusters.ClusterJson("events", body.RootElement))!.AsObject();
        var config = document["spec"]!["rabbitmq"]!["additionalConfig"]!.GetValue<string>();
        document["spec"]!["rabbitmq"]!["additionalConfig"] = config + "consumer_timeout = 1800000\n";

        RabbitmqClusters.Matches(document.ToJsonString(), body.RootElement).ShouldBeTrue(
            "a config block that gained a line it did not lose was reported as drift."
        );
    }

    [Fact]
    public void AConfigThatLOSTTheQueueTypeLineIsDrift() {
        // The other direction, and the one an equality comparison would also have caught — which is
        // why it is worth writing: containment must not be so loose that it stops noticing removal.
        using var body = JsonDocument.Parse(RabbitmqClusters.Body(ClusterId));

        var document = JsonNode.Parse(RabbitmqClusters.ClusterJson("events", body.RootElement))!.AsObject();
        document["spec"]!["rabbitmq"]!["additionalConfig"] = "max_message_size = 134217728\n";

        RabbitmqClusters.Matches(document.ToJsonString(), body.RootElement).ShouldBeFalse(
            "an object whose config no longer sets a default queue type at all was reported as "
            + "carrying the desired spec."
        );
    }

    [Fact]
    public void UnparseableJsonAndAMissingSpecAreDriftRatherThanExceptions() {
        // A reconciler calls this on whatever the API server returned. A throw here would surface as
        // an unhandled exception inside a reminder rather than as a resource that has not converged.
        using var body = JsonDocument.Parse(RabbitmqClusters.Body(ClusterId));

        foreach (var malformed in new[] { "{", "[]", "null", "{\"kind\":\"RabbitmqCluster\"}" }) {
            RabbitmqClusters.Matches(malformed, body.RootElement).ShouldBeFalse(malformed);
        }
    }

    /// <summary>
    ///     A rendered object as the API server and the operator would hand it back.
    /// </summary>
    /// <param name="rendered">What this provider applied.</param>
    /// <remarks>
    ///     ⚠ Every addition here is real and is named in <c>charts/managed/rabbitmq/SOURCE</c>: six
    ///     CRD <c>default:</c> values, a webhook-written <c>spec.image</c> (already rendered by this
    ///     provider, so the webhook is a no-op — the point is that it WOULD have written one), the
    ///     three controller-written annotations, the finalizer, <c>managedFields</c> and a status.
    /// </remarks>
    static string AsClusterWouldReturn(string rendered) {
        var document = JsonNode.Parse(rendered)!.AsObject();

        document["apiVersion"] = "rabbitmq.com/v1beta1";
        document["kind"] = "RabbitmqCluster";

        var metadata = document["metadata"]!.AsObject();
        metadata["namespace"] = "aaaa-prod";
        metadata["uid"] = "8f2b0d5a-0000-4000-8000-000000000001";
        metadata["resourceVersion"] = "4711";
        metadata["generation"] = 2;
        metadata["finalizers"] = new JsonArray { "deletion.finalizers.rabbitmqclusters.rabbitmq.com" };
        metadata["annotations"] = new JsonObject {
            ["rabbitmq.com/version"] = "4.1.3",
            ["rabbitmq.com/erlang-version"] = "27.2",
            ["rabbitmq.com/queueRebalanceNeededAt"] = "2026-08-12T00:00:00Z"
        };

        metadata["managedFields"] = new JsonArray {
            new JsonObject {
                ["manager"] = "cybercloud/cybercloud.messaging", ["operation"] = "Apply"
            },
            new JsonObject { ["manager"] = "rabbitmq-cluster-operator", ["operation"] = "Update" }
        };

        var spec = document["spec"]!.AsObject();

        // The CRD's own defaults, materialised on write. `replicas`, `persistence.storage` and
        // `image` are already present because this provider writes all three — which is exactly why
        // it writes them.
        spec["service"] = new JsonObject { ["type"] = "ClusterIP" };
        spec["terminationGracePeriodSeconds"] = 604800;
        spec["delayStartSeconds"] = 30;

        document["status"] = new JsonObject {
            ["observedGeneration"] = 2,
            ["conditions"] = new JsonArray {
                new JsonObject { ["type"] = "ClusterAvailable", ["status"] = "True" },
                new JsonObject { ["type"] = "ReconcileSuccess", ["status"] = "True" }
            },
            ["defaultUser"] = new JsonObject {
                ["secretReference"] = new JsonObject {
                    ["name"] = RabbitmqClusters.DefaultUserSecretName("events"),
                    ["namespace"] = "aaaa-prod",
                    ["keys"] = new JsonObject { ["username"] = "username", ["password"] = "password" }
                }
            }
        };

        return document.ToJsonString();
    }

    static string Versioned(string body, string version) {
        var node = JsonNode.Parse(body)!.AsObject();
        node["properties"]!.AsObject()["version"] = version;
        return node.ToJsonString();
    }
}
