// E2E — docs/plan/23 § Build, row `E2E` `Chaos` `Load`: "Against a real deployment".
// docs/plan/23 § Test layers, row `E2E`: "Playwright + `cyc` against a real staging deployment",
// nightly and pre-release.
//
// ── ⚠ THE SENTENCE THIS TARGET IS RESPONSIBLE FOR ────────────────────────────────────────────────
//
// docs/plan/09 § The platform's own cluster, part 2 of the phase-3 answer: `deploy/bootstrap/`
// "remains supported and tested forever — it is what an operator runs to repair or reinstall the
// platform with no platform running. IT IS EXERCISED BY EVERY E2E RUN, SO IT CANNOT ROT."
//
// That was aspirational: nothing in the build invoked `bootstrap.sh`, so the only thing keeping it
// working was that nobody had changed it. It is now the first phase of this target, and — this is
// the part that matters — the half of it that needs no cluster runs on EVERY invocation, including
// the invocations that go on to block. A bootstrap check that only runs when staging exists is a
// bootstrap check that does not run, which is the state doc 09 was describing without meaning to.

using System;
using System.Collections.Generic;
using System.Linq;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Serilog;

partial class Build
{
    /// <summary>The staging deployment the journeys run against. docs/plan/23 § Environments and rollout.</summary>
    [Parameter("Base URL of the deployment E2E runs against, e.g. https://api.staging.cybercloud.example.")]
    readonly string? E2EBaseUrl;

    /// <summary>The kubectl context <c>bootstrap.sh</c> is dry-run against.</summary>
    [Parameter("kubectl context for the bootstrap dry-run phase of E2E. Absent skips the cluster half, not the whole phase.")]
    readonly string? KubeContext;

    /// <summary>An env file of durable shard connection strings — <c>deploy/bootstrap/shards.env.example</c>.</summary>
    [Parameter("Env file of durable shard connection strings, for the bootstrap dry-run.")]
    readonly string? ShardsFile;

    AbsolutePath BootstrapScript => RootDirectory / "deploy" / "bootstrap" / "bootstrap.sh";

    /// <summary>
    ///     ⚠ The deployment these three targets run against is an <em>input</em>, not a dependency.
    ///     See the note beside the target graph in <c>Build.cs</c> for why there is no
    ///     <c>DependsOn(Deploy)</c> edge and no <c>Deploy</c> target to point one at.
    /// </summary>
    void RunE2ETests()
    {
        ExerciseBootstrap();

        var suites = ProjectsIn(TestSuite.EndToEnd);
        var preconditions = new TargetPreconditions(nameof(E2E));

        preconditions.Require(
            suites.Count > 0,
            "there is no E2E suite — no project under test/ is named CyberCloud.E2E",
            "create test/CyberCloud.E2E (docs/plan/03 § test/) and add it to CyberCloud.slnx. "
            + "Build.Test.cs § SuiteOwning already routes that name here, so the project is the only "
            + "missing piece");

        preconditions.Require(
            !string.IsNullOrWhiteSpace(E2EBaseUrl),
            "no deployment is configured to run against",
            "pass --e2e-base-url https://api.staging…. docs/plan/23 § Test layers puts this suite "
            + "nightly and pre-release against real staging, so there is deliberately nothing for it "
            + "to fall back to — an E2E run against a local process is a different test wearing this "
            + "one's name");

        preconditions.Require(
            CycProject is not null,
            "there is no `cyc` CLI to drive — cli/ contains no project",
            "build the CLI under cli/ (docs/plan/03 § cli/, docs/plan/21 § The CLI). docs/plan/23 "
            + "§ Test layers specifies \"Playwright + `cyc`\", and the `cyc` half is the one that "
            + "exercises the public API the way a customer does");

        preconditions.AssertSatisfied(
            "docs/plan/23 § Test layers, row E2E: \"Playwright + `cyc` against a real staging "
            + "deployment\", gating \"green before release\". Every input above is part of that "
            + "sentence.");

        Log.Information("E2E: {Count} suite(s) against {Url}", suites.Count, E2EBaseUrl);

        RunSuites(
            nameof(E2E),
            suites,
            new Dictionary<string, string>
            {
                ["CYBERCLOUD_E2E_BASE_URL"] = E2EBaseUrl!,
                ["CYBERCLOUD_E2E_CYC"] = CycProject!,
            });
    }

    /// <summary>
    ///     The <c>cyc</c> project, or <see langword="null" /> while <c>cli/</c> is a skeleton.
    /// </summary>
    string? CycProject
    {
        get
        {
            var cli = RootDirectory / "cli";

            return cli.DirectoryExists()
                ? cli.GlobFiles("**/*.csproj").Select(x => x.ToString()).FirstOrDefault()
                : null;
        }
    }

    // ── Phase 1: deploy/bootstrap/ ────────────────────────────────────────────────────────────

    /// <summary>
    ///     Exercises <c>deploy/bootstrap/bootstrap.sh</c>, which is what makes docs/plan/09 § The
    ///     platform's own cluster's "exercised by every e2e run" true rather than aspirational.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Every case below is chosen to die before the script's first cluster call, and
    ///         that is a safety property rather than a stylistic one.</b> <c>bootstrap.sh</c>'s
    ///         preflight runs in a fixed order — kubectl on PATH, <c>--image</c>, <c>--shards</c>,
    ///         the shards file exists, then <c>kubectl version</c>. Everything before that line is
    ///         pure argument handling and safe to run anywhere. Everything after it talks to whatever
    ///         cluster the ambient kubeconfig points at, and the fifth step of the script creates a
    ///         namespace. A check that "just runs bootstrap.sh to see if it works" on a developer's
    ///         machine is a check that installs Cyber Cloud into whatever they were last pointed at.
    ///     </para>
    ///     <para>
    ///         ⚠ The empty-shard-file guard is deliberately <b>not</b> exercised here even though it
    ///         is a preflight check in spirit: it sits after <c>kubectl version</c> in the script, so
    ///         reaching it means reaching the cluster.
    ///     </para>
    ///     <para>
    ///         This catches the ways a shell script rots without anyone noticing: deletion, losing
    ///         its executable bit, a syntax error (<c>bash -n</c> parses the whole file, so a broken
    ///         <c>case</c> arm three hundred lines down fails here), and a guard being removed —
    ///         which is the one that costs an operator an hour at 3 a.m.
    ///     </para>
    /// </remarks>
    void ExerciseBootstrap()
    {
        Assert.FileExists(
            BootstrapScript,
            "deploy/bootstrap/bootstrap.sh is missing. docs/plan/09 § The platform's own cluster "
            + "makes it the supported repair path for a platform that is not running, and phase 0 of "
            + "the rollout installs from it by hand.");

        var bash = ToolResolver.GetPathTool("bash");
        var shards = ArtifactsDirectory / "e2e" / "shards.env";

        shards.Parent.CreateDirectory();
        shards.WriteAllText("CyberCloud__Storage__Durable__Shards__probe=Host=127.0.0.1;Database=probe\n");

        // ⚠ EVERY CASE THAT PARSES ARGUMENTS PASSES A CONTEXT THAT CANNOT EXIST, AND THAT IS A
        // BELT-AND-BRACES SAFETY MEASURE RATHER THAN A DETAIL.
        //
        // The cases below are chosen to die in the script's argument handling, before its first
        // cluster call. But "chosen to" is a claim about the script as it is today: a run against a
        // version whose guards have been REMOVED — precisely the regression this check exists to
        // catch — sails past them and reaches `kubectl version` against the ambient kubeconfig.
        // Observed exactly that while testing this file. A context nothing can resolve makes the
        // worst case a failed kubectl rather than an install into whatever the developer was last
        // pointed at.
        const string unresolvable = "cybercloud-bootstrap-selftest-no-such-context";

        // Syntax before behaviour: a parse error would otherwise surface as every case below failing
        // for the same unhelpful reason.
        Refuses(bash, "-n is a syntax check, not a run", $"-n {BootstrapScript}", null);

        Refuses(bash, "--help prints usage", $"{BootstrapScript} --help", null, "Usage:");

        // ⚠ THE FOUR CASES BELOW ARE INDEPENDENT, AND MAKING THEM SO TOOK TWO ATTEMPTS.
        //
        // The first version asserted only on the exit code, and each case supplied only the argument
        // under test. Deleting the script's `--image is required` guard outright still failed every
        // case, because `--shards` was missing too and its guard fired second — so a case only ever
        // proved that SOME guard fired, which is not a check on any particular one.
        //
        // Two changes fix it, and both are needed. Each case now supplies everything EXCEPT the one
        // thing under test, and each asserts on the MESSAGE rather than on the exit code — because
        // a script that has stopped guarding still exits non-zero, one step later, for a reason that
        // has nothing to do with the guard.
        Refuses(bash, "no --image", $"{BootstrapScript} --context {unresolvable} --shards {shards}", "--image is required");

        Refuses(bash, "no --shards", $"{BootstrapScript} --context {unresolvable} --image ghcr.io/x@sha256:0", "--shards is required");

        Refuses(
            bash,
            "--shards naming a file that does not exist",
            $"{BootstrapScript} --context {unresolvable} --image ghcr.io/x@sha256:0 --shards {ArtifactsDirectory / "e2e" / "absent.env"}",
            "does not exist");

        Refuses(bash, "an argument it does not know", $"{BootstrapScript} --wat", "unknown argument");

        Log.Information(
            "E2E: bootstrap.sh preflight exercised — 7 case(s), each pinned to its own message, no "
            + "cluster touched. docs/plan/09 § The platform's own cluster: \"exercised by every e2e "
            + "run, so it cannot rot\".");

        BootstrapDryRun(bash, shards);
    }

    /// <summary>
    ///     Runs one bootstrap case and asserts it ended the way the script promises.
    /// </summary>
    /// <param name="bash">The shell.</param>
    /// <param name="what">The case, for the failure message.</param>
    /// <param name="arguments">What to run.</param>
    /// <param name="refusal">
    ///     The text the refusal must contain, or <see langword="null" /> for a case that must
    ///     succeed. ⚠ A substring of the script's own message, so a rewording that keeps the meaning
    ///     is fine and a deletion is not.
    /// </param>
    /// <param name="says">Text a successful case must print. Only <c>--help</c> uses it.</param>
    void Refuses(Tool bash, string what, string arguments, string? refusal, string? says = null)
    {
        var exitCode = 0;

        var output = bash(
            arguments,
            workingDirectory: RootDirectory,
            logOutput: false,
            exitHandler: process => exitCode = process.ExitCode);

        var text = string.Join('\n', output.Select(x => x.Text));

        if (refusal is null)
        {
            Assert.True(
                exitCode == 0 && (says is null || text.Contains(says, StringComparison.Ordinal)),
                $"deploy/bootstrap/bootstrap.sh, case \"{what}\": expected exit 0"
                + (says is null ? string.Empty : $" and output containing \"{says}\"")
                + $", got {exitCode}. This is the script an operator runs when nothing else works; it "
                + $"has to at least parse and answer --help.\n{text}");

            return;
        }

        Assert.True(
            exitCode != 0 && text.Contains(refusal, StringComparison.Ordinal),
            $"deploy/bootstrap/bootstrap.sh, case \"{what}\": expected a non-zero exit whose message "
            + $"contains \"{refusal}\", got {exitCode}. ⚠ The exit code alone is not the assertion: a "
            + "script that has stopped guarding still exits non-zero one step later, from the cluster "
            + "call, and that is a partial install to unpick rather than a refusal. The script's own "
            + "preflight comment says every check there is one an operator would otherwise make "
            + $"halfway through, under pressure.\n{text}");
    }

    /// <summary>
    ///     The cluster half: <c>bootstrap.sh --dry-run</c> against the E2E cluster, which is the only
    ///     way to exercise the manifests themselves — rendering, RBAC namespacing, the Job's schema.
    /// </summary>
    /// <remarks>
    ///     ⚠ <c>--dry-run</c> is <c>kubectl apply --dry-run=client</c> throughout and the script exits
    ///     before deleting or creating the schema Job, so this validates the manifests without
    ///     mutating the cluster. The full run belongs to whatever provisions the E2E environment, not
    ///     to a build target that a developer might invoke while pointed at production.
    /// </remarks>
    void BootstrapDryRun(Tool bash, AbsolutePath fallbackShards)
    {
        if (string.IsNullOrWhiteSpace(KubeContext))
        {
            Log.Warning(
                "E2E: the bootstrap dry-run against a cluster was SKIPPED — no --kube-context. Only "
                + "the argument-handling half of bootstrap.sh ran, so the manifests in "
                + "deploy/bootstrap/ were not rendered or validated by an API server. ○, not ✔.");

            return;
        }

        var image = SiloImageReference();
        var shards = ShardsFile is null ? fallbackShards : (AbsolutePath)ShardsFile;

        bash(
            $"{BootstrapScript} --dry-run --context {KubeContext} --image {image} --shards {shards}",
            workingDirectory: RootDirectory);

        Log.Information("E2E: bootstrap.sh --dry-run clean against context {Context}", KubeContext);
    }

    /// <summary>
    ///     The silo image to dry-run with, from <see cref="ImageManifestFile" /> when <c>Images</c>
    ///     has run.
    /// </summary>
    /// <remarks>
    ///     ⚠ Falls back to a syntactically valid digest rather than a tag. <c>bootstrap.sh</c> warns
    ///     on a tag, and a dry run whose output always contains a warning is a dry run whose warnings
    ///     nobody reads. The fallback never reaches a registry: <c>--dry-run</c> pulls nothing.
    /// </remarks>
    string SiloImageReference()
    {
        if (!ImageManifestFile.FileExists())
        {
            Log.Warning(
                "E2E: {Manifest} does not exist — ./build.sh Images has not run, so the dry-run uses "
                + "a placeholder digest. The manifests are still validated; the image reference in "
                + "them is not a real one.",
                RootDirectory.GetRelativePathTo(ImageManifestFile));

            return "registry.invalid/cybercloud-silo-host@sha256:"
                + new string('0', 64);
        }

        var manifest = System.Text.Json.Nodes.JsonNode.Parse(ImageManifestFile.ReadAllBytes())!.AsObject();

        return (manifest["images"] as System.Text.Json.Nodes.JsonArray ?? [])
            .Select(x => x!.AsObject())
            .Where(x => x["host"]!.GetValue<string>().EndsWith("Silo.Host", StringComparison.Ordinal))
            .Select(x => x["reference"]!.GetValue<string>())
            .First();
    }
}
