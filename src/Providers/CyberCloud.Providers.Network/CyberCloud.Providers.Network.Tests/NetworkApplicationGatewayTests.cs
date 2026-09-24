using CyberCloud.Core.Contracts;
using CyberCloud.ResourceManager;
using CyberCloud.ResourceManager.Conformance;
using CyberCloud.ResourceManager.Reconcile;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CyberCloud.Providers.Network.Tests;

/// <summary>
///     <c>CyberCloud.Network/virtualNetworks/applicationGateways</c> — the grammars, the rendered
///     configuration of both processes, the two reads from outside the body, and the tables that exist
///     twice.
/// </summary>
/// <remarks>
///     ⚠ <b>What routes and what blocks is proven on a real k3s</b>, by
///     <c>ApplicationGatewayTrafficConformance</c>: a request routed by host and path to a real backend
///     pod, a SQL-injection probe answered 403 in prevention mode and passed and logged in detection
///     mode. This file pins what that run cannot see — the grammar edges, a machine member, a vault
///     handle outside the tenant's prefix — against a seeded resolver and a view that answers.
/// </remarks>
public sealed class NetworkApplicationGatewayTests {
    static readonly Guid Tenant = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000021");
    static readonly Guid OtherTenant = Guid.Parse("bbbbbbbb-0000-4000-8000-000000000022");
    static readonly Guid Subscription = Guid.Parse("aaaaaaaa-0000-4000-8000-00000000002a");
    static readonly Guid Cluster = Guid.Parse("cccccccc-0000-4000-8000-000000000003");

    // ── The reconciler holds nothing ─────────────────────────────────────────────────────────────

    [Fact]
    public void TheReconcilerHoldsNoMutableState() =>
        ReconcilerConformance.CheckNoHiddenState(new ApplicationGatewayReconciler(new FixedClock())).ShouldBeEmpty();

    // ── Routing ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RulesRenderInTheirOrderOnWholeSegmentsAgainstTheLowerCasedPortlessHost() {
        using var body = Parse(
            ApplicationGateways.Body(
                Cluster,
                rules: ["shop.example.com/api=api", "*.example.com/=web", "*/static=web", "*/=web"],
                members: ["api=10.20.1.30:8080", "web=10.20.1.31:8080"]
            )
        );

        var config = ApplicationGateways.HaproxyConfig(body.RootElement, ApplicationGateways.NoResolution);

        config.ShouldContain("http-request set-var(txn.host) req.hdr(host),field(1,:),lower");

        var lines = config.Split('\n').Where(static x => x.StartsWith("  use_backend", StringComparison.Ordinal)).ToList();

        lines.ShouldBe(
            [
                "  use_backend pool-api if { var(txn.host) -m str shop.example.com } { path /api } || { var(txn.host) -m str shop.example.com } { path_beg /api/ }",
                "  use_backend pool-web if { var(txn.host) -m end .example.com }",
                "  use_backend pool-web if { path /static } || { path_beg /static/ }",
                "  use_backend pool-web"
            ],
            "the rules are evaluated first-match-wins, so their order in the configuration is the tenant's order"
        );

        config.ShouldContain("default_backend no-route");
        config.ShouldContain("http-request return status 404");
    }

    [Theory]
    [InlineData("*/=nowhere", "no entry of '/properties/backendPools' is in a pool of that name")]
    [InlineData("*/api/=web", "has an empty segment")]
    [InlineData("*//api=web", "has an empty segment")]
    public void ARuleThatCouldNeverServeIsRefused(string rule, string expected) {
        using var body = Parse(ApplicationGateways.Body(Cluster, rules: [rule]));

        ApplicationGateways.BodyProblem(body.RootElement).ShouldNotBeNull().ShouldContain(expected);
    }

    [Theory]
    [InlineData("web=10.20.1.20:8080", "this gateway's own frontend address")]
    [InlineData("web=not-an-address:8080", "neither an IP address nor a resource id")]
    [InlineData("web=10.20.1.11:0", "outside 1 to 65535")]
    [InlineData("web=[FD00::1]:8080", "upper-case")]
    // ⚠ Spellings IPAddress.TryParse reads as some other address, which used to be written into
    // haproxy.cfg as spelled — #31's review.
    [InlineData("web=10.1:8080", "Write '10.0.0.1'")]
    [InlineData("web=0x0a.0.0.1:8080", "Write '10.0.0.1'")]
    [InlineData("web=010.20.1.11:8080", "Write '8.20.1.11'")]
    [InlineData("web=[fd00::0:1]:8080", "Write 'fd00::1'")]
    [InlineData("web=[fd00::1%3]:8080", "carries a zone")]
    [InlineData("web=127.0.0.1:9000", "loopback")]
    [InlineData("web=127.8.8.8:8080", "loopback")]
    [InlineData("web=[::1]:8080", "loopback")]
    [InlineData("web=[::ffff:127.0.0.1]:8080", "loopback")]
    [InlineData("web=169.254.169.254:80", "link-local")]
    [InlineData("web=[fe80::1]:8080", "link-local")]
    [InlineData("web=0.0.0.0:8080", "unspecified")]
    [InlineData("web=224.0.0.1:8080", "multicast")]
    [InlineData("web=[::ffff:10.20.1.20]:8080", "this gateway's own frontend address")]
    [InlineData(
        "web=/tenants/aaaaaaaa-0000-4000-8000-000000000021/subscriptions/aaaaaaaa-0000-4000-8000-00000000002a/resourceGroups/prod/providers/CyberCloud.Storage/accounts/files:8080",
        "CyberCloud.ContainerInstance/containerGroups is not a published type"
    )]
    public void AMemberThatCouldNeverServeIsRefused(string member, string expected) {
        using var body = Parse(ApplicationGateways.Body(Cluster, members: [member]));

        ApplicationGateways.BodyProblem(body.RootElement).ShouldNotBeNull().ShouldContain(expected);
    }

    [Fact]
    public void AnIpv6MemberIsBracketedAndATwiceListedMemberIsRefused() {
        using var v6 = Parse(ApplicationGateways.Body(Cluster, members: ["web=[fd00:20:1::11]:8080"]));

        ApplicationGateways.BodyProblem(v6.RootElement).ShouldBeNull();
        ApplicationGateways.HaproxyConfig(v6.RootElement, ApplicationGateways.NoResolution)
            .ShouldContain("server m1 [fd00:20:1::11]:8080 check");

        using var twice = Parse(ApplicationGateways.Body(Cluster, members: ["web=10.20.1.11:8080", "web=10.20.1.11:8080"]));

        ApplicationGateways.BodyProblem(twice.RootElement).ShouldNotBeNull().ShouldContain("twice");
    }

    [Theory]
    [InlineData(ApplicationGateways.WafPort, 443, "reserves")]
    [InlineData(ApplicationGateways.ReadinessPort, 443, "reserves")]
    [InlineData(8443, 8443, "same port")]
    public void AListenerOnAPortThePodCannotGiveItIsRefused(int http, int https, string expected) {
        using var body = Parse(ApplicationGateways.Body(Cluster, httpPort: http, httpsPort: https, certificate: Handle(Tenant)));

        ApplicationGateways.BodyProblem(body.RootElement).ShouldNotBeNull().ShouldContain(expected);
    }

    // ── The WAF policy ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void PreventionBlocksAndFailsClosedAndDetectionDoesNeither() {
        using var prevention = Parse(ApplicationGateways.Body(Cluster, wafMode: ApplicationGateways.WafPrevention));
        using var detection = Parse(ApplicationGateways.Body(Cluster, wafMode: ApplicationGateways.WafDetection));

        var blocking = ApplicationGateways.HaproxyConfig(prevention.RootElement, ApplicationGateways.NoResolution);
        var watching = ApplicationGateways.HaproxyConfig(detection.RootElement, ApplicationGateways.NoResolution);

        blocking.ShouldContain("http-request deny deny_status 403 hdr waf-block request if { var(txn.coraza.action) -m str deny }");
        blocking.ShouldContain(
            "http-request deny deny_status 503 if { var(txn.coraza.error) -m int gt 0 }",
            customMessage: "a firewall that does not answer in prevention mode must be a 503 and not a pass"
        );

        // ⚠ Both send every request to the agent and both log what it found — detection is the same
        // inspection with the enforcement taken away, in HAProxy AND in Coraza.
        foreach (var config in (ReadOnlySpan<string>)[blocking, watching]) {
            config.ShouldContain("filter spoe engine coraza");
            config.ShouldContain("http-request send-spoe-group coraza coraza-req");
            config.ShouldContain("option http-buffer-request");
            config.ShouldContain("waf-rules:%[var(txn.coraza.rule_ids)]");
        }

        watching.ShouldNotContain("deny_status", customMessage: "detection mode blocks nothing, including when the agent is down");

        ApplicationGateways.Directives(prevention.RootElement).ShouldContain("SecRuleEngine On");
        ApplicationGateways.Directives(detection.RootElement).ShouldContain("SecRuleEngine DetectionOnly");
    }

    [Fact]
    public void OffRunsNoAgentNoFilterAndDrawsHalf() {
        using var off = Parse(ApplicationGateways.Body(Cluster, wafMode: ApplicationGateways.WafOff));
        using var on = Parse(ApplicationGateways.Body(Cluster));
        var id = Address("edge", Tenant);

        ApplicationGateways.HaproxyConfig(off.RootElement, ApplicationGateways.NoResolution)
            .ShouldNotContain("spoe");

        var deployment = JsonNode.Parse(
            ApplicationGateways.DeploymentJson("ns", id, off.RootElement, ApplicationGateways.NoResolution, "")
        )!;

        deployment["spec"]!["template"]!["spec"]!["containers"]!.AsArray()
            .Select(static x => x!["image"]!.GetValue<string>())
            .ShouldBe([ApplicationGateways.ProxyImage]);

        ApplicationGateways.Containers(off.RootElement).ShouldBe(1);
        ApplicationGateways.Containers(on.RootElement).ShouldBe(2);

        JsonNode.Parse(ApplicationGateways.ConfigMapJson(id, off.RootElement, ApplicationGateways.NoResolution))!["data"]!
            .AsObject()
            .ContainsKey(ApplicationGateways.WafConfigFile)
            .ShouldBeFalse();
    }

    [Fact]
    public void ThePolicyLoadsInTheOrderThatMakesItMeanWhatItSays() {
        using var body = Parse(
            ApplicationGateways.Body(
                Cluster,
                paranoiaLevel: 3,
                exclusions: ["942100:ARGS:password", "920350", "941100-941199"],
                customRules: [
                    "allow path /healthz", "deny ip 203.0.113.0/24", "deny useragent SQLMap", "deny host shop.example.com"
                ]
            )
        );

        var directives = ApplicationGateways.Directives(body.RootElement);

        directives.ShouldBe(
            [
                "Include @coraza.conf-recommended",
                "SecRuleEngine On",
                "Include @crs-setup.conf.example",
                "SecAction \"id:900000,phase:1,pass,t:none,nolog,setvar:tx.blocking_paranoia_level=3\"",
                "SecRule REQUEST_URI_RAW \"@beginsWith /healthz\" \"id:100001,phase:1,t:none,t:urlDecodeUni,t:normalizePathWin,allow,nolog,msg:'CyberCloud custom rule 1: allow path /healthz'\"",
                "SecRule REMOTE_ADDR \"@ipMatch 203.0.113.0/24\" \"id:190002,phase:1,t:none,deny,status:403,log,msg:'CyberCloud custom rule 2: deny ip 203.0.113.0/24'\"",
                "SecRule REQUEST_HEADERS:User-Agent \"@contains sqlmap\" \"id:190003,phase:1,t:none,t:lowercase,deny,status:403,log,msg:'CyberCloud custom rule 3: deny useragent SQLMap'\"",
                "SecRule REQUEST_HEADERS:Host \"@rx ^shop[.]example[.]com[.]?(:[0-9]*)?$\" \"id:190004,phase:1,t:none,t:lowercase,deny,status:403,log,msg:'CyberCloud custom rule 4: deny host shop.example.com'\"",
                "Include @owasp_crs/*.conf",
                "SecRuleUpdateTargetById 942100 \"!ARGS:password\"",
                "SecRuleRemoveById 920350",
                "SecRuleRemoveById 941100-941199"
            ],
            "the mode after the recommended file that turns it to DetectionOnly, the paranoia level after "
            + "the CRS setup, custom rules before the CRS so an allow skips it, exclusions after the rules "
            + "they remove"
        );

        // ⚠ AND THE YAML AROUND THEM IS WHAT coraza-spoa's KnownFields(true) DECODER ACCEPTS: every
        // directive is indented under the literal block and nothing else follows it.
        var yaml = ApplicationGateways.WafConfig(body.RootElement);

        yaml.ShouldContain("bind: 127.0.0.1:9000", customMessage: "the agent must answer on the pod's loopback only");
        yaml.Split('\n').SkipWhile(static x => x != "    directives: |").Skip(1)
            .Where(static x => x.Length > 0)
            .ShouldAllBe(static x => x.StartsWith("      ", StringComparison.Ordinal));
    }

    /// <summary>
    ///     ⚠ A host deny matches every spelling routing treats as the same host, and nothing else — the
    ///     regular expression a <c>host</c> rule renders, run the way Coraza runs it: after
    ///     <c>t:lowercase</c>.
    /// </summary>
    /// <remarks>
    ///     #31's review: the rule compared <c>SERVER_NAME</c> raw with <c>@streq</c>, and routing reads
    ///     <c>req.hdr(host),field(1,:),lower</c>, so the upper-case and the port-carrying spellings
    ///     routed to the pool the deny was written to protect. That they are 403 through a real agent is
    ///     <c>ApplicationGatewayTrafficConformance</c>'s.
    /// </remarks>
    [Theory]
    [InlineData("shop.example.com", true)]
    [InlineData("SHOP.Example.COM", true)]
    [InlineData("shop.example.com:80", true)]
    [InlineData("shop.example.com.", true)]
    [InlineData("shop.example.com.:8443", true)]
    [InlineData("shopxexample.com", false)]
    [InlineData("api.shop.example.com", false)]
    [InlineData("shop.example.com.evil", false)]
    public void AHostDenyMatchesEverySpellingRoutingTreatsAsThatHost(string header, bool denied) {
        var pattern = new System.Text.RegularExpressions.Regex(ApplicationGateways.HostPattern("shop.example.com"));

        pattern.IsMatch(header.ToLowerInvariant()).ShouldBe(denied, header);
    }

    [Fact]
    public void APathRuleIsComparedAfterTheNormalizationABackendWouldApply() {
        using var body = Parse(ApplicationGateways.Body(Cluster, customRules: ["deny path /secret"]));

        // ⚠ REQUEST_URI_RAW and not REQUEST_FILENAME: coraza-spoa builds the latter with Go's url.Parse,
        // which reads `//secret/x` as the authority `secret` and the path `/x`.
        ApplicationGateways.Directives(body.RootElement)
            .ShouldContain(
                "SecRule REQUEST_URI_RAW \"@beginsWith /secret\" \"id:190001,phase:1,t:none,t:urlDecodeUni,"
                + "t:normalizePathWin,deny,status:403,log,msg:'CyberCloud custom rule 1: deny path /secret'\""
            );
    }

    [Theory]
    [InlineData("deny ip 203.0.113.0/33", "not an IP address or range")]
    [InlineData("deny ip 203.0.113.300", "not an IP address or range")]
    public void ACustomRuleWhoseAddressDoesNotParseIsRefused(string rule, string expected) {
        using var body = Parse(ApplicationGateways.Body(Cluster, customRules: [rule]));

        ApplicationGateways.BodyProblem(body.RootElement).ShouldNotBeNull().ShouldContain(expected);
    }

    [Fact]
    public void TheGrammarsAdmitNothingThatCouldLeaveTheStringItIsRenderedInto() {
        // ⚠ THE INJECTION A RAW-SECLANG CUSTOM RULE WOULD BE: a quote closes the operator, a newline
        // starts a directive of the tenant's choosing. Each pattern is what BodyProblem enforces per
        // element before anything is rendered, and the second half asks BodyProblem itself.
        foreach (var (pattern, hostile) in (ReadOnlySpan<(string, string)>)[
                     (ApplicationGateways.CustomRulePattern, "deny path /a\" \"id:1,phase:1,pass\"\nSecRuleEngine Off"),
                     (ApplicationGateways.CustomRulePattern, "deny useragent a'b"),
                     (ApplicationGateways.ExclusionPattern, "942100:ARGS:\"x"),
                     (ApplicationGateways.RulePattern, "*/=web\n  use_backend evil"),
                     (ApplicationGateways.MemberPattern, "web=10.0.0.1:80 backup")
                 ]) {
            Regex.IsMatch(hostile, "^(?:" + pattern + ")$", RegexOptions.None, TimeSpan.FromSeconds(1))
                .ShouldBeFalse($"'{hostile}' is admitted by {pattern}");
        }

        // ⚠ AND THE RECONCILER REFUSES THEM, which is the half that matters: the schema does not carry
        // these patterns (a chart cannot say one per element — `cidr-shape-is-unenforced`), so the API
        // answers 202 and BodyProblem is the only thing between the text and the rendered files.
        foreach (var body in (ReadOnlySpan<string>)[
                     ApplicationGateways.Body(Cluster, customRules: ["deny path /a\" \"id:1,phase:1,pass\"\nSecRuleEngine Off"]),
                     ApplicationGateways.Body(Cluster, exclusions: ["942100:ARGS:\"x"]),
                     ApplicationGateways.Body(Cluster, rules: ["*/=web\n  use_backend evil"]),
                     ApplicationGateways.Body(Cluster, members: ["web=10.20.1.11:80 backup"])
                 ]) {
            using var parsed = Parse(body);

            ApplicationGateways.BodyProblem(parsed.RootElement).ShouldNotBeNull().ShouldContain("which is not");
        }
    }

    [Fact]
    public void EveryRenderedFileEndsWithALineFeed() {
        // ⚠ MEASURED ON THE k3s RUN, against the test's own backend configuration: HAProxy refuses a file
        // whose last line has no LF — "Missing LF on last line, file might have been truncated" — and
        // exits, which is a crash loop. A raw string literal ends without one, so it is pinned here for
        // the three files this type renders.
        using var body = Parse(ApplicationGateways.Body(Cluster));

        foreach (var file in (ReadOnlySpan<string>)[
                     ApplicationGateways.HaproxyConfig(body.RootElement, ApplicationGateways.NoResolution),
                     ApplicationGateways.SpoeConfig(),
                     ApplicationGateways.WafConfig(body.RootElement)
                 ]) {
            file.ShouldEndWith("\n");
        }
    }

    // ── The configuration is a rollout ───────────────────────────────────────────────────────────

    [Fact]
    public void ChangingAMemberOrThePolicyChangesThePodTemplate() {
        var id = Address("edge", Tenant);

        using var before = Parse(ApplicationGateways.Body(Cluster));
        using var member = Parse(ApplicationGateways.Body(Cluster, members: [ApplicationGateways.DefaultMember, "web=10.20.1.12:8080"]));
        using var policy = Parse(ApplicationGateways.Body(Cluster, exclusions: ["920350"]));

        var hash = Hash(before);

        Hash(member).ShouldNotBe(hash, "a member change applied cleanly and restarted nothing");
        Hash(policy).ShouldNotBe(hash, "a WAF policy change applied cleanly and restarted nothing");
        Hash(before).ShouldBe(hash, "the same body rolled the gateway, which it would on every pass");

        string Hash(JsonDocument body) =>
            JsonNode.Parse(ApplicationGateways.DeploymentJson("ns", id, body.RootElement, ApplicationGateways.NoResolution, ""))!
                ["spec"]!["template"]!["metadata"]!["annotations"]![ApplicationGateways.ConfigChecksumAnnotation]!
                .GetValue<string>();
    }

    [Fact]
    public void TheAgentIsStartedByCommandBecauseTheImageHasNoEntrypoint() {
        // ⚠ MEASURED AGAINST THE PINNED IMAGE: its config declares a Cmd and no Entrypoint, so a pod
        // that passes `args` replaces the binary and the container fails with
        // `exec: "--config": executable file not found in $PATH`.
        using var body = Parse(ApplicationGateways.Body(Cluster));

        var agent = JsonNode.Parse(
                ApplicationGateways.DeploymentJson("ns", Address("edge", Tenant), body.RootElement, ApplicationGateways.NoResolution, "")
            )!["spec"]!["template"]!["spec"]!["containers"]![1]!;

        agent["args"].ShouldBeNull();
        agent["command"]!.AsArray().Select(static x => x!.GetValue<string>())
            .ShouldBe([ApplicationGateways.WafBinary, "--config", "/etc/coraza-spoa/coraza-spoa.yaml"]);
        agent["securityContext"]!["runAsUser"]!.GetValue<int>().ShouldBe(ApplicationGateways.WafUid);
    }

    // ── The certificate ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ACertificateOutsideTheTenantsVaultPrefixIsRefusedBeforeItIsResolved() {
        var resolver = new SeededResolver();
        resolver.Values[Handle(OtherTenant)] = Pem();

        using var body = Parse(ApplicationGateways.Body(Cluster, certificate: Handle(OtherTenant)));
        var connection = new RecordingConnection();

        var outcome = await Pass(connection, Address("edge", Tenant), body.RootElement, resolver);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.Failed);
        outcome.Error!.Code.ShouldBe(ErrorCode.AuthorizationFailed);
        resolver.Asked.ShouldBeEmpty("another tenant's vault path reached the resolver");
        connection.Applied.ShouldBeEmpty("a refused handle applied something");
    }

    /// <summary>
    ///     ⚠ Each of these starts with the tenant's own prefix, and the first reads the other tenant's
    ///     certificate and key once an HTTP client collapses the dot segments — which is what the
    ///     parser's <c>StartsWith</c> let through until #31's review.
    /// </summary>
    [Theory]
    [InlineData("tenants/aaaaaaaa-0000-4000-8000-000000000021/../bbbbbbbb-0000-4000-8000-000000000022/certs/edge#pem")]
    [InlineData("tenants/aaaaaaaa-0000-4000-8000-000000000021/certs/../../../platform/root#pem")]
    [InlineData("tenants/aaaaaaaa-0000-4000-8000-000000000021/./certs/edge#pem")]
    [InlineData("tenants/aaaaaaaa-0000-4000-8000-000000000021//certs/edge#pem")]
    public async Task ACertificatePathThatClimbsOutOfTheTenantsPrefixIsRefusedBeforeItIsResolved(string handle) {
        var resolver = new SeededResolver();
        resolver.Values[handle] = Pem();

        using var body = Parse(ApplicationGateways.Body(Cluster, certificate: handle));
        var connection = new RecordingConnection();

        var outcome = await Pass(connection, Address("edge", Tenant), body.RootElement, resolver);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.Failed);
        outcome.Error!.Code.ShouldBe(ErrorCode.AuthorizationFailed);
        outcome.Error.Message.ShouldContain("'..'");
        resolver.Asked.ShouldBeEmpty("a path that is not canonical reached the resolver");
        connection.Applied.ShouldBeEmpty("a refused handle applied something");
    }

    [Fact]
    public async Task ACertificateIsASecretTheProxyMountsAndItsHashRollsThePod() {
        var resolver = new SeededResolver();
        var pem = Pem();
        resolver.Values[Handle(Tenant)] = pem;

        using var body = Parse(ApplicationGateways.Body(Cluster, certificate: Handle(Tenant)));
        var connection = new RecordingConnection();
        var id = Address("edge", Tenant);

        (await Pass(connection, id, body.RootElement, resolver)).Kind.ShouldBe(ReconcileOutcomeKind.Converged);

        var ns = ReconcileDriver.NamespaceFor(id);
        var secret = connection.Objects[RecordingConnection.Key(ApplicationGateways.TlsSecretRef(ns, id))];

        KubeSecret.Value(new() { Ref = ApplicationGateways.TlsSecretRef(ns, id), Json = secret }, ApplicationGateways.TlsFile)
            .GetValueOrThrow()
            .ShouldBe(pem);

        var deployment = JsonNode.Parse(connection.Objects[RecordingConnection.Key(ApplicationGateways.DeploymentRef(ns, id))])!;

        deployment["spec"]!["template"]!["metadata"]!["annotations"]![ApplicationGateways.TlsChecksumAnnotation]!
            .GetValue<string>()
            .ShouldBe(KubeLabels.ReconcileHash(pem));

        deployment.ToJsonString().ShouldNotContain("PRIVATE KEY", customMessage: "the key reached the pod template");

        // ⚠ MEASURED ON THE FIRST k3s RUN: a Secret volume is root-owned, HAProxy is uid 99, and at 0400
        // it could not open tls.pem — a pod Running and never Ready. fsGroup 99 and 0440 are the fix.
        var podSpec = deployment["spec"]!["template"]!["spec"]!;

        podSpec["securityContext"]!["fsGroup"]!.GetValue<int>().ShouldBe(LoadBalancers.ProxyUid);
        podSpec["volumes"]!.AsArray().Single(static x => x!["name"]!.GetValue<string>() == "tls")!
            ["secret"]!["defaultMode"]!.GetValue<int>().ShouldBe(0b100_100_000, "0440: the owner and the proxy's group read, nobody else");
        body.RootElement.GetRawText().ShouldNotContain("PRIVATE KEY");

        ApplicationGateways.HaproxyConfig(body.RootElement, ApplicationGateways.NoResolution)
            .ShouldContain("bind :443 ssl crt /etc/cybercloud/tls/tls.pem");

        // ⚠ AND TAKING THE CERTIFICATE AWAY DELETES THE SECRET: it holds a private key nothing mounts.
        using var plain = Parse(ApplicationGateways.Body(Cluster));

        (await Pass(connection, id, plain.RootElement, resolver)).Kind.ShouldBe(ReconcileOutcomeKind.Converged);

        connection.Objects.ContainsKey(RecordingConnection.Key(ApplicationGateways.TlsSecretRef(ns, id)))
            .ShouldBeFalse("a removed certificate left its private key in the tenant's namespace");
    }

    [Fact]
    public async Task AValueThatIsNotACertificateAndAKeyIsRefusedRatherThanCrashingTheProxy() {
        var resolver = new SeededResolver();
        resolver.Values[Handle(Tenant)] = "-----BEGIN CERTIFICATE-----\nMIIB\n-----END CERTIFICATE-----\n";

        using var body = Parse(ApplicationGateways.Body(Cluster, certificate: Handle(Tenant)));
        var connection = new RecordingConnection();

        var outcome = await Pass(connection, Address("edge", Tenant), body.RootElement, resolver);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.Failed);
        outcome.Error!.Target.ShouldBe("/properties/listeners/certificate");
        connection.Applied.ShouldBeEmpty();
    }

    // ── Machine members, through the cross-resource view ─────────────────────────────────────────

    [Fact]
    public async Task AMachineMemberResolvesToTheAddressKubeVirtReportsAndIsRecordedBesideTheConfig() {
        var id = Address("edge", Tenant);
        var ns = ReconcileDriver.NamespaceFor(id);
        var machine = MachinePath("api-1");
        var view = new AnsweringView();
        view.Machines[machine] = ("net", ns);

        var connection = new RecordingConnection();
        connection.Objects[RecordingConnection.Key(InstanceRef(ns, "api-1"))] = Instance("10.20.1.40");

        using var body = Parse(ApplicationGateways.Body(Cluster, rules: ["*/=api"], members: ["api=" + machine + ":8080"]));

        (await Pass(connection, id, body.RootElement, view: view)).Kind.ShouldBe(ReconcileOutcomeKind.Converged);

        var config = connection.Objects[RecordingConnection.Key(ApplicationGateways.ConfigMapRef(ns, id))];

        JsonNode.Parse(config)!["data"]![ApplicationGateways.ProxyConfigFile]!.GetValue<string>()
            .ShouldContain("server m1 10.20.1.40:8080 check");
        ApplicationGateways.ResolvedOf(config).ShouldBe(new Dictionary<string, string> { [machine] = "10.20.1.40" });

        // ⚠ THE DRIFT SCAN HAS NO VIEW, AND STILL AGREES: it re-renders from the record.
        var observed = await new ApplicationGatewayReconciler(new FixedClock()).ObserveAsync(
            new(id, ApplicationGateways.V2026, body.RootElement, ns, connection),
            TestContext.Current.CancellationToken
        );

        observed.Summary.ShouldBe("the gateway carries the desired configuration");

        // And a hand edit to the record is drift, because the configuration beside it no longer agrees.
        var edited = JsonNode.Parse(config)!;
        edited["data"]![ApplicationGateways.ResolvedKey] = ApplicationGateways.ResolvedJson(
            new Dictionary<string, string> { [machine] = "10.20.1.99" }
        );
        connection.Objects[RecordingConnection.Key(ApplicationGateways.ConfigMapRef(ns, id))] = edited.ToJsonString();

        (await new ApplicationGatewayReconciler(new FixedClock()).ObserveAsync(
            new(id, ApplicationGateways.V2026, body.RootElement, ns, connection),
            TestContext.Current.CancellationToken
        )).Summary.ShouldBe("the gateway has drifted");
    }

    [Fact]
    public async Task AMachineTheGatewayMayNotReadIsLeftOutAndNamedAndTheRestStillServes() {
        var id = Address("edge", Tenant);
        var ns = ReconcileDriver.NamespaceFor(id);
        var machine = MachinePath("api-1");
        var connection = new RecordingConnection();
        var log = new ListLog();

        using var body = Parse(
            ApplicationGateways.Body(Cluster, rules: ["*/=api"], members: ["api=" + machine + ":8080", "api=10.20.1.41:8080"])
        );

        (await Pass(connection, id, body.RootElement, view: new AnsweringView(), log: log))
            .Kind.ShouldBe(ReconcileOutcomeKind.Converged);

        var proxy = JsonNode.Parse(connection.Objects[RecordingConnection.Key(ApplicationGateways.ConfigMapRef(ns, id))])!
            ["data"]![ApplicationGateways.ProxyConfigFile]!.GetValue<string>();

        proxy.ShouldContain("server m2 10.20.1.41:8080");
        proxy.ShouldContain("# m1 " + machine + " did not resolve to an address");
        log.Lines.ShouldContain(x => x.StartsWith("unresolved:", StringComparison.Ordinal) && x.Contains("reader role", StringComparison.Ordinal));

        // And the action says so, rather than leaving the tenant to find the comment.
        var answer = await new ShowRoutingHandler(new FixedClock()).InvokeAsync(
            new(id, ApplicationGateways.V2026, ApplicationGateways.RoutingAction, body.RootElement, body.RootElement, ns, connection, new SeededResolver()),
            TestContext.Current.CancellationToken
        );

        var routing = JsonNode.Parse(answer.GetValueOrThrow())!;

        routing["unresolved"]!.AsArray().Select(static x => x!.GetValue<string>()).ShouldBe(["api=" + machine + ":8080"]);
        routing["members"]!.AsArray().Select(static x => x!.GetValue<string>()).ShouldBe(["api=10.20.1.41:8080"]);
        routing["readyReplicas"]!.GetValue<int>().ShouldBe(0);
    }

    /// <summary>
    ///     ⚠ A machine's address is its guest agent's report — the tenant's word — and it is held to what
    ///     a body may name: loopback and link-local are passed over, and what is rendered is the
    ///     address's own spelling.
    /// </summary>
    [Fact]
    public void AMachinesReportedAddressIsHeldToWhatABodyMayName() {
        static string Reported(params string[] addresses) =>
            new JsonObject {
                ["status"] = new JsonObject {
                    ["interfaces"] = new JsonArray([
                        .. addresses.Select(static x => (JsonNode)new JsonObject { ["ipAddress"] = x })
                    ])
                }
            }.ToJsonString();

        ApplicationGatewayReconciler.AddressOf(Reported("127.0.0.1", "169.254.169.254", "10.20.1.40")).ShouldBe("10.20.1.40");
        ApplicationGatewayReconciler.AddressOf(Reported("::1", "fe80::1", "FD00:20:1::0:40")).ShouldBe("fd00:20:1::40");
        ApplicationGatewayReconciler.AddressOf(Reported("127.0.0.1")).ShouldBeNull("a machine reporting only loopback has no address");
    }

    [Fact]
    public async Task AMachineInAnotherNetworkIsRefusedTerminally() {
        var id = Address("edge", Tenant);
        var ns = ReconcileDriver.NamespaceFor(id);
        var machine = MachinePath("api-1");
        var view = new AnsweringView();
        view.Machines[machine] = ("other-net", ns);

        using var body = Parse(ApplicationGateways.Body(Cluster, rules: ["*/=api"], members: ["api=" + machine + ":8080"]));
        var connection = new RecordingConnection();

        var outcome = await Pass(connection, id, body.RootElement, view: view);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.Failed);
        outcome.Error!.Message.ShouldContain("'other-net'");
        outcome.Error.Target.ShouldBe("/properties/backendPools");
        connection.Applied.ShouldBeEmpty();
    }

    // ── The tables that exist twice ──────────────────────────────────────────────────────────────

    [Fact]
    public void TheChartAndTheProviderAgreeOnImagesPresetsPortsAndTheHashPrefix() {
        var helpers = Read("templates/_helpers.tpl");

        helpers.ShouldContain(ApplicationGateways.ProxyImage, Case.Sensitive);
        helpers.ShouldContain(ApplicationGateways.WafImage, Case.Sensitive);

        foreach (var (name, (cpu, memory)) in ApplicationGateways.Presets) {
            Regex.IsMatch(
                    helpers,
                    $@"""{Regex.Escape(name)}""\s+\(dict\s+""cpu""\s+""{Regex.Escape(cpu)}""\s+""memory""\s+""{Regex.Escape(memory)}""\)",
                    RegexOptions.None,
                    TimeSpan.FromSeconds(5)
                )
                .ShouldBeTrue($"the chart's preset '{name}' is not {cpu}/{memory}");
        }

        var template = Read("templates/gateway.yaml");

        template.ShouldContain(@"printf ""sha256:%s""", Case.Sensitive);
        template.ShouldContain(ApplicationGateways.ConfigChecksumAnnotation, Case.Sensitive);
        helpers.ShouldContain("bind: 127.0.0.1:" + ApplicationGateways.WafPort.ToString(System.Globalization.CultureInfo.InvariantCulture), Case.Sensitive);

        var chart = Read("Chart.yaml");

        chart.ShouldContain("cybercloud.io/resource-type: " + ApplicationGateways.Type, Case.Sensitive);
        chart.ShouldContain("cybercloud.io/api-version: \"" + ApplicationGateways.V2026 + "\"", Case.Sensitive);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────

    static JsonDocument Parse(string json) => JsonDocument.Parse(json);

    static string Handle(Guid tenant) => $"tenants/{tenant:D}/certs/edge#pem";

    static string MachinePath(string name) =>
        $"/tenants/{Tenant:D}/subscriptions/{Subscription:D}/resourceGroups/prod/providers/CyberCloud.Compute/virtualMachines/{name}";

    static ObjectRef InstanceRef(string ns, string name) =>
        new() { Kind = ApplicationGatewayReconciler.MachineInstanceKind, Namespace = ns, Name = name };

    static string Instance(string address) =>
        new JsonObject {
            ["kind"] = "VirtualMachineInstance",
            ["status"] = new JsonObject {
                ["interfaces"] = new JsonArray { new JsonObject { ["name"] = "default", ["ipAddress"] = address } }
            }
        }.ToJsonString();

    static string Pem() {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=edge.example", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));

        return certificate.ExportCertificatePem() + "\n" + key.ExportPkcs8PrivateKeyPem() + "\n";
    }

    static ResourceId Address(string name, Guid tenant) =>
        new(tenant, Subscription, "prod", ApplicationGateways.Type, name, Guid.Parse("66666666-6666-4666-8666-666666666666"), "net");

    static async Task<ReconcileOutcome> Pass(
        RecordingConnection connection,
        ResourceId id,
        JsonElement desired,
        ISecretResolver? resolver = null,
        IResourceView? view = null,
        IReconcileLog? log = null
    ) =>
        await new ApplicationGatewayReconciler(new FixedClock()).ReconcileAsync(
            new(
                id,
                ApplicationGateways.V2026,
                desired,
                null,
                ReconcileDriver.NamespaceFor(id),
                connection,
                resolver ?? new UnavailableSecretResolver(),
                log ?? new NullLog()
            ) { View = view ?? new RefusingResourceView() },
            TestContext.Current.CancellationToken
        );

    static string Read(string file) {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CyberCloud.slnx"))) {
            directory = directory.Parent;
        }

        directory.ShouldNotBeNull("no CyberCloud.slnx above the test assembly");

        return File.ReadAllText(
            Path.Combine(directory.FullName, "charts", "managed", "application-gateway", file.Replace('/', Path.DirectorySeparatorChar))
        );
    }

    /// <summary>A resolver holding a few values, which records every handle it was asked for.</summary>
    sealed class SeededResolver : ISecretResolver {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

        public List<string> Asked { get; } = [];

        public Task<Result<string>> ResolveAsync(SecretRef reference, CancellationToken cancellationToken = default) {
            Asked.Add(reference.ToString());

            return Task.FromResult(
                Values.TryGetValue(reference.ToString(), out var value)
                    ? Result<string>.Success(value)
                    : Result<string>.Failure(ErrorCode.ResourceNotFound, $"'{reference}' is not in the vault.")
            );
        }
    }

    /// <summary>
    ///     A view that knows a few machines — their network and the namespace their <c>VirtualMachine</c>
    ///     was rendered into — and answers "not found" for everything else, as the real one does for a
    ///     resource the gateway was never granted.
    /// </summary>
    sealed class AnsweringView : IResourceView {
        public Dictionary<string, (string Network, string Namespace)> Machines { get; } = new(StringComparer.Ordinal);

        public Task<Result<ResourceSnapshot>> ReadAsync(ResourceId target, CancellationToken cancellationToken = default) =>
            Task.FromResult(
                Machines.TryGetValue(target.Path, out var machine)
                    ? Result<ResourceSnapshot>.Success(
                        new() {
                            Path = target.Path,
                            Name = target.Name,
                            ClusterId = Cluster,
                            Body = new JsonObject {
                                ["properties"] = new JsonObject {
                                    ["network"] = new JsonObject { ["virtualNetwork"] = machine.Network, ["subnet"] = "web" }
                                }
                            }.ToJsonString()
                        }
                    )
                    : Result<ResourceSnapshot>.Failure(ErrorCode.ResourceNotFound, $"'{target.Path}' was not found.")
            );

        public Task<Result<ImmutableArray<ObjectRef>>> RenderedObjectsAsync(
            ResourceId target,
            CancellationToken cancellationToken = default
        ) =>
            Task.FromResult(
                Machines.TryGetValue(target.Path, out var machine)
                    ? Result<ImmutableArray<ObjectRef>>.Success(
                        [
                            new() {
                                Kind = new() { Group = "kubevirt.io", Version = "v1", Kind = "VirtualMachine", Plural = "virtualmachines" },
                                Namespace = machine.Namespace,
                                Name = target.Name
                            }
                        ]
                    )
                    : Result<ImmutableArray<ObjectRef>>.Failure(ErrorCode.ResourceNotFound, $"'{target.Path}' was not found.")
            );
    }

    /// <summary>A log that keeps its lines, as <c>phase: detail</c>.</summary>
    sealed class ListLog : IReconcileLog {
        public List<string> Lines { get; } = [];

        public void Report(string phase, string detail) => Lines.Add(phase + ": " + detail);

        public void Report(string phase, string detail, int percent) => Lines.Add(phase + ": " + detail);
    }
}
