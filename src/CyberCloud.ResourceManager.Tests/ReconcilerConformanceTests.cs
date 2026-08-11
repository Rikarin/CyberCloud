using CyberCloud.ResourceManager.Conformance;
using CyberCloud.ResourceManager.Tests.Infrastructure;
using System.Text.Json;

namespace CyberCloud.ResourceManager.Tests;

/// <summary>
///     The four-clause reconciler contract, and the proof that the suite rejects a reconciler that
///     breaks it. docs/plan/08 § The reconcile loop.
/// </summary>
public sealed class ReconcilerConformanceTests {
    static ResourceId Address(string name) =>
        new(
            ResourceManagerCluster.Tenant,
            ResourceManagerCluster.Subscription,
            "prod",
            ConformingReconciler.TypeName,
            name,
            Guid.NewGuid()
        );

    static ReconcileContext Context(ResourceId id, RecordingReconcileLog? log = null) {
        using var document = JsonDocument.Parse(TestingProvider.Body());
        return ReconcilerConformance.ContextFor(
            id,
            TestingProvider.V2026,
            document.RootElement.Clone(),
            log ?? new RecordingReconcileLog()
        );
    }

    /// <summary>
    ///     Clause 4's world: remove what was applied, and report whether it is back — both read around
    ///     the reconciler rather than through it.
    /// </summary>
    static ConformanceWorld World(ResourceId id) =>
        new(
            () => {
                FakeWorld.Applied.TryRemove(id.Id, out _);
                return Task.CompletedTask;
            },
            () => Task.FromResult(FakeWorld.Applied.ContainsKey(id.Id))
        );

    [Fact]
    public async Task AConformingReconcilerPassesAllFourClauses() {
        FakeWorld.Reset();
        var id = Address("conforming");
        var reconciler = new ConformingReconciler(TestClock.Instance);

        var report = await ReconcilerConformance.RunAsync(
            reconciler,
            Context(id),
            // Clause 4's world: broken around the reconciler, and its ground truth read around it
            // too — the reconciler's own ObserveAsync is exactly as unreliable as the reconciler.
            World(id),
            TestClock.Instance,
            TestContext.Current.CancellationToken
        );

        report.Conforms.ShouldBeTrue(report.ToString());
    }

    [Fact]
    public async Task TheSuiteRejectsAReconcilerThatAssumesInsteadOfObserving() {
        // ⚠ THE TEST THAT TESTS THE TEST. docs/plan/08 § The reconcile loop, clause 4: "Observes, never
        // assumes. Converged means it READ BACK the desired shape, not that the apply returned 200."
        //
        // NonConformingReconciler remembers `applied = true` and reports Converged from memory. It is
        // indistinguishable from a correct one while the apply keeps working — so the harness empties
        // the world behind its back, and this asserts the harness notices.
        FakeWorld.Reset();
        var id = Address("assuming");
        var reconciler = new NonConformingReconciler();

        var report = await ReconcilerConformance.RunAsync(
            reconciler,
            Context(id),
            World(id),
            TestClock.Instance,
            TestContext.Current.CancellationToken
        );

        report.Conforms.ShouldBeFalse("the suite must reject a reconciler that assumes");

        report.Findings.ShouldContain(
            x => x.Clause == ReconcilerClause.ObservesNeverAssumes,
            $"clause 4 must be reported. Report was: {report}"
        );
    }

    [Fact]
    public void TheSuiteRejectsAReconcilerWithAMutableInstanceField() {
        // ⚠ Clause 2. docs/plan/08 § The reconcile loop: "A reconciler with a field is a reconciler
        // that breaks when the grain moves silo." It breaks by converging on stale state rather than
        // by throwing, which is why a structural check is worth having.
        var findings = ReconcilerConformance.CheckNoHiddenState(new NonConformingReconciler());

        findings.ShouldContain(x => x.Clause == ReconcilerClause.NoHiddenState);
        findings.ShouldContain(x => x.Detail.Contains("applied", StringComparison.Ordinal));
    }

    [Fact]
    public void APrimaryConstructorDependencyIsNotAHiddenStateViolation() {
        // ⚠ The compiler turns a captured primary-constructor parameter into a private field named
        // `<name>P`. ConformingReconciler(IClock clock) has one, and it is a DEPENDENCY — which a
        // singleton is supposed to hold — not per-pass state. A check that flagged it would make the
        // rule unusable and would be turned off.
        var findings = ReconcilerConformance.CheckNoHiddenState(new ConformingReconciler(TestClock.Instance));

        findings.ShouldBeEmpty();
    }

    [Fact]
    public async Task TheSuiteRejectsAReconcilerThatIsNotBounded() {
        // ⚠ Clause 3. "Returns within 30 seconds or returns InProgress. A reconciler that blocks on a
        // four-minute cluster creation blocks that grain's turn, and Orleans grains are
        // single-threaded."
        //
        // The harness's budget is Reconcile.ReconcileDriver.PassBudget. Rather than wait 30 real
        // seconds, this cancels the harness's outer token — which UnboundedReconciler honours the same
        // way it would honour the budget — and asserts the run does not report conformance.
        FakeWorld.Reset();
        var id = Address("unbounded");

        using var source = new CancellationTokenSource();
        source.CancelAfter(TimeSpan.FromMilliseconds(200));

        var reconciler = new UnboundedReconciler();

        // The outer cancellation propagates, which is the correct behaviour: an externally cancelled
        // run is not a conformance verdict. What matters is that a reconciler which never returns
        // cannot produce a passing report.
        await Should.ThrowAsync<OperationCanceledException>(
            () => ReconcilerConformance.RunAsync(reconciler, Context(id), null, TestClock.Instance, source.Token)
        );
    }

    [Fact]
    public async Task ASecondPassOnAConvergedResourceIsConvergedAndChangesNothing() {
        // Clause 1, directly. "The reminder WILL fire on a grain that already converged."
        FakeWorld.Reset();
        var id = Address("idempotent");
        var reconciler = new ConformingReconciler(TestClock.Instance);
        var context = Context(id);

        var first = await reconciler.ReconcileAsync(context, TestContext.Current.CancellationToken);
        first.IsConverged.ShouldBeTrue();

        var appliedAfterFirst = FakeWorld.Applied[id.Id];
        var passesAfterFirst = FakeWorld.Passes[id.Id];

        var second = await reconciler.ReconcileAsync(context, TestContext.Current.CancellationToken);

        second.IsConverged.ShouldBeTrue();
        FakeWorld.Applied[id.Id].ShouldBe(appliedAfterFirst, "the second pass changed nothing");
        FakeWorld.Passes[id.Id].ShouldBe(passesAfterFirst + 1, "it did run — it just changed nothing");
    }

    [Fact]
    public async Task ClauseFourIsReportedAsSkippedRatherThanPassedWhenTheWorldCannotBeBroken() {
        // ⚠ A caller who cannot break their own world has not tested clause 4, and the report says so
        // rather than coming back clean. A silent pass here would be the suite's own version of
        // "assumes rather than observes".
        FakeWorld.Reset();
        var reconciler = new ConformingReconciler(TestClock.Instance);

        var report = await ReconcilerConformance.RunAsync(
            reconciler,
            Context(Address("unbreakable")),
            null,
            TestClock.Instance,
            TestContext.Current.CancellationToken
        );

        report.Conforms.ShouldBeFalse();
        report.Findings.ShouldContain(
            x => x.Clause == ReconcilerClause.ObservesNeverAssumes && x.Detail.StartsWith("SKIPPED", StringComparison.Ordinal)
        );
    }

    [Fact]
    public async Task AReconcilerReportsProgressThroughTheLogAndNotThroughTheOutcome() {
        // docs/plan/08 § The reconcile loop: "A reconciler that wants to say something else wants to
        // log, and IReconcileLog is how — those entries stream to operation-progress and appear in the
        // portal and in `cyc --wait`, which is what turns a four-minute cluster creation from a spinner
        // into a story."
        FakeWorld.Reset();
        var log = new RecordingReconcileLog();
        var reconciler = new ConformingReconciler(TestClock.Instance);

        await reconciler.ReconcileAsync(Context(Address("chatty"), log), TestContext.Current.CancellationToken);

        log.Entries.ShouldContain(x => x.Step == "applying");
        log.Entries.ShouldContain(x => x.Step == "ready" && x.Percent == 100);
    }

    [Fact]
    public void TheNamespaceRuleIsOneFunctionAndFitsADnsLabel() {
        // ⚠ docs/plan/09 § The command builder's example reads `ctx.Namespace`, which docs/plan/08's
        // ReconcileContext does not have. Deriving it in one place is what stops twenty reconcilers
        // disagreeing about which namespace a resource group maps to.
        var id = Address("ns");
        var ns = Reconcile.ReconcileDriver.NamespaceFor(id);

        ns.Length.ShouldBeLessThanOrEqualTo(63, "a Kubernetes namespace is a DNS-1123 label");
        ns.ShouldStartWith(id.SubscriptionId.ToString("N"));
        ns.ShouldEndWith("-prod");

        // Same group, same namespace — every time.
        Reconcile.ReconcileDriver.NamespaceFor(Address("other")).ShouldBe(ns);
    }
}
