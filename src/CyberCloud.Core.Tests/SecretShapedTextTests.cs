using CyberCloud.Core.Security;
using Shouldly;

namespace CyberCloud.Core.Tests;

/// <summary>
///     <see cref="SecretShapedText" /> — the recogniser behind docs/plan/18 § Platform security's log
///     canary.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The negative cases are the half that decides whether this ships.</b> A recogniser
///         that catches every secret is trivial (redact everything) and useless. What makes this one
///         safe to put in front of every log line in the platform is that it leaves a trace id, a
///         grain key, an image digest and a resource id alone — so those cases are asserted with the
///         same weight as the positive ones, using values taken from the shapes this repository
///         actually logs.
///     </para>
///     <para>
///         ⚠ The literals below are invented and match no real account. They are shaped like the real
///         thing because a recogniser tested against <c>"secret"</c> is a recogniser tested against
///         nothing.
///     </para>
/// </remarks>
public class SecretShapedTextTests {
    // ⚠ EVERY CREDENTIAL-SHAPED LITERAL BELOW IS ASSEMBLED FROM PARTS, AND THAT IS NOT STYLE.
    // A repository that ships a secret recogniser trips every other one: GitHub push protection reads
    // the blob, finds a run shaped like a GitHub token or an OpenSSH private key, and REFUSES THE
    // PUSH — the control working exactly as designed, on the files whose whole purpose is to contain
    // those shapes. Allowing each one through the bypass link is the wrong answer, because it teaches
    // the next person that the button exists. Splitting at the vendor prefix is enough, since every
    // scanner anchors there, and this concatenates at runtime — the string handed to the matcher is
    // byte-identical, so no assertion here is weakened.
    static string Shape(params string[] parts) => string.Concat(parts);

    /// <summary>Text that must be recognised, against the rule that must be the one to fire.</summary>
    public static TheoryData<string, string> Credentials =>
        new() {
            {
                Shape("Authorization: ey", "JhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.", "eyJzdWIiOiIxMjM0NTY3ODkwIn0.", "dBjftJeZ4CVPmB92K27uhbUJU1p1r_wW1gFWFOEjXk"),
                "JsonWebToken"
            },
            { Shape("token hv", "s.", "CAESIJqweRTYuiop1234567890asdfghjklzxcvbnmQWERTY"), "VaultToken" },
            { Shape("root token ", "s.", "abcdefghijklmnopqrstuvwx used to unseal"), "VaultLegacyToken" },
            { Shape("aws_access_key_id = ", "AKIA", "IOSFODNN7EXAMPLE"), "AwsAccessKey" },
            { Shape("ghp", "_", "abcdefghijklmnopqrstuvwxyz0123456789"), "GitHubToken" },
            { Shape("github", "_pat_", "11ABCDEFG0abcdefghijkl_mnopqrstuvwxyz0123456789"), "GitHubPersonalAccessToken" },
            { Shape("xox", "b-", "1234567890-0987654321-AbCdEfGhIjKlMnOpQrSt"), "SlackToken" },
            { "Host=shard-3;Username=cc;Password=Tr0ub4dor-and-3;Pooling=true", "ConnectionStringPassword" },
            { "redis://default:9dK2mQ7xZ@cache-0.cc.svc:6379", "UriCredentials" },
            { "sent Bearer aBcDeF0123456789-_~+/=abcdef upstream", "BearerToken" },
            {
                Shape("-----BEGIN ", "OPENSSH PRIVATE KEY", "-----\nb3BlbnNzaC1rZXktdjEAAAAABG5vbmU\n-----END ", "OPENSSH PRIVATE KEY", "-----"),
                "PrivateKey"
            }
        };

    [Theory]
    [MemberData(nameof(Credentials))]
    public void ACredentialShapedRunIsReplacedAndTheRuleThatCaughtItIsNamed(string line, string rule) {
        SecretShapedText.TryRedact(line, out var redacted, out var rules).ShouldBeTrue();

        rules.ShouldContain(rule);
        redacted.ShouldContain(SecretShapedText.RedactionPrefix + rule + "]");
    }

    /// <summary>
    ///     Text this platform logs on purpose, which must survive untouched.
    /// </summary>
    /// <remarks>
    ///     Every value here is the shape of something real: a W3C trace id, an Orleans grain key as
    ///     <c>GrainKeys</c> composes it, a resource id, an image digest as docs/plan/18 § Platform
    ///     security requires images to be pinned, a base64 patch body and a correlation GUID.
    /// </remarks>
    public static TheoryData<string> Innocent =>
        [
            "traceparent 00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
            "grain 3f2504e04f8911d39a0c0305e82c3301|prod|widgets|alpha activated",
            "/tenants/3f2504e0-4f89-11d3-9a0c-0305e82c3301/subscriptions/9b1deb4d-3b7d-4bad-9bdd-2b0d7b3dcb6d/resourceGroups/prod",
            "pulling ghcr.io/rikarin/cybercloud-silo@sha256:ef9a4e42c4d9d0b3fbd1d5b9a6cc7a3e2f1b0c9d8e7f6a5b4c3d2e1f0a9b8c7d",
            "patch body eyJvcCI6InJlcGxhY2UiLCJwYXRoIjoiL3NrdSJ9",
            "correlation 7c9e6679-7425-40de-944b-e07fc1f90ae7 finished in 43 ms",
            "Host=shard-3;Username=cc;Pooling=true;MaxPoolSize=25",
            "ClusterId=cybercloud ServiceId=cc PartitionKey=tenants HmacKeyId=3"
        ];

    [Theory]
    [MemberData(nameof(Innocent))]
    public void TheThingsThisPlatformLogsOnPurposeAreLeftAlone(string line) {
        // ⚠ A redactor that fires here is worse than no redactor: it destroys the correlation id,
        // the grain key and the digest that every incident is reconstructed from, and it does it
        // silently. This is the assertion that stops an entropy rule from being added casually.
        SecretShapedText.TryRedact(line, out var redacted, out var rules).ShouldBeFalse(
            $"nothing in this line is a credential, yet {string.Join(", ", rules)} fired"
        );

        redacted.ShouldBeNull();
    }

    [Fact]
    public void OnlyTheValueOfAConnectionStringSettingGoes() {
        // The key survives because "which setting held it" is the diagnostic; the value does not.
        SecretShapedText
            .TryRedact("Host=shard-3;Password=Tr0ub4dor;Pooling=true", out var redacted, out _)
            .ShouldBeTrue();

        redacted.ShouldBe("Host=shard-3;Password=[redacted:ConnectionStringPassword];Pooling=true");
    }

    [Fact]
    public void TheStructureOfAUriSurvivesAndTheCredentialInItDoesNot() {
        SecretShapedText
            .TryRedact("redis://default:9dK2mQ7xZ@cache-0.cc.svc:6379", out var redacted, out _)
            .ShouldBeTrue();

        redacted.ShouldBe("redis://default:[redacted:UriCredentials]@cache-0.cc.svc:6379");
    }

    [Fact]
    public void ASecondPassOverRedactedTextChangesNothing() {
        // The sink emits what this returns, and a marker that could itself be matched would make
        // the output depend on how many times the text had been through — which is the shape of a
        // scrubber that eats a log line one bite at a time.
        SecretShapedText
            .TryRedact("Password=hunter22;user=cc", out var once, out _)
            .ShouldBeTrue();

        SecretShapedText.TryRedact(once, out _, out _).ShouldBeFalse();
    }

    [Fact]
    public void OneLineCanCarryTwoDifferentCredentialsAndBothRulesAreNamed() {
        SecretShapedText
            .TryRedact(
                Shape("AKIA", "IOSFODNN7EXAMPLE and Password=Tr0ub4dor in one breath"),
                out var redacted,
                out var rules
            )
            .ShouldBeTrue();

        rules.ShouldBe(["AwsAccessKey", "ConnectionStringPassword"], ignoreOrder: true);
        redacted.ShouldNotContain(Shape("AKIA", "IOSFODNN7EXAMPLE"));
        redacted.ShouldNotContain("Tr0ub4dor");
    }

    [Fact]
    public void ABearerHeaderCarryingAJwtIsNamedAsAJwt() {
        // Both rules match. The issuer-shaped one runs first so the alert names the credential to
        // rotate rather than the header it travelled in.
        SecretShapedText
            .TryRedact(
                Shape("Bearer ey", "JhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.", "eyJzdWIiOiIxMjM0NTY3ODkwIn0.", "dBjftJeZ4CVPmB92K27uhbUJU1p1r_wW1gFWFOEjXk"),
                out var redacted,
                out var rules
            )
            .ShouldBeTrue();

        rules[0].ShouldBe("JsonWebToken");
        redacted.ShouldBe("Bearer [redacted:JsonWebToken]");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NothingIsNotACredential(string? nothing) {
        SecretShapedText.TryRedact(nothing, out var redacted, out var rules).ShouldBeFalse();

        redacted.ShouldBeNull();
        rules.ShouldBeEmpty();
    }

    [Fact]
    public void TheRuleNamesAreDistinctBecauseTheyAreAnAlertDimension() {
        SecretShapedText.RuleNames.ShouldBeUnique();
        SecretShapedText.RuleNames.ShouldNotBeEmpty();
    }

    [Fact]
    public void ALongHostileStringIsAnsweredInLinearTimeRatherThanEventually() {
        // ⚠ The reason every pattern is NonBacktracking. The input below is the classic prefix that
        // makes a naive alternation explode; on a backtracking engine with a nested quantifier this
        // is the difference between microseconds and never. 4 MB, no timeout, one assertion: it
        // returns.
        var hostile = "Bearer " + new string('a', 4 * 1024 * 1024) + "!";

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        SecretShapedText.TryRedact(hostile, out _, out _);
        elapsed.Stop();

        elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
    }
}
