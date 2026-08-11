using StackExchange.Redis;

namespace CyberCloud.ServiceDefaults.Storage;

/// <summary>
///     Shard id → how to connect to it. The second half of the two lines docs/plan/05 § Storage
///     provider wiring calls "the load-bearing five lines of this document".
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The durable side must return the <i>same string instance content</i> for every tenant
///         on a shard.</b> Npgsql keys its connection pool by connection string, so a per-tenant
///         string means a per-tenant pool and the <c>MaxPoolSize</c> arithmetic in docs/plan/05
///         § Storage provider wiring stops being per-shard. This interface exists partly to make that
///         a single implementation detail rather than something every call site could get wrong.
///     </para>
/// </remarks>
public interface IShardConnections
{
    /// <summary>The Npgsql connection string for a durable shard.</summary>
    /// <param name="shard">A shard id from <see cref="IShardMapCache.DurableShardFor" />.</param>
    /// <exception cref="KeyNotFoundException">The shard is not in the configured shard table.</exception>
    string Durable(string shard);

    /// <summary>
    ///     The hot tier's cluster configuration. There is one, for the whole cluster — see
    ///     <see cref="HotTierOptions" /> for why the hot tier has no per-shard connection.
    /// </summary>
    ConfigurationOptions HotCluster();
}
