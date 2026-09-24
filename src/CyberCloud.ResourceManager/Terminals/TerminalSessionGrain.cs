using CyberCloud.Core.Time;
using Microsoft.Extensions.Logging;
using Orleans.Multitenant;
using System.Globalization;
using System.Text.Json.Nodes;

namespace CyberCloud.ResourceManager.Terminals;

/// <summary>
///     One cloud-terminal session — docs/plan/19 § Architecture's <c>ITerminalSessionGrain</c>: the
///     attach stream to the resource's pod, the replay ring, the resize channel and the idle timer.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The stream is pumped by a task the grain starts, not by a request.</b> A grain call
///         cannot stay open for the life of a shell, so <see cref="PumpAsync" /> runs on the
///         activation's scheduler between requests: every <c>await</c> in it hands the activation
///         back, so a keystroke's <see cref="SendAsync" /> interleaves with output at each read. Its
///         continuations run on the activation's scheduler because nothing here uses
///         <c>ConfigureAwait(false)</c> — and that is load-bearing twice: grain state is only safe on
///         that scheduler, and a grain call made off it would reach the cluster connection grain as a
///         client and be refused by its tenancy check.
///     </para>
///     <para>
///         ⚠ <b>Kept alive by its timer while it holds a shell.</b> An idle activation is collected, and
///         a collected session grain is an idle timer that never fires — the precise way a million
///         abandoned tabs become a million pods. The timer is <c>KeepAlive</c> until the session ends
///         and its pod is gone, and is disposed then, so an ended session is collected like any other
///         idle grain.
///     </para>
///     <para>
///         ⚠ <b>In the resource manager and not in <c>CyberCloud.Providers.Terminal</c>, because the
///         Architecture gate refuses it there.</b> Rule 8 of docs/plan/03 § Assembly graph rules
///         forbids a provider to ask <see cref="IResourceAuthorizer" /> or open a connection through
///         <see cref="IClusterConnectionFactory" /> outside a pass, and those are the two constructor
///         parameters below. <c>ConnectionGrain</c> is here for the same reason. The provider fills
///         <see cref="TerminalSessionSpec" /> in Kubernetes terms and registers it through
///         <see cref="ActionContext.Terminals" />; nothing here knows what a console is.
///     </para>
/// </remarks>
/// <param name="clusters">Turns the resource's cluster id into a connection. The silo's own.</param>
/// <param name="authorizer">
///     The ReBAC seam every attach re-checks the spec's permission through. ⚠ The manager's, from
///     <c>CyberCloud.ResourceManager.Contracts</c> — a provider may ask the enforcement seam a question
///     and may never name the engine behind it (docs/plan/07 § The enforcement seam).
/// </param>
/// <param name="clock">What the idle clock is read from.</param>
/// <param name="logger">Where a session's life is written: who, when, which console, and how it ended.</param>
public sealed class TerminalSessionGrain(
    IClusterConnectionFactory clusters,
    IResourceAuthorizer authorizer,
    IClock clock,
    ILogger<TerminalSessionGrain> logger
)
    : Grain, ITerminalSessionGrain {
    /// <summary>How many bytes of output a reconnect is replayed.</summary>
    /// <remarks>
    ///     64 KiB is a few screens of an ordinary shell and one screen of a busy full-screen program —
    ///     enough to find your place, and small enough that ten thousand sessions on a silo hold
    ///     640 MB rather than a multiple of it.
    /// </remarks>
    public const int RingBytes = 64 * 1024;

    /// <summary>How much typing is held while the stream is still opening. Past this, input is refused.</summary>
    public const int PendingInputBytes = 16 * 1024;

    /// <summary>How often the idle clock is read.</summary>
    /// <remarks>
    ///     ⚠ The resolution of the idle timeout, not the timeout. A twenty-minute timeout reclaims
    ///     between twenty minutes and twenty minutes fifteen seconds after the last keystroke.
    /// </remarks>
    public static readonly TimeSpan IdleCheckInterval = TimeSpan.FromSeconds(15);

    /// <summary>How long an attach waits for the pod to reach <c>Running</c> before the session gives up.</summary>
    /// <remarks>
    ///     docs/plan/19 § The image puts a cold pull of the default image at about forty seconds; five
    ///     minutes covers a slow registry and a busy node, and a pod still <c>Pending</c> after that is
    ///     not going to start.
    /// </remarks>
    public static readonly TimeSpan StartBudget = TimeSpan.FromMinutes(5);

    /// <summary>How many times a dropped stream under a still-running shell is re-opened in a row.</summary>
    const int ReopenAttempts = 3;

    /// <summary>How many idle ticks an ended session spends trying to delete its pod — five minutes.</summary>
    const int ReclaimAttempts = 20;

    readonly OutputRing ring = new(RingBytes);
    readonly List<ITerminalViewer> viewers = [];
    readonly Dictionary<ITerminalViewer, List<byte[]>> joining = [];
    readonly List<byte[]> pendingInput = [];

    Guid tenantId;
    string sessionId = string.Empty;
    TerminalSessionSpec? spec;
    CallerContext? owner;
    TerminalSessionPhase phase = TerminalSessionPhase.Unknown;
    string endedBecause = string.Empty;
    IKubeTerminal? terminal;
    Task? opening;
    IGrainTimer? idleTimer;
    DateTimeOffset lastActivityAt;
    int pendingBytes;
    int reopened;
    bool reclaimOwed;
    int reclaimAttempts;
    int columns = 80;
    int rows = 24;

    /// <inheritdoc />
    public override Task OnActivateAsync(CancellationToken cancellationToken) {
        var tenant = this.GetTenantId()
            ?? throw new InvalidOperationException(
                $"{nameof(TerminalSessionGrain)} is a tenant-scoped grain but was activated with no tenant "
                + "qualification. Reach it with IGrainFactory.ForTenant(tenantId).GetGrain<ITerminalSessionGrain>(…) "
                + "— ADR-002."
            );

        if (!Guid.TryParseExact(tenant, "D", out tenantId)) {
            throw new InvalidOperationException(
                $"{nameof(TerminalSessionGrain)} was activated for tenant '{tenant}', which is not a tenant GUID."
            );
        }

        var within = this.GetKeyWithinTenant();

        // ⚠ Parsed, not trusted — the ConnectionGrain rule. A grain activated under another shape
        // would be serving a key it does not understand.
        if (!within.StartsWith(TerminalSessionKeys.Prefix, StringComparison.Ordinal)
            || !TerminalSessionKeys.IsSessionId(within[TerminalSessionKeys.Prefix.Length..])) {
            throw new InvalidOperationException(
                $"{nameof(TerminalSessionGrain)} was activated with the key '{within}', which is not a "
                + $"'{TerminalSessionKeys.Prefix}{{sessionId}}' key. Build it with TerminalSessionKeys.Session."
            );
        }

        sessionId = within[TerminalSessionKeys.Prefix.Length..];
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken) {
        idleTimer?.Dispose();

        if (terminal is { } open) {
            terminal = null;
            await open.DisposeAsync();
        }

        if (phase != TerminalSessionPhase.Ended && spec is not null) {
            // ⚠ The pod outlives this activation, deliberately: a silo shutting down must not take
            // every shell on it down too. The next connect re-registers the session; until then the
            // idle clock is not running — conformance.yaml § owed, `no-idle-sweep-without-an-activation`.
            logger.LogWarning(
                "Terminal session {Session} of {Console} deactivated ({Reason}) while its shell was "
                + "still running; the pod is left to the next connect or its hard cap.",
                sessionId,
                spec.Resource.Path,
                reason.Description
            );
        }
    }

    // ── The surface ───────────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<Result> OpenAsync(TerminalSessionSpec spec, CallerContext owner) {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(owner);

        // ⚠ Three things must agree with the key before anything is stored, because the key is the
        // only part of this call the tenant did not write: the tenant qualification (from the token
        // the gateway saw), the caller's own tenant, and the resource's.
        if (owner.TenantId != tenantId || spec.Resource.TenantId != tenantId) {
            return Refuse(
                ErrorCode.AuthorizationFailed,
                "A terminal session was registered for a different tenant than the key it was "
                + "registered under. The key's tenant comes from the token — docs/plan/00 § The "
                + "tenant-separation row, corrected."
            );
        }

        if (!string.Equals(spec.PodUid, sessionId, StringComparison.Ordinal)) {
            return Refuse(
                ErrorCode.InvalidRequestBody,
                $"Session {sessionId} was registered for pod {spec.PodUid}. The session id is the pod's "
                + "UID, so the two cannot differ."
            );
        }

        if (phase == TerminalSessionPhase.Ended) {
            return Refuse(
                ErrorCode.PreconditionFailed,
                $"This shell has ended ({endedBecause}) and cannot be re-joined. Connect again for a "
                + "new one; the home directory is the same."
            );
        }

        if (this.owner is { } bound && !SameSubject(bound, owner)) {
            // ⚠ Conflict and not 404: the caller holds `connect` on this console — the manager checked
            // before the handler ran — so the console's existence is known to them, and "somebody else
            // is in this shell" is the useful answer. What they do NOT get is the session.
            return Refuse(
                ErrorCode.Conflict,
                $"The shell of '{spec.Resource.Path}' is open for another person. A session belongs to "
                + "the person who opened it; it ends when they exit, when it sits idle, or on terminate."
            );
        }

        if (this.owner is null) {
            // ⚠ The per-tenant cap is taken HERE, once, when a session first gets a person — a
            // re-registration by the same owner renews nothing and costs nothing. Refused, connect
            // deletes the pod it just started — see CloudConsoleSessionHandler in CyberCloud.Providers.Terminal.
            var admitted = await Limit(spec.Resource.TenantId).AdmitAsync(sessionId);
            if (admitted.IsFailure) {
                return admitted;
            }

            logger.LogInformation(
                "Terminal session {Session} opened for {Owner} on {Console} (cluster {Cluster}).",
                sessionId,
                owner,
                spec.Resource.Path,
                spec.ClusterId
            );
        }

        this.spec = spec;
        this.owner = owner;

        if (phase == TerminalSessionPhase.Unknown) {
            phase = TerminalSessionPhase.Registered;
        }

        // A connect is somebody at the keyboard; the idle clock starts from it.
        lastActivityAt = clock.UtcNow;
        idleTimer ??= this.RegisterGrainTimer(
            TickAsync,
            new GrainTimerCreationOptions {
                DueTime = IdleCheckInterval, Period = IdleCheckInterval, KeepAlive = true, Interleave = true
            }
        );

        return Result.Success;
    }

    /// <inheritdoc />
    public async Task<Result> AttachAsync(CallerContext caller, ITerminalViewer viewer, int columns, int rows) {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(viewer);

        var mine = Mine(caller);
        if (mine.IsFailure) {
            return mine;
        }

        // ⚠ RE-CHECKED ON EVERY ATTACH, FULLY CONSISTENT. Ownership says whose shell this is; ReBAC
        // says whether that person may still have one. A role revoked at 09:05 must stop the 09:06
        // reconnect — the same rule docs/plan/10 § SignalR applies to the interest hubs. Keystrokes
        // are not re-checked: one check per byte typed is a check per byte typed, and a revoked
        // person's open socket closes at the next reconnect, which is the interest hubs' bound too.
        var allowed = await authorizer.AuthorizeAsync(
            spec!.Resource,
            spec.Permission,
            spec.ReadPermission,
            caller,
            fullyConsistent: true
        );

        if (allowed.TryGetError(out var refusal)) {
            logger.LogWarning(
                "Terminal session {Session}: {Caller} was refused on attach by ReBAC: {Message}",
                sessionId,
                caller,
                refusal.Message
            );

            return Result.Failure(refusal);
        }

        if (phase == TerminalSessionPhase.Ended) {
            return Result.Failure(ErrorCode.PreconditionFailed, EndedMessage());
        }

        Size(columns, rows);
        lastActivityAt = clock.UtcNow;

        // ⚠ THE REPLAY AND THE LIVE STREAM MUST NOT OVERTAKE EACH OTHER, AND THEY WOULD. Delivering the
        // replay awaits, and the pump runs during that await: a live chunk sent to this pane then could
        // arrive before the replay (two outstanding calls to one observer are not ordered), and one
        // sent only to the others would be missing from both. So the pane joins as JOINING: the pump
        // queues its chunks, the replay is delivered, then the queue — until it is empty — and only then
        // does the pane go live. Every byte arrives once, in order.
        if (!viewers.Contains(viewer) && !joining.ContainsKey(viewer)) {
            joining[viewer] = [];

            try {
                var replay = ring.Snapshot();

                if (replay.Length > 0) {
                    await viewer.OutputAsync(replay);
                }

                while (joining.TryGetValue(viewer, out var backlog) && backlog.Count > 0) {
                    var next = backlog[0];
                    backlog.RemoveAt(0);
                    await viewer.OutputAsync(next);
                }
            } catch (Exception ex) when (ex is not OutOfMemoryException) {
                return Result.Failure(
                    ErrorCode.InternalError,
                    $"The pane went away while its replay was being delivered: {ex.GetType().Name}."
                );
            } finally {
                joining.Remove(viewer);
            }

            if (phase == TerminalSessionPhase.Ended) {
                return Result.Failure(ErrorCode.PreconditionFailed, EndedMessage());
            }

            viewers.Add(viewer);
        }

        if (terminal is { } open) {
            await ResizeOpenAsync(open);
        } else {
            EnsureOpening();
        }

        return Result.Success;
    }

    /// <inheritdoc />
    public async Task<Result> SendAsync(CallerContext caller, byte[] data) {
        ArgumentNullException.ThrowIfNull(caller);

        var mine = Mine(caller);
        if (mine.IsFailure) {
            return mine;
        }

        if (phase == TerminalSessionPhase.Ended) {
            return Result.Failure(ErrorCode.PreconditionFailed, EndedMessage());
        }

        if (data is null || data.Length == 0) {
            return Result.Success;
        }

        lastActivityAt = clock.UtcNow;

        if (terminal is not { } open) {
            // ⚠ Held, and bounded. The pane is attached — the person sees a cursor — and the pod may
            // still be pulling its image. Dropping what they typed would be the silent swallow
            // TerminalHub refused; holding it without a bound would be a memory leak a paste can
            // trigger.
            if (pendingBytes + data.Length > PendingInputBytes) {
                return Result.Failure(
                    ErrorCode.OperationInProgress,
                    "The shell is still starting and has not taken the input already typed. Wait for "
                    + "the prompt."
                );
            }

            pendingInput.Add(data);
            pendingBytes += data.Length;

            // A keystroke after the stream gave up is a person asking for it again. While it's still
            // opening this does nothing.
            EnsureOpening();
            return Result.Success;
        }

        try {
            await open.WriteAsync(data);
        } catch (Exception ex) when (ex is not OutOfMemoryException) {
            return Result.Failure(
                ErrorCode.OperationInProgress,
                $"The keystrokes did not reach the shell ({ex.GetType().Name}); the stream is being "
                + "re-opened. Type again in a moment."
            );
        }

        return Result.Success;
    }

    /// <inheritdoc />
    public async Task<Result> ResizeAsync(CallerContext caller, int columns, int rows) {
        ArgumentNullException.ThrowIfNull(caller);

        var mine = Mine(caller);
        if (mine.IsFailure) {
            return mine;
        }

        if (phase == TerminalSessionPhase.Ended) {
            return Result.Failure(ErrorCode.PreconditionFailed, EndedMessage());
        }

        Size(columns, rows);

        if (terminal is { } open) {
            await ResizeOpenAsync(open);
        }

        return Result.Success;
    }

    /// <inheritdoc />
    public Task DetachAsync(ITerminalViewer viewer) {
        viewers.Remove(viewer);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<TerminalSessionStatus> StatusAsync() =>
        Task.FromResult(
            new TerminalSessionStatus {
                Phase = phase,
                Owner = owner is null ? string.Empty : owner.SubjectType + ":" + owner.SubjectId,
                Viewers = viewers.Count,
                Buffered = ring.Count,
                EndedBecause = endedBecause
            }
        );

    // ── The stream ────────────────────────────────────────────────────────────────────────────

    /// <summary>Starts opening the stream, unless it's open or already being opened.</summary>
    /// <remarks>
    ///     ⚠ <b>Checked by <see cref="Task.IsCompleted" />, not cleared by the opener.</b> An opener that
    ///     cleared the field in a <c>finally</c> could finish before the assignment that stored it —
    ///     every path through it can complete without yielding once no pane is listening — and would
    ///     leave a finished task in the field, so no attach or keystroke ever opened the stream again.
    /// </remarks>
    void EnsureOpening() {
        if (terminal is null && phase == TerminalSessionPhase.Registered && opening is not { IsCompleted: false }) {
            opening = OpenStreamAsync();
        }
    }

    /// <summary>Waits for the pod to run, attaches, and starts the pump.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Only the pod decides that a session has ended.</b> A pod that finished, went away or
    ///         never started ends it. A failure to reach the pod doesn't: an attach refused, a cluster
    ///         that stopped answering or a silo with no connection leaves the session
    ///         <see cref="TerminalSessionPhase.Registered" />, tells the panes, and the next attach or
    ///         keystroke tries again. Ending it there would bind an ended grain to a pod that's still
    ///         running under the same UID, which is the session id, and every later <c>connect</c>
    ///         would be refused until somebody called <c>terminate</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><see cref="ErrorCode.OperationInProgress" /> is retried here.</b> It's the
    ///         connection grain's answer for a cluster it can't reach right now — "a retry, not a
    ///         refusal" — so the loop keeps asking until the start budget runs out.
    ///     </para>
    /// </remarks>
    async Task OpenStreamAsync() {
        var deadline = clock.UtcNow + StartBudget;
        var started = DateTimeOffset.UtcNow;
        string? unreachable = null;

        while (phase == TerminalSessionPhase.Registered) {
            if (Cluster() is not { } cluster) {
                await StreamFailedAsync("this silo has no connection to the shell's cluster");
                return;
            }

            var state = await PodStateAsync(cluster);

            if (state.Ended is { } ended) {
                await EndAsync(ended, deletePod: state.Completed);
                return;
            }

            unreachable = state.Unreadable;

            if (state.Running) {
                var attached = await cluster.AttachAsync(spec!.Pod, spec.Container);

                if (attached.IsSuccess) {
                    await StartAsync(attached.GetValueOrThrow());
                    return;
                }

                var refusal = attached.Error!;

                logger.LogWarning(
                    "Terminal session {Session}: attaching to {Pod} failed ({Code}): {Message}",
                    sessionId,
                    spec.Pod,
                    refusal.Code,
                    refusal.Message
                );

                if (refusal.Code != ErrorCode.OperationInProgress) {
                    await StreamFailedAsync("the platform could not attach to the shell: " + refusal.Message);
                    return;
                }

                unreachable = refusal.Message;
            }

            // ⚠ Wall-clock and the injected clock both bound the wait. The injected one is the
            // platform's; the wall-clock one is here because a test clock that never moves would
            // otherwise make this loop wait forever.
            if (clock.UtcNow > deadline || DateTimeOffset.UtcNow - started > StartBudget) {
                if (unreachable is not null) {
                    await StreamFailedAsync(
                        $"the shell's cluster did not answer for {StartBudget.TotalMinutes:0} minutes ({unreachable})"
                    );
                } else {
                    await EndAsync(
                        $"the shell did not start within {StartBudget.TotalMinutes:0} minutes (the pod is {state.Phase})",
                        deletePod: true
                    );
                }

                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }
    }

    /// <summary>Takes an opened stream: sizes it, sends what was typed meanwhile, and starts the pump.</summary>
    /// <param name="open">The stream the attach returned.</param>
    async Task StartAsync(IKubeTerminal open) {
        if (phase != TerminalSessionPhase.Registered) {
            await open.DisposeAsync();
            return;
        }

        terminal = open;
        phase = TerminalSessionPhase.Open;

        await ResizeOpenAsync(open);

        foreach (var held in pendingInput) {
            await open.WriteAsync(held);
        }

        pendingInput.Clear();
        pendingBytes = 0;

        _ = PumpAsync(open);
    }

    /// <summary>
    ///     Gives up on the stream for now without ending the session: the panes are told, and the next
    ///     attach or keystroke tries again.
    /// </summary>
    /// <param name="reason">What went wrong, for the pane and the log.</param>
    async Task StreamFailedAsync(string reason) {
        if (phase == TerminalSessionPhase.Ended) {
            return;
        }

        phase = TerminalSessionPhase.Registered;
        reopened = 0;

        // What was typed into a stream that never opened is dropped: sending it minutes later, when
        // somebody reconnects, would run commands the person has stopped expecting.
        pendingInput.Clear();
        pendingBytes = 0;

        logger.LogWarning("Terminal session {Session}: the stream is not open: {Reason}.", sessionId, reason);

        await NoticeAsync($"\r\n[Cyber Cloud: {reason}. The shell is still there; type or reconnect to try again.]\r\n");
    }

    /// <summary>Reads the shell's output until the stream ends, then decides what the end meant.</summary>
    /// <param name="open">The terminal this pump owns.</param>
    async Task PumpAsync(IKubeTerminal open) {
        var buffer = new byte[16 * 1024];

        while (true) {
            int read;

            try {
                read = await open.ReadAsync(buffer);
            } catch (Exception ex) when (ex is not OutOfMemoryException) {
                logger.LogWarning(ex, "Terminal session {Session}: the stream failed.", sessionId);
                read = 0;
            }

            if (read == 0) {
                break;
            }

            var chunk = buffer.AsSpan(0, read).ToArray();
            reopened = 0;
            ring.Append(chunk);
            await DeliverAsync(chunk);
        }

        if (!ReferenceEquals(terminal, open)) {
            // Superseded — reclaimed or ended while the last read was outstanding. Whoever replaced
            // it has already disposed this one.
            return;
        }

        terminal = null;
        await open.DisposeAsync();

        if (phase == TerminalSessionPhase.Ended) {
            return;
        }

        // ⚠ "The stream ended" is two different facts and the pod says which. A shell that exited —
        // `exit`, the kubelet's hard cap, a terminate — has a pod that is gone or finished. A stream
        // that dropped under a live shell (an API server restart, a network blip) has a pod still
        // Running with the same UID, and ending the session then would throw away a shell somebody
        // is in the middle of.
        phase = TerminalSessionPhase.Registered;

        if (Cluster() is not { } cluster) {
            await StreamFailedAsync("this silo has no connection to the shell's cluster");
            return;
        }

        var state = await PodStateAsync(cluster);

        if (state.Ended is { } ended) {
            await EndAsync(ended, deletePod: state.Completed);
            return;
        }

        if (++reopened > ReopenAttempts) {
            await StreamFailedAsync("the stream to the shell kept dropping");
            return;
        }

        logger.LogWarning(
            "Terminal session {Session}: the stream dropped under a running shell; re-opening ({Attempt}/{Max}).",
            sessionId,
            reopened,
            ReopenAttempts
        );

        // ⚠ Unconditionally, not through EnsureOpening: this pump can still be running inside the
        // opener that started it, which isn't complete yet, and EnsureOpening would take that for an
        // open in progress and do nothing. A second opener racing an attach's is harmless — StartAsync
        // closes the stream that arrives second.
        opening = OpenStreamAsync();
    }

    /// <summary>
    ///     Writes a line from the platform to every live pane, outside the shell's own output — it is
    ///     not kept in the replay ring.
    /// </summary>
    /// <param name="text">The line, with its own carriage returns.</param>
    async Task NoticeAsync(string text) {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);

        foreach (var viewer in viewers.ToArray()) {
            try {
                await viewer.OutputAsync(bytes);
            } catch (Exception ex) when (ex is not OutOfMemoryException) {
                viewers.Remove(viewer);
                logger.LogDebug(ex, "Terminal session {Session}: a pane missed a notice.", sessionId);
            }
        }
    }

    async Task DeliverAsync(byte[] chunk) {
        foreach (var backlog in joining.Values) {
            backlog.Add(chunk);
        }

        foreach (var viewer in viewers.ToArray()) {
            try {
                await viewer.OutputAsync(chunk);
            } catch (Exception ex) when (ex is not OutOfMemoryException) {
                // ⚠ The gateway pod holding this pane is gone, or its socket is. Dropped rather than
                // retried: its person reconnects through a fresh Attach, and the ring holds what
                // they missed.
                viewers.Remove(viewer);

                logger.LogInformation(
                    "Terminal session {Session}: dropped a pane that could not be reached ({Error}).",
                    sessionId,
                    ex.GetType().Name
                );
            }
        }
    }

    async Task ResizeOpenAsync(IKubeTerminal open) {
        try {
            await open.ResizeAsync(columns, rows);
        } catch (Exception ex) when (ex is not OutOfMemoryException) {
            // The pump will see the same failure on its next read and decide; a resize is not worth
            // a second decision.
            logger.LogDebug(ex, "Terminal session {Session}: a resize did not reach the shell.", sessionId);
        }
    }

    // ── The idle clock and the end ────────────────────────────────────────────────────────────

    async Task TickAsync(CancellationToken cancellationToken) {
        if (phase == TerminalSessionPhase.Ended) {
            if (reclaimOwed) {
                await ReclaimAsync();
            }

            return;
        }

        if (spec is null) {
            return;
        }

        // The slot is renewed on every tick while the session lives — see ITerminalSessionLimitGrain.
        await Limit(spec.Resource.TenantId).AdmitAsync(sessionId);

        var idle = clock.UtcNow - lastActivityAt;

        if (idle >= TimeSpan.FromSeconds(spec.IdleTimeoutSeconds)) {
            await EndAsync(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"the shell was reclaimed after {spec.IdleTimeoutSeconds / 60} minutes with no input; the home directory is kept"
                ),
                deletePod: true
            );
        }
    }

    /// <summary>Ends the session: closes the stream, tells every pane, and reclaims the pod when asked.</summary>
    /// <param name="reason">Why, for the pane and the log.</param>
    /// <param name="deletePod">
    ///     Whether to delete the pod. ⚠ <see langword="true" /> for an idle reclaim and for a shell that
    ///     exited — a finished pod left in place is one the next <c>connect</c> would apply unchanged and
    ///     never run again — and <see langword="false" /> when the pod is already gone, going, or
    ///     another session's.
    /// </param>
    async Task EndAsync(string reason, bool deletePod) {
        if (phase == TerminalSessionPhase.Ended) {
            return;
        }

        phase = TerminalSessionPhase.Ended;
        endedBecause = reason;
        pendingInput.Clear();
        pendingBytes = 0;

        if (terminal is { } open) {
            terminal = null;
            await open.DisposeAsync();
        }

        // docs/plan/19 § Auditing: who, when, which console, which cluster, how long. The duration is
        // the one fact only this grain holds; the sink it belongs in is owed (`no-audit-sink`).
        logger.LogInformation(
            "Terminal session {Session} of {Owner} on {Console} (cluster {Cluster}) ended: {Reason}.",
            sessionId,
            owner,
            spec?.Resource.Path,
            spec?.ClusterId,
            reason
        );

        foreach (var viewer in viewers.ToArray()) {
            try {
                await viewer.EndedAsync(reason);
            } catch (Exception ex) when (ex is not OutOfMemoryException) {
                logger.LogDebug(ex, "Terminal session {Session}: a pane missed the end.", sessionId);
            }
        }

        viewers.Clear();

        if (spec is not null) {
            await Limit(spec.Resource.TenantId).ReleaseAsync(sessionId);
        }

        reclaimOwed = deletePod && spec is not null;

        if (reclaimOwed) {
            await ReclaimAsync();
        } else {
            idleTimer?.Dispose();
            idleTimer = null;
        }
    }

    /// <summary>
    ///     Deletes the ended session's pod, and keeps the timer alive to try again when the cluster
    ///     doesn't take the delete.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Retried, because the failure it meets is usually a retry.</b> The connection grain
    ///     answers a delete on a cluster it hasn't heard from within its staleness window with
    ///     <see cref="ErrorCode.OperationInProgress" />, and a reclaim that gave up there would leave an
    ///     idle pod running to its hard cap — hours of the cost the reclaim exists to stop. So the idle
    ///     timer, which would otherwise be disposed with the session, keeps ticking until the delete
    ///     lands or <see cref="ReclaimAttempts" /> have failed. After that the pod stops at its hard cap,
    ///     and the next <c>connect</c> replaces it, since its session has ended.
    /// </remarks>
    async Task ReclaimAsync() {
        var cluster = spec is null ? null : Cluster();
        Result deleted;

        if (cluster is null) {
            deleted = Result.Failure(ErrorCode.InternalError, "this silo has no connection to the shell's cluster");
        } else {
            // ⚠ Through KubeCommand like the action's own apply, so the delete is labeled for the
            // tenant it is made on behalf of — the same spelling the provider's `terminate` uses.
            deleted = await KubeCommand.For(cluster)
                .WithTenantId(spec!.Resource.TenantId)
                .WithResourceId(spec.Resource)
                .InNamespace(spec.Pod.Namespace)
                .WithKind(spec.Pod.Kind)
                .WithApiVersion(spec.ApiVersion)
                .ObjectJson(new JsonObject { ["metadata"] = new JsonObject { ["name"] = spec.Pod.Name } }.ToJsonString())
                .DeleteAsync(CascadePolicy.Foreground, CancellationToken.None);
        }

        if (deleted.TryGetError(out var deleteError) && deleteError.Code != ErrorCode.ResourceNotFound) {
            if (++reclaimAttempts < ReclaimAttempts) {
                logger.LogWarning(
                    "Terminal session {Session}: the shell's pod could not be deleted yet ({Attempt}/{Max}): {Message}.",
                    sessionId,
                    reclaimAttempts,
                    ReclaimAttempts,
                    deleteError.Message
                );

                return;
            }

            logger.LogError(
                "Terminal session {Session}: the shell's pod could not be deleted after the session ended "
                + "({Reason}): {Message}. It stops at its hard cap, or at the next connect.",
                sessionId,
                endedBecause,
                deleteError.Message
            );
        }

        reclaimOwed = false;
        idleTimer?.Dispose();
        idleTimer = null;
    }

    /// <summary>What the pod says about the shell.</summary>
    /// <param name="cluster">The pod's cluster.</param>
    /// <returns>
    ///     Whether it's running; the reason it has ended when it has; whether the pod object is still
    ///     there to be removed; its phase; and, when the pod couldn't be read at all, why.
    /// </returns>
    async Task<(bool Running, string? Ended, bool Completed, string Phase, string? Unreadable)> PodStateAsync(
        IKubeClusterConnection cluster
    ) {
        var read = await cluster.GetAsync(spec!.Pod);

        if (read.TryGetError(out var readError)) {
            return readError.Code == ErrorCode.ResourceNotFound
                ? (false, "the shell was terminated", false, "gone", null)
                // A read that failed says nothing about the shell. Treated as "not yet": the caller's
                // budget bounds how long that can go on.
                : (false, null, false, "unreadable", readError.Message);
        }

        var pod = JsonNode.Parse(read.GetValueOrThrow().Json);
        var uid = pod?["metadata"]?["uid"]?.GetValue<string>();
        var podPhase = pod?["status"]?["phase"]?.GetValue<string>() ?? "Pending";

        if (!string.Equals(uid, sessionId, StringComparison.Ordinal)) {
            // A pod of the same name with a different UID is a NEW shell — the console was reconnected
            // after this one was reclaimed — and it is not this session's to touch.
            return (false, "the shell was replaced by a newer one", false, podPhase, null);
        }

        if (pod?["metadata"]?["deletionTimestamp"] is not null) {
            return (false, "the shell was terminated", false, podPhase, null);
        }

        return podPhase switch {
            "Running" => (true, null, false, podPhase, null),
            "Succeeded" => (false, "the shell exited", true, podPhase, null),
            "Failed" => (false, "the shell stopped: " + (pod?["status"]?["reason"]?.GetValue<string>() ?? "Failed"), true, podPhase, null),
            _ => (false, null, false, podPhase, null)
        };
    }

    // ── Checks ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Whether the caller is the person this session belongs to.</summary>
    /// <remarks>
    ///     ⚠ <b>One answer for "no such session" and "not yours": <see cref="ErrorCode.ResourceNotFound" />.</b>
    ///     Session ids are pod UIDs and are not secret — anybody with <c>connect</c> on the console
    ///     gets the same one back — so the refusal must not become an oracle for whose shells are live.
    /// </remarks>
    Result Mine(CallerContext caller) {
        if (caller.TenantId != tenantId || owner is null || spec is null || !SameSubject(owner, caller)) {
            if (owner is not null && caller.TenantId == tenantId) {
                logger.LogWarning(
                    "Terminal session {Session}: {Caller} tried to reach a session that belongs to {Owner}.",
                    sessionId,
                    caller,
                    owner
                );
            }

            return Result.Failure(
                ErrorCode.ResourceNotFound,
                $"There is no terminal session '{sessionId}' for this caller. Call connect on the "
                + "console for a session id."
            );
        }

        return Result.Success;
    }

    static bool SameSubject(CallerContext a, CallerContext b) =>
        a.TenantId == b.TenantId
        && string.Equals(a.SubjectType, b.SubjectType, StringComparison.Ordinal)
        && string.Equals(a.SubjectId, b.SubjectId, StringComparison.Ordinal);

    void Size(int columns, int rows) {
        // A pane that reports zero cells is a pane not yet laid out; the last good size stands.
        if (columns >= 1 && rows >= 1) {
            this.columns = Math.Min(columns, 1000);
            this.rows = Math.Min(rows, 1000);
        }
    }

    string EndedMessage() =>
        $"This shell has ended ({endedBecause}). Connect again for a new one; the home directory is the same.";

    ITerminalSessionLimitGrain Limit(Guid tenant) =>
        GrainFactory
            .ForTenant(tenant.ToString("D", CultureInfo.InvariantCulture))
            .GetGrain<ITerminalSessionLimitGrain>(TerminalSessionKeys.Limit);

    IKubeClusterConnection? Cluster() => spec is null ? null : clusters.Connect(spec.ClusterId);

    static Result Refuse(ErrorCode code, string message) => Result.Failure(code, message);
}
