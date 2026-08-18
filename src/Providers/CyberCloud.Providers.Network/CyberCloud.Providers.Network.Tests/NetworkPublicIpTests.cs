using CyberCloud.ResourceManager;
using CyberCloud.ResourceManager.Conformance;
using CyberCloud.ResourceManager.Reconcile;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Network.Tests;

/// <summary>
///     <c>CyberCloud.Network/publicIpAddresses</c> — the renderer, the convergence predicate and the
///     two failure classes this type meets that its three siblings do not.
/// </summary>
/// <remarks>
///     ⚠ <b>Everything here is about a controller that the cluster-backed suite does not run.</b> The
///     k3s that suite starts has no Kube-OVN, so the behaviours that decide whether this type works —
///     the allocation, the write-back to <c>.spec</c>, the field-manager conflict, the refusal to
///     change an allocated address — are invisible to it. These tests hand-write the
///     controller-shaped read-back, exactly as <c>NetworkMatchesTests</c> does for the subnet, and it
///     is the only place in this repository where those cases are checked at all.
/// </remarks>
public sealed class NetworkPublicIpTests {
    static readonly Guid TenantOne = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000011");
    static readonly Guid TenantTwo = Guid.Parse("bbbbbbbb-0000-4000-8000-000000000012");
    static readonly Guid SubscriptionOne = Guid.Parse("aaaaaaaa-0000-4000-8000-00000000001a");
    static readonly Guid SubscriptionTwo = Guid.Parse("bbbbbbbb-0000-4000-8000-00000000001b");
    static readonly Guid Cluster = Guid.Parse("cccccccc-0000-4000-8000-000000000003");

    // ── Failure class (a): a reconciler with a field ─────────────────────────────────────────────

    [Fact]
    public void ThePublicAddressReconcilerHoldsNoMutableState() =>
        ReconcilerConformance.CheckNoHiddenState(new PublicIpAddressReconciler(new FixedClock()))
            .ShouldBeEmpty();

    [Fact]
    public async Task OnePublicAddressReconcilerServesTwoTenantsWithoutMixingThem() {
        // ⚠ THE ONLY TEST THAT CATCHES THE READONLY-MUTABLE-FIELD SHAPE, run for the fourth
        // reconciler in this family. CheckNoHiddenState above is structurally blind to it — seven
        // sightings in seven families — and AddCyberCloudProvider registers a reconciler as a
        // SINGLETON BY CONCRETE TYPE, so in a real silo ONE instance serves every tenant.
        //
        // ⚠ AND ON THIS TYPE THE COLLISION IS THE WORST IN THE FAMILY, because the object is an
        // ALLOCATION rather than a configuration. Two tenants whose addresses rendered one OvnEip
        // would be handed THE SAME ADDRESS, and the second tenant's inbound traffic would arrive at
        // the first tenant's NAT rule. A wrong security group leaks a rule set; a wrong address leaks
        // packets.
        var reconciler = new PublicIpAddressReconciler(new FixedClock());
        var connection = new RecordingConnection();

        var alice = Address("edge", TenantOne, SubscriptionOne);
        var bob = Address("edge", TenantTwo, SubscriptionTwo);

        using var aliceBody = JsonDocument.Parse(
            PublicIpAddresses.Body(Cluster, addressV4: "10.100.0.7")
        );

        using var bobBody = JsonDocument.Parse(
            PublicIpAddresses.Body(Cluster, addressV4: "10.100.0.9")
        );

        await Pass(reconciler, connection, alice, aliceBody.RootElement);
        await Pass(reconciler, connection, bob, bobBody.RootElement);
        await Pass(reconciler, connection, alice, aliceBody.RootElement);
        await Pass(reconciler, connection, bob, bobBody.RootElement);

        var applied = connection.Applied;
        applied.Count.ShouldBe(4);

        // ⚠ THE THIRD AND FOURTH PASSES, not the first two. A cache populated on pass one is only
        // visible from pass three, which is why the sequence is A, B, A, B.
        Spec(applied[2].Body)["v4Ip"]!.GetValue<string>()
            .ShouldBe("10.100.0.7", "tenant A asked for one address and was rendered tenant B's");

        Spec(applied[3].Body)["v4Ip"]!.GetValue<string>()
            .ShouldBe("10.100.0.9", "tenant B asked for one address and was rendered tenant A's");

        applied[0].Target.Name.ShouldNotBe(
            applied[1].Target.Name,
            "two subscriptions' identically-named addresses rendered ONE cluster-scoped OvnEip, so "
            + "two tenants would have been handed the same address"
        );
    }

    // ── Failure class (d): an empty value is not an absent one ───────────────────────────────────

    [Fact]
    public void AnUnrequestedAddressIsAbsentFromTheSpecRatherThanEmpty() {
        // ⚠ THE FIELD-MANAGER DEADLOCK, PINNED. createOrUpdateOvnEipCR writes the ALLOCATED address
        // into spec.v4Ip through a full OvnEips().Update(...), taking ownership of the field. An
        // apply carrying `v4Ip: ""` would claim the same field at a different value, and every later
        // apply would answer ApplyResult.Conflict — the resource would sit in InProgress forever on
        // an address that was allocated correctly the first time.
        //
        // ⚠ It is CyberCloud.Terminal/consoles' finding — an empty value is not an absent one —
        // arriving on a SCALAR instead of a list, and with a worse symptom: there the resource never
        // left InProgress, here it never leaves InProgress AND the reason is reported as somebody
        // else's field ownership.
        using var body = JsonDocument.Parse(PublicIpAddresses.Body(Cluster));

        var spec = Spec(PublicIpAddresses.OvnEipJson("ns", "edge", body.RootElement));

        spec.ContainsKey("v4Ip").ShouldBeFalse(
            "the rendered OvnEip carries a v4Ip key for a body that asked for no particular address"
        );

        spec.ContainsKey("v6Ip").ShouldBeFalse();

        // ⚠ And the two fields that are never sent at all, for two different reasons: the pool is the
        // operator's --external-gateway-switch and the MAC is IPAM's, and both are written back to
        // the spec by the controller.
        spec.ContainsKey("externalSubnet").ShouldBeFalse();
        spec.ContainsKey("macAddress").ShouldBeFalse();

        spec["type"]!.GetValue<string>().ShouldBe(PublicIpAddresses.UsageTypeNat);
    }

    [Fact]
    public void ARequestedAddressIsSentVerbatim() {
        using var body = JsonDocument.Parse(
            PublicIpAddresses.Body(Cluster, addressV4: "10.100.0.7", addressV6: "fd00:ff::7")
        );

        var spec = Spec(PublicIpAddresses.OvnEipJson("ns", "edge", body.RootElement));

        spec["v4Ip"]!.GetValue<string>().ShouldBe("10.100.0.7");
        spec["v6Ip"]!.GetValue<string>().ShouldBe("fd00:ff::7");
    }

    // ── The convergence predicate, against a controller-shaped read-back ─────────────────────────

    [Fact]
    public void AnAddressTheFabricAllocatedIsNotDrift() {
        // ⚠ THE SINGLE MOST LIKELY BUG IN THIS TYPE, RUN AS A TEST RATHER THAN ARGUED IN A COMMENT.
        // A body that asked for no particular address is applied with no v4Ip key at all; one
        // controller pass later the object carries spec.v4Ip, spec.v6Ip, spec.macAddress and
        // spec.externalSubnet, all written by createOrUpdateOvnEipCR and handleAddOvnEip. A Matches
        // that compared the address would report drift on an address allocated exactly as asked,
        // forever, and the resource would never leave InProgress.
        //
        // ⚠ It is NetworkSubnets.Matches' canonicalisation trap in its SECOND shape: there the
        // controller REWRITES what it was sent, here it FILLS IN what it was not.
        using var body = JsonDocument.Parse(PublicIpAddresses.Body(Cluster));

        PublicIpAddresses.Matches(AfterTheController("10.100.0.7"), body.RootElement).ShouldBeTrue(
            "an address the fabric picked was reported as drift, so the resource never converges"
        );
    }

    [Fact]
    public void ARequestedAddressTheObjectDoesNotCarryIsDrift() {
        // ⚠ THE OTHER HALF, AND WITHOUT IT THE TEST ABOVE WOULD PASS AGAINST A PREDICATE THAT COMPARED
        // NOTHING. A tenant who asked for 10.100.0.7 and whose object carries 10.100.0.9 has an
        // address that is not theirs — the fabric refused the static request and allocated something
        // else — and reporting that as converged would publish the wrong address through
        // showAllocation with the resource saying Succeeded.
        using var body = JsonDocument.Parse(
            PublicIpAddresses.Body(Cluster, addressV4: "10.100.0.7")
        );

        PublicIpAddresses.Matches(AfterTheController("10.100.0.9"), body.RootElement).ShouldBeFalse();
        PublicIpAddresses.Matches(AfterTheController("10.100.0.7"), body.RootElement).ShouldBeTrue();
    }

    [Fact]
    public void AnObjectWhoseTypeTheControllerChangedIsDrift() {
        // ⚠ `type` is the one field this provider sends unconditionally, so it is the one field a
        // comparison can insist on. An OvnEip that came back as `lsp` has a bare logical switch port
        // on a node attached to it, which is an operator's object wearing a tenant's labels.
        using var body = JsonDocument.Parse(PublicIpAddresses.Body(Cluster));

        var lsp = JsonNode.Parse(AfterTheController("10.100.0.7"))!.AsObject();
        lsp["spec"]!["type"] = "lsp";

        PublicIpAddresses.Matches(lsp.ToJsonString(), body.RootElement).ShouldBeFalse();
    }

    // ── Failure class (b): what an empty body exposes ────────────────────────────────────────────

    [Fact]
    public void AnAddressOnItsOwnRendersNothingThatCarriesTraffic() {
        // ⚠ THE SAFETY QUESTION FOR THIS TYPE, ANSWERED FROM THE RENDERED MANIFEST RATHER THAN FROM
        // INTENT. Four families have shipped an unsafe default when a tenant asked for nothing —
        // SeaweedFS's anonymous admin, Qdrant's unset api_key, MariaDB's root password,
        // harbor-helm's published Harbor12345 — so "what does the empty body do" is asked every time.
        //
        // Here the answer is safe, and it is safe STRUCTURALLY rather than by a default: every path
        // from the internet to a tenant workload goes through a SECOND Kube-OVN object naming this
        // EIP — an OvnFip, an OvnDnatRule or an OvnSnatRule — and this provider renders exactly one
        // object. There is no field on an OvnEip that publishes anything.
        using var body = JsonDocument.Parse(PublicIpAddresses.Body(Cluster));

        var document = JsonNode.Parse(
            PublicIpAddresses.OvnEipJson("ns", "edge", body.RootElement)
        )!.AsObject();

        document["kind"]!.GetValue<string>().ShouldBe("OvnEip");

        var spec = document["spec"]!.AsObject();

        // ⚠ The whole spec, enumerated. An assertion that only checked for the fields it knew about
        // would go green the day somebody added one that publishes.
        spec.Select(x => x.Key).ShouldBe(["type"]);

        foreach (var forbidden in new[] { "nat", "fip", "dnat", "snat", "externalSubnet" }) {
            spec.ContainsKey(forbidden).ShouldBeFalse(
                $"the rendered OvnEip carries '{forbidden}', so an address created with an empty body "
                + "is doing something on the fabric"
            );
        }
    }

    // ── What the API refuses, and what is left over for the reconciler ───────────────────────────

    [Theory]
    [InlineData("10.0.0")]
    [InlineData("10.0.0.1.2")]
    [InlineData("10.100.0.7/32")]
    [InlineData("hello")]
    public void AMalformedIpv4AddressIsRefusedAtTheApiWithItsOwnPointer(string malformed) {
        // ⚠ REFUSED BEFORE THE WRITE PATH ANSWERS 202, which is what the whole family has been
        // reaching for since charts/managed/kube-ovn-vpc recorded
        // `address-space-is-validated-after-202`. ⚠ `10.100.0.7/32` is in the list deliberately: it
        // is the spelling somebody who has read the rest of this family will type, because every
        // other address property here is a CIDR. A public address is one host and the substrate wants
        // a bare address.
        var validated = PublicIpAddresses.Schema2026.Validate(
            JsonDocument.Parse(PublicIpAddresses.Body(Cluster, addressV4: malformed)).RootElement
        );

        validated.IsSuccess.ShouldBeFalse($"'{malformed}' was accepted by the schema");

        validated.Error!.Target.ShouldBe(
            "/properties/address/v4",
            "docs/plan/08 § Errors: target is a JSON Pointer into the request body so the portal can "
            + "highlight the field"
        );
    }

    [Fact]
    public void AnEmptyAddressIsAcceptedBecauseItIsTheOrdinaryRequest() {
        PublicIpAddresses.Schema2026
            .Validate(JsonDocument.Parse(PublicIpAddresses.Body(Cluster)).RootElement)
            .IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void AWrongFamilyAddressReachesTheReconcilerAndIsRefusedTerminally() {
        // ⚠ WHAT THE PATTERN CANNOT SEE. `fd00:ff::7` typed into the v4 slot matches neither pattern
        // as a v4 shape — but `10.100.0.7` typed into the V6 slot DOES match the v6 pattern, which
        // admits digits, colons and dots. So the family check has to be somewhere, and there is no
        // provider predicate anywhere on ResourceManagerService's write path — see NetworkAddressing.
        // It runs in the reconciler, terminally, because a body naming the wrong family can never
        // converge and retrying it forever would hide that behind a spinner.
        PublicIpAddresses.Schema2026
            .Validate(
                JsonDocument.Parse(PublicIpAddresses.Body(Cluster, addressV6: "10.100.0.7")).RootElement
            )
            .IsSuccess.ShouldBeTrue(
                "the v6 pattern admits digits and dots, so the API cannot refuse this"
            );

        using var body = JsonDocument.Parse(
            PublicIpAddresses.Body(Cluster, addressV6: "10.100.0.7")
        );

        PublicIpAddresses.AddressProblem(body.RootElement)
            .ShouldNotBeNull()
            .ShouldContain("/properties/address/v6");
    }

    [Fact]
    public void AnUpperCaseIpv6AddressIsRefusedHereRatherThanByTheController() {
        // ⚠ THE SUBSTRATE'S OWN RULE, MOVED EARLIER. handleAddOvnEip refuses an EIP whose spec.v6Ip
        // contains an upper-case character — util.ContainsUppercase — and it does so in the
        // controller, which is after this platform has answered 202. Refusing it in the reconciler
        // costs nothing and turns a silent non-convergence into a message with a pointer in it.
        using var body = JsonDocument.Parse(
            PublicIpAddresses.Body(Cluster, addressV6: "FD00:FF::7")
        );

        PublicIpAddresses.AddressProblem(body.RootElement)
            .ShouldNotBeNull()
            .ShouldContain("lower case");

        using var lower = JsonDocument.Parse(
            PublicIpAddresses.Body(Cluster, addressV6: "fd00:ff::7")
        );

        PublicIpAddresses.AddressProblem(lower.RootElement).ShouldBeNull();
    }

    [Fact]
    public async Task AWrongFamilyAddressNeverReachesTheCluster() {
        var reconciler = new PublicIpAddressReconciler(new FixedClock());
        var connection = new RecordingConnection();

        using var body = JsonDocument.Parse(
            PublicIpAddresses.Body(Cluster, addressV4: "fd00:ff::7")
        );

        var outcome = await Pass(
            reconciler,
            connection,
            Address("edge", TenantOne, SubscriptionOne),
            body.RootElement
        );

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.Failed);

        connection.Applied.ShouldBeEmpty(
            "a body the reconciler refuses was still applied, so the fabric holds an object for a "
            + "resource the platform reports as Failed"
        );
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     An <c>OvnEip</c> as it reads back one controller pass after this provider applied one.
    /// </summary>
    /// <param name="allocated">The address the fabric handed out.</param>
    /// <remarks>
    ///     ⚠ <b>Hand-written, because there is no controller in any harness this repository runs.</b>
    ///     Every field below is one <c>createOrUpdateOvnEipCR</c> writes on an object this platform
    ///     applied: the address, the MAC and the type into <c>.spec</c> through a full
    ///     <c>Update()</c>, and the same values again into <c>.status</c> through a merge patch on the
    ///     status subresource.
    ///     <para>
    ///         ⚠ <b><c>spec.externalSubnet</c> stays EMPTY here on purpose, and getting that wrong
    ///         would have made this fixture flatter than the truth.</b> Only
    ///         <c>createOrUpdateOvnEipCR</c>'s <i>create</i> branch — for EIPs the controller makes
    ///         itself — sets that field; the update branch leaves it exactly as it was applied. So on
    ///         an object this provider owns the pool is visible on the
    ///         <c>ovn.kubernetes.io/subnet</c> label rather than in the spec, and a fixture that
    ///         filled the spec field in would be asserting against a shape that never occurs.
    ///     </para>
    /// </remarks>
    static string AfterTheController(string allocated) =>
        new JsonObject {
            ["kind"] = "OvnEip",
            ["metadata"] = new JsonObject { ["name"] = "ns-edge" },
            ["spec"] = new JsonObject {
                ["externalSubnet"] = string.Empty,
                ["v4Ip"] = allocated,
                ["v6Ip"] = string.Empty,
                ["macAddress"] = "00:00:00:1A:2B:3C",
                ["type"] = PublicIpAddresses.UsageTypeNat
            },
            ["status"] = new JsonObject {
                ["v4Ip"] = allocated,
                ["v6Ip"] = string.Empty,
                ["macAddress"] = "00:00:00:1A:2B:3C",
                ["type"] = PublicIpAddresses.UsageTypeNat,
                ["nat"] = string.Empty,
                ["ready"] = true
            }
        }.ToJsonString();

    static async Task<ReconcileOutcome> Pass(
        PublicIpAddressReconciler reconciler,
        RecordingConnection connection,
        ResourceId address,
        JsonElement desired
    ) =>
        await reconciler.ReconcileAsync(
            new(
                address,
                PublicIpAddresses.V2026,
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
            PublicIpAddresses.Type,
            name,
            Guid.Parse("44444444-4444-4444-8444-444444444444")
        );

    static JsonObject Spec(string objectJson) => JsonNode.Parse(objectJson)!["spec"]!.AsObject();
}
