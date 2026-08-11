using CyberCloud.Core;

namespace CyberCloud.Tenancy.Contracts;

/// <summary>
///     A tenant — the customer, the identity boundary, homed to a region (docs/plan/06 § The
///     hierarchy).
/// </summary>
/// <remarks>
///     <para>
///         <b>Kind</b> Entity · <b>Tier</b> Durable · <b>Key</b> <c>tenant/{tenantId:N}</c>,
///         tenant-qualified (docs/plan/04 § Grain taxonomy, the Entity row). Build it with
///         <c>GrainKeys.Tenant</c> and reach it with <c>IGrainFactory.ForTenant(id).GetGrain&lt;…&gt;</c>
///         — see the ⚠ on <c>GrainKeys.Tenant</c> for why docs/plan/06's key table has no row for
///         this grain and this one does.
///     </para>
///     <para>
///         ⚠ <b>This grain is not the tenant directory.</b> It holds the tenant's own record, in the
///         tenant's own shard, in the tenant's own region. The <i>directory</i> is a separate,
///         global, null-tenant grain holding 200 bytes per tenant so that a gateway in any region can
///         answer "which region is this tenant in" before it can route anything (docs/plan/05 § The
///         tenant directory). Merging them would put the whole tenant record in the one global store,
///         which is the bottleneck the design exists to avoid.
///     </para>
/// </remarks>
[Alias("CyberCloud.Tenancy.ITenantGrain")]
public interface ITenantGrain : IGrainWithStringKey
{
    /// <summary>Creates the tenant in <see cref="TenantStatus.Provisioning" />. Idempotent.</summary>
    /// <param name="slug">The globally unique DNS-1123 slug.</param>
    /// <param name="displayName">The human-facing name.</param>
    /// <param name="homeRegion">The region the tenant is homed to. Permanent until a migration.</param>
    /// <remarks>
    ///     ⚠ <b>Idempotent by design, not by accident.</b> docs/plan/06 § Tenant lifecycle makes
    ///     tenant creation a long-running operation whose "every step is idempotent and re-drivable".
    ///     A second call with the same arguments returns the existing descriptor; one with
    ///     <i>different</i> arguments is a <c>Conflict</c>, because silently accepting it would make
    ///     a retry a rename.
    /// </remarks>
    Task<Result<TenantDescriptor>> CreateAsync(string slug, string displayName, string homeRegion);

    /// <summary>The tenant's record, or <c>TenantNotFound</c>.</summary>
    Task<Result<TenantDescriptor>> GetAsync();

    /// <summary>
    ///     Moves the tenant through <see cref="TenantStatus" />. Rejects transitions the lifecycle
    ///     table does not allow.
    /// </summary>
    /// <param name="status">The new status.</param>
    /// <param name="reason">Why, for the audit log. Required.</param>
    /// <remarks>
    ///     ⚠ <see cref="TenantStatus.Purged" /> is terminal and irreversible: docs/plan/06 § Tenant
    ///     lifecycle says the directory entry is "tombstoned forever (never reuse an id)". Nothing
    ///     transitions out of it.
    /// </remarks>
    Task<Result<TenantDescriptor>> SetStatusAsync(TenantStatus status, string reason);

    /// <summary>Whether a control-plane write is currently permitted for this tenant.</summary>
    /// <remarks>
    ///     The read side of the lifecycle table's Effects column. <see cref="TenantStatus.Suspended" />
    ///     returns <c>false</c> <b>and the data plane keeps running</b> — this method answers a
    ///     control-plane question only.
    /// </remarks>
    Task<Result<bool>> AreControlPlaneWritesAllowedAsync();

    /// <summary>Records a subscription as belonging to this tenant.</summary>
    /// <param name="subscriptionId">The subscription.</param>
    Task<Result> AddSubscriptionAsync(Guid subscriptionId);

    /// <summary>The tenant's subscriptions.</summary>
    Task<Result<IReadOnlyList<Guid>>> ListSubscriptionsAsync();

    /// <summary>
    ///     Drops this activation. The next call re-reads durable state — the test seam for "the silo
    ///     died here".
    /// </summary>
    /// <remarks>
    ///     ⚠ Present on every grain in this assembly on purpose. docs/plan/23 § Test layers wants
    ///     failure injected rather than simulated, and an interruption test that never destroys an
    ///     activation is testing a mock of the failure. This is the smallest honest destruction
    ///     available in-process: the activation is gone, in-memory state with it, and everything the
    ///     grain knows next comes back out of PostgreSQL.
    /// </remarks>
    Task DeactivateAsync();
}
