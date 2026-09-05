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
///         rather than off a deadline. The one field it keeps across a pass — <see cref="resumeFrom" />,
///         the rotation cursor — is in memory and is a scheduling hint rather than a fact about a
///         resource: losing it repeats a window of the registry, which is what every pass did before
///         it existed.
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

    /// <summary>
    ///     Where the next pass starts, or <see langword="null" /> to start at the beginning.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>THE ROTATION <c>IExpirySweeperGrain.MaxPerSweep</c> ARGUES FOR, AND IT IS IN
    ///         MEMORY BECAUSE MAKING IT DURABLE WOULD COST MORE THAN LOSING IT DOES.</b> Losing it —
    ///         a silo restart, a migration, a collection between ticks — restarts the rotation at the
    ///         head of the ordering, which is where every pass started before this field existed; it
    ///         cannot make a pass act on the wrong entry, because the entry is re-read from the
    ///         registry and re-checked against the index either way. Persisting it would put this
    ///         grain in <c>durable-grains.txt</c> and add a state write per pass to protect a
    ///         scheduling hint, and it would still be lost on the one event that matters most
    ///         (a registry that shrank under it).
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the activation normally outlives the interval.</b>
    ///         <c>IExpirySweeperGrain.SweepPeriod</c> is an hour and Orleans' default collection age
    ///         is two, so a group over the cap keeps ticking on the same activation and the rotation
    ///         advances. That is a default rather than a guarantee, which is why the paragraph above
    ///         has to say what losing it costs.
    ///     </para>
    /// </remarks>
    string? resumeFrom;

    /// <inheritdoc />
    public override Task OnActivateAsync(CancellationToken cancellationToken) {
        tenantId = ResourceManagerGrainKeys.TenantOf(this);

        var key = ResourceManagerGrainKeys.Decode(this, GrainKeyKind.ExpirySweeper);
        subscriptionId = key.Id;
        group = key.Name;

        // ⚠ NOTHING IS ARMED HERE, AND THE ABSENCE IS DELIBERATE. An activation is not evidence that
        // this group has anything parked — a sweep, a hand call or the arm itself brings the grain
        // up — so arming on activation would register a reminder for every group anybody ever asks
        // about, including the one whose last entry a purge has just cleared. ResourceGroupGrain
        // does arm on activation, and the difference is what it arms off: its evidence is its own
        // durable state, already loaded, while this grain HOLDS NOTHING — so the same line here
        // would be a registry read in front of every ArmAsync a delete makes, to answer a question
        // ArmIfParkedAsync answers once per group at start-up instead.
        //
        // ⚠ WHAT PUTS A LOST ROW BACK, since "the reminder is durable in Orleans' table" is not an
        // answer to a table restored from a backup or to a park that ran on a silo with no reminder
        // service (2026-09-05, #12 review). Three things: the next ArmAsync in this group, a hand
        // SweepAsync — which arms as well as sweeps — and ExpirySweeperBackfill, which walks every
        // resource group at silo start and calls ArmIfParkedAsync. Before those, a group whose last
        // delete had already happened was never swept at all, which is the state issue #12 exists
        // to end.
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<Result<bool>> ArmAsync() => Result<bool>.Success(await ArmCoreAsync());

    /// <inheritdoc />
    public async Task<Result<bool>> IsArmedAsync() {
        try {
            return Result<bool>.Success(await this.GetReminder(ReminderName) is not null);
        }
        catch (InvalidOperationException) {
            // A silo with no reminder service has no row and never will have one, which is exactly
            // what `false` says to the operator asking. The arm that would have created it has
            // already logged the warning; repeating it on a read would print one line per question.
            return Result<bool>.Success(false);
        }
    }

    /// <inheritdoc />
    public async Task<Result<bool>> ArmIfParkedAsync() {
        var parked = await Parked().ListAsync();

        if (parked.TryGetError(out var listError)) {
            // ⚠ PROPAGATED RATHER THAN READ AS "NOTHING PARKED", which is the same rule SweepAsync's
            // own list failure follows and for the same reason: a registry that could not be read is
            // not a registry with nothing in it, and the caller here is a backfill that reports how
            // many groups it covered.
            return Result<bool>.Failure(listError);
        }

        if (parked.GetValueOrThrow().Count == 0) {
            return Result<bool>.Success(false);
        }

        return Result<bool>.Success(await ArmCoreAsync());
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

        // ⚠ THE ORDER IS THE REGISTRY'S — canonical path, ordinally — AND THE WINDOW OVER IT ROTATES
        // RATHER THAN BEING A FIXED PREFIX (2026-09-05, #12 review). This line was
        // `entries.Take(MaxPerSweep)`, justified with "a resource near the end of the ordering is
        // reached once the ones before it have been purged, which they will be, because a purge
        // removes its entry". Two refusals this file documents as PERSISTENT falsify that premise:
        // EndWindowAsync's CanNotDelete/ReadOnly lock case, which stays parked and is refused on
        // every tick until the lock is lifted, and its withdrawn-provider case, whose entry stays by
        // design. With more than MaxPerSweep entries in one group, enough of either inside the first
        // window meant everything past it was NEVER examined — windows ending with nothing sweeping
        // them, indefinitely, which is the state issue #12 exists to close. Resuming where the last
        // pass stopped and wrapping bounds any entry's wait at ⌈entries ÷ MaxPerSweep⌉ passes
        // regardless of what the entries before it answer.
        //
        // ⚠ THE ORDERING STILL DOES THE WORK, and that is why this is a cursor rather than a shuffle.
        // The window moves by a whole pass at a time over a stable ordering, so an entry is examined
        // once per rotation rather than on a coin toss — and a pass under the cap is the whole
        // registry, which is every group in the tree today.
        var batch = Rotate(entries, out var nextFrom);

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

        // ⚠ ADVANCED ONLY HERE, AFTER THE PASS RAN, so a pass that threw leaves the cursor where it
        // was and the next one re-examines the same window rather than skipping past whatever the
        // failure interrupted.
        resumeFrom = nextFrom;

        var report = new ExpirySweep {
            Examined = batch.Count,
            Purged = purged.ToImmutable(),
            Forgotten = forgotten.ToImmutable(),
            Deferred = entries.Count - batch.Count,
            Disarmed = await ArmOrDisarmAsync(),
            ResumeFrom = nextFrom
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
    ///     Registers the reminder when this group still has something parked, and cancels it when it
    ///     has nothing left.
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
    ///         ⚠ <b>IT ARMS AS WELL AS DISARMS, AND IT USED TO ONLY DISARM (2026-09-05, #12
    ///         review).</b> The arm costs nothing when the row is already there — <see cref="ArmCoreAsync" />
    ///         reads <c>GetReminder</c> first — and it is what makes a hand-driven
    ///         <see cref="SweepAsync" /> a repair rather than a single pass: an operator sweeping a
    ///         group whose row was lost, or was never written because the park ran on a silo with no
    ///         reminder service, leaves it ticking. It also closes the ordinary case of the residual
    ///         described below in one direction, since a tick that finds entries re-asserts the row
    ///         it is running on.
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
    async Task<bool> ArmOrDisarmAsync() {
        var remaining = await Parked().ListAsync();

        if (remaining.IsFailure) {
            // A registry that could not be re-read is not a registry with nothing in it, and the
            // direction of that ignorance is what decides this: leaving the row alone leaves the
            // group swept, while cancelling on a failed read would stand a sweeper down over a
            // storage blip. Nothing is armed either, because there is no evidence to arm off.
            return false;
        }

        if (remaining.GetValueOrThrow().Count > 0) {
            _ = await ArmCoreAsync();
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

    /// <summary>
    ///     Registers the reminder if it is not already registered, and says whether it had to.
    /// </summary>
    /// <returns>
    ///     Whether this call created the row. <see langword="false" /> both when it was already there
    ///     and when this silo has no reminder service — see <c>IExpirySweeperGrain.ArmAsync</c>.
    /// </returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>THE <c>GetReminder</c> GUARD IS THE WHOLE POINT OF THIS METHOD AND THE FIX IT
    ///         CARRIES (2026-09-05, #12 review).</b> <c>RegisterOrUpdateReminder</c> rewrites an
    ///         existing row with <c>StartAt = UtcNow + dueTime</c> and restarts the local timer, so
    ///         calling it unconditionally with a due time of <c>SweepPeriod</c> pushed the next tick
    ///         a full hour out on every arm. Every converged delete arms
    ///         (<c>OperationGrain.ParkAsync</c>) and every refused restore arms
    ///         (<c>ResourceManagerService.RepairParkedRegistryAsync</c>), so a resource group with a
    ///         soft delete more often than hourly never swept once — the exact failure the grain
    ///         exists to prevent, produced by the grain's own arming. The guard is the idiom
    ///         <see cref="ArmOrDisarmAsync" /> already used two hundred lines below to cancel.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The race the guard leaves is in the harmless direction.</b> Two arms interleaving
    ///         can both see <see langword="null" /> and both register; the second write is the same
    ///         row with a due time a few milliseconds later, which is a rounding error against an
    ///         hour rather than the unbounded deferral above. Nothing here can make a tick happen
    ///         <i>earlier</i> than the row says, and a sweep cannot purge early whatever it is
    ///         handed — <c>IResourceIndexGrain.ResolveExpiredAsync</c> decides that.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>SUCCESS ON A SILO WITH NO REMINDER SERVICE, AND THE CALLER IS THE REASON.</b>
    ///         Both writers are on a path that is parking a resource, and a park that failed because
    ///         the platform could not arrange to purge the resource in seven days' time would leave
    ///         the delete stuck for ever over a schedule. The same call, the same refusal and the
    ///         same decision as <c>ResourceGroupGrain.ArmOrDisarmAsync</c>'s, whose remarks put it as
    ///         "turning an absent cleanup into an absent platform". What is lost is exactly what
    ///         master loses today: nothing ends this group's windows on the clock's account.
    ///         <see cref="SweepAsync" /> is still callable and <c>POST …/purge</c> still works.
    ///     </para>
    /// </remarks>
    async Task<bool> ArmCoreAsync() {
        try {
            if (await this.GetReminder(ReminderName) is not null) {
                return false;
            }

            _ = await this.RegisterOrUpdateReminder(
                ReminderName,
                IExpirySweeperGrain.SweepPeriod,
                IExpirySweeperGrain.SweepPeriod
            );

            return true;
        }
        catch (InvalidOperationException error) {
            logger.LogWarning(
                "Resource group '{Group}' in subscription {Subscription} could not arm its expired-"
                + "window sweeper because this silo has no reminder service: {Reason} Recovery "
                + "windows in this group will not be ended automatically here; "
                + "IExpirySweeperGrain.SweepAsync still works and an authorized purge is unaffected.",
                group,
                subscriptionId,
                error.Message
            );

            return false;
        }
    }

    /// <summary>
    ///     The window of entries this pass looks at, and where the next pass starts.
    /// </summary>
    /// <param name="entries">The registry's whole listing, in its own canonical-path ordering.</param>
    /// <param name="nextFrom">
    ///     The canonical path the next pass begins at, or <see langword="null" /> when this pass took
    ///     the whole registry.
    /// </param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Resumed <i>inclusively</i> from a path rather than from an index, because the
    ///         registry moves between passes.</b> An index would name a different entry after a purge
    ///         removed something earlier in the ordering; a path names the same entry, or — when that
    ///         entry has gone — the next one after it, which is where the rotation should carry on
    ///         anyway. A cursor that has fallen off the end of a shrunken registry restarts at the
    ///         head, which is the only other place it could sensibly go.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The wrap is what bounds the wait, and it is the half a plain "skip forward" would
    ///         not have.</b> Without it a cursor past the last entry would sweep a short tail and
    ///         then start again, so the entries just before the cursor would be examined half as
    ///         often as the rest. Taking <c>MaxPerSweep</c> entries from the cursor <i>around</i> the
    ///         ordering makes every entry's turn come exactly once per rotation.
    ///     </para>
    /// </remarks>
    List<ParkedResource> Rotate(IReadOnlyList<ParkedResource> entries, out string? nextFrom) {
        nextFrom = null;

        if (entries.Count == 0) {
            return [];
        }

        var start = 0;

        if (resumeFrom is { } cursor) {
            for (var i = 0; i < entries.Count; i++) {
                if (string.CompareOrdinal(entries[i].AddressOf().CanonicalPath, cursor) >= 0) {
                    start = i;
                    break;
                }
            }
        }

        var take = Math.Min(IExpirySweeperGrain.MaxPerSweep, entries.Count);
        var batch = new List<ParkedResource>(take);

        for (var i = 0; i < take; i++) {
            batch.Add(entries[(start + i) % entries.Count]);
        }

        if (take < entries.Count) {
            nextFrom = entries[(start + take) % entries.Count].AddressOf().CanonicalPath;
        }

        return batch;
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
