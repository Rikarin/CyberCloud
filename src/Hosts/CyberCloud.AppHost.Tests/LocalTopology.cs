using Aspire.Hosting;
using Aspire.Hosting.Testing;
using CyberCloud.ServiceDefaults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Configuration;
using Projects;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.NetworkInformation;

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
///     <para>
///         ⚠
///         <b>
///             Nor beside a second copy of <i>this suite</i> — which is why the bring-up takes a
///             machine-wide lock (<see cref="MachineLockPath" />).
///         </b>
///         The paragraph above anticipated a developer's manual <c>dotnet run</c>; it did not
///         anticipate a second checkout running <c>./build.sh Test</c> at the same time, which is
///         now routine and collides identically. The observed failure is <b>not</b> an
///         <c>AddressInUseException</c>, because 6443 is a Docker-published port rather than a
///         socket this process binds: Docker refuses the container with
///         <c>Bind for 0.0.0.0:6443 failed: port is already allocated</c>, k3s never starts, and the
///         only test that notices is <c>TheK3sApiServerAnswersKubernetes</c>, which fails with
///         <c>Connection refused (127.0.0.1:6443)</c> while every other resource is genuinely
///         healthy. That reads as a mystery, and it is the reason this lock exists rather than a
///         comment advising people not to do it.
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

    /// <summary>
    ///     How long to wait for the previous run's k3s container to release host port 6443.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This waits for a release, it does not retry a failure.</b> Holding the machine lock
    ///     means no other copy of this suite is starting; anything still on 6443 is therefore the
    ///     <i>previous</i> holder's container, and Aspire's teardown returns before Docker has
    ///     finished removing it — measured at over 20 s on this machine. Waiting for a resource to
    ///     be released is not the same as re-running an operation until it happens to work: if the
    ///     port never frees, the bring-up fails with the reason rather than proceeding.
    /// </remarks>
    static readonly TimeSpan PortReleaseBudget = TimeSpan.FromSeconds(90);

    readonly ConcurrentDictionary<string, string> resourceStates =
        new(StringComparer.Ordinal);

    IHost clientHost = null!;

    /// <summary>The machine-wide lock, held for as long as the topology is up.</summary>
    FileStream? machineLock;

    /// <summary>
    ///     Where the machine-wide lock lives.
    /// </summary>
    /// <remarks>
    ///     ⚠ The system temp directory, deliberately, because the point is to be found by a process
    ///     that shares nothing else with this one — a different git worktree, a different checkout,
    ///     a different branch. A path under the repository would be per-worktree and would lock
    ///     nothing.
    /// </remarks>
    static string MachineLockPath { get; } =
        Path.Combine(Path.GetTempPath(), "cybercloud-apphost-local-topology.lock");

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

        machineLock = await AcquireMachineLockAsync(token);
        await WaitForApiPortToBeFreeAsync(token);

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

        // ⚠ Last, and after the application is disposed. Releasing the lock is the signal that the
        // ports are the next run's to take, and Aspire only asks Docker to remove the containers
        // during DisposeAsync. Releasing earlier would hand over ports this process still holds —
        // which is the collision this lock exists to prevent, merely made narrower.
        machineLock?.Dispose();
        machineLock = null;
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

    /// <summary>
    ///     Takes the machine-wide lock, waiting for whoever holds it.
    /// </summary>
    /// <param name="cancellationToken">The bring-up budget.</param>
    /// <remarks>
    ///     ⚠ <b>A file lock rather than a <see cref="Mutex" />, and the reason is the crash case.</b>
    ///     A named mutex abandoned by a killed process surfaces as
    ///     <c>AbandonedMutexException</c> on the next waiter, and a test run cancelled with Ctrl-C —
    ///     the normal way a bring-up ends when somebody changes their mind — is exactly that case.
    ///     A <see cref="FileStream" /> handle is released by the operating system when the process
    ///     dies, however it dies, so a killed run cannot wedge every future one.
    /// </remarks>
    static async Task<FileStream> AcquireMachineLockAsync(CancellationToken cancellationToken) {
        var announced = false;

        while (true) {
            cancellationToken.ThrowIfCancellationRequested();

            try {
                return new FileStream(
                    MachineLockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None
                );
            } catch (IOException) {
                if (!announced) {
                    // ⚠ Said out loud, because the alternative is a suite that looks hung. Another
                    // checkout's `./build.sh Test` holds this for the length of its bring-up.
                    Console.WriteLine(
                        $"[CyberCloud.AppHost] waiting for another AppHost bring-up on this machine "
                        + $"to finish (lock: {MachineLockPath})."
                    );

                    announced = true;
                }

                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }
    }

    /// <summary>
    ///     Waits until nothing is listening on the k3s API port, and explains it if that never
    ///     happens.
    /// </summary>
    /// <param name="cancellationToken">The bring-up budget.</param>
    /// <exception cref="InvalidOperationException">The port never became free.</exception>
    static async Task WaitForApiPortToBeFreeAsync(CancellationToken cancellationToken) {
        var clock = Stopwatch.StartNew();

        while (IsApiPortTaken()) {
            if (clock.Elapsed > PortReleaseBudget) {
                throw new InvalidOperationException(
                    $"Host port {CyberCloudResources.K3sApiPort} is still in use after "
                    + $"{PortReleaseBudget.TotalSeconds:F0} s, and CyberCloud.AppHost publishes k3s "
                    + $"on it unproxied, so this bring-up would leave k3s unable to start and only "
                    + $"TheK3sApiServerAnswersKubernetes would notice. Something outside this test "
                    + $"run holds it — a manual `dotnet run` of CyberCloud.AppHost, or a k3s "
                    + $"container left behind by an earlier run "
                    + $"(`docker ps --filter publish={CyberCloudResources.K3sApiPort}`)."
                );
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    /// <summary>Whether anything on this machine is listening on the k3s API port.</summary>
    static bool IsApiPortTaken() =>
        IPGlobalProperties.GetIPGlobalProperties()
            .GetActiveTcpListeners()
            .Any(x => x.Port == CyberCloudResources.K3sApiPort);

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
    ///         wrong does not fail loudly: the client never finds a gateway it is allowed to
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
