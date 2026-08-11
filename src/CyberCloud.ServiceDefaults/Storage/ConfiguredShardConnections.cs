using Npgsql;
using StackExchange.Redis;

namespace CyberCloud.ServiceDefaults.Storage;

/// <summary>
///     <see cref="IShardConnections" /> over <see cref="CyberCloudStorageOptions" />, with the
///     connection-pool arithmetic of docs/plan/05 § Storage provider wiring applied once per shard.
/// </summary>
public sealed class ConfiguredShardConnections : IShardConnections
{
    readonly Dictionary<string, string> durable;
    readonly ConfigurationOptions hot;

    /// <summary>Builds the connection table.</summary>
    /// <param name="options">The bound <c>CyberCloud:Storage</c> section.</param>
    public ConfiguredShardConnections(CyberCloudStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // ⚠ Built ONCE, here, and handed out by reference. Every tenant on a shard therefore gets a
        // byte-identical connection string and shares one Npgsql pool — which is the only reading of
        // "MaxPoolSize of 5 per silo per shard" (docs/plan/05 § Storage provider wiring) that is
        // actually per shard. Orleans.Multitenant builds one storage provider per tenant, so the
        // obvious implementation — compose the string inside configureTenantOptions — gives one pool
        // per tenant per shard and multiplies the connection count by the tenant count.
        durable = options.Durable.Shards.ToDictionary(
            x => x.Key,
            x => Canonicalise(x.Value, options.Durable),
            StringComparer.Ordinal);

        hot = ConfigurationOptions.Parse(options.Hot.ConnectionString);
    }

    /// <inheritdoc />
    public string Durable(string shard)
    {
        ArgumentException.ThrowIfNullOrEmpty(shard);

        return durable.TryGetValue(shard, out var connectionString)
            ? connectionString
            : throw new KeyNotFoundException(
                $"Durable shard '{shard}' is not in {CyberCloudStorageOptions.SectionName}:Durable:Shards. "
                + $"Known shards: {string.Join(", ", durable.Keys.Order(StringComparer.Ordinal))}.");
    }

    /// <inheritdoc />
    public ConfigurationOptions HotCluster() => hot;

    /// <summary>
    ///     Applies the pool settings docs/plan/05 § Storage provider wiring requires, and the two
    ///     Npgsql settings that transaction-mode PgBouncer requires.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Composed through <see cref="NpgsqlConnectionStringBuilder" /> rather than by string
    ///         concatenation so that a connection string which already carries a
    ///         <c>Maximum Pool Size</c> is <i>overridden</i> rather than given a second, ignored
    ///         copy. A pool setting silently dropped is exactly the failure mode this file exists to
    ///         prevent, and appending <c>;Maximum Pool Size=5</c> to a string that already says 200
    ///         is how it happens.
    ///     </para>
    /// </remarks>
    static string Canonicalise(string connectionString, DurableTierOptions options)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            MaxPoolSize = options.MaxPoolSize,
        };

        if (options.PgBouncerTransactionMode)
        {
            // See DurableTierOptions.PgBouncerTransactionMode for the evidence behind both of these.
            builder.MaxAutoPrepare = 0;
            builder.NoResetOnClose = true;
        }

        return builder.ConnectionString;
    }
}
