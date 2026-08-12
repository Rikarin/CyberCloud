using System.Text.RegularExpressions;

namespace CyberCloud.ResourceManager.Contracts.Tests;

/// <summary>
///     The platform has one quantity parser, and this is what notices when it has two.
/// </summary>
/// <remarks>
///     ⚠ <b>It has had two.</b> <see cref="KubeQuantity" /> was written with the claim that "there is no
///     second implementation to reconcile with", and <c>ValkeyCaches.QuantityBytes</c> was written
///     anyway — the same digits, the same suffix table, in <see langword="double" /> instead of
///     <see langword="decimal" />. Neither author was careless. The Valkey one was written by someone
///     who could not see this assembly's file, because what they <i>could</i> see was
///     <c>ValkeyCaches.QuantityPattern</c>: the grammar was duplicated into every provider, so the
///     obvious place to turn one of those strings into a number was beside the copy, not beside the
///     parser.
///     <para>
///         So the fix was to move the grammar to <see cref="KubeQuantity.Pattern" /> rather than to
///         trust the next author to search harder — and this test is the part that does not depend on
///         anyone searching at all. A review catches a second parser once; a test catches it every
///         time, which is the same argument the assembly-graph rules make about reference sets.
///     </para>
/// </remarks>
public sealed class QuantityParserTests {
    /// <summary>The suffix table is the thing you cannot write a quantity parser without.</summary>
    /// <remarks>
    ///     ⚠ <b>It matches on <c>Ki</c> rather than on a method name, because a second parser will not
    ///     be called <c>TryParse</c>.</b> The one that existed was called <c>QuantityBytes</c> and
    ///     returned <see langword="long" />?. What it could not avoid was mapping the fourteen suffixes
    ///     onto their multipliers, and <c>Ki</c> is the one that pins the intent: it is 1024 where
    ///     <c>k</c> is 1000, so a file spelling out that distinction is converting quantities whatever
    ///     it calls itself.
    ///     <para>
    ///         ⚠ A match is not automatically a defect — it is a question. If the new file really is
    ///         converting quantities, delete it and call <see cref="KubeQuantity.TryParse" />; if it
    ///         needs different rounding or a different unit, do what <c>ValkeyCaches.QuantityBytes</c>
    ///         now does and wrap the one parser rather than repeat it. Only if it is neither does the
    ///         allow-list below grow, and then with the reason written beside it.
    ///     </para>
    /// </remarks>
    [Fact]
    public void OnlyOneFileInTheTreeMapsTheKubernetesSuffixesOntoTheirMultipliers() {
        var table = new Regex("\"Ki\"", RegexOptions.None, TimeSpan.FromSeconds(5));

        var offenders = Directory
            .EnumerateFiles(Path.Combine(RepositoryRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(x => !x.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(x => !x.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(x => Path.GetFileName(x) is not ("KubeQuantity.cs" or "QuantityParserTests.cs"))
            .Where(x => table.IsMatch(File.ReadAllText(x)))
            .Select(x => Path.GetRelativePath(RepositoryRoot(), x))
            .Order(StringComparer.Ordinal)
            .ToArray();

        offenders.ShouldBeEmpty(
            $"these files spell out the Kubernetes suffix table: {string.Join(", ", offenders)}. "
            + "KubeQuantity is the platform's one quantity parser — a provider that needs a quantity "
            + "calls KubeQuantity.TryParse, and a provider that needs different rounding or a "
            + "different unit wraps it the way ValkeyCaches.QuantityBytes wraps it. Two parsers that "
            + "disagree by a rounding rule is a billing defect."
        );
    }

    /// <summary>
    ///     Every copy of the quantity grammar in the tree is the same string as the parser's.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>There are no copies left, and that is exactly why this scan stays.</b>
    ///     <c>ValkeyCaches</c>, <c>PostgresServers</c>, <c>KafkaClusters</c> and the resource manager's
    ///     own <c>TestProvider</c> now alias <see cref="KubeQuantity.Pattern" />, so the only text this
    ///     finds today is the declaration itself. The scan is not thereby vacuous: it is what notices
    ///     the <i>next</i> copy, and a new resource type is the moment one appears, because writing the
    ///     grammar out is easier than knowing the constant exists.
    ///     <para>
    ///         ⚠ <c>KubeQuantity.cs</c> is deliberately NOT excluded. It is what keeps
    ///         <c>ShouldNotBeEmpty</c> below meaningful — a scan that matched nothing would pass just as
    ///         happily if the search were pointed at the wrong directory, which is the failure a
    ///         drift guard cannot afford.
    ///     </para>
    ///     <para>
    ///         Copies are found by their text rather than by referencing the provider assemblies this
    ///         project cannot see — and referencing them to compare two <see langword="const" /> strings
    ///         would not work anyway, because a <see langword="const" /> is inlined and leaves no
    ///         binding reference. That is the same blind spot
    ///         <c>CyberCloud.Providers.DBforPostgreSQL.Contracts.csproj</c> records against rule 2, and
    ///         it is why aliasing the constant does not make this test redundant: the aliases are
    ///         invisible to the assembly graph, so text is still the only evidence.
    ///     </para>
    /// </remarks>
    [Fact]
    public void EveryCopyOfTheQuantityGrammarIsTheGrammarTheParserReads() {
        // The suffix alternation, which is the half a drifting copy would get wrong. ⚠ ASCII letters
        // and pipes only, so `(m|k|M|G|…)?` in a sentence about the grammar is prose rather than a
        // copy of it. A real copy that drifted would still be spelled in ASCII.
        var alternation = new Regex(@"\(m\|k\|[A-Za-z|]*\)\?", RegexOptions.None, TimeSpan.FromSeconds(5));

        KubeQuantity.Pattern.ShouldEndWith("(m|k|M|G|T|P|E|Ki|Mi|Gi|Ti|Pi|Ei)?");

        var copies = Directory
            .EnumerateFiles(Path.Combine(RepositoryRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(x => !x.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(x => !x.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            // ⚠ ChartAnnotationTests is the one allowed copy and it is allowed because it is not a
            // copy: its `(m|k|M|G|Ki|Mi|Gi)?` is a deliberately abbreviated fixture for the
            // `@pattern` emitter, which exists to prove a pattern carrying `|` survives a block that
            // splits `@enum` on that character. Nothing validates a body with it, and it is asserted
            // byte-for-byte on the emitted side, so widening it to the real grammar would be churn in
            // a test about something else.
            .Where(x => Path.GetFileName(x) is not ("QuantityParserTests.cs" or "ChartAnnotationTests.cs"))
            .SelectMany(
                x => alternation.Matches(File.ReadAllText(x))
                    .Select(match => (File: Path.GetRelativePath(RepositoryRoot(), x), match.Value))
            )
            .ToArray();

        copies.ShouldNotBeEmpty("the grammar is declared somewhere, so this scan is looking in the wrong place");

        foreach (var (file, value) in copies) {
            value.ShouldBe(
                "(m|k|M|G|T|P|E|Ki|Mi|Gi|Ti|Pi|Ei)?",
                $"{file} declares a quantity grammar that is not KubeQuantity.Pattern's. A schema that "
                + "validates a suffix set the parser does not read accepts bodies that then fail to "
                + "meter, or refuses ones that would have."
            );
        }
    }

    /// <summary>
    ///     Finds the repository root by walking up from the test assembly.
    /// </summary>
    /// <remarks>
    ///     ⚠ The same walk <c>CyberCloud.AppHost.Tests</c>'s <c>TestPaths</c> does, and for its reason:
    ///     <c>Directory.Build.props</c> redirects every output into <c>artifacts/bin/…</c>, so the depth
    ///     from an assembly to the root is a property of the build layout rather than of the project.
    /// </remarks>
    static string RepositoryRoot() {
        var directory = new DirectoryInfo(Path.GetDirectoryName(typeof(QuantityParserTests).Assembly.Location)!);

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
}
