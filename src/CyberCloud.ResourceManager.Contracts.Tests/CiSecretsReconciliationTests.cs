using System.Text.RegularExpressions;

namespace CyberCloud.ResourceManager.Contracts.Tests;

/// <summary>
///     docs/plan/23 § CI secrets claims to list every secret the workflows under
///     <c>.github/workflows/</c> read. This is what notices when that claim stops being true.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             A job that skips for want of a secret points its reader at that section by name
///         </b> — every <c>skipped: … — docs/plan/23 § CI secrets</c> step does — so a secret the
///         section does not list is a skip nobody can act on, which is #25's original complaint
///         ("they need secrets and targets that do not exist") one level up: the secret would exist
///         as a name in a YAML file and nowhere anybody configuring the repository would look.
///     </para>
///     <para>
///         ⚠ <b>Both directions, and each catches a different mistake.</b> A secret referenced and
///         not listed is the one above. A secret listed and not referenced is a row that survived
///         the job it unlocked — somebody follows it, creates the credential, and nothing changes.
///     </para>
///     <para>
///         ⚠ <b>Why this project.</b> The same reason <c>RoadmapReconciliationTests</c> gives: it
///         compares one checked-in text against another, needs no compiled assembly graph, and this
///         project is where that shape already lives (<c>QuantityParserTests</c> reads <c>src/</c>).
///         A seventeenth architecture gate would change a counted census for a check that needs
///         nothing the gates have.
///     </para>
///     <para>
///         ⚠ The repository root is resolved inside the test bodies and never from a static
///         initialiser — #82, as in <c>RoadmapReconciliationTests</c>.
///     </para>
/// </remarks>
public sealed class CiSecretsReconciliationTests {
    /// <summary>The heading whose table is the list, matched in full so it cannot drift.</summary>
    const string SecretsHeading = "## CI secrets";

    /// <summary>The next heading at the same level, which ends the section.</summary>
    const string NextHeading = "## Environments and rollout";

    /// <summary>
    ///     Every <c>${{ secrets.NAME }}</c> a workflow reads is a row of docs/plan/23 § CI secrets,
    ///     and every row is read by a workflow.
    /// </summary>
    /// <remarks>
    ///     ⚠ <c>GITHUB_TOKEN</c> is excluded from both sides. The workflows read it as
    ///     <c>github.token</c>, it is issued by the runner rather than configured by anybody, and a
    ///     row for it would be a row nobody can act on — the opposite of what the table is for.
    /// </remarks>
    [Fact]
    public void EverySecretAWorkflowReadsIsListedInThePlanAndEveryListedSecretIsRead() {
        var root = RepositoryRoot();

        var referenced = ReferencedSecrets(root);
        var listed = ListedSecrets(root);

        referenced.ShouldNotBeEmpty(
            ".github/workflows/ reads no secret at all, so the grep over `${{ secrets.NAME }}` has "
            + "stopped matching — main.yml alone reads four."
        );

        listed.ShouldNotBeEmpty(
            $"docs/plan/23 has no backticked secret names between '{SecretsHeading}' and "
            + $"'{NextHeading}'. If the section has been reshaped, reshape this test with it rather "
            + "than deleting it."
        );

        referenced.Except(listed, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ShouldBeEmpty(
                "a workflow under .github/workflows/ reads a secret that docs/plan/23 § CI secrets "
                + "does not list. Every `skipped: … — docs/plan/23 § CI secrets` step sends its reader "
                + "to that table; add a row saying what the secret unlocks and who sets it."
            );

        listed.Except(referenced, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ShouldBeEmpty(
                "docs/plan/23 § CI secrets lists a secret no workflow reads. Somebody following that "
                + "row would create a credential nothing consumes; delete the row or the job it "
                + "describes has lost its reference."
            );
    }

    /// <summary>
    ///     Every job that gates on secrets has the visible half of the skip: a step whose name starts
    ///     <c>skipped:</c> and cites docs/plan/23 § CI secrets.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ This is the property that lets <c>gate-on-secrets.sh</c> skip at all — its header
    ///         lists three, and this is the first. A gate call with no named skip step beside it is a
    ///         green job whose step list says nothing about what did not run, which is the shape
    ///         <c>require-secrets.sh</c> was written to refuse.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Per job, not per file.</b> The first version counted gate calls and
    ///         <c>skipped:</c> steps across a whole workflow and compared the totals, so a file with
    ///         two gated jobs, one of them carrying two skip steps and the other none, passed — the
    ///         property the script header promises is "a named skip beside every call", and a total
    ///         cannot assert "beside". #25's review found it. The jobs are cut by
    ///         <see cref="JobHeader" /> — a two-space-indented key under <c>jobs:</c>, which is how
    ///         every workflow here is laid out — and the counts are compared inside each cut.
    ///     </para>
    /// </remarks>
    [Fact]
    public void EveryJobThatGatesOnSecretsHasANamedSkippedStep() {
        var root = RepositoryRoot();
        var workflows = WorkflowFiles(root);

        workflows.ShouldNotBeEmpty("no workflow files under .github/workflows/");

        var gated = new List<string>();
        var withoutSkip = new List<string>();

        foreach (var file in workflows) {
            var name = Path.GetFileName(file);

            foreach (var (job, text) in Jobs(File.ReadAllText(file))) {
                var gates = GateCall.Matches(text).Select(static x => x.Groups["label"].Value).ToArray();
                var skips = SkippedStep.Count(text);

                gated.AddRange(gates.Select(x => $"{name} / {job} ({x})"));

                if (gates.Length > skips) {
                    withoutSkip.Add(
                        $"{name} / {job}: {gates.Length} gate-on-secrets call(s), {skips} `skipped:` step(s)"
                    );
                }
            }
        }

        gated.ShouldNotBeEmpty(
            "no job in any workflow calls .github/scripts/gate-on-secrets.sh, so either the gate regex "
            + "or the job cut has stopped matching — main.yml's images job calls it."
        );

        withoutSkip.ShouldBeEmpty(
            "a job gates on secrets without a step named `skipped: … — docs/plan/23 § CI secrets` in "
            + "the same job. The named step is what makes the skip visible in that job's step list; "
            + "a skip step in another job of the same file does not stand in for it."
        );
    }

    /// <summary>
    ///     The jobs of a workflow file: each job id paired with the text from its header to the next
    ///     job's header.
    /// </summary>
    /// <remarks>
    ///     ⚠ Text before <c>jobs:</c> is dropped rather than attributed to a job, so the header
    ///     comment that mentions the gate script by name is counted nowhere — the same reason
    ///     <see cref="GateCall" /> is anchored to the start of a line.
    /// </remarks>
    static IEnumerable<(string Job, string Text)> Jobs(string workflow) {
        var start = workflow.IndexOf("\njobs:", StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, "a workflow file has no `jobs:` key at column zero.");

        var body = workflow[start..];
        var headers = JobHeader.Matches(body);

        headers.Count.ShouldBeGreaterThan(
            0,
            "a workflow file has a `jobs:` key and no two-space-indented job under it."
        );

        for (var i = 0; i < headers.Count; i++) {
            var from = headers[i].Index;
            var to = i + 1 < headers.Count ? headers[i + 1].Index : body.Length;

            yield return (headers[i].Groups["id"].Value, body[from..to]);
        }
    }

    /// <summary>Every distinct <c>NAME</c> in a <c>secrets.NAME</c> expression across the workflows.</summary>
    static HashSet<string> ReferencedSecrets(string root) =>
        WorkflowFiles(root)
            .SelectMany(static file => SecretReference.Matches(File.ReadAllText(file))
                    .Select(static x => x.Groups["name"].Value)
            )
            .Where(static x => x != "GITHUB_TOKEN")
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>Every backticked upper-case name in the first column of the § CI secrets table.</summary>
    /// <remarks>
    ///     ⚠ The first column only. The other columns and the prose mention environment variables
    ///     and job names in backticks too, and a match over the whole section would count
    ///     <c>GITHUB_STEP_SUMMARY</c> as a secret.
    /// </remarks>
    static HashSet<string> ListedSecrets(string root) {
        var lines = File.ReadAllLines(Path.Combine(root, "docs", "plan", "23-build-ci-and-testing.md"));

        var start = Array.FindIndex(lines, static x => x.StartsWith(SecretsHeading, StringComparison.Ordinal));
        start.ShouldBeGreaterThanOrEqualTo(0, $"docs/plan/23 no longer has a '{SecretsHeading}' heading.");

        var end = Array.FindIndex(lines, start + 1, static x => x.StartsWith(NextHeading, StringComparison.Ordinal));
        end.ShouldBeGreaterThan(
            start,
            $"docs/plan/23 no longer has a '{NextHeading}' heading after '{SecretsHeading}'."
        );

        return lines
            .Skip(start)
            .Take(end - start)
            .Where(static x => x.StartsWith('|'))
            .Select(static x => x.Split('|')[1])
            .SelectMany(static cell => SecretName.Matches(cell).Select(static x => x.Groups["name"].Value))
            .ToHashSet(StringComparer.Ordinal);
    }

    static string[] WorkflowFiles(string root) =>
        Directory
            .EnumerateFiles(Path.Combine(root, ".github", "workflows"), "*.yml")
            .Order(StringComparer.Ordinal)
            .ToArray();

    /// <summary><c>secrets.NAME</c> inside an expression.</summary>
    static readonly Regex SecretReference = new(
        @"secrets\.(?<name>[A-Z][A-Z0-9_]*)",
        RegexOptions.None,
        TimeSpan.FromSeconds(5)
    );

    /// <summary>A backticked upper-case name — the shape of a secret in the table's first column.</summary>
    static readonly Regex SecretName = new(
        "`(?<name>[A-Z][A-Z0-9_]*)`",
        RegexOptions.None,
        TimeSpan.FromSeconds(5)
    );

    /// <summary>A call of the gate script at the start of a run line, with the label it was given.</summary>
    /// <remarks>
    ///     ⚠ Anchored to the start of a line so a comment that mentions the script by name — main.yml's
    ///     header does — is not counted as a gate. The first version was not anchored and counted three
    ///     gates in a file with two.
    /// </remarks>
    static readonly Regex GateCall = new(
        @"^\s*\.github/scripts/gate-on-secrets\.sh\s+(?<label>[A-Za-z0-9_-]+)",
        RegexOptions.Multiline,
        TimeSpan.FromSeconds(5)
    );

    /// <summary>A job header: a two-space-indented key on a line of its own, under <c>jobs:</c>.</summary>
    /// <remarks>
    ///     ⚠ Exactly two spaces, anchored to the line. A step's <c>with:</c> or <c>env:</c> sits at six
    ///     or more, and the workflow's own top-level keys sit at zero; the only two-space keys after
    ///     <c>jobs:</c> in these files are job ids, which is what <see cref="Jobs" /> relies on.
    /// </remarks>
    static readonly Regex JobHeader = new(
        @"^  (?<id>[A-Za-z0-9_-]+):[ \t\r]*$",
        RegexOptions.Multiline,
        TimeSpan.FromSeconds(5)
    );

    /// <summary>A step named for a skip, citing the section.</summary>
    static readonly Regex SkippedStep = new(
        @"- name: ""skipped: .* — docs/plan/23 § CI secrets""",
        RegexOptions.None,
        TimeSpan.FromSeconds(5)
    );

    /// <summary>
    ///     Finds the repository root by walking up from the test assembly.
    /// </summary>
    /// <remarks>
    ///     ⚠ The same walk <c>RoadmapReconciliationTests</c> does, for its reason: the depth from an
    ///     assembly to the root is a property of the build layout, and the walk is called from a test
    ///     body rather than a static initialiser — #82.
    /// </remarks>
    static string RepositoryRoot() {
        var directory = new DirectoryInfo(
            Path.GetDirectoryName(typeof(CiSecretsReconciliationTests).Assembly.Location)!
        );

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
