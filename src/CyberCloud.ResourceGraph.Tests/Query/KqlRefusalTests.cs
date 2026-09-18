using CyberCloud.ResourceGraph.Query;

namespace CyberCloud.ResourceGraph.Tests.Query;

/// <summary>
///     Everything outside the subset is refused by name, with the supported list in the message —
///     docs/plan/08 § The resource-graph projection, and ADR-011's <i>"state the supported subset
///     explicitly"</i> applied to a language.
/// </summary>
/// <remarks>
///     ⚠ <b>Each row asserts the token the refusal names and not only that a refusal happened.</b> A
///     translator that refused everything would pass a weaker test; this one has to name
///     <c>mv-expand</c> for an <c>mv-expand</c> and <c>ago</c> for an <c>ago</c>, because the reader of
///     the message is looking up one word in the list that follows it.
/// </remarks>
public sealed class KqlRefusalTests {
    [Theory]
    // Tabular operators outside the subset.
    [InlineData("resources | mv-expand tags", "mv-expand")]
    [InlineData("resources | join (resources) on name", "join")]
    [InlineData("resources | union resources", "union")]
    [InlineData("resources | top 3 by name", "top")]
    [InlineData("resources | project-away version", "project-away")]
    [InlineData("resources | project-rename n = name", "project-rename")]
    [InlineData("resources | search 'x'", "search")]
    [InlineData("resources | parse name with * '-' suffix", "parse")]
    [InlineData("resources | render table", "render")]
    // Comparisons outside the subset.
    [InlineData("resources | where name matches regex 'a.*'", "matches regex")]
    [InlineData("resources | where name contains_cs 'x'", "contains_cs")]
    [InlineData("resources | where name has_cs 'x'", "has_cs")]
    [InlineData("resources | where name !has 'x'", "!has")]
    [InlineData("resources | where name !contains 'x'", "!contains")]
    [InlineData("resources | where name !in ('a')", "!in")]
    [InlineData("resources | where name in~ ('a')", "in~")]
    [InlineData("resources | where name has_any ('a', 'b')", "has_any")]
    [InlineData("resources | where version between (1 .. 2)", "between")]
    [InlineData("resources | where version + 1 > 2", "+")]
    [InlineData("resources | where version * 2 > 2", "*")]
    // Scalar functions outside the subset.
    [InlineData("resources | where createdAt > ago(1d)", "ago")]
    [InlineData("resources | extend t = now()", "now")]
    [InlineData("resources | summarize count() by bin(createdAt, 1d)", "bin")]
    [InlineData("resources | extend x = extract('a(.*)', 1, name)", "extract")]
    [InlineData("resources | extend x = strlen(name)", "strlen")]
    [InlineData("resources | extend x = iff(version > 1, 'a', 'b')", "iff")]
    [InlineData("resources | extend x = case(version > 1, 'a', 'b')", "case")]
    [InlineData("resources | where isnull(name)", "isnull")]
    // Aggregates outside the subset, and an aggregate in the wrong place.
    [InlineData("resources | summarize percentile(version, 50)", "percentile")]
    [InlineData("resources | summarize make_list(name)", "make_list")]
    [InlineData("resources | summarize any(name)", "any")]
    [InlineData("resources | where count() > 1", "'count'")]
    // Statements and shapes that are not one pipe over the table.
    [InlineData("let x = 1; resources | take x", "let")]
    [InlineData("print 1", "print")]
    [InlineData("resources; resources", "2 statements")]
    [InlineData("resources | order by name nulls first", "nulls first")]
    [InlineData("resources | distinct *", "distinct *")]
    [InlineData("resources | take 1h", "1h")]
    public void AnOperatorOrFunctionOutsideTheSubsetIsRefusedByName(string kql, string named) {
        var refused = KqlTranslator.Translate(kql, KqlTranslationGoldenTests.Context());

        refused.IsFailure.ShouldBeTrue($"'{kql}' was translated: {(refused.IsSuccess ? refused.GetValueOrThrow().Sql : "")}");
        refused.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        refused.Error.Message.ShouldContain(named, customMessage: $"the refusal for '{kql}' does not name '{named}': {refused.Error.Message}");
        refused.Error.Message.ShouldContain("resource graph's KQL subset", customMessage: "the refusal does not carry the supported list");
        refused.Error.Message.ShouldContain("docs/plan/08", customMessage: "the refusal does not say where the subset is written down");
    }

    [Theory]
    // The binder's own sentences, for names it does not know.
    [InlineData("resources | where foo == 1", "'foo'")]
    [InlineData("widgets | take 1", "'widgets'")]
    [InlineData("resources | where access has 'user:alice'", "'access'")]
    [InlineData("resources | where is_deleted == 1", "'is_deleted'")]
    // The translator's type rules.
    [InlineData("resources | where name == 1", "compares a string with a long")]
    [InlineData("resources | where tolower(version) == '1'", "'tolower' takes a string")]
    [InlineData("resources | where tags == 'x'", "compares the whole tag map")]
    [InlineData("resources | where name has location", "'has' takes a string literal")]
    [InlineData("resources | where version.major == 1", "not a property bag")]
    [InlineData("resources | project ['weird name'] = name", "'weird name' is not a column name")]
    [InlineData("resources | project name, name", "already declared")]
    [InlineData("resources | take -1", "whole number")]
    [InlineData("resources | where name", "must have the type bool")]
    [InlineData("", "The query is empty")]
    [InlineData("   ", "The query is empty")]
    public void AQueryTheBinderOrTheTypeRulesRejectSaysWhat(string kql, string sentence) {
        var refused = KqlTranslator.Translate(kql, KqlTranslationGoldenTests.Context());

        refused.IsFailure.ShouldBeTrue($"'{kql}' was translated");
        refused.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        refused.Error.Message.ShouldContain(sentence, customMessage: refused.Error.Message);
    }

    [Fact]
    public void TheAccessAndTombstoneColumnsAreNotInTheLanguage() {
        // ⚠ The property the whole filter rests on: the caller cannot name the columns the base
        // SELECT filters on, so no `where` can widen what they see. The binder does not know them
        // because the schema does not declare them.
        ResourceGraphSchema.ByName.ShouldNotContainKey("access");
        ResourceGraphSchema.ByName.ShouldNotContainKey("is_deleted");
        ResourceGraphSchema.ByName.ShouldNotContainKey("tenant_id");
        ResourceGraphSchema.ByName.ShouldNotContainKey("desired_hash");
    }

    [Fact]
    public void TheSupportedSentenceNamesEveryListOnce() {
        foreach (var name in KqlSubset.Operators.Concat(KqlSubset.Aggregates).Concat(KqlSubset.Functions).Concat(KqlSubset.Comparisons)) {
            KqlSubset.SupportedSentence.ShouldContain(name);
        }

        KqlSubset.SupportedSentence.ShouldContain("docs/plan/08 § The resource-graph projection");
    }
}
