// E2E — docs/plan/23 § Build, row `E2E` `Chaos` `Load`: "Against a real deployment".
// docs/plan/23 § Test layers, row `E2E`: "Playwright + `cyc` against a real staging deployment",
// nightly and pre-release.
//
// ── ⚠ THE SENTENCE THIS TARGET IS RESPONSIBLE FOR, AND WHERE THE CODE FOR IT LIVES ───────────────
//
// docs/plan/09 § The platform's own cluster, part 2 of the phase-3 answer: `deploy/bootstrap/`
// "remains supported and tested forever — it is what an operator runs to repair or reinstall the
// platform with no platform running. IT IS EXERCISED BY EVERY E2E RUN, SO IT CANNOT ROT."
//
// That was aspirational: nothing in the build invoked `bootstrap.sh`, so the only thing keeping it
// working was that nobody had changed it. It is the first phase of this target, and — this is the
// part that matters — the half of it that needs no cluster runs on EVERY invocation, including the
// invocations that go on to block. A bootstrap check that only runs when staging exists is a
// bootstrap check that does not run, which is the state doc 09 was describing without meaning to.
//
// The phase itself is Build.Bootstrap.cs since issue #25, because it is also a target of its own:
// nightly.yml's kind and hostile-BYO jobs run `Bootstrap` rather than `E2E`, so a dry-run that passes
// is a green job and not a pass buried in the log of a job blocked on the missing suite. That file's
// header has the account. This one keeps the deployment-driven half: the suite, the URL, `cyc`.

using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;

partial class Build {
    /// <summary>The staging deployment the journeys run against. docs/plan/23 § Environments and rollout.</summary>
    [Parameter("Base URL of the deployment E2E runs against, e.g. https://api.staging.cybercloud.example.")]
    readonly string? E2EBaseUrl;

    /// <summary>
    ///     ⚠ The deployment these three targets run against is an <em>input</em>, not a dependency.
    ///     See the note beside the target graph in <c>Build.cs</c> for why there is no
    ///     <c>DependsOn(Deploy)</c> edge and no <c>Deploy</c> target to point one at.
    /// </summary>
    void RunE2ETests() {
        ExerciseBootstrap();

        var suites = ProjectsIn(TestSuite.EndToEnd);
        var preconditions = new TargetPreconditions(nameof(E2E));

        preconditions.Require(
            suites.Count > 0,
            "there is no E2E suite — no project under test/ is named CyberCloud.E2E",
            "create test/CyberCloud.E2E (docs/plan/03 § test/) and add it to CyberCloud.slnx. "
            + "Build.Test.cs § SuiteOwning already routes that name here, so the project is the only "
            + "missing piece"
        );

        preconditions.Require(
            !string.IsNullOrWhiteSpace(E2EBaseUrl),
            "no deployment is configured to run against",
            "pass --e2e-base-url https://api.staging…. docs/plan/23 § Test layers puts this suite "
            + "nightly and pre-release against real staging, so there is deliberately nothing for it "
            + "to fall back to — an E2E run against a local process is a different test wearing this "
            + "one's name"
        );

        preconditions.Require(
            CycProject is not null,
            "there is no `cyc` CLI to drive — cli/ contains no project",
            "build the CLI under cli/ (docs/plan/03 § cli/, docs/plan/21 § The CLI). docs/plan/23 "
            + "§ Test layers specifies \"Playwright + `cyc`\", and the `cyc` half is the one that "
            + "exercises the public API the way a customer does"
        );

        preconditions.AssertSatisfied(
            "docs/plan/23 § Test layers, row E2E: \"Playwright + `cyc` against a real staging "
            + "deployment\", gating \"green before release\". Every input above is part of that "
            + "sentence."
        );

        Log.Information("E2E: {Count} suite(s) against {Url}", suites.Count, E2EBaseUrl);

        RunSuites(
            nameof(E2E),
            suites,
            new Dictionary<string, string> {
                ["CYBERCLOUD_E2E_BASE_URL"] = E2EBaseUrl!, ["CYBERCLOUD_E2E_CYC"] = CycProject!
            }
        );
    }

    /// <summary>
    ///     The <c>cyc</c> project, or <see langword="null" /> while <c>cli/</c> is a skeleton.
    /// </summary>
    string? CycProject {
        get {
            var cli = RootDirectory / "cli";

            return cli.DirectoryExists()
                ? cli.GlobFiles("**/*.csproj").Select(x => x.ToString()).FirstOrDefault()
                : null;
        }
    }
}
