using CyberCloud.ResourceManager;
using CyberCloud.ResourceManager.Conformance;
using CyberCloud.ResourceManager.Reconcile;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Network.Tests;

/// <summary>
///     <c>CyberCloud.Network/virtualNetworks/natGateways</c> — the two joins, the convergence
///     predicate against a controller-shaped read-back, and the one thing the shared suite cannot
///     see: that the rule binds the subnet and the address of <i>this</i> resource group and network.
/// </summary>
/// <remarks>
///     ⚠ <b>Everything about the joins is asserted here and nowhere else.</b> The harness the shared
///     suite runs in resolves no name, and the k3s the cluster-backed suite starts has no Kube-OVN, so
///     an <c>OvnSnatRule</c> naming the wrong subnet or the wrong address looks identical to a correct
///     one in both. These tests compare the rendered names against the sibling types' own naming
///     functions with real addresses, including the collisions the harness cannot build.
/// </remarks>
public sealed class NetworkNatGatewayTests {
    static readonly Guid TenantOne = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000021");
    static readonly Guid TenantTwo = Guid.Parse("bbbbbbbb-0000-4000-8000-000000000022");
    static readonly Guid SubscriptionOne = Guid.Parse("aaaaaaaa-0000-4000-8000-00000000002a");
    static readonly Guid SubscriptionTwo = Guid.Parse("bbbbbbbb-0000-4000-8000-00000000002b");
    static readonly Guid Cluster = Guid.Parse("cccccccc-0000-4000-8000-000000000003");

    // ── The joins ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheRuleNamesTheSubnetAndTheAddressExactlyAsTheirOwnTypesNameThem() {
        // ⚠ THE ASSERTION THIS TYPE EXISTS FOR. An OvnSnatRule is a join by NAME: spec.vpcSubnet must
        // be the string NetworkSubnets.ObjectNameOf renders for that subnet and spec.ovnEip the string
        // PublicIpAddresses.ObjectNameOf renders for that address, or the fabric answers "failed to
        // get vpc subnet" / "failed to get eip" forever and the gateway never becomes ready. Comparing
        // against the sibling types' functions rather than against literals is what keeps this true
        // the day either naming changes.
        var gateway = Address("egress", TenantOne, SubscriptionOne, network: "prod");
        var ns = ReconcileDriver.NamespaceFor(gateway);

        using var body = JsonDocument.Parse(NatGateways.Body(Cluster, subnet: "db", publicIpAddress: "edge"));

        var spec = Spec(NatGateways.OvnSnatRuleJson(ns, gateway, body.RootElement));

        var subnet = new ResourceId(
            TenantOne,
            SubscriptionOne,
            "rg",
            NetworkSubnets.Type,
            "db",
            Guid.NewGuid(),
            "prod"
        );

        spec["vpcSubnet"]!.GetValue<string>().ShouldBe(NetworkSubnets.ObjectNameOf(ns, subnet));
        spec["ovnEip"]!.GetValue<string>().ShouldBe(PublicIpAddresses.ObjectNameOf(ns, "edge"));
        spec["vpc"]!.GetValue<string>().ShouldBe(VirtualNetworks.ObjectNameOf(ns, "prod"));

        // ⚠ The whole spec, enumerated. The three fields NOT sent — v4IpCidr, v6IpCidr, ipName — are
        // each a way of translating a range this resource's address does not vouch for, and an
        // assertion that only checked the fields it knew about would go green the day one was added.
        spec.Select(x => x.Key).Order(StringComparer.Ordinal).ShouldBe(["ovnEip", "vpc", "vpcSubnet"]);
    }

    [Fact]
    public void TheNetworkHalfOfTheSubnetComesFromTheAddressAndNotFromTheBody() {
        // ⚠ THE WORST FAILURE AVAILABLE ON THIS TYPE, MADE INEXPRESSIBLE. Two networks in one resource
        // group each hold a subnet called `web`; a gateway under `prod` must translate prod's `web`
        // and never staging's, whatever the body says — the body carries only the subnet's own name.
        var underProd = Address("egress", TenantOne, SubscriptionOne, network: "prod");
        var underStaging = Address("egress", TenantOne, SubscriptionOne, network: "staging");
        var ns = ReconcileDriver.NamespaceFor(underProd);

        using var body = JsonDocument.Parse(NatGateways.Body(Cluster));

        var prod = Spec(NatGateways.OvnSnatRuleJson(ns, underProd, body.RootElement));
        var staging = Spec(NatGateways.OvnSnatRuleJson(ns, underStaging, body.RootElement));

        prod["vpcSubnet"]!.GetValue<string>().ShouldBe(ns + "-prod-web");
        staging["vpcSubnet"]!.GetValue<string>().ShouldBe(ns + "-staging-web");
        prod["vpc"]!.GetValue<string>().ShouldBe(ns + "-prod");
        staging["vpc"]!.GetValue<string>().ShouldBe(ns + "-staging");

        NatGateways.ObjectNameOf(ns, underProd).ShouldNotBe(NatGateways.ObjectNameOf(ns, underStaging));
    }

    [Fact]
    public void TwoSubscriptionsAddressesOfTheSameNameAreTwoDifferentJoins() {
        // ⚠ The namespace is a component of the OvnEip's name, so a body naming `edge` can only ever
        // name THIS resource group's `edge` — another tenant's address of the same name renders a
        // different object name and cannot be reached from here.
        var alice = Address("egress", TenantOne, SubscriptionOne);
        var bob = Address("egress", TenantTwo, SubscriptionTwo);

        using var body = JsonDocument.Parse(NatGateways.Body(Cluster, publicIpAddress: "edge"));

        NatGateways.OvnEipOf(ReconcileDriver.NamespaceFor(alice), body.RootElement)
            .ShouldNotBe(NatGateways.OvnEipOf(ReconcileDriver.NamespaceFor(bob), body.RootElement));
    }

    [Fact]
    public void AnAddressWithoutAParentRefusesToRenderRatherThanColliding() {
        var orphan = new ResourceId(
            TenantOne,
            SubscriptionOne,
            "rg",
            VirtualNetworks.Type,
            "egress",
            Guid.NewGuid()
        );

        Should.Throw<ArgumentException>(() => NatGateways.ObjectNameOf("ns", orphan));
        Should.Throw<ArgumentException>(() => NatGateways.VpcRefOf("ns", orphan));
    }

    // ── Failure class (a): a reconciler with a field ─────────────────────────────────────────────

    [Fact]
    public void TheNatGatewayReconcilerHoldsNoMutableState() =>
        ReconcilerConformance.CheckNoHiddenState(new NatGatewayReconciler(new FixedClock()))
            .ShouldBeEmpty();

    [Fact]
    public async Task OneNatGatewayReconcilerServesTwoTenantsWithoutMixingThem() {
        // ⚠ THE ONLY TEST THAT CATCHES THE READONLY-MUTABLE-FIELD SHAPE. AddCyberCloudProvider
        // registers a reconciler as a SINGLETON BY CONCRETE TYPE, so one instance serves every
        // tenant — and on this kind a cached join would translate one tenant's subnet out through
        // another tenant's address.
        var reconciler = new NatGatewayReconciler(new FixedClock());
        var connection = new RecordingConnection();

        var alice = Address("egress", TenantOne, SubscriptionOne);
        var bob = Address("egress", TenantTwo, SubscriptionTwo);

        using var aliceBody = JsonDocument.Parse(NatGateways.Body(Cluster, publicIpAddress: "alice-edge"));
        using var bobBody = JsonDocument.Parse(NatGateways.Body(Cluster, publicIpAddress: "bob-edge"));

        await Pass(reconciler, connection, alice, aliceBody.RootElement);
        await Pass(reconciler, connection, bob, bobBody.RootElement);
        await Pass(reconciler, connection, alice, aliceBody.RootElement);
        await Pass(reconciler, connection, bob, bobBody.RootElement);

        var applied = connection.Applied;
        applied.Count.ShouldBe(4);

        // ⚠ The third and fourth passes: a cache populated on pass one is only visible from pass three.
        Spec(applied[2].Body)["ovnEip"]!.GetValue<string>()
            .ShouldBe(ReconcileDriver.NamespaceFor(alice) + "-alice-edge");

        Spec(applied[3].Body)["ovnEip"]!.GetValue<string>()
            .ShouldBe(ReconcileDriver.NamespaceFor(bob) + "-bob-edge");

        applied[0].Target.Name.ShouldNotBe(
            applied[1].Target.Name,
            "two subscriptions' identically-named NAT gateways rendered ONE cluster-scoped OvnSnatRule"
        );
    }

    // ── The convergence predicate, against a controller-shaped read-back ─────────────────────────

    [Fact]
    public void AReadyRuleWithTheControllersLabelsAndFinalizerIsNotDrift() {
        // ⚠ handleAddOvnSnatRule patches two labels, one annotation, a finalizer and the status onto
        // an object this provider applied. An equality comparison would report drift on a converged
        // rule forever; containment on the three spec fields is what converges.
        var gateway = Address("egress", TenantOne, SubscriptionOne);
        var ns = ReconcileDriver.NamespaceFor(gateway);

        using var body = JsonDocument.Parse(NatGateways.Body(Cluster));

        NatGateways.Matches(AfterTheController(ns, gateway, body.RootElement), ns, gateway, body.RootElement)
            .ShouldBeTrue("a rule the controller has marked ready was reported as drift");
    }

    [Fact]
    public void ARuleWhoseSubnetOrAddressWasRewrittenIsDrift() {
        // ⚠ THE OTHER HALF, AND WITHOUT IT THE TEST ABOVE WOULD PASS AGAINST A PREDICATE THAT COMPARED
        // NOTHING. A rule whose vpcSubnet names some other subnet is a translation of a range this
        // tenant did not ask for, out through their address, under their resource id.
        var gateway = Address("egress", TenantOne, SubscriptionOne);
        var ns = ReconcileDriver.NamespaceFor(gateway);

        using var body = JsonDocument.Parse(NatGateways.Body(Cluster));

        foreach (var field in new[] { "vpcSubnet", "ovnEip", "vpc" }) {
            var rewritten = JsonNode.Parse(AfterTheController(ns, gateway, body.RootElement))!.AsObject();
            rewritten["spec"]![field] = "somebody-elses";

            NatGateways.Matches(rewritten.ToJsonString(), ns, gateway, body.RootElement)
                .ShouldBeFalse($"a rewritten spec.{field} was reported as converged");
        }
    }

    [Fact]
    public void AnUnparseableObjectIsDriftRatherThanAThrow() {
        var gateway = Address("egress", TenantOne, SubscriptionOne);

        using var body = JsonDocument.Parse(NatGateways.Body(Cluster));

        NatGateways.Matches("not json", "ns", gateway, body.RootElement).ShouldBeFalse();
        NatGateways.Matches("{}", "ns", gateway, body.RootElement).ShouldBeFalse();
        NatGateways.Status("{}").ShouldBeNull();
    }

    // ── What the API refuses ─────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Web_Tier", "/properties/subnet")]
    [InlineData("-web", "/properties/subnet")]
    [InlineData("web.db", "/properties/subnet")]
    public void AMalformedSubnetNameIsRefusedAtTheApiWithItsOwnPointer(string malformed, string target) {
        var validated = NatGateways.Schema2026.Validate(
            JsonDocument.Parse(NatGateways.Body(Cluster, subnet: malformed)).RootElement
        );

        validated.IsSuccess.ShouldBeFalse($"'{malformed}' was accepted by the schema");
        validated.Error!.Target.ShouldBe(target);
    }

    [Fact]
    public void AMalformedAddressNameIsRefusedAtTheApiWithItsOwnPointer() {
        // ⚠ A resource ID typed where a NAME belongs is the likeliest mistake on this property, because
        // docs/plan/14 says the join is a resource id and every other cloud spells it that way. It is
        // refused by the pattern rather than accepted and resolved to nothing.
        var validated = NatGateways.Schema2026.Validate(
            JsonDocument.Parse(
                NatGateways.Body(Cluster, publicIpAddress: "/subscriptions/x/resourceGroups/rg/providers/edge")
            ).RootElement
        );

        validated.IsSuccess.ShouldBeFalse();
        validated.Error!.Target.ShouldBe("/properties/publicIpAddress");
    }

    [Fact]
    public void TheDefaultBodyIsAcceptedAndTheDefaultsAreTheFamilysOwnFixtures() {
        // ⚠ helm lint renders the generated chart with these literals, so they have to satisfy their
        // own patterns — and `web` is the subnet every other fixture in this family sits on, so the
        // fixtures read as one network rather than as an illustration of a gap.
        NatGateways.Schema2026
            .Validate(JsonDocument.Parse(NatGateways.Body(Cluster)).RootElement)
            .IsSuccess.ShouldBeTrue();

        NatGateways.DefaultSubnet.ShouldBe(LoadBalancers.DefaultSubnet);
    }

    [Fact]
    public void BothBodyPropertiesAreDeclaredImmutableBecauseTheControllerRefusesEveryChange() {
        // ⚠ handleUpdateOvnSnatRule refuses "vpc changed", "v4 eip changed", "v4 ip cidr changed" —
        // every effective change — once the rule is ready. The declaration is the honest shape even
        // though SchemaProperty.Immutable is not enforced by the manager; a schema that marked them
        // mutable would be promising an update the fabric refuses.
        foreach (var pointer in new[] { "/properties/subnet", "/properties/publicIpAddress" }) {
            NatGateways.Schema2026.Properties.Single(x => x.JsonPointer == pointer)
                .Immutable.ShouldBeTrue(pointer);
        }
    }

    // ── The reconciler's outcomes ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ARuleThatReadsBackAsAppliedConverges() {
        var reconciler = new NatGatewayReconciler(new FixedClock());
        var connection = new RecordingConnection();

        using var body = JsonDocument.Parse(NatGateways.Body(Cluster));

        var outcome = await Pass(reconciler, connection, Address("egress", TenantOne, SubscriptionOne), body.RootElement);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.Converged);
        connection.Applied.Single().Target.Kind.Plural.ShouldBe("ovn-snat-rules");
        connection.Applied.Single().Target.IsClusterScoped.ShouldBeTrue();
    }

    [Fact]
    public async Task AConflictIsInProgressAndNothingIsForced() {
        var reconciler = new NatGatewayReconciler(new FixedClock());
        var connection = new RecordingConnection { ConflictField = "spec.vpcSubnet" };

        using var body = JsonDocument.Parse(NatGateways.Body(Cluster));

        var outcome = await Pass(reconciler, connection, Address("egress", TenantOne, SubscriptionOne), body.RootElement);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.InProgress);
        connection.Applied.Single().Force.ShouldBeFalse();
    }

    [Fact]
    public async Task DeletingRemovesTheRuleAndTouchesNothingElse() {
        // ⚠ The address is left alone: deleting a gateway is not deleting an address, and the
        // controller clears the OvnEip's status.nat itself when the rule's finalizer drops.
        var reconciler = new NatGatewayReconciler(new FixedClock());
        var connection = new RecordingConnection();
        var gateway = Address("egress", TenantOne, SubscriptionOne);

        using var body = JsonDocument.Parse(NatGateways.Body(Cluster));

        await Pass(reconciler, connection, gateway, body.RootElement);

        var deleted = await reconciler.DeleteAsync(Context(gateway, body.RootElement, connection), TestContext.Current.CancellationToken);

        deleted.Kind.ShouldBe(ReconcileOutcomeKind.Converged);
        connection.Deleted.Single().Kind.Kind.ShouldBe("OvnSnatRule");
        connection.Objects.ShouldBeEmpty();
    }

    // ── The action ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ShowEgressReportsTheResolvedJoinOffTheStatus() {
        var gateway = Address("egress", TenantOne, SubscriptionOne);
        var ns = ReconcileDriver.NamespaceFor(gateway);
        var connection = new RecordingConnection();

        using var body = JsonDocument.Parse(NatGateways.Body(Cluster));

        connection.Objects[RecordingConnection.Key(NatGateways.OvnSnatRuleRef(ns, gateway))] =
            AfterTheController(ns, gateway, body.RootElement);

        var handler = new ShowEgressHandler(new FixedClock());

        var answer = await handler.InvokeAsync(
            new(
                gateway,
                NatGateways.V2026,
                NatGateways.EgressAction,
                JsonDocument.Parse("{}").RootElement,
                body.RootElement,
                ns,
                connection,
                new UnavailableSecretResolver()
            ),
            TestContext.Current.CancellationToken
        );

        var response = JsonNode.Parse(answer.GetValueOrThrow())!.AsObject();

        response["publicV4"]!.GetValue<string>().ShouldBe("192.0.2.7");
        response["sourceV4"]!.GetValue<string>().ShouldBe("10.20.1.0/24");
        response["ready"]!.GetValue<bool>().ShouldBeTrue();

        // ⚠ And the response satisfies the schema the action declares, which is what the SDK and the
        // portal are generated from.
        NatGateways.EgressResponse.Validate(JsonDocument.Parse(answer.GetValueOrThrow()).RootElement)
            .IsSuccess.ShouldBeTrue();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     An <c>OvnSnatRule</c> as it reads back once <c>handleAddOvnSnatRule</c> has run.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Hand-written, because there is no controller in any harness this repository runs.</b>
    ///     Every addition is one the controller makes on an object this platform applied: the
    ///     <c>ovn.kubernetes.io/eip_v4_ip</c> label, the <c>ovn.kubernetes.io/vpc_eip</c> annotation,
    ///     the finalizer, and the status. ⚠ <b>The spec is untouched</b>, which is the first kind in
    ///     this family for which that is true.
    /// </remarks>
    static string AfterTheController(string ns, ResourceId gateway, JsonElement desired) {
        var applied = JsonNode.Parse(NatGateways.OvnSnatRuleJson(ns, gateway, desired))!.AsObject();

        applied["metadata"]!["labels"] = new JsonObject { ["ovn.kubernetes.io/eip_v4_ip"] = "192.0.2.7" };
        applied["metadata"]!["annotations"] = new JsonObject { ["ovn.kubernetes.io/vpc_eip"] = NatGateways.OvnEipOf(ns, desired) };
        applied["metadata"]!["finalizers"] = new JsonArray("kubeovn.io/kube-ovn-controller");

        applied["status"] = new JsonObject {
            ["ready"] = true,
            ["vpc"] = NatGateways.VpcRefOf(ns, gateway),
            ["v4Eip"] = "192.0.2.7",
            ["v6Eip"] = string.Empty,
            ["v4IpCidr"] = "10.20.1.0/24",
            ["v6IpCidr"] = string.Empty
        };

        return applied.ToJsonString();
    }

    static async Task<ReconcileOutcome> Pass(
        NatGatewayReconciler reconciler,
        RecordingConnection connection,
        ResourceId address,
        JsonElement desired
    ) =>
        await reconciler.ReconcileAsync(Context(address, desired, connection), TestContext.Current.CancellationToken);

    static ReconcileContext Context(ResourceId address, JsonElement desired, RecordingConnection connection) =>
        new(
            address,
            NatGateways.V2026,
            desired,
            null,
            ReconcileDriver.NamespaceFor(address),
            connection,
            new UnavailableSecretResolver(),
            new NullLog()
        );

    static ResourceId Address(string name, Guid tenant, Guid subscription, string network = "net") =>
        new(
            tenant,
            subscription,
            "rg",
            NatGateways.Type,
            name,
            Guid.Parse("55555555-5555-4555-8555-555555555555"),
            network
        );

    static JsonObject Spec(string objectJson) => JsonNode.Parse(objectJson)!["spec"]!.AsObject();
}
