using CyberCloud.Core.Time;
using CyberCloud.Identity.Validation;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace CyberCloud.Gateway.Host.Hubs;

/// <summary>
///     Tickets in Redis, so the pod that mints one and the pod the WebSocket lands on need not be the
///     same pod. docs/plan/10 § What the gateway must never do.
/// </summary>
/// <remarks>
///     <para>
///         Two commands and no script. <c>SET … NX PX</c> writes the ticket with Redis's own expiry, so
///         nothing sweeps; <c>GETDEL</c> reads and removes it in one round trip, so two upgrades racing
///         on one ticket see it exactly once between them. The hub check happens after the
///         <c>GETDEL</c>, which is what makes a ticket tried on the wrong hub spent rather than
///         retryable — the contract <see cref="IHubTicketStore.RedeemAsync" /> states.
///     </para>
///     <para>
///         ⚠ <b>Fails closed, where the rate limiter fails open.</b> <c>RedisRateLimitCounters</c>
///         admits a request it could not count, because a Redis outage costing protection is better
///         than one costing availability. A ticket is the opposite case: a redeem that cannot reach
///         the store has no claims to establish, and establishing none is the only safe answer. The
///         outage is logged with the key and the upgrade gets the same <c>401</c> an unknown ticket
///         does. ⚠ An issue that cannot reach the store throws, and the pipeline's stage guard turns
///         that into the <c>500</c> every faulted stage gets — the caller retries, and the alternative
///         of handing out a ticket nothing can redeem is a slower way to say the same thing.
///     </para>
/// </remarks>
sealed class RedisHubTicketStore(
    IConnectionMultiplexer redis,
    IClock clock,
    ILogger<RedisHubTicketStore> logger
)
    : IHubTicketStore {
    /// <inheritdoc />
    public async Task<HubTicket> IssueAsync(TokenClaims claims, string hub, CancellationToken cancellationToken = default) {
        ArgumentException.ThrowIfNullOrEmpty(hub);
        cancellationToken.ThrowIfCancellationRequested();

        var now = clock.UtcNow;
        var ticket = new HubTicket(HubTickets.NewValue(), HubTickets.ExpiryFor(claims, now));
        var lifetime = ticket.ExpiresAt - now;

        if (lifetime <= TimeSpan.Zero) {
            // The token behind the request has already expired; stage 2 would have refused it, so
            // this is unreachable in the pipeline and kept honest here rather than written with a
            // negative expiry Redis would reject.
            lifetime = TimeSpan.FromMilliseconds(1);
        }

        var written = await redis.GetDatabase()
            .StringSetAsync(
                HubTicketCodec.Key(ticket.Value),
                HubTicketCodec.Encode(claims, hub),
                lifetime,
                When.NotExists
            );

        if (!written) {
            // 256 bits of randomness colliding with a live key is not a thing that happens; if it
            // did, the honest answer is a retry rather than a ticket that opens somebody else's hub.
            throw new InvalidOperationException("A hub ticket value collided with a live one; mint again.");
        }

        return ticket;
    }

    /// <inheritdoc />
    public async Task<TokenClaims?> RedeemAsync(string ticket, string hub, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(ticket);
        cancellationToken.ThrowIfCancellationRequested();

        RedisValue stored;

        try {
            stored = await redis.GetDatabase().StringGetDeleteAsync(HubTicketCodec.Key(ticket));
        } catch (RedisException exception) {
            logger.LogWarning(
                exception,
                "A hub ticket for '{Hub}' could not be read; the upgrade is refused. A ticket that "
                + "cannot be redeemed establishes no caller, which is the only safe answer.",
                hub
            );

            return null;
        }

        var decoded = HubTicketCodec.Decode(stored);

        if (decoded is null) {
            return null;
        }

        var (claims, forHub) = decoded.Value;
        var live = claims.ExpiresAt > clock.UtcNow && string.Equals(forHub, hub, StringComparison.Ordinal);

        return live ? claims : null;
    }
}
