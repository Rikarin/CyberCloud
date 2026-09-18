using CyberCloud.ResourceGraph.Query;
using System.Collections.Immutable;

namespace CyberCloud.ResourceGraph.Tests.Query;

/// <summary>
///     A literal is a parameter, whatever it contains — the injection half of docs/plan/08 § The
///     resource-graph projection's "every emitted SQL is parameterised".
/// </summary>
/// <remarks>
///     ⚠
///     <b>
///         The assertion is on the SQL text and on the parameter list, never on ClickHouse's
///         behaviour.
///     </b> A statement that ClickHouse happened to refuse would still have carried the
///     caller's text into a position where a different engine version, or a different table, would
///     run it. What these prove is that the text is not there to run.
/// </remarks>
public sealed class KqlInjectionTests {
    const string Payload = "x' OR 1=1; DROP TABLE tenant_11111111111141118111111111111111.resource_graph; --";

    [Theory]
    [InlineData("resources | where name == '{0}'")]
    [InlineData("resources | where tags['{0}'] == 'prod'")]
    [InlineData("resources | where tags.env == '{0}'")]
    [InlineData("resources | where name in ('a', '{0}')")]
    [InlineData("resources | where name contains '{0}'")]
    [InlineData("resources | where name startswith '{0}'")]
    [InlineData("resources | where name =~ '{0}'")]
    [InlineData("resources | extend x = strcat(name, '{0}') | project x")]
    [InlineData("resources | extend x = split(name, '{0}', 0) | project x")]
    [InlineData("resources | summarize count() by label = strcat('{0}', name)")]
    [InlineData("resources | order by strcat(name, '{0}')")]
    public void AStringLiteralIsBoundAndNeverSpelledIntoTheStatement(string template) {
        var kql = template.Replace(
            "{0}",
            Payload.Replace("'", """\'""", StringComparison.Ordinal),
            StringComparison.Ordinal
        );

        var translated = KqlTranslator.Translate(kql, KqlTranslationGoldenTests.Context());
        translated.IsSuccess.ShouldBeTrue(translated.Error?.Message);

        var query = translated.GetValueOrThrow();

        // The payload's fragments are not SQL text …
        query.Sql.ShouldNotContain("DROP");
        query.Sql.ShouldNotContain("1=1");
        query.Sql.ShouldNotContain("--");
        query.Sql.ShouldNotContain(";");

        // … the whole payload is one bound value …
        query.Parameters.ShouldContain(
            x => x.ClickHouseType == "String" && x.Value == Payload,
            "the payload is not bound as a string parameter"
        );

        // … and the statement is still one SELECT over one table in the caller's database.
        query.Sql.Count(static character => character == ';').ShouldBe(0);
        query.Sql.ShouldStartWith("SELECT ");
        query.Sql.Split(" FROM ")
            .Count(static fragment => fragment.Contains(".resource_graph", StringComparison.Ordinal))
            .ShouldBe(1);
    }

    [Fact]
    public void AHasTermWithRegexMetacharactersIsEscapedInsideItsParameter() {
        // ⚠ `has` builds an RE2 pattern from the term, which is the one place a literal is
        // transformed rather than passed through; a term of `.*` must match the two characters and
        // not everything.
        var translated = KqlTranslator.Translate(
            "resources | where name has '.*(a|b)$'",
            KqlTranslationGoldenTests.Context()
        );
        translated.IsSuccess.ShouldBeTrue(translated.Error?.Message);

        var pattern = translated.GetValueOrThrow().Parameters.Single(static x => x.Name == "p0").Value;

        pattern.ShouldBe("""(?i)(^|[^\p{L}\p{N}_])\.\*\(a\|b\)\$($|[^\p{L}\p{N}_])""");
        translated.GetValueOrThrow().Sql.ShouldContain("match(name, {p0:String})");
    }

    [Fact]
    public void TheAccessSubjectsAreOneArrayParameterWithQuotesEscaped() {
        var hostile = ImmutableArray.Create("user:o'brien", "group:eng#member", """user:back\slash""");

        var translated = KqlTranslator.Translate(
            "resources | count",
            new(KqlTranslationGoldenTests.Tenant, hostile, 10, 0)
        );
        translated.IsSuccess.ShouldBeTrue(translated.Error?.Message);

        var access = translated.GetValueOrThrow()
            .Parameters.Single(static x => x.Name == KqlTranslator.AccessParameter);

        access.ClickHouseType.ShouldBe("Array(String)");
        access.Value.ShouldBe("""['user:o\'brien','group:eng#member','user:back\\slash']""");
        translated.GetValueOrThrow().Sql.ShouldNotContain("o'brien");
    }

    [Fact]
    public void TheOnlyIdentifierInterpolatedIsTheTenantDatabaseAndItIsDerivedFromTheGuid() {
        var translated = KqlTranslator.Translate("resources | project name", KqlTranslationGoldenTests.Context());
        var sql = translated.GetValueOrThrow().Sql;

        sql.ShouldContain("FROM tenant_11111111111141118111111111111111.resource_graph FINAL");

        // A second tenant is a second database and nothing else changes.
        var other = Guid.Parse("22222222-2222-4222-8222-222222222222");
        var otherSql = KqlTranslator.Translate(
            "resources | project name",
            new(other, KqlTranslationGoldenTests.Caller, 50, 0)
        ).GetValueOrThrow().Sql;

        otherSql.ShouldBe(
            sql.Replace(
                "tenant_11111111111141118111111111111111",
                "tenant_22222222222242228222222222222222",
                StringComparison.Ordinal
            )
        );
    }

    [Fact]
    public void ACallerWithNoSubjectsIsAProgrammingErrorAndNotAnEmptyFilter() {
        // ⚠ An empty hasAny() matches nothing, which is the right answer for a caller nothing was
        // granted to — and the caller ALWAYS has their own subject, so an empty array can only be a
        // bug in the resolver, and a bug that silently hid every row would be found by nobody.
        Should.Throw<ArgumentException>(static () => KqlTranslator.Translate(
                "resources",
                new(KqlTranslationGoldenTests.Tenant, [], 50, 0)
            )
        );
    }
}
