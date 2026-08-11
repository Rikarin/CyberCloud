using CyberCloud.Gateway.Host.RateLimiting;
using Microsoft.Extensions.Logging;
using System.Collections.Immutable;

namespace CyberCloud.Gateway.Host.Hubs;

/// <summary>
///     The interest set of docs/plan/10 § SignalR, with authorization on every subscribe and on every
///     relation change.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Per-subscribe, never per-connect, and re-checked.</b> docs/plan/10 § SignalR is
///         explicit, and the reason is worth keeping in front of whoever edits this: a check that
///         runs once at connect passes at 09:00 and is still delivering that resource group's events
///         at 17:00 to somebody whose access was revoked at 09:05. The live channel would be an
///         authorization bypass with a nice UI, and it would be invisible — the REST API would
///         correctly answer <c>404</c> the whole time.
///     </para>
///     <para>
///         <b>Two things trigger a re-check.</b> <see cref="RecheckAsync" /> called from the tenant's
///         relation-version stream is the production path; calling it directly is what the revoke
///         test does. The stream subscription is deliberately <i>not</i> established in
///         <c>OnActivateAsync</c> here — it needs a configured stream provider, and a grain that
///         throws on activation when one is missing would make every hub connection fail in a
///         deployment that has not wired streams yet. <c>ConnectionStreamBridge</c> is where that
///         wiring belongs and it is owed; the authorization behaviour does not depend on it.
///     </para>
///     <para>
///         ⚠ <b>It uses no grain infrastructure</b> — no <c>this.GetPrimaryKeyString()</c>, no
///         reminders, no persistent state — which is what lets the tests instantiate it directly
///         instead of standing up a cluster. That is a constraint on future edits, not an accident.
///     </para>
/// </remarks>
public sealed class ConnectionGrain(
    IInterestAuthorizer authorizer,
    IConcurrencyLimiter limiter,
    ILogger<ConnectionGrain> logger
)
    : Grain, IConnectionGrain {
    readonly HashSet<ConnectionInterest> interests = [];

    CallerContext caller = new();
    string hub = "";

    /// <inheritdoc />
    public Task<Result> AttachAsync(CallerContext caller, string hub) {
        ArgumentNullException.ThrowIfNull(caller);

        this.caller = caller;
        this.hub = hub;

        // ⚠ Nothing is authorized here. A connection is a socket, not a grant.
        return Task.FromResult(Result.Success);
    }

    /// <inheritdoc />
    public async Task<Result> SubscribeAsync(ConnectionInterest interest) {
        if (caller.TenantId == Guid.Empty) {
            return Result.Failure(
                ErrorCode.AuthorizationFailed,
                "This connection has no caller. Call AttachAsync before subscribing."
            );
        }

        // docs/plan/10 § Rate limiting — the concurrency limit the exempt classes get instead of a
        // request count. An unbounded interest set is a re-check per relation change per interest.
        if (!interests.Contains(interest) && !limiter.PermitsStream(interests.Count)) {
            return Result.Failure(
                ErrorCode.QuotaExceeded,
                $"This connection already holds {interests.Count} subscriptions, which is the limit. "
                + "docs/plan/10 § Rate limiting: long-poll and SignalR get a concurrency limit "
                + "instead of a request count."
            );
        }

        var readable = await authorizer.CanReadAsync(caller, interest.ResourcePath);
        if (readable.TryGetError(out var error)) {
            // ⚠ Whatever the seam said, unchanged. It is a 404 for a resource the caller cannot see,
            // and turning it into a "subscription refused" here would be a second answer to a
            // question that already has one.
            return Result.Failure(error);
        }

        interests.Add(interest);
        return Result.Success;
    }

    /// <inheritdoc />
    public Task<Result> UnsubscribeAsync(ConnectionInterest interest) {
        interests.Remove(interest);
        return Task.FromResult(Result.Success);
    }

    /// <inheritdoc />
    public Task<ImmutableArray<ConnectionInterest>> InterestsAsync() =>
        Task.FromResult(interests.ToImmutableArray());

    /// <inheritdoc />
    public async Task<int> RecheckAsync() {
        if (interests.Count == 0) {
            return 0;
        }

        var dropped = 0;

        foreach (var interest in interests.ToArray()) {
            var readable = await authorizer.CanReadAsync(caller, interest.ResourcePath);
            if (!readable.TryGetError(out _)) {
                continue;
            }

            interests.Remove(interest);
            dropped++;

            logger.LogInformation(
                "Dropped interest {Interest} on hub {Hub} for {Caller}: the caller can no longer "
                + "read it. docs/plan/10 § SignalR — subscription authorization is re-checked on "
                + "relation changes.",
                interest,
                hub,
                caller
            );
        }

        return dropped;
    }

    /// <summary>The caller this connection is bound to. For a hub that needs it back.</summary>
    public CallerContext Caller => caller;
}
