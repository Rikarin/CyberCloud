using Orleans.Concurrency;
using System.Collections.Immutable;

namespace CyberCloud.ResourceManager.Contracts;

/// <summary>
///     What one pass of <see cref="IExpirySweeperGrain.SweepAsync" /> did.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Paths and counts, and deliberately no deadlines.</b> Nothing here says when a window
///         ends or how much of one is left. The sweep never reads
///         <c>IndexEntry.RecoverableUntil</c> — it asks <c>IResourceManager.PurgeExpiredAsync</c>,
///         which asks the grain that stamped the window — so it has no deadline to report and a
///         field for one would be a second opinion assembled out of nothing.
///         <c>IResourceIndexGrain.ResolveExpiredAsync</c>'s own remarks make the sharper version of
///         this point: the refusals it hands a mechanism are deliberately identical, so that "a
///         mechanism whose retries encode how much window is left" cannot exist.
///     </para>
///     <para>
///         ⚠ <b><see cref="Purged" /> is a list of purges <i>accepted</i> and not of purges
///         finished.</b> A purge is a long-running operation like every other write on this
///         platform: <c>PurgeCoreAsync</c> releases the index and starts an operation, and the data
///         plane comes down on that operation's own reminder. So an entry in this list means the
///         name is free and the quota is on its way back, not that the teardown has converged.
///     </para>
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.ResourceManager.ExpirySweep")]
public sealed record ExpirySweep {
    /// <summary>How many registry entries this pass looked at.</summary>
    /// <remarks>
    ///     ⚠ Bounded by <see cref="IExpirySweeperGrain.MaxPerSweep" />, so it is not the size of the
    ///     registry — <see cref="Deferred" /> is what the registry held beyond it.
    /// </remarks>
    [Id(0)]
    public int Examined { get; init; }

    /// <summary>The canonical paths whose purge this pass accepted, in the order it drove them.</summary>
    [Id(1)]
    public ImmutableArray<string> Purged { get; init; } = [];

    /// <summary>
    ///     The canonical paths whose registry entry this pass removed because the index no longer
    ///     agrees with it.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This is the reconcile half, and it is the reason the sweep asks the index before it
    ///     asks the manager.</b> The registry's invariant is <i>an entry exists only while the index
    ///     says <see cref="IndexEntryState.SoftDeleted" /></i>, and
    ///     <c>ResourceManagerService.RepairParkedRegistryAsync</c>'s "NARROWED, NOT CLOSED" remarks
    ///     describe the one interleaving that can break it in the long direction. Before this sweeper
    ///     existed such an entry was permanent, because nothing could address the resource again; now
    ///     it survives at most one sweep period.
    /// </remarks>
    [Id(2)]
    public ImmutableArray<string> Forgotten { get; init; } = [];

    /// <summary>
    ///     How many entries the registry held beyond <see cref="IExpirySweeperGrain.MaxPerSweep" />,
    ///     which the next pass takes.
    /// </summary>
    [Id(3)]
    public int Deferred { get; init; }

    /// <summary>
    ///     Whether this pass found nothing left parked and cancelled its own reminder.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Reported because a reminder is otherwise unobservable from outside the grain, and
    ///         "armed exactly while there is something to sweep" is a claim rather than a comment
    ///         only if something can read it.</b> Orleans exposes <c>GetReminder</c> to the grain and
    ///         to nobody else, so a test — or an operator looking at a log line — has no other way to
    ///         tell a sweeper that stood down from one that is still ticking over an empty registry
    ///         every hour for ever. It is the decision and not the outcome: on a silo with no
    ///         reminder service there was never a row to cancel, and this still says the pass found
    ///         nothing. <c>IExpirySweeperGrain.IsArmedAsync</c> answers the other half — what the row
    ///         actually is — and was added because this field alone could not.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It is one half of a decision that goes both ways.</b> A pass that ends with the
    ///         registry <i>not</i> empty arms rather than merely refraining from disarming, which is
    ///         <c>ResourceGroupGrain.ArmOrDisarmAsync</c>'s shape and is what makes a hand-driven
    ///         <c>SweepAsync</c> — the remedy this interface offers an operator whose group was
    ///         parked on a silo with no reminder service — leave the group ticking afterwards
    ///         instead of swept once and abandoned.
    ///     </para>
    /// </remarks>
    [Id(4)]
    public bool Disarmed { get; init; }

    /// <summary>
    ///     The canonical path the next pass starts at, or <see langword="null" /> when the next pass
    ///     starts at the beginning of the registry's ordering.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The rotating cursor <see cref="IExpirySweeperGrain.MaxPerSweep" /> describes, and it
    ///     is reported for the reason <see cref="Disarmed" /> is: it is a decision taken inside the
    ///     grain that changes what the <i>next</i> call will do, so an operator staring at a group
    ///     that is over the cap can see the window moving rather than infer it.</b> It is the first
    ///     entry <b>after</b> the last one this pass examined, so it is <see langword="null" />
    ///     exactly when the pass got all the way round — and a group under the cap therefore always
    ///     reports <see langword="null" />.
    /// </remarks>
    [Id(5)]
    public string? ResumeFrom { get; init; }

    /// <summary>
    ///     The entries this pass left exactly as it found them — the ordinary answer for a window
    ///     that is still running, and also what a lock or any other refusal produces.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Derived rather than stored, so it cannot disagree with the two lists.</b> It is also
    ///     why the sweep does not report <i>why</i> each was kept: the refusals it can see are
    ///     deliberately the same sentence (see the remarks on the type), so a breakdown would be a
    ///     breakdown of one bucket.
    /// </remarks>
    public int Kept => Examined - Purged.Length - Forgotten.Length;
}

/// <summary>
///     The clock behind <c>IResourceManager.PurgeExpiredAsync</c>: one activation per resource
///     group, holding a reminder while that group has anything parked, ending the windows that have
///     ended.
/// </summary>
/// <remarks>
///     <para>
///         <b>Kind</b> Entity · <b>Tier</b> <i>none</i> — this grain has no persistent state ·
///         <b>Key</b> <c>sweep/{subscriptionId:N}/rg/{name}</c>, tenant-qualified. Build it with
///         <c>GrainKeys.ExpirySweeper</c>.
///     </para>
///     <para>
///         ⚠ <b>THIS IS THE ITEM docs/plan/07 § Azure RBAC LEFT OWED, AND ONLY THE CALLER OF IT.</b>
///         That section decided the fork — <i>"the purge splits, and there is no system
///         principal"</i> — and built both fronts; what it recorded as still missing was <i>"the
///         caller of the mechanism … Nothing yet drives <c>PurgeExpiredAsync</c> on a clock; it is a
///         method with two tests and no scheduler."</i> This is that caller and it is nothing else:
///         it takes no decision the two fronts do not already take, and in particular it does not
///         decide whether a window has ended.
///     </para>
///     <para>
///         ⚠ <b>THE RECORDED SHAPE WAS A REMINDER PER PARKED RESOURCE, AND IT IS RE-TAKEN HERE
///         RATHER THAN FOLLOWED — because the premise it rested on stopped being true two commits
///         before this one.</b> docs/plan/07 § Azure RBAC and docs/plan/08 § Soft delete both say
///         the same thing in the same words: a reminder <i>"registered when the resource is parked
///         and firing at its deadline, rather than a scan — there is no enumeration of parked
///         resources anywhere … so a sweeper that searched would need an index that does not exist,
///         while a reminder needs only the moment the window is opened."</i> Issue #71 built that
///         index: <see cref="IParkedResourceRegistryGrain" /> is a per-resource-group registry of
///         exactly the resources a window is running on. The conclusion was correct for its premise
///         and the premise is gone, so this is a scan. Four reasons, in the order they mattered:
///     </para>
///     <list type="bullet">
///         <item>
///             ⚠ <b>A reminder that fires at the deadline is a second durable copy of the
///             deadline.</b> Its due time <i>is</i> <c>IndexEntry.RecoverableUntil</c>, written into
///             the reminder table by a different writer than the one that stamped the window and
///             read back by the reminder service's clock. docs/plan/07 § Azure RBAC's own argument
///             against a caller-side comparison — <c>SoftDeleteAsync</c> takes a duration <i>"so
///             that one activation stamps the window and reads it"</i> — is an argument against
///             that copy too. This grain's reminder carries no deadline at all: it says
///             <i>look</i>, and <c>ResolveExpiredAsync</c> says <i>whether</i>. Firing early is then
///             a refusal rather than a destruction.
///         </item>
///         <item>
///             ⚠ <b>A per-resource reminder has no repair path, and this one does.</b> If the
///             registration is lost — a silo with no reminder service at the moment of the park, a
///             crash between the two writes, a reminder table restored from a backup — a
///             per-resource reminder is the <i>only</i> record that a window needs driving, and the
///             resource is left exactly where master leaves it today: holding its name and its
///             committed quota forever. The candidate set here is re-derived from the registry on
///             every tick, and the registry has a stated invariant and a repair
///             (<c>ResourceManagerService.RepairParkedRegistryAsync</c>).
///             <para>
///                 ⚠ <b>THIS REASON WAS STATED TOO WIDELY AND THE NARROWING IS OWED TO IT
///                 (2026-09-05, #12 review).</b> A lost <i>group-level</i> row is not repaired by
///                 re-deriving the candidate set, because the re-derivation only happens on a tick
///                 that the lost row is what would have produced. The asymmetry is real and it runs
///                 the wrong way for this design: a per-resource reminder that is lost costs one
///                 resource's window, and a group-level row that is lost costs <i>every</i> window in
///                 that resource group. What makes the reason hold as narrowed is that the row is
///                 re-derivable from a durable record that this design has and the recorded one did
///                 not — <see cref="IParkedResourceRegistryGrain" /> — so three things put it back:
///                 the next <see cref="ArmAsync" /> in the group, a hand
///                 <see cref="SweepAsync" /> (which arms as well as sweeps), and
///                 <see cref="ArmIfParkedAsync" />, which <c>ExpirySweeperBackfill</c> drives over
///                 every resource group at silo start. A per-resource reminder has no equivalent,
///                 because there is no durable record anywhere of <i>which deadline</i> was lost.
///             </para>
///         </item>
///         <item>
///             ⚠ <b>The scan reconciles what the reminder could not.</b> Asking the index per entry
///             is what lets a sweep <i>remove</i> an entry the index no longer agrees with — see
///             <see cref="ExpirySweep.Forgotten" />. A reminder registered per resource has nothing
///             to reconcile against and would leave that entry standing for ever.
///         </item>
///         <item>
///             Cost, which is the weakest of the four and still points the same way: one reminder
///             row per resource group that currently has something parked, and none at all in the
///             steady state where nothing does, against one row per parked resource for the whole
///             length of its window.
///         </item>
///     </list>
///     <para>
///         ⚠ <b>A GRAIN OF ITS OWN AND NOT A REMINDER ON THE REGISTRY, WHICH IS A CYCLE RATHER THAN
///         A PREFERENCE.</b> A sweep calls <c>PurgeExpiredAsync</c>, and
///         <c>ResourceManagerService.PurgeCoreAsync</c> calls
///         <see cref="IParkedResourceRegistryGrain.UnparkAsync" /> — so a reminder that fired on the
///         registry grain would be an activation awaiting a call back into itself, which Orleans
///         queues behind the turn that is waiting for it. Every grain a purge touches is closed to
///         the driver for that reason. And even without the cycle the placement would be wrong: one
///         turn at a time means a sweep in progress delays every park and unpark in that group,
///         which is precisely the three choreographies <see cref="IParkedResourceRegistryGrain" />
///         is written to stay out of the way of. See <c>GrainKeys.ExpirySweeper</c>.
///     </para>
///     <para>
///         ⚠ <b>IT AUTHORIZES NOTHING AND IT IS NOT A SUBJECT.</b> docs/plan/07 § Azure RBAC
///         declined a system principal because <i>"a system principal is a subject that passes every
///         check, and this document has no way to bound one"</i>. Nothing changes here: the sweep
///         builds an <see cref="ExpiredPurgeRequest" />, whose entire content is the absence of a
///         <c>CallerContext</c>, and the request path it reaches names no subject — so a
///         <c>Check</c> reached from it would find no tuple and deny. What the sweeper adds to the
///         platform is a clock, not an identity.
///     </para>
///     <para>
///         ⚠ <b>What it does inherit is every refusal the authorized front inherits, and the lock is
///         the one worth naming.</b> A <c>CanNotDelete</c> lock stops a clock-driven purge exactly as
///         it stops a typed one, so a locked resource whose window has ended stays parked and this
///         grain will retry it, refused, on every tick until the lock is lifted or the resource is
///         restored. docs/plan/07 § Azure RBAC: <i>"a clock that overruled it would make the lock
///         mean 'until the platform disagrees'"</i>.
///     </para>
///     <para>
///         ⚠ <b>WHAT THIS MAKES ROUTINE, SAID HERE BECAUSE IT IS THE COST OF BUILDING IT.</b> Purges
///         of the five types that declare a window stop being something a person types and start
///         happening on a schedule, and two consequences already recorded elsewhere become ordinary
///         rather than rare. docs/plan/08 § Soft delete: of those five, the claims of
///         <c>CyberCloud.DBforMySQL/servers</c> and <c>CyberCloud.Storage/accounts</c> belong to
///         operators this repository cannot name, so <i>"their purges still leave their disks"</i> —
///         after this, on a timetable. And issue #69 (open, blocker) finds
///         <c>CyberCloud.DBforPostgreSQL/servers</c>' window hollow, because CloudNativePG owns its
///         claims and Kubernetes garbage-collects them at the delete: the sweeper does not destroy
///         anything #69 has not already destroyed, but it does release the name and return the quota
///         seven days later with nobody in the loop, where today an operator would at least have had
///         to type the purge. ⚠ <b>Neither is an argument for holding every window open for
///         ever</b> — that trades a disk for a name and a quota held permanently — and neither is
///         this grain's to fix.
///     </para>
/// </remarks>
[Alias("CyberCloud.ResourceManager.IExpirySweeperGrain")]
public interface IExpirySweeperGrain : IGrainWithStringKey {
    /// <summary>
    ///     How long a sweep waits between passes, and therefore the longest a window can outlive its
    ///     own deadline.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>One hour, and the number is the overrun rather than the frequency.</b> A window
    ///         is declared in whole days (<c>ResourceTypeRegistration.SoftDeleteDays</c>), so what
    ///         this buys is measured against a day at the smallest. Counted on 2026-09-05 over every
    ///         shipping declaration — the five <c>SupportsSoftDelete</c> calls in <c>src/Providers</c>
    ///         (<c>DBforPostgreSQL/servers</c>, <c>DBforMySQL/servers</c>, <c>Storage/accounts</c>,
    ///         <c>Monitor/workspaces</c>, <c>ContainerRegistry/registries</c>), each declaring
    ///         <b>7</b> — an hour is at most <b>1/168</b>, or 0.6%, of any window the platform serves
    ///         today, and at most <b>1/24</b> of the shortest one the type system can express. It
    ///         goes stale if a type declares a window shorter than a day, which the <c>int</c> of
    ///         days cannot currently express.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It is a floor on lateness and never on earliness.</b> Being late costs a name and
    ///         a committed quota held a little longer; being early would destroy a resource somebody
    ///         could still have restored, and nothing here can be early, because the tick does not
    ///         decide — <c>IResourceIndexGrain.ResolveExpiredAsync</c> does, on the clock that
    ///         stamped the window. That asymmetry is why an hour is affordable and a minute would buy
    ///         nothing worth its ticks. Orleans' reminder floor is one minute either way.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>THE SENTENCE ABOVE IS ONLY TRUE BECAUSE <see cref="ArmAsync" /> REGISTERS AT MOST
    ///         ONCE, AND IT WAS FALSE WHEN THIS FILE FIRST SHIPPED (fixed 2026-09-05, #12 review).</b>
    ///         Orleans' <c>RegisterOrUpdateReminder(name, dueTime, period)</c> does not leave an
    ///         existing row alone: it rewrites it with <c>StartAt = UtcNow + dueTime</c> and restarts
    ///         the local timer. This grain passes <see cref="SweepPeriod" /> as the due time, so an
    ///         arm on every converged delete pushed the next tick a whole hour out — and a resource
    ///         group with a soft delete more often than hourly never swept at all, which is the exact
    ///         state #12 exists to close. The arm is now guarded on <c>GetReminder(…) is null</c>, the
    ///         idiom this file's own disarm already used, so the row's <c>StartAt</c> is written once
    ///         and the period below is what separates two ticks.
    ///     </para>
    /// </remarks>
    static TimeSpan SweepPeriod { get; } = TimeSpan.FromHours(1);

    /// <summary>
    ///     The most entries one pass will look at. The rest wait for the next tick, as
    ///     <see cref="ExpirySweep.Deferred" />.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A cap because a pass holds this activation, and <c>ListRequest.MaxPageSize</c>
    ///         because the platform has already argued that number.</b> One entry costs an index read
    ///         plus a whole purge choreography, and a group whose registry holds hundreds would keep
    ///         this activation busy for as long as that takes. <c>ListRequest.MaxPageSize</c> is the
    ///         tree's existing answer to "how much work is one pass worth", chosen for a listing
    ///         rather than for this, and reusing it beats inventing a second number that would drift
    ///         away from it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>THE CAP TAKES A ROTATING WINDOW AND NOT A FIXED PREFIX, WHICH IS A CORRECTNESS
    ///         FIX RATHER THAN A REFINEMENT (2026-09-05, #12 review).</b> This remark used to say the
    ///         entries beyond the cap were reached "once the ones before them have been purged, which
    ///         they will be, because a purge removes its entry". That premise is contradicted by two
    ///         refusals this very interface documents as <i>persistent</i>: a <c>CanNotDelete</c> or
    ///         <c>ReadOnly</c> lock, which "stays parked and this grain will retry it, refused, on
    ///         every tick until the lock is lifted", and a type this host no longer serves, whose
    ///         entry "stays" by design. With more than <see cref="MaxPerSweep" /> entries in one
    ///         group, enough permanently-refused ones inside the first window meant entries beyond it
    ///         were <b>never examined</b> — their windows ended and nothing swept them, for ever. So
    ///         a pass now resumes after the last entry the previous pass looked at and wraps around
    ///         the registry's ordering, which bounds the wait for any entry at
    ///         ⌈registry ÷ <see cref="MaxPerSweep" />⌉ passes no matter what the others answer. The
    ///         cursor is <see cref="ExpirySweep.ResumeFrom" />; it is in memory rather than durable,
    ///         which <c>ExpirySweeperGrain</c>'s own remarks account for.
    ///     </para>
    /// </remarks>
    static int MaxPerSweep => ListRequest.MaxPageSize;

    /// <summary>
    ///     Registers this group's sweep reminder, if it is not already registered.
    /// </summary>
    /// <returns>
    ///     Whether this call created the row — <see langword="false" /> when the group was already
    ///     armed <i>and</i> when this silo has no reminder service at all. Never a failure; see the
    ///     remarks.
    /// </returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Called by every writer that <i>adds</i> a registry entry, which is what makes
    ///         "armed while there is something to sweep" true rather than aspirational.</b> There are
    ///         two — <c>OperationGrain.ParkAsync</c>, where a window opens, and
    ///         <c>ResourceManagerService.RepairParkedRegistryAsync</c>, which puts back an entry a
    ///         refused restore cleared. A third writer would have to come here too, and the symmetry
    ///         is deliberate: <see cref="SweepAsync" /> disarms when it finds the registry empty, so
    ///         an entry that appeared without arming would sit unswept until the group's next park.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>REGISTERED AT MOST ONCE, AND THIS PARAGRAPH USED TO SAY THE OPPOSITE WAS
    ///         HARMLESS (2026-09-05, #12 review).</b> It read <i>"Orleans'
    ///         <c>RegisterOrUpdateReminder</c> is itself idempotent on (grain, name), so a second call
    ///         re-registers the same row rather than adding one"</i>. Idempotent on the row's
    ///         <i>identity</i> it is; idempotent in effect it is not. The call rewrites the row with
    ///         <c>StartAt = UtcNow + dueTime</c> and restarts the local timer, and this grain's due
    ///         time is <see cref="SweepPeriod" /> — so every arm pushed the next tick a full hour
    ///         out, and a resource group whose deletes arrive more often than hourly <b>never swept
    ///         at all</b>. The implementation now registers only when
    ///         <c>GetReminder(ReminderName)</c> answers <see langword="null" />, which is the idiom
    ///         the disarm in the same grain was already using. The <see cref="bool" /> this returns
    ///         is what makes "at most once" assertable from outside a grain, since a reminder row is
    ///         not otherwise observable — see <see cref="IsArmedAsync" />.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><see cref="AlwaysInterleaveAttribute" />, because both callers are on a request
    ///         path and a sweep is deliberately long.</b> <see cref="MaxPerSweep" /> purge
    ///         choreographies run in one turn of a non-reentrant activation, and the tree configures
    ///         no <c>ResponseTimeout</c>, so Orleans' 30-second default applies: an arm that queued
    ///         behind a sweep in progress could time out and <i>throw</i> into
    ///         <c>OperationGrain.ParkAsync</c> or into a tenant's restore. Interleaving is safe here
    ///         in a way <c>[Reentrant]</c> on the whole grain would not be — this method touches no
    ///         state a sweep touches; the reminder row is Orleans' and the three key fields are
    ///         written once at activation and never again.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A silo with no reminder service succeeds anyway, and that is the same decision
    ///         <c>ResourceGroupGrain.ArmOrDisarmAsync</c> takes for the two-phase-create reaper.</b>
    ///         <c>RegisterOrUpdateReminder</c> throws there, and letting that escape would fail the
    ///         <i>delete</i> that was parking the resource — turning an absent sweeper into an absent
    ///         platform. What is lost instead is only what master already loses: nothing ends that
    ///         window on the clock's account. <see cref="SweepAsync" /> is still callable by hand,
    ///         and an ordinary <c>POST …/purge</c> still works. That is also why this returns no
    ///         failure at all: a caller that cannot act on the answer should not be handed a refusal
    ///         to mis-handle — <c>OperationGrain</c> carried a guard on one for a while and it was
    ///         dead code.
    ///     </para>
    /// </remarks>
    [AlwaysInterleave]
    Task<Result<bool>> ArmAsync();

    /// <summary>
    ///     Whether this group's sweep reminder is registered right now.
    /// </summary>
    /// <returns>
    ///     Whether a row exists. <see langword="false" /> on a silo with no reminder service, which
    ///     is the truth from that silo's point of view: nothing is going to tick here.
    /// </returns>
    /// <remarks>
    ///     ⚠ <b>This exists because <see cref="ExpirySweep.Disarmed" />'s remarks complain that it
    ///     should.</b> They say a reminder "is otherwise unobservable from outside the grain", and
    ///     that "a test — or an operator looking at a log line — has no other way to tell a sweeper
    ///     that stood down from one that is still ticking over an empty registry every hour for
    ///     ever". <c>GetReminder</c> is available to the grain, so the grain can answer it; the only
    ///     reason it did not was that nothing asked. It answers the operator's question — <i>is
    ///     anything actually going to end this group's windows?</i> — and it is what lets
    ///     <see cref="ArmAsync" />'s at-most-once rule and <see cref="ArmIfParkedAsync" />'s backfill
    ///     be asserted rather than described.
    /// </remarks>
    [AlwaysInterleave]
    Task<Result<bool>> IsArmedAsync();

    /// <summary>
    ///     Arms this group's sweeper if — and only if — the group has something parked.
    /// </summary>
    /// <returns>Whether this call armed it, or the registry's failure if it could not be read.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>THE BACKFILL, AND IT EXISTS BECAUSE ARMING FROM THE WRITE PATH ALONE COVERS ONLY
    ///         THE WINDOWS THAT OPEN AFTER IT IS DEPLOYED (2026-09-05, #12 review).</b>
    ///         <see cref="ArmAsync" />'s two callers both sit on a path that has just <i>added</i> a
    ///         registry entry, so on the deploy that first ships this grain every resource already
    ///         inside a window has no sweeper — and a group whose last delete has already happened
    ///         never gets one, because nothing in the tree would ever call. That is the objection the
    ///         design argument on this interface makes to a per-resource reminder — <i>"if the
    ///         registration is lost … the resource is left exactly where master leaves it today"</i> —
    ///         read at group granularity, where it is worse rather than better: a lost row costs a
    ///         whole resource group's windows rather than one resource's.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It is what <c>ExpirySweeperBackfill</c> calls, once per resource group, and
    ///         nothing else may.</b> The condition is the whole point: arming unconditionally would
    ///         put a reminder row behind every resource group that has ever existed, which is exactly
    ///         the standing cost <see cref="ExpirySweep.Disarmed" /> exists to avoid. So the answer
    ///         comes from the registry — one read, no purge, no index call — and a group with nothing
    ///         parked ends the call having written nothing anywhere.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The registry's failure is propagated here and not swallowed, unlike
    ///         <see cref="ArmAsync" />'s.</b> "Could not read the registry" is not "the group has
    ///         nothing parked", and the caller is a backfill that reports how many groups it covered:
    ///         a shard that was unreachable must not be counted as a group that needed no sweeper.
    ///     </para>
    ///     <para>
    ///         ⚠ <see cref="AlwaysInterleaveAttribute" /> for <see cref="ArmAsync" />'s reason and one
    ///         more: the backfill walks the platform at start-up, and a walk that blocked behind a
    ///         sweep in progress would take a response timeout per busy group.
    ///     </para>
    /// </remarks>
    [AlwaysInterleave]
    Task<Result<bool>> ArmIfParkedAsync();

    /// <summary>
    ///     One pass: reconcile this group's parked-resource registry against the index, and hand
    ///     every entry that is still true to <c>IResourceManager.PurgeExpiredAsync</c>.
    /// </summary>
    /// <returns>What the pass did, or the registry's own failure if it could not be listed.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Public rather than only reachable from the reminder, for the reason
    ///         <c>IResourceGroupGrain.ReapOrphansAsync</c> is:</b> a sweeper nobody can run is a
    ///         sweeper nobody can test without waiting an hour, and an operator holding a resource
    ///         group that was parked on a silo with no reminder service needs a way to end its
    ///         windows that is not "wait".
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It takes no argument, and in particular it does not take a time.</b> Every other
    ///         sweep in this tree takes one — <c>ReapOrphansAsync(TimeSpan olderThan)</c> — because
    ///         age is the thing it is judging. This one judges nothing: the deadline belongs to
    ///         <c>IResourceIndexGrain</c> and is read by the front this drives. A parameter here
    ///         would be a knob that could make a purge early.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It leaves the group armed if it found anything, and stands down only if it did
    ///         not.</b> That is the same arm-or-disarm decision
    ///         <c>ResourceGroupGrain.ArmOrDisarmAsync</c> takes, and it is what makes the hand call
    ///         above worth offering: an operator who sweeps a group whose reminder was never
    ///         registered — the silo with no reminder service, a reminder table restored from a
    ///         backup — gets the row put back by the same call, rather than one pass and the same
    ///         silence afterwards.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Safe to run twice and safe to interleave with an ordinary purge or restore.</b>
    ///         Every step it takes is one another caller could have taken a moment earlier: a purge
    ///         of a resource that has just been restored is refused by <c>RestorableAsync</c>, a
    ///         purge of one already purged finds no binding, and the unpark of a stale entry is
    ///         idempotent.
    ///     </para>
    /// </remarks>
    Task<Result<ExpirySweep>> SweepAsync();

    /// <summary>Drops this activation — see <c>ITenantGrain.DeactivateAsync</c>.</summary>
    Task DeactivateAsync();
}
