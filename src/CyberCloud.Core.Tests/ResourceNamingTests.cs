using CyberCloud.Core.Resources;
using Shouldly;

namespace CyberCloud.Core.Tests;

/// <summary>The naming rule from docs/plan/06:87-90.</summary>
public class ResourceNamingTests
{
    // ── Boundary lengths: 0, 1, 63, 64 ─────────────────────────────────────────────────────────
    // docs/plan/09 caps a Kubernetes label value at 63, and docs/plan/08:84 puts the resource id in
    // a label, so 63 must pass and 64 must not. The off-by-one here is the difference between "a
    // name you can use" and "an object the API server rejects at apply time, in production".

    [Fact]
    public void ZeroCharacterNameIsRejected()
    {
        var result = ResourceNaming.Validate(string.Empty);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe(ErrorCode.InvalidResourceName);
        result.Error.Message.ShouldContain("is empty");
    }

    [Fact]
    public void OneCharacterNameIsAccepted()
    {
        ResourceNaming.IsValid("a").ShouldBeTrue();
        ResourceNaming.IsValid("0").ShouldBeTrue();
    }

    [Fact]
    public void SixtyThreeCharacterNameIsAccepted()
    {
        var name = new string('a', 63);

        name.Length.ShouldBe(ResourceNaming.MaxLength);
        ResourceNaming.Validate(name).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void SixtyFourCharacterNameIsRejectedAndTheMessageSaysBothNumbers()
    {
        var name = new string('a', 64);

        var result = ResourceNaming.Validate(name);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Message.ShouldContain("64 characters long");
        result.Error.Message.ShouldContain("the limit is 63");
    }

    [Fact]
    public void NullNameIsRejectedWithoutThrowing()
    {
        var result = ResourceNaming.Validate(null);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Message.ShouldContain("null");
    }

    // ── Separator injection ────────────────────────────────────────────────────────────────────
    // The highest-severity class of bug this assembly can have. Everything downstream — the path
    // grammar, the grain key, the Kubernetes object name, the ReBAC tuple — assumes a name cannot
    // contain a separator. This is where that assumption is actually enforced.

    [Fact]
    public void EveryInjectionCharacterIsRejected()
    {
        foreach (var (value, why) in Corpus.InjectionCharacters)
        {
            foreach (var candidate in new[] { value, "pg" + value, value + "pg", "pg" + value + "x" })
            {
                ResourceNaming.IsValid(candidate).ShouldBeFalse(
                    $"'{Corpus.Printable(candidate)}' must be rejected — it contains "
                    + $"{Corpus.Printable(value)}, {why}");
            }
        }
    }

    [Fact]
    public void TheRejectionMessageNamesTheOffendingCharacterAndItsCodePoint()
    {
        var result = ResourceNaming.Validate("pg|prod");

        result.IsFailure.ShouldBeTrue();
        result.Error!.Message.ShouldContain("'|' (U+007C)");
        result.Error.Message.ShouldContain("at position 2");
    }

    [Fact]
    public void AControlCharacterIsQuotedRatherThanPastedIntoTheMessage()
    {
        // A NUL or a newline reaching a log line verbatim is how one message becomes two.
        var result = ResourceNaming.Validate("pg\0prod");

        result.IsFailure.ShouldBeTrue();
        result.Error!.Message.ShouldNotContain("\0");
        result.Error.Message.ShouldContain("\\u0000");
        result.Error.Message.ShouldContain("the character U+0000");
    }

    // ── Hyphen placement ───────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("-pg")]
    [InlineData("pg-")]
    [InlineData("-")]
    public void AHyphenAtEitherEndIsRejected(string name) =>
        ResourceNaming.IsValid(name).ShouldBeFalse();

    [Theory]
    [InlineData("pg-prod")]
    [InlineData("a-b-c-d")]
    [InlineData("pg--prod")]
    [InlineData("0-9")]
    public void AHyphenInTheMiddleIsAccepted(string name) =>
        ResourceNaming.IsValid(name).ShouldBeTrue();

    // ── The message is the mitigation (docs/plan/06:92-94) ─────────────────────────────────────

    [Fact]
    public void TheMessageStatesTheRuleTheLimitAndWhyWeDoNotMangle()
    {
        var result = ResourceNaming.Validate("PG-A7f3", "resource name");

        result.IsFailure.ShouldBeTrue();
        var message = result.Error!.Message;

        message.ShouldContain("'PG-A7f3'", Case.Sensitive);          // names the actual value
        message.ShouldContain(ResourceNaming.Pattern);               // states the rule
        message.ShouldContain("DNS-1123");                           // says where it comes from
        message.ShouldContain("63");                                 // states the limit
        message.ShouldContain("pg-a7f3");                            // the support ticket it prevents
        message.ShouldContain("docs/plan/06");                       // where to read more
    }

    [Fact]
    public void TheKindIsWovenIntoTheMessageSoTheCallerKnowsWhichFieldIsWrong()
    {
        ResourceNaming.Validate("BAD", "resource group name").Error!.Message
            .ShouldContain("is not a valid resource group name");

        ResourceNaming.Validate("BAD", "resource name").Error!.Message
            .ShouldContain("is not a valid resource name");
    }

    [Fact]
    public void ATargetIsCarriedThroughSoThePortalCanHighlightTheField()
    {
        var result = ResourceNaming.Validate("BAD", "resource name", "/properties/name");

        result.Error!.Target.ShouldBe("/properties/name");
    }

    // ── The generated corpus ───────────────────────────────────────────────────────────────────

    [Fact]
    public void EveryGeneratedNameIsValidAndUsableAsAKubernetesLabelValue()
    {
        var count = 0;
        foreach (var name in Corpus.ValidNames(5_000, seed: 1))
        {
            count++;
            ResourceNaming.IsValid(name).ShouldBeTrue($"generated name '{name}' should be valid");
            name.Length.ShouldBeLessThanOrEqualTo(63);
            name.ShouldAllBe(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');
        }

        count.ShouldBe(5_000);
    }
}
