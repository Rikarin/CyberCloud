using CyberCloud.Core.Time;
using StackExchange.Redis;
using System.Collections.Concurrent;

namespace CyberCloud.Identity.SignIn;

/// <summary>
///     The production lockout counter: one Redis <c>INCR</c>, no grain. docs/plan/11 § Credentials.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This type takes no <c>IGrainFactory</c> and must never take one.</b> docs/plan/11
///         § Credentials: "an authentication endpoint whose failure path costs a grain activation is
///         a denial-of-service amplifier." An unauthenticated attacker chooses the address, so they
///         would be choosing which activations the cluster creates — a request that costs the
///         attacker one packet and the cluster a durable-tier read is the definition of an
///         amplifier.
///     </para>
///     <para>
///         ⚠ <b>Each key is its own Redis Cluster hash tag, deliberately.</b> The rest of the hot
///         tier tags by tenant (<c>TenantHotKeys</c>) so a tenant's state lives on one shard and a
///         tenant delete is not a fan-out. That reasoning does not apply here and its opposite does:
///         nothing ever reads two lockout counters in one command, and a shared tag would funnel
///         every failed sign-in in the platform through a single slot — which is precisely the hot
///         spot the counter exists to survive.
///     </para>
///     <para>
///         <b>The <c>INCR</c>-then-<c>EXPIRE</c> pair is not atomic and does not need to be.</b> A
///         crash between them leaves a counter with no expiry, which fails <i>closed</i> — the
///         account stays locked slightly too long rather than not at all. The expiry is refreshed on
///         every failure anyway, so the window is sliding, which is what "exponential backoff over a
///         window" means.
///     </para>
/// </remarks>
public sealed class RedisLockoutCounter(IDatabase database, IClock clock) : ILockoutCounter {
    /// <summary>The suffix on the key that records when the lockout lifts.</summary>
    const string UntilSuffix = ":until";

    /// <inheritdoc />
    public async Task<bool> IsLockedAsync(LockoutKey key, CancellationToken cancellationToken = default) {
        cancellationToken.ThrowIfCancellationRequested();

        var until = await database.StringGetAsync(Physical(key) + UntilSuffix);
        return until.HasValue
            && long.TryParse(until.ToString(), out var ticks)
            && clock.UtcNow.UtcTicks < ticks;
    }

    /// <inheritdoc />
    public async Task<int> RecordFailureAsync(LockoutKey key, CancellationToken cancellationToken = default) {
        cancellationToken.ThrowIfCancellationRequested();

        var physical = Physical(key);
        var count = await database.StringIncrementAsync(physical);
        await database.KeyExpireAsync(physical, LockoutPolicy.Window);

        var delay = LockoutPolicy.DelayFor((int)Math.Min(count, int.MaxValue));
        if (delay > TimeSpan.Zero) {
            var until = clock.UtcNow + delay;
            await database.StringSetAsync(physical + UntilSuffix, until.UtcTicks, delay);
        }

        return (int)Math.Min(count, int.MaxValue);
    }

    /// <inheritdoc />
    public async Task ResetAsync(LockoutKey key, CancellationToken cancellationToken = default) {
        cancellationToken.ThrowIfCancellationRequested();

        var physical = Physical(key);
        await database.KeyDeleteAsync(physical);
        await database.KeyDeleteAsync(physical + UntilSuffix);
    }

    static string Physical(LockoutKey key) => "{" + key.Value + "}";
}

/// <summary>
///     A lockout counter in process memory. For a single-silo development host and for tests.
/// </summary>
/// <remarks>
///     ⚠ <b>Not production, and the reason is not performance.</b> A per-process counter means an
///     attacker spreading attempts across silos gets <c>N</c> times the free attempts, and a silo
///     restart clears every lockout. It is here so the sign-in path is exercisable without a Redis,
///     and so the "the failure path touches no grain" test asserts against a real implementation of
///     the interface rather than a stub written for the test.
/// </remarks>
public sealed class InMemoryLockoutCounter(IClock clock) : ILockoutCounter {
    readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task<bool> IsLockedAsync(LockoutKey key, CancellationToken cancellationToken = default) {
        cancellationToken.ThrowIfCancellationRequested();

        if (!entries.TryGetValue(key.Value, out var entry)) {
            return Task.FromResult(false);
        }

        return Task.FromResult(clock.UtcNow < entry.LockedUntil);
    }

    /// <inheritdoc />
    public Task<int> RecordFailureAsync(LockoutKey key, CancellationToken cancellationToken = default) {
        cancellationToken.ThrowIfCancellationRequested();

        var now = clock.UtcNow;

        var updated = entries.AddOrUpdate(
            key.Value,
            _ => Next(new(0, now, now), now),
            (_, existing) => Next(existing, now)
        );

        return Task.FromResult(updated.Failures);
    }

    /// <inheritdoc />
    public Task ResetAsync(LockoutKey key, CancellationToken cancellationToken = default) {
        cancellationToken.ThrowIfCancellationRequested();

        entries.TryRemove(key.Value, out _);
        return Task.CompletedTask;
    }

    static Entry Next(Entry existing, DateTimeOffset now) {
        // The window slides: a run of failures with a quiet period in the middle starts over, which
        // is what makes the ladder a defence against a script rather than a punishment for a person
        // who mistypes twice a week.
        var failures = now - existing.LastFailure > LockoutPolicy.Window ? 1 : existing.Failures + 1;
        return new(failures, now, now + LockoutPolicy.DelayFor(failures));
    }

    sealed record Entry(int Failures, DateTimeOffset LastFailure, DateTimeOffset LockedUntil);
}
