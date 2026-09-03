using Shouldly;

namespace CyberCloud.Bundle.Cluster.Conformance;

/// <summary>
///     Which components <c>charts/bundle/install.sh</c> acts on, and in what order, read without a
///     cluster.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every other test in this assembly passes a selector, so until this class existed the
///         installer had never been run over the whole roster by anything.</b> Both cluster-backed
///         classes pass <c>--phase</c>, and both daemon-free companions pass <c>--dry-run --phase</c>
///         — so the loop that walks the phases in order, the loop that picks a phase's components out
///         of the roster, and the numeric sort between them were reached exactly once per run, over a
///         single phase, where an ordering defect cannot show. That is one of the three gaps
///         <c>charts/bundle/bundle.yaml</c> § owed,
///         <c>most-of-the-roster-has-never-been-installed</c>, names.
///     </para>
///     <para>
///         ⚠ <b>What this closes is the ORDER, and not the barrier, and the two are different
///         claims.</b> <c>bundle.yaml</c> § phases says a phase is a barrier: "every component in
///         phase N is installed and its CRDs are established before phase N+1 begins". The half a
///         dry run can answer is <i>which component is attempted when</i>, over all nineteen rows and
///         all eight phases, which is what this class asserts. The half it cannot answer is whether
///         "installed" implies "serving" — that is <c>--wait</c>, it is asserted on a real cluster by
///         the two installing classes, and for the six <c>manifest:</c> components it is not true at
///         all: the <c>kubectl</c> branch waits for nothing unless the component has a
///         <c>manifestExtra</c>, and even then it waits inside the component rather than at the phase
///         boundary. <c>bundle.yaml</c> § owed, <c>the-manifest-path-waits-for-nothing</c>.
///     </para>
///     <para>
///         ⚠ <b>It needs no Docker daemon and installs nothing</b>, which is why it is worth having
///         at all: it is the only assertion in this assembly that covers all nineteen components,
///         and it costs under a second.
///     </para>
/// </remarks>
public sealed class BundleInstallSelection {
    /// <summary>
    ///     A full dry run attempts every component the roster lists, once each, in the roster's
    ///     order, grouped under ascending phase headers.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The expected sequence is read out of <c>bundle.yaml</c> and the actual one out of the
    ///     script's own output, so the two arrive at the order by different routes</b> — the same
    ///     rule every other assertion in this assembly follows. Writing the nineteen names here
    ///     would make this file a second place the roster lives, which is the thing
    ///     <c>bundle.yaml</c>'s header forbids for versions and which is worse for an order, because
    ///     an order has no registry to resolve it against.
    ///     ⚠ <b>Strictly increasing positions rather than an equality on a parsed list.</b> A parser
    ///     that mistook a <c>would run:</c> line for a component name would produce a longer list and
    ///     a diff nobody can read; positions of exact whole lines cannot make that mistake, and the
    ///     "once each" clause is carried by <c>LastIndexOf</c> agreeing with <c>IndexOf</c>.
    /// </remarks>
    [Fact]
    public async Task TheDryRunAttemptsEveryRosteredComponentOnceInTheRostersOrder() {
        Assert.SkipUnless(
            BundleInstaller.OnPath("bash"),
            "SKIPPED — charts/bundle/install.sh is a bash script and `bash` is not on PATH, so the "
            + "order it would install in could not be read. WOULD PROVE: that a run with no selector "
            + "attempts all nineteen components, once each, in charts/bundle/bundle.yaml's order."
        );

        var run = await BundleInstaller.RunAsync(
            "--dry-run",
            kubeconfig: null,
            TestContext.Current.CancellationToken
        );

        run.ExitCode.ShouldBe(
            0,
            "charts/bundle/install.sh --dry-run executes nothing and must therefore succeed on any "
            + "machine with bash. Its output was:\n" + run.Output
        );

        var roster = BundleInstaller.Roster();

        roster.ShouldNotBeEmpty(
            "charts/bundle/bundle.yaml's `components:` block read as empty, so this test would have "
            + "asserted an order over nothing and passed. That is the shape of failure this "
            + "repository keeps finding — a check that answers a narrower question than it appears "
            + "to — and here the narrower question is the empty one."
        );

        var previous = -1;
        var previousName = "(the start of the output)";

        foreach (var (phase, component) in roster) {
            var line = "\n  " + component + "\n";
            var at = run.Output.IndexOf(line, StringComparison.Ordinal);

            at.ShouldBeGreaterThanOrEqualTo(
                0,
                $"charts/bundle/install.sh --dry-run never attempted `{component}`, which "
                + $"charts/bundle/bundle.yaml puts in phase {phase}. A rostered component the "
                + "installer walks past is one that is never installed, and the only report of it is "
                + "the operator it was supposed to bring — charts/bundle/README.md § The ordering "
                + "rule. Its output was:\n" + run.Output
            );

            run.Output.LastIndexOf(line, StringComparison.Ordinal).ShouldBe(
                at,
                $"charts/bundle/install.sh --dry-run attempted `{component}` more than once. A "
                + "component installed twice in one run is a helm upgrade over a release that was "
                + "just created, which succeeds and hides whichever of the two invocations was "
                + "wrong. Its output was:\n" + run.Output
            );

            at.ShouldBeGreaterThan(
                previous,
                $"charts/bundle/install.sh --dry-run attempted `{component}` before "
                + $"`{previousName}`, and charts/bundle/bundle.yaml lists them the other way round. "
                + "The roster carries the ORDER — bundle.yaml's header calls it \"a property of the "
                + "set\" — and the order is what stops a webhook being installed onto a cluster with "
                + "no CNI. Its output was:\n" + run.Output
            );

            previous = at;
            previousName = component;
        }

        // ⚠ The phase headers, separately from the components under them. Nineteen components in the
        // right order would still be the wrong output if the run printed one phase header for all of
        // them: the header is what a person reading a failed install uses to say which barrier the
        // run died at.
        var phasePrevious = -1;

        foreach (var phase in roster.Select(entry => entry.Phase).Distinct()) {
            var at = run.Output.IndexOf("── phase " + phase + " ", StringComparison.Ordinal);

            at.ShouldBeGreaterThan(
                phasePrevious,
                $"charts/bundle/install.sh --dry-run printed no `── phase {phase}` header, or "
                + "printed it out of order. Its output was:\n" + run.Output
            );

            phasePrevious = at;
        }
    }

    /// <summary>
    ///     A <c>--phase</c> that matches no component fails, rather than reporting an empty success.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Measured on 2026-09-03, before the fix this asserts.</b>
    ///     <c>install.sh --dry-run --phase 99</c> printed one empty phase header and exited 0 under
    ///     "Dry run. No command above was executed.", and <c>install.sh --verify --phase 99</c>
    ///     printed <i>"Every pin resolves"</i> having resolved none — over a cluster, the same typo
    ///     under no <c>--dry-run</c> would have reported "Bundle applied." That is the mirror image
    ///     of the defect <c>verify_component</c>'s own comment records — <i>"a verifier that fails
    ///     when there is nothing to verify is the same defect as one that passes when there is"</i> —
    ///     and it is the more dangerous half, because its output reads like a green run.
    /// </remarks>
    [Fact]
    public async Task APhaseThatSelectsNothingIsAFailureRatherThanAnEmptySuccess() {
        Assert.SkipUnless(
            BundleInstaller.OnPath("bash"),
            "SKIPPED — charts/bundle/install.sh is a bash script and `bash` is not on PATH. WOULD "
            + "PROVE: that a --phase matching no component in bundle.yaml fails loudly rather than "
            + "reporting an install that did nothing."
        );

        var run = await BundleInstaller.RunAsync(
            "--dry-run --phase 99",
            kubeconfig: null,
            TestContext.Current.CancellationToken
        );

        run.ExitCode.ShouldBe(
            2,
            "charts/bundle/install.sh --dry-run --phase 99 selected no component and did not fail. "
            + "bundle.yaml has no phase 99, so this is a typo that reports success — and the same "
            + "typo without --dry-run reports \"Bundle applied\" over a cluster nothing was installed "
            + "onto. Its output was:\n" + run.Output
        );

        run.Output.ShouldContain(
            "selected no component",
            Case.Sensitive,
            "the failure did not say what was wrong. An exit code alone sends the reader to look for "
            + "a broken pin. Its output was:\n" + run.Output
        );
    }

    /// <summary>
    ///     A <c>--component</c> naming something off the roster fails, and says which name it was.
    /// </summary>
    /// <remarks>
    ///     ⚠ Asserted separately from the empty-selection case above, and the message is the reason:
    ///     a misspelled component and an empty phase are the same outcome and different mistakes, and
    ///     the report a person can act on is the one that names the string they typed.
    /// </remarks>
    [Fact]
    public async Task AComponentThatIsNotOnTheRosterIsRefusedByName() {
        Assert.SkipUnless(
            BundleInstaller.OnPath("bash"),
            "SKIPPED — charts/bundle/install.sh is a bash script and `bash` is not on PATH. WOULD "
            + "PROVE: that --component refuses a name charts/bundle/bundle.yaml does not list."
        );

        var run = await BundleInstaller.RunAsync(
            "--dry-run --component cloudnativepg",
            kubeconfig: null,
            TestContext.Current.CancellationToken
        );

        run.ExitCode.ShouldBe(
            2,
            "charts/bundle/install.sh --dry-run --component cloudnativepg did not fail. The roster's "
            + "row is `cloudnative-pg`; a selector that silently matches nothing turns a typo into a "
            + "clean run. Its output was:\n" + run.Output
        );

        run.Output.ShouldContain(
            "cloudnativepg",
            Case.Sensitive,
            "the failure did not repeat the name that was typed, which is the one piece of "
            + "information the reader does not already have. Its output was:\n" + run.Output
        );
    }
}
