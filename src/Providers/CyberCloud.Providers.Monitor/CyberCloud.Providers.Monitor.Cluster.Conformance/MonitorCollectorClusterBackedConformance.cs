using CyberCloud.Cluster.Conformance;
using CyberCloud.Cluster.Conformance.Infrastructure;
using CyberCloud.Providers.Monitor.Conformance;
using CyberCloud.Providers.Monitor.Contracts;
using CyberCloud.ResourceManager.Contracts;
using CyberCloud.ResourceManager.Reconcile;
using k8s;
using Shouldly;
using System.Globalization;
using System.Net;
using System.Text;

namespace CyberCloud.Providers.Monitor.ClusterConformance;

/// <summary>
///     The cluster-backed suite, run against the collector type — plus the one assertion on this
///     type that reads what a <b>kubelet</b> did rather than what the API server holds.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THE FIRST TEST IN ANY <c>.Cluster.Conformance</c> PROJECT THAT WAITS FOR A POD</b>
///         — <c>GrafanaClusterBackedConformance</c> beside it is the second, and it goes one step
///         further into the pod for a reason its remarks give.
///         Every shared criterion reads objects back off the API server, and every family before this
///         one was content with that: a <c>StatefulSet</c> an operator would have reconciled, a
///         <c>VMUser</c> vmauth would have resolved, none of which runs on the harness's k3s. This
///         type is different in the one way that matters — its product <i>is</i> the pod — so
///         "converged" meaning "three objects read back" would leave the whole product unproved. The
///         k3s has a kubelet (docs/plan/23 § The lane that needs a kubelet), so the pod is waited for,
///         and then asked a question through the API server's service proxy.
///     </para>
///     <para>
///         ⚠ <b>WHAT THE ASSERTION PROVES AND WHAT IT DOES NOT.</b> The rendered <c>Deployment</c> is
///         one a real kubelet turns into a Running, Ready pod: the image pulls by digest, the
///         non-root uid is accepted, the read-only root filesystem is enough, the three
///         <c>env</c> references resolve against the workspace ancestor's row and <c>Secret</c>, the
///         mounted configuration is one the collector accepts, and the OTLP/HTTP receiver answers an
///         export <c>200</c>. It proves <b>nothing</b> about where the export goes: the k3s has no
///         VictoriaMetrics and no ClickHouse, both exporters point at hosts that do not resolve, and
///         the batch processor is what lets the receiver say <c>200</c> in front of them. That half is
///         <c>conformance.yaml § assertions</c>, <c>an-export-lands-in-the-workspace</c>, behind the
///         same wall the workspace's round trip is.
///     </para>
///     <para>
///         ⚠ <b>THROUGH THE API SERVER'S SERVICE PROXY, WITH THE HARNESS'S OWN HANDLER.</b> The
///         k3s exposes one port to this process — the API server's — so the collector's
///         <c>ClusterIP</c> is reached at
///         <c>/api/v1/namespaces/{ns}/services/{name}:4318/proxy/v1/logs</c>, which the API server
///         forwards inside the container's network. The generated client's <c>ConnectPost…Proxy</c>
///         methods carry no body, so the request goes through
///         <c>ClusterConformanceHarness.CreateApiServerClient</c> — a plain <c>HttpClient</c> holding the
///         kubeconfig's client certificate — which exists for this test and any later one like it.
///     </para>
/// </remarks>
/// <param name="fixture">The harness.</param>
public sealed class MonitorCollectorClusterBackedConformance(ClusterConformanceFixture<MonitorCollectorCase> fixture)
    : ClusterConformanceTests<MonitorCollectorCase>(fixture), IClassFixture<ClusterConformanceFixture<MonitorCollectorCase>> {
    /// <summary>How long a kubelet gets to pull the image and start the pod before this is a failure.</summary>
    /// <remarks>
    ///     Four minutes: the contrib image is a few hundred megabytes and this k3s pulls it cold on
    ///     the first run, over whatever the machine's connection is. A start that takes longer than
    ///     this on a warm node is a pod that is not going to start.
    /// </remarks>
    static readonly TimeSpan PodStartBudget = TimeSpan.FromMinutes(4);

    /// <summary>
    ///     One OTLP/HTTP logs export, in the JSON encoding the receiver accepts with
    ///     <c>Content-Type: application/json</c>.
    /// </summary>
    const string OneLogRecord = """
        {"resourceLogs":[{"resource":{"attributes":[{"key":"service.name","value":{"stringValue":"cluster-conformance"}}]},
        "scopeLogs":[{"logRecords":[{"timeUnixNano":"1700000000000000000","severityText":"INFO","body":{"stringValue":"the collector accepted this"}}]}]}]}
        """;

    [Fact]
    public async Task TheCollectorPodStartsAndAcceptsAnOtlpExport() {
        var harness = Fixture.Require(
            "that the rendered Deployment is one a real kubelet turns into a Ready pod — the image "
            + "pulls by digest, the three env references resolve against the workspace's row and "
            + "Secret, the mounted configuration is one the collector accepts — and that an OTLP/HTTP "
            + "export POSTed to the Service is answered 200."
        );

        var token = TestContext.Current.CancellationToken;
        const string name = "real-pod";

        var accepted = (await WriteAsync(harness, name)).GetValueOrThrow();
        var status = await ConvergeAsync(harness, accepted.OperationId);

        status.State.ShouldBe(
            OperationState.Succeeded,
            $"the operation ended {status.State} against a real API server: {status.Error?.Message}"
        );

        var address = AddressOf(accepted.Resource.Id, name);
        var ns = ReconcileDriver.NamespaceFor(address);
        var objectName = MonitorCollectors.ObjectNameOf(address);

        // ── The kubelet's half: a Ready pod behind the Deployment ─────────────────────────────
        var deadline = DateTimeOffset.UtcNow + PodStartBudget;
        var available = 0;

        while (DateTimeOffset.UtcNow < deadline) {
            var deployment = await harness.Raw.AppsV1.ReadNamespacedDeploymentAsync(objectName, ns, cancellationToken: token);
            available = deployment.Status?.AvailableReplicas ?? 0;

            if (available >= 1) {
                break;
            }

            await Task.Delay(TimeSpan.FromSeconds(3), token);
        }

        available.ShouldBeGreaterThanOrEqualTo(
            1,
            $"no pod behind '{objectName}' became available within {PodStartBudget.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} s. "
            + await PodDiagnostics.DescribeAsync(harness.Raw, ns, MonitorCollectors.InstanceLabel, objectName, token)
        );

        // ── The collector's half: an export answered 200 through the API server's proxy ───────
        using var http = harness.CreateApiServerClient();

        var url = $"api/v1/namespaces/{ns}/services/{objectName}:{MonitorCollectors.OtlpHttpPort}/proxy/v1/logs";
        using var content = new StringContent(OneLogRecord, Encoding.UTF8, "application/json");
        using var response = await http.PostAsync(url, content, token);
        var body = await response.Content.ReadAsStringAsync(token);

        response.StatusCode.ShouldBe(
            HttpStatusCode.OK,
            $"the OTLP/HTTP receiver behind '{objectName}' answered {(int)response.StatusCode} to an export through "
            + $"{url}: {body}. A 401 or 403 is the proxy refusing this process, not the collector; a 404 is the "
            + "Service not addressing the pod; anything else is the collector's own answer."
        );

        // ⚠ The receiver's success body is `{"partialSuccess":{}}` — an empty partialSuccess is
        // upstream's spelling of "everything was accepted". A body naming rejected records would be a
        // collector that took the request and dropped part of it, which is not acceptance.
        body.ShouldContain("partialSuccess");
        body.ShouldNotContain("rejected");

        await TearDownAsync(harness, name);
    }
}

/// <summary>docs/plan/24 § Phase 1's exit criterion 3, against the collector type.</summary>
public sealed class MonitorCollectorSiloKillConformance : SiloKillConformanceTests<MonitorCollectorCase>;
