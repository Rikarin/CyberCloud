using System.Globalization;

namespace CyberCloud.ResourceManager.Contracts;

// ── The terminal session — docs/plan/19 § Architecture ─────────────────────────────────────────
//
// ⚠ THE PLATFORM'S AND NOT THE PROVIDER'S, AND docs/plan/03 § Assembly graph rules, RULE 8, IS WHY.
// A session re-asks ReBAC on every attach and holds a cluster stream outside any reconcile pass — so
// its grain needs IResourceAuthorizer and IClusterConnectionFactory, the two seams rule 8 forbids a
// Providers.* assembly to name, and the Architecture gate refuses the grain in
// CyberCloud.Providers.Terminal on exactly those two types. So the grain lives beside ConnectionGrain,
// which is here for the same reason (it needs the enforcement seam), and a provider reaches it the way
// listInstallCommand reaches the agent tunnel: through a seam on ActionContext —
// ActionContext.Terminals. Nothing below knows what a console is; the provider fills the spec.

/// <summary>
///     What a session grain has to know about the shell it serves, handed over by the action that
///     started it.
/// </summary>
/// <remarks>
///     ⚠ <b>Everything here is a fact the action already holds</b> — the resource's address, the pod
///     it just applied and read back, the permission a person needs to attach, and the idle timeout
///     from the body. The grain is told rather than left to look them up, and it is told in
///     Kubernetes terms rather than in a provider's, so one grain serves any resource whose product is
///     a shell in a pod.
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.ResourceManager.TerminalSessionSpec")]
public sealed record TerminalSessionSpec {
    /// <summary>The resource the session belongs to, with its GUID resolved — what the ReBAC check is made on.</summary>
    [Id(0)]
    public ResourceId Resource { get; init; }

    /// <summary>The api-version the resource was reached at — what a reclaim's delete is labeled with.</summary>
    [Id(1)]
    public string ApiVersion { get; init; } = string.Empty;

    /// <summary>The cluster the pod runs in.</summary>
    [Id(2)]
    public Guid ClusterId { get; init; }

    /// <summary>The pod — a core <c>v1</c> <c>Pod</c> in the resource's namespace.</summary>
    [Id(3)]
    public ObjectRef Pod { get; init; } = new();

    /// <summary>The container whose process the session attaches to.</summary>
    [Id(4)]
    public string Container { get; init; } = string.Empty;

    /// <summary>The pod's UID — the session id, and how a re-created pod is told apart from this one.</summary>
    [Id(5)]
    public string PodUid { get; init; } = string.Empty;

    /// <summary>How long the shell may go without a keystroke before it is reclaimed, in seconds.</summary>
    [Id(6)]
    public int IdleTimeoutSeconds { get; init; }

    /// <summary>The permission an attach re-checks on <see cref="Resource" /> — the action's own, for example <c>connect</c>.</summary>
    [Id(7)]
    public string Permission { get; init; } = string.Empty;

    /// <summary>The resource type's read permission, which decides between a <c>404</c> and a <c>403</c> when <see cref="Permission" /> is refused.</summary>
    [Id(8)]
    public string ReadPermission { get; init; } = "read";

    /// <inheritdoc />
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Resource.Path} (pod {PodUid}, idle {IdleTimeoutSeconds}s)");
}

/// <summary>
///     Where an action registers the terminal session it started — <see cref="ActionContext.Terminals" />.
/// </summary>
/// <remarks>
///     The action-side face of <see cref="ITerminalSessionGrain" />, for the reason
///     <see cref="ActionContext.Agents" /> exists: a provider may not reach the manager's grains
///     (docs/plan/03 § Assembly graph rules, rule 8), and a seam on the context is how the platform
///     lends it one call. The default refuses by name.
/// </remarks>
public interface ITerminalSessions {
    /// <summary>Registers a session, bound to the person who started it.</summary>
    /// <param name="spec">The resource, the pod and the idle timeout.</param>
    /// <param name="owner">The action's caller — <see cref="ActionContext.Caller" />.</param>
    /// <param name="cancellationToken">Stops the call.</param>
    /// <returns>What <see cref="ITerminalSessionGrain.OpenAsync" /> answered.</returns>
    Task<Result> OpenAsync(TerminalSessionSpec spec, CallerContext owner, CancellationToken cancellationToken = default);
}

/// <summary>The <see cref="ITerminalSessions" /> a context carries when nobody supplied one.</summary>
/// <remarks>
///     Refuses by name, as <c>UnavailableAgentTunnels</c> does: a shell whose session was never
///     registered is a pod nobody can type into and nothing will reclaim.
/// </remarks>
public sealed class UnavailableTerminalSessions : ITerminalSessions {
    /// <inheritdoc />
    public Task<Result> OpenAsync(TerminalSessionSpec spec, CallerContext owner, CancellationToken cancellationToken = default) =>
        Task.FromResult(
            Result.Failure(
                ErrorCode.InternalError,
                $"This host has no ITerminalSessions, so the session for '{spec?.Resource.Path}' cannot be "
                + "registered and nothing could attach to it. AddCyberCloudResourceManager registers the "
                + "grain-backed one; a dispatcher composed by hand has to pass it."
            )
        );
}

/// <summary>Where a session is in its life.</summary>
/// <remarks>
///     ⚠ A field of <see cref="TerminalSessionStatus" />, so it crosses the gateway→silo boundary and
///     carries a stable alias like every enum in <c>ResourceManagerEnums.cs</c>.
/// </remarks>
[Alias("CyberCloud.ResourceManager.TerminalSessionPhase")]
public enum TerminalSessionPhase {
    /// <summary>Nothing has registered this session — no <c>connect</c> named it.</summary>
    Unknown = 0,

    /// <summary>
    ///     <c>connect</c> named it and the stream isn't open: nobody has attached yet, the stream is
    ///     being opened, or the last attempt failed and the next attach or keystroke tries again.
    /// </summary>
    Registered = 1,

    /// <summary>The attach stream is open and output is flowing.</summary>
    Open = 2,

    /// <summary>
    ///     The shell is over — it exited, it was reclaimed after sitting idle, it never started, it hit
    ///     its hard cap, or the pod was terminated. Only the pod decides this; failing to reach the pod
    ///     leaves a session <see cref="Registered" />. A session never comes back from here, and the
    ///     next <c>connect</c> starts a new pod, so a new session id, removing this one's pod if it
    ///     still stands.
    /// </summary>
    Ended = 3
}

/// <summary>A session as its grain sees it — for an operator, a test and the idle sweep's log.</summary>
[GenerateSerializer]
[Alias("CyberCloud.ResourceManager.TerminalSessionStatus")]
public sealed record TerminalSessionStatus {
    /// <summary>Where the session is.</summary>
    [Id(0)]
    public TerminalSessionPhase Phase { get; init; }

    /// <summary>Who the session belongs to, as <c>{subjectType}:{subjectId}</c>, or empty before <c>connect</c>.</summary>
    [Id(1)]
    public string Owner { get; init; } = string.Empty;

    /// <summary>How many sockets are watching right now.</summary>
    [Id(2)]
    public int Viewers { get; init; }

    /// <summary>How many bytes the replay ring holds.</summary>
    [Id(3)]
    public int Buffered { get; init; }

    /// <summary>Why the session ended, or empty while it has not.</summary>
    [Id(4)]
    public string EndedBecause { get; init; } = string.Empty;
}

/// <summary>
///     One pane's socket, as a session grain sees it: where the shell's output goes.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             AN OBSERVER AND NOT AN ORLEANS STREAM, AND THE CHOICE IS THE SHAPE OF A TERMINAL.
///         </b> docs/plan/10 § SignalR routes the terminal "direct to the session grain — binary, no
///         backplane". A stream would put a stream provider and a pub-sub registration between one
///         producer and one consumer, and the three live-update hubs need that fan-out and a terminal
///         does not. An observer is a direct call from the grain to the gateway pod holding the
///         socket, and three properties a terminal needs follow from it:
///     </para>
///     <list type="bullet">
///         <item>
///             <b>Order.</b> The grain awaits each delivery before the next, so bytes arrive in the
///             order the shell printed them — an escape sequence split across two chunks and
///             reordered is a corrupted screen, not a late one.
///         </item>
///         <item>
///             <b>Backpressure.</b> A slow socket slows the grain's reads, which slows the stream
///             from the kubelet; nothing buffers without bound between the two.
///         </item>
///         <item>
///             <b>Liveness.</b> A delivery to a gateway pod that died fails at once, and the grain
///             drops the viewer. A stream subscription from a dead pod lingers until it is cleaned up.
///         </item>
///     </list>
///     <para>
///         What it costs: a viewer is gone when its gateway pod is, so a reconnect is a fresh
///         <c>Attach</c> — which is what the portal does anyway, because its ticket is single-use.
///     </para>
/// </remarks>
[Alias("Rm.TerminalViewer")]
public interface ITerminalViewer : IGrainObserver {
    /// <summary>Delivers bytes the shell printed, in order.</summary>
    /// <param name="data">Raw terminal output — escape sequences and all.</param>
    [Alias("Output")]
    Task OutputAsync(byte[] data);

    /// <summary>Says the session is over, so the pane stops rather than reconnects into a new pod.</summary>
    /// <param name="reason">A sentence for the person looking at the pane.</param>
    [Alias("Ended")]
    Task EndedAsync(string reason);
}

/// <summary>
///     One cloud-terminal session: the attach stream to the resource's pod, the replay ring, the
///     resize channel and the idle timer. docs/plan/19 § Architecture.
/// </summary>
/// <remarks>
///     <para>
///         <b>Kind</b> Session · <b>Tier</b> <b>none</b> · <b>Key</b> <c>terminal/{sessionId}</c>,
///         tenant-qualified. Build it with <see cref="TerminalSessionKeys.Session" />.
///     </para>
///     <para>
///         ⚠ <b>Owned by the person who opened it.</b> <c>connect</c> binds the session to its
///         caller; every <see cref="AttachAsync" />, <see cref="SendAsync" /> and
///         <see cref="ResizeAsync" /> must come from that same subject, and an attach additionally
///         re-checks <see cref="TerminalSessionSpec.Permission" /> on the resource through ReBAC, fully consistent,
///         so a revoked role stops the next attach rather than the next deploy. Another tenant never
///         reaches the grain at all: the key is tenant-qualified and the hub takes it from the token.
///     </para>
///     <para>
///         ⚠ <b>No storage, and docs/plan/19's "hot tier" is answered by the pod.</b> A lost
///         activation loses the replay ring and the open stream — both of which are reconnect-shaped
///         — and nothing else: the pod is still running, and the portal's reconnect is
///         <c>connect</c> again, which re-registers the same session id and re-binds its caller.
///         What is lost with it is the idle clock, and the pod's own hard cap
///         (<c>activeDeadlineSeconds</c>) is the kubelet's and still holds;
///         <c>charts/managed/cloud-shell/conformance.yaml § owed</c> records the sweeper that would
///         close the gap.
///     </para>
/// </remarks>
[Alias("Rm.TerminalSession")]
public interface ITerminalSessionGrain : IGrainWithStringKey {
    /// <summary>
    ///     Registers the session <c>connect</c> just started or re-joined, bound to whoever called
    ///     <c>connect</c>.
    /// </summary>
    /// <param name="spec">The resource, the pod and the idle timeout.</param>
    /// <param name="owner">The caller of <c>connect</c>.</param>
    /// <returns>
    ///     Success, including for the owner re-registering a live session. <see cref="ErrorCode.Conflict" />
    ///     when another person already holds it, and <see cref="ErrorCode.PreconditionFailed" /> when
    ///     it has ended. ⚠ An ended session can't be re-joined, and its pod may still stand, so the
    ///     caller of <c>connect</c> answers this by deleting the pod and registering the new one's
    ///     UID. Otherwise every later <c>connect</c> would name the same ended session.
    /// </returns>
    [Alias("Open")]
    Task<Result> OpenAsync(TerminalSessionSpec spec, CallerContext owner);

    /// <summary>
    ///     Joins a pane to the session: replays the ring into it, then streams live output until it
    ///     detaches.
    /// </summary>
    /// <param name="caller">Who the pane belongs to, from the hub's token.</param>
    /// <param name="viewer">Where the output goes.</param>
    /// <param name="columns">The pane's width in cells.</param>
    /// <param name="rows">The pane's height in cells.</param>
    /// <returns>
    ///     Success once the replay has been delivered. <see cref="ErrorCode.ResourceNotFound" /> for a
    ///     session nobody registered and for one registered to another person — one answer, so the hub
    ///     isn't an oracle for whose sessions exist. For the owner after losing
    ///     <see cref="TerminalSessionSpec.Permission" />, the enforcement seam's own refusal:
    ///     <see cref="ErrorCode.ResourceNotFound" /> without
    ///     <see cref="TerminalSessionSpec.ReadPermission" />, <see cref="ErrorCode.AuthorizationFailed" />
    ///     with it — the resource's rule, since the owner knows the session exists.
    ///     <see cref="ErrorCode.PreconditionFailed" /> for one that has ended.
    /// </returns>
    [Alias("Attach")]
    Task<Result> AttachAsync(CallerContext caller, ITerminalViewer viewer, int columns, int rows);

    /// <summary>Sends keystrokes to the shell, and resets the idle clock.</summary>
    /// <param name="caller">Who typed them.</param>
    /// <param name="data">The bytes, unmodified.</param>
    [Alias("Send")]
    Task<Result> SendAsync(CallerContext caller, byte[] data);

    /// <summary>Tells the shell its window changed size.</summary>
    /// <param name="caller">Who resized.</param>
    /// <param name="columns">The width in cells.</param>
    /// <param name="rows">The height in cells.</param>
    [Alias("Resize")]
    Task<Result> ResizeAsync(CallerContext caller, int columns, int rows);

    /// <summary>Removes a pane. Removing one that was never attached is success.</summary>
    /// <param name="viewer">The observer reference <see cref="AttachAsync" /> was given.</param>
    /// <remarks>
    ///     ⚠ Detaching does not end the session. The shell keeps running — that is what reconnect is
    ///     for — until it exits, idles out or is terminated.
    /// </remarks>
    [Alias("Detach")]
    Task DetachAsync(ITerminalViewer viewer);

    /// <summary>Where the session is.</summary>
    [Alias("Status")]
    Task<TerminalSessionStatus> StatusAsync();
}

/// <summary>
///     How many shells one tenant may have running at once, across every console and every person.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Counted in live sessions, not in consoles, because a session is what costs.</b> A
///         console with no shell is a home volume and two objects; a session is a pod with up to two
///         vCPU and four GiB. docs/plan/19 § The pod calls idle cost the design constraint, and a
///         tenant that opens a hundred tabs is the same cost whether it has one console or a hundred.
///     </para>
///     <para>
///         ⚠ <b>Leased, so a lost activation cannot hold a slot forever.</b> Each session renews its
///         slot on its idle tick. A session grain that vanished with its silo stops renewing, and its
///         slot lapses after <see cref="TerminalSessionLimits.Lease" /> — the same answer the session
///         grain gives to the pod it leaves behind: the pod runs to its hard cap, the slot does not.
///     </para>
///     <para>
///         <b>Kind</b> Session · <b>Tier</b> <b>none</b> · <b>Key</b> <see cref="TerminalSessionKeys.Limit" />,
///         tenant-qualified: one per tenant.
///     </para>
/// </remarks>
[Alias("Rm.TerminalSessionLimit")]
public interface ITerminalSessionLimitGrain : IGrainWithStringKey {
    /// <summary>Takes a slot for a session, or renews the one it holds.</summary>
    /// <param name="sessionId">The session.</param>
    /// <returns>
    ///     Success, or <see cref="ErrorCode.QuotaExceeded" /> when the tenant already
    ///     has <see cref="TerminalSessionLimits.LiveSessionsPerTenant" /> other live sessions.
    /// </returns>
    [Alias("Admit")]
    Task<Result> AdmitAsync(string sessionId);

    /// <summary>Gives a slot back. Releasing one that is not held is success.</summary>
    /// <param name="sessionId">The session.</param>
    [Alias("Release")]
    Task ReleaseAsync(string sessionId);

    /// <summary>How many slots are held and unexpired now.</summary>
    [Alias("Live")]
    Task<int> LiveAsync();
}

/// <summary>The numbers <see cref="ITerminalSessionLimitGrain" /> enforces.</summary>
public static class TerminalSessionLimits {
    /// <summary>The most shells one tenant may have running at once.</summary>
    /// <remarks>
    ///     ⚠ A constant, not a quota. docs/plan/22's quota grain reserves from a resource body at write
    ///     time, and a session is not a write — <c>charts/managed/cloud-shell/conformance.yaml § owed</c>,
    ///     <c>session-hours-are-not-a-state-meter</c>, is the same gap from the billing side. Ten is a
    ///     team's worth of people each with a tab open.
    /// </remarks>
    public const int LiveSessionsPerTenant = 10;

    /// <summary>How long a slot survives without its session renewing it.</summary>
    /// <remarks>Eight of the session grain's fifteen-second idle ticks, so a slow tick never loses a live slot.</remarks>
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);
}

/// <summary>The session grain's key.</summary>
/// <remarks>
///     ⚠ <b>Not a <c>GrainKeys</c> kind, for <c>ConnectionGrainKeys</c>' reason.</b> A session is an
///     ephemeral thing with no place in docs/plan/06's hierarchy — it is a pod's UID, gone when the pod
///     is — and giving it a <c>GrainKeyKind</c> would put it in the parser that validates subscription
///     and resource ids.
/// </remarks>
public static class TerminalSessionKeys {
    /// <summary>The key prefix.</summary>
    public const string Prefix = "terminal/";

    /// <summary><see cref="ITerminalSessionLimitGrain" />'s key — one per tenant, so the tenant qualification is the whole identity.</summary>
    public const string Limit = "terminal-sessions";

    /// <summary><c>terminal/{sessionId}</c> — <see cref="ITerminalSessionGrain" />, always tenant-qualified.</summary>
    /// <param name="sessionId">
    ///     The session id <c>connect</c> returned: the shell pod's UID. ⚠ Caller-supplied on the hub,
    ///     so it is checked — a UID is 36 characters of hex and hyphens, and anything else is refused
    ///     rather than escaped.
    /// </param>
    /// <exception cref="ArgumentException">The id is not a Kubernetes UID.</exception>
    public static string Session(string sessionId) {
        if (!IsSessionId(sessionId)) {
            throw new ArgumentException(
                $"'{sessionId}' is not a session id. A session id is the shell pod's UID, as connect "
                + "returned it.",
                nameof(sessionId)
            );
        }

        return Prefix + sessionId;
    }

    /// <summary>Whether a string has the shape of a pod UID.</summary>
    /// <param name="sessionId">The candidate.</param>
    /// <returns><c>true</c> for a GUID in its hyphenated lower-case form, which is how the API server writes a UID.</returns>
    public static bool IsSessionId(string? sessionId) =>
        sessionId is { Length: 36 }
        && Guid.TryParseExact(sessionId, "D", out var parsed)
        && string.Equals(parsed.ToString("D"), sessionId, StringComparison.Ordinal);
}
