using CyberCloud.Communication;
using CyberCloud.Communication.Contracts;
using CyberCloud.Communication.Providers;
using CyberCloud.Core.Contracts;
using CyberCloud.Core.Resources;
using CyberCloud.Core.Time;
using CyberCloud.Identity.Contracts;
using CyberCloud.Identity.Credentials;
using CyberCloud.Identity.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Multitenant;
using Orleans.Runtime;
using Orleans.Storage;
using Orleans.TestingHost;
using System.Globalization;
using System.Text.Json;

namespace CyberCloud.Identity.Tests;

/// <summary>
///     Issuing, delivering and redeeming a one-time code — docs/plan/11 § Credentials' <i>"6 digits,
///     10 min, 5 attempts"</i>, against a silo wired the way <c>CyberCloud.Silo.Host</c> wires one.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THE SUITE IS ORGANISED BY FAILURE CLASS RATHER THAN BY METHOD</b>, because every one
///         of these is a defect that ships silently and is discovered by a customer:
///     </para>
///     <list type="bullet">
///         <item>
///             <b>a retry sends twice</b> — <see cref="ARetriedIssueReachesTheCarrierOnce" />, and
///             its opposite <see cref="AResendAfterTheCooldownIsASecondMessage" />, which is what a
///             key that over-collapses would break;
///         </item>
///         <item>
///             <b>a code verifies twice</b> — <see cref="ACorrectCodeSucceedsExactlyOnce" /> and
///             <see cref="ACorrectCodeStillSucceedsOnlyOnceAcrossADeactivation" />, the second being
///             the one an in-memory challenge would fail;
///         </item>
///         <item>
///             <b>the answer says which thing went wrong</b> —
///             <see cref="EveryWayOfBeingWrongProducesTheIdenticalAnswer" />;
///         </item>
///         <item>
///             <b>the code reaches somewhere it should not</b> —
///             <see cref="TheCodeIsNowhereInGrainState" /> and
///             <see cref="TheCodeReachesNoLogMessage" />;
///         </item>
///         <item>
///             <b>the limit is per process rather than per user</b> —
///             <see cref="TheIssueLimitIsPerUserAndSurvivesADeactivation" />.
///         </item>
///     </list>
///     <para>
///         ⚠ <b>The real sending domain, not a double for <c>IMessageSender</c>.</b> The idempotency
///         guarantee lives in <c>IMessageGrain</c>, so a stub in front of it would leave every
///         assertion here measuring this suite's own bookkeeping. What <i>is</i> a double is
///         <see cref="InMemoryChannelProvider" />, standing where a carrier would — the same
///         arrangement <c>OtpDeliveryTests</c> and <c>CyberCloud.Communication.Tests</c> use.
///     </para>
/// </remarks>
[Collection(OtpIssuanceSuite.Name)]
public sealed class OtpIssuanceTests(OtpIssuanceCluster cluster) {
    // ── FAILURE CLASS: a retry sends twice ─────────────────────────────────────────────────────

    [Fact]
    public async Task ARetriedIssueReachesTheCarrierOnce() {
        var user = await cluster.NewUserAsync();

        // ⚠ The realistic shape: the caller timed out waiting for the first call and repeated it
        // verbatim. Nothing about the second call says "this is a retry" — that is the whole
        // difficulty, and OtpPolicy.ResendCooldown is the only discriminator available.
        (await cluster.User(user).IssueOtpAsync(OtpPurpose.SignIn, CredentialKind.EmailOtp))
            .IsSuccess.ShouldBeTrue();

        (await cluster.User(user).IssueOtpAsync(OtpPurpose.SignIn, CredentialKind.EmailOtp))
            .IsSuccess.ShouldBeTrue();

        cluster.Email.Calls.ShouldBe(
            1,
            "the same code redelivered computes the same SendRequest.IdempotencyKey, so IMessageGrain "
            + "recognises the second call as a retry and sends nothing. A key derived from the "
            + "attempt — Guid.NewGuid() being the obvious way to write one — sends a second message "
            + "here and passes every test that only ever issues once."
        );

        // ⚠ And the SAME code, not merely one message. A grain that minted a second code and then
        // failed to deliver it would also leave one carrier call, and would leave the user holding a
        // code the platform had already replaced.
        cluster.Codes.Distinct(StringComparer.Ordinal).Count().ShouldBe(1);
    }

    [Fact]
    public async Task AResendAfterTheCooldownIsASecondMessage() {
        // ⚠ THE MORE IMPORTANT HALF. A duplicate message is a support call; a swallowed one is a
        // person who cannot sign in while the platform reports that everything worked. This is the
        // direction a key that over-collapses — a clock window, or reusing the live code forever —
        // gets wrong.
        var user = await cluster.NewUserAsync();

        (await cluster.User(user).IssueOtpAsync(OtpPurpose.SignIn, CredentialKind.EmailOtp))
            .IsSuccess.ShouldBeTrue();

        cluster.Clock.Advance(OtpPolicy.ResendCooldown + TimeSpan.FromSeconds(1));

        (await cluster.User(user).IssueOtpAsync(OtpPurpose.SignIn, CredentialKind.EmailOtp))
            .IsSuccess.ShouldBeTrue();

        cluster.Email.Calls.ShouldBe(2, "a person who did not receive the first code pressed resend");
        cluster.Codes.Distinct(StringComparer.Ordinal).Count().ShouldBe(2);
    }

    [Fact]
    public async Task AResendInvalidatesTheCodeItReplaces() {
        // At most one challenge per purpose. Leaving the old one answerable would double the guess
        // budget every time somebody pressed resend.
        var user = await cluster.NewUserAsync();

        (await cluster.User(user).IssueOtpAsync(OtpPurpose.SignIn, CredentialKind.EmailOtp))
            .IsSuccess.ShouldBeTrue();

        var first = cluster.LastCode;

        cluster.Clock.Advance(OtpPolicy.ResendCooldown + TimeSpan.FromSeconds(1));

        (await cluster.User(user).IssueOtpAsync(OtpPurpose.SignIn, CredentialKind.EmailOtp))
            .IsSuccess.ShouldBeTrue();

        var second = cluster.LastCode;
        first.ShouldNotBe(second);

        (await cluster.User(user).RedeemOtpAsync(OtpPurpose.SignIn, first))
            .GetValueOrThrow()
            .ShouldBeFalse("the superseded code must be dead the moment its replacement is issued");

        (await cluster.User(user).RedeemOtpAsync(OtpPurpose.SignIn, second)).GetValueOrThrow().ShouldBeTrue();
    }

    // ── FAILURE CLASS: a code that verifies twice ──────────────────────────────────────────────

    [Fact]
    public async Task ACorrectCodeSucceedsExactlyOnce() {
        var user = await cluster.NewUserAsync();

        (await cluster.User(user).IssueOtpAsync(OtpPurpose.SignIn, CredentialKind.EmailOtp))
            .IsSuccess.ShouldBeTrue();

        var code = cluster.LastCode;

        (await cluster.User(user).RedeemOtpAsync(OtpPurpose.SignIn, code)).GetValueOrThrow().ShouldBeTrue();

        (await cluster.User(user).RedeemOtpAsync(OtpPurpose.SignIn, code))
            .GetValueOrThrow()
            .ShouldBeFalse(
                "a one-time code is one-time. The challenge is removed before the redeeming call's "
                + "state write returns, and the grain is single-threaded, so there is no window for "
                + "a second presentation — OtpPolicy property 1."
            );
    }

    [Fact]
    public async Task ACorrectCodeStillSucceedsOnlyOnceAcrossADeactivation() {
        // ⚠ THE ROW AN IN-MEMORY CHALLENGE WOULD FAIL, AND THE ONE THE IDENTITY HOST COULD NEVER
        // PASS. A code lives ten minutes and a deploy takes less than that, so "the process that
        // issued it is still running when it is redeemed" is not an assumption available to anybody.
        var user = await cluster.NewUserAsync();

        (await cluster.User(user).IssueOtpAsync(OtpPurpose.SignIn, CredentialKind.EmailOtp))
            .IsSuccess.ShouldBeTrue();

        var code = cluster.LastCode;

        await cluster.User(user).DeactivateAsync();
        (await cluster.User(user).RedeemOtpAsync(OtpPurpose.SignIn, code)).GetValueOrThrow().ShouldBeTrue();

        await cluster.User(user).DeactivateAsync();

        (await cluster.User(user).RedeemOtpAsync(OtpPurpose.SignIn, code))
            .GetValueOrThrow()
            .ShouldBeFalse("the burn is durable, not a fact the activation was holding");
    }

    [Fact]
    public async Task TwoPurposesAreTwoChallengesAndDoNotBurnEachOther() {
        var user = await cluster.NewUserAsync();

        (await cluster.User(user).IssueOtpAsync(OtpPurpose.SignIn, CredentialKind.EmailOtp))
            .IsSuccess.ShouldBeTrue();

        var signIn = cluster.LastCode;

        (await cluster.User(user).IssueOtpAsync(OtpPurpose.StepUp, CredentialKind.EmailOtp))
            .IsSuccess.ShouldBeTrue();

        var stepUp = cluster.LastCode;

        // ⚠ A code is bound to its purpose inside the MAC, so presenting a sign-in code as a step-up
        // answer fails even though both are live and both belong to this user.
        (await cluster.User(user).RedeemOtpAsync(OtpPurpose.StepUp, signIn)).GetValueOrThrow().ShouldBeFalse();
        (await cluster.User(user).RedeemOtpAsync(OtpPurpose.SignIn, signIn)).GetValueOrThrow().ShouldBeTrue();
        (await cluster.User(user).RedeemOtpAsync(OtpPurpose.StepUp, stepUp)).GetValueOrThrow().ShouldBeTrue();
    }

    // ── FAILURE CLASS: the answer says which thing went wrong ──────────────────────────────────

    [Fact]
    public async Task EveryWayOfBeingWrongProducesTheIdenticalAnswer() {
        var user = await cluster.NewUserAsync();
        var never = await cluster.NewUserAsync();

        (await cluster.User(user).IssueOtpAsync(OtpPurpose.SignIn, CredentialKind.EmailOtp))
            .IsSuccess.ShouldBeTrue();

        var code = cluster.LastCode;

        var wrong = await cluster.User(user).RedeemOtpAsync(OtpPurpose.SignIn, Wrong(code));
        var neverIssued = await cluster.User(never).RedeemOtpAsync(OtpPurpose.SignIn, code);
        var noSuchUser = await cluster.User(Guid.NewGuid()).RedeemOtpAsync(OtpPurpose.SignIn, code);

        cluster.Clock.Advance(OtpPolicy.Lifetime + TimeSpan.FromSeconds(1));
        var expired = await cluster.User(user).RedeemOtpAsync(OtpPurpose.SignIn, code);

        // ⚠ Four different facts and one answer. A caller that could separate them would learn
        // whether a code is outstanding for an account, whether the account exists at all, and how
        // many guesses are left — which is the whole of what an attacker holding a stolen mailbox
        // would want to know before spending their attempts.
        foreach (var (name, result) in new[] {
                     ("a wrong code", wrong),
                     ("a code for another user's challenge", neverIssued),
                     ("a code for an account that does not exist", noSuchUser),
                     ("an expired code", expired)
                 }) {
            result.IsSuccess.ShouldBeTrue(name + " must not be an error, which is itself an oracle");
            result.GetValueOrThrow().ShouldBeFalse(name);
        }
    }

    [Fact]
    public async Task TheAttemptBudgetIsFiveAndTheSixthCannotSucceedEvenWithTheRightCode() {
        var user = await cluster.NewUserAsync();

        (await cluster.User(user).IssueOtpAsync(OtpPurpose.SignIn, CredentialKind.EmailOtp))
            .IsSuccess.ShouldBeTrue();

        var code = cluster.LastCode;

        for (var i = 0; i < OtpPolicy.MaxAttempts; i++) {
            (await cluster.User(user).RedeemOtpAsync(OtpPurpose.SignIn, Wrong(code)))
                .GetValueOrThrow()
                .ShouldBeFalse();
        }

        // ⚠ docs/plan/11 § Credentials says "5 attempts", and the challenge is DESTROYED on the
        // fifth rather than merely refusing further ones. Leaving it in place would make "attempts
        // exhausted" and "wrong code" two states whose difference is observable in whether a later
        // correct answer works — and six digits is a million candidates, so the budget IS the
        // strength of the credential.
        (await cluster.User(user).RedeemOtpAsync(OtpPurpose.SignIn, code))
            .GetValueOrThrow()
            .ShouldBeFalse("the sixth answer must fail even when it is right");
    }

    // ── FAILURE CLASS: the code reaches somewhere it should not ────────────────────────────────

    [Fact]
    public async Task TheCodeIsNowhereInGrainState() {
        // ⚠ CC1005 DOES NOT COVER THIS AND WOULD NOT HAVE. The analyzer bans [Id] members whose
        // names end in Password, Secret, Token or Key — docs/plan/00 § Non-negotiables' list,
        // verbatim and closed. A member spelled Code, Otp or Digits matches none of them, so storing
        // the plaintext would compile clean and ship. This is the guard instead, and it is the same
        // instrument ManagedIdentityTests.NoSecretIsStoredAnywhereInTheFlow uses.
        var user = await cluster.NewUserAsync();

        (await cluster.User(user).IssueOtpAsync(OtpPurpose.SignIn, CredentialKind.EmailOtp))
            .IsSuccess.ShouldBeTrue();

        var code = cluster.LastCode;
        var state = await cluster.ReadUserStateAsync(user);

        state.OtpChallenges.Count.ShouldBe(1, "the challenge must actually be recorded");

        foreach (var member in state.OtpChallenges.Select(x => x.Digest)) {
            member.ShouldNotContain(code, Case.Sensitive, "the digest is a keyed hash, not an encoding");
        }

        // Everything serialized, not only the member the test happens to know about.
        Serialized(state).ShouldNotContain(
            code,
            Case.Sensitive,
            "the plaintext code appeared somewhere in UserGrainState. Six digits is a million "
            + "candidates, so anything reversible in the durable tier is the code — OtpPolicy "
            + "property 4. What is stored is an HMAC-SHA-256 under the vault pepper."
        );
    }

    [Fact]
    public async Task ADigestIsUselessWithoutThePepper() {
        // ⚠ The property the pepper actually buys, asserted rather than asserted-about. Two
        // protectors over the same code, tenant, user and purpose agree only if their keys agree —
        // so an attacker holding the durable tier and not the vault cannot compute a single
        // candidate digest, which is what makes six digits survivable at rest.
        var tenant = OtpIssuanceCluster.Tenant;
        var user = Guid.NewGuid();

        var withVault = new OtpCodeProtector("a-vault-resolved-pepper"u8);
        var without = new OtpCodeProtector(default);

        withVault.Digest(tenant, user, OtpPurpose.SignIn, "424242")
            .ShouldNotBe(without.Digest(tenant, user, OtpPurpose.SignIn, "424242"));

        // ⚠ And the binding: the same code for a different user or a different purpose is a
        // different digest, so one lifted out of another account's state verifies nowhere.
        withVault.Digest(tenant, user, OtpPurpose.SignIn, "424242")
            .ShouldNotBe(withVault.Digest(tenant, Guid.NewGuid(), OtpPurpose.SignIn, "424242"));

        withVault.Digest(tenant, user, OtpPurpose.SignIn, "424242")
            .ShouldNotBe(withVault.Digest(tenant, user, OtpPurpose.PasswordReset, "424242"));

        // Fixed length whatever the candidate, which is what lets CredentialDigest.FixedTimeEquals
        // do its job — that primitive returns immediately when two lengths differ, so a comparison
        // over variable-length values would leak the length rather than the value.
        withVault.Digest(tenant, user, OtpPurpose.SignIn, "1")
            .Length
            .ShouldBe(withVault.Digest(tenant, user, OtpPurpose.SignIn, new string('9', 500)).Length);
    }

    [Fact]
    public async Task TheCodeReachesNoLogMessage() {
        // ⚠ PiiNeverReachesALogMessageTests would NOT catch this. Its rule is docs/plan/11
        // § Auditing's — no email, name or IP in a message — and a one-time code is none of those,
        // so it would sail past that suite while being strictly worse to log than an address.
        var captured = new CapturingLoggerProvider();
        var user = await cluster.NewUserAsync();

        using (captured) {
            await cluster.WithLoggingAsync(
                captured,
                async () => {
                    (await cluster.User(user).IssueOtpAsync(OtpPurpose.SignIn, CredentialKind.EmailOtp))
                        .IsSuccess.ShouldBeTrue();

                    await cluster.User(user).RedeemOtpAsync(OtpPurpose.SignIn, cluster.LastCode);
                }
            );
        }

        var code = cluster.LastCode;

        foreach (var message in captured.Messages) {
            message.ShouldNotContain(
                code,
                Case.Sensitive,
                "a one-time code reached a log message. It is a live credential for ten minutes and "
                + "a log line outlives that by the retention policy."
            );
        }
    }

    // ── FAILURE CLASS: the limit is per process rather than per user ───────────────────────────

    [Fact]
    public async Task TheIssueLimitIsPerUserAndSurvivesADeactivation() {
        var user = await cluster.NewUserAsync();
        var other = await cluster.NewUserAsync();

        for (var i = 0; i < OtpPolicy.MaxIssuesPerWindow; i++) {
            (await cluster.User(user).IssueOtpAsync(OtpPurpose.SignIn, CredentialKind.EmailOtp))
                .IsSuccess.ShouldBeTrue($"issue {i + 1} is inside the cap");

            // Past the cooldown, so each of these is a genuine resend rather than a retry — a retry
            // is deliberately not charged for.
            cluster.Clock.Advance(OtpPolicy.ResendCooldown + TimeSpan.FromSeconds(1));
        }

        // ⚠ THE DEACTIVATION IS THE ASSERTION. A counter held in a process's memory would be gone
        // here — and in production "the process" is one of N identity-host replicas, so a per-process
        // cap is N times the cap and the load balancer does the spreading for the attacker.
        // OtpPolicy property 3.
        await cluster.User(user).DeactivateAsync();

        var refused = await cluster.User(user).IssueOtpAsync(OtpPurpose.SignIn, CredentialKind.EmailOtp);

        refused.IsFailure.ShouldBeTrue("the sixth code inside the window is over the per-user cap");
        refused.Error!.Code.ShouldBe(ErrorCode.QuotaExceeded);

        // ⚠ And it is per USER: a cap that had leaked into anything shared would refuse here too.
        (await cluster.User(other).IssueOtpAsync(OtpPurpose.SignIn, CredentialKind.EmailOtp))
            .IsSuccess.ShouldBeTrue("one user's spent budget is not another's");

        // The window slides rather than resetting wholesale, so the budget comes back.
        cluster.Clock.Advance(OtpPolicy.IssueWindow + TimeSpan.FromSeconds(1));

        (await cluster.User(user).IssueOtpAsync(OtpPurpose.SignIn, CredentialKind.EmailOtp))
            .IsSuccess.ShouldBeTrue("the window has rolled over");
    }

    [Fact]
    public async Task ARetryIsNotChargedAgainstTheIssueLimit() {
        // ⚠ Otherwise a caller repeating a call it never saw the answer to burns the user's whole
        // allowance on one code, and the user is then told to wait fifteen minutes for a code they
        // never received.
        var user = await cluster.NewUserAsync();

        for (var i = 0; i < OtpPolicy.MaxIssuesPerWindow + 3; i++) {
            (await cluster.User(user).IssueOtpAsync(OtpPurpose.SignIn, CredentialKind.EmailOtp))
                .IsSuccess.ShouldBeTrue($"call {i + 1} is a retry of the first and costs nothing");
        }

        cluster.Email.Calls.ShouldBe(1);
    }

    // ── The destination, which is never the caller's to choose ────────────────────────────────

    [Fact]
    public async Task ACodeGoesToTheAddressTheGrainHoldsAndNowhereElse() {
        var user = await cluster.NewUserAsync();

        (await cluster.User(user).IssueOtpAsync(OtpPurpose.SignIn, CredentialKind.EmailOtp))
            .IsSuccess.ShouldBeTrue();

        // ⚠ There is no destination parameter on IssueOtpAsync, and this is the assertion that it
        // stays that way: the address comes from the user's own state. A destination on the call
        // would let anything holding a half-authenticated session post the second factor to itself.
        cluster.Email.Sent.Single().Destination.ShouldBe(cluster.AddressOf(user));
    }

    [Fact]
    public async Task SmsIsRefusedWithASentenceRatherThanSentToAnUnprovenNumber() {
        var user = await cluster.NewUserAsync();

        var refused = await cluster.User(user).IssueOtpAsync(OtpPurpose.SignIn, CredentialKind.SmsOtp);

        refused.IsFailure.ShouldBeTrue(
            "docs/plan/11 § Credentials lists SMS at M1, and there is no verified number on a user "
            + "to send one to. Guessing a destination is worse than refusing one."
        );

        refused.Error!.Message.ShouldContain("verified destination");
        cluster.Sms.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task ADeliveryWithNoPurposeIsRefusedBeforeTheCarrier() {
        var user = await cluster.NewUserAsync();

        var refused = await cluster.User(user).IssueOtpAsync(OtpPurpose.Unknown, CredentialKind.EmailOtp);

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        cluster.Email.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task ASuspendedAccountIsSentNothing() {
        var user = await cluster.NewUserAsync();
        (await cluster.User(user).SetStatusAsync(UserStatus.Suspended)).IsSuccess.ShouldBeTrue();

        (await cluster.User(user).IssueOtpAsync(OtpPurpose.SignIn, CredentialKind.EmailOtp))
            .IsFailure.ShouldBeTrue();

        cluster.Email.Calls.ShouldBe(0);
    }

    /// <summary>A code that is not <paramref name="code" /> and is the same shape.</summary>
    /// <param name="code">The right one.</param>
    static string Wrong(string code) =>
        code[0] == '0' ? "1" + code[1..] : "0" + code[1..];

    static string Serialized(UserGrainState state) => JsonSerializer.Serialize(state);
}

/// <summary>
///     The other half of docs/plan/11's OTP story: a silo that composed identity and did
///     <b>not</b> opt into delivery.
/// </summary>
/// <remarks>
///     ⚠ <b>This row is why <c>UnavailableOtpDelivery</c>'s message exists, and until now it could
///     not be produced.</b> Three places in this tree claimed that type was "what every host gets";
///     no host registered an <c>IOtpDeliverySeam</c> at all and nothing called one, so the sentence
///     an operator was supposed to read at 03:00 was unreachable. It is reachable now, from the one
///     call that reaches the seam, and this is the assertion that keeps it so.
/// </remarks>
[Collection(IdentitySuite.Name)]
public sealed class UnwiredOtpDeliveryTests(IdentityCluster cluster) {
    [Fact]
    public async Task AnUnwiredSiloRefusesAndNamesTheMissingCall() {
        var userId = await cluster.CreateUserAsync("unwired-otp@example.com");

        var refused = await cluster.User(userId).IssueOtpAsync(OtpPurpose.SignIn, CredentialKind.EmailOtp);

        refused.IsFailure.ShouldBeTrue(
            "an OTP factor that reported delivery and sent nothing would lock every user who "
            + "enrolled in it out of their own account"
        );

        refused.Error!.Message.ShouldContain("AddCommunicationOtpDelivery");
    }

    [Fact]
    public async Task TheChallengeIsStillRecordedSoARetryRedeliversRatherThanReissuing() {
        // ⚠ The write happens BEFORE the send, so a failed delivery leaves a live challenge. That is
        // the safe order: the other one leaves a code the user is holding and the platform has never
        // heard of, which is a lockout with no explanation. It also means the operator's fix —
        // wiring the seam — makes the next retry deliver the code already issued.
        var userId = await cluster.CreateUserAsync("unwired-otp-retry@example.com");

        (await cluster.User(userId).IssueOtpAsync(OtpPurpose.SignIn, CredentialKind.EmailOtp))
            .IsFailure.ShouldBeTrue();

        // Nothing was delivered, so nothing can be redeemed — but the wrong answer is still the
        // uniform `false` rather than an error about wiring.
        (await cluster.User(userId).RedeemOtpAsync(OtpPurpose.SignIn, "000000"))
            .GetValueOrThrow()
            .ShouldBeFalse();
    }
}

/// <summary>
///     A silo wired the way <c>CyberCloud.Silo.Host</c> wires one: the identity module, the sending
///     domain, and <c>AddCommunicationOtpDelivery</c> pointing at a platform service.
/// </summary>
/// <remarks>
///     ⚠ Its own cluster rather than <c>IdentityCluster</c>'s, because <c>IdentityCluster</c> is
///     deliberately the <i>unwired</i> shape — <c>UnwiredOtpDeliveryTests</c> above depends on it
///     staying that way.
/// </remarks>
public sealed class OtpIssuanceCluster : IAsyncLifetime {
    readonly Dictionary<Guid, string> addresses = [];

    TestCluster cluster = null!;

    /// <summary>The tenant the users belong to.</summary>
    public static Guid Tenant { get; } = Guid.Parse("77777777-7777-4777-8777-777777777777");

    /// <summary>The tenant that owns the communication service. ⚠ Deliberately not <see cref="Tenant" />.</summary>
    public static Guid PlatformTenant { get; } = Guid.Parse("88888888-8888-4888-8888-888888888888");

    /// <summary>The service the route names.</summary>
    public static Guid ServiceId { get; } = Guid.Parse("99999999-9999-4999-8999-999999999999");

    /// <summary>The clock the whole silo reads. Advanced to reach a cooldown, an expiry or a window.</summary>
    public OtpClock Clock => OtpClock.Instance;

    /// <summary>The email carrier double.</summary>
    public InMemoryChannelProvider Email { get; } = new(ChannelKind.Email);

    /// <summary>The SMS carrier double. Nothing should ever reach it — see the SMS row.</summary>
    public InMemoryChannelProvider Sms { get; } = new(ChannelKind.Sms);

    /// <summary>Where the log lines go while <see cref="WithLoggingAsync" /> is running.</summary>
    public CapturingLoggerProvider? Capturing { get; private set; }

    /// <summary>Every code this suite has seen leave the platform, in order.</summary>
    /// <remarks>
    ///     ⚠ Read out of the message BODY rather than out of grain state, because grain state does
    ///     not have it — which is the point of <c>OtpIssuanceTests.TheCodeIsNowhereInGrainState</c>.
    ///     This is the only place in the suite the plaintext exists, and it exists because a carrier
    ///     is the one party that legitimately sees it.
    /// </remarks>
    public IReadOnlyList<string> Codes =>
        [.. Email.Sent.Select(x => x.Body.Split(' ', 2)[0])];

    /// <summary>The most recent code.</summary>
    public string LastCode => Codes[^1];

    /// <summary>A user grain in <see cref="Tenant" />.</summary>
    /// <param name="userId">Which user.</param>
    public IUserGrain User(Guid userId) =>
        cluster.GrainFactory
            .ForTenant(Tenant.ToString("D", CultureInfo.InvariantCulture))
            .GetGrain<IUserGrain>(GrainKeys.User(userId));

    /// <summary>The address a user was created with.</summary>
    /// <param name="userId">Which user.</param>
    public string AddressOf(Guid userId) => addresses[userId];

    /// <summary>
    ///     Creates an active user with a fresh address, and forgets both carriers.
    /// </summary>
    /// <remarks>
    ///     ⚠ A fresh user per test rather than a shared one, and it is not cosmetic. The fixture's
    ///     silo — and therefore its message grains and this user's issue window — outlives one test,
    ///     so two tests sending to one user would be each other's retry and the second would
    ///     correctly send nothing.
    /// </remarks>
    public async Task<Guid> NewUserAsync() {
        Email.Reset();
        Sms.Reset();

        var userId = Guid.NewGuid();
        var address = $"otp-{userId:N}@example.com";

        (await User(userId).CreateAsync(address, "Test User", UserStatus.Active)).IsSuccess.ShouldBeTrue();

        addresses[userId] = address;
        return userId;
    }

    /// <summary>The user's durable state, as the tier holds it.</summary>
    /// <param name="userId">Which user.</param>
    /// <remarks>
    ///     ⚠ Read from the storage provider rather than from the grain, so the assertion is about
    ///     what was <i>written</i> rather than about what an accessor chose to return.
    /// </remarks>
    public async Task<UserGrainState> ReadUserStateAsync(Guid userId) {
        var storage = cluster.Silos
            .OfType<InProcessSiloHandle>()
            .First()
            .SiloHost
            .Services
            .GetRequiredKeyedService<IGrainStorage>(StorageTiers.Durable);

        var state = new GrainState<UserGrainState>(new());

        await storage.ReadStateAsync(
            "user",
            cluster.GrainFactory
                .ForTenant(Tenant.ToString("D", CultureInfo.InvariantCulture))
                .GetGrain<IUserGrain>(GrainKeys.User(userId))
                .GetGrainId(),
            state
        );

        return state.State;
    }

    /// <summary>Runs <paramref name="body" /> with the silo's log lines going to a capture.</summary>
    /// <param name="into">Where to capture.</param>
    /// <param name="body">What to run.</param>
    public async Task WithLoggingAsync(CapturingLoggerProvider into, Func<Task> body) {
        ArgumentNullException.ThrowIfNull(body);

        Capturing = into;

        try {
            await body();
        } finally {
            Capturing = null;
        }
    }

    /// <inheritdoc />
    public async ValueTask InitializeAsync() {
        Instance = this;
        Clock.Reset();

        var builder = new TestClusterBuilder(1);
        builder.AddSiloBuilderConfigurator<SiloConfigurator>();
        cluster = builder.Build();
        await cluster.DeployAsync();

        var service = cluster.GrainFactory
            .ForTenant(PlatformTenant.ToString("D", CultureInfo.InvariantCulture))
            .GetGrain<ICommunicationServiceGrain>(CommunicationGrainKeys.Service(ServiceId));

        (await service.CreateAsync(PlatformTenant, "platform")).IsSuccess.ShouldBeTrue();

        foreach (var channel in (ChannelKind[])[ChannelKind.Email, ChannelKind.Sms]) {
            (await service.ConfigureChannelAsync(
                new() {
                    Channel = channel,
                    Provider = "in-memory",
                    Credentials = new() { Mode = CredentialMode.PlatformAccount },
                    Limits = new() {
                        MaxMessagesPerWindow = 10_000,
                        MaxSpendPerWindow = 10_000m,
                        Currency = "EUR"
                    },
                    EstimatedUnitCost = 0.05m,
                    Enabled = true
                }
            )).IsSuccess.ShouldBeTrue();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() {
        if (cluster is not null) {
            await cluster.StopAllSilosAsync();
            await cluster.DisposeAsync();
        }
    }

    /// <summary>The live fixture, for the configurator, which is constructed with <c>new()</c>.</summary>
    internal static OtpIssuanceCluster Instance { get; private set; } = null!;

    sealed class SiloConfigurator : ISiloConfigurator {
        public void Configure(ISiloBuilder silo) {
            silo.AddMemoryGrainStorage(StorageTiers.Durable);
            silo.AddMemoryGrainStorage(StorageTiers.Hot);

            silo.ConfigureServices(services => {
                    // FIRST, so both modules' TryAdd keeps them.
                    services.AddSingleton<IClock>(OtpClock.Instance);
                    services.AddSingleton<IPasswordHasher>(CheapArgon2.Hasher);
                    services.AddSingleton<IChannelProvider>(Instance.Email);
                    services.AddSingleton<IChannelProvider>(Instance.Sms);
                    services.AddSingleton<ILoggerProvider>(new ForwardingLoggerProvider());
                }
            );

            silo.AddCyberCloudCommunication();
            silo.AddCyberCloudIdentity();

            // ⚠ The line CyberCloud.Silo.Host makes from CyberCloud:Identity:OtpDelivery. Without it
            // this silo would resolve UnavailableOtpDelivery — which is what UnwiredOtpDeliveryTests
            // asserts against IdentityCluster, deliberately kept in that state.
            silo.AddCommunicationOtpDelivery(PlatformTenant, ServiceId);
        }
    }

    /// <summary>Sends the silo's log lines to whichever capture is currently installed.</summary>
    sealed class ForwardingLoggerProvider : ILoggerProvider {
        public ILogger CreateLogger(string categoryName) => new ForwardingLogger();

        public void Dispose() { }

        sealed class ForwardingLogger : ILogger {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull =>
                null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter
            ) {
                ArgumentNullException.ThrowIfNull(formatter);
                Instance.Capturing?.Record(formatter(state, exception));
            }
        }
    }
}

/// <summary>A clock this suite drives. Cooldowns, expiries and windows are all wall-clock here.</summary>
public sealed class OtpClock : IClock {
    /// <summary>The one instance the silo resolves.</summary>
    public static OtpClock Instance { get; } = new();

    /// <inheritdoc />
    public DateTimeOffset UtcNow { get; private set; } = new(2026, 8, 12, 9, 0, 0, TimeSpan.Zero);

    /// <summary>Moves time forward.</summary>
    /// <param name="by">How far.</param>
    public void Advance(TimeSpan by) => UtcNow += by;

    /// <summary>Back to the start.</summary>
    public void Reset() => UtcNow = new(2026, 8, 12, 9, 0, 0, TimeSpan.Zero);
}

/// <summary>Keeps every formatted log message, so a test can assert what is not in one.</summary>
public sealed class CapturingLoggerProvider : IDisposable {
    readonly List<string> messages = [];

    /// <summary>Everything logged while this was installed.</summary>
    public IReadOnlyList<string> Messages {
        get {
            lock (messages) {
                return [.. messages];
            }
        }
    }

    /// <summary>Records one formatted message.</summary>
    /// <param name="message">The message.</param>
    public void Record(string message) {
        lock (messages) {
            messages.Add(message);
        }
    }

    /// <inheritdoc />
    public void Dispose() {
        lock (messages) {
            messages.Clear();
        }
    }
}

/// <summary>Binds <see cref="OtpIssuanceCluster" /> to the class that shares it.</summary>
[CollectionDefinition(Name)]
public sealed class OtpIssuanceSuite : ICollectionFixture<OtpIssuanceCluster> {
    /// <summary>The collection name.</summary>
    public const string Name = "otp-issuance";
}
