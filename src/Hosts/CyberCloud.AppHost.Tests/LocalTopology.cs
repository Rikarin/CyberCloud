using Aspire.Hosting;
using Aspire.Hosting.Testing;
using CyberCloud.ServiceDefaults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Configuration;
using Projects;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace CyberCloud.AppHost.Tests;

/// <summary>
///     The real <c>CyberCloud.AppHost</c>, started, plus an Orleans client attached to the cluster
///     it brings up.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This starts <c>CyberCloud.AppHost</c>'s own <c>Program.cs</c>.</b>
///         <see cref="DistributedApplicationTestingBuilder" /> loads the AppHost assembly, runs its
///         entry point up to <c>Build()</c>, and hands back the application model — so the resources
///         under test are the resources <c>dotnet run</c> produces, not a copy of them maintained
///         next door. If somebody deletes the second silo from the AppHost, this fixture brings up
///         one silo and <c>TwoSiloClusterTests</c> fails, which is the only arrangement in which
///         that test means anything.
///     </para>
///     <para>
///         ⚠
///         <b>
///             It uses fixed ports and therefore cannot run beside a manual <c>dotnet run</c> of
///             the same AppHost.
///         </b>
///         11111/11112, 30011/30012 and 6443 are chosen in
///         <see cref="CyberCloudResources" /> and are the same in both. That is a deliberate
///         consequence of Orleans' ports not being Aspire endpoints: Aspire allocates free ports for
///         things it knows about, and it does not know about these. The failure is an
///         <c>AddressInUseException</c> from a silo, which names the port.
///     </para>
/// </remarks>
public sealed class LocalTopology : IAsyncLifetime {
    /// <summary>
    ///     How long the whole bring-up is allowed to take before the suite gives up.
    /// </summary>
    /// <remarks>
    ///     ⚠ Generous, and it is not a performance assertion. First run on a machine pulls four
    ///     container images; k3s alone is ~250 MB. ADR-014's justification is start-up time and
    ///     <see cref="ColdStart" /> is what measures it — but a timeout that fires on a cold image
    ///     cache would be a flaky test rather than a measurement.
    /// </remarks>
    static readonly TimeSpan BringUpBudget = TimeSpan.FromMinutes(10);

    readonly ConcurrentDictionary<string, string> resourceStates =
        new(StringComparer.Ordinal);

    IHost clientHost = null!;

    /// <summary>How long <c>StartAsync</c> plus both silos becoming healthy took.</summary>
    public TimeSpan ColdStart { get; private set; }

    /// <summary>The running application model, for connection strings and resource states.</summary>
    public DistributedApplication Application { get; private set; } = null!;

    /// <summary>An Orleans client attached to both silos' gateways.</summary>
    public IClusterClient Client => clientHost.Services.GetRequiredService<IClusterClient>();

    /// <inheritdoc />
    public async ValueTask InitializeAsync() {
        using var budget = new CancellationTokenSource(BringUpBudget);
        var token = budget.Token;

        var builder = await DistributedApplicationTestingBuilder
            .CreateAsync<CyberCloud_AppHost>([], token);

        Application = await builder.BuildAsync(token);

        var clock = Stopwatch.StartNew();
        await Application.StartAsync(token);

        // ⚠ Not decoration. When a resource never becomes healthy, the only thing the caller sees
        // otherwise is an OperationCanceledException from the wait below, which names neither the
        // resource nor the state it is stuck in. The last state of every resource is exactly the
        // diagnosis — "durable00 is Waiting", "durable-schema exited with 1" — and it is unavailable
        // afterwards because the notification stream is not replayable.
        _ = Task.Run(() => RecordResourceStatesAsync(token), CancellationToken.None);

        // ⚠ Healthy, not Running. `/health` on a silo is SiloReadinessHealthCheck, which asks
        // IManagementGrain a question — so a healthy silo is a silo whose grain runtime routes
        // messages, not merely a process that has not exited. Waiting on Running would race every
        // test below against Orleans' start-up.
        try {
            await Application.ResourceNotifications.WaitForResourceHealthyAsync(CyberCloudResources.SiloOne, token);

            await Application.ResourceNotifications.WaitForResourceHealthyAsync(CyberCloudResources.SiloTwo, token);
        } catch (OperationCanceledException timedOut) {
            throw new InvalidOperationException(
                $"The local topology did not come up within {BringUpBudget}. Last known resource "
                + $"states:{Environment.NewLine}{States()}",
                timedOut
            );
        }

        ColdStart = clock.Elapsed;

        // ⚠ Console, not ITestOutputHelper. A fixture has no test to attribute output to, and a
        // passing test's output is not shown by default — so the one number ADR-014 is justified by
        // would only ever be visible on a failing run.
        Console.WriteLine(
            $"[CyberCloud.AppHost] cold start: {ColdStart.TotalSeconds:F1} s "
            + "(StartAsync until both silos report healthy)"
        );

        clientHost = BuildClient();
        await clientHost.StartAsync(token);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() {
        if (clientHost is not null) {
            await clientHost.StopAsync();
            clientHost.Dispose();
        }

        if (Application is not null) {
            await Application.StopAsync();
            await Application.DisposeAsync();
        }
    }

    /// <summary>The last observed state of every resource, one per line.</summary>
    public string States() =>
        string.Join(
            Environment.NewLine,
            resourceStates
                .OrderBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => $"  {x.Key}: {x.Value}")
        );

    /// <summary>The Npgsql connection string for one durable shard, as the silos received it.</summary>
    /// <param name="shard">The shard id.</param>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    public async Task<string> ShardConnectionStringAsync(string shard, CancellationToken cancellationToken) =>
        await Application.GetConnectionStringAsync(shard, cancellationToken)
        ?? throw new InvalidOperationException($"The AppHost has no resource named '{shard}'.");

    async Task RecordResourceStatesAsync(CancellationToken cancellationToken) {
        try {
            await foreach (var change in Application.ResourceNotifications
                               .WatchAsync(cancellationToken)
                               .ConfigureAwait(false)) {
                var snapshot = change.Snapshot;
                var health = snapshot.HealthStatus?.ToString() ?? "no health check";

                resourceStates[change.Resource.Name] =
                    $"{snapshot.State?.Text ?? "?"} ({health})"
                    + (snapshot.ExitCode is { } code ? $", exit code {code}" : string.Empty);
            }
        } catch (OperationCanceledException) {
            // The bring-up budget elapsed or the fixture is being disposed.
        }
    }

    /// <summary>
    ///     An Orleans client pointed at <b>both</b> gateways.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Both, deliberately.</b> A client given one gateway would still reach grains on
    ///         the other silo — the gateway forwards — so a one-gateway client cannot distinguish
    ///         "two silos" from "one silo plus a dead port". Listing both means the client's own
    ///         connection is evidence that two processes are serving, and the grain-to-grain call in
    ///         <c>TwoSiloClusterTests</c> is evidence that they are the same cluster.
    ///     </para>
    ///     <para>
    ///         The <c>ClusterId</c>/<c>ServiceId</c> are <c>CyberCloudClusterOptions</c>' defaults,
    ///         which is what the silos run with because the AppHost overrides neither. Getting them
    ///         wrong does not fail loudly: the client simply never finds a gateway it is allowed to
    ///         talk to and times out.
    ///     </para>
    /// </remarks>
    static IHost BuildClient() {
        var defaults = new CyberCloudClusterOptions();

        return new HostBuilder()
            .UseOrleansClient(client => {
                    client.Configure<ClusterOptions>(options => {
                            options.ClusterId = defaults.ClusterId;
                            options.ServiceId = defaults.ServiceId;
                        }
                    );

                    client.UseLocalhostClustering(
                        [CyberCloudResources.SiloOneGatewayPort, CyberCloudResources.SiloTwoGatewayPort],
                        defaults.ServiceId,
                        defaults.ClusterId
                    );
                }
            )
            .Build();
    }
}

/// <summary>Binds <see cref="LocalTopology" /> to every class that shares it.</summary>
/// <remarks>
///     One bring-up for the whole assembly. Two would be two k3s containers and four silos on
///     colliding ports.
/// </remarks>
[CollectionDefinition(Name)]
public sealed class LocalTopologySuite : ICollectionFixture<LocalTopology> {
    /// <summary>The collection name.</summary>
    public const string Name = "apphost-local-topology";
}
