using CyberCloud.Gateway.Host.Hubs;
using CyberCloud.Gateway.Host.Tests.Infrastructure;
using CyberCloud.Identity.Validation;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace CyberCloud.Gateway.Host.Tests;

/// <summary>
///     The deployed ticket store against a real Redis — the property the in-memory one cannot have.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Two store instances over one Redis, and that is the test.</b> The gateway is N pods
///         behind an ingress with no session affinity, so the pod that mints a ticket and the pod the
///         WebSocket lands on are different processes with nothing in common but this store. A suite
///         that issued and redeemed through one instance would prove what
///         <c>InMemoryHubTicketStore</c> already proves. <c>RedisRateLimitCounters</c>, the sibling
///         this store's shape is copied from, has no such test; this is the first Redis-backed gateway
///         seam exercised against Redis.
///     </para>
///     <para>
///         The container is <c>redis:8-alpine</c>, the image every other Redis-backed suite in the
///         tree starts. The clock is the harness's <c>FakeClock</c> for the store's own expiry check
///         and Redis's real one for the key's TTL, so the expiry test waits out a real second.
///     </para>
/// </remarks>
public sealed class RedisHubTicketStoreTests : IAsyncLifetime {
    readonly RedisContainer redis = new RedisBuilder("redis:8-alpine").Build();
    readonly FakeClock clock = new();

    ConnectionMultiplexer multiplexer = null!;

    static TokenClaims ClaimsFor(string subject, DateTimeOffset expiresAt) =>
        new(GatewayHarness.TenantA, "user", subject, "", "", expiresAt);

    RedisHubTicketStore Pod() => new(multiplexer, clock, NullLogger<RedisHubTicketStore>.Instance);

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <inheritdoc />
    public async ValueTask InitializeAsync() {
        await redis.StartAsync();
        multiplexer = await ConnectionMultiplexer.ConnectAsync(redis.GetConnectionString());
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() {
        await multiplexer.DisposeAsync();
        await redis.DisposeAsync();
    }

    [Fact]
    public async Task ATicketMintedOnOnePodIsRedeemedOnceOnAnother() {
        var minting = Pod();
        var upgrading = Pod();
        var claims = ClaimsFor("user-9", clock.UtcNow.AddMinutes(10));

        var ticket = await minting.IssueAsync(claims, HubNames.Terminal, Ct);
        ticket.ExpiresAt.ShouldBe(clock.UtcNow + HubTickets.Lifetime);

        (await upgrading.RedeemAsync(ticket.Value, HubNames.Terminal, Ct)).ShouldBe(claims);
        (await upgrading.RedeemAsync(ticket.Value, HubNames.Terminal, Ct)).ShouldBeNull();
        (await minting.RedeemAsync(ticket.Value, HubNames.Terminal, Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task ATicketTriedOnTheWrongHubIsSpent() {
        var store = Pod();
        var ticket = await store.IssueAsync(ClaimsFor("user-9", clock.UtcNow.AddMinutes(10)), HubNames.Resources, Ct);

        (await store.RedeemAsync(ticket.Value, HubNames.Terminal, Ct)).ShouldBeNull();
        (await store.RedeemAsync(ticket.Value, HubNames.Resources, Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task TheKeyCarriesTheTokensRemainingLifetimeWhenThatIsShorter() {
        var store = Pod();
        var ticket = await store.IssueAsync(ClaimsFor("user-9", clock.UtcNow.AddSeconds(1)), HubNames.Terminal, Ct);

        ticket.ExpiresAt.ShouldBe(clock.UtcNow.AddSeconds(1));

        // Redis's own TTL is what sweeps: the key is gone once the second is up, whatever the fake
        // clock says — and the store's own check would refuse it anyway once that clock moved.
        await Task.Delay(TimeSpan.FromSeconds(1.5), Ct);

        (await multiplexer.GetDatabase().KeyExistsAsync(HubTicketCodec.Key(ticket.Value))).ShouldBeFalse();
        (await store.RedeemAsync(ticket.Value, HubNames.Terminal, Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task AnUnknownTicketAndBytesTheCodecDidNotWriteAreBothNothing() {
        var store = Pod();

        (await store.RedeemAsync("never-minted", HubNames.Terminal, Ct)).ShouldBeNull();

        await multiplexer.GetDatabase().StringSetAsync(HubTicketCodec.Key("planted"), "not json");
        (await store.RedeemAsync("planted", HubNames.Terminal, Ct)).ShouldBeNull();
    }
}
