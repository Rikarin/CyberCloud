using CyberCloud.ResourceManager.Registry;
using CyberCloud.Tenancy.Contracts;
using System.Text.Json;

namespace CyberCloud.Providers.Monitor.Tests;

/// <summary>
///     What a workspace draws, and the one property whose value is a bill nobody would guess is one.
/// </summary>
public sealed class MonitorQuotaTests {
    static readonly Guid ClusterId = Guid.Parse("eeeeeeee-0000-4000-8000-00000000000c");

    [Fact]
    public void MovingOnlyTheRetentionTierMovesTheStorageAmount() {
        // ⚠⚠ THE TEST THAT MAKES docs/plan/16 § Cost and retention honesty TRUE RATHER THAN AGREED
        // WITH. That section says retention is prevented from becoming "storing everything forever"
        // by being "a paid property". A property is only paid if it moves the amount the platform
        // reserves. Two bodies below differ in NOTHING but the logs tier — the same GiB/day, the same
        // caps — and the amounts must differ by exactly the ratio of the two day counts.
        //
        // ⚠ THIS IS ALSO THE ONE A DERIVATION COPIED FROM ANY EARLIER PROVIDER FAILS. Every other
        // family's storage meter reads a size the tenant typed; a derivation here that summed the
        // three GiB/day figures would be plausible, would pass every other test in this file, and
        // would charge a 400-day workspace what a 15-day one costs.
        var shortWindow = Storage(Body(logsTier: "short"));
        var longWindow = Storage(Body(logsTier: "extended"));

        var difference = (MonitorWorkspaces.DaysOf("logs", "extended")
            - MonitorWorkspaces.DaysOf("logs", "short"))
            * MonitorWorkspaces.DefaultLogsGbPerDay;

        (longWindow - shortWindow).ShouldBe(
            difference,
            "changing only the logs retention tier did not move the storage amount by the extra days "
            + "times the daily allowance. Retention is priced; a meter that does not read it is a "
            + "tenant storing 400 days of logs for the price of 7."
        );
    }

    [Fact]
    public void MovingOnlyTheDailyAllowanceMovesTheStorageAmount() {
        // The other factor of the same product. A derivation that read only the tier would charge
        // every workspace at the same rate however much it takes in.
        var small = Storage(Body(logsGbPerDay: 10));
        var large = Storage(Body(logsGbPerDay: 100));

        (large - small).ShouldBe(
            90 * MonitorWorkspaces.DaysOf("logs", MonitorWorkspaces.DefaultTier),
            "changing only the logs daily allowance did not move the storage amount by the extra "
            + "gibibytes times the retention days."
        );
    }

    [Fact]
    public void TheAmountIsTheSumOverAllThreeSignalsAndNotOneOfThem() {
        // ⚠ A SUM OVER THREE POPULATIONS, WHICH IS WHERE A DERIVATION THAT READ THE FIRST SIGNAL AND
        // GENERALISED GOES WRONG AND STAYS PLAUSIBLE. Each signal has its own tier AND its own
        // allowance, and the three tiers have DIFFERENT day counts for the same tier name — 15/7/3
        // at `short`. A meter that multiplied one retention by the sum of the three allowances is
        // right about nothing and wrong by a factor that changes with the body.
        using var body = JsonDocument.Parse(Body());

        var expected =
            (long)MonitorWorkspaces.DaysOf("metrics", "short") * MonitorWorkspaces.DefaultMetricsGbPerDay
            + (long)MonitorWorkspaces.DaysOf("logs", "short") * MonitorWorkspaces.DefaultLogsGbPerDay
            + (long)MonitorWorkspaces.DaysOf("traces", "short") * MonitorWorkspaces.DefaultTracesGbPerDay;

        MonitorWorkspaces.StorageCeilingGb(body.RootElement).ShouldBe(expected);
    }

    [Fact]
    public void EveryLegalBodyDrawsAPositiveAmount() {
        // ⚠ QuotaGrain.TryReserveAsync REFUSES A NON-POSITIVE AMOUNT — "a reservation must be
        // positive; 0 is not" — which is the seam that makes a conditional meter undeclarable on
        // three other types. It is NOT a hazard here, and this is what says so rather than the
        // comment on MonitorProvider: every quota property has a Minimum of 1 and every tier has a
        // positive day count, so the smallest legal body still draws.
        foreach (var metrics in MonitorWorkspaces.Tiers) {
            foreach (var logs in MonitorWorkspaces.Tiers) {
                foreach (var traces in MonitorWorkspaces.Tiers) {
                    Storage(Body(metrics, logs, traces, logsGbPerDay: 1)).ShouldBeGreaterThan(
                        0,
                        $"({metrics}, {logs}, {traces}) at the smallest legal allowance draws nothing, "
                        + "so every create with that body would be refused inside the quota grain "
                        + "with an error naming neither the tier nor the provider."
                    );
                }
            }
        }
    }

    [Fact]
    public void NoVcpuOrMemoryMeterIsDeclaredBecauseAWorkspaceRunsNothing() {
        // ⚠ AN ABSENCE ASSERTED, because the plausible mistake is to copy an earlier provider's three
        // derivations and leave two of them deriving zero. A zero-deriving meter is refused by the
        // quota grain on EVERY create, and the error names quota rather than the provider — which is
        // the diagnostic charts/managed/opensearch/conformance.yaml already records as owed.
        var registration = Registration();

        foreach (var absent in new[] { QuotaMeter.Vcpu, QuotaMeter.MemoryGb, QuotaMeter.PublicIps,
                     QuotaMeter.Clusters }) {
            registration.Meters.ShouldNotContain(
                x => x.Meter == absent,
                $"{absent} is declared on a resource type that provisions no pods, no addresses and "
                + "no clusters. It would derive zero on every body, and QuotaGrain.TryReserveAsync "
                + "refuses a non-positive reservation."
            );
        }

        registration.Meters.ShouldContain(x => x.Meter == QuotaMeter.StorageGb);
        registration.Meters.ShouldContain(x => x.Meter == QuotaMeter.Resources);
    }

    [Fact]
    public void TheStorageDerivationSaysItReadsBothFactorsOfAllThreeSignals() {
        // ⚠ THE READ SET IS THE ONLY REVIEWABLE PART OF A DERIVATION, because the amount itself is a
        // delegate nothing sandboxes. Six pointers: three tiers and three allowances. A derivation
        // naming only the allowances would be the "retention is not priced" defect with an honest
        // amount, and a reviewer reading the registry would see it.
        var derivation = Registration().Meters.Single(x => x.Meter == QuotaMeter.StorageGb).Derivation;

        derivation.ShouldNotBeNull();
        derivation.Expression.ShouldNotBeNullOrWhiteSpace();

        foreach (var pointer in new[] {
                     "/properties/retention/metrics",
                     "/properties/retention/logs",
                     "/properties/retention/traces",
                     "/properties/quota/metricsGbPerDay",
                     "/properties/quota/logsGbPerDay",
                     "/properties/quota/tracesGbPerDay"
                 }) {
            derivation.Reads.ShouldContain(
                pointer,
                $"the storage derivation does not say it reads '{pointer}'. The read set is what a "
                + "reviewer checks a meter against, and half a product declared is a meter nobody can "
                + "audit."
            );

            MonitorWorkspaces.Schema2026.Declares(pointer).ShouldBeTrue(
                $"the storage derivation declares it reads '{pointer}', which this api-version's "
                + "schema does not declare."
            );
        }
    }

    [Fact]
    public void TheDerivationIsAPureFunctionOfTheBody() {
        // ⚠ The delete path re-derives committed amounts from the resource's STORED body through the
        // same step the create reserved with — ResourceManagerService.CommittedBy. A derivation that
        // read a clock or a configuration would make a delete return a different number than the
        // create committed, and quota would drift upward on every create/delete cycle.
        using var body = JsonDocument.Parse(Body());

        var first = MonitorWorkspaces.StorageCeilingGb(body.RootElement);

        for (var attempt = 0; attempt < 5; attempt++) {
            MonitorWorkspaces.StorageCeilingGb(body.RootElement).ShouldBe(first);
        }
    }

    static ResourceTypeRegistration Registration() {
        var registry = ProviderRegistry.Build([new MonitorProvider()]);
        registry.TryGetType(MonitorWorkspaces.Type, out var registration).ShouldBeTrue();
        return registration;
    }

    static long Storage(string body) {
        using var document = JsonDocument.Parse(body);
        return MonitorWorkspaces.StorageCeilingGb(document.RootElement);
    }

    static string Body(
        string metricsTier = MonitorWorkspaces.DefaultTier,
        string logsTier = MonitorWorkspaces.DefaultTier,
        string tracesTier = MonitorWorkspaces.DefaultTier,
        int logsGbPerDay = MonitorWorkspaces.DefaultLogsGbPerDay
    ) =>
        MonitorWorkspaces.Body(ClusterId, metricsTier, logsTier, tracesTier, logsGbPerDay);
}
