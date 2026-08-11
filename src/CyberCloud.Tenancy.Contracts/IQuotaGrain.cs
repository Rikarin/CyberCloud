using CyberCloud.Core;

namespace CyberCloud.Tenancy.Contracts;

/// <summary>
///     Quota, enforced at the subscription and checked <b>before</b> the provider is called
///     (docs/plan/06 § Quota).
/// </summary>
/// <remarks>
///     <para>
///         <b>Kind</b> Coordinator · <b>Tier</b> Durable · <b>Key</b> <c>sub/{subscriptionId:N}</c>,
///         tenant-qualified — the <i>same key string</i> as <c>ISubscriptionGrain</c>, on a different
///         grain type. That is not a collision: Orleans addresses an activation by
///         (grain type, key), so the two are distinct grains that happen to name the same
///         subscription. Building a second key shape to say the same thing would add a spelling
///         without adding an identity.
///     </para>
///     <para>
///         ⚠
///         <b>
///             The tier is Durable, and docs/plan/04 § Grain taxonomy says "Hot + a durable
///             checkpoint" for the Coordinator kind.
///         </b>
///         That is a real disagreement and it is resolved
///         towards durable here, deliberately: a lost hot-tier record of committed quota is a
///         subscription that can exceed its limit until something recomputes it, and docs/plan/05
///         § Hot lists what the hot tier holds — "sessions, observed state, caches, counters" — with
///         quota grants appearing instead in the durable list. The <i>lease</i> half is genuinely
///         ephemeral, but it lives in the same record as the committed half and splitting one record
///         across two tiers to save a Postgres write on a path docs/plan/06 § Quota itself calls
///         rare ("it is fine because creates are rare") is not a trade worth making.
///     </para>
///     <para>
///         ⚠ <b>This grain serialises every create in its subscription, and that is correct.</b>
///         docs/plan/06 § Quota: "quota is exactly the thing that needs a single writer … What would
///         <i>not</i> be fine is putting a per-tenant rate limiter in the same grain, and that is why
///         rate limiting lives in the gateway over Redis counters and never touches a grain." No
///         method here is on a per-request path.
///     </para>
/// </remarks>
[Alias("CyberCloud.Tenancy.IQuotaGrain")]
public interface IQuotaGrain : IGrainWithStringKey {
    /// <summary>
    ///     Reserves quota under a lease — docs/plan/06 § Quota's signature, and its
    ///     "<b>Reservation, not a counter increment</b>".
    /// </summary>
    /// <param name="meter">The meter family.</param>
    /// <param name="amount">How much. Must be positive.</param>
    /// <param name="operationId">The operation that owns the lease.</param>
    /// <returns>The lease, or <c>QuotaExceeded</c>.</returns>
    /// <remarks>
    ///     The lease expires on its own if it is neither committed nor released — which is what
    ///     happens when "the operation grain dies". Expiry is evaluated on read, not by a timer, so
    ///     an expired lease frees its quota even if this grain has been deactivated the whole time.
    /// </remarks>
    Task<Result<QuotaLease>> TryReserveAsync(QuotaMeter meter, decimal amount, Guid operationId);

    /// <summary>
    ///     Turns a lease into committed usage. The resource now exists.
    /// </summary>
    /// <param name="leaseId">The lease.</param>
    Task<Result<QuotaUsage>> CommitAsync(Guid leaseId);

    /// <summary>
    ///     Releases a lease without committing — <b>the operation failed</b>. The quota is available
    ///     again immediately.
    /// </summary>
    /// <param name="leaseId">The lease.</param>
    Task<Result<QuotaUsage>> ReleaseAsync(Guid leaseId);

    /// <summary>Gives committed quota back, when a resource is deleted.</summary>
    /// <param name="meter">The meter.</param>
    /// <param name="amount">How much to return.</param>
    Task<Result<QuotaUsage>> ReturnAsync(QuotaMeter meter, decimal amount);

    /// <summary>Sets a meter's limit — the per-tenant override of docs/plan/06 § Quota.</summary>
    /// <param name="meter">The meter.</param>
    /// <param name="limit">The new ceiling. Must not be negative.</param>
    Task<Result<QuotaUsage>> SetLimitAsync(QuotaMeter meter, decimal limit);

    /// <summary>One meter's figures, with expired leases already discounted.</summary>
    /// <param name="meter">The meter.</param>
    Task<Result<QuotaUsage>> GetUsageAsync(QuotaMeter meter);

    /// <summary>Every lease that is currently live.</summary>
    Task<Result<IReadOnlyList<QuotaLease>>> ListLeasesAsync();

    /// <summary>Drops this activation — see <c>ITenantGrain.DeactivateAsync</c>.</summary>
    Task DeactivateAsync();
}
