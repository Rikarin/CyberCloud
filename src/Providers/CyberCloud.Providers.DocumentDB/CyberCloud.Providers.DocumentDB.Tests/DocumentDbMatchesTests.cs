using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.DocumentDB.Tests;

/// <summary>
///     Failure class (c): <c>Matches</c> must be containment, not equality.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>CHECKED AGAINST THE CRD AND THE OPERATOR'S SOURCE RATHER THAN AGAINST A README, AND
///         THIS TYPE NEEDS CONTAINMENT FOR THREE INDEPENDENT REASONS.</b> It is the first provider
///         that renders objects from three different worlds — an operator's custom resource, two
///         built-in kinds, and a third party's custom resource — so the three arguments the earlier
///         providers each made once all apply here at the same time.
///     </para>
///     <list type="number">
///         <item>
///             <b>The operator EDITS the list this provider sends.</b>
///             <c>api/v1/cluster_types.go</c> documents <c>shared_preload_libraries</c> as
///             <i>"lists of shared preload libraries to add to <b>the default ones</b>"</i>, so the
///             field comes back longer than it went out. That is stronger than the ordinary
///             structural-defaulting argument: it is not a field appearing, it is a field this
///             provider owns being rewritten.
///         </item>
///         <item>
///             <b>The built-in kinds are the most heavily defaulted objects in Kubernetes.</b>
///             <c>strategy</c>, <c>revisionHistoryLimit</c>, <c>progressDeadlineSeconds</c>,
///             <c>terminationMessagePath</c>, <c>dnsPolicy</c>, <c>clusterIP</c>, <c>ipFamilies</c> —
///             none sent, all returned. This is <c>NatsClusters.Matches</c>' finding and it applies to
///             a built-in kind whichever provider renders it.
///         </item>
///         <item>
///             <b>CloudNativePG writes a large <c>.status</c>,</b> which is a subresource this
///             provider must never compare against.
///         </item>
///     </list>
/// </remarks>
public sealed class DocumentDbMatchesTests {
    static readonly Guid ClusterId = Guid.Parse("dddddddd-0000-4000-8000-000000000006");

    [Fact]
    public void AClusterCarryingTheOPERATORSOwnPreloadLibrariesStillMatches() {
        // ⚠ THE ONE AN EQUALITY COMPARISON GETS WRONG ON THE VERY FIRST READ-BACK OF A CORRECT
        // CLUSTER, and it is not structural defaulting — it is the operator appending to a list this
        // provider sent. cluster_types.go says so in as many words: "to add to the default ones".
        using var desired = JsonDocument.Parse(DocumentDbAccounts.Body(ClusterId));

        var read = JsonNode.Parse(DocumentDbAccounts.ClusterJson("orders", desired.RootElement))!.AsObject();
        read["apiVersion"] = "postgresql.cnpg.io/v1";
        read["kind"] = "Cluster";

        var spec = read["spec"]!.AsObject();

        spec["postgresql"]!["shared_preload_libraries"] = new JsonArray {
            "pg_cron", "pg_documentdb_core", "pg_documentdb", "pg_stat_statements", "auto_explain"
        };

        // And what the API server and the operator add to every object.
        spec["postgresql"]!["syncReplicaElectionConstraint"] = new JsonObject { ["enabled"] = false };
        spec["primaryUpdateStrategy"] = "unsupervised";
        spec["logLevel"] = "info";
        read["metadata"]!.AsObject()["generation"] = 1;
        read["status"] = new JsonObject {
            ["instances"] = 2, ["readyInstances"] = 0, ["phase"] = "Setting up primary"
        };

        DocumentDbAccounts.Matches(read.ToJsonString(), desired.RootElement).ShouldBeTrue(
            "a Cluster read back with the operator's own preload libraries appended was reported as "
            + "drifted. That account would never leave InProgress while being perfectly correct."
        );
    }

    [Fact]
    public void ADeploymentAndAServiceCarryingEveryDefaultKubernetesAddsStillMatch() {
        using var desired = JsonDocument.Parse(DocumentDbAccounts.Body(ClusterId));

        var deployment = JsonNode.Parse(DocumentDbAccounts.DeploymentJson("orders", desired.RootElement))!
            .AsObject();

        deployment["kind"] = "Deployment";

        var deploymentSpec = deployment["spec"]!.AsObject();
        deploymentSpec["strategy"] = new JsonObject { ["type"] = "RollingUpdate" };
        deploymentSpec["revisionHistoryLimit"] = 10;
        deploymentSpec["progressDeadlineSeconds"] = 600;
        deploymentSpec["template"]!["spec"]!["dnsPolicy"] = "ClusterFirst";
        deploymentSpec["template"]!["spec"]!["restartPolicy"] = "Always";
        deploymentSpec["template"]!["spec"]!["containers"]!.AsArray()[0]!["imagePullPolicy"] =
            "IfNotPresent";

        DocumentDbAccounts.Matches(deployment.ToJsonString(), desired.RootElement).ShouldBeTrue();

        var service = JsonNode.Parse(DocumentDbAccounts.ServiceJson("orders"))!.AsObject();
        service["kind"] = "Service";
        service["spec"]!["clusterIP"] = "10.43.12.7";
        service["spec"]!["ipFamilies"] = new JsonArray { "IPv4" };
        service["spec"]!["sessionAffinity"] = "None";
        service["spec"]!["internalTrafficPolicy"] = "Cluster";

        DocumentDbAccounts.Matches(service.ToJsonString(), desired.RootElement).ShouldBeTrue();
    }

    [Fact]
    public void AClusterWhoseSuperuserAccessWasTurnedOffIsReportedAsDrifted() {
        // ⚠ THE FIELD WHOSE LOSS LEAVES EVERY OTHER SIGNAL GREEN, AND THIS TYPE'S EQUIVALENT OF
        // charts/managed/seaweedfs' stripped configSecret. cluster_types.go: with
        // enableSuperuserAccess disabled "the operator will ignore the SuperuserSecret content,
        // delete it". What is left is a healthy PostgreSQL, a healthy Deployment object, and gateway
        // pods that can never mount their credential — and the PostgreSQL cluster's own status says
        // nothing is wrong, because from its point of view nothing is.
        using var desired = JsonDocument.Parse(DocumentDbAccounts.Body(ClusterId));

        var read = JsonNode.Parse(DocumentDbAccounts.ClusterJson("orders", desired.RootElement))!.AsObject();
        read["kind"] = "Cluster";
        read["spec"]!["enableSuperuserAccess"] = false;

        DocumentDbAccounts.Matches(read.ToJsonString(), desired.RootElement).ShouldBeFalse(
            "a Cluster whose superuser access was turned off read back as matching. CloudNativePG "
            + "deletes the Secret the gateway mounts, so the database is fine and the service is "
            + "gone."
        );
    }

    [Fact]
    public void AClusterMissingOneOfThePreloadLibrariesIsReportedAsDrifted() {
        // ⚠ THE OTHER HALF OF CONTAINMENT, AND THE ONE IT IS EASY TO GET WRONG. Containment that
        // tolerated everything would be `return true`, which passes every test above. Losing
        // pg_documentdb specifically is a PostgreSQL that starts, accepts connections, and answers
        // every FerretDB call with "function does not exist".
        using var desired = JsonDocument.Parse(DocumentDbAccounts.Body(ClusterId));

        var read = JsonNode.Parse(DocumentDbAccounts.ClusterJson("orders", desired.RootElement))!.AsObject();
        read["kind"] = "Cluster";
        read["spec"]!["postgresql"]!["shared_preload_libraries"] = new JsonArray {
            "pg_cron", "pg_documentdb_core"
        };

        DocumentDbAccounts.Matches(read.ToJsonString(), desired.RootElement).ShouldBeFalse();
    }

    [Fact]
    public void AClusterOnTheStockPostgresImageIsReportedAsDrifted() {
        // A Cluster whose imageName was swapped for ghcr.io/cloudnative-pg/postgresql is a healthy
        // PostgreSQL with no documentdb extension in it — see
        // DocumentDbDeclarationTests.ThePostgresImageIsNeverTheStockOne for why that reads as correct
        // from everywhere else.
        using var desired = JsonDocument.Parse(DocumentDbAccounts.Body(ClusterId));

        var read = JsonNode.Parse(DocumentDbAccounts.ClusterJson("orders", desired.RootElement))!.AsObject();
        read["kind"] = "Cluster";
        read["spec"]!["imageName"] = "ghcr.io/cloudnative-pg/postgresql:17";

        DocumentDbAccounts.Matches(read.ToJsonString(), desired.RootElement).ShouldBeFalse();
    }

    [Fact]
    public void DriftOnAFieldThisProviderOWNSIsStillReportedOnEveryKind() {
        using var desired = JsonDocument.Parse(DocumentDbAccounts.Body(ClusterId, instances: 3));

        var cluster = JsonNode.Parse(DocumentDbAccounts.ClusterJson("orders", desired.RootElement))!.AsObject();
        cluster["kind"] = "Cluster";
        cluster["spec"]!["instances"] = 2;
        DocumentDbAccounts.Matches(cluster.ToJsonString(), desired.RootElement).ShouldBeFalse();

        var deployment = JsonNode.Parse(DocumentDbAccounts.DeploymentJson("orders", desired.RootElement))!
            .AsObject();

        deployment["kind"] = "Deployment";
        deployment["spec"]!["replicas"] = 9;
        DocumentDbAccounts.Matches(deployment.ToJsonString(), desired.RootElement).ShouldBeFalse();

        var service = JsonNode.Parse(DocumentDbAccounts.ServiceJson("orders"))!.AsObject();
        service["kind"] = "Service";
        service["spec"]!["ports"]!.AsArray()[0]!["port"] = 27018;
        DocumentDbAccounts.Matches(service.ToJsonString(), desired.RootElement).ShouldBeFalse();

        var monitor = JsonNode.Parse(DocumentDbAccounts.PodMonitorJson("orders"))!.AsObject();
        monitor["kind"] = "PodMonitor";
        monitor["spec"]!["podMetricsEndpoints"]!.AsArray()[0]!["path"] = "/metrics";
        DocumentDbAccounts.Matches(monitor.ToJsonString(), desired.RootElement).ShouldBeFalse(
            "a PodMonitor pointed at /metrics read back as matching. It would scrape a 404 forever "
            + "without failing, which is the quiet-scrape hazard docs/plan/12 § piece 6 exists to "
            + "avoid."
        );
    }

    [Fact]
    public void AShrunkenDataVolumeIsReportedRatherThanAccepted() {
        using var desired = JsonDocument.Parse(DocumentDbAccounts.Body(ClusterId, storageSize: "500Gi"));

        var read = JsonNode.Parse(DocumentDbAccounts.ClusterJson("orders", desired.RootElement))!.AsObject();
        read["kind"] = "Cluster";
        read["spec"]!["storage"]!["size"] = "20Gi";

        DocumentDbAccounts.Matches(read.ToJsonString(), desired.RootElement).ShouldBeFalse();
    }

    [Fact]
    public void GarbageAndUnknownKindsAreFalseRatherThanAnExceptionOrATrue() {
        using var desired = JsonDocument.Parse(DocumentDbAccounts.Body(ClusterId));

        // A reconciler that threw here would fail the resource on a malformed read rather than
        // reporting it as not-yet-converged and coming back.
        DocumentDbAccounts.Matches("{not json", desired.RootElement).ShouldBeFalse();
        DocumentDbAccounts.Matches("[]", desired.RootElement).ShouldBeFalse();
        DocumentDbAccounts.Matches("{}", desired.RootElement).ShouldBeFalse();

        // ⚠ AND AN UNRECOGNISED KIND IS FALSE RATHER THAN FALLING THROUGH TO A DEFAULT BRANCH. This
        // type dispatches over FOUR kinds, so a `_ =>` that guessed at the most likely one would let
        // a ConfigMap somebody's policy injected read back as a converged Cluster.
        DocumentDbAccounts.Matches(
            "{\"kind\":\"ConfigMap\",\"spec\":{}}",
            desired.RootElement
        ).ShouldBeFalse();

        DocumentDbAccounts.Matches(
            "{\"kind\":\"StatefulSet\",\"spec\":{\"replicas\":2}}",
            desired.RootElement
        ).ShouldBeFalse();
    }
}
