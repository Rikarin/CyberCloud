using CyberCloud.Core.Time;
using System.Collections.Concurrent;

namespace CyberCloud.Gateway.Host.RateLimiting;

/// <summary>
///     The same sliding window, in one process. For a single-pod development run and for tests.
/// </summary>
/// <remarks>
///     ⚠ <b>Correct for one pod and wrong for N, which is why it is not the default.</b> Each pod
///     would enforce the whole limit on its own, so a tenant behind four pods gets four times the
///     budget. That is the entire reason docs/plan/10 § Request pipeline says <i>Redis-backed</i>. It
///     is here because it exercises the same interface with the same semantics — including the
///     honest <c>Retry-After</c> — so a test of the <i>limiting</i> does not need a container, and
///     because a <c>dotnet run</c> with no Redis should still refuse a runaway loop.
/// </remarks>
sealed class InMemoryRateLimitCounters(IClock clock) : IRateLimitCounters {
    readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> windows = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task<WindowCount> CountAsync(
        string key,
        TimeSpan window,
        CancellationToken cancellationToken = default
    ) {
        ArgumentException.ThrowIfNullOrEmpty(key);
        cancellationToken.ThrowIfCancellationRequested();

        var now = clock.UtcNow;
        var stamps = windows.GetOrAdd(key, _ => new Queue<DateTimeOffset>());

        lock (stamps) {
            while (stamps.Count > 0 && stamps.Peek() <= now - window) {
                stamps.Dequeue();
            }

            stamps.Enqueue(now);

            var oldest = stamps.Peek();
            var retryAfter = oldest + window - now;

            return Task.FromResult(new WindowCount(
                stamps.Count,
                retryAfter < TimeSpan.Zero ? TimeSpan.Zero : retryAfter
            ));
        }
    }
}
