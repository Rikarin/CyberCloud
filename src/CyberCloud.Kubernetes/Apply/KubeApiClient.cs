using System.Globalization;
using System.Net;
using System.Text.Json;
using CyberCloud.Core.Time;
using k8s;
using k8s.Autorest;
using k8s.Models;

namespace CyberCloud.Kubernetes.Apply;

/// <summary>
///     <see cref="IKubeApiClient" /> over <c>KubernetesClient</c> 19.0.2 — server-side apply, reads,
///     deletes and lists against one cluster.
/// </summary>
/// <remarks>
///     <para>
///         <b>Everything goes through <c>ICustomObjectsOperations</c>, including built-in kinds, and
///         that is deliberate.</b> The fabric applies whatever a provider renders — a
///         <c>Deployment</c>, a <c>Service</c>, a <c>Cluster</c> from CloudNativePG — so it needs one
///         code path keyed by group/version/plural rather than one generated method per kind. The
///         custom-objects operations are exactly that path: they are not "for CRDs", they are the
///         untyped path, and the API server does not distinguish.
///     </para>
///     <para>
///         ⚠ <b>Not <c>GenericClient</c>, and this is the constraint that shaped the class.</b>
///         <c>GenericClient.PatchAsync&lt;T&gt;(V1Patch, name, ct)</c> has <b>no <c>fieldManager</c>
///         and no <c>force</c> parameter</b> — in 18.0.13 and in 19.0.2 alike, verified against both
///         assemblies. The API server <i>requires</i> <c>fieldManager</c> on an apply patch, so
///         server-side apply is simply not expressible through <c>GenericClient</c>. ADR-013 makes a
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
///         ⚠ <b><c>force</c> is passed as <c>command.Force</c>, which the builder pins to
///         <see langword="false" />.</b> Passing <see langword="true" /> would make the API server
///         take ownership of a conflicting field silently — the exact "silent revert" ADR-013 says
///         server-side apply exists to replace with a named drift event.
///     </para>
/// </remarks>
public sealed class KubeApiClient(IKubernetes client, Guid clusterId, IClock clock, bool ownsClient = true)
    : IKubeApiClient
{
    /// <summary>The default page size for an informer's list.</summary>
    public const int DefaultListPageSize = 500;

    /// <inheritdoc />
    public async Task<Result<string>> PingAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await client.Version
                .GetCodeWithHttpMessagesAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return Result<string>.Success(response.Body?.GitVersion ?? "unknown");
        }
        catch (Exception ex) when (IsTransport(ex))
        {
            return Result<string>.Failure(Unreachable(ex));
        }
    }

    /// <inheritdoc />
    public async Task<Result<KubeObject>> GetAsync(
        ObjectRef target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);

        try
        {
            using var response = target.IsClusterScoped
                ? await client.CustomObjects.GetClusterCustomObjectWithHttpMessagesAsync(
                    target.Kind.Group,
                    target.Kind.Version,
                    target.Kind.Plural,
                    target.Name,
                    cancellationToken: cancellationToken).ConfigureAwait(false)
                : await client.CustomObjects.GetNamespacedCustomObjectWithHttpMessagesAsync(
                    target.Kind.Group,
                    target.Kind.Version,
                    target.Namespace,
                    target.Kind.Plural,
                    target.Name,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

            var json = Serialize(response.Body);
            return Result<KubeObject>.Success(new KubeObject
            {
                Ref = target,
                Json = json,
                ResourceVersion = ResourceVersionOf(json),
            });
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode == HttpStatusCode.NotFound)
        {
            return Result<KubeObject>.Failure(
                ErrorCode.ResourceNotFound,
                $"{target} does not exist in cluster {clusterId:D}.");
        }
        catch (Exception ex) when (IsTransport(ex))
        {
            return Result<KubeObject>.Failure(Unreachable(ex));
        }
    }

    /// <inheritdoc />
    public async Task<Result<ApplyOutcome>> ApplyAsync(
        KubeCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var target = command.Target;

        // ⚠ A read before the write, and it buys the Created/Updated/Unchanged distinction.
        //
        // An apply PATCH returns 200 whether it created or updated, and it returns the object
        // WITHOUT telling you what the resourceVersion was beforehand — so "did anything actually
        // change" is not answerable from the response alone. Reading first gives the prior cursor;
        // an unchanged resourceVersion afterwards is the API server saying the apply was a no-op.
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
            && readError.Code != ErrorCode.ResourceNotFound)
        {
            return Result<ApplyOutcome>.Failure(readError);
        }

        var existedBefore = existing.IsSuccess;
        var priorVersion = existing.ValueOrDefault?.ResourceVersion ?? string.Empty;

        object body;
        try
        {
            body = new V1Patch(
                JsonSerializer.Deserialize<JsonElement>(command.Body),
                V1Patch.PatchType.ApplyPatch);
        }
        catch (JsonException ex)
        {
            return Result<ApplyOutcome>.Failure(
                ErrorCode.InvalidRequestBody,
                $"The command's body is not valid JSON: {ex.Message}");
        }

        try
        {
            using var response = target.IsClusterScoped
                ? await client.CustomObjects.PatchClusterCustomObjectWithHttpMessagesAsync(
                    body,
                    target.Kind.Group,
                    target.Kind.Version,
                    target.Kind.Plural,
                    target.Name,
                    fieldManager: command.FieldManager,
                    force: command.Force,
                    cancellationToken: cancellationToken).ConfigureAwait(false)
                : await client.CustomObjects.PatchNamespacedCustomObjectWithHttpMessagesAsync(
                    body,
                    target.Kind.Group,
                    target.Kind.Version,
                    target.Namespace,
                    target.Kind.Plural,
                    target.Name,
                    fieldManager: command.FieldManager,
                    force: command.Force,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

            var json = Serialize(response.Body);
            var newVersion = ResourceVersionOf(json);

            var result = !existedBefore
                ? ApplyResult.Created
                : string.Equals(newVersion, priorVersion, StringComparison.Ordinal)
                    ? ApplyResult.Unchanged
                    : ApplyResult.Updated;

            return Result<ApplyOutcome>.Success(new ApplyOutcome
            {
                Result = result,
                Target = target,
                ResourceVersion = newVersion,
                ReconcileHash = command.ReconcileHash,
                Message = string.Empty,
            });
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode == HttpStatusCode.Conflict)
        {
            // ⚠ THE ADR-013 PAYOFF, and the one branch that must not be an error.
            //
            // The field is NOT overwritten (force is false), the apply did NOT take effect, and this
            // is reported as a successful call carrying a drift event rather than as a failure —
            // because a failed Result becomes a failed operation and a "provisioning failed" in the
            // portal, and a tenant editing their own cluster is not a provisioning failure. It is
            // drift, it has a name, and docs/plan/08's drift detection is what consumes it.
            var conflicts = ConflictParser.Parse(ex.Response.Content);

            return Result<ApplyOutcome>.Success(new ApplyOutcome
            {
                Result = ApplyResult.Conflict,
                Target = target,
                ResourceVersion = priorVersion,
                ReconcileHash = command.ReconcileHash,
                Drift = new DriftEvent
                {
                    ResourceId = command.ResourceId,
                    ClusterId = clusterId,
                    Target = target,
                    FieldManager = command.FieldManager,
                    Conflicts = conflicts,
                    DetectedAt = clock.UtcNow,
                },
                Message = conflicts.Count > 0
                    ? $"{conflicts.Count.ToString(CultureInfo.InvariantCulture)} field(s) on {target} "
                        + $"are owned by another field manager and were not overwritten: "
                        + string.Join(", ", conflicts)
                    : "The apply conflicted with another field manager. " + ex.Response.Content,
            });
        }
        catch (Exception ex) when (IsTransport(ex))
        {
            return Result<ApplyOutcome>.Failure(Unreachable(ex));
        }
    }

    /// <inheritdoc />
    public async Task<Result> DeleteAsync(
        ObjectRef target,
        CascadePolicy policy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);

        var propagation = policy switch
        {
            CascadePolicy.Foreground => "Foreground",
            CascadePolicy.Orphan => "Orphan",
            _ => "Background",
        };

        try
        {
            using var response = target.IsClusterScoped
                ? await client.CustomObjects.DeleteClusterCustomObjectWithHttpMessagesAsync(
                    target.Kind.Group,
                    target.Kind.Version,
                    target.Kind.Plural,
                    target.Name,
                    propagationPolicy: propagation,
                    cancellationToken: cancellationToken).ConfigureAwait(false)
                : await client.CustomObjects.DeleteNamespacedCustomObjectWithHttpMessagesAsync(
                    target.Kind.Group,
                    target.Kind.Version,
                    target.Namespace,
                    target.Kind.Plural,
                    target.Name,
                    propagationPolicy: propagation,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

            return Result.Success;
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode == HttpStatusCode.NotFound)
        {
            // Deleting what is already gone is the desired end state. docs/plan/06 § Two-phase
            // create makes delete idempotent so a retried teardown converges instead of alternating
            // between 404 and success.
            return Result.Success;
        }
        catch (Exception ex) when (IsTransport(ex))
        {
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
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(kind);

        try
        {
            using var response = string.IsNullOrEmpty(ns)
                ? await client.CustomObjects.ListClusterCustomObjectWithHttpMessagesAsync(
                    kind.Group,
                    kind.Version,
                    kind.Plural,
                    continueParameter: continueToken,
                    labelSelector: labelSelector,
                    limit: limit ?? DefaultListPageSize,
                    resourceVersion: resourceVersion,
                    cancellationToken: cancellationToken).ConfigureAwait(false)
                : await client.CustomObjects.ListNamespacedCustomObjectWithHttpMessagesAsync(
                    kind.Group,
                    kind.Version,
                    ns,
                    kind.Plural,
                    continueParameter: continueToken,
                    labelSelector: labelSelector,
                    limit: limit ?? DefaultListPageSize,
                    resourceVersion: resourceVersion,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

            return Result<ListPage>.Success(ReadPage(Serialize(response.Body)));
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode == HttpStatusCode.Gone)
        {
            // ⚠ HTTP 410 — the resourceVersion we resumed from has been compacted out of etcd's
            // history. docs/plan/09 § Observing bounds the resume with exactly this caveat: "resume
            // from the last resourceVersion WHERE THE API SERVER STILL HAS IT". This is that
            // boundary, and SharedInformer's response is a full re-list.
            return Result<ListPage>.Failure(
                ErrorCode.PreconditionFailed,
                $"The API server no longer has resourceVersion '{resourceVersion}' for {kind} — it "
                + "has been compacted. The informer must fall back to a full list. This is the "
                + "expected outcome of a resume that arrives too late, not a fault.");
        }
        catch (Exception ex) when (IsTransport(ex))
        {
            return Result<ListPage>.Failure(Unreachable(ex));
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<KubeWatchEvent> WatchAsync(
        GroupVersionKind kind,
        string ns,
        string labelSelector,
        string resourceVersion,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(kind);

        // ⚠ allowWatchBookmarks: true is not an optimisation, it is what keeps a resume viable on a
        // quiet cluster — see the remarks on KubeWatchEventKind.Bookmark.
        var response = await client.CustomObjects
            .ListNamespacedCustomObjectWithHttpMessagesAsync<object>(
                kind.Group,
                kind.Version,
                ns,
                kind.Plural,
                allowWatchBookmarks: true,
                labelSelector: labelSelector,
                resourceVersion: resourceVersion,
                watch: true,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        using (response)
        {
            var stream = await response.Response.Content
                .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

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
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    yield break;
                }

                if (line.Length == 0)
                {
                    continue;
                }

                var parsed = ReadFrame(line);
                if (parsed is not null)
                {
                    yield return parsed;
                }
            }
        }
    }

    static KubeWatchEvent? ReadFrame(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;

            var type = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()
                : null;

            var kind = type switch
            {
                "ADDED" => KubeWatchEventKind.Added,
                "MODIFIED" => KubeWatchEventKind.Modified,
                "DELETED" => KubeWatchEventKind.Deleted,
                "BOOKMARK" => KubeWatchEventKind.Bookmark,
                "ERROR" => KubeWatchEventKind.Error,
                _ => KubeWatchEventKind.Unknown,
            };

            if (!root.TryGetProperty("object", out var obj))
            {
                return null;
            }

            var json = obj.GetRawText();
            return new KubeWatchEvent(kind, json, ResourceVersionOf(json));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (ownsClient)
        {
            client.Dispose();
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
    static string Serialize(object? body) => body switch
    {
        null => "{}",
        JsonElement element => element.GetRawText(),
        string text => text,
        _ => JsonSerializer.Serialize(body),
    };

    static string ResourceVersionOf(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("metadata", out var metadata)
                && metadata.TryGetProperty("resourceVersion", out var version)
                && version.ValueKind == JsonValueKind.String
                    ? version.GetString() ?? string.Empty
                    : string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    static ListPage ReadPage(string json)
    {
        List<string> items = [];
        var resourceVersion = string.Empty;
        var continueToken = string.Empty;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.TryGetProperty("metadata", out var metadata))
            {
                if (metadata.TryGetProperty("resourceVersion", out var version)
                    && version.ValueKind == JsonValueKind.String)
                {
                    resourceVersion = version.GetString() ?? string.Empty;
                }

                if (metadata.TryGetProperty("continue", out var cont)
                    && cont.ValueKind == JsonValueKind.String)
                {
                    continueToken = cont.GetString() ?? string.Empty;
                }
            }

            if (root.TryGetProperty("items", out var array) && array.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in array.EnumerateArray())
                {
                    items.Add(item.GetRawText());
                }
            }
        }
        catch (JsonException)
        {
            // A list body that will not parse is not a partial list — it is nothing. Returning an
            // empty page with no cursor makes the informer re-list rather than advance past objects
            // it never saw.
            return new ListPage([], string.Empty, string.Empty);
        }

        return new ListPage(items, resourceVersion, continueToken);
    }

    // ── Failure classification ─────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Whether an exception means "the cluster did not answer" rather than "we sent nonsense".
    /// </summary>
    /// <remarks>
    ///     ⚠ This predicate is the machinery behind docs/plan/09 § Cluster connections' central
    ///     distinction — <i>our</i> failure versus <i>unreachable</i>. It is deliberately broad on
    ///     the transport side (any <see cref="HttpRequestException" />, any timeout, any TLS or
    ///     socket failure, and any 5xx) and it deliberately does <b>not</b> swallow 4xx other than
    ///     the ones handled explicitly above, because a 400 or a 422 is us sending a malformed
    ///     object and must not be reported to a tenant as "cannot reach your cluster".
    /// </remarks>
    static bool IsTransport(Exception ex) => ex switch
    {
        HttpOperationException http => http.Response is null
            || (int)http.Response.StatusCode >= 500
            || http.Response.StatusCode == HttpStatusCode.RequestTimeout
            || http.Response.StatusCode == HttpStatusCode.TooManyRequests,
        HttpRequestException => true,
        TaskCanceledException => true,
        TimeoutException => true,
        System.Net.Sockets.SocketException => true,
        System.Security.Authentication.AuthenticationException => true,
        KubernetesException => true,
        _ => false,
    };

    Error Unreachable(Exception ex) => new(
        ErrorCode.InternalError,
        $"Cluster {clusterId:D} did not answer: {ex.GetType().Name}: {ex.Message}");
}
