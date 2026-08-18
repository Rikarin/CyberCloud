namespace CyberCloud.ResourceManager.Generator.Tests;

/// <summary>
///     The JSON report, which is the entire interface between this process and the two build targets
///     that decide whether the tree is acceptable.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Read <c>build/Build.Generate.cs</c> § <c>Parse</c> alongside this file.</b> It reaches
///         into the report with a null-forgiving indexer per field —
///         <c>x["apiVersion"]!.GetValue&lt;string&gt;()</c> — so a key renamed here does not fail
///         here, does not fail at compile time, and does not fail with a message about the report. It
///         fails with a <c>NullReferenceException</c> inside a Nuke target, and the reason lives one
///         character deep in a string literal in a different project.
///     </para>
///     <para>
///         ⚠ That is the same defect class as the <c>resourcegroup</c>/<c>resourceGroup</c> mismatch
///         that failed every create in this platform and surfaced as a 404. Two spellings of one name
///         across a boundary neither compiler checks; the only defence is a test that spells both.
///     </para>
/// </remarks>
public sealed class GenerationReportTests {
    const int Ok = 0;

    /// <summary>Exactly what <c>Build.Generate.cs § Parse</c> reads off the root, plus what it does not.</summary>
    static readonly string[] RootKeys = [
        // ⚠ The one key that is read off the REGISTRY rather than off anything this process emitted,
        // and it exists because no generated surface carries the fact behind it. An action reaches
        // openapi/, the cyc verb tree, the SDK and the portal form from its declaration alone;
        // whether a handler can serve it is in none of the four, so a document publishing a call that
        // answers 500 is byte-identical to one where it works. build/Build.Architecture.cs § the
        // Action handlers gate forms its verdict from this array.
        "actions",
        "apiVersions",
        "assembliesScanned",
        // ⚠ The four chart keys arrived with ADR-012's fifth surface and this list was not grown with
        // them, so this test had been red on the branch ever since. They are what
        // build/Build.Charts.cs reads back out of the report — the pair counts and the per-chart
        // annotation results — and Build.Generate.cs § Parse indexes every one of them by literal
        // name.
        "chartAnnotations",
        "chartManagedCharts",
        "chartTypesNamingAChart",
        "chartUnpaired",
        // ⚠ `clean` is written and, as of this test, read by nothing in build/. It is asserted
        // because it is published, not because it is consumed — see CleanIsTheAndOfBothHalves.
        "clean",
        "derived",
        "derivedStale",
        "documents",
        "providers",
        "resourceTypes",
        "stale"
    ];

    // ⚠ The MEMBERS of a chartAnnotations entry are deliberately not asserted here, and that is a
    // gap rather than an omission. Build.Generate.cs indexes them by literal name and null-forgives
    // the result, exactly as it does for DocumentKeys, so a rename is a NullReferenceException in a
    // build rather than a compiler error. But CyberCloud.Providers.Sample declares no chart on
    // purpose, so this run produces an empty array — and a loop over it would assert nothing while
    // looking like it asserted something. Closing it needs a fixture provider that names a chart.

    static readonly string[] DocumentKeys = [
        "apiVersion",
        "breakingChanges",
        "drifted",
        "file",
        "published",
        "structuralProblems"
    ];

    static readonly string[] DerivedKeys = [
        "apiVersion",
        "drifted",
        "file",
        "problems",
        "published",
        "surface"
    ];

    /// <summary>The members of one <c>actions</c> entry, which <c>Parse</c> also indexes by literal.</summary>
    /// <remarks>
    ///     ⚠ Asserted where <c>chartAnnotations</c>' members are not, and the difference is that this
    ///     array is never empty for the fixture: <c>CyberCloud.Providers.Sample</c> declares
    ///     <c>ping</c>, so the loop below has something to walk. That gap is recorded above.
    /// </remarks>
    static readonly string[] ActionKeys = [
        "handler",
        "longRunning",
        "name",
        "secret",
        "type"
    ];

    static IReadOnlyList<string> KeysOf(JsonObject value) =>
        [.. value.Select(x => x.Key).OrderBy(x => x, StringComparer.Ordinal)];

    [Fact]
    public void TheReportSpellsEveryKeyTheBuildReadsAndSpellsThemTheSameWay() {
        using var tree = new TemporaryTree();

        Generator.Run(tree, check: false, Generator.SampleProviderAssembly).ExitCode.ShouldBe(Ok);

        var report = tree.Report();

        KeysOf(report).ShouldBe(
            [.. RootKeys.OrderBy(x => x, StringComparer.Ordinal)],
            "build/Build.Generate.cs § Parse indexes this object by literal name and null-forgives "
            + "the result, so a key that is renamed, dropped or added here changes the build's "
            + "behaviour with nothing in either compiler to say so"
        );

        var documents = report["documents"]!.AsArray();

        // The Sample provider declares one api-version, so this is index.json plus one document.
        documents.Count.ShouldBe(2);

        foreach (var document in documents) {
            KeysOf(document!.AsObject()).ShouldBe([.. DocumentKeys.OrderBy(x => x, StringComparer.Ordinal)]);
        }

        var derived = report["derived"]!.AsArray();

        // Three surfaces from one api-version: the cyc verb tree, the portal forms and the .NET SDK.
        derived.Count.ShouldBe(3);

        foreach (var surface in derived) {
            KeysOf(surface!.AsObject()).ShouldBe([.. DerivedKeys.OrderBy(x => x, StringComparer.Ordinal)]);
        }

        var actions = report["actions"]!.AsArray();

        // The Sample provider declares exactly one action, `ping`. Asserted rather than assumed: an
        // empty array would let the loop below pass while walking nothing, which is the failure mode
        // the chartAnnotations note above records for the one case where it cannot be avoided.
        actions.Count.ShouldBe(1);

        foreach (var action in actions) {
            KeysOf(action!.AsObject()).ShouldBe([.. ActionKeys.OrderBy(x => x, StringComparer.Ordinal)]);
        }
    }

    [Fact]
    public void EveryValueHasTheJsonTypeTheBuildAsksItFor() {
        // ⚠ `GetValue<int>()` on a JSON string throws InvalidOperationException, not a cast error, and
        // the message names neither the field nor the file. A count rendered as "1" instead of 1 —
        // one stray ToString() — is a valid report, a passing generator and a build that breaks in a
        // place with no connection to the change.
        using var tree = new TemporaryTree();

        Generator.Run(tree, check: false, Generator.SampleProviderAssembly).ExitCode.ShouldBe(Ok);

        var report = tree.Report();

        // Every read below is the one Parse makes, in the type Parse asks for. Collected rather than
        // discarded so the reads cannot be optimised — or analysed — away.
        var read = new List<object>();

        Should.NotThrow(() => {
            read.Add(report["providers"]!.GetValue<int>());
            read.Add(report["resourceTypes"]!.GetValue<int>());
            read.Add(report["apiVersions"]!.GetValue<int>());
            read.Add(report["assembliesScanned"]!.GetValue<int>());
            read.Add(report["clean"]!.GetValue<bool>());

            foreach (var document in report["documents"]!.AsArray()) {
                var value = document!.AsObject();

                read.Add(value["file"]!.GetValue<string>());
                read.Add(value["apiVersion"]!.GetValue<string>());
                read.Add(value["published"]!.GetValue<bool>());
                read.Add(value["drifted"]!.GetValue<bool>());
                read.AddRange(value["structuralProblems"]!.AsArray().Select(x => x!.GetValue<string>()));
                read.AddRange(value["breakingChanges"]!.AsArray().Select(x => x!.GetValue<string>()));
            }

            foreach (var surface in report["derived"]!.AsArray()) {
                var value = surface!.AsObject();

                read.Add(value["surface"]!.GetValue<string>());
                read.Add(value["file"]!.GetValue<string>());
                read.Add(value["apiVersion"]!.GetValue<string>());
                read.Add(value["published"]!.GetValue<bool>());
                read.Add(value["drifted"]!.GetValue<bool>());
                read.AddRange(value["problems"]!.AsArray().Select(x => x!.GetValue<string>()));
            }

            foreach (var action in report["actions"]!.AsArray()) {
                var value = action!.AsObject();

                read.Add(value["type"]!.GetValue<string>());
                read.Add(value["name"]!.GetValue<string>());
                read.Add(value["longRunning"]!.GetValue<bool>());
                read.Add(value["secret"]!.GetValue<bool>());

                // ⚠ `handler` is the ONE nullable member of the report, and Parse reads it with a
                // pattern rather than a null-forgiving indexer for that reason. A JSON null is not a
                // JsonNull node — System.Text.Json.Nodes drops the property — so "the provider named
                // no handler" and "the key is missing" arrive identically, and the read must survive
                // both. `ping` names one, so what is exercised here is the non-null branch; the null
                // branch is what every row of actions-without-handlers.txt is.
                read.Add(value["handler"] is { } handler ? handler.GetValue<string>() : "(none)");
            }

            read.AddRange(report["stale"]!.AsArray().Select(x => x!.GetValue<string>()));
            read.AddRange(report["derivedStale"]!.AsArray().Select(x => x!.GetValue<string>()));
        });

        // 5 root values + 2 documents × 4 scalars + 3 derived surfaces × 5 scalars + 1 action × 5.
        read.Count.ShouldBe(33);
    }

    [Fact]
    public void AssembliesScannedCountsWhatWasHandedInRatherThanWhatWasFound() {
        // ⚠ THE VACUOUS-PASS TRIPWIRE, and the reason this project's suite exists at all. The
        // discovery predicate in build/Build.Generate.cs once matched providers at a fixed nesting
        // depth, found none, and the target reported success with a provider sitting in the solution.
        // The only signal that could have caught it is the difference between "no assembly was
        // handed to me" and "an assembly was handed to me and declared nothing" — which is exactly
        // the difference between these two numbers. Collapse them and the tripwire is gone.
        using var scannedNothing = new TemporaryTree();
        using var scannedSomething = new TemporaryTree();

        Generator.Run(scannedNothing).ExitCode.ShouldBe(Ok);
        Generator.Run(scannedSomething, check: false, Generator.ProviderlessAssembly).ExitCode.ShouldBe(Ok);

        scannedNothing.Report()["assembliesScanned"]!.GetValue<int>().ShouldBe(0);
        scannedNothing.Report()["providers"]!.GetValue<int>().ShouldBe(0);

        scannedSomething.Report()["assembliesScanned"]!.GetValue<int>().ShouldBe(1);
        scannedSomething.Report()["providers"]!.GetValue<int>().ShouldBe(0);
    }

    [Fact]
    public void AnEmptyRegistryStillProducesExactlyOneValidDocument() {
        // build/Build.Generate.cs warns rather than fails on zero providers, and its comment claims
        // the run "still wrote openapi/index.json — a valid, empty OpenAPI document that says zero in
        // three places", so that "the generator did not run" and "the generator found no provider"
        // are different files on disk. That claim is only true if this holds.
        using var tree = new TemporaryTree();

        Generator.Run(tree).ExitCode.ShouldBe(Ok);

        TemporaryTree.FilesUnder(tree.OpenApiDirectory).ShouldBe(["index.json"]);
        tree.Report()["documents"]!.AsArray().Count.ShouldBe(1);
        // No api-version exists, so nothing derived from one does either — and `derived` being empty
        // rather than absent is what keeps Build.Generate.cs's loop from being handed a null.
        tree.Report()["derived"]!.AsArray().Count.ShouldBe(0);
        tree.Report()["apiVersions"]!.GetValue<int>().ShouldBe(0);
    }

    [Fact]
    public void TheReportDirectoryIsCreatedRatherThanAssumedToExist() {
        // The build points --report at artifacts/generation-report.json, and artifacts/ is deleted by
        // `./build.sh Clean` and absent in a fresh clone. Without the mkdir the run fails with a
        // DirectoryNotFoundException, which the catch filter turns into exit 3 — a generator that
        // "could not run" because of a directory it was about to create.
        using var tree = new TemporaryTree();

        Directory.Exists(Path.GetDirectoryName(tree.ReportFile)!).ShouldBeFalse();

        Generator.Run(tree, check: false, Generator.SampleProviderAssembly).ExitCode.ShouldBe(Ok);

        File.Exists(tree.ReportFile).ShouldBeTrue();
    }

    [Fact]
    public void CleanIsTheAndOfBothHalvesRatherThanTheOpenApiHalfAlone() {
        // ⚠ The two halves are gated differently and summarised together. A run whose OpenAPI
        // documents are byte-identical and whose cyc verb tree is not is not a clean run, and a
        // `clean` computed from the first half alone would say it was.
        //
        // ⚠ Nothing in build/ reads this field today — Build.Generate.cs § Parse does not name it.
        // It is asserted because it is published in artifacts/generation-report.json, where a person
        // debugging a red Generate reads it; a summary that is wrong is worse than one that is absent.
        using var tree = new TemporaryTree();

        // First run: everything is new, so everything drifted.
        Generator.Run(tree, check: false, Generator.SampleProviderAssembly).ExitCode.ShouldBe(Ok);
        tree.Report()["clean"]!.GetValue<bool>().ShouldBeFalse();

        // Second run: both halves reproduce what the first wrote.
        Generator.Run(tree, check: false, Generator.SampleProviderAssembly).ExitCode.ShouldBe(Ok);
        tree.Report()["clean"]!.GetValue<bool>().ShouldBeTrue();

        // Now disturb only a derived surface. The OpenAPI half is untouched and still clean.
        File.WriteAllText(Path.Combine(tree.DerivedDirectory, "cli", "2026-08-01.json"), "{}");

        Generator.Run(tree, check: true, Generator.SampleProviderAssembly).ExitCode.ShouldBe(Ok);

        var report = tree.Report();

        report["documents"]!.AsArray().ShouldAllBe(x => !x!["drifted"]!.GetValue<bool>());
        report["derived"]!.AsArray().ShouldContain(x => x!["drifted"]!.GetValue<bool>());
        report["clean"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public void ACheckedInFileThisRunDidNotProduceIsReportedAsStaleRatherThanDeleted() {
        // docs/plan/08 § The provider registry gives removing an api-version a 12-month notice
        // window. `stale` is the only place a vanished version is visible, and OpenApiArtifacts'
        // remarks promise nothing is ever deleted — a build step that silently removed a published
        // contract is the failure that window exists to prevent.
        using var tree = new TemporaryTree();

        Generator.Run(tree, check: false, Generator.SampleProviderAssembly).ExitCode.ShouldBe(Ok);

        var orphan = Path.Combine(tree.OpenApiDirectory, "2019-01-01.json");
        File.WriteAllText(orphan, "{}");

        Generator.Run(tree, check: false, Generator.SampleProviderAssembly).ExitCode.ShouldBe(Ok);

        tree.Report()["stale"]!.AsArray().Select(x => x!.GetValue<string>()).ShouldContain("2019-01-01.json");
        File.Exists(orphan).ShouldBeTrue();
    }
}
