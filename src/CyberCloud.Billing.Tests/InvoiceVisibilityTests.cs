using CyberCloud.Authorization.Contracts;
using CyberCloud.ResourceManager;

namespace CyberCloud.Billing.Tests;

/// <summary>
///     The invoices a caller may read, from the real billing account and the real ReBAC engine —
///     issue #41's half of docs/plan/22 § What is owed, <c>billing-http-surface</c>: who may see a
///     tenant's invoices is <c>read</c> on the tenant, and a subscription reader is not enough.
/// </summary>
/// <remarks>
///     ⚠ <b>Every grant is spelled the scope authorizer's way</b> —
///     <see cref="ReBacScopeAuthorizer.ObjectOf" /> — because <c>InvoiceQueryGrain</c> spells the
///     tenant's object id a second time and may not reference that assembly.
/// </remarks>
[Collection(BillingClusterFixture.Name)]
public sealed class InvoiceVisibilityTests(BillingCluster cluster) : IAsyncLifetime {
    static readonly DateTimeOffset July = new(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset August = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

    /// <inheritdoc />
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
    public async Task ATenantReaderListsEveryInvoiceNewestFirstWithItsLines() {
        var world = await WorldAsync();
        await world.GrantTenantAsync("tina");

        var listed = (await cluster.Invoices.ListAsync(world.Tenant, Caller("tina"), TestContext.Current.CancellationToken)).GetValueOrThrow();

        listed.Select(static x => x.PeriodStart).ShouldBe([August, July], "newest first");
        listed[0].Number.ShouldBe(world.August.Number);
        listed[0].Lines.ShouldHaveSingleItem().Amount.ShouldBe(2.50m, "100 vCPU-hours at 0.025");
        listed[0].Total.ShouldBe(3.03m, "2.50 and 21 % Czech VAT, 0.525 rounded half away from zero");

        var one = (await cluster.Invoices.GetAsync(world.Tenant, Caller("tina"), world.August.Number, TestContext.Current.CancellationToken))
            .GetValueOrThrow();
        one.ShouldBeEquivalentTo(world.August);
    }

    /// <summary>
    ///     ⚠ The decision, as an assertion: an invoice carries every attached subscription's lines and the
    ///     tenant's legal profile, so reading one subscription is not reading the invoice.
    /// </summary>
    [Fact]
    public async Task ASubscriptionReaderCannotReadTheTenantsInvoices() {
        var world = await WorldAsync();
        var (type, id) = ReBacScopeAuthorizer.ObjectOf(ScopeId.Subscription(world.Tenant, world.Subscription));
        await cluster.GrantAsync(world.Tenant, ObjectRef.Of(type, id), Relations.Owner, SubjectRef.Of(ObjectTypes.User, "sam"));

        var listed = await cluster.Invoices.ListAsync(world.Tenant, Caller("sam"), TestContext.Current.CancellationToken);

        listed.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
        listed.Error.Message.ShouldBe($"'{new InvoiceAddress(world.Tenant, string.Empty).Path}' does not exist.");
    }

    /// <summary>
    ///     ⚠ 404, never 403 (docs/plan/07 § The enforcement seam): a stranger asking for a real number gets
    ///     exactly the sentence a tenant reader gets for a number that was never issued.
    /// </summary>
    [Fact]
    public async Task AStrangerAndAMissingNumberGetTheSameAnswer() {
        var world = await WorldAsync();
        await world.GrantTenantAsync("tina");

        var stranger = await cluster.Invoices.GetAsync(world.Tenant, Caller("mallory"), world.August.Number, TestContext.Current.CancellationToken);
        var missing = await cluster.Invoices.GetAsync(world.Tenant, Caller("tina"), "HT-INV-99999999", TestContext.Current.CancellationToken);

        stranger.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
        missing.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
        stranger.Error.Message.ShouldBe($"'{new InvoiceAddress(world.Tenant, world.August.Number).Path}' does not exist.");
        missing.Error.Message.ShouldBe($"'{new InvoiceAddress(world.Tenant, "HT-INV-99999999").Path}' does not exist.");
    }

    /// <summary>
    ///     ⚠ Fully consistent: a revoked tenant reader loses the invoices on the next read, not when the
    ///     check cache forgets them.
    /// </summary>
    [Fact]
    public async Task ARevokedTenantReaderLosesTheInvoicesAtOnce() {
        var world = await WorldAsync();
        await world.GrantTenantAsync("tina");
        (await cluster.Invoices.ListAsync(world.Tenant, Caller("tina"), TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();

        var (type, id) = ReBacScopeAuthorizer.ObjectOf(ScopeId.Tenant(world.Tenant));
        await cluster.RevokeAsync(world.Tenant, ObjectRef.Of(type, id), Relations.Reader, SubjectRef.Of(ObjectTypes.User, "tina"));

        var after = await cluster.Invoices.ListAsync(world.Tenant, Caller("tina"), TestContext.Current.CancellationToken);
        after.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
    }

    [Fact]
    public async Task AnAccountWithNoInvoicesListsNone() {
        var (tenant, _) = await cluster.NewSubscriptionAsync("prod");
        var (type, id) = ReBacScopeAuthorizer.ObjectOf(ScopeId.Tenant(tenant));
        await cluster.GrantAsync(tenant, ObjectRef.Of(type, id), Relations.Reader, SubjectRef.Of(ObjectTypes.User, "tina"));

        var listed = await cluster.Invoices.ListAsync(tenant, Caller("tina"), TestContext.Current.CancellationToken);

        listed.GetValueOrThrow().ShouldBeEmpty();
    }

    static CostCaller Caller(string user) => new() { SubjectType = "user", SubjectId = user };

    // ── The world ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     A Czech account with one subscription and two finalized months: an empty July and an August
    ///     with 100 vCPU-hours in it.
    /// </summary>
    async Task<World> WorldAsync() {
        var (tenant, subscription) = await cluster.NewSubscriptionAsync("prod");
        var vm = Guid.NewGuid();
        var path = BillingCluster.PathOf(tenant, subscription, "prod", "web", "CyberCloud.Compute/virtualMachines");
        await cluster.UseHoursAsync(tenant, subscription, vm, path, BillingMeter.VCpuHours, August, 25, 4m);

        var account = cluster.Account(tenant);
        (await account.ConfigureAsync(new() { LegalName = "Firma s.r.o.", Country = "CZ", Currency = "EUR" })).IsSuccess.ShouldBeTrue();
        (await account.AttachSubscriptionAsync(subscription)).IsSuccess.ShouldBeTrue();

        (await account.FinalizeAsync(July)).IsSuccess.ShouldBeTrue();
        var august = (await account.FinalizeAsync(August)).GetValueOrThrow();

        return new(cluster, tenant, subscription, august);
    }

    sealed record World(BillingCluster Cluster, Guid Tenant, Guid Subscription, Invoice August) {
        public Task GrantTenantAsync(string user) {
            var (type, id) = ReBacScopeAuthorizer.ObjectOf(ScopeId.Tenant(Tenant));
            return Cluster.GrantAsync(Tenant, ObjectRef.Of(type, id), Relations.Reader, SubjectRef.Of(ObjectTypes.User, user));
        }
    }
}
