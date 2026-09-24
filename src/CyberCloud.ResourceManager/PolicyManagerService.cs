using CyberCloud.Authorization.Contracts;
using CyberCloud.Core.Policy;
using Microsoft.Extensions.Logging;
using Orleans.Multitenant;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.ResourceManager;

/// <summary>
///     Policy definitions and assignments — resolve, check, validate, store — and the compliance
///     states an audit recorded. <see cref="IPolicyManager" />'s remarks carry why this is its own
///     entry point.
/// </summary>
/// <remarks>
///     <para><b>The order, for a write:</b></para>
///     <list type="number">
///         <item>
///             <description>
///                 <b>Resolve.</b> Parse the address; its tenant must be the caller's; its scope must
///                 exist, read from the scope's own grain. Every refusal that could leak is the
///                 canonical <c>404</c>.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <b>Check.</b> <c>assignRole</c> on the scope, <c>FullyConsistent</c> — an owner whose
///                 ownership was revoked a second ago assigning a deny policy over their old colleagues
///                 is the incident docs/plan/07 § Consistency's row is about.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <b>Validate.</b> The body, against the closed sets: <c>PolicyRule</c> for a definition;
///                 for an assignment, a definition in the same tenant at the assignment's scope or above
///                 it, and exclusions beneath it. After the check, so a caller with no grant learns
///                 nothing about the tree from the difference between two refusals.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <b>Store.</b> Through <see cref="IPolicyCatalogGrain" />, which parses the rule again
///                 and holds the invariants that span objects in one turn.
///             </description>
///         </item>
///     </list>
///     <para>
///         ⚠ <b>A definition is usable only at or beneath its own scope</b>, which is Azure's rule and
///         the one that makes a definition's scope mean anything: a subscription owner cannot assign a
///         rule another subscription's owner wrote, and a definition at the tenant is usable everywhere.
///         The management-group case walks the tree from the assignment's scope upwards.
///     </para>
///     <para>
///         ⚠ <b>Every grain reference goes through <c>ForTenant</c></b>, with the tenant from the
///         address — which the resolve step has already required to be the caller's.
///     </para>
/// </remarks>
public sealed class PolicyManagerService(
    IScopeAuthorizer scopes,
    IGrainFactory grains,
    ILogger<PolicyManagerService> logger
)
    : IPolicyManager {
    /// <inheritdoc />
    public async Task<Result<PolicyObjectSnapshot>> PutAsync(
        PolicyRequest request,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(request);

        var resolved = await ResolveItemAsync(request.Path, request.Caller);
        if (resolved.TryGetError(out var resolveError)) {
            return Result<PolicyObjectSnapshot>.Failure(resolveError);
        }

        var address = resolved.GetValueOrThrow();

        var permitted = await scopes.AuthorizeAsync(
            address.Scope,
            Permissions.AssignRole,
            Permissions.Read,
            request.Caller,
            true,
            cancellationToken
        );

        if (permitted.TryGetError(out var denied)) {
            return Result<PolicyObjectSnapshot>.Failure(denied);
        }

        var properties = PropertiesOf(request.Body, address);
        if (properties.TryGetError(out var bodyError)) {
            return Result<PolicyObjectSnapshot>.Failure(bodyError);
        }

        return address.Kind == PolicyObjectKind.Definition
            ? await PutDefinitionAsync(address, properties.GetValueOrThrow(), request.Caller)
            : await PutAssignmentAsync(address, properties.GetValueOrThrow(), request.Caller);
    }

    /// <inheritdoc />
    public async Task<Result<PolicyObjectSnapshot>> ReadAsync(
        PolicyRequest request,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(request);

        var resolved = await ResolveItemAsync(request.Path, request.Caller);
        if (resolved.TryGetError(out var resolveError)) {
            return Result<PolicyObjectSnapshot>.Failure(resolveError);
        }

        var address = resolved.GetValueOrThrow();

        var allowed = await scopes.AuthorizeAsync(address.Scope, Permissions.Read, Permissions.Read, request.Caller, false, cancellationToken);
        if (allowed.TryGetError(out var denied)) {
            return Result<PolicyObjectSnapshot>.Failure(denied);
        }

        var catalog = Catalog(address.TenantId);

        if (address.Kind == PolicyObjectKind.Definition) {
            var definition = await catalog.GetDefinitionAsync(address.Path);
            return definition.TryGetError(out var readError)
                ? Result<PolicyObjectSnapshot>.Failure(readError)
                : Result<PolicyObjectSnapshot>.Success(Snapshot(definition.GetValueOrThrow(), false));
        }

        var assignment = await catalog.GetAssignmentAsync(address.Path);
        return assignment.TryGetError(out var assignmentError)
            ? Result<PolicyObjectSnapshot>.Failure(assignmentError)
            : Result<PolicyObjectSnapshot>.Success(Snapshot(assignment.GetValueOrThrow(), false));
    }

    /// <inheritdoc />
    public async Task<Result> DeleteAsync(PolicyRequest request, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(request);

        var resolved = await ResolveItemAsync(request.Path, request.Caller);
        if (resolved.TryGetError(out var resolveError)) {
            return Result.Failure(resolveError);
        }

        var address = resolved.GetValueOrThrow();

        var permitted = await scopes.AuthorizeAsync(
            address.Scope,
            Permissions.AssignRole,
            Permissions.Read,
            request.Caller,
            true,
            cancellationToken
        );

        if (permitted.TryGetError(out var denied)) {
            return Result.Failure(denied);
        }

        var catalog = Catalog(address.TenantId);
        var deleted = address.Kind == PolicyObjectKind.Definition
            ? await catalog.DeleteDefinitionAsync(address.Path)
            : await catalog.DeleteAssignmentAsync(address.Path);

        if (deleted.IsSuccess) {
            logger.LogInformation("{Caller} deleted policy {Kind} '{Path}'.", request.Caller, address.Kind, address.Path);
        }

        return deleted;
    }

    /// <inheritdoc />
    public async Task<Result<PolicyListPage>> ListAsync(
        PolicyListRequest request,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(request);

        var parsed = PolicyAddress.ParsePath(request.Path);
        if (parsed.TryGetError(out var pathError)) {
            return Result<PolicyListPage>.Failure(pathError);
        }

        var address = parsed.GetValueOrThrow();

        if (!address.IsCollection) {
            return Result<PolicyListPage>.Failure(
                ErrorCode.InvalidResourceId,
                $"'{request.Path}' names one policy object, and a listing is addressed at its collection."
            );
        }

        var exists = await ScopeExistsAsync(address, request.Caller);
        if (exists.TryGetError(out var scopeError)) {
            return Result<PolicyListPage>.Failure(scopeError);
        }

        // ⚠ One check, on the scope, and no per-row filter — the shape IRoleAssignmentManager.ListAsync
        // argues for. A reader of a scope reads every resource beneath it (the rewrite is From(parent,
        // reader) all the way down), so a state row names nothing the caller could not have read.
        var allowed = await scopes.AuthorizeAsync(address.Scope, Permissions.Read, Permissions.Read, request.Caller, false, cancellationToken);
        if (allowed.TryGetError(out var denied)) {
            return Result<PolicyListPage>.Failure(denied);
        }

        var catalog = Catalog(address.TenantId);

        if (address.Kind == PolicyObjectKind.State) {
            var subscriptions = address.Scope.Kind == ScopeKind.ManagementGroup
                ? await SubscriptionsBeneathAsync(address.TenantId, address.Scope.ManagementGroup)
                : [];

            var states = await catalog.ListStatesAsync(address.Scope.Path, subscriptions);
            if (states.TryGetError(out var statesError)) {
                return Result<PolicyListPage>.Failure(statesError);
            }

            return Result<PolicyListPage>.Success(PageOfStates(states.GetValueOrThrow(), request));
        }

        ImmutableArray<PolicyObjectSnapshot> objects;

        if (address.Kind == PolicyObjectKind.Definition) {
            var listed = await catalog.ListDefinitionsAsync(address.Scope.Path);
            if (listed.TryGetError(out var listError)) {
                return Result<PolicyListPage>.Failure(listError);
            }

            objects = [.. listed.GetValueOrThrow().Select(static x => Snapshot(x, false))];
        } else {
            var listed = await catalog.ListAssignmentsAsync(address.Scope.Path);
            if (listed.TryGetError(out var listError)) {
                return Result<PolicyListPage>.Failure(listError);
            }

            objects = [.. listed.GetValueOrThrow().Select(static x => Snapshot(x, false))];
        }

        // Ordered by address and resumed after the last address served — the rule every collection
        // of this API pages by (ListRequest.Continuation).
        var page = objects
            .Where(x => string.CompareOrdinal(x.Path, request.Continuation) > 0)
            .OrderBy(static x => x.Path, StringComparer.Ordinal)
            .Take(request.PageSize + 1)
            .ToList();

        var hasMore = page.Count > request.PageSize;
        if (hasMore) {
            page.RemoveAt(page.Count - 1);
        }

        return Result<PolicyListPage>.Success(
            new() { Objects = [.. page], Continuation = hasMore ? page[^1].Path : string.Empty }
        );
    }

    // ── Definitions ────────────────────────────────────────────────────────────────────────────

    async Task<Result<PolicyObjectSnapshot>> PutDefinitionAsync(PolicyAddress address, JsonElement properties, CallerContext caller) {
        var allowed = Members(properties, [PolicyBodyProperties.DisplayName, PolicyBodyProperties.Description, PolicyBodyProperties.PolicyRule]);
        if (allowed.TryGetError(out var memberError)) {
            return Result<PolicyObjectSnapshot>.Failure(memberError);
        }

        var displayName = Text(properties, PolicyBodyProperties.DisplayName, PolicyBodyProperties.MaxDisplayName);
        if (displayName.TryGetError(out var displayError)) {
            return Result<PolicyObjectSnapshot>.Failure(displayError);
        }

        var description = Text(properties, PolicyBodyProperties.Description, PolicyBodyProperties.MaxDescription);
        if (description.TryGetError(out var descriptionError)) {
            return Result<PolicyObjectSnapshot>.Failure(descriptionError);
        }

        const string ruleTarget = "/properties/" + PolicyBodyProperties.PolicyRule;

        if (!properties.TryGetProperty(PolicyBodyProperties.PolicyRule, out var ruleElement)) {
            return Result<PolicyObjectSnapshot>.Failure(
                ErrorCode.InvalidRequestBody,
                "A policy definition needs a 'policyRule' — { \"if\": <condition>, \"then\": { \"effect\": "
                + "\"deny\" | \"audit\" | \"modify\" } }.",
                ruleTarget
            );
        }

        // ⚠ Parsed here for the error the caller reads — the closed set named, the member targeted —
        // and again in the grain, which is the single writer and does not trust this one.
        var rule = PolicyRule.Parse(ruleElement, ruleTarget);
        if (rule.TryGetError(out var ruleError)) {
            return Result<PolicyObjectSnapshot>.Failure(ruleError);
        }

        var written = await Catalog(address.TenantId)
            .PutDefinitionAsync(
                new() {
                    Path = address.Path,
                    Scope = address.Scope.Path,
                    Name = address.Name,
                    DisplayName = displayName.GetValueOrThrow(),
                    Description = description.GetValueOrThrow(),
                    Rule = JsonNode.Parse(ruleElement.GetRawText())!.ToJsonString()
                }
            );

        if (written.TryGetError(out var writeError)) {
            return Result<PolicyObjectSnapshot>.Failure(writeError);
        }

        var outcome = written.GetValueOrThrow();

        logger.LogInformation(
            "{Caller} wrote policy definition '{Path}' ({Effect}), version {Version}.",
            caller,
            address.Path,
            rule.GetValueOrThrow().EffectName,
            outcome.Record.Version
        );

        return Result<PolicyObjectSnapshot>.Success(Snapshot(outcome.Record, outcome.Created));
    }

    // ── Assignments ────────────────────────────────────────────────────────────────────────────

    async Task<Result<PolicyObjectSnapshot>> PutAssignmentAsync(PolicyAddress address, JsonElement properties, CallerContext caller) {
        var allowed = Members(properties, [PolicyBodyProperties.DisplayName, PolicyBodyProperties.PolicyDefinitionId, PolicyBodyProperties.NotScopes, PolicyBodyProperties.Scope]);
        if (allowed.TryGetError(out var memberError)) {
            return Result<PolicyObjectSnapshot>.Failure(memberError);
        }

        var displayName = Text(properties, PolicyBodyProperties.DisplayName, PolicyBodyProperties.MaxDisplayName);
        if (displayName.TryGetError(out var displayError)) {
            return Result<PolicyObjectSnapshot>.Failure(displayError);
        }

        // `scope` is what a GET answers with; a body that repeats it must agree, so a client can PUT back
        // what it read without the platform either trusting or silently discarding a value.
        if (properties.TryGetProperty(PolicyBodyProperties.Scope, out var echoedScope)
            && !string.Equals(echoedScope.ValueKind == JsonValueKind.String ? echoedScope.GetString() : null, address.Scope.Path, StringComparison.Ordinal)) {
            return Result<PolicyObjectSnapshot>.Failure(
                ErrorCode.InvalidRequestBody,
                $"'scope' is read-only and says where the assignment sits, which the address already says: "
                + $"'{address.Scope.Path}'. Omit it, or send the value the address implies.",
                "/properties/" + PolicyBodyProperties.Scope
            );
        }

        var definition = await DefinitionAsync(address, properties);
        if (definition.TryGetError(out var definitionError)) {
            return Result<PolicyObjectSnapshot>.Failure(definitionError);
        }

        var notScopes = await NotScopesAsync(address, properties);
        if (notScopes.TryGetError(out var notScopesError)) {
            return Result<PolicyObjectSnapshot>.Failure(notScopesError);
        }

        var written = await Catalog(address.TenantId)
            .PutAssignmentAsync(
                new() {
                    Path = address.Path,
                    Scope = address.Scope.Path,
                    Name = address.Name,
                    DisplayName = displayName.GetValueOrThrow(),
                    DefinitionPath = definition.GetValueOrThrow(),
                    NotScopes = notScopes.GetValueOrThrow()
                }
            );

        if (written.TryGetError(out var writeError)) {
            return Result<PolicyObjectSnapshot>.Failure(writeError);
        }

        var outcome = written.GetValueOrThrow();

        logger.LogInformation(
            "{Caller} assigned policy definition '{Definition}' at '{Scope}' as '{Path}', version {Version}.",
            caller,
            outcome.Record.DefinitionPath,
            address.Scope.Path,
            address.Path,
            outcome.Record.Version
        );

        return Result<PolicyObjectSnapshot>.Success(Snapshot(outcome.Record, outcome.Created));
    }

    /// <summary>
    ///     The assignment's definition: a definition address in the same tenant, at the assignment's
    ///     scope or above it.
    /// </summary>
    async Task<Result<string>> DefinitionAsync(PolicyAddress assignment, JsonElement properties) {
        const string target = "/properties/" + PolicyBodyProperties.PolicyDefinitionId;

        if (!properties.TryGetProperty(PolicyBodyProperties.PolicyDefinitionId, out var element)
            || element.ValueKind != JsonValueKind.String) {
            return Result<string>.Failure(
                ErrorCode.InvalidRequestBody,
                "An assignment needs a 'policyDefinitionId' — the address of the definition it applies, "
                + "'{scope}/providers/CyberCloud.Policy/policyDefinitions/{name}'.",
                target
            );
        }

        var parsed = PolicyAddress.ParsePath(element.GetString());

        if (parsed.IsFailure
            || parsed.GetValueOrThrow().Kind != PolicyObjectKind.Definition
            || parsed.GetValueOrThrow().IsCollection) {
            return Result<string>.Failure(
                ErrorCode.InvalidRequestBody,
                $"'{element.GetString()}' is not a policy definition address. It is "
                + "'{scope}/providers/CyberCloud.Policy/policyDefinitions/{name}', on a tenant, a management "
                + "group or a subscription.",
                target
            );
        }

        var definition = parsed.GetValueOrThrow();

        // ⚠ Another tenant's definition is "does not exist", in the same words, by the same rule the
        // catalog applies — the catalog is this tenant's and cannot hold it.
        if (definition.TenantId != assignment.TenantId) {
            return Result<string>.Failure(
                ErrorCode.InvalidRequestBody,
                $"'{definition.Path}' is not a policy definition in this tenant, so it cannot be assigned. "
                + "Create the definition first.",
                target
            );
        }

        var above = await IsAtOrAboveAsync(definition.Scope, assignment.Scope);
        if (!above) {
            return Result<string>.Failure(
                ErrorCode.InvalidRequestBody,
                $"'{definition.Path}' sits at '{definition.Scope.Path}', which is not '{assignment.Scope.Path}' or a "
                + "scope above it. A definition is usable at its own scope and beneath it — a definition at "
                + "the tenant is usable everywhere — so an owner assigns only the rules written for the part "
                + "of the tree they own.",
                target
            );
        }

        return Result<string>.Success(definition.Path);
    }

    /// <summary>Whether <paramref name="upper" /> is <paramref name="lower" /> or an ancestor of it.</summary>
    async Task<bool> IsAtOrAboveAsync(ScopeId upper, ScopeId lower) {
        switch (upper.Kind) {
            case ScopeKind.Tenant:
                return true;
            case ScopeKind.Subscription:
                return lower.Kind is ScopeKind.Subscription or ScopeKind.ResourceGroup
                    && lower.SubscriptionId == upper.SubscriptionId;
            case ScopeKind.ManagementGroup:
                string start;

                if (lower.Kind == ScopeKind.ManagementGroup) {
                    start = lower.ManagementGroup;
                } else {
                    var subscription = await Tenant(lower.TenantId)
                        .GetGrain<ISubscriptionGrain>(GrainKeys.Subscription(lower.SubscriptionId))
                        .GetAsync();

                    if (subscription.IsFailure) {
                        return false;
                    }

                    start = subscription.GetValueOrThrow().ManagementGroup;
                }

                return await ChainContainsAsync(lower.TenantId, start, upper.ManagementGroup);
            default:
                return false;
        }
    }

    async Task<bool> ChainContainsAsync(Guid tenantId, string start, string wanted) {
        var name = start;

        for (var hop = 0; hop < IManagementGroupGrain.MaxDepth && name.Length > 0; hop++) {
            if (string.Equals(name, wanted, StringComparison.Ordinal)) {
                return true;
            }

            var group = await Tenant(tenantId).GetGrain<IManagementGroupGrain>(GrainKeys.ManagementGroup(name)).GetAsync();
            if (group.IsFailure) {
                return false;
            }

            name = group.GetValueOrThrow().Parent;
        }

        return false;
    }

    /// <summary>
    ///     The exclusions: scope or resource addresses beneath the assignment's scope, stored canonical.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>"Beneath" is a walk for a management group and a prefix for everything else.</b> A
    ///     subscription's and a resource group's descendants carry their path as a prefix; a management
    ///     group's do not — <c>/tenants/{t}/managementGroups/{g}</c> is a prefix of nothing, and what
    ///     sits beneath it is decided by each subscription's group and each group's parent. So for an
    ///     assignment at a group, an exclusion is checked by walking up from it.
    /// </remarks>
    async Task<Result<ImmutableArray<string>>> NotScopesAsync(PolicyAddress assignment, JsonElement properties) {
        const string target = "/properties/" + PolicyBodyProperties.NotScopes;

        if (!properties.TryGetProperty(PolicyBodyProperties.NotScopes, out var element)) {
            return Result<ImmutableArray<string>>.Success([]);
        }

        if (element.ValueKind != JsonValueKind.Array) {
            return Result<ImmutableArray<string>>.Failure(ErrorCode.InvalidRequestBody, "'notScopes' is an array of addresses.", target);
        }

        if (element.GetArrayLength() > PolicyBodyProperties.MaxNotScopes) {
            return Result<ImmutableArray<string>>.Failure(
                ErrorCode.InvalidRequestBody,
                $"'notScopes' holds at most {PolicyBodyProperties.MaxNotScopes.ToString(CultureInfo.InvariantCulture)} addresses.",
                target
            );
        }

        var built = new SortedSet<string>(StringComparer.Ordinal);
        var index = 0;
        var prefix = assignment.Scope.Path + "/";

        foreach (var item in element.EnumerateArray()) {
            var at = target + "/" + index.ToString(CultureInfo.InvariantCulture);
            index++;

            var text = item.ValueKind == JsonValueKind.String ? item.GetString() : null;

            // A scope, or a resource; either way the canonical spelling is what the catalog compares,
            // and the scope it sits in is what "beneath" is asked of.
            string? canonical;
            ScopeId within;

            if (ScopeId.TryParsePath(text, out var scope)) {
                canonical = scope.Path;
                within = scope;
            } else if (ResourceId.ParsePath(text) is { IsSuccess: true } resource) {
                var id = resource.GetValueOrThrow();
                canonical = id.CanonicalPath;
                within = ScopeId.Group(id.TenantId, id.SubscriptionId, id.ResourceGroup);
            } else {
                return Result<ImmutableArray<string>>.Failure(
                    ErrorCode.InvalidRequestBody,
                    $"'{text}' is not a scope or resource address.",
                    at
                );
            }

            // ⚠ Beneath the assignment, in its tenant — an exclusion anywhere else excludes nothing, and a
            // body that asked for one has misunderstood what the assignment reaches.
            var beneath = assignment.Scope.Kind == ScopeKind.ManagementGroup
                ? within.TenantId == assignment.TenantId
                    && within != assignment.Scope
                    && await IsAtOrAboveAsync(assignment.Scope, within)
                : canonical.StartsWith(prefix, StringComparison.Ordinal);

            if (!beneath) {
                return Result<ImmutableArray<string>>.Failure(
                    ErrorCode.InvalidRequestBody,
                    $"'{text}' is not beneath '{assignment.Scope.Path}', where the assignment sits, so excluding "
                    + "it would exclude nothing.",
                    at
                );
            }

            built.Add(canonical);
        }

        return Result<ImmutableArray<string>>.Success([.. built]);
    }

    // ── Resolve ────────────────────────────────────────────────────────────────────────────────

    async Task<Result<PolicyAddress>> ResolveItemAsync(string path, CallerContext caller) {
        var parsed = PolicyAddress.ParsePath(path);
        if (parsed.TryGetError(out var pathError)) {
            return Result<PolicyAddress>.Failure(pathError);
        }

        var address = parsed.GetValueOrThrow();

        if (address.IsCollection || address.Kind == PolicyObjectKind.State) {
            return Result<PolicyAddress>.Failure(
                ErrorCode.InvalidResourceId,
                $"'{path}' is a collection. A definition or an assignment is written, read and deleted at "
                + "'{scope}/providers/CyberCloud.Policy/{policyDefinitions|policyAssignments}/{name}', and "
                + "the compliance states are only ever read."
            );
        }

        var exists = await ScopeExistsAsync(address, caller);
        return exists.TryGetError(out var scopeError)
            ? Result<PolicyAddress>.Failure(scopeError)
            : Result<PolicyAddress>.Success(address);
    }

    /// <summary>The tenant is the caller's and the scope exists — read from the scope's own grain.</summary>
    /// <remarks>
    ///     ⚠ Existence read and not inferred from the check, for the reason
    ///     <c>RoleAssignmentService</c> gives: a <c>parent</c> edge left aimed at a scope whose create
    ///     failed would otherwise let the tenant's owner hang policy on a subscription that was never
    ///     created.
    /// </remarks>
    async Task<Result> ScopeExistsAsync(PolicyAddress address, CallerContext caller) {
        if (address.TenantId != caller.TenantId) {
            return NotFound(address.Path);
        }

        var scope = address.Scope;
        var tenant = Tenant(scope.TenantId);

        var exists = scope.Kind switch {
            ScopeKind.Tenant => (await tenant.GetGrain<ITenantGrain>(GrainKeys.Tenant(scope.TenantId)).GetAsync()).IsSuccess,
            ScopeKind.Subscription => (await tenant.GetGrain<ISubscriptionGrain>(GrainKeys.Subscription(scope.SubscriptionId)).GetAsync()).IsSuccess,
            ScopeKind.ResourceGroup => (await tenant
                    .GetGrain<IResourceGroupGrain>(GrainKeys.ResourceGroup(scope.SubscriptionId, scope.ResourceGroup))
                    .GetAsync()).IsSuccess,
            ScopeKind.ManagementGroup => (await tenant
                    .GetGrain<IManagementGroupGrain>(GrainKeys.ManagementGroup(scope.ManagementGroup))
                    .GetAsync()).IsSuccess,
            _ => false
        };

        return exists ? Result.Success : NotFound(address.Path);
    }

    /// <summary>Every subscription in a management group's subtree, by a walk down its children.</summary>
    /// <remarks>
    ///     ⚠ Bounded by the tree: <c>IManagementGroupGrain.MaxDepth</c> levels, and a visited set, so a
    ///     cycle that a data error introduced cannot loop the listing.
    /// </remarks>
    async Task<ImmutableArray<Guid>> SubscriptionsBeneathAsync(Guid tenantId, string root) {
        var found = new List<Guid>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var level = new List<string> { root };

        for (var depth = 0; depth < IManagementGroupGrain.MaxDepth && level.Count > 0; depth++) {
            var next = new List<string>();

            foreach (var name in level.Where(visited.Add)) {
                var group = await Tenant(tenantId).GetGrain<IManagementGroupGrain>(GrainKeys.ManagementGroup(name)).GetAsync();
                if (group.IsFailure) {
                    continue;
                }

                found.AddRange(group.GetValueOrThrow().Subscriptions);
                next.AddRange(group.GetValueOrThrow().Children);
            }

            level = next;
        }

        return [.. found.Distinct()];
    }

    // ── Body ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     The body's <c>properties</c> object. <c>id</c>, <c>name</c> and <c>type</c> may be repeated
    ///     from a <c>GET</c> and must agree with the address; nothing else may sit beside
    ///     <c>properties</c>.
    /// </summary>
    static Result<JsonElement> PropertiesOf(string body, PolicyAddress address) {
        JsonDocument document;

        try {
            document = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        } catch (JsonException exception) {
            // The parser's message describes the caller's own input — docs/plan/08 § Errors.
            return Result<JsonElement>.Failure(ErrorCode.InvalidRequestBody, $"The request body is not valid JSON: {exception.Message}", "");
        }

        using (document) {
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object) {
                return Result<JsonElement>.Failure(ErrorCode.InvalidRequestBody, "A policy body is a JSON object with a 'properties' member.", "");
            }

            foreach (var member in root.EnumerateObject()) {
                var expected = member.Name switch {
                    "properties" => null,
                    "id" => address.Path,
                    "name" => address.Name,
                    "type" => address.Kind == PolicyObjectKind.Definition ? PolicyAddress.DefinitionTypeName : PolicyAddress.AssignmentTypeName,
                    _ => ""
                };

                if (expected is null) {
                    continue;
                }

                if (expected.Length == 0) {
                    return Result<JsonElement>.Failure(
                        ErrorCode.InvalidRequestBody,
                        $"'{member.Name}' is not a member of a policy body, which is 'properties' — with 'id', 'name' "
                        + "and 'type' allowed only when they repeat what the address says.",
                        PolicyField.Append("", member.Name)
                    );
                }

                var actual = member.Value.ValueKind == JsonValueKind.String ? member.Value.GetString() : null;

                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) {
                    return Result<JsonElement>.Failure(
                        ErrorCode.InvalidRequestBody,
                        $"The body's '{member.Name}' is '{actual}' and the address says '{expected}'. The address is "
                        + "the object; a body that repeats part of it must agree.",
                        PolicyField.Append("", member.Name)
                    );
                }
            }

            if (!root.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object) {
                return Result<JsonElement>.Failure(ErrorCode.InvalidRequestBody, "A policy body carries a 'properties' object.", "/properties");
            }

            return Result<JsonElement>.Success(properties.Clone());
        }
    }

    static Result Members(JsonElement properties, string[] known) {
        foreach (var member in properties.EnumerateObject()) {
            if (!known.Contains(member.Name, StringComparer.Ordinal)) {
                // ⚠ Refused rather than dropped, as the registry refuses an unknown property: a
                // misspelt 'notScope' silently dropped is an exclusion the caller believes is in force.
                return Result.Failure(
                    ErrorCode.InvalidRequestBody,
                    $"'{member.Name}' is not a property here. The properties are [{string.Join(", ", known)}].",
                    PolicyField.Append("/properties", member.Name)
                );
            }
        }

        return Result.Success;
    }

    static Result<string> Text(JsonElement properties, string name, int maxLength) {
        if (!properties.TryGetProperty(name, out var element)) {
            return Result<string>.Success("");
        }

        if (element.ValueKind != JsonValueKind.String) {
            return Result<string>.Failure(ErrorCode.InvalidRequestBody, $"'{name}' is a string.", "/properties/" + name);
        }

        var text = element.GetString()!;

        return text.Length > maxLength
            ? Result<string>.Failure(
                ErrorCode.InvalidRequestBody,
                $"'{name}' is at most {maxLength.ToString(CultureInfo.InvariantCulture)} characters.",
                "/properties/" + name
            )
            : Result<string>.Success(text);
    }

    // ── Shaping ────────────────────────────────────────────────────────────────────────────────

    static PolicyObjectSnapshot Snapshot(PolicyDefinitionRecord definition, bool created) {
        var properties = new JsonObject {
            [PolicyBodyProperties.DisplayName] = definition.DisplayName,
            [PolicyBodyProperties.Description] = definition.Description,
            [PolicyBodyProperties.PolicyRule] = JsonNode.Parse(definition.Rule)
        };

        return new() {
            Path = definition.Path,
            Name = definition.Name,
            Type = PolicyAddress.DefinitionTypeName,
            Scope = definition.Scope,
            Properties = properties.ToJsonString(),
            Created = created
        };
    }

    static PolicyObjectSnapshot Snapshot(PolicyAssignmentRecord assignment, bool created) {
        var notScopes = new JsonArray();
        foreach (var excluded in assignment.NotScopes) {
            notScopes.Add(excluded);
        }

        var properties = new JsonObject {
            [PolicyBodyProperties.DisplayName] = assignment.DisplayName,
            [PolicyBodyProperties.PolicyDefinitionId] = assignment.DefinitionPath,
            [PolicyBodyProperties.NotScopes] = notScopes,
            [PolicyBodyProperties.Scope] = assignment.Scope
        };

        return new() {
            Path = assignment.Path,
            Name = assignment.Name,
            Type = PolicyAddress.AssignmentTypeName,
            Scope = assignment.Scope,
            Properties = properties.ToJsonString(),
            Created = created
        };
    }

    /// <summary>A page of states, resumed after the (resource, assignment) pair the token names.</summary>
    /// <remarks>
    ///     ⚠ The token is <c>{resourcePath}|{assignmentPath}</c>. Neither path can contain a <c>|</c> —
    ///     every segment of both is a GUID, a literal or a DNS-1123 name — so the split is exact, and
    ///     the comparison is on the pair rather than on the joined string, whose order would depend on
    ///     where <c>|</c> sorts.
    /// </remarks>
    static PolicyListPage PageOfStates(ImmutableArray<PolicyStateRecord> states, PolicyListRequest request) {
        var split = request.Continuation.IndexOf('|', StringComparison.Ordinal);
        var afterResource = split < 0 ? request.Continuation : request.Continuation[..split];
        var afterAssignment = split < 0 ? "" : request.Continuation[(split + 1)..];

        var page = states
            .Where(x => {
                var byResource = string.CompareOrdinal(x.ResourcePath, afterResource);
                return request.Continuation.Length == 0
                    || byResource > 0
                    || (byResource == 0 && string.CompareOrdinal(x.AssignmentPath, afterAssignment) > 0);
            })
            .Take(request.PageSize + 1)
            .ToList();

        var hasMore = page.Count > request.PageSize;
        if (hasMore) {
            page.RemoveAt(page.Count - 1);
        }

        return new() {
            States = [.. page],
            Continuation = hasMore ? page[^1].ResourcePath + "|" + page[^1].AssignmentPath : string.Empty
        };
    }

    IPolicyCatalogGrain Catalog(Guid tenantId) =>
        Tenant(tenantId).GetGrain<IPolicyCatalogGrain>(GrainKeys.PolicyCatalog(tenantId));

    TenantGrainFactory Tenant(Guid tenantId) => grains.ForTenant(tenantId.ToString("D", CultureInfo.InvariantCulture));

    static Result NotFound(string path) =>
        Result.Failure(
            ErrorCode.ResourceNotFound,
            // ⚠ Byte-identical to the sentence both authorizers produce for an object the caller may
            // not see. Two different messages would be the oracle the shared status code closed.
            $"'{path}' does not exist."
        );
}
