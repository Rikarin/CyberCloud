using CyberCloud.ResourceManager.Registry;

namespace CyberCloud.Providers.Network.Tests;

/// <summary>
///     What this provider declares into the registry, and the isolation claim it may not exceed.
/// </summary>
public sealed class NetworkDeclarationTests {
    /// <summary>
    ///     Every CLI group key in the tree, as a literal.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>LITERALS RATHER THAN A REFLECTION SWEEP, AND THAT IS THE POINT OF THE TEST.</b>
    ///     <c>CliEmitter</c> derives a group key from each provider namespace's last segment, and a
    ///     sweep would compute the same wrong answer as the code it is checking. These are typed out
    ///     so that the day somebody adds a namespace whose key collides with a short name here, this
    ///     list is out of date and a human notices — which is a better failure than
    ///     System.CommandLine's, which is <c>ArgumentException: An item with the same key has already
    ///     been added</c> on <b>every</b> <c>cyc</c> parse, naming neither the provider nor the string.
    ///     Two agents have hit exactly that.
    /// </remarks>
    public static readonly string[] GroupKeys = [
        "sample",
        "dbforpostgresql",
        "cache",
        "messaging",
        "storage",
        "search",
        "documentdb",
        "analytics",
        "dbformysql",
        "network"
    ];

    /// <summary>Every short name already declared in the tree, as a literal.</summary>
    public static readonly string[] ExistingShortNames = [
        "widget",
        "postgres",
        "valkey",
        "kafka",
        "nats",
        "rabbitmq",
        "objectstore",
        "bucket",
        "opensearch",
        "docdb",
        "clickhouse",
        "mariadb"
    ];

    [Fact]
    public void TheProviderBuildsTheWayASiloBuildsIt() {
        // ⚠ ProviderRegistry.Build is what runs at silo start, and it is where a duplicate short
        // name, a reconciler registered for the wrong type and an incoherent schema all become a
        // process that does not start. Running it here is what turns those into a test failure.
        var registry = Build();

        registry.Types.Length.ShouldBe(2);

        registry.Types.Select(x => x.Type.ToString()).ShouldBe(
            ["CyberCloud.Network/virtualNetworks", "CyberCloud.Network/virtualNetworks/subnets"],
            ignoreOrder: true
        );
    }

    [Fact]
    public void TheChildInterleavesItsParentRatherThanBeingFlattened() {
        // ⚠ THE ASSERTION THAT KEEPS THE 28TH CONFORMANCE ASSERTION APPLICABLE.
        // CreatingUnderAParentThatDoesNotExistIsTheSame404AsAnAbsentResource self-skips at Depth 1,
        // so a flattened `subnets` would silently drop the one assertion a child type exists to
        // exercise. On this family the flattened form would also be a lie about the substrate: a
        // Kube-OVN Subnet with no `spec.vpc` belongs to the DEFAULT VPC, which is the platform's own.
        NetworkSubnets.TypePath.ShouldBe("virtualNetworks/subnets");
        NetworkSubnets.Type.Depth.ShouldBe(2);
        VirtualNetworks.Type.Depth.ShouldBe(1);
    }

    [Fact]
    public void TheChildIsServedAtTheSameApiVersionAsItsParent() {
        // ⚠ A parent and a child at different dates would make
        // …/virtualNetworks/{n}/subnets/{s}?api-version=… a request whose single version parameter
        // has to mean two things.
        NetworkSubnets.V2026.ShouldBe(VirtualNetworks.V2026);
    }

    [Theory]
    [MemberData(nameof(EveryGroupKey))]
    public void NeitherShortNameIsAnyProvidersGroupKey(string groupKey) {
        // ⚠ System.CommandLine's ValidTokens is ONE dictionary of every command token and every alias
        // in the whole tree, so a group and an alias that share a string throw on the first parse of
        // ANY command line. ProviderRegistry.Build refuses a DUPLICATE short name and never compares
        // one against a group name — charts/managed/seaweedfs/conformance.yaml § owed,
        // `short-name-collides-with-the-group` — so this is checked by hand, for the third and fourth
        // time.
        foreach (var shortName in ShortNames()) {
            shortName.ShouldNotBe(
                groupKey,
                $"'{shortName}' is also a CLI group key, so every `cyc` invocation would throw "
                + "ArgumentException naming neither this provider nor the string"
            );
        }
    }

    [Theory]
    [MemberData(nameof(EveryExistingShortName))]
    public void NeitherShortNameCollidesWithOneThatAlreadyExists(string existing) {
        foreach (var shortName in ShortNames()) {
            shortName.ShouldNotBe(existing);
        }
    }

    [Fact]
    public void TheTwoShortNamesAreDistinctFromEachOther() {
        // ⚠ With two types in one family there are two chances to collide, including with each
        // other — and ProviderRegistry.Build DOES refuse this one, which is why the assertion is
        // cheap and worth having anyway: it names the problem where a silo-start failure would not.
        var names = ShortNames().ToList();

        names.Distinct(StringComparer.Ordinal).Count().ShouldBe(names.Count);
    }

    [Fact]
    public void TheShortNamesAreTheOnesTheProviderMeantToDeclare() {
        ShortNames().ShouldBe(["vnet", "subnet"], ignoreOrder: true);
    }

    [Fact]
    public void NeitherTypeDeclaresSoftDelete() {
        // ⚠ Nothing in CyberCloud.ResourceManager reads SoftDeleteDays, so declaring a recovery
        // window would be a promise made to the users most likely to test it. A separate agent is
        // building the feature; this asserts the promise is not made early.
        foreach (var type in Build().Types) {
            type.SoftDeleteDays.ShouldBe(0, type.Type.ToString());
        }
    }

    [Fact]
    public void BothTypesRequireAClusterAndSupportTags() {
        foreach (var type in Build().Types) {
            type.SupportsTags.ShouldBeTrue(type.Type.ToString());
            type.ClusterIdPointer.ShouldBe(ClusterPlacement.DefaultPointer, type.Type.ToString());
        }
    }

    [Fact]
    public void TheObjectNameFoldsInTheNamespaceAndCannotOverflowAKubernetesName() {
        // ⚠ THE ARITHMETIC, PINNED, BECAUSE THE OBJECT IS CLUSTER-SCOPED AND THERE IS NO TRUNCATION
        // BRANCH. A Kubernetes object name is 253 characters. The worst case here is a 32-character
        // GUID with no hyphens, a hyphen, a 63-character resource group, a hyphen, a 63-character
        // network name and — for a subnet — a hyphen and a 63-character subnet name.
        const int Guid32 = 32;
        const int MaxName = 63; // ResourceNaming.Pattern's cap.

        var worstNetwork = Guid32 + 1 + MaxName + 1 + MaxName;
        var worstSubnet = worstNetwork + 1 + MaxName;

        worstSubnet.ShouldBeLessThan(
            253,
            "a cluster-scoped object name can overflow, and there is no truncation branch — which is "
            + "correct, because truncation would silently make two tenants' objects collide again"
        );

        VirtualNetworks.ObjectNameOf("ns", "net").ShouldBe("ns-net");
    }

    [Fact]
    public void ASubnetsObjectNameCarriesItsNetworkAndRefusesAnAddressWithoutOne() {
        var id = new ResourceId(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "prod",
            NetworkSubnets.Type,
            "web",
            Guid.NewGuid(),
            "net"
        );

        NetworkSubnets.ObjectNameOf("ns", id).ShouldBe("ns-net-web");
        NetworkSubnets.VpcRefOf("ns", id).ShouldBe("ns-net");

        // ⚠ An id with no parent throws rather than rendering an empty `spec.vpc`, because an unbound
        // Kube-OVN Subnet joins the DEFAULT VPC — the platform's own — which is the worst available
        // failure and would be reported by nothing.
        var orphan = new ResourceId(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "prod",
            VirtualNetworks.Type,
            "web",
            Guid.NewGuid()
        );

        Should.Throw<ArgumentException>(() => NetworkSubnets.ObjectNameOf("ns", orphan));
        Should.Throw<ArgumentException>(() => NetworkSubnets.VpcRefOf("ns", orphan));
    }

    [Fact]
    public void BothTypesRenderClusterScopedObjectRefs() {
        // ⚠ Every Kube-OVN kind this family touches is +kubebuilder:resource:scope="Cluster", and
        // ObjectRef spells that as an empty namespace. A ref carrying a namespace would be applied to
        // a REST path the API server does not serve for these kinds.
        VirtualNetworks.VpcRef("ns", "net").IsClusterScoped.ShouldBeTrue();

        var id = new ResourceId(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "prod",
            NetworkSubnets.Type,
            "web",
            Guid.NewGuid(),
            "net"
        );

        NetworkSubnets.SubnetRef("ns", id).IsClusterScoped.ShouldBeTrue();
    }

    // ── The isolation claim, which docs/plan/14 makes the named risk of this row ─────────────────

    [Fact]
    public void TheRegisteredSummaryCarriesTheIsolationClaimVerbatim() {
        // ⚠ docs/plan/14: "the marketing must not claim more than the substrate delivers". The
        // registered summary is what reaches the OpenAPI document, the CLI help, the portal and the
        // chart description, so it is the one place an optimistic sentence would spread from. Deriving
        // it from the constant rather than restating it is what makes that impossible; this asserts
        // the derivation is real.
        var network = Build().Types.Single(x => x.Type == VirtualNetworks.Type);

        network.Display.Summary.ShouldContain(VirtualNetworks.IsolationClaim, Case.Sensitive);
    }

    [Theory]
    [InlineData("isolated")]
    [InlineData("fully isolated")]
    [InlineData("completely")]
    [InlineData("air-gap")]
    [InlineData("dedicated hardware")]
    [InlineData("encrypted")]
    [InlineData("guaranteed")]
    public void NeitherTheClaimNorTheSummaryUsesAWordTheSubstrateDoesNotEarn(string forbidden) {
        // ⚠ THE TEST THAT MAKES docs/plan/14'S WARNING CHECKABLE. Every word here is one a reader
        // completes with a stronger guarantee than Open vSwitch provides. The claim says
        // "network-layer tenant separation … on shared hardware", which is what is true.
        VirtualNetworks.IsolationClaim.ShouldNotContain(forbidden, Case.Insensitive);

        foreach (var type in Build().Types) {
            type.Display.Summary.ShouldNotContain(forbidden, Case.Insensitive, type.Type.ToString());
        }
    }

    [Fact]
    public void TheClaimSaysTheHardwareIsShared() {
        // ⚠ The positive half. A claim that merely avoids the forbidden words could still imply more
        // by omission; docs/plan/14's caveat is specifically that Kube-OVN is NOT a hardware boundary
        // and that "a kernel bug in OVS is a cross-tenant risk", so the sentence has to say so.
        VirtualNetworks.IsolationClaim.ShouldContain("shared hardware", Case.Insensitive);
    }

    [Fact]
    public void TheLimitsTableNamesTheHardwareBoundaryAndOffersTheAlternative() {
        var limits = VirtualNetworks.IsolationLimits;

        limits.Length.ShouldBeGreaterThanOrEqualTo(4);

        limits.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count().ShouldBe(limits.Length);

        limits.ShouldContain(x => x.Id == "not-a-hardware-boundary");

        // ⚠ Each row must give a tenant somewhere to go. docs/plan/14 names the answer for this one —
        // "a dedicated cluster on dedicated hardware" — and a limits table that only says "no" is a
        // table that gets deleted by the first person who has to answer a customer with it.
        foreach (var limit in limits) {
            limit.Instead.Length.ShouldBeGreaterThan(20, limit.Id);
            limit.Because.Length.ShouldBeGreaterThan(40, limit.Id);
        }
    }

    [Fact]
    public void TheIsolationResponseCarriesTheClaimAndTheLimits() {
        var response = VirtualNetworks.ShowIsolationResponse;

        response.Declares("/claim").ShouldBeTrue();
        response.Declares("/limits").ShouldBeTrue();
        response.Declares("/substrate").ShouldBeTrue();

        // ⚠ The limits are an ARRAY OF SENTENCES rather than an array of rows, because
        // SchemaProperty.ElementKind refuses an array of objects. The structured table the platform
        // holds is flattened on the way out; this pins the shape so the reason stays visible.
        var limits = response.Properties.Single(x => x.JsonPointer == "/limits");

        limits.Kind.ShouldBe(SchemaKind.Array);
        limits.ElementKind.ShouldBe(SchemaKind.Text);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────

    public static TheoryData<string> EveryGroupKey() => [.. GroupKeys];

    public static TheoryData<string> EveryExistingShortName() => [.. ExistingShortNames];

    static IEnumerable<string> ShortNames() => Build().Types.Select(x => x.Display.Alias);

    static ProviderRegistry Build() => ProviderRegistry.Build([new NetworkProvider()]);
}
