using CyberCloud.Core.Time;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Text.Json;

namespace CyberCloud.Kubernetes.Apply;

/// <summary>
///     <see cref="IKubeApiClient" /> over <c>KubernetesClient</c> 19.0.2 — server-side apply, reads,
///     deletes and lists against one cluster.
/// </summary>
/// <remarks>
///     <para>
///         <b>
///             Everything goes through <c>ICustomObjectsOperations</c>, including built-in kinds, and
///             that is deliberate.
///         </b>
///         The fabric applies whatever a provider renders — a
///         <c>Deployment</c>, a <c>Service</c>, a <c>Cluster</c> from CloudNativePG — so it needs one
///         code path keyed by group/version/plural rather than one generated method per kind. The
///         custom-objects operations are exactly that path: they are not "for CRDs", they are the
///         untyped path, and the API server does not distinguish.
///     </para>
///     <para>
///         ⚠ <b>Not <c>GenericClient</c>, and this is the constraint that shaped the class.</b>
///         <c>GenericClient.PatchAsync&lt;T&gt;(V1Patch, name, ct)</c> has
///         <b>
///             no <c>fieldManager</c>
///             and no <c>force</c> parameter
///         </b>
///         — in 18.0.13 and in 19.0.2 alike, verified against both
///         assemblies. The API server <i>requires</i> <c>fieldManager</c> on an apply patch, so
///         server-side apply is not expressible through <c>GenericClient</c>. ADR-013 makes a
///         stable per-provider field manager the whole basis of conflict detection, so the choice
///         made itself.
///     </para>
///     <para>
///         <b>How the apply patch is signalled.</b> The body is a <c>V1Patch</c> whose
///         <c>PatchType</c> is <c>ApplyPatch</c>; <c>AbstractKubernetes</c> type-tests the
///         <c>object body</c> parameter at run time and sets
///         <c>Content-Type: application/apply-patch+yaml</c> from it. (JSON is valid YAML, so sending
///         a JSON document under that content type is correct and is what every client does.)
///     </para>
///     <para>
///         ⚠
///         <b>
///             <c>force</c> is passed as <c>command.Force</c>, which the builder pins to
///             <see langword="false" />.
///         </b>
///         Passing <see langword="true" /> would make the API server
///         take ownership of a conflicting field silently — the exact "silent revert" ADR-013 says
///         server-side apply exists to replace with a named drift event.
///     </para>
/// </remarks>
public sealed class KubeApiClient(
    IKubernetes client,
    Guid clusterId,
    IClock clock,
    bool ownsClient = true,
    ILogger? logger = null
) : IKubeApiClient {
    /// <summary>The default page size for an informer's list.</summary>
    public const int DefaultListPageSize = 500;

    /// <inheritdoc />
    public async Task<Result<string>> PingAsync(CancellationToken cancellationToken = default) {
        try {
            using var response = await client.Version
                .GetCodeWithHttpMessagesAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return Result<string>.Success(response.Body?.GitVersion ?? "unknown");
        } catch (HttpOperationException ex) when (!IsTransport(ex)) {
            // ⚠ The cluster ANSWERED, so this must not become "cannot reach your cluster". A version
            // probe that comes back 401 means the kubeconfig has expired or been rotated, which is
            // an operator's job and not a network fault — and ClusterHealth.Message is quoted to the
            // tenant verbatim when a reconcile is suspended, so it is written here rather than
            // copied from the API server.
            var status = ((int?)ex.Response?.StatusCode ?? 0).ToString(CultureInfo.InvariantCulture);

            logger?.LogError(
                "Cluster {Cluster} answered a version probe with HTTP {Status}: {Body}",
                clusterId,
                status,
                ex.Response?.Content ?? "(no body)"
            );

            return Result<string>.Failure(
                ErrorCode.AuthorizationFailed,
                $"Cluster {clusterId:D} is reachable but answered HTTP {status} to a version probe. "
                + "The credentials the platform holds for it are not working; the connection needs "
                + "an operator's attention."
            );
        } catch (Exception ex) when (IsTransport(ex)) {
            return Result<string>.Failure(Unreachable(ex));
        }
    }

    /// <inheritdoc />
    public async Task<Result<KubeObject>> GetAsync(
        ObjectRef target,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(target);

        try {
            using var response = target.IsClusterScoped
                ? await client.CustomObjects.GetClusterCustomObjectWithHttpMessagesAsync(
                        target.Kind.Group,
                        target.Kind.Version,
                        target.Kind.Plural,
                        target.Name,
                        cancellationToken: cancellationToken
                    )
                    .ConfigureAwait(false)
                : await client.CustomObjects.GetNamespacedCustomObjectWithHttpMessagesAsync(
                        target.Kind.Group,
                        target.Kind.Version,
                        target.Namespace,
                        target.Kind.Plural,
                        target.Name,
                        cancellationToken: cancellationToken
                    )
                    .ConfigureAwait(false);

            var json = Serialize(response.Body);
            return Result<KubeObject>.Success(
                new() { Ref = target, Json = json, ResourceVersion = ResourceVersionOf(json) }
            );
        } catch (HttpOperationException ex) {
            // ⚠ The 404 handled here used to be unconditional, and that was the read half of the
            // defect. "The object is absent" and "this cluster does not serve this kind" are both
            // 404, and reporting the second as ResourceNotFound tells ApplyAsync to go ahead and
            // create — against a kind that does not exist. KubeFailures tells them apart.
            return Refuse<KubeObject>(ex, target, "read");
        } catch (Exception ex) when (IsTransport(ex)) {
            return Result<KubeObject>.Failure(Unreachable(ex));
        }
    }

    /// <inheritdoc />
    public async Task<Result<ApplyOutcome>> ApplyAsync(
        KubeCommand command,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(command);

        var target = command.Target;

        // ⚠ A read before the write, and it buys the Created/Updated/Unchanged distinction.
        //
        // An apply PATCH returns 200 whether it created or updated, and it returns the object
        // WITHOUT telling you what it looked like beforehand — so "did anything actually change" is
        // not answerable from the response alone. Reading first gives the before picture, and
        // OurFieldsChanged diffs it against the after through metadata.managedFields.
        //
        // The obvious saving — skip the apply entirely when the object's cybercloud.io/reconcile-hash
        // already matches ours — is deliberately NOT taken, and the reason is worth stating because
        // docs/plan/09 § The command builder calls that annotation "cheap no-op detection" and the
        // shortcut looks free. It is not: a tenant who hand-edits .spec.replicas does not touch our
        // annotation, so the hash still matches, so the shortcut would skip the apply, so the
        // conflict would never be raised and the drift would never be detected. ADR-013's conflict
        // detection only works if we actually apply. The hash therefore reports (in
        // ApplyOutcome.ReconcileHash) rather than decides.
        var existing = await GetAsync(target, cancellationToken).ConfigureAwait(false);

        if (existing.TryGetError(out var readError)
            && readError.Code != ErrorCode.ResourceNotFound) {
            return Result<ApplyOutcome>.Failure(readError);
        }

        var existedBefore = existing.IsSuccess;
        var priorVersion = existing.ValueOrDefault?.ResourceVersion ?? string.Empty;
        var priorJson = existing.ValueOrDefault?.Json;

        object body;
        try {
            body = new V1Patch(
                JsonSerializer.Deserialize<JsonElement>(command.Body),
                V1Patch.PatchType.ApplyPatch
            );
        } catch (JsonException ex) {
            return Result<ApplyOutcome>.Failure(
                ErrorCode.InvalidRequestBody,
                $"The command's body is not valid JSON: {ex.Message}"
            );
        }

        try {
            using var response = target.IsClusterScoped
                ? await client.CustomObjects.PatchClusterCustomObjectWithHttpMessagesAsync(
                        body,
                        target.Kind.Group,
                        target.Kind.Version,
                        target.Kind.Plural,
                        target.Name,
                        fieldManager: command.FieldManager,
                        force: command.Force,
                        cancellationToken: cancellationToken
                    )
                    .ConfigureAwait(false)
                : await client.CustomObjects.PatchNamespacedCustomObjectWithHttpMessagesAsync(
                        body,
                        target.Kind.Group,
                        target.Kind.Version,
                        target.Namespace,
                        target.Kind.Plural,
                        target.Name,
                        fieldManager: command.FieldManager,
                        force: command.Force,
                        cancellationToken: cancellationToken
                    )
                    .ConfigureAwait(false);

            var json = Serialize(response.Body);
            var newVersion = ResourceVersionOf(json);

            var result = !existedBefore
                ? ApplyResult.Created
                : OurFieldsChanged(priorJson, json, command.FieldManager, priorVersion, newVersion)
                    ? ApplyResult.Updated
                    : ApplyResult.Unchanged;

            return Result<ApplyOutcome>.Success(
                new() {
                    Result = result,
                    Target = target,
                    ResourceVersion = newVersion,
                    ReconcileHash = command.ReconcileHash,
                    Message = string.Empty
                }
            );
        } catch (HttpOperationException ex) when (ex.Response?.StatusCode == HttpStatusCode.Conflict) {
            // ⚠ THE ADR-013 PAYOFF, and the one branch that must not be an error.
            //
            // The field is NOT overwritten (force is false), the apply did NOT take effect, and this
            // is reported as a successful call carrying a drift event rather than as a failure —
            // because a failed Result becomes a failed operation and a "provisioning failed" in the
            // portal, and a tenant editing their own cluster is not a provisioning failure. It is
            // drift, it has a name, and docs/plan/08's drift detection is what consumes it.
            var conflicts = ConflictParser.Parse(ex.Response.Content);

            return Result<ApplyOutcome>.Success(
                new() {
                    Result = ApplyResult.Conflict,
                    Target = target,
                    ResourceVersion = priorVersion,
                    ReconcileHash = command.ReconcileHash,
                    Drift = new() {
                        ResourceId = command.ResourceId,
                        ClusterId = clusterId,
                        Target = target,
                        FieldManager = command.FieldManager,
                        Conflicts = conflicts,
                        DetectedAt = clock.UtcNow
                    },
                    Message = conflicts.Count > 0
                        ? $"{conflicts.Count.ToString(CultureInfo.InvariantCulture)} field(s) on {target} "
                        + $"are owned by another field manager and were not overwritten: "
                        + string.Join(", ", conflicts)
                        : "The apply conflicted with another field manager. " + ex.Response.Content
                }
            );
        } catch (HttpOperationException ex) {
            // ⚠ THE OTHER HALF OF IsTransport, and it was missing entirely.
            //
            // Everything the API server refuses that is not a 409 arrived here as a raw
            // k8s.Autorest.HttpOperationException, which is not a serializable type — so it crossed
            // the connection grain's boundary and reached the caller as a CodecNotFoundException
            // naming no status code and no cause. Measured, not predicted: a provider's suite
            // against a bare k3s went red with that exception and no status, because a 404 for a
            // kind whose CRD is not served was mapped by nothing.
            return Refuse<ApplyOutcome>(ex, target, "apply");
        } catch (Exception ex) when (IsTransport(ex)) {
            return Result<ApplyOutcome>.Failure(Unreachable(ex));
        }
    }

    /// <inheritdoc />
    public async Task<Result> DeleteAsync(
        ObjectRef target,
        CascadePolicy policy,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(target);

        var propagation = policy switch {
            CascadePolicy.Foreground => "Foreground",
            CascadePolicy.Orphan => "Orphan",
            _ => "Background"
        };

        try {
            using var response = target.IsClusterScoped
                ? await client.CustomObjects.DeleteClusterCustomObjectWithHttpMessagesAsync(
                        target.Kind.Group,
                        target.Kind.Version,
                        target.Kind.Plural,
                        target.Name,
                        propagationPolicy: propagation,
                        cancellationToken: cancellationToken
                    )
                    .ConfigureAwait(false)
                : await client.CustomObjects.DeleteNamespacedCustomObjectWithHttpMessagesAsync(
                        target.Kind.Group,
                        target.Kind.Version,
                        target.Namespace,
                        target.Kind.Plural,
                        target.Name,
                        propagationPolicy: propagation,
                        cancellationToken: cancellationToken
                    )
                    .ConfigureAwait(false);

            return Result.Success;
        } catch (HttpOperationException ex) {
            // Deleting what is already gone is the desired end state. docs/plan/06 § Two-phase
            // create makes delete idempotent so a retried teardown converges instead of alternating
            // between 404 and success.
            //
            // ⚠ But ONLY when the 404 names the object. This catch used to accept every 404, so a
            // teardown against a cluster that does not serve the kind reported Success and the
            // resource left `Deleting` as converged — a delete that never happened, recorded as one
            // that did. KubeFailures.Classify returns ResourceNotFound for "the object is absent"
            // and InvalidResourceType for "the kind is not served", and only the first is a
            // successful delete.
            var refused = Refuse<KubeObject>(ex, target, "delete");

            return refused.Error!.Code == ErrorCode.ResourceNotFound
                ? Result.Success
                : Result.Failure(refused.Error);
        } catch (Exception ex) when (IsTransport(ex)) {
            return Result.Failure(Unreachable(ex));
        }
    }

    /// <inheritdoc />
    public async Task<Result<ListPage>> ListAsync(
        GroupVersionKind kind,
        string ns,
        string labelSelector,
        string? resourceVersion = null,
        string? continueToken = null,
        int? limit = null,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(kind);

        try {
            using var response = string.IsNullOrEmpty(ns)
                ? await client.CustomObjects.ListClusterCustomObjectWithHttpMessagesAsync(
                        kind.Group,
                        kind.Version,
                        kind.Plural,
                        continueParameter: continueToken,
                        labelSelector: labelSelector,
                        limit: limit ?? DefaultListPageSize,
                        resourceVersion: resourceVersion,
                        cancellationToken: cancellationToken
                    )
                    .ConfigureAwait(false)
                : await client.CustomObjects.ListNamespacedCustomObjectWithHttpMessagesAsync(
                        kind.Group,
                        kind.Version,
                        ns,
                        kind.Plural,
                        continueParameter: continueToken,
                        labelSelector: labelSelector,
                        limit: limit ?? DefaultListPageSize,
                        resourceVersion: resourceVersion,
                        cancellationToken: cancellationToken
                    )
                    .ConfigureAwait(false);

            return Result<ListPage>.Success(ReadPage(Serialize(response.Body)));
        } catch (HttpOperationException ex) when (ex.Response?.StatusCode == HttpStatusCode.Gone) {
            // ⚠ HTTP 410 — the resourceVersion we resumed from has been compacted out of etcd's
            // history. docs/plan/09 § Observing bounds the resume with exactly this caveat: "resume
            // from the last resourceVersion WHERE THE API SERVER STILL HAS IT". This is that
            // boundary, and SharedInformer's response is a full re-list.
            return Result<ListPage>.Failure(
                ErrorCode.PreconditionFailed,
                $"The API server no longer has resourceVersion '{resourceVersion}' for {kind} — it "
                + "has been compacted. The informer must fall back to a full list. This is the "
                + "expected outcome of a resume that arrives too late, not a fault."
            );
        } catch (HttpOperationException ex) {
            // A list of a kind the cluster does not serve is the informer's version of the same
            // defect: it threw out of EstablishAsync instead of naming the kind.
            return Refuse<ListPage>(ex, new() { Kind = kind, Namespace = ns, Name = "*" }, "list");
        } catch (Exception ex) when (IsTransport(ex)) {
            return Result<ListPage>.Failure(Unreachable(ex));
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<KubeWatchEvent> WatchAsync(
        GroupVersionKind kind,
        string ns,
        string labelSelector,
        string resourceVersion,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(kind);

        // ⚠ allowWatchBookmarks: true is not an optimisation, it is what keeps a resume viable on a
        // quiet cluster — see the remarks on KubeWatchEventKind.Bookmark.
        var response = await client.CustomObjects
            .ListNamespacedCustomObjectWithHttpMessagesAsync<object>(
                kind.Group,
                kind.Version,
                ns,
                kind.Plural,
                true,
                labelSelector: labelSelector,
                resourceVersion: resourceVersion,
                watch: true,
                cancellationToken: cancellationToken
            )
            .ConfigureAwait(false);

        using (response) {
            var stream = await response.Response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);

            using var reader = new StreamReader(stream);

            // ⚠ Read the frames directly rather than through WatcherExt.
            //
            // A watch response is newline-delimited JSON, one {"type":…,"object":…} per line, and
            // that is a stable part of the Kubernetes API rather than a client detail. Reading it
            // here costs a dozen lines and avoids two problems with the library's helpers: they are
            // generic over a typed list/item pair (`Watch<T, L>`), which an untyped fabric does not
            // have, and the WatcherExt overloads are marked [Obsolete] as of 18.0.4 with a
            // replacement that only exists for the strongly-typed ClientSets path. Depending on an
            // obsolete generic helper to do something we can do exactly is the worse trade.
            while (!cancellationToken.IsCancellationRequested) {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null) {
                    yield break;
                }

                if (line.Length == 0) {
                    continue;
                }

                var parsed = ReadFrame(line);
                if (parsed is not null) {
                    yield return parsed;
                }
            }
        }
    }

    /// <inheritdoc />
    public void Dispose() {
        if (ownsClient) {
            client.Dispose();
        }
    }

    /// <summary>
    ///     Whether the apply changed a field we own — the question
    ///     <see cref="ApplyResult.Updated" /> is defined by.
    /// </summary>
    /// <param name="before">The object as it was read, or <see langword="null" /> if it was absent.</param>
    /// <param name="after">The object the apply returned.</param>
    /// <param name="fieldManager">Our manager, whose <c>managedFields</c> entry says what "we own" means.</param>
    /// <param name="priorVersion">The <c>resourceVersion</c> before the apply, for the fallback.</param>
    /// <param name="newVersion">The <c>resourceVersion</c> after it.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The <c>resourceVersion</c> comparison this replaced answered a different
    ///         question</b> — "did anyone write between our read and our write" — and any other
    ///         writer inside that window made it wrong. Against a real k3s on an idle machine,
    ///         creating a <c>Deployment</c> and immediately re-applying the identical body reported
    ///         <see cref="ApplyResult.Updated" /> 4 times in 20, because the deployment controller
    ///         writes <c>status</c> in the window and a status write bumps the object's single shared
    ///         <c>resourceVersion</c>. The same body re-applied after a settle was
    ///         <see cref="ApplyResult.Unchanged" />, so the apply itself was always a genuine no-op.
    ///         <see cref="OwnedFieldSnapshot" /> carries the reasoning and the traps in the two
    ///         narrower fixes.
    ///     </para>
    ///     <para>
    ///         <b>The fallback is still <c>resourceVersion</c></b>, for the case where field
    ///         management cannot be read off either body. That is the old behaviour, flakiness
    ///         included, and it is the right floor: it over-reports
    ///         <see cref="ApplyResult.Updated" />, never under-reports it, so a consumer counting
    ///         convergence retries rather than declaring victory early.
    ///     </para>
    /// </remarks>
    static bool OurFieldsChanged(
        string? before,
        string after,
        string fieldManager,
        string priorVersion,
        string newVersion
    ) =>
        OwnedFieldSnapshot.TryCapture(before, fieldManager, out var was)
        && OwnedFieldSnapshot.TryCapture(after, fieldManager, out var now)
            ? !string.Equals(was, now, StringComparison.Ordinal)
            : !string.Equals(newVersion, priorVersion, StringComparison.Ordinal);

    static KubeWatchEvent? ReadFrame(string line) {
        try {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;

            var type = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()
                : null;

            var kind = type switch {
                "ADDED" => KubeWatchEventKind.Added,
                "MODIFIED" => KubeWatchEventKind.Modified,
                "DELETED" => KubeWatchEventKind.Deleted,
                "BOOKMARK" => KubeWatchEventKind.Bookmark,
                "ERROR" => KubeWatchEventKind.Error,
                _ => KubeWatchEventKind.Unknown
            };

            if (!root.TryGetProperty("object", out var obj)) {
                return null;
            }

            var json = obj.GetRawText();
            return new(kind, json, ResourceVersionOf(json));
        } catch (JsonException) {
            return null;
        }
    }

    // ── JSON helpers ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Renders the untyped body the custom-objects operations return.
    /// </summary>
    /// <remarks>
    ///     ⚠ The declared type is <see cref="object" />, and what actually arrives is a
    ///     <see cref="JsonElement" /> — the client deserializes an untyped body with
    ///     <c>System.Text.Json</c> and hands back the DOM node. Re-serializing rather than assuming
    ///     the runtime type keeps this correct if that ever changes.
    /// </remarks>
    static string Serialize(object? body) =>
        body switch {
            null => "{}",
            JsonElement element => element.GetRawText(),
            string text => text,
            _ => JsonSerializer.Serialize(body)
        };

    static string ResourceVersionOf(string json) {
        try {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("metadata", out var metadata)
                && metadata.TryGetProperty("resourceVersion", out var version)
                && version.ValueKind == JsonValueKind.String
                    ? version.GetString() ?? string.Empty
                    : string.Empty;
        } catch (JsonException) {
            return string.Empty;
        }
    }

    static ListPage ReadPage(string json) {
        List<string> items = [];
        var resourceVersion = string.Empty;
        var continueToken = string.Empty;

        try {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.TryGetProperty("metadata", out var metadata)) {
                if (metadata.TryGetProperty("resourceVersion", out var version)
                    && version.ValueKind == JsonValueKind.String) {
                    resourceVersion = version.GetString() ?? string.Empty;
                }

                if (metadata.TryGetProperty("continue", out var cont)
                    && cont.ValueKind == JsonValueKind.String) {
                    continueToken = cont.GetString() ?? string.Empty;
                }
            }

            if (root.TryGetProperty("items", out var array) && array.ValueKind == JsonValueKind.Array) {
                foreach (var item in array.EnumerateArray()) {
                    items.Add(item.GetRawText());
                }
            }
        } catch (JsonException) {
            // A list body that will not parse is not a partial list — it is nothing. Returning an
            // empty page with no cursor makes the informer re-list rather than advance past objects
            // it never saw.
            return new([], string.Empty, string.Empty);
        }

        return new(items, resourceVersion, continueToken);
    }

    // ── Failure classification ─────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Reports an answer the API server refused to act on, logging the operator's copy and
    ///     returning the tenant's.
    /// </summary>
    /// <typeparam name="T">The value the caller wanted.</typeparam>
    /// <param name="exception">What the client threw.</param>
    /// <param name="target">The object the call addressed.</param>
    /// <param name="verb">What was attempted — <c>apply</c>, <c>read</c>, <c>delete</c>, <c>list</c>.</param>
    /// <remarks>
    ///     <para>
    ///         The two strings part company here: <see cref="KubeRefusal.OperatorDetail" /> goes to
    ///         the log with the API server's full message and every cause, and
    ///         <see cref="KubeRefusal.TenantMessage" /> goes into the <see cref="Result" /> that the
    ///         portal and <c>ResourceSnapshot.LastFailure</c> quote. See <see cref="KubeFailures" />
    ///         for which half of the API server's text ends up in which.
    ///     </para>
    ///     <para>
    ///         ⚠ The <see langword="null" /> branch is not a gap. <see cref="KubeFailures.Classify" />
    ///         declines exactly the statuses <see cref="IsTransport" /> claims — no response, a
    ///         <c>408</c>, a <c>429</c>, and any other 5xx — so "unclassified" and "transport" are
    ///         the same set and nothing falls between them. That is what makes it safe for the
    ///         callers to catch <see cref="HttpOperationException" /> ahead of the transport filter.
    ///     </para>
    /// </remarks>
    Result<T> Refuse<T>(HttpOperationException exception, ObjectRef target, string verb)
        where T : notnull {
        var refusal = KubeFailures.Classify(exception, target, clusterId, verb);

        if (refusal is null) {
            return Result<T>.Failure(Unreachable(exception));
        }

        // ⚠ A missing object is not an incident. It is what every create reads before it writes and
        // what every re-driven delete finds, so logging it would put an error line on the happy
        // path and bury the refusals that matter.
        if (refusal.Code != ErrorCode.ResourceNotFound) {
            logger?.LogError("{Detail}", refusal.OperatorDetail);
        }

        return Result<T>.Failure(refusal.Code, refusal.TenantMessage);
    }

    /// <summary>
    ///     Whether an exception means "the cluster did not answer" rather than "we sent nonsense".
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ This predicate is the machinery behind docs/plan/09 § Cluster connections' central
    ///         distinction — <i>our</i> failure versus <i>unreachable</i>. It is deliberately broad on
    ///         the transport side (any <see cref="HttpRequestException" />, any timeout, any TLS or
    ///         socket failure, and any 5xx) and it deliberately does <b>not</b> swallow 4xx other than
    ///         the ones handled explicitly above, because a 400 or a 422 is us sending a malformed
    ///         object and must not be reported to a tenant as "cannot reach your cluster".
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The "any 5xx" arm is wrong for one measured case and <see cref="KubeFailures" />
    ///         carves it out before this predicate is consulted.</b> An apply whose body will not
    ///         type-check — <c>.spec.replicas: "lots"</c> — comes back <b>500</b>, not 400:
    ///         <c>failed to create typed patch object (…): expected numeric (int or float), got
    ///         string</c>. Left to this predicate it reports as "cluster … did not answer", which is
    ///         the exact outcome the paragraph above promises cannot happen. Verified against k3s
    ///         1.35 in <c>KubeFailureMappingTests</c>.
    ///     </para>
    /// </remarks>
    static bool IsTransport(Exception ex) =>
        ex switch {
            HttpOperationException http => http.Response is null
                || (int)http.Response.StatusCode >= 500
                || http.Response.StatusCode == HttpStatusCode.RequestTimeout
                || http.Response.StatusCode == HttpStatusCode.TooManyRequests,
            HttpRequestException => true,
            TaskCanceledException => true,
            TimeoutException => true,
            SocketException => true,
            AuthenticationException => true,
            KubernetesException => true,
            _ => false
        };

    Error Unreachable(Exception ex) =>
        new(
            ErrorCode.InternalError,
            $"Cluster {clusterId:D} did not answer: {ex.GetType().Name}: {ex.Message}"
        );
}
