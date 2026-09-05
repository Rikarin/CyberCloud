using CyberCloud.ResourceManager.Contracts.Registry;
using Microsoft.Extensions.Logging;
using Orleans.Multitenant;
using System.Collections.Immutable;
using System.Globalization;

namespace CyberCloud.ResourceManager.Grains;

/// <summary>
///     <see cref="IExpirySweeperGrain" /> — Entity, <b>no persistent state</b>, key
///     <c>sweep/{subscriptionId:N}/rg/{name}</c>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>IT HOLDS NOTHING, AND THAT IS WHY IT IS NOT IN <c>durable-grains.txt</c>.</b>
///         Everything a sweep needs is somewhere that already owns it: what is parked is
///         <see cref="IParkedResourceRegistryGrain" />'s, whether an entry is still true is
///         <c>IResourceIndexGrain.ResolveSoftDeletedAsync</c>'s, and whether a window has ended is
///         <c>IResourceIndexGrain.ResolveExpiredAsync</c>'s. The one piece of durable state behind
///         this grain is its reminder row, which Orleans keeps in the reminder table — and a
///         reminder is not a fact about a resource, which is the whole reason
///         <see cref="IExpirySweeperGrain" /> argues for arming off "there is something parked"
///         rather than off a deadline.
///     </para>
///     <para>
///         ⚠ <b>It decides one thing and one thing only: whether a registry entry is still true.</b>
///         It does not decide whether a window has ended — <c>PurgeExpiredAsync</c> asks the grain
///         that stamped it — and it does not decide who may purge, because nobody is asking. Adding
///         a comparison against <c>IndexEntry.RecoverableUntil</c> here would put this process's
///         clock on the one path where being early destroys something still restorable, which is the
///         defect docs/plan/07 § Azure RBAC took the whole split to avoid.
///     </para>
///     <para>
///         ⚠ <b>Every grain it reaches is one a purge does not reach back into.</b> That is not a
///         coincidence: <c>ResourceManagerService.PurgeCoreAsync</c> calls the resource grain, the
///         index grain, the parked registry and a fresh operation grain, so a sweeper living on any
///         of those would await a call back into its own activation. See
///         <c>GrainKeys.ExpirySweeper</c>.
///     </para>
///     <para>
///         ⚠ <b>This is the first place the write path runs <i>inside a grain</i>, and the
///         consequence is in the platform's favour rather than against it.</b>
///         <c>IResourceManager</c> is documented as a service held by the gateway, and the gateway is
///         an Orleans <i>client</i> — which <c>CyberCloud.Tenancy</c>'s tenant-separation wiring says
///         in as many words is outside <c>TenantSeparatingCallFilter</c>, because that filter reads
///         <c>context.SourceId</c> and returns without asking when the source is a client. A sweep's
///         calls carry this grain as their source, so every grain the purge touches is checked
///         against this activation's tenant on the way in. Nothing here relies on that — every grain
///         is reached through <c>ForTenant</c> with this grain's own tenant, so the filter has
///         nothing to refuse — and it is written down because it is the one respect in which the
///         clock-driven front is <i>more</i> constrained than the typed one.
///     </para>
/// </remarks>
public sealed class ExpirySweeperGrain(
    IResourceManager manager,
    IProviderRegistry registry,
    IGrainFactory grains,
    ILogger<ExpirySweeperGrain> logger
)
    : Grain, IExpirySweeperGrain, IRemindable {
    /// <summary>The reminder's name. One reminder per resource group.</summary>
    public const string ReminderName = "sweep-expired";

    Guid tenantId;
    Guid subscriptionId;
    string group = string.Empty;

    /// <inheritdoc />
    public override Task OnActivateAsync(CancellationToken cancellationToken) {
        tenantId = ResourceManagerGrainKeys.TenantOf(this);

        var key = ResourceManagerGrainKeys.Decode(this, GrainKeyKind.ExpirySweeper);
        subscriptionId = key.Id;
        group = key.Name;

        // ⚠ NOTHING IS ARMED HERE, AND THE ABSENCE IS DELIBERATE. An activation is not evidence that
        // this group has anything parked — a sweep, a hand call or the arm itself brings the grain
        // up — so arming on activation would register a reminder for every group anybody ever asks
        // about, including the one whose last entry a purge has just cleared. The reminder row is
        // durable in Orleans' own table and survives a silo loss without help; what re-establishes
        // it after it is genuinely lost is the next ArmAsync, which every writer that adds a registry
        // entry makes.
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<Result> ArmAsync() {
        try {
            _ = await this.RegisterOrUpdateReminder(
                ReminderName,
                IExpirySweeperGrain.SweepPeriod,
                IExpirySweeperGrain.SweepPeriod
            );

            return Result.Success;
        }
        catch (InvalidOperationException error) {
            // ⚠ SUCCESS, AND THE CALLER IS THE REASON. Both callers are on a path that is parking a
            // resource, and a park that failed because the platform could not arrange to purge the
            // resource in seven days' time would leave the delete stuck for ever over a schedule.
            // The same call, the same refusal and the same decision as
            // ResourceGroupGrain.ArmOrDisarmAsync's, whose remarks put it as "turning an absent
            // cleanup into an absent platform". What is lost is exactly what master loses today:
            // nothing ends this group's windows on the clock's account. SweepAsync is still callable
            // and POST …/purge still works.
            logger.LogWarning(
                "Resource group '{Group}' in subscription {Subscription} could not arm its expired-"
                + "window sweeper because this silo has no reminder service: {Reason} Recovery "
                + "windows in this group will not be ended automatically here; "
                + "IExpirySweeperGrain.SweepAsync still works and an authorized purge is unaffected.",
                group,
                subscriptionId,
                error.Message
            );

            return Result.Success;
        }
    }

    /// <inheritdoc />
    public async Task<Result<ExpirySweep>> SweepAsync() {
        var listed = await Parked().ListAsync();
        if (listed.TryGetError(out var listError)) {
            // ⚠ THE PASS FAILS RATHER THAN REPORTING AN EMPTY ONE, and the reminder stays armed. A
            // registry that could not be read is not a registry with nothing in it, and the
            // difference matters here more than it does at a listing: "nothing parked" is also the
            // condition this method disarms on, so treating a failure as empty would cancel the
            // sweeper of a group whose entries it never saw.
            return Result<ExpirySweep>.Failure(listError);
        }

        var entries = listed.GetValueOrThrow();

        // ⚠ THE ORDER IS THE REGISTRY'S — canonical path, ordinally — so a group that is over the cap
        // works through the same prefix on every tick rather than sampling a different arbitrary
        // subset each time. A resource near the end of the ordering is reached once the ones before
        // it have been purged, which they will be, because a purge removes its entry.
        var batch = entries.Take(IExpirySweeperGrain.MaxPerSweep).ToList();

        // One id per PASS and not per purge: ExpiredPurgeRequest.CorrelationId's remarks say it "says
        // which sweep ended this window", singular, and every operation record this pass produces
        // should point back at the same tick.
        var correlationId = $"sweep/{Guid.NewGuid():N}";

        var purged = ImmutableArray.CreateBuilder<string>();
        var forgotten = ImmutableArray.CreateBuilder<string>();

        foreach (var entry in batch) {
            var address = entry.AddressOf();

            if (address.Id == Guid.Empty) {
                // Unreachable: ParkAsync refuses an unresolved address, and AddressOf re-parses a
                // path its own ResourceId.Path produced. If it ever were reachable, an entry nothing
                // can address is one this pass must not act on — leaving it visible in the registry
                // is what makes it findable.
                continue;
            }

            if (await StillParkedAsync(entry) is false) {
                // ⚠ THE RECONCILE, AND IT RUNS BEFORE THE PURGE RATHER THAN AFTER. See
                // StillParkedAsync for why the index rather than the manager answers this, and
                // ExpirySweep.Forgotten for what the entry being false means.
                var unparked = await Parked().UnparkAsync(entry.ResourceId);

                if (unparked.IsSuccess) {
                    forgotten.Add(address.CanonicalPath);
                }

                continue;
            }

            if (await EndWindowAsync(address, correlationId)) {
                purged.Add(address.CanonicalPath);
            }
        }

        var report = new ExpirySweep {
            Examined = batch.Count,
            Purged = purged.ToImmutable(),
            Forgotten = forgotten.ToImmutable(),
            Deferred = entries.Count - batch.Count,
            Disarmed = await DisarmIfNothingIsParkedAsync()
        };

        Report(report);

        return Result<ExpirySweep>.Success(report);
    }

    /// <inheritdoc />
    public async Task ReceiveReminder(string reminderName, TickStatus status) {
        if (!string.Equals(reminderName, ReminderName, StringComparison.Ordinal)) {
            return;
        }

        // ⚠ The result is discarded because there is nobody to hand it to and nothing to do with a
        // refusal: a failed pass leaves the registry exactly as it found it and the next tick tries
        // again. SweepAsync has already logged whatever it did — the same shape as
        // ResourceGroupGrain.ReceiveReminder's discard of ReapOrphansAsync.
        _ = await SweepAsync();
    }

    /// <inheritdoc />
    public Task DeactivateAsync() {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Whether the index still says this entry is true — soft-deleted, and of this GUID.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>THE INDEX RATHER THAN THE MANAGER, BECAUSE THE MANAGER CANNOT TELL THIS PASS
    ///         APART FROM AN UNEXPIRED WINDOW AND MUST NOT.</b>
    ///         <c>IResourceIndexGrain.ResolveExpiredAsync</c> answers the canonical absence for both
    ///         "there is no parked resource here" and "its window has not ended yet", and its own
    ///         remarks say why that identity is deliberate: <i>"a mechanism that could tell them
    ///         apart would be a mechanism whose retries encode how much window is left"</i>. So a
    ///         sweep that inferred staleness from a refused purge would be inferring it from a
    ///         sentence written not to carry it. <c>ResolveSoftDeletedAsync</c> is a different
    ///         question with a different answer and no deadline in it at all.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An index that did not answer leaves the entry alone, which is
    ///         <c>ResourceGroupGrain.ReapOrphansAsync</c>'s rule and for its reason:</b> reaping on a
    ///         shard that is unreachable turns a storage outage into resources vanishing from a
    ///         listing. The only failure this grain can actually receive is
    ///         <see cref="ErrorCode.ResourceNotFound" /> — the index answered and said the binding is
    ///         not soft-deleted — because a grain that could not be read throws rather than returning
    ///         one. Anything else is treated as "no answer" on purpose, so that a refusal added to
    ///         that method later does not silently become a reason to delete a registry entry.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The GUID is compared and not only the state.</b> A name that was purged,
    ///         re-created and soft-deleted again is soft-deleted at the same address and is a
    ///         different resource; the old entry offers a restore of something that no longer exists.
    ///         The same comparison <c>RepairParkedRegistryAsync</c> makes before it re-parks, from
    ///         the other side.
    ///     </para>
    /// </remarks>
    async Task<bool> StillParkedAsync(ParkedResource entry) {
        var bound = await Index(entry.AddressOf()).ResolveSoftDeletedAsync();

        if (bound.IsSuccess) {
            return bound.GetValueOrThrow() == entry.ResourceId;
        }

        return bound.Error!.Code != ErrorCode.ResourceNotFound;
    }

    /// <summary>
    ///     Hands one parked resource to the clock-driven front of purge, and says whether it took it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>EVERY REFUSAL IS ORDINARY HERE, WHICH IS WHY NONE OF THEM IS AN ERROR.</b> The
    ///         common one is the window that has not ended, which every unexpired entry produces on
    ///         every tick until it does; the next is a <c>CanNotDelete</c> or <c>ReadOnly</c> lock,
    ///         which docs/plan/07 § Azure RBAC decided the clock does not outrank; and a restore that
    ///         landed a moment ago produces a third. All three mean "not this pass", the entry stays
    ///         where it is, and the next tick asks again.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The api-version is the type's newest, and that is a substitution rather than a
    ///         lookup.</b> <see cref="ExpiredPurgeRequest" /> says a driver <i>"records the version
    ///         the resource was stored under and hands it back"</i>; nothing durable records it —
    ///         <c>ResourceState.ApiVersion</c> is the version of the last write, but
    ///         <c>IResourceGrain.GetAsync</c> needs a version in order to be asked and echoes back
    ///         the one it was given, so there is no version-free way to read it. What makes the
    ///         substitution safe is that the purge path reads nothing per-version:
    ///         <c>PurgeCoreAsync</c> takes the whole stored superset (<c>GetAsync(version, [])</c>,
    ///         empty pointers), and purge protection, the meters and the permissions are all on
    ///         <c>ResourceTypeRegistration</c> rather than on an <c>ApiVersionRegistration</c>. The
    ///         version reaches <c>ResolveAsync</c>, which must accept it, and the change notification,
    ///         which reports it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A type the registry does not serve is skipped rather than purged.</b> The only
    ///         way a registry entry can name one is a provider that was withdrawn from this host
    ///         while a window was running, and the safe direction there is obvious: an entry stays,
    ///         the resource keeps its name, and a host that serves the type again ends the window.
    ///         It is deliberately <i>not</i> treated as a stale entry — the index still says
    ///         soft-deleted, so the entry is true and removing it would hide the resource from the
    ///         one listing that names it.
    ///     </para>
    /// </remarks>
    async Task<bool> EndWindowAsync(ResourceId address, string correlationId) {
        if (!registry.TryGetType(address.Type, out var registration) || registration.Newest.IsEmpty) {
            logger.LogWarning(
                "'{Path}' is parked in resource group '{Group}' and this host serves no api-version "
                + "of '{Type}', so its recovery window cannot be ended here. The entry stays: the "
                + "index still says it is soft-deleted, and a host that serves the type will end it.",
                address.CanonicalPath,
                group,
                address.Type
            );

            return false;
        }

        var ended = await manager.PurgeExpiredAsync(
            new() {
                Path = address.Path, ApiVersion = registration.Newest.Value, CorrelationId = correlationId
            }
        );

        if (ended.IsSuccess) {
            return true;
        }

        // Debug rather than Warning: the overwhelmingly common case is a window that is still
        // running, and a per-entry warning every hour for seven days per parked resource would bury
        // the log line that matters in the one that never does.
        logger.LogDebug(
            "'{Path}' was not purged by sweep {Correlation}: {Reason}",
            address.CanonicalPath,
            correlationId,
            ended.Error!.Message
        );

        return false;
    }

    /// <summary>
    ///     Cancels the reminder when this group has nothing parked left.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Armed only while there is something to sweep, which is
    ///         <c>ResourceGroupGrain.ArmOrDisarmAsync</c>'s cost decision reached from the other
    ///         side.</b> A standing reminder per resource group platform-wide would be a row per
    ///         group and a tick per group per hour, for ever, to look at a registry that is empty for
    ///         all but a few days of a group's life.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The registry is listed AGAIN rather than the pass's own list being reused, and
    ///         the second read is the point.</b> Between the list at the top of a pass and this line
    ///         sits every purge the pass drove, and a delete in the same group can park a resource in
    ///         that whole interval; disarming on the older answer would cancel the sweeper of a group
    ///         that had just acquired a window. Re-reading narrows the window to the one grain call
    ///         between this list and the cancellation below.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>THAT RESIDUAL IS REAL AND IS LEFT, AND THE DIRECTION IS WHY.</b> An
    ///         <see cref="ArmAsync" /> that lands between the two would be undone, and the group's
    ///         registry would then hold an entry nothing sweeps until its next park. That is
    ///         <i>under</i>-driving: the resource keeps its name and its committed quota a while
    ///         longer, which is precisely the state master leaves every parked resource in today, and
    ///         it is recovered by the next park in the group, by an ordinary purge, or by
    ///         <see cref="SweepAsync" /> called by hand. The other direction — a purge running early
    ///         — is the one that cannot be recovered from, and nothing here can produce it.
    ///     </para>
    /// </remarks>
    /// <returns>
    ///     Whether this pass found nothing parked — <see cref="ExpirySweep.Disarmed" />. It is the
    ///     decision rather than the outcome, so a silo with no reminder service still reports
    ///     <see langword="true" />: there was never a row for it to cancel.
    /// </returns>
    async Task<bool> DisarmIfNothingIsParkedAsync() {
        var remaining = await Parked().ListAsync();

        if (remaining.IsFailure || remaining.GetValueOrThrow().Count > 0) {
            return false;
        }

        try {
            if (await this.GetReminder(ReminderName) is { } existing) {
                await this.UnregisterReminder(existing);
            }
        }
        catch (InvalidOperationException error) {
            // The silo that has no reminder service also has nothing to cancel — the arm that would
            // have created this row already logged and carried on. Swallowed for the same reason.
            logger.LogDebug(
                "Resource group '{Group}' has nothing parked and no reminder service to disarm: "
                + "{Reason}",
                group,
                error.Message
            );
        }

        return true;
    }

    /// <summary>One log line per pass, and only when the pass did something.</summary>
    /// <remarks>
    ///     ⚠ A sweeper that logged every tick would print, for a group with one parked resource,
    ///     168 lines saying "nothing yet" for every line that says something happened. What is worth
    ///     a record is a purge nobody asked for and an entry the index disagreed with — both of them
    ///     are things a person may have to explain afterwards.
    /// </remarks>
    void Report(ExpirySweep sweep) {
        if (sweep.Purged.Length == 0 && sweep.Forgotten.Length == 0) {
            return;
        }

        logger.LogInformation(
            "Expiry sweep of resource group '{Group}' in subscription {Subscription} examined "
            + "{Examined} parked resource(s) and deferred {Deferred}: purged {PurgedCount} whose "
            + "recovery window had ended ({Purged}), and forgot {ForgottenCount} registry entry(s) "
            + "the index no longer agrees with ({Forgotten}). Nobody authorized the purges — "
            + "docs/plan/07 § Azure RBAC: an expiry is not a request, and what stands where the "
            + "check stands is IResourceIndexGrain.ResolveExpiredAsync.",
            group,
            subscriptionId,
            sweep.Examined,
            sweep.Deferred,
            sweep.Purged.Length,
            string.Join(", ", sweep.Purged),
            sweep.Forgotten.Length,
            string.Join(", ", sweep.Forgotten)
        );
    }

    /// <summary>The registry this sweeper reads — the same resource group, one key shape over.</summary>
    IParkedResourceRegistryGrain Parked() =>
        Tenant()
            .GetGrain<IParkedResourceRegistryGrain>(GrainKeys.ParkedResourceRegistry(subscriptionId, group));

    /// <summary>
    ///     One parked resource's path index — the grain that owns both the binding and the deadline.
    /// </summary>
    /// <remarks>
    ///     ⚠ Reached by the entry's own address, which is what makes the answer about that entry
    ///     rather than about the name: <c>GrainKeys.PathIndex</c> digests the path, so an entry whose
    ///     name has since been given to a new resource reaches the <i>same</i> grain and is told,
    ///     by the GUID comparison in <see cref="StillParkedAsync" />, that it is no longer the one
    ///     bound there.
    /// </remarks>
    IResourceIndexGrain Index(ResourceId address) =>
        Tenant().GetGrain<IResourceIndexGrain>(GrainKeys.PathIndex(address));

    TenantGrainFactory Tenant() => grains.ForTenant(tenantId.ToString("D", CultureInfo.InvariantCulture));
}
