using System.Globalization;

namespace CyberCloud.AppHost;

/// <summary>
///     The two pieces of wiring that appear on more than one resource, so that the silos cannot
///     drift apart from each other or from the schema job.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Everything here writes <c>CyberCloud__…</c> environment variables rather than
///         <c>ConnectionStrings__…</c>, and that is not a style choice.</b> Aspire's
///         <c>WithReference(resource)</c> injects <c>ConnectionStrings__&lt;name&gt;</c>, which is
///         what an <c>Aspire.*</c> client integration reads. This repository has no Aspire client
///         integrations — <c>CyberCloud.ServiceDefaults.csproj</c> lists, with reasons, what
///         Survival takes from Aspire and this does not — and the storage tiers bind
///         <c>CyberCloud:Storage</c> (see <c>CyberCloudStorageOptions</c>). Injecting the connection
///         strings under the names the tiers already bind is what keeps ADR-014 true: the silo does
///         not know it was started by Aspire, and <c>dotnet run</c> on it with the same environment
///         behaves identically.
///     </para>
/// </remarks>
public static class CyberCloudResourceExtensions
{
    const string StoragePrefix = "CyberCloud__Storage__";
    const string ClusterPrefix = "CyberCloud__Cluster__";

    /// <summary>
    ///     Declares one durable shard as a database on a PostgreSQL server, <b>and creates it</b>.
    /// </summary>
    /// <param name="postgres">The PostgreSQL server resource.</param>
    /// <param name="shard">The shard id, which is also the database name.</param>
    /// <returns>The database resource.</returns>
    /// <remarks>
    ///     ⚠ <c>AddDatabase</c> alone declares a connection string and a health check that opens it.
    ///     It does not issue a <c>CREATE DATABASE</c>, so the resource stays unhealthy and everything
    ///     downstream waits — the only diagnosis being <c>FATAL: database "…" does not exist</c> in
    ///     the container's log. This exists so that the pairing cannot be forgotten for one shard out
    ///     of three.
    /// </remarks>
    public static IResourceBuilder<PostgresDatabaseResource> AddShard(
        this IResourceBuilder<PostgresServerResource> postgres,
        string shard)
    {
        ArgumentNullException.ThrowIfNull(postgres);
        ArgumentException.ThrowIfNullOrWhiteSpace(shard);

        return postgres
            .AddDatabase(shard)
            .WithCreationScript($"""CREATE DATABASE "{shard}";""");
    }

    /// <summary>
    ///     Points a resource at both storage tiers — the Redis cluster and every durable shard.
    /// </summary>
    /// <param name="builder">The resource being configured.</param>
    /// <param name="redis">The Redis resource backing the hot tier.</param>
    /// <param name="shardA">The first tenant-carrying durable shard.</param>
    /// <param name="shardB">The second tenant-carrying durable shard.</param>
    /// <param name="platformShard">The shard that carries every null-tenant platform grain.</param>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <returns>The same builder, for chaining.</returns>
    public static IResourceBuilder<T> WithCyberCloudStorage<T>(
        this IResourceBuilder<T> builder,
        IResourceBuilder<IResourceWithConnectionString> redis,
        IResourceBuilder<IResourceWithConnectionString> shardA,
        IResourceBuilder<IResourceWithConnectionString> shardB,
        IResourceBuilder<IResourceWithConnectionString> platformShard)
        where T : IResourceWithEnvironment
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder
            .WithEnvironment($"{StoragePrefix}Hot__ConnectionString", redis)
            .WithDurableShards(shardA, shardB, platformShard);
    }

    /// <summary>
    ///     Points a resource at the three durable shards, and says which of them is which.
    /// </summary>
    /// <param name="builder">The resource being configured.</param>
    /// <param name="shardA">The first tenant-carrying durable shard.</param>
    /// <param name="shardB">The second tenant-carrying durable shard.</param>
    /// <param name="platformShard">The shard that carries every null-tenant platform grain.</param>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <returns>The same builder, for chaining.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><c>NullTenantShard</c> is the setting that would otherwise be discovered in
    ///         production.</b> Every Platform grain in docs/plan/04 § Grain taxonomy — the tenant
    ///         directory, the shard map, the provider registry — is null-tenant and durable, and
    ///         <c>Orleans.Multitenant</c> hands the storage layer the literal string <c>"Null"</c>
    ///         for those. Unset, they hash that sentinel into the tenant shard list: deterministic,
    ///         arbitrary, and indistinguishable from working until somebody adds a shard and the
    ///         directory moves.
    ///     </para>
    ///     <para>
    ///         <c>BootstrapShard</c> is the shard <c>Orleans.Multitenant</c> opens at silo start for
    ///         its tenant-unaware provider. It is the reason a silo pointed at an empty database
    ///         fails to <i>start</i> rather than failing on the first grain write, which is why the
    ///         schema job must complete first.
    ///     </para>
    /// </remarks>
    public static IResourceBuilder<T> WithDurableShards<T>(
        this IResourceBuilder<T> builder,
        IResourceBuilder<IResourceWithConnectionString> shardA,
        IResourceBuilder<IResourceWithConnectionString> shardB,
        IResourceBuilder<IResourceWithConnectionString> platformShard)
        where T : IResourceWithEnvironment
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder
            .WithEnvironment($"{StoragePrefix}Durable__Shards__{CyberCloudResources.ShardA}", shardA)
            .WithEnvironment($"{StoragePrefix}Durable__Shards__{CyberCloudResources.ShardB}", shardB)
            .WithEnvironment(
                $"{StoragePrefix}Durable__Shards__{CyberCloudResources.PlatformShard}",
                platformShard)
            .WithEnvironment(
                $"{StoragePrefix}Durable__NullTenantShard",
                CyberCloudResources.PlatformShard)
            .WithEnvironment($"{StoragePrefix}Durable__BootstrapShard", CyberCloudResources.ShardA);
    }

    /// <summary>
    ///     Gives a silo its two Orleans sockets.
    /// </summary>
    /// <param name="builder">The silo resource.</param>
    /// <param name="siloPort">The silo-to-silo port.</param>
    /// <param name="gatewayPort">The client-facing gateway port.</param>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <returns>The same builder, for chaining.</returns>
    /// <remarks>
    ///     ⚠ These are <b>not</b> Aspire endpoints and must not be declared as any. Aspire allocates
    ///     an endpoint by binding the port itself to check it is free, then hands the target port to
    ///     the process; Orleans binds these two directly from configuration. Declaring them would
    ///     mean two things racing for one socket. The cost is that Aspire's dashboard does not know
    ///     about them and cannot detect a collision — which is exactly why they are fixed here and
    ///     away from Orleans' 11111/30000 defaults.
    /// </remarks>
    public static IResourceBuilder<T> WithOrleansPorts<T>(
        this IResourceBuilder<T> builder,
        int siloPort,
        int gatewayPort)
        where T : IResourceWithEnvironment
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder
            // ⚠ SET, NOT INHERITED, AND IT DECIDES THE MEMBERSHIP PROVIDER.
            //
            // OrleansApplication.CreateSilo branches on IsDevelopment(): Development gets
            // UseLocalhostClustering, anything else gets UseKubeMembership + UseKubernetesHosting.
            // Aspire passes its own environment through to the projects it launches, so the branch
            // would be decided by how the AppHost happened to be started — and under
            // Aspire.Hosting.Testing that is Production. Observed exactly that: silo-1 died at
            // startup with
            //     OptionsValidationException: KubernetesHostingOptions.Namespace is not set.
            //     Set it via the POD_NAMESPACE environment variable
            // which names neither Aspire nor the environment that produced it. ADR-014 says this
            // AppHost IS local development, so it says so rather than hoping.
            .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
            .WithEnvironment("DOTNET_ENVIRONMENT", "Development")
            .WithEnvironment(
                $"{ClusterPrefix}LocalhostSiloPort",
                siloPort.ToString(CultureInfo.InvariantCulture))
            .WithEnvironment(
                $"{ClusterPrefix}LocalhostGatewayPort",
                gatewayPort.ToString(CultureInfo.InvariantCulture));
    }
}
