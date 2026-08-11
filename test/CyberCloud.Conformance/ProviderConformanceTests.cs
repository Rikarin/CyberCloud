using CyberCloud.Conformance.Harness;
using CyberCloud.ResourceManager;
using CyberCloud.ResourceManager.Conformance;
using CyberCloud.ResourceManager.Reconcile;
using System.Collections.Immutable;
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
///         Four of those five run here. <b>The fifth — killing a silo mid-create — does not, and is
///         not silently absent</b>: it is a named, loudly-skipped test in
///         <see cref="ClusterBackedConformanceTests" />, because an in-process
///         <c>TestCluster</c> over in-memory storage cannot tell "the silo died and the durable state
///         brought it back" from "a grain deactivated and reactivated over the same dictionary". A
///         suite that ran the second under the first's name would be worse than one that skipped.
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

    [Fact]
    public async Task DeleteTearsDownTheDataPlaneAndTheResourceIsGone() {
        ProviderTestCluster<TSource>.Reset();

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

        foreach (var target in objects) {
            Cluster.World.Holds(target).ShouldBeFalse(
                $"'{target}' is still in the cluster after a converged teardown — docs/plan/06 "
                + "§ Two-phase create: never silently gone while its pods still run and its meter "
                + "still ticks, and never still running while the resource says it is gone"
            );
        }

        var read = await ReadAsync("goodbye");
        read.IsFailure.ShouldBeTrue();
        read.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);

        var entry = await Cluster.Index(ProviderTestCluster<TSource>.Address("goodbye")).GetAsync();
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
            new UnavailableSecretResolver(),
            log
        );

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

        action.IsSuccess.ShouldBeTrue(action.Error?.Message);
        action.GetValueOrThrow().OperationId.ShouldNotBe(Guid.Empty);
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
                    new UnavailableSecretResolver(),
                    new RecordingLog()
                ),
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
