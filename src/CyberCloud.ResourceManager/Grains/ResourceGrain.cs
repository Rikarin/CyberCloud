using CyberCloud.Core.Time;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.ResourceManager.Grains;

/// <summary>
///     <see cref="IResourceGrain" /> — Entity, Durable, key <c>res/{resourceId:N}</c>.
/// </summary>
/// <remarks>
///     ⚠ <b>This grain never provisions inline.</b> docs/plan/08 § The reconcile loop: <i>"The
///     resource grain never provisions inline. It records intent and returns; a reminder drives
///     convergence."</i> Nothing here calls a reconciler, reaches a cluster, or awaits anything but
///     its own storage — which is what keeps a <c>PUT</c> a sub-millisecond write rather than a
///     four-minute request.
/// </remarks>
public sealed class ResourceGrain(
    [PersistentState("resource", StorageTiers.Durable)] IPersistentState<ResourceState> state,
    IClock clock
)
    : Grain, IResourceGrain {
    /// <summary>
    ///     The tag cap of docs/plan/06 § Tags, locks — 50 pairs.
    /// </summary>
    /// <remarks>
    ///     ⚠ <see cref="Contracts.Registry.TagRules.MaxTags" />' value rather than a second 50. The emitted OpenAPI
    ///     document publishes the cap as <c>maxProperties</c>, so a number written twice would be a
    ///     published contract and an enforced limit that could silently disagree.
    /// </remarks>
    public const int MaxTags = Contracts.Registry.TagRules.MaxTags;

    Guid resourceId;

    /// <inheritdoc />
    public override Task OnActivateAsync(CancellationToken cancellationToken) {
        _ = ResourceManagerGrainKeys.TenantOf(this);
        resourceId = ResourceManagerGrainKeys.Decode(this, GrainKeyKind.Resource).Id;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<Result<ResourceSnapshot>> SubmitDesiredAsync(DesiredSubmission submission) {
        ArgumentNullException.ThrowIfNull(submission);

        var address = ResourceId.ParsePath(submission.Path);
        if (address.TryGetError(out var pathError)) {
            return Result<ResourceSnapshot>.Failure(pathError);
        }

        if (state.State.Lock == LockLevel.ReadOnly) {
            return Result<ResourceSnapshot>.Failure(
                ErrorCode.ScopeLocked,
                $"'{submission.Path}' carries a ReadOnly lock, so writes are refused. Remove the lock "
                + "to change it — docs/plan/06 § Tags, locks, and the small stuff that is not small."
            );
        }

        if (submission.IfMatch.Length > 0
            && !string.Equals(submission.IfMatch, state.State.Etag, StringComparison.Ordinal)) {
            return Result<ResourceSnapshot>.Failure(
                ErrorCode.PreconditionFailed,
                $"The If-Match etag '{submission.IfMatch}' does not match the resource's current etag "
                + $"'{state.State.Etag}'. Something changed since the copy you are editing was read."
            );
        }

        // Another live operation on the same resource is a conflict rather than a queue: two
        // reconcilers driving one resource towards two shapes is exactly the race a single writer
        // exists to prevent. A retry of the SAME operation is not a conflict — that is the resumable
        // path.
        if (state.State.OperationId != Guid.Empty
            && state.State.OperationId != submission.OperationId
            && state.State.ProvisioningState
                is ProvisioningState.Creating or ProvisioningState.Updating or ProvisioningState.Deleting) {
            return Result<ResourceSnapshot>.Failure(
                ErrorCode.OperationInProgress,
                $"Operation {state.State.OperationId:D} is already driving '{submission.Path}' and it "
                + $"is {state.State.ProvisioningState}. Poll that operation, or cancel it, before "
                + "submitting another change."
            );
        }

        var incoming = Parse(submission.Body);
        if (incoming.TryGetError(out var bodyError)) {
            return Result<ResourceSnapshot>.Failure(bodyError);
        }

        var tagProblem = CheckTags(submission.Tags);
        if (tagProblem is not null) {
            return Result<ResourceSnapshot>.Failure(tagProblem);
        }

        var superset = Parse(state.State.Superset).GetValueOrThrow();
        var declared = submission.DeclaredPointers;

        // ⚠ The write applies `declared` and the response projects `readable`, and the two differ by
        // exactly the Secret properties. Replacing the slice needs every pointer or a PUT would drop
        // what it did not list; the response needs the filtered one or a secret the caller just sent
        // comes straight back out. Empty means "not supplied", which falls back to declared.
        var readable = submission.ReadablePointers.IsDefaultOrEmpty ? declared : submission.ReadablePointers;

        // ⚠ The verb's whole meaning is these two branches.
        //
        // PUT is a full replacement OF THIS VERSION'S SLICE — every pointer the version declares is
        // removed first and then written from the body, so a property the caller omitted is gone.
        // It is *not* a replacement of the superset: a newer version's fields are not this caller's
        // to delete, and dropping them would make an old-version PUT a data-loss event.
        //
        // PATCH is RFC 7386 JSON Merge Patch and merges: an absent property is left alone and an
        // explicit null removes one. That asymmetry is why PUT is idempotent and PATCH is not.
        var next = submission.Verb == WriteVerb.Put
            ? ReplaceSlice(superset, declared, incoming.GetValueOrThrow())
            : MergePatch(superset, incoming.GetValueOrThrow());

        // ⚠ Canonical, so that "identical" means identical in meaning rather than in member order —
        // see JsonCanonical. The stored superset is canonical too, which is what lets the comparison
        // below be plain string equality and what keeps DesiredHash stable across writes.
        var nextJson = JsonCanonical.Of(next).ToJsonString();
        var tagsUnchanged = TagsEqual(state.State.Tags, submission.Tags);

        // ⚠ THE NO-OP BRANCH. docs/plan/06 § Two-phase create: "the caller retries the PUT — which is
        // idempotent because PUT with the same body on an existing resource is a no-op, which is
        // exactly why the API is PUT and not POST." The comparison is over the serialized superset,
        // which JsonNode writes in insertion order — and ReplaceSlice rebuilds that order from the
        // schema's pointer list rather than from the request, so a body whose properties arrived in a
        // different order still compares equal. Without that, "idempotent" would depend on the
        // client's JSON serializer.
        if (state.State.Exists
            && state.State.ProvisioningState is ProvisioningState.Succeeded
            && string.Equals(state.State.Superset, nextJson, StringComparison.Ordinal)
            && tagsUnchanged) {
            return Result<ResourceSnapshot>.Success(Snapshot(submission.ApiVersion, readable));
        }

        var now = clock.UtcNow;
        var creating = !state.State.Exists;

        state.State.Path = submission.Path;
        state.State.ApiVersion = submission.ApiVersion;
        state.State.Superset = nextJson;
        state.State.ProvisioningState = creating ? ProvisioningState.Creating : ProvisioningState.Updating;
        state.State.OperationId = submission.OperationId;
        state.State.LastFailure = string.Empty;
        state.State.Location = submission.Location.Length > 0 ? submission.Location : state.State.Location;
        state.State.ClusterId = submission.ClusterId != Guid.Empty ? submission.ClusterId : state.State.ClusterId;
        state.State.ModifiedBy = submission.Caller.ToString();
        state.State.ModifiedAt = now;
        state.State.Version++;
        state.State.Etag = NextEtag();

        if (!tagsUnchanged) {
            state.State.Tags = new(submission.Tags, StringComparer.Ordinal);
        }

        if (creating) {
            state.State.CreatedBy = state.State.ModifiedBy;
            state.State.CreatedAt = now;
        }

        await state.WriteStateAsync();
        return Result<ResourceSnapshot>.Success(Snapshot(submission.ApiVersion, readable));
    }

    /// <inheritdoc />
    public Task<Result<ResourceSnapshot>> GetAsync(string apiVersion, ImmutableArray<string> declaredPointers) {
        if (!state.State.Exists) {
            return Task.FromResult(NotFound<ResourceSnapshot>());
        }

        return Task.FromResult(Result<ResourceSnapshot>.Success(Snapshot(apiVersion, declaredPointers)));
    }

    /// <inheritdoc />
    public async Task<Result<ResourceSnapshot>> BeginDeleteAsync(Guid operationId, string ifMatch) {
        if (!state.State.Exists) {
            return NotFound<ResourceSnapshot>();
        }

        if (state.State.Lock is LockLevel.CanNotDelete or LockLevel.ReadOnly) {
            return Result<ResourceSnapshot>.Failure(
                ErrorCode.ScopeLocked,
                $"'{state.State.Path}' carries a {state.State.Lock} lock, so it cannot be deleted. "
                + "That is what the lock is for — docs/plan/06 § Tags, locks, and the small stuff "
                + "that is not small."
            );
        }

        if (ifMatch.Length > 0 && !string.Equals(ifMatch, state.State.Etag, StringComparison.Ordinal)) {
            return Result<ResourceSnapshot>.Failure(
                ErrorCode.PreconditionFailed,
                $"The If-Match etag '{ifMatch}' does not match '{state.State.Etag}'."
            );
        }

        // ⚠ ADDED BY THE CONFORMANCE SUITE. docs/plan/03 § Providers lists "delete while an operation
        // is running → 409" among the things every provider must pass, and this grain was accepting
        // the delete instead: SubmitDesiredAsync had the single-writer guard and BeginDeleteAsync did
        // not, so a DELETE arriving mid-create flipped a Creating resource straight to Deleting while
        // the create's reconcile pass was still applying objects. The teardown and the create then
        // raced on the same objects, which is the exact race the guard on the write side exists to
        // prevent — and it left the create's quota lease and index claim owned by an operation whose
        // resource was on its way out.
        //
        // A re-drive of the SAME operation is not a conflict: that is the resumable path
        // (docs/plan/08 § Long-running operations), and refusing it would strand every delete that
        // outlived a silo.
        if (state.State.OperationId != Guid.Empty
            && state.State.OperationId != operationId
            && state.State.ProvisioningState
                is ProvisioningState.Creating or ProvisioningState.Updating or ProvisioningState.Deleting) {
            return Result<ResourceSnapshot>.Failure(
                ErrorCode.OperationInProgress,
                $"Operation {state.State.OperationId:D} is already driving '{state.State.Path}' and it "
                + $"is {state.State.ProvisioningState}. Poll that operation, or cancel it, before "
                + "deleting — a delete that raced a live create would tear down objects the create is "
                + "still applying."
            );
        }

        state.State.ProvisioningState = ProvisioningState.Deleting;
        state.State.OperationId = operationId;
        state.State.ModifiedAt = clock.UtcNow;
        state.State.Version++;
        state.State.Etag = NextEtag();

        await state.WriteStateAsync();
        return Result<ResourceSnapshot>.Success(Snapshot(state.State.ApiVersion, []));
    }

    /// <inheritdoc />
    public async Task<Result> CompleteDeleteAsync() {
        if (!state.State.Exists) {
            // Already gone. Idempotent, because the delete path is re-driven from a reminder and a
            // second completion must not fail the operation that already succeeded.
            return Result.Success;
        }

        if (state.State.ProvisioningState != ProvisioningState.Deleting) {
            return Result.Failure(
                ErrorCode.Conflict,
                $"'{state.State.Path}' is {state.State.ProvisioningState} and only a Deleting "
                + "resource can complete a delete. Call BeginDeleteAsync first — the ordering is what "
                + "keeps a resource visible while its data plane is still up."
            );
        }

        await state.ClearStateAsync();
        state.State = new();
        return Result.Success;
    }

    /// <inheritdoc />
    public async Task<Result<ResourceSnapshot>> CompleteAsync(ProvisioningState terminal, Error? failure) {
        if (!state.State.Exists) {
            return NotFound<ResourceSnapshot>();
        }

        if (terminal is not (ProvisioningState.Succeeded or ProvisioningState.Failed or ProvisioningState.Canceled)) {
            return Result<ResourceSnapshot>.Failure(
                ErrorCode.InvalidRequestBody,
                $"{terminal} is not a terminal provisioning state. The terminal states are Succeeded, "
                + "Failed and Canceled — docs/plan/06 § Tags, locks, and the small stuff that is not "
                + "small."
            );
        }

        // ⚠ A resource whose teardown failed stays Deleting. docs/plan/06 § Two-phase create: it is
        // "left in Deleting with a retry reminder and is *visible* in listings with that state — never
        // silently gone while its pods still run and its meter still ticks." Moving it to Failed would
        // make it look like a resource that exists and is broken, which is a different and less
        // actionable thing than a resource that is on its way out and stuck.
        if (state.State.ProvisioningState == ProvisioningState.Deleting && terminal == ProvisioningState.Failed) {
            state.State.LastFailure = failure?.Message ?? string.Empty;
            state.State.ModifiedAt = clock.UtcNow;
            state.State.Version++;
            await state.WriteStateAsync();
            return Result<ResourceSnapshot>.Success(Snapshot(state.State.ApiVersion, []));
        }

        state.State.ProvisioningState = terminal;
        state.State.LastFailure = failure?.Message ?? string.Empty;
        state.State.ModifiedAt = clock.UtcNow;
        state.State.Version++;

        if (terminal is ProvisioningState.Succeeded) {
            state.State.OperationId = Guid.Empty;
        }

        await state.WriteStateAsync();
        return Result<ResourceSnapshot>.Success(Snapshot(state.State.ApiVersion, []));
    }

    /// <inheritdoc />
    public async Task<Result> ReportObservedAsync(ObservedState observed) {
        ArgumentNullException.ThrowIfNull(observed);

        if (!state.State.Exists) {
            return NotFound();
        }

        state.State.Observed = observed;
        await state.WriteStateAsync();
        return Result.Success;
    }

    /// <inheritdoc />
    public async Task<Result> SetLockAsync(LockLevel level) {
        if (!state.State.Exists) {
            return NotFound();
        }

        state.State.Lock = level;
        state.State.ModifiedAt = clock.UtcNow;
        state.State.Version++;
        await state.WriteStateAsync();
        return Result.Success;
    }

    /// <inheritdoc />
    public Task<Result<ReconcileInput>> GetReconcileInputAsync() {
        if (!state.State.Exists) {
            return Task.FromResult(NotFound<ReconcileInput>());
        }

        return Task.FromResult(
            Result<ReconcileInput>.Success(
                new() {
                    Path = state.State.Path,
                    ResourceId = resourceId,
                    ApiVersion = state.State.ApiVersion,
                    Desired = state.State.Superset,
                    Observed = state.State.Observed,
                    ClusterId = state.State.ClusterId,
                    ProvisioningState = state.State.ProvisioningState,
                    OperationId = state.State.OperationId
                }
            )
        );
    }

    /// <inheritdoc />
    public Task DeactivateAsync() {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }

    // ── Internals ──────────────────────────────────────────────────────────────────────────────

    ResourceSnapshot Snapshot(string apiVersion, ImmutableArray<string> declaredPointers) {
        var superset = Parse(state.State.Superset).GetValueOrThrow();
        var projected = Project(superset, declaredPointers);

        var type = ResourceTypeName.TryParse(TypeOf(state.State.Path), out var parsedType) ? parsedType : default;

        return new() {
            Id = resourceId,
            Path = state.State.Path,
            Type = type.ToString(),
            Name = NameOf(state.State.Path),
            ApiVersion = apiVersion,
            ProvisioningState = state.State.ProvisioningState,
            Properties = projected.ToJsonString(),
            Tags = state.State.Tags.ToImmutableDictionary(StringComparer.Ordinal),
            Etag = state.State.Etag,
            Location = state.State.Location,
            ClusterId = state.State.ClusterId,
            CreatedBy = state.State.CreatedBy,
            CreatedAt = state.State.CreatedAt,
            ModifiedBy = state.State.ModifiedBy,
            ModifiedAt = state.State.ModifiedAt,
            LastFailure = state.State.LastFailure,
            OperationId = state.State.OperationId,
            Lock = state.State.Lock
        };
    }

    /// <summary>
    ///     Keeps only the pointers an api-version declares — the "projects down" of
    ///     docs/plan/08 § The provider registry.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>An empty pointer list projects the whole superset, not nothing.</b> That is a
    ///     deliberate escape hatch for the paths that have no registry to consult — the delete path,
    ///     which reports a snapshot of a resource whose api-version may already be retired, and the
    ///     reconcile driver, which needs the state as stored. Every path that <i>does</i> have a
    ///     registry passes the real list, and
    ///     <c>WritePathTests.AReadAtAnOldVersionKeepsGettingTheShapeItWasWrittenAgainst</c> asserts an
    ///     old version never sees a newer one's field.
    /// </remarks>
    static JsonObject Project(JsonObject superset, ImmutableArray<string> declaredPointers) {
        if (declaredPointers.IsDefaultOrEmpty) {
            return (JsonObject)superset.DeepClone();
        }

        var projected = new JsonObject();

        foreach (var pointer in declaredPointers) {
            var node = JsonPointer.Read(superset, pointer);
            if (node is null or JsonObject) {
                // A container is rebuilt by whichever of its leaves lands first; projecting it whole
                // would carry the undeclared members inside it straight through the filter.
                continue;
            }

            JsonPointer.Write(projected, pointer, node.DeepClone());
        }

        return projected;
    }

    /// <summary>
    ///     Removes every pointer this version declares, then writes the ones the body carries.
    /// </summary>
    /// <remarks>
    ///     ⚠ This is what makes <c>PUT</c> a <i>replacement</i>: a property the caller left out is
    ///     removed rather than kept. It replaces only the declared slice, so a newer api-version's
    ///     fields survive an old-version <c>PUT</c> — the superset is shared and an old client must
    ///     not be able to delete what it cannot see.
    /// </remarks>
    static JsonObject ReplaceSlice(JsonObject superset, ImmutableArray<string> declared, JsonObject body) {
        var next = (JsonObject)superset.DeepClone();

        if (declared.IsDefaultOrEmpty) {
            // No registry slice to scope the replacement to: replace outright, which is what a type
            // with no declared properties means.
            return (JsonObject)body.DeepClone();
        }

        foreach (var pointer in declared) {
            // ⚠ A CONTAINER IS NEVER REMOVED, AND SKIPPING IT IS LOAD-BEARING.
            //
            // The schema models a nested object as its own property plus deeper ones — /properties
            // and /properties/size are two entries. Removing /properties would take every member
            // inside it, INCLUDING the ones a newer api-version declares and this version has never
            // heard of. That is exactly the data loss this method's scoping exists to prevent, and it
            // is not hypothetical: without this guard, a 2026 PUT wiped the 2027-only field, and
            // WritePathTests.AnOldVersionPutDoesNotDeleteANewerVersionsField is what caught it.
            //
            // The leaves inside the container are removed individually by their own pointers, so the
            // replacement semantics of PUT are unchanged for everything this version declares.
            if (JsonPointer.Read(next, pointer) is JsonObject) {
                continue;
            }

            JsonPointer.Remove(next, pointer);
        }

        foreach (var pointer in declared) {
            var value = JsonPointer.Read(body, pointer);
            if (value is null or JsonObject) {
                continue;
            }

            JsonPointer.Write(next, pointer, value.DeepClone());
        }

        return next;
    }

    /// <summary>RFC 7386 JSON Merge Patch: an absent member is left alone, an explicit null removes.</summary>
    static JsonObject MergePatch(JsonObject target, JsonObject patch) {
        var next = (JsonObject)target.DeepClone();

        foreach (var member in patch) {
            if (member.Value is null) {
                next.Remove(member.Key);
                continue;
            }

            if (member.Value is JsonObject nested && next[member.Key] is JsonObject existing) {
                next[member.Key] = MergePatch(existing, nested);
                continue;
            }

            next[member.Key] = member.Value.DeepClone();
        }

        return next;
    }

    static Result<JsonObject> Parse(string json) {
        try {
            var node = JsonNode.Parse(json);
            return node is JsonObject obj
                ? Result<JsonObject>.Success(obj)
                : Result<JsonObject>.Failure(
                    ErrorCode.InvalidRequestBody,
                    "A resource body is a JSON object.",
                    ""
                );
        }
        catch (JsonException exception) {
            return Result<JsonObject>.Failure(
                ErrorCode.InvalidRequestBody,
                // ⚠ The parser's message names the offset and the token and nothing about our stack —
                // docs/plan/08 § Errors bans exception details in an error body, and a JSON syntax
                // message is data about the caller's own input rather than about us.
                $"The request body is not valid JSON: {exception.Message}",
                ""
            );
        }
    }

    static Error? CheckTags(ImmutableDictionary<string, string> tags) =>
        tags.Count <= MaxTags
            ? null
            : new Error(
                ErrorCode.InvalidRequestBody,
                $"A resource carries at most {MaxTags.ToString(CultureInfo.InvariantCulture)} tags "
                + $"and {tags.Count.ToString(CultureInfo.InvariantCulture)} were supplied — "
                + "docs/plan/06 § Tags, locks, and the small stuff that is not small.",
                "/tags"
            );

    static bool TagsEqual(Dictionary<string, string> left, ImmutableDictionary<string, string> right) {
        if (left.Count != right.Count) {
            return false;
        }

        foreach (var pair in left) {
            if (!right.TryGetValue(pair.Key, out var value)
                || !string.Equals(value, pair.Value, StringComparison.Ordinal)) {
                return false;
            }
        }

        return true;
    }

    /// <summary>A fresh etag. Opaque by construction, so nobody parses it.</summary>
    static string NextEtag() => Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

    static string TypeOf(string path) {
        var parsed = ResourceId.ParsePath(path);
        return parsed.IsSuccess ? parsed.GetValueOrThrow().Type.ToString() : string.Empty;
    }

    static string NameOf(string path) {
        var parsed = ResourceId.ParsePath(path);
        return parsed.IsSuccess ? parsed.GetValueOrThrow().Name : string.Empty;
    }

    Result<T> NotFound<T>()
        where T : notnull =>
        Result<T>.Failure(
            ErrorCode.ResourceNotFound,
            $"Resource {resourceId:D} does not exist."
        );

    Result NotFound() =>
        Result.Failure(ErrorCode.ResourceNotFound, $"Resource {resourceId:D} does not exist.");
}
