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
                [new() { Kind = Probes.Kind, Namespace = ns, Name = Probes.ObjectNameOf(id) }],
            ObjectMatchesDesired = Probes.Matches
        };
}

/// <summary>
///     The reference provider's <b>child</b> type, registered exactly as its parent is.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This is the whole cost of putting a child type under the shared suite, and it is one
///         member longer than a top-level one.</b> Everything else is the same object shape:
///         <see cref="ProviderConformanceCase" /> gained nothing, the four shipping providers'
///         case files were not touched, and the child inherits all 27 assertions rather than a
///         subset — <c>ProviderTestCluster.Address</c> interleaves the ancestors the harness created,
///         so every assertion that addressed <c>…/probes/{name}</c> now addresses
///         <c>…/probes/ancestor-0/samples/{name}</c> and nothing else about it changes.
///     </para>
///     <para>
///         ⚠ <see cref="Ancestors" /> is the parent's own case object rather than a description of it
///         — see <c>IProviderCaseSource.Ancestors</c>. A provider shipping <c>servers/databases</c>
///         writes <c>[ServersCase.ProviderCase]</c> here for the same reason.
///     </para>
/// </remarks>
public sealed class ReferenceChildCase : IProviderCaseSource {
    /// <inheritdoc />
    public static ProviderConformanceCase ProviderCase { get; } =
        new() {
            DisplayName = "CyberCloud.ConformanceReference/probes/samples",
            CreateProvider = () => new ReferenceProvider(),
            ReconcilerType = typeof(SampleReconciler),
            CreateReconciler = clock => new SampleReconciler(clock),
            Type = Probes.ChildType,
            ApiVersion = Probes.V2026,
            Body = cluster => Probes.ChildBody(cluster),
            ChangedBody = cluster => Probes.ChildBody(cluster, "second"),
            InvalidBody = Probes.ChildBodyWithoutNote,
            InvalidBodyTarget = "/properties/note",
            ActionName = "ping",
            Objects = (id, ns) =>
                [new() { Kind = Probes.Kind, Namespace = ns, Name = Probes.ObjectNameOf(id) }],
            ObjectMatchesDesired = Probes.Matches
        };

    /// <inheritdoc />
    public static ImmutableArray<ProviderConformanceCase> Ancestors { get; } =
        [ReferenceCase.ProviderCase];
}

/// <summary>
///     A depth-2 source that describes no ancestors — the omission, so the guard can be shown to
///     catch it.
/// </summary>
/// <remarks>
///     ⚠ <b>Deliberately wrong, and it must never be given a test class of its own.</b> It exists so
///     <c>SuiteRejectionTests</c> can point the harness at the mistake a provider author makes once
///     and assert the message names the member. <see cref="Ancestors" /> is not written here at all,
///     which is the point: the default is what is under test.
/// </remarks>
public sealed class AncestorlessChildCase : IProviderCaseSource {
    /// <inheritdoc />
    public static ProviderConformanceCase ProviderCase => ReferenceChildCase.ProviderCase;
}

/// <summary>A depth-2 source whose one ancestor is not its own — the copy-paste, so the guard can be
///     shown to catch it.</summary>
public sealed class WrongAncestorChildCase : IProviderCaseSource {
    /// <inheritdoc />
    public static ProviderConformanceCase ProviderCase => ReferenceChildCase.ProviderCase;

    /// <inheritdoc />
    public static ImmutableArray<ProviderConformanceCase> Ancestors { get; } =
        [ReferenceChildCase.ProviderCase];
}

/// <summary>The shared suite, run against the reference provider.</summary>
/// <param name="cluster">The harness.</param>
public sealed class ReferenceProviderConformance(ProviderTestCluster<ReferenceCase> cluster)
    : ProviderConformanceTests<ReferenceCase>(cluster), IClassFixture<ProviderTestCluster<ReferenceCase>>;

/// <summary>
///     The <b>same</b> suite, run against the reference provider's child type.
/// </summary>
/// <remarks>
///     ⚠ <b>The same class, not a child-shaped copy of it.</b> A separate suite for children would be
///     free to assert less and nothing would say which assertions it had dropped — the exact shape of
///     "a suite that goes green because it asked less". Deriving from
///     <c>ProviderConformanceTests&lt;T&gt;</c> makes the count a fact of the compiler rather than of
///     anybody's diligence, and <c>ChildConformanceCoverageTests</c> is the assertion that says so out
///     loud.
/// </remarks>
/// <param name="cluster">The harness.</param>
public sealed class ReferenceChildProviderConformance(ProviderTestCluster<ReferenceChildCase> cluster)
    : ProviderConformanceTests<ReferenceChildCase>(cluster),
        IClassFixture<ProviderTestCluster<ReferenceChildCase>>;

/// <summary>
///     The signpost to the container-backed half, which runs in <c>CyberCloud.Cluster.Conformance</c>.
/// </summary>
public sealed class ReferenceProviderClusterSignpost()
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
    public void ADepthTwoSourceWithNoAncestorsIsRefusedByNameRatherThanFailingEveryTestAtOnce() {
        // ⚠ THE CALIBRATION FOR IProviderCaseSource.Ancestors' DEFAULT, and the reason that default is
        // not the "optional member the suite quietly stops asserting" the case record forbids.
        //
        // Pointed at a source that is registered for a depth-2 type and describes no ancestors — the
        // exact omission a provider author makes once. The failure has to name the case and the
        // member. Without this guard it is ResourceId's constructor throwing ArgumentException about
        // parent-name counts, from a static helper every test calls, so xUnit reports all 27 as
        // failed and none of them says which member is missing.
        var thrown = Should.Throw<InvalidOperationException>(
            () => ProviderTestCluster<AncestorlessChildCase>.Address("anything")
        );

        thrown.Message.ShouldContain("Ancestors");
        thrown.Message.ShouldContain(Probes.ChildTypePath);
        thrown.Message.ShouldContain("a child cannot be created until its parent exists");
    }

    [Fact]
    public void AnAncestorThatIsNotTheTypesOwnAncestorIsRefused() {
        // ⚠ The other half, and it is the one a copy-paste produces: the right NUMBER of ancestors
        // naming the wrong type. The harness registers ONE provider, so a case pointing at somebody
        // else's parent would have the harness creating a resource this run's registry cannot
        // address — and the create would fail with the registry's message rather than the case's.
        var thrown = Should.Throw<InvalidOperationException>(
            () => ProviderTestCluster<WrongAncestorChildCase>.Address("anything")
        );

        thrown.Message.ShouldContain(Probes.ChildTypePath);
        thrown.Message.ShouldContain("the same provider by construction");
    }

    [Fact]
    public void TheChildRunsEveryAssertionTheParentDoesRatherThanASubset() {
        // ⚠ FAILURE CLASS (a), ASSERTED RATHER THAN INTENDED. "The child case runs the same suite" is
        // a claim about a count, and a claim about a count that nothing counts is how a suite goes
        // green by asking less — this harness has been bitten by exactly that once, when a drift test
        // deleted one object of two.
        //
        // Both classes derive from ProviderConformanceTests<T>, so the count is a fact of the
        // compiler; what this pins is that neither has grown a `new` member, an override that hides
        // one, or a second base. It reads the RUNNABLE facts — public, [Fact]-attributed — off the
        // two closed generic types, which is what xUnit itself enumerates.
        var parent = RunnableFactsOf(typeof(ReferenceProviderConformance));
        var child = RunnableFactsOf(typeof(ReferenceChildProviderConformance));

        child.ShouldBe(
            parent,
            "the child type runs a different set of assertions than its parent does. A child-shaped "
            + "copy of the suite is free to assert less, and nothing but this test would say which "
            + "assertions it had dropped"
        );

        // …and the set is not empty, which is the other way a set comparison passes for free.
        parent.Length.ShouldBeGreaterThan(20);
    }

    /// <summary>Every <c>[Fact]</c> a test class runs, by name, ordered.</summary>
    /// <param name="suite">The closed test class.</param>
    static ImmutableArray<string> RunnableFactsOf(Type suite) =>
        [
            .. suite
                .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                .Where(x => x.GetCustomAttributes(typeof(FactAttribute), true).Length > 0)
                .Select(x => x.Name)
                .OrderBy(x => x, StringComparer.Ordinal)
        ];

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
