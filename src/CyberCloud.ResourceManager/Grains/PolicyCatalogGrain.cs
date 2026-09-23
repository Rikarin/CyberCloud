using CyberCloud.Core.Policy;
using CyberCloud.Core.Time;
using Microsoft.Extensions.Logging;
using Orleans.Multitenant;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.ResourceManager.Grains;

/// <summary>
///     A tenant's policy definitions, assignments and compliance states, and the evaluation step 5
///     asks for — <see cref="IPolicyCatalogGrain" />, keyed by <see cref="GrainKeys.PolicyCatalog" />.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The compiled cache is per scope, and it is what bounds evaluation.</b> A write
///         evaluates the assignments on its resource group, its subscription and every management
///         group above it. Each scope's assignments, with their definitions' rules parsed, are held in
///         <see cref="compiled" /> the first time a write asks and reused by every write after it; an
///         assignment written at a scope drops that scope's entry, and a definition written drops every
///         entry holding an assignment that names it. So the steady state is dictionary reads and
///         condition evaluation — bounded by <see cref="PolicyCondition.MaxNodes" /> per rule — and no
///         JSON parse of a rule on the request path at all.
///     </para>
///     <para>
///         ⚠
///         <b>
///             The management-group walk runs only when the tenant has an assignment at a management
///             group.
///         </b> The subscription's group comes from step 1, which already read the subscription;
///         the groups above it are one <see cref="IManagementGroupGrain" /> read each, at most
///         <c>IManagementGroupGrain.MaxDepth</c>. A tenant that assigns only at subscriptions and
///         resource groups — most of them — never pays for it. It is not cached, because a
///         subscription moves between groups by <c>SetManagementGroupAsync</c> and a cached chain would
///         keep applying the old group's policies to it after the move.
///     </para>
/// </remarks>
public sealed class PolicyCatalogGrain(
    [PersistentState("policyCatalog", StorageTiers.Durable)]
    IPersistentState<PolicyCatalogState> state,
    IGrainFactory grains,
    IClock clock,
    ILogger<PolicyCatalogGrain> logger
)
    : Grain, IPolicyCatalogGrain {
    /// <summary>Each scope's assignments with their rules parsed, by scope path. See the remarks.</summary>
    readonly Dictionary<string, ImmutableArray<CompiledAssignment>> compiled = new(StringComparer.Ordinal);

    Guid tenantId;
    long compilations;
    long hits;

    /// <inheritdoc />
    public override Task OnActivateAsync(CancellationToken cancellationToken) {
        tenantId = ResourceManagerGrainKeys.TenantOf(this);

        var key = ResourceManagerGrainKeys.Decode(this, GrainKeyKind.PolicyCatalog);

        // ⚠ The tenant is spelled twice in the physical key — the qualification and the payload — and
        // the two must agree, or this activation would hold one tenant's policy under another's name.
        if (key.Id != tenantId) {
            throw new InvalidOperationException(
                $"PolicyCatalogGrain was activated for tenant {tenantId:D} with the key of tenant {key.Id:D}. "
                + "Reach it with ForTenant(t).GetGrain<IPolicyCatalogGrain>(GrainKeys.PolicyCatalog(t)) for "
                + "the same t."
            );
        }

        return Task.CompletedTask;
    }

    // ── Definitions ────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<Result<PolicyDefinitionWrite>> PutDefinitionAsync(PolicyDefinitionRecord definition) {
        ArgumentNullException.ThrowIfNull(definition);

        var address = Own(definition.Path, PolicyObjectKind.Definition);
        if (address.TryGetError(out var addressError)) {
            return Result<PolicyDefinitionWrite>.Failure(addressError);
        }

        // ⚠ Parsed again here, whatever the manager checked: this grain is the single writer, and a
        // rule it stored without parsing would fail to compile at evaluation time — on somebody
        // else's write, with the definition's author long gone.
        var rule = PolicyRule.Parse(definition.Rule, "/properties/policyRule");
        if (rule.TryGetError(out var ruleError)) {
            return Result<PolicyDefinitionWrite>.Failure(ruleError);
        }

        var path = address.GetValueOrThrow().Path;
        var normalized = definition with { Path = path, Scope = address.GetValueOrThrow().Scope.Path, Name = address.GetValueOrThrow().Name };

        if (state.State.Definitions.TryGetValue(path, out var existing)
            && string.Equals(existing.Rule, normalized.Rule, StringComparison.Ordinal)
            && string.Equals(existing.DisplayName, normalized.DisplayName, StringComparison.Ordinal)
            && string.Equals(existing.Description, normalized.Description, StringComparison.Ordinal)) {
            return Result<PolicyDefinitionWrite>.Success(new() { Record = existing, Created = false });
        }

        var stored = normalized with { Version = (existing?.Version ?? 0) + 1 };
        state.State.Definitions[path] = stored;

        // ⚠ A CHANGED RULE TAKES ITS VERDICTS WITH IT. Every state recorded under this definition
        // describes the old rule; leaving them would have policyStates vouch for compliance with a
        // rule nobody evaluated. They come back on each resource's next write.
        if (existing is not null && !string.Equals(existing.Rule, stored.Rule, StringComparison.Ordinal)) {
            DropStates(x => string.Equals(x.DefinitionPath, path, StringComparison.Ordinal));
        }

        InvalidateDefinition(path);
        await state.WriteStateAsync();

        return Result<PolicyDefinitionWrite>.Success(new() { Record = stored, Created = existing is null });
    }

    /// <inheritdoc />
    public Task<Result<PolicyDefinitionRecord>> GetDefinitionAsync(string path) =>
        Task.FromResult(
            state.State.Definitions.TryGetValue(path ?? "", out var found)
                ? Result<PolicyDefinitionRecord>.Success(found)
                : NotFound<PolicyDefinitionRecord>(path ?? "")
        );

    /// <inheritdoc />
    public async Task<Result> DeleteDefinitionAsync(string path) {
        if (!state.State.Definitions.ContainsKey(path ?? "")) {
            return Result.Success;
        }

        var holders = state.State.Assignments.Values
            .Where(x => string.Equals(x.DefinitionPath, path, StringComparison.Ordinal))
            .Select(static x => x.Path)
            .Order(StringComparer.Ordinal)
            .ToArray();

        if (holders.Length > 0) {
            return Result.Failure(
                ErrorCode.Conflict,
                $"'{path}' is assigned by {holders.Length.ToString(CultureInfo.InvariantCulture)} assignment(s) — "
                + $"{string.Join(", ", holders)} — so it cannot be deleted. Delete the assignments first. "
                + "Deleting a definition out from under its assignments would lift an enforcement nobody "
                + "lifted on purpose."
            );
        }

        state.State.Definitions.Remove(path!);
        InvalidateDefinition(path!);
        await state.WriteStateAsync();
        return Result.Success;
    }

    /// <inheritdoc />
    public Task<Result<ImmutableArray<PolicyDefinitionRecord>>> ListDefinitionsAsync(string scopePath) =>
        Task.FromResult(
            Result<ImmutableArray<PolicyDefinitionRecord>>.Success(
                [
                    .. state.State.Definitions.Values
                        .Where(x => string.Equals(x.Scope, scopePath, StringComparison.Ordinal))
                        .OrderBy(static x => x.Path, StringComparer.Ordinal)
                ]
            )
        );

    // ── Assignments ────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<Result<PolicyAssignmentWrite>> PutAssignmentAsync(PolicyAssignmentRecord assignment) {
        ArgumentNullException.ThrowIfNull(assignment);

        var address = Own(assignment.Path, PolicyObjectKind.Assignment);
        if (address.TryGetError(out var addressError)) {
            return Result<PolicyAssignmentWrite>.Failure(addressError);
        }

        // ⚠ The definition exists, in this tenant, checked in the same turn as the write — which is
        // what makes "an assignment names a definition that exists" an invariant rather than a race
        // with DeleteDefinitionAsync.
        if (!state.State.Definitions.ContainsKey(assignment.DefinitionPath)) {
            return Result<PolicyAssignmentWrite>.Failure(
                ErrorCode.InvalidRequestBody,
                $"'{assignment.DefinitionPath}' is not a policy definition in this tenant, so it cannot be "
                + "assigned. Create the definition first.",
                "/properties/" + PolicyBodyProperties.PolicyDefinitionId
            );
        }

        var parsed = address.GetValueOrThrow();
        var path = parsed.Path;
        var normalized = assignment with { Path = path, Scope = parsed.Scope.Path, Name = parsed.Name };

        if (state.State.Assignments.TryGetValue(path, out var existing)
            && string.Equals(existing.DefinitionPath, normalized.DefinitionPath, StringComparison.Ordinal)
            && string.Equals(existing.DisplayName, normalized.DisplayName, StringComparison.Ordinal)
            && existing.NotScopes.SequenceEqual(normalized.NotScopes, StringComparer.Ordinal)) {
            return Result<PolicyAssignmentWrite>.Success(new() { Record = existing, Created = false });
        }

        var stored = normalized with { Version = (existing?.Version ?? 0) + 1 };
        state.State.Assignments[path] = stored;

        // See IPolicyCatalogGrain.PutAssignmentAsync: a replaced assignment's verdicts describe
        // something that no longer applies.
        DropStates(x => string.Equals(x.AssignmentPath, path, StringComparison.Ordinal));
        compiled.Remove(stored.Scope);

        await state.WriteStateAsync();

        return Result<PolicyAssignmentWrite>.Success(new() { Record = stored, Created = existing is null });
    }

    /// <inheritdoc />
    public Task<Result<PolicyAssignmentRecord>> GetAssignmentAsync(string path) =>
        Task.FromResult(
            state.State.Assignments.TryGetValue(path ?? "", out var found)
                ? Result<PolicyAssignmentRecord>.Success(found)
                : NotFound<PolicyAssignmentRecord>(path ?? "")
        );

    /// <inheritdoc />
    public async Task<Result> DeleteAssignmentAsync(string path) {
        if (!state.State.Assignments.Remove(path ?? "", out var removed)) {
            return Result.Success;
        }

        DropStates(x => string.Equals(x.AssignmentPath, path, StringComparison.Ordinal));
        compiled.Remove(removed.Scope);
        await state.WriteStateAsync();
        return Result.Success;
    }

    /// <inheritdoc />
    public Task<Result<ImmutableArray<PolicyAssignmentRecord>>> ListAssignmentsAsync(string scopePath) =>
        Task.FromResult(
            Result<ImmutableArray<PolicyAssignmentRecord>>.Success(
                [
                    .. state.State.Assignments.Values
                        .Where(x => string.Equals(x.Scope, scopePath, StringComparison.Ordinal))
                        .OrderBy(static x => x.Path, StringComparer.Ordinal)
                ]
            )
        );

    // ── Evaluation ─────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<Result<PolicyEvaluation>> EvaluateAsync(PolicySubject subject) {
        ArgumentNullException.ThrowIfNull(subject);

        var hadStates = state.State.States.ContainsKey(subject.ResourcePath);

        // The cheap exit, and the common one: a tenant with no assignments at all.
        if (state.State.Assignments.Count == 0) {
            return Result<PolicyEvaluation>.Success(new() { HadStates = hadStates });
        }

        JsonObject document;
        try {
            document = JsonNode.Parse(subject.Document) as JsonObject ?? new JsonObject();
        } catch (JsonException exception) {
            return Result<PolicyEvaluation>.Failure(
                ErrorCode.InvalidRequestBody,
                $"The body handed to policy evaluation is not valid JSON: {exception.Message}"
            );
        }

        var scopes = await ScopesAboveAsync(subject);
        if (scopes.TryGetError(out var scopeError)) {
            return Result<PolicyEvaluation>.Failure(scopeError);
        }

        var chain = scopes.GetValueOrThrow();

        var applicable = chain
            .SelectMany(x => Compiled(x))
            .Where(x => !x.Excludes(subject.ResourcePath, chain) && x.Rule.AppliesTo(subject.Operation))
            .ToArray();

        var facts = new PolicyFacts(subject.ResourceType, subject.ResourceName, subject.Operation, subject.Action, document);
        var entries = ImmutableArray.CreateBuilder<PolicyTraceEntry>(applicable.Length);
        var modifications = ImmutableArray.CreateBuilder<PolicyModificationRecord>();

        // ── Modify first — PolicyRule's remarks say why the order is modify, deny, audit ─────────
        foreach (var candidate in applicable.Where(static x => x.Rule.Effect == PolicyRuleEffect.Modify)) {
            var matched = candidate.Rule.Matches(facts);
            var applied = ImmutableArray.CreateBuilder<string>();

            if (matched) {
                foreach (var operation in candidate.Rule.Operations) {
                    var made = operation.ApplyTo(document, document);
                    if (made.TryGetError(out var madeError)) {
                        return Result<PolicyEvaluation>.Failure(
                            ErrorCode.PolicyViolation,
                            $"Policy assignment '{candidate.Assignment.Path}' (definition "
                            + $"'{candidate.Definition.Path}') could not rewrite the body: {madeError.Message}"
                        );
                    }

                    if (!made.GetValueOrThrow()) {
                        continue;
                    }

                    applied.Add(operation.Render());
                    modifications.Add(
                        new() {
                            AssignmentPath = candidate.Assignment.Path,
                            Operation = operation.Name,
                            Field = operation.Field.Value,
                            Value = operation.Value?.ToJsonString() ?? "null"
                        }
                    );
                }
            }

            entries.Add(Entry(candidate, matched, applied.ToImmutable()));
        }

        // ── Deny, against the body as the modify rules left it ─────────────────────────────────
        foreach (var candidate in applicable.Where(static x => x.Rule.Effect == PolicyRuleEffect.Deny)) {
            var matched = candidate.Rule.Matches(facts);
            entries.Add(Entry(candidate, matched, []));

            if (matched) {
                return Result<PolicyEvaluation>.Success(
                    new() {
                        Entries = entries.ToImmutable(),
                        Modifications = modifications.ToImmutable(),
                        HadStates = hadStates,
                        Denial = new() {
                            AssignmentPath = candidate.Assignment.Path,
                            DefinitionPath = candidate.Definition.Path,
                            AssignmentName = DisplayOf(candidate.Assignment.DisplayName, candidate.Assignment.Name),
                            DefinitionName = DisplayOf(candidate.Definition.DisplayName, candidate.Definition.Name),
                            Target = candidate.Rule.Condition.Fields
                                    .Where(static x => x.IsPointer)
                                    .Select(static x => x.Value)
                                    .FirstOrDefault()
                                ?? ""
                        }
                    }
                );
            }
        }

        // ── Audit, recording the state the resource is left in ─────────────────────────────────
        var states = ImmutableArray.CreateBuilder<PolicyStateRecord>();
        var now = clock.UtcNow;

        foreach (var candidate in applicable.Where(static x => x.Rule.Effect == PolicyRuleEffect.Audit)) {
            var matched = candidate.Rule.Matches(facts);
            entries.Add(Entry(candidate, matched, []));

            states.Add(
                new() {
                    ResourcePath = subject.ResourcePath,
                    ResourceType = subject.ResourceType,
                    AssignmentPath = candidate.Assignment.Path,
                    DefinitionPath = candidate.Definition.Path,
                    State = matched ? PolicyComplianceState.NonCompliant : PolicyComplianceState.Compliant,
                    Since = now,
                    AssignmentVersion = candidate.Assignment.Version,
                    DefinitionVersion = candidate.Definition.Version
                }
            );
        }

        return Result<PolicyEvaluation>.Success(
            new() {
                Entries = entries.ToImmutable(),
                Modifications = modifications.ToImmutable(),
                States = states.ToImmutable(),
                HadStates = hadStates
            }
        );
    }

    // ── Compliance states ──────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<Result> RecordStatesAsync(string resourcePath, ImmutableArray<PolicyStateRecord> states) {
        ArgumentNullException.ThrowIfNull(resourcePath);

        // ⚠ ONLY VERDICTS ABOUT WHAT IS IN FORCE NOW. This call arrives after the write's step 9, a
        // different turn from the evaluation, and a policy write can land between the two — the
        // interface's remarks. A verdict whose assignment is gone, or whose assignment or rule has a
        // newer version than the one it was evaluated under, is dropped here rather than written back.
        var incoming = (states.IsDefault ? [] : states)
            .Where(x => string.Equals(x.ResourcePath, resourcePath, StringComparison.Ordinal) && InForce(x))
            .ToImmutableArray();

        state.State.States.TryGetValue(resourcePath, out var existing);

        if (incoming.IsEmpty) {
            if (existing is null) {
                return Result.Success;
            }

            state.State.States.Remove(resourcePath);
            await state.WriteStateAsync();
            return Result.Success;
        }

        // ⚠ A verdict that did not change keeps the time it last changed — PolicyStateRecord.Since —
        // so an unchanged set compares equal and is not written at all.
        var merged = incoming
            .Select(x => existing?.FirstOrDefault(y => SameVerdict(x, y)) is { } kept ? x with { Since = kept.Since } : x)
            .OrderBy(static x => x.AssignmentPath, StringComparer.Ordinal)
            .ToList();

        if (existing is not null && existing.SequenceEqual(merged)) {
            return Result.Success;
        }

        state.State.States[resourcePath] = merged;
        await state.WriteStateAsync();
        return Result.Success;
    }

    /// <inheritdoc />
    public async Task<Result> ForgetResourceAsync(string resourcePath) {
        if (!state.State.States.Remove(resourcePath ?? "")) {
            return Result.Success;
        }

        await state.WriteStateAsync();
        return Result.Success;
    }

    /// <inheritdoc />
    public async Task<Result> ForgetScopeAsync(string scopePath) {
        var parsed = ScopeId.ParsePath(scopePath ?? "");
        if (parsed.TryGetError(out var parseError)) {
            return Result.Failure(parseError);
        }

        var scope = parsed.GetValueOrThrow();

        // ⚠ The two kinds a delete reaches today, and only in this tenant. A tenant's scope is every
        // path here and a subscription's delete isn't built; either would be a far wider forget than a
        // caller of this method means, so it's refused by name rather than done.
        if (scope.TenantId != tenantId || scope.Kind is not (ScopeKind.ManagementGroup or ScopeKind.ResourceGroup)) {
            return Result.Failure(
                ErrorCode.InvalidResourceId,
                $"'{scope.Path}' is not a management group or a resource group in tenant {tenantId:D}, so the "
                + "policy catalog forgets nothing for it."
            );
        }

        var path = scope.Path;

        var definitions = state.State.Definitions.Values
            .Where(x => string.Equals(x.Scope, path, StringComparison.Ordinal))
            .Select(static x => x.Path)
            .ToHashSet(StringComparer.Ordinal);

        var assignments = state.State.Assignments.Values
            .Where(x => string.Equals(x.Scope, path, StringComparison.Ordinal) || definitions.Contains(x.DefinitionPath))
            .ToArray();

        var beneath = path + "/";
        var hasStates = state.State.States.Keys.Any(x => x.StartsWith(beneath, StringComparison.Ordinal));

        if (definitions.Count == 0 && assignments.Length == 0 && !hasStates) {
            return Result.Success;
        }

        foreach (var assignment in assignments) {
            if (!string.Equals(assignment.Scope, path, StringComparison.Ordinal)) {
                // IPolicyCatalogGrain.ForgetScopeAsync's remarks: an assignment elsewhere of a definition
                // that went with its scope. Warning, because an enforcement nobody at that scope lifted
                // has just been lifted.
                logger.LogWarning(
                    "Policy assignment '{Assignment}' is removed with the deleted scope '{Scope}' because it "
                    + "names '{Definition}', a definition that scope held.",
                    assignment.Path,
                    path,
                    assignment.DefinitionPath
                );
            }

            state.State.Assignments.Remove(assignment.Path);
            compiled.Remove(assignment.Scope);
        }

        foreach (var definition in definitions) {
            state.State.Definitions.Remove(definition);
        }

        var removed = assignments.Select(static x => x.Path).ToHashSet(StringComparer.Ordinal);
        DropStates(x => removed.Contains(x.AssignmentPath));

        foreach (var resource in state.State.States.Keys.Where(x => x.StartsWith(beneath, StringComparison.Ordinal)).ToArray()) {
            state.State.States.Remove(resource);
        }

        await state.WriteStateAsync();
        return Result.Success;
    }

    /// <inheritdoc />
    public Task<Result<ImmutableArray<PolicyStateRecord>>> ListStatesAsync(
        string scopePath,
        ImmutableArray<Guid> subscriptions
    ) {
        var parsed = ScopeId.ParsePath(scopePath);
        if (parsed.TryGetError(out var error)) {
            return Task.FromResult(Result<ImmutableArray<PolicyStateRecord>>.Failure(error));
        }

        var scope = parsed.GetValueOrThrow();

        // ⚠ A '/' after the prefix, so the group `prod` never collects the states of `production`.
        string[] prefixes = scope.Kind == ScopeKind.ManagementGroup
            ? [.. (subscriptions.IsDefault ? [] : subscriptions).Select(x => ScopeId.Subscription(tenantId, x).Path + "/")]
            : [scope.Path + "/"];

        return Task.FromResult(
            Result<ImmutableArray<PolicyStateRecord>>.Success(
                [
                    .. state.State.States
                        .Where(x => prefixes.Any(p => x.Key.StartsWith(p, StringComparison.Ordinal)))
                        .SelectMany(static x => x.Value)
                        .OrderBy(static x => x.ResourcePath, StringComparer.Ordinal)
                        .ThenBy(static x => x.AssignmentPath, StringComparer.Ordinal)
                ]
            )
        );
    }

    /// <inheritdoc />
    public Task<PolicyCatalogStatistics> GetStatisticsAsync() => Task.FromResult(new PolicyCatalogStatistics(compilations, hits));

    /// <inheritdoc />
    public Task DeactivateAsync() {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }

    // ── Internals ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     The scopes whose assignments reach the subject, from the top of the tree down: management
    ///     groups root first, then the subscription, then the resource group.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Top down, and the order is what "the most specific scope wins" means.</b> Two modify
    ///     rules that replace the same field both apply, in this order, so the one nearest the resource
    ///     writes last and its value is the one stored — the owner of a resource group can refine what
    ///     the owner of its subscription set. A deny cannot be refined away: every deny that matches
    ///     refuses, wherever it sits.
    /// </remarks>
    async Task<Result<ImmutableArray<string>>> ScopesAboveAsync(PolicySubject subject) {
        var chain = new List<string>();

        if (subject.ManagementGroup.Length > 0 && HasManagementGroupAssignments()) {
            var tenant = grains.ForTenant(tenantId.ToString("D", CultureInfo.InvariantCulture));
            var name = subject.ManagementGroup;

            for (var hop = 0; hop < IManagementGroupGrain.MaxDepth && name.Length > 0; hop++) {
                chain.Add(ScopeId.ManagementGroupOf(tenantId, name).Path);

                var group = await tenant.GetGrain<IManagementGroupGrain>(GrainKeys.ManagementGroup(name)).GetAsync();

                // ⚠ A GROUP THAT IS GONE ENDS THE WALK; ANY OTHER FAILURE REFUSES THE WRITE. NotFound
                // means the subscription names a group deleted under it — #39's delete refuses while
                // the group holds a subscription, so this is a race with that delete, the group's own
                // policy went with it (ForgetScopeAsync), and its parent can't be read off a record
                // that no longer exists. Refusing would stop every write in the subscription for as
                // long as the dangling name lasted. So the groups ABOVE a deleted group are skipped
                // for the length of that race, and that is the one place step 5 does not fail closed.
                // A failure of any other kind is a group that exists and didn't answer, and skipping
                // it would skip every assignment above it for as long as it kept failing.
                if (group.TryGetError(out var groupError) && groupError.Code != ErrorCode.ResourceNotFound) {
                    return Result<ImmutableArray<string>>.Failure(
                        ErrorCode.InternalError,
                        $"Management group '{name}' above '{subject.ResourcePath}' could not be read for policy "
                        + $"evaluation, so the request is refused rather than let past its assignments: {groupError.Message}"
                    );
                }

                if (group.IsFailure) {
                    logger.LogWarning(
                        "Management group '{Group}' above {Resource} did not answer the policy walk: {Message}",
                        name,
                        subject.ResourcePath,
                        group.Error?.Message
                    );

                    break;
                }

                name = group.GetValueOrThrow().Parent;
            }

            chain.Reverse();
        }

        chain.Add(ScopeId.Subscription(tenantId, subject.SubscriptionId).Path);
        chain.Add(ScopeId.Group(tenantId, subject.SubscriptionId, subject.ResourceGroup).Path);

        return Result<ImmutableArray<string>>.Success([.. chain]);
    }

    bool HasManagementGroupAssignments() {
        // ScopeId spells the path; a one-letter group name cut back off leaves exactly the prefix every
        // management group scope in this tenant carries, without a second copy of the literal here.
        var probe = ScopeId.ManagementGroupOf(tenantId, "a").Path;
        var prefix = probe[..^1];

        return state.State.Assignments.Values.Any(x => x.Scope.StartsWith(prefix, StringComparison.Ordinal));
    }

    ImmutableArray<CompiledAssignment> Compiled(string scope) {
        if (compiled.TryGetValue(scope, out var cached)) {
            hits++;
            return cached;
        }

        var built = ImmutableArray.CreateBuilder<CompiledAssignment>();

        foreach (var assignment in state.State.Assignments.Values
                     .Where(x => string.Equals(x.Scope, scope, StringComparison.Ordinal))
                     .OrderBy(static x => x.Path, StringComparer.Ordinal)) {
            if (!state.State.Definitions.TryGetValue(assignment.DefinitionPath, out var definition)) {
                // Unreachable while this grain is the single writer: PutAssignmentAsync refuses an
                // unknown definition and DeleteDefinitionAsync refuses an assigned one. Logged rather
                // than thrown, so a state restored from a bad backup cannot stop every write.
                logger.LogError(
                    "Policy assignment '{Assignment}' names '{Definition}', which the catalog does not hold; "
                    + "the assignment is skipped.",
                    assignment.Path,
                    assignment.DefinitionPath
                );

                continue;
            }

            var rule = PolicyRule.Parse(definition.Rule, "/properties/policyRule");
            if (rule.TryGetError(out var ruleError)) {
                logger.LogError(
                    "Policy definition '{Definition}' no longer parses and its assignment '{Assignment}' is "
                    + "skipped: {Message}",
                    definition.Path,
                    assignment.Path,
                    ruleError.Message
                );

                continue;
            }

            built.Add(new(assignment, definition, rule.GetValueOrThrow()));
        }

        var result = built.ToImmutable();
        compiled[scope] = result;
        compilations++;
        return result;
    }

    void InvalidateDefinition(string definitionPath) {
        var scopes = state.State.Assignments.Values
            .Where(x => string.Equals(x.DefinitionPath, definitionPath, StringComparison.Ordinal))
            .Select(static x => x.Scope)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        foreach (var scope in scopes) {
            compiled.Remove(scope);
        }
    }

    void DropStates(Func<PolicyStateRecord, bool> which) {
        foreach (var resource in state.State.States.Keys.ToArray()) {
            var kept = state.State.States[resource].Where(x => !which(x)).ToList();

            if (kept.Count == 0) {
                state.State.States.Remove(resource);
            } else {
                state.State.States[resource] = kept;
            }
        }
    }

    Result<PolicyAddress> Own(string path, PolicyObjectKind kind) {
        var parsed = PolicyAddress.ParsePath(path);
        if (parsed.TryGetError(out var error)) {
            return Result<PolicyAddress>.Failure(error);
        }

        var address = parsed.GetValueOrThrow();

        // ⚠ The address must be this tenant's and of the kind the method stores. A manager bug that
        // handed this grain another tenant's path would otherwise file that tenant's object here,
        // where it would govern this tenant's writes under a foreign name.
        return address.Kind != kind || address.IsCollection || address.TenantId != tenantId
            ? Result<PolicyAddress>.Failure(
                ErrorCode.InvalidResourceId,
                $"'{path}' is not a {kind} address in tenant {tenantId:D}."
            )
            : Result<PolicyAddress>.Success(address);
    }

    /// <summary>Whether the assignment and the definition a verdict was evaluated under are the ones stored now.</summary>
    bool InForce(PolicyStateRecord verdict) =>
        state.State.Assignments.TryGetValue(verdict.AssignmentPath, out var assignment)
        && assignment.Version == verdict.AssignmentVersion
        && string.Equals(assignment.DefinitionPath, verdict.DefinitionPath, StringComparison.Ordinal)
        && state.State.Definitions.TryGetValue(verdict.DefinitionPath, out var definition)
        && definition.Version == verdict.DefinitionVersion;

    static bool SameVerdict(PolicyStateRecord incoming, PolicyStateRecord existing) =>
        string.Equals(incoming.AssignmentPath, existing.AssignmentPath, StringComparison.Ordinal)
        && string.Equals(incoming.DefinitionPath, existing.DefinitionPath, StringComparison.Ordinal)
        && string.Equals(incoming.ResourceType, existing.ResourceType, StringComparison.Ordinal)
        && incoming.State == existing.State;

    static PolicyTraceEntry Entry(CompiledAssignment candidate, bool matched, ImmutableArray<string> applied) =>
        new() {
            AssignmentPath = candidate.Assignment.Path,
            DefinitionPath = candidate.Definition.Path,
            Effect = candidate.Rule.EffectName,
            Matched = matched,
            Applied = applied
        };

    static string DisplayOf(string displayName, string name) => displayName.Length > 0 ? displayName : name;

    static Result<T> NotFound<T>(string path) where T : notnull =>
        Result<T>.Failure(ErrorCode.ResourceNotFound, $"'{path}' does not exist.");

    /// <summary>An assignment with its definition and its parsed rule — one entry of the per-scope cache.</summary>
    sealed record CompiledAssignment(PolicyAssignmentRecord Assignment, PolicyDefinitionRecord Definition, PolicyRule Rule) {
        /// <summary>Whether one of the assignment's exclusions covers the resource.</summary>
        /// <param name="resourcePath">The resource's canonical path.</param>
        /// <param name="chain">
        ///     The scopes above it, as the walk found them. ⚠ A management group excluded from an
        ///     assignment above it is not a prefix of anything — its descendants are decided by the tree,
        ///     not by their spelling — so it excludes the resource when it is on the resource's chain.
        /// </param>
        public bool Excludes(string resourcePath, ImmutableArray<string> chain) =>
            Assignment.NotScopes.Any(x =>
                string.Equals(resourcePath, x, StringComparison.Ordinal)
                || resourcePath.StartsWith(x + "/", StringComparison.Ordinal)
                || chain.Contains(x, StringComparer.Ordinal)
            );
    }
}
