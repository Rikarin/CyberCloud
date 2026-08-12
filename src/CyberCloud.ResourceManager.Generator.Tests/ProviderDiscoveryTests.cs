using CyberCloud.Providers.Sample.Contracts;

namespace CyberCloud.ResourceManager.Generator.Tests;

/// <summary>
///     What happens between <c>--provider-assembly &lt;path&gt;</c> and a file on disk: a real
///     provider assembly is loaded, its <c>Describe</c> is run, and the four surfaces come out.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The failure this suite is named after.</b> A generator that discovers no provider,
///         writes a valid empty document and exits 0 is indistinguishable from a healthy one at every
///         level except this one. It has happened in this repository —
///         <c>build/Build.Generate.cs § ProviderAssemblies</c> carries the post-mortem — and every
///         assertion below is a number that would have been zero.
///     </para>
///     <para>
///         ⚠ <b>A real assembly, loaded by path, not a hand-built registry.</b>
///         <c>CyberCloud.ResourceManager.Contracts.Tests/Generation</c> already drives the emitters
///         from fixtures, and it cannot fail on anything between the file name and the type: the
///         <c>Assembly.LoadFrom</c>, the exported-types filter, the parameterless constructor, or the
///         identity of <c>IResourceProvider</c> across load contexts.
///     </para>
/// </remarks>
public sealed class ProviderDiscoveryTests {
    const int Ok = 0;

    const string IndexFile = "index.json";
    const string DocumentFile = SampleWidgets.V2026 + ".json";

    [Fact]
    public void ARealProviderAssemblyBecomesADocumentRatherThanASuccessfulRunThatFoundNothing() {
        using var tree = new TemporaryTree();

        var run = Generator.Run(tree, check: false, Generator.SampleProviderAssembly);

        run.ExitCode.ShouldBe(Ok);

        var report = tree.Report();

        // ⚠ Each of these was 0 in the defect. `providers` counts namespaces, `resourceTypes` counts
        // types, `apiVersions` counts documents that describe one — three independent ways for a
        // discovery regression to be visible instead of silent.
        report["providers"]!.GetValue<int>().ShouldBe(1);
        report["resourceTypes"]!.GetValue<int>().ShouldBe(1);
        report["apiVersions"]!.GetValue<int>().ShouldBe(1);

        TemporaryTree.FilesUnder(tree.OpenApiDirectory).ShouldBe([DocumentFile, IndexFile]);

        // ADR-012's other three, from the document rather than from the registry.
        TemporaryTree.FilesUnder(tree.DerivedDirectory).ShouldBe([
            "cli/" + SampleWidgets.V2026 + ".json",
            "forms/" + SampleWidgets.V2026 + ".json",
            "sdk/" + SampleWidgets.V2026 + ".cs"
        ]);
    }

    [Fact]
    public void EveryNameSurvivesGenerationWithItsCasingIntact() {
        // ⚠ THE DEFECT CLASS THIS IS FOR, in the platform's own history: a
        // `resourcegroup`/`resourceGroup` mismatch failed every create and surfaced as a 404 with the
        // reason only in a log line. A URL template is compared byte for byte by every client on the
        // planet, and one folded character makes the generated CLI, SDK and portal call a path the
        // gateway does not route.
        //
        // Nothing in the emitters is culture-invariant by construction — they call ToLower/ToUpper to
        // build these names — so this asserts the outcome rather than the call, and it asserts it on
        // the file that was actually written.
        using var tree = new TemporaryTree();

        Generator.Run(tree, check: false, Generator.SampleProviderAssembly).ExitCode.ShouldBe(Ok);

        var document = File.ReadAllText(Path.Combine(tree.OpenApiDirectory, DocumentFile));

        document.ShouldContain("/providers/" + SampleWidgets.ProviderNamespace + "/" + SampleWidgets.TypePath + "/");
        document.ShouldContain("{resourceGroupName}");
        document.ShouldContain("/resourceGroups/");
        // The parameter's own `name`, which is what a client binds a value to. ⚠ Not the
        // `#/components/parameters/ResourceGroupName` key, which is PascalCase on purpose and was
        // asserted against by an earlier draft of this test — a component key names a definition and
        // a parameter name names a wire field, and only the second one has to match the gateway.
        document.ShouldContain("\"name\": \"resourceGroupName\"");

        // The folded spellings, each of which is a path the gateway does not serve.
        document.ShouldNotContain("resourcegroup", Case.Sensitive);
        document.ShouldNotContain("resourceGroupname", Case.Sensitive);
        document.ShouldNotContain(SampleWidgets.ProviderNamespace.ToUpperInvariant(), Case.Sensitive);
        document.ShouldNotContain(SampleWidgets.ProviderNamespace.ToLowerInvariant(), Case.Sensitive);
    }

    [Fact]
    public void TheApiVersionTheProviderDeclaredIsTheOneOnDiskAndInTheReport() {
        // A file name derived from anything but the declared version — a clock, a build number, a
        // culture-formatted date — would regenerate differently on a different day or machine, and
        // the Generated surfaces gate compares bytes.
        using var tree = new TemporaryTree();

        Generator.Run(tree, check: false, Generator.SampleProviderAssembly).ExitCode.ShouldBe(Ok);

        tree.Report()["documents"]!.AsArray()
            .Select(x => x!["apiVersion"]!.GetValue<string>())
            .ShouldContain(SampleWidgets.V2026);

        File.Exists(Path.Combine(tree.OpenApiDirectory, DocumentFile)).ShouldBeTrue();
    }

    [Fact]
    public void TwoIndependentRunsProduceByteIdenticalFiles() {
        // ⚠ docs/plan/23 § The architecture gates asks that all four surfaces "regenerate
        // byte-identically", and the gate compares bytes. Anything ordered by a hash set, any
        // culture-sensitive formatting, any clock reaching a document — including through a
        // provider's own Describe, which IResourceProvider forbids from consulting one — breaks that
        // non-deterministically, which is the worst way for a gate to fail.
        //
        // ⚠ Two runs into two DIFFERENT trees, not one tree compared with itself: a second run
        // against a populated directory reports "not drifted" by comparing against what the first run
        // wrote, which is self-consistency rather than reproducibility.
        using var first = new TemporaryTree();
        using var second = new TemporaryTree();

        Generator.Run(first, check: false, Generator.SampleProviderAssembly).ExitCode.ShouldBe(Ok);
        Generator.Run(second, check: false, Generator.SampleProviderAssembly).ExitCode.ShouldBe(Ok);

        foreach (var directory in new[] { "openapi", "generated" }) {
            var left = Path.Combine(first.Root, directory);
            var right = Path.Combine(second.Root, directory);

            var files = TemporaryTree.FilesUnder(left);
            files.ShouldBe(TemporaryTree.FilesUnder(right));
            files.ShouldNotBeEmpty();

            foreach (var file in files) {
                File.ReadAllBytes(Path.Combine(right, file)).ShouldBe(
                    File.ReadAllBytes(Path.Combine(left, file)),
                    $"{directory}/{file} did not regenerate byte-identically, so the Generated "
                    + "surfaces gate would pass or fail depending on which run CI happened to make"
                );
            }
        }
    }

    [Fact]
    public void NoEmittedFileNamesTheDirectoryItWasWrittenTo() {
        // An absolute path in a checked-in generated file is a byte gate that can only pass on the
        // machine that last ran the generator. The two trees below share nothing but a temp root, so
        // a leaked path is also a leaked run.
        using var tree = new TemporaryTree();

        Generator.Run(tree, check: false, Generator.SampleProviderAssembly).ExitCode.ShouldBe(Ok);

        foreach (var directory in new[] { tree.OpenApiDirectory, tree.DerivedDirectory }) {
            foreach (var file in TemporaryTree.FilesUnder(directory)) {
                var text = File.ReadAllText(Path.Combine(directory, file));

                text.ShouldNotContain(tree.Root, Case.Insensitive);
                text.ShouldNotContain(Environment.MachineName, Case.Insensitive);
            }
        }
    }

    [Fact]
    public void CheckWritesNothingBecauseAGateMustNotChangeTheTreeItInspects() {
        // ⚠ `--check` is what build/Build.Architecture.cs passes. An architecture gate that rewrites
        // the files it is about to compare is a gate that cannot fail: the drift it exists to find is
        // repaired by the act of looking for it, and the contributor's next `git status` is the only
        // notice anyone gets.
        using var tree = new TemporaryTree();

        var run = Generator.Run(tree, check: true, Generator.SampleProviderAssembly);

        run.ExitCode.ShouldBe(Ok);

        TemporaryTree.FilesUnder(tree.OpenApiDirectory).ShouldBeEmpty();
        TemporaryTree.FilesUnder(tree.DerivedDirectory).ShouldBeEmpty();

        // ⚠ The report is still written, and must be: it is the only thing the gate reads, and a
        // `--check` that suppressed it would fail the build with "wrote no report at …" rather than
        // with whatever it found.
        File.Exists(tree.ReportFile).ShouldBeTrue();
        tree.Report()["providers"]!.GetValue<int>().ShouldBe(1);
        // Nothing was checked in, so everything it would have written counts as drift — which is the
        // fact the gate turns into "run ./build.sh Generate and commit the diff".
        tree.Report()["documents"]!.AsArray().ShouldAllBe(x => x!["drifted"]!.GetValue<bool>());
        tree.Report()["documents"]!.AsArray().ShouldAllBe(x => !x!["published"]!.GetValue<bool>());
    }

    [Fact]
    public void CheckStillReportsDriftAgainstWhatIsAlreadyThere() {
        // The gate's actual job: files exist, one of them is wrong, and saying so must not involve
        // fixing it.
        using var tree = new TemporaryTree();

        Generator.Run(tree, check: false, Generator.SampleProviderAssembly).ExitCode.ShouldBe(Ok);

        var edited = Path.Combine(tree.OpenApiDirectory, DocumentFile);
        var original = File.ReadAllBytes(edited);
        File.WriteAllText(edited, "{\"openapi\":\"3.1.0\"}");

        Generator.Run(tree, check: true, Generator.SampleProviderAssembly).ExitCode.ShouldBe(Ok);

        tree.Report()["documents"]!.AsArray()
            .ShouldContain(x => x!["file"]!.GetValue<string>() == DocumentFile
                && x["drifted"]!.GetValue<bool>()
                && x["published"]!.GetValue<bool>());

        // Untouched by the run that reported it.
        File.ReadAllBytes(edited).ShouldNotBe(original);
        File.ReadAllText(edited).ShouldBe("{\"openapi\":\"3.1.0\"}");
    }

    [Fact]
    public void WithoutCheckADriftedDocumentIsRewrittenInPlace() {
        // The other half of the same decision — OpenApiArtifacts' remarks: "the fix for a red
        // Generate is `git add`". Writing and then reporting is the only order with both properties,
        // and a generator that reported drift without repairing it would send contributors to
        // hand-copy JSON out of a build log.
        using var tree = new TemporaryTree();

        Generator.Run(tree, check: false, Generator.SampleProviderAssembly).ExitCode.ShouldBe(Ok);

        var document = Path.Combine(tree.OpenApiDirectory, DocumentFile);
        var generated = File.ReadAllBytes(document);
        File.WriteAllText(document, "{\"openapi\":\"3.1.0\"}");

        Generator.Run(tree, check: false, Generator.SampleProviderAssembly).ExitCode.ShouldBe(Ok);

        File.ReadAllBytes(document).ShouldBe(generated);
        tree.Report()["documents"]!.AsArray()
            .ShouldContain(x => x!["file"]!.GetValue<string>() == DocumentFile && x["drifted"]!.GetValue<bool>());
    }

    [Fact]
    public void WithoutADerivedOutputTheOtherThreeSurfacesAreNotWrittenAnywhere() {
        // ⚠ The flag is optional and the default must be "do nothing", not "somewhere sensible". The
        // only other candidate is the current directory, which for `./build.sh Generate` is the
        // repository root — three generated files dropped next to README.md, and generated/
        // untouched and still passing its byte comparison.
        using var tree = new TemporaryTree();

        var run = Generator.Invoke(
            "--output", tree.OpenApiDirectory,
            "--report", tree.ReportFile,
            "--provider-assembly", Generator.SampleProviderAssembly
        );

        run.ExitCode.ShouldBe(Ok);

        TemporaryTree.FilesUnder(tree.OpenApiDirectory).ShouldNotBeEmpty();
        Directory.Exists(tree.DerivedDirectory).ShouldBeFalse();

        var report = tree.Report();

        report["derived"]!.AsArray().Count.ShouldBe(0);
        report["derivedStale"]!.AsArray().Count.ShouldBe(0);
    }
}
