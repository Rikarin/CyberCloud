using CyberCloud.Authorization.Contracts;
using CyberCloud.ResourceManager;

namespace CyberCloud.Billing.Tests;

/// <summary>
///     The cost query, answered from the real ledger and filtered by the real ReBAC engine —
///     docs/plan/22 § Cost visibility, and #38's "a Reader of one group sees that group's cost only".
/// </summary>
/// <remarks>
///     ⚠ <b>Every grant here is spelled the resource manager's way</b> —
///     <see cref="ReBacResourceAuthorizer.GroupObjectId" /> and
///     <see cref="ReBacResourceAuthorizer.SubscriptionObjectId" /> — because <c>CostQueryGrain</c>
///     spells the same object ids a second time and may not reference that assembly. A drift between
///     the two would read here as a reader who sees nothing.
/// </remarks>
[Collection(BillingClusterFixture.Name)]
public sealed class CostVisibilityTests(BillingCluster cluster) {
    static readonly DateTimeOffset August = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset September = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ASubscriptionReaderSeesEveryGroup() {
        var world = await WorldAsync();
        await world.GrantSubscriptionAsync("alice");

        var answer = (await world.QueryAsync("alice", CostGrouping.ResourceGroup)).GetValueOrThrow();

        answer.Rows.Select(static x => x.Name).ShouldBe(["prod", "dev"], "most expensive first");
        answer.Rows[0].Amount.ShouldBe(2.50m);
        answer.Rows[1].Amount.ShouldBe(0.25m);
        answer.Total.ShouldBe(2.75m);
        answer.Filtered.ShouldBeFalse();
        answer.Currency.ShouldBe("EUR");
    }

    /// <summary>⚠ The issue's sentence, as an assertion.</summary>
    [Fact]
    public async Task AReaderOfOneGroupSeesThatGroupsCostOnly() {
        var world = await WorldAsync();
        await world.GrantGroupAsync("prod", "bob");

        var whole = (await world.QueryAsync("bob", CostGrouping.Resource)).GetValueOrThrow();

        whole.Rows.ShouldHaveSingleItem().Name.ShouldBe(world.Prod);
        whole.Total.ShouldBe(2.50m, "dev's 0.25 is not in the total either");
        whole.Filtered.ShouldBeTrue("something was withheld, and the answer says so without saying what");

        var theirGroup = (await world.QueryAsync("bob", CostGrouping.Meter, group: "prod")).GetValueOrThrow();
        theirGroup.Rows.ShouldHaveSingleItem().Quantity.ShouldBe(100m);

        var otherGroup = await world.QueryAsync("bob", CostGrouping.Meter, group: "dev");
        otherGroup.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
    }

    [Fact]
    public async Task AReaderOfOneResourceSeesThatResourceOnly() {
        var world = await WorldAsync();
        await cluster.GrantAsync(world.Tenant, ObjectRef.Of(ObjectTypes.Resource, world.DevResource), Relations.Reader, SubjectRef.Of(ObjectTypes.User, "carol"));

        var answer = (await world.QueryAsync("carol", CostGrouping.Resource)).GetValueOrThrow();

        answer.Rows.ShouldHaveSingleItem().Name.ShouldBe(world.Dev);
        answer.Filtered.ShouldBeTrue();
    }

    /// <summary>
    ///     ⚠ 404, never 403 (docs/plan/07 § The enforcement seam): a caller who may read nothing in scope
    ///     gets exactly the answer a subscription that does not exist gets.
    /// </summary>
    [Fact]
    public async Task ACallerWhoMayReadNothingGetsTheAnswerAnAbsentSubscriptionGets() {
        var world = await WorldAsync();
        await world.GrantSubscriptionAsync("alice");

        var stranger = await world.QueryAsync("mallory", CostGrouping.Day);

        var absent = Guid.NewGuid();
        var nowhere = await cluster.Costs.QueryAsync(
            world.Tenant,
            new() { Caller = new() { SubjectType = "user", SubjectId = "alice" }, SubscriptionId = absent, From = August, To = September, Grouping = CostGrouping.Day },
            TestContext.Current.CancellationToken
        );

        stranger.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
        stranger.Error.Message.ShouldBe($"'/tenants/{world.Tenant:D}/subscriptions/{world.Subscription:D}' does not exist.");
        nowhere.Error!.Message.ShouldBe($"'/tenants/{world.Tenant:D}/subscriptions/{absent:D}' does not exist.");
    }

    [Fact]
    public async Task AGroupReaderWithNoUsageInThePeriodGetsAnEmptyAnswerNotA404() {
        var world = await WorldAsync();
        await world.GrantGroupAsync("idle", "dave");

        var answer = (await world.QueryAsync("dave", CostGrouping.ResourceGroup)).GetValueOrThrow();

        answer.Rows.ShouldBeEmpty();
        answer.Total.ShouldBe(0m);
    }

    [Fact]
    public async Task RowsGroupByDayMeterAndType() {
        var world = await WorldAsync();
        await world.GrantSubscriptionAsync("alice");

        var days = (await world.QueryAsync("alice", CostGrouping.Day)).GetValueOrThrow();
        var types = (await world.QueryAsync("alice", CostGrouping.ResourceType)).GetValueOrThrow();
        var meters = (await world.QueryAsync("alice", CostGrouping.Meter)).GetValueOrThrow();

        days.Rows.Select(static x => x.Name).Order(StringComparer.Ordinal).ShouldBe(["2026-08-01", "2026-08-02", "2026-08-03", "2026-08-04"]);
        days.Total.ShouldBe(2.75m);
        types.Rows.Select(static x => x.Name).ShouldBe(["CyberCloud.Compute/virtualMachines", "CyberCloud.Network/publicIpAddresses"]);
        meters.Rows.Single(static x => x.Name == "PublicIpHours").Quantity.ShouldBe(62.5m);
    }

    [Fact]
    public async Task APeriodIsWidenedToWholeHoursAndBoundedToAYear() {
        var world = await WorldAsync();
        await world.GrantSubscriptionAsync("alice");

        var widened = (await world.QueryAsync("alice", CostGrouping.Day, from: August.AddMinutes(30), to: August.AddHours(2).AddMinutes(1))).GetValueOrThrow();
        widened.From.ShouldBe(August);
        widened.To.ShouldBe(August.AddHours(3));

        (await world.QueryAsync("alice", CostGrouping.Day, from: August, to: August.AddDays(367))).Error!.Target.ShouldBe("/to");
        (await world.QueryAsync("alice", CostGrouping.Unknown)).Error!.Target.ShouldBe("/groupBy");
    }

    /// <summary>
    ///     ⚠ The cost view and the invoice read one rating path (<c>UsagePricing</c>), so for a month
    ///     with one meter the view's total is the invoice's subtotal to the cent.
    /// </summary>
    [Fact]
    public async Task AMonthsCostIsWhatItsInvoiceCharges() {
        var world = await WorldAsync();
        await world.GrantSubscriptionAsync("alice");
        var account = cluster.Account(world.Tenant);
        (await account.ConfigureAsync(new() { LegalName = "Firma", Country = "CZ", Currency = "EUR" })).IsSuccess.ShouldBeTrue();
        (await account.AttachSubscriptionAsync(world.Subscription)).IsSuccess.ShouldBeTrue();

        var cost = (await world.QueryAsync("alice", CostGrouping.Meter, from: August, to: September)).GetValueOrThrow();
        var invoice = (await account.PreviewAsync(August)).GetValueOrThrow();

        foreach (var line in invoice.Lines) {
            cost.Rows.Single(x => x.Name == line.Meter.ToString()).Amount.ShouldBe(line.Amount);
        }

        cost.Total.ShouldBe(invoice.Subtotal);
    }

    // ── The world ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     A subscription with two groups with usage in each — a VM in <c>prod</c> at 2.50 € and a
    ///     public IP in <c>dev</c> at 0.25 € — and a third group, <c>idle</c>, with none.
    /// </summary>
    async Task<World> WorldAsync() {
        var (tenant, subscription) = await cluster.NewSubscriptionAsync("prod", "dev", "idle");
        var prodResource = Guid.NewGuid();
        var devResource = Guid.NewGuid();
        var prod = BillingCluster.PathOf(tenant, subscription, "prod", "web", "CyberCloud.Compute/virtualMachines");
        var dev = BillingCluster.PathOf(tenant, subscription, "dev", "ip", "CyberCloud.Network/publicIpAddresses");

        // 100 vCPU-hours at 0.025 = 2.50, over the first 25 hours.
        await cluster.UseHoursAsync(tenant, subscription, prodResource, prod, BillingMeter.VCpuHours, August, 25, 4m);
        // 62.5 IP-hours at 0.004 = 0.25, over fifty hours from the 2nd.
        await cluster.UseHoursAsync(tenant, subscription, devResource, dev, BillingMeter.PublicIpHours, August.AddDays(1), 50, 1.25m);

        return new(cluster, tenant, subscription, prod, dev, devResource);
    }

    sealed record World(BillingCluster Cluster, Guid Tenant, Guid Subscription, string Prod, string Dev, Guid DevResource) {
        public Task GrantSubscriptionAsync(string user) {
            ResourceId.TryParsePath(Prod, out var anyResource).ShouldBeTrue();
            return Cluster.GrantAsync(
                Tenant,
                ObjectRef.Of(ObjectTypes.Subscription, ReBacResourceAuthorizer.SubscriptionObjectId(anyResource)),
                Relations.Reader,
                SubjectRef.Of(ObjectTypes.User, user)
            );
        }

        public Task GrantGroupAsync(string group, string user) {
            ResourceId.TryParsePath(BillingCluster.PathOf(Tenant, Subscription, group, "anything"), out var inGroup).ShouldBeTrue();
            return Cluster.GrantAsync(
                Tenant,
                ObjectRef.Of(ObjectTypes.ResourceGroup, ReBacResourceAuthorizer.GroupObjectId(inGroup)),
                Relations.Reader,
                SubjectRef.Of(ObjectTypes.User, user)
            );
        }

        public Task<Result<CostQueryResult>> QueryAsync(
            string user,
            CostGrouping grouping,
            string group = "",
            DateTimeOffset? from = null,
            DateTimeOffset? to = null
        ) =>
            Cluster.Costs.QueryAsync(
                Tenant,
                new() {
                    Caller = new() { SubjectType = "user", SubjectId = user },
                    SubscriptionId = Subscription,
                    ResourceGroup = group,
                    From = from ?? August,
                    To = to ?? September,
                    Grouping = grouping
                }
            );
    }
}
