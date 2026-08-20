using CyberCloud.Core.Contracts;
using CyberCloud.Identity.Contracts;
using CyberCloud.Identity.Credentials;
using CyberCloud.Identity.Tests.Infrastructure;

namespace CyberCloud.Identity.Tests;

/// <summary>
///     A TOTP code is valid for a whole step, so verifying it is not enough — a code observed on the
///     wire can be presented again inside the same thirty seconds.
///     <see cref="IUserGrain.ClaimTotpCounterAsync" /> is what makes the second presentation fail.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b><see cref="CredentialTests" /> covers <see cref="TotpAuthenticator" /> against RFC
///         6238's vectors, which is a different claim.</b> "This code is arithmetically correct for
///         this secret and this instant" is true for both the user and the attacker who watched them
///         type it. The claim here is that the platform spends the counter, and it is a property of
///         the grain's durable state rather than of the verifier.
///     </para>
///     <para>
///         ⚠ The spent list is <em>pruned</em>, and the pruning is the part that can go wrong
///         quietly. An unbounded list on a durable grain is a row that grows until the write fails;
///         prune too aggressively and a counter becomes replayable again while it is still inside
///         the drift window. Both directions are asserted below.
///     </para>
/// </remarks>
[Collection(IdentitySuite.Name)]
public sealed class TotpCounterReplayTests(IdentityCluster cluster) {
    static TotpEnrollment Enrollment() =>
        new() { SecretRef = new SecretRef { Path = "tenants/x/users/u/totp", Field = "secret" } };

    [Fact]
    public async Task ACounterIsSpentOnceAndTheSecondPresentationFails() {
        var user = cluster.User(await cluster.CreateUserAsync("totpc-totp@example.com"));
        (await user.EnrollTotpAsync(Enrollment())).IsSuccess.ShouldBeTrue();

        var counter = TotpAuthenticator.CounterFor(TestClock.Instance.UtcNow);

        (await user.ClaimTotpCounterAsync(counter)).GetValueOrThrow().ShouldBeTrue();

        // ⚠ Same counter, and the code behind it is still arithmetically valid for another few
        // seconds. Without this the attacker who read it over the user's shoulder gets a free
        // second factor for the rest of the step.
        (await user.ClaimTotpCounterAsync(counter))
            .GetValueOrThrow()
            .ShouldBeFalse("the code was valid and is already spent — that is a replay");

        (await user.ClaimTotpCounterAsync(counter + 1)).GetValueOrThrow().ShouldBeTrue();
    }

    [Fact]
    public async Task EveryCounterInsideTheDriftWindowStaysRemembered() {
        var user = cluster.User(await cluster.CreateUserAsync("totpc-drift@example.com"));
        (await user.EnrollTotpAsync(Enrollment())).IsSuccess.ShouldBeTrue();

        var now = TotpAuthenticator.CounterFor(TestClock.Instance.UtcNow);

        // The verifier accepts ±1 step, so these three are the only counters a caller can present
        // at this instant. All three must be claimable, and none of them re-claimable.
        long[] live = [now - TotpParameters.DriftSteps, now, now + TotpParameters.DriftSteps];

        foreach (var counter in live) {
            (await user.ClaimTotpCounterAsync(counter)).GetValueOrThrow().ShouldBeTrue($"{counter} is live");
        }

        foreach (var counter in live) {
            (await user.ClaimTotpCounterAsync(counter))
                .GetValueOrThrow()
                .ShouldBeFalse(
                    $"{counter} is inside the drift window of now, so it can still be presented and "
                    + "must still be remembered — pruning it would make it replayable"
                );
        }
    }

    [Fact]
    public async Task ACounterFarEnoughInThePastIsForgottenBecauseItCanNeverBePresentedAgain() {
        var user = cluster.User(await cluster.CreateUserAsync("totpc-pruned@example.com"));
        (await user.EnrollTotpAsync(Enrollment())).IsSuccess.ShouldBeTrue();

        var old = TotpAuthenticator.CounterFor(TestClock.Instance.UtcNow);
        (await user.ClaimTotpCounterAsync(old)).GetValueOrThrow().ShouldBeTrue();

        try {
            // Well past the drift window. The verifier will never accept a code for `old` again, so
            // remembering it buys nothing and costs a row that grows forever.
            TestClock.Instance.Advance(TimeSpan.FromMinutes(10));

            (await user.ClaimTotpCounterAsync(old))
                .GetValueOrThrow()
                .ShouldBeFalse(
                    "⚠ pruning happens on a successful claim's write, not on a read, so the stale "
                    + "counter is still remembered until something else is claimed. Asserting the "
                    + "other way round would be asserting a garbage collector runs when nothing has "
                    + "been allocated"
                );

            // The claim that does the pruning.
            var now = TotpAuthenticator.CounterFor(TestClock.Instance.UtcNow);
            (await user.ClaimTotpCounterAsync(now)).GetValueOrThrow().ShouldBeTrue();

            (await user.ClaimTotpCounterAsync(old))
                .GetValueOrThrow()
                .ShouldBeTrue(
                    "the spent list is now pruned below the drift window. ⚠ This is not a replay "
                    + "hole and the distinction is the whole reason the pruning is safe: the "
                    + "verifier accepts ±1 step, so a code for a counter twenty steps back cannot be "
                    + "produced by any secret and nothing can present it. What it buys is a durable "
                    + "row that stops growing"
                );
        } finally {
            TestClock.Instance.Reset();
        }
    }

    [Fact]
    public async Task AnAccountWithNoAuthenticatorClaimsNothing() {
        var user = cluster.User(await cluster.CreateUserAsync("totpc-no-totp@example.com"));

        (await user.GetTotpAsync()).Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);

        // ⚠ False rather than an error. The endpoint above answers uniformly, and a distinguishable
        // outcome here would tell an attacker which accounts have a second factor.
        (await user.ClaimTotpCounterAsync(TotpAuthenticator.CounterFor(TestClock.Instance.UtcNow)))
            .GetValueOrThrow()
            .ShouldBeFalse();
    }

    [Fact]
    public async Task ASuspendedAccountClaimsNothing() {
        var user = cluster.User(await cluster.CreateUserAsync("totpc-totp-suspended@example.com"));
        (await user.EnrollTotpAsync(Enrollment())).IsSuccess.ShouldBeTrue();

        (await user.SetStatusAsync(UserStatus.Suspended)).IsSuccess.ShouldBeTrue();

        var counter = TotpAuthenticator.CounterFor(TestClock.Instance.UtcNow);
        (await user.ClaimTotpCounterAsync(counter)).GetValueOrThrow().ShouldBeFalse();

        // ⚠ And the counter was not spent while the account was suspended. Un-suspending must not
        // hand back an account whose live counters were quietly consumed by the refused attempts.
        (await user.SetStatusAsync(UserStatus.Active)).IsSuccess.ShouldBeTrue();
        (await user.ClaimTotpCounterAsync(counter)).GetValueOrThrow().ShouldBeTrue();
    }

    [Fact]
    public async Task ReEnrollingClearsTheSpentCountersBecauseTheSecretChanged() {
        var user = cluster.User(await cluster.CreateUserAsync("totpc-re-enrol@example.com"));
        (await user.EnrollTotpAsync(Enrollment())).IsSuccess.ShouldBeTrue();

        var counter = TotpAuthenticator.CounterFor(TestClock.Instance.UtcNow);
        (await user.ClaimTotpCounterAsync(counter)).GetValueOrThrow().ShouldBeTrue();

        // A new enrolment is a new shared secret, so the codes for these counters are different
        // numbers. Carrying the spent list over would refuse the user's first code from their new
        // authenticator, which reads as "the app I just set up does not work".
        (await user.EnrollTotpAsync(
            Enrollment() with { SecretRef = new SecretRef { Path = "tenants/x/users/u/totp", Field = "secret2" } }
        )).IsSuccess.ShouldBeTrue();

        (await user.ClaimTotpCounterAsync(counter)).GetValueOrThrow().ShouldBeTrue();
    }

    [Fact]
    public async Task AnEnrolmentMustCarryAVaultHandle() {
        var user = cluster.User(await cluster.CreateUserAsync("totpc-handle@example.com"));

        var enrolled = await user.EnrollTotpAsync(new TotpEnrollment());

        enrolled.IsSuccess.ShouldBeFalse(
            "docs/plan/11 § Credentials: the shared secret is stored as a SecretRef, never in grain "
            + "state. An enrolment with no handle would be a second factor with no secret behind it."
        );
        enrolled.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        (await user.GetTotpAsync()).IsSuccess.ShouldBeFalse();
    }
}
