using CyberCloud.Core;
using CyberCloud.Core.Resources;

namespace CyberCloud.Tenancy.Contracts;

/// <summary>
///     A resource group — <b>a lifecycle unit, not a folder</b> (docs/plan/06 § The hierarchy).
/// </summary>
/// <remarks>
///     <para>
///         <b>Kind</b> Entity · <b>Tier</b> Durable · <b>Key</b>
///         <c>sub/{subscriptionId:N}/rg/{name}</c>, tenant-qualified (docs/plan/06 § Grain keys).
///         Build it with <c>GrainKeys.ResourceGroup</c>.
///     </para>
///     <para>
///         <b>What this grain owns and what it does not.</b> It owns <i>membership</i> and the
///         lifecycle of the group. It does <b>not</b> own the resources: <c>IResourceGrain</c>, its
///         desired/observed split, the providers and the reconcilers are the resource manager's
///         (docs/plan/08) and are deliberately not built here. The four methods below are the
///         resource-group half of the create and delete choreographies in docs/plan/06 § Two-phase
///         create; the orchestrator that calls them, in order, alongside the index grain and the
///         resource grain, is the resource manager.
///     </para>
///     <para>
///         ⚠ <b>The delete order is the reverse of the create order and it is the harder half.</b>
///         docs/plan/06 § Two-phase create: "release the index first (so the name is immediately
///         reusable), then tear down the data plane, then delete the grain state. A resource whose
///         data plane teardown fails is left in <c>Deleting</c> with a retry reminder and is
///         <i>visible</i> in listings with that state — never silently gone while its pods still run
///         and its meter still ticks."
///     </para>
/// </remarks>
[Alias("CyberCloud.Tenancy.IResourceGroupGrain")]
public interface IResourceGroupGrain : IGrainWithStringKey {
    /// <summary>Creates the group. Idempotent on the same region.</summary>
    /// <param name="tenantId">The owning tenant.</param>
    /// <param name="region">The region its resources default to.</param>
    Task<Result<ResourceGroupDescriptor>> CreateAsync(Guid tenantId, string region);

    /// <summary>The group's record, or <c>ResourceGroupNotFound</c>.</summary>
    Task<Result<ResourceGroupDescriptor>> GetAsync();

    /// <summary>
    ///     Step 2 of docs/plan/06 § Two-phase create, from the group's side: records the resource as
    ///     a member in <see cref="ProvisioningState.Creating" />.
    /// </summary>
    /// <param name="address">The resource's address. Its <c>Id</c> is the GUID being created.</param>
    /// <remarks>
    ///     Called <b>after</b> <c>IResourceIndexGrain.TryClaimAsync</c> and <b>before</b>
    ///     <c>ConfirmAsync</c>. A member left in <see cref="ProvisioningState.Creating" /> with no
    ///     confirmed index is the orphan docs/plan/06 § Two-phase create says "is swept by a
    ///     per-subscription reaper reminder" — <see cref="ListOrphansAsync" /> is what the reaper
    ///     reads.
    /// </remarks>
    Task<Result> BeginCreateAsync(ResourceId address);

    /// <summary>Step 4: the resource reached a terminal state.</summary>
    /// <param name="resourceId">The resource.</param>
    /// <param name="terminal">The terminal state.</param>
    Task<Result> CompleteCreateAsync(Guid resourceId, ProvisioningState terminal);

    /// <summary>
    ///     Delete, step 1: marks the resource <see cref="ProvisioningState.Deleting" />. It stays
    ///     listed.
    /// </summary>
    /// <param name="resourceId">The resource.</param>
    Task<Result> BeginDeleteAsync(Guid resourceId);

    /// <summary>
    ///     Delete, the failure path: teardown failed, so the member <b>stays</b>
    ///     <see cref="ProvisioningState.Deleting" /> and stays listed.
    /// </summary>
    /// <param name="resourceId">The resource.</param>
    /// <param name="failure">Why teardown failed.</param>
    /// <remarks>
    ///     ⚠ This method deliberately cannot remove the member and deliberately cannot move it to
    ///     <see cref="ProvisioningState.Failed" />. Both would make the resource look finished while
    ///     its pods still run and its meter still ticks, which docs/plan/06 § Two-phase create names
    ///     as "a billing-dispute prevention measure as much as a correctness one".
    /// </remarks>
    Task<Result> FailDeleteAsync(Guid resourceId, string failure);

    /// <summary>Delete, the last step: teardown succeeded, so the member goes.</summary>
    /// <param name="resourceId">The resource.</param>
    Task<Result> CompleteDeleteAsync(Guid resourceId);

    /// <summary>
    ///     Every member, <b>including</b> the ones in <see cref="ProvisioningState.Deleting" />.
    /// </summary>
    Task<Result<IReadOnlyList<ResourceGroupMember>>> ListAsync();

    /// <summary>
    ///     Members that have been <see cref="ProvisioningState.Creating" /> for longer than
    ///     <paramref name="olderThan" /> — what the reaper reminder of docs/plan/06 § Two-phase
    ///     create sweeps.
    /// </summary>
    /// <param name="olderThan">The age threshold.</param>
    Task<Result<IReadOnlyList<ResourceGroupMember>>> ListOrphansAsync(TimeSpan olderThan);

    /// <summary>Drops this activation — see <c>ITenantGrain.DeactivateAsync</c>.</summary>
    Task DeactivateAsync();
}
