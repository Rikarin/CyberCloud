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
using System.Collections.Immutable;
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

            // ── Custom denies hold for every spelling routing or a backend reads as the same ─────
            //
            // ⚠ #31's review: the host rule compared the raw Host and the path rule a path Go's
            // url.Parse had already mangled, so these spellings evaded a deny and were routed. Each
            // request carries a unique probe in its query so its own access line can be found, and
            // that line has to name the custom rule — a 403 the CRS gave for some other reason would
            // prove nothing about the rule.
            foreach (var (host, path, rule) in ((string, string, string)[])[
                         ("blocked.example", "/?probe=h1", "190001"),
                         ("BLOCKED.Example", "/?probe=h2", "190001"),
                         ("blocked.example:80", "/?probe=h3", "190001"),
                         ("blocked.example.", "/?probe=h4", "190001"),
                         ("other.example", "/secret?probe=p1", "190002"),
                         ("other.example", "//secret?probe=p2", "190002"),
                         ("other.example", "/%73ecret?probe=p3", "190002"),
                         ("other.example", "/./secret?probe=p4", "190002"),
                         ("other.example", "/b/../secret?probe=p5", "190002")
                     ]) {
                var denied = await GetAsync(raw, ns, gateway, host, path, token);

                denied.ShouldContain("403 Forbidden", customMessage: $"Host '{host}' {path} evaded a custom deny: {denied}");
                denied.ShouldNotContain("served-by", customMessage: $"Host '{host}' {path} reached a backend");

                var probe = path[(path.IndexOf('?', StringComparison.Ordinal) + 1)..];
                var line = (await LogAsync(raw, ns, gateway, "haproxy", token))
                    .Split('\n')
                    .LastOrDefault(x => x.Contains(probe, StringComparison.Ordinal));

                line.ShouldNotBeNull($"no access line for {probe}");
                line.ShouldMatch("waf-rules:[0-9,]*" + rule, $"the 403 for {probe} was not custom rule {rule}: {line}");
            }

            // And a name that only starts like the denied host is routed as before.
            (await GetAsync(raw, ns, gateway, "blocked.example.org", "/", token))
                .ShouldContain("served-by=backend-a");

            // ── Fail closed: the same configuration with no agent answering is a 503 ──────────
            await FailsClosedWithoutItsAgentAsync(raw, ns, id, token);

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

            // ── A member that stops answering its probe leaves its pool ────────────────────────
            //
            // Pool b's one member is taken away; the rendered `check inter 5000ms fall 3` has to take
            // it out, after which HAProxy answers 503 for the pool rather than holding the request on
            // a dead address — and says so in its own log.
            await raw.CoreV1.DeleteNamespacedPodAsync("appgw-traffic-b", ns, gracePeriodSeconds: 0, cancellationToken: token);

            var removed = await PollAsync(
                () => GetAsync(raw, ns, gateway, "other.example", "/b/x", token, withLogs: false),
                static x => x.Contains(" 503 ", StringComparison.Ordinal),
                TimeSpan.FromSeconds(90),
                token
            );

            removed.ShouldContain(" 503 ", customMessage: $"pool b kept a member that no longer answers its probe: {removed}");
            (await LogAsync(raw, ns, gateway, "haproxy", token)).ShouldContain("pool-b/m2 is DOWN");

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
            // Rule 1 is 190001 and rule 2 is 190002 — ApplicationGateways.Directives numbers them.
            customRules: ["deny host blocked.example", "deny path /secret"],
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

    // ── Fail closed ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     The gateway's own pod template, run as a bare pod with the agent's container taken out: the
    ///     configuration the reconciler rendered, served by the pinned HAProxy with nothing on
    ///     <c>127.0.0.1:9000</c>, answers 503 and reports itself unready.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The agent removed rather than stopped</b>, because it cannot be stopped from outside in
    ///     a way a test can hold: the pod's restart policy brings a killed agent back within a second,
    ///     and SIGSTOP is refused for a namespace's PID 1 from inside it. With no agent at all, what is
    ///     under test is exactly the rendered <c>set-on-error</c> and <c>deny_status 503</c> lines and
    ///     the <c>monitor fail</c> — until #31's review they were asserted only as text.
    /// </remarks>
    static async Task FailsClosedWithoutItsAgentAsync(IKubernetes raw, string ns, ResourceId id, CancellationToken token) {
        const string pod = "appgw-traffic-noagent";

        var deployment = await raw.AppsV1.ReadNamespacedDeploymentAsync(ApplicationGateways.ObjectNameOf(id), ns, cancellationToken: token);
        var spec = deployment.Spec.Template.Spec;

        spec.Containers = [.. spec.Containers.Where(static x => x.Name == "haproxy")];
        spec.Volumes = [.. spec.Volumes.Where(static x => x.Name != "tmp")];

        await DeletePodAsync(raw, ns, pod, token);

        await raw.CoreV1.CreateNamespacedPodAsync(
            new V1Pod {
                // ⚠ Not the Deployment's labels, so neither the Deployment nor WaitForGatewayAsync
                // counts this pod as the gateway's.
                Metadata = new() { Name = pod, Labels = new Dictionary<string, string> { ["appgw-traffic"] = "noagent" } },
                Spec = spec
            },
            ns,
            cancellationToken: token
        );

        try {
            var ip = await WaitForRunningAsync(raw, ns, pod, token);

            var answer = await PollAsync(
                () => GetAsync(raw, ns, ip, "other.example", "/b/x", token, withLogs: false),
                static x => x.Contains(" 503 ", StringComparison.Ordinal),
                TimeSpan.FromSeconds(60),
                token
            );

            answer.ShouldContain(" 503 ", customMessage: $"a prevention gateway with no firewall answering did not fail closed: {answer}");
            answer.ShouldNotContain("served-by", customMessage: "a request passed a firewall that was not there");

            // ⚠ Polled: the agent's server is taken out by its own check (`inter 2s`, three falls),
            // and until then the monitor counts it — the request above failed on the SPOE connect.
            var monitor = await PollAsync(
                () => GetAsync(
                    raw,
                    ns,
                    ip,
                    "any",
                    ApplicationGateways.ReadinessPath,
                    token,
                    withLogs: false,
                    port: ApplicationGateways.ReadinessPort
                ),
                static x => x.Contains(" 503 ", StringComparison.Ordinal),
                TimeSpan.FromSeconds(60),
                token
            );

            monitor.ShouldContain(" 503 ", customMessage: $"the readiness monitor reported a gateway with no firewall as up: {monitor}");

            // ⚠ POLLED, AND NOT READ ONCE AFTER A FIXED WAIT — measured on the second k3s run: the pod
            // was Ready twelve seconds in. HAProxy starts a checked server UP, so the monitor answers 200
            // until the agent's server has failed three checks (about six seconds), the kubelet's first
            // probe can land inside that, and three failed probes five seconds apart are what take Ready
            // away again. What the monitor promises is that the pod does not STAY ready.
            var unready = false;
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(90);

            while (!unready && DateTimeOffset.UtcNow < deadline) {
                var read = await raw.CoreV1.ReadNamespacedPodAsync(pod, ns, cancellationToken: token);

                unready = read.Status?.Conditions?.Any(static x => x.Type == "Ready" && x.Status == "False") ?? false;

                if (!unready) {
                    await Task.Delay(TimeSpan.FromSeconds(2), token);
                }
            }

            unready.ShouldBeTrue("the kubelet went on counting a gateway with no firewall as ready");
        } finally {
            await DeletePodAsync(raw, ns, pod, CancellationToken.None);
        }
    }

    static async Task DeletePodAsync(IKubernetes raw, string ns, string name, CancellationToken token) {
        try {
            await raw.CoreV1.DeleteNamespacedPodAsync(name, ns, gracePeriodSeconds: 0, cancellationToken: token);
        } catch (k8s.Autorest.HttpOperationException ex) when (ex.Response?.StatusCode == System.Net.HttpStatusCode.NotFound) {
            return;
        }

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(2);

        while (DateTimeOffset.UtcNow < deadline) {
            try {
                await raw.CoreV1.ReadNamespacedPodAsync(name, ns, cancellationToken: token);
            } catch (k8s.Autorest.HttpOperationException ex) when (ex.Response?.StatusCode == System.Net.HttpStatusCode.NotFound) {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), token);
        }
    }

    static async Task<string> WaitForRunningAsync(IKubernetes raw, string ns, string name, CancellationToken token) {
        var deadline = DateTimeOffset.UtcNow + PodReady;
        var last = "not read yet";

        while (DateTimeOffset.UtcNow < deadline) {
            var pod = await raw.CoreV1.ReadNamespacedPodAsync(name, ns, cancellationToken: token);

            if (pod.Status?.ContainerStatuses?.All(static x => x.State?.Running is not null) == true
                && pod.Status.PodIP is { Length: > 0 } ip) {
                return ip;
            }

            last = $"{pod.Status?.Phase} "
                + string.Join(
                    "; ",
                    pod.Status?.ContainerStatuses?.Select(static x =>
                        $"{x.Name} waiting={x.State?.Waiting?.Reason}:{x.State?.Waiting?.Message} restarts={x.RestartCount}"
                    ) ?? []
                );

            await Task.Delay(TimeSpan.FromSeconds(2), token);
        }

        throw new TimeoutException($"'{name}' did not start running in {PodReady}: {last}");
    }

    static async Task<string> PollAsync(Func<Task<string>> attempt, Func<string, bool> done, TimeSpan within, CancellationToken token) {
        var deadline = DateTimeOffset.UtcNow + within;
        var last = string.Empty;

        while (DateTimeOffset.UtcNow < deadline) {
            last = await attempt();

            if (done(last)) {
                return last;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), token);
        }

        return last;
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
        bool https = false,
        bool withLogs = true,
        int port = 0
    ) {
        // ⚠ Every value here is the test's own — a literal host, a percent-encoded path, a pod address
        // — so the single quotes cannot be closed by what they hold.
        var url = https
            ? $"https://{gateway}:{(port > 0 ? port : 443)}{path}"
            : $"http://{gateway}:{(port > 0 ? port : 80)}{path}";
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
        if (withLogs && output.ToString().Contains(" 503 ", StringComparison.Ordinal)) {
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

/// <summary>
///     An application gateway and a load balancer of one name in one network, both converged on a real
///     k3s, each with its own objects — and neither's teardown reaching the other's.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The collision #31's review measured.</b> Both types render a <c>ConfigMap</c> and a
///         <c>Deployment</c> into the resource group's namespace under one field manager, and both used
///         to name them <c>{network}-{name}</c>: the second apply took the first's objects over without
///         a conflict, and deleting the gateway deleted the balancer's pod. The gateway's names are
///         <c>{network}.{name}</c> now (<see cref="ApplicationGateways.ObjectNameOf" />), and a delete
///         refuses an object labelled for another resource (<c>OwnedDelete</c>) — this class holds both.
///     </para>
///     <para>
///         ⚠ Not in the shared suite, because the shared suite is one type's — and so is this harness's
///         silo, which registers the reconciler of the type under test and no other (measured: a
///         balancer written through it failed <i>"LoadBalancerReconciler … is not registered in the
///         container"</i>). The gateway goes through the real write path; the balancer is converged by
///         its own reconciler, driven by hand against the same real connection, the way the shared
///         suite drives a pass for its drift assertions.
///     </para>
/// </remarks>
/// <param name="fixture">The harness.</param>
public sealed class ApplicationGatewayBesideALoadBalancerConformance(
    ClusterConformanceFixture<ApplicationGatewayCase> fixture
) : IClassFixture<ClusterConformanceFixture<ApplicationGatewayCase>> {
    const string Name = "twin";
    const int MaxDrives = 40;

    [Fact]
    public async Task AGatewayAndABalancerOfOneNameInOneNetworkKeepTheirOwnObjectsAndTeardowns() {
        var harness = fixture.Require(
            "that an application gateway and a load balancer with the same name in the same network render "
            + "distinct objects on a real API server, and that removing one leaves the other's in place."
        );

        var token = TestContext.Current.CancellationToken;
        var ns = ClusterConformanceHarness<ApplicationGatewayCase>.Namespace;
        var cluster = ClusterConformanceHarness<ApplicationGatewayCase>.ClusterId;

        var gatewayAddress = ClusterConformanceHarness<ApplicationGatewayCase>.Address(Name);
        var balancer = new ResourceId(
            gatewayAddress.TenantId,
            gatewayAddress.SubscriptionId,
            gatewayAddress.ResourceGroup,
            LoadBalancers.Type,
            Name,
            Guid.NewGuid(),
            ProviderTestCluster<ApplicationGatewayCase>.AncestorPath
        );

        using var balancerBody = JsonDocument.Parse(LoadBalancers.Body(cluster));

        try {
            (await DriveBalancerAsync(harness, balancer, balancerBody.RootElement, delete: false))
                .Kind.ShouldBe(ReconcileOutcomeKind.Converged, "the load balancer did not converge");

            var gateway = await PutAsync(harness, gatewayAddress, ApplicationGateways.V2026, ApplicationGateways.Body(cluster), token);

            var balancerObjects = LoadBalancers.Objects(ns, balancer);
            var gatewayObjects = ApplicationGateways.Objects(ns, gateway);

            gatewayObjects.Select(static x => (x.Kind.Kind, x.Name))
                .Intersect(balancerObjects.Select(static x => (x.Kind.Kind, x.Name)))
                .ShouldBeEmpty("the two types still render one object name");

            // Each object on the API server carries its own resource's id — the second apply did not
            // take the first's over.
            foreach (var (objects, owner) in ((ImmutableArray<ObjectRef>, ResourceId)[])[
                         (balancerObjects, balancer), (gatewayObjects, gateway)
                     ]) {
                foreach (var target in objects) {
                    (await ResourceIdLabelAsync(harness, target, token))
                        .ShouldBe(KubeLabels.GuidValue(owner.Id), $"'{target}' is not {owner.Type}'s own");
                }
            }

            // ⚠ The platform's half: a delete made on the gateway's behalf that names the balancer's
            // Deployment — the old collision, by hand — is refused, and the Deployment stays.
            var balancerDeployment = balancerObjects.Single(static x => x.Kind.Kind == "Deployment");

            var reached = await KubeCommand.For(harness.Connection)
                .WithTenantId(gateway.TenantId)
                .WithResourceId(gateway)
                .InNamespace(ns)
                .WithKind(LoadBalancers.DeploymentKind)
                .WithApiVersion(ApplicationGateways.V2026)
                .ObjectJson(new JsonObject { ["metadata"] = new JsonObject { ["name"] = balancerDeployment.Name } }.ToJsonString())
                .DeleteAsync(CascadePolicy.Foreground, token);

            reached.Error.ShouldNotBeNull("a gateway's delete removed the load balancer's Deployment");
            reached.Error.Code.ShouldBe(CyberCloud.Core.ErrorCode.Conflict);
            (await harness.Connection.GetAsync(balancerDeployment, token)).IsSuccess.ShouldBeTrue();

            // And the gateway's real teardown leaves the balancer's objects where they were.
            await DeleteAsync(harness, gatewayAddress, ApplicationGateways.V2026, token);

            foreach (var target in ApplicationGateways.AllObjects(ns, gateway)) {
                (await harness.Connection.GetAsync(target, token)).Error?.Code
                    .ShouldBe(CyberCloud.Core.ErrorCode.ResourceNotFound, $"'{target}' outlived its gateway");
            }

            foreach (var target in balancerObjects) {
                (await harness.Connection.GetAsync(target, token)).IsSuccess
                    .ShouldBeTrue($"the gateway's teardown removed the load balancer's '{target}'");
            }

            (await DriveBalancerAsync(harness, balancer, balancerBody.RootElement, delete: true))
                .Kind.ShouldBe(ReconcileOutcomeKind.Converged, "the load balancer's own teardown did not converge");
        } finally {
            // Best effort, and idempotent: whatever the body above left, neither type's objects outlive
            // the test to meet the lifecycle classes' resources.
            await DeleteAsync(harness, gatewayAddress, ApplicationGateways.V2026, CancellationToken.None);
            await DriveBalancerAsync(harness, balancer, balancerBody.RootElement, delete: true);
        }
    }

    /// <summary>Runs the balancer's own reconciler against the real connection until a pass is terminal.</summary>
    static async Task<ReconcileOutcome> DriveBalancerAsync(
        ClusterConformanceHarness<ApplicationGatewayCase> harness,
        ResourceId balancer,
        JsonElement desired,
        bool delete
    ) {
        var reconciler = new LoadBalancerReconciler(harness.Clock);
        ReconcileOutcome? last = null;

        for (var i = 0; i < MaxDrives; i++) {
            var context = new ReconcileContext(
                balancer,
                LoadBalancers.V2026,
                desired,
                null,
                ReconcileDriver.NamespaceFor(balancer),
                harness.Connection,
                ClusterConformanceState<ApplicationGatewayCase>.Vault,
                new RecordingLog()
            );

            last = delete
                ? await reconciler.DeleteAsync(context, TestContext.Current.CancellationToken)
                : await reconciler.ReconcileAsync(context, TestContext.Current.CancellationToken);

            if (last.Kind != ReconcileOutcomeKind.InProgress) {
                return last;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        }

        last.ShouldNotBeNull();
        return last;
    }

    static async Task<ResourceId> PutAsync(
        ClusterConformanceHarness<ApplicationGatewayCase> harness,
        ResourceId address,
        string apiVersion,
        string body,
        CancellationToken token
    ) {
        var accepted = (await harness.Manager.WriteAsync(
                new() {
                    Path = address.Path,
                    ApiVersion = apiVersion,
                    Verb = WriteVerb.Put,
                    Body = body,
                    Caller = ClusterConformanceHarness<ApplicationGatewayCase>.Caller()
                },
                token
            )).GetValueOrThrow();

        var status = await ConvergeAsync(harness, accepted.OperationId, token);
        status.State.ShouldBe(OperationState.Succeeded, $"'{address.Path}' ended {status.State}: {status.Error?.Message}");

        return address.WithId(accepted.Resource.Id);
    }

    static async Task DeleteAsync(
        ClusterConformanceHarness<ApplicationGatewayCase> harness,
        ResourceId address,
        string apiVersion,
        CancellationToken token
    ) {
        var deleted = await harness.Manager.DeleteAsync(
            new() { Path = address.Path, ApiVersion = apiVersion, Caller = ClusterConformanceHarness<ApplicationGatewayCase>.Caller() },
            token
        );

        // Already gone is the end state a teardown in `finally` wants.
        if (deleted.TryGetError(out var error)) {
            error.Code.ShouldBe(CyberCloud.Core.ErrorCode.ResourceNotFound, error.Message);

            return;
        }

        var status = await ConvergeAsync(harness, deleted.GetValueOrThrow().OperationId, token);
        status.State.ShouldBe(OperationState.Succeeded, $"deleting '{address.Path}' ended {status.State}: {status.Error?.Message}");
    }

    static async Task<OperationStatus> ConvergeAsync(
        ClusterConformanceHarness<ApplicationGatewayCase> harness,
        Guid operationId,
        CancellationToken token
    ) {
        var operation = harness.Operation(ConformanceIds.Tenant, operationId);
        OperationStatus? last = null;

        for (var i = 0; i < MaxDrives; i++) {
            last = (await operation.DriveAsync()).GetValueOrThrow();

            if (last.IsTerminal) {
                return last;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), token);
        }

        last.ShouldNotBeNull();
        return last;
    }

    static async Task<string?> ResourceIdLabelAsync(
        ClusterConformanceHarness<ApplicationGatewayCase> harness,
        ObjectRef target,
        CancellationToken token
    ) {
        var read = (await harness.Connection.GetAsync(target, token)).GetValueOrThrow();

        return JsonNode.Parse(read.Json)?["metadata"]?["labels"]?[KubeLabels.ResourceId]?.GetValue<string>();
    }
}
