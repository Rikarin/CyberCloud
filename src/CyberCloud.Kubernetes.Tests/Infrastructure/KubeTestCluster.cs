using System.Globalization;
using CyberCloud.Core.Contracts;
using CyberCloud.Core.Resources;
using CyberCloud.Core.Time;
using CyberCloud.Kubernetes.Apply;
using CyberCloud.Kubernetes.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Orleans.Multitenant;
using Orleans.TestingHost;

namespace CyberCloud.Kubernetes.Tests.Infrastructure;

/// <summary>
///     An <see cref="IClusterOperatorAuthority" /> a test can switch on and off — the same device
///     <c>SwitchablePlatformOperatorAuthority</c> is in the tenancy suite.
/// </summary>
public sealed class SwitchableClusterOperatorAuthority : IClusterOperatorAuthority
{
    /// <summary>The operator id to report, or <see langword="null" /> to deny.</summary>
    public static string? Operator { get; set; }

    /// <inheritdoc />
    public string? OperatorFor(Guid clusterId, Guid owningTenantId) => Operator;
}

/// <summary>
///     The clock every grain in the test silo reads, shared with the test so it can be advanced.
/// </summary>
/// <remarks>
///     ⚠ Static because the silo runs in this process but resolves its own services, and the health
///     transition is caused by <i>time passing</i> — there is nothing to call to make it happen. The
///     alternative, waiting 90 seconds, is the test nobody runs. Advancing is monotonic and forward
///     only, so it cannot disturb the tests in this collection that do not read the clock.
/// </remarks>
public sealed class SharedTestClock : IClock
{
    /// <summary>The one instance the silo resolves.</summary>
    public static SharedTestClock Instance { get; } = new();

    /// <inheritdoc />
    public DateTimeOffset UtcNow { get; private set; } = new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Moves time forward.</summary>
    /// <param name="by">How far.</param>
    public void Advance(TimeSpan by) => UtcNow += by;
}

/// <summary>An <see cref="IKubeApiClientFactory" /> that hands out one shared fake.</summary>
public sealed class FakeApiClientFactory : IKubeApiClientFactory
{
    /// <summary>The client every connection gets. Static so the test and the silo share it.</summary>
    public static RecordingApiClient Client { get; } = new();

    /// <inheritdoc />
    public Task<Result<IKubeApiClient>> ConnectAsync(
        ClusterConnectionDescriptor descriptor,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Result<IKubeApiClient>.Success((IKubeApiClient)Client));
}

/// <summary>Captures log lines so a test can assert that an edge was logged.</summary>
public sealed class LogCapture : ILoggerProvider
{
    /// <summary>Every line, as rendered.</summary>
    public static System.Collections.Concurrent.ConcurrentBag<(LogLevel Level, string Message)> Lines { get; } = [];

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new Sink();

    /// <inheritdoc />
    public void Dispose() => GC.SuppressFinalize(this);

    sealed class Sink : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception, string> formatter) =>
            Lines.Add((logLevel, formatter(state, exception!)));
    }
}

/// <summary>
///     A grain that makes a grain call on behalf of a test.
/// </summary>
/// <remarks>
///     ⚠ <b>The same reason <c>IReacherGrain</c> exists in the tenancy suite, and it is load-bearing
///     here.</b> The caller's tenant is read from <c>IGrainCallContext.SourceId</c>, which is
///     <see langword="null" /> for a call made by a client. A test calling the connection grain
///     directly is a client, so it could only ever exercise the <see cref="CallerKind.Client" />
///     path. Routing through a tenant-qualified grain is the only way to put a real tenant on the
///     calling side.
/// </remarks>
[Alias("CyberCloud.Kubernetes.Tests.IKubeReacher")]
public interface IKubeReacherGrain : IGrainWithStringKey
{
    /// <summary>Calls <c>GetHealthAsync</c> on a cluster connection.</summary>
    /// <param name="clusterId">The cluster.</param>
    [Alias("Health")]
    Task<string> ReachHealthAsync(Guid clusterId);

    /// <summary>Calls <c>ApplyAsync</c> with a command labelled for a tenant.</summary>
    /// <param name="clusterId">The cluster.</param>
    /// <param name="command">The command.</param>
    [Alias("Apply")]
    Task<string> ReachApplyAsync(Guid clusterId, KubeCommand command);

    /// <summary>Calls <c>AttachAsync</c>.</summary>
    /// <param name="descriptor">The descriptor.</param>
    [Alias("Attach")]
    Task<string> ReachAttachAsync(ClusterConnectionDescriptor descriptor);

    /// <summary>Calls <c>WatchAsync</c>.</summary>
    /// <param name="clusterId">The cluster.</param>
    [Alias("Watch")]
    Task<string> ReachWatchAsync(Guid clusterId);

    /// <summary>Calls <c>PingAsync</c> and returns the resulting health state.</summary>
    /// <param name="clusterId">The cluster.</param>
    [Alias("Ping")]
    Task<string> ReachPingAsync(Guid clusterId);

    /// <summary>Calls <c>ApplyAsync</c> and returns the outcome's message.</summary>
    /// <param name="clusterId">The cluster.</param>
    /// <param name="command">The command.</param>
    [Alias("ApplyMessage")]
    Task<string> ReachApplyMessageAsync(Guid clusterId, KubeCommand command);

    /// <summary>The tenant this grain is qualified to, as Orleans.Multitenant reports it.</summary>
    [Alias("MyTenant")]
    Task<string?> MyTenantAsync();

    /// <summary>
    ///     Invokes <b>every</b> method on <c>IClusterConnectionGrain</c> and returns the names of
    ///     any that did not refuse.
    /// </summary>
    /// <param name="clusterId">A cluster this grain's tenant does not own.</param>
    /// <param name="descriptor">A sample descriptor, for <c>AttachAsync</c>.</param>
    /// <param name="command">A sample command, for <c>ApplyAsync</c> and <c>DeleteAsync</c>.</param>
    /// <remarks>
    ///     ⚠ <b>The reflection lives in a grain rather than in the test, and it has to.</b> The
    ///     connection grain is null-tenant, so a tenant-qualified reference to it
    ///     (<c>ForTenant(x).GetGrain&lt;IClusterConnectionGrain&gt;(...)</c>) is not merely wrong —
    ///     the grain rejects the key outright on activation, because a tenant-qualified spelling
    ///     would be a second activation of a thing there is exactly one of. So the only way to be a
    ///     tenant reaching the real grain is to be a tenant-qualified grain calling the unqualified
    ///     key, which is what this method is.
    /// </remarks>
    [Alias("ProbeAll")]
    Task<IReadOnlyList<string>> ProbeUncheckedMethodsAsync(
        Guid clusterId,
        ClusterConnectionDescriptor descriptor,
        KubeCommand command);
}

/// <inheritdoc />
public sealed class KubeReacherGrain : Grain, IKubeReacherGrain
{
    IClusterConnectionGrain Connection(Guid clusterId) =>
        GrainFactory.GetGrain<IClusterConnectionGrain>(GrainKeys.ClusterConnection(clusterId));

    /// <inheritdoc />
    public async Task<string> ReachHealthAsync(Guid clusterId)
    {
        var outcome = await Connection(clusterId).GetHealthAsync();
        return outcome.IsSuccess
            ? outcome.GetValueOrThrow().State.ToString()
            : $"<{outcome.Error!.Code}>";
    }

    /// <inheritdoc />
    public async Task<string> ReachApplyAsync(Guid clusterId, KubeCommand command)
    {
        var outcome = await Connection(clusterId).ApplyAsync(command);
        return outcome.IsSuccess
            ? outcome.GetValueOrThrow().Result.ToString()
            : $"<{outcome.Error!.Code}>";
    }

    /// <inheritdoc />
    public async Task<string> ReachAttachAsync(ClusterConnectionDescriptor descriptor)
    {
        var outcome = await Connection(descriptor.ClusterId).AttachAsync(descriptor);
        return outcome.IsSuccess ? "ok" : $"<{outcome.Error!.Code}>";
    }

    /// <inheritdoc />
    public async Task<string> ReachWatchAsync(Guid clusterId)
    {
        var outcome = await Connection(clusterId).WatchAsync(
            new GroupVersionKind { Group = "apps", Version = "v1", Kind = "Deployment", Plural = "deployments" },
            string.Empty);

        return outcome.IsSuccess ? outcome.GetValueOrThrow().LabelSelector : $"<{outcome.Error!.Code}>";
    }

    /// <inheritdoc />
    public async Task<string> ReachPingAsync(Guid clusterId)
    {
        var outcome = await Connection(clusterId).PingAsync();
        return outcome.IsSuccess
            ? outcome.GetValueOrThrow().State.ToString()
            : $"<{outcome.Error!.Code}>";
    }

    /// <inheritdoc />
    public async Task<string> ReachApplyMessageAsync(Guid clusterId, KubeCommand command)
    {
        var outcome = await Connection(clusterId).ApplyAsync(command);
        return outcome.IsSuccess
            ? outcome.GetValueOrThrow().Message
            : $"<{outcome.Error!.Code}>";
    }

    /// <inheritdoc />
    public Task<string?> MyTenantAsync() =>
        Task.FromResult(AddressableExtensions.GetTenantId(this));

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ProbeUncheckedMethodsAsync(
        Guid clusterId,
        ClusterConnectionDescriptor descriptor,
        KubeCommand command)
    {
        var connection = Connection(clusterId);
        var unchecked_ = new List<string>();

        foreach (var method in typeof(IClusterConnectionGrain)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            var arguments = method.GetParameters()
                .Select(p => Sample(p.ParameterType, descriptor, command))
                .ToArray();

            var task = (Task)method.Invoke(connection, arguments)!;
            await task;

            var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
            var isFailure = (bool)result.GetType().GetProperty("IsFailure")!.GetValue(result)!;

            if (!isFailure)
            {
                unchecked_.Add(method.Name);
            }
        }

        return unchecked_;
    }

    static object Sample(Type type, ClusterConnectionDescriptor descriptor, KubeCommand command)
    {
        if (type == typeof(ClusterConnectionDescriptor))
        {
            return descriptor;
        }

        if (type == typeof(KubeCommand))
        {
            return command;
        }

        if (type == typeof(ObjectRef))
        {
            return new ObjectRef { Kind = SampleKind, Namespace = "ns", Name = "main" };
        }

        if (type == typeof(GroupVersionKind))
        {
            return SampleKind;
        }

        if (type == typeof(string))
        {
            return string.Empty;
        }

        if (type == typeof(CascadePolicy))
        {
            return CascadePolicy.Background;
        }

        throw new NotSupportedException(
            $"{type} has no sample value; add one so the probe keeps covering the whole interface.");
    }

    static GroupVersionKind SampleKind => new()
    {
        Group = "apps", Version = "v1", Kind = "Deployment", Plural = "deployments",
    };
}

/// <summary>
///     A cross-tenant authorizer that mirrors <c>PlatformCrossTenantAuthorizer</c>'s null-tenant rule.
/// </summary>
/// <remarks>
///     ⚠ Written out here rather than referenced from <c>CyberCloud.Tenancy</c> on purpose: the rule
///     under test is that the <b>connection grain</b> enforces tenancy in code, and importing the
///     tenancy assembly's authorizer would make this suite depend on an implementation assembly that
///     <c>CyberCloud.Kubernetes</c> deliberately does not reference. What matters is that the edge
///     into a null-tenant grain is ALLOWED here, exactly as production allows it — otherwise the
///     grain's own check would never be reached and this suite would be testing Orleans' separation
///     instead of ours.
/// </remarks>
public sealed class AllowIntoNullTenant : ICrossTenantAuthorizer
{
    /// <inheritdoc />
    public bool IsAccessAuthorized(string? sourceTenantId, string? targetTenantId) =>
        targetTenantId is null;
}

/// <summary>The separator: everything except Orleans' own system grains.</summary>
public sealed class AllCallsSeparated : IGrainCallTenantSeparator
{
    /// <inheritdoc />
    public bool IsTenantSeparatedCall(IIncomingGrainCallContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return !context.InterfaceName.StartsWith("Orleans.", StringComparison.Ordinal);
    }
}

/// <summary>An in-process Orleans cluster with the Kubernetes fabric wired as production wires it.</summary>
public sealed class KubeTestCluster : IAsyncLifetime
{
    TestCluster cluster = null!;

    /// <summary>The cluster's grain factory. ⚠ Tenant-unaware — a client, in the filter's terms.</summary>
    public IGrainFactory Grains => cluster.GrainFactory;

    /// <summary>A tenant-qualified grain factory.</summary>
    public TenantGrainFactory For(Guid tenant) =>
        Grains.ForTenant(tenant.ToString("D", CultureInfo.InvariantCulture));

    /// <summary>A reacher grain inside a tenant — the only way to be a grain-side caller.</summary>
    public IKubeReacherGrain Reacher(Guid tenant) =>
        For(tenant).GetGrain<IKubeReacherGrain>("reacher");

    /// <summary>The connection grain, reached as a client would.</summary>
    public IClusterConnectionGrain Connection(Guid clusterId) =>
        Grains.GetGrain<IClusterConnectionGrain>(GrainKeys.ClusterConnection(clusterId));

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        var builder = new TestClusterBuilder(1);
        builder.AddSiloBuilderConfigurator<SiloConfigurator>();
        cluster = builder.Build();
        await cluster.DeployAsync();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (cluster is not null)
        {
            await cluster.StopAllSilosAsync();
            await cluster.DisposeAsync();
        }
    }

    sealed class SiloConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder silo)
        {
            silo.AddMemoryGrainStorage(StorageTiers.Durable);

            silo.ConfigureServices(services =>
            {
                services.AddSingleton<ILoggerProvider, LogCapture>();
                services.AddSingleton<IClock>(SharedTestClock.Instance);
                services.TryAddSingleton<IClusterOperatorAuthority, SwitchableClusterOperatorAuthority>();
                services.TryAddSingleton<IKubeApiClientFactory, FakeApiClientFactory>();

                // The health timer is off: a test asserting a health transition cannot share a
                // process with a loop quietly repairing it. Same argument as
                // TenancyRefreshOptions.RunBackgroundRefresh.
                services.Configure<KubernetesOptions>(o =>
                {
                    o.RunHealthTimer = false;
                    o.InformerStaggerWindow = TimeSpan.Zero;
                });
            });

            // The production wiring, which is what registers ClusterConnectionTenantFilter.
            silo.AddCyberCloudKubernetes();

            // Orleans' own separation, allowing the edge into a null-tenant grain exactly as
            // PlatformCrossTenantAuthorizer does — so the connection grain's own check is reached.
            silo.AddMultitenantCommunicationSeparation(
                _ => new AllowIntoNullTenant(),
                _ => new AllCallsSeparated());
        }
    }
}

/// <summary>Binds <see cref="KubeTestCluster" /> to the classes that share it.</summary>
[CollectionDefinition(Name)]
public sealed class KubeClusterSuite : ICollectionFixture<KubeTestCluster>
{
    /// <summary>The collection name.</summary>
    public const string Name = "kube-test-cluster";
}
