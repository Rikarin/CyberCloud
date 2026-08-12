using System.Text.RegularExpressions;

namespace CyberCloud.Providers.Search.Tests;

/// <summary>
///     The two copies of the sizing table — the C# one the reconciler renders from and the Helm one a
///     support engineer reads — compared row for row.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>There are two copies because <c>CyberCloud.Kubernetes.Charts</c> does not exist</b>
///         (docs/plan/03 § src), so the object is built in C# and the chart is the human-readable
///         statement of the same thing. The moment a renderer lands, the C# table and the reconciler's
///         use of it should go and the chart's should stay, because the chart is the file a support
///         engineer opens.
///     </para>
///     <para>
///         ⚠ <b><c>./build.sh Charts</c> cannot catch this drift and it is worth saying why.</b>
///         ADR-012's fifth surface generates the <c>@param</c> block of <c>values.yaml</c> from the
///         registry, and <c>ChartSurfaces</c> filters <c>templates/</c> out of the chart tree on
///         purpose — a Helm template is not a schema. So nothing in the gate set has ever read
///         <c>_helpers.tpl</c>. This test reads it as <b>text</b>, out of an embedded resource, which
///         is the only mechanism there is.
///     </para>
/// </remarks>
public sealed partial class OpenSearchSizingTests {
    [Fact]
    public void EveryPresetInTheChartIsTheSamePairAsInTheRegistry() {
        var chart = ChartPresets();

        chart.Count.ShouldBe(
            OpenSearchServices.Presets.Count,
            "the chart's preset table and the registry's have different numbers of rows. A preset in "
            + "one and not the other is a body the API accepts and the chart cannot render, or a "
            + "rung a support engineer can read and nobody can ask for."
        );

        foreach (var (preset, (cpu, memory)) in OpenSearchServices.Presets) {
            chart.ShouldContainKey(preset);
            chart[preset].ShouldBe(
                (cpu, memory),
                $"'{preset}' is {cpu}/{memory} in C# and {chart[preset].Cpu}/{chart[preset].Memory} "
                + "in charts/managed/opensearch/templates/_helpers.tpl. One of them is what the "
                + "cluster gets and the other is what the documentation says it gets."
            );
        }
    }

    [Fact]
    public void TheControlPlaneShareIsTheSameInBothCopies() {
        // ⚠ THE ROW THE QUOTA METERS DEPEND ON, AND THE ONE MOST LIKELY TO BE COPIED WRONG FROM
        // charts/managed/seaweedfs, WHICH USES 250m/512Mi FOR THE SAME JOB. A SeaweedFS master is a Go
        // binary; a cluster-manager node is a JVM, and 512 MiB is below what OpenSearch's own startup
        // heap check passes — so the copied value produces a pool that CrashLoopBackOffs before it
        // joins, which reads as a cluster that will not form rather than as a sizing mistake.
        // ⚠ THE `define` BODY AND NOT THE WHOLE FILE. This template's comments NAME the figures it
        // deliberately does not use — 512Mi, m1.nano — because the reason it does not use them is the
        // interesting part. A scan over the raw text would therefore fail on the prose that exists to
        // stop the mistake, which is the wrong way round and is exactly what the first run of this
        // test did.
        var block = Define("opensearch.controlPlaneResources");

        block.ShouldContain(
            "cpu: \"" + OpenSearchServices.ControlPlaneCpu + "\"",
            Case.Sensitive,
            "the chart's control-plane CPU is not " + OpenSearchServices.ControlPlaneCpu
        );

        block.ShouldContain(
            "memory: \"" + OpenSearchServices.ControlPlaneMemory + "\"",
            Case.Sensitive,
            "the chart's control-plane memory is not " + OpenSearchServices.ControlPlaneMemory
        );

        // ⚠ And the fixed volume every non-data node gets, which the storage meter adds per node.
        Define("opensearch.controlPlaneVolume").Trim().ShouldContain(
            OpenSearchServices.ControlPlaneVolumeSize,
            Case.Sensitive,
            "the chart's control-plane volume size is not " + OpenSearchServices.ControlPlaneVolumeSize
        );

        // ⚠ THE NEGATIVE HALF, WHICH IS THE ONE THAT CATCHES THE COPY. A chart that had taken
        // charts/managed/seaweedfs' figures for the same job would satisfy nothing above and would
        // fail here by name.
        block.ShouldNotContain(
            "512Mi",
            Case.Sensitive,
            "the chart carries charts/managed/seaweedfs' control-plane memory. That is a Go binary's "
            + "share and this is a JVM: 512Mi is below OpenSearch's own startup heap check, so the "
            + "pool would CrashLoopBackOff before joining and the symptom would be a cluster that "
            + "never forms."
        );
    }

    [Fact]
    public void TheChartDoesNotOfferARungTheRegistryRefuses() {
        // ⚠ m1.nano and m1.micro exist in docs/plan/12's vocabulary and in charts/managed/valkey's
        // table, and they are deliberately absent from both copies here. Somebody completing the table
        // "for consistency" is exactly how a 1 GiB search node ships.
        //
        // ⚠ Asserted against the PARSED table rather than the file's text, for the reason
        // TheControlPlaneShareIsTheSameInBothCopies gives: the template's own comment names both rungs
        // in order to say why they are missing.
        var chart = ChartPresets();

        chart.Keys.ShouldNotContain("m1.nano");
        chart.Keys.ShouldNotContain("m1.micro");
    }

    /// <summary>The preset table as the chart spells it.</summary>
    /// <remarks>
    ///     ⚠ Parsed out of the <c>dict</c> literal rather than rendered with <c>helm template</c>: the
    ///     point is to compare the two <i>tables</i>, and rendering would only ever show the one row
    ///     the values file happens to select.
    /// </remarks>
    static Dictionary<string, (string Cpu, string Memory)> ChartPresets() {
        var found = new Dictionary<string, (string Cpu, string Memory)>(StringComparer.Ordinal);

        foreach (Match match in PresetRow().Matches(Helpers())) {
            found[match.Groups["preset"].Value] =
                (match.Groups["cpu"].Value, match.Groups["memory"].Value);
        }

        found.ShouldNotBeEmpty(
            "no preset rows were found in charts/managed/opensearch/templates/_helpers.tpl. Either "
            + "the table moved or this regex stopped matching it, and a comparison that finds nothing "
            + "passes."
        );

        return found;
    }

    /// <summary>The body of one named <c>define</c> in the chart's helper template.</summary>
    /// <param name="name">The template name, as <c>define</c> spells it.</param>
    /// <remarks>
    ///     ⚠ Comment blocks in this file deliberately name the values the chart does <i>not</i> use, so
    ///     a negative assertion has to be scoped to the code rather than run over the text. This takes
    ///     everything between the named <c>define</c> and the <c>end</c> that closes it, and refuses
    ///     rather than returning empty when the name is not there — a scope that found nothing would
    ///     make every assertion inside it vacuous.
    /// </remarks>
    static string Define(string name) {
        var helpers = Helpers();
        var opening = helpers.IndexOf("define \"" + name + "\"", StringComparison.Ordinal);

        opening.ShouldBeGreaterThanOrEqualTo(
            0,
            $"charts/managed/opensearch/templates/_helpers.tpl declares no `define \"{name}\"`. It was "
            + "renamed or removed, and every assertion scoped to it would otherwise pass over an empty "
            + "string."
        );

        var closing = helpers.IndexOf("{{- end -}}", opening, StringComparison.Ordinal);
        closing.ShouldBeGreaterThan(opening, $"`define \"{name}\"` is not closed.");

        return helpers[opening..closing];
    }

    /// <summary>The chart's helper template, as text.</summary>
    static string Helpers() {
        using var stream = typeof(OpenSearchSizingTests).Assembly
            .GetManifestResourceStream("opensearch.helpers.tpl")
            ?? throw new InvalidOperationException(
                "charts/managed/opensearch/templates/_helpers.tpl is not embedded in this assembly. "
                + "See the EmbeddedResource item in the .csproj."
            );

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [GeneratedRegex(
        """"^\s*"(?<preset>m1\.[a-z0-9]+)"\s+\(dict "cpu" "(?<cpu>[^"]+)"\s+"memory" "(?<memory>[^"]+)"\)"""",
        RegexOptions.Multiline | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex PresetRow();
}
