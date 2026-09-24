using CyberCloud.Authorization.Contracts;
using CyberCloud.Billing.Contracts;
using CyberCloud.Core.Resources;
using CyberCloud.Gateway.Host;
using CyberCloud.Metering.Contracts;
using CyberCloud.ServiceDefaults;
using CyberCloud.Silo.Host;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Multitenant;
using Shouldly;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;
// Orleans has an ErrorCode too, and the Orleans global using arrives with both hosts' reference sets.
using ErrorCode = CyberCloud.Core.ErrorCode;

namespace CyberCloud.Hosts.Tests;

/// <summary>
///     The billing grain calls, made from the real gateway host's cluster client to the real silo host
///     — the process boundary a <c>TestCluster</c> hides.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             Why this exists: batch 3's post-merge defect and #39's were both a type that crossed a
///             process boundary for the first time and was refused by the receiving host's type
///             manifest.
///         </b> Every billing test elsewhere runs a <c>TestCluster</c>, whose client and silo share one
///         manifest, so an unaliased wire type or a generic invokable would pass there and fail here.
///         Both hosts are composed by their own <c>BuildAsync</c> — the call <c>Program.cs</c> makes — and
///         every call below goes gateway → silo: the cost query the dispatch stage makes, a budget the
///         reconciler would write, an invoice draft, and the finalized invoices the invoices address
///         reads (#41), each carrying decimals, immutable arrays and nested records across.
///     </para>
///     <para>
///         ⚠ <b>In one OS process, and that is the limit of what it proves.</b> Two hosts with two
///         service providers and two Orleans runtimes, talking over the silo's gateway port — so
///         serialization, the allowed-type manifest and tenant qualification are all real — but not two
///         processes. <c>CyberCloud.AppHost.Tests</c> is where a request crosses two, and a cost query
///         through it is owed with the portal's cost page (docs/plan/22 § What is owed).
///     </para>
/// </remarks>
public sealed class BillingAcrossTheHostsTests : IAsyncLifetime {
    const string TenantShard = "durable-00";
    const string PlatformShard = "platform";

    // The images StorageFixture in CyberCloud.ServiceDefaults.Tests uses, so a machine that has run
    // that suite pulls nothing for this one.
    readonly RedisContainer redis = new RedisBuilder("redis:8-alpine").WithCommand("--maxmemory-policy", "noeviction").Build();

    readonly PostgreSqlContainer tenantShard = Shard();

    readonly PostgreSqlContainer platformShard = Shard();

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Real storage, because the durable tier is where a billing type can fail and a
    ///     <c>TestCluster</c> cannot.</b> The silo's durable tier writes grain state as JSON through
    ///     <c>SystemTextJsonGrainStorageSerializer</c> into PostgreSQL; an invoice, a budget and a number
    ///     sequence are written here through the real provider, which is the round-trip
    ///     <c>CyberCloud.Billing.Tests</c>' in-memory storage owes. Two shards, because the numbering
    ///     grain is null-tenant and the silo keeps the platform's shard apart from the tenants'.
    /// </remarks>
    public async ValueTask InitializeAsync() {
        var ct = TestContext.Current.CancellationToken;

        await Task.WhenAll(redis.StartAsync(ct), tenantShard.StartAsync(ct), platformShard.StartAsync(ct));

        // The shipped applier — the call `--apply-durable-schema` makes.
        await ServiceDefaults.Storage.OrleansAdoNetSchema.ApplyAsync(tenantShard.GetConnectionString(), ct);
        await ServiceDefaults.Storage.OrleansAdoNetSchema.ApplyAsync(platformShard.GetConnectionString(), ct);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() =>
        await Task.WhenAll(redis.DisposeAsync().AsTask(), tenantShard.DisposeAsync().AsTask(), platformShard.DisposeAsync().AsTask());

    [Fact]
    public async Task ACostQueryABudgetAndAnInvoiceCrossFromTheGatewayToTheSiloAndSurviveARestart() {
        var ct = TestContext.Current.CancellationToken;
        var gatewayPort = FreePort();

        var silo = await StartSiloAsync(gatewayPort, ct);
        var gateway = await StartGatewayAsync(gatewayPort, ct);

        // ── A tenant with a subscription, usage and a reader, set up over the gateway's client ────────
        var client = gateway.Services.GetRequiredService<IGrainFactory>();
        var tenant = Guid.NewGuid();
        var subscription = Guid.NewGuid();
        var grains = client.ForTenant(tenant.ToString("D", CultureInfo.InvariantCulture));

        (await grains.GetGrain<Tenancy.Contracts.ISubscriptionGrain>(GrainKeys.Subscription(subscription)).CreateAsync("billing"))
            .IsSuccess.ShouldBeTrue();

        // ⚠ THE MONTH'S FIRST HOUR, and the first version took the hour before now. In the first UTC hour
        // of a month that was last month's, which the monthly budget below doesn't count, so the test
        // failed for an hour every month. The month's first hour is this month's whenever the test runs,
        // and while it's still under way the budget and the cost view count it all the same.
        var now = DateTimeOffset.UtcNow;
        var month = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var hour = month;
        var path = $"/tenants/{tenant:D}/subscriptions/{subscription:D}/resourceGroups/prod/providers/CyberCloud.Compute/virtualMachines/web";

        var appended = await grains.GetGrain<IUsageLedgerGrain>(GrainKeys.Subscription(subscription))
            .AppendAsync(
                new() {
                    TenantId = tenant,
                    ResourceId = Guid.NewGuid(),
                    ResourcePath = path,
                    Meter = BillingMeter.VCpuHours,
                    Region = "eu-central",
                    WindowStart = hour,
                    WindowEnd = hour.AddHours(1),
                    Quantity = 40m,
                    SampleCount = 12
                }
            );
        appended.IsSuccess.ShouldBeTrue(appended.Error?.Message);

        var tuple = RelationTuple.Create(
                ObjectRef.Of(ObjectTypes.Subscription, subscription),
                Relations.Reader,
                SubjectRef.Of(ObjectTypes.User, "alice")
            )
            .GetValueOrThrow();
        (await grains.GetGrain<ITupleStoreGrain>(GrainKeys.TupleStore(tenant)).WriteAsync(tuple)).IsSuccess.ShouldBeTrue();

        // ── 1. The cost query, through the seam the dispatch stage holds ─────────────────────────────
        var costs = gateway.Services.GetRequiredService<ICostQuery>();

        var answer = await costs.QueryAsync(
            tenant,
            new() {
                Caller = new() { SubjectType = "user", SubjectId = "alice" },
                SubscriptionId = subscription,
                From = hour.AddDays(-1),
                To = hour.AddHours(1),
                Grouping = CostGrouping.Resource
            },
            ct
        );

        answer.IsSuccess.ShouldBeTrue(answer.Error?.Message);
        answer.GetValueOrThrow().Rows.ShouldHaveSingleItem().Name.ShouldBe(path);
        answer.GetValueOrThrow().Total.ShouldBe(1.00m, "40 vCPU-hours at 0.025");

        var stranger = await costs.QueryAsync(
            tenant,
            new() {
                Caller = new() { SubjectType = "user", SubjectId = "mallory" },
                SubscriptionId = subscription,
                From = hour.AddDays(-1),
                To = hour.AddHours(1),
                Grouping = CostGrouping.Resource
            },
            ct
        );
        stranger.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);

        // ── 2. A budget, through the control plane the reconciler holds ──────────────────────────────
        var budgets = gateway.Services.GetRequiredService<IBudgetControlPlane>();
        var budgetId = Guid.NewGuid();

        var upserted = await budgets.UpsertAsync(
            tenant,
            new() {
                BudgetId = budgetId,
                Name = "monthly",
                SubscriptionId = subscription,
                ResourceGroup = "prod",
                Scope = BudgetScope.ResourceGroup,
                Amount = 1.50m,
                Period = BudgetPeriod.Monthly,
                Thresholds = [new() { Percent = 50m, Kind = ThresholdKind.Actual }],
                Notification = new() {
                    ServicePath = $"/tenants/{tenant:D}/subscriptions/{subscription:D}/resourceGroups/prod/providers/CyberCloud.Communication/services/alerts",
                    Channel = "email",
                    Recipients = ["finance@example.com"]
                }
            },
            ct
        );
        upserted.IsSuccess.ShouldBeTrue(upserted.Error?.Message);

        var evaluated = (await budgets.EvaluateAsync(tenant, budgetId, ct)).GetValueOrThrow();
        evaluated.Actual.ShouldBe(1.00m);
        evaluated.Fired.ShouldBe(1, "1.00 is past 50 % of 1.50");

        var held = (await budgets.GetAsync(tenant, budgetId, ct)).GetValueOrThrow();
        held.Alerts.ShouldHaveSingleItem().Notification.ShouldContain("refused", Case.Sensitive, "no sending service exists, and the sending module says so by name");

        // ── 3. An invoice draft, through the billing account ─────────────────────────────────────────
        var account = grains.GetGrain<IBillingAccountGrain>(GrainKeys.Tenant(tenant));
        (await account.ConfigureAsync(new() { LegalName = "Firma s.r.o.", Country = "CZ", Currency = "EUR" })).IsSuccess.ShouldBeTrue();
        (await account.AttachSubscriptionAsync(subscription)).IsSuccess.ShouldBeTrue();

        var draft = (await account.PreviewAsync(month)).GetValueOrThrow();
        draft.Status.ShouldBe(InvoiceStatus.Draft);
        draft.Lines.ShouldHaveSingleItem().Amount.ShouldBe(1.00m);
        draft.Tax.RatePercent.ShouldBe(21m);

        // ── 4. A finalized invoice, numbered by the null-tenant grain on the platform's shard ────────
        //
        // Two months back, so the 48-hour window has passed whatever day this runs. No usage then: a
        // zero invoice is still a document and still takes the next number.
        var closed = month.AddMonths(-2);
        var finalized = (await account.FinalizeAsync(closed)).GetValueOrThrow();

        finalized.Number.ShouldStartWith("HT-INV-");
        finalized.Lines.ShouldBeEmpty();

        // ── 4b. The invoices, through the reader the dispatch stage holds (#41) ─────────────────────
        //
        // A new invokable on a new stateless grain, and the invoice document coming back across: a tenant
        // reader lists it, and alice — a subscription reader — gets the address's 404.
        var tenantReader = RelationTuple.Create(ObjectRef.Of(ObjectTypes.Tenant, tenant), Relations.Reader, SubjectRef.Of(ObjectTypes.User, "tina"))
            .GetValueOrThrow();
        (await grains.GetGrain<ITupleStoreGrain>(GrainKeys.TupleStore(tenant)).WriteAsync(tenantReader)).IsSuccess.ShouldBeTrue();

        var invoices = gateway.Services.GetRequiredService<IInvoiceReader>();

        var listed = await invoices.ListAsync(tenant, new() { SubjectType = "user", SubjectId = "tina" }, ct);
        listed.IsSuccess.ShouldBeTrue(listed.Error?.Message);
        listed.GetValueOrThrow().ShouldHaveSingleItem().ShouldBeEquivalentTo(finalized);

        var notTheirs = await invoices.GetAsync(tenant, new() { SubjectType = "user", SubjectId = "alice" }, finalized.Number, ct);
        notTheirs.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound, "reading a subscription is not reading the tenant's invoices");

        // ── 5. The month close: armed in the silo's Redis reminder table, and callable across ────────
        //
        // The account was attached this month, so the close owns no month yet and answers an empty
        // array. What crosses here is the new invokable and its result, not a finalization.
        (await account.CloseMonthsAsync()).GetValueOrThrow().ShouldBeEmpty();

        var armed = await silo.Services.GetRequiredService<IReminderTable>().ReadRow(account.GetGrainId(), "close-months");
        armed.ShouldNotBeNull("attaching a subscription arms the month close, in the table the production silo uses");
        armed.Period.ShouldBe(IBillingAccountGrain.MonthCloseTick);

        // ── 6. A new silo over the same storage ──────────────────────────────────────────────────────
        //
        // ⚠ A RESTART, NOT A DEACTIVATION. The first version deactivated the account alone and re-read an
        // invoice with no lines. Below, every activation is gone: the account, the budget, the ledger and
        // the numbering singleton each load their state from PostgreSQL through the durable tier's JSON
        // serializer, the round trip CyberCloud.Billing.Tests' in-memory storage can't make.
        await gateway.StopAsync(ct);
        await silo.StopAsync(ct);
        await gateway.DisposeAsync();
        await silo.DisposeAsync();

        gatewayPort = FreePort();
        silo = await StartSiloAsync(gatewayPort, ct);
        gateway = await StartGatewayAsync(gatewayPort, ct);

        client = gateway.Services.GetRequiredService<IGrainFactory>();
        account = client.ForTenant(tenant.ToString("D", CultureInfo.InvariantCulture)).GetGrain<IBillingAccountGrain>(GrainKeys.Tenant(tenant));

        (await account.GetInvoiceAsync(finalized.Number)).GetValueOrThrow().ShouldBeEquivalentTo(finalized);
        (await account.GetAsync()).GetValueOrThrow().Subscriptions.ShouldBe([subscription]);
        (await account.PreviewAsync(month)).GetValueOrThrow().Lines.ShouldHaveSingleItem().Amount.ShouldBe(1.00m, "the ledger survived too");

        var budget = (await gateway.Services.GetRequiredService<IBudgetControlPlane>().GetAsync(tenant, budgetId, ct)).GetValueOrThrow();
        budget.Actual.ShouldBe(1.00m);
        budget.Spec.Thresholds.ShouldHaveSingleItem().Percent.ShouldBe(50m);
        budget.Alerts.ShouldHaveSingleItem().ShouldBeEquivalentTo(held.Alerts.Single());

        var numbering = client.GetGrain<IInvoiceNumberingGrain>(GrainKeys.PlatformSingleton(GrainKeys.InvoiceNumberingSingleton));
        var audit = (await numbering.AuditAsync("cc-hosts-test", DocumentSeries.Invoice)).GetValueOrThrow();
        audit.Allocated.ShouldBe(1, "one invoice, one number, and the counter kept it");
        audit.Unconfirmed.ShouldBeEmpty();

        await gateway.StopAsync(ct);
        await silo.StopAsync(ct);
        await gateway.DisposeAsync();
        await silo.DisposeAsync();
    }

    /// <summary>Starts the real silo host over this test's Redis and its two PostgreSQL shards.</summary>
    /// <param name="gatewayPort">The port the silo's gateway listens on, and the gateway host dials.</param>
    /// <param name="ct">The test's token.</param>
    async Task<WebApplication> StartSiloAsync(int gatewayPort, CancellationToken ct) {
        const string storage = ServiceDefaults.Storage.CyberCloudStorageOptions.SectionName;

        var silo = await SiloComposition.BuildAsync(
            [
                "--environment", "Development",
                "--urls", "http://127.0.0.1:0",
                $"--{CyberCloudClusterOptions.SectionName}:LocalhostSiloPort={FreePort()}",
                $"--{CyberCloudClusterOptions.SectionName}:LocalhostGatewayPort={gatewayPort}",
                $"--{storage}:Hot:ConnectionString={redis.GetConnectionString()}",
                $"--{storage}:Durable:Shards:{TenantShard}={tenantShard.GetConnectionString()}",
                $"--{storage}:Durable:Shards:{PlatformShard}={platformShard.GetConnectionString()}",
                $"--{storage}:Durable:NullTenantShard={PlatformShard}",
                $"--{storage}:Durable:BootstrapShard={TenantShard}",
                // The issuer a deployment configures — no default exists, by design (BillingOptions).
                "--CyberCloud:Billing:Issuer:Code=cc-hosts-test",
                "--CyberCloud:Billing:Issuer:LegalName=Cyber Cloud Hosts Test s.r.o.",
                "--CyberCloud:Billing:Issuer:Country=CZ",
                "--CyberCloud:Billing:Issuer:VatId=CZ00000001",
                "--CyberCloud:Billing:Issuer:NumberPrefix=HT"
            ]
        );

        await silo.StartAsync(ct);
        return silo;
    }

    /// <summary>Starts the real gateway host, its cluster client dialing <paramref name="gatewayPort" />.</summary>
    /// <param name="gatewayPort">The silo's gateway port.</param>
    /// <param name="ct">The test's token.</param>
    static async Task<WebApplication> StartGatewayAsync(int gatewayPort, CancellationToken ct) {
        var gateway = await GatewayComposition.BuildAsync(
            [
                "--environment", "Development",
                "--urls", "http://127.0.0.1:0",
                $"--{CyberCloudClusterOptions.SectionName}:LocalhostGatewayPort={gatewayPort}",
                "--CyberCloud:Gateway:Identity:Issuer=http://127.0.0.1:1"
            ]
        );

        await gateway.StartAsync(ct);
        return gateway;
    }

    static PostgreSqlContainer Shard() =>
        new PostgreSqlBuilder("postgres:17-alpine").WithDatabase("cybercloud").WithUsername("cybercloud").WithPassword("cybercloud").Build();

    static int FreePort() {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }
}
