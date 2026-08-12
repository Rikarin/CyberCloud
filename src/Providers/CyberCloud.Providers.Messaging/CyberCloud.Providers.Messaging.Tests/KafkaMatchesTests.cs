using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Messaging.Tests;

/// <summary>
///     <c>Matches</c> is a containment test, and these are the reasons — each written as something
///     an equality test would fail.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The usual argument for containment does not apply to this operator, and the tests
///         below are the ones that survive checking.</b> The received wisdom is "the operator edits
///         the spec it is given, so equality reports drift forever" — true of some operators.
///         Strimzi's <c>Kafka</c> CRD at v1beta2 declares <b>no <c>default:</c> anywhere</b>, so the
///         API server's structural defaulting adds nothing on write, and the cluster operator writes
///         <c>.status</c>, which is a subresource. Believing the wrong reason would be fine until
///         somebody "simplified" the comparison on the strength of it.
///     </para>
///     <para>
///         What is true, and what each test below pins: the read-back document carries
///         <c>metadata</c>, <c>managedFields</c> and an operator-written <c>status</c> this provider
///         never sent; server-side apply leaves other managers' fields in place; and
///         <c>KafkaNodePool</c> declares a <c>scale</c> subresource over <c>.spec.replicas</c>, so a
///         third party can write a field <i>inside</i> spec.
///     </para>
/// </remarks>
public sealed class KafkaMatchesTests {
    [Fact]
    public void TheRenderedDocumentMatchesItself() {
        // The floor. If this is false nothing else in the file means anything.
        using var desired = JsonDocument.Parse(KafkaClusters.Body(ClusterId));

        KafkaClusters.Matches(Kafka(desired.RootElement), desired.RootElement).ShouldBeTrue();
        KafkaClusters.Matches(NodePool(desired.RootElement), desired.RootElement).ShouldBeTrue();
    }

    [Fact]
    public void AnOperatorWrittenStatusIsNotDrift() {
        // ⚠ THE SABOTAGE. `AsSubmitted` is what equality would compare against, and it is not what
        // an API server returns: the operator writes a large `status` through the status subresource,
        // and the server adds `metadata.managedFields` and `metadata.uid`. An equality-based Matches
        // reports drift on every pass for the life of the resource, and the symptom is a resource
        // that never leaves Updating while being completely healthy.
        using var desired = JsonDocument.Parse(KafkaClusters.Body(ClusterId));

        var readBack = JsonNode.Parse(Kafka(desired.RootElement))!.AsObject();
        readBack["status"] = new JsonObject {
            ["clusterId"] = "Yjg0OTQ0",
            ["kafkaMetadataState"] = "KRaft",
            ["conditions"] = new JsonArray {
                new JsonObject { ["type"] = "Ready", ["status"] = "True" }
            },
            ["listeners"] = new JsonArray {
                new JsonObject {
                    ["name"] = "internal",
                    ["bootstrapServers"] = "events-kafka-bootstrap.ns.svc:9092"
                }
            }
        };
        readBack["metadata"]!.AsObject()["uid"] = "0c1a4a2e-0000-4000-8000-00000000000f";
        readBack["metadata"]!.AsObject()["managedFields"] = new JsonArray {
            new JsonObject { ["manager"] = KafkaClusters.FieldManager, ["operation"] = "Apply" }
        };

        KafkaClusters.Matches(readBack.ToJsonString(), desired.RootElement).ShouldBeTrue(
            "an operator-written status made the resource look drifted. Matches must read the fields "
            + "this provider owns and ignore everything else."
        );

        // And the sabotage's own control: equality against the submitted document is false here, so
        // the test above is not passing by accident.
        Equal(readBack.ToJsonString(), Kafka(desired.RootElement)).ShouldBeFalse(
            "the read-back and the submitted document are byte-identical, so this test proves nothing."
        );
    }

    [Fact]
    public void AnotherFieldManagersAdditionInsideSpecIsNotDrift() {
        // ⚠ Server-side apply leaves other managers' fields in place. A tenant's own controller
        // setting `spec.kafka.template` — a field this provider never writes and has no opinion about
        // — is co-ownership working as ADR-013 intends, not drift. Equality would call it drift and
        // the next pass would try to remove it, which is the silent revert ADR-013 exists to replace.
        using var desired = JsonDocument.Parse(KafkaClusters.Body(ClusterId));

        var readBack = JsonNode.Parse(Kafka(desired.RootElement))!.AsObject();
        readBack["spec"]!["kafka"]!.AsObject()["template"] = new JsonObject {
            ["pod"] = new JsonObject { ["priorityClassName"] = "tenant-critical" }
        };

        KafkaClusters.Matches(readBack.ToJsonString(), desired.RootElement).ShouldBeTrue();
    }

    [Fact]
    public void AThirdPartyScalingTheNodePoolIsDrift() {
        // ⚠ The other direction, and the one containment must NOT swallow. `KafkaNodePool` declares a
        // `scale` subresource over `.spec.replicas`, so `kubectl scale` and any autoscaler can write
        // a field INSIDE spec that this provider owns. That is real drift — the resource no longer
        // has the node count its body asks for — and a comparison loose enough to miss it would make
        // the drift scan blind to the one field a third party can most easily move.
        using var desired = JsonDocument.Parse(KafkaClusters.Body(ClusterId, nodes: 3));

        var scaled = JsonNode.Parse(NodePool(desired.RootElement))!.AsObject();
        scaled["spec"]!.AsObject()["replicas"] = 7;

        KafkaClusters.Matches(scaled.ToJsonString(), desired.RootElement).ShouldBeFalse(
            "a node pool scaled behind the platform's back reads as converged."
        );
    }

    [Fact]
    public void TurningExternalExposureOnIsVisibleToMatches() {
        // The update path's ground truth. A body change the world can see must be one Matches can
        // see, or the conformance suite's update test passes while proving the change never left the
        // grain.
        using var plain = JsonDocument.Parse(KafkaClusters.Body(ClusterId));
        using var exposed = JsonDocument.Parse(WithExternalEnabled(KafkaClusters.Body(ClusterId)));

        KafkaClusters.Matches(Kafka(plain.RootElement), exposed.RootElement).ShouldBeFalse(
            "a cluster with one listener satisfies a body asking for external exposure."
        );

        KafkaClusters.Matches(Kafka(exposed.RootElement), exposed.RootElement).ShouldBeTrue();
    }

    [Fact]
    public void AMalformedOrEmptyDocumentIsNotAMatch() {
        // ⚠ `false`, not an exception. A reconciler asks this about whatever the API server returned,
        // and a throw here would turn an unparseable response into a failed operation rather than
        // into another pass.
        using var desired = JsonDocument.Parse(KafkaClusters.Body(ClusterId));

        KafkaClusters.Matches("{not json", desired.RootElement).ShouldBeFalse();
        KafkaClusters.Matches("{}", desired.RootElement).ShouldBeFalse();
        KafkaClusters.Matches("""{"kind":"Kafka"}""", desired.RootElement).ShouldBeFalse();
    }

    // ── Harness ───────────────────────────────────────────────────────────────────────────────

    static readonly Guid ClusterId = Guid.Parse("eeeeeeee-0000-4000-8000-000000000005");

    /// <summary>The Kafka as an API server would return it — with the `kind` the read adds.</summary>
    static string Kafka(JsonElement desired) => WithKind(KafkaClusters.KafkaJson("events", desired), "Kafka");

    static string NodePool(JsonElement desired) =>
        WithKind(KafkaClusters.NodePoolJson("events", desired), "KafkaNodePool");

    /// <summary>
    ///     ⚠ The rendered document carries no <c>kind</c> — <c>KubeCommand</c> supplies it from the
    ///     <see cref="GroupVersionKind" /> — and a read-back always does. <c>Matches</c> dispatches on
    ///     it, so a test comparing the raw render would exercise the wrong arm.
    /// </summary>
    static string WithKind(string json, string kind) {
        var node = JsonNode.Parse(json)!.AsObject();
        node["apiVersion"] = KafkaClusters.KafkaKind.Group + "/" + KafkaClusters.KafkaKind.Version;
        node["kind"] = kind;
        return node.ToJsonString();
    }

    static string WithExternalEnabled(string body) {
        var node = JsonNode.Parse(body)!.AsObject();
        node["properties"]!.AsObject()["external"] = new JsonObject { ["enabled"] = true };
        return node.ToJsonString();
    }

    /// <summary>The comparison this file exists to argue against, so the argument can be tested.</summary>
    static bool Equal(string left, string right) =>
        string.Equals(
            JsonNode.Parse(left)!.ToJsonString(),
            JsonNode.Parse(right)!.ToJsonString(),
            StringComparison.Ordinal
        );
}
