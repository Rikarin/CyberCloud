using CyberCloud.Core.Time;
using CyberCloud.ResourceManager.Contracts.Registry;
using Orleans.Multitenant;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;

namespace CyberCloud.ResourceManager.Reconcile;

/// <summary>What one reconcile pass produced.</summary>
/// <param name="Outcome">The reconciler's verdict, or the driver's when the reconciler misbehaved.</param>
/// <param name="Progress">Everything the reconciler put through <see cref="IReconcileLog" />.</param>
/// <param name="Applied">
///     Whether the pass may have touched the data plane. ⚠ Pessimistic: true whenever the reconciler
///     was entered at all, because a pass that was interrupted after an apply and before its own
///     bookkeeping is indistinguishable from one that applied nothing — and getting that wrong in the
///     optimistic direction is a cancelled create that leaves resources running.
/// </param>
public readonly record struct ReconcilePass(
    ReconcileOutcome Outcome,
    ImmutableArray<OperationProgress> Progress,
    bool Applied
);

/// <summary>
///     Runs one reconcile pass: resolves the reconciler, builds its context, bounds it, and records
///     what it observed.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Clause 3 is enforced here and not trusted.</b> docs/plan/08 § The reconcile loop:
///         <i>"Bounded. Returns within 30 seconds or returns <c>InProgress</c>. A reconciler that
///         blocks on a four-minute cluster creation blocks that grain's turn, and Orleans grains are
///         single-threaded."</i> The driver passes a token that cancels at
///         <see cref="PassBudget" /> and turns an overrun into a retryable failure naming the
///         reconciler. A reconciler that ignores its token can still block the turn — nothing inside
///         a single-threaded activation can pre-empt it — but it does so <i>visibly</i>, with an
///         error naming the type, rather than as a mystery latency spike.
///     </para>
///     <para>
///         ⚠ <b>Clause 4 is why the driver observes after a converged pass.</b> A reconciler reporting
///         <see cref="ReconcileOutcomeKind.Converged" /> is claiming it read the desired shape back;
///         the driver takes it at its word for the outcome and then calls
///         <c>ObserveAsync</c> itself so the resource's observed state is what the drift scan compares
///         against. The <i>conformance suite</i> is what turns the claim into a check.
///     </para>
///     <para>
///         ⚠ <b>The driver creates the namespace, and nothing deletes it.</b>
///         <see cref="NamespaceFor" /> derives the name, <see cref="NamespaceEnsurer" /> applies it
///         with ADR-013's seven labels before the pass, and the second half of that sentence is a
///         decision rather than an omission. Deleting a namespace is a recursive delete of everything
///         inside it, and the platform cannot tell "empty" from "empty of objects we wrote": a
///         tenant's own <c>PersistentVolumeClaim</c>, a <c>Secret</c> an operator added and a
///         <c>StatefulSet</c> from a chart nobody registered all live in the same namespace and all
///         carry no <c>cybercloud.io/managed-by</c>. There is also nothing to hang the delete on —
///         <c>IResourceGroupGrain</c> has <c>BeginDeleteAsync</c>/<c>CompleteDeleteAsync</c> for its
///         <i>members</i> and <b>no method that deletes the group itself</b>. So an emptied group
///         leaves an empty namespace behind, which costs an etcd object and no compute, and the
///         alternative — a sweeper that deletes namespaces it believes are empty — is how a tenant's
///         running database disappears. <c>src/Providers/README.md</c> § Namespaces records it as
///         owed with what closing it needs.
///     </para>
/// </remarks>
public sealed class ReconcileDriver(
    IProviderRegistry registry,
    IServiceProvider services,
    IGrainFactory grains,
    IClusterConnectionFactory clusters,
    IClusterConnectionRegistrar clusterRegistrar,
    ISecretResolver secrets,
    ISecretWriter secretWriter,
    NamespaceEnsurer namespaces,
    IClock clock
) {
    /// <summary>How long one pass may take. Clause 3 of docs/plan/08 § The reconcile loop.</summary>
    public static TimeSpan PassBudget { get; } = TimeSpan.FromSeconds(30);

    /// <summary>Runs one pass.</summary>
    /// <param name="spec">The operation, which names the resource and the tenant.</param>
    /// <param name="tearingDown">
    ///     Whether to call <c>DeleteAsync</c> rather than <c>ReconcileAsync</c>. True for a delete and
    ///     for the teardown half of a cancellation.
    /// </param>
    /// <param name="cancellationToken">Cancels the pass from outside.</param>
    public async Task<ReconcilePass> RunAsync(
        OperationSpec spec,
        bool tearingDown,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(spec);

        var resource = Resource(spec);
        var input = await resource.GetReconcileInputAsync();

        if (input.TryGetError(out var inputError)) {
            // A resource that is gone during a teardown is a teardown that succeeded — the grain
            // state is the thing being removed, so its absence is the goal rather than a problem.
            return tearingDown
                ? new(ReconcileOutcome.Converged, [], false)
                : new(ReconcileOutcome.Failed(inputError), [], false);
        }

        var reconcileInput = input.GetValueOrThrow();

        var address = ResourceId.ParsePath(reconcileInput.Path);
        if (address.TryGetError(out var addressError)) {
            return new(ReconcileOutcome.Failed(addressError), [], false);
        }

        var id = address.GetValueOrThrow().WithId(reconcileInput.ResourceId);

        if (!registry.TryGetType(id.Type, out var registration)) {
            return new(
                ReconcileOutcome.Failed(
                    ErrorCode.InvalidResourceType,
                    $"'{id.Type}' has no registration, so '{id.Path}' cannot be reconciled. A resource "
                    + "whose provider was removed from the silo is stuck rather than silently "
                    + "converged."
                ),
                [],
                false
            );
        }

        // A type with no reconciler converges on the write. docs/plan/08 § What the resource manager
        // deliberately does not do makes the manager work for providers with no data plane at all —
        // a role assignment IS the resource, and there is nothing to apply.
        if (registration.ReconcilerType is null) {
            var note = Progress("recorded", "The resource type has no reconciler; desired state is the resource.", 100);
            return new(ReconcileOutcome.Converged, [note], false);
        }

        if (services.GetService(registration.ReconcilerType) is not IResourceReconciler reconciler) {
            return new(
                ReconcileOutcome.Failed(
                    ErrorCode.InternalError,
                    $"'{registration.ReconcilerType.FullName}' is declared as the reconciler for "
                    + $"'{id.Type}' and is not registered in the container. Register it as a singleton "
                    + "— see the remarks on IResourceReconciler."
                ),
                [],
                false
            );
        }

        var connection = clusters.Connect(reconcileInput.ClusterId);
        if (registration.RequiresCluster && connection is null) {
            return new(
                ReconcileOutcome.Failed(
                    ErrorCode.InternalError,
                    $"'{id.Type}' declares RequiresCluster and no connection was available for cluster "
                    + $"{reconcileInput.ClusterId:D}. Either the resource carries no clusterId or no "
                    + "IClusterConnectionFactory is wired — the driver refuses rather than handing the "
                    + "reconciler a null it would dereference."
                ),
                [],
                false
            );
        }

        var desired = ParseOrEmpty(reconcileInput.Desired);
        var log = new CollectingReconcileLog(clock);
        var produced = new CollectingClusterConnectionSink();
        var ns = NamespaceFor(id);

        // ── The namespace every reconciler applies into ──────────────────────────────────────────
        //
        // ⚠ HERE, AND NOT IN THE RESOURCE-GROUP GRAIN. NamespaceEnsurer's remarks carry the argument;
        // the short form is that a namespace is keyed by (group, cluster) and this is the only place
        // that holds both. It is skipped on a teardown, because a delete that begins by creating the
        // namespace it is about to empty is a delete that recreates what an operator just removed.
        if (!tearingDown && connection is not null) {
            // ⚠ BOUNDED LIKE THE PASS ITSELF. This runs before the reconciler's own budget is set up
            // and it is a read and a patch against an API server, so an unbounded token here would
            // reintroduce exactly what clause 3 exists to prevent — a grain turn blocked on a cluster
            // that stopped answering — one step above the reconciler that is not allowed to do it.
            using var namespaceBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            namespaceBudget.CancelAfter(PassBudget);

            Result<NamespaceEnsured> namespaceReady;
            try {
                namespaceReady = await namespaces.EnsureAsync(id, ns, connection, namespaceBudget.Token);
            }
            catch (OperationCanceledException) when (namespaceBudget.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested) {
                namespaceReady = Result<NamespaceEnsured>.Failure(
                    ErrorCode.ProvisioningFailed,
                    $"Creating the namespace '{ns}' on cluster {reconcileInput.ClusterId:D} did not "
                    + $"finish within {PassBudget.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} "
                    + "seconds. The cluster is answering too slowly to place anything in; the next "
                    + "pass tries again."
                );
            }

            if (namespaceReady.TryGetError(out var namespaceError)) {
                // ⚠ THE PASS FAILS RATHER THAN CONTINUING, and that is the whole point of doing this
                // in the driver. Letting the reconciler run would produce a 404 from the API server
                // naming a namespace, in twenty different reconcilers' words, on a pass that had
                // nothing wrong with it.
                log.Report(
                    "ensuring-namespace",
                    $"the namespace '{ns}' is not available on cluster {reconcileInput.ClusterId:D}: "
                    + namespaceError.Message
                );

                // ⚠ THE CODE DECIDES, NOT THIS CALL SITE — and hard-coding `retryable: true` here was
                // a real bug that the conformance suite's admission-refusal case caught. A namespace
                // an admission policy refuses, or that this platform's credentials may not create,
                // will be refused identically on every pass for the next hour; rescheduling it turns
                // a refusal the tenant could act on into an OperationTimeout an hour later.
                // ReconcileOutcome.FromFailure is where the four terminal codes are listed, and every
                // reconciler in the catalogue already routes its own apply failures through it.
                return new(ReconcileOutcome.FromFailure(namespaceError), log.Drain(), true);
            }

            var namespaceOutcome = namespaceReady.GetValueOrThrow();

            switch (namespaceOutcome.Result) {
                case ApplyResult.Created:
                    log.Report("created-namespace", $"namespace '{ns}' now exists and carries the tenant's labels");
                    break;

                // ⚠ Reported and not failed. The namespace is there, so the pass can proceed; what is
                // not ours is one of the seven labels on it, and CloudConsoles' tenant-wide egress
                // rule reads those. A tenant-boundary label held by another field manager is worth a
                // line in the operation's progress that names it.
                case ApplyResult.Conflict:
                    log.Report(
                        "ensuring-namespace",
                        $"namespace '{ns}' exists and another field manager owns a label this "
                        + $"platform sets: {namespaceOutcome.Message}"
                    );

                    break;

                default:
                    break;
            }
        }

        var context = new ReconcileContext(
            id,
            reconcileInput.ApiVersion,
            desired,
            reconcileInput.Observed,
            ns,
            connection,
            secrets,
            log
        ) {
            // ⚠ The one place the host's writer reaches a pass. Everything else that builds a context
            // — a test, a conformance harness — gets RefusingSecretWriter and has to say otherwise.
            SecretWriter = secretWriter,
            // ⚠ COLLECTED HERE AND ACTED ON BELOW, WHICH IS WHAT KEEPS THE ATTACH BEHIND THE
            // CONVERGENCE. The reconciler reports; this driver decides whether the report is due.
            ClusterConnections = produced
        };

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(PassBudget);

        ReconcileOutcome outcome;
        try {
            outcome = tearingDown
                ? await reconciler.DeleteAsync(context, budget.Token)
                : await reconciler.ReconcileAsync(context, budget.Token);
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested && !cancellationToken.IsCancellationRequested) {
            // Clause 3, violated. Retryable, because the next pass may find the slow thing finished —
            // but the message names the type so the violation is attributable rather than ambient.
            return new(
                ReconcileOutcome.Failed(
                    new Error(
                        ErrorCode.ProvisioningFailed,
                        $"'{reconciler.GetType().Name}' did not return within "
                        + $"{PassBudget.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} "
                        + "seconds. A reconciler is bounded and returns InProgress rather than "
                        + "blocking — docs/plan/08 § The reconcile loop, clause 3. The pass was "
                        + "cancelled; anything it applied stays applied and the next pass will "
                        + "converge on it."
                    ),
                    true
                ),
                log.Drain(),
                true
            );
        }

        if (outcome is null) {
            return new(
                ReconcileOutcome.Failed(
                    ErrorCode.InternalError,
                    $"'{reconciler.GetType().Name}' returned null. ReconcileOutcome has exactly three "
                    + "cases and null is not one of them."
                ),
                log.Drain(),
                true
            );
        }

        // ── Attaching a cluster this pass produced. See IClusterConnectionRegistrar. ──────────────
        //
        // ⚠ AFTER THE PASS AND ONLY ON Converged, WHICH IS THE POINT OF DOING IT HERE AT ALL. Clause
        // 4 makes Converged mean the reconciler READ THE DESIRED SHAPE BACK, so on this type it means
        // the control plane reported ready — and a connection registered before that is a connection
        // every later placement fails against, for the six to eight minutes docs/plan/09 § Kubernetes
        // in Kubernetes budgets. A descriptor reported on an InProgress pass is dropped; the next
        // pass reports it again, because a reconciler is idempotent.
        if (produced.Descriptor is { } reported && outcome.IsConverged) {
            // ⚠ THE CLUSTER ID AND THE OWNING TENANT ARE STAMPED HERE AND NOT TAKEN FROM THE
            // PROVIDER, which is the second half of "the seam is above the provider". Both are facts
            // the manager owns — the resource's own GUID and the tenant its operation belongs to —
            // and a provider that supplied them would be a provider able to register somebody else's
            // cluster under its own tenant, or to register itself under a tenant that does not own
            // it. The grain checks the owner on every later call, so what is written here is what
            // every subsequent tenancy decision about this cluster is made against.
            var descriptor = reported with {
                ClusterId = id.Id,
                OwningTenantId = spec.TenantId
            };

            var attached = await clusterRegistrar.AttachAsync(descriptor, cancellationToken);

            if (attached.TryGetError(out var attachError)) {
                // ⚠ THE PASS FAILS, and reporting Converged here instead was the tempting mistake.
                // The reconciler is right that the cluster exists; what does not exist is any way to
                // reach it, and a resource that reports Succeeded for a cluster nothing can place
                // anything in moves the failure to whoever tries next — one resource later, with an
                // error about the second resource.
                log.Report(
                    "attaching-cluster",
                    $"the control plane converged and its connection could not be registered: {attachError.Message}"
                );

                outcome = ReconcileOutcome.FromFailure(attachError);
            } else {
                log.Report(
                    "attached-cluster",
                    $"cluster {descriptor.ClusterId:D} is registered and can now be placed in",
                    100
                );
            }
        }

        // Observe after every pass that ran, converged or not. The drift scan compares against this,
        // and an observation taken only on convergence would leave a stuck resource's observed state
        // frozen at whatever it was before the trouble started.
        await ObserveAsync(reconciler, resource, id, reconcileInput, desired, connection, cancellationToken);

        return new(outcome, log.Drain(), true);
    }

    /// <summary>Reads the world back and records it on the resource grain.</summary>
    /// <remarks>
    ///     ⚠ A failed observation is <b>not</b> a failed pass. Observation is how we learn, not how we
    ///     act, and turning "the API server did not answer a read" into "the provision failed" would
    ///     fail creates that had already succeeded.
    /// </remarks>
    async Task ObserveAsync(
        IResourceReconciler reconciler,
        IResourceGrain resource,
        ResourceId id,
        ReconcileInput input,
        JsonElement desired,
        IKubeClusterConnection? connection,
        CancellationToken cancellationToken
    ) {
        try {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(PassBudget);

            var observed = await reconciler.ObserveAsync(
                new(id, input.ApiVersion, desired, NamespaceFor(id), connection),
                budget.Token
            );

            if (observed is not null) {
                await resource.ReportObservedAsync(observed);
            }
        }
        catch (OperationCanceledException) {
            // Swallowed on purpose — see the remarks.
        }
    }

    /// <summary>
    ///     The Kubernetes namespace a resource's objects belong in.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Derived here rather than by each reconciler.</b> docs/plan/09 § The command builder's
    ///     worked example reads <c>ctx.Namespace</c>, which docs/plan/08's <c>ReconcileContext</c> does
    ///     not have; twenty reconcilers deriving it themselves would be twenty chances to disagree
    ///     about what namespace a resource group maps to, and a disagreement would put two resources
    ///     from one group in two namespaces.
    ///     <para>
    ///         The rule: <c>{subscriptionId:N}-{resourceGroup}</c>, truncated to the 63-character
    ///         DNS-1123 label limit. The subscription id first because it is fixed-width, so the
    ///         truncation only ever eats the group name — and group names are already DNS-1123
    ///         (docs/plan/06 § Identifiers), so the result always is too. ⚠ Truncation can collide two
    ///         very long group names in one subscription; a collision-proof scheme needs a hash and a
    ///         lookup, which is a change to <c>KubeLabels</c> rather than a decision this driver can
    ///         make alone.
    ///     </para>
    /// </remarks>
    public static string NamespaceFor(ResourceId id) {
        const int dnsLabelLimit = 63;

        var prefix = id.SubscriptionId.ToString("N", CultureInfo.InvariantCulture);
        var candidate = prefix + "-" + id.ResourceGroup;

        return candidate.Length <= dnsLabelLimit ? candidate : candidate[..dnsLabelLimit];
    }

    IResourceGrain Resource(OperationSpec spec) =>
        grains
            .ForTenant(spec.TenantId.ToString("D", CultureInfo.InvariantCulture))
            .GetGrain<IResourceGrain>(GrainKeys.Resource(spec.ResourceId));

    static JsonElement ParseOrEmpty(string json) {
        try {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException) {
            // Desired state that will not parse is a bug on the write path, not on this one. An empty
            // object keeps the pass running so the reconciler can report what it sees, rather than
            // throwing out of a reminder where nobody catches it.
            using var fallback = JsonDocument.Parse("{}");
            return fallback.RootElement.Clone();
        }
    }

    OperationProgress Progress(string step, string detail, int percent) =>
        new() { At = clock.UtcNow, Step = step, Detail = detail, PercentComplete = percent };
}

/// <summary>
///     The <see cref="IReconcileLog" /> a pass writes into. Buffers, and the grain flushes once.
/// </summary>
/// <remarks>
///     ⚠ <b>Not thread-safe, and does not need to be.</b> One instance is built per pass and is handed
///     to exactly one reconciler call, inside one grain turn. Making it concurrent would suggest it
///     could outlive the pass, which is precisely the hidden state clause 2 forbids.
/// </remarks>
sealed class CollectingReconcileLog(IClock clock) : IReconcileLog {
    /// <summary>
    ///     How many entries one pass may report. Beyond this the pass is reporting rather than
    ///     working.
    /// </summary>
    public const int MaxPerPass = 100;

    readonly List<OperationProgress> entries = [];

    /// <inheritdoc />
    public void Report(string step, string detail) => Report(step, detail, 0);

    /// <inheritdoc />
    public void Report(string step, string detail, int percentComplete) {
        if (entries.Count >= MaxPerPass) {
            return;
        }

        entries.Add(
            new() {
                At = clock.UtcNow,
                Step = step ?? string.Empty,
                Detail = detail ?? string.Empty,
                // Clamped rather than refused — a provider miscounting replicas should not fail an
                // otherwise healthy provision.
                PercentComplete = Math.Clamp(percentComplete, 0, 100)
            }
        );
    }

    /// <summary>Takes everything reported and empties the buffer.</summary>
    public ImmutableArray<OperationProgress> Drain() {
        var drained = entries.ToImmutableArray();
        entries.Clear();
        return drained;
    }
}

/// <summary>
///     The <see cref="IClusterConnectionSink" /> one pass writes into. The driver decides what to do
///     with it.
/// </summary>
/// <remarks>
///     ⚠ <b>One descriptor, last one wins, and not a list.</b> A pass converges one resource and a
///     resource is one cluster, so two reports in a pass is a reconciler correcting itself rather than
///     two clusters. Keeping a list would make "which of these did we attach" a question, and the only
///     safe answer to it would be all of them.
/// </remarks>
sealed class CollectingClusterConnectionSink : IClusterConnectionSink {
    /// <summary>What the pass reported, or <see langword="null" /> if it reported nothing.</summary>
    public ClusterConnectionDescriptor? Descriptor { get; private set; }

    /// <inheritdoc />
    public void Produced(ClusterConnectionDescriptor descriptor) {
        ArgumentNullException.ThrowIfNull(descriptor);

        Descriptor = descriptor;
    }
}
