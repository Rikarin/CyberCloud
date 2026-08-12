using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Search.Tests;

/// <summary>
///     Failure class (c): <see cref="OpenSearchServices.Matches" /> is containment and not equality,
///     and the evidence for that is the CRD rather than the operator's README.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THE THREE DEFAULTS BELOW WERE READ OFF <c>api/v1/opensearch_types.go</c>'s kubebuilder
///         markers, and one of them makes equality fail on the FIRST create rather than eventually.</b>
///         <c>+kubebuilder:default=9200</c> on <c>GeneralConfig.HttpPort</c> and
///         <c>+kubebuilder:default=true</c> on <c>GeneralConfig.SetVMMaxMapCount</c> are ordinary
///         structural defaults. <c>ConfMgmt.SmartScaler</c> carries
///         <c>+kubebuilder:default=true</c> <i>together with</i>
///         <c>+kubebuilder:validation:Required</c> — so the API server writes a
///         <c>spec.confMgmt.smartScaler: true</c> that this provider never sent into <b>every</b>
///         object on <b>every</b> apply, whatever the body said.
///     </para>
///     <para>
///         ⚠ <b>The cluster-backed conformance suite cannot see this, which is why it is asserted
///         here.</b> <c>ClusterConformanceHarness</c> derives a CRD stub with an <i>open</i> schema
///         from the case's own <c>Objects</c>; a stub has no defaults, so a read-back against it
///         returns exactly what was applied and an equality comparison would pass. The failure only
///         appears against a cluster with the operator's real definition installed — which is every
///         production cluster and no test cluster.
///     </para>
/// </remarks>
public sealed class OpenSearchMatchesTests {
    static readonly Guid ClusterId = Guid.Parse("eeeeeeee-0000-4000-8000-000000000006");

    [Fact]
    public void AnObjectCarryingTheCrdsOwnDefaultsStillMatches() {
        // ⚠ THE SABOTAGE THIS TEST EXISTS FOR IS "somebody rewrites Matches as a JSON equality check".
        // The document below is what a real API server returns for the object this provider applies:
        // the applied spec plus the three fields the CRD defaults. Nothing else differs.
        using var body = JsonDocument.Parse(OpenSearchServices.Body(ClusterId));

        var readBack = JsonNode.Parse(OpenSearchServices.ClusterJson("logs", body.RootElement))!
            .AsObject();

        readBack["kind"] = "OpenSearchCluster";
        readBack["apiVersion"] = "opensearch.opster.io/v1";

        var spec = readBack["spec"]!.AsObject();

        // +kubebuilder:default=true, +kubebuilder:validation:Required — the one that decides it.
        spec["confMgmt"] = new JsonObject { ["smartScaler"] = true };

        // +kubebuilder:default=true on GeneralConfig.SetVMMaxMapCount.
        spec["general"]!.AsObject()["setVMMaxMapCount"] = true;

        // And the status subresource, which the operator owns entirely.
        readBack["status"] = new JsonObject { ["phase"] = "RUNNING", ["availableNodes"] = 6 };

        OpenSearchServices.Matches(readBack.ToJsonString(), body.RootElement).ShouldBeTrue(
            "an object carrying only the fields the operator's own CRD defaults was reported as "
            + "drifted. Matches is an equality comparison, and every service would sit in InProgress "
            + "forever while its cluster was perfectly correct."
        );
    }

    [Fact]
    public void APoolWhoseReplicaCountMovedDoesNotMatch() {
        using var body = JsonDocument.Parse(OpenSearchServices.Body(ClusterId, dataNodes: 3));

        var readBack = JsonNode.Parse(OpenSearchServices.ClusterJson("logs", body.RootElement))!
            .AsObject();

        readBack["spec"]!["nodePools"]!.AsArray()
            .Single(x => x!["component"]!.GetValue<string>() == "data")!["replicas"] = 5;

        OpenSearchServices.Matches(readBack.ToJsonString(), body.RootElement).ShouldBeFalse();
    }

    [Fact]
    public void ReorderingTheNodePoolsStillMatches() {
        // ⚠ THE POOLS ARE COMPARED BY COMPONENT AND NOT BY INDEX, AND THIS IS THE TEST THAT SAYS SO.
        // A positional comparison would make Matches agree with the RENDERER rather than with the
        // cluster: an operator, an admission policy or a merge that reordered the array would read
        // back as drifted forever, and the diff would be the data pool's replica count compared
        // against the masters'.
        using var body = JsonDocument.Parse(
            OpenSearchServices.Body(ClusterId, dataNodes: 4, masterNodes: 3, coordinatingNodes: 2)
        );

        var readBack = JsonNode.Parse(OpenSearchServices.ClusterJson("logs", body.RootElement))!
            .AsObject();

        var pools = readBack["spec"]!["nodePools"]!.AsArray();
        var reversed = new JsonArray([.. pools.Reverse().Select(x => x!.DeepClone())]);
        readBack["spec"]!.AsObject()["nodePools"] = reversed;

        OpenSearchServices.Matches(readBack.ToJsonString(), body.RootElement).ShouldBeTrue(
            "the node pools are compared positionally, so a reordered array reads as drift and the "
            // ⚠ And the failure would be silent in the worst way: the counts would be compared
            // across pools, so a three-master five-data cluster could report converged while a
            // five-master three-data one did not.
            + "comparison is between two different pools."
        );
    }

    [Fact]
    public void AStrippedSecurityBlockDoesNotMatch() {
        // ⚠ THE FIELD WHOSE ABSENCE PRODUCES A CLUSTER THAT NEVER FORMS AND REPORTS NOTHING WRONG.
        // pkg/reconcilers/tls.go skips entirely when spec.security.tls is nil, so an object whose
        // security block was stripped by an admission policy or a `kubectl edit` is a set of nodes
        // with no certificates. Nothing else in this provider would ever report it.
        using var body = JsonDocument.Parse(OpenSearchServices.Body(ClusterId));

        var readBack = JsonNode.Parse(OpenSearchServices.ClusterJson("logs", body.RootElement))!
            .AsObject();

        readBack["spec"]!.AsObject().Remove("security");

        OpenSearchServices.Matches(readBack.ToJsonString(), body.RootElement).ShouldBeFalse(
            "an OpenSearchCluster with no spec.security.tls was reported as converged. The operator "
            + "generates no certificates for it and the nodes never discover each other."
        );
    }

    [Fact]
    public void TurningTransportTlsGenerationOffDoesNotMatch() {
        using var body = JsonDocument.Parse(OpenSearchServices.Body(ClusterId));

        var readBack = JsonNode.Parse(OpenSearchServices.ClusterJson("logs", body.RootElement))!
            .AsObject();

        readBack["spec"]!["security"]!["tls"]!["transport"]!.AsObject()["generate"] = false;

        OpenSearchServices.Matches(readBack.ToJsonString(), body.RootElement).ShouldBeFalse();
    }

    [Fact]
    public void APoolTheDesiredBodyNoLongerAsksForDoesNotMatch() {
        // ⚠ THE HALF A CONTAINMENT CHECK OVER THE DESIRED ENTRIES ALONE CANNOT SEE, AND THE REASON
        // Matches ALSO COMPARES THE COUNT. Dropping coordinatingNodes back to zero removes an entry
        // from the desired array; a loop that only checked "every desired pool is present and
        // correct" would report Converged while a coordinating StatefulSet was still running, still
        // holding pods, and still being billed for by the meters that no longer count it.
        using var wanted = JsonDocument.Parse(
            OpenSearchServices.Body(ClusterId, coordinatingNodes: 0)
        );

        using var previous = JsonDocument.Parse(
            OpenSearchServices.Body(ClusterId, coordinatingNodes: 2)
        );

        var stale = OpenSearchServices.ClusterJson("logs", previous.RootElement);

        OpenSearchServices.Matches(stale, wanted.RootElement).ShouldBeFalse(
            "an object still carrying a coordinating pool the desired body no longer asks for was "
            + "reported as converged."
        );
    }

    [Fact]
    public void AnObjectOfAnotherKindDoesNotMatch() {
        using var body = JsonDocument.Parse(OpenSearchServices.Body(ClusterId));

        var readBack = JsonNode.Parse(OpenSearchServices.ClusterJson("logs", body.RootElement))!
            .AsObject();

        readBack["kind"] = "Seaweed";

        OpenSearchServices.Matches(readBack.ToJsonString(), body.RootElement).ShouldBeFalse();
    }

    [Fact]
    public void MalformedJsonDoesNotThrow() {
        using var body = JsonDocument.Parse(OpenSearchServices.Body(ClusterId));

        OpenSearchServices.Matches("{ not json", body.RootElement).ShouldBeFalse();
        OpenSearchServices.Matches("", body.RootElement).ShouldBeFalse();
    }
}
