using CyberCloud.ResourceManager.Actions;
using CyberCloud.ResourceManager.Registry;
using Microsoft.Extensions.DependencyInjection;
using CyberCloud.ResourceManager.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace CyberCloud.ResourceManager.Tests;

/// <summary>
///     Step 6 over a body whose amounts are Kubernetes quantities rather than numbers.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The gap.</b> <c>MeterRegistration</c> reserved the <i>number</i> at a JSON pointer, and
///         no managed service has one: a PostgreSQL server spells its vCPU <c>500m</c>, its memory
///         <c>2Gi</c> and its disk <c>20Gi</c>, and usually spells none of them because a sizing preset
///         names them indirectly. So the only meter a real provider could declare was
///         <see cref="QuotaMeter.Resources" /> — a count of one — and quota, which is built and
///         enforced, did not cover the two things a customer buys.
///     </para>
///     <para>
///         ⚠ <b><c>CyberCloud.Testing/sizedwidgets</c> is the fixture for it</b>, and it exists because
///         <c>widgets</c> meters <c>/properties/size</c>, a JSON number — the one body shape that does
///         not occur in the catalogue. It declares both halves of the seam: a lambda over two pointers
///         for <see cref="QuotaMeter.Vcpu" />, and the one-quantity-at-one-pointer form for
///         <see cref="QuotaMeter.StorageGb" />.
///     </para>
/// </remarks>
[Collection(ResourceManagerSuite.Name)]
public sealed class MeteredAmountTests(ResourceManagerCluster cluster) {
    /// <summary>
    ///     A create reserves the derived amount, and a delete gives back exactly it — on a fractional
    ///     number, on every meter.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The failure this pins is quota drifting up across deletes, and it has happened
    ///         here once.</b> Committed amounts were not re-derived on delete, so a subscription's
    ///         committed figure climbed by one resource's worth on every create/delete cycle and the
    ///         allowance never came back — silent, and worse the longer a subscription is used. The
    ///         repair made the delete path fill <see cref="OperationSpec.CommittedQuota" /> from the
    ///         resource's stored superset through the same <c>AmountFor</c> step 6 reserved with.
    ///         <c>DeletePathTests.ADeleteReturnsExactlyWhatTheCreateCommittedOnEveryMeter</c> holds
    ///         that for the pointed case; this holds it for the <b>derived</b> one, which is the case
    ///         that could break it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A derivation is provider code on the quota path, and the symmetry survives only
    ///         because it is a pure function of the body.</b> Both sides run the same lambda over the
    ///         same JSON — the request's on the create, the stored superset on the delete — so they
    ///         cannot disagree. A derivation that read a clock, a config value or a table that ships
    ///         separately from the body would reintroduce exactly this drift, which is why
    ///         <c>MeterDerivation</c> requires purity and declares a read set.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>3 × 500m is 1.5, and 1.5 is chosen deliberately.</b> A conversion that floored would
    ///         reserve 1 and hand out half a vCPU free; one that ceilinged would reserve 2 and overcharge.
    ///         The ledger is <see langword="decimal" /> end to end, so it is neither — and a delete that
    ///         returned a rounded figure would leave a permanent fractional residue on the meter, which
    ///         is the drift again in miniature.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task ADeleteReturnsExactlyWhatADerivedCreateCommittedIncludingTheFraction() {
        ResourceManagerCluster.ResetDoubles();
        var address = Sized("derived-round-trip");

        var quota = cluster.Quota(ResourceManagerCluster.Tenant, ResourceManagerCluster.Subscription);
        var vcpuBefore = await Committed(quota, QuotaMeter.Vcpu);
        var storageBefore = await Committed(quota, QuotaMeter.StorageGb);
        var countBefore = await Committed(quota, QuotaMeter.Resources);

        var created = await Create(address, TestingProvider.SizedBody(replicas: 3, cpu: "500m", disk: "20Gi"));
        created.IsSuccess.ShouldBeTrue(created.Error?.Message);
        await Converge(created.GetValueOrThrow());

        (await Committed(quota, QuotaMeter.Vcpu)).ShouldBe(
            vcpuBefore + 1.5m,
            "three instances at 500m each is one and a half cores, not one and not two"
        );

        (await Committed(quota, QuotaMeter.StorageGb)).ShouldBe(storageBefore + 20m, "20Gi is 20 GiB");
        (await Committed(quota, QuotaMeter.Resources)).ShouldBe(countBefore + 1);

        var deleted = await cluster.Manager.DeleteAsync(
            new() { Path = address.Path, ApiVersion = TestingProvider.V2026, Caller = ResourceManagerCluster.Caller() },
            TestContext.Current.CancellationToken
        );

        deleted.IsSuccess.ShouldBeTrue(deleted.Error?.Message);

        var operation = cluster.Operation(ResourceManagerCluster.Tenant, deleted.GetValueOrThrow().OperationId);
        (await operation.DriveAsync()).GetValueOrThrow().State.ShouldBe(OperationState.Succeeded);

        (await Committed(quota, QuotaMeter.Vcpu)).ShouldBe(
            vcpuBefore,
            "the delete re-derived 1.5 from the stored body through the same step the create used"
        );

        (await Committed(quota, QuotaMeter.StorageGb)).ShouldBe(storageBefore);
        (await Committed(quota, QuotaMeter.Resources)).ShouldBe(countBefore);

        // ⚠ NOT TWICE. The operation is re-drivable from a reminder and is already terminal.
        await operation.DriveAsync();

        (await Committed(quota, QuotaMeter.Vcpu)).ShouldBe(vcpuBefore);
        (await Committed(quota, QuotaMeter.StorageGb)).ShouldBe(storageBefore);
    }

    /// <summary>
    ///     Ten create/delete cycles leave every meter exactly where they found it.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Drift is a residue per cycle, and one cycle can hide it.</b> A rounding rule that lost
    ///     0.5 vCPU per delete looks like a rounding rule; ten of them look like a subscription that has
    ///     lost five cores it is entitled to and can never get back without a support ticket. Running
    ///     the loop is what turns "close enough" into a failing assertion.
    /// </remarks>
    [Fact]
    public async Task TenCreateDeleteCyclesLeaveTheMetersWhereTheyStarted() {
        ResourceManagerCluster.ResetDoubles();

        var quota = cluster.Quota(ResourceManagerCluster.Tenant, ResourceManagerCluster.Subscription);
        var vcpuBefore = await Committed(quota, QuotaMeter.Vcpu);
        var storageBefore = await Committed(quota, QuotaMeter.StorageGb);

        for (var cycle = 0; cycle < 10; cycle++) {
            var address = Sized($"cycle-{cycle}");

            var created = await Create(address, TestingProvider.SizedBody(replicas: 3, cpu: "333m", disk: "512Mi"));
            created.IsSuccess.ShouldBeTrue(created.Error?.Message);
            await Converge(created.GetValueOrThrow());

            var deleted = await cluster.Manager.DeleteAsync(
                new() {
                    Path = address.Path,
                    ApiVersion = TestingProvider.V2026,
                    Caller = ResourceManagerCluster.Caller()
                },
                TestContext.Current.CancellationToken
            );

            deleted.IsSuccess.ShouldBeTrue(deleted.Error?.Message);
            await cluster.Operation(ResourceManagerCluster.Tenant, deleted.GetValueOrThrow().OperationId).DriveAsync();
        }

        (await Committed(quota, QuotaMeter.Vcpu)).ShouldBe(vcpuBefore, "ten cycles of 0.999 cores each");
        (await Committed(quota, QuotaMeter.StorageGb)).ShouldBe(storageBefore, "ten cycles of 1.5 GiB each");
    }

    /// <summary>
    ///     A meter whose pointer does not resolve refuses the write instead of reserving something.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Zero is the dangerous default and one is no better.</b> Before this,
    ///         <c>MeterRegistration.Fallback</c> was a <see langword="decimal" /> that
    ///         <c>IResourceTypeBuilder.Meter</c> defaulted to <c>1</c>, and <c>AmountFor</c> returned it
    ///         whenever the pointer was absent, held the wrong kind, or held a non-positive number. So a
    ///         renamed property, a bumped api-version, or a schema and a meter that drifted apart all
    ///         produced the same outcome: quota passes, the resource provisions at whatever size it
    ///         likes, and the meter carries a number nobody chose. Nothing anywhere reports it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The body below is VALID.</b> <c>/properties/disk</c> is optional in
    ///         <c>TestingProvider.SizedSchema</c>, so step 5 accepts it and step 6 is where the meter
    ///         discovers it cannot say how much. That is the shape of the real failure — a body that
    ///         validates and a meter that cannot measure it — rather than a malformed request.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task AMeterThatCannotSayHowMuchRefusesTheWriteAndMovesNothing() {
        ResourceManagerCluster.ResetDoubles();
        var address = Sized("unmeterable");

        var quota = cluster.Quota(ResourceManagerCluster.Tenant, ResourceManagerCluster.Subscription);
        var vcpuBefore = await Committed(quota, QuotaMeter.Vcpu);
        var storageBefore = await Committed(quota, QuotaMeter.StorageGb);

        // ⚠ A DELTA, NOT AN ABSOLUTE. The suite shares one subscription and other classes hold live
        // leases of their own; asserting `Reserved == 0` here would be asserting something about them.
        var reservedBefore = (await quota.GetUsageAsync(QuotaMeter.Vcpu)).GetValueOrThrow().Reserved;

        var created = await Create(address, TestingProvider.SizedBody(replicas: 2, cpu: "500m", disk: null));

        created.IsFailure.ShouldBeTrue("a meter that cannot determine its amount must not let the write through");
        created.Error!.Code.ShouldBe(ErrorCode.InternalError);
        created.Error.Message.ShouldContain(TestingProvider.DiskPointer);

        // ⚠ And the vcpu meter, which DID resolve and was reserved first, is released rather than left
        // holding a subscription's capacity for the lease duration against a refused request.
        (await Committed(quota, QuotaMeter.Vcpu)).ShouldBe(vcpuBefore);
        (await quota.GetUsageAsync(QuotaMeter.Vcpu)).GetValueOrThrow()
            .Reserved.ShouldBe(
                reservedBefore,
                "the vcpu lease taken before the storage meter refused is given back, not left holding "
                + "a subscription's capacity for the lease duration against a request that was refused"
            );

        (await Committed(quota, QuotaMeter.StorageGb)).ShouldBe(storageBefore);

        // The name is free: nothing was claimed, because step 6 runs before step 7.
        var entry = await cluster.Index(address).GetAsync();
        entry.GetValueOrThrow().State.ShouldBe(IndexEntryState.Free);
    }

    /// <summary>
    ///     A provider's derivation that throws becomes the same refusal a returned failure is.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The seam puts provider code on the write path, and an escaping exception would land in
    ///     two bad places.</b> On a create it is a <c>500</c> with a stack trace where a stated error
    ///     belongs; on a <c>DELETE</c> it would escape <i>after</i> the index was released, leaving a
    ///     name freed and a resource that never tears down. Both become the refusal instead.
    /// </remarks>
    [Fact]
    public async Task ADerivationThatThrowsIsARefusalRatherThanAnEscapingException() {
        ResourceManagerCluster.ResetDoubles();

        // ⚠ Its own manager over its own registry, because a provider whose every write fails does not
        // belong in the shared fixture. The grain factory is the suite's, so the subscription and the
        // resource group step 1 reads are the real ones.
        var manager = new ResourceManagerService(
            ProviderRegistry.Build([new ThrowingProvider()]),
            new SwitchableAuthorizer(),
            new RecordingRelationWriter(),
            new SwitchableLockResolver(),
            new SwitchablePolicyEvaluator(),
            new RecordingChangeSink(),
            cluster.Grains,
            new ActionDispatcher(
                new ServiceCollection().BuildServiceProvider(),
                new NoClusterConnectionFactory(),
                new UnavailableSecretResolver()
            ),
            NullLogger<ResourceManagerService>.Instance
        );

        var address = new ResourceId(
            ResourceManagerCluster.Tenant,
            ResourceManagerCluster.Subscription,
            "prod",
            ThrowingProvider.TypeName,
            "explodes",
            Guid.Empty
        );

        var written = await manager.WriteAsync(
            new() {
                Path = address.Path,
                ApiVersion = "2026-08-01",
                Verb = WriteVerb.Put,
                Body = """{"location":"eu-central"}""",
                Caller = ResourceManagerCluster.Caller()
            },
            TestContext.Current.CancellationToken
        );

        written.IsFailure.ShouldBeTrue("a throwing derivation must not escape into the caller's face");
        written.Error!.Code.ShouldBe(ErrorCode.InternalError);
        written.Error.Message.ShouldContain("InvalidOperationException");
        written.Error.Message.ShouldContain("pure function of the body");
    }

    sealed class ThrowingProvider : IResourceProvider {
        public static ResourceTypeName TypeName { get; } = new("CyberCloud.Throwing", "things");

        public string ProviderNamespace => "CyberCloud.Throwing";

        public void Describe(IProviderBuilder builder) =>
            builder
                .ResourceType("things")
                .ApiVersion(
                    "2026-08-01",
                    ResourceSchema.Of([new("/location", SchemaKind.Text, Required: true)])
                )
                .Meter(
                    QuotaMeter.Vcpu,
                    MeterDerivation.Of(
                        "throws",
                        ["/properties/size"],
                        _ => throw new InvalidOperationException("a provider bug")
                    )
                );
    }

    static ResourceId Sized(string name) =>
        new(
            ResourceManagerCluster.Tenant,
            ResourceManagerCluster.Subscription,
            "prod",
            TestingProvider.SizedTypeName,
            name,
            Guid.Empty
        );

    static async Task<decimal> Committed(IQuotaGrain quota, QuotaMeter meter) =>
        (await quota.GetUsageAsync(meter)).GetValueOrThrow().Committed;

    Task<Result<WriteAccepted>> Create(ResourceId address, string body) =>
        cluster.Manager.WriteAsync(
            new() {
                Path = address.Path,
                ApiVersion = TestingProvider.V2026,
                Verb = WriteVerb.Put,
                Body = body,
                Caller = ResourceManagerCluster.Caller()
            },
            TestContext.Current.CancellationToken
        );

    async Task Converge(WriteAccepted accepted) {
        var operation = cluster.Operation(ResourceManagerCluster.Tenant, accepted.OperationId);

        for (var i = 0; i < 5; i++) {
            var status = await operation.DriveAsync();
            if (status.GetValueOrThrow().IsTerminal) {
                return;
            }
        }
    }
}
