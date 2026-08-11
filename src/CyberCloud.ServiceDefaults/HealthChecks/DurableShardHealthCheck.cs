using CyberCloud.ServiceDefaults.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using System.Globalization;

namespace CyberCloud.ServiceDefaults.HealthChecks;

/// <summary>
///     Which durable shards this silo can actually reach, named one by one.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This exists because a silo starts healthy with a dead shard, and nothing noticed.</b>
///         <c>Orleans.Multitenant</c>'s tenant-unaware bootstrap provider is never constructed — its
///         keyed <c>IGrainStorage</c> registration is overwritten by the multitenant wrapper, so the
///         lifecycle participant resolves the wrapper and the bare provider underneath never runs its
///         <c>Init</c>. <c>BootstrapProviderLivenessTests</c> is that result. The silo joins
///         membership, passes readiness, takes traffic, and only the tenants on the dead shard get
///         errors. At 16 shards that is one-sixteenth of the estate failing while every dashboard is
///         green, and docs/plan/05 § Storage provider wiring now says so.
///     </para>
///     <para>
///         ⚠ <b>UNTAGGED, and that is the decision.</b> It carries neither
///         <see cref="HealthCheckTags.Ready" /> nor <see cref="HealthCheckTags.Live" />, so
///         <c>/health</c> and <c>/alive</c> never run it and it can neither evict a pod from a Service
///         nor restart one. It appears on <c>/api/health</c>, whose predicate is "everything", which
///         is what a scrape and an alert read. That split is the entire point:
///         <b>the operator finds out, the load balancer does not act.</b>
///     </para>
///     <para>
///         <b>The alternative that was rejected: a <see cref="HealthStatus.Degraded" /> check tagged
///         <see cref="HealthCheckTags.Ready" />.</b> It is worse in both directions at once. ASP.NET's
///         default probe predicate treats <c>Degraded</c> as <i>passing</i> — only <c>Unhealthy</c>
///         becomes a 503 — so on the readiness probe it would report a shard outage as a 200 and
///         change nothing, which is a lie with extra steps. And it would sit one word away from
///         becoming <c>Unhealthy</c> in a later edit, at which point an unreachable shard evicts every
///         silo bound to it and concentrates that load on its neighbours: the
///         health-check-induced outage that <see cref="SiloReadinessHealthCheck" />'s remarks refuse
///         by name. Untagged has no such edge. It is also why the status here <i>is</i>
///         <c>Unhealthy</c> rather than <c>Degraded</c>: a check that cannot evict anything should say
///         the true thing, and "a shard is unreachable" is not a degradation of this silo, it is a
///         failure of a dependency.
///     </para>
///     <para>
///         <b>What it probes, and what that costs.</b> One connection per shard and one
///         <c>SELECT 1</c>, cached for <see cref="DurableTierOptions.HealthProbeInterval" /> and
///         single-flighted, so N scrapes in a window cost one round of probes and not N.
///     </para>
///     <list type="number">
///         <item>
///             <b><c>Pooling=false</c>, on the tier's own canonical connection string.</b> The
///             connection is the tier's — same host, port, database and user, so the probe proves the
///             path a grain write takes — but it is deliberately outside the tier's pool. Borrowing
///             from the pool instead would put the probe in competition with grain writes for the
///             <c>MaxPoolSize</c> of 5 that docs/plan/05 § Storage provider wiring budgets per tenant
///             per shard, and a probe that times out because the pool is busy reports "shard down"
///             during exactly the load spike where that is most expensive to believe.
///         </item>
///         <item>
///             <b><c>SELECT 1</c>, not just <c>Open</c>.</b> Behind the transaction-mode PgBouncer
///             that docs/plan/05 calls non-negotiable, opening a connection only proves PgBouncer is
///             up — it hands out a client connection without touching a server one. The statement is
///             what forces PgBouncer to obtain a backend, so it is the cheapest thing that actually
///             proves PostgreSQL is there.
///         </item>
///         <item>
///             <b>The arithmetic, since "a <c>SELECT 1</c> per shard on a timer" is not free at 16+
///             shards.</b> Per silo per interval it is one short-lived backend per shard, held for a
///             connect plus one round trip. At the 30-second default, 16 shards and 30 silos, each
///             shard sees one probe connection per second and at most 30 concurrent if every silo
///             aligned — against the 150 pooled connections those same silos already hold there. Silos
///             start at different times and scrapes arrive at different times, so alignment is a bound
///             rather than a behaviour. Raise the interval before raising the shard count if that
///             bound ever matters.
///         </item>
///     </list>
///     <para>
///         <b>The report names the shard.</b> "Storage is unhealthy" at 16 shards tells an operator
///         to check 16 servers. The description lists the unreachable shard ids, and
///         <see cref="HealthCheckResult.Data" /> carries every shard with either <c>reachable</c> or
///         the failure text, so <c>/api/health</c> is a diagnosis rather than a prompt to go looking.
///     </para>
/// </remarks>
sealed class DurableShardHealthCheck : IHealthCheck, IDisposable {
    /// <summary>The name it is registered under, and the name that appears on <c>/api/health</c>.</summary>
    public const string Name = "durable-shards";

    /// <summary>The value in <see cref="HealthCheckResult.Data" /> for a shard that answered.</summary>
    public const string Reachable = "reachable";

    readonly IShardConnections connections;
    readonly DurableTierOptions durable;
    readonly IReadOnlyList<string> shards;
    readonly SemaphoreSlim refreshing = new(1, 1);
    readonly TimeProvider time;

    IReadOnlyDictionary<string, string>? lastResults;
    long lastProbedAt = long.MinValue;

    /// <summary>Creates the check over the shard table the durable tier is wired against.</summary>
    /// <param name="options">
    ///     The bound <c>CyberCloud:Storage</c> section, for the shard ids and the probe's timings.
    /// </param>
    /// <param name="connections">
    ///     The same connection table the tier hands to <c>configureTenantOptions</c>, so the probe
    ///     cannot drift from what a grain write uses.
    /// </param>
    /// <param name="time">
    ///     The clock the staleness window is measured against. Injected so a test can move it rather
    ///     than sleep.
    /// </param>
    public DurableShardHealthCheck(
        CyberCloudStorageOptions options,
        IShardConnections connections,
        TimeProvider? time = null
    ) {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(connections);

        this.connections = connections;
        this.time = time ?? TimeProvider.System;
        durable = options.Durable;
        shards = [.. options.Durable.Shards.Keys.Order(StringComparer.Ordinal)];
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default
    ) {
        if (shards.Count == 0) {
            // Reachable in the only sense available: there is nothing to reach. A silo with no
            // durable shards is caught long before this, by the tier not being wired at all.
            return HealthCheckResult.Healthy("No durable shards are configured.");
        }

        var results = await ResultsAsync(cancellationToken).ConfigureAwait(false);

        var unreachable = results
            .Where(x => !string.Equals(x.Value, Reachable, StringComparison.Ordinal))
            .Select(x => x.Key)
            .Order(StringComparer.Ordinal)
            .ToList();

        var data = results.ToDictionary(x => x.Key, x => (object)x.Value, StringComparer.Ordinal);

        return unreachable.Count == 0
            ? HealthCheckResult.Healthy(
                string.Create(CultureInfo.InvariantCulture, $"All {results.Count} durable shard(s) reachable."),
                data
            )
            : HealthCheckResult.Unhealthy(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{unreachable.Count} of {results.Count} durable shard(s) unreachable: "
                    + $"{string.Join(", ", unreachable)}. Tenants on those shards are failing their "
                    + $"grain writes; this silo is serving everything else and is deliberately not "
                    + $"evicted for it."
                ),
                data: data
            );
    }

    /// <inheritdoc />
    public void Dispose() => refreshing.Dispose();

    /// <summary>
    ///     The cached per-shard result, refreshed at most once per
    ///     <see cref="DurableTierOptions.HealthProbeInterval" />.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Single-flight, and it matters more than the cache.</b> <c>/api/health</c> already has
    ///     a 10-second output cache in front of it, but nothing stops two scrapers, a human and a
    ///     dashboard arriving together on a cold cache. Without the semaphore each of those is a full
    ///     round of connections to every shard. With it, the second caller waits for the first and
    ///     then reads what the first stored.
    /// </remarks>
    async Task<IReadOnlyDictionary<string, string>> ResultsAsync(CancellationToken cancellationToken) {
        if (Fresh()) {
            return lastResults!;
        }

        await refreshing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            if (Fresh()) {
                return lastResults!;
            }

            var probes = shards.Select(async shard => (shard, result: await ProbeAsync(shard, cancellationToken)));
            var probed = await Task.WhenAll(probes).ConfigureAwait(false);

            lastResults = probed.ToDictionary(x => x.shard, x => x.result, StringComparer.Ordinal);
            lastProbedAt = time.GetTimestamp();

            return lastResults;
        } finally {
            refreshing.Release();
        }
    }

    bool Fresh() =>
        lastResults is not null
        && lastProbedAt != long.MinValue
        && time.GetElapsedTime(lastProbedAt) < durable.HealthProbeInterval;

    /// <summary>Opens one shard and asks it for a 1.</summary>
    /// <returns><see cref="Reachable" />, or the failure's message.</returns>
    async Task<string> ProbeAsync(string shard, CancellationToken cancellationToken) {
        var seconds = (int)Math.Max(1, Math.Round(durable.HealthProbeTimeout.TotalSeconds));

        var connectionString = new NpgsqlConnectionStringBuilder(connections.Durable(shard)) {
            // See the remarks: the tier's own string, deliberately outside the tier's pool.
            Pooling = false,

            // Both, because they cover different halves. Timeout bounds the TCP connect and the
            // startup handshake; CommandTimeout bounds a server that accepted the connection and then
            // stopped answering, which is what a wedged PgBouncer looks like from here.
            Timeout = seconds,
            CommandTimeout = seconds
        }.ConnectionString;

        try {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var probe = new NpgsqlCommand("SELECT 1;", connection);
            await probe.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

            return Reachable;
        } catch (Exception failure) when (failure is not OperationCanceledException) {
            // ⚠ The message only, never the exception. This lands in a /api/health body that is
            // reachable from the cluster network, and docs/plan/08 § Errors forbids stack frames
            // there — SiloHostTests asserts the body carries no "   at " lines.
            return failure.Message;
        }
    }
}

/// <summary>
///     Registers <c>durable-shards</c> — the per-shard reachability check.
/// </summary>
public static class DurableShardHealthCheckExtensions {
    /// <summary>
    ///     Adds the durable-shard reachability check, untagged.
    /// </summary>
    /// <param name="services">
    ///     The silo's services. <see cref="CyberCloudStorageOptions" /> and
    ///     <see cref="IShardConnections" /> must already be registered, which
    ///     <c>AddCyberCloudGrainStorage</c> does immediately before calling this.
    /// </param>
    /// <returns>The same collection, for chaining.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Called from the storage wiring, not from <c>AddOrleansHealthChecks</c>.</b> The
    ///         check resolves <see cref="IShardConnections" />, and <c>AddOrleansHealthChecks</c> runs
    ///         on every silo including the ones with no <c>CyberCloud:Storage</c> section at all
    ///         (<c>OrleansApplication.CreateSilo</c> makes that a deliberate property). Registering it
    ///         there would give those silos a check that reports Unhealthy with a DI error forever.
    ///         Registering it here makes "there is a durable tier" and "its shards are watched" the
    ///         same edit.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>No tags, and <c>HealthCheckWiringTests</c> is what keeps it that way.</b> See the
    ///         remarks on <see cref="DurableShardHealthCheck" /> for why readiness must not carry
    ///         this.
    ///     </para>
    /// </remarks>
    public static IServiceCollection AddDurableShardHealthCheck(this IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(services);

        // Singleton because it carries the staleness window and the single-flight gate across probes.
        // A transient would probe every shard on every scrape, which is the cost the cache exists to
        // avoid.
        services.AddSingleton<DurableShardHealthCheck>();
        services.AddHealthChecks().AddCheck<DurableShardHealthCheck>(DurableShardHealthCheck.Name);

        return services;
    }
}
