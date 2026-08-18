using System.Reflection;
using System.Text.RegularExpressions;

namespace CyberCloud.Providers.Terminal.Tests;

/// <summary>
///     The tables that exist twice — once in C# and once in a Helm template — compared value for value.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THIS IS THE HALF <c>./build.sh Charts</c> DOES NOT REACH, AND IT IS THE HALF THAT
///         MATTERS ON THIS ROW.</b> That target regenerates the chart's <c>@param</c> block from
///         <c>CloudConsoles.Schema2026</c> and byte-diffs it, so the configuration SURFACE cannot
///         drift. <c>ChartSurfaces</c> filters <c>templates/</c> out on purpose — no emitter has ever
///         read a Helm template — so the values behind the surface can, and here that means the sizing
///         table, the object-name joining and the image digests.
///     </para>
///     <para>
///         ⚠ <b>The object names are the sharpest of the three.</b> Two spellings of one object's name
///         is a pod that mounts a claim nobody created, and the failure is a shell that will not
///         schedule with a message about a missing volume rather than about a name.
///     </para>
/// </remarks>
public sealed class ConsoleSizingTests {
    [Fact]
    public void ThePresetTableIsTheSameInCSharpAndInTheChart() {
        var template = Helpers();

        foreach (var (name, expected) in CloudConsoles.Presets) {
            var row = Regex.Match(
                template,
                $@"""{Regex.Escape(name)}""\s+\(dict\s+""cpu""\s+""(?<cpu>[^""]+)""\s+""memory""\s+""(?<memory>[^""]+)""\)",
                RegexOptions.None,
                TimeSpan.FromSeconds(5)
            );

            row.Success.ShouldBeTrue($"the chart's helper has no row for the preset '{name}'");
            row.Groups["cpu"].Value.ShouldBe(expected.Cpu, name);
            row.Groups["memory"].Value.ShouldBe(expected.Memory, name);
        }

        // And no extra rows: a preset the chart offers and the API refuses is a values file that
        // renders and a resource body that 400s.
        Regex.Matches(template, @"""(c1\.[a-z]+)""\s+\(dict", RegexOptions.None, TimeSpan.FromSeconds(5))
            .Select(x => x.Groups[1].Value)
            .Order(StringComparer.Ordinal)
            .ShouldBe(CloudConsoles.Presets.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TheObjectNameJoiningIsTheSameInCSharpAndInTheChart() {
        var template = Helpers();

        // The chart spells them as printf formats; C# spells them as concatenations. Comparing the
        // SUFFIX is the strongest thing that survives both spellings.
        template.ShouldContain(@"printf ""%s-home""");
        template.ShouldContain(@"printf ""%s-shell""");

        CloudConsoles.HomeClaimName("x").ShouldBe("x-home");
        CloudConsoles.ShellName("x").ShouldBe("x-shell");
    }

    [Fact]
    public void TheImageDigestsAreTheSameInCSharpAndInTheChart() {
        // ⚠ Both are placeholders today — conformance.yaml § owed, `no-image-pipeline` — and this is
        // what makes them stay placeholders in BOTH places when one is finally filled in. A chart
        // pinned to a different digest from the API is two different terminals with one name.
        var template = Helpers();

        foreach (var (variant, digest) in CloudConsoles.ImageDigests) {
            template.ShouldContain($@"""{variant}"" ""{digest}""");
        }

        template.ShouldContain("cloudshell.image");
    }

    [Fact]
    public void TheEphemeralLimitIsTheSameInCSharpAndInTheChart() {
        // It is the only thing between a `git clone` in /tmp and a full node, and it is a constant in
        // two files.
        Values().ShouldContain("ephemeralStorageLimit: " + CloudConsoles.EphemeralStorageLimit);
    }

    [Fact]
    public void TheChartsResourceTypeIsThisTypeAndItsApiVersionIsThisApiVersion() {
        // `Build.Charts` reads both out of Chart.yaml and writes them into values.schema.json as
        // x-cybercloud-*; a mismatch would pair this registry type with a different chart's surface.
        var chart = Read("Chart.yaml");

        chart.ShouldContain("cybercloud.io/resource-type: " + CloudConsoles.Type);
        chart.ShouldContain("cybercloud.io/api-version: \"" + CloudConsoles.V2026 + "\"");
    }

    /// <summary>The chart's helper template, embedded at build time.</summary>
    /// <remarks>
    ///     ⚠ An <c>EmbeddedResource</c> rather than a path walked at run time: a test that reads
    ///     <c>../../../../charts</c> passes on a developer's machine and fails wherever the test
    ///     assembly is run from somewhere else.
    /// </remarks>
    static string Helpers() {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("cloudshell.helpers.tpl");
        stream.ShouldNotBeNull("the chart's _helpers.tpl is not embedded in this test assembly");

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    ///     The chart's values file, read from disk.
    /// </summary>
    /// <remarks>
    ///     ⚠ NOT embedded, unlike <see cref="Helpers" />, and the difference is deliberate:
    ///     <c>./build.sh Charts</c> REWRITES values.yaml in place from the registry, and a copy
    ///     embedded at compile time would be compared against a file the build had already changed.
    ///     The two rows read here are <c>@internal</c> and are therefore the ones generation carries
    ///     through as bytes.
    /// </remarks>
    static string Values() => Read("values.yaml");

    static string Read(string file) {
        // ⚠ THE ANCHOR IS THE SOLUTION FILE AND NOT A `charts` DIRECTORY, and the first draft used the
        // directory. `./build.sh Charts` runs `helm package` into `artifacts/charts/`, so after any
        // chart run there are TWO directories named `charts` above the test assembly and the nearer
        // one holds tarballs. The test passed on a clean tree and failed on the gate, which is the
        // worst order to discover it in. `CyberCloud.slnx` exists exactly once and only at the root.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CyberCloud.slnx"))) {
            directory = directory.Parent;
        }

        directory.ShouldNotBeNull("no CyberCloud.slnx above the test assembly");

        var path = Path.Combine(directory.FullName, "charts", "managed", "cloud-shell", file);
        File.Exists(path).ShouldBeTrue(path);

        return File.ReadAllText(path);
    }
}
