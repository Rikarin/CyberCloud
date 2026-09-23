using CyberCloud.Cluster.Conformance.Infrastructure;
using CyberCloud.Conformance;
using CyberCloud.Gateway.Host.Hubs;
using CyberCloud.Providers.Terminal.Conformance;
using CyberCloud.Providers.Terminal.Contracts;
using Orleans.Multitenant;
using System.Globalization;
using System.Text.Json;
using k8s;
using k8s.Autorest;

namespace CyberCloud.Gateway.Host.Cluster.Conformance;

/// <summary>
///     docs/plan/24's M1 exit story, step 6 — a person opens the cloud shell and types into it — run
///     through the real gateway's <c>/hubs/terminal</c>, a real silo's session grain and a real
///     kubelet.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every hop is the production one except where the fixture says otherwise.</b>
///         <c>connect</c> goes over HTTP through the eight stages and the resource manager; the ticket
///         is minted by the gateway and spent by the WebSocket upgrade; <c>Attach</c>, <c>Send</c> and
///         <c>Resize</c> reach <see cref="ITerminalSessionGrain" /> in the silo; the grain attaches to
///         the pod's <c>pods/attach</c> stream; output returns through the grain observer the hub
///         registered. See <see cref="TerminalGatewayFixture" /> for the three stage-8 seams that are
///         substitutes and why none of them is on this path.
///     </para>
///     <para>
///         ⚠ <b>What k3s-in-Docker cannot show, stated so green is not over-read.</b> The console's
///         NetworkPolicy is applied and nothing here asserts that it constrains the shell —
///         <c>a-networkpolicy-that-nothing-enforces-still-reads-back</c> in the chart's owed list is
///         unchanged. The session grain and the gateway share one process with the silo here, as
///         every <c>TestCluster</c> does; the cross-process half is the wire contract's
///         (<c>[Alias]</c> on every type and method) and the AppHost's, which this lane does not start.
///     </para>
/// </remarks>
/// <param name="fixture">The silo, the cluster and the gateway in front of them.</param>
public sealed class TerminalOverTheGatewayTests(TerminalGatewayFixture fixture) : IClassFixture<TerminalGatewayFixture> {
    const string WouldProve =
        "a person opens a cloud shell through the gateway's terminal hub with a ticket, a command's "
        + "output round-trips, a resize reaches the terminal, a reconnect replays the ring, an idle "
        + "shell is reclaimed with its home volume kept, and another person or tenant is refused.";

    static readonly TimeSpan PodBudget = TimeSpan.FromMinutes(5);
    static readonly TimeSpan ScreenBudget = TimeSpan.FromSeconds(45);

    [Fact]
    public async Task AShellEchoesResizesReplaysOnReconnectAndIsReclaimedWhenIdle() {
        var harness = fixture.Require(WouldProve);
        var owner = fixture.Token(ConformanceIds.Tenant, "person-a");
        var console = await ConvergedConsoleAsync(harness, "story");

        // ── connect, over HTTP, as the portal does ─────────────────────────────────────────────
        var sessionId = await ConnectAsync(console, owner);
        await RunningAsync(harness, console.Name, sessionId);

        // ── the hub: a ticket, the socket, Attach ──────────────────────────────────────────────
        await using (var pane = await fixture.OpenPaneAsync(owner)) {
            (await pane.InvokeAsync(TerminalProtocol.Attach, sessionId, 80, 24)).ShouldBeNull();

            // ⚠ The command's own text is not what is waited for: the terminal echoes `cc-$((6*7))`
            // as typed, and only the shell's arithmetic prints `cc-42`.
            (await pane.TypeAsync(sessionId, "echo cc-$((6*7))\r")).ShouldBeNull();
            await pane.WaitForScreenAsync("cc-42", ScreenBudget);

            (await pane.InvokeAsync(TerminalProtocol.Resize, sessionId, 120, 40)).ShouldBeNull();
            (await pane.TypeAsync(sessionId, "stty size\r")).ShouldBeNull();
            await pane.WaitForScreenAsync("40 120", ScreenBudget);
        }

        // ── reconnect: the same connect, a fresh ticket, and the ring replayed ─────────────────
        (await ConnectAsync(console, owner)).ShouldBe(sessionId, "a live shell answers connect with the same session");

        await using var again = await fixture.OpenPaneAsync(owner);
        (await again.InvokeAsync(TerminalProtocol.Attach, sessionId, 120, 40)).ShouldBeNull();

        // Nothing was typed on this socket: `cc-42` can only have come from the replay ring.
        await again.WaitForScreenAsync("cc-42", ScreenBudget);
        again.Screen.ShouldContain("40 120");

        var session = harness.For(ConformanceIds.Tenant)
            .GetGrain<ITerminalSessionGrain>(TerminalSessionKeys.Session(sessionId));
        (await session.StatusAsync()).Phase.ShouldBe(TerminalSessionPhase.Open);

        // ── idle: past the console's timeout with no input, the pod goes and the home stays ─────
        harness.Clock.Advance(TimeSpan.FromMinutes(21));

        var ended = await again.WaitForEndedAsync(TimeSpan.FromSeconds(60));
        ended.ShouldContain("reclaimed");

        await GoneAsync(harness, console.Name);

        (await harness.Raw.CoreV1.ReadNamespacedPersistentVolumeClaimAsync(
            CloudConsoles.HomeClaimName(console.Name),
            ClusterConformanceHarness<CloudConsoleCase>.Namespace,
            cancellationToken: TestContext.Current.CancellationToken
        )).Metadata.DeletionTimestamp.ShouldBeNull("the home volume outlives an idle reclaim");

        var status = await session.StatusAsync();
        status.Phase.ShouldBe(TerminalSessionPhase.Ended);
        status.EndedBecause.ShouldContain("reclaimed");

        // The hub closed that socket after Ended. An ended session is not re-joinable from a new one
        // either, and the next connect is a NEW shell.
        await using (var late = await fixture.OpenPaneAsync(owner)) {
            (await late.InvokeAsync(TerminalProtocol.Attach, sessionId, 80, 24)).ShouldNotBeNull().ShouldContain("has ended");
        }

        var fresh = await ConnectAsync(console, owner);
        fresh.ShouldNotBe(sessionId);

        await fixture.PostAsync(console.Path + "/terminate", owner);
    }

    [Fact]
    public async Task AnotherPersonAnotherTenantAndARevokedRoleAreRefused() {
        var harness = fixture.Require(WouldProve);
        var owner = fixture.Token(ConformanceIds.Tenant, "person-a");
        var colleague = fixture.Token(ConformanceIds.Tenant, "person-b");
        var stranger = fixture.Token(ConformanceIds.OtherTenant, "person-a");
        var console = await ConvergedConsoleAsync(harness, "isolated");

        var sessionId = await ConnectAsync(console, owner);

        // ── the same tenant, another person, holding `connect` on the same console ─────────────
        var (status, body) = await fixture.PostAsync(console.Path + "/connect", colleague);
        status.ShouldBe(409, body);
        body.ShouldNotContain(sessionId, Case.Sensitive, "a refused connect must not hand out the session id");

        await using (var pane = await fixture.OpenPaneAsync(colleague)) {
            (await pane.InvokeAsync(TerminalProtocol.Attach, sessionId, 80, 24)).ShouldNotBeNull().ShouldContain(
                "no terminal session"
            );
            (await pane.TypeAsync(sessionId, "id\r")).ShouldNotBeNull();
        }

        // ── another tenant: the key is qualified by the token's tenant, so it names another grain ─
        await using (var pane = await fixture.OpenPaneAsync(stranger)) {
            (await pane.InvokeAsync(TerminalProtocol.Attach, sessionId, 80, 24)).ShouldNotBeNull().ShouldContain(
                "no terminal session"
            );
        }

        // ── the owner, after losing `connect` on the console: ReBAC is asked again on attach ───
        var authorizer = ClusterConformanceState<CloudConsoleCase>.Authorizer;

        try {
            authorizer.Restricted = true;
            authorizer.Granted["read"] = true;

            await using var pane = await fixture.OpenPaneAsync(owner);
            (await pane.InvokeAsync(TerminalProtocol.Attach, sessionId, 80, 24)).ShouldNotBeNull().ShouldContain(
                "does not have 'connect'"
            );
        } finally {
            authorizer.Reset();
        }

        // And with the role back, the owner is let in — the refusals above were about who, not what.
        await using (var pane = await fixture.OpenPaneAsync(owner)) {
            (await pane.InvokeAsync(TerminalProtocol.Attach, sessionId, 80, 24)).ShouldBeNull();
        }

        await fixture.PostAsync(console.Path + "/terminate", owner);
    }

    [Fact]
    public async Task ATenantIsCappedAtItsLiveSessionsAndALapsedLeaseFreesItsSlot() {
        var harness = fixture.Require(WouldProve);

        // A tenant of its own, so the story's sessions do not count against it.
        var limit = harness.For(Guid.NewGuid()).GetGrain<ITerminalSessionLimitGrain>(TerminalSessionKeys.Limit);
        var sessions = Enumerable.Range(0, TerminalSessionLimits.LiveSessionsPerTenant)
            .Select(static _ => Guid.NewGuid().ToString("D"))
            .ToList();

        foreach (var session in sessions) {
            (await limit.AdmitAsync(session)).IsSuccess.ShouldBeTrue();
        }

        var over = await limit.AdmitAsync(Guid.NewGuid().ToString("D"));
        over.IsFailure.ShouldBeTrue();
        over.Error!.Code.ShouldBe(ErrorCode.QuotaExceeded);

        // A renewal is not a new session.
        (await limit.AdmitAsync(sessions[0])).IsSuccess.ShouldBeTrue();

        await limit.ReleaseAsync(sessions[0]);
        (await limit.AdmitAsync(Guid.NewGuid().ToString("D"))).IsSuccess.ShouldBeTrue();

        // A session grain that vanished stops renewing, and its slot lapses.
        harness.Clock.Advance(TerminalSessionLimits.Lease + TimeSpan.FromSeconds(1));
        (await limit.LiveAsync()).ShouldBe(0);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────

    static async Task<ResourceId> ConvergedConsoleAsync(ClusterConformanceHarness<CloudConsoleCase> harness, string name) {
        var address = ClusterConformanceHarness<CloudConsoleCase>.Address(name);

        var accepted = await harness.Manager.WriteAsync(
            new() {
                Path = address.Path,
                ApiVersion = CloudConsoles.V2026,
                Verb = WriteVerb.Put,
                Body = CloudConsoles.Body(ConformanceIds.Cluster),
                Caller = ClusterConformanceHarness<CloudConsoleCase>.Caller()
            },
            TestContext.Current.CancellationToken
        );

        accepted.IsSuccess.ShouldBeTrue(accepted.Error?.Message);

        var operation = harness.Operation(ConformanceIds.Tenant, accepted.GetValueOrThrow().OperationId);
        OperationStatus? last = null;

        for (var drive = 0; drive < 60 && last?.IsTerminal != true; drive++) {
            last = (await operation.DriveAsync()).GetValueOrThrow();

            if (!last.IsTerminal) {
                await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
            }
        }

        last!.State.ShouldBe(OperationState.Succeeded, last.Error?.Message);
        return address;
    }

    async Task<string> ConnectAsync(ResourceId console, string token) {
        var (status, body) = await fixture.PostAsync(console.Path + "/connect", token);
        status.ShouldBe(200, body);

        var answer = JsonDocument.Parse(body).RootElement;
        answer.GetProperty("hub").GetString().ShouldBe(CloudConsoles.HubPath);

        return answer.GetProperty(CloudConsoles.SessionIdField).GetString()!;
    }

    static async Task RunningAsync(ClusterConformanceHarness<CloudConsoleCase> harness, string console, string sessionId) {
        var deadline = DateTimeOffset.UtcNow + PodBudget;

        while (true) {
            var pod = await harness.Raw.CoreV1.ReadNamespacedPodAsync(
                CloudConsoles.ShellName(console),
                ClusterConformanceHarness<CloudConsoleCase>.Namespace,
                cancellationToken: TestContext.Current.CancellationToken
            );

            pod.Metadata.Uid.ShouldBe(sessionId);

            if (pod.Status?.Phase == "Running") {
                return;
            }

            if (DateTimeOffset.UtcNow > deadline) {
                throw new TimeoutException(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"the shell pod stayed {pod.Status?.Phase} for {PodBudget.TotalMinutes} minutes: "
                    )
                    + string.Join("; ", pod.Status?.ContainerStatuses?.Select(static x => x.State?.Waiting?.Message) ?? [])
                );
            }

            await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        }
    }

    static async Task GoneAsync(ClusterConformanceHarness<CloudConsoleCase> harness, string console) {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(1);

        while (DateTimeOffset.UtcNow < deadline) {
            try {
                await harness.Raw.CoreV1.ReadNamespacedPodAsync(
                    CloudConsoles.ShellName(console),
                    ClusterConformanceHarness<CloudConsoleCase>.Namespace,
                    cancellationToken: TestContext.Current.CancellationToken
                );
            } catch (HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound) {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        }

        throw new TimeoutException("the idle shell's pod was never deleted.");
    }
}
