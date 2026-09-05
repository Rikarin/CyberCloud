using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Multitenant;
using System.Globalization;

namespace CyberCloud.ResourceManager.Expiry;

/// <summary>
///     What one backfill pass found. See <see cref="ExpirySweeperBackfill" />.
/// </summary>
/// <param name="Groups">Resource groups asked.</param>
/// <param name="Armed">How many of them had something parked and were not already armed.</param>
/// <param name="Unreadable">
///     How many could not be asked at all — a registry that would not list, a subscription that would
///     not enumerate. ⚠ Counted separately from <see cref="Groups" /> minus <see cref="Armed" />
///     because "nothing parked here" and "could not tell" are different answers, and only the second
///     one means a group may still be uncovered.
/// </param>
public readonly record struct ExpiryBackfill(int Groups, int Armed, int Unreadable);

/// <summary>
///     Arms the expiry sweeper of every resource group that already has something parked, once per
///     silo start — issue #12.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THIS EXISTS BECAUSE ARMING FROM THE WRITE PATH COVERS ONLY THE WINDOWS THAT OPEN
///         AFTER IT SHIPS.</b> <c>IExpirySweeperGrain.ArmAsync</c> has two callers and both sit on a
///         path that has just added a registry entry — <c>OperationGrain.ParkAsync</c> and
///         <c>ResourceManagerService.RepairParkedRegistryAsync</c>. So on the deploy that first
///         carries the sweeper, every resource already inside a recovery window has nothing driving
///         it, and a resource group whose last delete has already happened would never acquire a
///         sweeper at all: its windows would end and nothing would notice, which is the exact state
///         issue #12 exists to close. The same hole swallows the two losses
///         <c>IExpirySweeperGrain.ArmAsync</c> tolerates by design — a park that ran on a silo with
///         no reminder service, and a reminder table restored from a backup — because both leave a
///         registry entry with no row behind it.
///     </para>
///     <para>
///         ⚠ <b>AN ENUMERATION IS THE ONLY THING THAT CAN CLOSE IT, AND SAYING SO IS THE POINT.</b>
///         Every event-driven arm — on a park, on a repair, on the sweeper's own activation — needs
///         something to happen in the group, and the uncovered case is defined by nothing happening
///         in it. <c>IExpirySweeperGrain</c>'s own remarks reject arming on activation for the same
///         reason: a group nobody touches is a group whose grain is never activated. What is left is
///         to walk the platform, which is what this does, over the three enumerations that already
///         exist — <c>ITenantDirectoryGrain.GetDeltaAsync</c>,
///         <c>ITenantGrain.ListSubscriptionsAsync</c> and
///         <c>ISubscriptionGrain.ListResourceGroupsAsync</c>.
///     </para>
///     <para>
///         ⚠ <b>ONCE, AT START, AND NOT ON A TIMER.</b> The steady state is already covered: a park
///         arms, a repair arms, and a sweep that finds anything re-arms. A period here would be a
///         second clock over the same registries — the "second durable copy" objection
///         <c>IExpirySweeperGrain</c> makes to the recorded design, re-made against itself — and it
///         would put the cost below on a repeating schedule rather than on a restart.
///     </para>
///     <para>
///         ⚠ <b>WHAT IT COSTS, SAID HERE BECAUSE IT IS THE PRICE OF CLOSING THE HOLE.</b> One grain
///         call per tenant, one per subscription and one per resource group, on every silo that
///         starts — and the per-group call reads that group's parked registry, so it activates one
///         registry grain per resource group. There is no leader election in this tree, so every
///         silo does the whole walk; the calls are idempotent and the second silo's arms all answer
///         <see langword="false" />, so what is duplicated is the reading rather than the writing.
///         ⚠ It is the shape that would have to change first if the platform grew past the point
///         where a walk per silo start is affordable — a resumable cursor, or one silo holding it —
///         and neither is built here, because both are a design rather than a fix and the hole is
///         open until something walks.
///     </para>
///     <para>
///         ⚠ <b>It never throws and it never fails a start-up.</b> A silo whose directory is
///         unreachable is a silo that cannot serve tenants either, and the failure that matters will
///         be reported by whatever is actually asking. What this does instead is log what it covered
///         and what it could not, so "some group is not being swept" is a line an operator can find
///         rather than a silence.
///     </para>
/// </remarks>
public sealed class ExpirySweeperBackfill(
    IGrainFactory grains,
    IOptions<ExpirySweeperBackfillOptions> options,
    ILogger<ExpirySweeperBackfill> logger
)
    : BackgroundService {
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        if (!options.Value.RunOnStart) {
            return;
        }

        try {
            // ⚠ AFTER A DELAY, BECAUSE A SILO THAT IS STILL JOINING CANNOT ANSWER GRAIN CALLS AND
            // BECAUSE NOTHING HERE IS URGENT. The windows this covers have been open for hours or
            // days already, and the first sweep of anything armed is a SweepPeriod away regardless,
            // so trading a little more lateness for not competing with start-up is free — the
            // asymmetry IExpirySweeperGrain.SweepPeriod's remarks describe, applied to the walk.
            await Task.Delay(options.Value.StartDelay, stoppingToken);

            var covered = await RunAsync(stoppingToken);

            logger.LogInformation(
                "Expiry-sweeper backfill looked at {Groups} resource group(s) and armed {Armed} that "
                + "had resources parked with no sweeper; {Unreadable} could not be read. Issue #12: "
                + "arming happens on a park, and a window that opened before this silo had a sweeper "
                + "has no park left to arm it.",
                covered.Groups,
                covered.Armed,
                covered.Unreadable
            );
        }
        catch (OperationCanceledException) {
            // Shutdown.
        }
        catch (Exception error) {
            logger.LogWarning(
                error,
                "The expiry-sweeper backfill did not complete, so a resource group whose recovery "
                + "windows opened before this silo had a sweeper may still have nothing ending them. "
                + "IExpirySweeperGrain.SweepAsync arms as well as sweeps and can be called by hand, "
                + "and the next silo start runs this again — issue #12."
            );
        }
    }

    /// <summary>
    ///     Walks every tenant, subscription and resource group, arming the sweepers that are owed
    ///     one.
    /// </summary>
    /// <param name="cancellationToken">The host's shutdown token.</param>
    /// <returns>What the pass covered.</returns>
    /// <remarks>
    ///     ⚠ <b>Public and separate from <see cref="ExecuteAsync" /> for the reason
    ///     <c>IExpirySweeperGrain.SweepAsync</c> is public:</b> a backfill nobody can run is a
    ///     backfill nobody can test without starting a host and waiting, and an operator who has just
    ///     restored a reminder table needs a way to re-cover the platform that is not "restart every
    ///     silo".
    /// </remarks>
    public async Task<ExpiryBackfill> RunAsync(CancellationToken cancellationToken = default) {
        // ⚠ The whole directory, which is what a knownVersion of 0 asks
        // ITenantDirectoryGrain.GetDeltaAsync for — its own remarks call that the first call's
        // answer. A platform singleton, so it is reached without ForTenant, exactly as
        // ScopeManagerService reaches it.
        var directory = await grains.GetGrain<ITenantDirectoryGrain>(GrainKeys.TenantDirectory()).GetDeltaAsync(0);

        if (directory.TryGetError(out var directoryError)) {
            logger.LogWarning(
                "The expiry-sweeper backfill could not read the tenant directory, so no resource "
                + "group was covered: {Reason}",
                directoryError.Message
            );

            return new(0, 0, 0);
        }

        var groups = 0;
        var armed = 0;
        var unreadable = 0;

        foreach (var entry in directory.GetValueOrThrow().Entries) {
            cancellationToken.ThrowIfCancellationRequested();

            // ⚠ A PURGED TENANT IS SKIPPED AND NOTHING ELSE IS. Suspended, Disabled and Warned
            // tenants keep their data — TenantStatus says so in as many words — so their recovery
            // windows are still windows, and a tenant that cannot make control-plane writes is
            // precisely the one that cannot park anything to arm its own sweeper.
            if (entry.Status == TenantStatus.Purged) {
                continue;
            }

            var tenant = grains.ForTenant(entry.TenantId.ToString("D", CultureInfo.InvariantCulture));

            var subscriptions = await tenant
                .GetGrain<ITenantGrain>(GrainKeys.Tenant(entry.TenantId))
                .ListSubscriptionsAsync();

            if (subscriptions.TryGetError(out var subscriptionError)) {
                unreadable++;

                logger.LogWarning(
                    "Tenant {TenantId}'s subscriptions could not be listed, so none of its resource "
                    + "groups was covered by the expiry-sweeper backfill: {Reason}",
                    entry.TenantId,
                    subscriptionError.Message
                );

                continue;
            }

            foreach (var subscription in subscriptions.GetValueOrThrow()) {
                var names = await tenant
                    .GetGrain<ISubscriptionGrain>(GrainKeys.Subscription(subscription))
                    .ListResourceGroupsAsync();

                if (names.TryGetError(out var groupError)) {
                    unreadable++;

                    logger.LogWarning(
                        "Subscription {Subscription}'s resource groups could not be listed, so none "
                        + "of them was covered by the expiry-sweeper backfill: {Reason}",
                        subscription,
                        groupError.Message
                    );

                    continue;
                }

                foreach (var name in names.GetValueOrThrow()) {
                    groups++;

                    var sweeper = tenant.GetGrain<IExpirySweeperGrain>(
                        GrainKeys.ExpirySweeper(subscription, name)
                    );

                    var covered = await sweeper.ArmIfParkedAsync();

                    if (covered.TryGetError(out var sweeperError)) {
                        unreadable++;

                        logger.LogWarning(
                            "Resource group '{Group}' in subscription {Subscription} could not be "
                            + "asked whether it has anything parked, so if it has, nothing is ending "
                            + "those windows on a clock: {Reason}",
                            name,
                            subscription,
                            sweeperError.Message
                        );

                        continue;
                    }

                    if (covered.GetValueOrThrow()) {
                        armed++;

                        logger.LogInformation(
                            "Resource group '{Group}' in subscription {Subscription} had resources "
                            + "parked and no sweep reminder, and one was registered. Issue #12: this "
                            + "is a window that opened before anything drove PurgeExpiredAsync on a "
                            + "clock, or one whose reminder row was lost.",
                            name,
                            subscription
                        );
                    }
                }
            }
        }

        return new(groups, armed, unreadable);
    }
}

/// <summary>
///     Whether and when <see cref="ExpirySweeperBackfill" /> runs.
/// </summary>
/// <remarks>
///     ⚠ <b>Off is for tests, and it is the same knob <c>TenancyRefreshOptions.RunBackgroundRefresh</c>
///     is, for the same reason.</b> A suite that asserts on which resource groups are armed cannot
///     share a process with a loop that is quietly arming them, and the assertion would pass or fail
///     on timing. A harness turns this off and calls <see cref="ExpirySweeperBackfill.RunAsync" />
///     explicitly, which exercises the same method the host runs.
/// </remarks>
public sealed class ExpirySweeperBackfillOptions {
    /// <summary>Whether the backfill runs at silo start. On by default.</summary>
    /// <remarks>
    ///     ⚠ <b>Defaulted ON because a host that has to remember a second call will not.</b>
    ///     <c>ResourceManagerSiloBuilderExtensions</c> carries the tree's own version of that lesson:
    ///     <c>AddCyberCloudProvider</c> had no caller anywhere for a while and no silo served a
    ///     single resource type. A backfill that is off unless asked for is a backfill that is off.
    /// </remarks>
    public bool RunOnStart { get; set; } = true;

    /// <summary>How long after start-up the walk begins.</summary>
    public TimeSpan StartDelay { get; set; } = TimeSpan.FromSeconds(30);
}
