using CyberCloud.ResourceManager.Conformance;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Immutable;

namespace CyberCloud.Conformance;

/// <summary>
///     Everything the shared suite has to be told about one provider. <b>This is the registration.</b>
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/03 § Providers:
///         <i>
///             "The conformance suite is what makes the catalogue safe to
///             grow. It is one xUnit theory that every provider must pass … A provider is not registered
///             in the platform bundle until it passes."
///         </i> The suite is parameterised on this record so
///         that adding the twentieth provider is a case object and two class declarations in that
///         provider's own <c>.Conformance</c> project — not a copy of the suite.
///     </para>
///     <para>
///         ⚠
///         <b>
///             Everything here is data or a pure function, and none of it is a hook the suite calls
///             to decide whether the provider passed.
///         </b> A case supplies bodies, addresses and the
///         objects a resource owns; the <i>assertions</i> are the suite's and are the same for every
///         provider. A case that could supply an assertion would be a provider grading its own
///         homework.
///     </para>
///     <para>
///         ⚠
///         <b>
///             <see cref="ObjectMatchesDesired" /> is read <i>around</i> the reconciler and must
///             stay that way.
///         </b> It is the ground truth of clause 4 — <c>ReconcilerConformance</c>'s
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
    ///     <i>
    ///         "<c>target</c> is a JSON Pointer into the request body so the portal can highlight the
    ///         field."
    ///     </i>
    /// </remarks>
    public required Func<Guid, string> InvalidBody { get; init; }

    /// <summary>The JSON Pointer <see cref="InvalidBody" /> must be refused at.</summary>
    public required string InvalidBodyTarget { get; init; }

    /// <summary>
    ///     An action the type declares, for the POST half of the verb grammar, or empty when the type
    ///     declares none.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             THIS WAS <c>required</c> UNTIL A TYPE WITH NO ACTIONS EXISTED, AND THE HARNESS
    ///             COULD NOT EXPRESS ONE AT ALL.
    ///         </b> Thirteen provider families had each declared at
    ///         least one action, so "every type has an action" had never been tested as an
    ///         assumption — it was simply true of the sample so far.
    ///         <c>CyberCloud.Mail/domains</c> is the first that declares none, and deliberately:
    ///         <c>actions-without-handlers.txt</c> permits a handler-less action only on an
    ///         already-published api-version, and that type's api-version is published by the change
    ///         that would have declared one.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Empty means the two POST assertions SKIP LOUDLY rather than pass quietly.</b> The
    ///         alternative — a sentinel action name nobody declares — would have made
    ///         <see cref="ProviderConformanceTests{TSource}.AnActionOnAnExistingResourceIsAccepted" />
    ///         assert a refusal for the wrong reason and report it as coverage.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>IT IS STILL <c>required</c>, AND THAT MATTERS MORE THAN IT LOOKS.</b> The first
    ///         attempt at this made the member optional with a default of <c>""</c>, and
    ///         <c>ReferenceConformance.EveryCaseFieldIsRequiredSoAPartialRegistrationDoesNotCompile</c>
    ///         went red — correctly. Its rule is that
    ///         <i>
    ///             "an optional one is an assertion the suite
    ///             quietly stops making for the provider that omits it"
    ///         </i>, and a defaulted member is
    ///         omitted by <b>every</b> case that never thinks about it, including ones that do have
    ///         an action and simply forgot. Keeping it required costs a no-action provider one
    ///         explicit <c>ActionName = string.Empty</c> — a line a reviewer can see and ask about —
    ///         and keeps the accident impossible. The relaxation was the lazy fix and the test
    ///         caught it.
    ///     </para>
    /// </remarks>
    public required string ActionName { get; init; }

    /// <summary>The objects a converged resource owns in the cluster.</summary>
    /// <remarks>
    ///     The parameters are the resource's id (with its GUID resolved) and the namespace
    ///     <c>ReconcileDriver.NamespaceFor</c> derived. Empty for a clusterless provider — which
    ///     then supplies <see cref="IProviderCaseSource.ConvergedModule" /> or <see cref="DataPlane" />
    ///     instead, and is refused by name if it supplies neither or both
    ///     (<c>ProviderConformanceTests.AClusterlessTypeSuppliesTheWorldItConvergesOnto</c>).
    ///     ⚠ The world-facing assertions read that world rather than skip; this remark said "the
    ///     cluster-facing half of the suite skips itself" for fourteen families before the first
    ///     clusterless one showed that a suite which skipped would have been green over a reconciler
    ///     that wrote nowhere. The two assertions with no clusterless analogue — an admission
    ///     refusal and a dropped connection — assert the inverse: the operation converges and
    ///     nothing reached the cluster. See <c>ClusterlessWorld</c>.
    /// </remarks>
    public required Func<ResourceId, string, ImmutableArray<ObjectRef>> Objects { get; init; }

    /// <summary>Whether an object read out of the cluster carries what a desired body asked for.</summary>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         Takes a <see cref="MatchContext" /> rather than two strings, and the address in it is
    ///         the point.
    ///     </b> See that type's remarks: without it a child's suite checks strictly less
    ///     than a parent's, and two provider families each recorded that in their own notes rather
    ///     than fixing it.
    /// </remarks>
    public required Func<MatchContext, bool> ObjectMatchesDesired { get; init; }

    /// <summary>
    ///     Objects an <b>operator</b> writes that this platform never applies, and their JSON.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>
    ///             Every managed service whose credential is generated rather than minted needs this,
    ///             and nothing in the harness could express it.
    ///         </b> A <c>listKeys</c> on
    ///         <c>CyberCloud.DBforPostgreSQL/servers</c> reads <c>{cluster}-app</c> — a Secret
    ///         CloudNativePG creates while bringing the cluster up. The reconciler does not apply it,
    ///         so <see cref="Objects" /> must not name it (that member is what the suite asserts a
    ///         converged resource <i>put</i> there, and a reconciler is judged against it). But the
    ///         fake cluster is empty except for what was applied, so without this the action reads
    ///         nothing and the handler is judged on a world its real counterpart never sees.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Placed behind the reconciler's back on purpose.</b> These arrive by the same
    ///         route <c>FakeKubeCluster.MutateBehindTheirBack</c> models — something other than this
    ///         platform wrote them — which is exactly what an operator is. A provider that placed one
    ///         through the apply path would be asserting its reconciler creates an object it does
    ///         not.
    ///     </para>
    ///     <para>
    ///         The parameters are the resource's id (with its GUID resolved) and the namespace
    ///         <c>ReconcileDriver.NamespaceFor</c> derived — the same two <see cref="Objects" />
    ///         takes.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             <c>required</c> like every other member, and it was written with a default
    ///             first.
    ///         </b> <c>SuiteRejectionTests.EveryCaseFieldIsRequiredSoAPartialRegistrationDoesNotCompile</c>
    ///         refused that within one run, and its reason applies here exactly: a member with a
    ///         default is an assertion the suite quietly stops making for the provider that omits it,
    ///         and the provider most likely to omit this one is the next one to grow an
    ///         operator-generated credential — whose handler would then be tested against a cluster
    ///         that does not contain it. Most types return an empty array, and returning it is a
    ///         statement rather than a formality.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             The claims an operator creates and OWNS belong here too, and naming the owner is
    ///             what makes the shared claims case follow them — issue #69.
    ///         </b> CloudNativePG stamps a controller reference on every
    ///         <c>PersistentVolumeClaim</c> it creates, so a soft delete that removed the
    ///         <c>Cluster</c> without detaching them would lose them to the garbage collector. A
    ///         family whose operator does that declares the claims as it would any operator-written
    ///         object, with <c>metadata.ownerReferences</c> naming the owner by <c>apiVersion</c>,
    ///         <c>kind</c> and <c>name</c> and an <b>empty</b> <c>uid</c>: the harness fills in the
    ///         uid the fake issued for the object the reconciler applied — the operator's
    ///         <c>SetAsOwnedBy</c>, done by the harness because it holds the object the operator
    ///         would read — and refuses by name when no such object was applied.
    ///         <c>ProviderConformanceTests.TheClaimsATeardownKeepsSurviveItAndTheFinalTeardownRemovesThem</c>
    ///         then asserts the claims survive the soft delete with no owner, belong to the restored
    ///         object's new uid after the restore, and are gone after the purge. A family that
    ///         declares no such claim is skipped there, and the skip says what it did not find.
    ///     </para>
    /// </remarks>
    public required Func<ResourceId, string, ImmutableArray<(ObjectRef Target, string Json)>>
        OperatorWritten { get; init; }

    // ── A data plane that is not a cluster object — docs/plan/13 § Artifact feeds ─────────────────
    //
    // ⚠ THE FIRST TYPE WHOSE DATA PLANE IS A PLATFORM HOST, AND WHAT THE HARNESS COULD NOT SEE
    // WITHOUT THESE TWO MEMBERS. Every assertion above that reads AROUND the reconciler reads a
    // cluster: it removes the objects behind the reconciler's back, reads them back, and compares
    // them to the desired body. CyberCloud.ContainerRegistry/feeds applies no object — its data
    // plane is a durable catalogue grain plus a prefix on the platform's object store — so for it
    // the harness has nothing to remove and nothing to read, and every world-facing assertion would
    // pass vacuously. The suite branches on the registry's RequiresCluster and asks these instead.
    //
    // ⚠ THE SECOND OF TWO CLUSTERLESS REGISTRATIONS, AND THE SUITE READS BOTH THROUGH ONE SHAPE.
    // IProviderCaseSource.ConvergedModule — a module in the silo, #33's Communication family — and
    // this member — a platform host's grain, #29's feeds — were written on the same day against a
    // master that had neither. Neither replaced the other: ClusterlessWorld in the harness adapts
    // whichever a case registered, and a clusterless case registers exactly one. A module has a hand
    // edit and can be put back from the body; a data plane has neither, and the suite's drift
    // assertion reads that difference off the world rather than off a second branch.
    //
    // ⚠ REQUIRED AND NULLABLE, WHICH IS A SHAPE NO MEMBER ABOVE HAS, AND THE SUITE REFUSES THE
    // COMBINATION THAT WOULD MAKE IT A LOOPHOLE. Required, because ReferenceConformance
    // .EveryCaseFieldIsRequiredSoAPartialRegistrationDoesNotCompile holds that an optional member is
    // an assertion the suite quietly stops making — so every cluster-backed case writes
    // `DataPlane = null, StoragePrefix = null` and says so in a line beside it, and a case that
    // forgets does not compile. Nullable, because for a type that declares RequiresCluster the
    // cluster IS its world and there is nothing to describe, and for a type whose world is a module
    // the module is. A type that supplies a DataPlane MUST supply a StoragePrefix too, and the suite
    // fails one that leaves it null with a message naming the member. So a provider cannot escape
    // the world-facing assertions by declining a cluster: it trades one world the harness can reach
    // for one it has to describe.

    /// <summary>
    ///     For a type with no cluster data plane: the world the four-clause check breaks and reads
    ///     around the reconciler, built over the harness's grain factory and the resource's address.
    ///     <see langword="null" /> for every type whose data plane is cluster objects.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The nearest thing in this record to a hook, and what keeps it from being one.</b>
    ///     <c>BreakAsync</c> mutates the data plane behind the reconciler's back — the catalogue
    ///     grain, reached through <c>ForTenant</c> like the harness reaches every other grain — and
    ///     <c>MatchesDesiredAsync</c> reads it back. Neither decides whether the provider passed: the
    ///     suite runs the reconciler after the break and fails it if it reports <c>Converged</c> over
    ///     a world that does not match, which is clause 4 exactly as the cluster branch checks it. A
    ///     case whose break did nothing would be caught by the suite asserting that
    ///     <c>MatchesDesiredAsync</c> is <i>false</i> after the break and before the pass.
    /// </remarks>
    public required Func<IGrainFactory, ResourceId, ConformanceWorld>? DataPlane { get; init; }

    /// <summary>
    ///     For a type that keeps bytes on the platform's object store: the prefix under which one
    ///     resource's bytes live, and nothing else's. <see langword="null" /> for a type that keeps none.
    /// </summary>
    /// <remarks>
    ///     The suite plants an object under it after a create converges and asserts the teardown
    ///     removed it — the object-store half of "delete tears down the data plane", which the
    ///     cluster branch asserts by reading the fake API server.
    /// </remarks>
    public required Func<ResourceId, string>? StoragePrefix { get; init; }

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
    // ⚠ WHAT NEITHER DESIGN BOUGHT, AND WHAT ISSUE #91 ADDED WITHOUT ADDING A MEMBER. The declared
    // version looked stronger because a provider could supply the operator's real CRD. In practice it
    // did not: the one provider that used it supplied a hand-written stub with
    // `x-kubernetes-preserve-unknown-fields` and said so in its own remarks. A stub — derived or
    // written — makes the plural address a real path, makes server-side apply real, and makes the
    // seven labels pass real admission. It does NOT prove the rendered spec satisfies the operator's
    // schema, and for a month nothing in this repository did: charts/managed/seaweedfs-bucket rendered
    // three fields in a shape the real definition refuses, under twenty-eight green assertions.
    //
    // What checks it now is derived the same way this member was refused for not being: `Objects`
    // still supplies group, version, kind, plural and scope; charts/bundle/crds.sh derives the SET of
    // kinds from charts/managed/*/templates/ and commits the real definition of each from the release
    // the component pins; FakeKubeCluster validates every apply against the committed definition and
    // ClusterConformanceHarness installs it into k3s in place of the stub; and
    // ProviderConformanceTests.EveryCustomKindTheCaseRendersHasACommittedDefinition refuses a case
    // whose Objects name a kind with no definition — or whose reconciler applies one they do not.
    // A provider still declares nothing it could under-declare. The floor is still
    // underivable-from-nothing, and the ceiling is the operator's.
    //
    // ⚠ AND THE BODIES ARE DERIVED THE SAME WAY, for the same reason. `Body` is one body, and the
    // review of #91 found the shape one flag away from it refused by the definition every suite
    // was green against. A `Variants` member here would be a second list to under-declare, so
    // ProviderConformanceTests.EveryPropertyVariantTheSchemaAdmitsRendersAShapeTheDefinitionAdmits
    // reads the variants off the type's own schema instead — PropertyVariants' remarks say what.
}

/// <summary>
///     One object read out of the cluster, the body it should carry, and
///     <b>
///         the address it was
///         rendered for
///     </b>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             This type exists for <see cref="Id" />, and everything else on it was already
///             reachable.
///         </b> <see cref="ProviderConformanceCase.ObjectMatchesDesired" /> used to be
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
///         ⚠
///         <b>
///             A record rather than more positional parameters, and that is what makes the next
///             member cheap.
///         </b> The harness constructs this and a case only reads it, so adding a member
///         later touches <c>ProviderConformanceTests</c>, <c>ClusterConformanceTests</c> and
///         <c>SiloKillConformanceTests</c> — and nothing in the fourteen provider families. A third
///         positional parameter would have touched all fourteen again, which is the cost this change
///         paid once and should not pay twice. Two adjacent positional strings that were both JSON
///         were also one transposition away from a suite that passed for the wrong reason.
///     </para>
///     <para>
///         ⚠
///         <b>
///             Every member is <c>required</c>, for the reason
///             <see cref="ProviderConformanceCase" />'s are.
///         </b> Here the rule points the other way — an
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
    ///         ⚠
    ///         <b>
    ///             It is here, on the source, rather than a member of
    ///             <see cref="ProviderConformanceCase" />, and that is the whole design.
    ///         </b> Every member
    ///         of that record is <c>required</c> on purpose and
    ///         <c>SuiteRejectionTests.EveryCaseFieldIsRequiredSoAPartialRegistrationDoesNotCompile</c>
    ///         enforces it —
    ///         <i>
    ///             "an optional member is an assertion the suite quietly stops making for
    ///             the provider that leaves it out"
    ///         </i>, which a nullable <c>ParentCase</c> would be
    ///         exactly. A <c>required</c> one would be no better from the other side: the four
    ///         providers that ship today have no child, and making them each write
    ///         <c>Ancestors = []</c> would put the cost of the first child type on every provider that
    ///         does not have one.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             A <c>static virtual</c> with a default is an optional member, and the reason it is
    ///             not the failure that rule describes is that omitting it is not silent.
    ///         </b> The rule's
    ///         objection is to an assertion that <i>stops being made</i>. Nothing stops here: a
    ///         depth-1 case has no ancestors to describe, and a depth-2 case that leaves this empty
    ///         does not run a smaller suite — it runs no suite at all.
    ///         <c>ProviderTestCluster.AncestorsOf</c> refuses the mismatch by name before a single
    ///         test does anything, and
    ///         <c>SuiteRejectionTests.ADepthTwoSourceWithNoAncestorsIsRefusedByNameRatherThanFailingEveryTestAtOnce</c>
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

    /// <summary>
    ///     Resources the type under test <b>relates to</b> without descending from — the second
    ///     virtual network a peering joins to its parent — created once per harness before the first
    ///     assertion, beside the ancestor chain. Empty for every type before
    ///     <c>CyberCloud.Network/virtualNetworks/peerings</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             WHY THIS EXISTS, AND WHY <see cref="Ancestors" /> COULD NOT CARRY IT.
    ///         </b> Issue #89: the shared harness builds the parent chain and nothing else, so a
    ///         peering — a child of network A whose body names network B — had no B to converge onto
    ///         and could not be given a case at all. An ancestor is a resource the type's <i>address</i>
    ///         interleaves and the harness derives the whole chain from <c>ResourceTypeName.Depth</c>;
    ///         a sibling is named by the type's <i>body</i>, and nothing about the address says how
    ///         many there are or what they are called. So the case source says: which case describes
    ///         the sibling (its type, api-version and a body its schema accepts, composed rather than
    ///         restated for the reason <see cref="Ancestors" /> gives) and what the harness should
    ///         call it. The body the case supplies then names <see cref="SiblingResource.Name" />.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Where a sibling lives.</b> Under the harness's own ancestor names, at the sibling
    ///         type's depth: a <c>virtualNetworks</c> sibling of a <c>virtualNetworks/peerings</c> case
    ///         sits at the root beside <c>ancestor-0</c>; a <c>probes/samples</c> sibling of a
    ///         <c>probes/samples</c> case sits under <c>ancestor-0</c> beside the samples the suite
    ///         creates. <c>ProviderTestCluster.SiblingAddress</c> is the one place that arithmetic is
    ///         done, and <c>ProviderTestCluster.Siblings</c> refuses, by member name and before the
    ///         first assertion, a sibling from another provider, one nested deeper than the case's
    ///         ancestor chain reaches, one whose ancestor types are not the case's own, and one named
    ///         like the ancestor at its level.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             Here, on the source, with a default, for exactly the reason <see cref="Ancestors" />
    ///             is.
    ///         </b> Omitting it is not silent: a type whose body names a sibling that was never
    ///         created never converges, and the first convergence assertion fails with the reconciler's
    ///         own words. Nothing stops being asserted.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             The sibling's objects survive a reset, and until the first co-writing type they
    ///             did not.
    ///         </b> <c>ConformanceState.Reset</c> used to empty the fake cluster between
    ///         assertions, so only the sibling <i>resource</i> — its grain, its index binding, its
    ///         <c>Succeeded</c> state — persisted and its objects were gone. A type that co-writes
    ///         onto a sibling's object needs that object there when a test starts, so the harness now
    ///         takes a baseline of the world once the ancestors, siblings, and
    ///         <see cref="Companions" /> have all converged (<c>FakeKubeCluster.Baseline</c>) and
    ///         every reset restores it.
    ///         <c>ReferenceSiblingProviderConformance.TheSiblingSurvivesAResetAsAResourceAndAsObjects</c>
    ///         pins both halves, and that a test's own objects still do not survive.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             A sibling is the case's own provider's; a resource of another provider is a
    ///             <see cref="Companions" /> entry.
    ///         </b> The two are the same idea — a resource the body
    ///         names, created before the first assertion, kept through every reset — and differ in
    ///         what the harness has to do for them: a sibling is addressed under the harness's own
    ///         ancestors and driven to <c>Succeeded</c>; a companion is top-level, brings a second
    ///         provider into the run's registry, and may carry a body of its own.
    ///     </para>
    /// </remarks>
    static virtual ImmutableArray<SiblingResource> Siblings => [];

    /// <summary>
    ///     The module a <b>clusterless</b> type converges onto, or <see langword="null" /> for a type
    ///     that converges Kubernetes objects — which is every type before
    ///     <c>CyberCloud.Communication/services</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             A <c>static virtual</c> with a default, for the reason <see cref="Ancestors" />
    ///             is one: omitting it is not silent.
    ///         </b> A case whose type declares no
    ///         <c>RequiresCluster</c> and supplies neither a module nor a
    ///         <see cref="ProviderConformanceCase.DataPlane" /> does not run a smaller suite — it is
    ///         refused by name before its first assertion, because a clusterless run with nothing
    ///         to read would be green over a reconciler that wrote nowhere. A case that supplies
    ///         a module for a type that <i>does</i> declare <c>RequiresCluster</c> is refused too,
    ///         and so is one that supplies both a module and a data plane: one world per type, and
    ///         the registration says which.
    ///         <c>ProviderConformanceTests.AClusterlessTypeSuppliesTheWorldItConvergesOnto</c> is
    ///         the calibration.
    ///     </para>
    ///     <para>
    ///         See <see cref="IConvergedModule" /> for what the module answers and what a clusterless
    ///         run still cannot say, and <c>ClusterlessWorld</c> for how the suite reads a module and
    ///         a data plane through one shape.
    ///     </para>
    /// </remarks>
    static virtual IConvergedModule? ConvergedModule => null;

    /// <summary>
    ///     What the harness silo must hold beyond the harness's own doubles for this case's provider
    ///     to be <i>constructible</i> — the seams a sibling type's action handlers take in their
    ///     constructors. Nothing, for every case before <c>CyberCloud.Monitor/workspaces</c>.
    /// </summary>
    /// <param name="silo">The harness silo being built.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             WHY THIS EXISTS, AND WHY <see cref="ConvergedModule" /> COULD NOT CARRY IT.
    ///         </b> The harness registers <i>every</i> action handler the case's provider declares
    ///         into the silo container, by concrete type, because a long-running action is driven
    ///         inside the silo and the registry stores a type — and the silo's host validates that
    ///         container on build. Fifteen families' handlers took no constructor argument the
    ///         harness did not already register; <c>CyberCloud.Monitor/workspaces/alertRules</c>'
    ///         <c>listInstances</c> takes <c>IAlertControlPlane</c>, and that type is a sibling of
    ///         the workspace under one provider. So the <b>workspace's</b> suite — a cluster-backed
    ///         case, refused a module by name — failed at fixture start with a DI validation error
    ///         naming a handler it never invokes. A module is one world per type; this is the wiring
    ///         a family's <i>application module</i> would do in a host, and the harness has no
    ///         application module.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A <c>static virtual</c> with an empty default, and omitting it is not silent.</b>
    ///         A case whose provider declares a handler with an unmet dependency fails every test in
    ///         the class at fixture start, with the handler and the missing service named — which is
    ///         exactly the failure that produced this member. Wiring only: nothing here decides
    ///         whether the provider passed, which is the line <see cref="ProviderConformanceCase" />'s
    ///         remarks draw.
    ///     </para>
    /// </remarks>
    static virtual void ConfigureSilo(ISiloBuilder silo) { }

    /// <summary>
    ///     What the <i>dispatcher's</i> container must hold for this case's synchronous handlers to
    ///     reach the world a suite built for them — the client-side half of
    ///     <see cref="ConfigureSilo" />. Nothing, for every case before
    ///     <c>CyberCloud.Monitor/workspaces/components</c>.
    /// </summary>
    /// <param name="services">The container <c>ActionDispatcher</c> resolves handlers from.</param>
    /// <remarks>
    ///     ⚠ <b>A synchronous action runs inside <c>ResourceManagerService</c>, not in the silo</b>,
    ///     so what a handler holds is registered in the container the harness builds for the
    ///     dispatcher — the gateway's, in production. A clusterless case gets there through
    ///     <see cref="IConvergedModule.ConfigureHandlers" />; a cluster-backed case whose views read a
    ///     store the suite started (the component's ClickHouse) gets there through this. Registered
    ///     after the harness's own doubles, so a case can replace one.
    /// </remarks>
    static virtual void ConfigureHandlers(IServiceCollection services) { }

    /// <summary>
    ///     Resources of <b>other</b> providers that must exist before this case's own resource can
    ///     converge — what a backup vault protects. Nothing, for every case before
    ///     <c>CyberCloud.RecoveryServices/vaults</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             WHY THIS EXISTS, AND WHY NEITHER <see cref="Ancestors" /> NOR
    ///             <see cref="ProviderConformanceCase.OperatorWritten" /> COULD CARRY IT.
    ///         </b> The suite registers ONE provider, and until the vault that was a fact about every
    ///         type rather than a limit: a nested type and its parent are one provider by
    ///         construction, and an operator-written object is placed behind the reconciler's back
    ///         into the fake cluster. A vault's reconciler does neither. It hands a protected item's
    ///         path to <c>ReconcileContext.View</c>, and the view answers <c>ResourceNotFound</c> for a
    ///         type the silo's registry does not serve and for a path the tenant's index has never
    ///         bound — so a planted <c>Cluster</c> object is invisible to it, and a harness that
    ///         registered the vault alone would refuse every item and fail every create. A companion
    ///         is a real resource of a real second provider, created through the same write path as
    ///         an ancestor, whose objects the companion's <i>own</i> reconciler applied.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Composed rather than restated, for the reason <see cref="Ancestors" /> is</b>: the
    ///         companion's <see cref="ProviderConformanceCase" /> is the other family's own case object,
    ///         referenced from its <c>.Conformance</c> project. A <c>.Conformance</c> project is a test
    ///         project, which docs/plan/03 § Assembly graph rules excludes from rule 2 by construction —
    ///         the shipping vault assembly still names nothing of the other family's.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             A companion's objects survive the per-test <c>Reset</c>, by the same baseline a
    ///             sibling's do.
    ///         </b> Every assertion puts the fake cluster back to the fixture's world
    ///         before it runs, and a vault whose companion's <c>Cluster</c> vanished would refuse the
    ///         item on every assertion after the first. The harness takes that baseline once, after
    ///         the ancestors, <see cref="Siblings" />, and companions have all converged — the
    ///         companions last, being top-level and dependent on nothing the harness made — and
    ///         <c>Reset</c> restores it; see <c>FakeKubeCluster.Baseline</c>. Nothing about the
    ///         case's own objects is kept.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A <c>static virtual</c> with an empty default, and omitting it is not silent.</b> A
    ///         vault case that left this empty does not run a smaller suite; every convergence
    ///         assertion fails with the reconciler's own refusal naming the item's pointer. Depth-1
    ///         companions only: a companion with ancestors of its own is refused by name in
    ///         <c>ProviderTestCluster.Companions</c>, because nothing has needed one and a
    ///         half-built ancestor chain would fail as a 404 naming the wrong path.
    ///     </para>
    /// </remarks>
    static virtual ImmutableArray<CompanionCase> Companions => [];
}

/// <summary>
///     One resource of another provider the harness creates before the case under test runs — see
///     <see cref="IProviderCaseSource.Companions" />.
/// </summary>
public sealed record CompanionCase {
    /// <summary>The other family's own case object.</summary>
    public required ProviderConformanceCase ProviderCase { get; init; }

    /// <summary>The name the harness creates it under. DNS-1123, per docs/plan/06 § Identifiers.</summary>
    public required string Name { get; init; }

    /// <summary>
    ///     The body the harness creates the companion with, or <see langword="null" /> for the other
    ///     family's own <see cref="ProviderConformanceCase.Body" />.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Exists because a lane against a real operator needs a companion sized for it.</b> It was
    ///     added when <c>PostgresServers.Body</c> rendered a backup section CloudNativePG's webhook
    ///     refused for <i>"missing credentials"</i> (<c>charts/managed/postgres/conformance.yaml § owed</c>,
    ///     <c>the-default-bucket-is-not-filled-in</c>, closed by #30); the vault's CloudNativePG lane
    ///     now uses it for one instance, no pooler and a one-gibibyte volume — a server a k3s in Docker
    ///     brings up in minutes. The override is a function of the same cluster id, so the two bodies
    ///     differ in what the lane says and nothing else.
    /// </remarks>
    public Func<Guid, string>? Body { get; init; }

    /// <summary>The body the harness writes: <see cref="Body" /> when set, the family's own otherwise.</summary>
    /// <param name="clusterId">The harness's cluster.</param>
    public string BodyFor(Guid clusterId) => (Body ?? ProviderCase.Body)(clusterId);

    /// <summary>The address the harness creates the companion at, in the run's tenant and subscription.</summary>
    /// <param name="tenant">The tenant, defaulting to <see cref="ConformanceIds.Tenant" />.</param>
    /// <param name="subscription">The subscription, defaulting to <see cref="ConformanceIds.Subscription" />.</param>
    /// <remarks>
    ///     ⚠ A pure function of the ids the harness fixes, so a case's <c>Body</c> can name the
    ///     companion's path without being handed anything: <c>Body</c> takes a cluster id and nothing
    ///     else, and widening it would touch sixteen families for one.
    /// </remarks>
    public ResourceId Address(Guid? tenant = null, Guid? subscription = null) =>
        new(
            tenant ?? ConformanceIds.Tenant,
            subscription ?? ConformanceIds.Subscription,
            ConformanceIds.ResourceGroup,
            ProviderCase.Type,
            Name,
            Guid.Empty
        );
}

/// <summary>
///     One resource the harness creates beside the ancestor chain because the type under test
///     relates to it — <see cref="IProviderCaseSource.Siblings" />.
/// </summary>
/// <remarks>
///     ⚠ Both members <c>required</c>, for the reason every member of
///     <see cref="ProviderConformanceCase" /> is: a sibling with no case has no body the harness can
///     create, and one with no name is one the case's body cannot name.
/// </remarks>
public sealed record SiblingResource {
    /// <summary>
    ///     The sibling type's own conformance case — its type, its api-version, and a body its schema
    ///     accepts. Composed from the sibling type's <c>.Conformance</c> registration rather than
    ///     written again here, so there is one description of that type's valid body.
    /// </summary>
    public required ProviderConformanceCase Case { get; init; }

    /// <summary>
    ///     What the harness calls it — the name the case's body then refers to. DNS-1123, per
    ///     docs/plan/06 § Identifiers, and never the harness's ancestor name at the same level.
    /// </summary>
    public required string Name { get; init; }
}
