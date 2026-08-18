using CyberCloud.Conformance;
using CyberCloud.Conformance.Harness;
using CyberCloud.Providers.Terminal.Contracts;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Terminal.Conformance;

/// <summary>
///     <c>CyberCloud.Terminal/consoles</c>, registered into the shared provider suite.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THIS FILE IS THE ENTIRE COST OF PUTTING THE TWELFTH PROVIDER UNDER CONFORMANCE, AND
///         IT IS THE FIRST WHOSE PRODUCT THE SUITE CANNOT SEE.</b>
///         <c>test/CyberCloud.Conformance</c> was not touched for a type that renders three
///         core-group objects where the last one rendered a custom resource — which is the claim that
///         shape has always made. What is new is that the claim is now being made about a case that
///         describes <b>only the durable half of the resource</b>: the pod, the session and the hub
///         are outside <see cref="ProviderConformanceCase" /> entirely.
///     </para>
///     <para>
///         ⚠ <b>SO STATE PLAINLY WHAT A GREEN RUN HERE PROVES AND WHAT IT DOES NOT.</b> It proves the
///         twelve-step write path, the verb grammar, the four reconciler clauses, the cross-tenant
///         404, the seven labels and the delete-read-back, over a home volume, a service account and
///         a network policy. It proves <b>nothing at all</b> about whether a shell starts, whether the
///         network policy constrains anything, whether the idle timeout is honoured, or whether a
///         person can type into the result. Every one of those is a hand-written test in
///         <c>CyberCloud.Providers.Terminal.Tests</c> or an entry in
///         <c>charts/managed/cloud-shell/conformance.yaml § owed</c>.
///         <c>the-suite-never-attaches</c> is the entry that says so.
///     </para>
/// </remarks>
public sealed class CloudConsoleCase : IProviderCaseSource {
    /// <inheritdoc />
    public static ProviderConformanceCase ProviderCase { get; } =
        new() {
            DisplayName = "CyberCloud.Terminal/consoles",
            CreateProvider = () => new TerminalProvider(),
            ReconcilerType = typeof(CloudConsoleReconciler),
            CreateReconciler = clock => new CloudConsoleReconciler(clock),
            Type = CloudConsoles.Type,
            ApiVersion = CloudConsoles.V2026,
            Body = cluster => CloudConsoles.Body(cluster),
            // ⚠ Changes the EGRESS POSTURE, which the reconciler renders into the network policy's
            // rule list and CloudConsoles.Matches reads back as a count. Two other candidates were
            // wrong for two different reasons, and both are worth recording because both look right:
            //
            //   * `session.idleTimeoutMinutes` would pass the update test while proving nothing —
            //     that number lives on the POD, and the pod is not an object this case lists.
            //   * `home.size` would prove the update reached the cluster and would FAIL against a
            //     real one. A PersistentVolumeClaim's size may only be changed on a StorageClass with
            //     `allowVolumeExpansion: true`, and k3s' bundled local-path provisioner does not set
            //     it — so the API server refuses the resize outright. The schema's "it grows and never
            //     shrinks" is the API's promise; whether the cluster can keep it is the storage
            //     class's. conformance.yaml § owed, `growing-the-home-volume-needs-an-expandable-class`.
            ChangedBody = cluster => CloudConsoles.Body(cluster, egress: "TenantOnly"),
            // Drops the required `/properties/home/size`.
            // ⚠ Built from a valid body with one required property removed rather than hand-written: a
            // hand-written invalid body drifts out of date the day the schema gains a property and
            // then tests "invalid for the wrong reason" while still going green.
            InvalidBody = cluster => WithoutHomeSize(CloudConsoles.Body(cluster)),
            InvalidBodyTarget = "/properties/home/size",
            // ⚠ THE SUITE'S ACTION ASSERTION IS ABOUT THE VERB GRAMMAR — that a POST to a declared
            // action on an existing resource is routed and that one to a name that does not exist is
            // the canonical 404. `terminate` is named rather than `connect` because it is the one
            // whose whole behaviour against a world with no pod is a documented success, so the
            // assertion says the same thing in both harnesses.
            ActionName = CloudConsoles.TerminateAction,
            // ⚠ THREE OBJECTS, AND THE FOURTH — THE POD — IS DELIBERATELY ABSENT. A case listing an
            // object the reconciler does not apply fails every world-facing assertion for the case's
            // reason rather than the provider's; and a console with no pod is the correct converged
            // state of this type, so listing it would make the suite demand the very thing the design
            // exists to avoid.
            Objects = (id, ns) => CloudConsoles.Objects(ns, id.Name),
            // This platform mints or computes everything this type's actions hand back, so no operator
            // writes an object any action reads. Stated rather than defaulted — see
            // ProviderConformanceCase.OperatorWritten.
            OperatorWritten = static (_, _) => [],
            ObjectMatchesDesired = (objectJson, desiredJson) => {
                using var desired = JsonDocument.Parse(desiredJson);
                return CloudConsoles.Matches(objectJson, desired.RootElement);
            }
        };

    /// <summary>A valid body with the required home size removed.</summary>
    /// <param name="body">A valid body.</param>
    static string WithoutHomeSize(string body) {
        var node = JsonNode.Parse(body)!.AsObject();
        node["properties"]!.AsObject()["home"]!.AsObject().Remove("size");
        return node.ToJsonString();
    }
}

/// <summary>The shared suite, run against the cloud-terminal provider.</summary>
/// <param name="cluster">The harness.</param>
public sealed class CloudConsoleConformance(ProviderTestCluster<CloudConsoleCase> cluster)
    : ProviderConformanceTests<CloudConsoleCase>(cluster), IClassFixture<ProviderTestCluster<CloudConsoleCase>>;

/// <summary>The container-backed half, skipped loudly, against the cloud-terminal provider.</summary>
/// <remarks>
///     ⚠ <b>Still declared, even though this provider has a real <c>*.Cluster.Conformance</c> project
///     that makes the same assertions against a real API server.</b> The skips are not redundant with
///     it: they run on a machine with no Docker and say, by name, which criteria were not checked.
///     Deleting them here would make "conformance: green" readable as "the cluster-backed criteria
///     were met" on exactly the machines where they were not.
/// </remarks>
public sealed class CloudConsoleClusterBackedConformance() : ClusterBackedConformanceTests(CloudConsoleCase.ProviderCase);
