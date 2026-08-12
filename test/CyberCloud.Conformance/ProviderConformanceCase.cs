using System.Collections.Immutable;

namespace CyberCloud.Conformance;

/// <summary>
///     Everything the shared suite has to be told about one provider. <b>This is the registration.</b>
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/03 § Providers: <i>"The conformance suite is what makes the catalogue safe to
///         grow. It is one xUnit theory that every provider must pass … A provider is not registered
///         in the platform bundle until it passes."</i> The suite is parameterised on this record so
///         that adding the twentieth provider is a case object and two class declarations in that
///         provider's own <c>.Conformance</c> project — not a copy of the suite.
///     </para>
///     <para>
///         ⚠ <b>Everything here is data or a pure function, and none of it is a hook the suite calls
///         to decide whether the provider passed.</b> A case supplies bodies, addresses and the
///         objects a resource owns; the <i>assertions</i> are the suite's and are the same for every
///         provider. A case that could supply an assertion would be a provider grading its own
///         homework.
///     </para>
///     <para>
///         ⚠ <b><see cref="ObjectMatchesDesired" /> is read <i>around</i> the reconciler and must
///         stay that way.</b> It is the ground truth of clause 4 — <c>ReconcilerConformance</c>'s
///         remarks explain why the harness cannot use the reconciler's own <c>ObserveAsync</c> for
///         this: an observer is exactly as unreliable as the reconciler it belongs to. Implement it
///         against the object's JSON and nothing else.
///     </para>
/// </remarks>
public sealed record ProviderConformanceCase {
    /// <summary>What to call this provider in a test name and a failure message.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Builds the provider. Called once per harness.</summary>
    /// <remarks>
    ///     ⚠ A factory rather than an instance, because <c>IResourceProvider.Describe</c> is run twice
    ///     — once by <c>DiscoveringProviderBuilder</c> to learn the reconciler types and once by
    ///     <c>ProviderRegistry</c> to build the registrations — and a case that handed out a shared
    ///     instance would hide a <c>Describe</c> that was not pure.
    /// </remarks>
    public required Func<IResourceProvider> CreateProvider { get; init; }

    /// <summary>The reconciler's concrete type, as the registry stores it and the driver resolves it.</summary>
    public required Type ReconcilerType { get; init; }

    /// <summary>Builds a reconciler with the harness's clock.</summary>
    /// <remarks>
    ///     ⚠ <b>A factory rather than reflection over <see cref="ReconcilerType" />.</b> The suite
    ///     drives some passes directly — the clause check and the drift repair — and doing that through
    ///     <c>Activator.CreateInstance</c> would bake "every reconciler takes exactly an
    ///     <c>IClock</c>" into the shared suite, which is true of one provider and will not stay true.
    ///     The clock is passed because a reconciler that stamps an observation needs one and the
    ///     harness owns the only clock the silo agrees with.
    ///     <para>
    ///         The suite asserts that this factory and <see cref="ReconcilerType" /> agree with the
    ///         registry, so a case cannot quietly test a different reconciler than the one the driver
    ///         runs.
    ///     </para>
    /// </remarks>
    public required Func<Core.Time.IClock, IResourceReconciler> CreateReconciler { get; init; }

    /// <summary>The resource type under test.</summary>
    public required ResourceTypeName Type { get; init; }

    /// <summary>The api-version every request in the run carries.</summary>
    public required string ApiVersion { get; init; }

    /// <summary>A valid body, for the cluster the harness owns.</summary>
    /// <remarks>The parameter is the harness's cluster id, for a type that declares <c>RequiresCluster</c>.</remarks>
    public required Func<Guid, string> Body { get; init; }

    /// <summary>
    ///     A second valid body that differs from <see cref="Body" /> in a way the world can see.
    /// </summary>
    /// <remarks>
    ///     ⚠ Must change something the reconciler <i>applies</i>, not only something the grain stores.
    ///     The update test asserts that the change reached the cluster, and a body that differed only
    ///     in a field the reconciler ignores would pass that test while proving nothing.
    /// </remarks>
    public required Func<Guid, string> ChangedBody { get; init; }

    /// <summary>A body the type's schema must refuse, and the pointer the error must target.</summary>
    /// <remarks>
    ///     The suite asserts <see cref="ErrorCode.InvalidRequestBody" /> and that
    ///     <see cref="Error.Target" /> is <see cref="InvalidBodyTarget" /> — docs/plan/08 § Errors:
    ///     <i>"<c>target</c> is a JSON Pointer into the request body so the portal can highlight the
    ///     field."</i>
    /// </remarks>
    public required Func<Guid, string> InvalidBody { get; init; }

    /// <summary>The JSON Pointer <see cref="InvalidBody" /> must be refused at.</summary>
    public required string InvalidBodyTarget { get; init; }

    /// <summary>An action the type declares, for the POST half of the verb grammar.</summary>
    public required string ActionName { get; init; }

    /// <summary>The objects a converged resource owns in the cluster.</summary>
    /// <remarks>
    ///     The parameters are the resource's id (with its GUID resolved) and the namespace
    ///     <c>ReconcileDriver.NamespaceFor</c> derived. Empty for a clusterless provider, and the
    ///     cluster-facing half of the suite skips itself for one.
    /// </remarks>
    public required Func<ResourceId, string, ImmutableArray<ObjectRef>> Objects { get; init; }

    /// <summary>Whether an object read out of the cluster carries what a desired body asked for.</summary>
    /// <remarks>The parameters are the object's JSON and the desired body's JSON text.</remarks>
    public required Func<string, string, bool> ObjectMatchesDesired { get; init; }

    /// <summary>
    ///     The <c>CustomResourceDefinition</c>s a real API server must be serving before
    ///     <see cref="Objects" /> can be addressed at all, as YAML documents. Empty for a provider that
    ///     renders only built-in kinds.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This member exists because the cluster-backed half of the suite could not host a
    ///         real provider without it, and the first one to try was the third provider in the
    ///         tree.</b> <c>ClusterConformanceTests</c> says "to add a provider, do not touch this file
    ///         … derive one class from this one in that provider's own <c>.Cluster.Conformance</c>
    ///         project", and that was true of the reference provider and of the sample because both
    ///         render a core-group <c>ConfigMap</c>. Every service in docs/plan/12 § The catalogue
    ///         renders a <b>custom resource</b>, and a bare <c>k3s</c> serves no REST path for one:
    ///         the apply comes back <c>404</c>, which nothing maps, so all five assertions fail with a
    ///         serialization error naming <c>k8s.Autorest.HttpOperationException</c> and no status
    ///         code. Measured, not predicted — <c>CyberCloud.Cache/redis</c> went 5-of-6 red before
    ///         this existed.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It is deliberately CRDs and not "manifests".</b> A provider does not get to
    ///         install an operator, a namespace or a fixture into the suite's cluster — the point of
    ///         reading around our own code is that the world is the API server's and not the
    ///         provider's. What a provider is entitled to say is which REST paths have to exist for its
    ///         objects to be addressable, which is exactly a CRD and nothing else.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What a stub CRD does and does not prove.</b> Served, it makes the plural address a
    ///         real path, makes server-side apply real, and makes the seven labels pass real admission
    ///         — which is what the assertions here are about. It does <i>not</i> prove the rendered
    ///         spec satisfies the operator's own schema, because the CRD a provider supplies here is
    ///         the provider's own file. A case that supplied a permissive stub and called the result
    ///         "the API server accepts our manifest" would be overclaiming, so say in the CRD which one
    ///         it is.
    ///     </para>
    /// </remarks>
    public ImmutableArray<string> RequiredCrds { get; init; } = [];
}

/// <summary>
///     Supplies the case to the harness through a type parameter rather than through mutable state.
/// </summary>
/// <remarks>
///     ⚠ <b>A static abstract member, and the alternative is worse.</b> Orleans'
///     <c>TestClusterBuilder.AddSiloBuilderConfigurator&lt;T&gt;</c> constructs the configurator with
///     <c>new()</c>, so a configurator cannot be handed anything — which is why
///     <c>CyberCloud.ResourceManager.Tests</c> reaches for mutable statics. Threading the case through
///     a type parameter gives the silo the same reach with no mutable global, so two providers'
///     harnesses can exist at once without a lock and without ordering rules.
/// </remarks>
public interface IProviderCaseSource {
    /// <summary>The provider under test.</summary>
    /// <remarks>
    ///     ⚠ Not spelled <c>Case</c>: <c>CA1716</c> is an error here and <c>Case</c> is a reserved
    ///     word in other .NET languages, which the rule applies to interface members even when nothing
    ///     will ever implement this one outside C#.
    /// </remarks>
    static abstract ProviderConformanceCase ProviderCase { get; }
}
