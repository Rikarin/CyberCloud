using CyberCloud.ResourceManager;
using CyberCloud.ResourceManager.Conformance;
using CyberCloud.ResourceManager.Reconcile;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Network.Tests;

/// <summary>
///     <c>CyberCloud.Network/virtualNetworks/peerings</c> — the two fragments, the range rule the
///     schema cannot state, the convergence predicate against a controller-shaped read-back, and the
///     co-writer's outcomes over a connection that models a second manager.
/// </summary>
/// <remarks>
///     ⚠ <b>Everything about the fragments' shape is asserted here and nowhere else.</b> The shared
///     suite's fake stores what the co-owned apply produced and the k3s the cluster-backed suite
///     starts has no Kube-OVN, so a fragment naming the wrong <c>Vpc</c>, or a route pointing at the
///     wrong end of the link, converges in both. These tests compare each entry against the sibling
///     types' naming functions and against the link arithmetic, on both sides, with real addresses.
/// </remarks>
public sealed class NetworkPeeringTests {
    static readonly Guid TenantOne = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000031");
    static readonly Guid SubscriptionOne = Guid.Parse("aaaaaaaa-0000-4000-8000-00000000003a");
    static readonly Guid SubscriptionTwo = Guid.Parse("bbbbbbbb-0000-4000-8000-00000000003b");
    static readonly Guid Cluster = Guid.Parse("cccccccc-0000-4000-8000-000000000003");
    static readonly Guid HubId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    static readonly Guid SpokeId = Guid.Parse("22222222-2222-4222-8222-222222222222");

    // ── The fragments ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheTwoFragmentsAreMirrorImagesAndEachNamesTheOtherNetworksVpc() {
        // ⚠ THE ASSERTION THIS TYPE EXISTS FOR. Kube-OVN builds a peer port named {vpc}-{remoteVpc}
        // on each router and peers it with {remoteVpc}-{vpc}; the names have to be the exact strings
        // VirtualNetworks.ObjectNameOf renders for each network, and the two routes have to point at
        // the OTHER side's link address. A fragment that named the tenant's short name, or routed to
        // its own port, would be accepted by the API server and connect nothing.
        var peering = Address("to-spoke", TenantOne, SubscriptionOne, network: "hub");
        var ns = ReconcileDriver.NamespaceFor(peering);

        using var body = JsonDocument.Parse(VirtualNetworkPeerings.Body(Cluster));

        var local = JsonNode.Parse(VirtualNetworkPeerings.LocalFragmentJson(ns, peering, body.RootElement))!["spec"]!;
        var remote = JsonNode.Parse(VirtualNetworkPeerings.RemoteFragmentJson(ns, peering, body.RootElement))!["spec"]!;

        local["vpcPeerings"]![0]!["remoteVpc"]!.GetValue<string>().ShouldBe(VirtualNetworks.ObjectNameOf(ns, "spoke"));
        local["vpcPeerings"]![0]!["localConnectIP"]!.GetValue<string>().ShouldBe("10.255.255.1/30");
        local["staticRoutes"]![0]!["cidr"]!.GetValue<string>().ShouldBe(VirtualNetworkPeerings.DefaultRemoteAddressSpace);
        local["staticRoutes"]![0]!["nextHopIP"]!.GetValue<string>().ShouldBe("10.255.255.2");
        local["staticRoutes"]![0]!["policy"]!.GetValue<string>().ShouldBe("policyDst");

        remote["vpcPeerings"]![0]!["remoteVpc"]!.GetValue<string>().ShouldBe(VirtualNetworks.ObjectNameOf(ns, "hub"));
        remote["vpcPeerings"]![0]!["localConnectIP"]!.GetValue<string>().ShouldBe("10.255.255.2/30");
        remote["staticRoutes"]![0]!["cidr"]!.GetValue<string>().ShouldBe(VirtualNetworkPeerings.DefaultLocalAddressSpace);
        remote["staticRoutes"]![0]!["nextHopIP"]!.GetValue<string>().ShouldBe("10.255.255.1");

        // ⚠ And a fragment carries no identity: the builder refuses kind, metadata and labels by
        // name, and a fragment that carried them would be refused on every pass.
        foreach (var fragment in new[] { local.Parent!, remote.Parent! }) {
            fragment.AsObject().ContainsKey("metadata").ShouldBeFalse();
            fragment.AsObject().ContainsKey("kind").ShouldBeFalse();
        }
    }

    [Fact]
    public void TheRemoteIsQualifiedWithThisResourcesNamespaceSoAnotherSubscriptionsNetworkCannotBeNamed() {
        // ⚠ Two subscriptions each hold a network called `spoke`, and two peerings each name `spoke`.
        // The remote Vpc each writes onto is its OWN subscription's, because the qualifier is the
        // peering's namespace and not anything in the body — which is the whole of what keeps a body
        // from injecting routes into a router in another subscription.
        var alice = Address("to-spoke", TenantOne, SubscriptionOne);
        var bob = Address("to-spoke", TenantOne, SubscriptionTwo);

        using var body = JsonDocument.Parse(VirtualNetworkPeerings.Body(Cluster));

        var aliceRemote = VirtualNetworkPeerings.RemoteVpcRef(ReconcileDriver.NamespaceFor(alice), body.RootElement);
        var bobRemote = VirtualNetworkPeerings.RemoteVpcRef(ReconcileDriver.NamespaceFor(bob), body.RootElement);

        aliceRemote.Name.ShouldNotBe(bobRemote.Name);
        aliceRemote.Name.ShouldBe(VirtualNetworks.VpcRef(ReconcileDriver.NamespaceFor(alice), "spoke").Name);
        aliceRemote.IsClusterScoped.ShouldBeTrue("a Vpc is cluster-scoped; the namespace is in the name");
    }

    [Fact]
    public void APeeringWithoutAParentRefusesToRenderRatherThanGuessingALocalNetwork() {
        // ⚠ A top-level address, because ResourceId itself refuses a child type with no parent name —
        // so the only way an address with no parent reaches these functions is under a different type.
        var orphan = new ResourceId(TenantOne, SubscriptionOne, "rg", VirtualNetworks.Type, "to-spoke", Guid.NewGuid());

        Should.Throw<ArgumentException>(() => VirtualNetworkPeerings.NetworkOf(orphan)).Message.ShouldContain("child type");
    }

    // ── The range rule the schema cannot state ───────────────────────────────────────────────────

    [Theory]
    [InlineData("10.20.0.0/16", "10.20.128.0/17", "10.255.255.0/30", "/properties/localAddressSpace/v4")]
    [InlineData("10.20.0.0/16", "10.30.0.0/16", "10.20.0.0/30", "/properties/localAddressSpace/v4")]
    [InlineData("10.20.0.0/16", "10.30.0.0/16", "10.30.1.0/30", "/properties/remoteAddressSpace/v4")]
    public void OverlappingRangesAreRefusedNamingBothPointers(string local, string remote, string link, string firstPointer) {
        var peering = Address("to-spoke", TenantOne, SubscriptionOne);

        using var body = JsonDocument.Parse(
            VirtualNetworkPeerings.Body(Cluster, localAddressSpaceV4: local, remoteAddressSpaceV4: remote, linkV4: link)
        );

        var problem = VirtualNetworkPeerings.AddressProblem(peering, body.RootElement);

        problem.ShouldNotBeNull();
        problem.ShouldContain(firstPointer);
        problem.ShouldContain("overlap");
        // docs/plan/08 § Errors: the message names the actual values, not only the rule.
        problem.ShouldContain(local == remote ? local : link == "10.255.255.0/30" ? remote : link);
    }

    [Fact]
    public void ALinkNarrowerThanA30IsRefusedBecauseItHasNoTwoHostAddresses() {
        var peering = Address("to-spoke", TenantOne, SubscriptionOne);

        using var body = JsonDocument.Parse(VirtualNetworkPeerings.Body(Cluster, linkV4: "10.255.255.0/31"));

        var problem = VirtualNetworkPeerings.AddressProblem(peering, body.RootElement);

        problem.ShouldNotBeNull();
        problem.ShouldContain("/properties/link/v4");
        problem.ShouldContain("/30");
    }

    [Fact]
    public void ALinkInAReservedRangeIsRefusedByTheReservedListLikeAnyOtherRange() {
        // ⚠ 169.254.0.0/16 is the link-local block, and it is the range Kube-OVN's own documentation
        // uses for a peering link. It is refused here because the platform's reserved list refuses
        // it for every range a tenant declares, and a peering's link is a range a tenant declares.
        var peering = Address("to-spoke", TenantOne, SubscriptionOne);

        using var body = JsonDocument.Parse(VirtualNetworkPeerings.Body(Cluster, linkV4: "169.254.0.0/30"));

        var problem = VirtualNetworkPeerings.AddressProblem(peering, body.RootElement);

        problem.ShouldNotBeNull();
        problem.ShouldContain("link-local");
    }

    [Fact]
    public void ANetworkCannotPeerWithItself() {
        var peering = Address("to-self", TenantOne, SubscriptionOne, network: "hub");

        using var body = JsonDocument.Parse(VirtualNetworkPeerings.Body(Cluster, remoteNetwork: "hub"));

        VirtualNetworkPeerings.AddressProblem(peering, body.RootElement).ShouldNotBeNull().ShouldContain("own network");
    }

    [Fact]
    public void TheDefaultBodyIsAcceptedAndItsThreeRangesAreDisjointAndUnreserved() {
        // ⚠ helm lint renders the generated chart with these literals, so they have to satisfy their
        // own patterns; and the conformance case converges with them, so they have to pass the
        // reconciler's own rule in every region the reserved list knows.
        var peering = Address("to-spoke", TenantOne, SubscriptionOne);

        using var body = JsonDocument.Parse(VirtualNetworkPeerings.Body(Cluster));

        VirtualNetworkPeerings.Schema2026.Validate(body.RootElement).IsSuccess.ShouldBeTrue();
        VirtualNetworkPeerings.AddressProblem(peering, body.RootElement).ShouldBeNull();

        foreach (var region in NetworkAddressing.ReservedRanges.Select(x => x.Region).Distinct()) {
            using var elsewhere = JsonDocument.Parse(VirtualNetworkPeerings.Body(Cluster, location: region.Length == 0 ? "eu-central" : region));
            VirtualNetworkPeerings.AddressProblem(peering, elsewhere.RootElement).ShouldBeNull(region);
        }

        VirtualNetworkPeerings.DefaultLocalAddressSpace.ShouldBe("10.20.0.0/16", "the network type's own default, so the fixtures read as one network");
    }

    [Theory]
    [InlineData("not-a-prefix", "/properties/link/v4")]
    [InlineData("10.20.0.0", "/properties/link/v4")]
    public void AMalformedLinkIsRefusedAtTheApiWithItsOwnPointer(string malformed, string target) {
        var validated = VirtualNetworkPeerings.Schema2026.Validate(
            JsonDocument.Parse(VirtualNetworkPeerings.Body(Cluster, linkV4: malformed)).RootElement
        );

        validated.IsSuccess.ShouldBeFalse($"'{malformed}' was accepted by the schema");
        validated.Error!.Target.ShouldBe(target);
    }

    [Fact]
    public void AResourceIdTypedWhereTheRemoteNameBelongsIsRefusedAtTheApi() {
        // ⚠ The likeliest mistake on this property, because the schema COULD have taken one — see
        // VirtualNetworkPeerings' remarks for why it does not.
        var validated = VirtualNetworkPeerings.Schema2026.Validate(
            JsonDocument.Parse(
                VirtualNetworkPeerings.Body(Cluster, remoteNetwork: "/subscriptions/x/resourceGroups/rg/providers/CyberCloud.Network/virtualNetworks/spoke")
            ).RootElement
        );

        validated.IsSuccess.ShouldBeFalse();
        validated.Error!.Target.ShouldBe("/properties/remoteNetwork");
    }

    [Fact]
    public void OnlyTheRemoteIsImmutableBecauseARangeCanBeReRoutedAndARemoteCannotBeWithdrawnFrom() {
        VirtualNetworkPeerings.Schema2026.Properties.Single(x => x.JsonPointer == "/properties/remoteNetwork").Immutable.ShouldBeTrue();

        foreach (var pointer in new[] { "/properties/localAddressSpace/v4", "/properties/remoteAddressSpace/v4", "/properties/link/v4" }) {
            VirtualNetworkPeerings.Schema2026.Properties.Single(x => x.JsonPointer == pointer)
                .Immutable.ShouldBeFalse($"{pointer} is a route, and a changed route is re-rendered as a changed fragment");
        }
    }

    // ── The convergence predicate, against a controller-shaped read-back ─────────────────────────

    [Fact]
    public void AVpcWithTheControllersAdditionsAndAnotherPeeringsEntriesStillMatches() {
        // ⚠ formatVpc fills `policy` in and adds a finalizer; the controller writes status; and a
        // second peering's entries sit in the same two arrays. Containment over both lists is what
        // converges; equality would report drift forever.
        var peering = Address("to-spoke", TenantOne, SubscriptionOne, network: "hub");
        var ns = ReconcileDriver.NamespaceFor(peering);

        using var body = JsonDocument.Parse(VirtualNetworkPeerings.Body(Cluster));

        var live = AfterTheController(ns, peering, body.RootElement, VirtualNetworkPeerings.Side.Local);

        VirtualNetworkPeerings.Matches(live, ns, peering, body.RootElement, VirtualNetworkPeerings.Side.Local).ShouldBeTrue();
        VirtualNetworkPeerings.Matches(live, ns, peering, body.RootElement, VirtualNetworkPeerings.Side.Remote)
            .ShouldBeFalse("the local Vpc does not carry the REMOTE side's entries, and a predicate that said so would pass a peering routed to itself");
        VirtualNetworkPeerings.ConnectedPeers(live).ShouldContain(VirtualNetworks.ObjectNameOf(ns, "spoke"));
    }

    [Fact]
    public void ARouteWhoseNextHopWasRewrittenIsDrift() {
        var peering = Address("to-spoke", TenantOne, SubscriptionOne, network: "hub");
        var ns = ReconcileDriver.NamespaceFor(peering);

        using var body = JsonDocument.Parse(VirtualNetworkPeerings.Body(Cluster));

        var live = JsonNode.Parse(AfterTheController(ns, peering, body.RootElement, VirtualNetworkPeerings.Side.Local))!.AsObject();
        // ⚠ Index 1: the other peering's route sits first in the controller-shaped read-back.
        live["spec"]!["staticRoutes"]![1]!["nextHopIP"] = "10.255.255.9";

        VirtualNetworkPeerings.Matches(live.ToJsonString(), ns, peering, body.RootElement, VirtualNetworkPeerings.Side.Local)
            .ShouldBeFalse("a route pointing anywhere but the peer's port is a route to nowhere, under this tenant's resource id");

        VirtualNetworkPeerings.Matches("not json", ns, peering, body.RootElement, VirtualNetworkPeerings.Side.Local).ShouldBeFalse();
        VirtualNetworkPeerings.Matches("{}", ns, peering, body.RootElement, VirtualNetworkPeerings.Side.Local).ShouldBeFalse();
    }

    // ── Clause 2 ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ThePeeringReconcilerHoldsNoMutableState() =>
        ReconcilerConformance.CheckNoHiddenState(new VirtualNetworkPeeringReconciler(new FixedClock()))
            .ShouldBeEmpty();

    // ── The reconciler's outcomes, over a connection that models a second manager ────────────────

    [Fact]
    public async Task APeeringWritesBothVpcsAsFragmentsUnderTheOwnersManagersAndConverges() {
        var reconciler = new VirtualNetworkPeeringReconciler(new FixedClock());
        var peering = Address("to-spoke", TenantOne, SubscriptionOne, network: "hub");
        var ns = ReconcileDriver.NamespaceFor(peering);
        var world = new CoOwnedConnection();

        world.PlaceOwnedVpc(ns, "hub", HubId, peering);
        world.PlaceOwnedVpc(ns, "spoke", SpokeId, peering);

        using var body = JsonDocument.Parse(VirtualNetworkPeerings.Body(Cluster));

        var outcome = await reconciler.ReconcileAsync(Context(peering, body.RootElement, world), TestContext.Current.CancellationToken);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.Converged, outcome.ToString());

        world.Applied.Count.ShouldBe(2);
        world.Applied.ShouldAllBe(x => x.IsCoOwned, "a peering applies nothing it owns");
        world.Applied.ShouldAllBe(x => x.Labels.Count == 0, "a co-writer writes no labels");
        world.Applied[0].OwnerResourceId.ShouldBe(HubId);
        world.Applied[1].OwnerResourceId.ShouldBe(SpokeId);
        world.Applied[0].FieldManager.ShouldBe(KubeLabels.CoWriterFieldManager(KubeLabels.ResourceTypeValue(VirtualNetworks.Type), KubeLabels.GuidValue(HubId)));

        // The owner's object is still the owner's, with the slice beside its own field.
        var hub = JsonNode.Parse(world.Objects[CoOwnedConnection.Key(VirtualNetworks.VpcRef(ns, "hub"))])!.AsObject();
        hub["metadata"]!["labels"]![KubeLabels.ResourceId]!.GetValue<string>().ShouldBe(KubeLabels.GuidValue(HubId));
        hub["spec"]!["enableExternal"].ShouldNotBeNull();
        hub["spec"]!["vpcPeerings"]!.AsArray().Count.ShouldBe(1);
        (hub["metadata"]!["annotations"] as JsonObject)!.ContainsKey(KubeLabels.FragmentAnnotation(peering.Id)).ShouldBeTrue();

        // A second pass is the idempotent one: both applies say Unchanged and the objects do not move.
        var before = world.Objects.ToDictionary(x => x.Key, x => x.Value);
        var again = await reconciler.ReconcileAsync(Context(peering, body.RootElement, world), TestContext.Current.CancellationToken);
        again.Kind.ShouldBe(ReconcileOutcomeKind.Converged);
        world.Objects.ToDictionary(x => x.Key, x => x.Value).ShouldBe(before);
    }

    [Fact]
    public async Task AnAbsentRemoteIsInProgressNamingTheRemoteAndNothingIsCreated() {
        var reconciler = new VirtualNetworkPeeringReconciler(new FixedClock());
        var peering = Address("to-spoke", TenantOne, SubscriptionOne, network: "hub");
        var ns = ReconcileDriver.NamespaceFor(peering);
        var world = new CoOwnedConnection();

        world.PlaceOwnedVpc(ns, "hub", HubId, peering);

        using var body = JsonDocument.Parse(VirtualNetworkPeerings.Body(Cluster));

        var outcome = await reconciler.ReconcileAsync(Context(peering, body.RootElement, world), TestContext.Current.CancellationToken);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.InProgress, outcome.ToString());
        outcome.Reason.ShouldContain("remote");
        outcome.Reason.ShouldContain(VirtualNetworks.ObjectNameOf(ns, "spoke"));

        world.Objects.ContainsKey(CoOwnedConnection.Key(VirtualNetworks.VpcRef(ns, "spoke")))
            .ShouldBeFalse("a co-writer never creates the owner's object");

        // ⚠ And the local half landed, which is the documented intermediate state.
        VirtualNetworkPeerings.CarriesFragmentOf(world.Objects[CoOwnedConnection.Key(VirtualNetworks.VpcRef(ns, "hub"))], peering.Id).ShouldBeTrue();
    }

    [Fact]
    public async Task OverlappingRangesFailTerminallyRatherThanRetryingForAnHour() {
        var reconciler = new VirtualNetworkPeeringReconciler(new FixedClock());
        var peering = Address("to-spoke", TenantOne, SubscriptionOne, network: "hub");
        var ns = ReconcileDriver.NamespaceFor(peering);
        var world = new CoOwnedConnection();

        world.PlaceOwnedVpc(ns, "hub", HubId, peering);
        world.PlaceOwnedVpc(ns, "spoke", SpokeId, peering);

        using var body = JsonDocument.Parse(VirtualNetworkPeerings.Body(Cluster, remoteAddressSpaceV4: "10.20.0.0/16"));

        var outcome = await reconciler.ReconcileAsync(Context(peering, body.RootElement, world), TestContext.Current.CancellationToken);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.Failed);
        outcome.IsTerminal.ShouldBeTrue("a body whose ranges overlap can never converge");
        outcome.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        outcome.Error.Message.ShouldContain("10.20.0.0/16");
        world.Applied.ShouldBeEmpty("nothing is written for a body that is refused");
    }

    [Fact]
    public async Task ARemoteWhoseNameIsAnotherGroupsNetworkIsRefusedByGroupAndThatRouterIsUntouched() {
        // ⚠ THE #31 REVIEW'S PROBE, KEPT AS THE TEST. A Vpc's name is {sub}-{group}-{network} and
        // both halves admit hyphens, so `prod`'s network `a-b` and `prod-a`'s network `b` render ONE
        // object name in one subscription. Before the review, "the remote is qualified with this
        // resource's namespace" was the whole of the boundary, and this peering — in `prod`, naming
        // `a-b` — converged with a peer port and a static route written into `prod-a`'s router, a
        // group its author may hold no role on. The co-owned apply now holds the live object's
        // subscription and group against the writer's, in the builder and again before the PATCH.
        var reconciler = new VirtualNetworkPeeringReconciler(new FixedClock());
        var peering = new ResourceId(TenantOne, SubscriptionOne, "prod", VirtualNetworkPeerings.Type, "x", Guid.Parse("66666666-6666-4666-8666-666666666666"), "hub");
        var ns = ReconcileDriver.NamespaceFor(peering);

        var victimId = Guid.Parse("77777777-7777-4777-8777-777777777777");
        var victim = new ResourceId(TenantOne, SubscriptionOne, "prod-a", VirtualNetworks.Type, "b", victimId);
        var victimNs = ReconcileDriver.NamespaceFor(victim);

        VirtualNetworks.ObjectNameOf(ns, "a-b").ShouldBe(VirtualNetworks.ObjectNameOf(victimNs, "b"), "the collision this test is about");

        var world = new CoOwnedConnection();
        world.PlaceOwnedVpc(ns, "hub", HubId, peering);
        world.PlaceOwnedVpc(victimNs, "b", victimId, victim);

        using var body = JsonDocument.Parse(VirtualNetworkPeerings.Body(Cluster, remoteNetwork: "a-b"));

        var outcome = await reconciler.ReconcileAsync(Context(peering, body.RootElement, world), TestContext.Current.CancellationToken);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.Failed, outcome.ToString());
        outcome.Error!.Message.ShouldContain("'prod-a'");
        outcome.Error.Message.ShouldContain("'prod'");
        outcome.Error.Message.ShouldContain("resource group");

        var router = JsonNode.Parse(world.Objects[CoOwnedConnection.Key(VirtualNetworks.VpcRef(victimNs, "b"))])!.AsObject();
        VirtualNetworkPeerings.CarriesFragmentOf(router.ToJsonString(), peering.Id).ShouldBeFalse("the other group's router carries the peering's bookkeeping");
        router["spec"]!.AsObject().ContainsKey("vpcPeerings").ShouldBeFalse("a peer port was written into the other group's router");
        router["spec"]!.AsObject().ContainsKey("staticRoutes").ShouldBeFalse("a route was written into the other group's router");
        router["metadata"]!["labels"]![KubeLabels.ResourceGroup]!.GetValue<string>().ShouldBe("prod-a");
    }

    [Fact]
    public async Task DeletingWithdrawsBothFragmentsAndLeavesBothVpcsStanding() {
        var reconciler = new VirtualNetworkPeeringReconciler(new FixedClock());
        var peering = Address("to-spoke", TenantOne, SubscriptionOne, network: "hub");
        var ns = ReconcileDriver.NamespaceFor(peering);
        var world = new CoOwnedConnection();

        world.PlaceOwnedVpc(ns, "hub", HubId, peering);
        world.PlaceOwnedVpc(ns, "spoke", SpokeId, peering);

        using var body = JsonDocument.Parse(VirtualNetworkPeerings.Body(Cluster));

        (await reconciler.ReconcileAsync(Context(peering, body.RootElement, world), TestContext.Current.CancellationToken)).IsConverged.ShouldBeTrue();

        var deleted = await reconciler.DeleteAsync(Context(peering, body.RootElement, world), TestContext.Current.CancellationToken);

        deleted.Kind.ShouldBe(ReconcileOutcomeKind.Converged, deleted.ToString());
        world.Deleted.ShouldBeEmpty("a withdrawal deletes nothing");

        foreach (var network in new[] { "hub", "spoke" }) {
            var json = world.Objects[CoOwnedConnection.Key(VirtualNetworks.VpcRef(ns, network))];
            VirtualNetworkPeerings.CarriesFragmentOf(json, peering.Id).ShouldBeFalse($"{network} still carries the fragment");
            var spec = JsonNode.Parse(json)!["spec"]!.AsObject();
            spec.ContainsKey("vpcPeerings").ShouldBeFalse($"{network} still carries the peering entry");
            spec["enableExternal"].ShouldNotBeNull("the owner's own field survives the withdrawal");
        }

        // A second delete pass — the operation re-driven — is Converged with nothing applied.
        var applied = world.Applied.Count;
        (await reconciler.DeleteAsync(Context(peering, body.RootElement, world), TestContext.Current.CancellationToken)).IsConverged.ShouldBeTrue();
        world.Applied.Count.ShouldBe(applied, "a withdrawal of a fragment that is not there applies nothing");
    }

    [Fact]
    public async Task DeletingAPeeringWhoseRemoteIsAlreadyGoneConvergesBecauseTheOwnersDeleteWins() {
        var reconciler = new VirtualNetworkPeeringReconciler(new FixedClock());
        var peering = Address("to-spoke", TenantOne, SubscriptionOne, network: "hub");
        var ns = ReconcileDriver.NamespaceFor(peering);
        var world = new CoOwnedConnection();

        world.PlaceOwnedVpc(ns, "hub", HubId, peering);
        world.PlaceOwnedVpc(ns, "spoke", SpokeId, peering);

        using var body = JsonDocument.Parse(VirtualNetworkPeerings.Body(Cluster));

        (await reconciler.ReconcileAsync(Context(peering, body.RootElement, world), TestContext.Current.CancellationToken)).IsConverged.ShouldBeTrue();
        world.Objects.TryRemove(CoOwnedConnection.Key(VirtualNetworks.VpcRef(ns, "spoke")), out _).ShouldBeTrue();

        var deleted = await reconciler.DeleteAsync(Context(peering, body.RootElement, world), TestContext.Current.CancellationToken);

        deleted.Kind.ShouldBe(ReconcileOutcomeKind.Converged, deleted.ToString());
        VirtualNetworkPeerings.CarriesFragmentOf(world.Objects[CoOwnedConnection.Key(VirtualNetworks.VpcRef(ns, "hub"))], peering.Id).ShouldBeFalse();
    }

    [Fact]
    public async Task ObserveReadsTheLocalVpcAndSaysWhichSideHasDrifted() {
        var reconciler = new VirtualNetworkPeeringReconciler(new FixedClock());
        var peering = Address("to-spoke", TenantOne, SubscriptionOne, network: "hub");
        var ns = ReconcileDriver.NamespaceFor(peering);
        var world = new CoOwnedConnection();

        world.PlaceOwnedVpc(ns, "hub", HubId, peering);
        world.PlaceOwnedVpc(ns, "spoke", SpokeId, peering);

        using var body = JsonDocument.Parse(VirtualNetworkPeerings.Body(Cluster));

        var absent = await reconciler.ObserveAsync(new(peering, VirtualNetworkPeerings.V2026, body.RootElement, ns, world), TestContext.Current.CancellationToken);
        absent.Exists.ShouldBeFalse("nothing has been written yet");

        (await reconciler.ReconcileAsync(Context(peering, body.RootElement, world), TestContext.Current.CancellationToken)).IsConverged.ShouldBeTrue();

        var observed = await reconciler.ObserveAsync(new(peering, VirtualNetworkPeerings.V2026, body.RootElement, ns, world), TestContext.Current.CancellationToken);
        observed.Exists.ShouldBeTrue();
        observed.Summary.ShouldContain("both networks carry the peering");

        world.Objects.TryRemove(CoOwnedConnection.Key(VirtualNetworks.VpcRef(ns, "spoke")), out _);

        var half = await reconciler.ObserveAsync(new(peering, VirtualNetworkPeerings.V2026, body.RootElement, ns, world), TestContext.Current.CancellationToken);
        half.Exists.ShouldBeTrue();
        half.Summary.ShouldContain("the remote does not");
    }

    // ── The action ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ShowRoutesReportsWrittenAndConnectedPerSide() {
        var peering = Address("to-spoke", TenantOne, SubscriptionOne, network: "hub");
        var ns = ReconcileDriver.NamespaceFor(peering);
        var world = new CoOwnedConnection();

        using var body = JsonDocument.Parse(VirtualNetworkPeerings.Body(Cluster));

        // The local Vpc as the controller leaves it — written AND connected; the remote absent.
        world.Objects[CoOwnedConnection.Key(VirtualNetworks.VpcRef(ns, "hub"))] =
            AfterTheController(ns, peering, body.RootElement, VirtualNetworkPeerings.Side.Local);

        var handler = new ShowRoutesHandler(new FixedClock());

        var answer = await handler.InvokeAsync(
            new(
                peering,
                VirtualNetworkPeerings.V2026,
                VirtualNetworkPeerings.RoutesAction,
                JsonDocument.Parse("{}").RootElement,
                body.RootElement,
                ns,
                world,
                new UnavailableSecretResolver()
            ),
            TestContext.Current.CancellationToken
        );

        var response = JsonNode.Parse(answer.GetValueOrThrow())!.AsObject();

        response["localVpc"]!.GetValue<string>().ShouldBe(VirtualNetworks.ObjectNameOf(ns, "hub"));
        response["remoteVpc"]!.GetValue<string>().ShouldBe(VirtualNetworks.ObjectNameOf(ns, "spoke"));
        response["localConnectIP"]!.GetValue<string>().ShouldBe("10.255.255.1/30");
        response["remoteConnectIP"]!.GetValue<string>().ShouldBe("10.255.255.2/30");
        response["localWritten"]!.GetValue<bool>().ShouldBeTrue();
        response["localConnected"]!.GetValue<bool>().ShouldBeTrue();
        response["remoteWritten"]!.GetValue<bool>().ShouldBeFalse("the remote Vpc is not there, and that is an answer rather than a 404");
        response["remoteConnected"]!.GetValue<bool>().ShouldBeFalse();

        // ⚠ And the response satisfies the schema the action declares, which is what the SDK and the
        // portal are generated from.
        VirtualNetworkPeerings.RoutesResponse.Validate(JsonDocument.Parse(answer.GetValueOrThrow()).RootElement)
            .IsSuccess.ShouldBeTrue();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     One side's <c>Vpc</c> as it reads back once <c>handleAddOrUpdateVpc</c> has run: the
    ///     owner's field, this peering's entries, a second peering's entries beside them, the
    ///     controller's finalizer and its <c>status.vpcPeerings</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Hand-written, because there is no controller in any harness this repository runs.</b>
    /// </remarks>
    static string AfterTheController(string ns, ResourceId peering, JsonElement desired, VirtualNetworkPeerings.Side side) {
        var fragment = JsonNode.Parse(
            side == VirtualNetworkPeerings.Side.Local
                ? VirtualNetworkPeerings.LocalFragmentJson(ns, peering, desired)
                : VirtualNetworkPeerings.RemoteFragmentJson(ns, peering, desired)
        )!.AsObject();

        var peerings = fragment["spec"]!["vpcPeerings"]!.AsArray();
        var routes = fragment["spec"]!["staticRoutes"]!.AsArray();

        // Another peering's slice, first in both lists.
        peerings.Insert(0, new JsonObject { ["remoteVpc"] = ns + "-elsewhere", ["localConnectIP"] = "10.254.0.1/30" });
        routes.Insert(0, new JsonObject { ["policy"] = "policyDst", ["cidr"] = "10.40.0.0/16", ["nextHopIP"] = "10.254.0.2" });

        var remote = peerings[1]!["remoteVpc"]!.GetValue<string>();

        return new JsonObject {
            ["apiVersion"] = "kubeovn.io/v1",
            ["kind"] = "Vpc",
            ["metadata"] = new JsonObject {
                ["name"] = side == VirtualNetworkPeerings.Side.Local
                    ? VirtualNetworkPeerings.LocalVpcNameOf(ns, peering)
                    : VirtualNetworkPeerings.RemoteVpcNameOf(ns, desired),
                ["finalizers"] = new JsonArray("kubeovn.io/kube-ovn-controller")
            },
            ["spec"] = new JsonObject {
                ["enableExternal"] = false,
                ["vpcPeerings"] = peerings.DeepClone(),
                ["staticRoutes"] = routes.DeepClone(),
                ["bfdPort"] = new JsonObject { ["enabled"] = false }
            },
            ["status"] = new JsonObject { ["vpcPeerings"] = new JsonArray(ns + "-elsewhere", remote) }
        }.ToJsonString();
    }

    static ReconcileContext Context(ResourceId address, JsonElement desired, IKubeClusterConnection connection) =>
        new(
            address,
            VirtualNetworkPeerings.V2026,
            desired,
            null,
            ReconcileDriver.NamespaceFor(address),
            connection,
            new UnavailableSecretResolver(),
            new NullLog()
        );

    static ResourceId Address(string name, Guid tenant, Guid subscription, string network = "hub") =>
        new(
            tenant,
            subscription,
            "rg",
            VirtualNetworkPeerings.Type,
            name,
            Guid.Parse("66666666-6666-4666-8666-666666666666"),
            network
        );
}

/// <summary>
///     A connection that holds labelled <c>Vpc</c>s and models the co-writers' shared manager one
///     manager deep — what a peering's reconciler needs from a cluster and nothing more.
/// </summary>
/// <remarks>
///     ⚠ <b>Not <c>RecordingConnection</c>, on purpose.</b> That double stores a body verbatim, and a
///     co-owned command's body is an unlabelled fragment with a <c>resourceVersion</c> — stored
///     verbatim it would replace the owner's object, and the next read would find nothing this
///     platform owns. What is modelled here is what <c>FakeKubeCluster.ApplyCoOwned</c> models in the
///     shared harness: the shape and owner checks the platform itself runs, the version lock, the
///     previous union taken back, the new one set, the annotations replaced. Per-manager ownership
///     against a real API server is <c>CoOwnedApplyTests</c>'.
/// </remarks>
sealed class CoOwnedConnection : IKubeClusterConnection {
    public ConcurrentDictionary<string, string> Objects { get; } = new(StringComparer.Ordinal);
    public List<KubeCommand> Applied { get; } = [];
    public List<ObjectRef> Deleted { get; } = [];

    readonly ConcurrentDictionary<string, long> versions = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, JsonObject> unions = new(StringComparer.Ordinal);

    public Guid ClusterId => Guid.Parse("cccccccc-0000-4000-8000-000000000003");

    /// <summary>Places a network's <c>Vpc</c> as its own reconciler would have applied it — the seven labels and its one field.</summary>
    public void PlaceOwnedVpc(string ns, string network, Guid ownerId, ResourceId tenantOf) {
        var target = VirtualNetworks.VpcRef(ns, network);

        Objects[Key(target)] = new JsonObject {
            ["apiVersion"] = "kubeovn.io/v1",
            ["kind"] = "Vpc",
            ["metadata"] = new JsonObject {
                ["name"] = target.Name,
                ["uid"] = ownerId.ToString("N"),
                ["labels"] = new JsonObject {
                    [KubeLabels.TenantId] = KubeLabels.GuidValue(tenantOf.TenantId),
                    [KubeLabels.SubscriptionId] = KubeLabels.GuidValue(tenantOf.SubscriptionId),
                    [KubeLabels.ResourceGroup] = tenantOf.ResourceGroup,
                    [KubeLabels.ResourceId] = KubeLabels.GuidValue(ownerId),
                    [KubeLabels.ResourceType] = KubeLabels.ResourceTypeValue(VirtualNetworks.Type),
                    [KubeLabels.ApiVersion] = VirtualNetworks.V2026,
                    [KubeLabels.ManagedBy] = KubeLabels.ManagedByValue
                },
                ["annotations"] = new JsonObject {
                    [KubeLabels.ResourcePathAnnotation] = "/networks/" + network,
                    [KubeLabels.ReconcileHashAnnotation] = "sha256:owner"
                }
            },
            ["spec"] = new JsonObject { ["enableExternal"] = false }
        }.ToJsonString();

        versions[Key(target)] = 1;
    }

    public Task<Result<ApplyOutcome>> ApplyAsync(KubeCommand command, CancellationToken cancellationToken = default) {
        Applied.Add(command);

        if (!command.IsCoOwned) {
            throw new InvalidOperationException("a peering applies nothing it owns; an ordinary apply here is a bug in the reconciler");
        }

        var shape = command.CheckCoOwnedShape();
        if (shape.TryGetError(out var shapeError)) {
            return Task.FromResult(Result<ApplyOutcome>.Failure(shapeError));
        }

        var key = Key(command.Target);

        if (!Objects.TryGetValue(key, out var before)) {
            return Task.FromResult(Result<ApplyOutcome>.Failure(ErrorCode.ResourceNotFound, $"'{command.Target}' is not here, and a co-writer never creates it."));
        }

        var version = versions.GetValueOrDefault(key, 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

        var against = command.CheckCoOwnedAgainst(new() { Ref = command.Target, Json = before, ResourceVersion = version });
        if (against.TryGetError(out var ownerError)) {
            return Task.FromResult(Result<ApplyOutcome>.Failure(ownerError));
        }

        var body = JsonNode.Parse(command.Body)!.AsObject();

        if (body["metadata"]!["resourceVersion"]!.GetValue<string>() != version) {
            return Task.FromResult(Result<ApplyOutcome>.Success(new() { Result = ApplyResult.Stale, Target = command.Target }));
        }

        var live = JsonNode.Parse(before)!.AsObject();

        if (unions.TryGetValue(key, out var previous)) {
            Remove(live, previous);
        }

        var union = new JsonObject();
        foreach (var (name, value) in body) {
            if (name is not ("apiVersion" or "kind" or "metadata")) {
                union[name] = value?.DeepClone();
            }
        }

        Set(live, union);

        var annotations = live["metadata"]!["annotations"]!.AsObject();
        foreach (var stale in annotations.Where(x => KubeLabels.IsFragmentAnnotation(x.Key)).Select(x => x.Key).ToList()) {
            annotations.Remove(stale);
        }

        foreach (var (name, value) in command.Annotations) {
            annotations[name] = value;
        }

        var after = live.ToJsonString();
        var changed = after != before;

        Objects[key] = after;
        unions[key] = union;

        if (changed) {
            versions[key] = versions.GetValueOrDefault(key, 1) + 1;
        }

        return Task.FromResult(
            Result<ApplyOutcome>.Success(
                new() {
                    Result = changed ? ApplyResult.Updated : ApplyResult.Unchanged,
                    Target = command.Target,
                    ResourceVersion = versions.GetValueOrDefault(key, 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ReconcileHash = command.ReconcileHash
                }
            )
        );
    }

    public Task<Result<KubeObject>> GetAsync(ObjectRef target, CancellationToken cancellationToken = default) =>
        Task.FromResult(
            Objects.TryGetValue(Key(target), out var json)
                ? Result<KubeObject>.Success(
                    new() {
                        Ref = target,
                        Json = json,
                        ResourceVersion = versions.GetValueOrDefault(Key(target), 1).ToString(System.Globalization.CultureInfo.InvariantCulture)
                    }
                )
                : Result<KubeObject>.Failure(ErrorCode.ResourceNotFound, $"'{target}' is not here.")
        );

    public Task<Result> DeleteAsync(KubeCommand command, CascadePolicy policy = CascadePolicy.Background, CancellationToken cancellationToken = default) {
        Deleted.Add(command.Target);
        return Task.FromResult(Result.Failure(ErrorCode.InvalidRequestBody, "a peering deletes nothing; a co-owned withdrawal goes through ApplyAsync"));
    }

    static void Remove(JsonObject target, JsonObject owned) {
        foreach (var (name, value) in owned) {
            if (value is JsonObject nested && target[name] is JsonObject inner) {
                Remove(inner, nested);
            } else {
                target.Remove(name);
            }
        }
    }

    static void Set(JsonObject target, JsonObject union) {
        foreach (var (name, value) in union) {
            if (value is JsonObject nested) {
                if (target[name] is not JsonObject inner) {
                    inner = [];
                    target[name] = inner;
                }

                Set(inner, nested);
            } else {
                target[name] = value?.DeepClone();
            }
        }
    }

    internal static string Key(ObjectRef target) => target.Kind.Kind + "/" + target.Namespace + "/" + target.Name;
}
