using CyberCloud.Cluster.Conformance;
using CyberCloud.Cluster.Conformance.Infrastructure;
using CyberCloud.Conformance;
using CyberCloud.Core.Resources;
using CyberCloud.Providers.Monitor.Conformance;
using CyberCloud.Providers.Monitor.Contracts;
using CyberCloud.ResourceManager.Contracts;
using CyberCloud.ResourceManager.Reconcile;
using k8s;
using k8s.Models;
using Shouldly;
using System.Globalization;
using System.Net;
using System.Text.Json;

namespace CyberCloud.Providers.Monitor.ClusterConformance;

/// <summary>
///     The cluster-backed suite, run against the managed Grafana type — plus the one assertion that
///     reads what the <b>pod</b> serves rather than what the API server holds.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THE ASSERTION IS ON EACH DATASOURCE'S HEALTH, NOT THE SERVER'S, BECAUSE THE SERVER'S
///         WAS THE ANSWER THAT LIED.</b> The first version of this branch left the Grafana pod's start
///         unproved by record and the adversarial review of #32 ran the image: Grafana 13.2.2's
///         installer tried to update the <i>bundled</i> Prometheus plugin in place on the read-only
///         root, stopped its process, failed to put it back, and <c>/api/health</c> — the readiness
///         probe — answered <c>200</c> throughout while the provisioned <c>Metrics</c> datasource
///         answered <c>Plugin not registered</c>. A test that waited for Ready would have passed. So
///         this one waits for Ready and then asks <c>/api/datasources/uid/{uid}/health</c> for both
///         datasources, which is answered by the datasource's own plugin or not at all.
///         <see cref="Grafanas.DeploymentJson" />'s remarks carry the finding;
///         <c>charts/managed/grafana/SOURCE § What was run</c> the transcript.
///     </para>
///     <para>
///         ⚠ <b>WHAT THE ASSERTION PROVES AND WHAT IT DOES NOT.</b> The rendered <c>Deployment</c> is
///         one a real kubelet turns into a Ready pod on a read-only root; the three <c>env</c>
///         references resolve against the workspace's row and <c>Secret</c>; Grafana's <c>$VAR</c>
///         interpolation puts the row's accountID and database into the two provisioned datasources
///         — each health response quotes the URL its plugin requested, and the test asserts the
///         workspace's PromQL endpoint under its accountID is in one and its <c>/sql/{database}</c>
///         path in the other; the ClickHouse plugin was fetched at its pinned version and registered;
///         the bundled Prometheus plugin is still alive. It proves <b>nothing</b> about what either
///         store would answer: the k3s has no VictoriaMetrics and no ClickHouse, so each request fails
///         on DNS after the plugin made it, and that failure is not asserted — only that neither
///         answer is <c>plugin.notRegistered</c>. The store's half is
///         <c>conformance.yaml § assertions</c>, <c>the-instance-sees-its-workspace-and-nothing-else</c>.
///     </para>
///     <para>
///         ⚠ <b>THE WORKSPACE'S TWO OBJECTS ARE WRITTEN BY THIS TEST, AND THAT IS RECORDED AS
///         OWED.</b> The suite registers one provider, so no workspace reconciler runs here, and the
///         workspace is a body property rather than an ancestor the harness would create. The test
///         first watches the kubelet hold the pod in <c>CreateContainerConfigError</c> naming the
///         row — which is the documented shape of "a Grafana created before its workspace" — and then
///         applies <see cref="MonitorWorkspaces.RowJson" /> and
///         <see cref="MonitorWorkspaces.KeySecretJson" /> for a workspace with a fresh GUID, which are
///         the documents <c>MonitorWorkspaceReconciler</c> applies.
///         <c>charts/managed/grafana/conformance.yaml § owed</c>,
///         <c>the-workspace-in-the-kubelet-test-is-the-harness-standing-in</c>.
///     </para>
///     <para>
///         ⚠ <b>ANONYMOUS VIEWING IS ON IN THIS TEST'S BODY, BECAUSE THE ADMIN CREDENTIAL CANNOT
///         TRAVEL THROUGH THE PROXY.</b> The request reaches Grafana through the API server's service
///         proxy, and the API server deletes the <c>Authorization</c> header once it has authenticated
///         a request — so a basic-auth header for Grafana's admin would be consumed as an attempt to
///         authenticate to Kubernetes and never arrive. <c>anonymousViewers</c> is the body's own
///         switch, an anonymous Viewer may ask a datasource's health (measured, SOURCE step 7), and a
///         panel embedded by URL is exactly such a visit. The admin password's correctness inside the
///         pod is <c>conformance.yaml § assertions</c>, <c>the-credential-is-minted-once</c>, and is
///         still unproved on a kubelet.
///     </para>
/// </remarks>
/// <param name="fixture">The harness.</param>
public sealed class GrafanaClusterBackedConformance(ClusterConformanceFixture<GrafanaCase> fixture)
    : ClusterConformanceTests<GrafanaCase>(fixture), IClassFixture<ClusterConformanceFixture<GrafanaCase>> {
    /// <summary>
    ///     How long the kubelet gets to pull the image and reach the container's configuration — at
    ///     which point it reports the missing row — before this is a failure.
    /// </summary>
    /// <remarks>
    ///     Five minutes, a minute more than the collector's: the kubelet pulls the image before it
    ///     resolves the container's <c>env</c>, so the first observation waits out a cold pull of the
    ///     Grafana image too.
    /// </remarks>
    static readonly TimeSpan HeldBudget = TimeSpan.FromMinutes(5);

    /// <summary>
    ///     How long the pod gets to start once the workspace's objects exist. The image is on the node
    ///     by then; what is left is the kubelet's retry, one plugin download, and Grafana's start.
    /// </summary>
    static readonly TimeSpan StartBudget = TimeSpan.FromMinutes(4);

    /// <summary>The uid of the provisioned Prometheus datasource — fixed in the provisioning file.</summary>
    const string MetricsUid = "cybercloud-metrics";

    /// <summary>The uid of the provisioned ClickHouse datasource — fixed in the provisioning file.</summary>
    const string LogsUid = "cybercloud-logs";

    [Fact]
    public async Task TheGrafanaPodStartsAndBothDatasourcesAnswer() {
        var harness = Fixture.Require(
            "that the rendered Deployment is one a real kubelet holds while its workspace's row is "
            + "missing and turns into a Ready pod once it exists, and that BOTH provisioned datasources "
            + "— the bundled Prometheus plugin on the read-only root and the ClickHouse plugin fetched "
            + "at its pinned version — answer a health check with a request at the workspace's own "
            + "coordinates rather than with 'Plugin not registered'."
        );

        var token = TestContext.Current.CancellationToken;
        const string name = "real-pod";

        var body = Grafanas.Body(ClusterConformanceHarness<GrafanaCase>.ClusterId, GrafanaCase.HarnessWorkspace, anonymousViewers: true);
        var accepted = (await WriteAsync(harness, name, body)).GetValueOrThrow();
        var status = await ConvergeAsync(harness, accepted.OperationId);

        status.State.ShouldBe(
            OperationState.Succeeded,
            $"the operation ended {status.State} against a real API server: {status.Error?.Message}"
        );

        var address = AddressOf(accepted.Resource.Id, name);
        var ns = ReconcileDriver.NamespaceFor(address);
        var objectName = Grafanas.ObjectNameOf(name);

        // ── The kubelet's first half: held, naming the row that is not there ──────────────────
        var held = await WaitForAsync(
            HeldBudget,
            () => PodDiagnostics.DecidedWaitingReasonAsync(harness.Raw, ns, Grafanas.InstanceLabel, objectName, token),
            token
        );

        held.ShouldNotBeNull(
            $"the kubelet did not decide anything about the pod behind '{objectName}' within {Seconds(HeldBudget)} s — "
            + "it should be holding it for the workspace's row, which nothing has written yet. "
            + await PodDiagnostics.DescribeAsync(harness.Raw, ns, Grafanas.InstanceLabel, objectName, token)
        );

        held.Value.Reason.ShouldBe(
            "CreateContainerConfigError",
            $"the pod behind '{objectName}' is waiting on '{held.Value.Reason}: {held.Value.Message}' — with no workspace row in the "
            + "namespace the only correct hold is the kubelet failing to resolve the row's configMapKeyRef."
        );

        held.Value.Message.ShouldContain(
            MonitorWorkspaces.RowName(GrafanaCase.HarnessWorkspaceName),
            customMessage: "the hold should name the workspace's row, which is the operator's one clue to which object is missing"
        );

        // ── The harness standing in for the workspace's reconciler ────────────────────────────
        var workspace = new ResourceId(
            ConformanceIds.Tenant,
            ConformanceIds.Subscription,
            ConformanceIds.ResourceGroup,
            MonitorWorkspaces.Type,
            GrafanaCase.HarnessWorkspaceName,
            Guid.NewGuid()
        );

        var accountId = MonitorWorkspaces.AccountId(workspace).ToString(CultureInfo.InvariantCulture);
        var database = MonitorWorkspaces.Database(workspace);

        await ApplyWorkspaceObjectsAsync(harness, ns, workspace, token);

        try {
            // ── The kubelet's second half: a Ready pod behind the Deployment ──────────────────
            var available = await WaitForAsync<bool?>(
                StartBudget,
                async () => {
                    var deployment = await harness.Raw.AppsV1.ReadNamespacedDeploymentAsync(objectName, ns, cancellationToken: token);
                    return (deployment.Status?.AvailableReplicas ?? 0) >= 1 ? true : (bool?)null;
                },
                token
            );

            available.ShouldNotBeNull(
                $"no pod behind '{objectName}' became available within {Seconds(StartBudget)} s of the workspace's row and Secret appearing. "
                + await PodDiagnostics.DescribeAsync(harness.Raw, ns, Grafanas.InstanceLabel, objectName, token)
            );

            // ── Grafana's half: each datasource answers from its own plugin ───────────────────
            using var http = harness.CreateApiServerClient();

            var metrics = await HealthAsync(http, ns, objectName, MetricsUid, token);
            var logs = await HealthAsync(http, ns, objectName, LogsUid, token);

            // Into the test's output, so a green run leaves the two answers on record beside the
            // assertions on them — the review of #32 was about a green run that recorded nothing.
            TestContext.Current.TestOutputHelper?.WriteLine($"held: {held.Value.Reason} — {held.Value.Message}");
            TestContext.Current.TestOutputHelper?.WriteLine($"{MetricsUid}: {(int)metrics.Status} {metrics.Body}");
            TestContext.Current.TestOutputHelper?.WriteLine($"{LogsUid}: {(int)logs.Status} {logs.Body}");

            // ⚠ 404 with `plugin.notRegistered` is the finding: a Ready pod whose datasource has no
            // plugin behind it. Anything else is the plugin answering — 200 would be a store that
            // exists, 400 a store that does not, and the k3s has none.
            foreach (var (uid, answer) in new[] { (MetricsUid, metrics), (LogsUid, logs) }) {
                answer.Body.ShouldNotContain(
                    "notRegistered",
                    customMessage: $"datasource '{uid}' has no plugin behind it: {(int)answer.Status} {answer.Body}. For the Prometheus one that is the "
                    + "bundled plugin killed by an in-place update on the read-only root; for the ClickHouse one it is a preinstall that did not happen."
                );

                answer.Status.ShouldNotBe(HttpStatusCode.NotFound, $"datasource '{uid}' is not provisioned at all: {answer.Body}");
                answer.Status.ShouldNotBe(HttpStatusCode.Unauthorized, $"the anonymous Viewer was refused datasource '{uid}': {answer.Body}");
                answer.Status.ShouldNotBe(HttpStatusCode.Forbidden, $"the anonymous Viewer was refused datasource '{uid}': {answer.Body}");
            }

            // ⚠ The health response quotes the URL the plugin requested, and that URL is the proof of
            // two substitutions: the kubelet's, of the row's values into the pod's environment, and
            // Grafana's, of `$CYBERCLOUD_…` into the provisioning file. A pod that started with
            // empty variables would request `/select//prometheus` and this would say so.
            metrics.Body.ShouldContain(
                MonitorWorkspaces.PromqlEndpoint(accountId),
                customMessage: $"the Prometheus datasource did not request the workspace's PromQL endpoint under accountID {accountId}: {metrics.Body}"
            );

            logs.Body.ShouldContain(
                "/sql/" + database,
                customMessage: $"the ClickHouse datasource did not request the workspace's SQL endpoint under database {database}: {logs.Body}"
            );

            await TearDownAsync(harness, name);
        } finally {
            await RemoveWorkspaceObjectsAsync(harness, ns, token);
        }
    }

    /// <summary>
    ///     Writes the workspace's row and ingest-key <c>Secret</c> into the namespace, from the same
    ///     two documents <c>MonitorWorkspaceReconciler</c> applies, as a field manager that is not the
    ///     platform's.
    /// </summary>
    /// <remarks>
    ///     A plain create rather than a server-side apply, because nothing else will ever manage these
    ///     two objects and the test removes them itself. The documents carry no <c>apiVersion</c> and
    ///     no namespace — the platform's apply path adds both — so they are added here.
    /// </remarks>
    static async Task ApplyWorkspaceObjectsAsync(ClusterConformanceHarness<GrafanaCase> harness, string ns, ResourceId workspace, CancellationToken token) {
        using var desired = JsonDocument.Parse(MonitorWorkspaces.Body(ClusterConformanceHarness<GrafanaCase>.ClusterId));

        var secret = KubernetesJson.Deserialize<V1Secret>(MonitorWorkspaces.KeySecretJson(workspace.Name, MonitorWorkspaces.GenerateIngestKey()));
        secret.ApiVersion = "v1";
        secret.Metadata.NamespaceProperty = ns;

        var row = KubernetesJson.Deserialize<V1ConfigMap>(MonitorWorkspaces.RowJson(workspace, desired.RootElement));
        row.ApiVersion = "v1";
        row.Metadata.NamespaceProperty = ns;

        // The Secret first, as the workspace's reconciler orders them: the row names its Secret.
        await harness.Raw.CoreV1.CreateNamespacedSecretAsync(secret, ns, cancellationToken: token);
        await harness.Raw.CoreV1.CreateNamespacedConfigMapAsync(row, ns, cancellationToken: token);
    }

    /// <summary>Removes the two objects <see cref="ApplyWorkspaceObjectsAsync" /> wrote, so the lifecycle test after this one finds the namespace as it expects it.</summary>
    static async Task RemoveWorkspaceObjectsAsync(ClusterConformanceHarness<GrafanaCase> harness, string ns, CancellationToken token) {
        foreach (var remove in new Func<Task>[] {
            () => harness.Raw.CoreV1.DeleteNamespacedConfigMapAsync(MonitorWorkspaces.RowName(GrafanaCase.HarnessWorkspaceName), ns, cancellationToken: token),
            () => harness.Raw.CoreV1.DeleteNamespacedSecretAsync(MonitorWorkspaces.KeySecretName(GrafanaCase.HarnessWorkspaceName), ns, cancellationToken: token)
        }) {
            try {
                await remove();
            } catch (k8s.Autorest.HttpOperationException ex) when (ex.Response?.StatusCode == HttpStatusCode.NotFound) {
                // Already gone — the namespace was torn down under it, or the create never happened.
            }
        }
    }

    /// <summary>One datasource's health, asked through the API server's service proxy.</summary>
    static async Task<(HttpStatusCode Status, string Body)> HealthAsync(HttpClient http, string ns, string objectName, string uid, CancellationToken token) {
        var url = $"api/v1/namespaces/{ns}/services/{objectName}:{Grafanas.Port}/proxy/api/datasources/uid/{uid}/health";
        using var response = await http.GetAsync(url, token);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(token));
    }

    /// <summary>Polls every three seconds until <paramref name="observe" /> answers, or the budget is spent.</summary>
    static async Task<T?> WaitForAsync<T>(TimeSpan budget, Func<Task<T?>> observe, CancellationToken token) {
        var deadline = DateTimeOffset.UtcNow + budget;

        while (true) {
            var observed = await observe();

            if (observed is not null || DateTimeOffset.UtcNow >= deadline) {
                return observed;
            }

            await Task.Delay(TimeSpan.FromSeconds(3), token);
        }
    }

    static string Seconds(TimeSpan span) => span.TotalSeconds.ToString("0", CultureInfo.InvariantCulture);
}

/// <summary>docs/plan/24 § Phase 1's exit criterion 3, against the managed Grafana type.</summary>
public sealed class GrafanaSiloKillConformance : SiloKillConformanceTests<GrafanaCase>;
