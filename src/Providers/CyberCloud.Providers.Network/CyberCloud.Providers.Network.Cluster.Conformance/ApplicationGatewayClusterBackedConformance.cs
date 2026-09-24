using CyberCloud.Cluster.Conformance;
using CyberCloud.Cluster.Conformance.Infrastructure;
using CyberCloud.Conformance;
using CyberCloud.Core.Resources;
using CyberCloud.Kubernetes.Contracts;
using CyberCloud.Providers.Network.Conformance;
using CyberCloud.Providers.Network.Contracts;
using CyberCloud.ResourceManager.Contracts;
using CyberCloud.ResourceManager.Reconcile;
using k8s;
using k8s.Models;
using Shouldly;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Network.ClusterConformance;

/// <summary>
///     The shared cluster-backed suite against <c>CyberCloud.Network/virtualNetworks/applicationGateways</c>.
/// </summary>
/// <remarks>
///     ⚠ Built-in kinds, as the load balancer's are, so the API server validates the pod template this
///     type renders — two containers, per-container users, the sysctl, the readiness probe on the
///     monitor port. What the shared suite does not do is send a request; the class below does.
/// </remarks>
/// <param name="fixture">The harness.</param>
public sealed class ApplicationGatewayLifecycleConformance(
    ClusterConformanceFixture<ApplicationGatewayCase> fixture
) : ClusterConformanceTests<ApplicationGatewayCase>(fixture),
    IClassFixture<ClusterConformanceFixture<ApplicationGatewayCase>>;

/// <summary>docs/plan/24 § Phase 1's exit criterion 3, against the application gateway.</summary>
public sealed class ApplicationGatewaySiloKillConformance : SiloKillConformanceTests<ApplicationGatewayCase>;

/// <summary>
///     Traffic through a real application gateway on a real k3s: routed by host and path to real
///     backend pods, served over HTTPS from a certificate in the harness's vault, and a SQL-injection
///     probe blocked in prevention mode and passed and logged in detection mode.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The resource goes through the real write path and the real grains</b> — a <c>PUT</c> to
///         <c>ResourceManagerService</c>, an operation driven by the silo's
///         <c>ApplicationGatewayReconciler</c>, the certificate resolved from the silo's vault, the pod
///         scheduled by the k3s kubelet with both pinned images pulled from their registries — and the
///         requests come from a third pod, over the pod network, through the gateway pod's address.
///         Nothing between the body and the 403 is a double.
///     </para>
///     <para>
///         ⚠ <b>What it cannot establish is the tenant-network half.</b> The k3s runs flannel, not
///         Kube-OVN: the pod carries the <c>logical_switch</c> and <c>ip_pool</c> annotations and
///         nothing obeys them, so it lands on the pod network and reaches its backends by pod address —
///         <c>charts/managed/application-gateway/conformance.yaml § owed</c>,
///         <c>routing-is-unproven-inside-a-real-vpc</c>.
///     </para>
///     <para>
///         ⚠ <b>The backends and the client run the gateway's own proxy image</b>, a backend answering
///         with its own name from an <c>http-request return</c> and the client running busybox
///         <c>wget</c>, so the test pulls nothing the gateway does not.
///     </para>
/// </remarks>
/// <param name="fixture">The harness.</param>
public sealed class ApplicationGatewayTrafficConformance(
    ClusterConformanceFixture<ApplicationGatewayCase> fixture
) : IClassFixture<ClusterConformanceFixture<ApplicationGatewayCase>> {
    const int MaxDrives = 40;
    const string Name = "edge";
    const string BackendConfig = "appgw-traffic-backend";

    /// <summary>A request the OWASP CRS scores as SQL injection — rule 942100, libinjection.</summary>
    const string SqlInjection = "/?id=1%27%20OR%20%271%27=%271";

    static readonly TimeSpan PodReady = TimeSpan.FromMinutes(8);

    [Fact]
    public async Task AGatewayRoutesByHostAndPathAndItsFirewallBlocksInPreventionAndLogsInDetection() {
        var harness = fixture.Require(
            "that a gateway pod rendered by the real reconciler routes a request by Host and by path to "
            + "real backend pods, serves HTTPS from a vault-held certificate, answers a SQL-injection probe "
            + "with 403 in prevention mode, and passes and logs the same probe in detection mode."
        );

        var token = TestContext.Current.CancellationToken;
        var ns = ClusterConformanceHarness<ApplicationGatewayCase>.Namespace;
        var raw = harness.Raw;

        await StartBackendsAsync(raw, ns, token);

        try {
            var a = await WaitForPodAsync(raw, ns, "appgw-traffic-a", token);
            var b = await WaitForPodAsync(raw, ns, "appgw-traffic-b", token);
            await WaitForPodAsync(raw, ns, "appgw-traffic-client", token);

            // ── The certificate, in the harness's vault under the tenant's own prefix ──────────
            var handle = $"tenants/{ConformanceIds.Tenant:D}/certs/{Name}";

            (await ClusterConformanceState<ApplicationGatewayCase>.Vault.MintAsync(handle, new Dictionary<string, string> { ["pem"] = Pem() }, token))
                .IsSuccess.ShouldBeTrue();

            // ── Prevention ─────────────────────────────────────────────────────────────────────
            var prevention = Body(a, b, ApplicationGateways.WafPrevention, handle + "#pem");
            var id = await PutAsync(harness, prevention, token);
            var gateway = await WaitForGatewayAsync(raw, ns, id, prevention, token);

            // Routing: a Host rule, a whole-segment path rule and its near miss, the catch-all.
            var byHost = await GetAsync(raw, ns, gateway, "a.example", "/b/x", token);
            byHost.ShouldContain("served-by=backend-a", customMessage: byHost);

            var byPath = await GetAsync(raw, ns, gateway, "other.example", "/b/x", token);
            byPath.ShouldContain("served-by=backend-b", customMessage: byPath);

            var nearMiss = await GetAsync(raw, ns, gateway, "other.example", "/bee", token);
            nearMiss.ShouldContain(
                "served-by=backend-a",
                customMessage: "/bee matched the rule for /b — a path prefix is matched on whole segments: " + nearMiss
            );

            // HTTPS, from the Secret the reconciler resolved out of the vault.
            var overTls = await GetAsync(raw, ns, gateway, "other.example", "/b/tls", token, https: true);
            overTls.ShouldContain("served-by=backend-b", customMessage: overTls);

            var blocked = await GetAsync(raw, ns, gateway, "other.example", SqlInjection, token);

            blocked.ShouldContain("403 Forbidden", customMessage: $"prevention mode passed a SQL-injection probe: {blocked}");
            blocked.ShouldNotContain("served-by", customMessage: "the probe reached a backend in prevention mode");

            (await LogAsync(raw, ns, gateway, "haproxy", token)).ShouldContain("waf-action:deny");

            // ── Detection: the same resource, PUT with the other mode — a rollout ───────────────
            var detection = Body(a, b, ApplicationGateways.WafDetection, handle + "#pem");
            await PutAsync(harness, detection, token);
            gateway = await WaitForGatewayAsync(raw, ns, id, detection, token);

            var passed = await GetAsync(raw, ns, gateway, "other.example", SqlInjection, token);

            passed.ShouldContain("served-by=backend-a", customMessage: $"detection mode blocked the probe: {passed}");

            var proxyLog = await LogAsync(raw, ns, gateway, "haproxy", token);
            var wafLog = await LogAsync(raw, ns, gateway, "waf", token);

            proxyLog.ShouldContain("waf-rules:942100", customMessage: $"the gateway did not log the match: {proxyLog}");
            proxyLog.ShouldNotContain("waf-action:deny");
            wafLog.ShouldContain("\"rule_id\":942100", customMessage: $"the firewall did not log the match: {wafLog}");

            // ── The action, over the harness's real dispatcher and the real API server ─────────
            var routing = (await harness.Manager.ActionAsync(
                    new() {
                        Path = ClusterConformanceHarness<ApplicationGatewayCase>.Address(Name).Path,
                        ApiVersion = ApplicationGateways.V2026,
                        Verb = WriteVerb.Post,
                        Action = ApplicationGateways.RoutingAction,
                        Caller = ClusterConformanceHarness<ApplicationGatewayCase>.Caller()
                    },
                    token
                )).GetValueOrThrow();

            var answer = JsonNode.Parse(routing.ActionResponse)!;

            answer["readyReplicas"]!.GetValue<int>().ShouldBe(1);
            answer["waf"]!.GetValue<string>().ShouldStartWith("detection, OWASP CRS 4.25");

            // ── Teardown: the pod, the configuration and the certificate all go ───────────────
            var deleted = (await harness.Manager.DeleteAsync(
                    new() {
                        Path = ClusterConformanceHarness<ApplicationGatewayCase>.Address(Name).Path,
                        ApiVersion = ApplicationGateways.V2026,
                        Caller = ClusterConformanceHarness<ApplicationGatewayCase>.Caller()
                    },
                    token
                )).GetValueOrThrow();

            (await ConvergeAsync(harness, deleted.OperationId)).State.ShouldBe(OperationState.Succeeded);

            foreach (var target in ApplicationGateways.AllObjects(ns, id)) {
                (await harness.Connection.GetAsync(target, token)).Error?.Code
                    .ShouldBe(CyberCloud.Core.ErrorCode.ResourceNotFound, $"'{target}' outlived its gateway");
            }
        } finally {
            await StopBackendsAsync(raw, ns);
        }
    }

    // ── The gateway ──────────────────────────────────────────────────────────────────────────────

    static string Body(string a, string b, string mode, string certificate) =>
        ApplicationGateways.Body(
            ClusterConformanceHarness<ApplicationGatewayCase>.ClusterId,
            rules: ["a.example/=a", "*/b=b", "*/=a"],
            members: [$"a={a}:8080", $"b={b}:8080"],
            wafMode: mode,
            certificate: certificate
        );

    static async Task<ResourceId> PutAsync(
        ClusterConformanceHarness<ApplicationGatewayCase> harness,
        string body,
        CancellationToken token
    ) {
        var accepted = (await harness.Manager.WriteAsync(
                new() {
                    Path = ClusterConformanceHarness<ApplicationGatewayCase>.Address(Name).Path,
                    ApiVersion = ApplicationGateways.V2026,
                    Verb = WriteVerb.Put,
                    Body = body,
                    Caller = ClusterConformanceHarness<ApplicationGatewayCase>.Caller()
                },
                token
            )).GetValueOrThrow();

        var status = await ConvergeAsync(harness, accepted.OperationId);
        status.State.ShouldBe(OperationState.Succeeded, $"the gateway ended {status.State}: {status.Error?.Message}");

        return ClusterConformanceHarness<ApplicationGatewayCase>.Address(Name).WithId(accepted.Resource.Id);
    }

    /// <summary>
    ///     The gateway pod carrying <paramref name="body" />'s configuration hash, once it is ready —
    ///     which in prevention mode means the firewall agent is answering too.
    /// </summary>
    static async Task<string> WaitForGatewayAsync(
        IKubernetes raw,
        string ns,
        ResourceId id,
        string body,
        CancellationToken token
    ) {
        using var desired = JsonDocument.Parse(body);
        var hash = ApplicationGateways.ConfigHash(desired.RootElement, ApplicationGateways.NoResolution);
        var selector = "app.kubernetes.io/instance=" + ApplicationGateways.ObjectNameOf(id);
        var deadline = DateTimeOffset.UtcNow + PodReady;
        var last = "no pod";

        while (DateTimeOffset.UtcNow < deadline) {
            var pods = await raw.CoreV1.ListNamespacedPodAsync(ns, labelSelector: selector, cancellationToken: token);

            foreach (var pod in pods.Items) {
                pod.Metadata.Annotations.TryGetValue(ApplicationGateways.ConfigChecksumAnnotation, out var carried);
                // ⚠ Every container's state in the message: "Running and never Ready" is the symptom of
                // three different faults here, and the first run of this test hit one of them.
                last = $"{pod.Metadata.Name} {pod.Status?.Phase} hash={carried} containers: "
                    + string.Join(
                        "; ",
                        pod.Status?.ContainerStatuses?.Select(static x =>
                            $"{x.Name} ready={x.Ready} restarts={x.RestartCount} "
                            + $"waiting={x.State?.Waiting?.Reason} last={x.LastState?.Terminated?.Reason}:"
                            + $"{x.LastState?.Terminated?.Message}"
                        ) ?? []
                    );

                if (carried == hash
                    && pod.Metadata.DeletionTimestamp is null
                    && pod.Status?.Conditions?.Any(static x => x.Type == "Ready" && x.Status == "True") == true) {
                    return pod.Status.PodIP;
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(2), token);
        }

        var logs = new StringBuilder();

        foreach (var pod in (await raw.CoreV1.ListNamespacedPodAsync(ns, labelSelector: selector, cancellationToken: token)).Items) {
            foreach (var container in (string[])["haproxy", "waf"]) {
                try {
                    await using var stream = await raw.CoreV1.ReadNamespacedPodLogAsync(
                        pod.Metadata.Name,
                        ns,
                        container: container,
                        tailLines: 20,
                        cancellationToken: token
                    );
                    using var reader = new StreamReader(stream);
                    logs.Append($"\n--- {container} ---\n").Append(await reader.ReadToEndAsync(token));
                } catch (k8s.Autorest.HttpOperationException ex) {
                    logs.Append($"\n--- {container}: {ex.Response?.StatusCode} ---");
                }
            }
        }

        throw new TimeoutException($"no gateway pod carrying {hash} became ready in {PodReady}; last seen: {last}{logs}");
    }

    // ── The backends and the client ──────────────────────────────────────────────────────────────

    static async Task StartBackendsAsync(IKubernetes raw, string ns, CancellationToken token) {
        await StopBackendsAsync(raw, ns);

        await raw.CoreV1.CreateNamespacedConfigMapAsync(
            new V1ConfigMap {
                Metadata = new() { Name = BackendConfig },
                Data = new Dictionary<string, string> {
                    ["haproxy.cfg"] = """
                        global
                          log stdout format raw local0 info
                        defaults
                          mode http
                          timeout connect 5s
                          timeout client 60s
                          timeout server 60s
                        frontend web
                          bind :8080
                          http-request return status 200 content-type text/plain lf-string "served-by=%[env(NAME)] path=%[path]\n"
                        """
                        // ⚠ HAProxy refuses a file whose last line has no LF — "Missing LF on last line, file
                        // might have been truncated" — and a raw string literal ends without one: the
                        // backends crash-looped on the first k3s runs, read off `kubectl logs --previous`.
                        + "\n"
                }
            },
            ns,
            cancellationToken: token
        );

        foreach (var (pod, role) in ((string, string)[])[
                     ("appgw-traffic-a", "backend-a"),
                     ("appgw-traffic-b", "backend-b"),
                     ("appgw-traffic-client", "client")
                 ]) {
            var container = new V1Container {
                Name = "main",
                Image = ApplicationGateways.ProxyImage,
                Env = [new() { Name = "NAME", Value = role }],
                VolumeMounts = [new() { Name = "config", MountPath = "/usr/local/etc/haproxy", ReadOnlyProperty = true }]
            };

            if (role == "client") {
                container.Command = ["sleep", "3600"];
            }

            await raw.CoreV1.CreateNamespacedPodAsync(
                new V1Pod {
                    Metadata = new() { Name = pod, Labels = new Dictionary<string, string> { ["appgw-traffic"] = role } },
                    Spec = new() {
                        Containers = [container],
                        Volumes = [new() { Name = "config", ConfigMap = new() { Name = BackendConfig } }],
                        TerminationGracePeriodSeconds = 0
                    }
                },
                ns,
                cancellationToken: token
            );
        }
    }

    static async Task StopBackendsAsync(IKubernetes raw, string ns) {
        foreach (var pod in (string[])["appgw-traffic-a", "appgw-traffic-b", "appgw-traffic-client"]) {
            try {
                await raw.CoreV1.DeleteNamespacedPodAsync(pod, ns, gracePeriodSeconds: 0);
            } catch (k8s.Autorest.HttpOperationException ex) when (ex.Response?.StatusCode == System.Net.HttpStatusCode.NotFound) {
                // Already gone, which is what this wants.
            }
        }

        try {
            await raw.CoreV1.DeleteNamespacedConfigMapAsync(BackendConfig, ns);
        } catch (k8s.Autorest.HttpOperationException ex) when (ex.Response?.StatusCode == System.Net.HttpStatusCode.NotFound) {
            // Already gone.
        }
    }

    static async Task<string> WaitForPodAsync(IKubernetes raw, string ns, string name, CancellationToken token) {
        var deadline = DateTimeOffset.UtcNow + PodReady;
        var last = "not read yet";

        while (DateTimeOffset.UtcNow < deadline) {
            var pod = await raw.CoreV1.ReadNamespacedPodAsync(name, ns, cancellationToken: token);

            if (pod.Status?.ContainerStatuses?.All(static x => x.Ready) == true && pod.Status.PodIP is { Length: > 0 } ip) {
                return ip;
            }

            last = $"{pod.Status?.Phase} "
                + string.Join("; ", pod.Status?.Conditions?.Select(static x => $"{x.Type}={x.Status} {x.Reason} {x.Message}") ?? [])
                + " | "
                + string.Join(
                    "; ",
                    pod.Status?.ContainerStatuses?.Select(static x =>
                        $"{x.Name} ready={x.Ready} waiting={x.State?.Waiting?.Reason}:{x.State?.Waiting?.Message} "
                        + $"restarts={x.RestartCount}"
                    ) ?? []
                );

            await Task.Delay(TimeSpan.FromSeconds(2), token);
        }

        throw new TimeoutException($"'{name}' did not become ready in {PodReady}: {last}");
    }

    // ── Requests and logs ────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     One request from the client pod through the gateway, as busybox <c>wget</c> reports it: the
    ///     body on success, the status line on a refusal — both, merged, so an assertion can read either.
    /// </summary>
    static async Task<string> GetAsync(
        IKubernetes raw,
        string ns,
        string gateway,
        string host,
        string path,
        CancellationToken token,
        bool https = false
    ) {
        // ⚠ Every value here is the test's own — a literal host, a percent-encoded path, a pod address
        // — so the single quotes cannot be closed by what they hold.
        var url = https ? $"https://{gateway}:443{path}" : $"http://{gateway}:80{path}";
        var command = $"wget -q -S -O - -T 10 --no-check-certificate --header 'Host: {host}' '{url}' 2>&1; true";
        var output = new StringBuilder();

        await raw.NamespacedPodExecAsync(
            "appgw-traffic-client",
            ns,
            "main",
            ["sh", "-c", command],
            false,
            async (_, stdOut, stdErr) => {
                using var outReader = new StreamReader(stdOut);
                using var errReader = new StreamReader(stdErr);
                output.Append(await outReader.ReadToEndAsync(token));
                output.Append(await errReader.ReadToEndAsync(token));
            },
            token
        );

        // ⚠ A 503 from this gateway is one of three causes — no healthy member, no route, or the
        // firewall not answering in time — and only the proxy's own log line says which, so a 503 is
        // returned with that log attached rather than as a bare status line.
        if (output.ToString().Contains(" 503 ", StringComparison.Ordinal)) {
            output.Append("\n--- haproxy ---\n").Append(await LogAsync(raw, ns, gateway, "haproxy", token));
            output.Append("\n--- waf ---\n").Append(await LogAsync(raw, ns, gateway, "waf", token));
        }

        return output.ToString();
    }

    static async Task<string> LogAsync(IKubernetes raw, string ns, string gatewayIp, string container, CancellationToken token) {
        var pods = await raw.CoreV1.ListNamespacedPodAsync(ns, cancellationToken: token);
        var pod = pods.Items.Single(x => x.Status?.PodIP == gatewayIp && x.Metadata.DeletionTimestamp is null);

        // ⚠ A second's grace: HAProxy writes its access line when the response completes, and the
        // agent logs its match on the same request — both are asynchronous to the client's read.
        await Task.Delay(TimeSpan.FromSeconds(1), token);

        await using var stream = await raw.CoreV1.ReadNamespacedPodLogAsync(pod.Metadata.Name, ns, container: container, cancellationToken: token);
        using var reader = new StreamReader(stream);

        return await reader.ReadToEndAsync(token);
    }

    static async Task<OperationStatus> ConvergeAsync(ClusterConformanceHarness<ApplicationGatewayCase> harness, Guid operationId) {
        var operation = harness.Operation(ConformanceIds.Tenant, operationId);
        OperationStatus? last = null;

        for (var i = 0; i < MaxDrives; i++) {
            last = (await operation.DriveAsync()).GetValueOrThrow();

            if (last.IsTerminal) {
                return last;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        }

        last.ShouldNotBeNull();
        return last;
    }

    static string Pem() {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=a.example", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));

        return certificate.ExportCertificatePem() + "\n" + key.ExportPkcs8PrivateKeyPem() + "\n";
    }
}
