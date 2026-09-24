using CyberCloud.ResourceManager.Contracts.Registry;
using Orleans.Multitenant;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.ResourceManager.Orchestration;

/// <summary>
///     <see cref="IDeploymentManager" /> — the what-if. docs/plan/08 § Long-running operations.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Step 1's ownership checks and step 3, in that order, and then nothing that writes.</b>
///         The tenant in the path must be the caller's and the subscription must be one that tenant
///         has, both answered with the canonical <c>404</c>, before the registry is asked anything —
///         for <see cref="ResourceManagerService" />'s reason: the registry's refusal describes the
///         platform. Then the caller must hold the action's permission at the deployment's address —
///         the deployment itself when it exists, its group when it does not, which is
///         <see cref="IResourceAuthorizer" />'s own rule for an address with no id.
///     </para>
///     <para>
///         ⚠ <b>Every comparison is a read as the caller</b>, through
///         <see cref="IResourceManager.ReadAsync" /> — the same seam, the same permission and the same
///         <c>404</c> a <c>GET</c> gets. A what-if therefore shows the caller nothing about a resource
///         they could not read with a <c>GET</c>; see <see cref="IDeploymentManager.WhatIfAsync" /> on
///         why an unreadable resource is reported as a create.
///     </para>
///     <para>
///         ⚠ <b>Secret properties are not compared.</b> A read withholds them
///         (<c>ReadablePointers</c>), so the current side never has them and every template that
///         sets one would report it as a change on every run. They are left out of the delta on both
///         sides, which says less and says nothing false.
///     </para>
/// </remarks>
public sealed class DeploymentManagerService(
    IResourceManager manager,
    IProviderRegistry registry,
    IResourceAuthorizer authorizer,
    IGrainFactory grains
) : IDeploymentManager {
    /// <inheritdoc />
    public async Task<Result<DeploymentWhatIf>> WhatIfAsync(
        WriteRequest request,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(request);

        // ── Step 1: the address, the two ownership checks, then the registry ──────────────────
        var parsed = ResourceId.ParsePath(request.Path);
        if (parsed.TryGetError(out var pathError)) {
            return Result<DeploymentWhatIf>.Failure(pathError);
        }

        var address = parsed.GetValueOrThrow();

        if (address.TenantId != request.Caller.TenantId) {
            return NotFound(request.Path);
        }

        var tenant = grains.ForTenant(address.TenantId.ToString("D", CultureInfo.InvariantCulture));

        var subscription = await tenant
            .GetGrain<ISubscriptionGrain>(GrainKeys.Subscription(address.SubscriptionId))
            .GetAsync();

        if (subscription.IsFailure) {
            return NotFound(request.Path);
        }

        if (!Deployments.Is(address.Type)) {
            return NotFound(request.Path);
        }

        var resolution = registry.Resolve(address.Type, request.ApiVersion);
        if (resolution.TryGetError(out var registryError)) {
            return Result<DeploymentWhatIf>.Failure(registryError);
        }

        var registration = resolution.GetValueOrThrow().Registration;

        if (!registration.TryGetAction(Deployments.WhatIfAction, out var action)) {
            return NotFound(request.Path);
        }

        // The deployment's own id when it exists, so the check is on it and inherits what it
        // inherits; Guid.Empty when it does not, which the authorizer reads as "check the group".
        var existing = await tenant.GetGrain<IResourceIndexGrain>(GrainKeys.PathIndex(address)).ResolveAsync();
        var target = existing.IsSuccess ? address.WithId(existing.GetValueOrThrow()) : address;

        // ── Step 3 ──────────────────────────────────────────────────────────────────────────────
        var authorized = await authorizer.AuthorizeAsync(
            target,
            action.Permission,
            registration.ReadPermission,
            request.Caller,
            false,
            cancellationToken
        );

        if (authorized.TryGetError(out var authError)) {
            return Result<DeploymentWhatIf>.Failure(authError);
        }

        // ── Step 2's checks, after the caller has been shown to own the scope ────────────────────
        JsonDocument body;

        try {
            body = JsonDocument.Parse(request.Body.Length == 0 ? "{}" : request.Body);
        } catch (JsonException exception) {
            return Result<DeploymentWhatIf>.Failure(
                ErrorCode.InvalidRequestBody,
                $"The request body is not valid JSON: {exception.Message}",
                ""
            );
        }

        DeploymentPlan plan;

        using (body) {
            if (action.Request is { } schema) {
                var validated = schema.Validate(body.RootElement);
                if (validated.TryGetError(out var schemaError)) {
                    return Result<DeploymentWhatIf>.Failure(schemaError);
                }
            }

            var planned = DeploymentTemplate.EvaluateBody(address, body.RootElement);
            if (planned.TryGetError(out var planError)) {
                return Result<DeploymentWhatIf>.Failure(planError);
            }

            plan = planned.GetValueOrThrow();
        }

        // ── The comparison, one read as the caller per resource ─────────────────────────────────
        var changes = ImmutableArray.CreateBuilder<WhatIfChange>(plan.Resources.Length);

        foreach (var resource in plan.Resources) {
            var compared = await CompareAsync(resource, request.Caller, cancellationToken);
            if (compared.TryGetError(out var compareError)) {
                return Result<DeploymentWhatIf>.Failure(compareError);
            }

            changes.Add(compared.GetValueOrThrow());
        }

        return Result<DeploymentWhatIf>.Success(new(changes.MoveToImmutable()));
    }

    async Task<Result<WhatIfChange>> CompareAsync(
        PlannedResource resource,
        CallerContext caller,
        CancellationToken cancellationToken
    ) {
        var type = resource.Id.Type;

        // The template's api-version must be one the type serves — the same refusal the real PUT
        // would meet at its step 1, given here rather than as a create that could never happen.
        var resolution = registry.Resolve(type, resource.ApiVersion);
        if (resolution.TryGetError(out var registryError)) {
            return Result<WhatIfChange>.Failure(
                registryError.Code,
                $"'{resource.Id.Path}' cannot be deployed: {registryError.Message}",
                resource.Id.Path
            );
        }

        var secrets = resolution.GetValueOrThrow()
            .Schema.Properties.Where(static x => x.Secret)
            .Select(static x => x.JsonPointer)
            .ToHashSet(StringComparer.Ordinal);

        var proposed = Leaves(JsonNode.Parse(resource.Body) as JsonObject ?? [], secrets);

        var read = await manager.ReadAsync(
            new() { Path = resource.Id.Path, ApiVersion = resource.ApiVersion, Caller = caller },
            cancellationToken
        );

        if (read.TryGetError(out var readError)) {
            if (readError.Code != ErrorCode.ResourceNotFound) {
                return Result<WhatIfChange>.Failure(
                    readError.Code,
                    $"'{resource.Id.Path}' cannot be compared: {readError.Message}",
                    resource.Id.Path
                );
            }

            return Result<WhatIfChange>.Success(
                new(
                    resource.Id.Path,
                    type.ToString(),
                    WhatIfChangeTypes.Create,
                    [
                        .. proposed.Select(static x => new WhatIfPropertyChange(
                                x.Key,
                                WhatIfChangeTypes.PropertyCreate,
                                null,
                                x.Value
                            )
                        )
                    ]
                )
            );
        }

        var snapshot = read.GetValueOrThrow();
        var current = JsonNode.Parse(snapshot.Body) as JsonObject ?? [];

        // A PUT replaces the tag bag with whatever the body carries, so the current tags belong on the
        // current side whether or not the template mentions any.
        if (snapshot.Tags.Count > 0) {
            var tags = new JsonObject();

            foreach (var (key, value) in snapshot.Tags.OrderBy(static x => x.Key, StringComparer.Ordinal)) {
                tags[key] = value;
            }

            current["tags"] = tags;
        }

        var before = Leaves(current, secrets);
        var delta = ImmutableArray.CreateBuilder<WhatIfPropertyChange>();

        foreach (var pointer in proposed.Keys.Union(before.Keys).OrderBy(static x => x, StringComparer.Ordinal)) {
            var had = before.TryGetValue(pointer, out var was);
            var will = proposed.TryGetValue(pointer, out var becomes);

            if (had && will) {
                if (!string.Equals(was, becomes, StringComparison.Ordinal)) {
                    delta.Add(new(pointer, WhatIfChangeTypes.PropertyModify, was, becomes));
                }
            } else if (will) {
                delta.Add(new(pointer, WhatIfChangeTypes.PropertyCreate, null, becomes));
            } else {
                delta.Add(new(pointer, WhatIfChangeTypes.PropertyDelete, was, null));
            }
        }

        return Result<WhatIfChange>.Success(
            new(
                resource.Id.Path,
                type.ToString(),
                delta.Count == 0 ? WhatIfChangeTypes.NoChange : WhatIfChangeTypes.Modify,
                delta.ToImmutable()
            )
        );
    }

    /// <summary>
    ///     Every leaf of a body — anything that is not an object, arrays included — against its JSON
    ///     Pointer, in canonical JSON so two spellings of one value compare equal.
    /// </summary>
    static Dictionary<string, string> Leaves(JsonObject body, HashSet<string> skip) {
        var leaves = new Dictionary<string, string>(StringComparer.Ordinal);
        Walk(body, "", leaves, skip);
        return leaves;
    }

    static void Walk(JsonObject node, string prefix, Dictionary<string, string> leaves, HashSet<string> skip) {
        foreach (var (key, value) in node) {
            var pointer = prefix + "/" + key.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

            if (skip.Contains(pointer)) {
                continue;
            }

            if (value is JsonObject child) {
                Walk(child, pointer, leaves, skip);
                continue;
            }

            // Canonicalised inside a one-member wrapper, because JsonCanonical orders an object's
            // members and a leaf may be an array of objects whose member order is not meaningful.
            var wrapped = new JsonObject { ["v"] = value?.DeepClone() };
            leaves[pointer] = JsonCanonical.Of(wrapped)["v"]?.ToJsonString() ?? "null";
        }
    }

    static Result<DeploymentWhatIf> NotFound(string path) =>
        Result<DeploymentWhatIf>.Failure(
            ErrorCode.ResourceNotFound,
            // The same message a missing resource gets — docs/plan/07 § The enforcement seam.
            $"'{path}' does not exist."
        );
}
