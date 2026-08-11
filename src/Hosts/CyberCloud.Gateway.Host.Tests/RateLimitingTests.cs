using CyberCloud.Gateway.Host.Http;
using CyberCloud.Gateway.Host.RateLimiting;
using CyberCloud.Gateway.Host.Routing;
using CyberCloud.Gateway.Host.Tests.Infrastructure;
using NSubstitute;

namespace CyberCloud.Gateway.Host.Tests;

/// <summary>
///     docs/plan/10 § Rate limiting: the five buckets, the <c>429</c> shape, the two exemptions, and
///     the one property that matters most — a flood costs no grain call.
/// </summary>
public sealed class RateLimitingTests {
    [Fact]
    public void TheFiveBucketsAreTheFiveTheDocumentGives() {
        RateLimitBuckets.All.Length.ShouldBe(5);

        RateLimitBuckets.SubscriptionReads.Limit.ShouldBe(12_000);
        RateLimitBuckets.SubscriptionReads.Window.ShouldBe(TimeSpan.FromMinutes(5));
        RateLimitBuckets.SubscriptionWrites.Limit.ShouldBe(1_200);
        RateLimitBuckets.SubscriptionWrites.Window.ShouldBe(TimeSpan.FromMinutes(5));
        RateLimitBuckets.TenantTotal.Limit.ShouldBe(30_000);
        RateLimitBuckets.TenantTotal.Window.ShouldBe(TimeSpan.FromMinutes(5));
        RateLimitBuckets.IpUnauthenticated.Limit.ShouldBe(60);
        RateLimitBuckets.IpUnauthenticated.Window.ShouldBe(TimeSpan.FromMinutes(1));
        RateLimitBuckets.UserInteractive.Limit.ShouldBe(600);
        RateLimitBuckets.UserInteractive.Window.ShouldBe(TimeSpan.FromMinutes(1));
    }

    /// <summary>
    ///     ⚠ THE test for this stage. docs/plan/10 § Request pipeline: <i>"a rate limiter that costs
    ///     a grain call is a rate limiter that amplifies an attack"</i>.
    /// </summary>
    /// <remarks>
    ///     The flood runs past the limit and into the <c>429</c>s, and the assertion is on the grain
    ///     factory the whole gateway shares: not one call, at any point, allowed or refused.
    /// </remarks>
    [Fact]
    public async Task AFloodPastTheLimitCostsNoGrainCall() {
        var gateway = new GatewayHarness();
        var throttled = 0;

        for (var i = 0; i < RateLimitBuckets.IpUnauthenticated.Limit + 20; i++) {
            var response = await gateway.SendAsync("GET", "/openapi", null);

            if (response.Status == StatusCodes.Status429TooManyRequests) {
                throttled++;
            }
        }

        throttled.ShouldBe(20);
        gateway.Grains.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task The429CarriesRetryAfterAndTheRemainingHeaders() {
        var gateway = new GatewayHarness();
        var token = gateway.Token(GatewayHarness.TenantA);
        var path = GatewayHarness.ResourcePath(GatewayHarness.TenantA);

        GatewayResponse? refused = null;

        for (var i = 0; i <= RateLimitBuckets.UserInteractive.Limit; i++) {
            var response = await gateway.SendAsync("GET", path, token);

            if (response.Status == StatusCodes.Status429TooManyRequests) {
                refused = response;
                break;
            }
        }

        refused.ShouldNotBeNull();

        // Every cloud SDK's retry policy already understands these — docs/plan/10 § Rate limiting.
        int.Parse(refused.Header(GatewayHeaders.RetryAfter), System.Globalization.CultureInfo.InvariantCulture)
            .ShouldBeGreaterThan(0);

        refused.Header(GatewayHeaders.RemainingUserInteractive).ShouldBe("0");
        refused.Body.ShouldContain("user-interactive");
        refused.Body.ShouldContain("600");
    }

    [Fact]
    public async Task TheRemainingHeadersAreOnASuccessToo() {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "GET",
            GatewayHarness.ResourcePath(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA)
        );

        response.Status.ShouldBe(StatusCodes.Status200OK);

        // ⚠ A client that only learns its budget after exceeding it cannot pace itself.
        response.Header(GatewayHeaders.RemainingSubscriptionReads).ShouldBe("11999");
        response.Header(GatewayHeaders.RemainingTenantTotal).ShouldBe("29999");
        response.Header(GatewayHeaders.RemainingUserInteractive).ShouldBe("599");
    }

    [Fact]
    public async Task AWriteDrawsOnTheWriteBucketAndNotTheReadBucket() {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "PUT",
            GatewayHarness.ResourcePath(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA),
            body: "{\"properties\":{}}"
        );

        response.Header(GatewayHeaders.RemainingSubscriptionWrites).ShouldBe("1199");
        response.Header(GatewayHeaders.RemainingSubscriptionReads).ShouldBe("");
    }

    /// <summary>
    ///     docs/plan/10 § Rate limiting: long-poll and SignalR are exempt from the request-count
    ///     limits and get a concurrency limit instead.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Counting a 30-second long-poll against a 5-minute window is how you rate-limit your
    ///     own portal.</b> A tab holding one poll open spends budget while doing nothing, and the
    ///     symptom — the portal throttling itself while idle — reads as a platform fault.
    /// </remarks>
    [Theory]
    [InlineData("/hubs/resources", "")]
    [InlineData("/operations/11111111-1111-1111-1111-111111111111", "wait=30")]
    public async Task LongPollAndSignalRAreExemptFromTheCountBuckets(string path, string extraQuery) {
        var gateway = new GatewayHarness();
        var token = gateway.Token(GatewayHarness.TenantA);

        var query = "api-version=" + OneTypeRegistry.TheVersion
            + (extraQuery.Length == 0 ? "" : "&" + extraQuery);

        for (var i = 0; i < RateLimitBuckets.UserInteractive.Limit + 50; i++) {
            var response = await gateway.SendAsync("GET", path, token, query);
            response.Status.ShouldNotBe(StatusCodes.Status429TooManyRequests);
        }

        // ⚠ And no remaining-budget header either: the request was never counted, so reporting a
        // remaining count would be reporting a number that means nothing.
        var last = await gateway.SendAsync("GET", path, token, query);
        last.Header(GatewayHeaders.RemainingUserInteractive).ShouldBe("");
    }

    [Fact]
    public void TheClassifierSeesTheExemptionsWithoutTheRegistry() {
        var query = new QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues> {
            ["wait"] = "30"
        });

        GatewayRouter.Classify("/hubs/resources", "POST", new QueryCollection()).ShouldBe(RequestClass.Hub);
        GatewayRouter.Classify("/operations/x", "GET", query).ShouldBe(RequestClass.LongPoll);
        GatewayRouter.Classify("/operations/x", "GET", new QueryCollection()).ShouldBe(RequestClass.Read);
        GatewayRouter.Classify("/tenants/x", "PUT", new QueryCollection()).ShouldBe(RequestClass.Write);
    }

    /// <summary>
    ///     The exempt classes get connections per tenant and streams per connection instead.
    /// </summary>
    [Fact]
    public void TheConcurrencyLimiterCapsConnectionsPerTenantAndStreamsPerConnection() {
        var limiter = new ProcessConcurrencyLimiter(new() { ConnectionsPerTenant = 2, StreamsPerConnection = 3 });

        limiter.TryAcquireConnection(GatewayHarness.TenantA).ShouldBeTrue();
        limiter.TryAcquireConnection(GatewayHarness.TenantA).ShouldBeTrue();
        limiter.TryAcquireConnection(GatewayHarness.TenantA).ShouldBeFalse();

        // ⚠ Per tenant, so a tenant at its cap does not affect another one.
        limiter.TryAcquireConnection(GatewayHarness.TenantB).ShouldBeTrue();

        limiter.ReleaseConnection(GatewayHarness.TenantA);
        limiter.TryAcquireConnection(GatewayHarness.TenantA).ShouldBeTrue();

        limiter.PermitsStream(2).ShouldBeTrue();
        limiter.PermitsStream(3).ShouldBeFalse();
    }

    [Fact]
    public async Task TheWindowSlidesRatherThanResettingOnABoundary() {
        var clock = new FakeClock();
        var counters = new InMemoryRateLimitCounters(clock);

        var first = await counters.CountAsync("k", TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);
        first.Count.ShouldBe(1);

        clock.Advance(TimeSpan.FromSeconds(30));
        (await counters.CountAsync("k", TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken)).Count.ShouldBe(2);

        // The first entry has now fallen out of the window; the second has not.
        clock.Advance(TimeSpan.FromSeconds(31));
        var third = await counters.CountAsync("k", TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);
        third.Count.ShouldBe(2);

        // ⚠ Retry-After is the time until the OLDEST entry leaves, not the whole window. Telling a
        // caller to wait a minute when the budget frees in seconds stalls a well-behaved SDK.
        third.RetryAfter.ShouldBeLessThan(TimeSpan.FromMinutes(1));
    }
}
