using CyberCloud.Tenancy.Contracts;
using CyberCloud.Tenancy.Tests.Infrastructure;
using Shouldly;

namespace CyberCloud.Tenancy.Tests;

/// <summary>
///     docs/plan/06 § Quota: <b>"Reservation, not a counter increment — the lease is released if the
///     operation fails, and expires on its own if the operation grain dies."</b>
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The distinction between a reservation and a counter is the whole point of these
///         tests, and it is testable in exactly two places</b>: an operation that fails must give the
///         quota back, and an operation that is abandoned must lose it anyway. A counter has neither
///         property — it has no owner to release it and no expiry to reclaim it, so the only repair
///         for a leaked increment is a manual reconciliation nobody will run.
///     </para>
///     <para>
///         The serialisation property — "the quota grain is per-subscription and therefore serialises
///         every create in that subscription. That is correct" — is asserted directly rather than
///         asserted away.
///     </para>
/// </remarks>
[Collection(TenancySuite.Name)]
public sealed class QuotaGrainTests(TenancyCluster cluster)
{
    static Guid Tenant(int n) => TenancyCluster.Tenant(5000 + n);

    [Fact]
    public async Task AReservationIsNotCommittedUsageUntilItIsCommitted()
    {
        var (quota, _) = Quota(1);

        (await quota.SetLimitAsync(QuotaMeter.Vcpu, 10m)).IsSuccess.ShouldBeTrue();

        var lease = (await quota.TryReserveAsync(QuotaMeter.Vcpu, 4m, Guid.NewGuid()))
            .GetValueOrThrow();

        var usage = (await quota.GetUsageAsync(QuotaMeter.Vcpu)).GetValueOrThrow();

        usage.Reserved.ShouldBe(4m);
        usage.Committed.ShouldBe(0m, "a reservation is not usage — the resource does not exist yet.");

        (await quota.CommitAsync(lease.LeaseId)).IsSuccess.ShouldBeTrue();

        var after = (await quota.GetUsageAsync(QuotaMeter.Vcpu)).GetValueOrThrow();
        after.Reserved.ShouldBe(0m);
        after.Committed.ShouldBe(4m);
    }

    [Fact]
    public async Task AReservationCountsAgainstTheLimitWhileItIsHeld()
    {
        // Otherwise two concurrent creates would both pass the check and the subscription would end
        // up over its limit, with no single moment at which anything was wrong.
        var (quota, _) = Quota(2);

        (await quota.SetLimitAsync(QuotaMeter.Vcpu, 10m)).IsSuccess.ShouldBeTrue();
        (await quota.TryReserveAsync(QuotaMeter.Vcpu, 8m, Guid.NewGuid())).IsSuccess.ShouldBeTrue();

        var second = await quota.TryReserveAsync(QuotaMeter.Vcpu, 4m, Guid.NewGuid());

        second.IsFailure.ShouldBeTrue();
        second.Error!.Code.ShouldBe(ErrorCode.QuotaExceeded);
        second.Error.Message.ShouldContain("reserved");
    }

    [Fact]
    public async Task AFailedOperationReleasesItsLeaseAndTheQuotaIsAvailableImmediately()
    {
        // ⚠ HALF ONE OF THE RESERVATION PROPERTY: "the lease is released if the operation fails".
        var (quota, _) = Quota(3);

        (await quota.SetLimitAsync(QuotaMeter.Vcpu, 10m)).IsSuccess.ShouldBeTrue();

        var lease = (await quota.TryReserveAsync(QuotaMeter.Vcpu, 8m, Guid.NewGuid()))
            .GetValueOrThrow();

        (await quota.TryReserveAsync(QuotaMeter.Vcpu, 4m, Guid.NewGuid())).IsFailure.ShouldBeTrue();

        // The operation fails and releases.
        (await quota.ReleaseAsync(lease.LeaseId)).IsSuccess.ShouldBeTrue();

        var retried = await quota.TryReserveAsync(QuotaMeter.Vcpu, 4m, Guid.NewGuid());
        retried.IsSuccess.ShouldBeTrue("the released quota is available at once, not after a sweep.");

        (await quota.GetUsageAsync(QuotaMeter.Vcpu)).GetValueOrThrow().Committed.ShouldBe(
            0m, "a released lease was never usage.");
    }

    [Fact]
    public async Task AnAbandonedLeaseExpiresOnItsOwnEvenAcrossADeactivation()
    {
        // ⚠ HALF TWO, AND THE ONE A COUNTER CANNOT HAVE: "expires on its own if the operation grain
        // dies". The quota grain is deactivated for the whole lease, so nothing is running to
        // notice — and the quota still comes back, because expiry is evaluated on read.
        var (quota, key) = Quota(4);

        (await quota.SetLimitAsync(QuotaMeter.Vcpu, 10m)).IsSuccess.ShouldBeTrue();
        (await quota.TryReserveAsync(QuotaMeter.Vcpu, 9m, Guid.NewGuid())).IsSuccess.ShouldBeTrue();
        (await quota.TryReserveAsync(QuotaMeter.Vcpu, 4m, Guid.NewGuid())).IsFailure.ShouldBeTrue();

        // The operation grain dies, and so does the quota grain. Nobody releases anything.
        await quota.DeactivateAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken);

        cluster.Clock.Advance(TimeSpan.FromMinutes(61));

        var revived = Quota(key);

        (await revived.ListLeasesAsync()).GetValueOrThrow().ShouldBeEmpty(
            "an expired lease is swept the moment the grain is asked anything.");

        var afterExpiry = await revived.TryReserveAsync(QuotaMeter.Vcpu, 9m, Guid.NewGuid());
        afterExpiry.IsSuccess.ShouldBeTrue(
            "the abandoned lease's quota came back with no timer, no reminder and no operator.");
    }

    [Fact]
    public async Task AnExpiredLeaseCannotBeCommitted()
    {
        // The race the expiry creates, and the safe answer: the slow operation must not commit
        // quota that has already been handed to somebody else.
        var (quota, key) = Quota(5);

        (await quota.SetLimitAsync(QuotaMeter.Vcpu, 10m)).IsSuccess.ShouldBeTrue();
        var lease = (await quota.TryReserveAsync(QuotaMeter.Vcpu, 9m, Guid.NewGuid()))
            .GetValueOrThrow();

        cluster.Clock.Advance(TimeSpan.FromMinutes(61));

        var late = await Quota(key).CommitAsync(lease.LeaseId);

        late.IsFailure.ShouldBeTrue();
        late.Error!.Code.ShouldBe(ErrorCode.Conflict);
        (await Quota(key).GetUsageAsync(QuotaMeter.Vcpu)).GetValueOrThrow().Committed.ShouldBe(0m);
    }

    [Fact]
    public async Task ALeaseCannotBeCommittedTwice()
    {
        var (quota, _) = Quota(6);

        (await quota.SetLimitAsync(QuotaMeter.Vcpu, 10m)).IsSuccess.ShouldBeTrue();
        var lease = (await quota.TryReserveAsync(QuotaMeter.Vcpu, 3m, Guid.NewGuid()))
            .GetValueOrThrow();

        (await quota.CommitAsync(lease.LeaseId)).IsSuccess.ShouldBeTrue();
        (await quota.CommitAsync(lease.LeaseId)).IsFailure.ShouldBeTrue(
            "double-committing would bill twice for one resource.");

        (await quota.GetUsageAsync(QuotaMeter.Vcpu)).GetValueOrThrow().Committed.ShouldBe(3m);
    }

    [Fact]
    public async Task ReleasingAnAlreadyGoneLeaseIsANoOp()
    {
        var (quota, _) = Quota(7);

        (await quota.SetLimitAsync(QuotaMeter.Vcpu, 10m)).IsSuccess.ShouldBeTrue();
        var lease = (await quota.TryReserveAsync(QuotaMeter.Vcpu, 3m, Guid.NewGuid()))
            .GetValueOrThrow();

        (await quota.ReleaseAsync(lease.LeaseId)).IsSuccess.ShouldBeTrue();
        (await quota.ReleaseAsync(lease.LeaseId)).IsSuccess.ShouldBeTrue(
            "the failure path is re-driven from a reminder and must be safe to run twice.");
    }

    [Fact]
    public async Task ALeaseNeedsAnOwningOperationBecauseThatIsWhatMakesItALeaseAndNotACounter()
    {
        var (quota, _) = Quota(8);

        var refused = await quota.TryReserveAsync(QuotaMeter.Vcpu, 1m, Guid.Empty);

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        refused.Error.Message.ShouldContain("counter increment");
    }

    [Fact]
    public async Task CommittedQuotaComesBackWhenTheResourceIsDeleted()
    {
        var (quota, _) = Quota(9);

        (await quota.SetLimitAsync(QuotaMeter.Vcpu, 10m)).IsSuccess.ShouldBeTrue();
        var lease = (await quota.TryReserveAsync(QuotaMeter.Vcpu, 6m, Guid.NewGuid()))
            .GetValueOrThrow();
        (await quota.CommitAsync(lease.LeaseId)).IsSuccess.ShouldBeTrue();

        (await quota.ReturnAsync(QuotaMeter.Vcpu, 6m)).GetValueOrThrow().Committed.ShouldBe(0m);
    }

    [Fact]
    public async Task AMeterCannotBeDrivenNegative()
    {
        var (quota, _) = Quota(10);

        var refused = await quota.ReturnAsync(QuotaMeter.Vcpu, 1m);

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.Conflict);
        refused.Error.Message.ShouldContain("below zero");
    }

    [Fact]
    public async Task QuotaSurvivesTheGrainDyingBecauseItIsDurable()
    {
        var (quota, key) = Quota(11);

        (await quota.SetLimitAsync(QuotaMeter.StorageGb, 500m)).IsSuccess.ShouldBeTrue();
        var lease = (await quota.TryReserveAsync(QuotaMeter.StorageGb, 100m, Guid.NewGuid()))
            .GetValueOrThrow();
        (await quota.CommitAsync(lease.LeaseId)).IsSuccess.ShouldBeTrue();

        await quota.DeactivateAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken);

        var usage = (await Quota(key).GetUsageAsync(QuotaMeter.StorageGb)).GetValueOrThrow();
        usage.Committed.ShouldBe(100m);
        usage.Limit.ShouldBe(500m);
    }

    [Fact]
    public async Task ConcurrentReservationsAreSerialisedAndTheLimitHolds()
    {
        // ⚠ docs/plan/06 § Quota: "The quota grain is per-subscription and therefore serialises
        // every create in that subscription. That is correct — quota is exactly the thing that needs
        // a single writer." This is that sentence as an experiment: twenty concurrent reservations
        // of 1 against a limit of 5. Exactly five may win. A read-modify-write against a shared
        // counter with no single writer would let more than five through under this load.
        var (quota, _) = Quota(12);

        (await quota.SetLimitAsync(QuotaMeter.PublicIps, 5m)).IsSuccess.ShouldBeTrue();

        var attempts = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            quota.TryReserveAsync(QuotaMeter.PublicIps, 1m, Guid.NewGuid())));

        attempts.Count(x => x.IsSuccess).ShouldBe(5);
        attempts.Count(x => x.IsFailure).ShouldBe(15);
        attempts.Where(x => x.IsFailure).ShouldAllBe(x => x.Error!.Code == ErrorCode.QuotaExceeded);

        (await quota.GetUsageAsync(QuotaMeter.PublicIps)).GetValueOrThrow().Reserved.ShouldBe(5m);
    }

    [Fact]
    public async Task TheQuotaGrainAndTheSubscriptionGrainShareAKeyAndAreStillTwoGrains()
    {
        // The key shape decision: IQuotaGrain reuses sub/{id}. Orleans addresses an activation by
        // (grain type, key), so this is one subscription with two grains, not a collision.
        var tenant = Tenant(13);
        var subscription = Guid.NewGuid();

        (await cluster.TenantGrain(tenant).CreateAsync("quota-key", "Q", "eu-central")).IsSuccess
            .ShouldBeTrue();
        (await cluster.SubscriptionGrain(tenant, subscription).CreateAsync("prod")).IsSuccess
            .ShouldBeTrue();

        var quota = cluster.QuotaGrain(tenant, subscription);
        (await quota.SetLimitAsync(QuotaMeter.Clusters, 3m)).IsSuccess.ShouldBeTrue();

        cluster.SubscriptionGrain(tenant, subscription).GetGrainId().Key.ToString()
            .ShouldBe(quota.GetGrainId().Key.ToString());

        cluster.SubscriptionGrain(tenant, subscription).GetGrainId().Type.ToString()
            .ShouldNotBe(quota.GetGrainId().Type.ToString());

        (await cluster.SubscriptionGrain(tenant, subscription).GetAsync()).GetValueOrThrow()
            .DisplayName.ShouldBe("prod");
        (await quota.GetUsageAsync(QuotaMeter.Clusters)).GetValueOrThrow().Limit.ShouldBe(3m);
    }

    [Fact]
    public async Task EveryMeterHasADefaultLimitSoAnUnconfiguredSubscriptionIsNotUnlimited()
    {
        var (quota, _) = Quota(14);

        foreach (var meter in Enum.GetValues<QuotaMeter>().Where(x => x != QuotaMeter.Unknown))
        {
            (await quota.GetUsageAsync(meter)).GetValueOrThrow().Limit.ShouldBeGreaterThan(
                0m, $"{meter} has no default limit, so it is effectively unlimited.");
        }
    }

    (IQuotaGrain Quota, (Guid Tenant, Guid Subscription) Key) Quota(int n)
    {
        var tenant = Tenant(n);
        var subscription = TenancyCluster.Tenant(6000 + n);
        return (cluster.QuotaGrain(tenant, subscription), (tenant, subscription));
    }

    IQuotaGrain Quota((Guid Tenant, Guid Subscription) key) =>
        cluster.QuotaGrain(key.Tenant, key.Subscription);
}
