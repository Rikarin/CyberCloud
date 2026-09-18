using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json.Nodes;

namespace CyberCloud.Conformance.Harness;

/// <summary>
///     An in-memory stand-in for one cluster's API server: apply, read back, delete.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>What this does not prove, stated first so it is not assumed.</b> It is a dictionary,
///         not Kubernetes. It does not validate a manifest, does not run admission, does not do
///         field-level server-side-apply ownership, and its <see cref="ApplyResult.Conflict" /> is
///         a switch a test flips rather than a field another manager took. Everything that needs a
///         real API server lives in <c>CyberCloud.Cluster.Conformance</c>, which runs the same
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
///         ⚠
///         <b>
///             The store is keyed by <see cref="ObjectRef" />'s three parts and holds the body the
///             command carried, labels and annotations included.
///         </b> That is what lets
///         <c>ProviderConformanceTests</c> assert ADR-013's seven mandatory labels against output a
///         reconciler really rendered — docs/plan/23 § The architecture gates' <c>Labels</c> row asks
///         for exactly that, "asserted against real output", and until a provider existed there was no
///         real output to assert against.
///     </para>
///     <para>
///         ⚠
///         <b>
///             It DOES issue a <c>metadata.uid</c> and it DOES garbage-collect a dependent with its
///             owner, because issue #69 was invisible without both.
///         </b> CloudNativePG stamps a controller reference on every claim it creates, and the
///         collector removes the claim with the <c>Cluster</c> — before a recovery window starts.
///         A fake that echoed applies and deleted only what it was told to delete could not show a
///         claim dying, so the shared conformance case over that family reported a window it never
///         exercised. <see cref="DeleteAsync" /> now follows <c>ownerReferences</c> by uid the way
///         the collector does — background and foreground cascade remove the dependents, orphan
///         strips the reference — and <see cref="ApplyAsync" /> mints a uid on create and keeps it
///         across updates, so a reconciler that reads one back to adopt a claim reads a real one.
///         <see cref="RemoveBehindTheirBack" /> deliberately does not cascade: it models a hand
///         edit the reconciler must notice and repair, and the drift cases plant nothing owned.
///     </para>
///     <para>
///         ⚠
///         <b>
///             It is an echo in every respect but one: a BUILT-IN object is stored with its empty
///             collections removed.
///         </b> That exception exists because "the fake echoes the apply" is not a
///         harmless simplification — it made the harness structurally blind to everything a real API
///         server takes <i>away</i> under <c>omitempty</c>, and a provider hung against k3s on exactly
///         that. A <i>custom</i> resource is still echoed, because that is what a real server does to
///         one, and three families use an empty object as a presence flag. See
///         <see cref="DropEmptyCollections" /> for the measurement that settled the boundary and for
///         the larger half this still does not model at all.
///     </para>
/// </remarks>
public sealed class FakeKubeCluster(Guid clusterId) : IKubeClusterConnection {
    readonly ConcurrentDictionary<string, string> objects = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, string> hashes = new(StringComparer.Ordinal);

    // ⚠ The addresses, kept beside the bodies because Key() is lossy: it folds the plural away, and
    // a namespace listing has to hand back a GroupVersionKind a caller could address with.
    readonly ConcurrentDictionary<string, ObjectRef> addresses = new(StringComparer.Ordinal);

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

    /// <summary>
    ///     When set, every apply comes back as a <b>failed</b> <see cref="Result" /> carrying this
    ///     code — the API server refusing, rather than the API server being out of reach.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A failed Result, and that is the whole distinction this lever exists to make.</b>
    ///     <see cref="Suspended" /> and <see cref="ConflictOn" /> both answer <i>successfully</i>,
    ///     because a cluster we cannot reach and a field somebody else owns are states the reconcile
    ///     loop expects. A refusal is not: <c>KubeFailures.Classify</c> turns the API server's own
    ///     answer into one of <see cref="ErrorCode.PolicyViolation" />,
    ///     <see cref="ErrorCode.InvalidRequestBody" />, <see cref="ErrorCode.InvalidResourceType" />
    ///     or <see cref="ErrorCode.AuthorizationFailed" />, and the reconciler is supposed to read the
    ///     code rather than assume another pass will do better. Until this lever existed there was no
    ///     way to reach that branch of a reconciler from the Docker-free suite at all, which is why
    ///     every provider was passing <c>retryable: true</c> unchallenged.
    /// </remarks>
    public ErrorCode? RefuseWith { get; set; }

    /// <summary>Forgets everything, including the levers — everything but the baseline.</summary>
    /// <remarks>
    ///     ⚠ A baseline, once <see cref="MarkBaseline" /> has taken one, is put back after the clear.
    ///     Nothing about it enters <see cref="Applied" />: the objects were applied by a companion's
    ///     reconciler before the first test, and the labels assertion over <see cref="Applied" /> is
    ///     about what the case under test wrote.
    /// </remarks>
    public void Reset() {
        objects.Clear();
        hashes.Clear();
        addresses.Clear();
        Applied.Clear();
        Deleted.Clear();
        Suspended = false;
        ConflictOn = string.Empty;
        RefuseWith = null;

        if (baseline is not { } kept) {
            return;
        }

        foreach (var (key, json, hash, address) in kept) {
            objects[key] = json;
            addresses[key] = address;
            if (hash is not null) {
                hashes[key] = hash;
            }
        }
    }

    ImmutableArray<(string Key, string Json, string? Hash, ObjectRef Address)>? baseline;

    /// <summary>
    ///     Remembers everything the cluster holds now as the world every <see cref="Reset" /> puts
    ///     back — the objects a case's companions rendered, which its own reconciler reads and must
    ///     not lose between assertions.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Taken once, by the harness, after the companions converge and before the first test.</b>
    ///     A baseline taken later would carry a test's own leftovers into every test after it, which
    ///     is the ordering coupling <c>ConformanceState.Namespaces</c>' remarks describe. See
    ///     <see cref="IProviderCaseSource.Companions" />.
    /// </remarks>
    public void MarkBaseline() =>
        baseline = [
            .. addresses.Select(x => (
                x.Key,
                objects.TryGetValue(x.Key, out var json) ? json : "{}",
                hashes.TryGetValue(x.Key, out var hash) ? hash : null,
                x.Value
            ))
        ];

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
        addresses.TryRemove(Key(target), out _);
        return objects.TryRemove(Key(target), out _);
    }

    /// <summary>Replaces an object's JSON behind the reconciler's back — a hand edit in the cluster.</summary>
    /// <param name="target">Which object.</param>
    /// <param name="json">What it now says.</param>
    /// <remarks>
    ///     ⚠ It records the address as well as the body, so an object planted this way is visible to
    ///     <see cref="ListNamespaceAsync" />. That is the whole point when the thing being modelled
    ///     is a <c>Secret</c> an operator added or a chart nobody registered — an occupant a
    ///     namespace reclaim has to find and which no apply of ours ever created.
    /// </remarks>
    public void MutateBehindTheirBack(ObjectRef target, string json) {
        objects[Key(target)] = WithUid(json, Key(target));
        addresses[Key(target)] = target;
    }

    /// <summary>The stored object's <c>metadata.uid</c>, or empty when it is not there.</summary>
    /// <param name="target">Which object.</param>
    public string UidOf(ObjectRef target) =>
        objects.TryGetValue(Key(target), out var json) ? KubeJson.UidOf(JsonNode.Parse(json)) : string.Empty;

    /// <summary>The stored object's controller, or <see langword="null" /> when it has none or is not there.</summary>
    /// <param name="target">Which object.</param>
    public OwnerRef? ControllerOf(ObjectRef target) =>
        objects.TryGetValue(Key(target), out var json) ? KubeJson.ControllerOf(JsonNode.Parse(json)) : null;

    /// <inheritdoc />
    public Task<Result<ApplyOutcome>> ApplyAsync(
        KubeCommand command,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(command);
        Applied.Enqueue(command);

        if (RefuseWith is { } refusal) {
            // The tenant-facing half of a KubeRefusal, phrased the way KubeFailures.Classify phrases
            // an admission decision: the cluster's own words, and "the object was not written".
            return Task.FromResult(
                Result<ApplyOutcome>.Failure(
                    refusal,
                    $"Cluster {clusterId:D} refused to apply {command.Target}: admission webhook "
                    + "\"policy.cybercloud.test\" denied the request. The object was not written."
                )
            );
        }

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

        // ⚠ A BUILT-IN OBJECT IS STORED WITHOUT ITS EMPTY COLLECTIONS, AND THAT IS THE ONE PLACE THIS
        // STOPS BEING AN ECHO. A custom resource is echoed, because that is what a real API server
        // does to one. See DropEmptyCollections' remarks.
        objects[key] = WithUid(DropEmptyCollections(command.Target, command.Body), key);
        hashes[key] = command.ReconcileHash;
        addresses[key] = command.Target;

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
        addresses.TryRemove(key, out _);

        if (!objects.TryRemove(key, out var removed)) {
            return Task.FromResult(
                Result.Failure(ErrorCode.ResourceNotFound, $"'{command.Target}' is not in cluster {clusterId:D}.")
            );
        }

        // ⚠ THE GARBAGE COLLECTOR'S HALF, and the reason a soft-deleted PostgreSQL server's claims
        // can be seen to die here. A dependent is found by the uid in its ownerReferences, never by
        // name — an owner re-created under the same name has a new uid and inherits nothing.
        Collect(KubeJson.UidOf(JsonNode.Parse(removed)), command.Target.Namespace, policy);

        return Task.FromResult(Result.Success);
    }

    /// <summary>
    ///     What the garbage collector does when an owner goes: removes every dependent naming its
    ///     uid, recursively, or under <see cref="CascadePolicy.Orphan" /> strips the reference and
    ///     leaves the dependent standing.
    /// </summary>
    void Collect(string ownerUid, string ns, CascadePolicy policy) {
        if (ownerUid.Length == 0) {
            return;
        }

        foreach (var (key, target) in addresses.ToArray()) {
            if (!string.Equals(target.Namespace, ns, StringComparison.Ordinal)
                || !objects.TryGetValue(key, out var json)
                || JsonNode.Parse(json) is not JsonObject root
                || root["metadata"] is not JsonObject metadata
                || metadata["ownerReferences"] is not JsonArray owners) {
                continue;
            }

            var names = owners.OfType<JsonObject>()
                .Any(owner => owner["uid"] is JsonValue value && value.TryGetValue<string>(out var uid) && uid == ownerUid);

            if (!names) {
                continue;
            }

            if (policy == CascadePolicy.Orphan) {
                var kept = new JsonArray();
                foreach (var owner in owners.OfType<JsonObject>()) {
                    if (owner["uid"] is not JsonValue value || !value.TryGetValue<string>(out var uid) || uid != ownerUid) {
                        kept.Add(owner.DeepClone());
                    }
                }

                if (kept.Count == 0) {
                    metadata.Remove("ownerReferences");
                } else {
                    metadata["ownerReferences"] = kept;
                }

                objects[key] = root.ToJsonString();
                continue;
            }

            hashes.TryRemove(key, out _);
            addresses.TryRemove(key, out _);
            objects.TryRemove(key, out _);
            Collect(KubeJson.UidOf(root), ns, policy);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     One kind under one selector, which is the shape a provider uses to find the claims an
    ///     operator created for it. Every selector pair must match a label exactly; an empty selector
    ///     is refused, as the production lister refuses it.
    /// </remarks>
    public Task<Result<IReadOnlyList<KubeObjectSummary>>> ListAsync(
        GroupVersionKind kind,
        string ns,
        string labelSelector,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(kind);

        if (string.IsNullOrEmpty(labelSelector)) {
            return Task.FromResult(
                Result<IReadOnlyList<KubeObjectSummary>>.Failure(
                    ErrorCode.InvalidRequestBody,
                    $"A selected listing of {kind} in '{ns}' was asked for with no selector."
                )
            );
        }

        var wanted = labelSelector.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(x => x[0], x => x.Length > 1 ? x[1] : string.Empty, StringComparer.Ordinal);

        var found = new List<KubeObjectSummary>();

        foreach (var (key, target) in addresses) {
            // ⚠ Group, version and kind, not the whole record: a caller's plural need not match
            // the one the object was applied under for it to be the same REST resource.
            if (target.Kind.ApiVersion != kind.ApiVersion
                || target.Kind.Kind != kind.Kind
                || !string.Equals(target.Namespace, ns, StringComparison.Ordinal)
                || !objects.TryGetValue(key, out var json)) {
                continue;
            }

            var labels = LabelsOf(json);

            if (wanted.All(pair => labels.TryGetValue(pair.Key, out var value) && value == pair.Value)) {
                found.Add(new() { Kind = target.Kind, Namespace = ns, Name = target.Name, Labels = labels });
            }
        }

        return Task.FromResult(Result<IReadOnlyList<KubeObjectSummary>>.Success(found));
    }

    /// <inheritdoc />
    /// <remarks>
    ///     The merge patch as the API server would hold it: the list replaced by the one owner, or
    ///     the key removed. Nothing else on the object moves.
    /// </remarks>
    public Task<Result> SetOwnerAsync(ObjectRef target, OwnerRef? owner, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(target);

        if (!objects.TryGetValue(Key(target), out var json) || JsonNode.Parse(json) is not JsonObject root) {
            return Task.FromResult(Result.Failure(ErrorCode.ResourceNotFound, $"'{target}' is not in cluster {clusterId:D}."));
        }

        if (root["metadata"] is not JsonObject metadata) {
            metadata = [];
            root["metadata"] = metadata;
        }

        if (owner is null) {
            metadata.Remove("ownerReferences");
        } else {
            metadata["ownerReferences"] = new JsonArray(KubeJson.OwnerReference(owner));
        }

        objects[Key(target)] = root.ToJsonString();
        return Task.FromResult(Result.Success);
    }

    static Dictionary<string, string> LabelsOf(string json) {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);

        if (JsonNode.Parse(json) is JsonObject root
            && root["metadata"] is JsonObject metadata
            && metadata["labels"] is JsonObject written) {
            foreach (var (name, value) in written) {
                labels[name] = value?.GetValue<string>() ?? string.Empty;
            }
        }

        return labels;
    }

    /// <summary>
    ///     The body with a <c>metadata.uid</c>: the one the store already holds for this key, else the
    ///     one the body brought, else a fresh one — what a real API server does on every create and
    ///     preserves on every update.
    /// </summary>
    string WithUid(string json, string key) {
        if (JsonNode.Parse(json) is not JsonObject root) {
            return json;
        }

        if (root["metadata"] is not JsonObject metadata) {
            metadata = [];
            root["metadata"] = metadata;
        }

        var existing = objects.TryGetValue(key, out var previous) ? KubeJson.UidOf(JsonNode.Parse(previous)) : string.Empty;

        if (existing.Length > 0) {
            metadata["uid"] = existing;
        } else if (KubeJson.UidOf(root).Length == 0) {
            metadata["uid"] = $"{clusterId:N}-{Interlocked.Increment(ref minted).ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        }

        return root.ToJsonString();
    }

    int minted;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         The one member of <see cref="IKubeClusterConnection" /> whose default implementation
    ///         refuses, overridden here because a fake that inherited it would make every namespace-reclaim
    ///         test assert a refusal it did not mean.
    ///     </b> What it models is a listing over everything the
    ///     store holds in that namespace — which is the shape of the real one, minus the API discovery
    ///     that is the expensive half. It cannot show that a real cluster's <c>ServiceAccount/default</c>
    ///     is there, because nothing here creates one; <c>NamespaceReclaim.IsAmbient</c> is the rule
    ///     for those and the cluster-backed suite is where a real one is met.
    /// </remarks>
    public Task<Result<IReadOnlyList<KubeObjectSummary>>> ListNamespaceAsync(
        string ns,
        CancellationToken cancellationToken = default
    ) {
        var found = new List<KubeObjectSummary>();

        foreach (var (key, target) in addresses) {
            if (!string.Equals(target.Namespace, ns, StringComparison.Ordinal)
                || !objects.TryGetValue(key, out var json)) {
                continue;
            }

            found.Add(new() { Kind = target.Kind, Namespace = ns, Name = target.Name, Labels = LabelsOf(json) });
        }

        return Task.FromResult(Result<IReadOnlyList<KubeObjectSummary>>.Success(found));
    }

    /// <summary>Every object currently held, for a test that wants to assert on the whole cluster.</summary>
    public ImmutableArray<string> Everything => [.. objects.Values];

    static string Key(ObjectRef target) =>
        target.Kind.ApiVersion + "|" + target.Kind.Kind + "|" + target.Namespace + "|" + target.Name;

    /// <summary>
    ///     Returns a <b>built-in</b> object's body with every empty array and empty object removed, at
    ///     every depth, and a custom resource's body unchanged.
    /// </summary>
    /// <param name="target">Which object, whose API group decides whether the body is stripped.</param>
    /// <param name="body">The body the command carried.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Why anything is stripped at all.</b> A real API server drops an empty collection
    ///         when the Go field it deserialises into carries <c>omitempty</c> — which is <i>every</i>
    ///         optional list and map on <i>every</i> built-in object.
    ///         <c>NetworkPolicySpec.Ingress</c> is one: the empty list that spells "deny all ingress"
    ///         comes back with no key at all, so <c>CyberCloud.Terminal/consoles</c> converged in this
    ///         harness and hung forever against k3s. Storing the body verbatim made that undetectable
    ///         here by construction.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             Why ONLY built-ins are stripped, which was settled by measurement rather than by
    ///             argument.
    ///         </b> The first version of this stripped every kind, on the theory that an
    ///         empty collection never carries meaning and that forcing the tolerant spelling
    ///         everywhere was therefore free.
    ///         <b>
    ///             It is not, and three provider families proved it in
    ///             one run.
    ///         </b> A custom resource has no <c>omitempty</c> — a CRD's stored JSON keeps what
    ///         was applied — and Strimzi, Cluster API and kube-ovn all use an
    ///         <b>
    ///             empty object as a
    ///             presence flag
    ///         </b>: <c>spec.cruiseControl = {}</c> means "run Cruise Control", and
    ///         <c>bridge = {}</c> and <c>pod = {}</c> mean the same kind of thing. For those the
    ///         difference between absent and present-but-empty is real, a real server preserves it,
    ///         and no tolerant spelling can recover information the harness threw away. Stripping them
    ///         did not make anybody's comparison better; it made nine tests in each of three families
    ///         fail for a reason that does not exist outside this class.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             The group test errs toward not stripping, which is the direction that cannot
    ///             invent a failure.
    ///         </b> Kubernetes reserves <c>k8s.io</c> for itself, so a group that is
    ///         empty, ends in <c>.k8s.io</c>, or is one of the five legacy names is a built-in and
    ///         nothing else can be. A built-in group missing from that set leaves today's blind spot
    ///         in place for that kind — a gap, not a false alarm — whereas a custom group wrongly
    ///         included would break a legitimate presence flag, which is the failure that was just
    ///         measured.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What it does not model, so nobody reads a green run as more than it is.</b>
    ///         <c>omitempty</c> also drops an empty string, a zero number and a <see langword="false" />
    ///         boolean, and this drops none of those — which fields carry the tag lives in Go struct
    ///         tags this repository does not have, and guessing would break comparisons that are
    ///         correct. Nor does it model the other direction at all: a real server ADDS a CRD's
    ///         <c>+kubebuilder:default</c>, a <c>status</c> and <c>managedFields</c>, and this harness
    ///         derives its CRD stub from <c>ProviderConformanceCase.Objects</c>, so a derived stub has
    ///         no defaults and an equality-instead-of-containment bug still passes here. That one is
    ///         covered by <c>CyberCloud.Cluster.Conformance</c> and by <c>KubeJson.Contains</c>, and
    ///         by nothing in this class.
    ///     </para>
    /// </remarks>
    static string DropEmptyCollections(ObjectRef target, string body) {
        if (!IsBuiltIn(target.Kind.Group) || JsonNode.Parse(body) is not JsonObject document) {
            return body;
        }

        Strip(document);
        return document.ToJsonString();
    }

    /// <summary>Whether an API group is one Kubernetes itself serves.</summary>
    /// <param name="group">The group, empty for core.</param>
    /// <remarks>
    ///     ⚠ The five names are the pre-<c>k8s.io</c> groups, which are a closed set — everything
    ///     added since is under a <c>.k8s.io</c> suffix, and that suffix is reserved, so a provider's
    ///     own CRD group cannot collide with this test.
    /// </remarks>
    static bool IsBuiltIn(string group) =>
        group.Length == 0
        || group.EndsWith(".k8s.io", StringComparison.Ordinal)
        || group is "apps" or "batch" or "autoscaling" or "policy" or "extensions";

    /// <summary>Removes every empty array and empty object under one node, depth first.</summary>
    /// <param name="node">The node to strip in place.</param>
    /// <remarks>
    ///     ⚠ Depth first, so a map whose only members were themselves empty collections is itself
    ///     empty by the time its parent looks at it — which is what a real server does, an empty
    ///     struct being just as absent as an empty list.
    /// </remarks>
    static void Strip(JsonNode node) {
        switch (node) {
            case JsonObject map:
                foreach (var key in map.Select(x => x.Key).ToList()) {
                    if (map[key] is not { } child) {
                        continue;
                    }

                    Strip(child);

                    if (KubeJson.IsAbsentOrEmpty(map[key])) {
                        map.Remove(key);
                    }
                }

                break;

            case JsonArray array:
                foreach (var child in array.Where(x => x is not null)) {
                    Strip(child!);
                }

                break;
        }
    }
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
