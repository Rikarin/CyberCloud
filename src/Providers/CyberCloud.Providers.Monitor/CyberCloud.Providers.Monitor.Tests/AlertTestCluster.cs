using CyberCloud.Communication;
using CyberCloud.Communication.Providers;
using CyberCloud.Core.Time;
using CyberCloud.Providers.Monitor.Alerting;
using CyberCloud.ResourceManager.Conformance;
using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;

namespace CyberCloud.Providers.Monitor.Tests;

/// <summary>
///     The substitute for VictoriaMetrics and ClickHouse: answers each workspace's queries with
///     whatever the test scripted, and records every question it was asked.
/// </summary>
/// <remarks>
///     ⚠ <b>Static for the reason <c>Carriers</c> is.</b> Orleans constructs a silo configurator with
///     <c>new()</c>, so the silo's container and the test share an instance only through one. Keyed
///     by workspace path, because every test here owns a workspace and two tests must not read
///     each other's script.
/// </remarks>
public sealed class ScriptedQuerySeam : IAlertQuerySeam {
    readonly ConcurrentDictionary<string, Func<AlertQuery, Result<AlertQueryResult>>> scripts = new(StringComparer.Ordinal);
    readonly ConcurrentQueue<AlertQuery> asked = new();

    /// <summary>Every query, in the order it arrived.</summary>
    public IReadOnlyCollection<AlertQuery> Asked => asked;

    /// <summary>Makes every query for a workspace answer one value.</summary>
    /// <param name="workspacePath">The workspace's canonical path.</param>
    /// <param name="value">The value.</param>
    public void Answer(string workspacePath, double value) =>
        scripts[workspacePath] = _ => Result<AlertQueryResult>.Success(new() { Samples = [new() { Labels = "{instance=\"web-1\"}", Value = value }] });

    /// <summary>Makes every query for a workspace answer these samples.</summary>
    /// <param name="workspacePath">The workspace's canonical path.</param>
    /// <param name="samples">The samples.</param>
    public void Answer(string workspacePath, ImmutableArray<AlertSample> samples) =>
        scripts[workspacePath] = _ => Result<AlertQueryResult>.Success(new() { Samples = samples });

    /// <summary>Makes every query for a workspace fail, the way a store that is down does.</summary>
    /// <param name="workspacePath">The workspace's canonical path.</param>
    /// <param name="reason">Why.</param>
    public void Fail(string workspacePath, string reason) =>
        scripts[workspacePath] = _ => Result<AlertQueryResult>.Failure(ErrorCode.InternalError, reason);

    /// <inheritdoc />
    public Task<Result<AlertQueryResult>> QueryAsync(AlertQuery query, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(query);
        asked.Enqueue(query);

        return Task.FromResult(
            scripts.TryGetValue(query.WorkspacePath, out var script)
                ? script(query)
                : Result<AlertQueryResult>.Failure(ErrorCode.InternalError, $"nothing is scripted for workspace '{query.WorkspacePath}'")
        );
    }
}

/// <summary>The in-memory carriers the silo resolves, reachable from a test.</summary>
public static class Carriers {
    /// <summary>The email double.</summary>
    public static InMemoryChannelProvider Email { get; } = new(ChannelKind.Email);

    /// <summary>The SMS double.</summary>
    public static InMemoryChannelProvider Sms { get; } = new(ChannelKind.Sms);
}

/// <summary>
///     The one collection every silo-backed test class here joins, so they share a silo and run one
///     at a time — <see cref="Carriers" /> and <see cref="AlertTestCluster.Queries" /> are static.
/// </summary>
[CollectionDefinition(Name)]
public sealed class AlertSilo : ICollectionFixture<AlertTestCluster> {
    /// <summary>The collection's name.</summary>
    public const string Name = "alert silo";
}

/// <summary>
///     An in-process silo with the sending module wired as production wires it, the two in-memory
///     carriers beside the refusing seams, the alert evaluator hosted from this provider's own
///     assembly, and a scripted store in place of VictoriaMetrics.
/// </summary>
public sealed class AlertTestCluster : IAsyncLifetime {
    TestCluster cluster = null!;

    /// <summary>The tenant every test works in.</summary>
    public static Guid Tenant { get; } = Guid.Parse("11111111-1111-4111-8111-111111111111");

    /// <summary>A second tenant, for anything that must not cross.</summary>
    public static Guid OtherTenant { get; } = Guid.Parse("22222222-2222-4222-8222-222222222222");

    /// <summary>The subscription every address sits in.</summary>
    public static Guid Subscription { get; } = Guid.Parse("33333333-3333-4333-8333-333333333333");

    /// <summary>The running test's cancellation token.</summary>
    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The clock the silo reads and the evaluator stamps with.</summary>
    public static TestClock Clock { get; } = new();

    /// <summary>The store's substitute.</summary>
    public static ScriptedQuerySeam Queries { get; } = new();

    /// <summary>The evaluator's seam, client-side — what the reconciler and the handler hold.</summary>
    public IAlertControlPlane Alerts { get; private set; } = null!;

    /// <summary>The sending module's control plane, client-side — what a service is configured through.</summary>
    public ICommunicationControlPlane Communication { get; private set; } = null!;

    /// <inheritdoc />
    public async ValueTask InitializeAsync() {
        var builder = new TestClusterBuilder(1);
        builder.AddSiloBuilderConfigurator<Configurator>();
        cluster = builder.Build();
        await cluster.DeployAsync();

        var services = new ServiceCollection();
        services.AddSingleton(cluster.GrainFactory);
        services.AddCyberCloudCommunicationClient();
        services.AddCyberCloudMonitorAlerting();

        var provider = services.BuildServiceProvider();
        Alerts = provider.GetRequiredService<IAlertControlPlane>();
        Communication = provider.GetRequiredService<ICommunicationControlPlane>();
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

    /// <summary>A <c>workspaces/{name}</c> address in the test tenant.</summary>
    public static ResourceId Workspace(string name, Guid? tenant = null) =>
        new(tenant ?? Tenant, Subscription, "prod", MonitorWorkspaces.Type, name, Guid.NewGuid());

    /// <summary>A <c>workspaces/{workspace}/alertRules/{name}</c> address.</summary>
    public static ResourceId Rule(string workspace, string name, Guid? tenant = null) =>
        new(tenant ?? Tenant, Subscription, "prod", MonitorAlertRules.Type, name, Guid.NewGuid(), workspace);

    /// <summary>The path a body names a sending service by.</summary>
    public static string ServicePath(string name, Guid? tenant = null) =>
        new ResourceId(
            tenant ?? Tenant,
            Subscription,
            "prod",
            new(MonitorAlertRules.ServiceNamespace, MonitorAlertRules.ServiceType),
            name,
            Guid.Empty
        ).Path;

    /// <summary>The grain id the sending module keys a service on, from its path.</summary>
    public static Guid ServiceIdOf(string servicePath) {
        ResourceId.TryParsePath(servicePath, out var service).ShouldBeTrue(servicePath);
        return CommunicationGrainKeys.ResourceIdFor(service.TenantId, service.CanonicalPath);
    }

    // ── The sending side, set up the way a tenant's Communication resources would ──────────────

    /// <summary>Creates a sending service with an in-memory email channel, and returns its path.</summary>
    /// <param name="name">The service's name.</param>
    public async Task<string> SendingServiceAsync(string name) {
        var path = ServicePath(name);
        var serviceId = ServiceIdOf(path);

        (await Communication.EnsureServiceAsync(Tenant, serviceId, name, "en", Ct)).IsSuccess.ShouldBeTrue();

        var configured = await Communication.ConfigureChannelAsync(
            Tenant,
            serviceId,
            new() {
                Channel = ChannelKind.Email,
                Provider = "in-memory",
                Enabled = true,
                Credentials = new() { Mode = CredentialMode.PlatformAccount },
                Limits = new() { MaxMessagesPerWindow = 1000, MaxSpendPerWindow = 1000m, Currency = "EUR" },
                EstimatedUnitCost = 0m,
                OwnerResourceId = Guid.NewGuid()
            },
            Ct
        );

        configured.IsSuccess.ShouldBeTrue(configured.Error?.Message);
        return path;
    }

    // ── The reconciler and the handler, driven directly ───────────────────────────────────────

    /// <summary>Runs one reconcile pass of the rule reconciler over a body.</summary>
    public async Task<ReconcileOutcome> ReconcileAsync(ResourceId id, string body) {
        using var desired = JsonDocument.Parse(body);
        return await Reconciler().ReconcileAsync(Context(id, desired.RootElement), Ct);
    }

    /// <summary>Runs the rule reconciler's delete pass over a body.</summary>
    public async Task<ReconcileOutcome> DeleteAsync(ResourceId id, string body) {
        using var desired = JsonDocument.Parse(body);
        return await Reconciler().DeleteAsync(Context(id, desired.RootElement), Ct);
    }

    /// <summary>Runs the rule reconciler's observation over a body.</summary>
    public async Task<ObservedState> ObserveAsync(ResourceId id, string body) {
        using var desired = JsonDocument.Parse(body);
        return await Reconciler().ObserveAsync(new(id, MonitorWorkspaces.V2026, desired.RootElement, string.Empty, null), Ct);
    }

    /// <summary>Converges a rule and asserts it did.</summary>
    public async Task ConvergedAsync(ResourceId id, string body) {
        var outcome = await ReconcileAsync(id, body);
        outcome.IsConverged.ShouldBeTrue(outcome.ToString());
    }

    /// <summary>Runs one evaluation pass on a rule's workspace.</summary>
    public async Task<AlertEvaluationReport> EvaluateAsync(ResourceId rule) {
        var report = await Alerts.EvaluateAsync(rule.TenantId, MonitorAlertRules.EvaluatorIdFor(rule), Ct);
        report.IsSuccess.ShouldBeTrue(report.Error?.Message);
        return report.GetValueOrThrow();
    }

    /// <summary>The rule as the evaluator holds it.</summary>
    public async Task<AlertRuleSnapshot> HeldAsync(ResourceId rule) {
        var held = await Alerts.GetRuleAsync(rule.TenantId, MonitorAlertRules.EvaluatorIdFor(rule), rule.Id, Ct);
        held.IsSuccess.ShouldBeTrue(held.Error?.Message);
        return held.GetValueOrThrow();
    }

    /// <summary>Runs the <c>listInstances</c> handler over a rule.</summary>
    public Task<Result<string>> ListInstancesAsync(ResourceId rule, string body) {
        using var desired = JsonDocument.Parse(body);
        using var empty = JsonDocument.Parse("{}");

        return new MonitorAlertRuleListInstancesHandler(Alerts).InvokeAsync(
            new(rule, MonitorWorkspaces.V2026, MonitorAlertRules.ListInstancesAction, empty.RootElement, desired.RootElement, string.Empty, null, new InMemorySecretVault()),
            Ct
        );
    }

    MonitorAlertRuleReconciler Reconciler() => new(Clock, Alerts);

    static ReconcileContext Context(ResourceId id, JsonElement desired) =>
        new(id, MonitorWorkspaces.V2026, desired, null, string.Empty, null, new InMemorySecretVault(), new RecordingLog());

    sealed class Configurator : ISiloConfigurator {
        public void Configure(ISiloBuilder silo) {
            silo.AddMemoryGrainStorage(StorageTiers.Durable);
            silo.AddMemoryGrainStorage(StorageTiers.Hot);
            silo.UseInMemoryReminderService();

            silo.ConfigureServices(services => {
                    services.AddSingleton<IClock>(Clock);
                    // Beside the refusing seams, not instead of them — the registry resolves by
                    // name, and every channel here says `provider: in-memory`.
                    services.AddSingleton<IChannelProvider>(Carriers.Email);
                    services.AddSingleton<IChannelProvider>(Carriers.Sms);
                    // Before AddCyberCloudMonitorAlerting, whose TryAdd then keeps this one.
                    services.AddSingleton<IAlertQuerySeam>(Queries);
                    services.AddCyberCloudMonitorAlerting();
                }
            );

            // The sending module, exactly as the silo host hosts it — IMessageSender is what the
            // evaluator delivers through.
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

/// <summary>The clock the silo reads, shared with the test so it can be advanced.</summary>
public sealed class TestClock : IClock {
    /// <summary>Deliberately mid-afternoon.</summary>
    public static DateTimeOffset Start { get; } = new(2026, 9, 15, 14, 22, 09, TimeSpan.Zero);

    /// <inheritdoc />
    public DateTimeOffset UtcNow { get; private set; } = Start;

    /// <summary>Moves time forward.</summary>
    /// <param name="by">How far.</param>
    public void Advance(TimeSpan by) => UtcNow += by;
}

/// <summary>Spells a GUID the way every tenant-qualified call site does.</summary>
public static class Guids {
    /// <summary><c>"D"</c>, invariant.</summary>
    public static string D(this Guid id) => id.ToString("D", CultureInfo.InvariantCulture);
}
