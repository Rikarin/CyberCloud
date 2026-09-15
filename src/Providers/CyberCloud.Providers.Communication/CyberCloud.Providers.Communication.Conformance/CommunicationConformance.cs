using CyberCloud.Conformance;
using CyberCloud.Conformance.Harness;
using CyberCloud.Providers.Communication.Contracts;
using System.Collections.Immutable;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Communication.Conformance;

/// <summary>
///     <c>CyberCloud.Communication/services</c>, registered into the shared provider suite.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             THIS FAMILY IS THE ONE THAT COST <c>test/CyberCloud.Conformance</c> A CHANGE, AND
///             THE CHANGE IS THE CLUSTERLESS HALF THE SUITE HAD PROMISED SINCE THE SAMPLE.
///         </b> <c>ProviderConformanceCase.Objects</c> said a clusterless provider hands the suite
///         an empty list and the cluster-facing half skips itself. Fourteen families never tested
///         that sentence, and it was half true: the suite could skip, and could not <i>assert</i>,
///         because everything it read was the fake cluster. So the four cases below hand it
///         <see cref="CommunicationModule" /> through <c>IProviderCaseSource.ConvergedModule</c>,
///         and the world-facing assertions — applied and matching, drift put back, hand edit
///         overwritten, clause 4, teardown gone, update seen — read the module instead. Two skip
///         loudly and say why: an admission refusal and a dropped connection are a cluster's
///         answers, and an in-process silo has neither.
///     </para>
///     <para>
///         ⚠ <b>SO STATE PLAINLY WHAT A GREEN RUN HERE PROVES AND WHAT IT DOES NOT.</b> It proves
///         the twelve-step write path, the verb grammar, the four reconciler clauses, the
///         cross-tenant 404 and the delete-read-back, over grain state read around the reconciler.
///         It proves <b>nothing</b> about a message reaching anybody — no carrier exists in this
///         build, every channel resolves to the refusing seam, and a <c>send</c> here would refuse
///         honestly. It proves nothing about the silo-kill criterion either, because the
///         cluster-backed harness refuses a case with no objects by name; that is
///         <c>charts/bundle/bundle.yaml § owed</c>. And the one property that matters most on this
///         family — that a suppressed address is never handed to a carrier — is not this suite's to
///         prove: <c>SuppressionEnforcementTests</c> in <c>CyberCloud.Providers.Communication.Tests</c>
///         drives a real send against a real list and was sabotage-tested.
///     </para>
/// </remarks>
public sealed class CommunicationServiceCase : IProviderCaseSource {
    /// <inheritdoc />
    public static ProviderConformanceCase ProviderCase { get; } =
        new() {
            DisplayName = "CyberCloud.Communication/services",
            CreateProvider = () => new CommunicationProvider(),
            ReconcilerType = typeof(CommunicationServiceReconciler),
            // ⚠ The module's seam, read when the factory RUNS — after the harness attached a cluster
            // — and not when this case was constructed. CommunicationModule.Plane refuses by name if
            // that order is ever wrong.
            CreateReconciler = clock => new CommunicationServiceReconciler(clock, Modules.Service.Plane),
            Type = CommunicationServices.Type,
            ApiVersion = CommunicationServices.V2026,
            Body = _ => CommunicationServices.Body("en"),
            // Changes the one property the grain holds beyond the name, and the send path reads it.
            ChangedBody = _ => CommunicationServices.Body("cs-CZ"),
            // Drops the required `/location` — the one required property this type has.
            InvalidBody = _ => WithoutLocation(CommunicationServices.Body("en")),
            InvalidBodyTarget = "/location",
            // ⚠ Of the four, the one that takes no required argument: `send` needs a channel, a
            // recipient and a key, and the suite posts an EMPTY body. What the POST half of the verb
            // grammar proves here is that a handler reaches the module from the request path;
            // `send` itself is SuppressionEnforcementTests' to drive.
            ActionName = CommunicationServices.ListSuppressionsAction,
            Objects = static (_, _) => [],
            OperatorWritten = static (_, _) => [],
            // Never asked — a clusterless case has no object to match — and false rather than
            // true so that a harness bug that DID ask would read as a failure.
            ObjectMatchesDesired = static _ => false
        };

    /// <inheritdoc />
    public static IConvergedModule? ConvergedModule => Modules.Service;

    /// <summary>A valid body with its required region removed.</summary>
    internal static string WithoutLocation(string body) {
        var node = JsonNode.Parse(body)!.AsObject();
        node.Remove("location");
        return node.ToJsonString();
    }

    /// <summary>A valid body with one property under <c>/properties</c> removed.</summary>
    internal static string WithoutProperty(string body, string name) {
        var node = JsonNode.Parse(body)!.AsObject();
        node["properties"]!.AsObject().Remove(name);
        return node.ToJsonString();
    }
}

/// <summary>
///     <c>CyberCloud.Communication/services/channels</c>, registered into the shared provider suite.
/// </summary>
/// <remarks>
///     ⚠ <b>The suite's reset removes every channel kind on the shared ancestor before each test,
///     and this case is why <c>IConvergedModule.Reset</c> exists.</b> One service holds one
///     configuration per kind, owned by one resource; every test here creates a fresh resource
///     saying <c>kind: email</c> under the same ancestor and never deletes it, so without the reset
///     the second test would be refused by the first test's leftover — correctly, and uselessly.
/// </remarks>
public sealed class CommunicationChannelCase : IProviderCaseSource {
    /// <inheritdoc />
    public static ProviderConformanceCase ProviderCase { get; } =
        new() {
            DisplayName = "CyberCloud.Communication/services/channels",
            CreateProvider = () => new CommunicationProvider(),
            ReconcilerType = typeof(CommunicationChannelReconciler),
            CreateReconciler = clock => new CommunicationChannelReconciler(clock, Modules.Channel.Plane),
            Type = CommunicationChannels.Type,
            ApiVersion = CommunicationServices.V2026,
            Body = _ => CommunicationChannels.Body(kind: "email", maxMessagesPerDay: 100),
            // Changes the limit, which the grain holds and the send path reserves against.
            ChangedBody = _ => CommunicationChannels.Body(kind: "email", maxMessagesPerDay: 250),
            InvalidBody = _ => CommunicationServiceCase.WithoutProperty(CommunicationChannels.Body(), "kind"),
            InvalidBodyTarget = "/properties/kind",
            // ⚠ EXPLICITLY EMPTY — a channel is configuration and declares no action. Written out
            // rather than defaulted, for the reason ProviderConformanceCase.ActionName gives.
            ActionName = string.Empty,
            Objects = static (_, _) => [],
            OperatorWritten = static (_, _) => [],
            ObjectMatchesDesired = static _ => false
        };

    /// <inheritdoc />
    public static ImmutableArray<ProviderConformanceCase> Ancestors { get; } = [CommunicationServiceCase.ProviderCase];

    /// <inheritdoc />
    public static IConvergedModule? ConvergedModule => Modules.Channel;
}

/// <summary>
///     <c>CyberCloud.Communication/services/templates</c>, registered into the shared provider suite.
/// </summary>
/// <remarks>
///     ⚠ The body declares its one variable as <i>optional</i>, and that is for the suite rather
///     than for the template: the POST assertion posts an empty body, and a required variable would
///     make <c>render</c> refuse — correctly — before the assertion could see a <c>200</c>. The
///     refusal itself is <c>TemplateRenderTests</c>' to pin.
/// </remarks>
public sealed class CommunicationTemplateCase : IProviderCaseSource {
    /// <inheritdoc />
    public static ProviderConformanceCase ProviderCase { get; } =
        new() {
            DisplayName = "CyberCloud.Communication/services/templates",
            CreateProvider = () => new CommunicationProvider(),
            ReconcilerType = typeof(CommunicationTemplateReconciler),
            CreateReconciler = clock => new CommunicationTemplateReconciler(clock, Modules.Template.Plane),
            Type = CommunicationTemplates.Type,
            ApiVersion = CommunicationServices.V2026,
            Body = _ => CommunicationTemplates.Body(body: "Your code is {code}.", variables: [], optionalVariables: ["code"]),
            // Changes the text, which appends a version the send path would use.
            ChangedBody = _ => CommunicationTemplates.Body(body: "Your one-time code is {code}.", variables: [], optionalVariables: ["code"]),
            InvalidBody = _ => CommunicationServiceCase.WithoutProperty(CommunicationTemplates.Body(), "body"),
            InvalidBodyTarget = "/properties/body",
            ActionName = CommunicationTemplates.RenderAction,
            Objects = static (_, _) => [],
            OperatorWritten = static (_, _) => [],
            ObjectMatchesDesired = static _ => false
        };

    /// <inheritdoc />
    public static ImmutableArray<ProviderConformanceCase> Ancestors { get; } = [CommunicationServiceCase.ProviderCase];

    /// <inheritdoc />
    public static IConvergedModule? ConvergedModule => Modules.Template;
}

/// <summary>
///     <c>CyberCloud.Communication/services/suppressions</c>, registered into the shared provider
///     suite.
/// </summary>
/// <remarks>
///     ⚠ Every test here blocks the same address on the same ancestor, and that is fine for the
///     reason it would not be for a channel: a manual block is one entry however many resources
///     name it, the reconciler reads before it writes, and the delete releases only a manual block.
///     What the suite cannot see — an entry held for a complaint surviving the resource that named
///     it — is <c>SuppressionEnforcementTests</c>' to pin.
/// </remarks>
public sealed class CommunicationSuppressionCase : IProviderCaseSource {
    /// <inheritdoc />
    public static ProviderConformanceCase ProviderCase { get; } =
        new() {
            DisplayName = "CyberCloud.Communication/services/suppressions",
            CreateProvider = () => new CommunicationProvider(),
            ReconcilerType = typeof(CommunicationSuppressionReconciler),
            CreateReconciler = clock => new CommunicationSuppressionReconciler(clock, Modules.Suppression.Plane),
            Type = CommunicationSuppressions.Type,
            ApiVersion = CommunicationServices.V2026,
            Body = _ => CommunicationSuppressions.Body(note: "Asked us to stop."),
            // Changes the note, which the entry carries and a support case reads.
            ChangedBody = _ => CommunicationSuppressions.Body(note: "Asked us to stop, twice."),
            InvalidBody = _ => CommunicationServiceCase.WithoutProperty(CommunicationSuppressions.Body(), "destination"),
            InvalidBodyTarget = "/properties/destination",
            ActionName = string.Empty,
            Objects = static (_, _) => [],
            OperatorWritten = static (_, _) => [],
            ObjectMatchesDesired = static _ => false
        };

    /// <inheritdoc />
    public static ImmutableArray<ProviderConformanceCase> Ancestors { get; } = [CommunicationServiceCase.ProviderCase];

    /// <inheritdoc />
    public static IConvergedModule? ConvergedModule => Modules.Suppression;
}

/// <summary>The shared suite, run against the service type.</summary>
/// <param name="cluster">The harness.</param>
public sealed class CommunicationServiceConformance(ProviderTestCluster<CommunicationServiceCase> cluster)
    : ProviderConformanceTests<CommunicationServiceCase>(cluster), IClassFixture<ProviderTestCluster<CommunicationServiceCase>>;

/// <summary>The shared suite, run against the channel type.</summary>
/// <param name="cluster">The harness.</param>
public sealed class CommunicationChannelConformance(ProviderTestCluster<CommunicationChannelCase> cluster)
    : ProviderConformanceTests<CommunicationChannelCase>(cluster), IClassFixture<ProviderTestCluster<CommunicationChannelCase>>;

/// <summary>The shared suite, run against the template type.</summary>
/// <param name="cluster">The harness.</param>
public sealed class CommunicationTemplateConformance(ProviderTestCluster<CommunicationTemplateCase> cluster)
    : ProviderConformanceTests<CommunicationTemplateCase>(cluster), IClassFixture<ProviderTestCluster<CommunicationTemplateCase>>;

/// <summary>The shared suite, run against the suppression type.</summary>
/// <param name="cluster">The harness.</param>
public sealed class CommunicationSuppressionConformance(ProviderTestCluster<CommunicationSuppressionCase> cluster)
    : ProviderConformanceTests<CommunicationSuppressionCase>(cluster), IClassFixture<ProviderTestCluster<CommunicationSuppressionCase>>;

/// <summary>
///     The cluster-backed half, which this family does not have — said here, by name, rather than
///     left to be noticed.
/// </summary>
/// <remarks>
///     ⚠ <b>Not derived from <c>ClusterBackedConformanceTests</c>, because that class's skip
///     promises a <c>*.Cluster.Conformance</c> project that does not exist for this family and
///     cannot yet.</b> <c>test/CyberCloud.Cluster.Conformance</c> refuses a case with no objects
///     (<c>ClusterConformanceTests.TheCaseOwnsClusterObjectsOrThisWholeSuiteWouldBeVacuous</c>),
///     so a clusterless family has no harness for the one criterion in that half that would mean
///     something here: killing the silo mid-create and finding the grains converged from real
///     PostgreSQL. That is owed, and <c>charts/bundle/bundle.yaml § owed</c> records it.
/// </remarks>
public sealed class CommunicationClusterBackedConformance {
    /// <summary>The skip that says what the cluster-backed half would have checked.</summary>
    [Fact]
    [Trait("Requires", "cluster")]
    public void TheSiloKillCriterionHasNoClusterlessHarnessYet() =>
        Assert.Skip(
            "SKIPPED, AND SAYING SO — CyberCloud.Communication's four types are clusterless, and "
            + "test/CyberCloud.Cluster.Conformance refuses a case with no objects by name. Four of its "
            + "five criteria are about a real API server and do not apply; the fifth — killing the silo "
            + "mid-create and finding the resource converged from a real durable tier — does apply, and "
            + "has no clusterless harness yet. charts/bundle/bundle.yaml § owed carries it as "
            + "communication-silo-kill-has-no-clusterless-harness."
        );
}
