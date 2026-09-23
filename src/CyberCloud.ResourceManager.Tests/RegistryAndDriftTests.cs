using CyberCloud.Kubernetes.Contracts;
using CyberCloud.ResourceManager.Drift;
using CyberCloud.ResourceManager.Reconcile;
using CyberCloud.ResourceManager.Registry;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Immutable;

namespace CyberCloud.ResourceManager.Tests;

/// <summary>
///     The provider registry: one object that both validates the request body and is shaped so the
///     five surfaces can be generated from it. docs/plan/08 § The provider registry.
/// </summary>
public sealed class ProviderRegistryTests {
    static ProviderRegistry Built => ProviderRegistry.Build([new TestingProvider()]);

    [Fact]
    public void TheRegistryThatValidatesIsTheOneAnEmitterWouldRead() {
        // ⚠ THE IDENTITY THAT MAKES DRIFT IMPOSSIBLE. docs/plan/08 § The provider registry: "the same
        // registry that generates the CLI is the one that validates the request body." This asserts
        // that everything an emitter needs is on the same object the write path resolves — the
        // versions, the schemas with their pointers, kinds and requiredness, the permissions, the
        // actions and the meters — and all five of ADR-012's emitters now read exactly this object.
        // ⚠ The fifth needs one more member than the other four: ResourceTypeRegistration.Chart is
        // what pairs a type with the values.yaml whose @param block is generated from its schema, and
        // it is the one pairing fact no emitted OpenAPI document carries.
        var resolved = Built.Resolve(ConformingReconciler.TypeName, TestingProvider.V2026);

        resolved.IsSuccess.ShouldBeTrue(resolved.Error?.Message);
        var registration = resolved.GetValueOrThrow().Registration;

        registration.ApiVersions.Length.ShouldBe(2);
        registration.ReadPermission.ShouldBe("read");
        registration.WritePermission.ShouldBe("write");
        registration.DeletePermission.ShouldBe("delete");
        // restart, listKeys, orphaned and resize. `resize` declares a request and a response schema,
        // which is the expressiveness an action had none of; `orphaned` declares no handler, which is
        // the shape every action in the catalogue had before one could be named at all.
        registration.Actions.Length.ShouldBe(4);

        // ⚠ THE HANDLER REACHES THE REGISTRY, WHICH IS WHAT ActionDispatcher RESOLVES FROM. A
        // declaration that carried a handler the registry dropped would be an action that refuses at
        // run time with a message about a missing handler the provider plainly named.
        registration.Actions
            .Single(static x => x.Name == "restart")
            .HandlerType
            .ShouldBe(typeof(RestartHandler));

        registration.Actions.Single(static x => x.Name == "orphaned").HandlerType.ShouldBeNull();
        registration.Meters.Length.ShouldBe(2);
        registration.SupportsTags.ShouldBeTrue();
        registration.ReconcilerType.ShouldBe(typeof(ConformingReconciler));

        // ⚠ THE RECOVERY WINDOW AND ITS THREE DEPENDENT FACTS ARE ON A DIFFERENT TYPE, AND THE PAIR OF
        // ASSERTIONS IS THE POINT. `widgets` declared SupportsSoftDelete(7) while nothing in the
        // manager read it; the manager reads it now, so a type that declares one is one whose DELETE
        // parks the resource rather than tearing it down — and the hard-delete half of the suite needs
        // a type that does not. Asserting BOTH sides here is what stops the window drifting back onto
        // `widgets` and quietly rewriting what every DeletePathTests case means.
        registration.SoftDeleteDays.ShouldBe(
            0,
            "widgets is the hard-delete fixture — docs/plan/08 § Soft delete makes a positive window "
            + "change what DELETE does"
        );

        registration.PurgePermission.ShouldBeEmpty("a type with no window has no purge to permit");

        var vault = Built.Resolve(TestingProvider.VaultTypeName, TestingProvider.V2026)
            .GetValueOrThrow()
            .Registration;

        vault.SoftDeleteDays.ShouldBe(7);

        // ⚠ Not "delete". docs/plan/08 § Soft delete keeps "may delete" and "may destroy permanently"
        // separable, following Azure's `deletedVaults/purge/action` sitting in Key Vault Contributor's
        // notActions; a purge permission that equalled the delete permission would be the separation
        // deleted rather than declared.
        vault.PurgePermission.ShouldBe("purge");
        vault.PurgePermission.ShouldNotBe(vault.DeletePermission);
        vault.PurgeProtectionPointer.ShouldBe(TestingProvider.PurgeProtectionPointer);

        // The schema an emitter would walk.
        var schema = resolved.GetValueOrThrow().Schema;
        schema.Properties.ShouldContain(x => x.JsonPointer == "/properties/size" && x.Required);
        schema.Properties.ShouldContain(x => x.JsonPointer == "/location" && x.Kind == SchemaKind.Text);
    }

    [Fact]
    public void VersionsAreKeptOldestFirstAndNewestIsTheLast() {
        var registration = Built.Resolve(ConformingReconciler.TypeName, TestingProvider.V2026)
            .GetValueOrThrow()
            .Registration;

        registration.ApiVersions[0].Version.Value.ShouldBe(TestingProvider.V2026);
        registration.Newest.Value.ShouldBe(TestingProvider.V2027);
    }

    [Fact]
    public void ATypeLookupIsCaseInsensitiveAsAzureIs() {
        Built.TryGetType(new("cybercloud.testing", "WIDGETS"), out var registration).ShouldBeTrue();
        registration.Type.ShouldBe(ConformingReconciler.TypeName);
    }

    [Fact]
    public void AnUnknownTypeNamesTheNamespacesTheRegistryDoesServe() {
        var resolved = Built.Resolve(new("CyberCloud.Nothing", "widgets"), TestingProvider.V2026);

        resolved.IsFailure.ShouldBeTrue();
        resolved.Error!.Code.ShouldBe(ErrorCode.InvalidResourceType);
        resolved.Error.Message.ShouldContain("CyberCloud.Testing");
    }

    [Fact]
    public void APublishedApiVersionCannotBeRedeclared() {
        // ⚠ "API versions are dates and they are immutable." A second declaration is either a
        // copy-paste or an attempt to change a published version, and the second is the thing the rule
        // forbids outright.
        Should.Throw<ArgumentException>(static () => ProviderRegistry.Build([new DuplicateVersionProvider()]))
            .Message.ShouldContain("immutable");
    }

    [Fact]
    public void AProviderMayNotDeclareTheReservedNamespace() {
        // ⚠ THE OTHER HALF OF "IsGroupScoped CANNOT BE FORGED". The platform stamps
        // `cybercloud.io/resource-type = cybercloud.resources_resourcegroups` on the one object it
        // writes on a resource group's behalf — the group's namespace — and DriftScanner's orphan
        // join and ProviderConformanceTests' labels assertion both read that label to mean "not
        // attributed to a resource, do not compare its id to a grain". A provider declaring this
        // namespace would render objects both of them then decline to check, which is an opt-out of
        // orphan detection and of the Labels architecture gate at once.
        //
        // ADR-013 already closes the other door: resource-type is one of the seven, injected by
        // KubeCommandBuilder from the resource's own type, and WithLabels throws on an attempt to set
        // it. A caller cannot write the label; this is what stops a caller owning a TYPE that
        // produces it.
        Should.Throw<InvalidOperationException>(static () => ProviderRegistry.Build([new ReservedNamespaceProvider()]))
            .Message.ShouldContain(KubeLabels.ReservedNamespace);
    }

    [Theory]
    [InlineData(RoleAssignmentId.ProviderNamespace)]
    [InlineData("cybercloud.authorization")]
    public void AProviderMayNotDeclareTheRoleAssignmentNamespace(string spelling) {
        // ⚠ THE SECOND RESERVATION, AND IT IS A ROUTING FACT RATHER THAN A LABELLING ONE. A role
        // assignment on a resource group is a well-formed ten-segment resource path, so the gateway
        // routes everything under /providers/CyberCloud.Authorization/ as an assignment BEFORE it
        // looks at the registry — RoleAssignmentId's remarks. A provider registered here would have
        // every one of its types shadowed by that route, and the symptom would be a 400 naming a role
        // assignment for a PUT that never mentioned one. Case-insensitive, as every structural
        // literal of a path is matched.
        Should.Throw<InvalidOperationException>(() => ProviderRegistry.Build([new NamespacedProvider(spelling)]))
            .Message.ShouldContain(RoleAssignmentId.ProviderNamespace);
    }

    [Theory]
    [InlineData(ResourceGraphAddress.ProviderNamespace)]
    [InlineData("cybercloud.resourcegraph")]
    public void AProviderMayNotDeclareTheResourceGraphNamespace(string spelling) {
        // ⚠ THE THIRD RESERVATION, FOR THE SECOND ROUTING REASON (#54). The resource graph is queried
        // at /tenants/{t}/providers/CyberCloud.ResourceGraph/resources, and the gateway routes
        // everything under that namespace to the query service BEFORE it looks at the registry —
        // ResourceGraphAddress's remarks. A provider registered here would have every one of its
        // types answered as "not the resource graph's address". Case-insensitive, as above.
        Should.Throw<InvalidOperationException>(() => ProviderRegistry.Build([new NamespacedProvider(spelling)]))
            .Message.ShouldContain(ResourceGraphAddress.ProviderNamespace);
    }

    [Theory]
    [InlineData(PolicyAddress.ProviderNamespace)]
    [InlineData("cybercloud.policy")]
    public void AProviderMayNotDeclareThePolicyNamespace(string spelling) {
        // ⚠ THE FOURTH RESERVATION (#46), FOR THE FIRST ROUTING REASON AGAIN. A policy assignment on a
        // resource group is a well-formed ten-segment resource path of type
        // CyberCloud.Policy/policyAssignments, and the gateway routes the whole namespace to the policy
        // manager BEFORE it looks at the registry — PolicyAddress's remarks. Case-insensitive, as above.
        Should.Throw<InvalidOperationException>(() => ProviderRegistry.Build([new NamespacedProvider(spelling)]))
            .Message.ShouldContain(PolicyAddress.ProviderNamespace);
    }

    [Fact]
    public void AProviderThatDeclaresNothingIsABuildFailure() {
        Should.Throw<InvalidOperationException>(static () => ProviderRegistry.Build([new SilentProvider()]))
            .Message.ShouldContain("declared no resource types");
    }

    [Fact]
    public void ATypeWithNoApiVersionIsABuildFailure() {
        Should.Throw<InvalidOperationException>(static () => ProviderRegistry.Build([new VersionlessProvider()]))
            .Message.ShouldContain("declares no api-version");
    }

    [Fact]
    public void TwoProvidersCannotShareANamespace() {
        Should.Throw<InvalidOperationException>(static () => ProviderRegistry.Build(
                [new TestingProvider(), new TestingProvider()]
            )
        )
            .Message.ShouldContain("shadow");
    }

    [Fact]
    public void ATypeScopedCallBeforeAnyResourceTypeIsABug() {
        Should.Throw<InvalidOperationException>(static () => ProviderRegistry.Build([new PrematureProvider()]))
            .Message.ShouldContain("before any ResourceType");
    }

    [Fact]
    public void QuotaMeterUnknownIsNotAMeter() {
        Should.Throw<ArgumentException>(static () => ProviderRegistry.Build([new UnknownMeterProvider()]));
    }

    sealed class ReservedNamespaceProvider : IResourceProvider {
        public string ProviderNamespace => KubeLabels.ReservedNamespace;

        public void Describe(IProviderBuilder builder) => builder.ResourceType("resourceGroups");
    }

    sealed class NamespacedProvider(string ns) : IResourceProvider {
        public string ProviderNamespace => ns;

        public void Describe(IProviderBuilder builder) =>
            builder.ResourceType("things").ApiVersion("2026-08-01", ResourceSchema.Empty);
    }

    sealed class SilentProvider : IResourceProvider {
        public string ProviderNamespace => "CyberCloud.Silent";

        public void Describe(IProviderBuilder builder) { }
    }

    sealed class VersionlessProvider : IResourceProvider {
        public string ProviderNamespace => "CyberCloud.Versionless";

        public void Describe(IProviderBuilder builder) => builder.ResourceType("things");
    }

    sealed class DuplicateVersionProvider : IResourceProvider {
        public string ProviderNamespace => "CyberCloud.Duplicated";

        public void Describe(IProviderBuilder builder) =>
            builder
                .ResourceType("things")
                .ApiVersion("2026-08-01", ResourceSchema.Empty)
                .ApiVersion("2026-08-01", ResourceSchema.Empty);
    }

    sealed class PrematureProvider : IResourceProvider {
        public string ProviderNamespace => "CyberCloud.Premature";

        public void Describe(IProviderBuilder builder) =>
            ((IResourceTypeBuilder)builder).ApiVersion("2026-08-01", ResourceSchema.Empty);
    }

    sealed class UnknownMeterProvider : IResourceProvider {
        public string ProviderNamespace => "CyberCloud.Unmetered";

        public void Describe(IProviderBuilder builder) =>
            builder
                .ResourceType("things")
                .ApiVersion("2026-08-01", ResourceSchema.Empty)
                .Meters(QuotaMeter.Unknown);
    }
}

/// <summary>
///     The per-cluster drift diff: orphans, strays and divergence. docs/plan/08 § The reconcile loop.
/// </summary>
/// <remarks>
///     ⚠ <b>The diff is real and the inventory is not.</b> The live informer view of a real API server
///     is <see cref="IClusterObjectInventory" />, and the shipped implementation refuses rather than
///     reporting an empty cluster. These tests supply the inventory directly, which exercises
///     everything the scan decides and nothing about how it would see a cluster.
/// </remarks>
public sealed class DriftScannerTests {
    static DriftScanner Scanner => new(TestClock.Instance);

    static Guid ClusterId { get; } = Guid.Parse("44444444-4444-4444-8444-444444444444");

    static ClusterObjectRecord Object(Guid resourceId, string hash) =>
        Object(resourceId, hash, "cybercloud.testing_widgets");

    static ClusterObjectRecord Object(Guid resourceId, string hash, string resourceType) =>
        new() {
            ResourceId = resourceId,
            ResourcePath = "/tenants/…/widgets/x",
            ReconcileHash = hash,
            ResourceType = resourceType,
            Target = new() {
                Kind = new() { Group = "apps", Version = "v1", Kind = "Deployment", Plural = "deployments" },
                Namespace = "ns",
                Name = "x"
            }
        };

    [Fact]
    public void AResourceGroupsOwnNamespaceIsNotAnOrphan() {
        // ⚠ THE OBJECT NO RESOURCE GRAIN WILL EVER OWN, AND IT IS NOT A FINDING. NamespaceEnsurer
        // writes the resource group's namespace with a resource-id DERIVED FROM THE GROUP, because
        // stamping whichever resource happened to create it would orphan the namespace the moment
        // that one resource was deleted — while every other resource in the group is still living in
        // it. So the derived id matches nothing in `expected` by construction, and a scan that joined
        // on it blindly would report one permanent orphan per resource group, forever, about the one
        // object on the cluster the platform put there on purpose.
        var namespaceObject = Object(
            NamespaceEnsurer.IdFor(Guid.NewGuid(), "prod"),
            "sha256:abc",
            KubeLabels.ResourceGroupTypeValue
        );

        var report = Scanner.Scan(ClusterId, [namespaceObject], []);

        report.Findings.ShouldBeEmpty(
            "the resource group's own namespace was reported as drift. It is attributed to the group "
            + "rather than to a resource — KubeLabels.IsGroupScoped — and the orphan join must skip it."
        );
    }

    [Fact]
    public void AnObjectThatOnlyLOOKSGroupScopedIsStillJoined() {
        // The other half, so the skip above is a rule about the label rather than a hole. A real
        // object whose resource-type is anything else is joined exactly as before, so a reconciler
        // cannot reach the skip by getting its resource-id wrong.
        var report = Scanner.Scan(
            ClusterId,
            [Object(Guid.NewGuid(), "sha256:abc", KubeLabels.ResourceGroupTypeValue + "x")],
            []
        );

        report.Orphans.Count().ShouldBe(1);
    }

    [Fact]
    public void ALabelledObjectWithNoResourceGrainIsAnOrphan() {
        // ⚠ "deleted and billed for" — nobody is metering it and it keeps running.
        var ghost = Guid.NewGuid();
        var report = Scanner.Scan(ClusterId, [Object(ghost, "sha256:abc")], []);

        report.Orphans.Count().ShouldBe(1);
        report.Orphans.Single().ResourceId.ShouldBe(ghost);
        report.Orphans.Single().Detail.ShouldContain("nothing is metering");
    }

    [Fact]
    public void AResourceWithNoObjectsIsAStray() {
        // ⚠ "someone kubectl deleted production".
        var resourceId = Guid.NewGuid();
        var report = Scanner.Scan(
            ClusterId,
            [],
            [new(resourceId, "/tenants/…/widgets/x", "sha256:abc", ProvisioningState.Succeeded)]
        );

        report.Strays.Count().ShouldBe(1);
        report.Strays.Single().ResourceId.ShouldBe(resourceId);
    }

    [Fact]
    public void AResourceThatIsStillCreatingIsNotAStray() {
        // ⚠ Otherwise the scan's findings would be mostly its own platform's normal operation, which is
        // a scan nobody reads.
        var report = Scanner.Scan(
            ClusterId,
            [],
            [
                new(Guid.NewGuid(), "/a", "sha256:abc", ProvisioningState.Creating),
                new(Guid.NewGuid(), "/b", "sha256:abc", ProvisioningState.Deleting)
            ]
        );

        report.Findings.ShouldBeEmpty();
    }

    [Fact]
    public void AMatchingHashIsNotAFinding() {
        var resourceId = Guid.NewGuid();
        var report = Scanner.Scan(
            ClusterId,
            [Object(resourceId, "sha256:abc")],
            [new(resourceId, "/a", "sha256:abc", ProvisioningState.Succeeded)]
        );

        report.Findings.ShouldBeEmpty();
        report.ObjectsSeen.ShouldBe(1);
        report.ResourcesSeen.ShouldBe(1);
    }

    [Fact]
    public void AChangedHashIsDivergence() {
        var resourceId = Guid.NewGuid();
        var report = Scanner.Scan(
            ClusterId,
            [Object(resourceId, "sha256:changed")],
            [new(resourceId, "/a", "sha256:abc", ProvisioningState.Succeeded)]
        );

        report.Findings.Length.ShouldBe(1);
        report.Findings[0].Kind.ShouldBe(DriftKind.Diverged);
        report.Findings[0].Objects.Length.ShouldBe(1);
    }

    // ── A second writer on an object — issue #89 ────────────────────────────────────────────────

    static readonly Guid Vpc = Guid.Parse("3a8f0c22-5e6d-4a7b-8c9d-0e1f2a3b4c5d");
    static readonly Guid PeeringA = Guid.Parse("aaaaaaaa-0000-4000-8000-00000000000a");

    static readonly Guid SpokeVpc = Guid.Parse("4b9a1d33-6f7e-4b8c-9dae-1f2a3b4c5d6e");

    /// <summary>The owner's object with <paramref name="writer" />'s fragment on it.</summary>
    static ClusterObjectRecord CoWritten(Guid writer, string fragmentHash) =>
        Object(Vpc, "sha256:owner") with {
            Fragments = [new(writer, fragmentHash, "/tenants/…/virtualNetworks/hub/peerings/to-spoke")]
        };

    /// <summary>The other network's object — the remote side — with <paramref name="writer" />'s mirror-image fragment on it.</summary>
    static ClusterObjectRecord CoWrittenRemote(Guid writer, string fragmentHash) =>
        Object(SpokeVpc, "sha256:spoke-owner") with {
            Target = new() {
                Kind = new() { Group = "apps", Version = "v1", Kind = "Deployment", Plural = "deployments" },
                Namespace = "ns",
                Name = "spoke"
            },
            Fragments = [new(writer, fragmentHash, "/tenants/…/virtualNetworks/hub/peerings/to-spoke")]
        };

    /// <summary>The peering as the manager expects it: one fragment per object, each with its own hash.</summary>
    static ExpectedResource Peering(params (ClusterObjectRecord On, string Hash)[] fragments) =>
        new(
            PeeringA,
            "/tenants/…/virtualNetworks/hub/peerings/to-spoke",
            "sha256:peering-body",
            ProvisioningState.Succeeded,
            [.. fragments.Select(static x => new ExpectedFragment(x.On.Target, x.Hash))]
        );

    /// <summary>Both networks, converged, so that their objects are nobody's orphans.</summary>
    static ImmutableArray<ExpectedResource> BothOwners { get; } = [
        new(Vpc, "/tenants/…/virtualNetworks/hub", "sha256:owner", ProvisioningState.Succeeded),
        new(SpokeVpc, "/tenants/…/virtualNetworks/spoke", "sha256:spoke-owner", ProvisioningState.Succeeded)
    ];

    [Fact]
    public void AConvergedCoWriterIsNotAStrayThoughItOwnsNoObject() {
        // ⚠ THE FINDING THE #89 REVIEW MADE. A peering owns no object carrying its resource-id
        // label — its slice rides on the parent's Vpc under a fragment annotation — so a scan that
        // joined on the label alone reported every Succeeded peering as a permanent stray, "its
        // objects were deleted outside the platform", about a slice that was right there.
        var local = CoWritten(PeeringA, "sha256:fragment-a");

        var report = Scanner.Scan(
            ClusterId,
            [local],
            [
                new(Vpc, "/tenants/…/virtualNetworks/hub", "sha256:owner", ProvisioningState.Succeeded),
                Peering((local, "sha256:fragment-a"))
            ]
        );

        report.Findings.ShouldBeEmpty("the owner's hash matches the object's and the peering's matches its fragment's");
    }

    [Fact]
    public void ACoWriterWhoseFragmentIsGoneIsAStray() {
        // The owner's object is there and carries no fragment of the peering's: somebody removed
        // the annotation, or another co-writer's apply pruned it — the slice is gone either way.
        var report = Scanner.Scan(
            ClusterId,
            [Object(Vpc, "sha256:owner")],
            [
                new(Vpc, "/hub", "sha256:owner", ProvisioningState.Succeeded),
                Peering((CoWritten(PeeringA, "sha256:fragment-a"), "sha256:fragment-a"))
            ]
        );

        report.Strays.Count().ShouldBe(1);
        report.Strays.Single().ResourceId.ShouldBe(PeeringA);
        report.Strays.Single().Detail.ShouldContain("fragment");
    }

    [Fact]
    public void ACoWriterIsJudgedByItsFragmentHashAndNotByTheOwners() {
        // ⚠ The Vpc's reconcile-hash is the owner's, over the owner's body. Comparing the peering
        // against it would report every peering as diverged the moment its parent re-rendered.
        var local = CoWritten(PeeringA, "sha256:fragment-a");

        var matches = Scanner.Scan(ClusterId, [local], [Peering((local, "sha256:fragment-a"))]);

        matches.Diverged.ShouldBeEmpty();

        var changed = Scanner.Scan(
            ClusterId,
            [CoWritten(PeeringA, "sha256:fragment-a-as-applied")],
            [Peering((local, "sha256:fragment-a-desired"))]
        );

        var diverged = changed.Diverged.ShouldHaveSingleItem();
        diverged.ResourceId.ShouldBe(PeeringA);
        diverged.Objects.Length.ShouldBe(1);
        diverged.Detail.ShouldContain("fragment-hash");
    }

    [Fact]
    public void ACoWriterOfTwoObjectsIsJudgedPerObjectBecauseItsTwoFragmentsHashDifferently() {
        // ⚠ THE #31 REVIEW'S FINDING. A peering writes mirror images onto two Vpcs — each names the
        // OTHER network, each route points at the other end of the link — so the two fragments
        // never hash the same, and one desired hash held against both reported every converged
        // peering as diverged on every scan ("1 of 2 objects carry … other than …"). The expected
        // shape is now one hash per object.
        var local = CoWritten(PeeringA, "sha256:local-fragment");
        var remote = CoWrittenRemote(PeeringA, "sha256:remote-fragment");

        var converged = Scanner.Scan(
            ClusterId,
            [local, remote],
            [.. BothOwners, Peering((local, "sha256:local-fragment"), (remote, "sha256:remote-fragment"))]
        );

        converged.Findings.ShouldBeEmpty(
            "two fragments with two hashes on two objects, each the one expected there: " + converged
        );

        // And the hashes are held against the OBJECT, not merely against the set: the remote's
        // hash on the local object is the wrong slice on the wrong router.
        var swapped = Scanner.Scan(
            ClusterId,
            [CoWritten(PeeringA, "sha256:remote-fragment"), CoWrittenRemote(PeeringA, "sha256:local-fragment")],
            [.. BothOwners, Peering((local, "sha256:local-fragment"), (remote, "sha256:remote-fragment"))]
        );

        swapped.Diverged.ShouldHaveSingleItem().Objects.Length.ShouldBe(2);
    }

    [Fact]
    public void ACoWriterWhoseSliceIsGoneFromOneOfItsObjectsIsDivergedNamingThatObject() {
        // The remote network's Vpc lost the peering's fragment — a hand edit, or the owner's
        // re-create — while the local one kept it. Not a stray: a slice is still there. Diverged,
        // naming the object the slice went missing from.
        var local = CoWritten(PeeringA, "sha256:local-fragment");
        var remote = CoWrittenRemote(PeeringA, "sha256:remote-fragment");

        var report = Scanner.Scan(
            ClusterId,
            [local, Object(SpokeVpc, "sha256:spoke-owner") with { Target = remote.Target }],
            [.. BothOwners, Peering((local, "sha256:local-fragment"), (remote, "sha256:remote-fragment"))]
        );

        report.Strays.ShouldBeEmpty();

        var diverged = report.Diverged.ShouldHaveSingleItem();
        diverged.Objects.ShouldBe([remote.Target]);
        diverged.Detail.ShouldContain("went missing");
    }

    [Fact]
    public void AFragmentLeftOnAnObjectTheWriterNoLongerPlacesItOnIsDivergedNotOrphaned() {
        // ⚠ The changed-remote case, charts/managed/kube-ovn-vpc-peering/conformance.yaml § owed,
        // `a-changed-remote-leaves-a-fragment-behind`: a PUT naming a different remote applies onto
        // the new remote's Vpc and never withdraws from the old one. The grain still exists, so the
        // orphan pass — which skips every known writer — never sees it; before this test the owed
        // row claimed it would. It is a Diverged finding naming the old object, and this is the
        // only place the leftover is ever named.
        var local = CoWritten(PeeringA, "sha256:local-fragment");
        var newRemote = CoWrittenRemote(PeeringA, "sha256:remote-fragment");
        var oldRemote = CoWrittenRemote(PeeringA, "sha256:old-remote-fragment") with {
            Target = newRemote.Target with { Name = "old-spoke" }
        };

        var report = Scanner.Scan(
            ClusterId,
            [local, newRemote, oldRemote],
            [.. BothOwners, Peering((local, "sha256:local-fragment"), (newRemote, "sha256:remote-fragment"))]
        );

        report.Orphans.ShouldBeEmpty("the peering's grain exists; the orphan pass skips every known writer");

        var diverged = report.Diverged.ShouldHaveSingleItem();
        diverged.ResourceId.ShouldBe(PeeringA);
        diverged.Objects.ShouldBe([oldRemote.Target]);
        diverged.Detail.ShouldContain("left behind");
    }

    [Fact]
    public void AFragmentWhoseWriterHasNoGrainIsAnOrphanNamingTheSliceAndNotTheObject() {
        // ⚠ The fragment nothing else ever finds: a co-writer that vanished without withdrawing
        // leaves a slice every other co-writer's apply carries forward verbatim, forever. The
        // object is the owner's and is not orphaned; the finding names the writer and the path
        // its fragment-path annotation recorded, which is where a person starts.
        var report = Scanner.Scan(
            ClusterId,
            [CoWritten(PeeringA, "sha256:fragment-a")],
            [new(Vpc, "/hub", "sha256:owner", ProvisioningState.Succeeded)]
        );

        var orphan = report.Orphans.ShouldHaveSingleItem();
        orphan.ResourceId.ShouldBe(PeeringA);
        orphan.ResourcePath.ShouldBe("/tenants/…/virtualNetworks/hub/peerings/to-spoke");
        orphan.Objects.Length.ShouldBe(1);
        orphan.Detail.ShouldContain("no co-writer will ever withdraw");

        report.Findings.Where(static x => x.ResourceId == Vpc)
            .ShouldBeEmpty("the owner is converged and its object is its own");
    }

    [Fact]
    public async Task TheShippedInventoryRefusesRatherThanReportingAnEmptyCluster() {
        // ⚠ THE SAFETY PROPERTY OF THE STUB. An empty inventory says every resource on this cluster is
        // a stray — that somebody deleted all of production — and a scan that believed it would
        // re-apply an entire cluster's worth of objects. A failure says "do not conclude anything".
        var inventory = new UnavailableClusterObjectInventory();

        var result = await inventory.ListManagedAsync(ClusterId, TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Message.ShouldContain("fails rather than reporting an empty cluster");
    }

    [Fact]
    public void TheHashIsOneFunctionSoTheTwoUsesCannotDisagree() {
        // docs/plan/09 § The command builder stamps `cybercloud.io/reconcile-hash`, and
        // docs/plan/08 § The resource-graph projection carries `desired_hash`. The drift scan compares
        // them; two implementations would report everything as diverged forever.
        var body = TestingProvider.Body();

        DesiredHash.Of(body).ShouldStartWith("sha256:");
        DesiredHash.Of(body).ShouldBe(DesiredHash.Of(body));
        DesiredHash.Of(body).ShouldNotBe(DesiredHash.Of(TestingProvider.Body(3)));
    }
}

/// <summary>The seams that are stubbed, asserted to fail loudly rather than quietly.</summary>
public sealed class StubbedSeamTests {
    [Fact]
    public async Task TheStubPolicyEvaluatorSaysNoEngineRanRatherThanAllowing() {
        // ⚠ An Allow is indistinguishable from a policy engine that evaluated and permitted;
        // NotSupported says no engine ran, which is what an audit log has to be able to state. It is
        // no longer what a host gets — CatalogPolicyEvaluator is, since #46 — and stays for the
        // hand-built harnesses whose subject is not policy.
        var decision = await new NotSupportedPolicyEvaluator().EvaluateAsync(
            new() { Id = ResourceManagerCluster.Address("x"), Operation = "create", Caller = ResourceManagerCluster.Caller() },
            TestContext.Current.CancellationToken
        );

        decision.Effect.ShouldBe(PolicyEffect.NotSupported);
        decision.Effect.ShouldNotBe(PolicyEffect.Allow);
        decision.Permits.ShouldBeTrue("the write path carries on");
    }

    [Fact]
    public async Task TheDefaultSecretResolverRefusesRatherThanReturningEmpty() {
        // An empty password in a rendered manifest is a database with no password, reported as a
        // successful provision.
        var resolved = await new UnavailableSecretResolver().ResolveAsync(
            new() { Path = "tenants/x/postgres/main", Field = "adminPassword" },
            TestContext.Current.CancellationToken
        );

        resolved.IsFailure.ShouldBeTrue();
        resolved.Error!.Message.ShouldContain("CyberCloud.Vault");
    }

    [Fact]
    public void TheDefaultClusterFactoryReturnsNullWhichIsALegitimateAnswer() {
        // docs/plan/08 § What the resource manager deliberately does not do: the manager "must work for
        // a provider with no cluster at all (a DNS zone, a mail domain, a role assignment)".
        new NoClusterConnectionFactory().Connect(Guid.NewGuid()).ShouldBeNull();
        new NoClusterConnectionFactory().Connect(Guid.Empty).ShouldBeNull();
    }

    [Fact]
    public void TheStrongestOfTwoLocksIsNotTheirNumericMaximum() {
        // ⚠ THE TRAP IN THE INHERITED LOCK, AND IT IS A ONE-CHARACTER TRAP. The enum reads
        // None = 0, ReadOnly = 1, CanNotDelete = 2 — so `Math.Max` or `>` would rank CanNotDelete
        // above ReadOnly, and a resource carrying CanNotDelete inside a subscription locked ReadOnly
        // would resolve to CanNotDelete and let the write through. That is the exact incident
        // docs/plan/06 § Tags, locks says a lock exists to prevent, arrived at by an operator who set
        // the STRONGER lock at the HIGHER scope, which is the sensible thing to do.
        LockLevels.Strongest(LockLevel.CanNotDelete, LockLevel.ReadOnly).ShouldBe(LockLevel.ReadOnly);
        LockLevels.Strongest(LockLevel.ReadOnly, LockLevel.CanNotDelete).ShouldBe(LockLevel.ReadOnly);
        LockLevels.Strongest(LockLevel.None, LockLevel.CanNotDelete).ShouldBe(LockLevel.CanNotDelete);
        LockLevels.Strongest(LockLevel.CanNotDelete, LockLevel.None).ShouldBe(LockLevel.CanNotDelete);
        LockLevels.Strongest(LockLevel.None, LockLevel.None).ShouldBe(LockLevel.None);

        // And the numbers really are the wrong way round, so the test above is not vacuous.
        ((int)LockLevel.ReadOnly).ShouldBeLessThan((int)LockLevel.CanNotDelete);
    }

    [Fact]
    public async Task TheShippedRelationWriterRefusesAnAddressWithNoIdentity() {
        // ⚠ A resource that has not been assigned a GUID has no ReBAC object, and answering "success"
        // would silently skip the parent edge — which is the failure mode the whole step exists to
        // close, arrived at from the other side. docs/plan/06 § Identifiers: a parsed path yields
        // Guid.Empty.
        var writer = new ReBacResourceRelationWriter(
            NullGrainFactory.Instance,
            NullLogger<ReBacResourceRelationWriter>.Instance
        );

        var refused = await writer.LinkToParentAsync(
            ResourceManagerCluster.Address("no-identity"),
            Guid.Empty,
            TestContext.Current.CancellationToken
        );

        refused.IsFailure.ShouldBeTrue("an address with no GUID was linked to a parent");
        refused.Error!.Code.ShouldBe(ErrorCode.InvalidResourceId);
    }

    /// <summary>An <see cref="IGrainFactory" /> that is never reached, for the id-less lock path.</summary>
    sealed class NullGrainFactory : IGrainFactory {
        public static NullGrainFactory Instance { get; } = new();

        public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithGuidKey =>
            throw new NotSupportedException();

        public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithIntegerKey =>
            throw new NotSupportedException();

        public TGrainInterface GetGrain<TGrainInterface>(string primaryKey, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithStringKey =>
            throw new NotSupportedException();

        public TGrainInterface GetGrain<TGrainInterface>(
            Guid primaryKey,
            string keyExtension,
            string? grainClassNamePrefix = null
        )
            where TGrainInterface : IGrainWithGuidCompoundKey =>
            throw new NotSupportedException();

        public TGrainInterface GetGrain<TGrainInterface>(
            long primaryKey,
            string keyExtension,
            string? grainClassNamePrefix = null
        )
            where TGrainInterface : IGrainWithIntegerCompoundKey =>
            throw new NotSupportedException();

        public IAddressable GetGrain(GrainId grainId) => throw new NotSupportedException();

        public IAddressable GetGrain(GrainId grainId, GrainInterfaceType interfaceType) =>
            throw new NotSupportedException();

        public TGrainObserverInterface CreateObjectReference<TGrainObserverInterface>(IGrainObserver obj)
            where TGrainObserverInterface : IGrainObserver =>
            throw new NotSupportedException();

        public void DeleteObjectReference<TGrainObserverInterface>(IGrainObserver obj)
            where TGrainObserverInterface : IGrainObserver =>
            throw new NotSupportedException();

        public TGrainInterface GetGrain<TGrainInterface>(GrainId grainId)
            where TGrainInterface : IAddressable =>
            throw new NotSupportedException();

        public IGrain GetGrain(Type grainInterfaceType, Guid grainPrimaryKey) => throw new NotSupportedException();

        public IGrain GetGrain(Type grainInterfaceType, long grainPrimaryKey) => throw new NotSupportedException();

        public IGrain GetGrain(Type grainInterfaceType, string grainPrimaryKey) => throw new NotSupportedException();

        public IGrain GetGrain(Type grainInterfaceType, Guid grainPrimaryKey, string keyExtension) =>
            throw new NotSupportedException();

        public IGrain GetGrain(Type grainInterfaceType, long grainPrimaryKey, string keyExtension) =>
            throw new NotSupportedException();

        public IAddressable GetGrain(Type grainInterfaceType, IdSpan grainKey) => throw new NotSupportedException();

        public IAddressable GetGrain(Type grainInterfaceType, IdSpan grainKey, string grainClassNamePrefix) =>
            throw new NotSupportedException();
    }
}
