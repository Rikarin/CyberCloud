using CyberCloud.ResourceManager.Contracts.Registry;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Multitenant;
using System.Globalization;

namespace CyberCloud.ResourceManager.Reconcile;

/// <summary>
///     What one backfill pass found. See <see cref="PeriodicPassBackfill" />.
/// </summary>
/// <param name="Resources">Members of a periodic type that were asked.</param>
/// <param name="Armed">How many of them had no <c>periodic-pass</c> reminder and got one.</param>
/// <param name="Unreadable">
///     How many tenants, subscriptions, groups or resources could not be asked. ⚠ Counted apart from
///     the rest because "already armed" and "could not tell" are different answers, and only the
///     second means a resource may still have no pass.
/// </param>
public readonly record struct PeriodicPassBackfilled(int Resources, int Armed, int Unreadable);

/// <summary>
///     Arms the <c>periodic-pass</c> reminder of every converged resource of a type that declares
///     <c>PassEvery</c> and has none, once per silo start — docs/plan/08 § The manager-started pass.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Found by #30's review, and the same hole <see cref="Expiry.ExpirySweeperBackfill" />
///         closes for recovery windows.</b> <c>ResourceGrain</c> arms the reminder when a write
///         converges, so every backup vault that converged before manager-started passes shipped has
///         no reminder, and its retention runs only when somebody writes to it. The vault's
///         <c>retention-is-enforced-on-passes</c> row promises a point outlives its window by at most
///         <c>PassEvery</c>, which was false for all of them. A reminder row lost with a restored
///         reminder table is the same hole.
///     </para>
///     <para>
///         ⚠ <b>The same walk, and the same price.</b> One call per tenant, subscription and resource
///         group, then one <c>ListAsync</c> per group, on every silo that starts. It asks nothing when
///         no registered type declares a period, which is the whole cost on a silo without one.
///         <see cref="IResourceGrain.ArmPeriodicPassAsync" /> is idempotent, so a second silo's walk
///         arms nothing.
///     </para>
///     <para>
///         ⚠ It never throws and never fails a start-up, for the reason the expiry backfill gives.
///     </para>
/// </remarks>
public sealed class PeriodicPassBackfill(
    IGrainFactory grains,
    IProviderRegistry registry,
    IOptions<PeriodicPassBackfillOptions> options,
    ILogger<PeriodicPassBackfill> logger
)
    : BackgroundService {
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        if (!options.Value.RunOnStart) {
            return;
        }

        try {
            await Task.Delay(options.Value.StartDelay, stoppingToken);

            var covered = await RunAsync(stoppingToken);

            logger.LogInformation(
                "Periodic-pass backfill asked {Resources} resource(s) of a periodic type and armed {Armed} "
                + "that had no reminder; {Unreadable} could not be read. A write arms the reminder when it "
                + "converges, and a resource that converged before its type had a pass has had no write since.",
                covered.Resources,
                covered.Armed,
                covered.Unreadable
            );
        } catch (OperationCanceledException) {
            // Shutdown.
        } catch (Exception error) {
            logger.LogWarning(
                error,
                "The periodic-pass backfill did not complete, so a converged resource of a periodic type may "
                + "still have no pass until somebody writes to it. IResourceGrain.ArmPeriodicPassAsync can be "
                + "called by hand, and the next silo start runs this again."
            );
        }
    }

    /// <summary>Walks every tenant, subscription and resource group, arming the reminders owed.</summary>
    /// <param name="cancellationToken">The host's shutdown token.</param>
    /// <returns>What the pass covered.</returns>
    /// <remarks>
    ///     Public for the reason <see cref="Expiry.ExpirySweeperBackfill.RunAsync" /> is: a test and an
    ///     operator both need to run it without restarting a silo.
    /// </remarks>
    public async Task<PeriodicPassBackfilled> RunAsync(CancellationToken cancellationToken = default) {
        var periodic = registry.Types.Where(static x => x.PassPeriod > TimeSpan.Zero).Select(static x => x.Type).ToHashSet();

        if (periodic.Count == 0) {
            return new(0, 0, 0);
        }

        var directory = await grains.GetGrain<ITenantDirectoryGrain>(GrainKeys.TenantDirectory()).GetDeltaAsync(0);

        if (directory.TryGetError(out var directoryError)) {
            logger.LogWarning(
                "The periodic-pass backfill could not read the tenant directory, so no resource was covered: {Reason}",
                directoryError.Message
            );

            return new(0, 0, 1);
        }

        var resources = 0;
        var armed = 0;
        var unreadable = 0;

        foreach (var entry in directory.GetValueOrThrow().Entries) {
            cancellationToken.ThrowIfCancellationRequested();

            if (entry.Status == TenantStatus.Purged) {
                continue;
            }

            var tenant = grains.ForTenant(entry.TenantId.ToString("D", CultureInfo.InvariantCulture));
            var subscriptions = await tenant.GetGrain<ITenantGrain>(GrainKeys.Tenant(entry.TenantId)).ListSubscriptionsAsync();

            if (subscriptions.IsFailure) {
                unreadable++;
                continue;
            }

            foreach (var subscription in subscriptions.GetValueOrThrow()) {
                var names = await tenant.GetGrain<ISubscriptionGrain>(GrainKeys.Subscription(subscription)).ListResourceGroupsAsync();

                if (names.IsFailure) {
                    unreadable++;
                    continue;
                }

                foreach (var name in names.GetValueOrThrow()) {
                    var members = await tenant.GetGrain<IResourceGroupGrain>(GrainKeys.ResourceGroup(subscription, name)).ListAsync();

                    if (members.IsFailure) {
                        unreadable++;
                        continue;
                    }

                    foreach (var member in members.GetValueOrThrow()) {
                        // ⚠ Succeeded members only. A member in any other state has an operation that
                        // arms the reminder when it converges, or is waiting for its owner.
                        if (member.State != ProvisioningState.Succeeded
                            || ResourceId.ParsePath(member.CanonicalPath) is not { IsSuccess: true } address
                            || !periodic.Contains(address.GetValueOrThrow().Type)) {
                            continue;
                        }

                        resources++;

                        var covered = await tenant.GetGrain<IResourceGrain>(GrainKeys.Resource(member.ResourceId)).ArmPeriodicPassAsync();

                        if (covered.IsFailure) {
                            unreadable++;
                        } else if (covered.GetValueOrThrow()) {
                            armed++;
                        }
                    }
                }
            }
        }

        return new(resources, armed, unreadable);
    }
}

/// <summary>Whether and when <see cref="PeriodicPassBackfill" /> runs.</summary>
/// <remarks>
///     ⚠ Off is for tests, for the reason <see cref="Expiry.ExpirySweeperBackfillOptions" /> gives: a
///     suite asserting which resources are armed cannot share a process with a walk quietly arming them.
/// </remarks>
public sealed class PeriodicPassBackfillOptions {
    /// <summary>Whether the backfill runs at silo start. On by default.</summary>
    public bool RunOnStart { get; set; } = true;

    /// <summary>How long after start-up the walk begins.</summary>
    public TimeSpan StartDelay { get; set; } = TimeSpan.FromSeconds(30);
}
