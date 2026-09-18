using CyberCloud.Conformance.Harness;
using CyberCloud.ResourceManager;
using CyberCloud.ResourceManager.Conformance;
using CyberCloud.ResourceManager.Reconcile;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Conformance;

/// <summary>
///     The shared provider suite, docs/plan/03 § Providers, Docker-free half.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/03 § Providers, in full:
///         <i>
///             "create → 202 → poll → Succeeded → read back → tag → lock → delete → gone; create with
///             tenant B's ids → 404; delete while an operation is running → 409; reconcile after a
///             manual cluster mutation → drift corrected; kill the silo mid-create → resource still
///             converges. A provider is not registered in the platform bundle until it passes."
///         </i>
///     </para>
///     <para>
///         Four of those five run here.
///         <b>
///             The fifth — killing a silo mid-create — does not, and it
///             is not absent either
///         </b>: an in-process <c>TestCluster</c> over in-memory storage cannot
///         tell "the silo died and the durable state brought it back" from "a grain deactivated and
///         reactivated over the same dictionary", so running it here would assert something weaker
///         under the right name. It runs against real PostgreSQL and a real Redis reminder table in
///         <c>CyberCloud.Cluster.Conformance</c>, together with the cluster-backed halves of the
///         other four; <see cref="ClusterBackedConformanceTests" /> is the signpost this suite keeps
///         so that a Docker-free run still says where they are.
///     </para>
///     <para>
///         ⚠ <b>To add a provider, do not touch this file.</b> Write a
///         <see cref="ProviderConformanceCase" />, declare an <see cref="IProviderCaseSource" /> for
///         it, and derive one class from this one. That is the registration, and its shape is the
///         whole reason the suite is parameterised.
///     </para>
/// </remarks>
/// <typeparam name="TSource">The provider under test.</typeparam>
/// <param name="cluster">The harness.</param>
public abstract class ProviderConformanceTests<TSource>(ProviderTestCluster<TSource> cluster)
    where TSource : IProviderCaseSource {
    /// <summary>The provider under test.</summary>
    protected static ProviderConformanceCase Case => TSource.ProviderCase;

    /// <summary>The harness.</summary>
    protected ProviderTestCluster<TSource> Cluster { get; } = cluster;

    // ── Which world this type lives in ───────────────────────────────────────────────────────────
    //
    // ⚠ THE SUITE BRANCHES ON THE REGISTRY, NEVER ON THE CASE. A type declares RequiresCluster in
    // its Describe, and that declaration is what ReconcileDriver reads to decide whether a pass gets
    // a connection and a namespace at all. So it is the one fact both the driver and this suite can
    // agree on without either trusting the case — and a case that supplied a clusterless world for
    // a type that declares a cluster, or none for a type that does not, is caught by name in
    // AClusterlessTypeSuppliesTheWorldItConvergesOnto rather than silently choosing its own branch.
    //
    // ⚠ A CLUSTERLESS TYPE HAS ONE OF TWO REGISTRATIONS AND THIS SUITE HAS ONE BRANCH FOR BOTH. Two
    // branches merged on the same day each taught this file about "the first clusterless type":
    // #33's Communication family registers an IConvergedModule on its case source, #29's feeds type
    // a DataPlane and a StoragePrefix on its case. Every world-facing assertion below reads a
    // ClusterlessWorld — the harness's one shape over both — and the two facts that genuinely differ
    // between them (whether a pass can rebuild the world from the body; whether a hand edit exists)
    // are data on that shape rather than a second `if` in every method. ClusterlessWorld's remarks
    // carry the argument; ClusterlessWorldOf is where a case that registered neither, or both, is
    // refused by member name.

    /// <summary>
    ///     Whether the type under test applies objects to a cluster — the registry's own
    ///     <c>RequiresCluster</c>, which is what the driver reads.
    /// </summary>
    protected bool HasClusterDataPlane {
        get {
            Cluster.Registry.TryGetType(Case.Type, out var registration).ShouldBeTrue();
            return registration.RequiresCluster;
        }
    }

    /// <summary>
    ///     The world of a clusterless type, for one resource — the module the case source registered
    ///     or the data plane the case built over the harness's grain factory — and the refusal, by
    ///     member name, of a clusterless case that described neither.
    /// </summary>
    /// <param name="address">The resource, with its GUID resolved.</param>
    protected ClusterlessWorld ClusterlessWorldOf(ResourceId address) {
        HasClusterDataPlane.ShouldBeFalse("a type that declares RequiresCluster has the cluster for its world");

        if (ProviderTestCluster<TSource>.Module is { } module) {
            return ClusterlessWorld.Over(module, address, Case.StoragePrefix?.Invoke(address));
        }

        Case.DataPlane.ShouldNotBeNull(
            $"{Case.DisplayName} declares no RequiresCluster and supplies neither a module nor a "
            + "DataPlane. A type with no cluster has to tell the suite how its world is read and "
            + "broken, or every world-facing assertion here passes over nothing — set "
            + "IProviderCaseSource.ConvergedModule for a world in the silo, or "
            + "ProviderConformanceCase.DataPlane for one on a platform host."
        );

        Case.StoragePrefix.ShouldNotBeNull(
            $"{Case.DisplayName} declares no RequiresCluster, supplies a DataPlane and supplies no "
            + "StoragePrefix. The teardown assertion for a type whose bytes live on the platform's "
            + "object store is that the prefix is empty afterwards, and a case that names no prefix "
            + "asks the suite to assert nothing — see ProviderConformanceCase.StoragePrefix."
        );

        return ClusterlessWorld.Over(Case.DataPlane(Cluster.Grains, address), Case.StoragePrefix(address));
    }

    /// <summary>
    ///     Asserts that a pass of a clusterless type applied nothing to any cluster — the other
    ///     direction of <c>RequiresCluster</c>, and the only cluster-facing assertion such a type has.
    /// </summary>
    protected void AssertNothingReachedTheCluster() =>
        Cluster.World.Applied.ShouldBeEmpty(
            $"{Case.DisplayName} declares no RequiresCluster and a pass still applied "
            + $"{Cluster.World.Applied.Count.ToString(CultureInfo.InvariantCulture)} object(s) to the "
            + "fake API server. A reconciler that reaches a cluster it never declared is one the "
            + "driver hands a null connection to in production, which dereferences on the first pass."
        );

    // ── The registry, which is the platform's whole description of this provider ────────────────

    [Fact]
    public void TheTypeIsRegisteredWithAReconcilerAndAllThreePermissions() {
        // A provider whose Describe returned early, or whose reconciler names a type nobody declared,
        // is a provider whose endpoints answer 404 with nothing in the log. ProviderRegistry.Build
        // throws on most of that; this asserts the parts it cannot.
        Cluster.Registry.TryGetType(Case.Type, out var registration)
            .ShouldBeTrue($"'{Case.Type}' is not in the registry built from {Case.DisplayName}'s own Describe.");

        registration.ReconcilerType.ShouldBe(
            Case.ReconcilerType,
            "the registry's reconciler and the case's must be the same type — the driver resolves the "
            + "registry's, and a case naming a different one would test a reconciler nothing runs"
        );

        registration.ReadPermission.ShouldNotBeNullOrWhiteSpace(
            "docs/plan/08 § The provider registry: the read permission is what decides 404 versus 403, "
            + "so a type without one has to answer 404 to everyone, including its owner"
        );

        registration.WritePermission.ShouldNotBeNullOrWhiteSpace();
        registration.DeletePermission.ShouldNotBeNullOrWhiteSpace();
        registration.ApiVersions.ShouldNotBeEmpty();
    }

    [Fact]
    public void AClusterlessTypeSuppliesTheWorldItConvergesOnto() {
        // ⚠ THE CALIBRATION FOR BOTH CLUSTERLESS REGISTRATIONS — IProviderCaseSource.ConvergedModule
        // and ProviderConformanceCase.DataPlane — and it runs for every case rather than only the
        // clusterless ones. The failures it exists for: a cluster-backed case that names a world
        // beside its cluster, a clusterless case that names none, and a clusterless case that names
        // both. Each is a case with assertions reading a world its reconciler does not write to. See
        // ClusterlessWorld for why two registrations exist, and IConvergedModule for what a
        // clusterless run can and cannot say.
        Cluster.Registry.TryGetType(Case.Type, out var registration).ShouldBeTrue();

        var address = ProviderTestCluster<TSource>.Address("calibration").WithId(Guid.NewGuid());
        var objects = Case.Objects(address, ReconcileDriver.NamespaceFor(address));

        if (registration.RequiresCluster) {
            ProviderTestCluster<TSource>.Module.ShouldBeNull(
                $"{Case.DisplayName} declares RequiresCluster and supplies a module. A type has one "
                + "world — the cluster its objects land in, or the module its grains live in — and "
                + "the registration says which. A case that supplies both would have half its "
                + "assertions reading a world the reconciler never writes."
            );

            Case.DataPlane.ShouldBeNull(
                $"{Case.DisplayName} declares RequiresCluster and supplies a DataPlane, for the same "
                + "reason: the cluster IS its world, and a second one is one the reconciler never writes."
            );

            Case.StoragePrefix.ShouldBeNull(
                $"{Case.DisplayName} declares RequiresCluster and names a StoragePrefix. A cluster-backed "
                + "type's bytes live in its cluster; a prefix on the platform's store would be asserted "
                + "empty by a teardown that never touches it."
            );

            return;
        }

        (ProviderTestCluster<TSource>.Module is not null || Case.DataPlane is not null).ShouldBeTrue(
            $"{Case.DisplayName} declares no RequiresCluster and supplies neither a module nor a "
            + "DataPlane, so this suite has nothing to read the world through: every world-facing "
            + "assertion would skip and a reconciler that wrote nowhere would pass. Set "
            + "IProviderCaseSource.ConvergedModule for a world in the silo (see IConvergedModule), or "
            + "ProviderConformanceCase.DataPlane for one on a platform host."
        );

        (ProviderTestCluster<TSource>.Module is not null && Case.DataPlane is not null).ShouldBeFalse(
            $"{Case.DisplayName} supplies BOTH a module and a DataPlane. One world per type: the suite "
            + "reads whichever it is handed through ClusterlessWorld, and a case that hands it two "
            + "has half its assertions reading a world the reconciler never writes."
        );

        if (Case.DataPlane is not null) {
            // ⚠ Both halves, because either alone is a suite that asserts less than it says: a
            // DataPlane with no StoragePrefix is a teardown nobody checks emptied the store.
            Case.StoragePrefix.ShouldNotBeNull(
                $"{Case.DisplayName} supplies a DataPlane and no StoragePrefix — see "
                + "ProviderConformanceCase.StoragePrefix for what the teardown then cannot assert."
            );
        }

        objects.ShouldBeEmpty(
            $"{Case.DisplayName} is clusterless and names objects. Those objects can never be applied "
            + "— the driver hands a clusterless reconciler a null connection — so the case would "
            + "assert a world its type cannot reach."
        );
    }

    [Fact]
    public async Task EveryCustomKindTheCaseRendersHasACommittedDefinition() {
        // ⚠ THE FLOOR UNDER ISSUE #91. FakeKubeCluster validates a custom resource against the
        // operator's real definition WHEN ONE IS COMMITTED under charts/bundle/*/crds/, and echoes it
        // — accepts any shape — when none is. That echo is the state every custom kind was in while
        // charts/managed/seaweedfs-bucket rendered three fields in the wrong shape for a month, so
        // a provider must not be able to stay in it: every custom kind this case or its ancestors
        // render has to be a kind crds.sh has written a definition for, at the version and plural and
        // scope the case addresses it by. The fix for a red run is `./charts/bundle/crds.sh --refresh`
        // after the chart under charts/managed/ renders the kind — the script derives what to fetch
        // from the templates, so a kind a provider renders and no chart declares is a second finding.
        var address = ProviderTestCluster<TSource>.Address("definitions").WithId(Guid.NewGuid());
        var ns = ReconcileDriver.NamespaceFor(address);

        var rendered = ProviderTestCluster<TSource>.Ancestors
            .Select((ancestor, level) => ancestor.Objects(
                    new ResourceId(
                        address.TenantId,
                        address.SubscriptionId,
                        address.ResourceGroup,
                        ancestor.Type,
                        ConformanceIds.AncestorName(level),
                        Guid.NewGuid(),
                        string.Join('/', Enumerable.Range(0, level).Select(ConformanceIds.AncestorName))
                    ),
                    ns
                )
            )
            .Aggregate(Case.Objects(address, ns).AsEnumerable(), (all, next) => all.Concat(next))
            .Where(x => !FakeKubeCluster.IsBuiltIn(x.Kind.Group))
            .DistinctBy(x => x.Kind.ApiVersion + "|" + x.Kind.Kind + "|" + x.Kind.Plural + "|" + x.IsClusterScoped)
            .ToList();

        foreach (var target in rendered) {
            var definition = CommittedDefinitions.Find(target.Kind);

            definition.ShouldNotBeNull(
                $"{Case.DisplayName} renders {target.Kind} and no file under charts/bundle/*/crds/ defines "
                + $"{target.Kind.Kind} in {target.Kind.Group}. Without it FakeKubeCluster echoes whatever the "
                + "reconciler renders and this suite proves nothing about the shape — the state issue #91 "
                + "found a live defect in. Make sure a chart under charts/managed/ renders the kind, then run "
                + "`./charts/bundle/crds.sh --refresh` and commit what it writes."
            );

            definition.Versions.ContainsKey(target.Kind.Version).ShouldBeTrue(
                $"{Case.DisplayName} renders {target.Kind} and {definition.File} serves it only at "
                + $"{string.Join(", ", definition.Versions.Keys.OrderBy(x => x, StringComparer.Ordinal))}. "
                + "A real API server would answer 404 for the version the reconciler addresses."
            );

            definition.Plural.ShouldBe(
                target.Kind.Plural,
                $"{Case.DisplayName} addresses {target.Kind.Kind} as `{target.Kind.Plural}` and {definition.File} "
                + $"serves it as `{definition.Plural}`. The plural is the REST path; the wrong one is a 404."
            );

            definition.IsClusterScoped.ShouldBe(
                target.IsClusterScoped,
                $"{Case.DisplayName} renders {target.Kind.Kind} {(target.IsClusterScoped ? "cluster-scoped" : "namespaced")} "
                + $"and {definition.File} declares scope {(definition.IsClusterScoped ? "Cluster" : "Namespaced")}."
            );

            // ⚠ THE STORAGE VERSION, WHEN THE DEFINITION CONVERTS THROUGH A WEBHOOK. Cluster API
            // serves v1beta1 (deprecated) beside its storage version v1beta2 and names
            // capi-webhook-service for the conversion between them. The cluster-backed lane installs
            // the definition and no operator, so a request at any version but the storage one reaches
            // a webhook nobody answers — and a definition with no webhook converts by changing the
            // apiVersion string, which needs nobody. The Bundle gate checks only that the version is
            // SERVED, so this is the one place the distinction is made.
            if (definition.ConvertsThroughWebhook) {
                definition.Versions[target.Kind.Version].IsStorage.ShouldBeTrue(
                    $"{Case.DisplayName} renders {target.Kind} and {definition.File} stores "
                    + $"{definition.Kind} at {definition.StorageVersion}, converting through a webhook. On a "
                    + "cluster with the definition and no operator — the cluster-backed conformance lane — "
                    + "an apply at any other served version is a conversion webhook call nothing answers."
                );
            }
        }

        if (!HasClusterDataPlane) {
            return;
        }

        // ⚠ AND WHAT THE RECONCILER ACTUALLY APPLIED, not only what the case declared. FakeKubeCluster
        // echoes a custom kind with no committed definition — it has to, for the driver's own tests —
        // so a reconciler applying a custom kind its case does not name and no chart renders would be
        // echoed, pass every row above, and never meet the Bundle gate's template scan either. A
        // default-bodied converge is the cheapest way to see the applied set; the variant row below
        // sees every other body, and every apply of a defined kind is validated regardless.
        ProviderTestCluster<TSource>.Reset();
        var accepted = (await CreateAsync("definitions-applied")).GetValueOrThrow();
        await ConvergeAsync(accepted);

        var declared = rendered
            .Select(x => x.Kind.ApiVersion + "|" + x.Kind.Kind + "|" + x.Kind.Plural + "|" + x.IsClusterScoped)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var command in Cluster.World.Applied.Where(x => !FakeKubeCluster.IsBuiltIn(x.Target.Kind.Group))) {
            var key = command.Target.Kind.ApiVersion + "|" + command.Target.Kind.Kind + "|" + command.Target.Kind.Plural + "|"
                + command.Target.IsClusterScoped;

            declared.ShouldContain(
                key,
                $"{Case.DisplayName} applied {command.Target} and neither its Objects nor an ancestor's names that "
                + "kind at that version, plural and scope. An undeclared custom kind is one the fake echoes when no "
                + "definition is committed for it and one no row of this suite reads back — declare it in Objects, "
                + "so the rows above hold it to a committed definition."
            );
        }
    }

    [Fact]
    public async Task EveryPropertyVariantTheSchemaAdmitsRendersAShapeTheDefinitionAdmits() {
        // ⚠ THE ROW THE REVIEW OF #91 ASKED FOR, AND THE FINDING THAT JUSTIFIED IT IN THE SAME
        // BREATH. Every other row converges Case.Body — one body per family — so the committed
        // definition was only ever asked about that body, and a reconciler's other branches were as
        // unvalidated after #91 as before it. PostgresServers.ClusterJson wrote
        // `spec.postgresql_synchronous` for `synchronousReplication: true`, a key CloudNativePG's
        // definition does not declare; the flag defaults to false; sixteen suites were green. This
        // row derives, from the type's own schema, one body per property value the default body
        // does not carry — PropertyVariants' remarks say what and why — writes each through the
        // manager, converges it, and asks the fake whether a committed definition refused anything
        // on the way. The type's schema refuses some variants at the API (a bound the generator
        // could not see across two properties); those render nothing and are counted, not failed.
        //
        // ⚠ THE FAKE'S RECORD, NOT THE OPERATION'S ERROR. A reconciler may fail a pass for a reason
        // of its own with the same InvalidRequestBody — PostgresServerReconciler refuses a backup
        // with no destination before it applies anything — and a row that told the two apart by
        // reading the message would be one wording change from proving nothing. FakeKubeCluster.Refused
        // is written only by the definition's answer.
        if (!HasClusterDataPlane) {
            Assert.Skip(
                $"SKIPPED, AND SAYING SO — {Case.DisplayName} declares no RequiresCluster, so no body it accepts "
                + "renders a custom resource for a committed definition to refuse. The variants of a clusterless "
                + "type reach its module or data plane, which this row has no schema to hold them to."
            );

            return;
        }

        Cluster.Registry.TryGetType(Case.Type, out var registration).ShouldBeTrue();
        var schema = registration.SchemaFor(ApiVersion.Parse(Case.ApiVersion)).GetValueOrThrow();
        var variants = PropertyVariants.Of(schema, JsonNode.Parse(Body())!.AsObject(), registration.ClusterIdPointer);

        if (variants.Count == 0) {
            // A NAT gateway is a subnet reference and a public-IP reference, both under a name
            // pattern: nothing to flip, nothing to enumerate. The skip is loud so nobody reads the
            // family's green run as "its variants were checked".
            Assert.Skip(
                $"SKIPPED, AND SAYING SO — {Case.DisplayName}'s schema at {Case.ApiVersion} declares no property this "
                + "row can vary: no boolean, closed set, number, open string or array of a closed set. Every body "
                + "a tenant can send renders the shape the other rows converge, so there is no second shape to "
                + "hold to the definition. A property added to the schema later is a variant this row derives "
                + "without being told."
            );

            return;
        }

        var refusedByTheType = new List<string>();
        var findings = new List<string>();
        var converged = 0;

        foreach (var (index, variant) in variants.Index()) {
            ProviderTestCluster<TSource>.Reset();
            var address = ProviderTestCluster<TSource>.Address("variant-" + index.ToString(CultureInfo.InvariantCulture));

            var accepted = await Cluster.Manager.WriteAsync(Request(address, variant.Body), TestContext.Current.CancellationToken);

            if (accepted.TryGetError(out var refusal)) {
                // The type's own schema said no, at the API, before anything rendered. That is the
                // right answer for a value the generator could not know was out of bounds here, and
                // the wrong answer for anything else — a 404 or a 409 is the harness, not the type.
                refusal.Code.ShouldBe(
                    ErrorCode.InvalidRequestBody,
                    $"{Case.DisplayName} refused the variant {variant.JsonPointer} = {variant.Value} with {refusal.Code}: {refusal.Message}"
                );

                refusedByTheType.Add($"{variant.JsonPointer} = {variant.Value}");
                continue;
            }

            var status = await ConvergeAsync(accepted.GetValueOrThrow());
            converged++;

            foreach (var (target, message) in Cluster.World.Refused) {
                findings.Add(
                    $"{variant.JsonPointer} = {variant.Value} → {target}: {message}"
                    + (status.Error is { } error ? $" (the operation ended {status.State}: {error.Message})" : string.Empty)
                );
            }
        }

        // What the row measured, in the output rather than only in a failure: a family whose every
        // variant the type refused at the API is a family this row learned nothing about.
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{Case.DisplayName}: {variants.Count.ToString(CultureInfo.InvariantCulture)} single-property variant(s) derived, "
            + $"{converged.ToString(CultureInfo.InvariantCulture)} reached the reconciler and the committed definitions, "
            + $"{refusedByTheType.Count.ToString(CultureInfo.InvariantCulture)} refused by the type's own schema"
            + (refusedByTheType.Count > 0 ? ": " + string.Join("; ", refusedByTheType) : ".")
        );

        findings.ShouldBeEmpty(
            $"{Case.DisplayName} renders a shape a committed definition refuses for {findings.Count.ToString(CultureInfo.InvariantCulture)} "
            + $"of {variants.Count.ToString(CultureInfo.InvariantCulture)} single-property variant(s) of its default body. Each line "
            + "names the property and the value that reached the renderer, the object refused, and the API server's own "
            + $"sentence:{Environment.NewLine}  {string.Join(Environment.NewLine + "  ", findings)}"
        );

        converged.ShouldBeGreaterThan(
            0,
            $"{Case.DisplayName}'s schema refused every one of {variants.Count.ToString(CultureInfo.InvariantCulture)} variant(s) at "
            + "the API, so none reached the renderer and this row measured nothing: "
            + string.Join("; ", refusedByTheType)
        );
    }

    [Fact]
    public async Task AnUnknownApiVersionIsRefusedAndTheErrorNamesTheOnesThatExist() {
        // docs/plan/08 § The provider registry: api-versions are dates and they are immutable. There
        // is no "latest", so a caller who guesses must be told what to ask for.
        ProviderTestCluster<TSource>.Reset();

        var result = await Cluster.Manager.WriteAsync(
            Request(ProviderTestCluster<TSource>.Address("bad-version"), apiVersion: "2999-12-31"),
            TestContext.Current.CancellationToken
        );

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe(ErrorCode.InvalidApiVersion);
        result.Error.Message.ShouldContain(Case.ApiVersion);
    }

    // ── create → 202 → poll → Succeeded → read back ─────────────────────────────────────────────

    [Fact]
    public async Task CreateAnswers202WithAnOperationToPollAndTheResourceIsCreating() {
        // Step 12 of docs/plan/08 § The write path, end to end.
        ProviderTestCluster<TSource>.Reset();

        var accepted = await CreateAsync("lifecycle-accepted");
        var value = accepted.GetValueOrThrow();

        value.OperationId.ShouldNotBe(Guid.Empty);
        value.OperationUri.ShouldBe($"/operations/{value.OperationId:D}");
        value.RetryAfterSeconds.ShouldBe(ReconcileSchedule.InitialRetryAfterSeconds);
        value.Resource.ProvisioningState.ShouldBe(ProvisioningState.Creating);
        value.Trace.Reached.ShouldBe(WriteTrace.Canonical);
    }

    [Fact]
    public async Task PollingTheOperationReachesSucceededAndTheResourceFollowsIt() {
        ProviderTestCluster<TSource>.Reset();

        var accepted = (await CreateAsync("lifecycle-poll")).GetValueOrThrow();
        var status = await ConvergeAsync(accepted);

        status.State.ShouldBe(
            OperationState.Succeeded,
            $"the operation ended {status.State}: {status.Error?.Message}"
        );

        var snapshot = await ReadAsync("lifecycle-poll");
        snapshot.GetValueOrThrow().ProvisioningState.ShouldBe(ProvisioningState.Succeeded);
    }

    [Fact]
    public async Task TheResourceReadsBackWithWhatWasWritten() {
        ProviderTestCluster<TSource>.Reset();

        var accepted = (await CreateAsync("lifecycle-readback")).GetValueOrThrow();
        await ConvergeAsync(accepted);

        var snapshot = (await ReadAsync("lifecycle-readback")).GetValueOrThrow();

        // ⚠ Compared canonically rather than as text. The grain stores a canonical superset and
        // projects it down per api-version, so a body whose properties arrived in a different order
        // must read back equal — otherwise "idempotent" would depend on the client's serializer.
        Canonical(snapshot.Body).ShouldBe(Canonical(Body()));
        snapshot.Etag.ShouldNotBeNullOrEmpty();
        snapshot.ApiVersion.ShouldBe(Case.ApiVersion);
    }

    [Fact]
    public async Task WhatTheProviderAppliedIsInTheClusterAndMatchesTheDesiredBody() {
        // ⚠ READ AROUND THE PROVIDER. Every other assertion in the run goes through the manager, which
        // goes through the reconciler; this one reads the cluster the reconciler wrote into. A
        // provider that reported success and applied nothing passes everything above and fails here.
        ProviderTestCluster<TSource>.Reset();

        var accepted = (await CreateAsync("world-applied")).GetValueOrThrow();
        await ConvergeAsync(accepted);

        if (!HasClusterDataPlane) {
            // ⚠ THE SAME ASSERTION, READ THROUGH THE OTHER WORLD — around the reconciler, never
            // through its own ObserveAsync. A clusterless type's world is read through the module
            // its case source registered or the data plane its case built — for a feed, the
            // catalogue grain reached through ForTenant — and a clusterless type that reported
            // success and wrote nothing passes everything above and fails here, exactly as a
            // cluster-backed one would. The only thing the fake API server has to say about it is
            // that nothing arrived, because the type has no cluster to arrive at.
            AssertNothingReachedTheCluster();

            var world = ClusterlessWorldOf(AddressOf(accepted.Resource.Id, "world-applied"));
            var ct = TestContext.Current.CancellationToken;

            (await world.HoldsAsync(ct))
                .ShouldBeTrue($"{Case.DisplayName} converged and its world holds nothing for the resource");

            (await world.MatchesAsync(Body(), ct))
                .ShouldBeTrue($"what the world holds for '{Case.Type}' does not read back as the desired body");

            return;
        }

        var objects = ObjectsOf(accepted.Resource.Id, "world-applied");
        objects.ShouldNotBeEmpty($"{Case.DisplayName} declares RequiresCluster and applied nothing");

        foreach (var target in objects) {
            var json = Cluster.World.Read(target);
            json.ShouldNotBeNull($"'{target}' is not in the cluster");
            MatchesDesired(accepted.Resource.Id, "world-applied", target, json, Body())
                .ShouldBeTrue($"'{target}' does not carry the desired shape");
        }
    }

    [Fact]
    public async Task EveryAppliedObjectCarriesTheSevenMandatoryLabelsAndBothAnnotations() {
        // ⚠ docs/plan/23 § The architecture gates, the Labels row: "every reconciler's rendered output
        // carries the seven cybercloud.io/* labels, asserted against REAL OUTPUT". Build.Architecture
        // reports that gate as Blocked with the reason "no provider exists to render any". One does
        // now, and this is the assertion the gate was waiting for.
        ProviderTestCluster<TSource>.Reset();

        var accepted = (await CreateAsync("world-labelled")).GetValueOrThrow();
        await ConvergeAsync(accepted);

        if (!HasClusterDataPlane) {
            // ⚠ ASSERTED ABSENT, AND NOT SKIPPED, and the reason is the Labels architecture gate.
            // That gate runs this method per .Conformance project under `--minimum-expected-tests 1`,
            // and a filtered run whose only match skipped reports "Zero tests ran", which the gate
            // reads as the suite failing. So the branch asserts the one thing that IS true of the
            // seven labels on a clusterless type: no object was applied for them to be missing from
            // — NOT EVEN THE RESOURCE GROUP'S NAMESPACE, which the driver applies for every
            // cluster-backed type before the reconciler runs and must skip for this one. An object
            // here came from somewhere the driver did not hand it, carries none of ADR-013's labels
            // because nothing injected them, and means the type is not clusterless — which is the
            // lie worth catching. Where a project mixes the two — ContainerRegistry's feeds beside
            // its registries — the gate still sees a real rendered object; where every case is
            // clusterless — the Communication family — this absence is the whole of what the gate
            // can be shown, and it is shown rather than skipped.
            AssertNothingReachedTheCluster();
            return;
        }

        Cluster.World.Applied.ShouldNotBeEmpty();

        // ⚠ EVERY OBJECT IN THIS PASS, and one of them is not the provider's. NamespaceEnsurer
        // applies the resource group's namespace on the driver's path before the reconciler runs, so
        // `Applied` holds the reconciler's objects AND that namespace. The two are held to different
        // invariants — a namespace is attributed to the GROUP and carries a derived resource-id that
        // no resource grain will ever own — and the split below is a fork rather than an exemption:
        // both branches assert, and the group branch asserts MORE, because everything about a
        // platform-written namespace is computable from the address the test already has.
        //
        // ⚠ IT CANNOT BE USED AS AN ESCAPE HATCH BY A RECONCILER, which is the only thing that would
        // make this a weakening. The fork keys on `cybercloud.io/resource-type`, which is one of
        // ADR-013's seven: KubeCommandBuilder injects it from the resource's own type and
        // KubeLabels.IsMandatory refuses a caller that tries to set it. So a reconciler can only
        // reach the group branch if its resource's TYPE is in CyberCloud.Resources — and
        // ProviderRegistry.Build refuses to register a provider that declares that namespace. Both
        // doors are shut, and neither is shut by this file.
        var groupScoped = 0;

        foreach (var command in Cluster.World.Applied) {
            if (command.IsCoOwned) {
                // ⚠ THE CO-WRITER'S READING OF THE SAME GATE, AND IT ASSERTS MORE, NOT LESS. A
                // co-owned command carries NO labels by construction — the seven are the owner's and
                // stay on the object — so the assertion moves from the command to the object it
                // landed on: the owner's seven are still there, the tenant is ours, the resource-id
                // is NOT ours, and this resource's fragment, hash and path are beside the owner's two
                // annotations. The command itself is held to the shape the tunnel agent holds it to.
                //
                // ⚠ NOT AN ESCAPE HATCH, for the reason the group fork below is not one: IsCoOwned is
                // KubeCommand.OwnerResourceId, which only IKubeCommandBuilder.CoWriting sets, from the
                // live object's own resource-id label — and FakeKubeCluster.ApplyCoOwned refuses a
                // command whose shape or owner does not check out before anything lands.
                command.Labels.ShouldBeEmpty($"'{command.Target}' is a co-writer's command and carries labels");
                command.CheckCoOwnedShape().IsSuccess.ShouldBeTrue(command.CheckCoOwnedShape().Error?.Message);
                command.OwnerResourceId.ShouldNotBe(accepted.Resource.Id, "a co-writer names another resource as the owner");
                command.Annotations[KubeLabels.FragmentHashAnnotation(accepted.Resource.Id)].ShouldBe(command.ReconcileHash);

                var landed = Cluster.World.Read(command.Target);
                landed.ShouldNotBeNull($"'{command.Target}' was co-written and is not in the cluster");
                AssertCoOwnedObject(accepted.Resource.Id, command.Target, landed);

                continue;
            }

            foreach (var label in KubeLabels.Mandatory) {
                command.Labels.ShouldContainKey(label, $"'{command.Target}' is missing '{label}'");
                command.Labels[label].ShouldNotBeNullOrEmpty();
            }

            command.Labels[KubeLabels.TenantId].ShouldBe(KubeLabels.GuidValue(ConformanceIds.Tenant));
            command.Labels[KubeLabels.ManagedBy].ShouldBe(KubeLabels.ManagedByValue);

            foreach (var annotation in KubeLabels.MandatoryAnnotations) {
                command.Annotations.ShouldContainKey(annotation);
            }

            command.Annotations[KubeLabels.ReconcileHashAnnotation].ShouldStartWith("sha256:");

            // ⚠ The resource-id label is what makes orphan detection a hash join rather than a scan —
            // ADR-013 — and an EMPTY GUID here is the failure that produces objects the drift scan
            // reports as orphans forever. ReconcileContext's own remarks warn about it by name. It
            // holds for both branches, so it is asserted before the fork.
            command.Labels[KubeLabels.ResourceId].ShouldNotBe(KubeLabels.GuidValue(Guid.Empty));

            // ⚠ AND THE OBJECTS THE PROVIDER DOES NOT APPLY. Everything above reads
            // `command.Labels`, which is the object's own metadata.labels — and a claim a
            // StatefulSet's volumeClaimTemplate makes is a DIFFERENT OBJECT that nothing in this
            // platform ever applies. Asserting only over commands is the failure class this repo has
            // shipped thirteen times: a check that answers a narrower question than its name. See
            // AssertClaimTemplatesAreLabelled.
            AssertClaimTemplatesAreLabelled(command);

            if (KubeLabels.IsGroupScoped(command.Labels)) {
                groupScoped++;
                AssertGroupNamespace(command);
                continue;
            }

            command.Labels[KubeLabels.ResourceId].ShouldBe(KubeLabels.GuidValue(accepted.Resource.Id));
            command.Labels[KubeLabels.ApiVersion].ShouldBe(Case.ApiVersion);
        }

        // ⚠ EXACTLY ONE, AND ASSERTING IT IS WHAT KEEPS THE MEMO HONEST. NamespaceEnsurer caches
        // "this namespace exists on this cluster" and would otherwise skip the apply whenever an
        // earlier test in this class had warmed it — so this assertion passed or failed depending on
        // which test ran first, and the filtered single-test run the Labels architecture gate makes
        // was the only shape that ever saw the namespace. ConformanceState.Reset now calls
        // NamespaceEnsurer.Forget, and this line is what fails if that ever stops happening.
        groupScoped.ShouldBe(
            1,
            "the pass applied "
            + groupScoped.ToString(CultureInfo.InvariantCulture)
            + " group-attributed objects and exactly one — the resource group's namespace — is "
            + "expected. Zero means NamespaceEnsurer's memo was still warm from an earlier test, "
            + "which makes this assertion's result depend on test order; more than one means "
            + "something else is writing objects under KubeLabels.ReservedNamespace."
        );

        // ⚠ AND A TYPE THAT OWNS NONE OF ITS OBJECTS STILL HAS TO HAVE WRITTEN SOMETHING. The two
        // counts together cover every command: a pass that applied only the namespace would be the
        // vacuous green this whole assertion exists to refuse, on the one kind of type whose own
        // commands carry no labels for the loop above to fail on.
        (Cluster.World.Applied.Count - groupScoped).ShouldBeGreaterThan(
            0,
            $"{Case.DisplayName} converged and applied nothing but the resource group's namespace"
        );

    }

    /// <summary>
    ///     Asserts that every claim template nested inside an applied body carries the six
    ///     lifetime-stable labels, and that it does not carry the seventh.
    /// </summary>
    /// <param name="command">Any applied command.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This asks a question the rest of the assertion cannot.</b> Everything else here
    ///         reads <c>command.Labels</c> — an object's own <c>metadata.labels</c>, which
    ///         <c>KubeCommandBuilder</c> fills unconditionally. A
    ///         <c>PersistentVolumeClaim</c> made from a <c>volumeClaimTemplate</c> is a separate
    ///         object that this platform never applies, so no command describes it and the labels
    ///         gate was green over every provider that renders one while the claims carried nothing
    ///         but the workload selector's <c>matchLabels</c>. That is the gap that made a
    ///         managed-only listing report an empty namespace precisely when a tenant's restorable
    ///         volumes were in it.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             The templates are found by SHAPE and by name, over the whole body, rather than
    ///             from whatever the provider declared.
    ///         </b> A check that read
    ///         <c>WithTemplateLabels</c>'s own argument would pass for a provider that declared
    ///         nothing, which is the only way this can be got wrong. Walking the body means a
    ///         provider that renders a claim template and forgets to declare it fails here.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             The seventh is asserted ABSENT, and that is a decision rather than an
    ///             oversight.
    ///         </b> <c>cybercloud.io/api-version</c> is stamped from the request that
    ///         caused the reconcile, so it differs between reconciles — and a live
    ///         <c>StatefulSet</c>'s <c>spec.volumeClaimTemplates</c> refuses every change, measured
    ///         against the cluster lane's own k3s pin. A template carrying it would make the resource
    ///         unreconcilable the first time a tenant called at a newer api-version. See
    ///         <see cref="KubeLabels.LifetimeStable" />.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What it does NOT cover: <c>dataVolumeTemplates</c>.</b>
    ///         <c>CyberCloud.ContainerService/agentPools</c> renders one inside a
    ///         <c>KubevirtMachineTemplate</c>, and a Cluster API infrastructure machine template is a
    ///         thing the CAPI contract rotates rather than edits — so stamping it risks the same
    ///         rejected-forever apply this assertion's own exclusion exists to avoid, on a kind
    ///         nothing here can measure. It is named in <c>src/Providers/README.md § Namespaces</c>
    ///         as owed rather than silently skipped.
    ///     </para>
    /// </remarks>
    static void AssertClaimTemplatesAreLabelled(KubeCommand command) {
        var body = JsonNode.Parse(command.Body);

        foreach (var template in ClaimTemplates(body)) {
            var labels = (template["metadata"] as JsonObject)?["labels"] as JsonObject;

            labels.ShouldNotBeNull(
                $"'{command.Target}' renders a claim template with no metadata.labels, so the "
                + "PersistentVolumeClaims it produces are invisible to every selector this platform "
                + "has — including the managed-only listing a soft-deleted resource's volumes have "
                + "to be found by. Pass the template's path to WithTemplateLabels."
            );

            foreach (var key in KubeLabels.LifetimeStable) {
                labels[key]?.GetValue<string>()
                    .ShouldBe(
                        command.Labels[key],
                        $"'{command.Target}'s claim template is missing '{key}', or disagrees with the "
                        + "object's own label of the same name."
                    );
            }

            labels.ContainsKey(KubeLabels.ApiVersion)
                .ShouldBeFalse(
                    $"'{command.Target}'s claim template carries '{KubeLabels.ApiVersion}'. That label "
                    + "is stamped from the request, and a StatefulSet's spec.volumeClaimTemplates is "
                    + "refused every change once the set exists — so the next reconcile at a different "
                    + "api-version would be rejected, and so would every one after it."
                );
        }
    }

    /// <summary>
    ///     Every claim template anywhere in a rendered body — <c>volumeClaimTemplates</c> entries and
    ///     a lone <c>volumeClaimTemplate</c>.
    /// </summary>
    /// <param name="node">The body, or a subtree of it.</param>
    /// <remarks>
    ///     ⚠ Keyed on the name <b>and</b> the shape. Two of this platform's rendered bodies carry a
    ///     key ending in <c>Template</c> whose value is a <see langword="string" /> naming a template
    ///     — a <c>ClickHouseInstallation</c>'s <c>defaults.templates.podTemplate</c> and its
    ///     <c>dataVolumeClaimTemplate</c> — so a name-only rule would try to read
    ///     <c>metadata.labels</c> off a string.
    /// </remarks>
    static IEnumerable<JsonObject> ClaimTemplates(JsonNode? node) {
        switch (node) {
            case JsonObject map:
                foreach (var (key, value) in map) {
                    if (key is "volumeClaimTemplates" && value is JsonArray array) {
                        foreach (var entry in array.OfType<JsonObject>()) {
                            yield return entry;
                        }
                    } else if (key is "volumeClaimTemplate" && value is JsonObject one) {
                        yield return one;
                    }

                    foreach (var found in ClaimTemplates(value)) {
                        yield return found;
                    }
                }

                break;

            case JsonArray items:
                foreach (var item in items) {
                    foreach (var found in ClaimTemplates(item)) {
                        yield return found;
                    }
                }

                break;
        }
    }

    /// <summary>
    ///     Asserts that a group-attributed command is the namespace the platform writes, and nothing
    ///     else.
    /// </summary>
    /// <param name="command">The command whose <c>resource-type</c> named a resource group.</param>
    /// <remarks>
    ///     ⚠ <b>Every value here is computed from the address rather than read off the command.</b>
    ///     A branch that only checked "this looks group-scoped, carry on" would be the exemption this
    ///     fork must not be: an object reaching it is checked against the one object that is allowed
    ///     to reach it — the right kind, cluster-scoped, the platform's own field manager, the group's
    ///     derived id, and the group's own name — so there is no shape a reconciler could render that
    ///     lands here and passes.
    /// </remarks>
    static void AssertGroupNamespace(KubeCommand command) {
        var address = ProviderTestCluster<TSource>.Address("world-labelled");

        command.Target.Kind.Kind.ShouldBe(
            "Namespace",
            $"'{command.Target}' claims to belong to a resource group and the only object the "
            + "platform writes on a group's behalf is its namespace."
        );

        command.Target.IsClusterScoped.ShouldBeTrue("a Namespace is cluster-scoped.");
        command.Target.Name.ShouldBe(ReconcileDriver.NamespaceFor(address));
        command.FieldManager.ShouldBe(NamespaceEnsurer.FieldManager);

        command.Labels[KubeLabels.ResourceGroup].ShouldBe(address.ResourceGroup);

        command.Labels[KubeLabels.ResourceId].ShouldBe(
            KubeLabels.GuidValue(NamespaceEnsurer.IdFor(address.SubscriptionId, address.ResourceGroup)),
            "the namespace's resource-id is derived from the group it belongs to, so it is the same "
            + "on every silo and for every resource in the group."
        );
    }

    // ── tag ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TagsAreStoredAndReadBack() {
        // docs/plan/06 § Tags, locks. A type that does not declare SupportsTags refuses a body with
        // tags rather than accepting and dropping them, so this asserts the branch its declaration
        // selected rather than assuming one.
        ProviderTestCluster<TSource>.Reset();

        Cluster.Registry.TryGetType(Case.Type, out var registration).ShouldBeTrue();

        var address = ProviderTestCluster<TSource>.Address("tagged");
        var body = WithTags(Body(), ("env", "prod"), ("owner", "platform"));

        var written = await Cluster.Manager.WriteAsync(
            Request(address, body: body),
            TestContext.Current.CancellationToken
        );

        if (!registration.SupportsTags) {
            written.IsFailure.ShouldBeTrue(
                "the type does not declare SupportsTags, so a body carrying tags is refused rather "
                + "than accepted and silently dropped"
            );

            return;
        }

        written.IsSuccess.ShouldBeTrue(written.Error?.Message);
        await ConvergeAsync(written.GetValueOrThrow());

        var snapshot = (await ReadAsync("tagged")).GetValueOrThrow();
        snapshot.Tags.ShouldContainKeyAndValue("env", "prod");
        snapshot.Tags.ShouldContainKeyAndValue("owner", "platform");
    }

    // ── lock ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ACanNotDeleteLockRefusesTheDeleteAndTheResourceSurvives() {
        ProviderTestCluster<TSource>.Reset();

        var accepted = (await CreateAsync("locked")).GetValueOrThrow();
        await ConvergeAsync(accepted);

        Cluster.Locks.Level = LockLevel.CanNotDelete;

        var refused = await DeleteAsync("locked");

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.ScopeLocked);

        Cluster.Locks.Reset();

        var entry = await Cluster.Index(ProviderTestCluster<TSource>.Address("locked")).GetAsync();
        entry.GetValueOrThrow()
            .State.ShouldBe(
                IndexEntryState.Confirmed,
                "a refused delete must not have released the name — the release is irreversible"
            );

        foreach (var target in ObjectsOf(accepted.Resource.Id, "locked")) {
            Cluster.World.Holds(target).ShouldBeTrue("a lock that let the data plane go is not a lock");
        }

        if (!HasClusterDataPlane) {
            (await ClusterlessWorldOf(AddressOf(accepted.Resource.Id, "locked")).HoldsAsync(TestContext.Current.CancellationToken))
                .ShouldBeTrue("a lock that let the clusterless world's state go is not a lock");
        }
    }

    [Fact]
    public async Task AReadOnlyLockRefusesAWrite() {
        ProviderTestCluster<TSource>.Reset();

        var accepted = (await CreateAsync("readonly-locked")).GetValueOrThrow();
        await ConvergeAsync(accepted);

        Cluster.Locks.Level = LockLevel.ReadOnly;

        var refused = await Cluster.Manager.WriteAsync(
            Request(ProviderTestCluster<TSource>.Address("readonly-locked"), body: ChangedBody()),
            TestContext.Current.CancellationToken
        );

        Cluster.Locks.Reset();

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.ScopeLocked);
    }

    // ── delete → gone ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     <c>delete → gone</c>, in whichever of its two forms this provider declared.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             TWO OF THE ASSERTIONS BELOW ARE CORRECT FOR A HARD-DELETE TYPE AND WRONG FOR A
    ///             SOFT-DELETABLE ONE
    ///         </b>, and the branch is what reconciles them. The index entry going
    ///         back to <c>Free</c> — <i>"the name comes back"</i> — and the ReBAC parent tuple being
    ///         removed are both things a <c>DELETE</c> deliberately does <b>not</b> do when the type
    ///         declares a recovery window (docs/plan/08 § Soft delete): the name is held so a restore
    ///         has somewhere to go, and the edge moves to the subscription rather than being dropped so
    ///         the resource is never invisible.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             A THIRD ASSERTION USED TO BRANCH AND NO LONGER DOES: THE OBJECTS ARE GONE EITHER
    ///             WAY.
    ///         </b> The soft arm asserted they stayed, and two providers declared a window against
    ///         that arm, measured what a tenant was left with — a workload still running behind an
    ///         address answering <c>404</c>, still billed, and unreachable to delete again — and
    ///         withdrew. A soft delete tears the data plane down like any other delete; what the
    ///         window preserves is the name, the stored desired state, the committed quota and the
    ///         volumes, none of which a teardown removes. The soft arm asserts the restore round trip
    ///         instead, because without it every assertion here would also hold for a type that can
    ///         never hand anything back.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             THE BRANCH IS TAKEN FROM THE REGISTRY AND NOT FROM
    ///             <see cref="ProviderConformanceCase" />, and that distinction is the whole design.
    ///         </b>
    ///         That record's own remarks forbid a case supplying anything the suite decides with —
    ///         <i>
    ///             "a case that could supply an assertion would be a provider grading its own
    ///             homework"
    ///         </i> — and "which of these two contracts do I have to satisfy" is exactly such
    ///         a decision. The registry is not the provider's answer to the suite; it is the platform's
    ///         own description of the type, built from <c>Describe</c> and read by the write path, the
    ///         OpenAPI emitter and the four generated surfaces alike. A provider declares
    ///         <c>SupportsSoftDelete</c> once, in public, where it reaches the published document; the
    ///         suite <i>derives</i> which contract that declaration signs it up to. Nothing is
    ///         optional and nothing is skipped: both arms assert, and a provider cannot decline either
    ///         by omission the way a nullable case member would let it.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             Why not a <c>static virtual</c> on <see cref="IProviderCaseSource" />, which is the
    ///             one accepted way to add an optional member.
    ///         </b> <c>Ancestors</c> earns that shape
    ///         because its value is <i>not derivable</i> — a parent's api-version and a body its schema
    ///         accepts exist nowhere else — and because omitting it is refused by name rather than
    ///         silently running a smaller suite. Neither applies here: the window is already a registry
    ///         fact, so a case member would be a <b>second</b> declaration of it, and two descriptions
    ///         of one thing is the drift ADR-012 exists to remove. Worse, the two could disagree — a
    ///         case saying "hard delete" over a type declaring seven days would run the wrong contract
    ///         and pass.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             Which providers take the soft arm is a registry fact and therefore moves without
    ///             this file changing.
    ///         </b> <c>CyberCloud.ContainerRegistry/registries</c> and
    ///         <c>CyberCloud.Monitor/workspaces</c> declare a window;
    ///         <c>CyberCloud.ResourceManager.Tests.SoftDeletePathTests</c> drives the same contract
    ///         against a fixture type in isolation, which is where its own sabotage results were
    ///         taken.
    ///     </para>
    /// </remarks>
    /// <summary>
    ///     ⚠
    ///     <b>
    ///         The claims a teardown deliberately leaves survive it, and the teardown that ends the
    ///         resource for good removes them.
    ///     </b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is the assertion docs/plan/08 § Soft delete's owed item was waiting for:</b>
    ///         <i>
    ///             "a purge still leaves the volumes, because ending a window has to remove exactly
    ///             what a teardown keeps"
    ///         </i>. Deleting a <c>StatefulSet</c> does not delete the
    ///         <c>PersistentVolumeClaim</c>s its <c>volumeClaimTemplate</c> made — that is what makes
    ///         a recovery window worth having — and until <c>IResourceReconciler</c> gained
    ///         <c>RetainedVolumesAsync</c> nothing ever removed them, so a purged resource returned
    ///         its quota and left its disks.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             The claims are PLANTED rather than created, because the fake cluster runs no
    ///             controllers
    ///         </b>, and how they are planted is the load-bearing part. Kubernetes composes
    ///         a claim's name as <c>{volume}-{set}-{ordinal}</c> and copies the set's
    ///         <c>spec.selector.matchLabels</c> onto it; both halves are read
    ///         <i>
    ///             out of the document
    ///             the provider actually applied
    ///         </i> rather than supplied by the case, so a provider
    ///         cannot make this pass by describing claims it does not create. The same round trip
    ///         against a real API server, where the controller makes the claims itself, is what
    ///         proves the model — <c>CyberCloud.Cluster.Conformance</c>.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             The branch is the registry's, exactly as in
    ///             <see cref="DeleteTearsDownTheDataPlaneAndTheResourceIsGone" />.
    ///         </b> A type with a
    ///         window must keep its claims through the soft delete — asserting they are gone there
    ///         would be asserting a restore has nothing to restore from — and lose them at the purge.
    ///         A type with no window loses them at the delete, which is the same defect one step
    ///         earlier and is the reason this test is not filed under soft delete.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             The claims an OPERATOR creates take the same round trip, and the fake's garbage
    ///             collector is what makes that round trip a test — issue #69.
    ///         </b> CloudNativePG creates a server's claims itself and stamps a controller
    ///         reference on each, so a teardown that deletes the <c>Cluster</c> without detaching
    ///         them loses them to the collector before the window starts, and a restore that
    ///         re-creates the <c>Cluster</c> without adopting them leaves the operator blind to
    ///         them. A family declares such claims through
    ///         <see cref="ProviderConformanceCase.OperatorWritten" /> — a
    ///         <c>PersistentVolumeClaim</c> naming its owner by kind and name — and the harness plants
    ///         them owned by the object the reconciler applied, with the uid the fake issued. The
    ///         soft arm then asserts three things a template-made claim never needed: that the
    ///         claim is still there (the collector would have taken it), that it names no owner
    ///         (the one it named is gone), and after the restore that its controller is the
    ///         <c>Cluster</c> the restore created — by uid, because that is how the operator finds
    ///         it and how the collector decides whether it lives.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A family that renders no <c>volumeClaimTemplate</c> and declares no
    ///         operator-owned claim SKIPS, and the skip names the gap it leaves.</b> Most of the
    ///         catalogue owns no disk, and a test that iterated an empty collection and reported
    ///         success would be the vacuous green this suite's own vacuity guard exists to refuse.
    ///         But a skip is also how this case hid #69 for as long as it did: a family whose
    ///         operator owns its claims rendered no template, was skipped correctly, and advertised a
    ///         window nothing had exercised. So the skip now says which of the two it did not find,
    ///         and what a family whose operator creates claims owes this case before its window is
    ///         evidence rather than a declaration.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task TheClaimsATeardownKeepsSurviveItAndTheFinalTeardownRemovesThem() {
        ProviderTestCluster<TSource>.Reset();

        Cluster.Registry.TryGetType(Case.Type, out var registration).ShouldBeTrue();
        var recoverable = registration.SoftDeleteDays > 0;

        var accepted = (await CreateAsync("keeps-disks")).GetValueOrThrow();
        await ConvergeAsync(accepted);

        var templated = ClaimsOf(Cluster.World.Applied);

        // ⚠ The operator's claims, owned by the object the reconciler applied — see the remarks. Every
        // owner is resolved to a uid the fake issued, so the collector below can find the dependents
        // exactly as the real one would.
        //
        // ⚠ SPLIT ON WHAT A PROVISIONER WRITES, AND THE SECOND HALF IS NEWER. A claim a dynamic
        // provisioner BINDS — CyberCloud.Storage/accounts/fileShares' ReadWriteMany claim, which the
        // reconciler applies and the CSI external-provisioner then writes spec.volumeName onto — is
        // planted through OperatorWritten so the action handler has a bound claim to read, and it
        // names no owner because in the real world nothing owns it. Asking such a claim for a
        // controller would refuse the real shape; not following it through the teardown would let a
        // reconciler leave its own claim behind. So a planted claim carrying spec.volumeName joins
        // `claims` — gone after a hard delete, kept through a soft one, gone at the purge — and is
        // exempt from the three controller assertions, which are about the collector and a collector
        // never touches it.
        //
        // ⚠ The split used to be "has a controller reference or not", and that relaxed an assertion
        // for every provider: an operator-owned fixture that FORGOT its owner reference was quietly
        // routed down the weaker path instead of failing "was declared operator-owned and was planted
        // with no controller". Keying on the provisioner's own field keeps that failure loud — a
        // planted claim with neither a volume nor a controller is a fixture that is wrong, and it
        // says so below.
        var planted = PlantOperatorObjects(accepted.Resource.Id, "keeps-disks")
            .Where(x => x.Target.Kind == RetainedVolume.ClaimKind)
            .ToList();

        var provisionerBound = planted.Where(x => VolumeNameOf(x.Json).Length > 0).ToList();
        var operatorOwned = planted.Except(provisionerBound).ToList();

        if (templated.Count == 0 && planted.Count == 0) {
            Assert.Skip(
                $"SKIPPED — {Case.DisplayName} renders no volumeClaimTemplate and declares no "
                + "operator-owned PersistentVolumeClaim in OperatorWritten, so this case has no claim "
                + "to follow through a teardown. That is the right answer for a family that owns no "
                + "disk. ⚠ It is the WRONG answer for a family whose operator creates claims and "
                + "stamps an owner reference on them — CloudNativePG does — because then a soft "
                + "delete garbage-collects the data before the window starts and nothing here can see "
                + "it (issue #69). Such a family declares its claims in OperatorWritten, naming the "
                + "owner by kind and name, and this case then follows them through the soft delete, "
                + "the restore and the purge."
            );
        }

        var claims = templated.Concat(operatorOwned).Concat(provisionerBound).ToList();

        // ⚠ The StatefulSet controller's job, done by hand because this cluster has no controllers.
        // The name and the labels both come from the applied document — see the remarks.
        foreach (var (target, json) in templated) {
            Cluster.World.MutateBehindTheirBack(target, json);
            Cluster.World.Holds(target).ShouldBeTrue();
        }

        foreach (var (target, _) in operatorOwned) {
            // A claim planted as owned must read back as owned, or the survival assertion below
            // would pass for a claim the collector was never going to take.
            var controller = Cluster.World.ControllerOf(target);
            controller.ShouldNotBeNull($"'{target}' was declared operator-owned and was planted with no controller");
            Cluster.World.UidOf(new() { Kind = KindOf(controller), Namespace = target.Namespace, Name = controller.Name })
                .ShouldBe(controller.Uid, $"'{target}' names an owner the reconciler did not apply");
        }

        var deleted = await DeleteAsync("keeps-disks");
        deleted.IsSuccess.ShouldBeTrue(deleted.Error?.Message);

        var status = await ConvergeAsync(deleted.GetValueOrThrow());
        status.State.ShouldBe(
            OperationState.Succeeded,
            $"the teardown ended {status.State}: {status.Error?.Message}"
        );

        if (!recoverable) {
            foreach (var (target, _) in claims) {
                Cluster.World.Holds(target)
                    .ShouldBeFalse(
                        $"'{target}' is still in the cluster after a converged hard delete. The resource "
                        + "is gone, its name is free and its quota is back — and its disk is still "
                        + "allocated, unreferenced and unbilled, until an operator finds it."
                    );
            }

            return;
        }

        // ── The window's half: the disks are what a restore restores from ───────────────────────
        foreach (var (target, _) in claims) {
            Cluster.World.Holds(target)
                .ShouldBeTrue(
                    $"'{target}' was removed by a SOFT delete on a type that declares a "
                    + $"{registration.SoftDeleteDays.ToString(CultureInfo.InvariantCulture)}-day window. "
                    + "The claims are the data a restore hands back; removing them makes the window an "
                    + "advertisement."
                    + (operatorOwned.Any(x => x.Target == target)
                        ? " This claim was owned by the object the teardown deleted, so the garbage "
                          + "collector took it with its owner — the teardown has to detach the claim "
                          + "BEFORE it deletes, which is what kubectl cnpg destroy --keep-pvc does by "
                          + "hand. docs/plan/08 § Soft delete, issue #69."
                        : string.Empty)
                );
        }

        foreach (var (target, _) in operatorOwned) {
            Cluster.World.ControllerOf(target)
                .ShouldBeNull(
                    $"'{target}' survived the soft delete and still names a controller. The object it "
                    + "names is gone, so a real collector would have removed this claim; the fake's "
                    + "did not only because the reference was written after the delete."
                );
        }

        // ── And the restore's half, for a claim the operator has to be able to find again ──────
        if (operatorOwned.Count > 0) {
            var restored = await RestoreAsync("keeps-disks");
            restored.IsSuccess.ShouldBeTrue(restored.Error?.Message);

            var back = await ConvergeAsync(restored.GetValueOrThrow());
            back.State.ShouldBe(OperationState.Succeeded, $"the restore ended {back.State}: {back.Error?.Message}");

            foreach (var (target, json) in operatorOwned) {
                var declared = KubeJson.ControllerOf(JsonNode.Parse(json))!;
                var owner = new ObjectRef { Kind = KindOf(declared), Namespace = target.Namespace, Name = declared.Name };
                var controller = Cluster.World.ControllerOf(target);

                controller.ShouldNotBeNull(
                    $"'{target}' has no controller after the restore. An operator that indexes its "
                    + "claims by controller reference cannot see this one, so it would bootstrap a "
                    + "fresh primary beside the tenant's data rather than over it."
                );

                controller.Uid.ShouldBe(
                    Cluster.World.UidOf(owner),
                    $"'{target}' names '{controller}' and the restored {owner.Kind.Kind} '{owner.Name}' "
                    + "has a different uid. The collector compares uids, so this claim is owned by "
                    + "nothing and goes on the next sweep."
                );
            }

            // Back into the window, so the purge below ends it the way a tenant's would.
            var again = await DeleteAsync("keeps-disks");
            again.IsSuccess.ShouldBeTrue(again.Error?.Message);
            (await ConvergeAsync(again.GetValueOrThrow())).State.ShouldBe(OperationState.Succeeded);

            foreach (var (target, _) in operatorOwned) {
                Cluster.World.Holds(target).ShouldBeTrue($"'{target}' did not survive the second soft delete");
            }
        }

        // ── And the purge's half: ending the window ends the disks ──────────────────────────────
        var purged = await PurgeAsync("keeps-disks");
        purged.IsSuccess.ShouldBeTrue(purged.Error?.Message);

        var ended = await ConvergeAsync(purged.GetValueOrThrow());
        ended.State.ShouldBe(
            OperationState.Succeeded,
            $"the purge ended {ended.State}: {ended.Error?.Message}"
        );

        foreach (var (target, _) in claims) {
            Cluster.World.Holds(target)
                .ShouldBeFalse(
                    $"'{target}' survived the purge. docs/plan/08 § Soft delete: ending a window has to "
                    + "remove exactly what a teardown keeps, or a purged resource returns its quota and "
                    + "leaves its disks."
                );
        }
    }

    /// <summary>
    ///     Every claim a <c>volumeClaimTemplate</c> in these commands would create, as the API server
    ///     would hold it.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Read out of the applied documents and out of nothing else.</b> The name is
    ///     Kubernetes' <c>{volume}-{set}-{ordinal}</c>; the labels are the set's own
    ///     <c>spec.selector.matchLabels</c>, which the <c>StatefulSet</c> controller copies onto each
    ///     claim it creates. A claim planted from anywhere else would be this suite deciding what a
    ///     provider owns.
    /// </remarks>
    /// <summary>
    ///     The volume a planted claim is bound to, or empty — the field a dynamic provisioner writes
    ///     and an operator's controller-owned claim never carries at plant time.
    /// </summary>
    static string VolumeNameOf(string claimJson) =>
        ((JsonNode.Parse(claimJson) as JsonObject)?["spec"] as JsonObject)?["volumeName"]?.GetValue<string>()
        ?? string.Empty;

    static List<(ObjectRef Target, string Json)> ClaimsOf(IEnumerable<KubeCommand> applied) {
        var claims = new List<(ObjectRef, string)>();

        foreach (var command in applied) {
            if (JsonNode.Parse(command.Body) is not JsonObject document
                || document["spec"] is not JsonObject spec
                || spec["volumeClaimTemplates"] is not JsonArray templates) {
                continue;
            }

            var replicas = spec["replicas"]?.GetValue<int>() ?? 1;
            var selector = (spec["selector"] as JsonObject)?["matchLabels"] as JsonObject;

            foreach (var template in templates.OfType<JsonObject>()) {
                if ((template["metadata"] as JsonObject)?["name"]?.GetValue<string>() is not { Length: > 0 } volume) {
                    continue;
                }

                for (var ordinal = 0; ordinal < replicas; ordinal++) {
                    var target = new ObjectRef {
                        Kind = RetainedVolume.ClaimKind,
                        Namespace = command.Target.Namespace,
                        Name = RetainedVolume.NameFor(volume, command.Target.Name, ordinal)
                    };

                    var metadata = new JsonObject { ["name"] = target.Name, ["namespace"] = target.Namespace };

                    if (selector is not null) {
                        metadata["labels"] = selector.DeepClone();
                    }

                    claims.Add(
                        (target,
                            new JsonObject {
                                ["apiVersion"] = "v1", ["kind"] = "PersistentVolumeClaim", ["metadata"] = metadata
                            }.ToJsonString())
                    );
                }
            }
        }

        return claims;
    }

    [Fact]
    public async Task DeleteTearsDownTheDataPlaneAndTheResourceIsGone() {
        ProviderTestCluster<TSource>.Reset();

        Cluster.Registry.TryGetType(Case.Type, out var registration).ShouldBeTrue();
        var recoverable = registration.SoftDeleteDays > 0;

        var accepted = (await CreateAsync("goodbye")).GetValueOrThrow();
        await ConvergeAsync(accepted);

        var objects = ObjectsOf(accepted.Resource.Id, "goodbye");

        // ⚠ Decided BEFORE the delete, from the converged world: afterwards a withdrawn fragment and
        // a removed object both read as "not ours", and the two are opposite contracts.
        var coOwned = objects.Where(target => IsCoOwned(accepted.Resource.Id, target)).ToImmutableArray();

        // ── A clusterless type's bytes, planted so the teardown has something to remove ─────────
        //
        // ⚠ The bytes are the tenant's and no reconciler writes them, so the suite writes one under
        // the type's own prefix — the way it plants an operator's Secret for a cluster-backed type —
        // and reads the prefix back after the teardown. A teardown that converged over a prefix it
        // never emptied is the object-store shape of "still running while the resource says gone".
        // A world that names no prefix keeps no bytes there, and a data plane must name one — see
        // AClusterlessTypeSuppliesTheWorldItConvergesOnto.
        var address = ProviderTestCluster<TSource>.Address("goodbye").WithId(accepted.Resource.Id);
        var clusterless = HasClusterDataPlane ? null : ClusterlessWorldOf(address);
        var storagePrefix = clusterless?.StoragePrefix;

        if (storagePrefix is not null) {
            (await Cluster.Objects.PutAsync(
                storagePrefix + "planted/by-the-suite.bin",
                "planted"u8.ToArray(),
                "application/octet-stream",
                TestContext.Current.CancellationToken
            )).IsSuccess.ShouldBeTrue();
        }

        var deleted = await DeleteAsync("goodbye");
        deleted.IsSuccess.ShouldBeTrue(deleted.Error?.Message);
        deleted.GetValueOrThrow().Resource.ProvisioningState.ShouldBe(ProvisioningState.Deleting);

        var status = await ConvergeAsync(deleted.GetValueOrThrow());
        status.State.ShouldBe(
            OperationState.Succeeded,
            $"the teardown ended {status.State}: {status.Error?.Message}"
        );

        // ── What BOTH contracts promise: the old address stops answering ────────────────────────
        //
        // ⚠ This is the one assertion that does not branch, and for a soft-deletable type it is the
        // sharpest thing the suite says. docs/plan/08 § Soft delete moved the resource out of its
        // resource group rather than flagging it in place precisely so that "a soft-deleted resource
        // that is still readable at its old address" is unreachable by construction — and the 404 is
        // the canonical one, never a 410, because a 410 would tell an unauthorized caller the name was
        // taken.
        var read = await ReadAsync("goodbye");
        read.IsFailure.ShouldBeTrue();
        read.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);

        var entry = await Cluster.Index(ProviderTestCluster<TSource>.Address("goodbye")).GetAsync();

        // ── AND WHAT BOTH CONTRACTS PROMISE SECOND: THE WORKLOAD IS DOWN ────────────────────────
        //
        // ⚠ THIS USED TO BRANCH, AND THE SOFT ARM ASSERTED THE OBJECTS WERE STILL THERE. That arm was
        // written from docs/plan/08 § Soft delete's "handing the data back is the entire feature, so
        // the volumes and the PVCs stay allocated" — a true sentence about the DATA, read as a claim
        // about the objects. Two providers declared a window against it, measured what a tenant
        // actually got, and withdrew: fifteen idle Harbor objects that keep costing money, and a
        // VMUser that keeps authorising writes into a store whose address answers 404. A delete that
        // does not delete is worse than no recovery window.
        //
        // ⚠ What the window preserves is what a teardown does not remove — the name, the resource's
        // stored desired state, the committed quota, and the volumes, because deleting a StatefulSet
        // leaves the claims its volumeClaimTemplate made. Those four are asserted below and by the
        // restore round trip; the running half is asserted gone here, for every type.
        foreach (var target in objects) {
            if (coOwned.Contains(target)) {
                // ⚠ THE CO-WRITER'S TEARDOWN IS A WITHDRAWAL, AND THE OPPOSITE ASSERTION HOLDS: the
                // owner's object is STILL THERE, still carries the owner's seven labels, and no longer
                // carries this resource's fragment. A co-writer that deleted the owner's object would
                // take a tenant's network down with the peering — docs/plan/09 § A second writer on
                // an object, "Teardown withdraws".
                var remaining = Cluster.World.Read(target);

                remaining.ShouldNotBeNull(
                    $"'{target}' is gone after a converged teardown of a resource that did not own it. A "
                    + "co-writer withdraws its fragment; it never deletes the owner's object."
                );

                CarriesFragmentOf(accepted.Resource.Id, target)
                    .ShouldBeFalse($"'{target}' still carries {accepted.Resource.Id:D}'s fragment after a converged teardown");

                Cluster.World.OwnerOf(target).ShouldNotBeNull($"'{target}' lost its owner's labels to the withdrawal");
                continue;
            }

            Cluster.World.Holds(target)
                .ShouldBeFalse(
                    $"'{target}' is still in the cluster after a converged teardown — docs/plan/06 "
                    + "§ Two-phase create: never silently gone while its pods still run and its meter "
                    + "still ticks, and never still running while the resource says it is gone"
                );
        }

        if (clusterless is not null) {
            // The same sentence, read through the clusterless world: a teardown that reported
            // Converged while the grain still answers for the resource — the module still holds it,
            // the catalogue still reads back as an open feed — is a resource that says it is gone
            // and is not.
            (await clusterless.HoldsAsync(TestContext.Current.CancellationToken))
                .ShouldBeFalse(
                    $"the clusterless world still holds '{Case.Type}' after a converged teardown — "
                    + "docs/plan/06 § Two-phase create: never still running while the resource says it is gone"
                );
        }

        if (storagePrefix is not null) {
            (await Cluster.Objects.ListAsync(storagePrefix, TestContext.Current.CancellationToken))
                .GetValueOrThrow()
                .ShouldBeEmpty(
                    $"'{storagePrefix}' still holds objects after a converged teardown. A resource whose "
                    + "address answers 404 while its bytes are still billed against the platform's "
                    + "bucket is the quota and the store disagreeing."
                );
        }

        if (recoverable) {
            // ── The recovery window's contract ──────────────────────────────────────────────────
            entry.GetValueOrThrow()
                .State.ShouldBe(
                    IndexEntryState.SoftDeleted,
                    "the name is held for the whole window — a name taken by somebody else leaves a "
                    + "restore with nowhere to go"
                );

            Cluster.Relations.Edges.ShouldContainKey(
                accepted.Resource.Id,
                "the resource is never parentless: the edge moves to the subscription while deleted "
                + "rather than being dropped, so a resource nobody can see cannot happen during the "
                + "recovery window either"
            );

            // ── And it comes back, which is the half that makes it a window ─────────────────────
            //
            // ⚠ WITHOUT THIS THE ARM ABOVE WOULD PASS FOR A SLOWER DELETE. Everything asserted so
            // far is also true of a type that tears down, holds the name for seven days and can
            // never hand anything back — which is not soft delete, it is destruction with a wait in
            // front of it. The objects returning is the only assertion that separates them, and it
            // is driven through the manager rather than by calling the reconciler, so it measures
            // what a tenant would get rather than what the provider is capable of.
            var restored = await RestoreAsync("goodbye");
            restored.IsSuccess.ShouldBeTrue(restored.Error?.Message);

            var back = await ConvergeAsync(restored.GetValueOrThrow());
            back.State.ShouldBe(
                OperationState.Succeeded,
                $"the restore ended {back.State}: {back.Error?.Message}"
            );

            foreach (var target in objects) {
                Cluster.World.Holds(target)
                    .ShouldBeTrue(
                        $"'{target}' did not come back, and this type declares a "
                        + $"{registration.SoftDeleteDays.ToString(CultureInfo.InvariantCulture)}-day "
                        + "recovery window. A restore re-applies the desired state the park kept — "
                        + "docs/plan/08 § Soft delete"
                    );

                if (coOwned.Contains(target)) {
                    // The co-writer's half of the same sentence: the object never went, so what has
                    // to come back is the fragment.
                    CarriesFragmentOf(accepted.Resource.Id, target)
                        .ShouldBeTrue($"'{target}' does not carry {accepted.Resource.Id:D}'s fragment again after the restore");
                }
            }

            (await ReadAsync("goodbye")).IsSuccess.ShouldBeTrue("and the old address answers again");

            return;
        }

        // ── The hard delete's contract ──────────────────────────────────────────────────────────
        entry.GetValueOrThrow().State.ShouldBe(IndexEntryState.Free, "the name comes back");

        // ⚠ AND THE AUTHORIZATION EDGE COMES BACK TOO. docs/plan/08 § The write path, end to end's
        // step 8 writes `resource:{id}#parent@resourceGroup:{sub}-{rg}` so the resource is visible to
        // whoever holds a role on its group; a delete that left it behind would be a row per resource
        // ever deleted, in every tenant, granting nothing and noticed by nobody.
        Cluster.Relations.Edges.ShouldNotContainKey(
            accepted.Resource.Id,
            "the resource is gone and its ReBAC parent tuple is not"
        );
    }

    [Fact]
    public async Task ACreateWritesTheParentEdgeBeforeItWritesDurableState() {
        // ⚠ THE STEP THAT MAKES A CREATED RESOURCE VISIBLE TO ITS CREATOR, AND ITS POSITION.
        //
        // A conformance run doubles the ReBAC engine on purpose — the isolation suite is where the
        // real one is driven — so what is asserted here is what a provider's lifecycle depends on:
        // that the edge is written at all, and that it is written BEFORE the durable resource. Any
        // other order leaves a window in which the resource exists and nobody can read it, and a silo
        // lost inside that window leaves it that way permanently.
        ProviderTestCluster<TSource>.Reset();

        var accepted = (await CreateAsync("linked")).GetValueOrThrow();

        Cluster.Relations.Edges.ShouldContainKey(
            accepted.Resource.Id,
            "the create wrote no parent edge, so the resource inherits nothing from its resource "
            + "group and is invisible to the caller who just created it"
        );

        var reached = accepted.Trace.Reached;

        reached.IndexOf(WriteStep.LinkParent)
            .ShouldBeLessThan(
                reached.IndexOf(WriteStep.SubmitDesired),
                "the parent edge was written after the durable resource"
            );
    }

    // ── create with another tenant's ids → 404 ──────────────────────────────────────────────────

    [Fact]
    public async Task CreatingWithAnotherTenantsIdsIs404AndNothingIsApplied() {
        // ⚠ 404, never 403 — docs/plan/07 § The enforcement seam. This is the conformance suite's one
        // isolation assertion; the whole attack surface is CyberCloud.Isolation's.
        ProviderTestCluster<TSource>.Reset();

        var theirs = ProviderTestCluster<TSource>.Address(
            "not-yours",
            ConformanceIds.OtherTenant,
            ConformanceIds.OtherSubscription
        );

        var refused = await Cluster.Manager.WriteAsync(
            new() {
                Path = theirs.Path,
                ApiVersion = Case.ApiVersion,
                Verb = WriteVerb.Put,
                Body = Body(),
                Caller = ProviderTestCluster<TSource>.Caller()
            },
            TestContext.Current.CancellationToken
        );

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
        refused.Error.Code.ShouldNotBe(ErrorCode.AuthorizationFailed);

        Cluster.World.Applied.ShouldBeEmpty("a refused write must never reach a provider");
    }

    // ── a child whose parent does not exist → the same 404 ──────────────────────────────────────

    [Fact]
    public async Task CreatingUnderAParentThatDoesNotExistIsTheSame404AsAnAbsentResource() {
        // ⚠ THE CHECK THAT MAKES A CHILD TYPE HARD, ASSERTED WHERE IT IS CHEAPEST TO ASSERT.
        //
        // docs/plan/12 § Child resources chose the interleaved address so a child's ReBAC `parent`
        // edge could name its parent. A child created under a parent that does not exist gets an edge
        // pointing at nothing — it inherits permission from nothing, and it is invisible to the
        // caller who just created it. ResourceManagerService.ResolveAsync closes that by resolving
        // the parent's index binding on every CREATE.
        //
        // ⚠ AND THE ANSWER IS THE SAME 404, WHICH IS THE HALF THAT IS EASY TO GET WRONG. A distinct
        // ParentNotFound would be an existence oracle: this check runs BEFORE the enforcement seam,
        // so a caller who may write in a resource group but may not read a particular parent would
        // learn, one probe at a time, which parent names are live. docs/plan/07 § The enforcement
        // seam. Compared here against the 404 for a resource that is simply absent, modulo the path
        // the caller themselves supplied — the same comparison
        // CrossTenantVerbTests.TheAnswerForAnInvisibleResourceIsIdenticalToTheAnswerForAnAbsentOne
        // makes one level up.
        if (Case.Type.Depth == 1) {
            Assert.Skip(
                $"SKIPPED — '{Case.Type}' is a top-level type, so its parent is the resource group "
                + "and there is no parent-existence check to fire. This assertion is not vacuous for "
                + "it; it is inapplicable, and saying so is how a Docker-free reader can tell which "
                + "providers in a run actually exercised the child grammar."
            );
        }

        ProviderTestCluster<TSource>.Reset();

        // ⚠ The SAME type at the SAME depth, hung off ancestors nothing ever created. Not a shallower
        // address and not a malformed one — the only thing wrong with this path is that its parent
        // is not there, which is the one fact the assertion is about.
        var orphaned = ProviderTestCluster<TSource>.Address("orphan-child") with {
            ParentNames = string.Join(
                '/',
                Enumerable.Range(0, Case.Type.Depth - 1).Select(level => "no-such-parent-" + level)
            )
        };

        var refused = await Cluster.Manager.WriteAsync(
            Request(orphaned),
            TestContext.Current.CancellationToken
        );

        refused.IsFailure.ShouldBeTrue(
            $"'{orphaned.Path}' was created under a parent that does not exist, so its ReBAC parent "
            + "edge points at nothing and it inherits permission from nothing"
        );

        refused.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);

        refused.Error.Code.ShouldNotBe(
            ErrorCode.AuthorizationFailed,
            "403 here would confirm the parent exists to somebody who cannot read it"
        );

        // The reference answer: a GET for a child at a perfectly good address that was never created.
        var absent = ProviderTestCluster<TSource>.Address("never-created-child");

        var onAbsent = await Cluster.Manager.ReadAsync(
            new() { Path = absent.Path, ApiVersion = Case.ApiVersion, Caller = ProviderTestCluster<TSource>.Caller() },
            TestContext.Current.CancellationToken
        );

        onAbsent.IsFailure.ShouldBeTrue();
        onAbsent.Error!.Code.ShouldBe(refused.Error.Code);
        onAbsent.Error.Target.ShouldBe(refused.Error.Target);

        // ⚠ THE MESSAGE, NOT ONLY THE CODE. Two 404s whose wording differs are still a way to ask
        // "is this parent real". The one legitimate difference is that each names the path it was
        // given — and NEITHER may name the parent's path, which is the string the caller was guessing
        // at.
        refused.Error.Message.Replace(orphaned.Path, "PATH", StringComparison.Ordinal)
            .ShouldBe(onAbsent.Error.Message.Replace(absent.Path, "PATH", StringComparison.Ordinal));

        // ⚠ AND "the message never names the parent's path" IS NOT ASSERTABLE AS WRITTEN, WHICH IS A
        // FACT ABOUT THE GRAMMAR RATHER THAN A GAP HERE. ResourceManagerService's own remarks say the
        // refusal "names the CHILD's path … and never the parent's". Under the interleaved address the
        // parent's path is a literal PREFIX of the child's —
        // `…/probes/{p}` inside `…/probes/{p}/samples/{c}` — so any message quoting the path the
        // caller supplied contains the parent's path too, and a substring check for it fails on a
        // correct implementation. It is also not a leak: the caller wrote the whole child path, so the
        // parent's is a string they already had. What actually has to hold is that the refusal carries
        // NOTHING BEYOND the path they supplied, and that is what the comparison above establishes —
        // mask the caller's own path out of both messages and they are the same sentence.
        refused.Error.Message.ShouldContain(
            orphaned.Path,
            customMessage: "the refusal does not say which path was refused"
        );

        // And nothing happened: no name claimed, nothing applied.
        (await Cluster.Index(orphaned).GetAsync()).GetValueOrThrow().State.ShouldBe(IndexEntryState.Free);
        Cluster.World.Applied.ShouldBeEmpty("a refused create reached a provider");
    }

    // ── delete while an operation is running → 409 ──────────────────────────────────────────────

    [Fact]
    public async Task DeletingWhileAnOperationIsRunningIs409AndTheNameIsNotReleased() {
        // ⚠ docs/plan/03 § Providers names this one directly. It is also the only item on that list
        // that the resource manager did not implement before this suite was written: the single-writer
        // guard was on SubmitDesiredAsync and not on BeginDeleteAsync, so a DELETE arriving mid-create
        // flipped a Creating resource to Deleting while the create's pass was still applying.
        ProviderTestCluster<TSource>.Reset();

        // The cluster is unreachable, so the create stays InProgress and the operation stays live.
        Cluster.World.Suspended = true;

        var accepted = (await CreateAsync("racing-delete")).GetValueOrThrow();

        // ⚠ A clusterless type has nothing to suspend — its first pass converges — so its create is
        // left UNDRIVEN, which is the same state a silo that has not yet picked the operation up
        // leaves it in. The guard under test is the resource's, not the pass's: a DELETE arriving
        // between the 202 and the first pass is exactly the race docs/plan/03 § Providers names.
        // (This used to skip for a clusterless type, on the grounds that no data plane could hold a
        // pass open on demand; the undriven create is the shape that needs no such data plane.)
        var operation = Cluster.Operation(ConformanceIds.Tenant, accepted.OperationId);
        var status = HasClusterDataPlane ? await operation.DriveAsync() : await operation.GetAsync();

        status.GetValueOrThrow()
            .IsTerminal.ShouldBeFalse("the create must still be running for this test to mean anything");

        var refused = await DeleteAsync("racing-delete");

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.OperationInProgress);
        refused.Error.Message.ShouldContain(accepted.OperationId.ToString("D"));

        var entry = await Cluster.Index(ProviderTestCluster<TSource>.Address("racing-delete")).GetAsync();
        entry.GetValueOrThrow()
            .State.ShouldBe(
                IndexEntryState.Confirmed,
                "the delete was refused, so the name must still be bound — releasing it first and then "
                + "refusing would hand somebody else a name that is still in use"
            );

        Cluster.World.Suspended = false;
    }

    // ── an API server refusal ends the operation now, not in an hour ────────────────────────────

    [Fact]
    public async Task AnAdmissionRefusalFailsTheOperationOnTheFirstPassAndKeepsTheClustersOwnWords() {
        // ⚠ THE HOUR-LONG-WAIT TEST, and it is a suite-wide assertion because it was a suite-wide bug:
        // every reconciler in the tree passed `retryable: true` for every failed apply, under a comment
        // claiming the connection returned "a non-retryable code" for a body the API server rejects.
        // Nothing did, until KubeFailures.Classify — and once something did, no reconciler read it.
        //
        // What that costs is the whole reason this test names the ending rather than the mechanism: an
        // admission decision does not change between passes, so the ladder in ReconcileSchedule runs
        // 10s → 30s → 2min → 10min for sixty minutes and the tenant is then handed an OperationTimeout
        // — in place of the policy's own message, which was available on the first pass.
        ProviderTestCluster<TSource>.Reset();

        // The cluster ANSWERED. Not Suspended ("we cannot reach it") and not Conflict ("somebody else
        // owns that field") — both of those are states a later pass really does find changed.
        Cluster.World.RefuseWith = ErrorCode.PolicyViolation;

        var accepted = (await CreateAsync("refused-by-admission")).GetValueOrThrow();

        if (!HasClusterDataPlane) {
            // ⚠ THE INVERSE ASSERTION, AND NOT A SKIP. An admission refusal is a cluster's answer,
            // and the harness has no way to make a module or a platform host refuse a grain write by
            // policy — so what a clusterless type can be held to is the other direction. A cluster
            // that refuses everything is invisible to a type that declared no cluster: the operation
            // converges as if the fake were healthy, because the driver hands the reconciler no
            // connection and skips the namespace it would otherwise have applied into the refusing
            // cluster. A clusterless type whose create FAILED here would be one that reached a
            // cluster after all.
            var converged = await ConvergeAsync(accepted);
            converged.State.ShouldBe(OperationState.Succeeded, $"the operation ended {converged.State}: {converged.Error?.Message}");
            AssertNothingReachedTheCluster();
            Cluster.World.RefuseWith = null;
            return;
        }

        var status = (await Cluster.Operation(ConformanceIds.Tenant, accepted.OperationId).DriveAsync())
            .GetValueOrThrow();

        // ⚠ ONE pass. This is OperationGrain's `Failed when !Retryable` branch; the branch it must not
        // take is the ScheduleAsync default that every other Failed outcome falls into.
        status.Attempts.ShouldBe(1);

        status.State.ShouldBe(
            OperationState.Failed,
            $"the operation ended {status.State} on its first pass against a cluster that refused the "
            + "apply outright — a refusal that is rescheduled is a refusal the tenant learns about an "
            + "hour late, as an OperationTimeout"
        );

        status.Error!.Code.ShouldBe(ErrorCode.PolicyViolation, status.Error.Message);
        status.Error.Code.ShouldNotBe(ErrorCode.OperationTimeout);

        // And the sentence the tenant reads is the cluster's, not ours. KubeRefusal's whole shape is
        // built on an admission message being the only useful diagnostic there is.
        status.Error.Message.ShouldContain("admission webhook");

        Cluster.World.RefuseWith = null;
    }

    [Fact]
    public async Task AClusterThatDidNotAnswerIsStillRetried() {
        // ⚠ The other half, and the more expensive one to get wrong. ErrorCode.InternalError is what a
        // transport fault arrives under — "the cluster did not answer" — and it is by far the commonest
        // failure a reconciler sees. A terminal-by-default rule would end an operation on a dropped
        // connection, so the rule has to be a named list of refusals rather than a named list of
        // hiccups. Asserted per provider because the list is read through each reconciler's own path.
        ProviderTestCluster<TSource>.Reset();

        Cluster.World.RefuseWith = ErrorCode.InternalError;

        var accepted = (await CreateAsync("cluster-fell-over")).GetValueOrThrow();

        if (!HasClusterDataPlane) {
            // The same inverse as the admission case: a transport fault is a cluster's silence, an
            // in-process silo has no connection the harness can drop, and a cluster that did not
            // answer is a cluster this type never asked.
            var converged = await ConvergeAsync(accepted);
            converged.State.ShouldBe(OperationState.Succeeded, $"the operation ended {converged.State}: {converged.Error?.Message}");
            AssertNothingReachedTheCluster();
            Cluster.World.RefuseWith = null;
            return;
        }

        var status = (await Cluster.Operation(ConformanceIds.Tenant, accepted.OperationId).DriveAsync())
            .GetValueOrThrow();

        status.IsTerminal.ShouldBeFalse(
            $"the operation ended {status.State} on a failure that means the cluster did not answer; "
            + "the next pass is what a dropped connection deserves"
        );

        // It came back rather than ended, which is the retry the ladder exists for.
        Cluster.World.RefuseWith = null;

        var recovered = await ConvergeAsync(accepted);

        recovered.State.ShouldBe(
            OperationState.Succeeded,
            $"the operation ended {recovered.State}: {recovered.Error?.Message}"
        );
    }

    // ── reconcile after a manual cluster mutation → drift corrected ─────────────────────────────

    [Fact]
    public async Task DriftIsCorrectedWhenSomebodyDeletesTheObjectsByHand() {
        // "kubectl delete"d production, in the fake. The next reconcile pass must put it back, and it
        // must do so because it LOOKED, not because it remembered.
        ProviderTestCluster<TSource>.Reset();

        var accepted = (await CreateAsync("drifting")).GetValueOrThrow();
        await ConvergeAsync(accepted);

        if (!HasClusterDataPlane) {
            // The same "kubectl delete", against the clusterless world: what it holds for the
            // resource is removed behind the reconciler's back — the module's RemoveAsync, the data
            // plane's BreakAsync — and the next pass is read through the world, never through the
            // reconciler's own observation.
            //
            // ⚠ CORRECTED OR NOTICED, AND WHICH ONE IS THE WORLD'S TO SAY. A module's grains are a
            // function of the desired body, like a deleted ConfigMap, so the pass must put them
            // back because it LOOKED. A catalogue somebody closed cannot be re-opened from the body,
            // because what it held was the tenant's and the body never carried it — so for a data
            // plane what the manager's reconcile path owes is clause 4: after the break, a pass that
            // reports Converged over a world that does not match is an assumption. The stronger
            // assertion is made wherever the world says it can be — ClusterlessWorld.RebuildsFromTheBody.
            var world = ClusterlessWorldOf(AddressOf(accepted.Resource.Id, "drifting"));
            var ct = TestContext.Current.CancellationToken;

            await world.BreakAsync(Body(), ct);
            (await world.HoldsAsync(ct)).ShouldBeFalse($"{Case.DisplayName}'s world did not remove what it holds; the drift test would measure nothing");

            var pass = await ReconcileOnceAsync(accepted.Resource.Id, "drifting");

            if (world.RebuildsFromTheBody) {
                pass.Kind.ShouldNotBe(ReconcileOutcomeKind.Failed, pass.ToString());

                (await world.HoldsAsync(ct)).ShouldBeTrue($"'{Case.Type}' was not put back");
                (await world.MatchesAsync(Body(), ct)).ShouldBeTrue("what was put back does not carry the desired body");
            } else if (pass.IsConverged) {
                (await world.MatchesAsync(Body(), ct))
                    .ShouldBeTrue("the pass reported Converged over a data plane that does not match the desired body");
            }

            AssertNothingReachedTheCluster();
            return;
        }

        var objects = ObjectsOf(accepted.Resource.Id, "drifting");
        objects.ShouldNotBeEmpty();

        var coOwned = objects.Where(target => IsCoOwned(accepted.Resource.Id, target)).ToImmutableArray();

        foreach (var target in objects) {
            // A kubectl delete of an owned object; a kubectl edit stripping this resource's slice off a
            // co-owned one — see BreakBehindTheirBack for why those are the same case.
            BreakBehindTheirBack(accepted.Resource.Id, target);
        }

        foreach (var target in coOwned) {
            MatchesDesired(accepted.Resource.Id, "drifting", target, Cluster.World.Read(target)!, Body())
                .ShouldBeFalse($"stripping {accepted.Resource.Id:D}'s fragment off '{target}' left it matching the desired body, so the break measured nothing");
        }

        // A fresh write with the same body is a no-op at the grain, so the drift is corrected by the
        // reconcile path rather than by a new submission — which is what the hourly per-cluster scan
        // of docs/plan/08 § The reconcile loop would do when it pokes a diverged resource.
        var repaired = await ReconcileOnceAsync(accepted.Resource.Id, "drifting");

        repaired.Kind.ShouldNotBe(ReconcileOutcomeKind.Failed, repaired.ToString());

        foreach (var target in objects) {
            Cluster.World.Holds(target).ShouldBeTrue($"'{target}' was not put back");
            MatchesDesired(accepted.Resource.Id, "drifting", target, Cluster.World.Read(target)!, Body())
                .ShouldBeTrue();
        }

        if (coOwned.IsEmpty) {
            return;
        }

        // ── AND THE OWNER'S DELETE WINS, which is the co-writer's other drift case ──────────────
        //
        // ⚠ Somebody kubectl-deleted the OWNER's object. The reading that separates a co-writer from
        // an owner is what happens next: an owner puts its object back; a co-writer must NOT, because
        // what it would create is the owner's object under the owner's name with none of the owner's
        // labels or spec — measured against a real API server in CoOwnedApplyTests
        // .TheApiServerDoesNotHoldTheLockAgainstAnAbsentObjectWhichIsWhyTheClientRefuses. So the pass
        // may wait, and may fail, and may not report Converged, and the object may not reappear.
        foreach (var target in coOwned) {
            Cluster.World.RemoveBehindTheirBack(target).ShouldBeTrue();
        }

        var refused = await ReconcileOnceAsync(accepted.Resource.Id, "drifting");

        refused.IsConverged.ShouldBeFalse(
            $"{Case.DisplayName} reported Converged while an object it co-writes onto was gone: {refused}"
        );

        foreach (var target in coOwned) {
            Cluster.World.Holds(target)
                .ShouldBeFalse(
                    $"'{target}' was re-created by a resource that does not own it. A co-writer never "
                    + "creates the owner's object — the owner's delete wins."
                );
        }
    }

    [Fact]
    public async Task AHandEditIsOverwrittenOnTheNextPass() {
        ProviderTestCluster<TSource>.Reset();

        var accepted = (await CreateAsync("hand-edited")).GetValueOrThrow();
        await ConvergeAsync(accepted);

        if (!HasClusterDataPlane) {
            var world = ClusterlessWorldOf(AddressOf(accepted.Resource.Id, "hand-edited"));
            var ct = TestContext.Current.CancellationToken;

            if (world.CorruptAsync is not { } corrupt) {
                Assert.Skip(
                    $"SKIPPED, AND SAYING SO — {Case.DisplayName} has no cluster object for anyone to "
                    + "hand-edit and its world describes no hand edit of its own. A DataPlane is "
                    + "broken the one way it describes, and "
                    + "DriftIsCorrectedWhenSomebodyDeletesTheObjectsByHand is where that break is "
                    + "driven through the manager's reconcile path; a module supplies CorruptAsync "
                    + "and is held to it here."
                );

                return;
            }

            await corrupt(Body(), ct);
            (await world.MatchesAsync(Body(), ct)).ShouldBeFalse("the world's hand edit changed nothing; the test would measure nothing");

            var put = await ReconcileOnceAsync(accepted.Resource.Id, "hand-edited");
            put.Kind.ShouldNotBe(ReconcileOutcomeKind.Failed, put.ToString());

            (await world.MatchesAsync(Body(), ct)).ShouldBeTrue("the hand edit survived a reconcile pass");

            return;
        }

        foreach (var target in ObjectsOf(accepted.Resource.Id, "hand-edited")) {
            if (IsCoOwned(accepted.Resource.Id, target)) {
                // ⚠ A HAND EDIT OF THE CO-WRITER'S ENTRIES, WITH THE OWNER'S IDENTITY LEFT ALONE. The
                // owned branch below replaces the whole object, labels included — and a co-owned
                // object with its owner's labels gone is one the co-writer must REFUSE to write onto
                // (the seven are how it knows the object is this platform's), which is right and is
                // not drift repair. What a tenant with kubectl edits is the values, and that is what
                // is edited: every leaf this resource's fragment set, overwritten, the bookkeeping
                // left in place so the edit reads as a tenant's rather than as a withdrawal.
                Cluster.World.CorruptFragmentBehindTheirBack(target, accepted.Resource.Id, "hand-edited")
                    .ShouldBeTrue($"'{target}' carried no fragment of {accepted.Resource.Id:D}'s to edit");

                MatchesDesired(accepted.Resource.Id, "hand-edited", target, Cluster.World.Read(target)!, Body())
                    .ShouldBeFalse($"the hand edit of '{target}' changed nothing the case compares, so the test would measure nothing");

                continue;
            }

            Cluster.World.MutateBehindTheirBack(target, """{"metadata":{"name":"hand-edited"},"data":{}}""");
        }

        var repaired = await ReconcileOnceAsync(accepted.Resource.Id, "hand-edited");
        repaired.Kind.ShouldNotBe(ReconcileOutcomeKind.Failed, repaired.ToString());

        foreach (var target in ObjectsOf(accepted.Resource.Id, "hand-edited")) {
            MatchesDesired(accepted.Resource.Id, "hand-edited", target, Cluster.World.Read(target)!, Body())
                .ShouldBeTrue("the hand edit survived a reconcile pass");
        }
    }

    // ── The reconciler contract, all four clauses ───────────────────────────────────────────────

    [Fact]
    public async Task TheReconcilerSatisfiesTheFourClauseContract() {
        // ⚠ RUN WITH A REAL ConformanceWorld, so clause 4 is CHECKED rather than reported as skipped.
        // ReconcilerConformance adds a finding when no world is supplied, precisely so that a provider
        // cannot pass clause 4 by not offering the harness a way to test it.
        ProviderTestCluster<TSource>.Reset();

        var accepted = (await CreateAsync("clauses")).GetValueOrThrow();
        var address = ProviderTestCluster<TSource>.Address("clauses").WithId(accepted.Resource.Id);
        var ns = ReconcileDriver.NamespaceFor(address);
        var objects = Case.Objects(address, ns);

        using var desired = JsonDocument.Parse(Body());

        var reconciler = Reconciler();
        var log = new RecordingLog();
        var (view, watch) = Cluster.Views.For(address);

        var context = new ReconcileContext(
            address,
            Case.ApiVersion,
            desired.RootElement,
            null,
            ns,
            // ⚠ null for a clusterless type, which is what ReconcileDriver hands one — a reconciler
            // that reached for the connection anyway would be a reconciler the driver could not run.
            HasClusterDataPlane ? Cluster.World : null,
            // ⚠ THE HARNESS'S VAULT, NOT THE REFUSING DEFAULT, AND THE SAME INSTANCE ON BOTH MEMBERS.
            // This context is built BY HAND rather than by ReconcileDriver, so nothing fills
            // SecretWriter in for it — and a provider whose create mints a credential then fails every
            // clause for a wiring reason. The refusal names the member to set, which is how this was
            // found rather than guessed.
            Cluster.Vault,
            log
            // ⚠ AND THE HARNESS'S OBJECT STORE, for the same reason and the same way: a type whose
            // teardown empties a storage prefix cannot pass clause 3 against RefusingObjectStore.
            // ⚠ AND THE CROSS-RESOURCE SEAM, bound to this address the way the driver binds it. A
            // type that reads another resource — the vault — would otherwise meet the refusing
            // default and fail clause 4 for a reason that is the harness's. See ProviderTestCluster.Views.
        ) { SecretWriter = Cluster.Vault, Objects = Cluster.Objects, View = view, Watch = watch };

        // ⚠ THE WORLD IS THE CLUSTER FOR A TYPE THAT DECLARED ONE AND THE CLUSTERLESS WORLD — the
        // module its case source registered, or the DataPlane its case built — FOR A TYPE THAT DID
        // NOT. Clause 4 is checked the same way in both: break, run a pass, and fail a pass that
        // reports Converged over a world that does not match, read around the reconciler exactly as
        // the objects are. What differs is who can reach the world — the harness owns the fake API
        // server; only the case knows its grain.
        var world = HasClusterDataPlane
            ? new ConformanceWorld(
                BreakAsync: () => {
                    foreach (var target in objects) {
                        // ⚠ Decided when the break runs, not when the world is built: the run above
                        // converges the reconciler first, and an object is co-owned by what it carries
                        // once converged. See BreakBehindTheirBack.
                        BreakBehindTheirBack(accepted.Resource.Id, target);
                    }

                    return Task.CompletedTask;
                },
                MatchesDesiredAsync: () => Task.FromResult(
                    objects.Length > 0
                    && objects.All(target => Cluster.World.Read(target) is { } json
                        && MatchesDesired(accepted.Resource.Id, "clauses", target, json, Body())
                    )
                )
            )
            : ClusterlessWorldOf(address).ForClauseFour(Body(), TestContext.Current.CancellationToken);

        var report = await ReconcilerConformance.RunAsync(
            reconciler,
            context,
            world,
            Cluster.Clock,
            TestContext.Current.CancellationToken
        );

        report.Conforms.ShouldBeTrue(report.ToString());

        log.Entries.ShouldNotBeEmpty(
            "a reconciler that reports nothing turns a four-minute provision into a spinner — "
            + "docs/plan/08 § The reconcile loop"
        );
    }

    // ── The verb grammar — docs/plan/08 § The write path, end to end ────────────────────────────

    [Fact]
    public async Task ARepeatedIdenticalPutIsANoOpAndStartsNoOperation() {
        ProviderTestCluster<TSource>.Reset();

        var first = (await CreateAsync("idempotent")).GetValueOrThrow();
        await ConvergeAsync(first);

        var before = (await ReadAsync("idempotent")).GetValueOrThrow();
        var again = (await CreateAsync("idempotent")).GetValueOrThrow();

        again.NoOp.ShouldBeTrue("PUT with the same body on an existing resource is a no-op");
        again.OperationId.ShouldBe(Guid.Empty, "a no-op starts no operation");

        var after = (await ReadAsync("idempotent")).GetValueOrThrow();
        after.Etag.ShouldBe(before.Etag, "an etag that moved on a no-op would invalidate a concurrent reader");
        after.ModifiedAt.ShouldBe(before.ModifiedAt);
    }

    [Fact]
    public async Task APutWithADifferentBodyReachesTheClusterAsWell() {
        ProviderTestCluster<TSource>.Reset();

        var first = (await CreateAsync("updated")).GetValueOrThrow();
        await ConvergeAsync(first);

        var changed = await Cluster.Manager.WriteAsync(
            Request(ProviderTestCluster<TSource>.Address("updated"), body: ChangedBody()),
            TestContext.Current.CancellationToken
        );

        changed.IsSuccess.ShouldBeTrue(changed.Error?.Message);
        changed.GetValueOrThrow().NoOp.ShouldBeFalse();
        changed.GetValueOrThrow().Resource.ProvisioningState.ShouldBe(ProvisioningState.Updating);

        await ConvergeAsync(changed.GetValueOrThrow());

        if (!HasClusterDataPlane) {
            // The update converged through the same path and still reached no cluster. What it did
            // reach is the clusterless world, which reads back as the CHANGED body afterwards — a
            // module holds the new locale or the new threshold; the changed body of a feed alters
            // nothing the catalogue holds, so "as desired" is the open catalogue itself, and the
            // assertion is that the update did not close it. An update that stopped at the resource
            // grain is an update the world never saw.
            AssertNothingReachedTheCluster();

            (await ClusterlessWorldOf(AddressOf(first.Resource.Id, "updated")).MatchesAsync(ChangedBody(), TestContext.Current.CancellationToken))
                .ShouldBeTrue("the clusterless world does not read back as the changed body after an update converged");

            return;
        }

        foreach (var target in ObjectsOf(first.Resource.Id, "updated")) {
            MatchesDesired(first.Resource.Id, "updated", target, Cluster.World.Read(target)!, ChangedBody())
                .ShouldBeTrue("an update that stopped at the grain is an update the tenant cannot see");
        }
    }

    [Fact]
    public async Task PostNeverCreates() {
        // docs/plan/08 § The write path, end to end: POST "appears only for actions on an existing
        // resource … never for creation." Checked by the manager, not by each action's handler.
        if (Case.ActionName.Length == 0) {
            Assert.Skip(
                $"SKIPPED, AND SAYING SO — {Case.DisplayName} declares NO ACTION, so there is no POST "
                + "for this assertion to make. That is a deliberate declaration rather than an "
                + "omission (see the provider's own remarks), and the skip is loud because a silent "
                + "pass here would read as 'the POST half of the verb grammar was checked'."
            );

            return;
        }

        ProviderTestCluster<TSource>.Reset();

        var address = ProviderTestCluster<TSource>.Address("never-created");

        var action = await Cluster.Manager.ActionAsync(
            new() {
                Path = address.Path,
                ApiVersion = Case.ApiVersion,
                Verb = WriteVerb.Post,
                Action = Case.ActionName,
                Caller = ProviderTestCluster<TSource>.Caller()
            },
            TestContext.Current.CancellationToken
        );

        action.IsFailure.ShouldBeTrue();
        action.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);

        (await Cluster.Index(address).GetAsync()).GetValueOrThrow().State.ShouldBe(IndexEntryState.Free);
        Cluster.World.Applied.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnActionOnAnExistingResourceIsAccepted() {
        if (Case.ActionName.Length == 0) {
            Assert.Skip(
                $"SKIPPED, AND SAYING SO — {Case.DisplayName} declares NO ACTION, so there is no POST "
                + "for this assertion to make. That is a deliberate declaration rather than an "
                + "omission (see the provider's own remarks), and the skip is loud because a silent "
                + "pass here would read as 'the POST half of the verb grammar was checked'."
            );

            return;
        }

        ProviderTestCluster<TSource>.Reset();

        var created = (await CreateAsync("actionable")).GetValueOrThrow();
        await ConvergeAsync(created);

        // ⚠ AFTER CONVERGENCE AND BEFORE THE ACTION, WHICH IS WHERE THE REAL ONES APPEAR. An
        // operator writes its generated Secret while bringing the engine up, so a handler reading one
        // is reading something that exists by the time a caller can invoke the action and does not
        // exist while the reconciler is still working. Placing it earlier would let a reconciler pass
        // that depended on it; placing it later than this would test a state no caller can reach.
        // A no-op for every type whose credential this platform mints itself — see
        // ProviderConformanceCase.OperatorWritten.
        PlaceOperatorObjects(created.Resource.Id, "actionable");

        var action = await Cluster.Manager.ActionAsync(
            new() {
                Path = ProviderTestCluster<TSource>.Address("actionable").Path,
                ApiVersion = Case.ApiVersion,
                Verb = WriteVerb.Post,
                Action = Case.ActionName,
                Caller = ProviderTestCluster<TSource>.Caller()
            },
            TestContext.Current.CancellationToken
        );

        // ⚠ WHAT "ACCEPTED" MEANS DEPENDS ON WHAT THE PROVIDER DECLARED, AND THE OLD SHAPE OF THIS
        // ASSERTION IS EXACTLY THE "GREEN BECAUSE IT ASKED LESS" PATTERN.
        //
        // It used to be `IsSuccess` plus `OperationId != Guid.Empty` — which every action in the
        // catalogue satisfied, because no action could RUN. `IProviderBuilder.Action` took no handler,
        // so a POST started an operation and OperationGrain drove the resource type's RECONCILER for
        // it. The suite watched an operation appear and called that acceptance; what it was really
        // watching was a reconcile pass with an action's name on it.
        //
        // So this now branches on the registration, and each branch is a real statement:
        //
        //   • a synchronous action WITH a handler answers 200 — its own body, no operation, and the
        //     body validates against the response shape the provider published;
        //   • an action with NO handler is REFUSED, by name. That is the honest answer for a
        //     declaration that reaches the OpenAPI document, the SDK and the CLI and cannot execute,
        //     and it is what most of the catalogue still is;
        //   • a long-running action still answers 202 with something to poll.
        //
        // ProviderBuilder.Action refuses `longRunning` together with a handler, so the fourth cell of
        // that table is unreachable and is not tested here.
        Cluster.Registry.TryGetType(Case.Type, out var registration).ShouldBeTrue();
        registration.TryGetAction(Case.ActionName, out var declared)
            .ShouldBeTrue($"'{Case.ActionName}' is the case's action and the provider does not declare it");

        // ⚠ LONG-RUNNING IS ASKED FIRST, AND THE ORDER IS THE ASSERTION.
        //
        // The table has four cells and only three are reachable, but they do not partition the way
        // "handler or no handler" suggests. `ResourceManagerService` refuses a missing handler only on
        // the SYNCHRONOUS branch — a long-running action never had a handler and never needed one,
        // because it starts an operation and the operation grain drives the work. Asking
        // `HandlerType is null` first swallows the long-running-without-a-handler cell into the refusal
        // branch and asserts a refusal the manager is right not to give.
        //
        // That is not hypothetical: `CyberCloud.ContainerService/agentPools` declares
        // `upgradeNodeImage` as `longRunning: true` with no handler, which is the correct declaration
        // for a node-image roll, and it was the first case to reach this branch.
        if (declared.LongRunning) {
            action.IsSuccess.ShouldBeTrue(action.Error?.Message);

            var started = action.GetValueOrThrow();
            started.Completed.ShouldBeFalse();
            started.OperationId.ShouldNotBe(Guid.Empty);

            return;
        }

        if (declared.HandlerType is null) {
            action.IsFailure.ShouldBeTrue(
                "a synchronous action with no handler answered success. Before handlers existed that "
                + "was a 202 for an operation that re-ran the reconciler; a refusal naming the action "
                + "is worth more than work the caller did not ask for."
            );

            action.Error!.Message.ShouldContain(Case.ActionName);
            action.Error.Message.ShouldContain("handler");

            return;
        }

        action.IsSuccess.ShouldBeTrue(action.Error?.Message);
        var accepted = action.GetValueOrThrow();

        accepted.Completed.ShouldBeTrue(
            "a synchronous action answers 200 with its own body; Completed is what the gateway keys "
            + "its status code on"
        );

        // ⚠ No operation, and on a `secret: true` action that is a containment property rather than a
        // tidiness one: OperationSpec and the LRO status are durable and are readable by anyone
        // holding `read`, while a key export checks a permission that deliberately is not `read`.
        accepted.OperationId.ShouldBe(Guid.Empty);
        accepted.OperationUri.ShouldBeEmpty();

        if (declared.Response is not { } shape) {
            return;
        }

        using var body = JsonDocument.Parse(accepted.ActionResponse.Length == 0 ? "{}" : accepted.ActionResponse);

        shape.Validate(body.RootElement)
            .IsSuccess
                .ShouldBeTrue(
                    "the handler's body does not match the response shape its provider published — which "
                    + "is what the OpenAPI document, the generated SDK and the portal form are built from"
                );
    }

    [Fact]
    public async Task APostThroughTheWriteVerbIsRefused() {
        ProviderTestCluster<TSource>.Reset();

        var result = await Cluster.Manager.WriteAsync(
            Request(ProviderTestCluster<TSource>.Address("post-to-write")) with { Verb = WriteVerb.Post },
            TestContext.Current.CancellationToken
        );

        result.IsFailure.ShouldBeTrue();
        result.Error!.Message.ShouldContain("never creates");
    }

    // ── The error shape — docs/plan/08 § Errors ─────────────────────────────────────────────────

    [Fact]
    public async Task AnInvalidBodyIsRefusedWithAJsonPointerTargetAndNothingIsClaimed() {
        // docs/plan/08 § Errors: "target is a JSON Pointer into the request body so the portal can
        // highlight the field." A refusal at step 2 must also leave steps 6 and 7 untouched.
        ProviderTestCluster<TSource>.Reset();

        var address = ProviderTestCluster<TSource>.Address("bad-body");

        var refused = await Cluster.Manager.WriteAsync(
            Request(address, body: Case.InvalidBody(ProviderTestCluster<TSource>.ClusterId)),
            TestContext.Current.CancellationToken
        );

        refused.IsFailure.ShouldBeTrue("the case's InvalidBody was accepted by the schema");
        refused.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        refused.Error.Target.ShouldBe(Case.InvalidBodyTarget);
        refused.Error.Target.ShouldStartWith("/");

        (await Cluster.Index(address).GetAsync()).GetValueOrThrow().State.ShouldBe(IndexEntryState.Free);
        Cluster.World.Applied.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnUnknownPropertyIsRefusedRatherThanDropped() {
        // docs/plan/08 § The provider registry, through ResourceSchema: "An unknown property is refused
        // rather than dropped: silently ignoring it produces a resource that is not what was asked for
        // and reports success."
        ProviderTestCluster<TSource>.Reset();

        var body = WithProperty(Body(), "thisIsNotAProperty", "surprise");

        var refused = await Cluster.Manager.WriteAsync(
            Request(ProviderTestCluster<TSource>.Address("unknown-property"), body: body),
            TestContext.Current.CancellationToken
        );

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        refused.Error.Target.ShouldBe("/thisIsNotAProperty");
    }

    [Fact]
    public async Task NoErrorMessageCarriesAStackTrace() {
        // docs/plan/08 § Errors: "No exception details, ever. A stack trace in an error body is an
        // information leak and a support-cost multiplier."
        ProviderTestCluster<TSource>.Reset();

        var refusals = new List<Error>();

        var badBody = await Cluster.Manager.WriteAsync(
            Request(ProviderTestCluster<TSource>.Address("no-trace"), body: "{ not json"),
            TestContext.Current.CancellationToken
        );

        if (badBody.IsFailure) {
            refusals.Add(badBody.Error!);
        }

        var badVersion = await Cluster.Manager.WriteAsync(
            Request(ProviderTestCluster<TSource>.Address("no-trace"), apiVersion: "2999-12-31"),
            TestContext.Current.CancellationToken
        );

        if (badVersion.IsFailure) {
            refusals.Add(badVersion.Error!);
        }

        refusals.ShouldNotBeEmpty();

        foreach (var error in refusals) {
            error.Message.Contains("   at ", StringComparison.Ordinal).ShouldBeFalse(error.Message);
            error.Message.Contains("System.", StringComparison.Ordinal).ShouldBeFalse(error.Message);
            error.Message.Contains(".cs:line", StringComparison.Ordinal).ShouldBeFalse(error.Message);
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>A valid body for the harness's cluster.</summary>
    protected static string Body() => Case.Body(ProviderTestCluster<TSource>.ClusterId);

    /// <summary>A second valid body that differs where the cluster can see it.</summary>
    protected static string ChangedBody() => Case.ChangedBody(ProviderTestCluster<TSource>.ClusterId);

    /// <summary>Builds a <c>PUT</c> for an address.</summary>
    /// <param name="address">Where.</param>
    /// <param name="body">What, defaulting to <see cref="Body" />.</param>
    /// <param name="apiVersion">Which version, defaulting to the case's.</param>
    protected static WriteRequest Request(ResourceId address, string? body = null, string? apiVersion = null) =>
        new() {
            Path = address.Path,
            ApiVersion = apiVersion ?? Case.ApiVersion,
            Verb = WriteVerb.Put,
            Body = body ?? Body(),
            Caller = ProviderTestCluster<TSource>.Caller(address.TenantId)
        };

    /// <summary>Creates a resource and asserts the request was accepted.</summary>
    /// <param name="name">The resource name.</param>
    protected async Task<Result<WriteAccepted>> CreateAsync(string name) {
        var accepted = await Cluster.Manager.WriteAsync(
            Request(ProviderTestCluster<TSource>.Address(name)),
            TestContext.Current.CancellationToken
        );

        accepted.IsSuccess.ShouldBeTrue(accepted.Error?.Message);
        return accepted;
    }

    /// <summary>Reads a resource at the case's api-version.</summary>
    /// <param name="name">The resource name.</param>
    protected Task<Result<ResourceSnapshot>> ReadAsync(string name) =>
        Cluster.Manager.ReadAsync(
            new() {
                Path = ProviderTestCluster<TSource>.Address(name).Path,
                ApiVersion = Case.ApiVersion,
                Caller = ProviderTestCluster<TSource>.Caller()
            },
            TestContext.Current.CancellationToken
        );

    /// <summary>Deletes a resource.</summary>
    /// <param name="name">The resource name.</param>
    protected Task<Result<WriteAccepted>> DeleteAsync(string name) =>
        Cluster.Manager.DeleteAsync(
            new() {
                Path = ProviderTestCluster<TSource>.Address(name).Path,
                ApiVersion = Case.ApiVersion,
                Caller = ProviderTestCluster<TSource>.Caller()
            },
            TestContext.Current.CancellationToken
        );

    /// <summary>Restores a soft-deleted resource.</summary>
    /// <param name="name">The resource name.</param>
    /// <remarks>
    ///     ⚠ Answers <c>202</c> like every other write, because a restore re-applies the resource's
    ///     stored desired state — a soft delete tears the data plane down, so there is something to
    ///     apply. Drive the returned operation with <see cref="ConvergeAsync" />.
    /// </remarks>
    /// <summary>Purges a soft-deleted resource, ending its window.</summary>
    /// <param name="name">The resource name.</param>
    protected Task<Result<WriteAccepted>> PurgeAsync(string name) =>
        Cluster.Manager.PurgeAsync(
            new() {
                Path = ProviderTestCluster<TSource>.Address(name).Path,
                ApiVersion = Case.ApiVersion,
                Caller = ProviderTestCluster<TSource>.Caller()
            },
            TestContext.Current.CancellationToken
        );

    protected Task<Result<WriteAccepted>> RestoreAsync(string name) =>
        Cluster.Manager.RestoreAsync(
            new() {
                Path = ProviderTestCluster<TSource>.Address(name).Path,
                ApiVersion = Case.ApiVersion,
                Caller = ProviderTestCluster<TSource>.Caller()
            },
            TestContext.Current.CancellationToken
        );

    /// <summary>Drives an accepted operation the way its reminder would, until it is terminal.</summary>
    /// <param name="accepted">The accepted response.</param>
    /// <remarks>
    ///     ⚠ Bounded at <see cref="MaxDrives" /> passes. A provider that needs more than that to
    ///     converge against an in-memory API server is a provider whose <c>InProgress</c> is not
    ///     making progress, and the assertion that reports it names the last status rather than
    ///     hanging the run.
    /// </remarks>
    protected async Task<OperationStatus> ConvergeAsync(WriteAccepted accepted) {
        var operation = Cluster.Operation(ConformanceIds.Tenant, accepted.OperationId);
        OperationStatus? last = null;

        for (var i = 0; i < MaxDrives; i++) {
            var status = await operation.DriveAsync();
            last = status.GetValueOrThrow();

            if (last.IsTerminal) {
                return last;
            }
        }

        last.ShouldNotBeNull();
        return last;
    }

    /// <summary>Runs one reconcile pass directly, the way the per-cluster drift scan pokes a resource.</summary>
    /// <param name="resourceId">The resource's GUID.</param>
    /// <param name="name">Its name.</param>
    protected async Task<ReconcileOutcome> ReconcileOnceAsync(Guid resourceId, string name) {
        var address = ProviderTestCluster<TSource>.Address(name).WithId(resourceId);
        using var desired = JsonDocument.Parse(Body());
        var (view, watch) = Cluster.Views.For(address);

        return await Reconciler()
            .ReconcileAsync(
                new(
                    address,
                    Case.ApiVersion,
                    desired.RootElement,
                    null,
                    ReconcileDriver.NamespaceFor(address),
                    // null for a clusterless type — what the driver hands one.
                    HasClusterDataPlane ? Cluster.World : null,
                    // ⚠ The harness's vault on both members. A drift-repair pass is a full reconcile
                    // pass — a provider that mints has to be able to mint on it, and the credential it
                    // finds must be the SAME one the create wrote, which is what mint-once buys and
                    // what a fresh store per call would hide.
                    Cluster.Vault,
                    new RecordingLog()
                    // ⚠ And the cross-resource seam, for the reason TheReconcilerSatisfiesTheFourClauseContract gives.
                ) { SecretWriter = Cluster.Vault, View = view, Watch = watch },
                TestContext.Current.CancellationToken
            );
    }

    /// <summary>The objects a converged resource should own.</summary>
    /// <param name="resourceId">The resource's GUID.</param>
    /// <param name="name">Its name.</param>
    protected static ImmutableArray<ObjectRef> ObjectsOf(Guid resourceId, string name) {
        var address = AddressOf(resourceId, name);
        return Case.Objects(address, ReconcileDriver.NamespaceFor(address));
    }

    /// <summary>The address <see cref="ObjectsOf" /> rendered for.</summary>
    /// <param name="resourceId">The resource's GUID.</param>
    /// <param name="name">Its name.</param>
    protected static ResourceId AddressOf(Guid resourceId, string name) =>
        ProviderTestCluster<TSource>.Address(name).WithId(resourceId);

    /// <summary>
    ///     Asks the case whether one object carries what a desired body asked for,
    ///     <b>
    ///         at a named
    ///         address
    ///     </b>.
    /// </summary>
    /// <param name="resourceId">The resource's GUID.</param>
    /// <param name="name">Its name.</param>
    /// <param name="target">Which of its objects.</param>
    /// <param name="objectJson">The object as the cluster holds it.</param>
    /// <param name="desiredJson">The body it should carry.</param>
    /// <remarks>
    ///     ⚠ The one place a <see cref="MatchContext" /> is built in this suite, so that a member
    ///     added to that record is one edit here rather than five. That is the whole reason the record
    ///     exists — see its remarks.
    /// </remarks>
    protected static bool MatchesDesired(
        Guid resourceId,
        string name,
        ObjectRef target,
        string objectJson,
        string desiredJson
    ) =>
        Case.ObjectMatchesDesired(
            new() {
                ObjectJson = objectJson,
                DesiredJson = desiredJson,
                Id = AddressOf(resourceId, name),
                Target = target,
                Namespace = ReconcileDriver.NamespaceFor(AddressOf(resourceId, name))
            }
        );

    // ── A type that owns none of the objects it applies to — docs/plan/09 § A second writer on an object ──
    //
    // ⚠ THE SUITE BRANCHES ON THE WORLD, NEVER ON THE CASE. Every assertion that presumed a type OWNS
    // what it applies — the seven labels on the command, teardown removing the objects, drift repaired
    // by re-creating them — read that presumption off nothing; it was true of every type before
    // CyberCloud.Network/virtualNetworks/peerings. A peering writes a fragment onto two Vpcs its
    // parent and its sibling own, and for it each of those assertions has a co-writer's reading: the
    // command carries NO labels and the object carries the OWNER's, teardown WITHDRAWS the fragment
    // and the object stays, drift is a fragment stripped by hand and put back. Which reading applies
    // is decided per OBJECT, from the fake's copy of it: an object whose cybercloud.io/resource-id is
    // another resource's is one this resource co-writes. That label is the builder's — injected
    // non-overridably on an owner's apply, refused by name on a co-writer's — so a case cannot elect
    // the gentler branch by describing itself; only the platform's own apply path can put a resource
    // there, and FakeKubeCluster.ApplyCoOwned runs the same two checks the tunnel agent and
    // KubeApiClient run before it will merge a fragment.

    /// <summary>
    ///     Whether <paramref name="target" /> is an object <paramref name="resourceId" /> writes onto
    ///     and does not own — read off the fake's copy of it.
    /// </summary>
    /// <param name="resourceId">The resource under test, with its GUID resolved.</param>
    /// <param name="target">One of the objects the case names.</param>
    protected bool IsCoOwned(Guid resourceId, ObjectRef target) =>
        Cluster.World.OwnerOf(target) is { } owner && owner != resourceId;

    /// <summary>
    ///     Asserts what a co-writer's converged object must carry: the owner's seven labels, this
    ///     resource's fragment bookkeeping, and the desired slice.
    /// </summary>
    /// <param name="resourceId">The co-writing resource.</param>
    /// <param name="target">The owner's object.</param>
    /// <param name="objectJson">The object as the fake holds it.</param>
    protected void AssertCoOwnedObject(Guid resourceId, ObjectRef target, string objectJson) {
        var root = JsonNode.Parse(objectJson)!.AsObject();
        var metadata = root["metadata"]!.AsObject();
        var labels = metadata["labels"]?.AsObject();
        var annotations = metadata["annotations"]?.AsObject();

        labels.ShouldNotBeNull($"'{target}' is co-written and carries no labels — its owner's have been lost");

        foreach (var label in KubeLabels.Mandatory) {
            labels[label]?.GetValue<string>().ShouldNotBeNullOrEmpty($"'{target}' lost its owner's '{label}' to a co-writer's apply");
        }

        labels[KubeLabels.TenantId]!.GetValue<string>().ShouldBe(KubeLabels.GuidValue(ConformanceIds.Tenant));
        labels[KubeLabels.ResourceId]!.GetValue<string>()
            .ShouldNotBe(
                KubeLabels.GuidValue(resourceId),
                $"'{target}' now carries the CO-WRITER's resource-id. A co-writer writes none of the seven, "
                + "and an object re-labelled for the co-writer is one the owner's next apply conflicts on."
            );

        annotations.ShouldNotBeNull($"'{target}' carries no annotations, so no fragment of {resourceId:D}'s");

        annotations[KubeLabels.FragmentAnnotation(resourceId)].ShouldNotBeNull(
            $"'{target}' carries no cybercloud.io/fragment.{resourceId:D}. Without it the next co-writer's "
            + "apply prunes this resource's slice — docs/plan/09 § A second writer on an object."
        );

        var hash = annotations[KubeLabels.FragmentHashAnnotation(resourceId)]?.GetValue<string>();
        hash.ShouldNotBeNull($"'{target}' carries a fragment of {resourceId:D}'s and no hash of it");
        hash.ShouldStartWith("sha256:");

        annotations[KubeLabels.FragmentPathAnnotation(resourceId)].ShouldNotBeNull(
            $"'{target}' carries a fragment of {resourceId:D}'s and no path — the drift scan names an orphan slice by it"
        );
    }

    /// <summary>
    ///     Whether an object still carries <paramref name="resourceId" />'s fragment annotation.
    /// </summary>
    /// <param name="resourceId">The co-writing resource.</param>
    /// <param name="target">The owner's object.</param>
    protected bool CarriesFragmentOf(Guid resourceId, ObjectRef target) =>
        Cluster.World.Read(target) is { } json
        && JsonNode.Parse(json) is JsonObject root
        && (root["metadata"] as JsonObject)?["annotations"] is JsonObject annotations
        && annotations.ContainsKey(KubeLabels.FragmentAnnotation(resourceId));

    /// <summary>
    ///     Breaks the world for one object the way its ownership calls for: a <c>kubectl delete</c>
    ///     of an owned object, a hand edit that strips this resource's slice off a co-owned one.
    /// </summary>
    /// <param name="resourceId">The resource under test.</param>
    /// <param name="target">The object.</param>
    /// <remarks>
    ///     ⚠ A co-writer's <c>kubectl delete</c> would be the <b>owner's</b> object going, and the
    ///     co-writer's right answer to that is to refuse — a co-writer never creates the owner's
    ///     object. What somebody deletes by hand on a co-owned object is the co-writer's entries, and
    ///     that is what is stripped, read off the object's own fragment annotation.
    /// </remarks>
    protected void BreakBehindTheirBack(Guid resourceId, ObjectRef target) {
        if (IsCoOwned(resourceId, target)) {
            Cluster.World.StripFragmentBehindTheirBack(target, resourceId)
                .ShouldBeTrue($"'{target}' is co-owned and carried no fragment of {resourceId:D}'s to strip — the break would have changed nothing");

            return;
        }

        Cluster.World.RemoveBehindTheirBack(target).ShouldBeTrue();
    }

    /// <summary>
    ///     Puts the objects this type's <b>operator</b> writes into the fake cluster.
    /// </summary>
    /// <param name="resourceId">The resource's GUID.</param>
    /// <param name="name">Its name.</param>
    /// <remarks>
    ///     ⚠ Behind the reconciler's back, because that is what an operator is — see
    ///     <see cref="ProviderConformanceCase.OperatorWritten" />. A no-op for every type that has
    ///     none, which is most of them.
    /// </remarks>
    protected void PlaceOperatorObjects(Guid resourceId, string name) => PlantOperatorObjects(resourceId, name);

    /// <summary>
    ///     <see cref="PlaceOperatorObjects" />, handing back what it planted with every owner
    ///     reference resolved to the uid the fake holds for the named object.
    /// </summary>
    /// <param name="resourceId">The resource's GUID.</param>
    /// <param name="name">Its name.</param>
    /// <remarks>
    ///     ⚠ <b>The resolution is the operator's <c>SetAsOwnedBy</c>, done here because the harness
    ///     holds the object the operator would read.</b> A case cannot know a uid the fake has not
    ///     issued yet, so it names the owner by kind and name and leaves <c>uid</c> empty; an owner
    ///     the fake does not hold fails the placement by name rather than planting a reference to
    ///     nothing — which the collector would read as "owner gone" and act on.
    /// </remarks>
    protected List<(ObjectRef Target, string Json)> PlantOperatorObjects(Guid resourceId, string name) {
        var address = AddressOf(resourceId, name);
        var planted = new List<(ObjectRef, string)>();

        foreach (var (target, json) in Case.OperatorWritten(address, ReconcileDriver.NamespaceFor(address))) {
            var resolved = WithResolvedOwners(target, json);
            Cluster.World.MutateBehindTheirBack(target, resolved);
            planted.Add((target, resolved));
        }

        return planted;
    }

    string WithResolvedOwners(ObjectRef target, string json) {
        if (JsonNode.Parse(json) is not JsonObject root
            || root["metadata"] is not JsonObject metadata
            || metadata["ownerReferences"] is not JsonArray owners) {
            return json;
        }

        foreach (var owner in owners.OfType<JsonObject>()) {
            if (owner["uid"]?.GetValue<string>() is { Length: > 0 }) {
                continue;
            }

            var kind = new GroupVersionKind {
                Group = ApiGroup(owner["apiVersion"]?.GetValue<string>() ?? string.Empty),
                Version = ApiVersionOf(owner["apiVersion"]?.GetValue<string>() ?? string.Empty),
                Kind = owner["kind"]?.GetValue<string>() ?? string.Empty
            };

            var candidate = Cluster.World.Applied.Select(x => x.Target)
                .FirstOrDefault(x => x.Kind.Kind == kind.Kind
                    && x.Kind.Group == kind.Group
                    && x.Name == owner["name"]?.GetValue<string>()
                    && x.Namespace == target.Namespace);

            var uid = candidate is null ? string.Empty : Cluster.World.UidOf(candidate);

            uid.ShouldNotBeEmpty(
                $"{Case.DisplayName} declares '{target}' as owned by {kind.Kind} '{owner["name"]}', "
                + "and the fake holds no such object. An operator-written object names an owner the "
                + "reconciler applied, or it is not this resource's."
            );

            owner["uid"] = uid;
        }

        return root.ToJsonString();
    }

    static string ApiGroup(string apiVersion) => apiVersion.Contains('/') ? apiVersion[..apiVersion.IndexOf('/')] : string.Empty;

    static string ApiVersionOf(string apiVersion) => apiVersion.Contains('/') ? apiVersion[(apiVersion.IndexOf('/') + 1)..] : apiVersion;

    /// <summary>The kind an owner reference names, with the plural the fake's store keys on.</summary>
    /// <remarks>
    ///     ⚠ The plural is looked up among the objects the reconciler applied, because an owner
    ///     reference does not carry one and the fake addresses by the full
    ///     <see cref="GroupVersionKind" />.
    /// </remarks>
    GroupVersionKind KindOf(OwnerRef owner) =>
        Cluster.World.Applied.Select(x => x.Target.Kind)
            .FirstOrDefault(x => x.Kind == owner.Kind && x.ApiVersion == owner.ApiVersion)
        ?? new() { Group = ApiGroup(owner.ApiVersion), Version = ApiVersionOf(owner.ApiVersion), Kind = owner.Kind };

    /// <summary>A fresh reconciler, built the way the container builds one.</summary>
    /// <remarks>
    ///     ⚠ Constructed here rather than pulled out of the silo, so that a pass driven by a test and a
    ///     pass driven by a reminder are demonstrably the same code with the same inputs. Clause 2 is
    ///     what makes that safe: a reconciler with no state cannot tell the difference.
    /// </remarks>
    protected IResourceReconciler Reconciler() => Case.CreateReconciler(Cluster.Clock);

    const int MaxDrives = 8;

    /// <summary>
    ///     A JSON document with every object's members in ordinal order, so two bodies that mean the
    ///     same thing compare equal.
    /// </summary>
    /// <remarks>
    ///     ⚠ Mirrors <c>CyberCloud.ResourceManager.JsonCanonical</c>, which is <c>internal</c>. It has
    ///     to: the grain stores a canonical superset and rebuilds property order from the schema's
    ///     pointer list, so a read-back compared as raw text would fail on ordering alone — which is
    ///     the exact trap that type's remarks record having already cost a test run. Arrays are left
    ///     alone here for the same reason they are there: an ordered list is data, not a bag.
    /// </remarks>
    static string Canonical(string json) {
        var node = System.Text.Json.Nodes.JsonNode.Parse(json);
        return Sort(node)?.ToJsonString() ?? "null";

        static System.Text.Json.Nodes.JsonNode? Sort(System.Text.Json.Nodes.JsonNode? value) {
            if (value is not System.Text.Json.Nodes.JsonObject map) {
                return value?.DeepClone();
            }

            var sorted = new System.Text.Json.Nodes.JsonObject();
            foreach (var member in map.OrderBy(x => x.Key, StringComparer.Ordinal)) {
                sorted[member.Key] = Sort(member.Value);
            }

            return sorted;
        }
    }

    static string WithTags(string body, params (string Key, string Value)[] tags) {
        var node = System.Text.Json.Nodes.JsonNode.Parse(body)!.AsObject();
        var map = new System.Text.Json.Nodes.JsonObject();

        foreach (var (key, value) in tags) {
            map[key] = value;
        }

        node["tags"] = map;
        return node.ToJsonString();
    }

    static string WithProperty(string body, string name, string value) {
        var node = System.Text.Json.Nodes.JsonNode.Parse(body)!.AsObject();
        node[name] = value;
        return node.ToJsonString();
    }
}

/// <summary>Collects what a reconciler reported, so a test can assert it said something.</summary>
public sealed class RecordingLog : IReconcileLog {
    readonly List<(string Phase, string Detail, int Percent)> entries = [];

    /// <summary>Everything reported.</summary>
    public IReadOnlyList<(string Phase, string Detail, int Percent)> Entries => entries;

    /// <inheritdoc />
    public void Report(string phase, string detail) => Report(phase, detail, 0);

    /// <inheritdoc />
    public void Report(string phase, string detail, int percentComplete) =>
        entries.Add((phase, detail, percentComplete));
}
