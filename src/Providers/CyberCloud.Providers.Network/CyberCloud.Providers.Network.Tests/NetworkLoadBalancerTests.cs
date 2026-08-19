using CyberCloud.ResourceManager;
using CyberCloud.ResourceManager.Conformance;
using CyberCloud.ResourceManager.Reconcile;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CyberCloud.Providers.Network.Tests;

/// <summary>
///     <c>CyberCloud.Network/virtualNetworks/loadBalancers</c> — the renderer, the convergence
///     predicate, and the two tables that exist twice.
/// </summary>
/// <remarks>
///     ⚠ <b>The assertion this file exists for is the config checksum</b>, because every other check in
///     the tree would be green without it: a <c>ConfigMap</c> that changes does not restart the pod
///     that mounts it, and HAProxy reads its file once at start — so a renderer that left the hash off
///     the pod template would apply cleanly, read back as desired, converge, report <c>Succeeded</c>,
///     and keep forwarding to the old servers.
/// </remarks>
public sealed class NetworkLoadBalancerTests {
    static readonly Guid TenantOne = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000011");
    static readonly Guid TenantTwo = Guid.Parse("bbbbbbbb-0000-4000-8000-000000000012");
    static readonly Guid SubscriptionOne = Guid.Parse("aaaaaaaa-0000-4000-8000-00000000001a");
    static readonly Guid SubscriptionTwo = Guid.Parse("bbbbbbbb-0000-4000-8000-00000000001b");
    static readonly Guid Cluster = Guid.Parse("cccccccc-0000-4000-8000-000000000003");

    // ── Failure class (a): a reconciler with a field ─────────────────────────────────────────────

    [Fact]
    public void TheLoadBalancerReconcilerHoldsNoMutableState() =>
        ReconcilerConformance.CheckNoHiddenState(new LoadBalancerReconciler(new FixedClock()))
            .ShouldBeEmpty();

    [Fact]
    public async Task OneLoadBalancerReconcilerServesTwoTenantsWithoutMixingThem() {
        // ⚠ THE ONLY TEST THAT CATCHES THE READONLY-MUTABLE-FIELD SHAPE, for the fifth reconciler in
        // this family. CheckNoHiddenState above is structurally blind to it, and
        // AddCyberCloudProvider registers a reconciler as a SINGLETON BY CONCRETE TYPE.
        //
        // ⚠ AND THE CROSS-TENANT ASSERTION IS THE OPPOSITE OF THIS FAMILY'S OTHER FOUR. Those render
        // CLUSTER-SCOPED objects, so what has to differ is the NAME; these are namespaced, so what
        // has to differ is the NAMESPACE — and the name may legitimately be equal.
        var reconciler = new LoadBalancerReconciler(new FixedClock());
        var connection = new RecordingConnection();

        var alice = Address("web", TenantOne, SubscriptionOne);
        var bob = Address("web", TenantTwo, SubscriptionTwo);

        using var aliceBody = JsonDocument.Parse(
            LoadBalancers.Body(Cluster, backendAddresses: "10.20.1.11")
        );

        using var bobBody = JsonDocument.Parse(
            LoadBalancers.Body(Cluster, backendAddresses: "10.20.1.99")
        );

        await Pass(reconciler, connection, alice, aliceBody.RootElement);
        await Pass(reconciler, connection, bob, bobBody.RootElement);
        await Pass(reconciler, connection, alice, aliceBody.RootElement);
        await Pass(reconciler, connection, bob, bobBody.RootElement);

        var configs = connection.Applied.Where(x => x.Target.Kind.Kind == "ConfigMap").ToList();

        configs.Count.ShouldBe(4);

        // ⚠ THE THIRD AND FOURTH PASSES, not the first two: a cache populated on pass one is only
        // visible from pass three, which is why the sequence is A, B, A, B.
        Config(configs[2].Body).ShouldContain(
            "10.20.1.11",
            Case.Sensitive,
            "tenant A's proxy was configured with tenant B's backend"
        );

        Config(configs[3].Body).ShouldContain(
            "10.20.1.99",
            Case.Sensitive,
            "tenant B's proxy was configured with tenant A's backend"
        );

        configs[0].Target.Namespace.ShouldNotBe(
            configs[1].Target.Namespace,
            "two subscriptions' identically-named load balancers landed in one namespace, so one "
            + "tenant's proxy configuration overwrote the other's"
        );
    }

    // ── The finding this type would otherwise ship ──────────────────────────────────────────────

    [Fact]
    public void ChangingABackendChangesThePodTemplateAndNotOnlyTheConfigMap() {
        // ⚠ THE SINGLE MOST LIKELY BUG IN THIS TYPE, AND IT IS INVISIBLE TO EVERY OTHER CHECK. A
        // ConfigMap that changes does NOT restart the pods that mount it, and HAProxy reads
        // /usr/local/etc/haproxy/haproxy.cfg once at start. Without the checksum annotation the
        // Deployment applied for a changed backend list is BYTE-IDENTICAL to the previous one, the
        // apply is Unchanged, the read-back matches, the resource reports Succeeded — and traffic
        // keeps going to the old servers for as long as the pod lives.
        var id = Address("web", TenantOne, SubscriptionOne);

        using var before = JsonDocument.Parse(
            LoadBalancers.Body(Cluster, backendAddresses: "10.20.1.11")
        );

        using var after = JsonDocument.Parse(
            LoadBalancers.Body(Cluster, backendAddresses: "10.20.1.11,10.20.1.12")
        );

        var first = Annotations(LoadBalancers.DeploymentJson("ns", id, before.RootElement));
        var second = Annotations(LoadBalancers.DeploymentJson("ns", id, after.RootElement));

        first[LoadBalancers.ConfigChecksumAnnotation]!.GetValue<string>().ShouldNotBe(
            second[LoadBalancers.ConfigChecksumAnnotation]!.GetValue<string>(),
            "the pod template does not change when the configuration does, so a backend change "
            + "converges instantly and changes nothing about where traffic goes"
        );

        // And the same body twice is the same template — otherwise the proxy rolls once per
        // reconcile reminder, forever.
        Annotations(LoadBalancers.DeploymentJson("ns", id, before.RootElement))
            [LoadBalancers.ConfigChecksumAnnotation]!
            .GetValue<string>()
            .ShouldBe(first[LoadBalancers.ConfigChecksumAnnotation]!.GetValue<string>());
    }

    [Fact]
    public void MatchesRefusesADeploymentWhoseConfigHashIsStale() {
        // The read-back half of the assertion above: a Deployment carrying the OLD hash must not
        // count as converged, or the reconciler reports Succeeded for a proxy running the old file.
        var id = Address("web", TenantOne, SubscriptionOne);

        using var before = JsonDocument.Parse(
            LoadBalancers.Body(Cluster, backendAddresses: "10.20.1.11")
        );

        using var after = JsonDocument.Parse(
            LoadBalancers.Body(Cluster, backendAddresses: "10.20.1.11,10.20.1.12")
        );

        var stale = LoadBalancers.DeploymentJson("ns", id, before.RootElement);

        LoadBalancers.Matches(stale, "ns", id, before.RootElement).ShouldBeTrue();

        LoadBalancers.Matches(stale, "ns", id, after.RootElement).ShouldBeFalse(
            "a Deployment whose pod template carries the previous configuration's hash was accepted "
            + "as converged"
        );
    }

    [Fact]
    public void TheProxyIsPlacedOnTheSubnetOfItsOwnNetworkAndTheNetworkComesFromTheAddress() {
        // ⚠ THE FIELD THAT DECIDES WHICH TENANT'S NETWORK THIS POD IS INSIDE. The switch name is
        // {namespace}-{network}-{subnet}; the namespace and the network come from the ADDRESS and
        // only the subnet from the body, so no body can place a proxy in another network.
        var id = Address("web", TenantOne, SubscriptionOne);

        using var body = JsonDocument.Parse(LoadBalancers.Body(Cluster, subnet: "app"));

        var annotations = Annotations(LoadBalancers.DeploymentJson("ns", id, body.RootElement));

        annotations[LoadBalancers.LogicalSwitchAnnotation]!.GetValue<string>().ShouldBe("ns-net-app");

        // And it is the very name kube-ovn-subnet renders for that subnet, rather than a second
        // spelling of the joining.
        var subnet = new ResourceId(
            TenantOne,
            SubscriptionOne,
            "prod",
            NetworkSubnets.Type,
            "app",
            Guid.NewGuid(),
            "net"
        );

        NetworkSubnets.ObjectNameOf("ns", subnet).ShouldBe("ns-net-app");
    }

    [Fact]
    public void ADualStackFrontendIsOneAddressAndNotTwoServers() {
        // ⚠ Kube-OVN's acquireStaticAddressHelper folds a TWO-entry ip_pool of DIFFERENT families
        // into one dual-stack address and treats a semicolon-separated list as separate addresses for
        // separate pods. A semicolon here would leave this proxy's v6 half unallocated.
        var id = Address("web", TenantOne, SubscriptionOne);

        using var body = JsonDocument.Parse(
            LoadBalancers.Body(Cluster, frontendV6: "fd00:20:1::10")
        );

        Annotations(LoadBalancers.DeploymentJson("ns", id, body.RootElement))
            [LoadBalancers.IpPoolAnnotation]!
            .GetValue<string>()
            .ShouldBe("10.20.1.10,fd00:20:1::10");

        using var v4Only = JsonDocument.Parse(LoadBalancers.Body(Cluster));

        Annotations(LoadBalancers.DeploymentJson("ns", id, v4Only.RootElement))
            [LoadBalancers.IpPoolAnnotation]!
            .GetValue<string>()
            .ShouldBe("10.20.1.10", "an unrequested v6 half rendered a trailing comma");
    }

    [Fact]
    public void AnIpv6BackendIsBracketedAndAnIpv4OneIsNot() {
        // `server s1 fd00::11:8080` is ambiguous to HAProxy's own parser and it refuses to start —
        // one backend written in the other family would take the whole load balancer down.
        using var body = JsonDocument.Parse(
            LoadBalancers.Body(Cluster, backendAddresses: "10.20.1.11,fd00:20:1::11")
        );

        var config = LoadBalancers.HaproxyConfig(body.RootElement);

        config.ShouldContain("server s1 10.20.1.11:8080 ", Case.Sensitive);
        config.ShouldContain("server s2 [fd00:20:1::11]:8080 ", Case.Sensitive);
    }

    [Fact]
    public void TheGlobalConnectionLimitIsTwiceTheFrontends() {
        // HAProxy counts BOTH sides of a proxied connection against `maxconn`, so a global limit
        // equal to the frontend's presents as a proxy that stalls at half its configured limit.
        using var body = JsonDocument.Parse(LoadBalancers.Body(Cluster, maxConnections: 1500));

        var config = LoadBalancers.HaproxyConfig(body.RootElement);

        config.ShouldContain("  maxconn 3000\n", Case.Sensitive);
        config.ShouldContain("  maxconn 1500\n", Case.Sensitive);
        config.ShouldContain("mode tcp", Case.Sensitive);

        // ⚠ And never `log /dev/log`, which almost every HAProxy example carries and which is a
        // socket that does not exist in this image — the proxy would start and log nothing.
        config.ShouldNotContain("/dev/log", Case.Sensitive);
    }

    // ── What the reconciler refuses after the 202 ────────────────────────────────────────────────

    [Theory]
    [InlineData("10.20.1.10", "the backend list contains the proxy's own frontend")]
    [InlineData("not-an-address", "the backend list contains something that is not an address")]
    public async Task ABodyTheApiAcceptedAndTheFabricCannotServeIsRefusedTerminally(
        string backends,
        string why
    ) {
        var reconciler = new LoadBalancerReconciler(new FixedClock());
        var connection = new RecordingConnection();

        using var body = JsonDocument.Parse(
            LoadBalancers.Body(Cluster, backendAddresses: backends)
        );

        var outcome = await Pass(
            reconciler,
            connection,
            Address("web", TenantOne, SubscriptionOne),
            body.RootElement
        );

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.Failed, why);

        connection.Applied.ShouldBeEmpty(
            "a body the reconciler refuses was still applied, so the cluster holds objects for a "
            + "resource the platform reports as Failed"
        );
    }

    [Fact]
    public void AnAddressWithNoParentThrowsRatherThanRenderingACollidingName() =>
        Should.Throw<ArgumentException>(
            () => LoadBalancers.ObjectNameOf(
                new ResourceId(
                    TenantOne,
                    SubscriptionOne,
                    "prod",
                    LoadBalancers.Type,
                    "web",
                    Guid.NewGuid()
                )
            )
        );

    // ── The two tables that exist twice ──────────────────────────────────────────────────────────

    [Fact]
    public void ThePresetTableIsTheSameInCSharpAndInTheChart() {
        // ⚠ THE HALF `./build.sh Charts` DOES NOT REACH. That target regenerates the chart's @param
        // block from LoadBalancers.Schema2026 and byte-diffs it, so the configuration SURFACE cannot
        // drift — and ChartSurfaces filters templates/ out on purpose, so the values BEHIND the
        // surface can. Two spellings of a sizing table is a resource that reserves one quantity
        // through QuotaMeter.Vcpu and runs another.
        var template = Read("templates/_helpers.tpl");

        foreach (var (name, expected) in LoadBalancers.Presets) {
            var row = Regex.Match(
                template,
                $@"""{Regex.Escape(name)}""\s+\(dict\s+""cpu""\s+""(?<cpu>[^""]+)""\s+""memory""\s+""(?<memory>[^""]+)""\)",
                RegexOptions.None,
                TimeSpan.FromSeconds(5)
            );

            row.Success.ShouldBeTrue($"the chart's helper has no row for the preset '{name}'");
            row.Groups["cpu"].Value.ShouldBe(expected.Cpu, name);
            row.Groups["memory"].Value.ShouldBe(expected.Memory, name);
        }

        Regex.Matches(template, @"""(c1\.[a-z]+)""\s+\(dict", RegexOptions.None, TimeSpan.FromSeconds(5))
            .Select(x => x.Groups[1].Value)
            .Order(StringComparer.Ordinal)
            .ShouldBe(LoadBalancers.Presets.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TheChartAndTheProviderAgreeOnTheImageTheSwitchAndTheHash() {
        var template = Read("templates/_helpers.tpl");

        // The image joining. Both spell `{repository}:{version}-alpine`.
        template.ShouldContain(@"printf ""%s:%s-alpine""", Case.Sensitive);

        using var body = JsonDocument.Parse(LoadBalancers.Body(Cluster));

        LoadBalancers.Image(body.RootElement)
            .ShouldBe(LoadBalancers.ImageRepository + ":" + LoadBalancers.DefaultVersion + "-alpine");

        // The switch joining. Both spell `{namespace}-{network}-{subnet}`.
        template.ShouldContain(@"printf ""%s-%s-%s""", Case.Sensitive);

        // ⚠ The hash PREFIX, which is the half a `sha256sum` in a template silently gets wrong:
        // KubeLabels.ReconcileHash writes `sha256:` + hex and Helm's own function writes bare hex, so
        // a chart install and a platform reconcile would disagree on a value that decides whether the
        // proxy restarts.
        Read("templates/loadbalancer.yaml").ShouldContain(@"printf ""sha256:%s""", Case.Sensitive);

        LoadBalancers.ConfigHash(body.RootElement).ShouldStartWith("sha256:");
    }

    [Fact]
    public void TheChartsResourceTypeIsThisTypeAndItsApiVersionIsThisApiVersion() {
        // `Build.Charts` reads both out of Chart.yaml and writes them into values.schema.json as
        // x-cybercloud-*; a mismatch would pair this registry type with a different chart's surface.
        var chart = Read("Chart.yaml");

        chart.ShouldContain("cybercloud.io/resource-type: " + LoadBalancers.Type, Case.Sensitive);
        chart.ShouldContain(
            "cybercloud.io/api-version: \"" + LoadBalancers.V2026 + "\"",
            Case.Sensitive
        );

        // ⚠ And the two version values the schema offers are the two the chart's own default sits
        // between: `appVersion` names the default line, so a body's enum that lost 3.2 would leave
        // the chart claiming a version nothing can select.
        chart.ShouldContain("appVersion: \"" + LoadBalancers.DefaultVersion + "\"", Case.Sensitive);

        LoadBalancers.Versions.ShouldContain(LoadBalancers.DefaultVersion);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────

    static async Task<ReconcileOutcome> Pass(
        LoadBalancerReconciler reconciler,
        RecordingConnection connection,
        ResourceId address,
        JsonElement desired
    ) =>
        await reconciler.ReconcileAsync(
            new(
                address,
                LoadBalancers.V2026,
                desired,
                null,
                ReconcileDriver.NamespaceFor(address),
                connection,
                new UnavailableSecretResolver(),
                new NullLog()
            ),
            TestContext.Current.CancellationToken
        );

    static ResourceId Address(string name, Guid tenant, Guid subscription) =>
        new(
            tenant,
            subscription,
            "prod",
            LoadBalancers.Type,
            name,
            Guid.Parse("55555555-5555-4555-8555-555555555555"),
            "net"
        );

    static JsonObject Annotations(string deploymentJson) =>
        JsonNode.Parse(deploymentJson)!["spec"]!["template"]!["metadata"]!["annotations"]!.AsObject();

    static string Config(string configMapJson) =>
        JsonNode.Parse(configMapJson)!["data"]![LoadBalancers.ConfigFile]!.GetValue<string>();

    /// <summary>One of the chart's files, read from disk.</summary>
    /// <remarks>
    ///     ⚠ The anchor is <c>CyberCloud.slnx</c> and not a <c>charts</c> directory, for
    ///     <c>ConsoleSizingTests</c>' reason: <c>./build.sh Charts</c> runs <c>helm package</c> into
    ///     <c>artifacts/charts/</c>, so after any chart run there are two directories named
    ///     <c>charts</c> above the test assembly and the nearer one holds tarballs.
    /// </remarks>
    static string Read(string file) {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CyberCloud.slnx"))) {
            directory = directory.Parent;
        }

        directory.ShouldNotBeNull("no CyberCloud.slnx above the test assembly");

        var path = Path.Combine(
            directory.FullName,
            "charts",
            "managed",
            "haproxy",
            file.Replace('/', Path.DirectorySeparatorChar)
        );

        File.Exists(path).ShouldBeTrue(path);

        return File.ReadAllText(path);
    }
}
