using CyberCloud.Authorization;
using CyberCloud.Authorization.Contracts;
using CyberCloud.Billing.Grains;
using CyberCloud.Communication;
using CyberCloud.Communication.Contracts;
using CyberCloud.Communication.Providers;
using CyberCloud.Core.Contracts;
using CyberCloud.Core.Time;
using CyberCloud.Metering;
using CyberCloud.Tenancy.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Multitenant;
using Orleans.Storage;
using Orleans.TestingHost;
using System.Globalization;

namespace CyberCloud.Billing.Tests.Infrastructure;

/// <summary>The clock the silo reads, shared with the test so it can be advanced.</summary>
/// <remarks>
///     ⚠ Static, for the reason <c>CyberCloud.Metering.Tests</c>' is: every property here is caused by
///     time passing — a month closing, the 48-hour window running out, a budget period turning over.
///     Tests that move it restore it, and none depends on another's position.
/// </remarks>
public sealed class TestClock : IClock {
    /// <summary>The one instance the silo resolves.</summary>
    public static TestClock Instance { get; } = new();

    /// <summary>
    ///     Mid-September 2026: August is closed and past its late-usage window, September is open.
    ///     ⚠ Not on an hour boundary, so a period truncation that forgot to truncate shows.
    /// </summary>
    public static DateTimeOffset Start { get; } = new(2026, 9, 10, 12, 17, 43, TimeSpan.Zero);

    /// <inheritdoc />
    public DateTimeOffset UtcNow { get; private set; } = Start;

    /// <summary>Sets the time.</summary>
    /// <param name="at">The instant.</param>
    public void Set(DateTimeOffset at) => UtcNow = at;

    /// <summary>Back to <see cref="Start" />.</summary>
    public void Reset() => UtcNow = Start;
}

/// <summary>The one carrier the sending module has in-process, reachable from a test.</summary>
public static class Carriers {
    /// <summary>Email, the channel every budget here notifies on.</summary>
    public static InMemoryChannelProvider Email { get; } = new(ChannelKind.Email);
}

/// <summary>
///     An in-process silo with billing wired as <c>CyberCloud.Silo.Host</c> wires it, over the real
///     usage ledger, the real ReBAC engine, the real subscription grain and the real sending module.
/// </summary>
public sealed class BillingCluster : IAsyncLifetime {
    TestCluster cluster = null!;

    /// <summary>The issuer every invoice here is issued by — Czech, so a German business is cross-border.</summary>
    public static InvoiceIssuer Issuer { get; } = new() {
        Code = "cc-test",
        LegalName = "Cyber Cloud Test Issuer s.r.o.",
        Country = "CZ",
        VatId = "CZ12345678",
        NumberPrefix = "CCT"
    };

    /// <summary>The silo's own container — where its <c>IReminderTable</c> is a singleton.</summary>
    public IServiceProvider SiloServices => ((InProcessSiloHandle)cluster.Primary).SiloHost.Services;

    /// <summary>The Durable tier the silo writes to, with a write that can be made to fail.</summary>
    public FaultInjectingGrainStorage Durable =>
        (FaultInjectingGrainStorage)SiloServices.GetRequiredKeyedService<IGrainStorage>(StorageTiers.Durable);

    /// <summary>The cluster's grain factory. ⚠ Tenant-unaware — a client.</summary>
    public IGrainFactory Grains => cluster.GrainFactory;

    /// <summary>The cost query, built on the client side as the gateway builds it.</summary>
    public ICostQuery Costs { get; private set; } = null!;

    /// <summary>The budget control plane, built on the client side as a reconciler's host builds it.</summary>
    public IBudgetControlPlane Budgets { get; private set; } = null!;

    /// <summary>The invoice reader the gateway's dispatch stage holds, over this cluster's client.</summary>
    public IInvoiceReader Invoices { get; private set; } = null!;

    /// <summary>A tenant-qualified factory.</summary>
    /// <param name="tenant">The tenant.</param>
    public TenantGrainFactory For(Guid tenant) => Grains.ForTenant(tenant.ToString("D", CultureInfo.InvariantCulture));

    /// <summary>A tenant's billing account.</summary>
    /// <param name="tenant">The tenant.</param>
    public IBillingAccountGrain Account(Guid tenant) => For(tenant).GetGrain<IBillingAccountGrain>(GrainKeys.Tenant(tenant));

    /// <summary>A subscription's usage ledger — the real one.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="subscription">The subscription.</param>
    public IUsageLedgerGrain Ledger(Guid tenant, Guid subscription) =>
        For(tenant).GetGrain<IUsageLedgerGrain>(GrainKeys.Subscription(subscription));

    /// <summary>The numbering singleton.</summary>
    public IInvoiceNumberingGrain Numbering =>
        Grains.GetGrain<IInvoiceNumberingGrain>(GrainKeys.PlatformSingleton(GrainKeys.InvoiceNumberingSingleton));

    /// <summary>A fresh tenant, subscription and group, created through the real tenancy grains.</summary>
    /// <param name="groups">The resource groups to create in the subscription.</param>
    public async Task<(Guid Tenant, Guid Subscription)> NewSubscriptionAsync(params string[] groups) {
        var tenant = Guid.NewGuid();
        var subscription = Guid.NewGuid();

        var grain = For(tenant).GetGrain<ISubscriptionGrain>(GrainKeys.Subscription(subscription));
        var created = await grain.CreateAsync("billing");
        created.IsSuccess.ShouldBeTrue(created.Error?.Message);

        // Through the subscription, which records the group — the list a cost query reads to tell a
        // reader of an idle group from a stranger.
        foreach (var group in groups) {
            var made = await grain.CreateResourceGroupAsync(group, "eu-central");
            made.IsSuccess.ShouldBeTrue(made.Error?.Message);
        }

        return (tenant, subscription);
    }

    /// <summary>A resource's path in the test subscription.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="subscription">The subscription.</param>
    /// <param name="group">The group.</param>
    /// <param name="name">The resource's name.</param>
    /// <param name="type">Its type, <c>{namespace}/{type}</c>.</param>
    public static string PathOf(Guid tenant, Guid subscription, string group, string name, string type = "CyberCloud.Sample/widgets") =>
        $"/tenants/{tenant:D}/subscriptions/{subscription:D}/resourceGroups/{group}/providers/{type}/{name}";

    /// <summary>Appends one hour of usage to the real ledger.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="subscription">The subscription.</param>
    /// <param name="resource">The resource.</param>
    /// <param name="path">Its path.</param>
    /// <param name="meter">The meter.</param>
    /// <param name="hour">The hour's start.</param>
    /// <param name="quantity">The quantity.</param>
    public async Task<UsageLedgerEntry> UseAsync(
        Guid tenant,
        Guid subscription,
        Guid resource,
        string path,
        BillingMeter meter,
        DateTimeOffset hour,
        decimal quantity
    ) {
        var appended = await Ledger(tenant, subscription)
            .AppendAsync(
                new() {
                    TenantId = tenant,
                    ResourceId = resource,
                    ResourcePath = path,
                    Meter = meter,
                    Region = "eu-central",
                    WindowStart = hour,
                    WindowEnd = hour.AddHours(1),
                    Quantity = quantity,
                    SampleCount = 12
                }
            );

        appended.IsSuccess.ShouldBeTrue(appended.Error?.Message);
        return appended.GetValueOrThrow();
    }

    /// <summary>Appends a run of consecutive hours.</summary>
    public async Task UseHoursAsync(
        Guid tenant,
        Guid subscription,
        Guid resource,
        string path,
        BillingMeter meter,
        DateTimeOffset firstHour,
        int hours,
        decimal perHour
    ) {
        for (var i = 0; i < hours; i++) {
            await UseAsync(tenant, subscription, resource, path, meter, firstHour.AddHours(i), perHour);
        }
    }

    /// <summary>Writes one ReBAC tuple through the real tuple store.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="on">The object.</param>
    /// <param name="relation">The relation — <c>reader</c>, <c>owner</c>.</param>
    /// <param name="subject">The subject.</param>
    public async Task GrantAsync(Guid tenant, ObjectRef on, string relation, SubjectRef subject) {
        var tuple = RelationTuple.Create(on, relation, subject);
        tuple.IsSuccess.ShouldBeTrue(tuple.Error?.Message);

        var written = await For(tenant).GetGrain<ITupleStoreGrain>(GrainKeys.TupleStore(tenant)).WriteAsync(tuple.GetValueOrThrow());
        written.IsSuccess.ShouldBeTrue(written.Error?.Message);
    }

    /// <summary>Deletes one ReBAC tuple <see cref="GrantAsync" /> wrote.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="on">The object.</param>
    /// <param name="relation">The relation.</param>
    /// <param name="subject">The subject.</param>
    public async Task RevokeAsync(Guid tenant, ObjectRef on, string relation, SubjectRef subject) {
        var tuple = RelationTuple.Create(on, relation, subject);
        tuple.IsSuccess.ShouldBeTrue(tuple.Error?.Message);

        var deleted = await For(tenant).GetGrain<ITupleStoreGrain>(GrainKeys.TupleStore(tenant)).DeleteAsync(tuple.GetValueOrThrow());
        deleted.IsSuccess.ShouldBeTrue(deleted.Error?.Message);
    }

    /// <summary>A sending service with an email channel on the in-memory carrier, as a tenant's Communication resources would configure it.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="subscription">The subscription.</param>
    /// <param name="name">The service's name.</param>
    /// <param name="group">The resource group it's in.</param>
    /// <returns>The service's path and its grain id.</returns>
    public async Task<(string Path, Guid Id)> SendingServiceAsync(Guid tenant, Guid subscription, string name, string group = "prod") {
        var path = PathOf(tenant, subscription, group, name, "CyberCloud.Communication/services");
        ResourceId.TryParsePath(path, out var service).ShouldBeTrue();
        var id = CommunicationGrainKeys.ResourceIdFor(tenant, service.CanonicalPath);

        var services = new ServiceCollection().AddSingleton(Grains).AddCyberCloudCommunicationClient().BuildServiceProvider();
        var plane = services.GetRequiredService<ICommunicationControlPlane>();

        (await plane.EnsureServiceAsync(tenant, id, name, "en", CancellationToken.None)).IsSuccess.ShouldBeTrue();

        var configured = await plane.ConfigureChannelAsync(
            tenant,
            id,
            new() {
                Channel = ChannelKind.Email,
                Provider = "in-memory",
                Enabled = true,
                Credentials = new() { Mode = CredentialMode.PlatformAccount },
                Limits = new() { MaxMessagesPerWindow = 1000, MaxSpendPerWindow = 1000m, Currency = "EUR" },
                EstimatedUnitCost = 0m,
                OwnerResourceId = Guid.NewGuid()
            },
            CancellationToken.None
        );

        configured.IsSuccess.ShouldBeTrue(configured.Error?.Message);
        return (path, id);
    }

    /// <inheritdoc />
    public async ValueTask InitializeAsync() {
        var builder = new TestClusterBuilder(1);
        builder.AddSiloBuilderConfigurator<Configurator>();
        cluster = builder.Build();
        await cluster.DeployAsync();

        var services = new ServiceCollection();
        services.AddSingleton(cluster.GrainFactory);
        services.AddCyberCloudBillingClient();

        var provider = services.BuildServiceProvider();
        Costs = provider.GetRequiredService<ICostQuery>();
        Budgets = provider.GetRequiredService<IBudgetControlPlane>();
        Invoices = provider.GetRequiredService<IInvoiceReader>();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() {
        if (cluster is not null) {
            await cluster.StopAllSilosAsync();
            await cluster.DisposeAsync();
        }
    }

    sealed class Configurator : ISiloConfigurator {
        public void Configure(ISiloBuilder silo) {
            silo.AddMemoryGrainStorage(StorageTiers.Durable);
            silo.AddMemoryGrainStorage(StorageTiers.Hot);
            silo.UseInMemoryReminderService();

            silo.ConfigureServices(static services => {
                    services.AddSingleton<IClock>(TestClock.Instance);
                    services.AddSingleton<IChannelProvider>(Carriers.Email);
                    FaultInjectingGrainStorage.Decorate(services, StorageTiers.Durable);
                }
            );

            // Every module the way the silo host composes it.
            silo.AddCyberCloudMetering();
            silo.AddCyberCloudAuthorization();
            silo.AddCyberCloudCommunication();
            silo.AddCyberCloudBilling(new() { Issuer = Issuer });
        }
    }
}

/// <summary>One silo for the whole assembly; tests keep apart by using a fresh tenant each.</summary>
[CollectionDefinition(Name)]
public sealed class BillingClusterFixture : ICollectionFixture<BillingCluster> {
    /// <summary>The collection's name.</summary>
    public const string Name = "billing";
}
