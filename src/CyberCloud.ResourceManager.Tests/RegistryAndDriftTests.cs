using CyberCloud.Kubernetes.Contracts;
using CyberCloud.ResourceManager.Drift;
using CyberCloud.ResourceManager.Registry;
using CyberCloud.ResourceManager.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Immutable;

namespace CyberCloud.ResourceManager.Tests;

/// <summary>
///     The provider registry: one object that both validates the request body and is shaped so the
///     four surfaces can be generated from it. docs/plan/08 § The provider registry.
/// </summary>
public sealed class ProviderRegistryTests {
    static ProviderRegistry Built => ProviderRegistry.Build([new TestingProvider()]);

    [Fact]
    public void TheRegistryThatValidatesIsTheOneAnEmitterWouldRead() {
        // ⚠ THE IDENTITY THAT MAKES DRIFT IMPOSSIBLE. docs/plan/08 § The provider registry: "the same
        // registry that generates the CLI is the one that validates the request body." This asserts
        // that everything an emitter needs is on the same object the write path resolves — the
        // versions, the schemas with their pointers, kinds and requiredness, the permissions, the
        // actions and the meters — and all four of ADR-012's emitters now read exactly this object.
        var resolved = Built.Resolve(ConformingReconciler.TypeName, TestingProvider.V2026);

        resolved.IsSuccess.ShouldBeTrue(resolved.Error?.Message);
        var registration = resolved.GetValueOrThrow().Registration;

        registration.ApiVersions.Length.ShouldBe(2);
        registration.ReadPermission.ShouldBe("read");
        registration.WritePermission.ShouldBe("write");
        registration.DeletePermission.ShouldBe("delete");
        // restart, listKeys and resize — the third declares a request and a response schema, which is
        // the expressiveness an action had none of.
        registration.Actions.Length.ShouldBe(3);
        registration.Meters.Length.ShouldBe(2);
        registration.SoftDeleteDays.ShouldBe(7);
        registration.SupportsTags.ShouldBeTrue();
        registration.ReconcilerType.ShouldBe(typeof(ConformingReconciler));

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
        Should.Throw<ArgumentException>(() => ProviderRegistry.Build([new DuplicateVersionProvider()]))
            .Message.ShouldContain("immutable");
    }

    [Fact]
    public void AProviderThatDeclaresNothingIsABuildFailure() {
        Should.Throw<InvalidOperationException>(() => ProviderRegistry.Build([new SilentProvider()]))
            .Message.ShouldContain("declared no resource types");
    }

    [Fact]
    public void ATypeWithNoApiVersionIsABuildFailure() {
        Should.Throw<InvalidOperationException>(() => ProviderRegistry.Build([new VersionlessProvider()]))
            .Message.ShouldContain("declares no api-version");
    }

    [Fact]
    public void TwoProvidersCannotShareANamespace() {
        Should.Throw<InvalidOperationException>(
                () => ProviderRegistry.Build([new TestingProvider(), new TestingProvider()])
            )
            .Message.ShouldContain("shadow");
    }

    [Fact]
    public void ATypeScopedCallBeforeAnyResourceTypeIsABug() {
        Should.Throw<InvalidOperationException>(() => ProviderRegistry.Build([new PrematureProvider()]))
            .Message.ShouldContain("before any ResourceType");
    }

    [Fact]
    public void QuotaMeterUnknownIsNotAMeter() {
        Should.Throw<ArgumentException>(() => ProviderRegistry.Build([new UnknownMeterProvider()]));
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
        new(
            resourceId,
            "/tenants/…/widgets/x",
            hash,
            new ObjectRef {
                Kind = new() { Group = "apps", Version = "v1", Kind = "Deployment", Plural = "deployments" },
                Namespace = "ns",
                Name = "x"
            }
        );

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
        DesiredHash.Of(body).ShouldNotBe(DesiredHash.Of(TestingProvider.Body(size: 3)));
    }
}

/// <summary>The seams that are stubbed, asserted to fail loudly rather than quietly.</summary>
public sealed class StubbedSeamTests {
    [Fact]
    public async Task TheDefaultPolicyEvaluatorSaysNoEngineRanRatherThanAllowing() {
        // ⚠ An Allow is indistinguishable from a policy engine that evaluated and permitted;
        // NotSupported says no engine ran, which is what an audit log has to be able to state.
        var decision = await new NotSupportedPolicyEvaluator().EvaluateAsync(
            ResourceManagerCluster.Address("x"),
            TestingProvider.V2026,
            "{}",
            ResourceManagerCluster.Caller(),
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

        public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string keyExtension, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithGuidCompoundKey =>
            throw new NotSupportedException();

        public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string keyExtension, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithIntegerCompoundKey =>
            throw new NotSupportedException();

        public IAddressable GetGrain(GrainId grainId) => throw new NotSupportedException();

        public IAddressable GetGrain(GrainId grainId, GrainInterfaceType interfaceType) => throw new NotSupportedException();

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
