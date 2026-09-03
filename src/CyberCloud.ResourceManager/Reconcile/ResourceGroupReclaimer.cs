using Microsoft.Extensions.Logging;
using Orleans.Multitenant;
using System.Globalization;

namespace CyberCloud.ResourceManager.Reconcile;

/// <summary>
///     Deletes a resource group: seals it, reclaims the namespace it holds on every cluster it ever
///     touched, and only then removes its record.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THE ORDER IS docs/plan/06 § Two-phase create IN REVERSE AND IT IS THE WHOLE
///         DESIGN.</b> Seal the group, then the members, then the namespace last. Sealing first is
///         the only thing that closes the create-during-delete race — a resource created between the
///         reclaim's evidence and the namespace delete has its objects destroyed by a verdict that
///         was true when it was reached, and no amount of re-checking below the group can prevent
///         that. <c>IResourceGroupGrain.BeginGroupDeleteAsync</c> is where the seal lives, because
///         only the grain can check the members and set the seal in one turn.
///     </para>
///     <para>
///         ⚠ <b>ONE DEVIATION FROM THE RESOURCE ORDERING, AND IT IS DELIBERATE: THE NAME IS FREED
///         LAST, NOT FIRST.</b> docs/plan/06 § Two-phase create says a delete should "release the
///         index first (so the name is immediately reusable)". For a resource that is right. For a
///         group it is the opposite of right, because a group's name is an <i>input to its namespace
///         name</i> — <see cref="ReconcileDriver.NamespaceFor(Guid, string)" /> is
///         <c>{subscriptionId:N}-{resourceGroup}</c>. Freeing it first lets a new group of the same
///         name be created, have <c>NamespaceEnsurer</c> apply the very same namespace, and then have
///         this delete remove it out from under the new group's first resource. So the subscription's
///         listing entry is dropped after everything else, and the name is unavailable for the few
///         seconds a reclaim takes.
///     </para>
///     <para>
///         ⚠ <b>A refusal leaves the group SEALED, and that is the same rule
///         <c>FailDeleteAsync</c> applies to a member.</b> A delete that began and did not finish
///         stays visible in <see cref="ProvisioningState.Deleting" /> rather than being quietly
///         returned to service. The whole choreography is idempotent, so an operator who clears
///         whatever the namespace refused over re-drives it and it finishes.
///     </para>
///     <para>
///         ⚠ <b>What is NOT done here, stated so it is owed rather than assumed.</b> The group's
///         <c>parent</c> tuple is left behind: <c>IScopeRelationWriter</c> has no unlink, and the
///         residue is inert for the same reason its own remarks give about a link written before a
///         create that failed — the tuple names an object that resolves to nothing, and a group
///         later recreated under the same name would be written the identical tuple. Adding the
///         unlink is a change to the authorization writer rather than to this choreography.
///     </para>
/// </remarks>
/// <param name="grains">The grain factory. Every reference goes through <c>ForTenant</c>.</param>
/// <param name="connections">Where a cluster id becomes something that can be written to.</param>
/// <param name="inventory">What says whether a namespace is empty. Refuses rather than guessing.</param>
/// <param name="namespaces">
///     The ensurer, because the delete and the memo that remembers the namespace exists have to be
///     the same object — see <c>NamespaceEnsurer.DeleteAsync</c>.
/// </param>
/// <param name="logger">Where a reclaim's refusals are recorded for an operator.</param>
public sealed class ResourceGroupReclaimer(
    IGrainFactory grains,
    IClusterConnectionFactory connections,
    INamespaceInventory inventory,
    NamespaceEnsurer namespaces,
    ILogger<ResourceGroupReclaimer> logger
) {
    /// <summary>
    ///     Runs the whole choreography for one group.
    /// </summary>
    /// <param name="scope">The group. ⚠ Must be a <see cref="ScopeKind.ResourceGroup" />.</param>
    /// <param name="cancellationToken">Cancels the reclaim.</param>
    /// <returns>
    ///     Success once the group's record is gone and every namespace it named has been reclaimed,
    ///     or the first refusal — with the group left sealed.
    /// </returns>
    public async Task<Result> DeleteAsync(ScopeId scope, CancellationToken cancellationToken = default) {
        if (scope.Kind != ScopeKind.ResourceGroup) {
            return Result.Failure(
                ErrorCode.InvalidResourceId,
                $"'{scope.Path}' is a {scope.Kind}, and this deletes a resource group."
            );
        }

        var tenant = scope.TenantId.ToString("D", CultureInfo.InvariantCulture);

        var group = grains
            .ForTenant(tenant)
            .GetGrain<IResourceGroupGrain>(GrainKeys.ResourceGroup(scope.SubscriptionId, scope.ResourceGroup));

        // ── 0. Sweep the phantoms first ──────────────────────────────────────────────────────────
        //
        // ⚠ WITHOUT THIS, A GROUP WHOSE ONLY MEMBER IS AN ORPHAN CAN NEVER BE DELETED. An orphan —
        // docs/plan/06 § Two-phase create: a name claimed and never confirmed — is a member record
        // for a resource that does not exist, so BeginGroupDeleteAsync refuses over it and the
        // tenant is told to delete a resource that is not there to delete. ReapOrphansAsync proves
        // each one against its own index before removing it, so this is not a licence to empty the
        // group: a member whose index is confirmed survives the sweep and the delete still refuses
        // over it, which is correct.
        var swept = await group.ReapOrphansAsync(IResourceGroupGrain.OrphanAge);

        if (swept.TryGetError(out var sweepError)) {
            return Result.Failure(sweepError);
        }

        if (swept.GetValueOrThrow() is { Count: > 0 } reaped) {
            logger.LogInformation(
                "Deleting resource group '{Group}' swept {Count} orphaned member(s) that claimed a "
                + "name and never confirmed it: {Paths}",
                scope.ResourceGroup,
                reaped.Count,
                string.Join(", ", reaped.Select(x => x.CanonicalPath))
            );
        }

        // ── 1. Seal. Nothing below this may run before it. ───────────────────────────────────────
        var sealed_ = await group.BeginGroupDeleteAsync();
        if (sealed_.TryGetError(out var sealError)) {
            return Result.Failure(sealError);
        }

        // ── 2. The namespaces, one per cluster the group ever touched ────────────────────────────
        var clusters = await group.ListClustersAsync();
        if (clusters.TryGetError(out var clusterError)) {
            return Result.Failure(clusterError);
        }

        var ns = ReconcileDriver.NamespaceFor(scope.SubscriptionId, scope.ResourceGroup);

        foreach (var clusterId in clusters.GetValueOrThrow()) {
            var reclaimed = await ReclaimAsync(group, clusterId, ns, scope, cancellationToken);

            if (reclaimed.TryGetError(out var reclaimError)) {
                // ⚠ THE FIRST REFUSAL STOPS EVERYTHING, and the group stays sealed. Carrying on to
                // the next cluster would end with a group record removed while one of its namespaces
                // is still full — the state in which nothing knows the namespace is anybody's, which
                // is the leak this whole issue is about, arrived at through the code meant to close
                // it.
                return Result.Failure(reclaimError);
            }
        }

        // ── 3. The group's own record ────────────────────────────────────────────────────────────
        var removed = await group.CompleteGroupDeleteAsync();
        if (removed.TryGetError(out var removeError)) {
            return Result.Failure(removeError);
        }

        // ── 4. The subscription's listing, LAST — see the remarks on why not first ───────────────
        var unlisted = await grains
            .ForTenant(tenant)
            .GetGrain<ISubscriptionGrain>(GrainKeys.Subscription(scope.SubscriptionId))
            .RemoveResourceGroupAsync(scope.ResourceGroup);

        if (unlisted.TryGetError(out var unlistError)) {
            // ⚠ The group's record is already gone, so this is a listing entry pointing at nothing —
            // a GET on it answers the canonical 404 and a re-drive removes it. Reported rather than
            // rolled back: recreating the group record to make the two agree would resurrect a group
            // whose namespaces have just been deleted.
            logger.LogError(
                "Resource group '{Group}' in subscription {Subscription} was deleted and could not "
                + "be removed from the subscription's listing: {Reason} The entry names a group that "
                + "no longer exists; deleting it again removes the entry.",
                scope.ResourceGroup,
                scope.SubscriptionId,
                unlistError.Message
            );

            return Result.Failure(unlistError);
        }

        return Result.Success;
    }

    /// <summary>
    ///     The namespace half, for one cluster: read what is in there, weigh it, and delete only on a
    ///     verdict.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Both halves of the evidence are read <i>after</i> the seal and neither is cached.</b>
    ///     The members come from the grain a second time — <c>BeginGroupDeleteAsync</c> has already
    ///     refused if there were any, and reading them again is what makes
    ///     <c>NamespaceReclaim.Decide</c>'s two-sided rule honest rather than a formality over a value
    ///     this method supplied itself.
    /// </remarks>
    async Task<Result> ReclaimAsync(
        IResourceGroupGrain group,
        Guid clusterId,
        string ns,
        ScopeId scope,
        CancellationToken cancellationToken
    ) {
        if (connections.Connect(clusterId) is not { } connection) {
            // ⚠ NOT a skip. A cluster we cannot reach is a namespace we cannot say anything about,
            // and "could not connect" must never converge into "reclaimed" — that is how a group is
            // reported deleted while its namespace and everything in it stays.
            return Result.Failure(
                ErrorCode.InternalError,
                $"Resource group '{scope.ResourceGroup}' has a namespace on cluster {clusterId:D} and "
                + "there is no connection to it, so nothing can say whether it is empty. The group "
                + "stays sealed and the delete can be re-driven when the cluster is reachable."
            );
        }

        var members = await group.ListAsync();
        if (members.TryGetError(out var memberError)) {
            return Result.Failure(memberError);
        }

        var occupants = await inventory.ListAllAsync(clusterId, ns, cancellationToken);
        if (occupants.TryGetError(out var inventoryError)) {
            return Result.Failure(
                inventoryError.Code,
                $"Namespace '{ns}' on cluster {clusterId:D} could not be enumerated, so resource "
                + $"group '{scope.ResourceGroup}' will not be reclaimed: {inventoryError.Message} "
                + "Nothing is deleted on a guess."
            );
        }

        var verdict = NamespaceReclaim.Decide(
            clusterId,
            ns,
            members.GetValueOrThrow(),
            occupants.GetValueOrThrow()
        );

        if (!verdict.Deletable) {
            if (verdict.OperatorReclaimable) {
                // ⚠ The honest answer for every group that ever ran a stateful type, and it is a
                // person's decision rather than a sweeper's — docs/plan/08 § Soft delete keeps a
                // purged resource's volumes on purpose. Logged at Warning so it is visible at the
                // level an operator filters on.
                logger.LogWarning(
                    "Namespace '{Namespace}' on cluster {Cluster} is finished with by the platform "
                    + "and is not empty, so it is left for an operator: {Why}",
                    ns,
                    clusterId,
                    verdict.Explain()
                );
            }

            return Result.Failure(
                ErrorCode.Conflict,
                $"Resource group '{scope.ResourceGroup}' cannot be deleted. " + verdict.Explain()
            );
        }

        return await namespaces.DeleteAsync(
            NamespaceEnsurer.GroupAddress(
                new(
                    scope.TenantId,
                    scope.SubscriptionId,
                    scope.ResourceGroup,
                    NamespaceEnsurer.GroupType,
                    scope.ResourceGroup,
                    NamespaceEnsurer.IdFor(scope.SubscriptionId, scope.ResourceGroup)
                )
            ),
            ns,
            connection,
            verdict,
            cancellationToken
        );
    }
}
