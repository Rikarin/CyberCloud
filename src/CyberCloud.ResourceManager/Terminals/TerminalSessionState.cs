namespace CyberCloud.ResourceManager.Terminals;

/// <summary>
///     The hot-tier state of an <see cref="ITerminalSessionGrain" />: whose session it is, and whether
///     it's over.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Found by the second review of #22: without it, a lost activation gave the shell away.</b>
///         The owner lived only in the activation. A silo restart, a rolling deploy or a rebalance left
///         the pod running under the same UID, the next <c>connect</c> in the tenant registered that
///         UID with a fresh activation, and the fresh activation bound whoever called first. That was
///         the same bash process, holding the owner's in-memory state, now belonging to a colleague who
///         held <c>connect</c> on the console.
///     </para>
///     <para>
///         Hot and not durable: a session is session-shaped state, and docs/plan/05 § Hot lists terminal
///         sessions by name. What a hot-tier loss would give away,
///         <see cref="TerminalSessionSpec.OwnerAnnotation" /> keeps: the grain reads the pod's stamp
///         before it binds anybody it has no record of.
///     </para>
///     <para>
///         Cleared once the session has ended and its pod is known to be gone. A pod's UID is never
///         reused, so nothing can name the session again. An ended session whose pod couldn't be deleted
///         keeps its record, so the next activation still refuses to re-open it.
///     </para>
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.ResourceManager.State.TerminalSession")]
public sealed class TerminalSessionState {
    /// <summary>The person the session belongs to, or <see langword="null" /> before anybody bound it.</summary>
    [Id(0)]
    public CallerContext? Owner { get; set; }

    /// <summary>Whether the session has ended. It never comes back from that.</summary>
    [Id(1)]
    public bool Ended { get; set; }

    /// <summary>Why it ended, for the refusal the next <c>connect</c> gets. Empty while it hasn't.</summary>
    [Id(2)]
    public string EndedBecause { get; set; } = string.Empty;
}
