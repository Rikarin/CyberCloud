using CyberCloud.Identity.Contracts;
using CyberCloud.Identity.SignIn;
using CyberCloud.Identity.Tests.Infrastructure;
using System.Diagnostics;
using System.Globalization;

namespace CyberCloud.Identity.Tests;

/// <summary>
///     docs/plan/11 § Credentials: sign-in, password reset and sign-up "return the same response and
///     take the same time whether or not the account exists".
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Both halves are asserted, and the second one is the point.</b> The response half is
///         cheap to get right and cheap to test. The timing half is where implementations actually
///         leak: an early <c>return</c> on the no-such-user branch skips an Argon2id verification, and
///         the difference between "50 ms" and "0.2 ms" is measurable over a handful of requests from
///         anywhere on the internet. A constant-time claim that is not measured is not a claim.
///     </para>
///     <para>
///         <b>How the measurement is made robust</b>, because wall-clock assertions in CI are how
///         flaky suites are born:
///     </para>
///     <list type="bullet">
///         <item>
///             <b>Medians, not means.</b> A GC pause or a scheduler hiccup moves a mean and does not
///             move a median.
///         </item>
///         <item>
///             <b>Interleaved samples.</b> The two branches alternate, so a machine that gets slower
///             halfway through the run slows both equally. Running all of one and then all of the
///             other is the classic way to measure the machine instead of the code.
///         </item>
///         <item>
///             <b>A warm-up pass that is discarded.</b> JIT, the first grain activation and the
///             first Argon2id allocation all land on sample one.
///         </item>
///         <item>
///             <b>A ratio bound, not an absolute one.</b> The assertion is that neither branch is
///             more than 2× the other. That is loose enough to survive a noisy CI box and far tighter
///             than the leak it is looking for, which is two to three orders of magnitude.
///         </item>
///     </list>
///     <para>
///         ⚠ <see cref="SignInOptions.MinimumDuration" /> is set to zero throughout. The production
///         default pads every attempt to a fixed floor, which would make any two measurements equal
///         and turn these assertions into tautologies —
///         <see cref="TheProductionFloorPadsEveryAttempt" /> is what covers the floor itself.
///     </para>
/// </remarks>
[Collection(IdentitySuite.Name)]
public sealed class EnumerationAndTimingTests(IdentityCluster cluster) {
    /// <summary>The runner's token, so a hung test is cancellable — xUnit1051.</summary>
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    const string Password = "correct-horse-battery-staple";

    /// <summary>How many measured samples per branch. Enough for a stable median, quick enough to run.</summary>
    const int Samples = 25;

    [Fact]
    public async Task AWrongPasswordAndAnUnknownAddressProduceTheIdenticalFailure() {
        var lockout = new InMemoryLockoutCounter(TestClock.Instance);
        var service = cluster.SignIn(lockout);

        await cluster.CreateUserAsync("present@example.com", Password);

        var wrongPassword = await service.SignInWithPasswordAsync(
            IdentityCluster.Tenant,
            "present@example.com",
            "not-the-password",
            new(), Ct);

        var noSuchUser = await service.SignInWithPasswordAsync(
            IdentityCluster.Tenant,
            "absent@example.com",
            Password,
            new(), Ct);

        wrongPassword.IsFailure.ShouldBeTrue();
        noSuchUser.IsFailure.ShouldBeTrue();

        // Identical code AND identical message. A different message is an oracle even when the code
        // matches, and a caller rendering `error.message` is the normal thing for a caller to do.
        wrongPassword.Error!.Code.ShouldBe(noSuchUser.Error!.Code);
        wrongPassword.Error!.Message.ShouldBe(noSuchUser.Error!.Message);
        wrongPassword.Error!.Message.ShouldBe(UniformFailures.SignIn);
    }

    [Fact]
    public async Task ASuspendedAccountFailsTheSameWayAsAnUnknownOne() {
        var lockout = new InMemoryLockoutCounter(TestClock.Instance);
        var service = cluster.SignIn(lockout);

        var userId = await cluster.CreateUserAsync("suspended@example.com", Password);
        await cluster.User(userId).SetStatusAsync(UserStatus.Suspended);

        var suspended = await service.SignInWithPasswordAsync(
            IdentityCluster.Tenant,
            "suspended@example.com",
            Password,
            new(), Ct);

        var unknown = await service.SignInWithPasswordAsync(
            IdentityCluster.Tenant,
            "never-existed@example.com",
            Password,
            new(), Ct);

        // ⚠ A correct password on a suspended account must not be distinguishable from a wrong
        // password on a missing one. "Your account is suspended" is a helpful message that tells an
        // attacker they have found a real account AND guessed its password.
        suspended.IsFailure.ShouldBeTrue();
        suspended.Error!.Message.ShouldBe(unknown.Error!.Message);
    }

    [Fact]
    public async Task AnAccountWithNoPasswordFailsTheSameWayAsAnUnknownOne() {
        var lockout = new InMemoryLockoutCounter(TestClock.Instance);
        var service = cluster.SignIn(lockout);

        // Passkey-only, which docs/plan/11 § Credentials makes the DEFAULT sign-up shape — so this is
        // the common case rather than an edge one, and it is the probe an attacker would use.
        await cluster.CreateUserAsync("passkey-only@example.com");

        var noPassword = await service.SignInWithPasswordAsync(
            IdentityCluster.Tenant,
            "passkey-only@example.com",
            Password,
            new(), Ct);

        noPassword.IsFailure.ShouldBeTrue();
        noPassword.Error!.Message.ShouldBe(UniformFailures.SignIn);
    }

    [Fact]
    public async Task PasswordResetSaysTheSameThingForAnAddressWithAndWithoutAnAccount() {
        var lockout = new InMemoryLockoutCounter(TestClock.Instance);
        var service = cluster.SignIn(lockout);

        await cluster.CreateUserAsync("reset-present@example.com", Password);

        var present = await service.RequestPasswordResetAsync(IdentityCluster.Tenant, "reset-present@example.com", Ct);
        var absent = await service.RequestPasswordResetAsync(IdentityCluster.Tenant, "reset-absent@example.com", Ct);
        var malformed = await service.RequestPasswordResetAsync(IdentityCluster.Tenant, "not-an-address", Ct);

        present.IsSuccess.ShouldBeTrue();
        absent.IsSuccess.ShouldBeTrue();

        // ⚠ Even a malformed address gets the uniform answer. A validation error would let a probe
        // separate "not an address" from "no account", which is a reliable oracle against a list.
        malformed.IsSuccess.ShouldBeTrue();

        present.GetValueOrThrow().ShouldBe(UniformFailures.PasswordReset);
        absent.GetValueOrThrow().ShouldBe(UniformFailures.PasswordReset);
        malformed.GetValueOrThrow().ShouldBe(UniformFailures.PasswordReset);
    }

    [Fact]
    public async Task SignInTakesTheSameTimeWhetherOrNotTheAccountExists() {
        var lockout = new InMemoryLockoutCounter(TestClock.Instance);
        var service = cluster.SignIn(lockout, options: new() { MinimumDuration = TimeSpan.Zero });

        await cluster.CreateUserAsync("timing-present@example.com", Password);

        var present = new List<double>(Samples);
        var absent = new List<double>(Samples);

        // ⚠ THE COUNTER IS RESET BEFORE EVERY SAMPLE, AND WITHOUT THAT THIS TEST MEASURES NOTHING.
        //
        // Every sample is a deliberate failure, so after LockoutPolicy.FreeAttempts the identifier is
        // locked and the path short-circuits before it does any work — which is correct behaviour and
        // makes both branches return in microseconds. The first version of this test did exactly
        // that: it reported "present median 0.00 ms, absent median 0.00 ms" and passed, having
        // measured the locked path on both sides and said nothing about the verification at all.
        //
        // Resetting first makes every sample a FIRST attempt, which is the state the enumeration
        // defence is actually about — an attacker probing a list of addresses sees one attempt each.
        async Task<double> Sample(string email) {
            await lockout.ResetAsync(LockoutKey.ForIdentifier(IdentityCluster.Tenant, email), Ct);
            return await Measure(service, email);
        }

        // Warm-up, discarded. The first call pays for JIT, the first grain activation and Argon2id's
        // first large allocation — all on whichever branch happens to go first.
        for (var i = 0; i < 3; i++) {
            await Sample("timing-present@example.com");
            await Sample("timing-absent@example.com");
        }

        // Interleaved, so a machine that slows down mid-run slows both branches equally.
        for (var i = 0; i < Samples; i++) {
            present.Add(await Sample("timing-present@example.com"));
            absent.Add(await Sample("timing-absent@example.com"));
        }

        var presentMedian = Median(present);
        var absentMedian = Median(absent);
        var ratio = Math.Max(presentMedian, absentMedian) / Math.Min(presentMedian, absentMedian);

        // The numbers go in the failure message whether or not it fails — an assertion about timing
        // whose failure does not say what the timings were is an assertion nobody can act on.
        var report = string.Create(
            CultureInfo.InvariantCulture,
            $"present median {presentMedian:F2} ms, absent median {absentMedian:F2} ms, ratio {ratio:F2}"
        );


        // ⚠ Reported on success as well as on failure. A timing assertion whose numbers are only
        // visible when it breaks gives nobody a baseline, so the margin silently erodes until the
        // day it fails — and then the first question is "what did it used to be".
        TestContext.Current.TestOutputHelper?.WriteLine("Enumeration timing — " + report);

        // ⚠ And the work has to have actually happened. A median in the microseconds means the path
        // short-circuited on BOTH branches — a locked identifier, a hasher that was replaced with a
        // no-op — and two equal near-zero medians would satisfy the ratio bound below while proving
        // nothing. This is the guard against the test passing for the wrong reason.
        Math.Min(presentMedian, absentMedian).ShouldBeGreaterThan(
            1.0,
            $"Both branches must actually run an Argon2id verification — {report}. A sub-millisecond "
            + "median means neither did, and the ratio assertion below would then pass vacuously."
        );

        ratio.ShouldBeLessThan(
            2.0,
            $"The two branches must cost the same work — {report}. A large ratio means one branch is "
            + "returning early: the no-such-user path must verify against IPasswordHasher.DummyHash "
            + "rather than short-circuit (docs/plan/11 § Credentials)."
        );
    }

    [Fact]
    public async Task TheProductionFloorPadsEveryAttempt() {
        var lockout = new InMemoryLockoutCounter(TestClock.Instance);

        // A floor well above the cheap Argon2 cost, so the pad is what is being measured.
        var floor = TimeSpan.FromMilliseconds(200);
        var service = cluster.SignIn(lockout, options: new() { MinimumDuration = floor });

        await cluster.CreateUserAsync("floor@example.com", Password);

        var wrong = await Measure(service, "floor@example.com");
        var missing = await Measure(service, "floor-absent@example.com");

        // ⚠ The floor is the SECOND layer, not the defence. It absorbs the differences the work
        // equalization cannot reach — a warm activation against a cold one, a user with fifty
        // recovery codes against one with none. A 10 ms tolerance covers timer granularity.
        wrong.ShouldBeGreaterThan(floor.TotalMilliseconds - 10);
        missing.ShouldBeGreaterThan(floor.TotalMilliseconds - 10);
    }

    static async Task<double> Measure(SignInService service, string email) {
        var started = Stopwatch.GetTimestamp();

        await service.SignInWithPasswordAsync(
            IdentityCluster.Tenant,
            email,
            "a-wrong-password",
            new(), Ct);

        return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    static double Median(List<double> values) {
        var sorted = values.Order().ToArray();
        return sorted.Length % 2 == 1
            ? sorted[sorted.Length / 2]
            : (sorted[(sorted.Length / 2) - 1] + sorted[sorted.Length / 2]) / 2;
    }
}
