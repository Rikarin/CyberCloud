using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.ContainerRegistry.Tests;

/// <summary>
///     Failure class (b): <c>Matches</c> as equality rather than containment.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THE USUAL ARGUMENT IS NOT MERELY FALSE HERE, IT IS UNAVAILABLE — AND THAT IS THE
///         DISTINCTION WORTH KEEPING.</b> Five families argue containment from a CRD's
///         <c>+kubebuilder:default</c> markers or from an operator's mutating webhook;
///         <c>KafkaClusters</c> and <c>ClickHouseClusters</c> found CRDs that declared none and could
///         at least look. <c>goharbor/harbor-operator</c> is archived and its CRDs are not installed,
///         so there is nothing to look at. What forces containment is that <b>five of the six kinds
///         this row renders are built-in</b>, and a built-in kind is the most heavily defaulted object
///         in Kubernetes.
///     </para>
///     <para>
///         ⚠ <b>Recorded for the author who reaches for the operator later:</b> it carries both
///         mechanisms and carries them hard — 298 <c>+kubebuilder:default</c> markers across
///         <c>apis/</c>, and a <c>MutatingWebhookConfiguration</c> whose <c>HarborCluster</c> defaulter
///         <i>nils out</i> every non-selected variant of <c>spec.cache</c>, <c>spec.database</c> and
///         <c>spec.storage</c>. So containment would be forced twice over. See <c>SOURCE</c>.
///     </para>
/// </remarks>
public sealed class ContainerRegistryMatchesTests {
    [Fact]
    public void AnObjectCarryingTheApiServersOwnDefaultsStillMatches() {
        // ⚠ THE COUNTER-EXAMPLE, RUN. A Deployment applied by this provider comes back from a real API
        // server carrying a `strategy`, a `revisionHistoryLimit`, a `progressDeadlineSeconds`, a
        // `terminationGracePeriodSeconds`, a `dnsPolicy`, a `restartPolicy`, a `schedulerName`, a
        // `securityContext` and an `imagePullPolicy` this provider never sent. An equality comparison
        // would report drift on a perfectly converged registry forever — and the symptom is a resource
        // stuck InProgress while the cluster is exactly right, which reads as a platform failure.
        using var body = JsonDocument.Parse(ContainerRegistries.Body(ClusterId));

        var rendered = ContainerRegistries.CoreDeploymentJson("images", body.RootElement);

        ContainerRegistries.Matches(rendered, body.RootElement).ShouldBeTrue(
            "the rendered document does not match the body it was rendered from"
        );

        ContainerRegistries.Matches(WithApiServerDefaults(rendered), body.RootElement).ShouldBeTrue(
            "a Deployment read back from a real API server does not match. `Matches` is a CONTAINMENT "
            + "test: the fields this provider owns hold the desired values, and everything Kubernetes "
            + "added is ignored."
        );
    }

    [Fact]
    public void AServiceCarryingItsAssignedClusterIpStillMatches() {
        // ⚠ The Service half, which is the one somebody would forget. A Service comes back with a
        // `clusterIP`, a `clusterIPs`, an `ipFamilies`, an `ipFamilyPolicy`, an `internalTrafficPolicy`
        // and a `sessionAffinity` — every one of them assigned by the API server on create.
        using var body = JsonDocument.Parse(ContainerRegistries.Body(ClusterId));

        var rendered = ContainerRegistries.CoreServiceJson("images", body.RootElement);
        var readBack = JsonNode.Parse(rendered)!.AsObject();

        readBack["kind"] = "Service";
        var spec = readBack["spec"]!.AsObject();
        spec["clusterIP"] = "10.43.12.7";
        spec["clusterIPs"] = new JsonArray { "10.43.12.7" };
        spec["ipFamilies"] = new JsonArray { "IPv4" };
        spec["ipFamilyPolicy"] = "SingleStack";
        spec["internalTrafficPolicy"] = "Cluster";
        spec["sessionAffinity"] = "None";

        ContainerRegistries.Matches(readBack.ToJsonString(), body.RootElement).ShouldBeTrue();
    }

    [Fact]
    public void ADriftedReplicaCountDoesNotMatchAndADriftedImageTagDoesNotEither() {
        // ⚠ THE OTHER DIRECTION, AND WITHOUT IT CONTAINMENT IS INDISTINGUISHABLE FROM "always true".
        // Both of these are drift a tenant asked to have corrected: one is somebody scaling a
        // Deployment by hand, the other is a version property that has changed and not yet reached the
        // cluster.
        using var body = JsonDocument.Parse(ContainerRegistries.Body(ClusterId));

        var drifted = JsonNode.Parse(ContainerRegistries.CoreDeploymentJson("images", body.RootElement))!
            .AsObject();

        drifted["kind"] = "Deployment";
        drifted["spec"]!["replicas"] = 7;

        ContainerRegistries.Matches(drifted.ToJsonString(), body.RootElement).ShouldBeFalse(
            "a Deployment scaled by hand reports as converged"
        );

        using var upgraded = JsonDocument.Parse(ContainerRegistries.Body(ClusterId, version: "2.14"));

        ContainerRegistries.Matches(
                ContainerRegistries.CoreDeploymentJson("images", body.RootElement),
                upgraded.RootElement
            )
            .ShouldBeFalse(
                "a workload still running the old image tag reports as converged, so a version change "
                + "would never be applied"
            );
    }

    [Fact]
    public void TheReplicaCountIsComparedOnTheThreeStatelessComponentsAndNotOnTheThreeVolumeOwners() {
        // ⚠ THE ONE ASYMMETRY IN THIS COMPARISON, AND GETTING IT WRONG IS PERMANENT DRIFT. Each of the
        // database, Redis and the registry owns a ReadWriteOnce claim and runs ONE replica whatever
        // `replicas` says — so a comparison that checked all six against Replicas(body) would report
        // the database as drifted the moment a tenant asked for two of anything, forever.
        using var body = JsonDocument.Parse(ContainerRegistries.Body(ClusterId, replicas: 4));

        foreach (var stateless in new[] {
                     ContainerRegistries.CoreDeploymentJson("images", body.RootElement),
                     ContainerRegistries.PortalDeploymentJson("images", body.RootElement),
                     ContainerRegistries.JobServiceDeploymentJson("images", body.RootElement)
                 }) {
            JsonNode.Parse(stateless)!["spec"]!["replicas"]!.GetValue<int>().ShouldBe(4);
            ContainerRegistries.Matches(stateless, body.RootElement).ShouldBeTrue();
        }

        foreach (var owner in new[] {
                     ContainerRegistries.DatabaseSetJson("images", body.RootElement),
                     ContainerRegistries.RedisSetJson("images", body.RootElement),
                     ContainerRegistries.RegistrySetJson("images", body.RootElement)
                 }) {
            JsonNode.Parse(owner)!["spec"]!["replicas"]!.GetValue<int>().ShouldBe(1);
            ContainerRegistries.Matches(owner, body.RootElement).ShouldBeTrue(
                "a volume-owning component is compared against the tenant's replica count, so it "
                + "reports permanent drift on any body asking for more than one"
            );
        }
    }

    [Fact]
    public void AnUnrecognisedDocumentIsFalseRatherThanAssumed() {
        using var body = JsonDocument.Parse(ContainerRegistries.Body(ClusterId));

        ContainerRegistries.Matches("not json at all", body.RootElement).ShouldBeFalse();
        ContainerRegistries.Matches("[]", body.RootElement).ShouldBeFalse();
        ContainerRegistries.Matches("{\"kind\":\"Namespace\"}", body.RootElement).ShouldBeFalse();
    }

    [Fact]
    public void ACredentialsSecretMissingOneFieldDoesNotMatch() {
        // ⚠ PRESENCE AND NOT CONTENT, AND WHAT IT CATCHES IS THE FAILURE THAT MATTERS. A Secret deleted
        // by a well-meant kubectl, emptied by an admission policy, or never applied is six components
        // whose secretKeyRefs cannot resolve. All three land as a missing field.
        var credentials = ContainerRegistries.GenerateCredentials();
        using var body = JsonDocument.Parse(ContainerRegistries.Body(ClusterId));

        var whole = ContainerRegistries.CredentialsSecretJson("images", credentials);
        ContainerRegistries.Matches(whole, body.RootElement).ShouldBeTrue();

        credentials.Remove(ContainerRegistries.CsrfKeyField);

        var partial = JsonNode.Parse(ContainerRegistries.CredentialsSecretJson("images", credentials))!
            .AsObject();

        partial["kind"] = "Secret";

        ContainerRegistries.Matches(partial.ToJsonString(), body.RootElement).ShouldBeFalse(
            "a credentials Secret with an empty field reports as converged, so the next pass would not "
            + "re-render it from the vault and Harbor's core would stay in CreateContainerConfigError"
        );
    }

    static readonly Guid ClusterId = Guid.Parse("eeeeeeee-0000-4000-8000-00000000000c");

    /// <summary>A rendered Deployment as a real API server hands it back.</summary>
    /// <remarks>
    ///     ⚠ Every field added here is one Kubernetes writes on create and this provider never sends.
    ///     They are typed out rather than read from anywhere, because the point is what an API server
    ///     does and not what this repository thinks it does.
    /// </remarks>
    static string WithApiServerDefaults(string rendered) {
        var document = JsonNode.Parse(rendered)!.AsObject();

        document["kind"] = "Deployment";
        document["apiVersion"] = "apps/v1";

        var spec = document["spec"]!.AsObject();
        spec["revisionHistoryLimit"] = 10;
        spec["progressDeadlineSeconds"] = 600;
        spec["strategy"] = new JsonObject {
            ["type"] = "RollingUpdate",
            ["rollingUpdate"] = new JsonObject { ["maxSurge"] = "25%", ["maxUnavailable"] = "25%" }
        };

        var pod = spec["template"]!["spec"]!.AsObject();
        pod["restartPolicy"] = "Always";
        pod["terminationGracePeriodSeconds"] = 30;
        pod["dnsPolicy"] = "ClusterFirst";
        pod["securityContext"] = new JsonObject();
        pod["schedulerName"] = "default-scheduler";

        foreach (var container in pod["containers"]!.AsArray()) {
            var entry = container!.AsObject();
            entry["imagePullPolicy"] = "IfNotPresent";
            entry["terminationMessagePath"] = "/dev/termination-log";
            entry["terminationMessagePolicy"] = "File";

            foreach (var port in entry["ports"]!.AsArray()) {
                port!["protocol"] = "TCP";
            }
        }

        return document.ToJsonString();
    }
}
