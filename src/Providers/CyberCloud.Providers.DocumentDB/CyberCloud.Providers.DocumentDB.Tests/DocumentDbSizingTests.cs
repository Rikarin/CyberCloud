using System.Reflection;
using System.Text.RegularExpressions;

namespace CyberCloud.Providers.DocumentDB.Tests;

/// <summary>
///     The three tables that live in <c>charts/managed/ferretdb/templates/_helpers.tpl</c> and in C#,
///     and have to agree.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THE HALF ADR-012's GENERATION DOES NOT REACH.</b> The chart's <c>@param</c> block is
///         generated from <c>DocumentDbAccounts.Schema2026</c> and byte-diffed by
///         <c>./build.sh Charts</c>; a Helm <i>template</i> is not a schema and nothing generates it —
///         <c>ChartSurfaces</c> filters <c>templates/</c> out of the chart tree on purpose. So each
///         table below is two hand-maintained copies of one fact, and this file is what stops them
///         drifting until <c>CyberCloud.Kubernetes.Charts</c> exists and one copy of each can be
///         deleted.
///     </para>
///     <para>
///         ⚠ <b>THREE, WHERE EVERY EARLIER PROVIDER HAD ONE OR TWO.</b> The third is the version
///         pairing, which no other chart has an equivalent of: one API property that has to resolve to
///         two image tags on both sides. It is also the most dangerous of the three, for the reason
///         its own test gives.
///     </para>
/// </remarks>
public sealed class DocumentDbSizingTests {
    [Fact]
    public void TheSizingTableAgreesWithTheChartsValueForValue() {
        var helpers = Embedded("ferretdb.helpers.tpl");

        foreach (var (preset, (cpu, memory)) in DocumentDbAccounts.Presets) {
            var row = Regex.Match(
                helpers,
                // ⚠ `\s+` between every token, not a single space. The chart's table is column-aligned
                // for a reader, so `"cpu" "1"    "memory"` has four spaces where `"cpu" "250m"` has
                // one — a regex written against one row matches half the table and reports the other
                // half as missing.
                "\"" + Regex.Escape(preset) + "\"\\s+\\(dict\\s+\"cpu\"\\s+\"([^\"]+)\"\\s+\"memory\"\\s+\"([^\"]+)\"\\)",
                RegexOptions.None,
                TimeSpan.FromSeconds(5)
            );

            row.Success.ShouldBeTrue($"the chart's preset table has no row for '{preset}'");
            row.Groups[1].Value.ShouldBe(cpu, preset);
            row.Groups[2].Value.ShouldBe(memory, preset);
        }

        // ⚠ And the other direction: a preset the chart has and the schema does not is a value a
        // tenant can write into values.yaml and never into a resource body.
        foreach (Match row in Regex.Matches(
                     helpers,
                     "\"(s1\\.[a-z0-9]+)\"\\s+\\(dict\\s+\"cpu\"",
                     RegexOptions.None,
                     TimeSpan.FromSeconds(5)
                 )) {
            DocumentDbAccounts.Presets.ShouldContainKey(row.Groups[1].Value);
        }
    }

    [Fact]
    public void TheVersionPairingAgreesWithTheChartTagForTag() {
        // ⚠ THE MOST DANGEROUS OF THE THREE, AND THE REASON IS THAT BOTH SIDES ARE VALID INPUT. A
        // preset the chart cannot find renders no resources block, which shows up as a pod with no
        // limits. A version pairing that differs between the chart and the reconciler renders a
        // PostgreSQL and a FerretDB that BOTH start, pass every probe, and disagree about the
        // extension's call signatures — visible only as query failures in a tenant's application.
        var helpers = Embedded("ferretdb.helpers.tpl");

        foreach (var (version, (gateway, postgres)) in DocumentDbAccounts.Versions) {
            var row = Regex.Match(
                helpers,
                "\"" + Regex.Escape(version)
                + "\"\\s+\\(dict\\s+\"gateway\"\\s+\"([^\"]+)\"\\s+\"postgres\"\\s+\"([^\"]+)\"\\)",
                RegexOptions.None,
                TimeSpan.FromSeconds(5)
            );

            row.Success.ShouldBeTrue($"the chart's version table has no row for '{version}'");
            row.Groups[1].Value.ShouldBe(gateway, version);
            row.Groups[2].Value.ShouldBe(postgres, version);
        }

        // The other direction, which is the half that catches an ADDED row — a version a tenant could
        // set in values.yaml and never in a resource body.
        foreach (Match row in Regex.Matches(
                     helpers,
                     "^\\s+\"([0-9]+\\.[0-9]+)\"\\s+\\(dict\\s+\"gateway\"",
                     RegexOptions.Multiline,
                     TimeSpan.FromSeconds(5)
                 )) {
            DocumentDbAccounts.Versions.ShouldContainKey(row.Groups[1].Value);
        }
    }

    [Fact]
    public void TheChartAndTheReconcilerAgreeOnWhatAGatewayPodCosts() {
        // ⚠ THE THIRD SHARED FACT, AND THE ONE THE QUOTA METERS DEPEND ON. `ferretdb.gatewayResources`
        // in the chart and DocumentDbAccounts.GatewayCpu/GatewayMemory in C# are two spellings of the
        // same pair, and DocumentDbProvider's derivations reserve against the C# one. If the chart's
        // figure drifted upward, the cluster would run pods a tenant is not charged for.
        var helpers = Embedded("ferretdb.helpers.tpl");
        var block = Regex.Match(
            helpers,
            "define \"ferretdb\\.gatewayResources\" -}}(.*?){{- end",
            RegexOptions.Singleline,
            TimeSpan.FromSeconds(5)
        );

        block.Success.ShouldBeTrue("the chart declares no `ferretdb.gatewayResources` helper");

        var declared = block.Groups[1].Value;

        declared.ShouldContain("cpu: \"" + DocumentDbAccounts.GatewayCpu + "\"");
        declared.ShouldContain("memory: \"" + DocumentDbAccounts.GatewayMemory + "\"");
    }

    [Fact]
    public void TheChartRendersTheSameThreePreloadLibrariesInTheSamePlace() {
        // ⚠ THE FOURTH SHARED FACT AND THE ONE THAT IS NOT A TABLE. The library list is in
        // DocumentDbAccounts.SharedPreloadLibraries and in templates/cluster.yaml, and the thing that
        // has to agree is not only the three names but WHERE they sit: a chart that put them under
        // `parameters:` would be rejected by CloudNativePG's validating webhook while the reconciler's
        // own object was accepted, so a `helm template` diff would look fine and the two paths would
        // behave differently.
        var cluster = Embedded("ferretdb.cluster.yaml");

        var block = Regex.Match(
            cluster,
            "\\n    shared_preload_libraries:\\n((?:      - \\S+\\n)+)",
            RegexOptions.None,
            TimeSpan.FromSeconds(5)
        );

        block.Success.ShouldBeTrue(
            "the chart declares no `shared_preload_libraries:` list two levels under spec — which is "
            + "where CloudNativePG's PostgresConfiguration.AdditionalLibraries sits, beside "
            + "`parameters` rather than inside it."
        );

        var declared = block.Groups[1].Value
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim().TrimStart('-').Trim())
            .ToList();

        declared.ShouldBe([.. DocumentDbAccounts.SharedPreloadLibraries]);

        // ⚠ And never under `parameters:`. This is the defect the row above HAD, until 2026-08-12 —
        // see charts/managed/postgres/conformance.yaml § owed,
        // `shared-preload-libraries-is-not-a-parameter`, which this assertion's twin over there now
        // guards.
        var parameters = Regex.Match(
            cluster,
            "\\n    parameters:\\n((?:      \\S.*\\n)+)",
            RegexOptions.None,
            TimeSpan.FromSeconds(5)
        );

        parameters.Success.ShouldBeTrue("the chart declares no `parameters:` block");
        parameters.Groups[1].Value.ShouldNotContain("shared_preload_libraries");
    }

    static string Embedded(string name) {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(
                $"'{name}' is not an embedded resource of this assembly. It is declared in "
                + "CyberCloud.Providers.DocumentDB.Tests.csproj with a LogicalName."
            );

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
