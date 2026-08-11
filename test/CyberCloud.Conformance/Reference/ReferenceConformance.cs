using CyberCloud.Conformance.Harness;
using CyberCloud.Core.Time;
using CyberCloud.ResourceManager;
using CyberCloud.ResourceManager.Conformance;
using CyberCloud.ResourceManager.Reconcile;
using System.Collections.Immutable;
using System.Text.Json;

namespace CyberCloud.Conformance.Reference;

/// <summary>
///     The reference provider's registration — and the worked example of what a real one writes.
/// </summary>
/// <remarks>
///     ⚠ <b>This is the whole registration surface.</b> A provider's own <c>.Conformance</c> project
///     writes one of these plus the two class declarations below, and inherits every assertion. If a
///     provider ever needs more than this to pass, that is a fact about the suite (or about the
///     platform) and belongs in a report, not in a bigger case.
/// </remarks>
public sealed class ReferenceCase : IProviderCaseSource {
    /// <inheritdoc />
    public static ProviderConformanceCase ProviderCase { get; } =
        new() {
            DisplayName = "CyberCloud.ConformanceReference/probes",
            CreateProvider = () => new ReferenceProvider(),
            ReconcilerType = typeof(ProbeReconciler),
            CreateReconciler = clock => new ProbeReconciler(clock),
            Type = Probes.Type,
            ApiVersion = Probes.V2026,
            Body = cluster => Probes.Body(cluster),
            ChangedBody = cluster => Probes.Body(cluster, "second"),
            InvalidBody = Probes.BodyWithoutNote,
            InvalidBodyTarget = "/properties/note",
            ActionName = "ping",
            Objects = (id, ns) =>
                [new() { Kind = Probes.Kind, Namespace = ns, Name = id.Name }],
            ObjectMatchesDesired = Probes.Matches
        };
}

/// <summary>The shared suite, run against the reference provider.</summary>
/// <param name="cluster">The harness.</param>
public sealed class ReferenceProviderConformance(ProviderTestCluster<ReferenceCase> cluster)
    : ProviderConformanceTests<ReferenceCase>(cluster), IClassFixture<ProviderTestCluster<ReferenceCase>>;

/// <summary>The container-backed half, skipped loudly, against the reference provider.</summary>
public sealed class ReferenceProviderClusterConformance()
    : ClusterBackedConformanceTests(ReferenceCase.ProviderCase);

/// <summary>
///     The suite proving it <b>rejects</b>, which no run against a conforming provider can.
/// </summary>
/// <remarks>
///     ⚠ <b>Every assertion here is about the suite, not about a provider.</b> A conformance harness
///     is a measuring instrument, and an instrument that has only ever been pointed at healthy
///     specimens has not been calibrated. These point it at a reconciler that is wrong in a known way
///     and assert it says so.
/// </remarks>
public sealed class SuiteRejectionTests {
    [Fact]
    public async Task TheSuiteRejectsAReconcilerThatRemembersInsteadOfObserving() {
        // ⚠ THE CALIBRATION. AssumingProbeReconciler applies once and then answers Converged forever.
        // With the world intact it is indistinguishable from a conforming one — which is exactly why
        // ReconcilerConformance breaks the world and asks the CALLER, not the reconciler, what
        // happened to it.
        var world = new FakeKubeCluster(ConformanceIds.Cluster);
        var clock = new ConformanceClock();

        var address = new ResourceId(
            ConformanceIds.Tenant,
            ConformanceIds.Subscription,
            ConformanceIds.ResourceGroup,
            Probes.Type,
            "calibration",
            Guid.Parse("f0f0f0f0-0000-4000-8000-00000000000f")
        );

        var ns = ReconcileDriver.NamespaceFor(address);
        var target = new ObjectRef { Kind = Probes.Kind, Namespace = ns, Name = address.Name };
        var desired = Probes.Body(ConformanceIds.Cluster);

        using var body = JsonDocument.Parse(desired);

        var context = new ReconcileContext(
            address,
            Probes.V2026,
            body.RootElement,
            null,
            ns,
            world,
            new UnavailableSecretResolver(),
            new RecordingLog()
        );

        var breakable = new ConformanceWorld(
            BreakAsync: () => {
                world.RemoveBehindTheirBack(target);
                return Task.CompletedTask;
            },
            MatchesDesiredAsync: () => Task.FromResult(
                world.Read(target) is { } json && Probes.Matches(json, desired)
            )
        );

        var report = await ReconcilerConformance.RunAsync(
            new AssumingProbeReconciler(),
            context,
            breakable,
            clock,
            TestContext.Current.CancellationToken
        );

        report.Conforms.ShouldBeFalse("the suite accepted a reconciler that never reads the world back");

        report.Findings.Select(x => x.Clause)
            .ShouldContain(ReconcilerClause.NoHiddenState, report.ToString());

        report.Findings.Select(x => x.Clause)
            .ShouldContain(ReconcilerClause.ObservesNeverAssumes, report.ToString());
    }

    [Fact]
    public async Task TheSuiteReportsClauseFourAsSkippedRatherThanPassedWhenItCannotCheckIt() {
        // ⚠ A caller who supplies no ConformanceWorld does not get a free pass on clause 4. That is
        // ReconcilerConformance's own rule and it is what stops a provider from passing the hardest
        // clause by declining to make it testable — so the shared suite always supplies a world, and
        // this is the assertion that the alternative is visibly worse.
        var world = new FakeKubeCluster(ConformanceIds.Cluster);
        var desired = Probes.Body(ConformanceIds.Cluster);

        using var body = JsonDocument.Parse(desired);

        var address = new ResourceId(
            ConformanceIds.Tenant,
            ConformanceIds.Subscription,
            ConformanceIds.ResourceGroup,
            Probes.Type,
            "unchecked",
            Guid.Parse("f1f1f1f1-0000-4000-8000-00000000000f")
        );

        var report = await ReconcilerConformance.RunAsync(
            new ProbeReconciler(new ConformanceClock()),
            new(
                address,
                Probes.V2026,
                body.RootElement,
                null,
                ReconcileDriver.NamespaceFor(address),
                world,
                new UnavailableSecretResolver(),
                new RecordingLog()
            ),
            null,
            new ConformanceClock(),
            TestContext.Current.CancellationToken
        );

        report.Conforms.ShouldBeFalse("clause 4 was not checked and was reported as passing");
        report.ToString().ShouldContain("SKIPPED");
    }

    [Fact]
    public void EveryCaseFieldIsRequiredSoAPartialRegistrationDoesNotCompile() {
        // ⚠ A structural assertion, and it is cheap insurance. ProviderConformanceCase uses `required`
        // on every member; if somebody relaxes one to make a provider "easier to register", the suite
        // starts silently skipping whatever that member drove. This reads the type rather than the
        // instance so it fails on the relaxation, not on the first provider that uses it.
        var optional = typeof(ProviderConformanceCase)
            .GetProperties()
            .Where(x => x.SetMethod is not null || x.GetMethod is not null)
            .Where(x => x.GetCustomAttributes(typeof(System.Runtime.CompilerServices.RequiredMemberAttribute), false).Length == 0)
            .Select(x => x.Name)
            .ToImmutableArray();

        optional.ShouldBeEmpty(
            "every member of a conformance case is required: an optional one is an assertion the "
            + "suite quietly stops making for the provider that omits it"
        );
    }
}
