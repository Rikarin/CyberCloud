using System.Globalization;
using System.Text.RegularExpressions;

namespace CyberCloud.ResourceManager.Contracts.Tests;

/// <summary>
///     docs/plan/24 § What has landed claims a set of published resource types and a count of them.
///     This is what notices when that claim stops being true.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The roadmap's phase tables were stale for weeks and nothing said so</b> (#45): four M2
///         rows and two M3 rows had shipped, the running total still counted them as work, and the
///         only way to find out was for somebody to read two documents side by side. A reconciliation
///         done by hand is true on the day it is done; this is the part that stays true.
///     </para>
///     <para>
///         ⚠ <b>Why a test and not a seventeenth architecture gate.</b> The gates in
///         <c>build/Build.Architecture.cs</c> are counted — "the ten in docs/plan/23 § The
///         architecture gates, plus …" is a sentence with a number in it, quoted in several places —
///         so adding one is a change to that census as well as to the build. This assertion needs
///         neither the compiled assembly graph nor the provider registry: it compares one checked-in
///         generated document against one checked-in prose document, which is exactly the shape
///         <c>QuantityParserTests</c> already established in this project.
///     </para>
///     <para>
///         ⚠ <b>The published document is the right side of the comparison, not the provider
///         registry.</b> The registry and the document are already tied together byte-for-byte by the
///         <b>Generated surfaces</b> gate, so reading <c>openapi/</c> inherits that guarantee and
///         costs this project no reference to any provider assembly — which it could not take
///         anyway, being on the contracts side of docs/plan/03 § The .Contracts split.
///     </para>
///     <para>
///         ⚠ <b>The repository root is resolved inside the test bodies and never from a static
///         initialiser</b>, which is #82 exactly: a static property that walks for
///         <c>CyberCloud.slnx</c> turns a missing source tree into a
///         <c>TypeInitializationException</c> that fails every test in the class rather than the ones
///         that needed the tree.
///     </para>
/// </remarks>
public sealed class RoadmapReconciliationTests {
    /// <summary>The one published api-version document, which docs/plan/24's pinned recount names.</summary>
    /// <remarks>
    ///     ⚠ Api-versions are dates and are immutable (docs/plan/08 § The provider registry), so a
    ///     second document appearing is a real event and not a rename — and it changes what "the
    ///     published resource types" means, because the roadmap counts them once rather than per
    ///     version. <see cref="TheRoadmapsPinnedRecountIsTheNumberTheCommandProduces" /> fails when
    ///     one appears, on purpose.
    /// </remarks>
    const string ApiVersionDocument = "2026-08-01.json";

    /// <summary>The heading row of the table this class checks, matched in full so it cannot drift.</summary>
    const string LandedTableHeader = "| Phase | Published types | Count |";

    /// <summary>
    ///     The set of resource types the roadmap's § What has landed table lists is exactly the set
    ///     the published api-version document declares.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Set equality, in both directions, and each direction catches a different
    ///         mistake.</b> A type in the document and not the table is the failure this issue was
    ///         filed about — a provider ships and the roadmap keeps costing it as future work. A type
    ///         in the table and not the document is the opposite and is worse to read: a row marked
    ///         ✅ <i>shipped ahead of phase</i> for something that was deleted, renamed, or never
    ///         published at all.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>This is why that table spells every type out.</b> An earlier draft abbreviated
    ///         the children — <c>…/subnets</c> under its parent — which reads well and matches
    ///         nothing, so the check would have passed by finding fewer names than it should and
    ///         nobody would have seen it. A guard that can be defeated by prose formatting is not a
    ///         guard, and the note above the table says so where the next editor will read it.
    ///     </para>
    /// </remarks>
    [Fact]
    public void TheRoadmapsLandedTableIsExactlyTheSetOfPublishedResourceTypes() {
        var root = RepositoryRoot();
        var published = PublishedResourceTypes(root);
        var rows = LandedTableRows(root);

        var listed = rows
            .SelectMany(x => QualifiedName.Matches(x).Select(match => match.Groups["name"].Value))
            .ToArray();

        listed
            .GroupBy(x => x, StringComparer.Ordinal)
            .Where(x => x.Count() > 1)
            .Select(x => x.Key)
            .ShouldBeEmpty("a resource type is listed twice in docs/plan/24 § What has landed, so its per-phase counts cannot both be right");

        listed
            .Order(StringComparer.Ordinal)
            .ShouldBe(
                published.Select(Unqualified).Order(StringComparer.Ordinal),
                "docs/plan/24 § What has landed no longer lists the resource types "
                + $"openapi/{ApiVersionDocument} publishes. A type published and not listed is the "
                + "drift #45 was filed about — work that has landed still being costed as future "
                + "work in § Running total. A type listed and not published is a row marked shipped "
                + "for something that is not there. Reconcile the phase tables and re-derive the "
                + "totals; do not delete a shipped row to make this pass."
            );
    }

    /// <summary>
    ///     Both numbers the roadmap pins — the per-phase counts, and the output of the recount
    ///     command it quotes — equal the number of published resource types.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>"The per-phase counts" means each row against its own names, not merely their
    ///         sum</b> — #45's review, which is the second time on this branch that a guard was
    ///         described in prose as one assertion wider than it was written. A sum that holds while
    ///         two rows are wrong in opposite directions is exactly the failure a total hides.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The pinned command output is the half most likely to rot and the half nobody
    ///         re-runs.</b> This tree has shipped pinned counts that did not reproduce more than
    ///         once — #81 was four of them, three sitting in the machinery that gates citation
    ///         honesty, and #78's review found a <c>grep</c> that was counting its own paragraph. A
    ///         number inside a fenced block looks like evidence, which is precisely why a stale one
    ///         is expensive.
    ///     </para>
    /// </remarks>
    [Fact]
    public void TheRoadmapsPinnedRecountIsTheNumberTheCommandProduces() {
        var root = RepositoryRoot();
        var published = PublishedResourceTypes(root);

        var documents = new DirectoryInfo(Path.Combine(root, "openapi"))
            .EnumerateFiles("????-??-??.json")
            .Select(x => x.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        const string OneDocument =
            "docs/plan/24's recount reads one api-version document by name and counts each type "
            + "once. A second published api-version means the roadmap is counting one document's "
            + "types and calling them the platform's — re-derive the recount before widening this "
            + "test. Api-versions are immutable (docs/plan/08 § The provider registry), so this is a "
            + "real event rather than a rename.";

        documents.Length.ShouldBe(1, OneDocument);
        documents[0].ShouldBe(ApiVersionDocument, OneDocument);

        var lines = RoadmapLines(root);

        // ── The per-phase table: the rows sum to the total, and the total is the published count ──
        var rows = LandedTableRows(root);
        var total = rows.Where(x => x.Contains("**Total**", StringComparison.Ordinal)).ToArray();
        var phases = rows.Where(x => !x.Contains("**Total**", StringComparison.Ordinal)).ToArray();

        total.Length.ShouldBe(1, $"docs/plan/24's '{LandedTableHeader}' table has no single **Total** row to check");
        phases.ShouldNotBeEmpty("docs/plan/24's § What has landed table lists no phases");

        phases.Sum(CountCell).ShouldBe(
            published.Length,
            "docs/plan/24 § What has landed's per-phase counts no longer add up to the number of "
            + $"resource types openapi/{ApiVersionDocument} publishes"
        );

        CountCell(total[0]).ShouldBe(
            published.Length,
            "docs/plan/24 § What has landed's **Total** row is not the number of published resource types"
        );

        // ⚠ EACH ROW'S OWN COUNT, not only the sum of them — added by #45's review, which found the
        // document advertising this check one assertion wider than it was. The sum and the **Total**
        // can both be right while the split across phases is wrong: all 22 correct names, listed
        // exactly once each, with phase 2 reading 14 and phase 3 reading 5, passed every assertion
        // above. And the split is not decoration — the prose around the table leans on it ("two of
        // the 22 belong to phase 4 and one is phase 1's deliberately trivial sample", and phase 3's
        // "four of them are this phase's `Data` row"), and § Running total's per-phase ✅ figures are
        // read off it. A count cell nobody checks against the row it sits on is the same shape of
        // claim as a pinned `grep` nobody re-runs.
        foreach (var phase in phases) {
            CountCell(phase).ShouldBe(
                QualifiedName.Count(phase),
                "a row of docs/plan/24 § What has landed counts a different number of resource types "
                + "than it names. The count cell and the backticked names in the same row have to "
                + "agree before the per-phase split means anything — recount that row rather than "
                + $"adjusting another to keep the total at {published.Length}. The row: {phase}"
            );
        }

        // ── The pinned command, and the number printed under it ───────────────────────────────────
        var opens = Array.FindIndex(lines, x => string.Equals(x, "```console", StringComparison.Ordinal));

        opens.ShouldBeGreaterThanOrEqualTo(
            0,
            "docs/plan/24 no longer contains the ```console block holding the recount command. The "
            + "command is the whole point of pinning the number — without it the 22 is a claim rather "
            + "than a result."
        );

        var closes = Array.FindIndex(lines, opens + 1, x => string.Equals(x, "```", StringComparison.Ordinal));

        closes.ShouldBeGreaterThan(opens + 1, "docs/plan/24's recount block is empty or unterminated");

        var block = lines[(opens + 1)..closes];

        block[0].ShouldContain(
            "x-cybercloud-resource-type",
            Case.Sensitive,
            "docs/plan/24's ```console block is no longer the resource-type recount"
        );

        string.Join(' ', block).ShouldContain(
            ApiVersionDocument,
            Case.Sensitive,
            $"docs/plan/24's recount command no longer names openapi/{ApiVersionDocument}"
        );

        int.Parse(block[^1].Trim(), CultureInfo.InvariantCulture).ShouldBe(
            published.Length,
            "the output pinned under docs/plan/24's recount command is not what the command produces "
            + "today. Re-run it against this tree and paste what it printed — a fenced number that "
            + "nobody re-ran is the failure #81 and #78 were both filed about."
        );
    }

    /// <summary>Every distinct <c>x-cybercloud-resource-type</c> in the published document.</summary>
    static string[] PublishedResourceTypes(string root) {
        var document = Path.Combine(root, "openapi", ApiVersionDocument);

        File.Exists(document).ShouldBeTrue($"openapi/{ApiVersionDocument} is the document docs/plan/24 counts, and it is not there");

        var types = PublishedType
            .Matches(File.ReadAllText(document))
            .Select(x => x.Groups["type"].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        // ⚠ THE EMPTY-INPUT GUARD, and this class needs it more than most. Every assertion above is
        // "does the prose match this set", so a set read from a moved or renamed document would make
        // the roadmap look wrong when it is right — or, if the prose table were ever emptied too,
        // make two empty sets agree and report a tick over nothing.
        types.ShouldNotBeEmpty(
            $"openapi/{ApiVersionDocument} declares no x-cybercloud-resource-type at all, so nothing "
            + "below is comparing the roadmap against anything"
        );

        return types;
    }

    /// <summary>The body rows of docs/plan/24 § What has landed's phase table, separator excluded.</summary>
    static string[] LandedTableRows(string root) {
        var lines = RoadmapLines(root);
        var header = Array.FindIndex(lines, x => x.StartsWith(LandedTableHeader, StringComparison.Ordinal));

        header.ShouldBeGreaterThanOrEqualTo(
            0,
            $"docs/plan/24 no longer has a '{LandedTableHeader}' table. That table is the reconciliation "
            + "#45 asked for; if it has been reshaped, reshape this test with it rather than deleting it."
        );

        // header + 1 is the |---|---|---| separator every Markdown table carries.
        return lines
            .Skip(header + 2)
            .TakeWhile(x => x.StartsWith('|'))
            .ToArray();
    }

    static string[] RoadmapLines(string root)
        => File.ReadAllLines(Path.Combine(root, "docs", "plan", "24-roadmap.md"));

    /// <summary>The last cell of a Markdown row, as an integer, with any bold markers removed.</summary>
    static int CountCell(string row) {
        var cells = row.Split('|', StringSplitOptions.TrimEntries);

        // "| a | b | c |" splits to ["", "a", "b", "c", ""], so the last cell is the penultimate item.
        return int.Parse(cells[^2].Replace("*", string.Empty, StringComparison.Ordinal), CultureInfo.InvariantCulture);
    }

    /// <summary>A resource type without its <c>CyberCloud.</c> prefix, which is how the table spells it.</summary>
    static string Unqualified(string type) {
        type.ShouldStartWith(
            "CyberCloud.",
            Case.Sensitive,
            "every published resource type is under the CyberCloud. namespace"
        );

        return type["CyberCloud.".Length..];
    }

    /// <summary>
    ///     Finds the repository root by walking up from the test assembly.
    /// </summary>
    /// <remarks>
    ///     ⚠ The same walk <c>QuantityParserTests</c> does in this project, and for its reason:
    ///     <c>Directory.Build.props</c> redirects every output into <c>artifacts/bin/…</c>, so the
    ///     depth from an assembly to the root is a property of the build layout rather than of the
    ///     project. ⚠ Called from a test body rather than a static initialiser — #82.
    /// </remarks>
    static string RepositoryRoot() {
        var directory = new DirectoryInfo(Path.GetDirectoryName(typeof(RoadmapReconciliationTests).Assembly.Location)!);

        while (directory is not null) {
            if (File.Exists(Path.Combine(directory.FullName, "CyberCloud.slnx"))) {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "No CyberCloud.slnx above the test assembly, so the repository root cannot be found."
        );
    }

    /// <summary>One <c>x-cybercloud-resource-type</c> entry in a published OpenAPI document.</summary>
    static readonly Regex PublishedType = new(
        "\"x-cybercloud-resource-type\":\\s*\"(?<type>[^\"]+)\"",
        RegexOptions.None,
        TimeSpan.FromSeconds(5)
    );

    /// <summary>
    ///     A backticked resource type in the table — a namespace tail and at least one slashed
    ///     segment, so that ordinary backticked prose in the same cell is not mistaken for one.
    /// </summary>
    static readonly Regex QualifiedName = new(
        "`(?<name>[A-Za-z][A-Za-z0-9]*(?:/[A-Za-z0-9]+)+)`",
        RegexOptions.None,
        TimeSpan.FromSeconds(5)
    );
}
