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
    /// <remarks>
    ///     ⚠ <b>Takes a <see cref="MatchContext" /> rather than two strings, and the address in it is
    ///     the point.</b> See that type's remarks: without it a child's suite checks strictly less
    ///     than a parent's, and two provider families each recorded that in their own notes rather
    ///     than fixing it.
    /// </remarks>
    public required Func<MatchContext, bool> ObjectMatchesDesired { get; init; }

    // ── There is deliberately NO RequiredCrds member, and the reason is worth keeping ─────────────
    //
    // A bare k3s serves no REST path for a custom resource, so the cluster-backed half of the suite
    // has to install one before it can address anything a real provider renders. Two designs were
    // built for that, independently and within hours of each other, and this is the one that won:
    // ClusterConformanceHarness DERIVES the CRDs from `Objects` above, which already carries every
    // fact a stub needs — group, version, kind and plural.
    //
    // The rejected design was a `required ImmutableArray<string> RequiredCrds` holding CRD YAML. It
    // is worse for one reason that outweighs everything else: a provider can under-declare it, and
    // the failure when it does is the worst message in the suite — every assertion fails with
    // `k8s.Autorest.HttpOperationException` and NO STATUS CODE, because a 404 for an unserved kind
    // was, at the time, mapped by nothing. Measured twice, not predicted: two providers each went
    // 5-of-6 red before their CRDs existed. `Objects` cannot be under-declared, because the suite
    // fails immediately and legibly without it.
    //
    // ⚠ WHAT NEITHER DESIGN BUYS. The declared version looked stronger because a provider could
    // supply the operator's real CRD. In practice it did not: the one provider that used it supplied
    // a hand-written stub with `x-kubernetes-preserve-unknown-fields` and said so in its own remarks.
    // A stub — derived or written — makes the plural address a real path, makes server-side apply
    // real, and makes the seven labels pass real admission. It does NOT prove the rendered spec
    // satisfies the operator's schema. Nothing in this repository checks that today; each chart's
    // SOURCE file records a review date, which is the weaker claim and is labelled as one.
    //
    // If a provider ever needs the operator's real CRD — to assert its manifest validates rather than
    // merely applies — add the member back as a SUPPLEMENT to the derivation, never as a replacement
    // for it. The floor must stay underivable-from-nothing.
}

/// <summary>
///     One object read out of the cluster, the body it should carry, and <b>the address it was
///     rendered for</b>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This type exists for <see cref="Id" />, and everything else on it was already
///         reachable.</b> <see cref="ProviderConformanceCase.ObjectMatchesDesired" /> used to be
///         <c>(objectJson, desiredJson) =&gt; bool</c>. That is enough for a top-level type, whose
///         whole rendered spec is a function of its body, and it is <i>not</i> enough for a child: a
///         bucket's <c>spec.name</c> is its own name and its <c>spec.clusterRef</c> is its parent's,
///         both of which live in the address and neither of which was reachable. So a child's suite
///         checked strictly less than a parent's — it could not tell which parent's object it was
///         looking at, and could not catch two accounts in one resource group each holding a bucket
///         called <c>assets</c>. Recorded first by <c>CyberCloud.Storage/accounts/buckets</c> and
///         again by the Network family before it was fixed.
///     </para>
///     <para>
///         ⚠ <b>A record rather than more positional parameters, and that is what makes the next
///         member cheap.</b> The harness constructs this and a case only reads it, so adding a member
///         later touches <c>ProviderConformanceTests</c>, <c>ClusterConformanceTests</c> and
///         <c>SiloKillConformanceTests</c> — and nothing in the fourteen provider families. A third
///         positional parameter would have touched all fourteen again, which is the cost this change
///         paid once and should not pay twice. Two adjacent positional strings that were both JSON
///         were also one transposition away from a suite that passed for the wrong reason.
///     </para>
///     <para>
///         ⚠ <b>Every member is <c>required</c>, for the reason
///         <see cref="ProviderConformanceCase" />'s are.</b> Here the rule points the other way — an
///         optional member would be something the <i>harness</i> quietly stops telling the case, and
///         a case cannot assert on a fact it was not given.
///         <c>SuiteRejectionTests.EveryCaseFieldIsRequiredSoAPartialRegistrationDoesNotCompile</c>
///         reads this type as well as the case.
///     </para>
/// </remarks>
public sealed record MatchContext {
    /// <summary>The object as the cluster holds it.</summary>
    public required string ObjectJson { get; init; }

    /// <summary>The desired body the object was rendered from, as JSON text.</summary>
    public required string DesiredJson { get; init; }

    /// <summary>
    ///     The resource the object was rendered for, with its GUID resolved.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b><c>ParentNames</c> is the half a child needs and the half nothing else carries.</b>
    ///     A child's rendered spec points at its parent by name, and that name is in the address and
    ///     in no body — <c>ResourceId.ParentNames</c> holds the ancestors outermost first. For a
    ///     top-level type it is empty and this member is usually unread, which is correct rather than
    ///     wasteful: the suite hands over what it knows, and each case decides what its own type's
    ///     spec is a function of.
    /// </remarks>
    public required ResourceId Id { get; init; }

    /// <summary>Which of the resource's objects this is.</summary>
    /// <remarks>
    ///     A case that renders several kinds can dispatch on <c>Target.Kind</c> rather than sniffing
    ///     <c>kind</c> out of <see cref="ObjectJson" />.
    /// </remarks>
    public required ObjectRef Target { get; init; }

    /// <summary>
    ///     The namespace <c>ReconcileDriver.NamespaceFor</c> derived — the same one
    ///     <see cref="ProviderConformanceCase.Objects" /> was handed.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Carried in its own right, and <c>Target.Namespace</c> is NOT a substitute for it.</b>
    ///     A cluster-scoped object has no namespace, so <c>ObjectRef.Namespace</c> is deliberately the
    ///     empty string for one — <c>NetworkSubnets.SubnetRef</c> sets exactly that, because a
    ///     kube-ovn <c>Subnet</c> is cluster-scoped. But the namespace is still a <i>name component</i>
    ///     of what such an object renders: a subnet's <c>spec.vpc</c> is
    ///     <c>VirtualNetworks.ObjectNameOf(ns, parent)</c>, which needs the derived namespace and not
    ///     the object's own. Reading it off <see cref="Target" /> gave the empty string and turned
    ///     five of that case's assertions red, which is how this member came to exist.
    /// </remarks>
    public required string Namespace { get; init; }
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

    /// <summary>
    ///     The cases of <see cref="ProviderCase" />'s ancestors, <b>outermost first</b>. Empty for a
    ///     top-level type; one entry for a <c>servers/databases</c>; two at depth 3.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A child cannot be created until its parent exists</b> —
    ///         <c>ResourceManagerService.ResolveAsync</c> reads the parent's index binding on every
    ///         create and answers the same 404 as "no such resource" when it is absent. So a
    ///         conformance run for a child type has to bring a parent into being before its first
    ///         assertion, and the harness needs three things to do that: the parent's type, its
    ///         api-version and a body its schema accepts. The first is a pure function of
    ///         <see cref="ProviderConformanceCase.Type" />; the other two are not derivable from
    ///         anything, and a body synthesised from the parent's <c>ResourceSchema</c> would be the
    ///         harness guessing at a provider's own validation rules.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It is here, on the source, rather than a member of
    ///         <see cref="ProviderConformanceCase" />, and that is the whole design.</b> Every member
    ///         of that record is <c>required</c> on purpose and
    ///         <c>SuiteRejectionTests.EveryCaseFieldIsRequiredSoAPartialRegistrationDoesNotCompile</c>
    ///         enforces it — <i>"an optional member is an assertion the suite quietly stops making for
    ///         the provider that leaves it out"</i>, which a nullable <c>ParentCase</c> would be
    ///         exactly. A <c>required</c> one would be no better from the other side: the four
    ///         providers that ship today have no child, and making them each write
    ///         <c>Ancestors = []</c> would put the cost of the first child type on every provider that
    ///         does not have one.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A <c>static virtual</c> with a default is an optional member, and the reason it is
    ///         not the failure that rule describes is that omitting it is not silent.</b> The rule's
    ///         objection is to an assertion that <i>stops being made</i>. Nothing stops here: a
    ///         depth-1 case has no ancestors to describe, and a depth-2 case that leaves this empty
    ///         does not run a smaller suite — it runs no suite at all.
    ///         <c>ProviderTestCluster.AncestorsOf</c> refuses the mismatch by name before a single
    ///         test does anything, and <c>SuiteRejectionTests.ADepthTwoSourceWithNoAncestorsIsRefusedByNameRatherThanFailingEveryTestAtOnce</c>
    ///         is the calibration that says so. The count is checked against
    ///         <c>ResourceTypeName.Depth</c>, which is derived from the type path and cannot be
    ///         under-declared any more than <see cref="ProviderConformanceCase.Objects" /> can.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Composed rather than restated.</b> A provider that ships <c>servers/databases</c>
    ///         must also ship <c>servers</c>, and <c>src/Providers/README.md</c> makes <c>servers</c>
    ///         unregisterable until it has a conformance case of its own — so the object this member
    ///         wants already exists in that provider's <c>.Conformance</c> project and is reused. A
    ///         second description of the parent, written for the child's benefit, would be a second
    ///         thing to keep in step with the parent's schema.
    ///     </para>
    /// </remarks>
    static virtual ImmutableArray<ProviderConformanceCase> Ancestors => [];
}
