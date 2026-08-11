using CyberCloud.Core.Resources;
using System.Globalization;

namespace CyberCloud.Identity.Contracts;

/// <summary>
///     What a failed-attempt counter is keyed by. Two shapes, and the difference between them is the
///     denial-of-service argument in docs/plan/11 § Credentials.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>DOC DEFECT, and this type is the repair.</b> docs/plan/11 § Credentials says "the
///         lockout counter lives in the hot tier keyed by <b>the user id</b>, so it is a Redis
///         <c>INCR</c>, not a grain call", and immediately explains why that matters: "an
///         authentication endpoint whose failure path costs a grain activation is a denial-of-service
///         amplifier."
///     </para>
///     <para>
///         Those two sentences cannot both be satisfied. A sign-in request carries an <i>email
///         address</i>, and turning an address into a user id means resolving
///         <c>IEmailIndexGrain</c> — which is a grain activation, on the failure path, before the
///         counter keyed by user id can be read. A counter that can only be consulted after the
///         thing it is meant to prevent has already happened prevents nothing.
///     </para>
///     <para>
///         <b>Resolved towards the reason rather than the wording</b>, because the reason is the part
///         with the argument in it. There are two counters:
///     </para>
///     <list type="bullet">
///         <item>
///             <see cref="ForIdentifier" /> — keyed by the same digest the email index key is built
///             from, which is a SHA-256 of <c>(tenant, normalized address)</c> and needs no grain to
///             compute. This is the one the endpoint checks <b>first</b>, and it is what makes the
///             failure path grain-free.
///         </item>
///         <item>
///             <see cref="ForUser" /> — the counter docs/plan/11 actually names, incremented once the
///             user is known. It is what makes "this account is locked" true across every address
///             that resolves to it, which matters after an email change.
///         </item>
///     </list>
///     <para>
///         ⚠ The identifier key reuses <c>GrainKeys.EmailIndex</c>'s digest deliberately. Inventing a
///         second hash of the same pair would be a second normalization rule to keep in step, and
///         <c>GrainKeys.NormalizeEmail</c>'s ASCII-only case folding is exactly the property a
///         lockout key needs — if <c>aK@x</c> and <c>ak@x</c> collapsed here but not in the index,
///         one account's failures would lock out another.
///     </para>
/// </remarks>
public readonly record struct LockoutKey {
    /// <summary>The key's text, as it appears in the hot tier.</summary>
    public string Value { get; }

    LockoutKey(string value) {
        Value = value;
    }

    /// <summary>
    ///     The pre-resolution counter, keyed by what the request actually carried.
    /// </summary>
    /// <param name="tenantId">The tenant the sign-in is scoped to.</param>
    /// <param name="email">The address typed. Normalized here, so casing cannot split a counter.</param>
    /// <remarks>
    ///     ⚠ Returns a key for an address that does not exist, and must. A counter that only existed
    ///     for real accounts would answer "does this account exist" by how fast it answered.
    /// </remarks>
    public static LockoutKey ForIdentifier(Guid tenantId, string email) =>
        new("lockout/id/" + GrainKeys.EmailIndex(tenantId, email)[GrainKeys.EmailIndexPrefix.Length..]);

    /// <summary>The per-account counter docs/plan/11 § Credentials names.</summary>
    /// <param name="userId">The resolved user.</param>
    public static LockoutKey ForUser(Guid userId) =>
        new("lockout/user/" + userId.ToString("N", CultureInfo.InvariantCulture));

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>
///     The failed-attempt counter. docs/plan/11 § Credentials — <b>hot tier, and never a grain</b>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Nothing that implements this may take a grain reference.</b> That is the whole
///         requirement: docs/plan/11 § Credentials calls an authentication endpoint whose failure
///         path costs a grain activation "a denial-of-service amplifier", because an unauthenticated
///         attacker choosing arbitrary addresses would then be choosing which activations the cluster
///         creates. A Redis <c>INCR</c> against a hash-tagged key costs one round trip and creates
///         nothing. <c>LockoutIsGrainFreeTests</c> asserts it against a recording grain factory.
///     </para>
///     <para>
///         <b>Exponential backoff, per docs/plan/11 § Credentials.</b> The window is
///         <see cref="LockoutPolicy" />'s and the delay grows with the count, so a human who mistypes
///         twice notices nothing and a script is quickly answering one request per minute.
///     </para>
/// </remarks>
public interface ILockoutCounter {
    /// <summary>
    ///     Whether this key is currently locked out, without changing anything.
    /// </summary>
    /// <param name="key">Which counter.</param>
    /// <param name="cancellationToken">Cancels the round trip to the hot tier.</param>
    /// <returns><c>true</c> when the caller must be refused before any further work.</returns>
    Task<bool> IsLockedAsync(LockoutKey key, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Records a failure and returns the new count. The <c>INCR</c>.
    /// </summary>
    /// <param name="key">Which counter.</param>
    /// <param name="cancellationToken">Cancels the round trip to the hot tier.</param>
    Task<int> RecordFailureAsync(LockoutKey key, CancellationToken cancellationToken = default);

    /// <summary>Clears a counter after a successful authentication.</summary>
    /// <param name="key">Which counter.</param>
    /// <param name="cancellationToken">Cancels the round trip to the hot tier.</param>
    Task ResetAsync(LockoutKey key, CancellationToken cancellationToken = default);
}

/// <summary>The lockout ladder. docs/plan/11 § Credentials — "per-account exponential backoff".</summary>
public static class LockoutPolicy {
    /// <summary>How many failures are free before the ladder starts.</summary>
    /// <remarks>
    ///     Five, because a person who has genuinely forgotten which of their passwords it is will
    ///     spend three or four attempts finding out, and locking them out for that is a support
    ///     ticket rather than a defence.
    /// </remarks>
    public const int FreeAttempts = 5;

    /// <summary>How long a counter survives with no further failures.</summary>
    public static TimeSpan Window { get; } = TimeSpan.FromMinutes(15);

    /// <summary>The longest a single lockout lasts, however many failures there have been.</summary>
    /// <remarks>
    ///     ⚠ Capped rather than unbounded, and the cap is what stops the lockout from becoming the
    ///     attack: an unbounded ladder lets anyone who knows an address deny its owner their account
    ///     indefinitely for the cost of a few requests a day.
    /// </remarks>
    public static TimeSpan MaximumDelay { get; } = TimeSpan.FromMinutes(30);

    /// <summary>
    ///     How long to refuse after <paramref name="failures" /> failures.
    /// </summary>
    /// <param name="failures">The count the counter reported.</param>
    /// <returns>
    ///     <see cref="TimeSpan.Zero" /> up to <see cref="FreeAttempts" />, then doubling from one
    ///     second, capped at <see cref="MaximumDelay" />.
    /// </returns>
    public static TimeSpan DelayFor(int failures) {
        if (failures <= FreeAttempts) {
            return TimeSpan.Zero;
        }

        // ⚠ The shift is bounded before it happens, not after. `1 << 40` on an int is not a large
        // number, it is `1 << 8` — C# masks the shift count to five bits — so an unbounded exponent
        // wraps round to a *short* delay at exactly the point the delay should be longest.
        var steps = Math.Min(failures - FreeAttempts, 16);
        var seconds = 1L << (steps - 1);

        var delay = TimeSpan.FromSeconds(seconds);
        return delay > MaximumDelay ? MaximumDelay : delay;
    }
}
