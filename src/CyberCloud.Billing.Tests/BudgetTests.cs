using CyberCloud.Authorization.Contracts;
using CyberCloud.Billing.Grains;

namespace CyberCloud.Billing.Tests;

/// <summary>
///     A budget, evaluated by its real grain against the real ledger, alerting through the real sending
///     module onto the in-process carrier — docs/plan/22 § Cost visibility, "Budgets and alerts:
///     threshold at 50/80/100/forecast, delivered via [17]".
/// </summary>
[Collection(BillingClusterFixture.Name)]
public sealed class BudgetTests(BillingCluster cluster) : IAsyncLifetime {
    static readonly DateTimeOffset September = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Every test starts at <see cref="TestClock.Start" />.</summary>
    public ValueTask InitializeAsync() {
        TestClock.Instance.Reset();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() {
        TestClock.Instance.Reset();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task AnActualThresholdFiresOncePerPeriodAndTheMessageReachesTheCarrier() {
        TestClock.Instance.Reset();
        var budget = await BudgetAsync(10m, [Actual(50m), Actual(80m), Actual(100m)]);

        // 200 vCPU-hours at 0.025 = 5.00: exactly 50 % of 10.00.
        await budget.UseAsync("prod", 200m);

        var first = await budget.EvaluateAsync();
        var second = await budget.EvaluateAsync();

        first.Actual.ShouldBe(5.00m);
        first.Fired.ShouldBe(1, "50 % is reached — at the line, not past it");
        second.Fired.ShouldBe(0, "a threshold fires once per period");

        var sent = budget.Sent().ShouldHaveSingleItem();
        sent.Channel.ShouldBe(Communication.Contracts.ChannelKind.Email);
        sent.Body.ShouldContain("5.00 EUR");
        sent.Body.ShouldContain("50 %");
        sent.Body.ShouldContain("resource group prod");

        var alert = (await budget.HeldAsync()).Alerts.ShouldHaveSingleItem();
        alert.Notification.ShouldContain("sent");
        alert.PeriodStart.ShouldBe(September);

        // Past 80 % now: one more alert, one more message.
        await budget.UseAsync("prod", 130m);
        (await budget.EvaluateAsync()).Fired.ShouldBe(1);
        budget.Sent().Count.ShouldBe(2);
    }

    [Fact]
    public async Task AForecastThresholdFiresOnTheTrailingSevenDaysRate() {
        TestClock.Instance.Reset();
        var budget = await BudgetAsync(10m, [Actual(50m), Forecast(100m)]);

        // One vCPU-hour an hour for the 160 hours before now: 4.00 so far, at a rate that reaches
        // 4.00 + 4.00 / 168 h × the ~491.7 h left in September ≈ 15.71 by the 30th.
        for (var hour = 0; hour < 160; hour++) {
            await budget.UseAtAsync("prod", TestClock.Start.AddHours(-160 + hour), 1m);
        }

        var report = await budget.EvaluateAsync();

        report.Actual.ShouldBe(4.00m);
        report.Forecast.ShouldBe(15.71m);
        report.Fired.ShouldBe(1);

        var alert = (await budget.HeldAsync()).Alerts.ShouldHaveSingleItem();
        alert.Kind.ShouldBe(ThresholdKind.Forecast);
        budget.Sent().ShouldHaveSingleItem().Body.ShouldContain("An estimate");
    }

    [Fact]
    public async Task AResourceGroupBudgetCountsItsOwnGroupOnly() {
        TestClock.Instance.Reset();
        var budget = await BudgetAsync(10m, [Actual(50m)]);

        await budget.UseAsync("dev", 400m);

        var report = await budget.EvaluateAsync();

        report.Actual.ShouldBe(0m, "10.00 of usage in dev is not prod's");
        report.Fired.ShouldBe(0);
        budget.Sent().ShouldBeEmpty();
    }

    /// <summary>
    ///     ⚠ The disclosure a subscription scope would otherwise be: a contributor of one group creates a
    ///     budget over the whole subscription and reads its spend. Until the budget itself is granted
    ///     reader on the subscription, nothing is evaluated and nobody is told.
    /// </summary>
    [Fact]
    public async Task ASubscriptionBudgetSeesNothingUntilItIsGrantedReaderOnTheSubscription() {
        TestClock.Instance.Reset();
        var budget = await BudgetAsync(10m, [Actual(50m)], BudgetScope.Subscription);

        await budget.UseAsync("dev", 400m);

        var refused = await budget.EvaluateAsync();

        refused.Evaluated.ShouldBeFalse();
        refused.Error.ShouldContain($"resource:{budget.Id:N}");
        budget.Sent().ShouldBeEmpty();
        (await budget.HeldAsync()).Actual.ShouldBe(0m, "no figure is kept for a scope the budget may not read");

        await cluster.GrantAsync(
            budget.Tenant,
            ObjectRef.Of(ObjectTypes.Subscription, budget.Subscription),
            Relations.Reader,
            SubjectRef.Of(ObjectTypes.Resource, budget.Id)
        );

        var granted = await budget.EvaluateAsync();

        granted.Evaluated.ShouldBeTrue();
        granted.Actual.ShouldBe(10.00m, "the whole subscription, dev included");
        granted.Fired.ShouldBe(1);
        budget.Sent().ShouldHaveSingleItem().Body.ShouldContain($"subscription {budget.Subscription:D}");
    }

    [Fact]
    public async Task TheNextPeriodFiresAgain() {
        TestClock.Instance.Reset();
        var budget = await BudgetAsync(1m, [Actual(100m)]);

        await budget.UseAsync("prod", 40m);
        (await budget.EvaluateAsync()).Fired.ShouldBe(1);

        var october = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        TestClock.Instance.Set(october.AddDays(3));
        await budget.UseAtAsync("prod", october.AddHours(5), 40m);

        var report = await budget.EvaluateAsync();
        TestClock.Instance.Reset();

        report.Fired.ShouldBe(1, "October is a new period");
        (await budget.HeldAsync()).Alerts.Select(static x => x.PeriodStart).ShouldBe([September, october]);
        budget.Sent().Count.ShouldBe(2);
    }

    [Fact]
    public async Task AnEnabledBudgetIsArmedAndADisabledOneIsNeitherArmedNorEvaluated() {
        TestClock.Instance.Reset();
        var budget = await BudgetAsync(1m, [Actual(1m)]);

        var ct = TestContext.Current.CancellationToken;

        (await cluster.Budgets.IsArmedAsync(budget.Tenant, budget.Id, ct)).GetValueOrThrow().ShouldBeTrue();

        (await cluster.Budgets.UpsertAsync(budget.Tenant, budget.Spec with { Enabled = false }, ct)).IsSuccess.ShouldBeTrue();

        (await cluster.Budgets.IsArmedAsync(budget.Tenant, budget.Id, ct)).GetValueOrThrow().ShouldBeFalse();
        (await budget.EvaluateAsync()).Evaluated.ShouldBeFalse();

        (await cluster.Budgets.RemoveAsync(budget.Tenant, budget.Id, ct)).IsSuccess.ShouldBeTrue();
        (await cluster.Budgets.GetAsync(budget.Tenant, budget.Id, ct)).Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
    }

    [Theory]
    [InlineData(BudgetPeriod.Monthly, "2026-09-01", "2026-10-01")]
    [InlineData(BudgetPeriod.Quarterly, "2026-07-01", "2026-10-01")]
    [InlineData(BudgetPeriod.Annually, "2026-01-01", "2027-01-01")]
    public void PeriodsAreCalendarAlignedInUtc(BudgetPeriod period, string start, string end) {
        var (from, to) = BudgetGrain.PeriodOf(period, TestClock.Start);

        from.ShouldBe(DateTimeOffset.Parse(start + "T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        to.ShouldBe(DateTimeOffset.Parse(end + "T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────────

    static BudgetThreshold Actual(decimal percent) => new() { Percent = percent, Kind = ThresholdKind.Actual };

    static BudgetThreshold Forecast(decimal percent) => new() { Percent = percent, Kind = ThresholdKind.Forecast };

    async Task<Budget> BudgetAsync(decimal amount, BudgetThreshold[] thresholds, BudgetScope scope = BudgetScope.ResourceGroup) {
        var (tenant, subscription) = await cluster.NewSubscriptionAsync("prod", "dev");
        var (servicePath, _) = await cluster.SendingServiceAsync(tenant, subscription, "alerts");
        var recipient = $"finance-{Guid.NewGuid():N}@example.com";
        var id = Guid.NewGuid();

        var spec = new BudgetSpec {
            BudgetId = id,
            Name = "monthly",
            SubscriptionId = subscription,
            ResourceGroup = "prod",
            Scope = scope,
            Amount = amount,
            Period = BudgetPeriod.Monthly,
            Thresholds = [.. thresholds],
            Notification = new() { ServicePath = servicePath, Channel = "email", Recipients = [recipient] },
            Enabled = true
        };

        (await cluster.Budgets.UpsertAsync(tenant, spec)).IsSuccess.ShouldBeTrue();

        return new(cluster, tenant, subscription, id, spec, recipient);
    }

    sealed record Budget(BillingCluster Cluster, Guid Tenant, Guid Subscription, Guid Id, BudgetSpec Spec, string Recipient) {
        /// <summary>vCPU-hours in a group, on the first hour of September.</summary>
        public Task UseAsync(string group, decimal vcpuHours) => UseAtAsync(group, September.AddHours(Hours++), vcpuHours);

        public async Task UseAtAsync(string group, DateTimeOffset hour, decimal vcpuHours) =>
            await Cluster.UseAsync(
                Tenant,
                Subscription,
                Guid.NewGuid(),
                BillingCluster.PathOf(Tenant, Subscription, group, "vm"),
                BillingMeter.VCpuHours,
                hour,
                vcpuHours
            );

        public async Task<BudgetEvaluationReport> EvaluateAsync() {
            var report = await Cluster.Budgets.EvaluateAsync(Tenant, Id);
            report.IsSuccess.ShouldBeTrue(report.Error?.Message);
            return report.GetValueOrThrow();
        }

        public async Task<BudgetSnapshot> HeldAsync() => (await Cluster.Budgets.GetAsync(Tenant, Id)).GetValueOrThrow();

        public List<Communication.Contracts.OutboundMessage> Sent() =>
            [.. Carriers.Email.Sent.Where(x => string.Equals(x.Destination, Recipient, StringComparison.Ordinal))];

        int Hours { get; set; }
    }
}
