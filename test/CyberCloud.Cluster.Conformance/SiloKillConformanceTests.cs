using CyberCloud.Cluster.Conformance.Infrastructure;
using CyberCloud.ResourceManager.Reconcile;
using System.Collections.Immutable;
using System.Globalization;

namespace CyberCloud.Cluster.Conformance;

/// <summary>
///     docs/plan/24 § Phase 1's exit criterion 3 — <i>kill the silo mid-create, and the resource still
///     converges</i> — against real durable storage.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Its own class because it destroys its cluster, and its own clusters because that is
///         the assertion.</b> Every other test in this assembly shares one Orleans cluster; this one
///         kills silos, so sharing would take the others down with it.
///     </para>
///     <para>
///         ⚠ <b>Why every silo is killed rather than one of two.</b> Orleans places an activation at
///         random, so "kill the silo the operation grain is on" is not a thing a test can ask for
///         without reaching into the placement director — and a test that killed the OTHER silo would
///         pass while proving nothing, silently, on most runs. Killing all of them leaves exactly one
///         place the state can have come from: the PostgreSQL row. The second cluster is built with
///         the SAME Orleans service id for that reason — <c>OrleansStorage</c> is keyed by it, so a
///         cluster that asked under a different id would find nothing and the test would fail loudly
///         rather than pass emptily.
///     </para>
///     <para>
///         ⚠ <b><c>KillSiloAsync</c> and not <c>StopSiloAsync</c>.</b> A graceful stop deactivates
///         grains, and deactivation <i>writes state</i> — so a resource that converged afterwards
///         would prove only that a clean shutdown flushes. The ungraceful path is Orleans' simulation
///         of a pod that went away, and anything not already in PostgreSQL is gone with it.
///     </para>
/// </remarks>
/// <typeparam name="TSource">The provider under test.</typeparam>
public abstract class SiloKillConformanceTests<TSource>
    where TSource : IProviderCaseSource {
    /// <summary>The Orleans service id both clusters ask under.</summary>
    protected const string ServiceId = "cluster-conformance-kill";

    /// <summary>The provider under test.</summary>
    protected static ProviderConformanceCase Case => TSource.ProviderCase;

    [Fact]
    public async Task KillingTheSiloMidCreateStillConverges() {
        var token = TestContext.Current.CancellationToken;

        var endpoints = await ClusterInfrastructure.TryStartAsync(token);

        if (endpoints is null) {
            Assert.Skip(
                ClusterInfrastructure.SkipMessage(
                    Case.DisplayName,
                    "docs/plan/08 § Long-running operations' claim that OperationGrain state "
                    + "'includes everything needed to re-drive' and that 'on activation after a silo "
                    + "loss the grain re-registers its reminder and continues' — which is "
                    + "docs/plan/24 § Phase 1's exit criterion 3, and which must not be claimed until "
                    + "this test has run."
                )
            );

            return;
        }

        const string name = "silo-kill";
        var serviceId = ServiceId + "-" + typeof(TSource).Name;

        Guid operationId;
        Guid resourceId;
        int activationsBeforeTheKill;
        ImmutableArray<ObjectRef> objects;

        // ── The cluster that dies ──────────────────────────────────────────────────────────────
        var doomed = await ClusterConformanceHarness<TSource>.StartAsync(
            2,
            serviceId,
            22500,
            endpoints,
            token
        );

        try {
            var accepted = (await doomed.Manager.WriteAsync(
                new() {
                    Path = ClusterConformanceHarness<TSource>.Address(name).Path,
                    ApiVersion = Case.ApiVersion,
                    Verb = WriteVerb.Put,
                    Body = Case.Body(ClusterConformanceHarness<TSource>.ClusterId),
                    Caller = ClusterConformanceHarness<TSource>.Caller()
                },
                token
            )).GetValueOrThrow();

            operationId = accepted.OperationId;
            resourceId = accepted.Resource.Id;

            var address = ClusterConformanceHarness<TSource>.Address(name).WithId(resourceId);
            objects = Case.Objects(address, ReconcileDriver.NamespaceFor(address));

            // ⚠ MID-CREATE, AND NO PASS IS DRIVEN BEFORE THE KILL. THAT IS DELIBERATE AND IT IS THE
            // DIFFERENCE BETWEEN THIS TEST AND A GREEN ONE THAT PROVES NOTHING.
            //
            // The first draft drove one pass first, to make "mid-create" feel literal. Against a real
            // API server the reference provider converges in that ONE pass — so the operation reached
            // Succeeded, TerminateAsync unregistered its reminder, and everything below was
            // measuring a cluster killed AFTER the work was finished. It passed the convergence
            // assertions for the wrong reason and failed only because the reminder row was gone,
            // which is the only reason the defect was found at all.
            //
            // The state the platform must survive is therefore the one every provider passes through
            // regardless of how fast it converges: the write is accepted, step 12 has answered 202,
            // the quota is leased, the name is claimed, the operation is durable and NOT terminal,
            // and nothing has been applied. docs/plan/06 § Two-phase create is entirely about this
            // window.
            var midFlight = (await doomed.Operation(ConformanceIds.Tenant, operationId).GetAsync())
                .GetValueOrThrow();

            midFlight.IsTerminal.ShouldBeFalse(
                $"the operation is already {midFlight.State} before anything was killed, so the kill "
                + "below would happen after the work rather than during it."
            );

            activationsBeforeTheKill = midFlight.Activations;

            accepted.Resource.ProvisioningState.ShouldBe(
                ProvisioningState.Creating,
                "the resource is not mid-create, so this is not the window the test is about."
            );

            // ⚠ The evidence that the state is out of the process ALREADY, before anything is killed.
            // Without this line, a later convergence could be explained by a second cluster simply
            // redoing the work rather than resuming it.
            var rows = await DurableRows.ReadAsync(
                doomed.DurableConnectionString,
                operationId.ToString("N", CultureInfo.InvariantCulture),
                token
            );

            rows.ShouldNotBeEmpty(
                "the operation is running and PostgreSQL holds no row for it. Nothing below could "
                + "prove recovery, because there would be nothing to recover from."
            );

            // ⚠ The reminder is registered, and it is registered somewhere that outlives a silo.
            // Both halves are asserted: the row exists, and the table holding it is the Redis one
            // rather than the in-memory grain every other harness in this repository uses.
            doomed.ReminderTableTypeName.ShouldBe(
                "RedisReminderTable",
                "the silos are using a different reminder table than the one this harness wired. An "
                + "in-memory reminder table is a GRAIN, so it dies with the silo that hosts it and "
                + "'re-registers its reminder and continues' would be a claim about a table that had "
                + "itself just been recreated."
            );

            (await doomed.ReminderRowsAsync(doomed.Operation(ConformanceIds.Tenant, operationId)))
                .ShouldBeGreaterThan(
                    0,
                    "the operation grain registered no reminder, so the safety net docs/plan/08 "
                    + "§ Long-running operations relies on to re-drive after a silo loss is not "
                    + "there. The whole reminder table holds: " + await doomed.AllRemindersAsync()
                );

            await doomed.KillEverySiloAsync();
        } finally {
            await doomed.DisposeAsync();
        }

        // ── The cluster that inherits ──────────────────────────────────────────────────────────
        // Different silos, different ports, same service id, same PostgreSQL, same Redis, same k3s.
        var successor = await ClusterConformanceHarness<TSource>.StartAsync(
            1,
            serviceId,
            22600,
            endpoints,
            token
        );

        try {
            var operation = successor.Operation(ConformanceIds.Tenant, operationId);

            // ⚠ THE REMINDER SURVIVED THE SILOS THAT REGISTERED IT. Read from a cluster whose silos
            // are all new processes' worth of state — so a row here came out of Redis, which is the
            // "safety net that survives a silo loss" docs/plan/08 § Long-running operations names.
            (await successor.ReminderRowsAsync(operation)).ShouldBeGreaterThan(
                0,
                "every silo that knew about this operation was killed and the reminder went with "
                + "them. Nothing would ever re-drive the operation on its own; it would sit in "
                + "Running until somebody noticed."
            );

            OperationStatus? last = null;
            for (var i = 0; i < 12; i++) {
                last = (await operation.DriveAsync()).GetValueOrThrow();

                if (last.IsTerminal) {
                    break;
                }
            }

            last.ShouldNotBeNull();

            // ⚠ THE OPERATION WAS RECOVERED, NOT RESTARTED. `Activations` is incremented only by
            // OnActivateAsync, and only when the state it just read back carries a live spec — so a
            // count above one on a silo that never saw the create is the durable state having been
            // read out of PostgreSQL and found sufficient. docs/plan/08 § Long-running operations:
            // "state includes everything needed to re-drive".
            last!.Activations.ShouldBeGreaterThan(
                activationsBeforeTheKill,
                $"the operation reported {activationsBeforeTheKill.ToString(CultureInfo.InvariantCulture)} "
                + "activations before every silo was killed and no more afterwards. Activations is "
                + "incremented by OnActivateAsync and ONLY when the state it has just read back "
                + "carries a live spec — so a count that did not move means the grain activated on "
                + "the new silo and found nothing, and whatever converged below was a fresh start "
                + "rather than a recovery."
            );

            last.Attempts.ShouldBeGreaterThan(
                0,
                "the operation says it never attempted a pass, so nothing re-drove it."
            );

            last.State.ShouldBe(
                OperationState.Succeeded,
                $"the operation ended {last.State} after the silos were killed: {last.Error?.Message}. "
                + "docs/plan/24 § Phase 1's exit criterion 3 is NOT met."
            );

            // ⚠ AND THE RESOURCE ACTUALLY CONVERGED — read out of the real API server, around every
            // line of our own code. "Still converges" is a claim about the cluster, not about a
            // status field.
            foreach (var target in objects) {
                var json = await ReadAsync(successor, target, token);

                json.ShouldNotBeNull(
                    $"the operation says Succeeded and '{target}' is not in the real cluster."
                );

                Case.ObjectMatchesDesired(json!, Case.Body(ClusterConformanceHarness<TSource>.ClusterId))
                    .ShouldBeTrue($"'{target}' is in the cluster without the desired shape.");
            }

            // And the resource grain, read on a silo that never wrote it, agrees.
            var snapshot = await successor.Manager.ReadAsync(
                new() {
                    Path = ClusterConformanceHarness<TSource>.Address(name).Path,
                    ApiVersion = Case.ApiVersion,
                    Caller = ClusterConformanceHarness<TSource>.Caller()
                },
                token
            );

            snapshot.IsSuccess.ShouldBeTrue(snapshot.Error?.Message);
            snapshot.GetValueOrThrow().ProvisioningState.ShouldBe(ProvisioningState.Succeeded);
            snapshot.GetValueOrThrow().Id.ShouldBe(
                resourceId,
                "the successor cluster created a NEW resource rather than reading the old one back."
            );
        } finally {
            await successor.DisposeAsync();
        }
    }

    static async Task<string?> ReadAsync(
        ClusterConformanceHarness<TSource> harness,
        ObjectRef target,
        CancellationToken cancellationToken
    ) {
        try {
            using var response = await harness.Raw.CustomObjects
                .GetNamespacedCustomObjectWithHttpMessagesAsync(
                    target.Kind.Group,
                    target.Kind.Version,
                    target.Namespace,
                    target.Kind.Plural,
                    target.Name,
                    cancellationToken: cancellationToken
                );

            return ((System.Text.Json.JsonElement)response.Body!).GetRawText();
        } catch (k8s.Autorest.HttpOperationException ex)
            when (ex.Response?.StatusCode == System.Net.HttpStatusCode.NotFound) {
            return null;
        }
    }
}
