// Portal — docs/plan/23 § Build, row `Portal`:
// "pnpm install/lint/test/build, performance budget, axe".
// docs/plan/23 § Test layers, row `Portal`, puts it on every PR: "Journeys pass, budgets met".
//
// ── ⚠ WHY THIS INVOKES ONE pnpm SCRIPT RATHER THAN SPELLING THE GATE OUT AGAIN IN C# ─────────────
//
// portal/package.json already composes exactly the row above:
//
//   gates  = node:gate && verify
//   verify = lint && test && build
//   build  = node:check && ng build portal --configuration production && budget && test:ssr
//
// Restating that sequence here would give the repository two definitions of "the portal gate", and
// the way that goes wrong is a developer running `pnpm verify` in portal/, seeing green, and
// pushing into a CI that runs one thing more. .github/workflows/gate.yml makes the same argument
// one level up about `./build.sh <Target>` — "Everything here invokes the build. Nothing here
// reimplements it" — and this file has the same shape: it invokes the workspace's own gate, and
// everything else it does is of one kind, proving the call was not a no-op.
//
// ⚠ THE TWO NO-OPS AN EXIT CODE DOES NOT CATCH, AND WHERE EACH IS CAUGHT.
//
//  * The chain is hollowed out. `verify` is a string in a JSON file: deleting `pnpm test &&` from
//    it leaves a gate that lints, builds, exits 0 and tests nothing, and nothing about this target
//    would change. → AssertGateStillChainsTheRow, before the run, from package.json.
//  * The chain is intact and a phase inspects nothing. Jest exits non-zero on "no tests found", so
//    the count guards itself; the word "axe" in the row does not, because deleting the one spec
//    that runs axe leaves the other suites green. → AssertAxeIsAsserted, before the run, and the
//    numbers Report reads back out of the run's own output.
//
// The last of those is also why this target reports "21 test(s) over 4 suite(s), initial JS
// 178.6 KB of 250.0 KB" rather than nothing at all. Build.Charts.cs treats a run that inspected
// zero charts as news worth printing; a run whose output carries no test count and no budget total
// is the same news, one toolchain over.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Serilog;

partial class Build
{
    // ── The workspace ─────────────────────────────────────────────────────────────────────────

    /// <summary>The pnpm workspace. docs/plan/03 § portal/, portal/README.md.</summary>
    AbsolutePath PortalDirectory => RootDirectory / "portal";

    AbsolutePath PortalManifest => PortalDirectory / "package.json";

    AbsolutePath PortalLockfile => PortalDirectory / "pnpm-lock.yaml";

    /// <summary>
    ///     The scripts docs/plan/23 § Build, row `Portal` names, which the entry script has to still
    ///     reach. <c>install</c> is not among them because this target runs it itself; <c>axe</c> is
    ///     not a script but an assertion inside <c>test</c>, and is checked by
    ///     <see cref="AssertAxeIsAsserted" />.
    /// </summary>
    static readonly string[] PortalPhases = ["lint", "test", "build", "budget"];

    // ── The target ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     ⚠ <c>Portal</c> deliberately has no <c>DependsOn</c>, which looks like an omission next
    ///     to <c>Generate</c> and is not.
    ///     <para>
    ///         The tempting edge is <c>Portal</c> → <c>Generate</c>, because <c>Generate</c> emits
    ///         <c>portal/libs/api</c> and the resource forms (ADR-012). But those are generated
    ///         *and committed* — docs/plan/03 § Assembly graph rules 6, "the generator owns the
    ///         directory" — and <c>Generate</c>'s job in the graph is to fail on drift, not to
    ///         produce inputs for a later target. A worktree the portal cannot build from is
    ///         already a failing <c>Generate</c>.
    ///     </para>
    ///     <para>
    ///         The cost of the edge is what settles it: <c>Generate</c> depends on <c>Compile</c>,
    ///         so <c>./build.sh Portal</c> would restore and build the whole .NET solution before
    ///         running <c>eslint</c>. That makes the .NET SDK a prerequisite for touching Angular,
    ///         and docs/plan/23's 25-minute PR budget is met by parallelism — which requires the
    ///         two toolchains to be independently invocable.
    ///     </para>
    /// </summary>
    void BuildPortal()
    {
        Assert.FileExists(
            PortalLockfile,
            $"{RootDirectory.GetRelativePathTo(PortalLockfile)} does not exist, so there is no pnpm "
            + "workspace to install and this target has nothing to gate. docs/plan/03 § portal/ "
            + "describes a committed workspace, so its absence is a broken checkout rather than an "
            + "early stage of the project.");

        var pnpm = ResolvePnpm();

        // ⚠ `gates` on CI and `verify` locally, and the one script between them is `node:gate`.
        //
        // portal/README.md § Node settles the Node pin and is explicit that its enforcement is
        // asymmetric on purpose: scripts/check-node.mjs warns locally and fails under --strict,
        // because "a blocked local install stops work over a version that will build fine, while a
        // drifted CI image silently produces artefacts nobody can reproduce". `verify` still runs
        // `node:check`, so the developer on Node 26 gets the warning and a working build; CI gets
        // the wall. Build.Test.cs § EnforceCoverageFloor draws the same line for the same reason.
        var entry = IsServerBuild ? "gates" : "verify";
        var axeSpecs = AxeSpecs();

        AssertGateStillChainsTheRow(entry);
        AssertAxeIsAsserted(axeSpecs);

        // ⚠ --frozen-lockfile, so a package.json edited without regenerating pnpm-lock.yaml fails
        // here instead of resolving something the lockfile never described. The whole point of the
        // lockfile in a gate is that CI installs the tree the developer tested against.
        pnpm("install --frozen-lockfile", workingDirectory: PortalDirectory);

        var exitCode = 0;

        var output = pnpm(
            entry,
            workingDirectory: PortalDirectory,
            exitHandler: process => exitCode = process.ExitCode);

        // The scripts are read for their numbers, not just their exit code, and colour codes would
        // sit between "Tests:" and the count. pnpm and jest emit none into a redirected pipe today;
        // stripping them costs one regex and removes the dependency on that staying true.
        var text = AnsiEscape.Replace(
            string.Join('\n', output.Select(x => x.Text ?? string.Empty)),
            string.Empty);

        Assert.True(
            exitCode == 0,
            $"`pnpm {entry}` exited {exitCode} over portal/ — its output is above, and the failing "
            + "phase is the last one it printed. docs/plan/23 § Build, row `Portal`, gates a PR on "
            + "lint, the Jest suites, the production build, the performance budget and axe. "
            + $"Reproduce with `pnpm {entry}` from portal/.");

        Report(entry, text, axeSpecs.Count);
    }

    // ── pnpm ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     <c>pnpm</c>, or a failure naming how to get it.
    /// </summary>
    /// <remarks>
    ///     ⚠ Deliberately not <c>npm</c> or <c>npx</c> as a fallback. portal/.npmrc sets
    ///     <c>strict-peer-dependencies=true</c> and <c>auto-install-peers=false</c>, and
    ///     portal/README.md § The Angular pin is a page about why the resolved tree has to be
    ///     exactly the one the lockfile describes. A different package manager over this workspace
    ///     resolves a different tree, which is a different gate wearing this one's name.
    /// </remarks>
    static Tool ResolvePnpm()
    {
        try
        {
            return ToolResolver.GetPathTool("pnpm");
        }
        catch (Exception exception)
        {
            Assert.Fail(
                "`pnpm` is not on PATH, so the portal workspace can neither be installed nor gated. "
                + "portal/package.json pins `packageManager: pnpm@11.18.0`, so the way to get "
                + "exactly that version is `corepack enable pnpm` on a Node 24 (portal/.nvmrc) — "
                + "which is also what .github/workflows/gate.yml does. Underlying error: "
                + $"{exception.Message}");

            throw;
        }
    }

    // ── Before the run: the gate is still the row ─────────────────────────────────────────────

    /// <summary>
    ///     Fails if the entry script no longer reaches every phase of docs/plan/23 § Build, row
    ///     <c>Portal</c>.
    /// </summary>
    /// <param name="entry">The script this target invokes — <c>gates</c> or <c>verify</c>.</param>
    /// <remarks>
    ///     ⚠ Expands <c>pnpm &lt;script&gt;</c> references transitively rather than matching the
    ///     text of one script, so re-nesting the chain — moving <c>budget</c> from <c>build</c> into
    ///     a new <c>gate:perf</c>, say — is fine and deleting a phase is not. This is the price of
    ///     invoking one script instead of five: the composition lives in a file this target does not
    ///     own, so the coupling is checked rather than assumed.
    /// </remarks>
    void AssertGateStillChainsTheRow(string entry)
    {
        var scripts = PortalScripts();

        Assert.True(
            scripts.ContainsKey(entry),
            $"portal/package.json has no `{entry}` script, so there is nothing for this target to "
            + "invoke. Build.Portal.cs runs `gates` on CI and `verify` locally; if those were "
            + "renamed, rename them here too.");

        var reached = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();

        pending.Push(entry);

        while (pending.Count > 0)
        {
            var name = pending.Pop();

            if (!reached.Add(name) || !scripts.TryGetValue(name, out var body))
                continue;

            foreach (var reference in PnpmScriptReference.Matches(body).Select(x => x.Groups[1].Value))
                pending.Push(reference);
        }

        var missing = PortalPhases.Where(x => !reached.Contains(x)).ToList();

        Assert.Empty(
            missing,
            $"`pnpm {entry}` in portal/package.json no longer reaches: {string.Join(", ", missing)}. "
            + "docs/plan/23 § Build, row `Portal`, is \"pnpm install/lint/test/build, performance "
            + "budget, axe\", and this target runs that row by invoking one script rather than "
            + "restating it — so a phase dropped out of the chain is a phase that silently stops "
            + "running on every PR, with no exit code to show for it. Put it back, or change the "
            + "row and Build.Portal.cs § PortalPhases together.");

        Log.Information(
            "Portal: `pnpm {Entry}` reaches {Count} script(s) — {Scripts}",
            entry,
            reached.Count,
            string.Join(", ", reached.OrderBy(x => x, StringComparer.Ordinal)));
    }

    /// <summary>The <c>scripts</c> block of portal/package.json.</summary>
    Dictionary<string, string> PortalScripts()
    {
        using var manifest = JsonDocument.Parse(PortalManifest.ReadAllText());

        var scripts = new Dictionary<string, string>(StringComparer.Ordinal);

        if (!manifest.RootElement.TryGetProperty("scripts", out var block))
            return scripts;

        foreach (var script in block.EnumerateObject())
            scripts[script.Name] = script.Value.GetString() ?? string.Empty;

        return scripts;
    }

    /// <summary>
    ///     The Jest specs that actually run axe — docs/plan/23 § Build, row <c>Portal</c>, "axe".
    /// </summary>
    /// <remarks>
    ///     Globbed under <c>apps/</c> and <c>libs/</c> rather than the workspace root, because
    ///     <c>portal/node_modules</c> holds tens of thousands of files and every one of them would
    ///     be read.
    /// </remarks>
    // Returns List rather than IReadOnlyCollection because CA1859 is an error here — the same
    // concession Build.Test.cs § ProjectsIn already makes.
    List<AbsolutePath> AxeSpecs() =>
        new[] { PortalDirectory / "apps", PortalDirectory / "libs" }
            .Where(x => x.DirectoryExists())
            .SelectMany(x => x.GlobFiles("**/*.spec.ts"))
            .Where(spec => AxeImport.IsMatch(spec.ReadAllText()))
            .OrderBy(x => x.Name, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    ///     Fails if no spec imports axe, which would make the last word of the row decorative.
    /// </summary>
    static void AssertAxeIsAsserted(List<AbsolutePath> axeSpecs)
        => Assert.True(
            axeSpecs.Count > 0,
            "no Jest spec under portal/apps or portal/libs imports `axe-core` or `jest-axe`, so "
            + "`pnpm test` would pass without checking a single accessibility rule. docs/plan/23 "
            + "§ Build, row `Portal`, ends in \"axe\", and docs/plan/20 § Accessibility, i18n, "
            + "theming makes WCAG 2.2 AA \"a gate, not a goal\". ⚠ No exit code can catch this: "
            + "deleting the one spec that runs axe leaves every remaining suite green.");

    // ── After the run: it was not a no-op ─────────────────────────────────────────────────────

    /// <summary>
    ///     Reads back what the run measured and fails if it measured nothing.
    /// </summary>
    /// <param name="entry">The script that was invoked, for the message.</param>
    /// <param name="output">Its combined output, with colour codes already stripped.</param>
    /// <param name="axeSpecs">How many Jest specs run axe, from <see cref="AxeSpecs" />.</param>
    /// <remarks>
    ///     ⚠ Both numbers come out of the tools' own summaries, so a reporter change is a build
    ///     failure here rather than a silent "0 test(s)". That is the intended direction: the
    ///     failure names the pattern to fix, and the alternative is a target that reports a number
    ///     nobody can distinguish from a suite that stopped running.
    /// </remarks>
    static void Report(string entry, string output, int axeSpecs)
    {
        var suites = FirstNumber(JestSuiteTotal, output);
        var tests = FirstNumber(JestTestTotal, output);
        var budget = BudgetInitialTotal.Match(output);

        Assert.True(
            tests > 0 && suites > 0,
            $"`pnpm {entry}` exited 0, but its output carries no Jest summary with a test in it. "
            + "Either the test phase ran nothing — docs/plan/23 § Test layers puts Jest + Angular "
            + "TestBed on every PR — or jest stopped printing \"Tests: N total\" and "
            + "Build.Portal.cs § JestTestTotal needs updating. Both are worth stopping for; a "
            + "target that cannot say how many tests ran cannot claim any ran.");

        Assert.True(
            budget.Success,
            $"`pnpm {entry}` exited 0, but portal/scripts/bundle-budget.mjs printed no initial-JS "
            + "TOTAL line. docs/plan/20 § Performance budget is enforced in CI \"failing the "
            + "build\", and a budget nobody can quote a number from was not applied. If the script's "
            + "output changed, Build.Portal.cs § BudgetInitialTotal is the pattern to fix.");

        Log.Information(
            "Portal: {Tests} test(s) over {Suites} suite(s), {Specs} of which run axe; initial JS "
            + "{Initial} of {Budget} gzipped",
            tests,
            suites,
            axeSpecs,
            budget.Groups[1].Value,
            budget.Groups[2].Value);
    }

    static int FirstNumber(Regex pattern, string output)
    {
        var match = pattern.Match(output);

        return match.Success
            ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)
            : 0;
    }

    // ── Patterns ──────────────────────────────────────────────────────────────────────────────

    /// <summary>A <c>pnpm &lt;script&gt;</c> or <c>pnpm run &lt;script&gt;</c> reference.</summary>
    static readonly Regex PnpmScriptReference =
        new(@"\bpnpm(?:\s+run)?\s+([A-Za-z][\w:.-]*)", RegexOptions.Compiled);

    /// <summary>An <c>axe-core</c> or <c>jest-axe</c> import specifier, in either quote style.</summary>
    static readonly Regex AxeImport =
        new("""['"](?:axe-core|jest-axe)['"]""", RegexOptions.Compiled);

    /// <summary>jest's <c>Test Suites: 4 passed, 4 total</c>.</summary>
    static readonly Regex JestSuiteTotal =
        new(@"^Test Suites:.*?(\d+) total", RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>jest's <c>Tests:       21 passed, 21 total</c>.</summary>
    static readonly Regex JestTestTotal =
        new(@"^Tests:.*?(\d+) total", RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>
    ///     bundle-budget.mjs's <c>TOTAL   178.6 KB  / 250.0 KB  PASS</c>. The verdict is matched but
    ///     not read: the script exits non-zero on FAIL, and this target does not second-guess it.
    /// </summary>
    static readonly Regex BudgetInitialTotal =
        new(@"^\s*TOTAL\s+(\S+ KB)\s+/\s+(\S+ KB)\s+(?:PASS|FAIL)",
            RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>An SGR colour escape.</summary>
    static readonly Regex AnsiEscape = new(@"\x1B\[[0-9;]*m", RegexOptions.Compiled);
}
