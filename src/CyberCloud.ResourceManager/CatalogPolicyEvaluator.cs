using CyberCloud.ResourceManager.Contracts.Registry;
using Microsoft.Extensions.Logging;
using Orleans.Multitenant;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.ResourceManager;

/// <summary>
///     Step 5, answered by the tenant's <see cref="IPolicyCatalogGrain" /> — the policy engine of
///     docs/plan/08 § Policy (issue #46).
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>One grain call per request, whatever the depth of the tree.</b> The catalog holds
///         every scope's assignments and walks the management groups itself, only when the tenant has
///         an assignment at one — see <c>PolicyCatalogGrain</c>'s remarks. The write path pays one
///         more call, after the write is accepted, only when an audit produced or cleared a verdict.
///     </para>
///     <para>
///         ⚠ <b>It fails closed.</b> A catalog that cannot be reached is a refusal naming the reason,
///         not an allow: a write that passed a deny rule because the rule's store was down is the
///         enforcement failing open, and the steps around this one — the quota grain, the index grain —
///         would refuse the same write for the same outage a moment later anyway. So does a management
///         group in the walk that fails to answer, and a PATCH or an action whose stored body can't be
///         read. ⚠ Two absences don't refuse, and both are a record that is gone rather than a store
///         that failed: a management group deleted under a subscription ends the walk (the groups above
///         it can't be named), and a DELETE whose resource has no record judges an empty body.
///         <c>PolicyCatalogGrain.ScopesAboveAsync</c> and <c>ResourceManagerService.DeleteAsync</c> say
///         why at each.
///     </para>
///     <para>
///         ⚠ <b>Every grain reference goes through <c>ForTenant</c></b>, with the tenant from the
///         resource's own address — which step 1 has already checked is the caller's. That is the
///         whole of tenant isolation for policy: tenant B's writes reach tenant B's catalog, and tenant
///         A's assignments are not in it.
///     </para>
/// </remarks>
public sealed class CatalogPolicyEvaluator(IGrainFactory grains, ILogger<CatalogPolicyEvaluator> logger) : IPolicyEvaluator {
    /// <inheritdoc />
    public async Task<PolicyDecision> EvaluateAsync(
        PolicyEvaluationRequest request,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(request);

        var subject = new PolicySubject {
            ResourcePath = request.Id.CanonicalPath,
            SubscriptionId = request.Id.SubscriptionId,
            ResourceGroup = request.Id.ResourceGroup,
            ManagementGroup = request.ManagementGroup,
            ResourceType = request.Id.Type.ToString(),
            ResourceName = request.Id.Name,
            Operation = request.Operation,
            Action = request.Action,
            Document = request.Document
        };

        Result<PolicyEvaluation> evaluated;

        try {
            evaluated = await Catalog(request.Id.TenantId).EvaluateAsync(subject);
        } catch (Exception exception) when (exception is not OperationCanceledException) {
            logger.LogError(
                exception,
                "Policy evaluation for {Resource} could not reach the catalog; the request is refused.",
                request.Id.Path
            );

            evaluated = Result<PolicyEvaluation>.Failure(
                ErrorCode.InternalError,
                "Policy could not be evaluated for this request, so it is refused rather than let past rules "
                + "nobody checked. Retry; the failure is logged under the request's correlation id."
            );
        }

        if (evaluated.TryGetError(out var error)) {
            return new() { Effect = PolicyEffect.Deny, Error = error };
        }

        var evaluation = evaluated.GetValueOrThrow();
        var records = evaluation.HadStates || !evaluation.States.IsDefaultOrEmpty;

        if (evaluation.Denial is { } denial) {
            return new() {
                Effect = PolicyEffect.Deny,
                Error = Refusal(request.Id, denial),
                Trace = evaluation.Entries,
                Modifications = evaluation.Modifications
            };
        }

        var effect = !evaluation.Modifications.IsDefaultOrEmpty
            ? PolicyEffect.Modify
            : evaluation.States.Any(static x => x.State == PolicyComplianceState.NonCompliant)
                ? PolicyEffect.Audit
                : PolicyEffect.Allow;

        return new() {
            Effect = effect,
            Trace = evaluation.Entries,
            Modifications = evaluation.Modifications,
            States = evaluation.States,
            RecordsCompliance = records
        };
    }

    /// <inheritdoc />
    public Task<Result> RecordComplianceAsync(ResourceId id, PolicyDecision decision, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(decision);

        return Guarded(() => Catalog(id.TenantId).RecordStatesAsync(id.CanonicalPath, decision.States));
    }

    /// <inheritdoc />
    public Task<Result> ForgetAsync(ResourceId id, CancellationToken cancellationToken = default) =>
        Guarded(() => Catalog(id.TenantId).ForgetResourceAsync(id.CanonicalPath));

    /// <summary>
    ///     A call whose failure the write path logs and survives — so a grain that threw becomes a
    ///     failed <see cref="Result" /> rather than an exception out of a write that already succeeded.
    /// </summary>
    async Task<Result> Guarded(Func<Task<Result>> call) {
        try {
            return await call();
        } catch (Exception exception) when (exception is not OperationCanceledException) {
            logger.LogError(exception, "A policy catalog call after an accepted write failed.");
            return Result.Failure(ErrorCode.InternalError, "The policy catalog could not be reached.");
        }
    }

    IPolicyCatalogGrain Catalog(Guid tenantId) =>
        grains.ForTenant(tenantId.ToString("D", CultureInfo.InvariantCulture))
            .GetGrain<IPolicyCatalogGrain>(GrainKeys.PolicyCatalog(tenantId));

    /// <summary>The <c>403</c> a deny rule produces — structured, naming the assignment and the definition.</summary>
    /// <remarks>
    ///     ⚠ <b>Named twice: in the sentence, and as two details a client can read without parsing it.</b>
    ///     The <c>target</c> is the first body pointer the rule tests, so a portal form can put the
    ///     refusal beside the field that caused it; a rule that tests only facts — the type, the
    ///     operation — has no field to point at and the target is absent.
    ///     <para>
    ///         ⚠ <b>It names scopes the caller may not be able to read</b> — an assignment on a
    ///         management group, a definition at the tenant — to a caller who holds rights only on the
    ///         resource group. That departs from the 404-and-say-nothing rule for unreadable scopes,
    ///         deliberately and as Azure does: a refusal that didn't say which rule refused is one
    ///         nobody can act on, and the names are all it gives away, never the rule. docs/plan/08
    ///         § Policy records it.
    ///     </para>
    /// </remarks>
    static Error Refusal(ResourceId id, PolicyDenial denial) =>
        new(
            ErrorCode.PolicyViolation,
            $"Policy assignment '{denial.AssignmentName}' denies this request to '{id.Path}': its definition "
            + $"'{denial.DefinitionName}' matched. The assignment is '{denial.AssignmentPath}' and the "
            + $"definition '{denial.DefinitionPath}' — docs/plan/08 § Policy.",
            denial.Target.Length == 0 ? null : denial.Target,
            [
                new(ErrorCode.PolicyViolation, "policyAssignmentId: " + denial.AssignmentPath),
                new(ErrorCode.PolicyViolation, "policyDefinitionId: " + denial.DefinitionPath)
            ]
        );
}

/// <summary>
///     The bodies step 5 reads and rewrites — what a condition sees, and what a modify changes.
/// </summary>
static class PolicyDocuments {
    /// <summary>
    ///     A stored resource as a condition sees it: the whole superset, with the tag bag and the
    ///     location put back where a request body carries them.
    /// </summary>
    /// <param name="snapshot">The resource grain's snapshot, read with no pointer filter.</param>
    /// <remarks>
    ///     ⚠ <b>The snapshot's bag replaces any <c>tags</c> member the superset holds.</b> The grain
    ///     keeps the bag beside the superset, and a <c>PATCH</c> merges its <c>tags</c> into the
    ///     superset as well — where a later <c>PUT</c>, which replaces only the version's declared
    ///     pointers, leaves them. So the superset's copy can be stale, and a condition that read it
    ///     would judge tags the resource no longer has. The write path sends a <c>PATCH</c>'s bag from
    ///     this document merged with the patch, so the bag a rule sees is the bag the write leaves.
    /// </remarks>
    public static JsonObject Stored(ResourceSnapshot snapshot) {
        var document = Parse(snapshot.Body);

        document.Remove(TagRules.Name);
        if (!snapshot.Tags.IsEmpty) {
            var tags = new JsonObject();
            foreach (var (key, value) in snapshot.Tags.OrderBy(static x => x.Key, StringComparer.Ordinal)) {
                tags[key] = value;
            }

            document[TagRules.Name] = tags;
        }

        if (snapshot.Location.Length > 0) {
            document["location"] = snapshot.Location;
        }

        return document;
    }

    /// <summary>
    ///     The document a condition is evaluated against: <paramref name="prospective" /> with every
    ///     secret property of the schema removed. <see cref="PolicyEvaluationRequest" />'s remarks say why.
    /// </summary>
    /// <param name="prospective">The body as the write would leave the resource.</param>
    /// <param name="schema">The type's schema at the request's api-version.</param>
    public static string Evaluated(JsonObject prospective, ResourceSchema schema) {
        var copy = (JsonObject)prospective.DeepClone();

        foreach (var property in schema.Properties.Where(static x => x.Secret)) {
            JsonPointer.Remove(copy, property.JsonPointer);
        }

        return copy.ToJsonString();
    }

    /// <summary>RFC 7386 JSON Merge Patch, as the resource grain applies it at step 9.</summary>
    /// <remarks>
    ///     ⚠ The same algorithm as <c>ResourceGrain.MergePatch</c>, so the body a condition judges on a
    ///     <c>PATCH</c> is the body the grain will store. Merge patch has one reading and it is short;
    ///     the grain's copy stays private to the grain, which must not reach into this assembly's write
    ///     path for its own merge.
    /// </remarks>
    public static JsonObject MergePatch(JsonObject target, JsonObject patch) {
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

    /// <summary>Parses a body the write path has already validated as a JSON object.</summary>
    public static JsonObject Parse(string json) {
        try {
            return JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json) as JsonObject ?? new JsonObject();
        } catch (JsonException) {
            return new JsonObject();
        }
    }

    /// <summary>
    ///     Makes the catalog's rewrites on the body the write sends, keeping the prospective body in
    ///     step so an <c>add</c> sees what a <c>PATCH</c> did not repeat.
    /// </summary>
    /// <returns>
    ///     Success, or the <see cref="ErrorCode.PolicyViolation" /> a rewrite with nowhere to go — or
    ///     aimed at a secret property — produced.
    /// </returns>
    /// <remarks>
    ///     ⚠ <b>A rewrite that touches a secret property refuses the write.</b> The catalog applies
    ///     its rewrites to <see cref="Evaluated" />'s document, which has the secrets taken out, and
    ///     this applies them again to the real body, which has them in. On a secret the two disagree:
    ///     an <c>add</c> the trace records as made is a no-op over a secret the caller sent, and a
    ///     <c>replace</c> overwrites the caller's secret with a constant from the definition, which
    ///     anyone who can read the definition can read. Neither is a rewrite policy can make honestly,
    ///     so the write is refused naming the assignment and the property, and its owner changes the
    ///     rule. Found by the review of issue #46.
    /// </remarks>
    public static Result Apply(
        ImmutableArray<PolicyModificationRecord> modifications,
        JsonObject target,
        JsonObject prospective,
        ResourceSchema schema
    ) {
        foreach (var record in modifications) {
            var field = Core.Policy.PolicyField.Parse(record.Field, "");
            if (field.TryGetError(out var fieldError)) {
                return Result.Failure(fieldError);
            }

            if (SecretUnder(record.Field, schema) is { } secret) {
                return Result.Failure(
                    ErrorCode.PolicyViolation,
                    $"Policy assignment '{record.AssignmentPath}' rewrites '{record.Field}', which holds the "
                    + $"secret property '{secret}'. Policy is evaluated without secrets, so it can't rewrite "
                    + "one — change the definition's operations. docs/plan/08 § Policy.",
                    record.Field
                );
            }

            var kind = record.Operation == "add"
                ? Core.Policy.PolicyModificationKind.Add
                : Core.Policy.PolicyModificationKind.Replace;

            var operation = new Core.Policy.PolicyModification(kind, field.GetValueOrThrow(), JsonNode.Parse(record.Value));
            var applied = operation.ApplyTo(target, prospective);

            if (applied.TryGetError(out var applyError)) {
                return Result.Failure(
                    ErrorCode.PolicyViolation,
                    $"Policy assignment '{record.AssignmentPath}' could not rewrite the body: {applyError.Message}"
                );
            }
        }

        return Result.Success;
    }

    /// <summary>
    ///     The first secret property a rewrite of <paramref name="pointer" /> would write — the
    ///     pointer itself, one beneath it, or one it sits beneath — or <see langword="null" />.
    /// </summary>
    static string? SecretUnder(string pointer, ResourceSchema schema) =>
        schema.Properties
            .Where(static x => x.Secret)
            .Select(static x => x.JsonPointer)
            .FirstOrDefault(secret => string.Equals(secret, pointer, StringComparison.Ordinal)
                || secret.StartsWith(pointer + "/", StringComparison.Ordinal)
                || pointer.StartsWith(secret + "/", StringComparison.Ordinal)
            );
}
