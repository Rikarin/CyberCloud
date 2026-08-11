using System.Collections.Immutable;

namespace CyberCloud.Gateway.Host.RateLimiting;

/// <summary>
///     One of docs/plan/10 § Rate limiting's five buckets.
/// </summary>
/// <param name="Name">The bucket's name, as it appears in a <c>429</c> message and a metric.</param>
/// <param name="Limit">How many requests the window allows.</param>
/// <param name="Window">The window.</param>
/// <param name="RemainingHeader">
///     The <c>x-ms-ratelimit-remaining-*</c> header this bucket reports on, or empty when it has
///     none. ⚠ The per-IP bucket has none on purpose: an unauthenticated caller learning how much
///     budget is left is being handed a tuning parameter for a credential-stuffing run.
/// </param>
readonly record struct RateLimitBucket(string Name, int Limit, TimeSpan Window, string RemainingHeader);

/// <summary>
///     The five buckets, with the document's numbers.
/// </summary>
/// <remarks>
///     ⚠ <b>The defaults are Azure's and the rationales are not interchangeable.</b> docs/plan/10
///     § Rate limiting gives each one a different reason — reads are capped where ARM caps them,
///     writes are ten times lower <i>"because writes cost a reconcile"</i>, and the per-tenant total
///     exists to stop <i>"one subscription's automation starving the tenant's portal"</i>. Raising one
///     because a customer complained does not license raising the others.
/// </remarks>
static class RateLimitBuckets {
    /// <summary>Per subscription, reads. 12 000 / 5 min — ARM's read limit.</summary>
    public static RateLimitBucket SubscriptionReads { get; } = new(
        "subscription-reads",
        12_000,
        TimeSpan.FromMinutes(5),
        Http.GatewayHeaders.RemainingSubscriptionReads
    );

    /// <summary>Per subscription, writes. 1 200 / 5 min — a write costs a reconcile.</summary>
    public static RateLimitBucket SubscriptionWrites { get; } = new(
        "subscription-writes",
        1_200,
        TimeSpan.FromMinutes(5),
        Http.GatewayHeaders.RemainingSubscriptionWrites
    );

    /// <summary>Per tenant, total. 30 000 / 5 min — one subscription must not starve the portal.</summary>
    public static RateLimitBucket TenantTotal { get; } = new(
        "tenant-total",
        30_000,
        TimeSpan.FromMinutes(5),
        Http.GatewayHeaders.RemainingTenantTotal
    );

    /// <summary>Per IP, unauthenticated. 60 / min — sign-in, token, discovery.</summary>
    public static RateLimitBucket IpUnauthenticated { get; } = new(
        "ip-unauthenticated",
        60,
        TimeSpan.FromMinutes(1),
        ""
    );

    /// <summary>Per user, interactive. 600 / min — the portal is chatty.</summary>
    public static RateLimitBucket UserInteractive { get; } = new(
        "user-interactive",
        600,
        TimeSpan.FromMinutes(1),
        Http.GatewayHeaders.RemainingUserInteractive
    );

    /// <summary>All five, for a test that asserts the set has not quietly changed.</summary>
    public static ImmutableArray<RateLimitBucket> All { get; } = [
        SubscriptionReads,
        SubscriptionWrites,
        TenantTotal,
        IpUnauthenticated,
        UserInteractive
    ];
}
