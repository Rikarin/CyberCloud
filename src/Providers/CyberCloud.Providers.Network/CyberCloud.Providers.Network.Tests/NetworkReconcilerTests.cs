using CyberCloud.Core.Time;
using CyberCloud.ResourceManager;
using CyberCloud.ResourceManager.Conformance;
using CyberCloud.ResourceManager.Reconcile;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Network.Tests;

/// <summary>
///     Both reconcilers against a connection that misbehaves in the ways a real cluster does.
/// </summary>
/// <remarks>
///     ⚠ <b>The harness at the bottom is a fresh copy and it has to be.</b>
///     <c>src/Providers/README.md § Hard rule</c> forbids a <c>Providers.*</c> assembly referencing
///     another, so <c>RecordingConnection</c>, <c>FixedClock</c> and <c>NullLog</c> cannot be shared
///     with any sibling however identical they look. That duplication is the price of the rule.
/// </remarks>
public sealed class NetworkReconcilerTests {
    static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001");
    static readonly Guid TenantB = Guid.Parse("bbbbbbbb-0000-4000-8000-000000000002");
    static readonly Guid SubscriptionA = Guid.Parse("aaaaaaaa-0000-4000-8000-00000000000a");
    static readonly Guid SubscriptionB = Guid.Parse("bbbbbbbb-0000-4000-8000-00000000000b");
    static readonly Guid ClusterId = Guid.Parse("cccccccc-0000-4000-8000-000000000003");

    // ── Failure class (a): a reconciler with a field ─────────────────────────────────────────────

    [Fact]
    public void NoReconcilerHoldsMutableState() {
        ReconcilerConformance.CheckNoHiddenState(new VirtualNetworkReconciler(new FixedClock()))
            .ShouldBeEmpty();

        ReconcilerConformance.CheckNoHiddenState(new NetworkSubnetReconciler(new FixedClock()))
            .ShouldBeEmpty();

        ReconcilerConformance.CheckNoHiddenState(new NetworkSecurityGroupReconciler(new FixedClock()))
            .ShouldBeEmpty();
    }

    [Fact]
    public async Task OneSecurityGroupReconcilerServesTwoTenantsWithoutMixingThem() {
        // ⚠ THE ONLY TEST THAT CATCHES THE READONLY-MUTABLE-FIELD SHAPE, RUN FOR THE THIRD RECONCILER
        // IN THIS FAMILY. CheckNoHiddenState above is structurally blind to it — six sightings in six
        // families — and AddCyberCloudProvider registers a reconciler as a SINGLETON BY CONCRETE TYPE,
        // so in a real silo ONE instance serves every tenant in the process.
        //
        // ⚠ AND ON THIS TYPE THE COLLISION IS THE WORST OF THE THREE. A SecurityGroup is
        // cluster-scoped AND carries no field naming its network, so the rendered NAME is the only
        // thing separating two tenants' groups called `web` — a collision would merge two rule sets
        // into one OVN port group, which on a firewall means each tenant's ports get the other's
        // allow list, with nothing reporting an error anywhere.
        var reconciler = new NetworkSecurityGroupReconciler(new FixedClock());

        var alice = GroupAddress("web", "net", TenantA, SubscriptionA);
        var bob = GroupAddress("web", "net", TenantB, SubscriptionB);

        var connection = new RecordingConnection();

        using var aliceBody = JsonDocument.Parse(
            NetworkSecurityGroups.Body(ClusterId, ingressTcpPorts: "443")
        );

        using var bobBody = JsonDocument.Parse(
            NetworkSecurityGroups.Body(ClusterId, ingressTcpPorts: "5432", allowSameGroupTraffic: true)
        );

        await PassGroup(reconciler, connection, alice, aliceBody.RootElement);
        await PassGroup(reconciler, connection, bob, bobBody.RootElement);
        await PassGroup(reconciler, connection, alice, aliceBody.RootElement);
        await PassGroup(reconciler, connection, bob, bobBody.RootElement);

        var applied = connection.Applied;
        applied.Count.ShouldBe(4);

        // ⚠ THE THIRD AND FOURTH PASSES, not the first two. A cache populated on pass one is only
        // visible from pass three, which is why the sequence is A, B, A, B rather than A, B.
        Spec(applied[2].Body)["ingressRules"]![0]!["portRangeMin"]!.GetValue<int>()
            .ShouldBe(443, "tenant A's rule came back as tenant B's");

        Spec(applied[3].Body)["allowSameGroupTraffic"]!.GetValue<bool>()
            .ShouldBeTrue("tenant B's same-group flag came back as tenant A's");

        applied[0].Target.Name.ShouldNotBe(
            applied[1].Target.Name,
            "two subscriptions' identically-named security groups rendered ONE cluster-scoped "
            + "SecurityGroup, so each tenant's ports would carry the other's allow list"
        );
    }

    [Fact]
    public async Task TwoNetworksInOneResourceGroupEachHoldAGroupOfTheSameNameWithoutColliding() {
        // ⚠ THE CASE THE SHARED HARNESS CANNOT BUILD, AND SHARPER HERE THAN ON A SUBNET. A Subnet at
        // least names its Vpc, so a collision would be visible in the object; a SecurityGroup names
        // nothing at all, so the only evidence would be the wrong rules on somebody's ports.
        var reconciler = new NetworkSecurityGroupReconciler(new FixedClock());
        var connection = new RecordingConnection();

        using var body = JsonDocument.Parse(NetworkSecurityGroups.Body(ClusterId));

        await PassGroup(
            reconciler,
            connection,
            GroupAddress("web", "frontend", TenantA, SubscriptionA),
            body.RootElement
        );

        await PassGroup(
            reconciler,
            connection,
            GroupAddress("web", "backend", TenantA, SubscriptionA),
            body.RootElement
        );

        connection.Applied[0].Target.Name.ShouldNotBe(
            connection.Applied[1].Target.Name,
            "two networks in ONE resource group each holding a group called `web` rendered one object"
        );
    }

    [Fact]
    public async Task ABackwardsPortRangeIsRefusedTerminallyAndNothingIsApplied() {
        // ⚠ THE ONE RELATION THE SCHEMA CANNOT SEE, AND FAILED RATHER THAN InProgress for the reason
        // the address-space refusals are: `443-80` can never converge, and retrying it forever would
        // leave the resource reading as "still working on it" rather than "your rule is backwards".
        var reconciler = new NetworkSecurityGroupReconciler(new FixedClock());
        var connection = new RecordingConnection();

        using var body = JsonDocument.Parse(
            NetworkSecurityGroups.Body(ClusterId, ingressTcpPorts: "443-80")
        );

        var outcome = await PassGroup(
            reconciler,
            connection,
            GroupAddress("bad", "net", TenantA, SubscriptionA),
            body.RootElement
        );

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.Failed);
        outcome.Error!.Message.ShouldContain("/properties/ingress/tcpPorts", Case.Sensitive);

        // ⚠ NOTHING WAS APPLIED, AND ON THIS TYPE THAT IS A SECURITY PROPERTY. A partially-rendered
        // security group is a perimeter with some of its rules in it, and a tenant reading Failed has
        // no reason to believe anything was programmed at all.
        connection.Applied.ShouldBeEmpty();
    }

    [Fact]
    public void TheStructuralCheckCatchesAReadonlyMutableCache() {
        // ⚠ CALIBRATION, AND IT NOW POINTS THE OTHER WAY. This test used to assert that
        // CheckNoHiddenState MISSED the counter-example below, because it skipped every
        // `field.IsInitOnly` — and `readonly` stops the FIELD being reassigned while stopping
        // nothing about the dictionary, so a per-tenant cache passed clause 2 while accumulating
        // state on a singleton every tenant shares. Seven families each pinned that blind spot
        // and it is now closed; this is what holds it closed.
        //
        // ⚠ THE CROSS-TENANT TEST BELOW STAYS, AND IS NOT MADE REDUNDANT BY THIS. This one reads
        // a field's declared TYPE. That one drives ONE reconciler instance through TWO tenants and
        // compares what each got, which is the only way to catch mixing no field type could show.
        var findings = ReconcilerConformance.CheckNoHiddenState(new ReconcilerWithAReadonlyCache());

        findings.ShouldContain(
            x => x.Clause == ReconcilerClause.NoHiddenState,
            "a readonly field holding a mutable Dictionary is state on a shared singleton, and the "
            + "structural check is what catches it before the behavioural test has to"
        );

        findings.ShouldContain(x => x.Detail.Contains("lastRendered", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OneVirtualNetworkReconcilerServesTwoTenantsWithoutMixingThem() {
        // ⚠ THE TEST A SINGLE-TENANT TEST CANNOT BE, AND THE ONLY ONE THAT CATCHES THE CACHE ABOVE.
        // AddCyberCloudProvider registers a reconciler as a SINGLETON BY CONCRETE TYPE, so in a real
        // silo ONE instance serves every tenant in the process.
        //
        // ⚠ AND ON THIS FAMILY IT CHECKS SOMETHING NO EARLIER FAMILY'S VERSION COULD: the Vpc is
        // CLUSTER-SCOPED, so the API server's own namespacing is not there to separate the two
        // tenants. If VirtualNetworks.ObjectNameOf stopped folding in the namespace, both tenants
        // would render ONE object named `prod` and each would converge by overwriting the other, with
        // nothing reporting an error anywhere.
        var reconciler = new VirtualNetworkReconciler(new FixedClock());

        // ⚠ THE SAME RESOURCE NAME IN BOTH TENANTS, which is the ordinary case. Each brings its OWN
        // subscription, because ReconcileDriver.NamespaceFor is `{subscriptionId:N}-{resourceGroup}`
        // and the TENANT ID IS NOT IN IT.
        var alice = Address("prod", TenantA, SubscriptionA);
        var bob = Address("prod", TenantB, SubscriptionB);

        var connection = new RecordingConnection();

        using var aliceBody = JsonDocument.Parse(
            VirtualNetworks.Body(ClusterId, addressSpaceV4: "10.20.0.0/16")
        );

        using var bobBody = JsonDocument.Parse(
            VirtualNetworks.Body(ClusterId, addressSpaceV4: "10.20.0.0/16", enableExternal: true)
        );

        // Interleaved, so a cache written on the first pass is read on the third.
        await Pass(reconciler, connection, alice, aliceBody.RootElement);
        await Pass(reconciler, connection, bob, bobBody.RootElement);
        await Pass(reconciler, connection, alice, aliceBody.RootElement);
        await Pass(reconciler, connection, bob, bobBody.RootElement);

        var applied = connection.Applied;
        applied.Count.ShouldBe(4);

        Spec(applied[0].Body)["enableExternal"]!.GetValue<bool>().ShouldBeFalse();
        Spec(applied[1].Body)["enableExternal"]!.GetValue<bool>().ShouldBeTrue();

        Spec(applied[2].Body)["enableExternal"]!.GetValue<bool>()
            .ShouldBeFalse("tenant A's external flag came back as tenant B's");

        Spec(applied[3].Body)["enableExternal"]!.GetValue<bool>()
            .ShouldBeTrue("tenant B's external flag came back as tenant A's");

        applied[0].Labels[KubeLabels.TenantId].ShouldBe(KubeLabels.GuidValue(TenantA));
        applied[1].Labels[KubeLabels.TenantId].ShouldBe(KubeLabels.GuidValue(TenantB));

        // ⚠ THE ASSERTION THAT REPLACES "different namespaces" FOR A CLUSTER-SCOPED OBJECT. Every
        // earlier family ends this test by checking the two objects landed in different NAMESPACES.
        // These two land in no namespace at all, so what has to differ is the NAME.
        applied[0].Target.Namespace.ShouldBe(string.Empty);
        applied[1].Target.Namespace.ShouldBe(string.Empty);

        applied[0].Target.Name.ShouldNotBe(
            applied[1].Target.Name,
            "two subscriptions' identically-named virtual networks rendered ONE cluster-scoped Vpc, so "
            + "each converges by overwriting the other and neither reports an error"
        );
    }

    [Fact]
    public async Task OneSubnetReconcilerServesTwoTenantsWithoutMixingThem() {
        var reconciler = new NetworkSubnetReconciler(new FixedClock());

        var alice = SubnetAddress("web", "net", TenantA, SubscriptionA);
        var bob = SubnetAddress("web", "net", TenantB, SubscriptionB);

        var connection = new RecordingConnection();

        using var aliceBody = JsonDocument.Parse(
            NetworkSubnets.Body(ClusterId, prefixV4: "10.20.1.0/24")
        );

        using var bobBody = JsonDocument.Parse(
            NetworkSubnets.Body(ClusterId, prefixV4: "10.20.2.0/24", natOutgoing: true)
        );

        await PassSubnet(reconciler, connection, alice, aliceBody.RootElement);
        await PassSubnet(reconciler, connection, bob, bobBody.RootElement);
        await PassSubnet(reconciler, connection, alice, aliceBody.RootElement);
        await PassSubnet(reconciler, connection, bob, bobBody.RootElement);

        var applied = connection.Applied;
        applied.Count.ShouldBe(4);

        Spec(applied[2].Body)["cidrBlock"]!.GetValue<string>()
            .ShouldBe("10.20.1.0/24", "tenant A's prefix came back as tenant B's");

        Spec(applied[3].Body)["natOutgoing"]!.GetValue<bool>()
            .ShouldBeTrue("tenant B's NAT flag came back as tenant A's");

        applied[0].Target.Name.ShouldNotBe(applied[1].Target.Name);
    }

    [Fact]
    public async Task TwoNetworksInOneResourceGroupEachHoldASubnetOfTheSameNameWithoutColliding() {
        // ⚠ THE CASE THE SHARED CONFORMANCE HARNESS CANNOT BUILD, AND THE ONE A CHILD TYPE EXISTS TO
        // BE WRONG ABOUT. ReconcileDriver.NamespaceFor is `{subscriptionId:N}-{resourceGroup}` — a
        // parent resource lives INSIDE a namespace rather than being one — so a renderer that ignored
        // ResourceId.ParentNames would have both subnets fighting over one Subnet object, each
        // converging by overwriting the other, with neither reporting an error.
        var reconciler = new NetworkSubnetReconciler(new FixedClock());
        var connection = new RecordingConnection();

        var inFrontend = SubnetAddress("web", "frontend", TenantA, SubscriptionA);
        var inBackend = SubnetAddress("web", "backend", TenantA, SubscriptionA);

        using var body = JsonDocument.Parse(NetworkSubnets.Body(ClusterId));

        await PassSubnet(reconciler, connection, inFrontend, body.RootElement);
        await PassSubnet(reconciler, connection, inBackend, body.RootElement);

        var applied = connection.Applied;

        applied[0].Target.Name.ShouldNotBe(
            applied[1].Target.Name,
            "two networks in ONE resource group each holding a subnet called `web` rendered one object"
        );

        // ⚠ And each binds to its OWN network's Vpc. This is the field the shared suite cannot check
        // — ObjectMatchesDesired carries no address — and the one whose wrong value hands a range out
        // inside another tenant's routing domain.
        Spec(applied[0].Body)["vpc"]!.GetValue<string>().ShouldEndWith("-frontend");
        Spec(applied[1].Body)["vpc"]!.GetValue<string>().ShouldEndWith("-backend");
    }

    // ── The refusal that belongs at the API and runs here ────────────────────────────────────────

    [Fact]
    public async Task AnAddressSpaceOverlappingTheUnderlayIsRefusedTerminallyAndNothingIsApplied() {
        // ⚠ THE CHECK docs/plan/14 ASKS THE API FOR. ResourceSchema cannot express it and there is no
        // provider seam on the write path — NetworkAddressing carries the whole argument — so it runs
        // here, after the caller was told 202.
        //
        // ⚠ FAILED RATHER THAN InProgress. A body whose address space overlaps the underlay can never
        // converge; retrying it every thirty seconds forever would leave the resource reading as
        // "still working on it".
        var reconciler = new VirtualNetworkReconciler(new FixedClock());
        var connection = new RecordingConnection();

        using var body = JsonDocument.Parse(
            VirtualNetworks.Body(ClusterId, addressSpaceV4: "10.96.0.0/12")
        );

        var outcome = await Pass(
            reconciler,
            connection,
            Address("bad", TenantA, SubscriptionA),
            body.RootElement
        );

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.Failed);
        outcome.Error!.Message.ShouldContain("kubernetes-services", Case.Sensitive);
        outcome.Error!.Message.ShouldContain("10.96.0.0/12", Case.Sensitive);

        // ⚠ NOTHING WAS APPLIED. The object is cluster-scoped, so a stray one would hold its name
        // against every other subscription on the platform until somebody deleted it by hand.
        connection.Applied.ShouldBeEmpty();
    }

    [Fact]
    public async Task ASubnetPrefixOverlappingTheUnderlayIsRefusedTheSameWay() {
        var reconciler = new NetworkSubnetReconciler(new FixedClock());
        var connection = new RecordingConnection();

        using var body = JsonDocument.Parse(
            NetworkSubnets.Body(ClusterId, prefixV4: "10.16.5.0/24")
        );

        var outcome = await PassSubnet(
            reconciler,
            connection,
            SubnetAddress("bad", "net", TenantA, SubscriptionA),
            body.RootElement
        );

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.Failed);
        outcome.Error!.Message.ShouldContain("kube-ovn-default-subnet", Case.Sensitive);
        connection.Applied.ShouldBeEmpty();
    }

    [Fact]
    public async Task ALegalAddressSpaceIsAppliedAndConverges() {
        var reconciler = new VirtualNetworkReconciler(new FixedClock());
        var connection = new RecordingConnection();

        using var body = JsonDocument.Parse(VirtualNetworks.Body(ClusterId));

        var outcome = await Pass(
            reconciler,
            connection,
            Address("good", TenantA, SubscriptionA),
            body.RootElement
        );

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.Converged);
        connection.Applied.Count.ShouldBe(1);
    }

    // ── The four clauses, isolated ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task ConvergedFollowsTheReadAndNotTheApply() {
        // Clause 4. An apply that reports success and stores nothing is what a swallowing admission
        // webhook looks like from here — and a reconciler that trusted the apply's own result would
        // report Converged for a cluster with nothing in it.
        var connection = new RecordingConnection { SwallowApplies = true };
        using var body = JsonDocument.Parse(VirtualNetworks.Body(ClusterId));

        var outcome = await Pass(
            new VirtualNetworkReconciler(new FixedClock()),
            connection,
            Address("swallowed", TenantA, SubscriptionA),
            body.RootElement
        );

        outcome.Kind.ShouldNotBe(ReconcileOutcomeKind.Converged);
        connection.Read.ShouldNotBeEmpty("clause 4 requires a read, and there was none");
    }

    [Fact]
    public async Task AnUnreachableClusterSuspendsRatherThanFailing() {
        var connection = new RecordingConnection { Suspend = true };
        using var body = JsonDocument.Parse(VirtualNetworks.Body(ClusterId));

        var outcome = await Pass(
            new VirtualNetworkReconciler(new FixedClock()),
            connection,
            Address("offline", TenantA, SubscriptionA),
            body.RootElement
        );

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.InProgress);
    }

    [Fact]
    public async Task AConflictIsNotForcedBecauseTheOtherFieldManagerIsTheController() {
        // ⚠ formatSubnet writes gateway, excludeIps, protocol, provider, gatewayType and enableLb;
        // formatVpc fills staticRoutes[].policy. Those are the CONTROLLER'S fields. Forcing would
        // fight it every pass, which is why neither reconciler forces.
        var connection = new RecordingConnection { ConflictField = ".spec.gateway" };
        using var body = JsonDocument.Parse(NetworkSubnets.Body(ClusterId));

        var outcome = await PassSubnet(
            new NetworkSubnetReconciler(new FixedClock()),
            connection,
            SubnetAddress("web", "net", TenantA, SubscriptionA),
            body.RootElement
        );

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.InProgress);

        connection.Applied.ShouldAllBe(x => !x.Force);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────

    static async Task<ReconcileOutcome> Pass(
        VirtualNetworkReconciler reconciler,
        RecordingConnection connection,
        ResourceId address,
        JsonElement desired
    ) =>
        await reconciler.ReconcileAsync(
            new(
                address,
                VirtualNetworks.V2026,
                desired,
                null,
                ReconcileDriver.NamespaceFor(address),
                connection,
                new UnavailableSecretResolver(),
                new NullLog()
            ),
            TestContext.Current.CancellationToken
        );

    static async Task<ReconcileOutcome> PassSubnet(
        NetworkSubnetReconciler reconciler,
        RecordingConnection connection,
        ResourceId address,
        JsonElement desired
    ) =>
        await reconciler.ReconcileAsync(
            new(
                address,
                NetworkSubnets.V2026,
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
            VirtualNetworks.Type,
            name,
            Guid.Parse("33333333-3333-4333-8333-333333333333")
        );

    static async Task<ReconcileOutcome> PassGroup(
        NetworkSecurityGroupReconciler reconciler,
        RecordingConnection connection,
        ResourceId address,
        JsonElement desired
    ) =>
        await reconciler.ReconcileAsync(
            new(
                address,
                NetworkSecurityGroups.V2026,
                desired,
                null,
                ReconcileDriver.NamespaceFor(address),
                connection,
                new UnavailableSecretResolver(),
                new NullLog()
            ),
            TestContext.Current.CancellationToken
        );

    static ResourceId GroupAddress(string name, string network, Guid tenant, Guid subscription) =>
        new(
            tenant,
            subscription,
            "prod",
            NetworkSecurityGroups.Type,
            name,
            Guid.Parse("33333333-3333-4333-8333-333333333333"),
            network
        );

    static ResourceId SubnetAddress(string name, string network, Guid tenant, Guid subscription) =>
        new(
            tenant,
            subscription,
            "prod",
            NetworkSubnets.Type,
            name,
            Guid.Parse("33333333-3333-4333-8333-333333333333"),
            network
        );

    static JsonObject Spec(string objectJson) => JsonNode.Parse(objectJson)!["spec"]!.AsObject();
}

/// <summary>
///     A reconciler that <c>CheckNoHiddenState</c> reports and that is not stateless.
/// </summary>
/// <remarks>
///     The field is <see langword="readonly" />, which stops it being reassigned and stops nothing
///     about the dictionary it holds. That is the shape a per-tenant cache takes when somebody adds
///     one for performance. <c>CheckNoHiddenState</c> used to skip it for being
///     <see langword="readonly" /> and now reports it; the cross-tenant test in the sibling file is
///     what still catches the mixing a field's declared type cannot show.
/// </remarks>
sealed class ReconcilerWithAReadonlyCache : IResourceReconciler {
    readonly Dictionary<string, string> lastRendered = new(StringComparer.Ordinal);

    public ResourceTypeName Type => VirtualNetworks.Type;

    public Task<ReconcileOutcome> ReconcileAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        lastRendered[context.Id.Name] = context.Desired.ToString();
        return Task.FromResult(ReconcileOutcome.Converged);
    }

    public Task<ReconcileOutcome> DeleteAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) => Task.FromResult(ReconcileOutcome.Converged);

    public Task<ObservedState> ObserveAsync(
        ObserveContext context,
        CancellationToken cancellationToken = default
    ) => Task.FromResult(ObservedState.Absent);
}

/// <summary>A cluster connection that records what it was asked to do.</summary>
sealed class RecordingConnection : IKubeClusterConnection {
    /// <summary>What is in the "cluster", keyed by kind, namespace and name.</summary>
    public ConcurrentDictionary<string, string> Objects { get; } = new(StringComparer.Ordinal);

    /// <summary>Every command applied, in order.</summary>
    public List<KubeCommand> Applied { get; } = [];

    /// <summary>Every object deleted, in order.</summary>
    public List<ObjectRef> Deleted { get; } = [];

    /// <summary>Every object <i>read</i>, in order — clause 4's evidence.</summary>
    public List<ObjectRef> Read { get; } = [];

    /// <summary>Whether every apply answers <c>Suspended</c>.</summary>
    public bool Suspend { get; init; }

    /// <summary>The field another manager owns, or empty.</summary>
    public string ConflictField { get; init; } = string.Empty;

    /// <summary>Whether an apply reports success and stores nothing — the clause-4 trap.</summary>
    public bool SwallowApplies { get; init; }

    public Guid ClusterId => Guid.Parse("cccccccc-0000-4000-8000-000000000003");

    public Task<Result<ApplyOutcome>> ApplyAsync(
        KubeCommand command,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(command);
        Applied.Add(command);

        if (Suspend) {
            return Task.FromResult(
                Result<ApplyOutcome>.Success(
                    new() {
                        Result = ApplyResult.Suspended,
                        Target = command.Target,
                        Message = "We cannot reach your cluster; this will resume automatically."
                    }
                )
            );
        }

        if (ConflictField.Length > 0) {
            return Task.FromResult(
                Result<ApplyOutcome>.Success(
                    new() {
                        Result = ApplyResult.Conflict,
                        Target = command.Target,
                        Drift = new() {
                            Target = command.Target,
                            FieldManager = command.FieldManager,
                            Conflicts = [new() { Field = ConflictField, OwnedBy = "kube-ovn-controller" }]
                        }
                    }
                )
            );
        }

        if (!SwallowApplies) {
            Objects[Key(command.Target)] = command.Body;
        }

        return Task.FromResult(
            Result<ApplyOutcome>.Success(new() { Result = ApplyResult.Created, Target = command.Target })
        );
    }

    public Task<Result<KubeObject>> GetAsync(
        ObjectRef target,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(target);
        Read.Add(target);

        return Task.FromResult(
            Objects.TryGetValue(Key(target), out var json)
                ? Result<KubeObject>.Success(new() { Ref = target, Json = json })
                : Result<KubeObject>.Failure(ErrorCode.ResourceNotFound, $"'{target}' is not here.")
        );
    }

    public Task<Result> DeleteAsync(
        KubeCommand command,
        CascadePolicy policy = CascadePolicy.Background,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(command);

        var removed = Objects.TryRemove(Key(command.Target), out _);

        if (removed) {
            Deleted.Add(command.Target);
        }

        return Task.FromResult(
            removed
                ? Result.Success
                : Result.Failure(ErrorCode.ResourceNotFound, $"'{command.Target}' is not here.")
        );
    }

    /// <summary>
    ///     ⚠ Keyed by kind, namespace AND name. On this family the namespace is always empty — every
    ///     object is cluster-scoped — so the NAME is doing all the separating, which is exactly what
    ///     the cross-tenant tests are checking.
    /// </summary>
    internal static string Key(ObjectRef target) =>
        target.Kind.Kind + "/" + target.Namespace + "/" + target.Name;
}

/// <summary>A clock that does not move. Nothing here depends on time passing.</summary>
sealed class FixedClock : IClock {
    public DateTimeOffset UtcNow => new(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);
}

/// <summary>A log that drops everything. These tests assert outcomes, not progress.</summary>
sealed class NullLog : IReconcileLog {
    public void Report(string phase, string detail) { }

    public void Report(string phase, string detail, int percent) { }
}
