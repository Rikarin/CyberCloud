using CyberCloud.Core.Contracts;
using CyberCloud.ServiceDefaults.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Multitenant;
using Orleans.Storage;
using Shouldly;
using System.Net;
using System.Net.Sockets;
using Testcontainers.Redis;

namespace CyberCloud.ServiceDefaults.Tests.Storage;

/// <summary>
///     What a silo does when a durable shard is unreachable: does it refuse to start, or does it
///     start and fail the grain call?
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This was written to settle a claim, and it reversed it.</b> The expectation was that
///         <c>Orleans.Multitenant</c>'s tenant-unaware "bootstrap" provider — the one its
///         <c>addStorageProvider</c> parameter registers, documented as "called at silo
///         initialization, to ensure that any shared dependencies … are initialized" — would connect
///         at startup and make an unreachable shard a crash loop.
///     </para>
///     <para>
///         It does not, and the mechanism is a registration collision.
///         <c>AddAdoNetGrainStorage(name)</c> registers a keyed
///         <c>ILifecycleParticipant&lt;ISiloLifecycle&gt;</c> that resolves by asking for the keyed
///         <c>IGrainStorage</c> under the same name — and <c>Orleans.Multitenant</c> then registers
///         its own <c>MultitenantStorage</c> under that same key, afterwards. Last registration wins
///         in <c>Microsoft.Extensions.DependencyInjection</c>, so the participant resolves to the
///         multitenant wrapper and the bare provider underneath is <b>never constructed</b>. The
///         bootstrap instance is inert.
///     </para>
///     <para>
///         <b>Why it matters operationally.</b> A silo whose durable shard is down starts, passes its
///         readiness probe, joins the cluster, and takes traffic — and every grain activation that
///         lands on that shard fails. The blast radius is the tenants on one shard rather than the
///         whole silo, which is arguably the better failure, but it is not the one the wiring
///         implies and it means
///         <b>
///             nothing checks a shard's reachability before a silo is declared
///             ready
///         </b>
///         . docs/plan/05 says nothing about this either way. If the desired behaviour is
///         "do not join the cluster with an unreachable shard", it needs a health check per shard —
///         it will not come from the storage wiring.
///     </para>
/// </remarks>
public sealed class BootstrapProviderLivenessTests : IAsyncLifetime {
    readonly RedisContainer redis = new RedisBuilder("redis:8-alpine").Build();

    WebApplication? silo;

    /// <inheritdoc />
    public async ValueTask InitializeAsync() => await redis.StartAsync(TestContext.Current.CancellationToken);

    /// <inheritdoc />
    public async ValueTask DisposeAsync() {
        if (silo is not null) {
            try {
                await silo.StopAsync();
            } catch (InvalidOperationException) {
                // Never started.
            }

            await silo.DisposeAsync();
        }

        await redis.DisposeAsync();
    }

    [Fact]
    public async Task ASiloStartsHealthyEvenThoughItsDurableShardIsUnreachable() {
        // A port nothing is listening on, with a two-second connect timeout so the failure is fast
        // whichever way it goes.
        var deadShard =
            $"Host=127.0.0.1;Port={FreePort()};Database=cc;Username=cc;Password=cc;Timeout=2;Command Timeout=2";

        var builder = OrleansApplication.CreateSilo(
            [
                "--environment", "Development",
                "--urls", "http://127.0.0.1:0",
                $"--{CyberCloudClusterOptions.SectionName}:LocalhostSiloPort={FreePort()}",
                $"--{CyberCloudClusterOptions.SectionName}:LocalhostGatewayPort={FreePort()}",
                $"--{CyberCloudStorageOptions.SectionName}:Hot:ConnectionString={redis.GetConnectionString()}",
                $"--{CyberCloudStorageOptions.SectionName}:Durable:Shards:durable-00={deadShard}"
            ]
        );

        await builder.Services.AddApplicationAsync<StorageSiloModule>();

        silo = builder.Build();

        // The finding: this does not throw.
        await silo.StartAsync(TestContext.Current.CancellationToken);

        silo.Services.GetRequiredKeyedService<IGrainStorage>(StorageTiers.Durable).ShouldNotBeNull();

        // And the hot tier, whose store IS reachable, works on the same silo — so the failure is
        // scoped to the tier and the shard rather than to the silo.
        var hot = silo.Services.GetRequiredService<IGrainFactory>()
            .ForTenant(Guid.NewGuid().ToString("D"))
            .GetGrain<IHotStateGrain>("session/alive");

        await hot.WriteAsync("the hot tier is fine");
        (await hot.ReadAsync()).ShouldBe("the hot tier is fine");

        // The durable one fails at the grain call, which is where the cost lands.
        var durable = silo.Services.GetRequiredService<IGrainFactory>()
            .ForTenant(Guid.NewGuid().ToString("D"))
            .GetGrain<IDurableStateGrain>("res/doomed");

        await Should.ThrowAsync<Exception>(() => durable.WriteAsync("never arrives"));
    }

    static int FreePort() {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }
}
