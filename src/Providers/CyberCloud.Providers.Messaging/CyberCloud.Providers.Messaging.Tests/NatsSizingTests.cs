using System.Reflection;
using System.Text.RegularExpressions;

namespace CyberCloud.Providers.Messaging.Tests;

/// <summary>
///     The two tables that live in <c>charts/managed/nats/templates/_helpers.tpl</c> and in C#, and
///     have to agree.
/// </summary>
/// <remarks>
///     ⚠ <b>THE HALF ADR-012's GENERATION DOES NOT REACH.</b> The chart's <c>@param</c> block is
///     generated from <c>NatsClusters.Schema2026</c> and byte-diffed by <c>./build.sh Charts</c>; a
///     Helm <i>template</i> is not a schema and nothing generates it. So each table below is two
///     hand-maintained copies of one fact, and this file is what stops them drifting until
///     <c>CyberCloud.Kubernetes.Charts</c> exists and one copy of each can be deleted.
/// </remarks>
public sealed class NatsSizingTests {
    [Fact]
    public void TheSizingTableAgreesWithTheChartsValueForValue() {
        var helpers = Embedded("nats.helpers.tpl");

        foreach (var (preset, (cpu, memory)) in NatsClusters.Presets) {
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
                     "\"(c1\\.[a-z0-9]+)\"\\s+\\(dict\\s+\"cpu\"",
                     RegexOptions.None,
                     TimeSpan.FromSeconds(5)
                 )) {
            NatsClusters.Presets.ShouldContainKey(row.Groups[1].Value);
        }
    }

    [Fact]
    public void ThePodSelectorLabelsAgreeWithTheChartsKeyForKey() {
        // ⚠ THE FACT IN THIS FILE THAT IS NOT ABOUT SIZING, AND THE MORE DANGEROUS OF THE TWO. A
        // StatefulSet's `spec.selector` is IMMUTABLE after create and both Services and the
        // PodMonitor select on the same set. If the chart and the reconciler disagreed, the chart
        // would render a workload whose Services select nothing — a cluster with no reachable
        // address, and no error anywhere, because selecting zero pods is a legal Service.
        var helpers = Embedded("nats.helpers.tpl");
        var block = Regex.Match(
            helpers,
            "define \"nats\\.selectorLabels\" -}}(.*?){{- end",
            RegexOptions.Singleline,
            TimeSpan.FromSeconds(5)
        );

        block.Success.ShouldBeTrue("the chart declares no `nats.selectorLabels` helper");

        var declared = block.Groups[1].Value;

        foreach (var (key, value) in NatsClusters.PodLabels("example")) {
            declared.ShouldContain(key + ":", Case.Sensitive, key);

            // The instance label is the resource's name and is templated; the other three are
            // literals and must match character for character.
            if (key != "app.kubernetes.io/instance") {
                declared.ShouldContain(key + ": \"" + value + "\"", Case.Sensitive, key);
            }
        }

        // ⚠ And the other direction, which is the half that catches an ADDED key. A label the chart
        // selects on and the reconciler does not write is a Service that selects nothing.
        foreach (Match line in Regex.Matches(
                     declared,
                     "^\\s*(app\\.kubernetes\\.io/[a-z-]+):",
                     RegexOptions.Multiline,
                     TimeSpan.FromSeconds(5)
                 )) {
            NatsClusters.PodLabels("example")
                .ShouldContain(x => x.Key == line.Groups[1].Value, line.Groups[1].Value);
        }
    }

    static string Embedded(string name) {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(
                $"'{name}' is not an embedded resource of this assembly. It is declared in "
                + "CyberCloud.Providers.Messaging.Tests.csproj with a LogicalName."
            );

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
