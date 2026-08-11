using CyberCloud.Identity.Contracts;
using CyberCloud.Identity.SignIn;
using CyberCloud.Identity.Tests.Infrastructure;

namespace CyberCloud.Identity.Tests;

/// <summary>
///     docs/plan/11 § Credentials: <i>"the lockout counter lives in the hot tier keyed by the user
///     id, so it is a Redis <c>INCR</c>, not a grain call. An authentication endpoint whose failure
///     path costs a grain activation is a denial-of-service amplifier."</i>
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This is asserted by counting, not by reading the code.</b> "Touches no grain" is a
///         property of a code path, and a code path acquires a grain reference the day somebody adds
///         a helpful audit lookup to the failure branch. <see cref="RecordingGrainFactory" /> wraps
///         the cluster's real factory and counts every reference taken, so the assertion is
///         mechanical and survives the next edit.
///     </para>
///     <para>
///         <b>Why it matters, concretely.</b> Sign-in is the one endpoint an unauthenticated attacker
///         can drive at volume, and the address in the request is theirs to choose. If a failed
///         attempt activated a grain, they would be choosing which activations the cluster creates —
///         one packet from them, a durable-tier read and an activation slot from us. That is the
///         amplification factor the paragraph is about.
///     </para>
/// </remarks>
[Collection(IdentitySuite.Name)]
public sealed class LockoutIsGrainFreeTests(IdentityCluster cluster) {
    /// <summary>The runner's token, so a hung test is cancellable — xUnit1051.</summary>
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ALockedIdentifierIsRefusedWithoutTouchingASingleGrain() {
        var lockout = new InMemoryLockoutCounter(TestClock.Instance);
        var recorder = new RecordingGrainFactory(cluster.Grains);
        var service = cluster.SignIn(lockout, recorder);

        const string email = "locked-out@example.com";
        var key = LockoutKey.ForIdentifier(IdentityCluster.Tenant, email);

        // Drive the counter past the free attempts so the identifier is locked.
        for (var i = 0; i <= LockoutPolicy.FreeAttempts; i++) {
            await lockout.RecordFailureAsync(key, Ct);
        }

        (await lockout.IsLockedAsync(key, Ct)).ShouldBeTrue("the ladder should have locked this identifier");

        recorder.Reset();

        // Twenty attempts against a locked identifier — the shape of the attack.
        for (var i = 0; i < 20; i++) {
            var attempt = await service.SignInWithPasswordAsync(
                IdentityCluster.Tenant,
                email,
                "anything",
                new(), Ct);

            attempt.IsFailure.ShouldBeTrue();
            attempt.Error!.Message.ShouldBe(UniformFailures.SignIn);
        }

        recorder.References.ShouldBe(
            0,
            "A locked identifier must be refused before any grain reference is taken. The path asked "
            + $"for: {string.Join(", ", recorder.Asked.Distinct())}. docs/plan/11 § Credentials calls "
            + "an authentication endpoint whose failure path costs a grain activation a "
            + "denial-of-service amplifier — and the attacker chooses the address."
        );
    }

    [Fact]
    public async Task AMalformedAddressIsRefusedWithoutTouchingASingleGrain() {
        var lockout = new InMemoryLockoutCounter(TestClock.Instance);
        var recorder = new RecordingGrainFactory(cluster.Grains);
        var service = cluster.SignIn(lockout, recorder);

        recorder.Reset();

        // ⚠ The cheapest possible attack: a request that cannot even produce a grain key. It must
        // cost nothing, and the answer must still be the uniform one.
        foreach (var candidate in new[] { "", "   ", "no-at-sign", "two@@ats.example", new string('x', 300) }) {
            var attempt = await service.SignInWithPasswordAsync(
                IdentityCluster.Tenant,
                candidate,
                "anything",
                new(), Ct);

            attempt.IsFailure.ShouldBeTrue();
            attempt.Error!.Message.ShouldBe(UniformFailures.SignIn);
        }

        recorder.References.ShouldBe(0);
    }

    [Fact]
    public async Task RecordingAFailureIsNotAGrainCall() {
        var lockout = new InMemoryLockoutCounter(TestClock.Instance);
        var recorder = new RecordingGrainFactory(cluster.Grains);
        var service = cluster.SignIn(lockout, recorder);

        // An address with no account: the resolve is one grain reference (IEmailIndexGrain), and the
        // INCR that follows must add none.
        await service.SignInWithPasswordAsync(
            IdentityCluster.Tenant,
            "unknown-for-incr@example.com",
            "anything",
            new(), Ct);

        recorder.Asked.ShouldBe(["IEmailIndexGrain"]);
        recorder.References.ShouldBe(
            1,
            "An unknown address costs exactly one grain reference — the email index resolve, which "
            + "is bounded by the lockout counter that ran before it. Recording the failure must add "
            + "none: it is a hot-tier INCR."
        );
    }

    [Fact]
    public void TheLadderIsExponentialAndCapped() {
        // Free attempts cost nothing, which is what keeps a person who mistypes twice out of the
        // support queue.
        for (var i = 0; i <= LockoutPolicy.FreeAttempts; i++) {
            LockoutPolicy.DelayFor(i).ShouldBe(TimeSpan.Zero);
        }

        LockoutPolicy.DelayFor(LockoutPolicy.FreeAttempts + 1).ShouldBe(TimeSpan.FromSeconds(1));
        LockoutPolicy.DelayFor(LockoutPolicy.FreeAttempts + 2).ShouldBe(TimeSpan.FromSeconds(2));
        LockoutPolicy.DelayFor(LockoutPolicy.FreeAttempts + 3).ShouldBe(TimeSpan.FromSeconds(4));

        // ⚠ THE SHIFT TRAP. `1 << 40` on an int is `1 << 8`, because C# masks the shift count to five
        // bits — so an unbounded exponent wraps round to a SHORT delay at exactly the point the delay
        // should be longest. This is the assertion that would catch that regression: every large
        // failure count must produce the cap, and none may produce something small.
        foreach (var failures in new[] { 20, 33, 40, 64, 100, 1_000, int.MaxValue }) {
            LockoutPolicy.DelayFor(failures).ShouldBe(
                LockoutPolicy.MaximumDelay,
                $"{failures} failures must produce the cap. A shift-count wrap would produce a short delay here."
            );
        }
    }

    [Fact]
    public async Task TheWindowSlidesSoAQuietPeriodStartsOver() {
        var clock = new TestClock();
        var lockout = new InMemoryLockoutCounter(clock);
        var key = LockoutKey.ForIdentifier(IdentityCluster.Tenant, "sliding@example.com");

        for (var i = 0; i < LockoutPolicy.FreeAttempts; i++) {
            await lockout.RecordFailureAsync(key, Ct);
        }

        clock.Advance(LockoutPolicy.Window + TimeSpan.FromMinutes(1));

        // ⚠ The count restarts rather than continuing. A ladder that never forgets turns "I get my
        // password wrong about once a month" into a permanent lockout.
        (await lockout.RecordFailureAsync(key, Ct)).ShouldBe(1);
        (await lockout.IsLockedAsync(key, Ct)).ShouldBeFalse();
    }

    [Fact]
    public void TheIdentifierKeyNeedsNoGrainAndFoldsCaseTheSameWayTheIndexDoes() {
        var upper = LockoutKey.ForIdentifier(IdentityCluster.Tenant, "Alice@Example.COM");
        var lower = LockoutKey.ForIdentifier(IdentityCluster.Tenant, "alice@example.com");

        upper.ShouldBe(lower);

        // ⚠ And it must NOT fold the way ToLowerInvariant does. U+212A KELVIN SIGN lower-cases onto
        // 'k' under the invariant culture, so `aK@x` and `ak@x` would share a counter — one account's
        // failed attempts locking another account out. GrainKeys.NormalizeEmail folds only A-Z, and
        // this key reuses it rather than inventing a second rule.
        LockoutKey.ForIdentifier(IdentityCluster.Tenant, "aK@example.com")
            .ShouldNotBe(LockoutKey.ForIdentifier(IdentityCluster.Tenant, "ak@example.com"));

        // Per tenant, like the email index — one tenant's failures never lock another's account.
        LockoutKey.ForIdentifier(IdentityCluster.Tenant, "alice@example.com")
            .ShouldNotBe(LockoutKey.ForIdentifier(IdentityCluster.OtherTenant, "alice@example.com"));
    }
}
