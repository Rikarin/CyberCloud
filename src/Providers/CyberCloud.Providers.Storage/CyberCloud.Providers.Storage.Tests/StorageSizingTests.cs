using System.Reflection;
using System.Text.RegularExpressions;

namespace CyberCloud.Providers.Storage.Tests;

/// <summary>
///     The two tables that live in <c>charts/managed/seaweedfs/templates/_helpers.tpl</c> and in C#,
///     and have to agree.
/// </summary>
/// <remarks>
///     ⚠ <b>THE HALF ADR-012's GENERATION DOES NOT REACH.</b> The chart's <c>@param</c> block is
///     generated from <c>StorageAccounts.Schema2026</c> and byte-diffed by <c>./build.sh Charts</c>; a
///     Helm <i>template</i> is not a schema and nothing generates it — <c>ChartSurfaces</c> filters
///     <c>templates/</c> out of the chart tree on purpose. So each table below is two hand-maintained
///     copies of one fact, and this file is what stops them drifting until
///     <c>CyberCloud.Kubernetes.Charts</c> exists and one copy of each can be deleted.
/// </remarks>
public sealed class StorageSizingTests {
    [Fact]
    public void TheSizingTableAgreesWithTheChartsValueForValue() {
        var helpers = Embedded("seaweedfs.helpers.tpl");

        foreach (var (preset, (cpu, memory)) in StorageAccounts.Presets) {
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
            StorageAccounts.Presets.ShouldContainKey(row.Groups[1].Value);
        }
    }

    [Fact]
    public void TheReplicationCodeTableAgreesWithTheChartsCodeForCode() {
        // ⚠ THE MORE DANGEROUS OF THE TWO, AND THE REASON IS THAT BOTH SIDES ARE VALID INPUT. A
        // preset the chart cannot find renders no resources block, which shows up as a pod with no
        // limits. A replication code that differs between the chart and the reconciler renders a
        // cluster that comes up perfectly and keeps a different number of copies than the tenant
        // asked for — visible only after a disk is lost.
        var helpers = Embedded("seaweedfs.helpers.tpl");

        foreach (var (member, code) in StorageAccounts.ReplicationCodes) {
            var row = Regex.Match(
                helpers,
                "\"" + Regex.Escape(member) + "\"\\s+\"([0-9]{3})\"",
                RegexOptions.None,
                TimeSpan.FromSeconds(5)
            );

            row.Success.ShouldBeTrue($"the chart's replication table has no row for '{member}'");
            row.Groups[1].Value.ShouldBe(code, member);
        }

        // The other direction, which is the half that catches an ADDED member.
        foreach (Match row in Regex.Matches(
                     helpers,
                     "^\\s+\"([A-Z][A-Za-z]+)\"\\s+\"[0-9]{3}\"",
                     RegexOptions.Multiline,
                     TimeSpan.FromSeconds(5)
                 )) {
            StorageAccounts.ReplicationCodes.ShouldContainKey(row.Groups[1].Value);
        }
    }

    [Fact]
    public void TheChartAndTheReconcilerAgreeOnWhatAControlPlanePodCosts() {
        // ⚠ THE THIRD SHARED FACT, AND THE ONE THE QUOTA METERS DEPEND ON. `seaweedfs.controlPlaneResources`
        // in the chart and StorageAccounts.ControlPlaneCpu/Memory in C# are two spellings of the same
        // pair, and StorageProvider's derivations reserve against the C# one. If the chart's figure
        // drifted upward, the cluster would run pods a tenant is not charged for.
        var helpers = Embedded("seaweedfs.helpers.tpl");
        var block = Regex.Match(
            helpers,
            "define \"seaweedfs\\.controlPlaneResources\" -}}(.*?){{- end",
            RegexOptions.Singleline,
            TimeSpan.FromSeconds(5)
        );

        block.Success.ShouldBeTrue("the chart declares no `seaweedfs.controlPlaneResources` helper");

        var declared = block.Groups[1].Value;

        declared.ShouldContain("cpu: \"" + StorageAccounts.ControlPlaneCpu + "\"");
        declared.ShouldContain("memory: \"" + StorageAccounts.ControlPlaneMemory + "\"");
    }

    static string Embedded(string name) {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(
                $"'{name}' is not an embedded resource of this assembly. It is declared in "
                + "CyberCloud.Providers.Storage.Tests.csproj with a LogicalName."
            );

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
