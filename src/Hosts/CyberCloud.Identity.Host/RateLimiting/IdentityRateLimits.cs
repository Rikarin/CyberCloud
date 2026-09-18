using CyberCloud.ServiceDefaults.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Serialization;

namespace CyberCloud.Identity.Host.RateLimiting;

/// <summary>
///     One per-IP bucket on the interactive surface.
/// </summary>
/// <param name="Name">The bucket's name, as it appears in a <c>429</c> body and a metric.</param>
/// <param name="Limit">How many requests the window allows from one address.</param>
/// <param name="Window">The window.</param>
public readonly record struct IdentityRateLimitBucket(string Name, int Limit, TimeSpan Window);

/// <summary>
///     The per-IP buckets this host counts, and the argument for each. docs/plan/11 § Credentials:
///     <i>"per-account exponential backoff with a global per-IP limit"</i>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             Per IP and nothing finer, on purpose, and that is what keeps the uniform-failure
///             property.
///         </b> docs/plan/11 § Credentials makes sign-in and sign-up "return the same
///         response and take the same time whether or not the account exists". A limit keyed by the
///         address in the body would be a second answer for an address somebody is hammering — and
///         which addresses get hammered is exactly what a probe wants to learn. A limit keyed by the
///         connection's address is a fact about the caller and nothing else: the <c>429</c> depends
///         on how many requests <i>this address</i> has sent, the body is never read to decide it,
///         a made-up address and a real one are counted alike, and the timing floor below the
///         limit is untouched. <c>GrantsOverHttpTests.ThePerIpLimitOnSignUpBeginTripsAndRecovers</c>
///         pins that the refusal reads the same for both.
///     </para>
///     <para>
///         ⚠ <b>Two buckets, not one per endpoint.</b> <c>/api/signup/begin</c> issues an OTP per
///         call — a mail per call, once #93 lands an MTA — and the grain behind it caps issues per
///         sign-up, not per caller, so a caller minting sign-ups is uncapped without this.
///         <see cref="SignUpBegin" /> is that cap. The code-verify endpoints — <c>/api/signup/verify</c>,
///         <c>/api/signin/otp</c>, <c>/api/signin/totp</c> and <c>/api/signin/recovery-code</c> —
///         each take a code a caller can guess at, and the grain behind each caps attempts per
///         <i>code</i> or per person; <see cref="CodeVerify" /> caps them per caller, across codes,
///         so a caller cannot buy more guesses by opening more sign-ups. A recovery code is ten
///         characters and unguessable in practice, and it is in the bucket anyway, because "every
///         route that takes a code" is a rule a reader can check and "every route that takes a
///         short code" is a judgement they would have to re-make. The password endpoint is not here:
///         <c>ILockoutCounter</c> already backs it off per account and the dummy hash makes each
///         attempt cost a caller what it costs the platform.
///     </para>
///     <para>
///         ⚠ <b>The numbers are per address, and an address is sometimes an office.</b> Ten sign-ups
///         from one NAT in ten minutes is a floor nobody honest reaches; sixty code answers a minute
///         is one every second from a whole building. Both sit above what a person does and below
///         what a script does, which is the only property a per-IP limit can have — the per-account
///         controls are the precise ones, and these exist so the precise ones are not the amplifier
///         docs/plan/11 § Credentials names.
///     </para>
///     <para>
///         The counters are <see cref="IRateLimitCounters" /> — the gateway's sliding window, shared
///         through <c>CyberCloud.ServiceDefaults</c>. The identity host registers the same pair the
///         gateway does, on the same rule: Redis when the container holds an
///         <c>IConnectionMultiplexer</c>, in process otherwise. ⚠ No host composition in this
///         repository registers one yet, so today every deployment counts per replica — N replicas
///         are N times each budget — and the Redis branch is there for the host change that wires
///         the multiplexer, the same change <c>ILockoutCounter</c>'s registration is waiting on.
///     </para>
/// </remarks>
public static class IdentityRateLimits {
    /// <summary>Per IP, <c>/api/signup/begin</c>: 10 per 10 minutes. Each call issues a code.</summary>
    public static IdentityRateLimitBucket SignUpBegin { get; } = new("signup-begin", 10, TimeSpan.FromMinutes(10));

    /// <summary>Per IP, the code-verify endpoints: 60 per minute. Each call is a guess.</summary>
    public static IdentityRateLimitBucket CodeVerify { get; } = new("code-verify", 60, TimeSpan.FromMinutes(1));

    /// <summary>Both, for a test that asserts the set has not quietly changed.</summary>
    public static ImmutableArray<IdentityRateLimitBucket> All { get; } = [SignUpBegin, CodeVerify];

    /// <summary>The sentence a refused caller reads — the same for every bucket and every address.</summary>
    public const string RefusedMessage = "Too many attempts from this address. Wait a moment and try again.";

    /// <summary>
    ///     Puts <paramref name="bucket" /> in front of an endpoint: the request is counted before the
    ///     handler runs, and refused with <c>429</c> when the window is full.
    /// </summary>
    /// <param name="endpoint">The route.</param>
    /// <param name="bucket">Which bucket counts it.</param>
    /// <remarks>
    ///     An endpoint filter rather than middleware, so the bucket is named where the route is and
    ///     an endpoint that carries none is visibly uncounted — the bucket is also stamped on the
    ///     endpoint as metadata, which is what
    ///     <c>TokenPolicyDocumentTests.TheRateLimitedRoutesAreExactlyTheOnesThatIssueOrTakeACode</c>
    ///     reads. ⚠ Filters run after authorization, so on a <c>RequireAuthorization()</c> route an
    ///     anonymous caller is a <c>401</c> before it is counted; that is the right order, because a
    ///     caller with no session cannot present a code to guess against.
    /// </remarks>
    public static RouteHandlerBuilder RateLimited(this RouteHandlerBuilder endpoint, IdentityRateLimitBucket bucket) {
        ArgumentNullException.ThrowIfNull(endpoint);

        return endpoint.WithMetadata(bucket).AddEndpointFilter(new RateLimitFilter(bucket));
    }

    sealed class RateLimitFilter(IdentityRateLimitBucket bucket) : IEndpointFilter {
        public async ValueTask<object?> InvokeAsync(
            EndpointFilterInvocationContext context,
            EndpointFilterDelegate next
        ) {
            var limiter = context.HttpContext.RequestServices.GetRequiredService<IdentityRateLimiter>();
            var decision = await limiter.EvaluateAsync(bucket, context.HttpContext, context.HttpContext.RequestAborted);

            if (decision.Allowed) {
                return await next(context);
            }

            context.HttpContext.Response.Headers.RetryAfter =
                decision.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);

            return Results.Json(
                new RateLimitedResponse(RefusedMessage, decision.RetryAfterSeconds),
                statusCode: StatusCodes.Status429TooManyRequests
            );
        }
    }
}

/// <summary>Whether a request may proceed, and what to tell the caller when it may not.</summary>
/// <param name="Allowed">Whether the address is inside the bucket's window.</param>
/// <param name="RetryAfterSeconds">The honest <c>Retry-After</c>, at least one second, when refused.</param>
public readonly record struct IdentityRateLimitDecision(bool Allowed, int RetryAfterSeconds);

/// <summary>
///     Counts a request against one of <see cref="IdentityRateLimits" />' buckets, by the
///     connection's address.
/// </summary>
/// <param name="counters">The sliding window — Redis across replicas, in process on a development run.</param>
/// <remarks>
///     <para>
///         ⚠ <c>RemoteIpAddress</c> and not <c>X-Forwarded-For</c>, for the reason
///         <c>IdentityEndpoints.Describe</c> gives: a caller sets their own headers, and a limit
///         keyed by one would be a limit the caller chooses the key for. Behind the ingress
///         docs/plan/10 puts in front of every host, <c>RemoteIpAddress</c> is the ingress's address
///         for everybody — one bucket for the whole platform, which a hostile caller fills for
///         everyone in ten requests — and the fix is not here but in front: the forwarded-headers
///         middleware, which <c>IdentityComposition.MapIdentityHost</c> runs first when
///         <c>CyberCloud:Identity:TrustedProxies</c> names the ingress, rewrites
///         <c>RemoteIpAddress</c> to the address the ingress appended before this type reads it.
///         <see cref="IdentityHostOptions.TrustedProxies" /> argues the list and
///         <see cref="TrustedProxies" /> why the middleware is conditional.
///     </para>
///     <para>
///         A request with no address at all — a test server's in-memory transport — is counted
///         under one shared key rather than admitted uncounted.
///     </para>
/// </remarks>
public sealed class IdentityRateLimiter(IRateLimitCounters counters) {
    /// <summary>Counts one request and decides.</summary>
    /// <param name="bucket">Which bucket.</param>
    /// <param name="context">The request, for its connection's address.</param>
    /// <param name="cancellationToken">Cancels the round trip.</param>
    public async Task<IdentityRateLimitDecision> EvaluateAsync(
        IdentityRateLimitBucket bucket,
        HttpContext context,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(context);

        var address = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var window = await counters.CountAsync($"rl:{{ip:{address}}}:{bucket.Name}", bucket.Window, cancellationToken);

        if (window.Count <= bucket.Limit) {
            return new(true, 0);
        }

        // At least one second: a Retry-After of 0 invites an immediate retry, which is how a
        // throttled client becomes a busier one.
        return new(false, Math.Max((int)Math.Ceiling(window.RetryAfter.TotalSeconds), 1));
    }
}

/// <summary>The <c>429</c> body — one sentence, and how long to wait.</summary>
/// <param name="Message"><see cref="IdentityRateLimits.RefusedMessage" />, verbatim.</param>
/// <param name="RetryAfterSeconds">The same number the <c>Retry-After</c> header carries.</param>
public sealed record RateLimitedResponse(
    [property: JsonPropertyName("message")]
    string Message,
    [property: JsonPropertyName("retryAfterSeconds")]
    int RetryAfterSeconds
);
