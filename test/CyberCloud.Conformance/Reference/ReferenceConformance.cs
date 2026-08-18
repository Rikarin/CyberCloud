using CyberCloud.Conformance.Harness;
using CyberCloud.Core.Time;
using CyberCloud.ResourceManager;
using CyberCloud.ResourceManager.Conformance;
using CyberCloud.ResourceManager.Reconcile;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;

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
            // This platform mints or computes everything this type's actions hand back, so no operator
            // writes an object any action reads. Stated rather than defaulted — see
            // ProviderConformanceCase.OperatorWritten.
            OperatorWritten = static (_, _) => [],
            ObjectMatchesDesired = match => Probes.Matches(match.ObjectJson, match.DesiredJson)
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
///         case files were not touched, and the child inherits all <b>28</b> assertions rather than a
///         subset — <c>ProviderTestCluster.Address</c> interleaves the ancestors the harness created,
///         so every assertion that addressed <c>…/probes/{name}</c> now addresses
///         <c>…/probes/ancestor-0/samples/{name}</c> and nothing else about it changes. All 28 are
///         <i>applicable</i> to it, where a top-level type self-skips the parent-existence one; the
///         child is therefore the only case in the tree that runs the whole suite.
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
            // This platform mints or computes everything this type's actions hand back, so no operator
            // writes an object any action reads. Stated rather than defaulted — see
            // ProviderConformanceCase.OperatorWritten.
            OperatorWritten = static (_, _) => [],
            ObjectMatchesDesired = match => Probes.Matches(match.ObjectJson, match.DesiredJson)
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
///     anybody's diligence, and
///     <c>SuiteRejectionTests.TheChildRunsEveryAssertionTheParentDoesRatherThanASubset</c> is the
///     assertion that says so out loud — measured at 28 apiece.
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
    public async Task AnEmptyCollectionDoesNotSurviveAnApplyTheWayItUsedTo() {
        // ⚠ THE CALIBRATION FOR THE ONE PLACE FakeKubeCluster IS NOT AN ECHO, and the instrument it
        // calibrates is the harness rather than a reconciler.
        //
        // The fake stored the applied body verbatim, which made it structurally blind to everything a
        // real API server takes AWAY. Every optional list and map on every built-in Kubernetes object
        // carries `omitempty`: NetworkPolicySpec.Ingress is one, the empty list that spells "deny all
        // ingress" comes back with NO KEY AT ALL, and CyberCloud.Terminal/consoles converged in this
        // harness and hung forever against k3s. Eleven other families never hit it only because they
        // render custom resources, whose x-kubernetes-preserve-unknown-fields schemas round-trip an
        // empty array intact.
        //
        // ⚠ THE PROBE RENDERS A CORE-GROUP ConfigMap, WHICH IS WHY THE STRIP APPLIES TO IT. It is
        // scoped to built-in groups, and the sibling test below is the calibration for the other
        // side of that boundary — a custom resource keeps its empty collections, because a real API
        // server keeps them and three families use an empty object as a presence flag.
        var world = new FakeKubeCluster(ConformanceIds.Cluster);

        var address = new ResourceId(
            ConformanceIds.Tenant,
            ConformanceIds.Subscription,
            ConformanceIds.ResourceGroup,
            Probes.Type,
            "empties",
            Guid.Parse("f0f0f0f0-0000-4000-8000-0000000000e0")
        );

        var ns = ReconcileDriver.NamespaceFor(address);
        var target = new ObjectRef { Kind = Probes.Kind, Namespace = ns, Name = address.Name };

        // ⚠ Built through KubeCommand.For rather than by hand, so this exercises the same apply path
        // every reconciler uses. The seven labels arrive with it, which is also what lets the
        // "removes nothing else" assertions below mean something.
        var applied = await KubeCommand.For(world)
            .WithTenantId(address.TenantId)
            .WithResourceId(address)
            .InNamespace(ns)
            .WithKind(Probes.Kind)
            .WithApiVersion(Probes.V2026)
            .ObjectJson(
                """
                {
                  "spec": {
                    "ingress": [],
                    "selector": {},
                    "egress": [ { "to": "anywhere" } ],
                    "nested": { "alsoEmpty": [] }
                  }
                }
                """
            )
            .ApplyAsync(TestContext.Current.CancellationToken);

        applied.IsSuccess.ShouldBeTrue(applied.Error?.Message);

        var stored = JsonNode.Parse(world.Read(target)!)!.AsObject();
        var spec = stored["spec"]!.AsObject();

        spec["ingress"].ShouldBeNull("an empty list must come back as NO KEY, the way omitempty leaves it");
        spec["selector"].ShouldBeNull("an empty map is dropped for the same reason an empty list is");

        // ⚠ Depth first: `nested` held nothing but an empty list, so it is itself empty by the time
        // its parent is considered — an empty struct being just as absent as an empty list.
        spec["nested"].ShouldBeNull("the strip must reach every depth, not only the top of spec");

        // ⚠ AND IT REMOVES NOTHING ELSE. A strip that also ate the non-empty list would make every
        // provider's comparison unfalsifiable, which is a worse failure than the one it fixes.
        spec["egress"]!.AsArray().Count.ShouldBe(1, "a list with entries must survive intact");

        stored["metadata"]!["labels"]![KubeLabels.TenantId]!.GetValue<string>()
            .ShouldBe(address.TenantId.ToString("D"), "the seven mandatory labels must survive the strip");

        // ⚠ The tolerant pattern accepts what the store now holds; the strict one does not. This is
        // the whole argument for the strip, asserted rather than described.
        KubeJson.IsAbsentOrEmpty(spec["ingress"]).ShouldBeTrue();
        (spec["ingress"] is JsonArray { Count: 0 }).ShouldBeFalse(
            "`is JsonArray { Count: 0 }` is the shape that passed this harness and hung against k3s"
        );
    }

    [Fact]
    public async Task ACustomResourceKeepsItsEmptyObjectBecauseThatIsAPresenceFlag() {
        // ⚠ THE OTHER SIDE OF THE BOUNDARY, AND IT IS HERE BECAUSE THE FIRST VERSION OF THE STRIP GOT
        // IT WRONG AND THREE FAMILIES SAID SO IN ONE RUN.
        //
        // The strip was unconditional at first, on the theory that an empty collection never carries
        // meaning, so forcing the tolerant spelling everywhere was free. A custom resource has no
        // `omitempty` — a CRD's stored JSON keeps what was applied — and Strimzi, Cluster API and
        // kube-ovn all use an EMPTY OBJECT AS A PRESENCE FLAG: `spec.cruiseControl = {}` means "run
        // Cruise Control", and `bridge = {}` and `pod = {}` mean the same kind of thing. Stripping
        // those threw away a distinction a real server preserves, and no tolerant comparison can
        // recover information the harness deleted: nine tests in each of Messaging, ContainerService
        // and Network went red for a reason that does not exist outside FakeKubeCluster.
        //
        // So the strip is scoped to built-in groups, and this is what holds the scope in place.
        var world = new FakeKubeCluster(ConformanceIds.Cluster);

        var address = new ResourceId(
            ConformanceIds.Tenant,
            ConformanceIds.Subscription,
            ConformanceIds.ResourceGroup,
            Probes.Type,
            "flagged",
            Guid.Parse("f0f0f0f0-0000-4000-8000-0000000000e1")
        );

        var ns = ReconcileDriver.NamespaceFor(address);

        var custom = new GroupVersionKind {
            Group = "kafka.strimzi.io",
            Version = "v1beta2",
            Kind = "Kafka",
            Plural = "kafkas"
        };

        var applied = await KubeCommand.For(world)
            .WithTenantId(address.TenantId)
            .WithResourceId(address)
            .InNamespace(ns)
            .WithKind(custom)
            .WithApiVersion(Probes.V2026)
            .ObjectJson("""{ "spec": { "cruiseControl": {}, "listeners": [] } }""")
            .ApplyAsync(TestContext.Current.CancellationToken);

        applied.IsSuccess.ShouldBeTrue(applied.Error?.Message);

        var spec = JsonNode.Parse(world.Read(new() { Kind = custom, Namespace = ns, Name = address.Name })!)!
            .AsObject()["spec"]!
            .AsObject();

        spec["cruiseControl"].ShouldNotBeNull(
            "an empty object on a CUSTOM resource is a presence flag a real API server preserves, and "
            + "a harness that deletes it makes a converging provider look broken"
        );

        spec["listeners"].ShouldNotBeNull("a custom resource's empty array survives for the same reason");
    }

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
        //
        // ⚠ MatchContext is read too, and the rule points the other way there. On the case, an
        // optional member is an assertion the suite stops making. On MatchContext — which the harness
        // builds and a case only reads — an optional member is a fact the harness stops HANDING OVER,
        // and a case cannot assert on what it was not given. That is the shape that kept every child
        // type's suite smaller than its parent's until the record existed.
        var optional = new[] { typeof(ProviderConformanceCase), typeof(MatchContext) }
            .SelectMany(type => type.GetProperties().Select(x => (Type: type, Property: x)))
            .Where(x => x.Property.SetMethod is not null || x.Property.GetMethod is not null)
            .Where(x => x.Property.GetCustomAttributes(typeof(System.Runtime.CompilerServices.RequiredMemberAttribute), false).Length == 0)
            .Select(x => $"{x.Type.Name}.{x.Property.Name}")
            .ToImmutableArray();

        optional.ShouldBeEmpty(
            "every member of a conformance case is required: an optional one is an assertion the "
            + "suite quietly stops making for the provider that omits it — and every member of a "
            + "MatchContext is required because an optional one is a fact the harness stops handing "
            + "the case"
        );
    }
}
