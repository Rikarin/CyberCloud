using CyberCloud.ResourceGraph.Query;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace CyberCloud.ResourceGraph.Tests.Query;

/// <summary>
///     Every <c>Query/Golden/*.kql</c> translated against one fixed tenant, one fixed caller and
///     one fixed page, compared byte for byte to the <c>.sql</c> beside it — the KQL → SQL contract
///     docs/plan/08 § The resource-graph projection promises, as files a reviewer can read.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             The golden file is the SQL, its parameters and its output columns, and a change to
///             any of the three is a deliberate edit to the file.
///         </b> Set
///         <c>CYBERCLOUD_UPDATE_GOLDEN=1</c> to rewrite every <c>.sql</c> from the current translator
///         and read the diff; the diff is the review. A case's first line may be
///         <c>// offset: N</c> to translate as the page after <c>N</c> rows.
///     </para>
///     <para>
///         ⚠ <b>Two things are asserted on every case beyond the bytes.</b> Every statement carries
///         the two filters no query may lose — <c>is_deleted = 0</c> and the access column bound to
///         the caller's subjects — and no literal from the query text appears in the SQL, only in a
///         parameter. <c>KqlInjectionTests</c> drives the second with hostile literals; this checks it
///         for the ordinary ones.
///     </para>
/// </remarks>
public sealed class KqlTranslationGoldenTests {
    public static readonly Guid Tenant = Guid.Parse("11111111-1111-4111-8111-111111111111");

    public static readonly ImmutableArray<string> Caller = ["user:alice", "group:eng#member"];

    static readonly string GoldenDirectory =
        Path.Combine(RepositoryRoot(), "src", "CyberCloud.ResourceGraph.Tests", "Query", "Golden");

    /// <summary>Every case's file name without its extension, as xUnit theory rows.</summary>
    public static TheoryData<string> Cases {
        get {
            var data = new TheoryData<string>();

            foreach (var file in Directory.GetFiles(GoldenDirectory, "*.kql").Order(StringComparer.Ordinal)) {
                data.Add(Path.GetFileNameWithoutExtension(file));
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void TheTranslationMatchesItsGoldenFile(string name) {
        var kql = File.ReadAllText(Path.Combine(GoldenDirectory, name + ".kql")).ReplaceLineEndings("\n");
        var offset = Offset(kql);

        var translated = KqlTranslator.Translate(kql, Context(offset));

        translated.IsSuccess.ShouldBeTrue(translated.Error?.Message);
        var query = translated.GetValueOrThrow();

        // ── The invariants every case carries ──────────────────────────────────────────────────
        query.Sql.ShouldContain("is_deleted = 0", customMessage: "a tombstone is never a result");
        query.Sql.ShouldContain(
            $"hasAny(access, {{{KqlTranslator.AccessParameter}:Array(String)}})",
            customMessage: "the access filter is not optional"
        );
        query.Sql.ShouldContain(
            ResourceGraphTable.Qualified(Tenant),
            customMessage: "the caller's tenant database and no other"
        );
        query.Parameters.ShouldContain(x => x.Name == KqlTranslator.AccessParameter
            && x.Value == "['user:alice','group:eng#member']"
        );
        query.Sql.ShouldEndWith(
            " LIMIT "
            + (query.PageSize + 1).ToString(CultureInfo.InvariantCulture)
            + (offset > 0 ? " OFFSET " + offset.ToString(CultureInfo.InvariantCulture) : "")
        );

        // ⚠ Every string literal the query spelled is a parameter value and never SQL text. The
        // SQL does carry quotes of its own — concat(provider, '/', type), = '' — so the assertion is
        // about the caller's literals, read back off the parse tree, and not about the quote character.
        foreach (var literal in StringLiterals(kql)) {
            query.Sql.ShouldNotContain(
                "'" + literal + "'",
                customMessage: $"the literal '{literal}' reached the SQL text"
            );
            query.Parameters.ShouldContain(
                x => x.Value.Contains(literal, StringComparison.Ordinal),
                $"the literal '{literal}' is bound by no parameter"
            );
        }

        // ── The bytes ──────────────────────────────────────────────────────────────────────────
        var rendered = Render(query);
        var goldenPath = Path.Combine(GoldenDirectory, name + ".sql");

        if (string.Equals(
                Environment.GetEnvironmentVariable("CYBERCLOUD_UPDATE_GOLDEN"),
                "1",
                StringComparison.Ordinal
            )) {
            File.WriteAllText(goldenPath, rendered, new UTF8Encoding(false));
        }

        File.Exists(goldenPath)
            .ShouldBeTrue($"{name}.sql is missing; run with CYBERCLOUD_UPDATE_GOLDEN=1 to write it, then read it");

        File.ReadAllText(goldenPath)
            .ReplaceLineEndings("\n")
            .ShouldBe(
                rendered,
                $"{name}.kql translates differently from {name}.sql. If the new translation is the intended one, "
                + "rerun with CYBERCLOUD_UPDATE_GOLDEN=1 and review the diff."
            );
    }

    [Fact]
    public void TheGoldenSetCoversEveryOperatorAndFunctionOfTheSubset() {
        // ⚠ A subset with an untranslated member is a refusal nobody meant; this keeps the golden set
        // and KqlSubset the same size. Comparisons are asserted by their KQL spelling, functions and
        // aggregates by name, operators by keyword.
        var corpus = string.Join(
            "\n",
            Directory.GetFiles(GoldenDirectory, "*.kql").Select(File.ReadAllText)
        );

        foreach (var name in KqlSubset.Functions.Concat(KqlSubset.Aggregates)) {
            corpus.ShouldContain(name + "(", customMessage: $"no golden case exercises {name}()");
        }

        foreach (var op in KqlSubset.Operators) {
            corpus.ShouldContain("| " + op, customMessage: $"no golden case exercises '{op}'");
        }

        foreach (var comparison in KqlSubset.Comparisons) {
            corpus.ShouldContain(" " + comparison + " ", customMessage: $"no golden case exercises '{comparison}'");
        }
    }

    /// <summary>The fixed context every case translates under.</summary>
    public static KqlTranslationContext Context(long offset = 0) => new(Tenant, Caller, 50, offset);

    /// <summary>Every non-empty string literal in the query, as the parser reads it (quotes and doubling removed).</summary>
    public static IReadOnlyList<string> StringLiterals(string kql) {
        var literals = new List<string>();
        Collect(Kusto.Language.KustoCode.Parse(kql).Syntax);
        return literals;

        void Collect(Kusto.Language.Syntax.SyntaxNode node) {
            for (var i = 0; i < node.ChildCount; i++) {
                switch (node.GetChild(i)) {
                    case Kusto.Language.Syntax.SyntaxToken {
                        Kind: Kusto.Language.Syntax.SyntaxKind.StringLiteralToken
                    } token when token.ValueText.Length > 0:
                        literals.Add(token.ValueText);
                        break;
                    case Kusto.Language.Syntax.SyntaxNode child:
                        Collect(child);
                        break;
                }
            }
        }
    }

    static long Offset(string kql) {
        const string marker = "// offset:";
        var firstLine = kql.Split('\n', 2)[0].Trim();

        return firstLine.StartsWith(marker, StringComparison.Ordinal)
            ? long.Parse(firstLine[marker.Length..].Trim(), CultureInfo.InvariantCulture)
            : 0;
    }

    static string Render(TranslatedQuery query) {
        var built = new StringBuilder();
        built.Append(query.Sql).Append("\n\n-- parameters\n");

        foreach (var parameter in query.Parameters) {
            built.Append("-- ")
                .Append(parameter.Name)
                .Append(':')
                .Append(parameter.ClickHouseType)
                .Append(" = ")
                .Append(parameter.Value)
                .Append('\n');
        }

        built.Append("\n-- columns\n-- ");
        built.AppendJoin(", ", query.Columns.Select(static x => x.Name + ":" + x.Type));
        built.Append('\n');

        return built.ToString();
    }

    static string RepositoryRoot() {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CyberCloud.slnx"))) {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException(
                "No CyberCloud.slnx above " + AppContext.BaseDirectory + ", so the golden files cannot be found."
            );
    }
}
