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
/// </remarks>
public sealed class ReconcileDriver(
    IProviderRegistry registry,
    IServiceProvider services,
    IGrainFactory grains,
    IClusterConnectionFactory clusters,
    ISecretResolver secrets,
    ISecretWriter secretWriter,
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

        var context = new ReconcileContext(
            id,
            reconcileInput.ApiVersion,
            desired,
            reconcileInput.Observed,
            NamespaceFor(id),
            connection,
            secrets,
            log
        ) {
            // ⚠ The one place the host's writer reaches a pass. Everything else that builds a context
            // — a test, a conformance harness — gets RefusingSecretWriter and has to say otherwise.
            SecretWriter = secretWriter
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
