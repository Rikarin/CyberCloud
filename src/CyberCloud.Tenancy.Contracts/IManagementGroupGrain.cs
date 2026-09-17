using CyberCloud.Core;

namespace CyberCloud.Tenancy.Contracts;

/// <summary>
///     One node of docs/plan/06 § The hierarchy's optional tree above the subscription — a
///     <i>management group</i>. Keyed by <c>GrainKeys.ManagementGroup(name)</c> within the tenant
///     (docs/plan/06 § Grain keys), which is what makes a group's name unique in its tenant without
///     an index: one activation per name, held by Orleans. Issue #39.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>What a group is for, so the shape below is not read as a folder.</b> A role assigned
///         at a group is inherited by every subscription in it and everything below them — that is
///         the whole product, and it is delivered by the ReBAC <c>parent</c> edge
///         (<c>subscription:{s}#parent@managementGroup:{g}</c>, <c>managementGroup:{g}#parent@…</c>)
///         and <c>CyberCloudSchema</c>'s <c>From("parent", …)</c> rewrites, not by anything in this
///         grain. This grain holds the <i>tree</i>: which group a group hangs off, which groups and
///         subscriptions hang off it. The tuple store holds the same facts as edges the evaluator
///         walks, and <c>ScopeManagerService</c> writes both in one flow, edge first, for the reason
///         step 8 of docs/plan/08 § The write path, end to end gives.
///     </para>
///     <para>
///         ⚠ <b>The tenant is the implicit root and has no grain of this type.</b> A group with
///         <see cref="ManagementGroupDescriptor.Parent" /> empty hangs off the tenant; a subscription
///         with <c>SubscriptionDescriptor.ManagementGroup</c> empty does too. Azure spells the root
///         as a group whose id is the tenant id; here it is spelled by absence, because a root group
///         nothing could delete or rename is a record with one legal state, and the tenant already
///         is one.
///     </para>
///     <para>
///         ⚠ <b>Depth is capped at <see cref="MaxDepth" /> and the cap is Azure's, for the same
///         reason.</b> Every hop in the tree is a hop in every <c>Check</c> below it: a resource is
///         resource → group → subscription → <i>n</i> management groups → tenant, and
///         docs/plan/07 § Check caps the walk at twelve. Six group levels puts the deepest resource
///         at nine hops, which leaves room for the userset hop a group grant adds and nothing else —
///         so the cap is not a product limit copied for parity, it is what keeps a legal tree inside
///         the evaluator's budget.
///     </para>
///     <para>
///         ⚠ <b>A group's parent is set at creation and cannot be changed, and that is a decision
///         with a recorded cost.</b> Azure lets a group be moved. Moving one here would mean
///         re-writing its <c>parent</c> tuple, re-checking the depth of every group and subscription
///         under it against the cap, and refusing a move that makes the tree a cycle — each of which
///         is one grain call per node in the subtree with nothing holding the tree still between
///         them. The only way to do that safely is the seal-then-move choreography
///         <c>IResourceGroupGrain.BeginGroupDeleteAsync</c> uses for a delete, and that is more than
///         this issue's group needs on day one. A <c>PUT</c> that names a different parent for an
///         existing group is therefore refused with a message that says so, and docs/plan/06 § The
///         hierarchy records the move as owed. A <i>subscription</i> can be moved between groups,
///         because it is a leaf: one tuple to replace and no subtree to re-check.
///     </para>
/// </remarks>
[Alias("CyberCloud.Tenancy.IManagementGroupGrain")]
public interface IManagementGroupGrain : IGrainWithStringKey {
    /// <summary>
    ///     The most levels of management groups a tenant may nest — a group at depth
    ///     <see cref="MaxDepth" /> has <see cref="MaxDepth" /> − 1 groups above it and cannot be a
    ///     parent. See the remarks on the type for why it is six.
    /// </summary>
    static int MaxDepth => 6;

    /// <summary>
    ///     Creates the group at this key, or returns the existing record when the arguments agree
    ///     with it. Idempotent for a re-driven <c>PUT</c>.
    /// </summary>
    /// <param name="displayName">What a person reads. Empty means "the name".</param>
    /// <param name="parent">
    ///     The parent group's name, or empty for a group that hangs off the tenant. ⚠ The caller has
    ///     already established that the parent exists and read its depth — this grain cannot reach
    ///     another group's record inside its own turn without a call, and the depth it records is
    ///     <paramref name="parentDepth" /> + 1.
    /// </param>
    /// <param name="parentDepth">
    ///     The parent's depth, or <c>0</c> for the tenant. The group refuses to be created at a depth
    ///     past <see cref="MaxDepth" />.
    /// </param>
    /// <returns>
    ///     The record. <see cref="Core.ErrorCode.Conflict" /> when the group exists with a different
    ///     parent — a re-drive must carry the same tree position, because accepting a different one
    ///     would turn a retry into the move the type's remarks refuse.
    /// </returns>
    Task<Result<ManagementGroupDescriptor>> CreateAsync(string displayName, string parent, int parentDepth);

    /// <summary>The record, or <see cref="Core.ErrorCode.ResourceNotFound" /> for a group never created or since deleted.</summary>
    Task<Result<ManagementGroupDescriptor>> GetAsync();

    /// <summary>Records a child group. Idempotent.</summary>
    /// <param name="child">The child's name.</param>
    Task<Result> AddChildAsync(string child);

    /// <summary>Forgets a child group. Idempotent — a child that was never listed is a success.</summary>
    /// <param name="child">The child's name.</param>
    Task<Result> RemoveChildAsync(string child);

    /// <summary>Records a subscription as assigned to this group. Idempotent.</summary>
    /// <param name="subscriptionId">The subscription.</param>
    Task<Result> AddSubscriptionAsync(Guid subscriptionId);

    /// <summary>Forgets a subscription. Idempotent.</summary>
    /// <param name="subscriptionId">The subscription.</param>
    Task<Result> RemoveSubscriptionAsync(Guid subscriptionId);

    /// <summary>
    ///     Deletes the group's record, in one turn with the check that it holds nothing.
    /// </summary>
    /// <returns>
    ///     The parent's name the record carried — empty for a root group — once the record is gone;
    ///     or the parent it carried when it <i>was</i> deleted, for a group already gone, because
    ///     absence is the goal and a re-driven <c>DELETE</c> must not report failure for work that
    ///     succeeded and must still be able to sweep the parent's child list.
    ///     <see cref="Core.ErrorCode.Conflict" /> while a child group or a subscription is still in it,
    ///     naming what is in the way.
    /// </returns>
    /// <remarks>
    ///     ⚠ <b>Does not cascade, for the reason a resource group's delete does not</b>
    ///     (<c>IScopeManager.DeleteAsync</c>): every member has its own authorization and its own
    ///     lock, and a cascade that skipped either would be a way to move a subscription out from
    ///     under its owner by deleting the group it is in. The check and the delete are one grain
    ///     turn so nothing can be added between them.
    /// </remarks>
    Task<Result<string>> DeleteAsync();

    /// <summary>Releases the activation. Tests use it to force a reactivation from durable state.</summary>
    Task DeactivateAsync();
}

/// <summary>A management group, as its grain records it.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Tenancy.ManagementGroupDescriptor")]
public sealed record ManagementGroupDescriptor {
    /// <summary>The DNS-1123 name — unique within the tenant, and the grain key's payload.</summary>
    [Id(0)]
    public string Name { get; init; } = string.Empty;

    /// <summary>The owning tenant.</summary>
    [Id(1)]
    public Guid TenantId { get; init; }

    /// <summary>What a person reads. Defaults to <see cref="Name" />.</summary>
    [Id(2)]
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>The parent group's name, or empty for a group that hangs off the tenant.</summary>
    [Id(3)]
    public string Parent { get; init; } = string.Empty;

    /// <summary>
    ///     How many groups are on the path from the tenant to this one, this one included: <c>1</c>
    ///     for a group under the tenant. Bounded by <see cref="IManagementGroupGrain.MaxDepth" />.
    /// </summary>
    [Id(4)]
    public int Depth { get; init; }

    /// <summary>The child groups' names, ordered ordinally.</summary>
    [Id(5)]
    public IReadOnlyList<string> Children { get; init; } = [];

    /// <summary>The subscriptions assigned to this group, ordered by id.</summary>
    [Id(6)]
    public IReadOnlyList<Guid> Subscriptions { get; init; } = [];

    /// <summary>When the record was created.</summary>
    [Id(7)]
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Bumped on every write; what a scope's <c>etag</c> is derived from.</summary>
    [Id(8)]
    public long Version { get; init; }
}
