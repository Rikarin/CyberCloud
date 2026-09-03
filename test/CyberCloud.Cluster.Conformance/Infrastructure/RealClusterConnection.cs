using CyberCloud.Kubernetes.Apply;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;

namespace CyberCloud.Cluster.Conformance.Infrastructure;

/// <summary>
///     An <see cref="IKubeClusterConnection" /> over a <b>real</b> API server, recording every
///     outcome the reconciler was handed.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Over <see cref="IKubeApiClient" /> rather than over
///         <c>IClusterConnectionGrain</c>, and the difference is one grain hop.</b> Everything the
///         cluster-backed suite asserts about the API server — server-side apply under our field
///         manager, the 409 body, <c>ConflictParser</c>, the plural in the REST path, the labels
///         surviving admission — is implemented in <see cref="KubeApiClient" />, which is exactly
///         what <c>ClusterConnectionGrain</c> calls. Going through the grain would add a
///         cluster-registration path that is not what any of these five tests is about, and would put
///         a serialization boundary between the assertion and the thing asserted.
///     </para>
///     <para>
///         ⚠ <b>It records, and that is load-bearing rather than convenient.</b> A reconciler maps
///         <see cref="ApplyResult.Conflict" /> onto <c>ReconcileOutcome.InProgress</c> — correctly,
///         because a tenant's hand edit is not a provisioning failure — so the
///         <see cref="DriftEvent" /> ADR-013 promises is invisible from the reconciler's return
///         value. Recording the <see cref="ApplyOutcome" /> is how a test can assert on the drift
///         event the API server really caused, rather than on one the harness wrote.
///     </para>
/// </remarks>
/// <param name="api">The real client.</param>
/// <param name="clusterId">The cluster id the conformance bodies name.</param>
public sealed class RealClusterConnection(IKubeApiClient api, Guid clusterId) : IKubeClusterConnection {
    /// <inheritdoc />
    public Guid ClusterId => clusterId;

    /// <summary>Every apply outcome the API server produced, in order.</summary>
    public ConcurrentQueue<ApplyOutcome> Outcomes { get; } = new();

    /// <summary>Every command that was sent, in order — the rendered manifest, labels included.</summary>
    public ConcurrentQueue<KubeCommand> Applied { get; } = new();

    /// <summary>Forgets the recordings. ⚠ Does not touch the cluster: a real one has no <c>Reset</c>.</summary>
    public void Reset() {
        Outcomes.Clear();
        Applied.Clear();
    }

    /// <summary>Every drift event the API server produced.</summary>
    public ImmutableArray<DriftEvent> Drift =>
        [.. Outcomes.Select(x => x.Drift).Where(x => x is not null).Select(x => x!)];

    /// <inheritdoc />
    public async Task<Result<ApplyOutcome>> ApplyAsync(
        KubeCommand command,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(command);
        Applied.Enqueue(command);

        var result = await api.ApplyAsync(command, cancellationToken).ConfigureAwait(false);

        if (result.IsSuccess) {
            Outcomes.Enqueue(result.GetValueOrThrow());
        }

        return result;
    }

    /// <inheritdoc />
    public Task<Result<KubeObject>> GetAsync(ObjectRef target, CancellationToken cancellationToken = default) =>
        api.GetAsync(target, cancellationToken);

    /// <inheritdoc />
    public Task<Result> DeleteAsync(
        KubeCommand command,
        CascadePolicy policy = CascadePolicy.Background,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(command);
        return api.DeleteAsync(command.Target, policy, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>The same code the connection grain runs, against the same API server, and that is
    ///     what makes this the only place the namespace inventory is really exercised.</b> Everything
    ///     it does that a dictionary cannot fail is here: the discovery of what a real cluster
    ///     serves, the objects Kubernetes puts in a namespace without being asked, and the CRDs the
    ///     conformance fixtures install.
    /// </remarks>
    public Task<Result<IReadOnlyList<KubeObjectSummary>>> ListNamespaceAsync(
        string ns,
        CancellationToken cancellationToken = default
    ) =>
        NamespaceContents.ListAsync(api, clusterId, ns, cancellationToken);
}

/// <summary>Hands the reconcile driver the one real connection a harness owns.</summary>
/// <remarks>
///     ⚠ Answers <see langword="null" /> for any cluster id but its own, exactly as
///     <c>FakeClusterConnectionFactory</c> does, so a body naming a different cluster still gets the
///     driver's named <c>RequiresCluster</c> error rather than a null the reconciler dereferences.
/// </remarks>
/// <param name="connection">The connection.</param>
public sealed class RealClusterConnectionFactory(RealClusterConnection connection) : IClusterConnectionFactory {
    /// <inheritdoc />
    public IKubeClusterConnection? Connect(Guid clusterId) =>
        clusterId == connection.ClusterId ? connection : null;
}

/// <summary>
///     An <see cref="IClusterObjectInventory" /> built from a real <c>LIST</c> against a real API
///     server, filtered by <c>cybercloud.io/managed-by=cybercloud</c>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THIS IS A TEST-LOCAL STAND-IN, AND SAYING SO IS THE POINT.</b> docs/plan/09
///         § Observing specifies a per-cluster <b>informer</b> holding a live view, and it is not
///         built: the shipped <c>UnavailableClusterObjectInventory</c> fails rather than
///         reporting an empty cluster, on purpose, and that has not changed. What this supplies is a
///         paged list at the moment of the scan.
///     </para>
///     <para>
///         What that buys, precisely, and it is not nothing: <c>DriftScanner</c>'s orphan and stray
///         cases run against objects a real reconciler really applied to a real API server, joined on
///         the <c>cybercloud.io/resource-id</c> label as ADR-013 intends, selected by the selector
///         docs/plan/09 § Observing specifies. A dictionary cannot fail any of that. What it does
///         <b>not</b> buy is the informer, the watch, the resume, or the hourly reminder — those stay
///         owed, and the drift test's remarks say so where a reader will meet them.
///     </para>
///     <para>
///         ⚠ <b>It refuses to report an empty inventory as a success for the same reason the shipped
///         implementation does.</b> A list that failed halfway would say "every resource here is a
///         stray", and a scan that believed it would re-apply a cluster.
///     </para>
/// </remarks>
/// <param name="api">The real client.</param>
/// <param name="kinds">The kinds to list. A real informer learns these from the registry.</param>
/// <param name="ns">The namespace to list in.</param>
public sealed class ListBackedClusterObjectInventory(
    IKubeApiClient api,
    ImmutableArray<GroupVersionKind> kinds,
    string ns
) : IClusterObjectInventory {
    /// <inheritdoc />
    public async Task<Result<ImmutableArray<ClusterObjectRecord>>> ListManagedAsync(
        Guid clusterId,
        CancellationToken cancellationToken = default
    ) {
        var records = ImmutableArray.CreateBuilder<ClusterObjectRecord>();

        foreach (var kind in kinds) {
            var continueToken = string.Empty;

            do {
                var page = await api.ListAsync(
                        kind,
                        ns,
                        KubeLabels.ManagedBySelector,
                        continueToken: string.IsNullOrEmpty(continueToken) ? null : continueToken,
                        cancellationToken: cancellationToken
                    )
                    .ConfigureAwait(false);

                if (page.TryGetError(out var error)) {
                    // ⚠ Fails rather than returning what it managed to read. A partial inventory
                    // reports the missing part as strays.
                    return Result<ImmutableArray<ClusterObjectRecord>>.Failure(error);
                }

                var value = page.GetValueOrThrow();

                foreach (var item in value.Items) {
                    if (Read(item, kind) is { } record) {
                        records.Add(record);
                    }
                }

                continueToken = value.ContinueToken;
            } while (!string.IsNullOrEmpty(continueToken));
        }

        return Result<ImmutableArray<ClusterObjectRecord>>.Success(records.ToImmutable());
    }

    static ClusterObjectRecord? Read(string json, GroupVersionKind kind) {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (!root.TryGetProperty("metadata", out var metadata)) {
            return null;
        }

        var labels = metadata.TryGetProperty("labels", out var l) ? l : default;
        var annotations = metadata.TryGetProperty("annotations", out var a) ? a : default;

        var resourceId = Text(labels, KubeLabels.ResourceId);

        // ⚠ An object carrying managed-by but no parseable resource-id is not silently dropped in
        // production either — it is the ADR-013 gap that produces objects nothing can attribute. Here
        // it is skipped, because DriftScanner joins on the GUID and has nothing to join a blank to.
        if (!Guid.TryParse(resourceId, out var owner)) {
            return null;
        }

        return new() {
            ResourceId = owner,
            ResourcePath = Text(annotations, KubeLabels.ResourcePathAnnotation),
            ReconcileHash = Text(annotations, KubeLabels.ReconcileHashAnnotation),
            // ⚠ Read off the object rather than defaulted, because the scan uses it to tell an
            // object that belongs to a RESOURCE from one that belongs to a resource GROUP — the
            // namespace the platform writes itself, whose resource-id is derived and matches no
            // grain. Leaving it blank makes every namespace on the cluster a permanent orphan
            // finding. KubeLabels.IsGroupScoped is the test; ClusterObjectRecord's remarks say why
            // the member is `required`.
            ResourceType = Text(labels, KubeLabels.ResourceType),
            Target = new() {
                Kind = kind,
                Namespace = Text(metadata, "namespace"),
                Name = Text(metadata, "name")
            }
        };
    }

    static string Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}
