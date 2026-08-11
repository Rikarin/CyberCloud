using CyberCloud.ServiceDefaults.Storage;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Shouldly;
using StackExchange.Redis;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace CyberCloud.ServiceDefaults.Tests.Storage;

/// <summary>
///     The hash tag against a Redis server that is <b>actually in cluster mode</b>, which is the only
///     place its behaviour differs from a prefix.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>One node, all 16 384 slots — and what that does and does not prove.</b> Slot
///         arithmetic and <c>CROSSSLOT</c> enforcement are properties of <i>cluster mode</i>, not of
///         node count: a single node with <c>cluster-enabled yes</c> computes slots exactly as a
///         twelve-shard cluster does and rejects a multi-key command that spans two slots exactly as
///         it would. So colocation, the tag rule and the cross-slot failure are all genuinely
///         exercised here.
///     </para>
///     <para>
///         What is <b>not</b> exercised, and cannot be without a multi-node cluster: <c>MOVED</c> and
///         <c>ASK</c> redirection, the client's slot-map refresh, behaviour during a resharding, and
///         the actual claim that a tenant's keys land on <i>one shard</i> rather than one slot. Slots
///         map to shards, so one slot implies one shard for any slot assignment — but the mapping
///         itself, and a failover under it, are untested here.
///     </para>
///     <para>
///         ⚠ <b>The announce settings are the whole trick.</b> A clustered Redis publishes the
///         address it thinks it has, and a container's is unreachable from the host, so the client
///         connects, reads <c>CLUSTER NODES</c>, and then times out against an internal IP. Binding a
///         known host port up front and passing it as <c>--cluster-announce-port</c> is what makes a
///         containerised cluster reachable at all.
///     </para>
/// </remarks>
public sealed class RedisClusterHashTagTests : IAsyncLifetime {
    readonly int port = FreePort();
    readonly int busPort = FreePort();

    IContainer container = null!;
    ConnectionMultiplexer multiplexer = null!;

    /// <inheritdoc />
    public async ValueTask InitializeAsync() {
        var token = TestContext.Current.CancellationToken;

        container = new ContainerBuilder("redis:8-alpine")
            .WithPortBinding(port, 6379)
            .WithPortBinding(busPort, 16379)
            .WithCommand(
                "redis-server",
                "--cluster-enabled",
                "yes",
                "--cluster-announce-ip",
                "127.0.0.1",
                "--cluster-announce-port",
                port.ToString(CultureInfo.InvariantCulture),
                "--cluster-announce-bus-port",
                busPort.ToString(CultureInfo.InvariantCulture),
                "--maxmemory-policy",
                "noeviction",
                "--appendonly",
                "no"
            )
            .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("redis-cli", "ping"))
            .Build();

        await container.StartAsync(token);

        await container.ExecAsync(["redis-cli", "cluster", "addslotsrange", "0", "16383"], token);

        for (var attempt = 0; attempt < 60; attempt++) {
            var info = await container.ExecAsync(["redis-cli", "cluster", "info"], token);
            if (info.Stdout.Contains("cluster_state:ok", StringComparison.Ordinal)) {
                break;
            }

            await Task.Delay(250, token);
        }

        var configuration = ConfigurationOptions.Parse($"127.0.0.1:{port}");
        configuration.AbortOnConnectFail = false;
        configuration.ConnectRetry = 5;
        configuration.ConnectTimeout = 10_000;
        // CLUSTER and CONFIG are admin commands in StackExchange.Redis, and this fixture asks the
        // server for its own opinion on both.
        configuration.AllowAdmin = true;

        multiplexer = await ConnectionMultiplexer.ConnectAsync(configuration);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() {
        multiplexer?.Dispose();

        if (container is not null) {
            await container.DisposeAsync();
        }
    }

    [Fact]
    public void TheServerIsActuallyInClusterModeOrNothingBelowMeansAnything() {
        var server = multiplexer.GetServer(multiplexer.GetEndPoints()[0]);
        server.ServerType.ShouldBe(ServerType.Cluster);

        // And the consequence that motivated writing RedisHashSlot at all: against a standalone
        // server this call returns -1, so it cannot be the source of truth in unit tests.
        multiplexer.HashSlot("foo").ShouldBe(12182);
    }

    [Fact]
    public void OurSlotFunctionAgreesWithTheServerOnEveryKeyShapeWeUse() {
        var server = multiplexer.GetServer(multiplexer.GetEndPoints()[0]);

        var keys = new List<string> { "foo", "somekey", "{}:empty-tag", "no-braces-at-all" };
        keys.AddRange(
            Enumerable.Range(0, 40)
                .Select(i =>
                    TenantHotKeys.Format(
                        "cc:t:" + StorageFixture.Tenant(i).ToString("N", CultureInfo.InvariantCulture),
                        "CyberCloud.Tests.Hot",
                        "res/" + i
                    )
                )
        );

        foreach (var key in keys) {
            var fromServer = (int)(long)(RedisResult)server.Execute("CLUSTER", "KEYSLOT", key);

            RedisHashSlot.Of(key).ShouldBe(fromServer, $"slot mismatch for '{key}'");
            multiplexer.HashSlot(key).ShouldBe(fromServer);
        }
    }

    [Fact]
    public void OneTenantIsOneSlotAndTwoTenantsAreNot() {
        var tagA = "cc:t:" + StorageFixture.Tenant(1).ToString("N", CultureInfo.InvariantCulture);
        var tagB = "cc:t:" + StorageFixture.Tenant(2).ToString("N", CultureInfo.InvariantCulture);

        var slotsOfA = Enumerable.Range(0, 200)
            .Select(i => TenantHotKeys.Format(tagA, "Resource", "k/" + i))
            .Select(key => multiplexer.HashSlot(key))
            .Distinct()
            .ToList();

        slotsOfA.Count.ShouldBe(1);
        multiplexer.HashSlot(TenantHotKeys.Format(tagB, "Resource", "k/0")).ShouldNotBe(slotsOfA[0]);
    }

    [Fact]
    public async Task AMultiKeyReadWithinOneTenantIsOneRoundTripAndAcrossTenantsIsCrossSlot() {
        // docs/plan/05 § Hot: "a multi-key read is one round trip". That is the payoff of the tag,
        // and the failure without it is not slowness — it is an error.
        var token = TestContext.Current.CancellationToken;
        var database = multiplexer.GetDatabase();

        var tagA = "cc:t:" + StorageFixture.Tenant(11).ToString("N", CultureInfo.InvariantCulture);
        var tagB = "cc:t:" + StorageFixture.Tenant(12).ToString("N", CultureInfo.InvariantCulture);

        var a1 = TenantHotKeys.Format(tagA, "Resource", "one");
        var a2 = TenantHotKeys.Format(tagA, "Resource", "two");
        var b1 = TenantHotKeys.Format(tagB, "Resource", "one");

        await database.StringSetAsync(a1, "1");
        await database.StringSetAsync(a2, "2");
        await database.StringSetAsync(b1, "3");

        var within = await database.StringGetAsync([a1, a2]);
        within.Select(x => x.ToString()).ShouldBe(["1", "2"]);

        // Two halves, because the failure has two layers and both are worth pinning.
        //
        // (a) The client refuses to route it — StackExchange.Redis computes the slots itself and
        //     will not send a command that spans two.
        var refusedByClient = Should.Throw<RedisCommandException>(() => database.StringGet([a1, b1]));
        refusedByClient.Message.ShouldContain("slot");

        // (b) And if it did send it, the SERVER rejects it. Issued with redis-cli inside the
        //     container so nothing client-side can intercept it, which is what makes this the real
        //     CROSSSLOT rather than a library opinion about one.
        var raw = await container.ExecAsync(["redis-cli", "mget", a1, b1], token);
        (raw.Stdout + raw.Stderr).ShouldContain("CROSSSLOT");
    }

    [Fact]
    public void EvictionIsOffOnTheHotTierBecauseAnEvictionWouldBeALostWrite() {
        // docs/plan/05 § Hot: "maxmemory-policy noeviction (a hot-tier eviction is a correctness bug,
        // not a capacity event — it must page, not silently drop state)".
        var server = multiplexer.GetServer(multiplexer.GetEndPoints()[0]);
        var policy = server.ConfigGet("maxmemory-policy").Single().Value;

        policy.ShouldBe("noeviction");
    }

    static int FreePort() {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }
}
