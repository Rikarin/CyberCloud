using CyberCloud.Core.Contracts;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Network.Tests;

/// <summary>
///     What a security group's body becomes, and the two questions the type had to get right.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THE FIRST QUESTION IS THE UNSAFE DEFAULT.</b> An empty rule set has to permit
///         <b>nothing</b>, and that is a claim about Kube-OVN rather than about this code:
///         <c>CreateSgDenyAllACL</c> installs a drop at <c>SecurityGroupDropPriority</c> beneath every
///         rule, and the controller has no special case for an empty list. What <i>this</i> repository
///         can check is the half that would break it — that an empty body renders empty rule arrays
///         rather than a wildcard, and that a half-typed body (a remote and no ports) renders nothing
///         rather than everything. Both are here.
///     </para>
///     <para>
///         ⚠ <b>THE SECOND IS THE EXPANSION.</b> The reshape's whole claim is that six scalars per
///         direction express a security group, and the cost is that the mapping to Kube-OVN rules is
///         a cross product. If the arithmetic is wrong, a tenant opens ports they did not ask for —
///         which is the failure this type exists to prevent.
///     </para>
/// </remarks>
public sealed class NetworkSecurityGroupTests {
    static readonly Guid ClusterId = Guid.Parse("cccccccc-0000-4000-8000-000000000003");

    // ── Failure class (b): what an empty declaration permits ─────────────────────────────────────

    [Fact]
    public void AnEmptySecurityGroupRendersNoRulesAtAll() {
        // ⚠ THE ASSERTION THE WHOLE TYPE TURNS ON. A body with no remotes and no ports must render
        // two EMPTY arrays. The dangerous alternative is a renderer that reads "nothing specified" as
        // "no restriction" and emits a `protocol: all` rule against 0.0.0.0/0 — which is what an
        // implementation that mirrored a cloud firewall's usual "allow all egress" default would do.
        var spec = SpecOf(Empty());

        spec["ingressRules"]!.AsArray().Count.ShouldBe(0);
        spec["egressRules"]!.AsArray().Count.ShouldBe(0);

        // ⚠ And the one field that grants traffic nobody wrote a rule for is sent, and is false.
        // Kube-OVN's zero value is already false; sending it is what lets Matches compare it and what
        // stops "the substrate's default happens to be safe" from being the argument.
        spec["allowSameGroupTraffic"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public void ARemoteWithNoPortsAndNoIcmpPermitsNothing() {
        // ⚠ THE HALF-TYPED BODY, WHICH IS THE SHAPE THIS CLASS OF BUG ACTUALLY TAKES. Somebody sets
        // the remote, means to add ports, and saves. Reading that as `protocol: all` would make the
        // most permissive rule the type can express the one you reach by stopping early.
        using var body = JsonDocument.Parse(
            NetworkSecurityGroups.Body(
                ClusterId,
                ingressRemoteV4: "0.0.0.0/0",
                ingressTcpPorts: "",
                egressRemoteV4: "",
                egressTcpPorts: ""
            )
        );

        NetworkSecurityGroups.AllRules(body.RootElement).ShouldBeEmpty();
    }

    [Fact]
    public void PortsWithNoRemotePermitNothingEither() {
        // ⚠ The mirror image, and it fails the other way: a renderer that looped over ports first
        // would emit a rule with an empty `remoteAddress`, which Kube-OVN's validateSgRule refuses
        // in the controller — long after this platform reported Succeeded.
        using var body = JsonDocument.Parse(
            NetworkSecurityGroups.Body(
                ClusterId,
                ingressRemoteV4: "",
                ingressTcpPorts: "80,443",
                egressRemoteV4: "",
                egressTcpPorts: "443"
            )
        );

        NetworkSecurityGroups.AllRules(body.RootElement).ShouldBeEmpty();
    }

    // ── The expansion ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TwoRemotesAndTwoPortsAreFourRulesInAFixedOrder() {
        using var body = JsonDocument.Parse(
            WithIngress(remoteV4: "10.0.0.0/8", remoteV6: "fd00::/8", tcpPorts: "80,443")
        );

        var rules = NetworkSecurityGroups.Rules(body.RootElement, NetworkSecurityGroups.Ingress);

        rules.Length.ShouldBe(4);

        // ⚠ v4 BEFORE v6, ENTRIES IN DECLARATION ORDER. Both arrays are ATOMIC under server-side
        // apply and Matches compares them element by element, so a renderer that sorted or used a
        // hash set would report drift on a converged group forever — and the symptom is a resource
        // that never leaves InProgress, not a wrong rule.
        rules.Select(x => $"{x.IpVersion}:{x.Ports}").ShouldBe(
            ["ipv4:80", "ipv4:443", "ipv6:80", "ipv6:443"]
        );
    }

    [Fact]
    public void TcpComesBeforeUdpComesBeforeIcmpWithinOneFamily() {
        using var body = JsonDocument.Parse(
            WithIngress(remoteV4: "10.0.0.0/8", tcpPorts: "443", udpPorts: "53", allowIcmp: true)
        );

        NetworkSecurityGroups.Rules(body.RootElement, NetworkSecurityGroups.Ingress)
            .Select(x => x.Protocol)
            .ShouldBe(["tcp", "udp", "icmp"]);
    }

    [Fact]
    public void APortRangeBecomesOneRuleAndNotOneRulePerPort() {
        // ⚠ `8000-8100` is 101 ports and ONE rule. A renderer that expanded a range would produce 101
        // ACL rows for one line of configuration, which is how an OVN northbound database gets slow
        // enough to be somebody else's incident.
        using var body = JsonDocument.Parse(
            WithIngress(remoteV4: "10.0.0.0/8", tcpPorts: "8000-8100")
        );

        var rules = NetworkSecurityGroups.Rules(body.RootElement, NetworkSecurityGroups.Ingress);

        rules.Length.ShouldBe(1);
        rules[0].Ports.ShouldBe(new PortRange(8000, 8100));
    }

    [Fact]
    public void AnIcmpRuleCarriesNoPortsAtAll() {
        // ⚠ An ICMP rule with `portRangeMin: 0` is a rule Kube-OVN's own validateSgRule would refuse
        // if it looked at it, and `0` is what an omitted int becomes. What is sent is what Matches
        // compares, so the two stay in step by construction — this pins the sent half.
        using var body = JsonDocument.Parse(WithIngress(remoteV4: "10.0.0.0/8", allowIcmp: true));

        var rule = SpecOf(body.RootElement)["ingressRules"]!.AsArray()[0]!.AsObject();

        rule["protocol"]!.GetValue<string>().ShouldBe("icmp");
        rule.ContainsKey("portRangeMin").ShouldBeFalse();
        rule.ContainsKey("portRangeMax").ShouldBeFalse();
    }

    // ── The vocabulary the controller enforces after the apply ───────────────────────────────────

    [Fact]
    public void EveryRuleUsesLowercaseIpVersionAndAllowRatherThanDeny() {
        using var body = JsonDocument.Parse(
            WithIngress(remoteV4: "10.0.0.0/8", remoteV6: "fd00::/8", tcpPorts: "443")
        );

        foreach (var node in SpecOf(body.RootElement)["ingressRules"]!.AsArray()) {
            var rule = node!.AsObject();

            // ⚠ LOWER CASE, and the adjacent object in the same API group spells it the other way:
            // Subnet.spec.protocol is IPv4/IPv6/Dual. validateSgRule is
            // `if rule.IPVersion != "ipv4" && rule.IPVersion != "ipv6"`, in the CONTROLLER, so the
            // wrong spelling is a rule that never programs and a resource that reads as Succeeded.
            rule["ipVersion"]!.GetValue<string>().ShouldBeOneOf("ipv4", "ipv6");

            // ⚠ `allow`, whose siblings are `drop` and `pass` — NOT `deny`. The Go doc comment says
            // "allow, pass or deny" and the constants say otherwise; charts/managed/kube-ovn-vpc
            // recorded that README-versus-code disagreement and this is what it was recorded for.
            rule["policy"]!.GetValue<string>().ShouldBe("allow");
            rule["remoteType"]!.GetValue<string>().ShouldBe("address");

            // ⚠ Sent explicitly: validateSgRule refuses a priority outside 1..16384 and an omitted
            // int is 0.
            rule["priority"]!.GetValue<int>().ShouldBe(1);
        }
    }

    [Fact]
    public void NothingRendersATierOrASecurityGroupRemote() {
        using var body = JsonDocument.Parse(WithIngress(remoteV4: "10.0.0.0/8", tcpPorts: "443"));

        var spec = SpecOf(body.RootElement);

        // ⚠ `tier` is an OPERATOR'S layering control — a group that elected the lower tier would
        // evaluate ahead of a platform policy nobody had written yet.
        spec.ContainsKey("tier").ShouldBeFalse();

        var rule = spec["ingressRules"]!.AsArray()[0]!.AsObject();

        // ⚠ A cross-resource reference with no reader (rule 2), and one that would need the OTHER
        // group's rendered object name rather than the tenant's own spelling.
        rule.ContainsKey("remoteSecurityGroup").ShouldBeFalse();
        rule.ContainsKey("localAddress").ShouldBeFalse();
    }

    // ── The port grammar, which is the reshape's claim ───────────────────────────────────────────

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("99999")]
    [InlineData("443,0")]
    [InlineData("8000-70000")]
    [InlineData("443,")]
    [InlineData("http")]
    [InlineData("443 ")]
    [InlineData("-443")]
    public void TheSchemaRefusesAPortItShouldNeverSee(string ports) {
        // ⚠ THE HALF THAT MAKES THE RESHAPE BETTER THAN THE ARRAY IT REPLACES, AND IT IS CHECKED
        // THROUGH ResourceSchema.Validate RATHER THAN AGAINST THE REGEX. An `array` of
        // SchemaKind.WholeNumber could carry NO bounds at all — Minimum/Maximum are PROPERTY
        // constraints and there is no per-element bounds member — and ADR-012's fifth surface refuses
        // `@pattern` on an array outright. So a patterned string validates strictly more, at the API,
        // with a JSON Pointer, before the write path answers 202.
        using var body = JsonDocument.Parse(
            NetworkSecurityGroups.Body(ClusterId, ingressTcpPorts: ports)
        );

        var validated = NetworkSecurityGroups.Schema2026.Validate(body.RootElement);

        validated.IsSuccess.ShouldBeFalse($"'{ports}' should be refused at the API");
    }

    [Theory]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("65535")]
    [InlineData("80,443")]
    [InlineData("8000-8100")]
    [InlineData("53,80,443,8000-8100,9090")]
    [InlineData("1-65535")]
    public void TheSchemaAcceptsAPortListAKubeOvnRuleWouldAccept(string ports) {
        using var body = JsonDocument.Parse(
            NetworkSecurityGroups.Body(ClusterId, ingressTcpPorts: ports)
        );

        NetworkSecurityGroups.Schema2026.Validate(body.RootElement).IsSuccess.ShouldBeTrue(ports);
    }

    [Fact]
    public void ABackwardsRangeIsTheOneThingLeftForTheReconciler() {
        // ⚠ THE FAMILY'S RECURRING DEFECT AT ITS NARROWEST, PINNED AS BOTH HALVES. The SHAPE passes
        // the schema — `443-80` is two well-formed ports — and only the RELATION between them is
        // wrong, which no SchemaProperty constraint sees because every one of them compares one value
        // against a constant. So the schema accepts it and PortProblem refuses it, terminally, in the
        // reconciler.
        using var body = JsonDocument.Parse(
            NetworkSecurityGroups.Body(ClusterId, ingressTcpPorts: "443-80")
        );

        NetworkSecurityGroups.Schema2026.Validate(body.RootElement).IsSuccess.ShouldBeTrue(
            "a backwards range is two well-formed ports, so the pattern cannot refuse it — if this "
            + "goes red the pattern started encoding a relation and § owed's "
            + "`a-backwards-port-range-is-refused-after-202` can be closed"
        );

        var problem = NetworkSecurityGroups.PortProblem(body.RootElement);

        problem.ShouldNotBeNull();

        // ⚠ docs/plan/08 § Errors: the message names the actual numbers and the pointer, because
        // "your rule is invalid" is a message whose only next step is to guess.
        problem.ShouldContain("/properties/ingress/tcpPorts");
        problem.ShouldContain("443");
        problem.ShouldContain("80");
    }

    [Fact]
    public void PortProblemFindsAProblemInEitherDirectionAndEitherProtocol() {
        // ⚠ A check that is right about the first list and wrong about the fourth is the one a
        // single-case test misses. There are four lists.
        foreach (var (pointer, body) in
                 (ReadOnlySpan<(string, string)>)[
                     ("/properties/ingress/tcpPorts",
                         NetworkSecurityGroups.Body(ClusterId, ingressTcpPorts: "9-8")),
                     ("/properties/egress/tcpPorts",
                         NetworkSecurityGroups.Body(ClusterId, egressTcpPorts: "9-8")),
                     ("/properties/ingress/udpPorts", WithIngress(remoteV4: "10.0.0.0/8", udpPorts: "9-8")),
                     ("/properties/egress/udpPorts", WithEgress(remoteV4: "10.0.0.0/8", udpPorts: "9-8"))
                 ]) {
            using var parsed = JsonDocument.Parse(body);

            NetworkSecurityGroups.PortProblem(parsed.RootElement).ShouldNotBeNull(pointer)
                .ShouldContain(pointer);
        }
    }

    // ── Matches: containment, and the count ──────────────────────────────────────────────────────

    [Fact]
    public void MatchesAcceptsAnObjectCarryingFieldsThisProviderNeverSent() {
        // ⚠ CONTAINMENT, AND FOR ONCE NOT BECAUSE THE CONTROLLER REWRITES THE SPEC — IT DOES NOT.
        // Every write pkg/controller/security_group.go makes is patchSgStatus, a merge patch against
        // the `status` subresource. Containment is used anyway because a finalizer, a field a later
        // Kube-OVN adds, or another field manager's addition is not drift in what was asked for.
        using var body = JsonDocument.Parse(WithIngress(remoteV4: "10.0.0.0/8", tcpPorts: "443"));

        var document = JsonNode.Parse(
            NetworkSecurityGroups.SecurityGroupJson("ns", Address(), body.RootElement)
        )!.AsObject();

        document["spec"]!["tier"] = 0;
        document["status"] = new JsonObject {
            ["portGroup"] = "ovn.sg.ns_net_web", ["ingressMd5"] = "abc"
        };
        document["metadata"]!["finalizers"] = new JsonArray("kubeovn.io/sg");

        NetworkSecurityGroups.Matches(document.ToJsonString(), body.RootElement).ShouldBeTrue();
    }

    [Fact]
    public void MatchesRejectsARuleThatWasAddedRemovedOrRewritten() {
        using var body = JsonDocument.Parse(
            WithIngress(remoteV4: "10.0.0.0/8", tcpPorts: "80,443")
        );

        var rendered = NetworkSecurityGroups.SecurityGroupJson("ns", Address(), body.RootElement);

        NetworkSecurityGroups.Matches(rendered, body.RootElement).ShouldBeTrue();

        // ⚠ ONE MORE RULE. A longer array is a rule somebody else added — on a firewall, silently
        // accepting that is the worst available outcome.
        var extra = JsonNode.Parse(rendered)!.AsObject();
        extra["spec"]!["ingressRules"]!.AsArray().Add(
            new JsonObject {
                ["ipVersion"] = "ipv4",
                ["protocol"] = "tcp",
                ["priority"] = 1,
                ["remoteType"] = "address",
                ["remoteAddress"] = "0.0.0.0/0",
                ["policy"] = "allow",
                ["portRangeMin"] = 22,
                ["portRangeMax"] = 22
            }
        );

        NetworkSecurityGroups.Matches(extra.ToJsonString(), body.RootElement).ShouldBeFalse();

        // ⚠ ONE FEWER. A shorter array is a rule that was dropped, which on an ALLOW list fails
        // closed — but it is still drift, and reporting Converged on it would hide it forever.
        var fewer = JsonNode.Parse(rendered)!.AsObject();
        fewer["spec"]!["ingressRules"]!.AsArray().RemoveAt(0);

        NetworkSecurityGroups.Matches(fewer.ToJsonString(), body.RootElement).ShouldBeFalse();

        // ⚠ THE SAME COUNT AND A WIDENED PORT — the change an attacker would make and the one a
        // count-only comparison would miss.
        var widened = JsonNode.Parse(rendered)!.AsObject();
        widened["spec"]!["ingressRules"]!.AsArray()[0]!["portRangeMax"] = 65535;

        NetworkSecurityGroups.Matches(widened.ToJsonString(), body.RootElement).ShouldBeFalse();

        // ⚠ THE SAME RULES AND A WIDENED REMOTE.
        var opened = JsonNode.Parse(rendered)!.AsObject();
        opened["spec"]!["ingressRules"]!.AsArray()[0]!["remoteAddress"] = "0.0.0.0/0";

        NetworkSecurityGroups.Matches(opened.ToJsonString(), body.RootElement).ShouldBeFalse();

        // ⚠ AND THE ONE FIELD THAT IS NOT A RULE.
        var same = JsonNode.Parse(rendered)!.AsObject();
        same["spec"]!["allowSameGroupTraffic"] = true;

        NetworkSecurityGroups.Matches(same.ToJsonString(), body.RootElement).ShouldBeFalse();
    }

    [Fact]
    public void AnAbsentRuleArrayAndAnEmptyOneAreTheSameThing() {
        // ⚠ Go's `omitempty` on ingressRules means a group with no inbound rules round-trips as a
        // MISSING KEY rather than as `[]`. Treating those as different would make every egress-only
        // group report drift forever, which is the reconciler never leaving InProgress.
        using var body = JsonDocument.Parse(Empty());

        var document = JsonNode.Parse(
            NetworkSecurityGroups.SecurityGroupJson("ns", Address(), body.RootElement)
        )!.AsObject();

        document["spec"]!.AsObject().Remove("ingressRules");
        document["spec"]!.AsObject().Remove("egressRules");

        NetworkSecurityGroups.Matches(document.ToJsonString(), body.RootElement).ShouldBeTrue();
    }

    [Fact]
    public void MatchesRefusesAnObjectOfTheWrongKindOrNoJsonAtAll() {
        using var body = JsonDocument.Parse(Empty());

        NetworkSecurityGroups.Matches("not json", body.RootElement).ShouldBeFalse();
        NetworkSecurityGroups.Matches("{}", body.RootElement).ShouldBeFalse();

        NetworkSecurityGroups.Matches(
            new JsonObject { ["kind"] = "Subnet", ["spec"] = new JsonObject() }.ToJsonString(),
            body.RootElement
        ).ShouldBeFalse();
    }

    // ── The object's name, which is the only thing separating two networks' groups ───────────────

    [Fact]
    public void TheObjectNameCarriesTheNamespaceTheNetworkAndTheGroup() {
        NetworkSecurityGroups.ObjectNameOf("ns", Address()).ShouldBe("ns-net-web");

        // ⚠ An address with no parent throws rather than rendering a two-component name. A Subnet at
        // least carries `spec.vpc`, so a collision there would be visible in the object; a
        // SecurityGroup names nothing, so two networks' groups called `web` would merge into one OVN
        // port group with nothing reporting an error anywhere.
        var orphan = new ResourceId(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "prod",
            VirtualNetworks.Type,
            "web",
            Guid.NewGuid()
        );

        Should.Throw<ArgumentException>(() => NetworkSecurityGroups.ObjectNameOf("ns", orphan));
    }

    // ── The action's response, which is the reshape's other half ─────────────────────────────────

    [Fact]
    public async Task ShowEffectiveRulesPublishesTheExpansionAndSatisfiesItsOwnSchema() {
        // ⚠ THE RESPONSE IS CHECKED AGAINST THE DECLARED SCHEMA HERE FOR THE REASON ActionDispatcher
        // checks it in production: the schema is what the OpenAPI document, the SDK and the portal
        // form are generated from, and a handler drifting from it publishes a contract nothing
        // honours. The dispatcher's failure is a 500; this one is a test.
        using var body = JsonDocument.Parse(
            WithIngress(remoteV4: "10.0.0.0/8", remoteV6: "fd00::/8", tcpPorts: "80,443")
        );

        var handler = new ShowEffectiveRulesHandler();

        handler.Type.ShouldBe(NetworkSecurityGroups.Type);
        handler.Action.ShouldBe(NetworkSecurityGroups.EffectiveRulesAction);

        var invoked = await handler.InvokeAsync(
            Context(body.RootElement),
            TestContext.Current.CancellationToken
        );

        invoked.IsSuccess.ShouldBeTrue();

        using var response = JsonDocument.Parse(invoked.GetValueOrThrow());

        NetworkSecurityGroups.EffectiveRulesResponse
            .Validate(response.RootElement)
            .IsSuccess
            .ShouldBeTrue();

        response.RootElement.GetProperty("count").GetInt32().ShouldBe(4);
        response.RootElement.GetProperty("defaultAction").GetString().ShouldBe("drop");

        var rules = response.RootElement.GetProperty("rules");

        rules.GetArrayLength().ShouldBe(4);

        // ⚠ The sentence has to name the direction, the protocol, the port and the remote, because it
        // is the ONLY form the four columns leave in — SchemaProperty.ElementKind refuses an array of
        // objects on a response schema exactly as on a request one.
        var first = rules[0].GetString().ShouldNotBeNull();

        first.ShouldContain("ingress");
        first.ShouldContain("tcp");
        first.ShouldContain("80");
        first.ShouldContain("10.0.0.0/8");
    }

    [Fact]
    public async Task ShowEffectiveRulesOnAnEmptyGroupSaysSoRatherThanFailing() {
        // ⚠ An empty list is a COMPLETE configuration here rather than an unfinished one, and the
        // response has to read that way — "0 rules, default drop" is the answer, not an error.
        using var body = JsonDocument.Parse(Empty());

        var invoked = await new ShowEffectiveRulesHandler().InvokeAsync(
            Context(body.RootElement),
            TestContext.Current.CancellationToken
        );

        using var response = JsonDocument.Parse(invoked.GetValueOrThrow());

        response.RootElement.GetProperty("count").GetInt32().ShouldBe(0);
        response.RootElement.GetProperty("rules").GetArrayLength().ShouldBe(0);
        response.RootElement.GetProperty("defaultAction").GetString().ShouldBe("drop");

        NetworkSecurityGroups.EffectiveRulesResponse
            .Validate(response.RootElement)
            .IsSuccess
            .ShouldBeTrue();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────

    static ResourceId Address() =>
        new(
            Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001"),
            Guid.Parse("aaaaaaaa-0000-4000-8000-00000000000a"),
            "prod",
            NetworkSecurityGroups.Type,
            "web",
            Guid.Parse("dddddddd-0000-4000-8000-000000000004"),
            "net"
        );

    static ActionContext Context(JsonElement desired) =>
        new(
            Address(),
            NetworkSecurityGroups.V2026,
            NetworkSecurityGroups.EffectiveRulesAction,
            desired,
            desired,
            "ns",
            null,
            new RefusingSecrets()
        );

    static JsonObject SpecOf(JsonElement desired) =>
        JsonNode.Parse(
            NetworkSecurityGroups.SecurityGroupJson("ns", Address(), desired)
        )!["spec"]!.AsObject();

    static JsonObject SpecOf(string body) {
        using var parsed = JsonDocument.Parse(body);
        return SpecOf(parsed.RootElement);
    }

    /// <summary>A body with nothing allowed in either direction.</summary>
    static string Empty() =>
        NetworkSecurityGroups.Body(
            ClusterId,
            ingressRemoteV4: "",
            ingressTcpPorts: "",
            egressRemoteV4: "",
            egressTcpPorts: ""
        );

    /// <summary>A body whose inbound section is exactly the arguments and whose outbound is empty.</summary>
    static string WithIngress(
        string remoteV4 = "",
        string remoteV6 = "",
        string tcpPorts = "",
        string udpPorts = "",
        bool allowIcmp = false
    ) => Section(NetworkSecurityGroups.Ingress, remoteV4, remoteV6, tcpPorts, udpPorts, allowIcmp);

    /// <summary>A body whose outbound section is exactly the arguments and whose inbound is empty.</summary>
    static string WithEgress(
        string remoteV4 = "",
        string remoteV6 = "",
        string tcpPorts = "",
        string udpPorts = "",
        bool allowIcmp = false
    ) => Section(NetworkSecurityGroups.Egress, remoteV4, remoteV6, tcpPorts, udpPorts, allowIcmp);

    static string Section(
        string direction,
        string remoteV4,
        string remoteV6,
        string tcpPorts,
        string udpPorts,
        bool allowIcmp
    ) {
        var body = JsonNode.Parse(Empty())!.AsObject();

        body["properties"]![direction] = new JsonObject {
            ["remoteV4"] = remoteV4,
            ["remoteV6"] = remoteV6,
            ["tcpPorts"] = tcpPorts,
            ["udpPorts"] = udpPorts,
            ["allowIcmp"] = allowIcmp
        };

        return body.ToJsonString();
    }
}

/// <summary>
///     A secret resolver that refuses everything.
/// </summary>
/// <remarks>
///     ⚠ Rule 2 forbids a provider test assembly from referencing another provider's, so this cannot
///     be shared however identical it looks. It refuses rather than returning empty for
///     <c>UnavailableSecretResolver</c>'s reason: none of this family's actions reads a secret, so a
///     resolver that answered would hide a handler that started to.
/// </remarks>
sealed class RefusingSecrets : ISecretResolver {
    public Task<Result<string>> ResolveAsync(
        SecretRef reference,
        CancellationToken cancellationToken = default
    ) =>
        Task.FromResult(
            Result<string>.Failure(
                ErrorCode.InternalError,
                "no action in CyberCloud.Network reads a secret, so this test resolver refuses rather "
                + "than answering. A handler that started reading one should fail here loudly."
            )
        );
}
