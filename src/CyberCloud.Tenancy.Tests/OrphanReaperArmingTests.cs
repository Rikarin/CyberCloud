using CyberCloud.Core.Resources;
using CyberCloud.Tenancy.Contracts;
using CyberCloud.Tenancy.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace CyberCloud.Tenancy.Tests;

/// <summary>
///     When <c>ResourceGroupGrain</c>'s two-phase-create reaper is armed, and — the point of this
///     file — what a second arm does to the row that is already there. Issue #83.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THE DEFECT WAS AN ARM THAT DEFERRED ITS OWN TICK.</b>
///         <c>RegisterOrUpdateReminder(name, dueTime, period)</c> is idempotent on the row's
///         <i>identity</i> and not in its <i>effect</i>: it rewrites the row with
///         <c>StartAt = UtcNow + dueTime</c> and restarts the local timer.
///         <c>ArmOrDisarmAsync</c> passed <c>OrphanSweepPeriod</c> — fifteen minutes — as the due
///         time and called it unconditionally, and it sits on the write path of every
///         <c>BeginCreateAsync</c> and every <c>CompleteCreateAsync</c>. A resource group creating
///         resources more often than once every fifteen minutes therefore pushed its own reaper tick
///         out on every call and never reached one: the reaper was armed, <c>GetReminder</c> answered
///         a row, and nothing swept. The same defect as <c>ExpirySweeperGrain</c>'s, found while
///         fixing that one for #12 and filed separately because the consequence differs.
///     </para>
///     <para>
///         ⚠ <b>THE REMINDER ROW IS READ DIRECTLY, AND THAT IS A STRONGER WITNESS THAN #12 COULD
///         GET.</b> <c>ExpirySweeperTests.ASecondArmDoesNotRewriteTheRowThatIsAlreadyThere</c>
///         asserts a <see langword="bool" /> the grain returns — "I did not register" — because that
///         file's own remarks record "the reminder itself is not observable from a test". That is not
///         true of a silo whose reminder service this suite wires up itself
///         (<c>TenancyCluster.StartSiloAsync</c> calls <c>UseInMemoryReminderService</c>):
///         <c>Orleans.IReminderTable</c> is public, it is a singleton in the silo's container, and
///         <c>ReadRow(grainId, name)</c> hands back the <c>ReminderEntry</c> with its <c>StartAt</c>,
///         its <c>Period</c> and its <c>ETag</c>. So what is asserted below is the schedule itself —
///         the tick did not move — rather than a proxy for it, and no member had to be added to
///         <see cref="IResourceGroupGrain" /> to make the fix assertable.
///     </para>
///     <para>
///         ⚠ <b>Every case gets its own tenant, subscription and group.</b> The reminder row is keyed
///         by <c>(GrainId, name)</c> and there is one per group, so two cases sharing a group would
///         have the first one's arm answer the second one's question.
///     </para>
/// </remarks>
[Collection(TenancySuite.Name)]
public sealed class OrphanReaperArmingTests(TenancyCluster cluster) {
    /// <summary>The reminder table the silo under test is really registering rows in.</summary>
    IReminderTable Reminders => cluster.Services.GetRequiredService<IReminderTable>();

    /// <summary>
    ///     ⚠ <b>A second create does not rewrite the reaper row, because rewriting it would push the
    ///     next tick a whole <c>OrphanSweepPeriod</c> out.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Issue #83 as a case. The two halves that make it mean something are both negative:
    ///         <c>StartAt</c> is unchanged after a second writer of <c>CreatingSince</c> comes
    ///         through — which is the deferral itself, not a stand-in for it — and the row is still
    ///         <i>there</i> afterwards, which is what stops a "fix" that satisfied the first half by
    ///         never arming at all.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The pause is what makes <c>StartAt</c> a witness rather than a coincidence.</b>
    ///         <c>StartAt</c> is <c>DateTime.UtcNow + dueTime</c> at the moment of the write, and
    ///         <c>DateTime.UtcNow</c> on Windows advances in steps of about 15.6 ms — so two grain
    ///         calls close enough together could produce the <i>same</i> <c>StartAt</c> even from the
    ///         unguarded arm, and the assertion would hold against the very code it exists to
    ///         reject. Sleeping well past that granularity first means a rewrite has to move the
    ///         value. The <c>ETag</c> assertion carries no such caveat and is the sharper of the two:
    ///         the table stamps a fresh one on every upsert, so it changes even if the clock has
    ///         not.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The disarm is driven at the end of the same case on purpose.</b> The guard is on
    ///         the arm, and a guard written into the wrong branch would leave a group that has
    ///         nothing in <c>Creating</c> still holding a reminder that wakes it every fifteen
    ///         minutes for ever — which is exactly the standing-row-per-group cost
    ///         <c>ArmOrDisarmAsync</c> exists to avoid.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task ASecondCreateDoesNotRewriteTheReaperRowThatIsAlreadyThere() {
        var first = await GroupAndAddress(Tenant(1), "armed-once", "web-01");
        var second = first with { Name = "web-02", Id = Guid.NewGuid() };
        var group = Group(first);

        (await Row(group)).ShouldBeNull(
            "nothing in this group is Creating, so nothing has armed the reaper"
        );

        (await group.BeginCreateAsync(first)).IsSuccess.ShouldBeTrue();

        var armed = await Row(group);
        armed.ShouldNotBeNull("the first create is what registers the row");
        armed!.Period.ShouldBe(ResourceGroupGrain.OrphanSweepPeriod);

        await PastTheClockGranularity();

        (await group.BeginCreateAsync(second)).IsSuccess.ShouldBeTrue();

        var afterSecondCreate = await Row(group);

        afterSecondCreate.ShouldNotBeNull(
            "answering 'I did not register' must not mean 'and there is no row'"
        );

        afterSecondCreate!.ETag.ShouldBe(
            armed.ETag,
            "a second create must leave the existing row alone: the table stamps a new ETag on "
            + "every upsert, so a changed one is RegisterOrUpdateReminder having been called again "
            + "— #83"
        );

        afterSecondCreate.StartAt.ShouldBe(
            armed.StartAt,
            "and this is the consequence the ETag stands for: a rewrite sets StartAt to "
            + "UtcNow + OrphanSweepPeriod, so a group creating resources more often than every "
            + "fifteen minutes would never reach a tick at all"
        );

        // ⚠ The OTHER writer of CreatingSince. A completion removes one entry and leaves the other,
        // so it comes through the arming branch too — and it must not rewrite the row either, or a
        // group with a steady stream of creates and completions defers its tick twice as often.
        (await group.CompleteCreateAsync(first.Id, ProvisioningState.Succeeded)).IsSuccess.ShouldBeTrue();

        var afterCompletion = await Row(group);
        afterCompletion.ShouldNotBeNull("web-02 is still Creating, so the reaper still has work");
        afterCompletion!.ETag.ShouldBe(armed.ETag);
        afterCompletion.StartAt.ShouldBe(armed.StartAt);

        // And the disarm still disarms: the guard belongs to the arm and must not have reached the
        // branch that cancels.
        (await group.CompleteCreateAsync(second.Id, ProvisioningState.Succeeded)).IsSuccess.ShouldBeTrue();

        (await Row(group)).ShouldBeNull(
            "the last completion empties CreatingSince, and a group with nothing to reap does not "
            + "keep a standing reminder row"
        );
    }

    /// <summary>
    ///     ⚠ <b>A reaper row that is gone from the table is put back, so the guard did not turn the
    ///     arm into a no-op.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The #12 review's second finding, asked of this grain: "the reminder is durable in
    ///         Orleans' table" is not an answer to a table restored from a backup, nor to a create
    ///         that armed on a silo with no reminder service — <c>ArmOrDisarmAsync</c> catches that
    ///         and carries on by design. The row is deleted underneath the grain here, which is the
    ///         faithful reproduction of both.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What puts it back is <c>OnActivateAsync</c>, and that is why this grain owes no
    ///         equivalent of <c>ExpirySweeperBackfill</c>.</b> That backfill exists because
    ///         <c>ExpirySweeperGrain</c> holds nothing — its evidence is a separate registry grain,
    ///         so an activation is not evidence and it deliberately arms nothing on one. This grain's
    ///         evidence is its own durable state, loaded by the activation itself, so the activation
    ///         can decide correctly with no extra read; the case below is that line doing its job.
    ///         What is left over is a group whose orphans nobody ever looks at, and every path that
    ///         can look — <c>ListAsync</c>, <c>BeginCreateAsync</c>, the group delete — is a call on
    ///         this grain and therefore arms the reaper on the way past.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task AReaperRowLostFromTheTableIsPutBackByTheNextActivation() {
        var address = await GroupAndAddress(Tenant(2), "row-lost", "web-01");
        var group = Group(address);

        (await group.BeginCreateAsync(address)).IsSuccess.ShouldBeTrue();

        var armed = await Row(group);
        armed.ShouldNotBeNull();

        await Reminders.RemoveRow(
            group.GetGrainId(),
            ResourceGroupGrain.OrphanReminderName,
            armed!.ETag
        );

        (await Row(group)).ShouldBeNull("the row is gone, exactly as a restored backup would leave it");

        await PastTheClockGranularity();

        // The silo loses the activation. The next call re-reads PostgreSQL, finds web-01 still in
        // CreatingSince, and arms off that.
        await group.DeactivateAsync();
        await WaitForDeactivation();

        (await Group(address).ListAsync()).GetValueOrThrow()
            .ShouldContain(x => x.ResourceId == address.Id, TwoPhaseCreateTests.StateSurvivedDeactivation);

        var back = await Row(group);

        back.ShouldNotBeNull(
            "OnActivateAsync arms off CreatingSince, which is what covers a group whose members "
            + "were recorded before this grain had a reaper at all"
        );

        back!.StartAt.ShouldBeGreaterThan(
            armed.StartAt,
            "and it is a NEW row rather than the old one having survived the delete"
        );
    }

    static Guid Tenant(int n) => TenancyCluster.Tenant(8300 + n);

    /// <summary>The reaper's reminder row as the table really holds it, or <c>null</c>.</summary>
    Task<ReminderEntry?> Row(IResourceGroupGrain group) =>
        Reminders.ReadRow(group.GetGrainId(), ResourceGroupGrain.OrphanReminderName)!;

    IResourceGroupGrain Group(ResourceId address) =>
        cluster.ResourceGroupGrain(address.TenantId, address.SubscriptionId, address.ResourceGroup);

    /// <summary>
    ///     Waits long enough that a rewritten <c>StartAt</c> would have to differ from the one
    ///     already in the row.
    /// </summary>
    /// <remarks>
    ///     ⚠ <c>DateTime.UtcNow</c>'s granularity on Windows is about 15.6 ms, and the whole assertion
    ///     is an equality on a value derived from it. This is not the test being slow for comfort: at
    ///     zero delay the unguarded arm could write the same <c>StartAt</c> back and the case would
    ///     pass against the defect. The cluster's own clock is <see cref="TenancyCluster.Clock" /> and
    ///     advancing it would do nothing here — Orleans stamps reminder rows from the wall clock, not
    ///     from anything this suite injects.
    /// </remarks>
    static Task PastTheClockGranularity() =>
        Task.Delay(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken);

    /// <summary>See <c>TwoPhaseCreateTests.WaitForDeactivation</c> — the same pause, same reason.</summary>
    static Task WaitForDeactivation() =>
        Task.Delay(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken);

    async Task<ResourceId> GroupAndAddress(Guid tenant, string groupName, string resourceName) {
        var subscription = Guid.NewGuid();

        (await cluster.TenantGrain(tenant).CreateAsync("t" + tenant.ToString("N")[..8], "T", "eu-central"))
            .IsSuccess.ShouldBeTrue();

        (await cluster.SubscriptionGrain(tenant, subscription).CreateAsync("prod")).IsSuccess.ShouldBeTrue();

        (await cluster.SubscriptionGrain(tenant, subscription)
            .CreateResourceGroupAsync(groupName, "eu-central")).IsSuccess.ShouldBeTrue();

        return new(
            tenant,
            subscription,
            groupName,
            new("CyberCloud.DBforPostgreSQL", "servers"),
            resourceName,
            Guid.NewGuid()
        );
    }
}
