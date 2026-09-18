using CyberCloud.Conformance;
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
///         proves nothing about Grafana starting or about a datasource answering: the fake cluster
///         schedules nothing. That is
///         <c>GrafanaClusterBackedConformance.TheGrafanaPodStartsAndBothDatasourcesAnswer</c>'s, on a
///         real kubelet, which puts the workspace's two objects into the namespace itself because this
///         case has no ancestor to do it — <see cref="HarnessWorkspace" />.
///     </para>
/// </remarks>
public sealed class GrafanaCase : IProviderCaseSource {
    /// <inheritdoc />
    public static ProviderConformanceCase ProviderCase { get; } =
        new() {
            DisplayName = "CyberCloud.Dashboard/grafanas",
            CreateProvider = static () => new DashboardProvider(),
            ReconcilerType = typeof(GrafanaReconciler),
            CreateReconciler = static clock => new GrafanaReconciler(clock),
            Type = Grafanas.Type,
            ApiVersion = Grafanas.V2026,
            Body = static cluster => Grafanas.Body(cluster, HarnessWorkspace),
            // Turns anonymous viewing on, which is an env entry on the Deployment and the one setting
            // a tenant is likely to flip after the create.
            ChangedBody = static cluster => Grafanas.Body(cluster, HarnessWorkspace, true),
            // A workspace that is not a resource id path — refused by the schema's format, at the pointer.
            InvalidBody = static cluster => WithWorkspace(Grafanas.Body(cluster, HarnessWorkspace), "not-a-path"),
            InvalidBodyTarget = Grafanas.WorkspacePointer,
            ActionName = Grafanas.UrlAction,
            Objects = static (id, ns) => Grafanas.Objects(ns, id.Name),
            OperatorWritten = static (_, _) => [],
            DataPlane = null,
            StoragePrefix = null,
            ObjectMatchesDesired = static match => {
                using var desired = JsonDocument.Parse(match.DesiredJson);
                var workspace = Grafanas.WorkspaceOf(match.Id, desired.RootElement);
                return workspace.IsSuccess
                    && Grafanas.Matches(match.ObjectJson, workspace.GetValueOrThrow().Name, desired.RootElement);
            }
        };

    /// <summary>The name of the workspace the harness's instances point at.</summary>
    public const string HarnessWorkspaceName = "telemetry";

    /// <summary>
    ///     The workspace the harness's instances point at: a <c>CyberCloud.Monitor/workspaces</c>
    ///     path in the harness's own tenant, subscription and resource group.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             No resource exists at this path in either suite, and that is not the same
    ///             statement in both.
    ///         </b> On the fake cluster nothing reads the workspace's objects, so
    ///         the pointer's shape is all that is checked. On the real k3s the pod's three <c>env</c>
    ///         references name <c>monitor-telemetry</c> and its ingest-key <c>Secret</c>, neither of
    ///         which any reconciler in a one-provider registry will write; the kubelet holds the pod
    ///         in <c>CreateContainerConfigError</c> until they appear, and the cluster-backed test
    ///         writes them itself from the workspace contract's own documents — the arrangement
    ///         <c>charts/managed/grafana/conformance.yaml § owed</c>,
    ///         <c>the-workspace-in-the-kubelet-test-is-the-harness-standing-in</c>, records. The first
    ///         version of that file said the harness had such a workspace; it did not.
    ///     </para>
    ///     <para>
    ///         ⚠ The suite also writes this body into the OTHER tenant for the cross-tenant assertion,
    ///         where it is refused with <c>404</c> before any pass runs — so the tenant check in
    ///         <see cref="Grafanas.WorkspaceOf" /> never sees it here, and <c>GrafanaReconcilerTests</c>
    ///         is where that check is exercised.
    ///     </para>
    /// </remarks>
    public static string HarnessWorkspace =>
        new ResourceId(
            ConformanceIds.Tenant,
            ConformanceIds.Subscription,
            ConformanceIds.ResourceGroup,
            MonitorWorkspaces.Type,
            HarnessWorkspaceName,
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
