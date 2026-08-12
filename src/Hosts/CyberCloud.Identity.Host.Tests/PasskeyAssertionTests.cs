using CyberCloud.Identity.Host.Api;

namespace CyberCloud.Identity.Host.Tests;

/// <summary>
///     The only part of a WebAuthn assertion this host parses, and it runs on an unauthenticated
///     endpoint.
/// </summary>
/// <remarks>
///     ⚠ <b>Every input here is attacker-supplied</b>, so the property under test is not "does it
///     read the id" — it is "does anything make it throw". An unhandled exception on
///     <c>/api/signin/passkey/complete</c> is a <c>500</c> beside the <c>200</c> every other
///     rejection produces, which is a distinguishable answer and therefore an oracle.
/// </remarks>
public sealed class PasskeyAssertionTests {
    [Fact]
    public void TheCredentialIdIsReadFromAWellFormedAssertion() =>
        PasskeyAssertion.CredentialIdOf(
            """{"id":"q1w2e3r4","rawId":"q1w2e3r4","type":"public-key","response":{}}"""
        ).ShouldBe("q1w2e3r4");

    [Fact]
    public void RawIdIsNotUsedWhenItDisagreesWithId() =>
        // ⚠ `id` and not `rawId`, because PasskeyCredential.CredentialId is stored base64url and `id`
        // is already that. Reading `rawId` would mean decoding a caller-supplied buffer to compare.
        PasskeyAssertion.CredentialIdOf("""{"id":"the-id","rawId":"the-raw-id"}""").ShouldBe("the-id");

    /// <summary>Bodies a hostile caller can post, none of which may throw.</summary>
    public static TheoryData<string?> Malformed =>
    [
        (string?)null,
        "",
        "   ",
        "not json at all",
        "{",
        "[]",
        "null",
        "true",
        "42",
        "\"a string\"",
        "{}",
        """{"id":null}""",
        """{"id":""}""",
        """{"id":123}""",
        """{"id":{"nested":"object"}}""",
        """{"id":["an","array"]}""",
        """{"rawId":"only-raw-id"}""",
        """{"ID":"wrong-case"}"""
    ];

    [Theory]
    [MemberData(nameof(Malformed))]
    public void MalformedInputAnswersNullRatherThanThrowing(string? candidate) =>
        // ⚠ `Should.NotThrow` and a null result are one assertion here, deliberately: the endpoint
        // treats null as "reject uniformly", so anything that is not null and not a usable id has to
        // be one or the other rather than an exception escaping to the pipeline.
        Should.NotThrow(() => PasskeyAssertion.CredentialIdOf(candidate)).ShouldBeNull();

    [Fact]
    public void AnUpperCaseIdKeyIsNotMatched() =>
        // JSON member lookup is ordinal, which is what keeps this from quietly accepting a
        // differently-cased key and then comparing it against a stored value that used the other one.
        PasskeyAssertion.CredentialIdOf("""{"ID":"x","Id":"y"}""").ShouldBeNull();

    [Fact]
    public void ADeeplyNestedBodyDoesNotThrow() {
        // A cheap parser-exhaustion probe. System.Text.Json's default 64-level depth limit turns this
        // into a JsonException, which must be caught rather than escaping as a 500.
        var nested = new string('[', 200) + new string(']', 200);

        Should.NotThrow(() => PasskeyAssertion.CredentialIdOf(nested)).ShouldBeNull();
    }
}
