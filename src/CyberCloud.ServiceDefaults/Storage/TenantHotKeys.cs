using Orleans.Multitenant;
using StackExchange.Redis;
using System.Text;

namespace CyberCloud.ServiceDefaults.Storage;

/// <summary>
///     The hot tier's key layout, bound to one tenant:
///     <c>{cc:t:&lt;tenantId&gt;}:&lt;grainType&gt;:&lt;keyWithinTenant&gt;</c>
///     (docs/plan/05 § Hot).
/// </summary>
/// <remarks>
///     <para>
///         An instance is created once per tenant, when <c>Orleans.Multitenant</c> builds that
///         tenant's <c>RedisGrainStorage</c>, and is installed as
///         <c>RedisStorageOptions.GetStorageKey</c>. <b>The tag is captured at construction</b>, not
///         recomputed per call, which is what makes "a grain physically cannot be stored on the wrong
///         tenant's shard" (docs/plan/05 § Storage provider wiring) structural rather than
///         conventional: the provider instance for tenant B has no expression in it that can produce
///         a key inside tenant A's tag.
///     </para>
///     <para>
///         ⚠ <b>The braces go around the tenant discriminator and nothing else.</b> Everything inside
///         them is the Redis Cluster hash tag and therefore the slot. Two ways to get this wrong both
///         pass on a single node and fail differently on a cluster: wrapping the <i>whole</i> key
///         gives every key its own slot, so a tenant's state scatters across every shard and a tenant
///         delete becomes a fan-out; omitting the braces does the same thing and additionally makes
///         any multi-key command a <c>CROSSSLOT</c> error at runtime.
///     </para>
///     <para>
///         ⚠
///         <b>
///             The layout carries no <c>ServiceId</c>, and that is docs/plan/05's layout, not an
///             oversight here.
///         </b>
///         Orleans' own default Redis key is
///         <c>{ServiceId}/state/{grainId}/{grainType}</c>. Dropping the service id means two Orleans
///         clusters pointed at one Redis Cluster — staging and production, or a blue/green pair —
///         share keys and overwrite each other's state. The mitigation the plan implies is that a
///         cluster gets its own Redis; that is worth being deliberate about rather than discovering.
///     </para>
/// </remarks>
public sealed class TenantHotKeys {
    readonly string tenantId;
    readonly byte[] prefix;

    /// <summary>The tag body, without braces. Exposed so tests and diagnostics can assert on it.</summary>
    public string HashTag { get; }

    /// <summary>Binds the layout to one tenant.</summary>
    /// <param name="tenantId">
    ///     The tenant id as <c>Orleans.Multitenant</c> supplies it to <c>configureTenantOptions</c>.
    /// </param>
    /// <param name="hashTag">
    ///     The tag body from <see cref="IShardMapCache.HotHashTagFor" /> — normally
    ///     <c>cc:t:&lt;tenantId&gt;</c>, or an operator override for a pinned tenant.
    /// </param>
    public TenantHotKeys(string tenantId, string hashTag) {
        ArgumentException.ThrowIfNullOrEmpty(tenantId);
        ArgumentException.ThrowIfNullOrEmpty(hashTag);

        if (hashTag.Contains('{', StringComparison.Ordinal) || hashTag.Contains('}', StringComparison.Ordinal)) {
            throw new ArgumentException(
                $"Hash tag '{hashTag}' contains a brace. The braces are added here; a tag that "
                + "carries its own would nest, and Redis takes the FIRST '{' to the FIRST following "
                + "'}', so the effective tag would be something nobody wrote.",
                nameof(hashTag)
            );
        }

        this.tenantId = tenantId;
        HashTag = hashTag;
        prefix = Encoding.UTF8.GetBytes("{" + hashTag + "}:");
    }

    /// <summary>
    ///     The <c>RedisStorageOptions.GetStorageKey</c> implementation for this tenant.
    /// </summary>
    /// <param name="grainType">Orleans' grain type string.</param>
    /// <param name="grainId">The grain being stored.</param>
    /// <exception cref="InvalidOperationException">
    ///     The grain belongs to a different tenant than the one this instance was built for.
    /// </exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The tenant check is defence in depth and is expected never to fire.</b>
    ///         <c>Orleans.Multitenant</c>'s <c>MultitenantStorage</c> selects the per-tenant provider
    ///         by <c>grainId.GetTenantId()</c> before this is reached, so the two can only disagree
    ///         if that routing is bypassed or changes. It is here because the alternative to throwing
    ///         is writing one tenant's state under another tenant's tag, which is a cross-tenant data
    ///         leak that no test downstream would notice.
    ///     </para>
    /// </remarks>
    public RedisKey Key(string grainType, GrainId grainId) {
        ArgumentNullException.ThrowIfNull(grainType);

        var actual = grainId.GetTenantId();
        if (!string.Equals(actual ?? string.Empty, tenantId, StringComparison.Ordinal)
            && actual is not null) {
            throw new InvalidOperationException(
                $"Grain {grainId} belongs to tenant '{actual}' but was routed to the hot-tier "
                + $"storage provider for tenant '{tenantId}'. Refusing to write it under the wrong "
                + "tenant's hash tag (docs/plan/05 § Storage provider wiring)."
            );
        }

        var within = grainId.GetKeyWithinTenant() ?? grainId.Key.ToString();
        var suffix = Encoding.UTF8.GetBytes(grainType + ":" + within);

        var key = new byte[prefix.Length + suffix.Length];
        prefix.CopyTo(key, 0);
        suffix.CopyTo(key, prefix.Length);

        return key;
    }

    /// <summary>Builds the key as text, for tests, diagnostics and slot arithmetic.</summary>
    /// <param name="hashTag">The tag body, without braces.</param>
    /// <param name="grainType">Orleans' grain type string.</param>
    /// <param name="keyWithinTenant">The grain key with the tenant qualification removed.</param>
    public static string Format(string hashTag, string grainType, string keyWithinTenant) =>
        "{" + hashTag + "}:" + grainType + ":" + keyWithinTenant;

    /// <summary>Creates the layout for a tenant, resolving the tag through the shard map.</summary>
    /// <param name="shardMap">The in-process shard map.</param>
    /// <param name="tenantId">The tenant id from <c>configureTenantOptions</c>.</param>
    public static TenantHotKeys For(IShardMapCache shardMap, string tenantId) {
        ArgumentNullException.ThrowIfNull(shardMap);

        return new(tenantId, shardMap.HotHashTagFor(tenantId));
    }
}
