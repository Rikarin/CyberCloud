using CyberCloud.Communication;
using CyberCloud.Communication.Providers;
using CyberCloud.Communication.Webhooks;
using CyberCloud.Core.Time;
using CyberCloud.ResourceManager.Conformance;
using CyberCloud.ResourceManager.Reconcile;
using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;
using System.Globalization;
using System.Text.Json;

namespace CyberCloud.Providers.Communication.Tests;

/// <summary>The in-memory carriers the silo resolves, reachable from a test.</summary>
/// <remarks>
///     ⚠ Static for the reason <c>CyberCloud.Communication.Tests</c>' are: Orleans constructs a silo
///     configurator with <c>new()</c>, so the silo's container and the test can share an instance
///     only through one. A test that asserts "the carrier was never called" has to ask the carrier
///     the send would have reached.
/// </remarks>
public static class Carriers {
    /// <summary>The email double.</summary>
    public static InMemoryChannelProvider Email { get; } = new(ChannelKind.Email);

    /// <summary>The SMS double.</summary>
    public static InMemoryChannelProvider Sms { get; } = new(ChannelKind.Sms);

    /// <summary>Forgets everything on both.</summary>
    public static void Reset() {
        Email.Reset();
        Sms.Reset();
    }
}

/// <summary>
///     The one collection every silo-backed test class here joins, so they share a silo and run
///     one at a time.
/// </summary>
/// <remarks>
///     ⚠ <b>One at a time, because <see cref="Carriers" /> is static.</b> xUnit runs test classes in
///     parallel; two classes each with their own silo would both register the same two carrier
///     doubles, and a "the carrier was called once" assertion in one class would count the other
///     class's send. A collection fixture is what makes the count mean what it says.
/// </remarks>
[CollectionDefinition(Name)]
public sealed class CommunicationSilo : ICollectionFixture<CommunicationTestCluster> {
    /// <summary>The collection's name.</summary>
    public const string Name = "communication silo";
}

/// <summary>
///     An in-process silo with the sending domain wired as production wires it, the two in-memory
///     carriers beside the refusing seams, and the provider's reconcilers built over the module's
///     own client-side seam.
/// </summary>
public sealed class CommunicationTestCluster : IAsyncLifetime {
    TestCluster cluster = null!;

    /// <summary>The tenant every test works in.</summary>
    public static Guid Tenant { get; } = Guid.Parse("11111111-1111-4111-8111-111111111111");

    /// <summary>A second tenant, for anything that must not cross.</summary>
    public static Guid OtherTenant { get; } = Guid.Parse("22222222-2222-4222-8222-222222222222");

    /// <summary>The subscription every address sits in.</summary>
    public static Guid Subscription { get; } = Guid.Parse("33333333-3333-4333-8333-333333333333");

    /// <summary>The running test's cancellation token.</summary>
    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The clock the silo reads and the reconcilers stamp with.</summary>
    public static TestClock Clock { get; } = new();

    /// <summary>The module's control plane, client-side — what a reconciler holds.</summary>
    public ICommunicationControlPlane Plane { get; private set; } = null!;

    /// <summary>The module's data plane, client-side — what identity holds.</summary>
    public IMessageSender Sender { get; private set; } = null!;

    /// <summary>The webhook router, client-side — what a carrier callback reaches.</summary>
    public IWebhookRouter Router { get; private set; } = null!;

    /// <summary>The grain factory, for anything a test reads around the seams.</summary>
    public IGrainFactory Grains => cluster.GrainFactory;

    /// <inheritdoc />
    public async ValueTask InitializeAsync() {
        var builder = new TestClusterBuilder(1);
        builder.AddSiloBuilderConfigurator<Configurator>();
        cluster = builder.Build();
        await cluster.DeployAsync();

        var services = new ServiceCollection();
        services.AddSingleton(cluster.GrainFactory);
        services.AddCyberCloudCommunicationClient();

        var provider = services.BuildServiceProvider();
        Plane = provider.GetRequiredService<ICommunicationControlPlane>();
        Sender = provider.GetRequiredService<IMessageSender>();

        Router = new WebhookRouter(
            new ChannelProviderRegistry([Carriers.Email, Carriers.Sms]),
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

        GC.SuppressFinalize(this);
    }

    // ── Addresses ──────────────────────────────────────────────────────────────────────────────

    /// <summary>A <c>services/{name}</c> address in the test tenant.</summary>
    public static ResourceId Service(string name, Guid? tenant = null) =>
        new(tenant ?? Tenant, Subscription, "prod", CommunicationServices.Type, name, Guid.NewGuid());

    /// <summary>A child address under a service.</summary>
    public static ResourceId Child(ResourceTypeName type, string service, string name, Guid? tenant = null) =>
        new(tenant ?? Tenant, Subscription, "prod", type, name, Guid.NewGuid(), service);

    // ── Reconcilers, driven directly ───────────────────────────────────────────────────────────

    /// <summary>Runs one reconcile pass of the type's reconciler over a body.</summary>
    /// <param name="id">The resource.</param>
    /// <param name="body">Its desired body, as JSON text.</param>
    public async Task<ReconcileOutcome> ReconcileAsync(ResourceId id, string body) {
        using var desired = JsonDocument.Parse(body);
        return await ReconcilerFor(id.Type).ReconcileAsync(Context(id, desired.RootElement), Ct);
    }

    /// <summary>Runs the type's delete pass over a body.</summary>
    /// <param name="id">The resource.</param>
    /// <param name="body">Its desired body, as JSON text.</param>
    public async Task<ReconcileOutcome> DeleteAsync(ResourceId id, string body) {
        using var desired = JsonDocument.Parse(body);
        return await ReconcilerFor(id.Type).DeleteAsync(Context(id, desired.RootElement), Ct);
    }

    /// <summary>Runs the type's observation over a body.</summary>
    /// <param name="id">The resource.</param>
    /// <param name="body">Its desired body, as JSON text.</param>
    public async Task<ObservedState> ObserveAsync(ResourceId id, string body) {
        using var desired = JsonDocument.Parse(body);
        return await ReconcilerFor(id.Type).ObserveAsync(new(id, CommunicationServices.V2026, desired.RootElement, string.Empty, null), Ct);
    }

    /// <summary>Converges a service and returns its address, so a test can hang children off it.</summary>
    /// <param name="name">The service's name.</param>
    public async Task<ResourceId> ConvergedServiceAsync(string name) {
        var id = Service(name);
        var outcome = await ReconcileAsync(id, CommunicationServices.Body());
        outcome.IsConverged.ShouldBeTrue(outcome.ToString());
        return id;
    }

    /// <summary>Converges an in-memory email channel on a service.</summary>
    /// <param name="service">The service's name.</param>
    /// <param name="maxMessagesPerDay">The limit.</param>
    public async Task<ResourceId> ConvergedEmailChannelAsync(string service, long maxMessagesPerDay = 100) {
        var id = Child(CommunicationChannels.Type, service, "email");
        var outcome = await ReconcileAsync(id, CommunicationChannels.Body(kind: "email", provider: "in-memory", maxMessagesPerDay: maxMessagesPerDay));
        outcome.IsConverged.ShouldBeTrue(outcome.ToString());
        return id;
    }

    IResourceReconciler ReconcilerFor(ResourceTypeName type) =>
        type.Type switch {
            CommunicationServices.TypePath => new CommunicationServiceReconciler(Clock, Plane),
            CommunicationChannels.TypePath => new CommunicationChannelReconciler(Clock, Plane),
            CommunicationTemplates.TypePath => new CommunicationTemplateReconciler(Clock, Plane),
            CommunicationSuppressions.TypePath => new CommunicationSuppressionReconciler(Clock, Plane),
            _ => throw new ArgumentOutOfRangeException(nameof(type))
        };

    static ReconcileContext Context(ResourceId id, JsonElement desired) =>
        new(id, CommunicationServices.V2026, desired, null, string.Empty, null, new InMemorySecretVault(), new RecordingLog());

    /// <summary>A send request through a service, as identity would build one.</summary>
    /// <param name="service">The service's address.</param>
    /// <param name="to">The recipient.</param>
    /// <param name="key">The idempotency key.</param>
    /// <param name="channel">The channel.</param>
    public static SendRequest Send(ResourceId service, string to, string key, ChannelKind channel = ChannelKind.Email) =>
        new() {
            ServiceId = CommunicationServices.ServiceIdOf(service),
            Channel = channel,
            Destination = to,
            Body = "Your code is 482913.",
            IdempotencyKey = key
        };

    sealed class Configurator : ISiloConfigurator {
        public void Configure(ISiloBuilder silo) {
            silo.AddMemoryGrainStorage(StorageTiers.Durable);
            silo.AddMemoryGrainStorage(StorageTiers.Hot);
            silo.UseInMemoryReminderService();

            silo.ConfigureServices(services => {
                    services.AddSingleton<IClock>(Clock);
                    // Beside the refusing seams, not instead of them — the registry resolves by
                    // name, and every channel body in these tests says `provider: in-memory`.
                    services.AddSingleton<IChannelProvider>(Carriers.Email);
                    services.AddSingleton<IChannelProvider>(Carriers.Sms);
                }
            );

            silo.AddCyberCloudCommunication();
        }
    }
}

/// <summary>Collects what a pass reported, so a test can assert on it.</summary>
public sealed class RecordingLog : IReconcileLog {
    /// <summary>Every entry, in order.</summary>
    public List<(string Phase, string Detail)> Entries { get; } = [];

    /// <inheritdoc />
    public void Report(string phase, string detail) => Entries.Add((phase, detail));

    /// <inheritdoc />
    public void Report(string phase, string detail, int percentComplete) => Entries.Add((phase, detail));
}

/// <summary>Spells a GUID the way every tenant-qualified call site does.</summary>
public static class Guids {
    /// <summary><c>"D"</c>, invariant.</summary>
    public static string D(this Guid id) => id.ToString("D", CultureInfo.InvariantCulture);
}

/// <summary>The clock the silo reads, shared with the test so it can be advanced.</summary>
public sealed class TestClock : IClock {
    /// <summary>Deliberately mid-afternoon, off every UTC day boundary a spend window rolls on.</summary>
    public static DateTimeOffset Start { get; } = new(2026, 9, 15, 14, 22, 09, TimeSpan.Zero);

    /// <inheritdoc />
    public DateTimeOffset UtcNow { get; private set; } = Start;

    /// <summary>Moves time forward.</summary>
    /// <param name="by">How far.</param>
    public void Advance(TimeSpan by) => UtcNow += by;
}
