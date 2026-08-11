using CyberCloud.ServiceDefaults.HealthChecks;
using CyberCloud.ServiceDefaults.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace CyberCloud.ServiceDefaults.Tests.Storage;

/// <summary>
///     A real silo with a real dead durable shard, asked the way Kubernetes asks it.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This is <c>BootstrapProviderLivenessTests</c> with the conclusion turned into a
///         probe, and without a container.</b> That test needed a Redis because it makes a grain call;
///         this one only needs the silo to <i>start</i>, and the same finding that makes the silo
///         start with a dead Postgres — <c>Orleans.Multitenant</c> overwrites the keyed
///         <c>IGrainStorage</c>, so neither bootstrap provider is ever constructed — makes it start
///         with an unreachable Redis too. Both stores here are addresses nothing is listening on.
///     </para>
///     <para>
///         The two claims it settles, over HTTP, which is the only way a probe is shown not to lie:
///         <c>/health</c> is <b>200</b> while a shard is dark, and <c>/api/health</c> is
///         <b>503</b> and names the shard.
///     </para>
/// </remarks>
public sealed class UnreachableShardReadinessFixture : IAsyncLifetime {
    /// <summary>The shard id the assertions look for by name.</summary>
    public const string DeadShard = "durable-07";

    WebApplication app = null!;

    /// <summary>An HTTP client pointed at the silo's own listener.</summary>
    public HttpClient Http { get; private set; } = null!;

    /// <inheritdoc />
    public async ValueTask InitializeAsync() {
        var builder = OrleansApplication.CreateSilo(
            [
                "--environment", "Development",
                "--urls", "http://127.0.0.1:0",
                $"--{CyberCloudClusterOptions.SectionName}:LocalhostSiloPort={FreePort()}",
                $"--{CyberCloudClusterOptions.SectionName}:LocalhostGatewayPort={FreePort()}",

                // Both tiers point at closed ports. Neither bootstrap provider is constructed, so
                // neither address is dialled at startup — which is the whole finding.
                $"--{CyberCloudStorageOptions.SectionName}:Hot:ConnectionString=127.0.0.1:{FreePort()}",
                $"--{CyberCloudStorageOptions.SectionName}:Durable:HealthProbeTimeout=00:00:01",
                $"--{CyberCloudStorageOptions.SectionName}:Durable:Shards:{DeadShard}="
                + $"Host=127.0.0.1;Port={FreePort()};Database=cc;Username=cc;Password=cc"
            ]
        );

        await builder.Services.AddApplicationAsync<SiloTestModule>();

        app = builder.Build();
        app.MapDefaultEndpoints();

        // The first assertion, and it is this line: a silo whose durable tier is entirely
        // unreachable starts.
        await app.StartAsync();

        var address = app.Services
            .GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses
            .First();

        Http = new() { BaseAddress = new(address) };
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() {
        Http?.Dispose();

        if (app is null) {
            return;
        }

        try {
            await app.StopAsync();
        } catch (InvalidOperationException) {
            // Never started.
        }

        await app.DisposeAsync();
    }

    static int FreePort() {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }
}

/// <summary>The two probes, asked over the wire.</summary>
public sealed class UnreachableShardReadinessTests(UnreachableShardReadinessFixture silo)
    : IClassFixture<UnreachableShardReadinessFixture> {
    [Fact]
    public async Task ReadinessStaysGreenWhileADurableShardIsDark() {
        // ⚠ The cascade guard, end to end. Kubernetes removes a pod from a Service the moment this
        // returns 503, so a 503 here would mean one unreachable shard evicting every silo bound to
        // it and concentrating that shard's load on its neighbours. docs/plan/05 § Storage provider
        // wiring says why that trade is refused; this is what keeps the refusal true.
        var response = await silo.Http.GetAsync(
            new Uri("/health", UriKind.Relative),
            TestContext.Current.CancellationToken
        );

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task TheDetailEndpointReportsTheShardByName() {
        var response = await silo.Http.GetAsync(
            new Uri("/api/health", UriKind.Relative),
            TestContext.Current.CancellationToken
        );

        // Unhealthy overall, because the report is where an operator and an alert look. Untagged is
        // what keeps that out of the probes; it is not a reason to soften the word.
        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var json = JsonDocument.Parse(body);

        var shards = json.RootElement.GetProperty("checks")
            .EnumerateArray()
            .Single(x => x.GetProperty("name").GetString() == DurableShardHealthCheck.Name);

        shards.GetProperty("status").GetString().ShouldBe("Unhealthy");

        // "Storage is unhealthy" at 16 shards is not actionable. The shard id is.
        (shards.GetProperty("description").GetString() ?? string.Empty)
            .ShouldContain(UnreachableShardReadinessFixture.DeadShard);

        // docs/plan/08 § Errors — the health body is reachable from the cluster network, so no
        // stack frames, even on the failure path that is the whole point of this check.
        body.ShouldNotContain("   at ");
        body.ShouldNotContain(".cs:line");
    }
}
