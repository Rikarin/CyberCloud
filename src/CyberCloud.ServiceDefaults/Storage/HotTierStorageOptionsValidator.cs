using Orleans.Persistence;

namespace CyberCloud.ServiceDefaults.Storage;

/// <summary>
///     The hot tier's <c>TGrainStorageOptionsValidator</c>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This exists because Orleans' own one is <c>internal</c>.</b>
///         <c>AddMultitenantGrainStorage&lt;TGrainStorage, TGrainStorageOptions,
///         TGrainStorageOptionsValidator&gt;</c> has a third type parameter constrained to
///         <c>class, IConfigurationValidator</c>, and the natural argument for the Redis tier —
///         <c>Orleans.Persistence.RedisStorageOptionsValidator</c> — is not public in
///         Microsoft.Orleans.Persistence.Redis 10.2.2 (verified by reflection over the shipped
///         assembly: <c>IsPublic == false</c>). It cannot be named from here, so the check is
///         restated. The durable tier does not have this problem —
///         <c>Orleans.Configuration.AdoNetGrainStorageOptionsValidator</c> is public — which is why
///         only one of the two tiers has a file like this.
///     </para>
///     <para>
///         ⚠ <b>The third type parameter is itself undocumented in the plan.</b> docs/plan/04
///         § Silo composition writes the call with two type arguments
///         (<c>&lt;RedisGrainStorage, RedisStorageOptions&gt;</c>); the real 4.0.0 signature takes
///         three and the third has no default.
///     </para>
///     <para>
///         The constructor shape — <c>(TOptions, string)</c> — is fixed by
///         <c>Orleans.Multitenant</c>, which builds the validator with
///         <c>ActivatorUtilities.CreateInstance(services, options, tenantProviderName)</c>.
///     </para>
/// </remarks>
/// <param name="options">The tenant's options, after <c>configureTenantOptions</c> has run.</param>
/// <param name="name">The provider name including the tenant id.</param>
public sealed class HotTierStorageOptionsValidator(RedisStorageOptions options, string name)
    : IConfigurationValidator
{
    /// <inheritdoc />
    public void ValidateConfiguration()
    {
        if (options is null || options.ConfigurationOptions is null)
        {
            throw new OrleansConfigurationException(
                $"Hot-tier grain storage '{name}' has no RedisStorageOptions.ConfigurationOptions. "
                + $"Set {CyberCloudStorageOptions.SectionName}:Hot:ConnectionString.");
        }

        if (options.GetStorageKey is null)
        {
            // Without this the provider falls back to Orleans' default key,
            // {ServiceId}/state/{grainId}/{grainType} — no braces, therefore no hash tag, therefore
            // a tenant's keys scattered across every shard of the cluster. It would work perfectly
            // on the single-node Redis a developer runs and fail as a CROSSSLOT error and a lost
            // one-shard tenant delete in production, which is precisely the class of bug
            // docs/plan/05 § Hot exists to prevent.
            throw new OrleansConfigurationException(
                $"Hot-tier grain storage '{name}' has no RedisStorageOptions.GetStorageKey, so it "
                + "would use Orleans' default un-tagged key layout instead of "
                + "{cc:t:<tenantId>}:<grainType>:<keyWithinTenant> (docs/plan/05 § Hot).");
        }
    }
}
