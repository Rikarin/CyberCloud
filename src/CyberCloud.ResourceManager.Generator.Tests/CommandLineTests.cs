using CyberCloud.Providers.Sample.Contracts;

namespace CyberCloud.ResourceManager.Generator.Tests;

/// <summary>
///     The generator's command line, which is the only thing standing between
///     <c>build/Build.Generate.cs</c> and a run that writes somewhere nobody is looking.
/// </summary>
/// <remarks>
///     ⚠ <b>Every test here is about a failure that would otherwise be silent.</b> A generator that
///     refuses loudly costs a contributor one minute; a generator that accepts a mistyped flag and
///     writes a valid, empty, wrong document costs whoever notices the missing API surface a day.
///     docs/plan/23 § The architecture gates compares the checked-in files byte for byte, so a run
///     that wrote to the wrong place does not fail — it passes, over files nothing regenerated.
/// </remarks>
public sealed class CommandLineTests {
    const int Ok = 0;
    const int BadArguments = 2;
    const int Failed = 3;

    [Fact]
    public void NoOutputDirectoryIsARefusalRatherThanAGuess() {
        // ⚠ The alternative is worse than it looks: with no --output the only other answer is the
        // current directory, and the current directory of `./build.sh Generate` is the repository
        // root. A run that "worked" would have scattered index.json and 2026-08-01.json next to
        // README.md, and the real openapi/ would still hold whatever it held.
        var run = Generator.Invoke("--check");

        run.ExitCode.ShouldBe(BadArguments);
        run.Error.ShouldContain("--output");
    }

    [Fact]
    public void AnUnrecognisedArgumentStopsTheRunAndSaysWhichOne() {
        using var tree = new TemporaryTree();

        // A typo, which is the realistic shape of this mistake.
        var run = Generator.Invoke("--outupt", tree.OpenApiDirectory);

        run.ExitCode.ShouldBe(BadArguments);
        // Naming the offender matters: "usage:" alone leaves a contributor diffing their command
        // against a five-flag synopsis by eye.
        run.Error.ShouldContain("--outupt");
        // ⚠ And nothing was written. An unrecognised argument that still ran would be the same
        // defect as no argument checking at all.
        TemporaryTree.FilesUnder(tree.Root).ShouldBeEmpty();
    }

    [Fact]
    public void AFlagWithNoValueIsRefusedRatherThanReadingPastTheEndOfTheArguments() {
        // `--output` as the last token. The parser's `when i + 1 < arguments.Length` guard is the
        // only thing between this and an IndexOutOfRangeException, which reaches a contributor as an
        // unhandled stack trace out of the middle of a Nuke target.
        Generator.Invoke("--output").ExitCode.ShouldBe(BadArguments);
        Generator.Invoke("--output", "somewhere", "--report").ExitCode.ShouldBe(BadArguments);
        Generator.Invoke("--output", "somewhere", "--derived-output").ExitCode.ShouldBe(BadArguments);
        Generator.Invoke("--output", "somewhere", "--provider-assembly").ExitCode.ShouldBe(BadArguments);
    }

    [Fact]
    public void EveryProviderAssemblyIsKeptRatherThanOnlyTheLast() {
        // ⚠ THE DEFECT THIS EXISTS FOR. The build passes one --provider-assembly per provider, and a
        // parser that assigned instead of accumulating would describe exactly one provider — the one
        // that sorted last — while reporting a successful run. `assembliesScanned` is the only number
        // in the report that can tell the two apart, so it is the one asserted.
        using var tree = new TemporaryTree();

        var run = Generator.Run(
            tree,
            check: false,
            Generator.SampleProviderAssembly,
            Generator.ProviderlessAssembly
        );

        run.ExitCode.ShouldBe(Ok);

        var report = tree.Report();

        report["assembliesScanned"]!.GetValue<int>().ShouldBe(2);
        // Both were loaded and only one of them declares a provider — which is also the difference
        // between "we scanned two assemblies" and "we found one provider".
        report["providers"]!.GetValue<int>().ShouldBe(1);
    }

    [Fact]
    public void AProviderAssemblyThatIsNotThereIsAnExitCodeRatherThanAStackTrace() {
        using var tree = new TemporaryTree();

        var run = Generator.Run(tree, check: false, Path.Combine(tree.Root, "CyberCloud.Providers.Nope.dll"));

        // ⚠ 3, not 0. docs/plan/23's Generated surfaces gate treats 0 as "the facts are in the
        // report"; a run that never loaded a provider assembly has no facts, and reporting one
        // anyway is how a whole provider's API surface goes missing under a green tick.
        run.ExitCode.ShouldBe(Failed);
        run.Error.ShouldNotBeEmpty();
        // The message is a contributor's, not a stack trace's: it survives the catch filter on
        // Program.Main, and narrowing that filter is what this asserts against.
        run.Error.ShouldNotContain("   at ", Case.Sensitive);
    }

    [Fact]
    public void AFileThatIsNotAManagedAssemblyIsAnExitCodeRatherThanAStackTrace() {
        using var tree = new TemporaryTree();

        // The realistic cause is a truncated or half-copied build output, not sabotage.
        var junk = Path.Combine(tree.Root, "CyberCloud.Providers.Broken.dll");
        File.WriteAllBytes(junk, [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07]);

        var run = Generator.Run(tree, check: false, junk);

        run.ExitCode.ShouldBe(Failed);
        run.Error.ShouldNotBeEmpty();
    }

    [Fact]
    public void TwoAssembliesDeclaringOneProviderNamespaceFailRatherThanShadowingEachOther() {
        // ⚠ ProviderRegistry.Build's own words for why: "the second would shadow the first's resource
        // types and the symptom would be endpoints answering 404 with nothing in the log". The
        // generator has to let that reach the exit code rather than swallowing it into a report,
        // because a shadowed provider produces a document that is structurally valid and wrong.
        using var tree = new TemporaryTree();

        var run = Generator.Run(
            tree,
            check: false,
            Generator.SampleProviderAssembly,
            Generator.SampleProviderAssembly
        );

        run.ExitCode.ShouldBe(Failed);
        run.Error.ShouldContain(SampleWidgets.ProviderNamespace);
    }

    [Fact]
    public void TheLogNamesWhichOfTheFourSurfacesActuallyRan() {
        // docs/plan/02 § ADR-012 names four surfaces. Program.Main says which of them ran on every
        // run including the clean ones, because a log that did not would read like the pipeline
        // finished whatever it did — and the two branches being the right way round is the whole of
        // that claim.
        using var withDerived = new TemporaryTree();
        using var withoutDerived = new TemporaryTree();

        Generator.Run(withDerived, check: false, Generator.SampleProviderAssembly)
            .Output.ShouldContain("All four");

        Generator.Invoke(
                "--output", withoutDerived.OpenApiDirectory,
                "--report", withoutDerived.ReportFile,
                "--provider-assembly", Generator.SampleProviderAssembly
            )
            .Output.ShouldContain("OpenAPI only");
    }
}
