using CyberCloud.Conformance;
using CyberCloud.Conformance.Harness;
using CyberCloud.Core.Resources;
using CyberCloud.Providers.Monitor.Contracts;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Monitor.Conformance;

/// <summary>
///     <c>CyberCloud.Dashboard/grafanas</c>, registered into the shared provider suite.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THE FIRST CASE WHOSE PROVIDER IS NOT THE ONLY ONE IN ITS ASSEMBLY.</b>
///         <c>CreateProvider</c> hands the harness <see cref="DashboardProvider" /> alone, so the
///         registry this suite runs against knows one namespace, <c>CyberCloud.Dashboard</c>, and
///         nothing of <c>CyberCloud.Monitor</c>. That is deliberate and it is also why the body below
///         names a workspace that does not exist in the harness: the reconciler checks the pointer's
///         <i>shape</i> — tenant, type, resource group — and never asks whether the workspace has been
///         created, because the pod reads the workspace's objects by name at start and a Grafana
///         created a minute before its workspace is the order a tenant scripting both will use. The
///         same arrangement the alert rule has with its sending service.
///     </para>
///     <para>
///         ⚠ <b>WHAT A GREEN RUN HERE PROVES AND WHAT IT DOES NOT.</b> The write path, the verb
///         grammar, the four reconciler clauses over four objects, the mint-once admin credential
///         through the harness's vault, the cross-tenant <c>404</c> and the delete-read-back. It
///         proves nothing about Grafana starting or about a datasource answering; the cluster-backed
///         suite converges the same four objects against a real API server and, by record, asserts
///         nothing about the pod either — <c>charts/managed/grafana/conformance.yaml § owed</c>,
///         <c>the-pod-start-is-unproved-on-a-kubelet</c>.
///     </para>
/// </remarks>
public sealed class GrafanaCase : IProviderCaseSource {
    /// <inheritdoc />
    public static ProviderConformanceCase ProviderCase { get; } =
        new() {
            DisplayName = "CyberCloud.Dashboard/grafanas",
            CreateProvider = () => new DashboardProvider(),
            ReconcilerType = typeof(GrafanaReconciler),
            CreateReconciler = clock => new GrafanaReconciler(clock),
            Type = Grafanas.Type,
            ApiVersion = Grafanas.V2026,
            Body = cluster => Grafanas.Body(cluster, HarnessWorkspace),
            // Turns anonymous viewing on, which is an env entry on the Deployment and the one setting
            // a tenant is likely to flip after the create.
            ChangedBody = cluster => Grafanas.Body(cluster, HarnessWorkspace, anonymousViewers: true),
            // A workspace that is not a resource id path — refused by the schema's format, at the pointer.
            InvalidBody = cluster => WithWorkspace(Grafanas.Body(cluster, HarnessWorkspace), "not-a-path"),
            InvalidBodyTarget = Grafanas.WorkspacePointer,
            ActionName = Grafanas.UrlAction,
            Objects = (id, ns) => Grafanas.Objects(ns, id.Name),
            OperatorWritten = static (_, _) => [],
            DataPlane = null,
            StoragePrefix = null,
            ObjectMatchesDesired = match => {
                using var desired = JsonDocument.Parse(match.DesiredJson);
                var workspace = Grafanas.WorkspaceOf(match.Id, desired.RootElement);
                return workspace.IsSuccess && Grafanas.Matches(match.ObjectJson, workspace.GetValueOrThrow().Name, desired.RootElement);
            }
        };

    /// <summary>
    ///     The workspace the harness's instances point at: a <c>CyberCloud.Monitor/workspaces</c>
    ///     path in the harness's own tenant, subscription and resource group.
    /// </summary>
    /// <remarks>
    ///     ⚠ The suite also writes this body into the OTHER tenant for the cross-tenant assertion,
    ///     where it is refused with <c>404</c> before any pass runs — so the tenant check in
    ///     <see cref="Grafanas.WorkspaceOf" /> never sees it here, and <c>GrafanaReconcilerTests</c> is
    ///     where that check is exercised.
    /// </remarks>
    static string HarnessWorkspace =>
        new ResourceId(
            ConformanceIds.Tenant,
            ConformanceIds.Subscription,
            ConformanceIds.ResourceGroup,
            MonitorWorkspaces.Type,
            "telemetry",
            Guid.Empty
        ).Path;

    static string WithWorkspace(string body, string workspace) {
        var node = JsonNode.Parse(body)!.AsObject();
        node["properties"]!["workspace"] = workspace;
        return node.ToJsonString();
    }
}

/// <summary>The shared suite, run against the managed Grafana type.</summary>
/// <param name="cluster">The harness.</param>
public sealed class GrafanaConformance(ProviderTestCluster<GrafanaCase> cluster)
    : ProviderConformanceTests<GrafanaCase>(cluster), IClassFixture<ProviderTestCluster<GrafanaCase>>;

/// <summary>The container-backed half, skipped loudly, against the managed Grafana type.</summary>
public sealed class GrafanaBackedConformance()
    : ClusterBackedConformanceTests(GrafanaCase.ProviderCase);
