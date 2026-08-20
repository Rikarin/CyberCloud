using CyberCloud.Identity.Contracts;
using CyberCloud.Identity.Tests.Infrastructure;

namespace CyberCloud.Identity.Tests;

/// <summary>
///     A WebAuthn signature counter that goes <b>backwards</b> is the cloned-authenticator signal the
///     specification exists to give, and it is only a signal if the platform persists it and acts on
///     it. <see cref="IUserGrain.RecordPasskeyAssertionAsync" /> is where both happen.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The rule is "reject a decrease from a non-zero counter", not "require an
///         increase".</b> A large number of authenticators — every one that keeps no per-credential
///         state, which includes most platform authenticators — reports a constant zero, and that is
///         legal. Requiring an increase rejects those users on their <em>second</em> sign-in, and it
///         looks like a broken key rather than a policy choice, so it is the kind of mistake that
///         gets "fixed" by removing the check altogether.
///     </para>
///     <para>
///         ⚠ Both halves are asserted here for that reason: the clone is refused, <em>and</em> the
///         zero-counter authenticator keeps working. A test for either one alone is a test somebody
///         can satisfy by breaking the other.
///     </para>
/// </remarks>
[Collection(IdentitySuite.Name)]
public sealed class PasskeySignCountTests(IdentityCluster cluster) {
    static PasskeyCredential Credential(string id, uint signCount = 0) =>
        new() {
            CredentialId = id,
            PublicKey = "cHVibGlj",
            AaGuid = Guid.Parse("33333333-3333-4333-8333-333333333333"),
            SignCount = signCount,
            Label = "A key"
        };

    [Fact]
    public async Task ACounterThatGoesBackwardsIsRefusedAndTheStoredCounterIsNotLowered() {
        var user = cluster.User(await cluster.CreateUserAsync("passkey-clone@example.com"));
        (await user.AddPasskeyAsync(Credential("k1", 10))).IsSuccess.ShouldBeTrue();

        (await user.RecordPasskeyAssertionAsync("k1", 11)).GetValueOrThrow().ShouldBeTrue();

        // The clone has its own counter, which is behind the real key's.
        (await user.RecordPasskeyAssertionAsync("k1", 5))
            .GetValueOrThrow()
            .ShouldBeFalse("a counter below the stored one is the cloned-authenticator signal");

        // ⚠ And the refusal must not write. A rejected assertion that still stored its counter would
        // let a clone walk the counter down one assertion at a time until the real key's next
        // sign-in looked like the clone.
        var stored = (await user.ListPasskeysAsync()).GetValueOrThrow().Single();
        stored.SignCount.ShouldBe(11u);

        (await user.RecordPasskeyAssertionAsync("k1", 12)).GetValueOrThrow().ShouldBeTrue();
    }

    [Fact]
    public async Task RepeatingTheSameCounterIsRefused() {
        var user = cluster.User(await cluster.CreateUserAsync("passkey-replay@example.com"));
        (await user.AddPasskeyAsync(Credential("k1", 4))).IsSuccess.ShouldBeTrue();

        (await user.RecordPasskeyAssertionAsync("k1", 4))
            .GetValueOrThrow()
            .ShouldBeFalse("an equal counter is a replayed assertion, not a new one");
    }

    [Fact]
    public async Task AnAuthenticatorThatAlwaysReportsZeroKeepsWorking() {
        var user = cluster.User(await cluster.CreateUserAsync("passkey-zero@example.com"));
        (await user.AddPasskeyAsync(Credential("k1"))).IsSuccess.ShouldBeTrue();

        // ⚠ THE HALF THAT IS EASY TO BREAK. Five sign-ins from an authenticator that reports a
        // constant zero, which is legal WebAuthn and is what most platform authenticators do.
        for (var i = 0; i < 5; i++) {
            (await user.RecordPasskeyAssertionAsync("k1", 0))
                .GetValueOrThrow()
                .ShouldBeTrue($"assertion {i + 1} from a stateless authenticator must be accepted");
        }
    }

    [Fact]
    public async Task AnAuthenticatorThatStopsReportingIsNotTreatedAsAClone() {
        var user = cluster.User(await cluster.CreateUserAsync("passkey-stopped@example.com"));
        (await user.AddPasskeyAsync(Credential("k1", 7))).IsSuccess.ShouldBeTrue();

        // Zero from a credential whose stored counter is non-zero: the specification allows an
        // authenticator to stop maintaining the counter, and refusing this locks the user out of
        // their own key with no way to tell what happened.
        (await user.RecordPasskeyAssertionAsync("k1", 0)).GetValueOrThrow().ShouldBeTrue();
    }

    [Fact]
    public async Task AnAssertionForACredentialThisAccountDoesNotHoldIsRefused() {
        var user = cluster.User(await cluster.CreateUserAsync("passkey-other@example.com"));
        (await user.AddPasskeyAsync(Credential("k1", 1))).IsSuccess.ShouldBeTrue();

        (await user.RecordPasskeyAssertionAsync("k2", 99))
            .GetValueOrThrow()
            .ShouldBeFalse("a credential id nothing on this account matches proves nothing about it");
    }

    [Fact]
    public async Task ASuspendedAccountAssertsNothingEvenWithAValidCounter() {
        var userId = await cluster.CreateUserAsync("passkey-suspended@example.com");
        var user = cluster.User(userId);
        (await user.AddPasskeyAsync(Credential("k1", 1))).IsSuccess.ShouldBeTrue();

        (await user.SetStatusAsync(UserStatus.Suspended)).IsSuccess.ShouldBeTrue();

        // ⚠ Status is checked here and not only at the endpoint. A passkey is a credential that
        // works without the platform being asked anything else, so an account status that was
        // enforced one layer up would be enforced nowhere on this path.
        (await user.RecordPasskeyAssertionAsync("k1", 2)).GetValueOrThrow().ShouldBeFalse();

        (await user.SetStatusAsync(UserStatus.Active)).IsSuccess.ShouldBeTrue();
        (await user.RecordPasskeyAssertionAsync("k1", 2)).GetValueOrThrow().ShouldBeTrue();
    }

    [Fact]
    public async Task EnrollingTheSameCredentialTwiceIsAConflict() {
        var user = cluster.User(await cluster.CreateUserAsync("passkey-dupe@example.com"));
        (await user.AddPasskeyAsync(Credential("k1", 3))).IsSuccess.ShouldBeTrue();

        var again = await user.AddPasskeyAsync(Credential("k1"));

        again.IsSuccess.ShouldBeFalse();
        again.Error!.Code.ShouldBe(ErrorCode.Conflict);

        // ⚠ And the first enrolment is untouched. A second Add that overwrote would reset the stored
        // counter to whatever the caller sent, which is the clone check turned off from outside.
        var stored = (await user.ListPasskeysAsync()).GetValueOrThrow().Single();
        stored.SignCount.ShouldBe(3u);
    }

    [Fact]
    public async Task RemovingAKeyStopsItAsserting() {
        var user = cluster.User(await cluster.CreateUserAsync("passkey-removed@example.com"));
        (await user.AddPasskeyAsync(Credential("k1", 1))).IsSuccess.ShouldBeTrue();
        (await user.AddPasskeyAsync(Credential("k2", 1))).IsSuccess.ShouldBeTrue();

        (await user.RemovePasskeyAsync("k1")).IsSuccess.ShouldBeTrue();

        (await user.ListPasskeysAsync()).GetValueOrThrow().Select(x => x.CredentialId).ShouldBe(["k2"]);
        (await user.RecordPasskeyAssertionAsync("k1", 2))
            .GetValueOrThrow()
            .ShouldBeFalse("a removed key is a lost or stolen key; it must stop working immediately");
        (await user.RecordPasskeyAssertionAsync("k2", 2)).GetValueOrThrow().ShouldBeTrue();
    }

    [Fact]
    public async Task RemovingAKeyThatIsNotEnrolledIsNotAnError() {
        var user = cluster.User(await cluster.CreateUserAsync("passkey-idempotent@example.com"));
        (await user.AddPasskeyAsync(Credential("k1"))).IsSuccess.ShouldBeTrue();

        // Removing a key twice happens whenever a user double-clicks, and the second call must not
        // be an error the portal has to explain.
        (await user.RemovePasskeyAsync("k1")).IsSuccess.ShouldBeTrue();
        (await user.RemovePasskeyAsync("k1")).IsSuccess.ShouldBeTrue();
        (await user.ListPasskeysAsync()).GetValueOrThrow().ShouldBeEmpty();
    }

    [Fact]
    public async Task ACredentialIdIsComparedOrdinally() {
        var user = cluster.User(await cluster.CreateUserAsync("passkey-ordinal@example.com"));
        (await user.AddPasskeyAsync(Credential("Abc-Def_123", 1))).IsSuccess.ShouldBeTrue();

        // ⚠ Base64url is case-significant: `A` and `a` are different six-bit groups. A
        // case-insensitive compare would match a credential id the browser never sent, and would let
        // one enrolled key answer for another.
        (await user.RecordPasskeyAssertionAsync("abc-def_123", 2)).GetValueOrThrow().ShouldBeFalse();
        (await user.RecordPasskeyAssertionAsync("Abc-Def_123", 2)).GetValueOrThrow().ShouldBeTrue();
    }

    [Fact]
    public async Task AnAccountThatDoesNotExistEnrolsNothing() {
        var user = cluster.User(Guid.NewGuid());

        var added = await user.AddPasskeyAsync(Credential("k1"));
        added.IsSuccess.ShouldBeFalse();
        added.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);

        (await user.ListPasskeysAsync()).GetValueOrThrow().ShouldBeEmpty();
        (await user.RecordPasskeyAssertionAsync("k1", 1)).GetValueOrThrow().ShouldBeFalse();
    }
}
