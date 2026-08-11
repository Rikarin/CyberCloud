using CyberCloud.Core;

namespace CyberCloud.Tenancy.Contracts;

/// <summary>
///     A subscription — the billing and quota boundary (docs/plan/06 § The hierarchy).
/// </summary>
/// <remarks>
///     <para>
///         <b>Kind</b> Entity · <b>Tier</b> Durable · <b>Key</b> <c>sub/{subscriptionId:N}</c>,
///         tenant-qualified (docs/plan/06 § Grain keys). Build it with <c>GrainKeys.Subscription</c>.
///     </para>
///     <para>
///         ⚠
///         <b>
///             One tenant, many subscriptions, and that is why this is a grain and not a field on
///             the tenant.
///         </b>
///         docs/plan/06 § The hierarchy: "production, staging, per-team — is the
///         shape every real customer wants within a month, and retrofitting it later means
///         renumbering every resource id".
///     </para>
/// </remarks>
[Alias("CyberCloud.Tenancy.ISubscriptionGrain")]
public interface ISubscriptionGrain : IGrainWithStringKey {
    /// <summary>Creates the subscription. Idempotent on the same display name.</summary>
    /// <param name="displayName">The display name.</param>
    Task<Result<SubscriptionDescriptor>> CreateAsync(string displayName);

    /// <summary>
    ///     The subscription's record, or <c>SubscriptionNotFound</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Step 1 of the write path calls this on every request, and the answer is the
    ///     subscription's <i>existence</i> as much as its content.</b> A path may name any GUID as a
    ///     subscription; only this grain can say whether the caller's tenant has one. See
    ///     <c>ResourceManagerService.ResolveAsync</c> — a subscription that does not exist and one
    ///     that belongs to another tenant are answered identically, and both as <c>404</c> on the
    ///     resource path (docs/plan/07 § The enforcement seam).
    /// </remarks>
    Task<Result<SubscriptionDescriptor>> GetAsync();

    /// <summary>Sets or clears the lock at this scope.</summary>
    /// <param name="level">The lock. <see cref="LockLevel.None" /> clears it.</param>
    /// <remarks>
    ///     ⚠ <b>The top of the chain that exists.</b> docs/plan/06 § Tags, locks makes a lock
    ///     "inherited down the hierarchy", and the hierarchy above a subscription is the management
    ///     group — docs/plan/01 puts management groups at M2 and there is no grain for one, so a lock
    ///     set here is the highest lock the platform can currently express.
    ///     <c>ResourceScopeLockResolver</c> says so out loud rather than implying that the walk is
    ///     complete.
    /// </remarks>
    Task<Result> SetLockAsync(LockLevel level);

    /// <summary>
    ///     Creates a resource group in this subscription, and records it here.
    /// </summary>
    /// <param name="name">The DNS-1123 name, unique within this subscription.</param>
    /// <param name="region">The region its resources default to.</param>
    /// <remarks>
    ///     Uniqueness of the name is enforced by <i>this</i> grain's single-threaded activation
    ///     rather than by an index grain, because the scope is already one activation: a resource
    ///     group name is unique within a subscription (docs/plan/06 § The hierarchy), and the
    ///     subscription is the grain. An index grain here would be a second mutex guarding a lock we
    ///     already hold.
    /// </remarks>
    Task<Result<ResourceGroupDescriptor>> CreateResourceGroupAsync(string name, string region);

    /// <summary>The resource groups in this subscription, sorted.</summary>
    Task<Result<IReadOnlyList<string>>> ListResourceGroupsAsync();

    /// <summary>
    ///     Removes a resource group from the listing, after its teardown has completed.
    /// </summary>
    /// <param name="name">The group name.</param>
    Task<Result> RemoveResourceGroupAsync(string name);

    /// <summary>Drops this activation — see <c>ITenantGrain.DeactivateAsync</c>.</summary>
    Task DeactivateAsync();
}
