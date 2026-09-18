using Shouldly;

namespace CyberCloud.Bundle.Cluster.Conformance;

/// <summary>
///     The digest recorded beside every image in a <c>component.yaml</c> is consumed by the install
///     path: a component whose record is unresolved, or whose tag no longer serves the recorded
///     digest, is refused before <c>install.sh</c> applies anything.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             This class exists because a record nothing reads is the defect this directory has
///             already shipped once.
///         </b> <c>redis-operator/component.yaml</c> carried an <c>imageDigest:</c> beside a comment
///         saying <c>install.sh --verify</c> compared it, and <c>verify_component</c> had never read
///         a digest — issue #75, and <c>charts/bundle/bundle.yaml</c> § owed,
///         <c>images-are-not-pinned-by-digest</c>. The <c>images:</c> block that replaced it was
///         honest that it was "a record, not a pin", and #74's closing comment declined to add a
///         <c>waitFor:</c> key for the same reason. Issue #17 makes the installer read the record,
///         and this class is what stops that reading from quietly becoming a no-op again: every
///         assertion here runs the real <c>install.sh</c> over a sabotaged copy of
///         <c>charts/bundle/</c> and reads the refusal off the script's own output.
///     </para>
///     <para>
///         ⚠ <b>Two of the three need no network, and the third is the one that keeps the other two honest.</b>
///         An <c>@unresolved</c> entry is refused on shape alone, so
///         <see cref="AnImageRecordedWithoutADigestRefusesTheComponentBeforeAnythingIsApplied" />
///         runs anywhere bash does. A moved tag can only be detected by asking the registry, so
///         <see cref="ATagThatNoLongerServesTheRecordedDigestRefusesTheComponentBeforeAnythingIsApplied" />
///         and <see cref="TheRecordedDigestIsWhatTheTagServesToday" /> skip when
///         <c>ghcr.io</c> does not answer — and the last of those is the positive control without
///         which a gate that refused everything would pass the first two.
///     </para>
///     <para>
///         ⚠ <b>The sabotaged component is <c>rabbitmq-cluster-operator</c>, and the choice is load-bearing.</b>
///         It is a <c>manifest:</c> component with exactly one image, so the apply path it would
///         reach is a <c>kubectl apply</c> — which means "nothing was applied" can be asserted as
///         "no kubectl line, no connection error" without <c>helm</c> being on the machine. Its
///         one image is on <c>ghcr.io</c>, which serves anonymous HEAD requests without the
///         rate-limit Docker Hub applies, so the network cases do not flake on the third run of the
///         afternoon.
///     </para>
///     <para>
///         ⚠ <b>Real apply mode, not <c>--dry-run</c>, for the two refusals.</b> A dry run prints
///         commands and runs none, so a dry run that refused would prove the gate is in the file
///         and not that it is in front of the apply. These runs pass no <c>--dry-run</c>, point
///         <c>KUBECONFIG</c> at a file that does not exist, and assert that the output holds the
///         refusal and no trace of kubectl having been invoked. A gate placed after the apply would
///         show up here as a connection error before the refusal.
///     </para>
/// </remarks>
public sealed class BundleImagePins {
    /// <summary>The component every case here sabotages. See the class remarks for why this one.</summary>
    const string Component = "rabbitmq-cluster-operator";

    /// <summary>
    ///     An <c>images:</c> entry with no digest — <c>@unresolved</c> — refuses the component
    ///     before any apply, under a real run and under <c>--dry-run</c> alike.
    /// </summary>
    [Fact]
    public async Task AnImageRecordedWithoutADigestRefusesTheComponentBeforeAnythingIsApplied() {
        Assert.SkipUnless(
            BundleInstaller.OnPath("bash"),
            BundleInstaller.SkipWithoutBash(
                "install.sh",
                "that an image recorded as `@unresolved` refuses its component before install.sh "
                + "applies anything."
            )
        );

        using var copy = BundleCopy.Create();
        var image = copy.SabotageDigest(Component, static digest => "unresolved");

        var run = await BundleInstaller.RunAsync(
            copy.Script,
            "--component " + Component,
            copy.NoCluster,
            TestContext.Current.CancellationToken
        );

        run.ExitCode.ShouldBe(
            1,
            $"charts/bundle/install.sh installed `{Component}` over an `images:` entry recorded as "
            + "`@unresolved`, or failed for another reason. A pin nobody resolved is not a pin, and "
            + "the installer is the thing that has to say so — a record only images.sh reads is the "
            + "`imageDigest:` defect again. Its output was:\n"
            + run.Output
        );

        run.Output.ShouldContain(
            image,
            Case.Sensitive,
            "the refusal did not name the image that has no digest, which is the one thing the "
            + "reader has to go and resolve. Its output was:\n"
            + run.Output
        );

        run.Output.ShouldContain(
            "no digest",
            Case.Sensitive,
            "the refusal did not say that the digest is missing, so a reader cannot tell this from "
            + "a moved tag. Its output was:\n"
            + run.Output
        );

        AssertNothingWasApplied(run);

        // ⚠ A dry run refuses on shape too, so a tree with an unresolved pin is red on the cheapest
        // run anybody makes. What it does NOT do is resolve — asserted by the message: a dry run
        // that printed "serves" would have made a registry call its contract says it does not.
        var dry = await BundleInstaller.RunAsync(
            copy.Script,
            "--dry-run --component " + Component,
            null,
            TestContext.Current.CancellationToken
        );

        dry.ExitCode.ShouldBe(
            1,
            "charts/bundle/install.sh --dry-run passed over an `images:` entry recorded as "
            + "`@unresolved`. The dry run is the check people make before a real one, and a dry run "
            + "that is green over a pin nobody resolved sends them into the real one. Its output "
            + "was:\n"
            + dry.Output
        );

        dry.Output.ShouldContain(image, Case.Sensitive, "the dry run's refusal did not name the image:\n" + dry.Output);

        dry.Output.ShouldNotContain(
            "serves ",
            Case.Sensitive,
            "charts/bundle/install.sh --dry-run resolved a tag against its registry. A dry run "
            + "executes nothing and needs no network; a registry round-trip in it is a contract "
            + "broken quietly. Its output was:\n"
            + dry.Output
        );
    }

    /// <summary>
    ///     A recorded digest the tag no longer serves refuses the component before any apply, and the
    ///     refusal prints both digests.
    /// </summary>
    [Fact]
    public async Task ATagThatNoLongerServesTheRecordedDigestRefusesTheComponentBeforeAnythingIsApplied() {
        Assert.SkipUnless(
            BundleInstaller.OnPath("bash"),
            BundleInstaller.SkipWithoutBash(
                "install.sh",
                "that a recorded digest the tag no longer serves refuses its component before "
                + "install.sh applies anything."
            )
        );

        Assert.SkipUnless(
            await Registry.Answers(TestContext.Current.CancellationToken),
            "SKIPPED — ghcr.io did not answer, so install.sh could not resolve the tag this case "
            + "sabotages the record of. WOULD PROVE: that a moved tag is refused, naming the recorded "
            + "digest and the one the registry serves."
        );

        using var copy = BundleCopy.Create();
        var recorded = string.Empty;
        var image = copy.SabotageDigest(
            Component,
            digest => {
                recorded = digest;

                // Still 64 lower-case hex characters, so the shape check passes and only the
                // registry can tell it is wrong — which is the whole point of this case.
                return "sha256:deadbeef" + digest["sha256:deadbeef".Length..];
            }
        );

        var run = await BundleInstaller.RunAsync(
            copy.Script,
            "--component " + Component,
            copy.NoCluster,
            TestContext.Current.CancellationToken
        );

        run.ExitCode.ShouldBe(
            1,
            $"charts/bundle/install.sh installed `{Component}` over a recorded digest its tag does "
            + "not serve. That is the substitution the record exists to catch — a tag rebuilt "
            + "upstream between review and install — and catching it only in images.sh is catching "
            + "it only when somebody remembers to run images.sh. Its output was:\n"
            + run.Output
        );

        run.Output.ShouldContain(image, Case.Sensitive, "the refusal did not name the image:\n" + run.Output);

        run.Output.ShouldContain(
            "recorded sha256:deadbeef",
            Case.Sensitive,
            "the refusal did not print the digest that was recorded, so a reader cannot see what "
            + "was reviewed. Its output was:\n"
            + run.Output
        );

        run.Output.ShouldContain(
            "serves   " + recorded,
            Case.Sensitive,
            "the refusal did not print the digest the registry serves — or printed one other than "
            + "the digest the checked-in component.yaml records, which would mean the tag has moved "
            + "for real and charts/bundle/"
            + Component
            + "/component.yaml needs re-review. Its "
            + "output was:\n"
            + run.Output
        );

        AssertNothingWasApplied(run);
    }

    /// <summary>
    ///     The checked-in record passes the gate: the tag serves the digest beside it. This is the
    ///     positive control for the two refusals above.
    /// </summary>
    /// <remarks>
    ///     ⚠ Over <c>--verify</c> rather than an apply, because the tree is the real one and a real
    ///     apply would need a cluster. <c>--verify</c> runs the same <c>verify_images</c> the apply
    ///     path does, and for a <c>manifest:</c> component the rest of it is one HTTP HEAD.
    /// </remarks>
    [Fact]
    public async Task TheRecordedDigestIsWhatTheTagServesToday() {
        Assert.SkipUnless(
            BundleInstaller.OnPath("bash"),
            BundleInstaller.SkipWithoutBash(
                "install.sh",
                "that the digest gate passes over the checked-in record, so the two refusals it "
                + "is tested for are refusals and not a gate that refuses everything."
            )
        );

        Assert.SkipUnless(
            await Registry.Answers(TestContext.Current.CancellationToken),
            "SKIPPED — ghcr.io did not answer. WOULD PROVE: that charts/bundle/"
            + Component
            + "/component.yaml's recorded digest is what its tag serves today."
        );

        var run = await BundleInstaller.RunAsync(
            "--verify --component " + Component,
            null,
            TestContext.Current.CancellationToken
        );

        run.ExitCode.ShouldBe(
            0,
            $"charts/bundle/install.sh --verify refused `{Component}` over the checked-in record. "
            + "Either the tag has moved — re-review it and `images.sh --resolve` — or the gate "
            + "refuses a correct record, in which case the two refusal cases in this class prove "
            + "nothing. Its output was:\n"
            + run.Output
        );

        var images = BundleInstaller.Images(Component);

        images.ShouldNotBeEmpty(
            $"charts/bundle/{Component}/component.yaml records no images, so this control verified "
            + "nothing and passed."
        );

        foreach (var entry in images) {
            run.Output.ShouldContain(
                "✔ image          " + entry,
                Case.Sensitive,
                $"charts/bundle/install.sh --verify did not report `{entry}` as serving its recorded "
                + "digest. Its output was:\n"
                + run.Output
            );
        }
    }

    /// <summary>
    ///     The refusal came before any apply: no kubectl or helm line, and no error from either.
    /// </summary>
    static void AssertNothingWasApplied(BundleInstaller.Run run) {
        run.Output.ShouldContain(
            "refused by the digest gate",
            Case.Sensitive,
            "the run failed without saying the digest gate refused the component, so a reader "
            + "would go looking for a broken chart. Its output was:\n"
            + run.Output
        );

        foreach (var trace in new[] { "kubectl", "helm", "connection", "KUBECONFIG", "Bundle applied" }) {
            run.Output.ShouldNotContain(
                trace,
                Case.Sensitive,
                $"the output mentions `{trace}`, which means install.sh reached the apply for a "
                + "component the digest gate should have stopped in front of. A gate behind the "
                + "apply is a record nothing consumed, with extra steps. Its output was:\n"
                + run.Output
            );
        }
    }

    /// <summary>
    ///     A throwaway copy of <c>charts/bundle/</c> whose <c>install.sh</c> reads the copy.
    /// </summary>
    /// <remarks>
    ///     ⚠ The whole directory and not one component, because <c>install.sh</c> reads
    ///     <c>bundle.yaml</c> for the roster and sources <c>oci.sh</c> beside itself; a copy of
    ///     one <c>component.yaml</c> would be a copy the script cannot find. The checked-in tree is
    ///     never edited: a sabotage that leaked into the working tree would be a broken pin
    ///     somebody commits.
    /// </remarks>
    sealed class BundleCopy : IDisposable {
        readonly string root;

        BundleCopy(string root) {
            this.root = root;
        }

        public static BundleCopy Create() {
            var source = Path.Combine(BundleInstaller.RepositoryRoot, "charts", "bundle");
            var root = Path.Combine(Path.GetTempPath(), "cybercloud-bundle-" + Guid.NewGuid().ToString("N"));

            foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories)) {
                Directory.CreateDirectory(Path.Combine(root, Path.GetRelativePath(source, directory)));
            }

            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)) {
                File.Copy(file, Path.Combine(root, Path.GetRelativePath(source, file)));
            }

            return new(root);
        }

        /// <summary>The copy's installer.</summary>
        public string Script => Path.Combine(root, "install.sh");

        /// <summary>A kubeconfig path that names no file, so an apply that slipped through fails loudly.</summary>
        public string NoCluster => Path.Combine(root, "no-such-kubeconfig");

        /// <summary>
        ///     Rewrites the digest of a component's first recorded image and returns the image
        ///     reference without its digest.
        /// </summary>
        /// <param name="component">The component's directory name.</param>
        /// <param name="rewrite">Maps the recorded <c>sha256:…</c> to what the copy should carry.</param>
        public string SabotageDigest(string component, Func<string, string> rewrite) {
            var file = Path.Combine(root, component, "component.yaml");
            var lines = File.ReadAllLines(file);
            var inside = false;

            for (var index = 0; index < lines.Length; index++) {
                var line = lines[index];

                if (line.StartsWith("images:", StringComparison.Ordinal)) {
                    inside = true;
                    continue;
                }

                if (!inside) {
                    continue;
                }

                if (!line.StartsWith("  - ", StringComparison.Ordinal)) {
                    break;
                }

                var entry = line[4..].Trim();
                var at = entry.IndexOf('@', StringComparison.Ordinal);

                at.ShouldBeGreaterThan(
                    0,
                    $"charts/bundle/{component}/component.yaml records `{entry}` with no digest to sabotage."
                );

                lines[index] = "  - " + entry[..at] + "@" + rewrite(entry[(at + 1)..]);
                File.WriteAllLines(file, lines);

                return entry[..at];
            }

            throw new InvalidOperationException(
                $"charts/bundle/{component}/component.yaml records no images: block to sabotage."
            );
        }

        public void Dispose() {
            try {
                Directory.Delete(root, true);
            } catch (IOException) {
                // A copy left in the temp directory is untidy and is not a failed assertion.
            }
        }
    }

    /// <summary>Whether the registry the sabotaged component pulls from answers at all.</summary>
    static class Registry {
        public static async Task<bool> Answers(CancellationToken cancellationToken) {
            try {
                using var client = new HttpClient();
                client.Timeout = TimeSpan.FromSeconds(15);
                using var request = new HttpRequestMessage(HttpMethod.Head, "https://ghcr.io/v2/");
                using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

                // 401 is the expected answer from an anonymous /v2/ probe and is a registry answering.
                return true;
            } catch (HttpRequestException) {
                return false;
            } catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) {
                return false;
            }
        }
    }
}
