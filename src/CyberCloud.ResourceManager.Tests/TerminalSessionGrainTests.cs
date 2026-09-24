using CyberCloud.Kubernetes.Contracts;
using CyberCloud.ResourceManager.Terminals;
using System.Globalization;

namespace CyberCloud.ResourceManager.Tests;

/// <summary>
///     The session grain's own decisions — when a session ends, when it only waits, and what happens
///     to what was typed meanwhile — against a scripted pod, so each branch can be reached on purpose.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The branches a real kubelet can't be made to take on demand.</b>
///         <c>TerminalOverTheGatewayTests</c> runs the happy path, the idle reclaim and the isolation
///         checks against k3s. A refused attach, a cluster that stops answering and a stream that keeps
///         dropping under a running shell aren't things a test can ask k3s for, and they're where the
///         review of #22 found a console that one failed attach locked out of new sessions.
///     </para>
///     <para>
///         Each test uses a tenant of its own, so the per-tenant cap of live sessions never counts
///         another test's sessions.
///     </para>
/// </remarks>
/// <param name="cluster">The suite's silo, whose connection factory knows the scripted cluster.</param>
[Collection(ResourceManagerSuite.Name)]
public sealed class TerminalSessionGrainTests(ResourceManagerCluster cluster) {
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task AFailedAttachLeavesTheSessionOpenToTheNextConnectAndTheNextAttachOpensIt() {
        // ⚠ Found by the review of #22: the grain ENDED the session on a failed attach and left the pod
        // running under the same UID, and the UID is the session id — so every later connect named the
        // same ended grain and was refused until somebody called terminate.
        var (session, owner, spec) = Arrange();
        ScriptedShell.Attaches.Enqueue(Result<IKubeTerminal>.Failure(ErrorCode.InternalError, "the kubelet said no"));

        (await session.OpenAsync(spec, owner)).IsSuccess.ShouldBeTrue();

        var pane = new RecordingViewer();
        var reference = Reference(pane);
        (await session.AttachAsync(owner, reference, 80, 24)).IsSuccess.ShouldBeTrue();

        await Until(() => pane.Screen.Contains("could not attach", StringComparison.Ordinal), "the pane was never told");
        (await session.StatusAsync()).Phase.ShouldBe(TerminalSessionPhase.Registered, "a failed attach is not the end of the shell");
        pane.Ended.ShouldBeNull();

        // A reconnect: connect registers the same session again, and the attach opens the stream.
        var reopened = await session.OpenAsync(spec, owner);
        reopened.IsSuccess.ShouldBeTrue(reopened.Error?.Message);

        (await session.AttachAsync(owner, reference, 80, 24)).IsSuccess.ShouldBeTrue();
        await Until(async () => (await session.StatusAsync()).Phase == TerminalSessionPhase.Open, "the stream never opened");
    }

    [Fact]
    public async Task AClusterThatStopsAnsweringIsRetriedRatherThanEndingTheSession() {
        // OperationInProgress is the connection grain's "the cluster isn't answering; retry".
        var (session, owner, spec) = Arrange();
        ScriptedShell.Attaches.Enqueue(Result<IKubeTerminal>.Failure(ErrorCode.OperationInProgress, "cluster degraded"));
        ScriptedShell.Attaches.Enqueue(Result<IKubeTerminal>.Failure(ErrorCode.OperationInProgress, "cluster degraded"));

        (await session.OpenAsync(spec, owner)).IsSuccess.ShouldBeTrue();
        (await session.AttachAsync(owner, Reference(new RecordingViewer()), 80, 24)).IsSuccess.ShouldBeTrue();

        await Until(async () => (await session.StatusAsync()).Phase == TerminalSessionPhase.Open, "the retry never opened the stream");
        ScriptedShell.AttachCalls.ShouldBe(3);
    }

    [Fact]
    public async Task WhatIsTypedWhileThePodStartsIsSentOnceTheStreamOpens() {
        var (session, owner, spec) = Arrange();
        ScriptedShell.Phase = "Pending";

        (await session.OpenAsync(spec, owner)).IsSuccess.ShouldBeTrue();
        (await session.AttachAsync(owner, Reference(new RecordingViewer()), 120, 40)).IsSuccess.ShouldBeTrue();
        (await session.SendAsync(owner, "ls\r"u8.ToArray())).IsSuccess.ShouldBeTrue();

        ScriptedShell.Phase = "Running";

        await Until(() => ScriptedShell.Opened.TryPeek(out var open) && open.Written.Contains("ls\r"), "the held keystrokes never arrived");

        ScriptedShell.Opened.TryPeek(out var terminal).ShouldBeTrue();
        terminal!.Resizes.ShouldContain((120, 40), "the pane's size reaches the terminal before what was typed into it");
    }

    [Fact]
    public async Task AShellThatExitedEndsTheSessionDeletesThePodAndTellsThePane() {
        var (session, owner, spec) = Arrange();

        (await session.OpenAsync(spec, owner)).IsSuccess.ShouldBeTrue();

        var pane = new RecordingViewer();
        (await session.AttachAsync(owner, Reference(pane), 80, 24)).IsSuccess.ShouldBeTrue();
        await Until(() => !ScriptedShell.Opened.IsEmpty, "the stream never opened");

        ScriptedShell.Opened.TryPeek(out var terminal).ShouldBeTrue();
        terminal!.Print("bye\r\n");
        ScriptedShell.Phase = "Succeeded";
        terminal.Close();

        await Until(() => pane.Ended is not null, "the pane was never told the shell ended");
        pane.Ended!.ShouldContain("exited");
        pane.Screen.ShouldContain("bye");
        ScriptedShell.Deleted.ShouldContain(ScriptedShell.Pod.Name, "a finished pod left in place would never run again");

        var again = await session.OpenAsync(spec, owner);
        again.IsFailure.ShouldBeTrue();
        again.Error!.Code.ShouldBe(ErrorCode.PreconditionFailed, "connect replaces the pod when it gets this answer");
    }

    [Fact]
    public async Task ADeleteTheClusterRefusesAsARetryIsTriedAgainOnTheNextTick() {
        // ⚠ Found running the cluster lane through the connection grain: the idle reclaim met
        // OperationInProgress — the connection grain's answer for a cluster past its staleness window —
        // gave up, and left the idle pod to its hard cap, hours of the cost the reclaim exists to stop.
        var (session, owner, spec) = Arrange();
        ScriptedShell.RefuseDeletes = 1;

        (await session.OpenAsync(spec, owner)).IsSuccess.ShouldBeTrue();

        var pane = new RecordingViewer();
        (await session.AttachAsync(owner, Reference(pane), 80, 24)).IsSuccess.ShouldBeTrue();
        await Until(() => !ScriptedShell.Opened.IsEmpty, "the stream never opened");

        ScriptedShell.Opened.TryPeek(out var terminal).ShouldBeTrue();
        ScriptedShell.Phase = "Succeeded";
        terminal!.Close();

        await Until(() => pane.Ended is not null, "the pane was never told the shell ended");
        ScriptedShell.Deleted.ShouldBeEmpty("the first delete was refused");

        // The idle timer outlives the session until the delete lands: one tick later.
        await Until(
            () => ScriptedShell.Deleted.Contains(ScriptedShell.Pod.Name),
            "the refused delete was never tried again",
            TerminalSessionGrain.IdleCheckInterval * 2
        );
    }

    [Fact]
    public async Task AStreamThatKeepsDroppingUnderARunningShellWaitsInsteadOfEnding() {
        var (session, owner, spec) = Arrange();
        ScriptedShell.DropOnOpen = true;

        (await session.OpenAsync(spec, owner)).IsSuccess.ShouldBeTrue();

        var pane = new RecordingViewer();
        (await session.AttachAsync(owner, Reference(pane), 80, 24)).IsSuccess.ShouldBeTrue();

        await Until(() => pane.Screen.Contains("kept dropping", StringComparison.Ordinal), "the pane was never told");
        (await session.StatusAsync()).Phase.ShouldBe(TerminalSessionPhase.Registered);
        pane.Ended.ShouldBeNull();
        ScriptedShell.Deleted.ShouldBeEmpty("a running shell is somebody's, and a dropping stream isn't a reason to delete it");

        // The next keystroke tries again, and this time the stream holds.
        ScriptedShell.DropOnOpen = false;
        var opened = ScriptedShell.AttachCalls;

        (await session.SendAsync(owner, "x"u8.ToArray())).IsSuccess.ShouldBeTrue();
        await Until(async () => (await session.StatusAsync()).Phase == TerminalSessionPhase.Open, "the keystroke never re-opened the stream");
        ScriptedShell.AttachCalls.ShouldBeGreaterThan(opened);
    }

    [Fact]
    public async Task ALostActivationKeepsTheSessionsOwner() {
        // ⚠ Found by the second review of #22. The owner lived only in the activation, so after a silo
        // restart, a rolling deploy or a rebalance the next person in the tenant to call connect was
        // bound to the still-running shell: the same pod, the same bash, the owner's state in it.
        var (session, alice, spec) = Arrange();
        var bob = alice with { SubjectId = "bob" };

        (await session.OpenAsync(spec, alice)).IsSuccess.ShouldBeTrue();
        (await session.OpenAsync(spec, bob)).Error!.Code.ShouldBe(ErrorCode.Conflict);

        await LoseActivationAsync(session);
        (await session.StatusAsync()).Phase.ShouldBe(TerminalSessionPhase.Unknown, "the activation was never lost");

        var taken = await session.OpenAsync(spec, bob);
        taken.IsFailure.ShouldBeTrue("a lost activation handed the shell to the next person to connect");
        taken.Error!.Code.ShouldBe(ErrorCode.Conflict);
        (await session.StatusAsync()).Owner.ShouldBe(TerminalSessionKeys.OwnerStamp(alice));
        (await session.AttachAsync(bob, Reference(new RecordingViewer()), 80, 24)).Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);

        // The owner's reconnect is connect again, and it re-registers the same session.
        (await session.OpenAsync(spec, alice)).IsSuccess.ShouldBeTrue();
        (await session.AttachAsync(alice, Reference(new RecordingViewer()), 80, 24)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task WithItsRecordGoneTheGrainTakesTheOwnerFromThePod() {
        // A grain with no record is a new session or a hot tier that lost its data; the stamp connect
        // put on the pod when it created it tells them apart. Nothing is recorded before the first
        // OpenAsync, so a fresh grain over a stamped pod is exactly the lost-record case.
        var (session, alice, spec) = Arrange();
        var bob = alice with { SubjectId = "bob" };
        var stamped = spec with { OwnerAnnotation = ScriptedShell.OwnerAnnotation };
        ScriptedShell.Owner = TerminalSessionKeys.OwnerStamp(alice);

        var taken = await session.OpenAsync(stamped, bob);
        taken.IsFailure.ShouldBeTrue("the grain bound somebody the pod's stamp doesn't name");
        taken.Error!.Code.ShouldBe(ErrorCode.Conflict);
        (await session.StatusAsync()).Owner.ShouldBeEmpty();

        (await session.OpenAsync(stamped, alice)).IsSuccess.ShouldBeTrue();
        (await session.StatusAsync()).Owner.ShouldBe(TerminalSessionKeys.OwnerStamp(alice));
    }

    [Fact]
    public async Task AnEndedSessionWhosePodStillStandsStaysEndedAcrossActivations() {
        // An ended session whose pod couldn't be deleted keeps its record, so the next activation still
        // answers PreconditionFailed and connect replaces the pod rather than re-opening a shell that
        // ended — possibly for somebody else.
        var (session, owner, spec) = Arrange();
        ScriptedShell.RefuseDeletes = int.MaxValue;

        (await session.OpenAsync(spec, owner)).IsSuccess.ShouldBeTrue();

        var pane = new RecordingViewer();
        (await session.AttachAsync(owner, Reference(pane), 80, 24)).IsSuccess.ShouldBeTrue();
        await Until(() => !ScriptedShell.Opened.IsEmpty, "the stream never opened");

        ScriptedShell.Opened.TryPeek(out var terminal).ShouldBeTrue();
        ScriptedShell.Phase = "Failed";
        terminal!.Close();
        await Until(() => pane.Ended is not null, "the pane was never told the shell ended");

        await LoseActivationAsync(session);

        var again = await session.OpenAsync(spec, owner);
        again.IsFailure.ShouldBeTrue("an ended session came back after its activation was lost");
        again.Error!.Code.ShouldBe(ErrorCode.PreconditionFailed);
        ScriptedShell.RefuseDeletes = 0;
    }

    /// <summary>Deactivates the session's activation, as a silo restart or a rebalance would.</summary>
    /// <remarks>Calls made after this reach a new activation: Orleans holds them until the old one has gone.</remarks>
    static async Task LoseActivationAsync(ITerminalSessionGrain session) =>
        await session.AsReference<Orleans.Core.Internal.IGrainManagementExtension>().DeactivateOnIdle();

    (ITerminalSessionGrain Session, CallerContext Owner, TerminalSessionSpec Spec) Arrange() {
        ResourceManagerCluster.ResetDoubles();
        var uid = ScriptedShell.Reset();
        var tenant = Guid.NewGuid();

        var session = cluster
            .For(tenant)
            .GetGrain<ITerminalSessionGrain>(TerminalSessionKeys.Session(uid));

        return (session, ResourceManagerCluster.Caller(tenant), ScriptedShell.Spec(tenant));
    }

    ITerminalViewer Reference(RecordingViewer pane) => cluster.Grains.CreateObjectReference<ITerminalViewer>(pane);

    static Task Until(Func<bool> condition, string because, TimeSpan? patience = null) =>
        Until(() => Task.FromResult(condition()), because, patience);

    static async Task Until(Func<Task<bool>> condition, string because, TimeSpan? patience = null) {
        var budget = patience ?? Patience;
        var deadline = DateTimeOffset.UtcNow + budget;

        while (!await condition()) {
            if (DateTimeOffset.UtcNow > deadline) {
                throw new TimeoutException(
                    string.Create(CultureInfo.InvariantCulture, $"{because} within {budget.TotalSeconds} seconds.")
                );
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
        }
    }
}
