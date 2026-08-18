using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace CyberCloud.Providers.Monitor.Tests;

/// <summary>
///     The nine retention numbers, in every place they are written.
/// </summary>
/// <remarks>
///     ⚠ <b>Retention is the only property on this type whose value is BOTH a bill and a data-loss
///     boundary</b>, which is why it gets a file of its own rather than a section of the declaration
///     tests. A wrong number here overcharges a tenant and deletes their logs, and neither symptom
///     points at a table.
/// </remarks>
public sealed class MonitorRetentionTests {
    /// <summary>
    ///     docs/plan/16 § <c>CyberCloud.Monitor/workspaces</c>, verbatim, typed out.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>LITERALS, AND THEY HAVE TO BE.</b> Reading these off
    ///     <see cref="MonitorWorkspaces.RetentionDays" /> would compare the table to itself. The
    ///     source sentence is <i>"Per signal: metrics 15/90/400 days, logs 7/30/90, traces
    ///     3/14/30"</i>, and the three tiers are in ascending order in that sentence, which is the
    ///     order <see cref="MonitorWorkspaces.Tiers" /> declares.
    /// </remarks>
    static readonly (string Signal, string Tier, int Days)[] DocumentedRetention = [
        ("metrics", "short", 15),
        ("metrics", "standard", 90),
        ("metrics", "extended", 400),
        ("logs", "short", 7),
        ("logs", "standard", 30),
        ("logs", "extended", 90),
        ("traces", "short", 3),
        ("traces", "standard", 14),
        ("traces", "extended", 30)
    ];

    [Fact]
    public void TheTableIsTheOneDocPlan16Prices() {
        foreach (var (signal, tier, days) in DocumentedRetention) {
            MonitorWorkspaces.DaysOf(signal, tier).ShouldBe(
                days,
                $"docs/plan/16 prices {signal} at the {tier} tier as {days} days. This number is the "
                + "second factor of the storage meter and the boundary at which a tenant's data is "
                + "deleted, so it is wrong in both directions at once."
            );
        }
    }

    [Fact]
    public void EveryTierOfEverySignalHasADayCount() {
        // ⚠ THE HOLE THAT MAKES THE STORAGE METER UNDECLARABLE, PINNED. A tier added to Tiers with no
        // row in RetentionDays derives ZERO days for that signal — and a meter that derives zero is
        // refused by QuotaGrain.TryReserveAsync ("a reservation must be positive; 0 is not"), so
        // every create of a workspace choosing that tier would fail with a quota error naming
        // neither the tier nor this table. MonitorProvider's derivation refuses by name instead; this
        // is what stops it ever having to.
        foreach (var signal in MonitorWorkspaces.Signals) {
            foreach (var tier in MonitorWorkspaces.Tiers) {
                MonitorWorkspaces.DaysOf(signal, tier).ShouldBeGreaterThan(
                    0,
                    $"'{signal}' has no day count at the '{tier}' tier. Add the row to "
                    + "MonitorWorkspaces.RetentionDays — a tier the schema accepts and the table does "
                    + "not carry is a create that fails inside the quota grain."
                );
            }
        }
    }

    [Fact]
    public void TheTiersAreTheOnesTheSchemaAccepts() {
        // Three places have to agree on the three names: the table's keys, the enum the API enforces,
        // and the chart's own copy. This is the first two.
        foreach (var pointer in new[] {
                     "/properties/retention/metrics",
                     "/properties/retention/logs",
                     "/properties/retention/traces"
                 }) {
            var property = MonitorWorkspaces.Schema2026.Properties.Single(x => x.JsonPointer == pointer);

            property.AllowedValues.ShouldBe(
                MonitorWorkspaces.Tiers,
                $"'{pointer}' accepts a set of tiers that is not MonitorWorkspaces.Tiers, so the API "
                + "accepts a tier the retention table cannot price."
            );
        }
    }

    [Fact]
    public void EveryTierIsStrictlyLongerThanTheOneBelowIt() {
        // ⚠ NOT DECORATION. MonitorWorkspaceReconciler refuses a retention SHRINK, and its check
        // compares DAYS rather than tier positions precisely so that it survives a tier being
        // inserted out of order. This test is the other half of that decision: it says the ordering
        // the tenant sees — short, standard, extended — is real, so a portal that renders them as a
        // ladder is not lying.
        foreach (var signal in MonitorWorkspaces.Signals) {
            var days = MonitorWorkspaces.Tiers.Select(tier => MonitorWorkspaces.DaysOf(signal, tier))
                .ToArray();

            for (var index = 1; index < days.Length; index++) {
                days[index].ShouldBeGreaterThan(
                    days[index - 1],
                    $"'{signal}' at the '{MonitorWorkspaces.Tiers[index]}' tier is not longer than at "
                    + $"'{MonitorWorkspaces.Tiers[index - 1]}'. The tiers are presented as a ladder and "
                    + "priced as one."
                );
            }
        }
    }

    [Fact]
    public void TheChartCarriesTheSameNineNumbers() {
        // ⚠ A SECOND COPY OF THE TABLE, DIFFED BY READING THE TEMPLATE AS TEXT. The chart renders the
        // day counts into the ingest row and the C# renders them into the same object; the two are
        // independent because CyberCloud.Kubernetes.Charts does not exist. `./build.sh Charts`
        // compares the chart's VALUES against the registry and never opens templates/, so this is the
        // half generation does not reach — the same argument ClickHouseSizingTests makes about a
        // preset table.
        var helpers = Helpers();

        foreach (var (signal, tier, days) in DocumentedRetention) {
            helpers.ShouldContain(
                $"\"{tier}\" {days.ToString(CultureInfo.InvariantCulture)}",
                Case.Sensitive,
                $"charts/managed/monitor-workspace/templates/_helpers.tpl does not carry {signal} at "
                + $"the {tier} tier as {days} days. The chart and MonitorWorkspaces.RetentionDays are "
                + "two renderings of one table and nothing but this test compares them."
            );
        }
    }

    [Fact]
    public void TheChartRoutesMetricsToAClusterNamedForTheTier() {
        // ⚠ THE OPEN-SOURCE VICTORIAMETRICS LIMIT, PINNED IN THE CHART. Per-tenant retention is an
        // ENTERPRISE feature (`-retentionFilter`); the open-source answer is one vmstorage group per
        // tier, so the tier has to reach the VMUser as a TARGET NAME. A chart that wrote a day count
        // into the VMUser instead would render an object the operator accepts and vmauth ignores,
        // and every workspace would land on whatever retention the default group has.
        Helpers().ShouldContain(
            "telemetry-%s",
            Case.Sensitive,
            "the chart no longer derives the metrics cluster name from the retention tier. Per-tenant "
            + "retention is not a thing open-source VictoriaMetrics has — see SOURCE — so the tier "
            + "IS the routing."
        );

        foreach (var tier in MonitorWorkspaces.Tiers) {
            MonitorWorkspaces.MetricsClusterName(tier).ShouldBe("telemetry-" + tier);
        }
    }

    [Fact]
    public void ShorteningARetentionIsExpressibleInABodyTheSchemaAccepts() {
        // ⚠ THE POINT OF THE RECONCILER'S REFUSAL, STATED AS A FACT ABOUT THE API RATHER THAN AS A
        // COMMENT. If the schema could refuse a shrink, the refusal would belong there — 400 before
        // the 202, with a JSON Pointer the portal can highlight. It cannot: ResourceSchema.Validate
        // takes ONE body and compares it against constants, so a body naming a shorter tier is
        // perfectly valid in isolation. This test asserts that, so that whoever closes the
        // provider-predicate seam finds it and can delete the reconciler's check.
        using var shorter = JsonDocument.Parse(
            MonitorWorkspaces.Body(Guid.NewGuid(), logsTier: "short")
        );

        MonitorWorkspaces.Schema2026.Validate(shorter.RootElement, allowTags: true).IsSuccess
            .ShouldBeTrue(
                "a body naming the shortest retention tier is refused by the schema, which would mean "
                + "the shrink check could move to the API. Delete MonitorWorkspaceReconciler's "
                + "ShrinkAsync and say so at conformance.yaml § owed, "
                + "`retention-shrink-is-refused-after-202`."
            );
    }

    /// <summary>The chart's helpers, read as text.</summary>
    static string Helpers() {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("monitor-workspace.helpers.tpl")
            ?? throw new InvalidOperationException(
                "charts/managed/monitor-workspace/templates/_helpers.tpl is not embedded. See the "
                + "EmbeddedResource item in this project's .csproj."
            );

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
