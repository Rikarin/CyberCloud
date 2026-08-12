namespace CyberCloud.Identity.Contracts;

/// <summary>
///     The one-time code's numbers — docs/plan/11 § Credentials' <i>"6 digits, 10 min, 5 attempts"</i>
///     — and, in the remarks, <b>which process owns issuing one and why</b>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>ISSUANCE, STORAGE AND VERIFICATION ALL HAPPEN IN <see cref="IUserGrain" />, ON A
///         SILO. THE IDENTITY HOST GENERATES NOTHING, STORES NOTHING AND COMPARES NOTHING.</b> This
///         paragraph is the answer to the question the shape of the code otherwise makes somebody
///         re-derive: the host is where <c>/api/signin/*</c> lives, so putting the code there looks
///         like the short path. It is wrong on all four properties a one-time code has to have, and
///         the grain is right on all four.
///     </para>
///     <list type="number">
///         <item>
///             <b>A code must be verifiable exactly once.</b> "Read the challenge, compare it, delete
///             it" is a read-modify-write, and two requests carrying the same correct code arrive
///             concurrently in exactly the case that matters — a double-submitted form, or an
///             attacker racing the legitimate user. A grain activation is single-threaded per key, so
///             the second call cannot begin until the first has cleared the challenge and its write
///             has returned. In the identity host the same sequence is a lock across N replicas, and
///             a lock across N replicas is a distributed lock nobody has built here.
///             <c>OtpIssuanceTests.ACorrectCodeSucceedsExactlyOnce</c> is the assertion.
///         </item>
///         <item>
///             <b>A code must survive a host restart between issue and verify.</b> There are ten
///             minutes between the two and a deploy takes less than that. The identity host holds no
///             state at all — it is an Orleans <i>client</i> — so anything it kept would be a
///             process-lifetime cache, and a rolling restart, or simply a second replica answering
///             the verify, would lose it. <see cref="IUserGrain" /> is durable and on
///             <c>durable-grains.txt</c>; the challenge is written before the message is handed to
///             <c>CyberCloud.Communication</c>, so the ordering also survives a crash <i>during</i>
///             issuance. <c>OtpIssuanceTests.ACorrectCodeStillSucceedsOnlyOnceAcrossADeactivation</c> covers the cold
///             case, which is the same thing an in-memory cache would fail.
///         </item>
///         <item>
///             <b>The limit must be per user, not per host instance.</b>
///             <see cref="MaxAttempts" /> and <see cref="MaxIssuesPerWindow" /> counted in a host's
///             memory are not limits: an attacker spread across N replicas gets N times the budget,
///             and the platform's own load balancer does the spreading for them. There is exactly one
///             activation of a user's grain in the cluster, so a counter in its state is per user by
///             construction. This is the same argument
///             <c>ILockoutCounter</c> makes for the hot tier and reaches a different answer for a
///             different reason — see the ⚠ below.
///         </item>
///         <item>
///             <b>The code must not be readable by anything that can read grain state.</b> A
///             six-digit code has a million candidates, so a plain SHA-256 of one is not a hash, it
///             is an encoding — a laptop grinds the whole space in milliseconds. What is stored is an
///             <b>HMAC-SHA-256 under the vault-resolved pepper</b> <c>AddCyberCloudIdentity</c>
///             already takes for Argon2id (<c>OtpCodeProtector</c>), so a stolen durable-tier backup
///             is useless without the vault. The plaintext exists in the silo's memory for the
///             duration of one grain call and is never written, never returned to the host and never
///             logged.
///         </item>
///     </list>
///     <para>
///         ⚠ <b>The denial-of-service rule that puts the lockout counter <i>outside</i> a grain does
///         not reach this, and the difference is worth being precise about.</b> docs/plan/11
///         § Credentials calls "an authentication endpoint whose failure path costs a grain
///         activation" a denial-of-service amplifier, which is why <c>ILockoutCounter</c> is a hot-tier
///         <c>INCR</c> keyed by a digest an unauthenticated caller can drive. The amplifier exists
///         because <i>the attacker chooses the address</i>, so they choose which activations the
///         cluster creates. Neither OTP path has that property: a code is issued and redeemed for a
///         user who has <b>already been resolved</b> — through the pending session cookie for a
///         second factor, or through <c>IEmailIndexGrain</c> on a path that already paid for one
///         activation — so the user grain is on the path regardless and no caller-chosen string
///         creates a new one.
///     </para>
///     <para>
///         ⚠ <b>What follows for delivery, and it is the reason <c>AddCommunicationOtpDelivery</c>
///         takes an <c>ISiloBuilder</c> rather than an <c>IServiceCollection</c>.</b>
///         <c>IOtpDeliverySeam</c> is called from inside the grain, which is on a silo, which is
///         exactly the receiver that extension already has. The alternative — return the plaintext to
///         the host and let the host send it — would put a live credential on a grain response, in a
///         second process, for no gain. <c>OtpDeliveryRoute</c>'s two ids are therefore configuration
///         on the <b>silo</b>, and they name the tenant that owns the platform's communication
///         service rather than the tenant the user belongs to. See <c>CommunicationOtpDelivery</c>.
///     </para>
///     <para>
///         ⚠ <b>SMS and WhatsApp are not issuable yet, and the refusal is deliberate rather than
///         missing.</b> docs/plan/11 § Credentials lists all three at M1, but the destination for a
///         code has to be a <i>proven</i> one — sending to a number supplied on the request would let
///         anyone holding a half-authenticated session redirect a second factor to a phone they own.
///         <see cref="IUserGrain" /> holds a verified email address and no verified number, so
///         <see cref="CredentialKind.EmailOtp" /> resolves its destination from grain state and the
///         other two are refused with a sentence saying what is missing. Number enrolment is the
///         owed piece.
///     </para>
/// </remarks>
public static class OtpPolicy {
    /// <summary>Six digits. docs/plan/11 § Credentials.</summary>
    /// <remarks>
    ///     ⚠ <b>Six is small, and the numbers below are what make it safe rather than the length.</b>
    ///     A million candidates falls to a script in seconds if it may guess freely, so the code's
    ///     strength is <see cref="MaxAttempts" /> × <see cref="MaxIssuesPerWindow" /> guesses per
    ///     <see cref="IssueWindow" /> — twenty-five, against a million, for as long as the window
    ///     lasts. Lengthening the code without those counters would be theatre; the counters without
    ///     the code would still work.
    /// </remarks>
    public const int Digits = 6;

    /// <summary>
    ///     How many wrong answers one challenge tolerates before it stops being answerable at all.
    ///     Five — docs/plan/11 § Credentials.
    /// </summary>
    /// <remarks>
    ///     ⚠ The challenge is <b>destroyed</b> on the fifth wrong answer rather than merely refusing
    ///     further ones, so a sixth attempt cannot be answered even by the right code. Leaving a
    ///     burnt challenge in place would make "attempts exhausted" and "wrong code" two states with
    ///     two behaviours, and the difference is observable in whether a later correct code works.
    /// </remarks>
    public const int MaxAttempts = 5;

    /// <summary>
    ///     How many codes one user may be sent inside <see cref="IssueWindow" />.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This is the carrier bill as much as it is the guess budget.</b> An unbounded resend
    ///     button is an unbounded SMS spend against one <c>ISendLimitGrain</c> window that every
    ///     tenant's codes share — <c>CommunicationOtpDelivery</c>'s remarks say what that costs — and
    ///     it is also a way to text somebody once a second for as long as you like.
    /// </remarks>
    public const int MaxIssuesPerWindow = 5;

    /// <summary>Ten minutes. docs/plan/11 § Credentials.</summary>
    /// <remarks>
    ///     Long enough for a mail to route through a slow provider and for a person to switch
    ///     devices, short enough that a code read over a shoulder is stale by the time it is useful.
    /// </remarks>
    public static TimeSpan Lifetime { get; } = TimeSpan.FromMinutes(10);

    /// <summary>The window <see cref="MaxIssuesPerWindow" /> is counted over.</summary>
    /// <remarks>
    ///     Fifteen minutes, matching <c>LockoutPolicy.Window</c>. The two are unrelated mechanisms
    ///     and there is no argument for them to differ, so they do not — an operator reading a
    ///     runbook has one number to remember.
    /// </remarks>
    public static TimeSpan IssueWindow { get; } = TimeSpan.FromMinutes(15);

    /// <summary>
    ///     How long after issuing a code a second issue counts as a <b>retry</b> of the first rather
    ///     than a <b>resend</b>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is the one place the two are told apart, and getting it wrong breaks one of
    ///         them.</b> A caller that timed out waiting for an issue and repeated the call must not
    ///         cause a second SMS; a person who did not receive the first message and pressed
    ///         "resend" must. Nothing in the two requests distinguishes them — that is the whole
    ///         difficulty — so the only available discriminator is how far apart they are.
    ///     </para>
    ///     <para>
    ///         Inside this window the <i>same code</i> is redelivered, which computes the same
    ///         <c>SendRequest.IdempotencyKey</c> and reaches
    ///         <c>IMessageGrain</c>'s existing record, so the carrier is called once. Outside it a
    ///         fresh code is minted, which computes a different key and genuinely sends again. ⚠ Note
    ///         which way round the failure modes fall: too long a window breaks resend, too short a
    ///         one sends twice, and thirty seconds is well past any sane client timeout and well
    ///         inside any human's patience.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Redelivering the same code needs the plaintext, which only the live activation
    ///         has</b> — grain state holds a keyed digest, by property 4 above. So an issue that
    ///         lands after the activation was collected mints a new code even inside this window,
    ///         which sends twice. That is the safe direction (the user gets a code either way) and it
    ///         is rare, because Orleans keeps an activation for its collection age and this window is
    ///         thirty seconds.
    ///     </para>
    /// </remarks>
    public static TimeSpan ResendCooldown { get; } = TimeSpan.FromSeconds(30);
}
