using CyberCloud.Core;
using Orleans.Providers;
using System.Globalization;

// ⚠ Orleans ships a PUBLIC `Orleans.ErrorCode` and the SDK imports the `Orleans` namespace globally,
// so the simple name is ambiguous here — the same trap CyberCloud.Tenancy/GlobalUsings.cs records.
using ErrorCode = CyberCloud.Core.ErrorCode;

namespace CyberCloud.Tenancy.Contracts;

/// <summary>
///     A silo's in-process mirror of the shard map, as the one call a tenant create needs from every
///     silo at once: refresh, then say which shard you would route this tenant to now.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Why a per-silo call exists for a cache that is filled by a timer.</b> The mirror is
///         <c>GrainBackedShardMapCache</c>, refreshed every <c>TenancyRefreshOptions.ShardMapInterval</c>
///         (fifteen seconds), and read from <c>DurableTierConfigurator.ConfigureForTenant</c> under a
///         per-tenant lock at the first activation of any of a tenant's grains on a silo — a path
///         that must never do I/O, so the cache cannot fetch on a miss. For a tenant it has not heard
///         of it falls back to the deterministic hash, and that is safe exactly when the recorded
///         assignment <i>is</i> the hash — which <c>ShardMapGrain.Place</c> arranges for the ordinary
///         placement. A pin (<see cref="IShardMapGrain.PinAsync" />, issue #39) exists to make the
///         record and the hash disagree, and a shard taken out of the rotation makes them disagree
///         too; for those, a tenant grain activated before the record reaches the silo has its
///         storage provider built against the hash-chosen shard and writes the tenant's first rows
///         there, and every silo that refreshes afterwards reads the pinned, empty one. The review
///         of issue #39 found that split.
///     </para>
///     <para>
///         <see cref="ShardMapPropagation.ConfirmAsync" /> closes it: after the assignment is
///         recorded and before the tenant's first durable write, it asks every silo's mirror this
///         question through <see cref="ShardMapMirrorController" /> and proceeds only when every
///         answer is the recorded shard. <c>ShardMapRefresher</c> is the implementation.
///     </para>
/// </remarks>
public interface IShardMapMirror {
    /// <summary>
    ///     Pulls the map's latest delta into this silo's mirror, then answers with the durable shard
    ///     the mirror now resolves the tenant to — the shard this silo's storage layer would build
    ///     the tenant's provider against if the tenant's first grain activated here next.
    /// </summary>
    /// <param name="tenantId">The tenant id in the hyphenated <c>"D"</c> form.</param>
    /// <returns>The shard id — the mirror's own answer, stale or not, never an exception for a stale one.</returns>
    Task<string> RefreshAndResolveAsync(string tenantId);
}

/// <summary>
///     The Orleans control-channel end of <see cref="IShardMapMirror" /> — what
///     <c>IManagementGrain.SendControlCommandToProvider</c> lands on, on each silo.
/// </summary>
/// <remarks>
///     ⚠ <b>A concrete class in the contracts assembly, because the runtime matches on the concrete
///     type.</b> Orleans 10.2.2's <c>SiloControl.SendControlCommandToProvider&lt;T&gt;</c> is, decompiled,
///     <c>GetKeyedServices&lt;IControllable&gt;(providerName).FirstOrDefault(svc =&gt; svc.GetType() ==
///     typeof(T))</c>: the registration is keyed as <see cref="IControllable" />, and <c>T</c> has to
///     be the registered object's own type — neither an interface it implements nor a base — or the
///     silo answers "Could not find a controllable service for type Orleans.Providers.IControllable"
///     (observed, with the mirror registered under an interface). The caller is in the resource
///     manager, which references this assembly and not <c>CyberCloud.Tenancy</c>, so the type it names
///     has to live here; <c>TenancySiloBuilderExtensions.AddCyberCloudTenancy</c> registers one of these
///     wrapping the silo's <c>ShardMapRefresher</c>.
/// </remarks>
/// <param name="mirror">The silo's mirror.</param>
public sealed class ShardMapMirrorController(IShardMapMirror mirror) : IControllable {
    /// <summary>The one command: <see cref="IShardMapMirror.RefreshAndResolveAsync" />.</summary>
    public const int RefreshAndResolve = 1;

    /// <inheritdoc />
    /// <param name="command"><see cref="RefreshAndResolve" />; anything else is a caller bug and throws.</param>
    /// <param name="arg">The tenant id in the hyphenated <c>"D"</c> form.</param>
    /// <returns>The shard id, as a <see cref="string" />.</returns>
    public async Task<object> ExecuteCommand(int command, object arg) {
        if (command != RefreshAndResolve) {
            throw new ArgumentOutOfRangeException(
                nameof(command),
                command,
                $"The shard map mirror answers one command, {nameof(RefreshAndResolve)} "
                + $"({RefreshAndResolve.ToString(CultureInfo.InvariantCulture)})."
            );
        }

        if (arg is not string tenantId || tenantId.Length == 0) {
            throw new ArgumentException(
                "The argument is the tenant id in the hyphenated 'D' form — IShardMapMirror.RefreshAndResolveAsync.",
                nameof(arg)
            );
        }

        return await mirror.RefreshAndResolveAsync(tenantId);
    }
}

/// <summary>
///     Confirms that a recorded shard assignment has reached every silo's mirror before the tenant
///     it places writes its first durable row.
/// </summary>
public static class ShardMapPropagation {
    /// <summary>
    ///     The keyed-service name every silo registers its <see cref="ShardMapMirrorController" />
    ///     under, and the <c>providerName</c> the fan-out addresses.
    /// </summary>
    public const string ProviderName = "cybercloud/shard-map";

    /// <summary>
    ///     Asks every silo to refresh its shard map mirror and to say which shard it now resolves the
    ///     tenant to, and succeeds only when every silo says the recorded one.
    /// </summary>
    /// <param name="grains">Any grain factory — a client's or a silo's.</param>
    /// <param name="assignment">The recorded assignment, as <see cref="IShardMapGrain.AssignAsync" /> returned it.</param>
    /// <returns>
    ///     Success when every silo resolves the tenant to <see cref="ShardAssignment.DurableShard" />.
    ///     <c>InternalError</c> naming the shard a silo disagreed with, or <c>OperationTimeout</c>
    ///     when a silo did not answer. Either way the caller has written nothing durable for the
    ///     tenant yet and refuses the create, which is the safe side: a tenant that does not exist
    ///     yet is retried, a tenant split across two shards is a data-loss incident.
    /// </returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Run for every tenant create, pinned or not, and cheap enough to be.</b> Whether
    ///         the record departs from the hash is the grain's business (<c>ShardMapGrain.Place</c>
    ///         departs when the hash lands on a drained shard) and this method does not recompute
    ///         the hash to find out — a second copy of the placement rule would be the drift this
    ///         exists to prevent. One fan-out per tenant creation, at a write rate docs/plan/05
    ///         § The tenant directory puts at 0.12/s, is nothing.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What it does not cover, said so it is not assumed.</b> A silo that joins the
    ///         cluster after the fan-out and activates one of the tenant's grains before its own
    ///         first refresh — <c>ShardMapRefreshService</c> refreshes once at start, so the window
    ///         is between the silo becoming active and that first pull. And a silo whose refresh
    ///         fails inside the fan-out answers with its stale mirror, which is a disagreement and a
    ///         refusal here rather than a silent fallback.
    ///     </para>
    /// </remarks>
    public static async Task<Result> ConfirmAsync(IGrainFactory grains, ShardAssignment assignment) {
        ArgumentNullException.ThrowIfNull(grains);
        ArgumentNullException.ThrowIfNull(assignment);

        var tenantId = assignment.TenantId.ToString("D", CultureInfo.InvariantCulture);
        object[] answers;

        try {
            answers = await grains
                .GetGrain<IManagementGrain>(0)
                .SendControlCommandToProvider<ShardMapMirrorController>(
                    ProviderName,
                    ShardMapMirrorController.RefreshAndResolve,
                    tenantId
                );
        } catch (Exception exception) when (exception is not OperationCanceledException) {
            return Result.Failure(
                ErrorCode.OperationTimeout,
                $"Tenant {tenantId} is recorded on durable shard '{assignment.DurableShard}' but not "
                + "every silo confirmed it: " + exception.Message + " No durable row has been written "
                + "for the tenant; retry the create once the cluster answers. Proceeding would let a "
                + "silo that has not refreshed its shard map place the tenant's first rows on the "
                + "shard the hash names instead — docs/plan/05 § The shard map."
            );
        }

        var disagreeing = answers
            .Select(x => x as string ?? "")
            .Where(x => !string.Equals(x, assignment.DurableShard, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        return disagreeing.Count == 0
            ? Result.Success
            : Result.Failure(
                ErrorCode.InternalError,
                $"Tenant {tenantId} is recorded on durable shard '{assignment.DurableShard}' and after "
                + $"a refresh {answers.Length.ToString(CultureInfo.InvariantCulture)} silo(s) answered, "
                + $"of which some still resolve it to '{string.Join("', '", disagreeing)}'. No durable "
                + "row has been written for the tenant; retry the create. A silo that cannot reach "
                + "the shard map keeps its last mirror and falls back to the hash for a tenant it has "
                + "not heard of, and a tenant created through it would be split across two shards — "
                + "docs/plan/05 § The shard map."
            );
    }
}
