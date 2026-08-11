using CyberCloud.Communication.Webhooks;
using CyberCloud.Core.Contracts;
using CyberCloud.Core.Time;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Multitenant;
using Orleans.TestingHost;
using System.Collections.Immutable;
using System.Globalization;

namespace CyberCloud.Communication.Tests.Infrastructure;

/// <summary>The clock the silo reads, shared with the test so it can be advanced.</summary>
/// <remarks>
///     ⚠ Static for the same reason <c>CyberCloud.Metering.Tests</c>' is: the silo runs in this
///     process but resolves its own services, and several properties under test here are caused by
///     <i>time passing</i> — a spend window turning over, a reservation lease expiring, a message
///     record ageing past its retention. There is nothing to call to make that happen.
/// </remarks>
public sealed class TestClock : IClock {
    /// <summary>The one instance the silo resolves.</summary>
    public static TestClock Instance { get; } = new();

    /// <inheritdoc />
    public DateTimeOffset UtcNow { get; private set; } = Start;

    /// <summary>
    ///     ⚠ Deliberately mid-afternoon rather than at midnight. A start time sitting on a UTC day
    ///     boundary would make the spend window's rollover ambiguous — the one arithmetic fact the
    ///     "a limit resets at the next UTC midnight" assertion rests on.
    /// </summary>
    public static DateTimeOffset Start { get; } = new(2026, 8, 11, 14, 22, 09, TimeSpan.Zero);

    /// <summary>Moves time forward.</summary>
    /// <param name="by">How far.</param>
    public void Advance(TimeSpan by) => UtcNow += by;

    /// <summary>Puts time back to the start of a test.</summary>
    public void Reset() => UtcNow = Start;
}

/// <summary>The in-memory providers the silo resolves, reachable from a test.</summary>
/// <remarks>
///     One per channel, because a test that asserts "the provider recorded zero calls" has to be
///     asking the provider the send would actually have reached.
/// </remarks>
public static class TestProviders {
    /// <summary>The SMS double.</summary>
    public static InMemoryChannelProvider Sms { get; } = new(ChannelKind.Sms);

    /// <summary>The email double.</summary>
    public static InMemoryChannelProvider Email { get; } = new(ChannelKind.Email);

    /// <summary>The WhatsApp double.</summary>
    public static InMemoryChannelProvider WhatsApp { get; } = new(ChannelKind.WhatsApp);

    /// <summary>Forgets everything on all three.</summary>
    public static void Reset() {
        Sms.Reset();
        Email.Reset();
        WhatsApp.Reset();
    }

    /// <summary>The double serving a channel.</summary>
    /// <param name="channel">The channel.</param>
    public static InMemoryChannelProvider For(ChannelKind channel) =>
        channel switch {
            ChannelKind.Sms => Sms,
            ChannelKind.Email => Email,
            ChannelKind.WhatsApp => WhatsApp,
            _ => throw new ArgumentOutOfRangeException(nameof(channel))
        };
}

/// <summary>
///     An in-process Orleans cluster with the sending domain wired as production wires it, plus the
///     in-memory channel providers standing in for carriers.
/// </summary>
/// <remarks>
///     ⚠ <b>In-memory storage, which is a deviation from ADR-018.</b> See this project's
///     <c>.csproj</c> for exactly what that costs and what is owed. Everything else — the seven
///     grains, the registry, the router, the sender — is the production wiring, reached through the
///     same <c>AddCyberCloudCommunication</c> a silo host would call.
/// </remarks>
public sealed class CommunicationCluster : IAsyncLifetime {
    TestCluster cluster = null!;

    /// <summary>The tenant every test sends from.</summary>
    public static Guid Tenant { get; } = Guid.Parse("11111111-1111-4111-8111-111111111111");

    /// <summary>A second tenant, for anything that must not cross.</summary>
    public static Guid OtherTenant { get; } = Guid.Parse("22222222-2222-4222-8222-222222222222");

    /// <summary>The cluster's grain factory. ⚠ Tenant-unaware — a client, in the filter's terms.</summary>
    public IGrainFactory Grains => cluster.GrainFactory;

    /// <summary>The sender, constructed on the <b>client</b> side, as a gateway or a host holds it.</summary>
    public IMessageSender Sender { get; private set; } = null!;

    /// <summary>The webhook router, likewise client-side — a carrier callback arrives at a gateway.</summary>
    public IWebhookRouter Router { get; private set; } = null!;

    /// <summary>A tenant-qualified grain factory.</summary>
    /// <param name="tenant">The tenant.</param>
    public TenantGrainFactory For(Guid tenant) =>
        Grains.ForTenant(tenant.ToString("D", CultureInfo.InvariantCulture));

    /// <summary>The service resource grain.</summary>
    /// <param name="service">The service's resource id.</param>
    /// <param name="tenant">The tenant, defaulting to <see cref="Tenant" />.</param>
    public ICommunicationServiceGrain Service(Guid service, Guid? tenant = null) =>
        For(tenant ?? Tenant).GetGrain<ICommunicationServiceGrain>(CommunicationGrainKeys.Service(service));

    /// <summary>The suppression list for a service.</summary>
    /// <param name="service">The service's resource id.</param>
    /// <param name="tenant">The tenant, defaulting to <see cref="Tenant" />.</param>
    public ISuppressionListGrain Suppression(Guid service, Guid? tenant = null) =>
        For(tenant ?? Tenant).GetGrain<ISuppressionListGrain>(CommunicationGrainKeys.Service(service));

    /// <summary>The spend counters for a service.</summary>
    /// <param name="service">The service's resource id.</param>
    /// <param name="tenant">The tenant, defaulting to <see cref="Tenant" />.</param>
    public ISendLimitGrain Limits(Guid service, Guid? tenant = null) =>
        For(tenant ?? Tenant).GetGrain<ISendLimitGrain>(CommunicationGrainKeys.Service(service));

    /// <summary>A message, by the key it was sent under.</summary>
    /// <param name="service">The service's resource id.</param>
    /// <param name="idempotencyKey">The key.</param>
    /// <param name="tenant">The tenant, defaulting to <see cref="Tenant" />.</param>
    public IMessageGrain Message(Guid service, string idempotencyKey, Guid? tenant = null) =>
        For(tenant ?? Tenant).GetGrain<IMessageGrain>(CommunicationGrainKeys.Message(service, idempotencyKey));

    /// <summary>A template resource grain.</summary>
    /// <param name="template">The template's resource id.</param>
    /// <param name="tenant">The tenant, defaulting to <see cref="Tenant" />.</param>
    public IMessageTemplateGrain Template(Guid template, Guid? tenant = null) =>
        For(tenant ?? Tenant).GetGrain<IMessageTemplateGrain>(CommunicationGrainKeys.Template(template));

    /// <summary>A sender resource grain.</summary>
    /// <param name="sender">The sender's resource id.</param>
    /// <param name="tenant">The tenant, defaulting to <see cref="Tenant" />.</param>
    public ISenderIdentityGrain SenderIdentity(Guid sender, Guid? tenant = null) =>
        For(tenant ?? Tenant).GetGrain<ISenderIdentityGrain>(CommunicationGrainKeys.Sender(sender));

    /// <summary>
    ///     The running test's cancellation token, so every awaited seam call is cancellable and
    ///     xUnit1051 stays satisfied — the same helper <c>CyberCloud.Sdk.Tests.Cancel</c> is.
    /// </summary>
    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Sends through <see cref="IMessageSender" /> — the seam identity, monitor and billing hold.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="request">What to send.</param>
    /// <remarks>
    ///     ⚠ A thin wrapper rather than a replacement: the tests exercise the real
    ///     <see cref="IMessageSender" />, and this exists only so the runner's token is supplied in
    ///     one place instead of at two hundred call sites.
    /// </remarks>
    public Task<Result<MessageSnapshot>> SendAsync(Guid tenant, SendRequest request) =>
        Sender.SendAsync(tenant, request, Ct);

    /// <summary>Reads a message's status through <see cref="IMessageSender" />.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="service">The service it was sent through.</param>
    /// <param name="idempotencyKey">The key it was sent under.</param>
    public Task<Result<MessageSnapshot>> StatusAsync(Guid tenant, Guid service, string idempotencyKey) =>
        Sender.GetStatusAsync(tenant, service, idempotencyKey, Ct);

    /// <summary>Routes one inbound message through <see cref="IWebhookRouter" />.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="inbound">The message.</param>
    public Task<Result<InboundOutcome>> InboundAsync(Guid tenant, InboundMessage inbound) =>
        Router.HandleInboundAsync(tenant, inbound, Ct);

    /// <summary>Routes one delivery receipt through <see cref="IWebhookRouter" />.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="service">The service the receipt arrived on.</param>
    /// <param name="receipt">The carrier's statement.</param>
    public Task<Result<bool>> ReceiptAsync(Guid tenant, Guid service, DeliveryReceipt receipt) =>
        Router.HandleReceiptAsync(tenant, service, receipt, Ct);

    /// <summary>Puts every double back to its default. Called at the top of every test.</summary>
    public static void ResetDoubles() {
        TestProviders.Reset();
        TestClock.Instance.Reset();
    }

    /// <summary>
    ///     Creates a service with one channel configured, which is the setup nearly every test needs.
    /// </summary>
    /// <param name="channel">Which channel.</param>
    /// <param name="messagesPerWindow">The message cap. Generous by default so a test asserting something else does not trip it.</param>
    /// <param name="spendPerWindow">The spend cap.</param>
    /// <param name="unitCost">What each message is estimated to cost.</param>
    /// <param name="senderId">A registered sender to require, or <see cref="Guid.Empty" />.</param>
    /// <param name="tenant">The tenant, defaulting to <see cref="Tenant" />.</param>
    /// <returns>The service's resource id. ⚠ Fresh per call, so tests never share grain state.</returns>
    public async Task<Guid> NewServiceAsync(
        ChannelKind channel = ChannelKind.Sms,
        long messagesPerWindow = 1000,
        decimal spendPerWindow = 1000m,
        decimal unitCost = 0.05m,
        Guid senderId = default,
        Guid? tenant = null
    ) {
        var serviceId = Guid.NewGuid();
        var service = Service(serviceId, tenant);

        (await service.CreateAsync(tenant ?? Tenant, "primary")).IsSuccess.ShouldBeTrue();

        (await service.ConfigureChannelAsync(
            new() {
                Channel = channel,
                Provider = "in-memory",
                Credentials = new() { Mode = CredentialMode.PlatformAccount },
                Limits = new() {
                    MaxMessagesPerWindow = messagesPerWindow,
                    MaxSpendPerWindow = spendPerWindow,
                    Currency = "EUR"
                },
                EstimatedUnitCost = unitCost,
                Enabled = true,
                SenderId = senderId
            }
        )).IsSuccess.ShouldBeTrue();

        return serviceId;
    }

    /// <summary>A plain free-text send request.</summary>
    /// <param name="serviceId">The service.</param>
    /// <param name="idempotencyKey">The caller's key.</param>
    /// <param name="destination">Where it goes.</param>
    /// <param name="channel">Which channel.</param>
    public static SendRequest Request(
        Guid serviceId,
        string idempotencyKey,
        string destination = "+420777123456",
        ChannelKind channel = ChannelKind.Sms
    ) =>
        new() {
            ServiceId = serviceId,
            Channel = channel,
            Destination = destination,
            Body = "Your code is 424242.",
            IdempotencyKey = idempotencyKey
        };

    /// <inheritdoc />
    public async ValueTask InitializeAsync() {
        var builder = new TestClusterBuilder(1);
        builder.AddSiloBuilderConfigurator<SiloConfigurator>();
        cluster = builder.Build();
        await cluster.DeployAsync();

        // ⚠ Built on the CLIENT side, which is the faithful shape. IMessageSender is held by a
        // gateway or by identity's OTP path, and IWebhookRouter by whatever terminates a carrier
        // callback — neither is a grain, which is what CC1006 is about.
        Sender = new GrainMessageSender(cluster.GrainFactory);
        Router = new WebhookRouter(
            new ChannelProviderRegistry([TestProviders.Sms, TestProviders.Email, TestProviders.WhatsApp]),
            cluster.GrainFactory,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<WebhookRouter>.Instance
        );
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() {
        if (cluster is not null) {
            await cluster.StopAllSilosAsync();
            await cluster.DisposeAsync();
        }
    }

    sealed class SiloConfigurator : ISiloConfigurator {
        public void Configure(ISiloBuilder silo) {
            silo.AddMemoryGrainStorage(StorageTiers.Durable);
            silo.AddMemoryGrainStorage(StorageTiers.Hot);

            silo.ConfigureServices(services => {
                    // The doubles go in FIRST so the production wiring's TryAdd keeps them.
                    services.AddSingleton<IClock>(TestClock.Instance);

                    // ⚠ Registered BESIDE the refusing seams rather than instead of them, which is
                    // the arrangement a real carrier would land in — see
                    // CommunicationSiloBuilderExtensions. Every configuration in these tests names
                    // "in-memory" explicitly, so the registry resolves by name and the refusing
                    // seams stay reachable for the test that wants one.
                    services.AddSingleton<IChannelProvider>(TestProviders.Sms);
                    services.AddSingleton<IChannelProvider>(TestProviders.Email);
                    services.AddSingleton<IChannelProvider>(TestProviders.WhatsApp);
                }
            );

            silo.AddCyberCloudCommunication();
        }
    }
}

/// <summary>
///     One cluster for the whole assembly. Deploying a silo per class costs seconds each, and no
///     test here shares state that survives <see cref="CommunicationCluster.ResetDoubles" /> —
///     grains are kept apart by a fresh service GUID per test.
/// </summary>
[CollectionDefinition(Name)]
public sealed class CommunicationClusterFixture : ICollectionFixture<CommunicationCluster> {
    /// <summary>The collection's name.</summary>
    public const string Name = "communication";
}

/// <summary>Small helpers the failure-class suites share.</summary>
public static class TestData {
    /// <summary>A template version with one required parameter.</summary>
    /// <param name="channel">Which channel it is written for.</param>
    public static ImmutableArray<TemplateParameter> OtpParameters { get; } = [
        new() { Name = "code", Required = true }
    ];

    /// <summary>The bodies for <see cref="OtpParameters" />.</summary>
    public static ImmutableArray<LocalizedBody> OtpBodies { get; } = [
        new() { Locale = "en-US", Subject = "Your code", Body = "Your code is {code}." }
    ];
}
