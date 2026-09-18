using CyberCloud.Core.Time;
using CyberCloud.Identity.Validation;
using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Gateway.Host.Hubs;

/// <summary>
///     The short-lived ticket a browser opens a hub's WebSocket with. docs/plan/10 § SignalR, docs/plan/19
///     § Architecture.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             A browser cannot put a header on a WebSocket, and stage 2 reads nothing but the
///             <c>Authorization</c> header.
///         </b> Those two facts together meant the four hubs were unreachable from the portal: the
///         SignalR JavaScript client's answer is to put the bearer token itself in the query string as
///         <c>access_token</c>, and a bearer token in a URL is in every proxy log, every browser
///         history and every <c>Referer</c> between here and the pod. The ticket is the alternative:
///         a caller who already holds a token asks <c>POST /hubs/{hub}/ticket</c> for one, over an
///         ordinary authenticated request, and the WebSocket upgrade carries <c>?ticket=</c> instead.
///         What the ticket is: 32 random bytes, bound to the claims stage 2 produced for the request
///         that minted it and to the one hub it was minted for, good for <see cref="Lifetime" /> or
///         until the token behind it expires, whichever is first, and redeemable once. A URL that
///         leaks therefore leaks a value that stopped working the moment the socket opened.
///     </para>
///     <para>
///         ⚠ <b>Redeemed only on the exact hub path, and never on the ticket route itself.</b> A
///         ticket accepted on <c>/hubs/terminal/ticket</c> would mint the next ticket, and a chain of
///         thirty-second tickets is a token that never expires. <see cref="IsRedeemableOn" /> is
///         where that rule lives, and <c>HubTicketTests.ATicketCannotMintAnotherTicket</c> holds it.
///     </para>
///     <para>
///         ⚠
///         <b>
///             The gateway mints it and not the console's <c>connect</c> action, although the
///             portal asks for both in one motion.
///         </b> An action handler runs with no caller — its own
///         remarks say so, and <c>charts/managed/cloud-shell/conformance.yaml § owed</c> records the
///         consequence — while a ticket is exactly a caller's claims made portable for thirty seconds.
///         Only the process that validated the token can vouch for them, and that process is this one.
///         It is also why the route is per hub rather than per console: the resources, operations and
///         metrics hubs face the same browser and need the same ticket.
///     </para>
/// </remarks>
static class HubTickets {
    /// <summary>How long a ticket is redeemable. Long enough for one upgrade, not for a second tab.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);

    /// <summary>The query parameter the upgrade carries the ticket in.</summary>
    /// <remarks>
    ///     ⚠ Deliberately not <c>access_token</c>, which is the SignalR client's name for the bearer
    ///     token itself. A reader of a URL should be able to tell which of the two it holds.
    /// </remarks>
    public const string QueryParameter = "ticket";

    /// <summary>The path segment after the hub's name that mints one: <c>/hubs/{hub}/ticket</c>.</summary>
    public const string RouteSegment = "ticket";

    /// <summary>
    ///     SignalR's own negotiate segment, <c>/hubs/{hub}/negotiate</c>, routed as the hub it belongs to.
    /// </summary>
    public const string NegotiateSegment = "negotiate";

    /// <summary>
    ///     The hub a request's path names — <c>terminal</c> for <c>/hubs/terminal</c> — or
    ///     <see langword="null" /> for any other path, including the ticket and negotiate routes.
    /// </summary>
    /// <param name="path">The request path.</param>
    public static string? HubRedeemableOn(PathString path) {
        var value = path.Value ?? "";

        if (!value.StartsWith("/hubs/", StringComparison.Ordinal)) {
            return null;
        }

        var hub = value["/hubs/".Length..].TrimEnd('/');
        return HubNames.IsKnown(hub) ? hub : null;
    }

    /// <summary>Whether a ticket in this request's query may stand in for the header.</summary>
    /// <param name="request">The request, before routing.</param>
    /// <param name="hub">The hub the ticket must have been minted for.</param>
    /// <param name="ticket">The ticket's value.</param>
    /// <returns>
    ///     <c>true</c> when the path is exactly a hub's, the <c>Authorization</c> header is absent and
    ///     the query carries one <c>ticket</c>. A header, when present, decides on its own — a ticket
    ///     beside a refused header is not a second chance.
    /// </returns>
    public static bool IsRedeemableOn(HttpRequest request, out string hub, out string ticket) {
        ArgumentNullException.ThrowIfNull(request);

        hub = "";
        ticket = "";

        if (request.Headers.Authorization.Count > 0) {
            return false;
        }

        if (HubRedeemableOn(request.Path) is not { } named) {
            return false;
        }

        if (!request.Query.TryGetValue(QueryParameter, out var values) || values.Count != 1) {
            return false;
        }

        var value = values[0] ?? "";

        if (value.Length == 0) {
            return false;
        }

        hub = named;
        ticket = value;
        return true;
    }

    /// <summary>A fresh ticket value: 32 random bytes, base64url, no padding.</summary>
    public static string NewValue() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>When a ticket minted now stops working: <see cref="Lifetime" /> from now, or the token's own expiry if sooner.</summary>
    public static DateTimeOffset ExpiryFor(TokenClaims claims, DateTimeOffset now) {
        var byLifetime = now + Lifetime;
        return claims.ExpiresAt < byLifetime ? claims.ExpiresAt : byLifetime;
    }

    /// <summary>The body <c>POST /hubs/{hub}/ticket</c> answers with.</summary>
    /// <param name="ticket">The minted ticket.</param>
    /// <param name="hub">The hub it opens.</param>
    public static string Body(HubTicket ticket, string hub) =>
        new JsonObject {
            ["ticket"] = ticket.Value,
            ["hub"] = "/hubs/" + hub,
            ["expiresAt"] = ticket.ExpiresAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
        }.ToJsonString();
}

/// <summary>What <see cref="IHubTicketStore.IssueAsync" /> hands back.</summary>
/// <param name="Value">The opaque value the upgrade carries as <c>?ticket=</c>.</param>
/// <param name="ExpiresAt">When it stops being redeemable.</param>
readonly record struct HubTicket(string Value, DateTimeOffset ExpiresAt);

/// <summary>
///     Where minted tickets wait to be redeemed.
/// </summary>
/// <remarks>
///     ⚠ <b>Two implementations, chosen the way stage 5's counters are.</b> The gateway is stateless
///     across pods — docs/plan/10 § What the gateway must never do — so a ticket minted on one pod
///     has to be redeemable on the pod the ingress sends the WebSocket to. <see cref="RedisHubTicketStore" />
///     is the deployed shape and <see cref="InMemoryHubTicketStore" /> is the one-pod shape, and
///     <c>GatewayServiceCollectionExtensions</c> picks by whether an <c>IConnectionMultiplexer</c> is
///     registered, exactly as it does for <c>IRateLimitCounters</c>.
/// </remarks>
interface IHubTicketStore {
    /// <summary>Mints a ticket for these claims and this hub.</summary>
    /// <param name="claims">What stage 2 established for the minting request.</param>
    /// <param name="hub">The hub the ticket may open. One of <see cref="HubNames" />.</param>
    /// <param name="cancellationToken">Cancels the store write.</param>
    Task<HubTicket> IssueAsync(TokenClaims claims, string hub, CancellationToken cancellationToken = default);

    /// <summary>Spends a ticket.</summary>
    /// <param name="ticket">The value from the query string.</param>
    /// <param name="hub">The hub the request is for. A ticket minted for another hub is not redeemed.</param>
    /// <param name="cancellationToken">Cancels the store read.</param>
    /// <returns>
    ///     The claims the ticket carried, or <see langword="null" /> when it is unknown, expired, spent,
    ///     or for another hub. ⚠ Whatever the reason, the ticket is gone afterwards: a mismatched hub
    ///     spends it too, so a value that was tried on the wrong hub cannot then be tried on the right one.
    /// </returns>
    Task<TokenClaims?> RedeemAsync(string ticket, string hub, CancellationToken cancellationToken = default);
}

/// <summary>
///     Tickets in this process's memory. For a single-pod development run and for tests.
/// </summary>
/// <remarks>
///     ⚠ <b>Correct for one pod and wrong for N</b>, for the reason <c>InMemoryRateLimitCounters</c>
///     is: a ticket minted here is unknown to every other pod, so behind an ingress with no session
///     affinity the upgrade fails on the pod it lands on. Expired entries are swept on the next issue
///     rather than by a timer, which keeps the store a dictionary and a clock and nothing else; the
///     sweep is bounded by how many tickets were minted in the last thirty seconds.
/// </remarks>
sealed class InMemoryHubTicketStore(IClock clock) : IHubTicketStore {
    readonly ConcurrentDictionary<string, (TokenClaims Claims, string Hub, DateTimeOffset ExpiresAt)> tickets =
        new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task<HubTicket> IssueAsync(TokenClaims claims, string hub, CancellationToken cancellationToken = default) {
        ArgumentException.ThrowIfNullOrEmpty(hub);
        cancellationToken.ThrowIfCancellationRequested();

        var now = clock.UtcNow;

        foreach (var (value, entry) in tickets) {
            if (entry.ExpiresAt <= now) {
                tickets.TryRemove(value, out _);
            }
        }

        var ticket = new HubTicket(HubTickets.NewValue(), HubTickets.ExpiryFor(claims, now));
        tickets[ticket.Value] = (claims, hub, ticket.ExpiresAt);

        return Task.FromResult(ticket);
    }

    /// <inheritdoc />
    public Task<TokenClaims?> RedeemAsync(string ticket, string hub, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(ticket);
        cancellationToken.ThrowIfCancellationRequested();

        if (!tickets.TryRemove(ticket, out var entry)) {
            return Task.FromResult<TokenClaims?>(null);
        }

        var live = entry.ExpiresAt > clock.UtcNow && string.Equals(entry.Hub, hub, StringComparison.Ordinal);

        return Task.FromResult<TokenClaims?>(live ? entry.Claims : null);
    }
}

/// <summary>How a ticket's claims are written to and read from Redis.</summary>
static class HubTicketCodec {
    /// <summary>The key a ticket lives under. Prefixed so a ticket cannot collide with a rate-limit window.</summary>
    public static string Key(string ticket) => "hub-ticket:" + ticket;

    /// <summary>The claims and the hub, as one JSON document.</summary>
    public static string Encode(TokenClaims claims, string hub) =>
        JsonSerializer.Serialize(new StoredTicket(claims, hub));

    /// <summary>The reverse, or <see langword="null" /> for bytes this codec did not write.</summary>
    public static (TokenClaims Claims, string Hub)? Decode(string? json) {
        if (string.IsNullOrEmpty(json)) {
            return null;
        }

        try {
            var stored = JsonSerializer.Deserialize<StoredTicket>(json);
            return stored is null ? null : (stored.Claims, stored.Hub);
        } catch (JsonException) {
            return null;
        }
    }

    sealed record StoredTicket(TokenClaims Claims, string Hub);
}
