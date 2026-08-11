using System.Collections.Immutable;

namespace CyberCloud.ResourceManager;

/// <summary>
///     Records which steps of docs/plan/08 § The write path, end to end a request entered.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This exists so that step order is a test rather than a review item.</b> The ordering
///         <i>is</i> the security property — <c>Check</c> before quota, quota before the index claim,
///         all three before a provider is called — and docs/plan/08 says so in as many words: <i>"A
///         provider that could skip step 3 is a provider that eventually will."</i> A trace makes the
///         order an assertion that fails when somebody moves a step, including when they move it for
///         a good reason and mean to move it back.
///     </para>
///     <para>
///         ⚠ <b><see cref="Enter" /> refuses to go backwards, and it throws.</b> A step recorded out
///         of order means the write path itself is wrong, in a way that would otherwise show up as a
///         quota reservation taken for a caller who was about to be refused. That is a bug and not a
///         domain outcome, so it fails loudly at the call site rather than producing a trace nobody
///         looks at (docs/plan/00 § Coding standards).
///     </para>
/// </remarks>
sealed class WriteTraceBuilder {
    readonly ImmutableArray<WriteStep>.Builder reached = ImmutableArray.CreateBuilder<WriteStep>(11);

    /// <summary>Records entering a step.</summary>
    /// <param name="step">The step, which must be later than every step already recorded.</param>
    /// <exception cref="InvalidOperationException">
    ///     <paramref name="step" /> is not later than the last one recorded.
    /// </exception>
    public void Enter(WriteStep step) {
        if (reached.Count > 0 && step <= reached[^1]) {
            throw new InvalidOperationException(
                $"The write path entered {step} after {reached[^1]}. The eleven steps of "
                + "docs/plan/08 § The write path, end to end run in order and the order is the "
                + "security property: the ReBAC check is step 3 precisely so that it happens before "
                + "quota is reserved, before a name is claimed and before any provider is reached. A "
                + $"step out of order is a bug. The trace so far is: {string.Join(" → ", reached)}."
            );
        }

        reached.Add(step);
    }

    /// <summary>The trace as it stands.</summary>
    public WriteTrace Build() => new() { Reached = reached.ToImmutable() };
}
