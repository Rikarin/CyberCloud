using System.Reflection;
using System.Text.RegularExpressions;

namespace CyberCloud.Providers.Analytics.Tests;

/// <summary>
///     The facts that live in <c>charts/managed/clickhouse/templates/_helpers.tpl</c> and in C#, and
///     have to agree.
/// </summary>
/// <remarks>
///     ⚠ <b>THE HALF ADR-012's GENERATION DOES NOT REACH.</b> The chart's <c>@param</c> block is
///     generated from <c>ClickHouseClusters.Schema2026</c> and byte-diffed by <c>./build.sh Charts</c>;
///     a Helm <i>template</i> is not a schema and nothing generates it — <c>ChartSurfaces</c> filters
///     <c>templates/</c> out of the chart tree on purpose. So each fact below is two hand-maintained
///     copies of one thing, and this file is what stops them drifting until
///     <c>CyberCloud.Kubernetes.Charts</c> exists and one copy of each can be deleted.
/// </remarks>
public sealed class ClickHouseSizingTests {
    [Fact]
    public void TheSizingTableAgreesWithTheChartsValueForValue() {
        var helpers = Embedded("clickhouse.helpers.tpl");

        foreach (var (preset, (cpu, memory)) in ClickHouseClusters.Presets) {
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
                     "\"(m1\\.[a-z0-9]+)\"\\s+\\(dict\\s+\"cpu\"",
                     RegexOptions.None,
                     TimeSpan.FromSeconds(5)
                 )) {
            ClickHouseClusters.Presets.ShouldContainKey(row.Groups[1].Value);
        }
    }

    [Fact]
    public void TheChartAndTheReconcilerAgreeOnWhatAKeeperPodCosts() {
        // ⚠ THE FACT THE QUOTA METERS DEPEND ON. `clickhouse.keeperResources` in the chart and
        // ClickHouseClusters.KeeperCpu/KeeperMemory in C# are two spellings of one pair, and
        // AnalyticsProvider's derivations reserve against the C# one. If the chart's figure drifted
        // upward, the cluster would run pods a tenant is not charged for — and the Keeper population
        // is the whole of this type's "sum" term.
        var helpers = Embedded("clickhouse.helpers.tpl");
        var block = Regex.Match(
            helpers,
            "define \"clickhouse\\.keeperResources\" -}}(.*?){{- end",
            RegexOptions.Singleline,
            TimeSpan.FromSeconds(5)
        );

        block.Success.ShouldBeTrue("the chart declares no `clickhouse.keeperResources` helper");

        var declared = block.Groups[1].Value;

        declared.ShouldContain("cpu: \"" + ClickHouseClusters.KeeperCpu + "\"");
        declared.ShouldContain("memory: \"" + ClickHouseClusters.KeeperMemory + "\"");
    }

    [Fact]
    public void TheChartAndTheReconcilerDeriveTheSameKeeperServiceName() {
        // ⚠ THE STRING WITH NO NATURAL DEFENCES, IN BOTH FILES. Nothing either of them applies creates
        // `keeper-{name}` — the operator does — so a prefix that drifted between the chart and the
        // reconciler would produce two renderings of one resource that disagree about where its
        // coordination lives, and both would apply, read back and converge.
        //
        // ⚠ ASSERTED AGAINST A LITERAL on both sides. Reading the prefix out of KeeperServiceName and
        // then looking for it in the chart would find whatever it was changed to.
        var helpers = Embedded("clickhouse.helpers.tpl");

        helpers.ShouldContain(
            "printf \"keeper-%s\"",
            Case.Sensitive,
            "the chart derives the Keeper Service name some other way than the operator's `keeper-` "
            + "prefix."
        );

        ClickHouseClusters.KeeperServiceName("events").ShouldBe("keeper-events");
    }

    [Fact]
    public void TheChartAndTheReconcilerAgreeOnBothImageRepositories() {
        // ⚠ ONE VERSION, TWO IMAGES, TWO FILES. A chart that tagged the Keeper from a different value
        // than the server would produce a cluster whose coordination and servers are on different
        // release trains — a combination nobody tests and which fails as a protocol error rather than
        // as a startup one.
        var helpers = Embedded("clickhouse.helpers.tpl");

        helpers.ShouldContain(
            "printf \"" + ClickHouseClusters.ServerImageRepository + ":%s\" .Values.version",
            Case.Sensitive
        );

        helpers.ShouldContain(
            "printf \"" + ClickHouseClusters.KeeperImageRepository + ":%s\" .Values.version",
            Case.Sensitive
        );
    }

    static string Embedded(string name) {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(
                $"'{name}' is not an embedded resource of this assembly. It is declared in "
                + "CyberCloud.Providers.Analytics.Tests.csproj with a LogicalName."
            );

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
