using System.Collections.Concurrent;
using System.Diagnostics;

namespace CyberCloud.Load.Scenarios;

/// <summary>
///     Issues requests at a fixed rate whether or not the previous ones have answered, and records
///     what each one took.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Open loop, on purpose.</b> A closed-loop driver — N workers each waiting for an
///         answer before sending the next — measures the system at whatever rate the system can
///         answer, which is never the rate the row in docs/plan/23 names. Issuing on a clock is what
///         "5 000 rps" means; if the system falls behind, the queue grows and the percentiles say so.
///         The in-flight cap keeps a stalled system from taking the process with it, and a request
///         shed by the cap is counted as an error, never silently dropped.
///     </para>
///     <para>
///         ⚠ <b>The first seconds are warm-up and are not counted.</b> Every first request activates
///         grains, fills the JWKS cache and opens connections; a p99 that includes them is a p99 of
///         the cold path, which is a different number — the ReBAC scenario measures that one
///         deliberately and separately.
///     </para>
/// </remarks>
public sealed class OpenLoopDriver(
    double requestsPerSecond,
    TimeSpan warmUp,
    TimeSpan window,
    int inFlightCap = 2_000) {
    readonly ConcurrentBag<double> latencies = [];
    readonly ConcurrentBag<(double At, double Ms)> timeline = [];
    int errors;
    int shed;
    int inFlight;

    /// <summary>What went wrong, by kind, for the report.</summary>
    public ConcurrentDictionary<string, int> Failures { get; } = new(StringComparer.Ordinal);

    /// <summary>
    ///     Runs the clock, calling <paramref name="request" /> once per tick and recording its outcome.
    /// </summary>
    /// <param name="request">
    ///     One request. Returns <see langword="null" /> on success, or a short reason on failure.
    ///     Throwing counts as a failure named after the exception.
    /// </param>
    /// <param name="cancellationToken">The test's token.</param>
    /// <returns>The distribution over the measured window.</returns>
    public async Task<Distribution> RunAsync(
        Func<int, CancellationToken, Task<string?>> request,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(request);

        var interval = TimeSpan.FromSeconds(1 / requestsPerSecond);
        var total = warmUp + window;
        var clock = Stopwatch.StartNew();
        var pending = new ConcurrentBag<Task>();
        var sequence = 0;
        var next = TimeSpan.Zero;

        // ⚠ Several requests per wake-up, not one per Task.Delay. Windows' timer wakes a Task.Delay
        // every 15 ms or so, and 500 rps is one request every 2 ms; a loop that slept once per
        // request would issue 65 rps and call it 500. Each wake-up issues everything that fell due
        // since the last one, so the average rate is the asked-for rate and the bursts are the
        // clock's granularity, which is what a real client population produces anyway.
        while (clock.Elapsed < total) {
            if (next > clock.Elapsed) {
                await Task.Delay(TimeSpan.FromMilliseconds(1), cancellationToken);
                continue;
            }

            next += interval;
            var measured = clock.Elapsed >= warmUp;
            var index = sequence++;

            if (Volatile.Read(ref inFlight) >= inFlightCap) {
                if (measured) {
                    Interlocked.Increment(ref shed);
                    Interlocked.Increment(ref errors);
                    Failures.AddOrUpdate("shed by the in-flight cap", 1, static (_, n) => n + 1);
                }

                continue;
            }

            Interlocked.Increment(ref inFlight);

            var issuedAt = (clock.Elapsed - warmUp).TotalSeconds;

            pending.Add(
                Task.Run(
                    async () => {
                        var started = Stopwatch.GetTimestamp();

                        try {
                            var failure = await request(index, cancellationToken);
                            var took = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

                            if (!measured) {
                                return;
                            }

                            if (failure is null) {
                                latencies.Add(took);
                                timeline.Add((issuedAt, took));
                            } else {
                                Interlocked.Increment(ref errors);
                                Failures.AddOrUpdate(failure, 1, static (_, n) => n + 1);
                            }
                        } catch (Exception ex) when (ex is not OperationCanceledException) {
                            if (measured) {
                                Interlocked.Increment(ref errors);
                                Failures.AddOrUpdate(ex.GetType().Name, 1, static (_, n) => n + 1);
                            }
                        } finally {
                            Interlocked.Decrement(ref inFlight);
                        }
                    },
                    CancellationToken.None
                )
            );
        }

        await Task.WhenAll(pending);

        return Distribution.Of([.. latencies], errors, window, requestsPerSecond);
    }

    /// <summary>How many requests the in-flight cap refused to issue.</summary>
    public int Shed => shed;

    /// <summary>
    ///     The slowest successful request in each <paramref name="bucket" /> of the measured window,
    ///     in milliseconds, in order — so a tail can be placed in time. The fifth load run's write
    ///     p99 was 7.6 s with a p50 of 35 ms; a distribution says that a tail exists, and this says
    ///     when it was.
    /// </summary>
    /// <param name="bucket">The width of a bucket.</param>
    /// <returns>One number per bucket, from the start of the measured window.</returns>
    public IReadOnlyList<double> WorstPer(TimeSpan bucket) {
        if (timeline.IsEmpty) {
            return [];
        }

        var width = bucket.TotalSeconds;
        var buckets = new double[(int)Math.Floor(timeline.Max(static x => x.At) / width) + 1];

        foreach (var (at, ms) in timeline) {
            var index = Math.Clamp((int)Math.Floor(at / width), 0, buckets.Length - 1);
            buckets[index] = Math.Max(buckets[index], ms);
        }

        return buckets.Select(static x => Math.Round(x)).ToList();
    }
}
