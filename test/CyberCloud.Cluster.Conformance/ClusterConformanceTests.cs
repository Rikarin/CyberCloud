using CyberCloud.Cluster.Conformance.Infrastructure;
using CyberCloud.ResourceManager;
using CyberCloud.ResourceManager.Drift;
using CyberCloud.ResourceManager.Reconcile;
using CyberCloud.ServiceDefaults.Storage;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using k8s.Models;

namespace CyberCloud.Cluster.Conformance;

/// <summary>
///     The four cluster-backed criteria of docs/plan/03 § Providers, run against a <b>real</b> API
///     server and a <b>real</b> durable tier.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             These four replace four <c>Assert.Skip</c>s, and the replacement has to be worth
///             more than the skip.
///         </b> <c>ClusterBackedConformanceTests</c> named two dishonest options
///         and refused both: deleting the test leaves a suite that is green because it asked less,
///         and re-pointing it at the in-memory harness leaves a suite that is green because it
///         asserted something weaker under the same name. So each test below asserts exactly what
///         its skip message said it would, and every assertion is read <b>around</b> our own code —
///         with the raw <c>KubernetesClient</c> against the API server, and with plain SQL against
///         PostgreSQL.
///     </para>
///     <para>
///         ⚠ <b>To add a provider, do not touch this file.</b> Write a <c>ProviderConformanceCase</c>
///         — the same one the Docker-free suite already takes — declare an <c>IProviderCaseSource</c>
///         for it, and derive one class from this one in that provider's own
///         <c>.Cluster.Conformance</c> project.
///     </para>
/// </remarks>
/// <typeparam name="TSource">The provider under test.</typeparam>
/// <param name="fixture">The harness.</param>
public abstract class ClusterConformanceTests<TSource>(ClusterConformanceFixture<TSource> fixture)
    where TSource : IProviderCaseSource {
    /// <summary>
    ///     How many times an operation may be driven before it is stuck rather than working.
    /// </summary>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         Twelve until 2026-08-12, and twelve was a budget in <i>passes</i> pretending to be a
    ///         budget in <i>time</i>.
    ///     </b> With no delay between drives the whole loop ran in a few
    ///     milliseconds, which is enough only when every remaining step is ours. The first operation
    ///     that had to wait for the API server to finish something — a <c>CascadePolicy.Foreground</c>
    ///     teardown, where the object is held under a <c>foregroundDeletion</c> finalizer until the
    ///     garbage collector has removed its dependents — reported <c>InProgress</c> twelve times and
    ///     failed on a status that was correct. Measured against
    ///     <c>CyberCloud.Cache/redis</c>: the finalizer clears in roughly twenty seconds here, because
    ///     the collector discovers a <i>newly created</i> custom resource on its periodic resync. A
    ///     platform cluster installs its CRDs from <c>charts/bundle/</c> long before a tenant creates
    ///     anything, so that particular wait is this fixture's and not production's — but the budget
    ///     has to cover it, and forty seconds is cheap next to the twenty this suite already spends
    ///     starting containers.
    /// </remarks>
    const int MaxDrives = 40;

    /// <summary>The provider under test.</summary>
    protected static ProviderConformanceCase Case => TSource.ProviderCase;

    /// <summary>The harness, when the infrastructure came up.</summary>
    protected ClusterConformanceFixture<TSource> Fixture { get; } = fixture;

    // ── The vacuity guard ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheCaseOwnsClusterObjectsOrThisWholeSuiteWouldBeVacuous() {
        // ⚠ A suite that discovered nothing to look at and reported success is the failure this
        // repository has already been bitten by — provider discovery once matched at a fixed nesting
        // depth, found zero providers, and went green. A clusterless provider (a DNS zone, a mail
        // domain, a role assignment) owns no Kubernetes objects, so every assertion below would pass
        // over an empty loop. That must be a SKIP that says so, never a pass.
        var address = ClusterConformanceHarness<TSource>.Address("vacuity");
        var objects = Case.Objects(address.WithId(Guid.NewGuid()), ClusterConformanceHarness<TSource>.Namespace);

        if (objects.IsDefaultOrEmpty) {
            Assert.Skip(
                $"SKIPPED — {Case.DisplayName} owns no cluster objects, so the cluster-backed half of "
                + "the conformance suite has nothing to assert against and would otherwise pass by "
                + "iterating an empty collection. A clusterless provider is legitimate "
                + "(docs/plan/08 § What the resource manager deliberately does not do), and this is "
                + "the suite saying so out loud rather than reporting a green it did not earn."
            );
        }

        objects.Length.ShouldBeGreaterThan(0);
    }

    // ── 1. The lifecycle, against a real API server ────────────────────────────────────────────

    [Fact]
    public async Task TheLifecycleRunsAgainstARealApiServer() {
        var harness = Fixture.Require(
            "that the rendered manifest is one the API server accepts, that server-side apply under "
            + "our field manager behaves as ADR-013 assumes, that the seven labels survive admission, "
            + "and that the plural in the GroupVersionKind addresses a real REST path."
        );

        var token = TestContext.Current.CancellationToken;
        const string name = "real-lifecycle";

        var accepted = (await WriteAsync(harness, name)).GetValueOrThrow();
        var status = await ConvergeAsync(harness, accepted.OperationId);

        status.State.ShouldBe(
            OperationState.Succeeded,
            $"the operation ended {status.State} against a real API server: {status.Error?.Message}"
        );

        var objects = ObjectsOf(accepted.Resource.Id, name);
        objects.ShouldNotBeEmpty();

        foreach (var target in objects) {
            // ⚠ READ AROUND EVERY LINE OF OUR OWN CODE. This is the raw KubernetesClient asking the
            // API server for the object by GROUP, VERSION, NAMESPACE, PLURAL and NAME. A 404 here is
            // the plural not addressing a real REST path — which is precisely the failure a
            // dictionary keyed by the same strings cannot have.
            var json = await ReadFromClusterAsync(harness, target, token);

            json.ShouldNotBeNull(
                $"'{target}' is not in the real cluster. The API server either refused the manifest "
                + "at admission or the plural does not address a REST path."
            );

            var root = JsonNode.Parse(json)!.AsObject();
            var metadata = root["metadata"]!.AsObject();
            var labels = metadata["labels"]?.AsObject();
            var annotations = metadata["annotations"]?.AsObject();

            labels.ShouldNotBeNull($"'{target}' came back from the API server with no labels at all.");

            // ⚠ THE SEVEN LABELS, SURVIVING ADMISSION. The Docker-free suite asserts them on the
            // COMMAND we rendered; this asserts them on the OBJECT the API server stored. Between
            // those two is admission, validation, and the label-key syntax rules — a prefix with an
            // upper-case letter, a value over 63 characters or a `/` in a value is rejected there and
            // nowhere else. docs/plan/23 § The architecture gates, the Labels row.
            foreach (var label in KubeLabels.Mandatory) {
                labels[label].ShouldNotBeNull(
                    $"the object the API SERVER is holding for '{target}' is missing '{label}'. It was "
                    + "in the command we sent, so it did not survive admission."
                );

                labels[label]!.GetValue<string>().ShouldNotBeNullOrEmpty();
            }

            labels[KubeLabels.ManagedBy]!.GetValue<string>().ShouldBe(KubeLabels.ManagedByValue);
            labels[KubeLabels.TenantId]!.GetValue<string>()
                .ShouldBe(KubeLabels.GuidValue(ConformanceIds.Tenant));
            labels[KubeLabels.ResourceId]!.GetValue<string>()
                .ShouldBe(KubeLabels.GuidValue(accepted.Resource.Id));
            labels[KubeLabels.ApiVersion]!.GetValue<string>().ShouldBe(Case.ApiVersion);

            annotations.ShouldNotBeNull();
            foreach (var annotation in KubeLabels.MandatoryAnnotations) {
                annotations[annotation].ShouldNotBeNull($"the stored object for '{target}' is missing '{annotation}'.");
            }

            // ⚠ SERVER-SIDE APPLY UNDER OUR FIELD MANAGER, AS ADR-013 ASSUMES. managedFields is
            // written by the API server's field-management machinery and by nothing we control. Its
            // absence would mean the apply reached the server as something other than an apply patch,
            // which is the assumption every conflict assertion downstream rests on.
            var managed = metadata["managedFields"]?.AsArray();
            managed.ShouldNotBeNull($"'{target}' carries no managedFields, so it was not server-side applied.");

            var ours = managed!
                .Select(static x => x!.AsObject())
                .Where(x => x["manager"]?.GetValue<string>() == FieldManagerOf(harness))
                .ToList();

            ours.ShouldNotBeEmpty(
                $"the API server recorded no field-manager entry for '{FieldManagerOf(harness)}' on "
                + $"'{target}'. managedFields holds: "
                + string.Join(", ", managed.Select(static x => x!["manager"]?.GetValue<string>()))
            );

            ours.ShouldContain(
                x => x["operation"]!.GetValue<string>() == "Apply",
                "the API server recorded our writes as something other than Apply, so nothing "
                + "downstream would ever conflict."
            );

            // And the object carries what the desired body asked for — the case's own ground truth,
            // read from the API server rather than from our cache of it.
            MatchesDesired(accepted.Resource.Id, name, target, json!, Body()).ShouldBeTrue(
                $"'{target}' is in the cluster but does not carry the desired shape: {json}"
            );
        }

        // ── delete → gone, against the same real server ────────────────────────────────────────
        var deleted = await harness.Manager.DeleteAsync(
            new() {
                Path = ClusterConformanceHarness<TSource>.Address(name).Path,
                ApiVersion = Case.ApiVersion,
                Caller = ClusterConformanceHarness<TSource>.Caller()
            },
            token
        );

        deleted.IsSuccess.ShouldBeTrue(deleted.Error?.Message);

        var teardown = await ConvergeAsync(harness, deleted.GetValueOrThrow().OperationId);
        teardown.State.ShouldBe(
            OperationState.Succeeded,
            $"the teardown ended {teardown.State}: {teardown.Error?.Message}"
        );

        // ⚠ "GONE" MEANS GONE FOR EVERY TYPE, AND THIS SUITE BRIEFLY BELIEVED OTHERWISE.
        //
        // ⚠ THE HISTORY IS WORTH KEEPING, BECAUSE THE ASSERTION THAT WAS HERE IS THE ONE THAT MISLED
        // A PROVIDER. This half asserted the hard-delete contract unconditionally and went green for
        // eleven families, because nothing had declared a recovery window. When the first type
        // declared one it failed here — with the message below, which says only that an object is
        // PRESENT — and `CyberCloud.ContainerRegistry/registries` read that end state as a
        // soft-deleted resource actively REBUILDING its data plane, and wrote the platform defect up
        // that way. The objects had never been torn down at all: OperationGrain.DriveAsync returned
        // before running a pass. An assertion over an end state cannot tell "never removed" from
        // "removed and re-applied", and which of those it was decided where the fix belonged.
        //
        // ⚠ WHAT IS ASSERTED NOW DOES NOT BRANCH, BECAUSE THE CONTRACT DOES NOT. A soft delete tears
        // the data plane down like any other delete — leaving a tenant's workload running behind an
        // address that answers 404 is the "silently gone while its pods still run and its meter still
        // ticks" docs/plan/06 § Two-phase create forbids by name. What the window preserves is what a
        // teardown does not remove: the name, the resource's stored desired state, the committed
        // quota, and the disks, because deleting a StatefulSet does not delete its claims. The
        // Docker-free twin drives the restore round trip that proves the last of those; this suite's
        // subject is the API server, and what it can say about a real one is that the objects are
        // gone.
        foreach (var target in objects) {
            var remaining = await ReadFromClusterAsync(harness, target, token);

            remaining.ShouldBeNull($"'{target}' is still in the real cluster after a converged teardown.");
        }
    }

    // ── 1b. The claims a teardown keeps, on a real API server ──────────────────────────────────

    /// <summary>
    ///     ⚠
    ///     <b>
    ///         The claims a real <c>StatefulSet</c> controller made outlive the teardown, and the
    ///         purge removes them.
    ///     </b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             This is the one assertion in the repository that observes the Kubernetes
    ///             behaviour the whole recovery window rests on rather than assuming it.
    ///         </b> docs/plan/08
    ///         § Soft delete says a soft-deleted resource keeps its disks because
    ///         <i>
    ///             "deleting a
    ///             <c>StatefulSet</c> does not delete the <c>PersistentVolumeClaim</c>s its
    ///             <c>volumeClaimTemplate</c> created"
    ///         </i> — that is the API server's behaviour and no
    ///         provider's, so a fake cluster can only ever model it. Here the claims are made by the
    ///         real controller, survive a real teardown, and are read back with the raw
    ///         <c>KubernetesClient</c> around every line of our own code.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It also verifies the evidence the purge's guard is built on.</b>
    ///         <c>VolumeReclaimer</c> refuses to delete a claim that does not carry the labels its
    ///         provider named, and the labels a claim carries are the set's own
    ///         <c>spec.selector.matchLabels</c> — copied by the controller, not written by us, and
    ///         therefore exactly the sort of thing that is true until it is not. The assertion below
    ///         reads them off the stored object. If Kubernetes ever stopped copying them the purge
    ///         would <i>refuse</i> rather than delete, which is the safe direction; this is what would
    ///         say so out loud.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A family whose objects create no claim on a real cluster SKIPS.</b> Most of the
    ///         catalogue delegates its storage to an operator whose claims this repository cannot
    ///         name, and a suite that asserted over an empty list would report the green its own
    ///         vacuity guard exists to refuse.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task TheClaimsAStatefulSetLeavesBehindOutliveTheTeardownAndThePurgeRemovesThem() {
        var harness = Fixture.Require(
            "that a real StatefulSet controller leaves its volumeClaimTemplate's claims behind when "
            + "the set is deleted — which is what docs/plan/08 § Soft delete's recovery window is "
            + "made of — and that a purge then removes exactly those."
        );

        var token = TestContext.Current.CancellationToken;
        const string name = "real-claims";

        harness.Registry.TryGetType(Case.Type, out var registration).ShouldBeTrue();
        var recoverable = registration.SoftDeleteDays > 0;

        var accepted = (await WriteAsync(harness, name)).GetValueOrThrow();
        var status = await ConvergeAsync(harness, accepted.OperationId);
        status.State.ShouldBe(OperationState.Succeeded, status.Error?.Message);

        var claims = await ClaimsOfResourceAsync(harness, accepted.Resource.Id, token);

        if (claims.Count == 0) {
            Assert.Skip(
                $"SKIPPED — {Case.DisplayName} created no PersistentVolumeClaim on a real cluster, so "
                + "there is nothing a teardown could keep and nothing a purge could remove. A family "
                + "whose storage belongs to an operator is legitimate; this is the suite saying so "
                + "rather than passing over an empty list."
            );
        }

        // ⚠ The evidence the guard checks, read off the object the API server made.
        foreach (var claim in claims) {
            claim.Value.ShouldNotBeNull(
                $"the claim '{claim.Key}' the StatefulSet controller created carries no labels at "
                + "all, so nothing connects it to the resource that owns it and VolumeReclaimer "
                + "would refuse to remove it. Kubernetes copies a set's spec.selector.matchLabels "
                + "onto every claim its volumeClaimTemplate produces — that is the ownership "
                + "evidence RetainedVolume.OwnedBy is built from."
            );
        }

        var deleted = await harness.Manager.DeleteAsync(
            new() {
                Path = ClusterConformanceHarness<TSource>.Address(name).Path,
                ApiVersion = Case.ApiVersion,
                Caller = ClusterConformanceHarness<TSource>.Caller()
            },
            token
        );

        deleted.IsSuccess.ShouldBeTrue(deleted.Error?.Message);

        var teardown = await ConvergeAsync(harness, deleted.GetValueOrThrow().OperationId);
        teardown.State.ShouldBe(
            OperationState.Succeeded,
            $"the teardown ended {teardown.State}: {teardown.Error?.Message}"
        );

        if (!recoverable) {
            (await ClaimsOfResourceAsync(harness, accepted.Resource.Id, token, false)).ShouldBeEmpty(
                "this type declares no recovery window, so its delete is final — and a final "
                + "teardown that leaves the claims returns the tenant's quota and keeps their disks."
            );

            return;
        }

        // ── The property the window is made of, observed rather than assumed ────────────────────
        // ⚠ Contains, not equals. "claims" is a floor read while the controller was still working:
        // ordinal 1's claim can appear between that read and the delete, and it's kept like the
        // rest. What the window depends on is that nothing read before the delete is gone after it.
        var kept = await ClaimsOfResourceAsync(harness, accepted.Resource.Id, token);

        claims.Keys.ShouldBeSubsetOf(
            kept.Keys,
            "a soft delete removed a claim on a real API server. Deleting a StatefulSet is not "
            + "supposed to delete the claims its volumeClaimTemplate made, and that behaviour is the "
            + "whole of what this type's recovery window hands back."
        );

        var purged = await harness.Manager.PurgeAsync(
            new() {
                Path = ClusterConformanceHarness<TSource>.Address(name).Path,
                ApiVersion = Case.ApiVersion,
                Caller = ClusterConformanceHarness<TSource>.Caller()
            },
            token
        );

        purged.IsSuccess.ShouldBeTrue(purged.Error?.Message);

        var ended = await ConvergeAsync(harness, purged.GetValueOrThrow().OperationId);
        ended.State.ShouldBe(
            OperationState.Succeeded,
            $"the purge ended {ended.State}: {ended.Error?.Message}"
        );

        (await ClaimsOfResourceAsync(harness, accepted.Resource.Id, token, false)).ShouldBeEmpty(
            "the purge left the volumes behind. docs/plan/08 § Soft delete: ending a window has to "
            + "remove exactly what a teardown keeps, or a purged resource returns its quota, frees "
            + "its name and leaves its disks allocated with nothing pointing at them."
        );
    }

    /// <summary>
    ///     Every <c>PersistentVolumeClaim</c> the real cluster is holding in this suite's namespace,
    ///     by name, with its labels.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The raw client, and the whole namespace rather than a predicted list of names.</b>
    ///         A test that asked the API server only for the claims it expected could not tell a
    ///         purge that removed the right ones from a purge that removed those and left others —
    ///         and "the namespace holds nothing" is also the condition <c>NamespaceReclaim.Decide</c>
    ///         requires before a resource group's namespace can ever be deleted.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It polls, because the claims are made by a controller rather than by us.</b> The
    ///         <c>StatefulSet</c> controller creates a pod's claims when it creates the pod, which is
    ///         some time after the apply the reconcile loop converged on. A single read here would be
    ///         a test that skipped for timing reasons and reported it as a family with no storage.
    ///     </para>
    /// </remarks>
    /// <summary>The claims in the namespace that belong to <paramref name="resourceId" />.</summary>
    /// <remarks>
    ///     ⚠ <b>SCOPED TO ONE RESOURCE, AND IT WAS NAMESPACE-WIDE.</b> Every class in this suite
    ///     shares one k3s and one namespace, so a namespace-wide list returns the claims of every
    ///     <i>other</i> test's resource too. The final "the purge removed them" assertion therefore
    ///     failed for a reason that had nothing to do with the purge — fifteen claims across five
    ///     sibling resources, none of them the one under test, whose own claims had been removed
    ///     correctly. The earlier assertions did not catch it because <b>both sides</b> of the
    ///     comparison were namespace-wide and so agreed.
    ///     <para>
    ///         The filter is <c>cybercloud.io/resource-id</c>, which a claim carries only because
    ///         <c>WithTemplateLabels</c> puts the six lifetime-stable labels into a
    ///         <c>volumeClaimTemplate</c>. Before that this scoping could not have been written —
    ///         which is why the test was, correctly, namespace-wide when it was drafted.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             The poll waits for a floor, and the claims can still grow past it.
    ///         </b> The floor is what the controller creates without waiting on a pod: ordinal 0's
    ///         claims, one per <c>volumeClaimTemplate</c> of every set of this resource with a
    ///         replica. Ordinal 1's claim waits for ordinal 0 to be ready, which depends on an image
    ///         pull, so no count past the floor is one a read can wait for. Returning on the first
    ///         claim instead let <c>CyberCloud.ContainerRegistry/registries</c>, which renders three
    ///         sets, read one claim before the delete and three after it. So a caller comparing two
    ///         reads compares by containment, and a floor the controller never reaches fails here
    ///         rather than coming back as a partial set.
    ///     </para>
    /// </remarks>
    static async Task<Dictionary<string, IDictionary<string, string>?>> ClaimsOfResourceAsync(
        ClusterConformanceHarness<TSource> harness,
        Guid resourceId,
        CancellationToken cancellationToken,
        bool expectSome = true
    ) {
        Dictionary<string, IDictionary<string, string>?> found = [];
        var selector = $"{KubeLabels.ResourceId}={KubeLabels.GuidValue(resourceId)}";

        using var sets = await harness.Raw.AppsV1.ListNamespacedStatefulSetWithHttpMessagesAsync(
            ClusterConformanceHarness<TSource>.Namespace,
            labelSelector: selector,
            cancellationToken: cancellationToken
        );

        var promised = sets.Body.Items.Where(static x => (x.Spec.Replicas ?? 1) > 0)
            .Sum(static x => x.Spec.VolumeClaimTemplates?.Count ?? 0);

        var floor = Math.Max(1, promised);

        for (var attempt = 0; attempt < 10; attempt++) {
            using var listed = await harness.Raw.CoreV1.ListNamespacedPersistentVolumeClaimWithHttpMessagesAsync(
                ClusterConformanceHarness<TSource>.Namespace,
                labelSelector: selector,
                cancellationToken: cancellationToken
            );

            found = listed.Body.Items.ToDictionary<V1PersistentVolumeClaim, string, IDictionary<string, string>?>(
                static x => x.Metadata.Name,
                static x => x.Metadata.Labels,
                StringComparer.Ordinal
            );

            if (expectSome ? found.Count >= floor : found.Count == 0) {
                return found;
            }

            await Task.Delay(BetweenDrives, cancellationToken);
        }

        if (expectSome && found.Count < promised) {
            Assert.Fail(
                $"the resource's StatefulSets declare {promised} volumeClaimTemplate(s) across the sets "
                + $"with a replica, and after ten reads the API server holds {found.Count} claim(s) "
                + $"carrying its resource-id ({string.Join(", ", found.Keys)}). Ordinal 0's claims "
                + "don't wait on a pod, so the StatefulSet controller never made them, or they "
                + "don't carry the label WithTemplateLabels puts in a template."
            );
        }

        return found;
    }

    // ── 2. A field conflict with another manager becomes a named DriftEvent ────────────────────

    [Fact]
    public async Task AFieldConflictWithAnotherManagerBecomesADriftEventWithAName() {
        var harness = Fixture.Require(
            "ADR-013's 'if a tenant hand-edits a field we own, the next apply reports a conflict "
            + "rather than silently reverting, and THAT becomes a drift event with a name' — "
            + "specifically that the API server's 409 body parses into the field paths and owner "
            + "names ConflictParser expects."
        );

        var token = TestContext.Current.CancellationToken;
        const string name = "real-conflict";
        const string rival = "tenant-kubectl";

        var accepted = (await WriteAsync(harness, name)).GetValueOrThrow();
        await ConvergeAsync(harness, accepted.OperationId);

        var target = ObjectsOf(accepted.Resource.Id, name)[0];

        // ⚠ THE TENANT'S HAND EDIT, AS A DIFFERENT FIELD MANAGER, TAKING A FIELD WE OWN.
        //
        // The field is `cybercloud.io/tenant-id`, and it is chosen rather than picked at random for
        // two reasons. It is a field EVERY provider's rendered object carries — it is one of the
        // seven, so this test does not need the case to nominate a contested field, and cannot be
        // written to contest one the provider happens not to own. And it is the case ADR-013's
        // admission policy is the belt to these braces: a tenant who owns the tenant-id label owns
        // billing attribution. `force: true` here is the rival TAKING ownership, which is what
        // `kubectl apply --force-conflicts` does; our side's Force is pinned false and the builder
        // has no method to change it.
        await RivalApplyAsync(
            harness,
            target,
            rival,
            new JsonObject {
                ["apiVersion"] = ApiVersionOf(target.Kind),
                ["kind"] = target.Kind.Kind,
                ["metadata"] = new JsonObject {
                    ["name"] = target.Name,
                    ["namespace"] = target.Namespace,
                    ["labels"] = new JsonObject { [KubeLabels.TenantId] = "00000000-0000-0000-0000-0000000000ff" }
                }
            }.ToJsonString(),
            token
        );

        (await LabelAsync(harness, target, KubeLabels.TenantId, token))
            .ShouldBe("00000000-0000-0000-0000-0000000000ff", "the rival's apply did not land.");

        // Now re-drive the provider's OWN reconciler through the real connection. Nothing about the
        // desired state changed; the conflict is entirely the API server's doing.
        harness.Connection.Reset();
        var outcome = await ReconcileOnceAsync(harness, accepted.Resource.Id, name);

        // (a) The reconciler did not report a failure. A tenant editing their own cluster is drift,
        //     not a provisioning failure — IKubeClusterConnection's own remarks make that the rule.
        outcome.Kind.ShouldNotBe(
            ReconcileOutcomeKind.Failed,
            $"a field conflict became a failed reconcile: {outcome.Error?.Message}"
        );

        // (b) It is a DRIFT EVENT WITH A NAME, produced by parsing the API server's own 409 body.
        harness.Connection.Drift.ShouldNotBeEmpty(
            "the apply did not conflict at all. Either force is no longer pinned false, or the rival "
            + "did not take a field we own. What the API server answered: "
            + string.Join(
                ", ",
                harness.Connection.Outcomes.Select(static x => x.Result.ToString() + " " + x.Message)
            )
        );

        var drift = harness.Connection.Drift[0];

        drift.ClusterId.ShouldBe(ClusterConformanceHarness<TSource>.ClusterId);
        drift.ResourceId.ShouldBe(accepted.Resource.Id);
        drift.FieldManager.ShouldBe(FieldManagerOf(harness));

        drift.Conflicts.ShouldNotBeEmpty(
            "the 409 must be parsed into named fields, not reported as 'conflict'. ConflictParser got: "
            + drift.Describe()
        );

        drift.Conflicts.ShouldContain(
            x => x.Field.Contains("tenant-id", StringComparison.Ordinal),
            "the conflict does not name the contested field: " + drift.Describe()
        );

        drift.Conflicts.ShouldContain(
            x => x.OwnedBy == rival,
            "the rival manager's name is only in the PROSE of the 409's causes[].message — there is "
            + "no structured field for it — so this is the assertion that ConflictParser reads a real "
            + "API server's wording rather than one this repository invented. It got: "
            + drift.Describe()
        );

        // (c) ⚠ NOT A SILENT REVERT. The tenant's value is still there.
        (await LabelAsync(harness, target, KubeLabels.TenantId, token)).ShouldBe(
            "00000000-0000-0000-0000-0000000000ff",
            "force is pinned false, so a conflicting apply must leave the rival's value alone. This "
            + "is the half that distinguishes a conflict from the silent revert ADR-013 exists to "
            + "replace."
        );

        // Not this test's subject; see TearDownAsync.
        await TearDownAsync(harness, name);
    }

    // ── 3. Drift corrected after a real kubectl delete ─────────────────────────────────────────

    [Fact]
    public async Task DriftIsCorrectedAfterARealKubectlDelete() {
        var harness = Fixture.Require(
            "the hourly per-cluster scan of docs/plan/08 § The reconcile loop, including the orphan "
            + "and stray cases, which are the two things nothing else would find."
        );

        var token = TestContext.Current.CancellationToken;
        const string name = "real-drift";

        var accepted = (await WriteAsync(harness, name)).GetValueOrThrow();
        await ConvergeAsync(harness, accepted.OperationId);

        var owned = ObjectsOf(accepted.Resource.Id, name);
        var target = owned[0];
        var desiredHash = harness.Connection.Applied.Last().ReconcileHash;

        // ⚠ A REAL `kubectl delete`, THROUGH THE RAW CLIENT, BEHIND THE RECONCILER'S BACK. Not
        // IKubeClusterConnection.DeleteAsync — the point of breaking the world is that it happens by
        // a path the platform cannot observe or record.
        //
        // ⚠ EVERY OBJECT THE RESOURCE OWNS, AND UNTIL 2026-08-12 IT WAS ONLY THE FIRST. A stray is a
        // resource in Succeeded with NO labelled object on the cluster — `DriftScanner` joins on the
        // resource-id label — so deleting one of two leaves the join satisfied by the survivor and
        // the scan correctly reports no stray. The assertion below then fails, and it fails for the
        // suite's reason rather than the provider's.
        //
        // Nothing had caught it because the only provider with a `.Cluster.Conformance` project
        // rendered ONE object: `CyberCloud.Sample/widgets`, a single ConfigMap. The second one to get
        // that project, `CyberCloud.Messaging/kafkaClusters`, renders a Kafka and a KafkaNodePool and
        // went red here on its first run. `CyberCloud.DBforPostgreSQL/servers` has rendered two
        // objects since it landed and would have found it a fortnight earlier if it had had the
        // project.
        //
        // "Somebody kubectl-deleted production" is the whole set, not an arbitrary member of it —
        // PARTIAL loss is a different finding, and one this suite does not yet assert. It is worth
        // having: a resource whose Kafka survives and whose node pool is gone is running and
        // unrunnable at once, and the scan reports it as neither a stray nor an orphan today.
        // ⚠ THE ABSENCE IS WAITED FOR RATHER THAN ASSERTED, AND UNTIL CyberCloud.Terminal/consoles IT
        // WAS ASSERTED. "Deleted" and "gone" are different states for any object carrying a finalizer,
        // and the API server adds `kubernetes.io/pvc-protection` to EVERY PersistentVolumeClaim on
        // admission — so a delete sets a `deletionTimestamp` and the object stays readable until the
        // pvc-protection controller removes it. The eleven families before that one render custom
        // resources and core objects with no finalizer, so this read came back null on the first try
        // every time and the assumption never cost anything.
        //
        // ⚠ IT IS NOT A SLEEP AND IT IS NOT A RACE. The wait is bounded and its failure message is the
        // same one the assertion gave, so a delete that genuinely does not land still fails here and
        // still names the object. What it stops doing is calling a finalizer a bug.
        // ⚠ THE ORPHAN'S SPEC IS COPIED FROM A REAL OBJECT, AND IT IS READ BEFORE THE DELETE.
        //
        // The orphan below used to be applied as apiVersion + kind + metadata and nothing else. That
        // was a valid object for a custom resource while the harness installed a stub whose schema
        // was `x-kubernetes-preserve-unknown-fields` — since issue #91 the harness installs the
        // operator's real definition and a Kafka with no `spec.kafka` is as invalid as the built-in
        // below — and it is an INVALID object for a built-in with required spec fields.
        // `CyberCloud.Terminal/consoles` is the first family to render one, and the API server
        // refused the metadata-only apply outright:
        //
        //   PersistentVolumeClaim "real-drift-orphan" is invalid:
        //     spec.accessModes: Required value: at least 1 access mode is required,
        //     spec.resources[storage]: Required value
        //
        // Copying the spec off an object the provider really converged is generic — it works for a
        // CRD and a built-in alike, it needs no new member on ProviderConformanceCase, and it is the
        // more faithful fake: a rival controller creating a lookalike creates a VALID one. The read
        // has to happen here, before the loop below deletes everything it could have read.
        var orphanSpec =
            JsonNode.Parse(await ReadFromClusterAsync(harness, target, token) ?? "{}") is JsonObject real
            && real.TryGetPropertyValue("spec", out var spec)
                ? spec?.DeepClone()
                : null;

        foreach (var each in owned) {
            await DeleteFromClusterAsync(harness, each, token);
            await WaitUntilAbsentAsync(harness, each, token);
        }

        // ── An orphan: a labelled object whose resource grain does not exist. Applied by somebody
        //    else entirely, carrying our managed-by label and a resource-id nothing owns. ─────────
        var orphanId = Guid.Parse("0a0a0a0a-0000-4000-8000-00000000000a");
        var orphanTarget = target with { Name = "real-drift-orphan" };

        var orphanBody = new JsonObject {
            ["apiVersion"] = ApiVersionOf(target.Kind),
            ["kind"] = target.Kind.Kind,
            ["metadata"] = new JsonObject {
                ["name"] = orphanTarget.Name,
                ["namespace"] = orphanTarget.Namespace,
                ["labels"] = new JsonObject {
                    [KubeLabels.ManagedBy] = KubeLabels.ManagedByValue,
                    [KubeLabels.ResourceId] = KubeLabels.GuidValue(orphanId)
                },
                ["annotations"] = new JsonObject { [KubeLabels.ResourcePathAnnotation] = "/orphaned/by/nobody" }
            }
        };

        // ⚠ ADDED only when there is one, never assigned. `orphanBody["spec"] = null` writes
        // `"spec": null`, and a kind with no spec at all refuses that outright —
        // `CyberCloud.Storage/accounts` renders a Secret and the API server answered
        // 500 "failed to create typed patch object … .spec: field not declared in schema".
        // A kind with no spec must get a body with no spec key, which is exactly what the eleven
        // families that render custom resources always sent.
        if (orphanSpec is not null) {
            orphanBody["spec"] = orphanSpec;
        }

        await RivalApplyAsync(harness, orphanTarget, "someone-elses-controller", orphanBody.ToJsonString(), token);

        // ── THE SCAN. A real LIST against the real API server, filtered by the selector
        //    docs/plan/09 § Observing specifies, joined on the resource-id label ADR-013 puts there
        //    precisely so that this is a hash join rather than a scan. ────────────────────────────
        // ⚠ THE NAMESPACE IS DERIVED FROM THE OBJECTS RATHER THAN ASSUMED, AND FOR A CLUSTER-SCOPED
        // KIND IT IS EMPTY. `KubeApiClient.ListAsync` already branches on exactly that — an empty
        // namespace selects `ListClusterCustomObject` — so the shipping code was right and only this
        // call site was passing a namespace a cluster-scoped kind has no REST path for. The symptom
        // was the DISCOVERY error, "does not serve kubeovn.io/v1 Vpc (as vpcs) … install or upgrade
        // the operator that provides it", which points at a missing CRD and is the one message
        // guaranteed to send a reader looking in the wrong place.
        var objectsUnderTest = ObjectsOf(accepted.Resource.Id, name);

        var inventory = new ListBackedClusterObjectInventory(
            harness.Api,
            [.. objectsUnderTest.Select(static x => x.Kind).Distinct()],
            objectsUnderTest.All(static x => x.IsClusterScoped)
                ? string.Empty
                : ClusterConformanceHarness<TSource>.Namespace
        );

        var objects = await inventory.ListManagedAsync(ClusterConformanceHarness<TSource>.ClusterId, token);
        objects.IsSuccess.ShouldBeTrue(objects.Error?.Message);

        var report = new DriftScanner(harness.Clock).Scan(
            ClusterConformanceHarness<TSource>.ClusterId,
            objects.GetValueOrThrow(),
            [
                new ExpectedResource(
                    accepted.Resource.Id,
                    ClusterConformanceHarness<TSource>.Address(name).Path,
                    desiredHash,
                    ProvisioningState.Succeeded
                )
            ]
        );

        // ⚠ THE STRAY. Somebody kubectl-deleted production, and this is the platform NOTICING ON ITS
        // OWN rather than finding out because a reconciler happened to be re-driven.
        report.Strays.ShouldContain(
            x => x.ResourceId == accepted.Resource.Id,
            "the scan did not notice that a Succeeded resource has no labelled object on the cluster. "
            + report.ToString()
        );

        // ⚠ THE ORPHAN. Labelled objects nobody owns: running, and nothing is metering them.
        report.Orphans.ShouldContain(
            x => x.ResourceId == orphanId,
            "the scan did not notice a labelled object with no owning resource grain. " + report.ToString()
        );

        report.ObjectsSeen.ShouldBeGreaterThan(
            0,
            "an inventory that saw nothing would report every resource as a stray, which is the "
            + "failure UnavailableClusterObjectInventory refuses rather than risks."
        );

        // ── AND THE DRIFT IS CORRECTED. The scan reports; docs/plan/08 § The reconcile loop's
        //    "pokes only what diverged" is the re-drive, and this is it. ─────────────────────────
        var repaired = await ReconcileOnceAsync(harness, accepted.Resource.Id, name);

        repaired.Kind.ShouldBe(
            ReconcileOutcomeKind.Converged,
            $"the re-drive did not converge: {repaired.Reason} {repaired.Error?.Message}"
        );

        var restored = await ReadFromClusterAsync(harness, target, token);

        restored.ShouldNotBeNull(
            $"'{target}' was not put back by the re-drive, so the drift was noticed and not corrected."
        );

        MatchesDesired(accepted.Resource.Id, name, target, restored!, Body()).ShouldBeTrue(
            "the object came back without the desired shape."
        );

        // And the scan agrees, which is the only way to know the repair closed the finding rather
        // than merely creating an object.
        var after = new DriftScanner(harness.Clock).Scan(
            ClusterConformanceHarness<TSource>.ClusterId,
            (await inventory.ListManagedAsync(ClusterConformanceHarness<TSource>.ClusterId, token))
            .GetValueOrThrow(),
            [
                new ExpectedResource(
                    accepted.Resource.Id,
                    ClusterConformanceHarness<TSource>.Address(name).Path,
                    harness.Connection.Applied.Last().ReconcileHash,
                    ProvisioningState.Succeeded
                )
            ]
        );

        after.Strays.ShouldNotContain(
            x => x.ResourceId == accepted.Resource.Id,
            "the object is back but the scan still calls it a stray. " + after.ToString()
        );

        // Not this test's subject; see TearDownAsync.
        await TearDownAsync(harness, name);
    }

    // ── 3b. What a real namespace actually holds ────────────────────────────────────────────────

    /// <summary>
    ///     What a resource-group reclaim sees of this family's resource on a real API server — while
    ///     it's live, and after it's gone.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             The listing has to succeed, or the cluster's age picks what's asserted.
    ///         </b> An aggregated API that isn't answering yet makes discovery refuse the whole
    ///         listing, and a test that accepted a refusal as an outcome asserts one thing on a young
    ///         k3s and another on an old one — <c>CyberCloud.Network</c>'s suite went green or red on
    ///         one tree that way. So the recipe installs no metrics-server
    ///         (<see cref="Infrastructure.ClusterInfrastructure.DisableMetricsServer" />),
    ///         <see cref="ListNamespaceAsync" /> waits for every <c>APIService</c> and fails on a
    ///         refusal, and the refusal itself is asserted where it's provoked on purpose,
    ///         <c>NamespaceDiscoveryRefusalTests</c>. ⚠ And a verdict is never checked against the
    ///         listing it was computed from: <see cref="NamespaceReclaim.Decide" /> weighed against its
    ///         own input can't fail, so each assertion below is about an object the test put there.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             Decided: the reclaim looks at the namespace and nothing else, and a cluster-scoped
    ///             object is not its business.
    ///         </b> Teaching it to see the cluster-scoped objects the
    ///         group's resources own would protect nothing. A namespace delete
    ///         removes namespaced objects only, and the garbage collector can't reach a cluster-scoped
    ///         object through a namespaced owner: Kubernetes treats that owner reference as
    ///         unresolvable and never collects the dependent. So the recursive delete the verdict
    ///         authorizes can't harm a <c>Vpc</c>. What protects a live resource of any scope is the
    ///         other half of the evidence — the group's members, which
    ///         <c>IResourceGroupGrain.BeginGroupDeleteAsync</c> refuses over — and what removes a
    ///         cluster-scoped object is its own resource's teardown. One that outlives its resource is
    ///         an orphan for the drift scan to find, and refusing a namespace over it would keep an
    ///         empty namespace forever without removing the leak. ⚠ Nothing finds such an orphan
    ///         today: the shipped cluster inventory refuses, so the scan can't run against a real
    ///         cluster (<c>DriftScanner</c>'s remarks). docs/plan/08 § Reclaiming a resource group's
    ///         namespace records the same decision and the same gap.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>So what's asserted follows from the scope of what the case renders.</b>
    ///         <c>ObjectRef.IsClusterScoped</c> is the declaration — each object the case names carries
    ///         its own, and the lifecycle test proves each against the REST path the API server
    ///         actually serves. For every family, the live resource is a real member and the verdict
    ///         refuses over it. For the namespaced objects, the listing holds each one and the
    ///         namespace evidence refuses on its own, naming it. For the cluster-scoped ones, they're in
    ///         the cluster and nowhere in the namespace — and a family with nothing namespaced leaves
    ///         the namespace as deletable as it found it. After the teardown, nothing labelled as the
    ///         resource is left in the namespace and nothing it rendered is left outside it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Scoped to this test's own resource, by <c>cybercloud.io/resource-id</c>.</b> Every
    ///         class in a provider's assembly shares one namespace, and a harness leaves its ancestors,
    ///         siblings and companions standing, so anything keyed on the whole namespace is an
    ///         assertion about which of those a family happens to have. The limit is the test's and
    ///         not the product's: an object a controller made from ours and didn't label is invisible
    ///         to this filter and still counted by the real reclaim, which weighs every occupant.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task ARealNamespaceHoldsWhatKubernetesPutsThereAndTheReclaimSeesIt() {
        // ⚠ THIS IS THE ONLY PLACE THE NAMESPACE INVENTORY MEETS A REAL API SERVER, AND IT IS WHERE
        // THE RULE IT FEEDS WAS FOUND TO BE UNSATISFIABLE. NamespaceReclaim.Decide said "the
        // namespace holds nothing at all", and every test that had ever weighed it supplied an empty
        // array — because INamespaceInventory had no implementation. A conformant cluster makes that
        // rule impossible: the service-account controller creates ServiceAccount/default in every
        // namespace and recreates it when it is deleted, and the root-CA publisher does the same with
        // ConfigMap/kube-root-ca.crt. Deletable would have been false forever in production while
        // being true in every unit test. NamespaceReclaim.IsAmbient is the answer and this is the
        // measurement behind it.
        var harness = Fixture.Require(
            "that a namespace listing is a real API discovery plus a list per served kind, and that "
            + "a reclaim weighing it refuses over this family's live resource and not over its "
            + "remains. A dictionary cannot fail discovery, cannot serve a CRD, and — most of all — "
            + "does not put anything in a namespace on its own."
        );

        var token = TestContext.Current.CancellationToken;
        var ns = ClusterConformanceHarness<TSource>.Namespace;
        var clusterId = ClusterConformanceHarness<TSource>.ClusterId;

        // ── 1. What Kubernetes puts there, read AROUND our own code ─────────────────────────────
        //
        // ⚠ The raw client, deliberately. What is being measured is what KUBERNETES puts in a
        // namespace, and reading it through the seam under test would let a bug in the seam hide the
        // very objects the seam exists to find.
        using var accounts = await harness.Raw.CoreV1
            .ListNamespacedServiceAccountWithHttpMessagesAsync(ns, cancellationToken: token);

        using var maps = await harness.Raw.CoreV1
            .ListNamespacedConfigMapWithHttpMessagesAsync(ns, cancellationToken: token);

        var ambient = ImmutableArray.Create(
            Occupant("ServiceAccount", accounts.Body.Items.Single(static x => x.Metadata.Name == "default")),
            Occupant("ConfigMap", maps.Body.Items.Single(static x => x.Metadata.Name == "kube-root-ca.crt"))
        );

        // ⚠ AND BOTH ARE UNLABELLED, which is what makes them indistinguishable from a tenant's own
        // object by every rule except their names — the reason IsAmbient matches on kind AND name
        // rather than on a kind.
        ambient[0].IsManaged.ShouldBeFalse();
        ambient[1].IsManaged.ShouldBeFalse();

        NamespaceReclaim.Decide(clusterId, ns, [], ambient)
            .Deletable
            .ShouldBeTrue(
                "a namespace holding nothing but Kubernetes' own objects is the state a finished "
                + "resource group leaves behind, and under the original 'nothing at all' rule it was "
                + "never deletable — which no test could show while nothing could list."
            );

        // ── 2. This family's resource, live ─────────────────────────────────────────────────────
        const string name = "real-reclaim";

        harness.Registry.TryGetType(Case.Type, out var registration).ShouldBeTrue();

        var accepted = (await WriteAsync(harness, name)).GetValueOrThrow();
        var status = await ConvergeAsync(harness, accepted.OperationId);
        status.State.ShouldBe(OperationState.Succeeded, status.Error?.Message);

        var id = accepted.Resource.Id;
        var rendered = ObjectsOf(id, name);
        var inside = rendered.Where(static x => !x.IsClusterScoped).ToList();
        var outside = rendered.Where(static x => x.IsClusterScoped).ToList();

        var scope = inside.Count == 0
            ? "cluster-scoped"
            : outside.Count == 0
                ? "namespaced"
                : "mixed";

        // ⚠ THE MEMBERS, FROM THE REAL GROUP GRAIN. This is the half of the evidence that protects a
        // live resource of every scope — a cluster-scoped one has nothing else — and it is read, not
        // supplied: a verdict weighed against a member list this test wrote would prove Decide and
        // nothing about the write path that is supposed to record membership.
        var members = await MembersAsync(harness);

        members.ShouldContain(
            x => x.ResourceId == id,
            $"'{accepted.Resource.Path}' converged and its resource group does not list it, so a "
            + "group delete would find no member to refuse over."
        );

        var live = await ListNamespaceAsync(harness, token);

        var verdict = NamespaceReclaim.Decide(clusterId, ns, members, [.. live.Select(static x => Occupant(x))]);

        verdict.Deletable.ShouldBeFalse(
            $"a reclaim authorized a recursive delete of '{ns}' while '{accepted.Resource.Path}' is "
            + "live in it: " + verdict.Explain()
        );

        verdict.Explain()
            .ShouldContain(
                $"still holds {members.Count} member(s)",
                Shouldly.Case.Sensitive,
                "the refusal must count the members it refused over, or an operator cannot tell the "
                + "group's own resources from the namespace's other occupants."
            );

        var ours = OursIn(live, id);

        if (inside.Count > 0) {
            // ⚠ THE NAMESPACED HALF. Every namespaced object the case says it renders is in the
            // listing — the listing is what the reclaim reads — and the namespace evidence refuses on
            // its own. "On its own" is the point: membership is a bookkeeping write that can fail
            // (docs/plan/06 § Two-phase create), and a resource whose member record went missing must
            // still not have its objects deleted by a namespace delete.
            foreach (var target in inside) {
                live.ShouldContain(
                    x => x.Kind.Kind == target.Kind.Kind && x.Name == target.Name,
                    $"'{target}' is in the real cluster — the lifecycle test reads it back — and the "
                    + $"listing of '{ns}' does not hold it, so a reclaim would delete it without "
                    + "having seen it."
                );
            }

            var alone = NamespaceReclaim.Decide(clusterId, ns, [], Evidence(live, id));

            alone.Deletable.ShouldBeFalse(
                $"'{accepted.Resource.Path}' is {scope} and its namespaced objects are in '{ns}', and "
                + "with no member list a reclaim would delete them: " + alone.Explain()
            );

            // ⚠ The ordinal-first one, because the refusal samples its names sorted and takes five.
            var named = ours.Select(static x => x.Kind.Kind + "/" + x.Name).Order(StringComparer.Ordinal).First();

            alone.Explain()
                .ShouldContain(
                    named,
                    Shouldly.Case.Sensitive,
                    "the refusal must name what it found, so an operator knows what would be deleted."
                );
        }

        if (outside.Count > 0) {
            // ⚠ THE CLUSTER-SCOPED HALF. In the cluster, at the path with no namespace segment, and
            // attributed to this resource — so it exists, and it is not the namespace's.
            foreach (var target in outside) {
                var json = await ReadFromClusterAsync(harness, target, token);

                json.ShouldNotBeNull($"'{target}' is not in the real cluster while its resource is live.");

                var metadata = JsonNode.Parse(json)!["metadata"]!;

                (metadata["namespace"]?.GetValue<string>()).ShouldBeNullOrEmpty(
                    $"'{target}' is declared cluster-scoped and the API server stored it in a namespace."
                );

                (metadata["labels"]?[KubeLabels.ResourceId]?.GetValue<string>()).ShouldBe(
                    KubeLabels.GuidValue(id),
                    $"'{target}' does not carry this resource's id, so nothing attributes it to the "
                    + "resource whose teardown has to remove it."
                );
            }
        }

        if (inside.Count == 0) {
            // ⚠ A FAMILY WITH NOTHING NAMESPACED PUTS NOTHING OF THIS RESOURCE IN THE NAMESPACE, and
            // the namespace evidence alone therefore says "deletable" — which is correct, because the
            // delete it authorizes cannot reach a cluster-scoped object. This is the arm the old
            // assertion had no way to state.
            ours.ShouldBeEmpty(
                $"'{Case.Type}' renders only cluster-scoped objects, and '{ns}' holds something "
                + "labelled as this resource: "
                + string.Join(", ", ours.Select(static x => x.Kind.Kind + "/" + x.Name))
                + ". Either the case's Objects omits a namespaced object it renders — which would make "
                + "the family mixed — or a controller made it."
            );

            var alone = NamespaceReclaim.Decide(clusterId, ns, [], Evidence(live, id));

            alone.Deletable.ShouldBeTrue(
                "the namespace holds nothing of this cluster-scoped resource, and the namespace "
                + "evidence refused anyway: " + alone.Explain()
            );
        }

        // ── 3. Gone ─────────────────────────────────────────────────────────────────────────────
        await TearDownAsync(harness, name);

        // ⚠ A type with a recovery window keeps its claims on a delete — on purpose, and a namespace
        // holding them must not be reclaimed (docs/plan/08 § Soft delete). The purge is what ends
        // that, so the purge is what "gone" is measured after.
        if (registration.SoftDeleteDays > 0) {
            var purged = await harness.Manager.PurgeAsync(
                new() {
                    Path = ClusterConformanceHarness<TSource>.Address(name).Path,
                    ApiVersion = Case.ApiVersion,
                    Caller = ClusterConformanceHarness<TSource>.Caller()
                },
                token
            );

            purged.IsSuccess.ShouldBeTrue(purged.Error?.Message);

            var ended = await ConvergeAsync(harness, purged.GetValueOrThrow().OperationId);
            ended.State.ShouldBe(OperationState.Succeeded, $"the purge ended {ended.State}: {ended.Error?.Message}");
        }

        (await MembersAsync(harness)).ShouldNotContain(
            x => x.ResourceId == id,
            $"'{accepted.Resource.Path}' was torn down and its group still lists it, so a group delete "
            + "would refuse over a resource that no longer exists."
        );

        // ⚠ Outside the namespace, the teardown is the only thing that removes it: the reclaim never
        // looks there, by the decision in the remarks.
        foreach (var target in outside) {
            (await ReadFromClusterAsync(harness, target, token)).ShouldBeNull(
                $"'{target}' outlived its resource. A namespace reclaim will never see a cluster-scoped "
                + "object, so nothing but the teardown was ever going to remove it."
            );
        }

        // ⚠ AND INSIDE IT, NOTHING OF THIS RESOURCE — WAITED FOR, BECAUSE A TEARDOWN THAT CONVERGED
        // CAN STILL HAVE A CONTROLLER'S WORK IN FLIGHT. A background cascade removes a set's pods after
        // the set, and pvc-protection holds a claim until they are gone; the reclaim refuses over both
        // until then, which is right. What never clears is an object the family forgot, and that
        // fails here with the reclaim's own words naming it.
        var remains = await RemainsAsync(harness, id, token);

        remains.Deletable.ShouldBeTrue(
            $"'{accepted.Resource.Path}' ({scope}) was torn down and left something in '{ns}' that a "
            + "group reclaim refuses over: " + remains.Explain()
        );
    }

    /// <summary>Every member the real resource-group grain lists for the harness's group.</summary>
    /// <param name="harness">The harness.</param>
    static async Task<IReadOnlyList<ResourceGroupMember>> MembersAsync(ClusterConformanceHarness<TSource> harness) {
        var group = harness.For(ConformanceIds.Tenant)
            .GetGrain<IResourceGroupGrain>(GrainKeys.ResourceGroup(ConformanceIds.Subscription, ConformanceIds.ResourceGroup));

        return (await group.ListAsync()).GetValueOrThrow();
    }

    /// <summary>
    ///     The harness namespace's whole contents through the shipped listing, once every aggregated
    ///     API the cluster registers is answering.
    /// </summary>
    /// <param name="harness">The harness.</param>
    /// <param name="cancellationToken">The test's token.</param>
    /// <remarks>
    ///     ⚠ <b>A refusal here fails the test.</b> A
    ///     discovery refusal on this harness means an <c>APIService</c> wasn't answering yet — the
    ///     fixture not waiting, not the platform misbehaving — so the wait comes first, and whatever
    ///     is still unavailable after it is named in the failure. The refusal itself is asserted
    ///     where it can be provoked on purpose, <c>NamespaceDiscoveryRefusalTests</c>.
    /// </remarks>
    static async Task<IReadOnlyList<KubeObjectSummary>> ListNamespaceAsync(
        ClusterConformanceHarness<TSource> harness,
        CancellationToken cancellationToken
    ) {
        await WaitForEveryApiServiceAsync(harness, cancellationToken);

        var listed = await harness.Connection.ListNamespaceAsync(
            ClusterConformanceHarness<TSource>.Namespace,
            cancellationToken
        );

        listed.IsSuccess.ShouldBeTrue(
            "every APIService reports Available and the namespace listing still refused, so the "
            + $"enumeration failed for a reason of its own: {listed.Error?.Message}"
        );

        return listed.GetValueOrThrow();
    }

    /// <summary>Waits until every <c>APIService</c> reports <c>Available=True</c>.</summary>
    /// <param name="harness">The harness.</param>
    /// <param name="cancellationToken">The test's token.</param>
    /// <remarks>
    ///     ⚠ With metrics-server off this returns on its first read — every <c>APIService</c> k3s
    ///     registers is served in-process. It's here for the next aggregated API, so that a
    ///     component with a pod behind its discovery makes this test wait rather than race.
    /// </remarks>
    static async Task WaitForEveryApiServiceAsync(
        ClusterConformanceHarness<TSource> harness,
        CancellationToken cancellationToken
    ) {
        List<string> waiting = [];

        for (var attempt = 0; attempt < 120; attempt++) {
            using var services = await harness.Raw.ApiregistrationV1.ListAPIServiceWithHttpMessagesAsync(
                cancellationToken: cancellationToken
            );

            waiting = [
                .. services.Body.Items
                    .Select(static x => (
                        x.Metadata.Name,
                        Available: x.Status?.Conditions?.FirstOrDefault(static c => c.Type == "Available")
                    ))
                    .Where(static x => x.Available?.Status != "True")
                    .Select(static x => $"{x.Name} ({x.Available?.Reason}: {x.Available?.Message})")
            ];

            if (waiting.Count == 0) {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        waiting.ShouldBeEmpty(
            "these APIServices never became Available, and discovery of their groups answers 503 "
            + "until they do — so a namespace listing would refuse over them: "
            + string.Join(", ", waiting)
        );
    }

    /// <summary>
    ///     The occupants a reclaim would refuse over that carry <paramref name="resourceId" />'s
    ///     <c>cybercloud.io/resource-id</c>.
    /// </summary>
    /// <param name="occupants">A namespace listing.</param>
    /// <param name="resourceId">The resource under test.</param>
    static List<KubeObjectSummary> OursIn(IReadOnlyList<KubeObjectSummary> occupants, Guid resourceId) {
        var value = KubeLabels.GuidValue(resourceId);

        return [
            .. occupants.Where(x => x.Labels.TryGetValue(KubeLabels.ResourceId, out var label)
                && string.Equals(label, value, StringComparison.Ordinal)
                && !NamespaceReclaim.IsAmbient(Occupant(x))
            )
        ];
    }

    /// <summary>
    ///     A listing cut down to what the namespace would hold if <paramref name="resourceId" /> were
    ///     the group's only resource: Kubernetes' own objects and whatever carries its label.
    /// </summary>
    /// <param name="occupants">A namespace listing.</param>
    /// <param name="resourceId">The resource under test.</param>
    /// <remarks>
    ///     ⚠ The shared namespace also holds the harness's ancestors, siblings and companions, and a
    ///     verdict over all of them says which of those a family has — see the test's remarks.
    /// </remarks>
    static ImmutableArray<NamespaceOccupant> Evidence(IReadOnlyList<KubeObjectSummary> occupants, Guid resourceId) => [
        .. occupants.Select(static x => Occupant(x)).Where(static x => NamespaceReclaim.IsAmbient(x)),
        .. OursIn(occupants, resourceId).Select(static x => Occupant(x))
    ];

    /// <summary>
    ///     Lists the namespace until nothing of <paramref name="resourceId" /> is left in it, or the
    ///     budget runs out, and returns the reclaim's verdict over what was left.
    /// </summary>
    /// <param name="harness">The harness.</param>
    /// <param name="resourceId">The resource that was torn down.</param>
    /// <param name="cancellationToken">The test's token.</param>
    /// <remarks>
    ///     ⚠ <b>Ninety seconds, and it's paid only by a family that leaves something.</b> A pod's
    ///     default grace period is thirty; a claim waits for its pod. One that clears returns on
    ///     the first read that finds it gone.
    ///     <para>
    ///         ⚠ <b>A clock, not a count.</b> One pass is a discovery plus a list per served kind
    ///         and then <see cref="BetweenDrives" />, which is two to three seconds on k3s, so ninety
    ///         passes was four minutes. The sabotage run that proved this test fails a family
    ///         leaving a <c>ConfigMap</c> behind is what measured it.
    ///     </para>
    /// </remarks>
    static async Task<NamespaceReclaim> RemainsAsync(
        ClusterConformanceHarness<TSource> harness,
        Guid resourceId,
        CancellationToken cancellationToken
    ) {
        var ns = ClusterConformanceHarness<TSource>.Namespace;
        var budget = Stopwatch.StartNew();
        NamespaceReclaim verdict;

        while (true) {
            var listed = await ListNamespaceAsync(harness, cancellationToken);

            verdict = NamespaceReclaim.Decide(
                ClusterConformanceHarness<TSource>.ClusterId,
                ns,
                [],
                Evidence(listed, resourceId)
            );

            if (verdict.Deletable || budget.Elapsed > TimeSpan.FromSeconds(90)) {
                return verdict;
            }

            await Task.Delay(BetweenDrives, cancellationToken);
        }
    }

    /// <summary>Reads a raw object as the reclaim decision sees it.</summary>
    static NamespaceOccupant Occupant(string kind, IMetadata<V1ObjectMeta> found) =>
        new() {
            Kind = kind,
            Name = found.Metadata.Name,
            Labels = found.Metadata.Labels is { } labels
                ? labels.ToImmutableDictionary(StringComparer.Ordinal)
                : ImmutableDictionary<string, string>.Empty
        };

    /// <summary>Reads a listed object as the reclaim decision sees it.</summary>
    static NamespaceOccupant Occupant(KubeObjectSummary found) =>
        new() {
            Kind = found.Kind.Kind,
            Name = found.Name,
            Labels = found.Labels.ToImmutableDictionary(StringComparer.Ordinal)
        };

    // ── 4. Desired state survives a real serialization round trip ──────────────────────────────

    [Fact]
    public async Task DesiredStateSurvivesARealSerializationRoundTrip() {
        var harness = Fixture.Require(
            "that ResourceState and OperationGrainState round-trip — in-memory storage keeps the "
            + "object graph, so the 'System.Text.Json does not populate a get-only collection' trap "
            + "cannot fire there."
        );

        var token = TestContext.Current.CancellationToken;
        const string name = "real-roundtrip";

        harness.Registry.TryGetType(Case.Type, out var registration).ShouldBeTrue();

        var body = registration.SupportsTags
            ? WithTags(Body(), ("env", "prod"), ("owner", "platform"))
            : Body();

        var accepted = (await WriteAsync(harness, name, body)).GetValueOrThrow();
        var status = await ConvergeAsync(harness, accepted.OperationId);

        status.State.ShouldBe(OperationState.Succeeded, status.Error?.Message);
        status.Progress.ShouldNotBeEmpty("the operation recorded no progress, so there is nothing to lose.");

        // ── The bytes. Read with plain SQL, around Orleans, because asking Orleans again would be
        //    answered out of the activation's memory. ────────────────────────────────────────────
        var operationRows = await DurableRows.ReadAsync(
            harness.DurableConnectionString,
            accepted.OperationId.ToString("N", CultureInfo.InvariantCulture),
            token
        );

        operationRows.ShouldNotBeEmpty(
            "no row in PostgreSQL's OrleansStorage carries this operation. The durable tier is not "
            + "durable, or the grain never wrote."
        );

        var operationPayload = operationRows.Select(static x => x.Payload).First(static x => x.Length > 0);

        // ⚠ THE ROUND TRIP, THROUGH THE SHIPPED SERIALIZER. Deserializing the stored bytes with
        // SystemTextJsonGrainStorageSerializer — the same instance type DurableTierConfigurator
        // installs — is what makes this an assertion about SERIALIZATION rather than about storage.
        var serializer = new SystemTextJsonGrainStorageSerializer();
        var operationState = serializer.Deserialize<OperationGrainState>(BinaryData.FromString(operationPayload));

        operationState.Spec.ShouldNotBeNull(
            "OperationGrainState.Spec did not come back. docs/plan/08 § Long-running operations claims "
            + "the state 'includes everything needed to re-drive'; without the spec there is nothing "
            + "to re-drive."
        );

        operationState.Spec!.ResourceId.ShouldBe(accepted.Resource.Id);
        operationState.Spec.Desired.Length.ShouldBeGreaterThan(0);

        // ⚠ THE TRAP, NAMED. `Progress` is a collection on a durable state object; if it were
        // get-only System.Text.Json would hand back an empty one and every operation would lose its
        // history the first time it round-tripped. CyberCloud.ResourceManager.Tests' .csproj records
        // this exact debt against these exact two types.
        operationState.Progress.ShouldNotBeEmpty(
            $"the operation reported {status.Progress.Length} progress entries and PostgreSQL gave "
            + "back none. That is the get-only-collection trap firing."
        );

        operationState.Progress.Count.ShouldBe(status.Progress.Length);
        operationState.Attempts.ShouldBe(status.Attempts);
        operationState.Status.ShouldBe(OperationState.Succeeded);

        // ── And the resource's own state. ──────────────────────────────────────────────────────
        var resourceRows = await DurableRows.ReadAsync(
            harness.DurableConnectionString,
            accepted.Resource.Id.ToString("N", CultureInfo.InvariantCulture),
            token
        );

        resourceRows.ShouldNotBeEmpty("no row in PostgreSQL carries the resource grain's state.");

        var resourceState = serializer.Deserialize<ResourceState>(
            BinaryData.FromString(resourceRows.Select(static x => x.Payload).First(static x => x.Length > 0))
        );

        resourceState.Path.ShouldBe(ClusterConformanceHarness<TSource>.Address(name).Path);
        resourceState.ApiVersion.ShouldBe(Case.ApiVersion);
        resourceState.ProvisioningState.ShouldBe(ProvisioningState.Succeeded);
        resourceState.Superset.Length.ShouldBeGreaterThan(2, "the desired body did not survive.");

        if (registration.SupportsTags) {
            // The other collection on the other durable type, for the same reason.
            resourceState.Tags.ShouldContainKeyAndValue("env", "prod");
            resourceState.Tags.ShouldContainKeyAndValue("owner", "platform");
        }

        // ── And the grain agrees with the bytes after it has been made to read them again. ─────
        await harness.Operation(ConformanceIds.Tenant, accepted.OperationId).DeactivateAsync();

        var reread = await harness.Operation(ConformanceIds.Tenant, accepted.OperationId).GetAsync();
        var afterReload = reread.GetValueOrThrow();

        afterReload.Progress.Length.ShouldBe(
            status.Progress.Length,
            "the grain re-activated over PostgreSQL and came back with a different history."
        );

        afterReload.Attempts.ShouldBe(status.Attempts);
        afterReload.State.ShouldBe(OperationState.Succeeded);

        // Not this test's subject; see TearDownAsync.
        await TearDownAsync(harness, name);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>A valid body for the harness's cluster.</summary>
    protected static string Body() => Case.Body(ClusterConformanceHarness<TSource>.ClusterId);

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
        ClusterConformanceHarness<TSource>.Address(name).WithId(resourceId);

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
    /// <param name="objectJson">The object as the API server holds it.</param>
    /// <param name="desiredJson">The body it should carry.</param>
    /// <remarks>
    ///     ⚠ The one place a <see cref="MatchContext" /> is built in this suite — see that record's
    ///     remarks for why it is a record and not a longer parameter list.
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

    /// <summary>Writes a resource and asserts the request was accepted.</summary>
    /// <param name="harness">The harness.</param>
    /// <param name="name">The resource name.</param>
    /// <param name="body">The body, defaulting to the case's.</param>
    protected static async Task<Result<WriteAccepted>> WriteAsync(
        ClusterConformanceHarness<TSource> harness,
        string name,
        string? body = null
    ) {
        var accepted = await harness.Manager.WriteAsync(
            new() {
                Path = ClusterConformanceHarness<TSource>.Address(name).Path,
                ApiVersion = Case.ApiVersion,
                Verb = WriteVerb.Put,
                Body = body ?? Body(),
                Caller = ClusterConformanceHarness<TSource>.Caller()
            },
            TestContext.Current.CancellationToken
        );

        accepted.IsSuccess.ShouldBeTrue(accepted.Error?.Message);
        return accepted;
    }

    /// <summary>
    ///     Deletes a resource a test created and drives its teardown to the end, so the next test
    ///     starts against the account it was written for and not against this one's leftovers.
    /// </summary>
    /// <param name="harness">The harness.</param>
    /// <param name="name">The resource name the test wrote under.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             EVERY TEST HERE WRITES INTO ONE ACCOUNT, AND A TYPE WHOSE OBJECTS ARE SHARED
    ///             BETWEEN SIBLINGS CANNOT PASS "DELETE → GONE" WITH A SIBLING LEFT BEHIND.
    ///         </b> The harness
    ///         creates the ancestors once, so <c>real-drift</c>, <c>real-conflict</c>,
    ///         <c>real-roundtrip</c> and the lifecycle's own resource are all children of the same
    ///         parent. For eleven families that was invisible: each resource's objects are its own,
    ///         and a sibling's leftovers are not in its <c>Objects</c>. <c>CyberCloud.Storage/accounts/fileShares</c>
    ///         is the first type to list an object
    ///         <i>
    ///             every share of the account applies and the last
    ///             one out removes
    ///         </i> — the account's <c>SeaweedCSIDriver</c> — and its lifecycle failed
    ///         here on the first run that ever reached a real API server: the drift test had left
    ///         <c>real-drift</c> standing, the lifecycle's teardown correctly saw a sibling and kept
    ///         the driver, and the suite reported the driver as
    ///         <i>
    ///             "still in the real cluster after a
    ///             converged teardown"
    ///         </i>. The reconciler was right and the suite was leaking.
    ///     </para>
    ///     <para>
    ///         So a test that creates a resource and is not itself about its deletion tears it down
    ///         at the end. ⚠ Through the manager and a converged operation, never a raw
    ///         <c>kubectl delete</c>: the provider's own teardown is what knows which objects are
    ///         shared, and it is the same path <c>TheLifecycleRunsAgainstARealApiServer</c> already
    ///         proves for every family, so this adds seconds and no new failure mode.
    ///     </para>
    /// </remarks>
    protected static async Task TearDownAsync(ClusterConformanceHarness<TSource> harness, string name) {
        var deleted = await harness.Manager.DeleteAsync(
            new() {
                Path = ClusterConformanceHarness<TSource>.Address(name).Path,
                ApiVersion = Case.ApiVersion,
                Caller = ClusterConformanceHarness<TSource>.Caller()
            },
            TestContext.Current.CancellationToken
        );

        deleted.IsSuccess.ShouldBeTrue(
            $"'{name}' could not be deleted at the end of the test: {deleted.Error?.Message}"
        );

        var teardown = await ConvergeAsync(harness, deleted.GetValueOrThrow().OperationId);
        teardown.State.ShouldBe(
            OperationState.Succeeded,
            $"the teardown of '{name}' at the end of the test ended {teardown.State}: {teardown.Error?.Message}"
        );
    }

    /// <summary>
    ///     How long to wait between two drives of an operation that is not terminal yet.
    /// </summary>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         A reminder does not fire twice in the same microsecond, and until 2026-08-12 this loop
    ///         did.
    ///     </b> Twelve back-to-back <c>DriveAsync</c> calls take a few milliseconds in total, so
    ///     the harness could only host a reconciler whose remaining work was <i>ours</i>. Anything
    ///     waiting on the API server to finish something — <c>CascadePolicy.Foreground</c>, which holds
    ///     the object under a <c>foregroundDeletion</c> finalizer until the garbage collector has
    ///     removed its dependents, is the case that found this — reported <c>InProgress</c> twelve
    ///     times and the test failed on a status that was correct.
    ///     <para>
    ///         The reconcile outcome carries its own <c>retryAfter</c> and this loop cannot see it: an
    ///         <c>OperationStatus</c> does not carry the backoff the last pass asked for. So this is a
    ///         floor rather than an honouring — long enough that a controller loop gets a turn, short
    ///         enough that <see cref="MaxDrives" /> of them is forty seconds. A provider that needs
    ///         more than that is provisioning for real and belongs on a reminder, not in a test.
    ///     </para>
    ///     <para>
    ///         ⚠ It is paid only by an operation that is <i>not</i> terminal: a provider that converges
    ///         in one pass waits nothing, which is every provider in the tree except on teardown.
    ///     </para>
    /// </remarks>
    static readonly TimeSpan BetweenDrives = TimeSpan.FromSeconds(1);

    /// <summary>Drives an operation the way its reminder would, until it is terminal.</summary>
    /// <param name="harness">The harness.</param>
    /// <param name="operationId">The operation.</param>
    protected static async Task<OperationStatus> ConvergeAsync(
        ClusterConformanceHarness<TSource> harness,
        Guid operationId
    ) {
        var operation = harness.Operation(ConformanceIds.Tenant, operationId);
        OperationStatus? last = null;

        for (var i = 0; i < MaxDrives; i++) {
            last = (await operation.DriveAsync()).GetValueOrThrow();

            if (last.IsTerminal) {
                return last;
            }

            // ⚠ AFTER the terminal check, so a converging operation pays nothing. See BetweenDrives.
            await Task.Delay(BetweenDrives, TestContext.Current.CancellationToken);
        }

        last.ShouldNotBeNull();
        return last;
    }

    /// <summary>Runs one reconcile pass directly, the way the per-cluster drift scan pokes a resource.</summary>
    /// <param name="harness">The harness.</param>
    /// <param name="resourceId">The resource's GUID.</param>
    /// <param name="name">Its name.</param>
    protected static async Task<ReconcileOutcome> ReconcileOnceAsync(
        ClusterConformanceHarness<TSource> harness,
        Guid resourceId,
        string name
    ) {
        var address = ClusterConformanceHarness<TSource>.Address(name).WithId(resourceId);
        using var desired = JsonDocument.Parse(Body());
        var (view, watch) = harness.Views.For(address);

        return await Case.CreateReconciler(harness.Clock)
            .ReconcileAsync(
                new(
                    address,
                    Case.ApiVersion,
                    desired.RootElement,
                    null,
                    ReconcileDriver.NamespaceFor(address),
                    harness.Connection,
                    // ⚠ The harness's vault on both members. This context is built BY HAND rather
                    // than by ReconcileDriver, so nothing fills SecretWriter in for it — and a
                    // provider whose pass mints a credential would fail every drift and conflict
                    // assertion for a wiring reason rather than for the reason under test. The same
                    // instance the silo holds, so the credential a create minted is the one a repair
                    // pass finds.
                    ClusterConformanceState<TSource>.Vault,
                    new RecordingLog()
                    // ⚠ And the cross-resource seam, bound to this address the way the driver binds it —
                    // a type that reads another resource would otherwise fail every hand-driven pass
                    // against RefusingResourceView, for the harness's reason and not its own.
                ) {
                    SecretWriter = ClusterConformanceState<TSource>.Vault,
                    Grants = ClusterConformanceState<TSource>.Grants,
                    View = view,
                    Watch = watch
                },
                TestContext.Current.CancellationToken
            );
    }

    /// <summary>The field manager the commands for the type UNDER TEST carried.</summary>
    /// <param name="harness">The harness.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             This used to peek the first applied command, and that was a proxy that held only
    ///             while every apply in a run came from one provider.
    ///         </b> It stopped holding the day
    ///         <c>NamespaceEnsurer</c> landed: the driver now applies the namespace before the pass,
    ///         under <c>cybercloud/resource-manager</c>, so the first command is the platform's and
    ///         every provider object was then checked against a manager that never wrote it. The
    ///         failure read <i>"the API server recorded no field-manager entry"</i> — which sounds
    ///         like the apply was not an apply, and was really this helper naming the wrong manager.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             And skipping the group-scoped commands stopped being enough the day a case brought
    ///             a companion.
    ///         </b> <see cref="ClusterConformanceHarness{TSource}" /> creates
    ///         <c>IProviderCaseSource.Companions</c> — another provider's resource, applied through the
    ///         same <see cref="RealClusterConnection" /> — before the first assertion, so the first
    ///         non-group-scoped command in <see cref="RealClusterConnection.Applied" /> was the
    ///         PostgreSQL server's Cluster under <c>cybercloud/cybercloud.dbforpostgresql</c>, and the
    ///         vault's ScheduledBackup was checked against it. Only the conflict test resets the
    ///         recording, so whether that failed depended on test ORDER — the run that shipped the
    ///         companion was green by luck. The helper now selects by the case's own
    ///         <c>cybercloud.io/resource-type</c> label, which no companion and no ancestor carries.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Selecting by the label is exact rather than approximate.</b> Every command a
    ///         provider renders carries <c>KubeCommandBuilder.FieldManagerFor(providerNamespace)</c>,
    ///         one manager per provider, and <see cref="KubeLabels.ResourceType" /> is injected by the
    ///         builder from the resource's address rather than set by the provider, so the first
    ///         command labelled with <see cref="ClusterConformanceHarness{TSource}.Case" />'s type is
    ///         the type under test's, whatever else the run applied before it.
    ///     </para>
    /// </remarks>
    protected static string FieldManagerOf(ClusterConformanceHarness<TSource> harness) {
        ArgumentNullException.ThrowIfNull(harness);

        var type = KubeLabels.ResourceTypeValue(ClusterConformanceHarness<TSource>.Case.Type);

        return harness.Connection.Applied
            .FirstOrDefault(x => x.Labels.TryGetValue(KubeLabels.ResourceType, out var value)
                && string.Equals(value, type, StringComparison.Ordinal)
            )
            ?.FieldManager
            ?? string.Empty;
    }

    /// <summary><c>apiVersion</c> as the wire spells it — the core group has no group segment.</summary>
    /// <param name="kind">The kind.</param>
    protected static string ApiVersionOf(GroupVersionKind kind) {
        ArgumentNullException.ThrowIfNull(kind);
        return string.IsNullOrEmpty(kind.Group) ? kind.Version : kind.Group + "/" + kind.Version;
    }

    /// <summary>Reads an object straight from the API server, or <see langword="null" /> when absent.</summary>
    /// <param name="harness">The harness.</param>
    /// <param name="target">Which object.</param>
    /// <param name="cancellationToken">The test's token.</param>
    protected static async Task<string?> ReadFromClusterAsync(
        ClusterConformanceHarness<TSource> harness,
        ObjectRef target,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(target);

        try {
            // ⚠ THE SCOPE BRANCH, WHICH `KubeApiClient` HAS HAD SINCE BEFORE ANY PROVIDER NEEDED IT
            // AND THIS HARNESS DID NOT. A cluster-scoped object is served at
            // /apis/{group}/{version}/{plural}/{name} with no namespace segment, and calling the
            // namespaced overload for one returns `404 page not found` — the bare Traefik-style body,
            // with no Kubernetes Status in it, so the failure names neither the object nor the reason.
            // Every provider family before CyberCloud.Network rendered namespaced objects only, so
            // this branch had no case to be wrong on. See ClusterConformanceHarness
            // § EnsureCustomResourceDefinitionsAsync, which derives the CRD stub's own scope from the
            // same `ObjectRef.IsClusterScoped`.
            using var response = target.IsClusterScoped
                ? await harness.Raw.CustomObjects.GetClusterCustomObjectWithHttpMessagesAsync(
                    target.Kind.Group,
                    target.Kind.Version,
                    target.Kind.Plural,
                    target.Name,
                    cancellationToken: cancellationToken
                )
                : await harness.Raw.CustomObjects.GetNamespacedCustomObjectWithHttpMessagesAsync(
                    target.Kind.Group,
                    target.Kind.Version,
                    target.Namespace,
                    target.Kind.Plural,
                    target.Name,
                    cancellationToken: cancellationToken
                );

            return ((JsonElement)response.Body!).GetRawText();
        } catch (k8s.Autorest.HttpOperationException ex)
            when (ex.Response?.StatusCode == System.Net.HttpStatusCode.NotFound) {
                return null;
            }
    }

    /// <summary>Waits until an object the suite deleted has actually gone.</summary>
    /// <param name="harness">The harness.</param>
    /// <param name="target">Which object.</param>
    /// <param name="cancellationToken">The test's token.</param>
    /// <remarks>
    ///     ⚠ <b>A delete is a request and a finalizer is a veto with a timer on it.</b> Kubernetes
    ///     answers a delete by writing a <c>deletionTimestamp</c>; the object is removed when the last
    ///     finalizer is cleared, by whichever controller owns it. <c>kubernetes.io/pvc-protection</c>
    ///     is added by the API server to every <c>PersistentVolumeClaim</c>, so the first provider in
    ///     the tree to render one — <c>CyberCloud.Terminal/consoles</c> — is the first for which
    ///     "deleted" and "gone" are separated by a controller round trip.
    ///     <para>
    ///         ⚠ The budget is deliberately short. This is not waiting for a workload to drain; it is
    ///         waiting for a controller that is already watching to remove one string. A delete that
    ///         has genuinely not landed fails here in two seconds with the object named, which is what
    ///         the assertion it replaced did.
    ///     </para>
    /// </remarks>
    protected static async Task WaitUntilAbsentAsync(
        ClusterConformanceHarness<TSource> harness,
        ObjectRef target,
        CancellationToken cancellationToken
    ) {
        string? json = null;

        for (var attempt = 0; attempt < 20; attempt++) {
            json = await ReadFromClusterAsync(harness, target, cancellationToken);

            if (json is null) {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }

        // ⚠ The body is in the message on purpose: a `deletionTimestamp` in it says "a finalizer is
        // holding this" and its absence says "the delete never reached the API server", and those are
        // different bugs with the same symptom.
        json.ShouldBeNull($"the delete of '{target}' did not land within two seconds.");
    }

    /// <summary>One label's value, read straight from the API server.</summary>
    /// <param name="harness">The harness.</param>
    /// <param name="target">Which object.</param>
    /// <param name="key">The label key. ⚠ Read as a map key, never by splitting a dotted path.</param>
    /// <param name="cancellationToken">The test's token.</param>
    protected static async Task<string?> LabelAsync(
        ClusterConformanceHarness<TSource> harness,
        ObjectRef target,
        string key,
        CancellationToken cancellationToken
    ) {
        var json = await ReadFromClusterAsync(harness, target, cancellationToken);

        return json is null
            ? null
            : JsonNode.Parse(json)?["metadata"]?["labels"]?[key]?.GetValue<string>();
    }

    /// <summary>Applies a document as a <b>different</b> field manager, taking ownership.</summary>
    /// <param name="harness">The harness.</param>
    /// <param name="target">Which object.</param>
    /// <param name="manager">The rival manager's name.</param>
    /// <param name="json">A minimal apply document naming only the fields the rival claims.</param>
    /// <param name="cancellationToken">The test's token.</param>
    protected static async Task RivalApplyAsync(
        ClusterConformanceHarness<TSource> harness,
        ObjectRef target,
        string manager,
        string json,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(target);

        // ⚠ The scope branch — see ReadFromClusterAsync for why it exists and what its absence looked
        // like.
        using var response = target.IsClusterScoped
            ? await harness.Raw.CustomObjects.PatchClusterCustomObjectWithHttpMessagesAsync(
                new V1Patch(JsonSerializer.Deserialize<JsonElement>(json), V1Patch.PatchType.ApplyPatch),
                target.Kind.Group,
                target.Kind.Version,
                target.Kind.Plural,
                target.Name,
                fieldManager: manager,
                force: true,
                cancellationToken: cancellationToken
            )
            : await harness.Raw.CustomObjects.PatchNamespacedCustomObjectWithHttpMessagesAsync(
                new V1Patch(JsonSerializer.Deserialize<JsonElement>(json), V1Patch.PatchType.ApplyPatch),
                target.Kind.Group,
                target.Kind.Version,
                target.Namespace,
                target.Kind.Plural,
                target.Name,
                fieldManager: manager,
                force: true,
                cancellationToken: cancellationToken
            );
    }

    /// <summary>Deletes an object behind the platform's back — the drift case's <c>kubectl delete</c>.</summary>
    /// <param name="harness">The harness.</param>
    /// <param name="target">Which object.</param>
    /// <param name="cancellationToken">The test's token.</param>
    protected static async Task DeleteFromClusterAsync(
        ClusterConformanceHarness<TSource> harness,
        ObjectRef target,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(target);

        // ⚠ The scope branch — see ReadFromClusterAsync.
        using var response = target.IsClusterScoped
            ? await harness.Raw.CustomObjects.DeleteClusterCustomObjectWithHttpMessagesAsync(
                target.Kind.Group,
                target.Kind.Version,
                target.Kind.Plural,
                target.Name,
                cancellationToken: cancellationToken
            )
            : await harness.Raw.CustomObjects.DeleteNamespacedCustomObjectWithHttpMessagesAsync(
                target.Kind.Group,
                target.Kind.Version,
                target.Namespace,
                target.Kind.Plural,
                target.Name,
                cancellationToken: cancellationToken
            );
    }

    static string WithTags(string body, params (string Key, string Value)[] tags) {
        var node = JsonNode.Parse(body)!.AsObject();
        var map = new JsonObject();

        foreach (var (key, value) in tags) {
            map[key] = value;
        }

        node["tags"] = map;
        return node.ToJsonString();
    }
}
