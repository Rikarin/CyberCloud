using CyberCloud.ResourceManager.Contracts.Generation;
using CyberCloud.ResourceManager.Registry;
using CyberCloud.Tenancy.Contracts;

namespace CyberCloud.Providers.Network.Tests;

/// <summary>
///     What this provider declares into the registry, and the isolation claim it may not exceed.
/// </summary>
public sealed class NetworkDeclarationTests {
    [Fact]
    public void TheProviderBuildsTheWayASiloBuildsIt() {
        // ⚠ ProviderRegistry.Build is what runs at silo start, and it is where a duplicate short
        // name, a reconciler registered for the wrong type and an incoherent schema all become a
        // process that does not start. Running it here is what turns those into a test failure.
        var registry = Build();

        registry.Types.Length.ShouldBe(5);

        registry.Types.Select(x => x.Type.ToString()).ShouldBe(
            [
                "CyberCloud.Network/virtualNetworks",
                "CyberCloud.Network/virtualNetworks/subnets",
                "CyberCloud.Network/virtualNetworks/securityGroups",
                // ⚠ ONE SEGMENT, NOT TWO. The other three types in this family are a network and two
                // things inside one; an OvnEip names no VPC, so this is a top-level type at Depth 1.
                "CyberCloud.Network/publicIpAddresses",
                // ⚠ TWO SEGMENTS, WHICH IS NOT HOW docs/plan/14 SPELLS IT. That document writes
                // `CyberCloud.Network/loadBalancers`, and the substrate disagrees: every object this
                // type renders is annotated onto one subnet of one VPC, so the network comes from the
                // ADDRESS and cannot be wrong. The alternative is a network-name property nothing
                // validates. See NetworkProvider's remarks.
                "CyberCloud.Network/virtualNetworks/loadBalancers"
            ],
            ignoreOrder: true
        );
    }

    [Fact]
    public void OnlyThePublicAddressDrawsTheScarceMeterAndItDrawsExactlyOne() {
        // ⚠ THE ASSERTION THIS TYPE EXISTS FOR. QuotaMeter.PublicIps has been in QuotaGrain's
        // defaults — 20 per subscription — since before this provider, and NOTHING HAD EVER DRAWN IT:
        // every provider that reached for it wanted a CONDITIONAL draw and QuotaGrain.TryReserveAsync
        // refuses a non-positive amount by name, "A reservation must be positive; 0 is not."
        //
        // ⚠ AND IT IS FLAT, WHICH IS THE HALF THAT IS EASY TO GET WRONG. ResourceManagerService.
        // AmountFor answers `meter.Fallback ?? 1m` only when AmountPointer is EMPTY; a pointer that
        // resolved to nothing with no fallback is an InternalError that refuses the write. So a meter
        // declared through `Meter(PublicIps, "/properties/count")` by somebody being helpful would
        // turn every create into a 500. `Meters(...)` is the pointerless overload and this pins it.
        foreach (var type in Build().Types) {
            var meters = type.Meters.Select(x => x.Meter).ToList();

            meters.ShouldContain(QuotaMeter.Resources, type.Type.ToString());

            if (type.Type == PublicIpAddresses.Type) {
                meters.ShouldBe([QuotaMeter.PublicIps, QuotaMeter.Resources], ignoreOrder: true);
            } else {
                meters.ShouldNotContain(
                    QuotaMeter.PublicIps,
                    $"{type.Type} draws the scarce meter, and a Vpc, a Subnet and a SecurityGroup are "
                    + "rows in a database that consume no address at all"
                );
            }

            foreach (var meter in type.Meters) {
                meter.AmountPointer.ShouldBeEmpty(
                    $"{type.Type}/{meter.Meter} declares a pointer, so AmountFor no longer answers "
                    + "Fallback ?? 1m and a body that does not carry it refuses the write"
                );

                // ⚠ THE ONE EXCEPTION, AND IT IS THE TYPE THAT PROVISIONS A POD. Four of these five
                // types are rows in OVN's databases and draw a flat count. A load balancer is an
                // HAProxy Deployment — because ADR-019's ENABLE_LB=false leaves Kube-OVN's own
                // SwitchLBRule unreconciled — so its vCPU and memory come off a sizing preset through
                // a MeterDerivation. Its `Resources` meter is still flat.
                if (type.Type == LoadBalancers.Type && meter.Meter != QuotaMeter.Resources) {
                    meter.Derivation.ShouldNotBeNull($"{type.Type}/{meter.Meter}");

                    continue;
                }

                meter.Derivation.ShouldBeNull($"{type.Type}/{meter.Meter}");
            }
        }
    }

    [Fact]
    public void OnlyTheLoadBalancerDrawsComputeAndItDrawsItsPresetsRow() {
        // ⚠ THE ASSERTION THE FIFTH TYPE EXISTS FOR, AND IT IS A CLAIM ABOUT THE SUBSTRATE RATHER
        // THAN ABOUT ARITHMETIC. A Vpc, a Subnet and a SecurityGroup provision nothing a node gives
        // up, and this row would have been the same — Kube-OVN's SwitchLBRule is a VIP on a logical
        // switch with no pod anywhere — except that its controller, queues and workers all sit inside
        // `if config.EnableLb` and ADR-019 sets ENABLE_LB=false. So the proxy is a pod, and a pod is
        // vCPU and memory somebody is charged for.
        foreach (var type in Build().Types) {
            var meters = type.Meters.Select(x => x.Meter).ToList();

            if (type.Type == LoadBalancers.Type) {
                meters.ShouldBe(
                    [QuotaMeter.Vcpu, QuotaMeter.MemoryGb, QuotaMeter.Resources],
                    ignoreOrder: true
                );

                continue;
            }

            meters.ShouldNotContain(QuotaMeter.Vcpu, type.Type.ToString());
            meters.ShouldNotContain(QuotaMeter.MemoryGb, type.Type.ToString());
        }

        // ⚠ AND NO StorageGb ON ANY OF THE FIVE. The proxy's root filesystem is read-only, it mounts
        // one ConfigMap and HAProxy in TCP mode buffers in memory — a storage meter would reserve
        // disk nothing allocates.
        foreach (var type in Build().Types) {
            type.Meters.Select(x => x.Meter).ShouldNotContain(
                QuotaMeter.StorageGb,
                type.Type.ToString()
            );
        }
    }

    [Fact]
    public void EveryDeclaredActionNamesAHandlerThatServesItsOwnTypeAndAction() {
        // ⚠ THE ASSERTION THAT WOULD HAVE CAUGHT THREE 500s. ActionDispatcher refuses a SYNCHRONOUS
        // action whose HandlerType is null — "declares the action '…' and no handler for it, so it
        // cannot be run", an InternalError — and this family shipped two such actions because at the
        // time no provider in the tree had a handler and there was nowhere to put one. The
        // declaration still reached the OpenAPI document, the SDK and the CLI, so the gap was visible
        // only to whoever called it.
        //
        // ⚠ AND THE THREE FURTHER REFUSALS ARE CHECKED HERE RATHER THAN AT SILO START, because
        // ProviderBuilder.Action checks only that the Type implements the interface — it cannot know
        // what an INSTANCE will report. A handler whose Type or Action disagrees is a 500 on the first
        // call, which for `showIsolation` means the first time somebody asks what the platform does
        // not protect them from.
        foreach (var type in Build().Types) {
            foreach (var action in type.Actions) {
                action.LongRunning.ShouldBeFalse(
                    $"{type.Type}/{action.Name} — a long-running action cannot have a handler, and "
                    + "the operation grain drives the RECONCILER for one"
                );

                action.HandlerType.ShouldNotBeNull($"{type.Type}/{action.Name}");

                var handler = (IResourceActionHandler)Activator.CreateInstance(
                    action.HandlerType,
                    // ⚠ The one handler with a dependency takes an IClock; the others take none.
                    action.HandlerType.GetConstructors()[0].GetParameters().Length == 0
                        ? []
                        : [new FixedClock()]
                )!;

                handler.Type.ShouldBe(type.Type, action.Name);

                handler.Action.ShouldBe(
                    action.Name,
                    "a handler reporting a different action is refused by ActionDispatcher at call "
                    + "time, which is a 500 rather than a silo-start failure"
                );
            }
        }
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

    [Fact]
    public void NoShortNameHereGivesACycTokenTwoMeanings() {
        // ⚠ DERIVED, AND THE TWO LISTS THIS REPLACES ARE THE REASON. They held every group key and
        // every short name in the tree as literals, and they were stale on two consecutive passes —
        // green by luck both times, because nothing they had missed happened to collide. A list is
        // maintained by whoever remembers it exists; this reads what the provider declares.
        //
        // ⚠ AND THE OLD ONES ASKED THE WRONG QUESTION, WHICH IS WORSE THAN ASKING IT LATE. Measured
        // against System.CommandLine 2.0.10: the token dictionary is per PARENT command, so `cyc
        // monitor network` parses cleanly with `network` also a top-level group — a short name equal
        // to another group's key cannot collide. What can is a short name equal to its OWN group's
        // key, a sibling's command name, or a sibling's short name, and no list checked any of those.
        // CliTokens carries the rule and CliTokenTests carries the measurements.
        CliTokens.Collisions(Declarations()).ShouldBeEmpty();
    }

    // ⚠ A RESERVED-GROUP ASSERTION LIVED HERE AND ITS PREMISE WAS MEASURED FALSE. It read every
    // short name against `CommandTree.ReservedGroups`, on the stated grounds that "a short NAME is an
    // alias in the same System.CommandLine ValidTokens dictionary". Measured against the pinned
    // 2.0.10, that dictionary is per PARENT command: an alias sits under `network` and `cyc login`
    // sits at the root, so the two can never be one token and the assertion could not fail. The half
    // that IS real — a generated GROUP taking one of the nine — `CommandTree` throws on while the
    // root command is built, and `cyc.Tests.ReservedGroupTests` asserts over the whole tree rather
    // than one family at a time.

    [Fact]
    public void TheShortNamesAreTheOnesTheProviderMeantToDeclare() {
        // ⚠ `secgroup` AND NOT `sg`, WHICH IS KUBE-OVN'S OWN shortName. Two characters is a token
        // somebody else will reach for, and the collision throws on EVERY `cyc` parse rather than on
        // the one command that uses it.
        //
        // ⚠ `publicip` AND NOT `pip` OR `eip`, for the same reason plus one more: `eip` is the
        // SUBSTRATE'S word rather than the product's, and docs/plan/21 § Grammar spells the type
        // `publicIpAddresses`. A tenant who has never heard of Kube-OVN should be able to guess it.
        // ⚠ `loadbalancer` AND NOT `lb`, WHICH IS `secgroup`'s ARGUMENT ONE MORE TIME. Two characters
        // is a token somebody else will reach for, and docs/plan/21 § Grammar spells the type
        // `loadBalancers` — a tenant who has never heard of HAProxy should be able to guess it.
        ShortNames().ShouldBe(
            ["vnet", "subnet", "secgroup", "publicip", "loadbalancer"],
            ignoreOrder: true
        );
    }

    [Fact]
    public void NoTypeDeclaresSoftDelete() {
        // ⚠ THE REASON FOR THIS ASSERTION HAS CHANGED AND THE OLD ONE IS NOW FALSE. It read "nothing
        // in CyberCloud.ResourceManager reads SoftDeleteDays". It does:
        // ResourceManagerService.DeleteAsync branches on SoftDeleteDays > 0 and calls
        // IResourceIndexGrain.SoftDeleteAsync instead of ReleaseAsync, the operation spec carries
        // SoftDelete forward, and OperationGrain withholds the committed quota until a purge.
        //
        // ⚠ WHAT IS MISSING IS THE HALF A TENANT WOULD NEED: RestoreAsync and PurgeAsync exist on the
        // manager and have NO HTTP ROUTE. So a window declared today parks the name AND HOLDS THE
        // QUOTA for its whole length, with no way to recover the resource or release it early. On a
        // security group there is a second reason: its rules ARE its content, and a group whose name
        // is parked while its ACLs are gone is a perimeter a tenant would reasonably believe still
        // exists.
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

        var group = id with { Type = NetworkSecurityGroups.Type };

        NetworkSecurityGroups.SecurityGroupRef("ns", group).IsClusterScoped.ShouldBeTrue();

        // ⚠ HYPHENATED, AND IT IS THE ONE NAME IN THIS FAMILY A HAND-WRITTEN PLURAL WOULD GET WRONG.
        // ClusterConformanceHarness derives its CRD stub's path from GroupVersionKind.Plural, so
        // `securitygroups` would install a definition at a path the apply never reaches — and the
        // symptom is a discovery error naming a missing operator rather than a wrong plural.
        NetworkSecurityGroups.SecurityGroupKind.Plural.ShouldBe("security-groups");

        PublicIpAddresses.OvnEipRef("ns", "web").IsClusterScoped.ShouldBeTrue();

        // ⚠ THE SECOND HYPHENATED PLURAL IN THIS FAMILY, AND THE SECOND ONE A HAND-WRITTEN GUESS
        // WOULD GET WRONG. `+kubebuilder:resource:...path="ovn-eips",singular="ovn-eip"` — read
        // firsthand from pkg/apis/kubeovn/v1/ovn-eip.go and confirmed in the CRD Kube-OVN's own chart
        // installs. Two out of the four kinds this family renders hyphenate, so "Kube-OVN pluralises
        // by lower-casing the kind" is a rule that is wrong half the time.
        PublicIpAddresses.OvnEipKind.Plural.ShouldBe("ovn-eips");
        PublicIpAddresses.OvnEipKind.Kind.ShouldBe("OvnEip");
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

    static IEnumerable<string> ShortNames() => Build().Types.Select(x => x.Display.Alias);

    /// <summary>
    ///     What this provider puts into the <c>cyc</c> token namespace, read off the built registry.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>One provider is deliberately all this can see.</b> src/Providers/README.md § Hard rule
    ///     forbids a <c>Providers.*</c> assembly referencing another, so the cross-provider half of
    ///     the question is answered where the whole tree is visible — <c>ProviderRegistry.Build</c> at
    ///     silo start, <c>CliEmitter.Emit</c> at generation, and
    ///     <c>GeneratedSurfaceTests.NoGroupInAnyShippedTreeGivesOneTokenTwoMeanings</c> over the
    ///     embedded tree in <c>dotnet test</c>. None of the three is a list.
    /// </remarks>
    static IEnumerable<CliDeclaration> Declarations() =>
        Build().Types.Select(x => new CliDeclaration(x.Type.Namespace, x.Type.Type, x.Display.Alias));

    static ProviderRegistry Build() => ProviderRegistry.Build([new NetworkProvider()]);
}
