using CyberCloud.Conformance.Harness;
using CyberCloud.ResourceManager;
using CyberCloud.ResourceManager.Conformance;
using CyberCloud.ResourceManager.Reconcile;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;

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
///         Four of those five run here. <b>The fifth — killing a silo mid-create — does not, and it
///         is not absent either</b>: an in-process <c>TestCluster</c> over in-memory storage cannot
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

    // ── The registry, which is the platform's whole description of this provider ────────────────

    [Fact]
    public void TheTypeIsRegisteredWithAReconcilerAndAllThreePermissions() {
        // A provider whose Describe returned early, or whose reconciler names a type nobody declared,
        // is a provider whose endpoints answer 404 with nothing in the log. ProviderRegistry.Build
        // throws on most of that; this asserts the parts it cannot.
        Cluster.Registry.TryGetType(Case.Type, out var registration).ShouldBeTrue(
            $"'{Case.Type}' is not in the registry built from {Case.DisplayName}'s own Describe."
        );

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
        Canonical(snapshot.Properties).ShouldBe(Canonical(Body()));
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

        var objects = ObjectsOf(accepted.Resource.Id, "world-applied");
        objects.ShouldNotBeEmpty($"{Case.DisplayName} declares RequiresCluster and applied nothing");

        foreach (var target in objects) {
            var json = Cluster.World.Read(target);
            json.ShouldNotBeNull($"'{target}' is not in the cluster");
            Case.ObjectMatchesDesired(json, Body()).ShouldBeTrue($"'{target}' does not carry the desired shape");
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

        Cluster.World.Applied.ShouldNotBeEmpty();

        foreach (var command in Cluster.World.Applied) {
            foreach (var label in KubeLabels.Mandatory) {
                command.Labels.ShouldContainKey(label, $"'{command.Target}' is missing '{label}'");
                command.Labels[label].ShouldNotBeNullOrEmpty();
            }

            command.Labels[KubeLabels.TenantId].ShouldBe(KubeLabels.GuidValue(ConformanceIds.Tenant));
            command.Labels[KubeLabels.ResourceId].ShouldBe(KubeLabels.GuidValue(accepted.Resource.Id));
            command.Labels[KubeLabels.ManagedBy].ShouldBe(KubeLabels.ManagedByValue);
            command.Labels[KubeLabels.ApiVersion].ShouldBe(Case.ApiVersion);

            foreach (var annotation in KubeLabels.MandatoryAnnotations) {
                command.Annotations.ShouldContainKey(annotation);
            }

            command.Annotations[KubeLabels.ReconcileHashAnnotation].ShouldStartWith("sha256:");

            // ⚠ The resource-id label is what makes orphan detection a hash join rather than a scan —
            // ADR-013 — and an EMPTY GUID here is the failure that produces objects the drift scan
            // reports as orphans forever. ReconcileContext's own remarks warn about it by name.
            command.Labels[KubeLabels.ResourceId].ShouldNotBe(KubeLabels.GuidValue(Guid.Empty));
        }
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
        entry.GetValueOrThrow().State.ShouldBe(
            IndexEntryState.Confirmed,
            "a refused delete must not have released the name — the release is irreversible"
        );

        foreach (var target in ObjectsOf(accepted.Resource.Id, "locked")) {
            Cluster.World.Holds(target).ShouldBeTrue("a lock that let the data plane go is not a lock");
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
    ///         ⚠ <b>TWO OF THE ASSERTIONS BELOW ARE CORRECT FOR A HARD-DELETE TYPE AND WRONG FOR A
    ///         SOFT-DELETABLE ONE</b>, and the branch is what reconciles them. The index entry going
    ///         back to <c>Free</c> — <i>"the name comes back"</i> — and the ReBAC parent tuple being
    ///         removed are both things a <c>DELETE</c> deliberately does <b>not</b> do when the type
    ///         declares a recovery window (docs/plan/08 § Soft delete): the name is held so a restore
    ///         has somewhere to go, and the edge moves to the subscription rather than being dropped so
    ///         the resource is never invisible.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A THIRD ASSERTION USED TO BRANCH AND NO LONGER DOES: THE OBJECTS ARE GONE EITHER
    ///         WAY.</b> The soft arm asserted they stayed, and two providers declared a window against
    ///         that arm, measured what a tenant was left with — a workload still running behind an
    ///         address answering <c>404</c>, still billed, and unreachable to delete again — and
    ///         withdrew. A soft delete tears the data plane down like any other delete; what the
    ///         window preserves is the name, the stored desired state, the committed quota and the
    ///         volumes, none of which a teardown removes. The soft arm asserts the restore round trip
    ///         instead, because without it every assertion here would also hold for a type that can
    ///         never hand anything back.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>THE BRANCH IS TAKEN FROM THE REGISTRY AND NOT FROM
    ///         <see cref="ProviderConformanceCase" />, and that distinction is the whole design.</b>
    ///         That record's own remarks forbid a case supplying anything the suite decides with —
    ///         <i>"a case that could supply an assertion would be a provider grading its own
    ///         homework"</i> — and "which of these two contracts do I have to satisfy" is exactly such
    ///         a decision. The registry is not the provider's answer to the suite; it is the platform's
    ///         own description of the type, built from <c>Describe</c> and read by the write path, the
    ///         OpenAPI emitter and the four generated surfaces alike. A provider declares
    ///         <c>SupportsSoftDelete</c> once, in public, where it reaches the published document; the
    ///         suite <i>derives</i> which contract that declaration signs it up to. Nothing is
    ///         optional and nothing is skipped: both arms assert, and a provider cannot decline either
    ///         by omission the way a nullable case member would let it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Why not a <c>static virtual</c> on <see cref="IProviderCaseSource" />, which is the
    ///         one accepted way to add an optional member.</b> <c>Ancestors</c> earns that shape
    ///         because its value is <i>not derivable</i> — a parent's api-version and a body its schema
    ///         accepts exist nowhere else — and because omitting it is refused by name rather than
    ///         silently running a smaller suite. Neither applies here: the window is already a registry
    ///         fact, so a case member would be a <b>second</b> declaration of it, and two descriptions
    ///         of one thing is the drift ADR-012 exists to remove. Worse, the two could disagree — a
    ///         case saying "hard delete" over a type declaring seven days would run the wrong contract
    ///         and pass.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Which providers take the soft arm is a registry fact and therefore moves without
    ///         this file changing.</b> <c>CyberCloud.ContainerRegistry/registries</c> and
    ///         <c>CyberCloud.Monitor/workspaces</c> declare a window;
    ///         <c>CyberCloud.ResourceManager.Tests.SoftDeletePathTests</c> drives the same contract
    ///         against a fixture type in isolation, which is where its own sabotage results were
    ///         taken.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task DeleteTearsDownTheDataPlaneAndTheResourceIsGone() {
        ProviderTestCluster<TSource>.Reset();

        Cluster.Registry.TryGetType(Case.Type, out var registration).ShouldBeTrue();
        var recoverable = registration.SoftDeleteDays > 0;

        var accepted = (await CreateAsync("goodbye")).GetValueOrThrow();
        await ConvergeAsync(accepted);

        var objects = ObjectsOf(accepted.Resource.Id, "goodbye");

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
            Cluster.World.Holds(target).ShouldBeFalse(
                $"'{target}' is still in the cluster after a converged teardown — docs/plan/06 "
                + "§ Two-phase create: never silently gone while its pods still run and its meter "
                + "still ticks, and never still running while the resource says it is gone"
            );
        }

        if (recoverable) {
            // ── The recovery window's contract ──────────────────────────────────────────────────
            entry.GetValueOrThrow().State.ShouldBe(
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
                Cluster.World.Holds(target).ShouldBeTrue(
                    $"'{target}' did not come back, and this type declares a "
                    + $"{registration.SoftDeleteDays.ToString(CultureInfo.InvariantCulture)}-day "
                    + "recovery window. A restore re-applies the desired state the park kept — "
                    + "docs/plan/08 § Soft delete"
                );
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

        reached.IndexOf(WriteStep.LinkParent).ShouldBeLessThan(
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
            new() {
                Path = absent.Path,
                ApiVersion = Case.ApiVersion,
                Caller = ProviderTestCluster<TSource>.Caller()
            },
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

        var status = await Cluster.Operation(ConformanceIds.Tenant, accepted.OperationId).DriveAsync();
        status.GetValueOrThrow().IsTerminal.ShouldBeFalse("the create must still be running for this test to mean anything");

        var refused = await DeleteAsync("racing-delete");

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.OperationInProgress);
        refused.Error.Message.ShouldContain(accepted.OperationId.ToString("D"));

        var entry = await Cluster.Index(ProviderTestCluster<TSource>.Address("racing-delete")).GetAsync();
        entry.GetValueOrThrow().State.ShouldBe(
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

        var objects = ObjectsOf(accepted.Resource.Id, "drifting");
        objects.ShouldNotBeEmpty();

        foreach (var target in objects) {
            Cluster.World.RemoveBehindTheirBack(target).ShouldBeTrue();
        }

        // A fresh write with the same body is a no-op at the grain, so the drift is corrected by the
        // reconcile path rather than by a new submission — which is what the hourly per-cluster scan
        // of docs/plan/08 § The reconcile loop would do when it pokes a diverged resource.
        var repaired = await ReconcileOnceAsync(accepted.Resource.Id, "drifting");

        repaired.Kind.ShouldNotBe(ReconcileOutcomeKind.Failed, repaired.ToString());

        foreach (var target in objects) {
            Cluster.World.Holds(target).ShouldBeTrue($"'{target}' was not put back");
            Case.ObjectMatchesDesired(Cluster.World.Read(target)!, Body()).ShouldBeTrue();
        }
    }

    [Fact]
    public async Task AHandEditIsOverwrittenOnTheNextPass() {
        ProviderTestCluster<TSource>.Reset();

        var accepted = (await CreateAsync("hand-edited")).GetValueOrThrow();
        await ConvergeAsync(accepted);

        foreach (var target in ObjectsOf(accepted.Resource.Id, "hand-edited")) {
            Cluster.World.MutateBehindTheirBack(target, """{"metadata":{"name":"hand-edited"},"data":{}}""");
        }

        var repaired = await ReconcileOnceAsync(accepted.Resource.Id, "hand-edited");
        repaired.Kind.ShouldNotBe(ReconcileOutcomeKind.Failed, repaired.ToString());

        foreach (var target in ObjectsOf(accepted.Resource.Id, "hand-edited")) {
            Case.ObjectMatchesDesired(Cluster.World.Read(target)!, Body()).ShouldBeTrue(
                "the hand edit survived a reconcile pass"
            );
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

        var context = new ReconcileContext(
            address,
            Case.ApiVersion,
            desired.RootElement,
            null,
            ns,
            Cluster.World,
            // ⚠ THE HARNESS'S VAULT, NOT THE REFUSING DEFAULT, AND THE SAME INSTANCE ON BOTH MEMBERS.
            // This context is built BY HAND rather than by ReconcileDriver, so nothing fills
            // SecretWriter in for it — and a provider whose create mints a credential then fails every
            // clause for a wiring reason. The refusal names the member to set, which is how this was
            // found rather than guessed.
            Cluster.Vault,
            log
        ) {
            SecretWriter = Cluster.Vault
        };

        var world = new ConformanceWorld(
            BreakAsync: () => {
                foreach (var target in objects) {
                    Cluster.World.RemoveBehindTheirBack(target);
                }

                return Task.CompletedTask;
            },
            MatchesDesiredAsync: () => Task.FromResult(
                objects.Length > 0
                && objects.All(target => Cluster.World.Read(target) is { } json
                    && Case.ObjectMatchesDesired(json, Body()))
            )
        );

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

        foreach (var target in ObjectsOf(first.Resource.Id, "updated")) {
            Case.ObjectMatchesDesired(Cluster.World.Read(target)!, ChangedBody()).ShouldBeTrue(
                "an update that stopped at the grain is an update the tenant cannot see"
            );
        }
    }

    [Fact]
    public async Task PostNeverCreates() {
        // docs/plan/08 § The write path, end to end: POST "appears only for actions on an existing
        // resource … never for creation." Checked by the manager, not by each action's handler.
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
        ProviderTestCluster<TSource>.Reset();

        var created = (await CreateAsync("actionable")).GetValueOrThrow();
        await ConvergeAsync(created);

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
        registration.TryGetAction(Case.ActionName, out var declared).ShouldBeTrue(
            $"'{Case.ActionName}' is the case's action and the provider does not declare it"
        );

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

        using var body = JsonDocument.Parse(
            accepted.ActionResponse.Length == 0 ? "{}" : accepted.ActionResponse
        );

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

        return await Reconciler()
            .ReconcileAsync(
                new(
                    address,
                    Case.ApiVersion,
                    desired.RootElement,
                    null,
                    ReconcileDriver.NamespaceFor(address),
                    Cluster.World,
                    // ⚠ The harness's vault on both members. A drift-repair pass is a full reconcile
                    // pass — a provider that mints has to be able to mint on it, and the credential it
                    // finds must be the SAME one the create wrote, which is what mint-once buys and
                    // what a fresh store per call would hide.
                    Cluster.Vault,
                    new RecordingLog()
                ) {
                    SecretWriter = Cluster.Vault
                },
                TestContext.Current.CancellationToken
            );
    }

    /// <summary>The objects a converged resource should own.</summary>
    /// <param name="resourceId">The resource's GUID.</param>
    /// <param name="name">Its name.</param>
    protected static ImmutableArray<ObjectRef> ObjectsOf(Guid resourceId, string name) {
        var address = ProviderTestCluster<TSource>.Address(name).WithId(resourceId);
        return Case.Objects(address, ReconcileDriver.NamespaceFor(address));
    }

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
