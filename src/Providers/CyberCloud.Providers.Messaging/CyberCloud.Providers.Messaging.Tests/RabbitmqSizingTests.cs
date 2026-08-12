using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CyberCloud.Providers.Messaging.Tests;

/// <summary>
///     The two blocks that live in <c>charts/managed/rabbitmq/templates/_helpers.tpl</c> and in C#,
///     and have to agree.
/// </summary>
/// <remarks>
///     ⚠ <b>THE HALF ADR-012's GENERATION DOES NOT REACH.</b> The chart's <c>@param</c> block is
///     generated from <c>RabbitmqClusters.Schema2026</c> and byte-diffed by <c>./build.sh Charts</c>;
///     a Helm <i>template</i> is not a schema and nothing generates it. So each block below is two
///     hand-maintained copies of one fact, and this file is what stops them drifting until
///     <c>CyberCloud.Kubernetes.Charts</c> exists and one copy of each can be deleted.
///     <para>
///         ⚠ <b>The second block is not a table, and it is the one that matters more.</b>
///         <c>rabbitmq.additionalConfig</c> is the <c>rabbitmq.conf</c> fragment
///         <c>default_queue_type</c> reaches the broker through — the setting docs/plan/12's whole
///         RabbitMQ row is about, and one the operator gives no CRD field for. If the chart and the
///         reconciler disagreed about the key, one of the two would render a cluster whose queues are
///         not replicated, and nothing anywhere would report an error.
///     </para>
/// </remarks>
public sealed class RabbitmqSizingTests {
    static readonly Guid ClusterId = Guid.Parse("eeeeeeee-0000-4000-8000-000000000009");

    [Fact]
    public void TheSizingTableAgreesWithTheChartsValueForValue() {
        var helpers = Embedded("rabbitmq.helpers.tpl");

        foreach (var (preset, (cpu, memory)) in RabbitmqClusters.Presets) {
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
            RabbitmqClusters.Presets.ShouldContainKey(row.Groups[1].Value);
        }
    }

    [Fact]
    public void TheConfigFragmentSetsTheSameKeysInTheSameOrderAsTheReconciler() {
        // ⚠ THE MORE DANGEROUS OF THE TWO BLOCKS, AND THE ONE NOTHING ELSE CHECKS. `default_queue_type`
        // has no member on the CRD, so both the chart and the reconciler write it as free text into
        // spec.rabbitmq.additionalConfig — and neither the API server nor the operator validates the
        // KEY. A chart that spelled it `default_queue-type`, or that reached for `queue_master_locator`
        // (the 3.x setting the operator still writes in its own block), would render a cluster whose
        // queues are unreplicated, start cleanly, and report nothing.
        var block = Regex.Match(
            Embedded("rabbitmq.helpers.tpl"),
            "define \"rabbitmq\\.additionalConfig\" -}}(.*?){{ end",
            RegexOptions.Singleline,
            TimeSpan.FromSeconds(5)
        );

        block.Success.ShouldBeTrue("the chart declares no `rabbitmq.additionalConfig` helper");

        var declared = Keys(block.Groups[1].Value);

        using var body = JsonDocument.Parse(RabbitmqClusters.Body(ClusterId));
        var rendered = Keys(RabbitmqClusters.AdditionalConfig(body.RootElement));

        // ⚠ SEQUENCE equality, not set equality. Two hand-written spellings of one file must agree on
        // the keys AND on the order, because the order is what makes the reconciler's output stable
        // across passes and what makes two clusters diffable by eye.
        declared.ShouldBe(
            rendered,
            "the chart's rabbitmq.conf fragment sets " + string.Join(", ", declared)
            + " and the reconciler sets " + string.Join(", ", rendered) + "."
        );

        declared.ShouldContain("default_queue_type");
    }

    /// <summary>The assignment keys of an INI fragment, in order.</summary>
    /// <param name="fragment">The fragment, possibly carrying Helm actions.</param>
    static string[] Keys(string fragment) => [
        .. Regex.Matches(fragment, "^\\s*([a-z_.]+)\\s*=", RegexOptions.Multiline, TimeSpan.FromSeconds(5))
            .Select(x => x.Groups[1].Value)
    ];

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
