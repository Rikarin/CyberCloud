using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Shouldly;
using StackExchange.Redis;

namespace CyberCloud.ServiceDefaults.Tests.Storage;

/// <summary>
///     What the hot tier does when Redis is full. docs/plan/05 § Hot:
///     "<c>maxmemory-policy noeviction</c> (a hot-tier eviction is a correctness bug, not a capacity
///     event — it must page, not silently drop state)".
/// </summary>
/// <remarks>
///     <para>
///         The reason this is a test rather than a configuration comment: <c>noeviction</c> and
///         <c>allkeys-lru</c> behave <i>identically</i> until the moment the tier is full, and then
///         one of them starts deleting a tenant's session state to make room for another tenant's,
///         reporting success both times. There is no metric on the write path that distinguishes
///         them; the only observable difference is the one asserted below.
///     </para>
/// </remarks>
public sealed class HotTierEvictionTests : IAsyncLifetime
{
    IContainer container = null!;
    ConnectionMultiplexer multiplexer = null!;

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        var token = TestContext.Current.CancellationToken;

        container = new ContainerBuilder("redis:8-alpine")
            .WithPortBinding(6379, true)
            .WithCommand("redis-server", "--maxmemory", "8mb", "--maxmemory-policy", "noeviction", "--appendonly", "no")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("redis-cli", "ping"))
            .Build();

        await container.StartAsync(token);

        // allowAdmin, because the first assertion is CONFIG GET maxmemory-policy and that is an
        // admin command as far as StackExchange.Redis is concerned.
        multiplexer = await ConnectionMultiplexer.ConnectAsync(
            $"{container.Hostname}:{container.GetMappedPublicPort(6379)},allowAdmin=true");
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        multiplexer?.Dispose();

        if (container is not null)
        {
            await container.DisposeAsync();
        }
    }

    [Fact]
    public void ThePolicyIsNoevictionAndNotAnLruVariant()
    {
        var server = multiplexer.GetServer(multiplexer.GetEndPoints()[0]);

        server.ConfigGet("maxmemory-policy").Single().Value.ShouldBe("noeviction");
        server.ConfigGet("maxmemory").Single().Value.ShouldNotBe("0");
    }

    [Fact]
    public async Task AFullHotTierFailsTheWriteLoudlyAndKeepsWhatItAlreadyHad()
    {
        var token = TestContext.Current.CancellationToken;
        var database = multiplexer.GetDatabase();
        var payload = new string('x', 256 * 1024);

        await database.StringSetAsync("{cc:t:canary}:Session:first", "the write that must survive");

        RedisServerException? refused = null;

        for (var i = 0; i < 200 && refused is null; i++)
        {
            token.ThrowIfCancellationRequested();

            try
            {
                await database.StringSetAsync($"{{cc:t:filler}}:Session:{i}", payload);
            }
            catch (RedisServerException ex)
            {
                refused = ex;
            }
        }

        // ⚠ The assertion that matters. The write is REFUSED — "OOM command not allowed when used
        // memory > 'maxmemory'" — it does not succeed by quietly deleting somebody else's key. In
        // Orleans terms that surfaces as a RedisStorageException out of WriteStateAsync, so the
        // grain call fails and the caller finds out, which is what "it must page" means.
        refused.ShouldNotBeNull("Redis accepted 50 MB into an 8 MB instance, so it is evicting");
        refused.Message.ShouldContain("OOM");

        // And nothing already stored was sacrificed to make room.
        (await database.StringGetAsync("{cc:t:canary}:Session:first")).ToString()
            .ShouldBe("the write that must survive");
    }
}
