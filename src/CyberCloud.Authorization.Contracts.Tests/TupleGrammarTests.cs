using Shouldly;

namespace CyberCloud.Authorization.Contracts.Tests;

/// <summary>
///     <c>object#relation@subject</c> — docs/plan/07 § The model's notation, as a parser.
/// </summary>
/// <remarks>
///     This grammar is not decoration. The regression corpus is checked in as these strings, so a
///     parser that accepts two spellings of one tuple, or re-cuts one tuple into another, would make
///     the corpus mean something different from what its author wrote.
/// </remarks>
public sealed class TupleGrammarTests {
    /// <summary>
    ///     Characters that make identifier code dangerous. The same shape as
    ///     <c>CyberCloud.Core.Tests.Corpus.InjectionCharacters</c>, narrowed to the ones that matter
    ///     for a tuple: the separators of this grammar and of the grain key.
    /// </summary>
    public static TheoryData<string, string> Injections =>
        new() {
            { "#", "the object/relation separator" },
            { "@", "the relation/subject separator" },
            { ":", "the type/id separator" },
            { "/", "the grain key separator" },
            { "|", "the Orleans.Multitenant tenant/key separator" },
            { "~", "the Orleans.Multitenant leading-character escape" },
            { "\n", "splits a log line in two; the second half is attacker-controlled" },
            { "\0", "terminates a C string" },
            { " ", "invisible at the end of a name" },
            { "İ", "LATIN CAPITAL LETTER I WITH DOT ABOVE" },
            { "／", "FULLWIDTH SOLIDUS — a look-alike for '/'" },
            { "..", "path traversal" }
        };

    [Theory]
    [InlineData("resourceGroup:prod#owner@user:alice")]
    [InlineData("resourceGroup:prod#reader@group:eng#member")]
    [InlineData("subscription:main#contains@resourceGroup:prod")]
    [InlineData("group:eng#member@group:platform#member")]
    public void TheFourExamplesInTheDocumentRoundTrip(string text) {
        // docs/plan/07 § The model's own four example tuples, verbatim.
        var parsed = RelationTuple.Parse(text);

        parsed.IsSuccess.ShouldBeTrue(parsed.Error?.Message);
        parsed.GetValueOrThrow().ToString().ShouldBe(text);
        parsed.GetValueOrThrow().IsValid.ShouldBeTrue();
    }

    [Fact]
    public void AUsersetSubjectIsToldApartFromAConcreteOne() {
        var userset = RelationTuple.Parse("resourceGroup:prod#reader@group:eng#member")
            .GetValueOrThrow();

        userset.Subject.IsUserset.ShouldBeTrue();
        userset.Subject.Relation.ShouldBe("member");
        userset.Subject.Object.ToString().ShouldBe("group:eng");

        var concrete = RelationTuple.Parse("resourceGroup:prod#owner@user:alice").GetValueOrThrow();

        concrete.Subject.IsUserset.ShouldBeFalse();
        concrete.Subject.Relation.ShouldBeEmpty();
    }

    [Fact]
    public void TheSplitIsOnTheFirstHashAndTheFirstAtAfterIt() {
        // The subject half legitimately contains a '#'. The object half cannot, because
        // RelationNaming excludes it — so this is unambiguous rather than lucky.
        var tuple = RelationTuple.Parse("group:eng#member@group:platform#member").GetValueOrThrow();

        tuple.Object.ToString().ShouldBe("group:eng");
        tuple.Relation.ShouldBe("member");
        tuple.Subject.ToString().ShouldBe("group:platform#member");
    }

    [Theory]
    [MemberData(nameof(Injections))]
    public void NoInjectedCharacterCanReCutATupleIntoADifferentOne(string injection, string why) {
        foreach (var forged in new[] {
                     $"resourceGroup:pr{injection}od#owner@user:alice",
                     $"resourceGroup:prod#ow{injection}ner@user:alice",
                     $"resourceGroup:prod#owner@user:al{injection}ice",
                     $"resource{injection}Group:prod#owner@user:alice"
                 }) {
            var parsed = RelationTuple.Parse(forged);

            if (parsed.IsFailure) {
                continue;
            }

            // The only tolerable outcome other than rejection is an exact round trip.
            parsed.GetValueOrThrow()
                .ToString()
                .ShouldBe(
                    forged,
                    $"'{Printable(forged)}' ({why}) parsed into a different tuple"
                );
        }
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("resourceGroup:prod", "no '#'")]
    [InlineData("resourceGroup:prod#owner", "no '@'")]
    [InlineData("resourceGroup:prod@user:alice#owner", "the '@' comes before the '#'")]
    [InlineData("prod#owner@user:alice", "the object has no ':'")]
    [InlineData("resourceGroup:prod#owner@alice", "the subject has no ':'")]
    [InlineData("resourceGroup:PROD#owner@user:alice", "an upper-case id")]
    [InlineData("ResourceGroup:prod#owner@user:alice", "a type starting upper-case")]
    [InlineData("resourceGroup:prod#Owner@user:alice", "a relation starting upper-case")]
    [InlineData("resourceGroup:-prod#owner@user:alice", "an id starting with a hyphen")]
    [InlineData("resourceGroup:#owner@user:alice", "an empty id")]
    [InlineData(":prod#owner@user:alice", "an empty type")]
    [InlineData("resourceGroup:prod#@user:alice", "an empty relation")]
    public void AMalformedTupleIsRejectedWithAnExplanation(string text, string why) {
        var parsed = RelationTuple.Parse(text);

        parsed.IsFailure.ShouldBeTrue($"'{Printable(text)}' ({why}) should not parse");
        parsed.Error!.Message.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void AGuidIdIsWrittenInTheSameNFormAsAGrainKey() {
        var id = Guid.Parse("7f2d4e88-1a3b-4c5d-8e9f-0a1b2c3d4e5f");
        var reference = ObjectRef.Of("resourceGroup", id);

        reference.Id.ShouldBe("7f2d4e881a3b4c5d8e9f0a1b2c3d4e5f");
        reference.ToString().ShouldBe("resourceGroup:7f2d4e881a3b4c5d8e9f0a1b2c3d4e5f");
        ObjectRef.Parse(reference.ToString()).GetValueOrThrow().ShouldBe(reference);
    }

    [Fact]
    public void ANamedSingletonIsAValidObjectIdBecauseTheTenancyLayerAlreadyNeedsOne() {
        // docs/plan/06 § Platform administration already requires `platform:root#operator`, which
        // docs/plan/07 § The model's "ids are GUIDs" does not allow for. See GrainKeys.ObjectRelations.
        var parsed = RelationTuple.Parse("platform:root#operator@user:ops1");

        parsed.IsSuccess.ShouldBeTrue(parsed.Error?.Message);
        parsed.GetValueOrThrow().Object.Id.ShouldBe("root");
    }

    [Fact]
    public void TwoTuplesThatSayTheSameThingAreEqual() {
        // Structural equality is what makes IObjectRelationsGrain's idempotent write idempotent.
        var first = RelationTuple.Parse("resourceGroup:prod#owner@user:alice").GetValueOrThrow();
        var second = RelationTuple.Parse("resourceGroup:prod#owner@user:alice").GetValueOrThrow();

        first.ShouldBe(second);
        first.Subject.ShouldBe(second.Subject);
        first.Object.ShouldBe(second.Object);
    }

    [Fact]
    public void AUsersetSubjectIsNotEqualToItsObjectHalf() {
        // `group:eng` and `group:eng#member` are different subjects and must never collapse: one is
        // "the group itself", the other is "everyone in it".
        SubjectRef.Of("group", "eng").ShouldNotBe(SubjectRef.Userset("group", "eng", "member"));
    }

    static string Printable(string value) =>
        value.Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\0", "\\0", StringComparison.Ordinal);
}
