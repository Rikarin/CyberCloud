using CyberCloud.Identity.Contracts;
using CyberCloud.Identity.Credentials;
using CyberCloud.Identity.Tests.Infrastructure;
using System.Text;

namespace CyberCloud.Identity.Tests;

/// <summary>
///     The credential primitives — Argon2id, RFC 6238 TOTP, recovery codes. docs/plan/11 § Credentials.
/// </summary>
public sealed class CredentialTests {
    // ── Argon2id ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void APasswordVerifiesAgainstItsOwnHashAndNothingElse() {
        var hasher = CheapArgon2.Hasher;
        var encoded = hasher.Hash("correct-horse-battery-staple");

        hasher.Verify("correct-horse-battery-staple", encoded).ShouldBeTrue();
        hasher.Verify("correct-horse-battery-stapl", encoded).ShouldBeFalse();
        hasher.Verify("", encoded).ShouldBeFalse();
    }

    [Fact]
    public void TwoHashesOfOnePasswordDifferBecauseTheSaltDoes() {
        var hasher = CheapArgon2.Hasher;

        var first = hasher.Hash("same-password");
        var second = hasher.Hash("same-password");

        // ⚠ Equal hashes would mean no per-user salt, which means one rainbow table covers every
        // account in the platform — docs/plan/11 § Credentials asks for a per-user salt by name.
        first.ShouldNotBe(second);
        hasher.Verify("same-password", first).ShouldBeTrue();
        hasher.Verify("same-password", second).ShouldBeTrue();
    }

    [Fact]
    public void TheStoredFormCarriesItsOwnParameters() {
        var hasher = new Argon2idPasswordHasher(CheapArgon2.Options);
        var encoded = hasher.Hash("anything");

        // PHC format. Self-describing is what makes raising the cost a no-op rather than a migration.
        encoded.ShouldStartWith("$argon2id$v=19$");
        encoded.ShouldContain($"m={CheapArgon2.Options.MemoryKibibytes}");
        encoded.ShouldContain($"t={CheapArgon2.Options.Iterations}");
        encoded.ShouldContain($"p={CheapArgon2.Options.Parallelism}");
    }

    [Fact]
    public void AHashMadeAtALowerCostStillVerifiesAndAsksToBeRehashed() {
        var weak = new Argon2idPasswordHasher(new() { MemoryKibibytes = 8_192, Iterations = 1, Parallelism = 1 });
        var strong = new Argon2idPasswordHasher(new() { MemoryKibibytes = 16_384, Iterations = 2, Parallelism = 1 });

        var old = weak.Hash("legacy-password");

        // ⚠ Verification uses the STORED parameters, so raising the cost does not lock anybody out.
        strong.Verify("legacy-password", old).ShouldBeTrue();

        // …and the only moment a re-hash is possible is right here, while the plaintext exists.
        strong.NeedsRehash(old).ShouldBeTrue();
        strong.NeedsRehash(strong.Hash("legacy-password")).ShouldBeFalse();
    }

    [Fact]
    public void ThePepperChangesTheHashAndIsNotStoredInIt() {
        var withPepper = new Argon2idPasswordHasher(CheapArgon2.Options, "a-vault-pepper"u8);
        var without = new Argon2idPasswordHasher(CheapArgon2.Options);

        var peppered = withPepper.Hash("shared-password");

        // ⚠ An attacker with the database but not the vault cannot compute a single candidate hash.
        // That is the whole value of a pepper and it only holds if the pepper is a KDF input rather
        // than something stored alongside.
        without.Verify("shared-password", peppered).ShouldBeFalse();
        withPepper.Verify("shared-password", peppered).ShouldBeTrue();

        peppered.ShouldNotContain("a-vault-pepper");
    }

    [Fact]
    public void TheDummyHashIsARealHashOfTheConfiguredShape() {
        var hasher = CheapArgon2.Hasher;

        hasher.DummyHash.ShouldStartWith("$argon2id$v=19$");
        hasher.DummyHash.ShouldContain($"m={CheapArgon2.Options.MemoryKibibytes}");

        // Nothing verifies against it, which is what makes it safe to use as the no-such-user branch.
        hasher.Verify("", hasher.DummyHash).ShouldBeFalse();
        hasher.Verify("password", hasher.DummyHash).ShouldBeFalse();

        // ⚠ And it differs between hashers. A constant dummy would hash identically in every process,
        // so anyone who obtained it once could recognise the no-such-user branch by the value being
        // compared — reintroducing the enumeration it exists to close.
        new Argon2idPasswordHasher(CheapArgon2.Options).DummyHash.ShouldNotBe(hasher.DummyHash);
    }

    [Fact]
    public void ACorruptOrForeignStoredHashAnswersNoRatherThanThrowing() {
        var hasher = CheapArgon2.Hasher;

        foreach (var junk in new[] {
            "", "not-a-hash", "$argon2id$v=19$", "$argon2id$v=19$m=x,t=3,p=4$aaaa$bbbb",
            "$argon2id$v=19$m=8192,t=1,p=1$!!!not-base64!!!$bbbb", "$2b$12$bcrypt.style.hash.value"
        }) {
            hasher.Verify("anything", junk).ShouldBeFalse($"'{junk}' must answer no, not throw");
        }
    }

    // ── TOTP, RFC 6238 ─────────────────────────────────────────────────────────────────────────

    [Theory]
    // ⚠ RFC 6238 Appendix B's SHA-1 vectors, against the RFC's own secret "12345678901234567890"
    // base32-encoded. These are the reason this implementation can be trusted at all: an in-house
    // TOTP that has never been checked against the specification's vectors is an in-house TOTP that
    // works with the app you tested against and no other.
    [InlineData(59L, "94287082")]
    [InlineData(1111111109L, "07081804")]
    [InlineData(1111111111L, "14050471")]
    [InlineData(1234567890L, "89005924")]
    [InlineData(2000000000L, "69279037")]
    [InlineData(20000000000L, "65353130")]
    public void TotpMatchesRfc6238AppendixB(long unixSeconds, string expectedEightDigits) {
        // GBIWCZDBMJRWIZLGM5UGS3THMFZQ==== is "12345678901234567890" in base32, unpadded here.
        const string secret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";

        var counter = TotpAuthenticator.CounterFor(DateTimeOffset.FromUnixTimeSeconds(unixSeconds));

        // The RFC's table is eight digits; docs/plan/11 § Credentials' profile is six, so the
        // comparison is against the last six — the truncation is the same, the modulus differs.
        var expected = expectedEightDigits[^TotpParameters.Digits..];

        TotpAuthenticator.Compute(secret, counter).ShouldBe(expected);
    }

    [Fact]
    public void AGeneratedSecretIsBase32AndRoundTrips() {
        var secret = TotpAuthenticator.GenerateSecret();

        // ⚠ Base32, not base64. Every authenticator app reads the otpauth `secret` parameter as
        // RFC 4648 base32; handing one base64 gives an app that shows six digits and never verifies,
        // with no error at either end.
        secret.ShouldAllBe(c => "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567".Contains(c, StringComparison.Ordinal));

        // 160 bits at 5 bits per character.
        secret.Length.ShouldBe((TotpParameters.SecretBytes * 8 + 4) / 5);

        var now = DateTimeOffset.UtcNow;
        var code = TotpAuthenticator.Compute(secret, TotpAuthenticator.CounterFor(now));
        TotpAuthenticator.Verify(secret, code, now).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void TheDriftWindowIsExactlyOneStepEitherWay() {
        var secret = TotpAuthenticator.GenerateSecret();
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var counter = TotpAuthenticator.CounterFor(now);

        TotpAuthenticator.Verify(secret, TotpAuthenticator.Compute(secret, counter - 1), now).IsValid.ShouldBeTrue();
        TotpAuthenticator.Verify(secret, TotpAuthenticator.Compute(secret, counter), now).IsValid.ShouldBeTrue();
        TotpAuthenticator.Verify(secret, TotpAuthenticator.Compute(secret, counter + 1), now).IsValid.ShouldBeTrue();

        // ⚠ And no further. Widening this is not a usability tweak: at ±1 an attacker gets three live
        // codes per million per attempt, at ±5 they get eleven.
        TotpAuthenticator.Verify(secret, TotpAuthenticator.Compute(secret, counter - 2), now).IsValid.ShouldBeFalse();
        TotpAuthenticator.Verify(secret, TotpAuthenticator.Compute(secret, counter + 2), now).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void VerifyReportsWhichStepMatchedSoTheCounterCanBeBurnt() {
        var secret = TotpAuthenticator.GenerateSecret();
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var counter = TotpAuthenticator.CounterFor(now);

        // A Boolean would decide the sign-in and be useless for the replay block — docs/plan/11
        // § Credentials asks for "replay-blocked per (user, counter)".
        var verified = TotpAuthenticator.Verify(secret, TotpAuthenticator.Compute(secret, counter - 1), now);

        verified.IsValid.ShouldBeTrue();
        verified.Counter.ShouldBe(counter - 1);
    }

    [Fact]
    public void AMalformedCodeIsRejectedWithoutThrowing() {
        var secret = TotpAuthenticator.GenerateSecret();
        var now = DateTimeOffset.UtcNow;

        foreach (var junk in new[] { null, "", "12345", "1234567", "abcdef", "  1234" }) {
            TotpAuthenticator.Verify(secret, junk, now).IsValid.ShouldBeFalse();
        }
    }

    [Fact]
    public void TheProvisioningUriNamesTheIssuerTwice() {
        var uri = TotpAuthenticator.BuildProvisioningUri("Cyber Cloud", "alice@example.com", "ABCDEFGH");

        // ⚠ Older apps read only the label prefix and newer ones only the parameter; omitting either
        // gives some fraction of users an entry called "Unknown".
        uri.ShouldStartWith("otpauth://totp/Cyber%20Cloud:");
        uri.ShouldContain("issuer=Cyber%20Cloud");
        uri.ShouldContain("secret=ABCDEFGH");
        uri.ShouldContain("digits=6");
        uri.ShouldContain("period=30");
    }

    // ── Recovery codes ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ABatchIsTenCodesOfTenCharacters() {
        var codes = RecoveryCodes.Generate();

        codes.Count.ShouldBe(RecoveryCodes.BatchSize);

        foreach (var code in codes) {
            RecoveryCodes.Normalize(code).Length.ShouldBe(RecoveryCodes.CodeLength);
            code.ShouldContain("-", Case.Sensitive);
        }

        codes.Distinct(StringComparer.Ordinal).Count().ShouldBe(RecoveryCodes.BatchSize);
    }

    [Fact]
    public void TheAlphabetOmitsTheCharactersPeopleTranscribeWrong() {
        // These are read off paper by somebody who has already lost their phone.
        foreach (var confusable in "01ILOU") {
            RecoveryCodes.Alphabet.ShouldNotContain(confusable);
        }

        foreach (var code in RecoveryCodes.Generate()) {
            RecoveryCodes.Normalize(code)
                .ShouldAllBe(c => RecoveryCodes.Alphabet.Contains(c, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void NormalizationSurvivesHowAPersonActuallyTypesACode() {
        var codes = RecoveryCodes.Generate();
        var code = codes[0];

        var mangled = code.ToLowerInvariant().Replace("-", " ", StringComparison.Ordinal);

        RecoveryCodes.Hash(mangled).ShouldBe(RecoveryCodes.Hash(code));
        RecoveryCodes.Hash(code.Replace("-", "", StringComparison.Ordinal)).ShouldBe(RecoveryCodes.Hash(code));
    }

    [Fact]
    public void HashingIsOneWayAndStable() {
        var code = RecoveryCodes.Generate()[0];
        var hash = RecoveryCodes.Hash(code);

        hash.ShouldBe(RecoveryCodes.Hash(code));
        hash.ShouldNotContain(RecoveryCodes.Normalize(code), Case.Insensitive);
    }

    // ── Constant-time comparison ───────────────────────────────────────────────────────────────

    [Fact]
    public void FixedTimeEqualsAgreesWithOrdinalEqualityOnEveryShape() {
        // The comparison has to be *correct* before it is interesting that it is constant-time.
        CredentialDigest.FixedTimeEquals("abc", "abc").ShouldBeTrue();
        CredentialDigest.FixedTimeEquals("abc", "abd").ShouldBeFalse();
        CredentialDigest.FixedTimeEquals("abc", "ab").ShouldBeFalse();
        CredentialDigest.FixedTimeEquals("", "").ShouldBeTrue();
        CredentialDigest.FixedTimeEquals(null, "abc").ShouldBeFalse();
        CredentialDigest.FixedTimeEquals("abc", null).ShouldBeFalse();
        CredentialDigest.FixedTimeEquals("ABC", "abc").ShouldBeFalse();
    }

    [Fact]
    public void ARandomHandleCarriesTheEntropyItClaims() {
        var handle = CredentialDigest.RandomHandle(32);
        var another = CredentialDigest.RandomHandle(32);

        handle.ShouldNotBe(another);

        // base64url of 32 bytes, unpadded.
        handle.Length.ShouldBe(43);
        handle.ShouldNotContain("=");
        handle.ShouldNotContain("+");
        handle.ShouldNotContain("/");
    }

    [Fact]
    public void AnAddressDigestIsShortOneWayAndNotTheAddress() {
        const string ip = "198.51.100.42";

        var digest = CredentialDigest.AddressDigest(ip);

        digest.Length.ShouldBe(16);
        digest.ShouldNotContain(ip);
        digest.ShouldBe(CredentialDigest.AddressDigest(ip));
        digest.ShouldNotBe(CredentialDigest.AddressDigest("198.51.100.43"));

        CredentialDigest.AddressDigest(null).ShouldBeEmpty();
        CredentialDigest.AddressDigest("").ShouldBeEmpty();
    }

    [Fact]
    public void Sha256IsTheStableOneWayFormUsedForHandlesAndCodes() {
        // A known vector, so a future "optimisation" of the encoding is caught rather than absorbed.
        var digest = CredentialDigest.Sha256("abc");

        digest.ShouldBe(
            Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("abc")))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_')
        );
    }
}
