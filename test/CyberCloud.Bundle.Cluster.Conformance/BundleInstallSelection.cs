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
///         "installed" implies "serving" — that is <c>--wait</c>, and it is asserted on a real
///         cluster by the two installing classes. For the six <c>manifest:</c> components it is half
///         true since #74: the <c>kubectl</c> branch now waits for every definition it applied to be
///         Established, which
///         <see cref="EveryManifestComponentIsFollowedByAnEstablishmentWait" /> asserts the shape of
///         here, and nothing waits for the operator behind those definitions to be running.
///         <c>bundle.yaml</c> § owed, <c>the-manifest-path-waits-for-nothing</c>.
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
    ///     Every <c>manifest:</c> component's apply is followed by an establishment wait before the
    ///     next component starts.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is the SHAPE of the phase barrier and not its truth, and the difference is
    ///         the whole reason the class comment above splits the two.</b> A dry run executes
    ///         nothing, so what it can say is that the installer emits a
    ///         <c>kubectl wait --for=condition=Established</c> after every <c>manifest:</c> apply and
    ///         before it moves on. What it cannot say is that the wait ever returns true, or that the
    ///         operator behind the definitions is running — <c>charts/bundle/bundle.yaml</c> § owed,
    ///         <c>the-manifest-path-waits-for-nothing</c>, is the row that owes the second half.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Until #74 that wait fired only for a component declaring a <c>manifestExtra</c>,
    ///         which is two of the six</b>, so four manifest applies were followed by nothing and the
    ///         phase they sit in ended with definitions the API server might not yet serve. The
    ///         assertion is per component rather than per phase for the reason install.sh gives at
    ///         the wait itself: phase 40's two providers admit against definitions the rows before
    ///         them <i>in the same phase</i> installed, so a boundary-only wait would not order them.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A <c>helm</c> component is asserted to run no <c>kubectl</c> at all</b>, which is
    ///         the other half of the same claim: the wait belongs to the branch that needs it, and a
    ///         line that leaked into the helm path would be a second barrier nobody argued for. Helm
    ///         has <c>--wait</c>, and <c>--wait</c> is the clause that was always true.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Sabotage-verified on 2026-09-05 rather than reasoned about, and the part worth
    ///         keeping is which rows survive it.</b> Moving the wait back inside the
    ///         <c>manifestExtra</c> guard — where it sat before #74 — turns FOUR of the six red and
    ///         leaves kubevirt and containerized-data-importer green, because those two are the
    ///         components that declare a second document and so reached the wait already. A
    ///         regression test whose sabotage turns every row red would not have told this defect
    ///         apart from the wait being deleted outright.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task EveryManifestComponentIsFollowedByAnEstablishmentWait() {
        Assert.SkipUnless(
            BundleInstaller.OnPath("bash"),
            "SKIPPED — charts/bundle/install.sh is a bash script and `bash` is not on PATH. WOULD "
            + "PROVE: that every `manifest:` component's apply is followed by a `kubectl wait "
            + "--for=condition=Established` before the next component begins."
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
        var manifests = 0;

        // ⚠ Every position is resolved before any of them is sliced between. A loop that looked up
        // the next component's header only when it needed it would index a string with -1 on the
        // run where the installer walked past a component, which is a crash rather than the report
        // that names the component.
        var positions = roster
            .Select(entry => run.Output.IndexOf("\n  " + entry.Component + "\n", StringComparison.Ordinal))
            .ToList();

        for (var index = 0; index < roster.Count; index++) {
            var component = roster[index].Component;
            var at = positions[index];

            at.ShouldBeGreaterThanOrEqualTo(
                0,
                $"charts/bundle/install.sh --dry-run never attempted `{component}`. Its output "
                + "was:\n" + run.Output
            );

            // ⚠ To the NEXT component's header rather than to the end of the output, so a wait
            // emitted once for the whole run — or emitted for the following component — cannot
            // satisfy this row. That is the exact defect being regression-tested: the wait existed
            // and was in the wrong place.
            var end = index + 1 < roster.Count && positions[index + 1] > at
                ? positions[index + 1]
                : run.Output.Length;

            var segment = run.Output[at..end];
            var kind = BundleInstaller.Pin(component, "install");

            if (kind == "manifest") {
                manifests++;

                segment.ShouldContain(
                    "kubectl wait --for=condition=Established",
                    Case.Sensitive,
                    $"charts/bundle/install.sh applied `{component}` — a `manifest:` component in "
                    + $"phase {roster[index].Phase} — and moved on without waiting for its "
                    + "definitions to be Established. `kubectl apply` returns when the API server has "
                    + "STORED the objects, so the next component, and the next phase, can be admitted "
                    + "against a definition that is not served yet. charts/bundle/bundle.yaml "
                    + "§ phases calls a phase a barrier and this is the half of it that is "
                    + "implemented. What install.sh emitted for this component was:\n" + segment
                );
            } else {
                segment.ShouldNotContain(
                    "kubectl",
                    Case.Sensitive,
                    $"charts/bundle/install.sh ran kubectl for `{component}`, whose "
                    + $"component.yaml declares `install: {kind}`. A helm component's barrier is "
                    + "helm's own `--wait`; a kubectl line here is a second barrier with no argument "
                    + "behind it, and the argument is the thing this repository checks. What "
                    + "install.sh emitted for this component was:\n" + segment
                );
            }
        }

        // ⚠ The count is read off the roster rather than written here, and it is asserted only to be
        // non-zero. A test that walked nineteen components and found no `manifest:` one would pass
        // every assertion above by never running one — the empty-set pass this file's other cases
        // exist to refuse.
        manifests.ShouldBeGreaterThan(
            0,
            "no component in charts/bundle/bundle.yaml declares `install: manifest`, so this test "
            + "asserted the barrier over nothing and passed. Either the reader is broken or the "
            + "roster no longer has a manifest component, and both are worth a red run."
        );
    }

    /// <summary>
    ///     The usage text counts the phases out of the roster instead of asserting how big they are.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>#74's third finding is a number that went stale in prose, twice.</b> The text said
    ///     <c>--phase</c> is <i>"for repairing one row"</i>, which is wrong for the fourteen
    ///     components that share a phase; the 2026-09-03 correction added <c>--component</c> and left
    ///     the sentence saying much the same thing. So the fix is not a better sentence — it is
    ///     removing the number from the sentence. <c>install.sh --help</c> now reads
    ///     <c>bundle.yaml</c> and prints what it finds, and this asserts the two agree.
    ///     ⚠ <b>The expected line is built from the roster, so this test cannot be the second place
    ///     the count lives either</b> — the same rule
    ///     <see cref="TheDryRunAttemptsEveryRosteredComponentOnceInTheRostersOrder" /> follows for the
    ///     order.
    /// </remarks>
    [Fact]
    public async Task TheUsageTextCountsThePhasesOutOfTheRoster() {
        Assert.SkipUnless(
            BundleInstaller.OnPath("bash"),
            "SKIPPED — charts/bundle/install.sh is a bash script and `bash` is not on PATH. WOULD "
            + "PROVE: that `--help` reports each phase's size as charts/bundle/bundle.yaml has it."
        );

        var run = await BundleInstaller.RunAsync(
            "--help",
            kubeconfig: null,
            TestContext.Current.CancellationToken
        );

        run.ExitCode.ShouldBe(
            0,
            "charts/bundle/install.sh --help must succeed. Its output was:\n" + run.Output
        );

        var held = BundleInstaller.Roster()
            .GroupBy(entry => entry.Phase)
            .ToDictionary(group => group.Key, group => group.Count());

        held.ShouldNotBeEmpty(
            "charts/bundle/bundle.yaml's `components:` block read as empty, so this test would have "
            + "compared the usage text against no phases at all and passed."
        );

        foreach (var (phase, count) in held) {
            var expected = $"phase {phase,-3} {count,2} component" + (count == 1 ? "" : "s");

            run.Output.ShouldContain(
                expected,
                Case.Sensitive,
                $"charts/bundle/install.sh --help does not say that phase {phase} holds {count} "
                + "component(s), which is what charts/bundle/bundle.yaml lists. A usage text that "
                + "disagrees with the roster is how `--phase` came to be documented as \"repairing "
                + "one row\" for a phase holding eight of them. Its output was:\n" + run.Output
            );
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
