using System.Collections.Concurrent;
using System.Collections.Immutable;

namespace CyberCloud.Conformance.Harness;

/// <summary>
///     An in-memory stand-in for one cluster's API server: apply, read back, delete.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>What this does not prove, stated first so it is not assumed.</b> It is a dictionary,
///         not Kubernetes. It does not validate a manifest, does not run admission, does not do
///         field-level server-side-apply ownership, does not garbage-collect an
///         <c>ownerReference</c>, and its <see cref="ApplyResult.Conflict" /> is a switch a test
///         flips rather than a field another manager took. Everything that needs a real API server
///         lives in <c>CyberCloud.Cluster.Conformance</c>, which runs the same
///         <c>ProviderConformanceCase</c> against a k3s container — including the conflict, which is
///         there produced by a second field manager and the API server's own 409 rather than by
///         <see cref="ConflictOn" />.
///     </para>
///     <para>
///         What it <i>does</i> prove is the half that is about <b>us</b>: that a reconciler applies
///         what the desired body says, reads it back before saying <c>Converged</c>, notices when the
///         world changes underneath it, and removes what it made. Those are properties of the
///         provider and of the reconcile loop, and a real API server would not make them any truer.
///     </para>
///     <para>
///         ⚠ <b>The store is keyed by <see cref="ObjectRef" />'s three parts and holds the body the
///         command carried, labels and annotations included.</b> That is what lets
///         <c>ProviderConformanceTests</c> assert ADR-013's seven mandatory labels against output a
///         reconciler really rendered — docs/plan/23 § The architecture gates' <c>Labels</c> row asks
///         for exactly that, "asserted against real output", and until a provider existed there was no
///         real output to assert against.
///     </para>
/// </remarks>
public sealed class FakeKubeCluster(Guid clusterId) : IKubeClusterConnection {
    readonly ConcurrentDictionary<string, string> objects = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, string> hashes = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Guid ClusterId => clusterId;

    /// <summary>Every apply this cluster has seen, in order, as the command carried it.</summary>
    public ConcurrentQueue<KubeCommand> Applied { get; } = new();

    /// <summary>Every delete this cluster has seen, in order.</summary>
    public ConcurrentQueue<ObjectRef> Deleted { get; } = new();

    /// <summary>
    ///     When set, every apply answers <see cref="ApplyResult.Suspended" /> and writes nothing —
    ///     the unreachable-cluster case of docs/plan/09 § Cluster connections.
    /// </summary>
    public bool Suspended { get; set; }

    /// <summary>
    ///     When set, every apply answers <see cref="ApplyResult.Conflict" /> and writes nothing.
    /// </summary>
    public string ConflictOn { get; set; } = string.Empty;

    /// <summary>Forgets everything, including the levers.</summary>
    public void Reset() {
        objects.Clear();
        hashes.Clear();
        Applied.Clear();
        Deleted.Clear();
        Suspended = false;
        ConflictOn = string.Empty;
    }

    /// <summary>Whether an object is present.</summary>
    /// <param name="target">Which object.</param>
    public bool Holds(ObjectRef target) => objects.ContainsKey(Key(target));

    /// <summary>The object's JSON, or <see langword="null" /> when it is not there.</summary>
    /// <param name="target">Which object.</param>
    public string? Read(ObjectRef target) => objects.TryGetValue(Key(target), out var json) ? json : null;

    /// <summary>
    ///     Removes an object <b>behind the reconciler's back</b> — the <c>kubectl delete</c> of the
    ///     drift case, and the <c>BreakAsync</c> half of a <see cref="ConformanceWorld" />.
    /// </summary>
    /// <param name="target">Which object.</param>
    /// <returns><c>true</c> when something was removed.</returns>
    /// <remarks>
    ///     ⚠ Deliberately not routed through <see cref="DeleteAsync" />: the point of breaking the
    ///     world is that it happens by a path the reconciler cannot observe or record, so a
    ///     reconciler that remembered its own deletes would still be caught.
    /// </remarks>
    public bool RemoveBehindTheirBack(ObjectRef target) {
        hashes.TryRemove(Key(target), out _);
        return objects.TryRemove(Key(target), out _);
    }

    /// <summary>Replaces an object's JSON behind the reconciler's back — a hand edit in the cluster.</summary>
    /// <param name="target">Which object.</param>
    /// <param name="json">What it now says.</param>
    public void MutateBehindTheirBack(ObjectRef target, string json) => objects[Key(target)] = json;

    /// <inheritdoc />
    public Task<Result<ApplyOutcome>> ApplyAsync(
        KubeCommand command,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(command);
        Applied.Enqueue(command);

        if (Suspended) {
            // ⚠ A SUCCESSFUL Result carrying Suspended, not a failure. docs/plan/09 § Cluster
            // connections: an unreachable cluster suspends reconciles rather than failing them, and
            // IKubeClusterConnection's own remarks make a failed Result mean "we got it wrong".
            return Task.FromResult(
                Result<ApplyOutcome>.Success(
                    new() {
                        Result = ApplyResult.Suspended,
                        Target = command.Target,
                        Message = "We cannot reach your cluster; this will resume automatically."
                    }
                )
            );
        }

        if (ConflictOn.Length > 0) {
            return Task.FromResult(
                Result<ApplyOutcome>.Success(
                    new() {
                        Result = ApplyResult.Conflict,
                        Target = command.Target,
                        Drift = new() {
                            ResourceId = command.ResourceId,
                            ClusterId = clusterId,
                            Target = command.Target,
                            FieldManager = command.FieldManager,
                            Conflicts = [new() { Field = ConflictOn, OwnedBy = "kubectl-edit" }]
                        }
                    }
                )
            );
        }

        var key = Key(command.Target);
        var existed = objects.ContainsKey(key);
        var unchanged = existed && hashes.TryGetValue(key, out var previous) && previous == command.ReconcileHash;

        objects[key] = command.Body;
        hashes[key] = command.ReconcileHash;

        return Task.FromResult(
            Result<ApplyOutcome>.Success(
                new() {
                    Result = unchanged ? ApplyResult.Unchanged : existed ? ApplyResult.Updated : ApplyResult.Created,
                    Target = command.Target,
                    ResourceVersion = objects.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ReconcileHash = command.ReconcileHash
                }
            )
        );
    }

    /// <inheritdoc />
    public Task<Result<KubeObject>> GetAsync(ObjectRef target, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(target);

        if (!objects.TryGetValue(Key(target), out var json)) {
            // ⚠ ResourceNotFound rather than a successful empty. WidgetReconciler's delete path keys
            // "the object is gone" off this exact code, and a stub that answered success-with-nothing
            // would converge a teardown that had not happened.
            return Task.FromResult(
                Result<KubeObject>.Failure(ErrorCode.ResourceNotFound, $"'{target}' is not in cluster {clusterId:D}.")
            );
        }

        return Task.FromResult(
            Result<KubeObject>.Success(
                new() {
                    Ref = target,
                    Json = json,
                    ResourceVersion = hashes.TryGetValue(Key(target), out var hash) ? hash : string.Empty
                }
            )
        );
    }

    /// <inheritdoc />
    public Task<Result> DeleteAsync(
        KubeCommand command,
        CascadePolicy policy = CascadePolicy.Background,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(command);
        Deleted.Enqueue(command.Target);

        var key = Key(command.Target);
        hashes.TryRemove(key, out _);

        return Task.FromResult(
            objects.TryRemove(key, out _)
                ? Result.Success
                : Result.Failure(ErrorCode.ResourceNotFound, $"'{command.Target}' is not in cluster {clusterId:D}.")
        );
    }

    /// <summary>Every object currently held, for a test that wants to assert on the whole cluster.</summary>
    public ImmutableArray<string> Everything => [.. objects.Values];

    static string Key(ObjectRef target) =>
        target.Kind.ApiVersion + "|" + target.Kind.Kind + "|" + target.Namespace + "|" + target.Name;
}

/// <summary>
///     Hands the reconcile driver the one <see cref="FakeKubeCluster" /> a harness owns.
/// </summary>
/// <remarks>
///     ⚠ Answers <see langword="null" /> for any cluster id but its own, which is what makes the
///     <c>RequiresCluster</c> refusal reachable in a test: a resource whose body names a different
///     cluster gets the driver's named error rather than a null the reconciler dereferences.
/// </remarks>
public sealed class FakeClusterConnectionFactory(FakeKubeCluster cluster) : IClusterConnectionFactory {
    /// <inheritdoc />
    public IKubeClusterConnection? Connect(Guid clusterId) => clusterId == cluster.ClusterId ? cluster : null;
}
