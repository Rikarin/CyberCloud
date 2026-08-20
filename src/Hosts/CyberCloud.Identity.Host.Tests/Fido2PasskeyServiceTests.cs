using CyberCloud.Core.Time;
using CyberCloud.Identity.Contracts;
using CyberCloud.Identity.Host.Credentials;
using Fido2NetLib;
using Fido2NetLib.Objects;
using System.Text;
using System.Text.Json;

namespace CyberCloud.Identity.Host.Tests;

/// <summary>
///     <see cref="Fido2PasskeyService" /> against the <b>real</b> Fido2NetLib library.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>A fake <see cref="IFido2" /> would make every assertion here vacuous.</b> Half of
///         what this file claims is that hostile input reaches the library and comes back as a
///         <see cref="Result" /> rather than as an exception — and a substitute that returned
///         whatever it was handed would never throw, so the <c>catch</c> clauses would be untested
///         while reading as covered. The other half is about the options the library is asked to
///         build, and those are only meaningful if the library builds them.
///     </para>
///     <para>
///         ⚠ What is <em>not</em> here is a successful registration or assertion, and the reason is
///         worth stating rather than leaving as a gap: producing one needs an authenticator, real
///         attestation, and a signature over a challenge this process issued. That is the WebAuthn
///         journey in <c>CyberCloud.E2E</c> with a virtual authenticator, and it is owed. Everything
///         below is reachable without one.
///     </para>
/// </remarks>
public sealed class Fido2PasskeyServiceTests {
    sealed class FrozenClock : IClock {
        public DateTimeOffset UtcNow { get; } = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
    }

    static readonly FrozenClock Clock = new();

    static Fido2PasskeyService Subject() =>
        new(
            new Fido2(
                new Fido2Configuration {
                    ServerDomain = "identity.example",
                    ServerName = "Cyber Cloud",
                    Origins = new HashSet<string>(StringComparer.Ordinal) { "https://identity.example" }
                }
            ),
            Clock
        );

    static PasskeyCredential Enrolled(string credentialId = "AQIDBA") =>
        new() {
            CredentialId = credentialId,
            PublicKey = "AQIDBA",
            SignCount = 3,
            Label = "A key"
        };

    // ── Registration options ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheUserHandleIsTheGuidsBytesAndNeverTheAddress() {
        var userId = Guid.Parse("44444444-4444-4444-8444-444444444444");

        var challenge = (await Subject().BeginRegistrationAsync(
            new() { UserId = userId, Email = "someone@example.com", DisplayName = "Someone", Existing = [] }
        )).GetValueOrThrow();

        var options = CredentialCreateOptions.FromJson(challenge.OptionsJson);

        // ⚠ THE ONE THAT CANNOT BE UNDONE LATER. WebAuthn stores the user handle on the
        // authenticator, so an address there means an email change orphans every enrolled passkey —
        // and prints the address on the authenticator's own screens.
        options.User.Id.ShouldBe(userId.ToByteArray());
        Encoding.UTF8.GetString(options.User.Id).ShouldNotContain("@");

        challenge.UserId.ShouldBe(userId);
        challenge.ExpiresAt.ShouldBe(Clock.UtcNow + Fido2PasskeyService.ChallengeLifetime);
    }

    [Fact]
    public async Task AResidentCredentialWithUserVerificationIsRequiredRatherThanPreferred() {
        var challenge = (await Subject().BeginRegistrationAsync(
            new() { UserId = Guid.NewGuid(), Email = "a@example.com", DisplayName = "A", Existing = [] }
        )).GetValueOrThrow();

        var options = CredentialCreateOptions.FromJson(challenge.OptionsJson);

        // ⚠ Required, not preferred. A resident (discoverable) credential with user verification is
        // what makes a passkey a single-step two-factor sign-in; "preferred" leaves the authenticator
        // free to produce something that still needs a password afterwards, which is not the
        // credential docs/plan/11 § Credentials makes the default.
        options.AuthenticatorSelection.ResidentKey.ShouldBe(ResidentKeyRequirement.Required);
        options.AuthenticatorSelection.UserVerification.ShouldBe(UserVerificationRequirement.Required);
    }

    [Fact]
    public async Task NoAttestationIsRequested() {
        var challenge = (await Subject().BeginRegistrationAsync(
            new() { UserId = Guid.NewGuid(), Email = "a@example.com", DisplayName = "A", Existing = [] }
        )).GetValueOrThrow();

        // ⚠ Asking for attestation returns a certificate identifying the authenticator model and,
        // for some vendors, the device. It is a privacy liability, it needs an MDS blob store to
        // verify against, and it buys a policy this platform does not have.
        CredentialCreateOptions.FromJson(challenge.OptionsJson)
            .Attestation
            .ShouldBe(AttestationConveyancePreference.None);
    }

    [Fact]
    public async Task AlreadyEnrolledCredentialsAreExcluded() {
        var challenge = (await Subject().BeginRegistrationAsync(
            new() {
                UserId = Guid.NewGuid(),
                Email = "a@example.com",
                DisplayName = "A",
                Existing = [Enrolled("AQIDBA"), Enrolled("BQYHCA")]
            }
        )).GetValueOrThrow();

        var excluded = CredentialCreateOptions.FromJson(challenge.OptionsJson).ExcludeCredentials;

        // So the authenticator declines rather than producing a second credential for an account it
        // already holds one for, which the user would then have to tell apart in a list.
        excluded.Count.ShouldBe(2);
    }

    // ── Assertion options ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnAddressWithNoPasskeysGetsAChallengeOfTheSameShape() {
        var subject = Subject();

        var none = (await subject.BeginAssertionAsync([])).GetValueOrThrow();
        var some = (await subject.BeginAssertionAsync([Enrolled()])).GetValueOrThrow();

        // ⚠ THE ENUMERATION PROPERTY. An empty list must produce a challenge, not a refusal — a
        // caller asking about an address with no account has to get the same shape of answer as one
        // asking about an address with three passkeys. What differs is the allow-list, which is what
        // a discoverable-credential sign-in looks like anyway.
        none.OptionsJson.ShouldNotBeNullOrEmpty();
        none.ExpiresAt.ShouldBe(some.ExpiresAt);

        var options = AssertionOptions.FromJson(none.OptionsJson);
        options.AllowCredentials.ShouldBeEmpty();
        options.UserVerification.ShouldBe(UserVerificationRequirement.Required);

        AssertionOptions.FromJson(some.OptionsJson).AllowCredentials.Count.ShouldBe(1);
    }

    // ── Hostile input reaches the library and comes back as a Result ───────────────────────────

    public static TheoryData<string> NotJson =>
        new() { "", "   ", "{", "not json at all", "[1,2,3]", "\"a string\"", "{\"id\":" };

    [Theory]
    [MemberData(nameof(NotJson))]
    public async Task AnAttestationThatIsNotAWebAuthnResponseIsARefusalAndNotAnException(string body) {
        var subject = Subject();
        var challenge = (await subject.BeginRegistrationAsync(
            new() { UserId = Guid.NewGuid(), Email = "a@example.com", DisplayName = "A", Existing = [] }
        )).GetValueOrThrow();

        var completed = await subject.CompleteRegistrationAsync(challenge, body);

        // ⚠ These endpoints are unauthenticated and the body is whatever was posted. An escaping
        // JsonException is a 500 with a stack trace; this is a 200 with a sentence the page renders.
        completed.IsSuccess.ShouldBeFalse(body);
        completed.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody, body);
    }

    [Theory]
    [MemberData(nameof(NotJson))]
    public async Task AnAssertionThatIsNotAWebAuthnResponseIsARefusalAndNotAnException(string body) {
        var subject = Subject();
        var challenge = (await subject.BeginAssertionAsync([Enrolled()])).GetValueOrThrow();

        var completed = await subject.CompleteAssertionAsync(challenge, body, Enrolled());

        completed.IsSuccess.ShouldBeFalse(body);
        completed.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody, body);
    }

    [Fact]
    public async Task AnAttestationShapedLikeTheBrowsersAnswerIsStillRefused() {
        var subject = Subject();
        var challenge = (await subject.BeginRegistrationAsync(
            new() { UserId = Guid.NewGuid(), Email = "a@example.com", DisplayName = "A", Existing = [] }
        )).GetValueOrThrow();

        // Shaped like the browser's answer, signed by nobody — the payload an attacker actually
        // posts, as opposed to the garbage in NotJson above.
        var attestation = JsonSerializer.Serialize(
            new {
                id = "AQIDBA",
                rawId = "AQIDBA",
                type = "public-key",
                response = new { attestationObject = "AQIDBA", clientDataJson = "AQIDBA" }
            }
        );

        var completed = await subject.CompleteRegistrationAsync(challenge, attestation);

        // ⚠ REFUSED, AND ASSERTED AS EITHER REFUSAL ON PURPOSE. Where the boundary falls — the
        // deserializer rejecting the shape, or the library rejecting the signature — is
        // Fido2NetLib's to decide and it moves between versions. What this service promises is that
        // a hostile body on an unauthenticated endpoint comes back as a Result and never as an
        // exception; pinning which of the two codes it is would be pinning the library's internals
        // as our contract, and the next upgrade would fail this test over nothing.
        completed.IsSuccess.ShouldBeFalse();
        completed.Error!.Code.ShouldBeOneOf(ErrorCode.InvalidRequestBody, ErrorCode.AuthorizationFailed);
    }

    [Fact]
    public async Task AnAssertionShapedLikeTheBrowsersAnswerIsStillRefused() {
        var subject = Subject();
        var challenge = (await subject.BeginAssertionAsync([Enrolled()])).GetValueOrThrow();

        var assertion = JsonSerializer.Serialize(
            new {
                id = "AQIDBA",
                rawId = "AQIDBA",
                type = "public-key",
                response = new {
                    authenticatorData = "AQIDBA",
                    clientDataJson = "AQIDBA",
                    signature = "AQIDBA",
                    userHandle = "AQIDBA"
                }
            }
        );

        var completed = await subject.CompleteAssertionAsync(challenge, assertion, Enrolled());

        // Either refusal, for the reason given on the registration counterpart above.
        completed.IsSuccess.ShouldBeFalse();
        completed.Error!.Code.ShouldBeOneOf(ErrorCode.InvalidRequestBody, ErrorCode.AuthorizationFailed);

        // ⚠ And no sign count came back. A refused assertion that still produced a number would let
        // a caller move the stored counter, which is the cloned-authenticator check turned off from
        // outside — see PasskeySignCountTests for the grain half of that pair.
        completed.ValueOrDefault.ShouldBe(0u);
    }

    // ── The encoding both halves depend on ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("ab")]
    [InlineData("abc")]
    [InlineData("abcd")]
    [InlineData("a credential id of some length")]
    [InlineData("~!@#$%^&*()_+")]
    public void Base64UrlRoundTripsAtEveryPaddingLength(string value) {
        // ⚠ Base64url without padding, which is what WebAuthn puts on the wire, and the padding
        // arithmetic is the part that goes wrong: a credential id whose length mod 4 is 1, 2 or 3
        // decodes to the wrong bytes or throws if the pad is computed off by one. Every residue is
        // covered here because the failure is silent — the id simply stops matching the enrolled one
        // and the user is told their passkey is not recognised.
        var encoded = Fido2PasskeyService.EncodeUtf8(value);

        encoded.ShouldNotContain("=");
        encoded.ShouldNotContain("+");
        encoded.ShouldNotContain("/");

        Fido2PasskeyService.RoundTrip(encoded).ShouldBe(encoded);
    }

    [Fact]
    public void AChallengeIsANonceAndNotASession() {
        // Five minutes. A challenge that lived for hours would be a replay window on the one exchange
        // whose entire security is that the server chose the value.
        Fido2PasskeyService.ChallengeLifetime.ShouldBeLessThanOrEqualTo(TimeSpan.FromMinutes(10));
        Fido2PasskeyService.ChallengeLifetime.ShouldBeGreaterThan(TimeSpan.Zero);
    }
}
