using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Analytics.Tests;

/// <summary>
///     <c>ClickHouseClusters.Matches</c> — the read-back that decides whether a cluster has converged.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Failure class (c): <c>Matches</c> must be containment, not equality, if anything else
///         edits the spec — and the reason here is NOT the reason three of the five providers before
///         this one give.</b> Their argument is structural defaulting: <c>NatsClusters</c> because
///         built-in kinds are the most heavily defaulted objects in Kubernetes,
///         <c>StorageAccounts</c> because the seaweedfs CRD carries <c>+kubebuilder:default</c>
///         markers. <b>Checked in the CRD rather than in a README: neither Altinity CRD declares a
///         single <c>default:</c>, and this operator ships no admission webhook.</b> That is the third
///         sighting of <c>KafkaClusters</c>' finding, and it means the usual argument is <i>false</i>
///         here.
///     </para>
///     <para>
///         The reasons that <i>are</i> here are two, and the tests below are written against both:
///         <c>spec.templating.policy: auto</c> merges a <c>ClickHouseInstallationTemplate</c> into
///         this spec at the request of a cluster operator who is not this platform (the CHI's own
///         <c>status.usedTemplates</c> exists to record it), and half of what this provider writes
///         lands under <c>x-kubernetes-preserve-unknown-fields: true</c> — which the API server does
///         not prune.
///     </para>
/// </remarks>
public sealed class ClickHouseMatchesTests {
    static readonly Guid ClusterId = Guid.Parse("eeeeeeee-0000-4000-8000-000000000006");

    [Fact]
    public void WhatTheProviderRendersMatchesItself() {
        using var body = JsonDocument.Parse(ClickHouseClusters.Body(ClusterId));

        ClickHouseClusters.Matches(
            ClickHouseClusters.ClickHouseJson("events", body.RootElement),
            body.RootElement
        ).ShouldBeTrue();

        ClickHouseClusters.Matches(
            ClickHouseClusters.KeeperJson("events", body.RootElement),
            body.RootElement
        ).ShouldBeTrue();
    }

    [Fact]
    public void AFieldThisProviderNeverSentDoesNotMakeTheObjectDrifted() {
        // ⚠ CONTAINMENT, AND THE COUNTER-EXAMPLE IS THE SUPPORTED FEATURE RATHER THAN AN INVENTED
        // ONE. `spec.templating.policy: auto` makes the operator merge every
        // ClickHouseInstallationTemplate whose chiSelector matches, which is the documented way to set
        // a cluster-wide podTemplate, an image pull secret or a node affinity. An equality comparison
        // would leave every ClickHouse cluster in the region stuck in InProgress the day somebody
        // installed one, with the workload perfectly correct.
        using var body = JsonDocument.Parse(ClickHouseClusters.Body(ClusterId));

        var document = JsonNode.Parse(
            ClickHouseClusters.ClickHouseJson("events", body.RootElement)
        )!.AsObject();

        document["spec"]!["templating"] = new JsonObject { ["policy"] = "auto" };
        document["spec"]!["taskID"] = "e7c1";
        document["status"] = new JsonObject {
            ["usedTemplates"] = new JsonArray { new JsonObject { ["name"] = "cluster-wide" } }
        };
        document["metadata"]!["finalizers"] = new JsonArray { "finalizer.clickhouseinstallation.altinity.com" };

        ClickHouseClusters.Matches(document.ToJsonString(), body.RootElement).ShouldBeTrue(
            "an equality comparison would report drift for fields nobody in this platform wrote."
        );
    }

    [Fact]
    public void ExtraKeysInsideAPreserveUnknownFieldsSubtreeAreAlsoContainment() {
        // ⚠ THE SECOND REASON, AND IT IS SPECIFIC TO THIS CRD. `configuration.settings` and the `spec`
        // of every entry in `templates.podTemplates` / `templates.volumeClaimTemplates` are all
        // `x-kubernetes-preserve-unknown-fields: true`, so the API server does not prune them and a
        // read-back carries whatever any manager put there.
        using var body = JsonDocument.Parse(ClickHouseClusters.Body(ClusterId));

        var document = JsonNode.Parse(
            ClickHouseClusters.ClickHouseJson("events", body.RootElement)
        )!.AsObject();

        document["spec"]!["configuration"]!["settings"]!["max_concurrent_queries"] = 200;
        document["spec"]!["templates"]!["podTemplates"]!.AsArray()[0]!["spec"]!["nodeSelector"] =
            new JsonObject { ["kubernetes.io/arch"] = "amd64" };

        ClickHouseClusters.Matches(document.ToJsonString(), body.RootElement).ShouldBeTrue();
    }

    [Theory]
    [InlineData("shards")]
    [InlineData("replicas")]
    [InlineData("keeperNodes")]
    public void AChangedCountIsDrift(string property) {
        // Containment is not "anything goes". Every value this provider owns still has to be present
        // and equal, and the two counts are the ones a tenant changes.
        using var body = JsonDocument.Parse(ClickHouseClusters.Body(ClusterId));
        using var bigger = JsonDocument.Parse(WithNumber(ClickHouseClusters.Body(ClusterId), property, 4));

        ClickHouseClusters.Matches(
            ClickHouseClusters.ClickHouseJson("events", body.RootElement),
            bigger.RootElement
        ).ShouldBe(
            // `keeperNodes` does not reach the installation at all, so a changed keeper count is not
            // drift THERE — it is drift on the Keeper, which the next assertion covers. Stating both
            // in one test is what stops somebody "fixing" the asymmetry by comparing the wrong object.
            property == "keeperNodes"
        );

        ClickHouseClusters.Matches(
            ClickHouseClusters.KeeperJson("events", body.RootElement),
            bigger.RootElement
        ).ShouldBe(property != "keeperNodes");
    }

    [Fact]
    public void ACoordinationPointerAtSomebodyElsesKeeperIsDrift() {
        // ⚠ THE FIELD WITH NO NATURAL DEFENCES, AND THE ONLY THING BETWEEN A TYPO AND A TENANT
        // DISCOVERING IT IN SQL. Every other value here becomes a workload that visibly fails when it
        // is wrong; a zookeeper host pointing at another tenant's — or at nothing — produces a
        // cluster that starts, answers, converges and cannot replicate.
        using var body = JsonDocument.Parse(ClickHouseClusters.Body(ClusterId));

        var document = JsonNode.Parse(
            ClickHouseClusters.ClickHouseJson("events", body.RootElement)
        )!.AsObject();

        document["spec"]!["configuration"]!["zookeeper"]!["nodes"]!.AsArray()[0]!["host"] =
            "keeper-somebody-else";

        ClickHouseClusters.Matches(document.ToJsonString(), body.RootElement).ShouldBeFalse();
    }

    [Fact]
    public void ARemovedCoordinationBlockIsDrift() {
        // ⚠ THE ONE FIELD WHOSE ABSENCE LEAVES A WORKING-LOOKING CLUSTER, which is why nothing else
        // would ever report it. A CHI with no zookeeper block is a perfectly healthy single-node
        // ClickHouse that refuses every ReplicatedMergeTree — so "temporarily drop the block so the
        // cluster can come up" is a change that would look like a fix.
        using var body = JsonDocument.Parse(ClickHouseClusters.Body(ClusterId));

        var document = JsonNode.Parse(
            ClickHouseClusters.ClickHouseJson("events", body.RootElement)
        )!.AsObject();

        document["spec"]!["configuration"]!.AsObject().Remove("zookeeper");

        ClickHouseClusters.Matches(document.ToJsonString(), body.RootElement).ShouldBeFalse();
    }

    [Fact]
    public void AKeeperDocumentIsNeverJudgedAgainstTheInstallationsRules() {
        // ⚠ ONE FUNCTION, TWO KINDS, AND THE CONFORMANCE CASE HANDS IT BOTH. A Matches that ignored
        // `kind` would judge the Keeper against the installation's rules — no shardsCount, no
        // zookeeper block — and report a correctly applied Keeper as permanently drifted.
        using var body = JsonDocument.Parse(ClickHouseClusters.Body(ClusterId));

        var keeper = ClickHouseClusters.KeeperJson("events", body.RootElement);

        keeper.ShouldNotContain("zookeeper", Case.Sensitive);
        keeper.ShouldNotContain("shardsCount", Case.Sensitive);
        ClickHouseClusters.Matches(keeper, body.RootElement).ShouldBeTrue();
    }

    [Fact]
    public void AKindThisTypeDoesNotOwnIsFalseRatherThanAssumed() {
        // ⚠ NOT TRUE-BY-DEFAULT. A Matches that fell through to `true` for an unrecognised document
        // would report an object that was never applied as converged — and on this type the object
        // most likely to be missing is the Keeper, whose absence is invisible until a tenant writes
        // DDL.
        using var body = JsonDocument.Parse(ClickHouseClusters.Body(ClusterId));

        ClickHouseClusters.Matches(
            new JsonObject {
                ["kind"] = "ConfigMap", ["spec"] = new JsonObject()
            }.ToJsonString(),
            body.RootElement
        ).ShouldBeFalse();
    }

    [Fact]
    public void MalformedJsonIsFalseRatherThanAnException() {
        using var body = JsonDocument.Parse(ClickHouseClusters.Body(ClusterId));

        ClickHouseClusters.Matches("{not json", body.RootElement).ShouldBeFalse();
        ClickHouseClusters.Matches("[]", body.RootElement).ShouldBeFalse();
        ClickHouseClusters.Matches("{}", body.RootElement).ShouldBeFalse();
    }

    static string WithNumber(string body, string property, int value) {
        var node = JsonNode.Parse(body)!.AsObject();
        node["properties"]!.AsObject()[property] = value;
        return node.ToJsonString();
    }
}
