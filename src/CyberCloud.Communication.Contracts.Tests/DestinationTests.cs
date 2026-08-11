namespace CyberCloud.Communication.Contracts.Tests;

/// <summary>
///     Destination normalization — the property the suppression list rests on.
/// </summary>
/// <remarks>
///     ⚠ <b>These are not formatting tests.</b> docs/plan/17 § The parts that are actually the work
///     makes suppression "honoured before dispatch", and a recipient who sent <c>STOP</c> from
///     <c>+420 777 123 456</c> has not consented to <c>+420777123456</c>. Every case below is a
///     spelling that must collapse onto one entry, or one that must not.
/// </remarks>
public sealed class DestinationTests {
    // ── FAILURE CLASS: two spellings of one number reach one suppression entry ──────────────────

    [Theory]
    [InlineData("+420777123456")]
    [InlineData("+420 777 123 456")]
    [InlineData("+420-777-123-456")]
    [InlineData("+420 (777) 123.456")]
    [InlineData("  +420777123456  ")]
    public void EverySpellingOfOneNumberNormalizesToTheSameString(string typed) =>
        Destinations.Normalize(ChannelKind.Sms, typed)
            .GetValueOrThrow()
            .ShouldBe(
                "+420777123456",
                "a suppression entry is stored under the normalized form, so a spelling that does "
                + "not collapse is a way around an opt-out"
            );

    [Fact]
    public void TheLookAlikeSeparatorsAPastedContactCardCarriesAreStrippedToo() =>
        // U+00A0 no-break space, U+2011 non-breaking hyphen, U+2013 en dash. Every one of these
        // reaches a form field from a copied contact card and none of them is a hyphen-minus.
        Destinations.Normalize(ChannelKind.Sms, "+420 777‑123–456")
            .GetValueOrThrow()
            .ShouldBe("+420777123456");

    [Fact]
    public void ANumberWithNoCountryCodeIsRefusedRatherThanGuessed() {
        var refused = Destinations.Normalize(ChannelKind.Sms, "777123456");

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        refused.Error.Message.ShouldContain(
            "+",
            Case.Sensitive,
            "the refusal has to say what is missing — inferring a country code sends the message to "
            + "a real handset in the wrong one, consistently, which is the failure that survives testing"
        );
    }

    [Theory]
    [InlineData("+420 777 12x 456")]
    [InlineData("+4207771234567890123")]
    [InlineData("+42")]
    [InlineData("")]
    [InlineData("   ")]
    public void SomethingThatIsNotAnE164NumberIsRefused(string typed) =>
        Destinations.Normalize(ChannelKind.Sms, typed).IsFailure.ShouldBeTrue();

    [Theory]
    [InlineData("Alice@Example.COM", "alice@example.com")]
    [InlineData("  alice@example.com ", "alice@example.com")]
    public void AnEmailAddressFoldsExactlyAsTheEmailIndexFoldsIt(string typed, string expected) =>
        Destinations.Normalize(ChannelKind.Email, typed)
            .GetValueOrThrow()
            .ShouldBe(
                expected,
                "this defers to GrainKeys.NormalizeEmail rather than repeating it — two "
                + "canonicalisers for one address shape is two answers, and the one that differs is "
                + "the one an opt-out was recorded under"
            );

    [Fact]
    public void APushTokenIsOnlyTrimmed() =>
        // Opaque to us and case-sensitive to APNs and FCM. There is no second spelling of a token
        // for a recipient to have consented from, so any other transformation is pure risk.
        Destinations.Normalize(ChannelKind.Push, " AbC-dEf ").GetValueOrThrow().ShouldBe("AbC-dEf");

    [Fact]
    public void AnUnknownChannelHasNoNormalizationAndSaysSo() =>
        Destinations.Normalize(ChannelKind.Unknown, "+420777123456").IsFailure.ShouldBeTrue();
}

/// <summary>
///     Stop-keyword matching — docs/plan/17 § The parts that are actually the work: <c>STOP</c>
///     handling is legally required in most jurisdictions.
/// </summary>
public sealed class StopKeywordTests {
    [Theory]
    [InlineData("STOP")]
    [InlineData("stop")]
    [InlineData("Stop.")]
    [InlineData("STOP!")]
    [InlineData("  UNSUBSCRIBE  ")]
    [InlineData("cancel")]
    [InlineData("QUIT")]
    [InlineData("OPT-OUT")]
    [InlineData("odhlasit")]
    [InlineData("ABMELDEN")]
    public void ARecognisedOptOutIsRecognised(string body) {
        StopKeywords.IsStop(body, out var keyword).ShouldBeTrue();
        keyword.ShouldNotBeEmpty();
    }

    [Theory]
    [InlineData("please don't stop sending these")]
    [InlineData("can you stop the alerts after friday")]
    [InlineData("thanks")]
    [InlineData("")]
    [InlineData("   ")]
    public void AReplyThatMerelyContainsTheWordIsNotAnOptOut(string body) =>
        // ⚠ The carriers' own rule is a whole-message match, and it has to be: scanning for the word
        // anywhere opts out the customer who wrote "please don't stop sending these".
        StopKeywords.IsStop(body, out _).ShouldBeFalse();

    [Fact]
    public void ALongReplyIsNotExaminedAsAKeyword() =>
        StopKeywords.IsStop(new('x', 500), out _).ShouldBeFalse();

    [Fact]
    public void TheKeywordListIsNotEmptyAndIsUpperCase() {
        StopKeywords.All.ShouldNotBeEmpty();

        foreach (var keyword in StopKeywords.All) {
            keyword.ShouldBe(keyword.ToUpperInvariant());
        }
    }
}
