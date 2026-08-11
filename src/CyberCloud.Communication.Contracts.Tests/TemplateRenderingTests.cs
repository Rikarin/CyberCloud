using System.Collections.Immutable;

namespace CyberCloud.Communication.Contracts.Tests;

/// <summary>
///     Template rendering — docs/plan/17 § The parts that are actually the work's "named, versioned,
///     localised, with typed parameters".
/// </summary>
public sealed class TemplateRenderingTests {
    static MessageTemplateVersion Otp(bool codeRequired = true) =>
        new() {
            Version = 3,
            Channel = ChannelKind.Sms,
            Parameters = [
                new() { Name = "code", Required = codeRequired },
                new() { Name = "minutes", Required = false }
            ],
            Bodies = [
                new() { Locale = "en-US", Subject = "Your code", Body = "Your code is {code}." },
                new() { Locale = "cs-CZ", Subject = "Váš kód", Body = "Váš kód je {code}." }
            ]
        };

    // ── FAILURE CLASS: a missing required parameter fails BEFORE dispatch ───────────────────────

    [Fact]
    public void AMissingRequiredParameterIsRefusedAndTheRefusalNamesIt() {
        var refused = TemplateRenderer.Render(Otp(), "en-US", []);

        refused.IsFailure.ShouldBeTrue(
            "the alternative is a customer receiving \"Your code is {code}\" — a wasted message, a "
            + "support ticket, and a complaint, all of which cost more than a refusal"
        );

        refused.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        refused.Error.Message.ShouldContain("'code'");
        refused.Error.Message.ShouldContain("before dispatch");
    }

    [Fact]
    public void EveryMissingParameterIsNamedAtOnce() {
        var version = Otp() with {
            Parameters = [
                new() { Name = "code" },
                new() { Name = "expiry" },
                new() { Name = "actor" }
            ]
        };

        var refused = TemplateRenderer.Render(version, "en-US", [new() { Name = "code", Value = "1" }]);

        refused.Error!.Message.ShouldContain("'expiry'");
        refused.Error.Message.ShouldContain(
            "'actor'",
            Case.Sensitive,
            "a caller fixing them one round trip at a time is a caller we made do the work"
        );
    }

    [Fact]
    public void AMissingOptionalParameterRendersAndLeavesThePlaceholderVisible() {
        var version = Otp() with {
            Bodies = [new() { Locale = "en-US", Body = "Code {code}, valid {minutes} minutes." }]
        };

        TemplateRenderer.Render(version, "en-US", [new() { Name = "code", Value = "424242" }])
            .GetValueOrThrow()
            .Body
            .ShouldBe(
                "Code 424242, valid {minutes} minutes.",
                "an unmatched placeholder is left as written rather than blanked — it is either an "
                + "optional parameter or a typo in the template, and both are things a tenant needs "
                + "to see in the message they are testing"
            );
    }

    // ── Substitution, and the injection shape it must not have ─────────────────────────────────

    [Fact]
    public void ASubstitutedValueIsNeverRescanned() {
        var version = Otp() with { Bodies = [new() { Locale = "en", Body = "{code}" }] };

        TemplateRenderer.Render(
                version,
                "en",
                [new() { Name = "code", Value = "{minutes}" }, new() { Name = "minutes", Value = "SECRET" }]
            )
            .GetValueOrThrow()
            .Body
            .ShouldBe(
                "{minutes}",
                "a value containing a placeholder is text. Re-scanning would let a caller who "
                + "controls one argument reach a parameter they were not given, on a channel that "
                + "sends password-reset links"
            );
    }

    [Fact]
    public void ParameterNamesMatchOrdinallyAndCaseSensitively() =>
        TemplateRenderer.Render(Otp(), "en-US", [new() { Name = "Code", Value = "424242" }])
            .IsFailure
            .ShouldBeTrue("matching case-insensitively would make the declared contract softer than the compiler's");

    // ── Localisation ───────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("cs-CZ", "cs-CZ")]
    [InlineData("cs", "cs-CZ")]
    [InlineData("de-DE", "en-US")]
    [InlineData("", "en-US")]
    [InlineData(null, "en-US")]
    public void TheLocaleFallbackChainIsExactAndTheLocaleUsedComesBack(string? asked, string expected) =>
        TemplateRenderer.Render(Otp(), asked, [new() { Name = "code", Value = "1" }])
            .GetValueOrThrow()
            .Locale
            .ShouldBe(
                expected,
                "a fallback rather than a failure, because a tenant adding a locale should not break "
                + "recipients who do not have one — but the locale actually used comes back so the "
                + "caller can see it happened"
            );

    [Fact]
    public void AVersionWithNoBodyHasNothingToSendAndSaysSo() {
        var refused = TemplateRenderer.Render(
            new() { Version = 1, Parameters = ImmutableArray<TemplateParameter>.Empty, Bodies = [] },
            "en",
            []
        );

        refused.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
    }

    [Fact]
    public void TheCarrierTemplateNameSurvivesTheRender() =>
        TemplateRenderer.Render(
                Otp() with { ProviderTemplateName = "otp_v3_en" },
                "en-US",
                [new() { Name = "code", Value = "1" }]
            )
            .GetValueOrThrow()
            .ProviderTemplateName
            .ShouldBe(
                "otp_v3_en",
                "WhatsApp sends by reference — the carrier's name for the template is what a "
                + "business-initiated message quotes, and losing it here loses the send"
            );
}
