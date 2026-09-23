using Orleans.Concurrency;
using System.Collections.Immutable;

namespace CyberCloud.ResourceManager.Contracts;

/// <summary>
///     A tenant's policy: its definitions, its assignments at every scope, the compliance its audits
///     recorded, and the evaluation step 5 of the write path asks for. Keyed by
///     <see cref="GrainKeys.PolicyCatalog" />.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The single writer for all three, and that is what keeps the invariants in one turn.</b>
///         An assignment names a definition that exists; a definition an assignment names cannot be
///         deleted; a deleted assignment takes its compliance states with it. Each is a check and a
///         write against state this grain holds, so none of them has a window another caller can get
///         into — which is also why the evaluation lives here rather than in a stateless worker that
///         would have to be told when its copy went stale.
///     </para>
///     <para>
///         ⚠ <b>Evaluation is <see cref="EvaluateAsync" />, and it is bounded.</b> The compiled rules
///         are cached per scope and dropped when an assignment at that scope, or a definition one of
///         them names, is written — so a write pays for parsing a rule once per change rather than once
///         per request. <see cref="GetStatisticsAsync" /> exists so a test can see the cache work.
///     </para>
/// </remarks>
[Alias("CyberCloud.ResourceManager.IPolicyCatalogGrain")]
public interface IPolicyCatalogGrain : IGrainWithStringKey {
    /// <summary>Creates or replaces a definition.</summary>
    /// <param name="definition">The definition. Its rule is parsed again here, whatever the caller checked.</param>
    /// <returns>The stored record, and whether it was created.</returns>
    Task<Result<PolicyDefinitionWrite>> PutDefinitionAsync(PolicyDefinitionRecord definition);

    /// <summary>One definition, or <see cref="ErrorCode.ResourceNotFound" />.</summary>
    /// <param name="path">The definition's canonical address.</param>
    [ReadOnly]
    Task<Result<PolicyDefinitionRecord>> GetDefinitionAsync(string path);

    /// <summary>
    ///     Deletes a definition. Success for one already gone; <see cref="ErrorCode.Conflict" />,
    ///     naming them, while an assignment names it.
    /// </summary>
    /// <param name="path">The definition's canonical address.</param>
    Task<Result> DeleteDefinitionAsync(string path);

    /// <summary>The definitions directly on one scope, ordered by address.</summary>
    /// <param name="scopePath">The scope's path.</param>
    [ReadOnly]
    Task<Result<ImmutableArray<PolicyDefinitionRecord>>> ListDefinitionsAsync(string scopePath);

    /// <summary>
    ///     Creates or replaces an assignment. <see cref="ErrorCode.InvalidRequestBody" /> when its
    ///     definition does not exist in this tenant.
    /// </summary>
    /// <param name="assignment">The assignment.</param>
    /// <returns>The stored record, and whether it was created.</returns>
    /// <remarks>
    ///     ⚠ Replacing an assignment drops the compliance states it recorded: they describe a rule, a
    ///     scope or an exclusion list that no longer applies, and a state the platform cannot vouch for
    ///     is worse than no state.
    /// </remarks>
    Task<Result<PolicyAssignmentWrite>> PutAssignmentAsync(PolicyAssignmentRecord assignment);

    /// <summary>One assignment, or <see cref="ErrorCode.ResourceNotFound" />.</summary>
    /// <param name="path">The assignment's canonical address.</param>
    [ReadOnly]
    Task<Result<PolicyAssignmentRecord>> GetAssignmentAsync(string path);

    /// <summary>Deletes an assignment and the states it recorded. Success for one already gone.</summary>
    /// <param name="path">The assignment's canonical address.</param>
    Task<Result> DeleteAssignmentAsync(string path);

    /// <summary>The assignments directly on one scope, ordered by address.</summary>
    /// <param name="scopePath">The scope's path.</param>
    [ReadOnly]
    Task<Result<ImmutableArray<PolicyAssignmentRecord>>> ListAssignmentsAsync(string scopePath);

    /// <summary>
    ///     Evaluates every assignment that applies to one request — step 5 of docs/plan/08 § The
    ///     write path, end to end.
    /// </summary>
    /// <param name="subject">The request, the resource and its body.</param>
    /// <returns>What happened, in the order it happened: modify, then deny, then audit.</returns>
    /// <remarks>
    ///     ⚠ <b><c>[ReadOnly]</c>, so evaluations interleave.</b> It is on the path of every write in
    ///     the tenant and changes nothing; a write to a definition or an assignment waits for the
    ///     evaluations in flight and they wait for it, which is the only ordering that keeps an
    ///     evaluation from reading half a change.
    /// </remarks>
    [ReadOnly]
    Task<Result<PolicyEvaluation>> EvaluateAsync(PolicySubject subject);

    /// <summary>
    ///     Replaces one resource's audit states with the ones its accepted write produced.
    /// </summary>
    /// <param name="resourcePath">The resource's canonical path.</param>
    /// <param name="states">The states. Empty clears the resource's states.</param>
    /// <remarks>
    ///     ⚠ <b>Called after the write is accepted, never from <see cref="EvaluateAsync" />.</b> A
    ///     state recorded at step 5 would describe a body that step 6 or 7 might still refuse.
    ///     ⚠ <b>A state that has not changed is not written</b> — see
    ///     <see cref="PolicyStateRecord.Since" /> — so re-applying the same body does not make every
    ///     write a durable write here.
    /// </remarks>
    Task<Result> RecordStatesAsync(string resourcePath, ImmutableArray<PolicyStateRecord> states);

    /// <summary>Forgets every state one resource recorded. Called when its delete is accepted.</summary>
    /// <param name="resourcePath">The resource's canonical path.</param>
    Task<Result> ForgetResourceAsync(string resourcePath);

    /// <summary>
    ///     The states of every resource at or beneath one scope, ordered by resource then assignment.
    /// </summary>
    /// <param name="scopePath">
    ///     A resource group or a subscription path, or a management group path — which covers the
    ///     subscriptions beneath it as they are placed when the call runs.
    /// </param>
    /// <param name="subscriptions">
    ///     For a management group, the subscriptions beneath it, which the caller resolved; ignored
    ///     otherwise. ⚠ Passed in rather than walked here, because the tree belongs to the tenancy
    ///     grains and a listing is not the place to pay for a walk twice.
    /// </param>
    [ReadOnly]
    Task<Result<ImmutableArray<PolicyStateRecord>>> ListStatesAsync(string scopePath, ImmutableArray<Guid> subscriptions);

    /// <summary>How often the compiled per-scope cache has been built and reused since activation.</summary>
    [ReadOnly]
    Task<PolicyCatalogStatistics> GetStatisticsAsync();

    /// <summary>Releases the activation. Tests use it to force a reactivation from durable state.</summary>
    Task DeactivateAsync();
}

/// <summary>A definition as the catalog stores it.</summary>
[GenerateSerializer]
[Alias("CyberCloud.ResourceManager.PolicyDefinitionRecord")]
public sealed record PolicyDefinitionRecord {
    /// <summary>The canonical address.</summary>
    [Id(0)]
    public string Path { get; init; } = string.Empty;

    /// <summary>The scope's path — a tenant, a management group or a subscription.</summary>
    [Id(1)]
    public string Scope { get; init; } = string.Empty;

    /// <summary>The name.</summary>
    [Id(2)]
    public string Name { get; init; } = string.Empty;

    /// <summary>What a person reads.</summary>
    [Id(3)]
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>The longer explanation.</summary>
    [Id(4)]
    public string Description { get; init; } = string.Empty;

    /// <summary>The <c>policyRule</c>, as JSON text.</summary>
    [Id(5)]
    public string Rule { get; init; } = "{}";

    /// <summary>
    ///     Bumped on every change, so a cache compiled from an older version is recognisably older.
    /// </summary>
    [Id(6)]
    public long Version { get; init; }
}

/// <summary>An assignment as the catalog stores it.</summary>
[GenerateSerializer]
[Alias("CyberCloud.ResourceManager.PolicyAssignmentRecord")]
public sealed record PolicyAssignmentRecord {
    /// <summary>The canonical address.</summary>
    [Id(0)]
    public string Path { get; init; } = string.Empty;

    /// <summary>The scope's path — a management group, a subscription or a resource group.</summary>
    [Id(1)]
    public string Scope { get; init; } = string.Empty;

    /// <summary>The name.</summary>
    [Id(2)]
    public string Name { get; init; } = string.Empty;

    /// <summary>What a person reads.</summary>
    [Id(3)]
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>The definition's canonical address.</summary>
    [Id(4)]
    public string DefinitionPath { get; init; } = string.Empty;

    /// <summary>
    ///     Addresses beneath <see cref="Scope" /> the assignment does not reach — scopes or resources,
    ///     canonical. A resource is excluded when its path is one of these or lies beneath one.
    /// </summary>
    [Id(5)]
    public ImmutableArray<string> NotScopes { get; init; } = [];

    /// <summary>Bumped on every change.</summary>
    [Id(6)]
    public long Version { get; init; }
}

/// <summary>What a definition put did: the record as stored, and whether it is new.</summary>
/// <remarks>
///     ⚠ Two records rather than one generic one: a generic wire type needs an arity-qualified alias
///     on every closed form a peer might see, and two concrete types cost less than being right about
///     that across a process boundary.
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.ResourceManager.PolicyDefinitionWrite")]
public sealed record PolicyDefinitionWrite {
    /// <summary>The record as stored.</summary>
    [Id(0)]
    public PolicyDefinitionRecord Record { get; init; } = new();

    /// <summary>Whether this call created it.</summary>
    [Id(1)]
    public bool Created { get; init; }
}

/// <summary>What an assignment put did: the record as stored, and whether it is new.</summary>
[GenerateSerializer]
[Alias("CyberCloud.ResourceManager.PolicyAssignmentWrite")]
public sealed record PolicyAssignmentWrite {
    /// <summary>The record as stored.</summary>
    [Id(0)]
    public PolicyAssignmentRecord Record { get; init; } = new();

    /// <summary>Whether this call created it.</summary>
    [Id(1)]
    public bool Created { get; init; }
}

/// <summary>Whether one resource met one audit assignment's rule when it was last written.</summary>
[Alias("CyberCloud.ResourceManager.PolicyComplianceState")]
public enum PolicyComplianceState {
    /// <summary>Never assigned.</summary>
    Unknown = 0,

    /// <summary>The audit rule did not match.</summary>
    Compliant = 1,

    /// <summary>The audit rule matched.</summary>
    NonCompliant = 2
}

/// <summary>One resource's compliance with one assignment — a row of <c>policyStates</c>.</summary>
[GenerateSerializer]
[Alias("CyberCloud.ResourceManager.PolicyStateRecord")]
public sealed record PolicyStateRecord {
    /// <summary>The resource's canonical path.</summary>
    [Id(0)]
    public string ResourcePath { get; init; } = string.Empty;

    /// <summary>The resource's type.</summary>
    [Id(1)]
    public string ResourceType { get; init; } = string.Empty;

    /// <summary>The assignment that audited it.</summary>
    [Id(2)]
    public string AssignmentPath { get; init; } = string.Empty;

    /// <summary>The definition that assignment applies.</summary>
    [Id(3)]
    public string DefinitionPath { get; init; } = string.Empty;

    /// <summary>The verdict.</summary>
    [Id(4)]
    public PolicyComplianceState State { get; init; } = PolicyComplianceState.Unknown;

    /// <summary>
    ///     When the verdict last <i>changed</i> — not when it was last confirmed.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A change time rather than an evaluation time, and that is what keeps the write cheap.</b>
    ///     A timestamp moved by every evaluation would make every write of an audited resource a
    ///     durable write to this grain, for a fact that did not change.
    /// </remarks>
    [Id(5)]
    public DateTimeOffset Since { get; init; }
}

/// <summary>What step 5 evaluates: one request against one resource.</summary>
[GenerateSerializer]
[Alias("CyberCloud.ResourceManager.PolicySubject")]
public sealed record PolicySubject {
    /// <summary>The resource's canonical path.</summary>
    [Id(0)]
    public string ResourcePath { get; init; } = string.Empty;

    /// <summary>The subscription.</summary>
    [Id(1)]
    public Guid SubscriptionId { get; init; }

    /// <summary>The resource group.</summary>
    [Id(2)]
    public string ResourceGroup { get; init; } = string.Empty;

    /// <summary>
    ///     The management group the subscription sits in, as step 1 read it, or empty for one that
    ///     hangs off the tenant.
    /// </summary>
    [Id(3)]
    public string ManagementGroup { get; init; } = string.Empty;

    /// <summary>The resource type.</summary>
    [Id(4)]
    public string ResourceType { get; init; } = string.Empty;

    /// <summary>The resource's own name.</summary>
    [Id(5)]
    public string ResourceName { get; init; } = string.Empty;

    /// <summary>One of <c>create</c>, <c>update</c>, <c>delete</c>, <c>action</c> — <c>PolicyOperations</c>.</summary>
    [Id(6)]
    public string Operation { get; init; } = string.Empty;

    /// <summary>The action's name, for an action.</summary>
    [Id(7)]
    public string Action { get; init; } = string.Empty;

    /// <summary>
    ///     The body as the write would leave the resource, as JSON text. ⚠ With the type's secret
    ///     properties removed — see <c>CatalogPolicyEvaluator</c>.
    /// </summary>
    [Id(8)]
    public string Document { get; init; } = "{}";
}

/// <summary>One assignment's part in one evaluation — what the write's trace records at step 5.</summary>
[GenerateSerializer]
[Alias("CyberCloud.ResourceManager.PolicyTraceEntry")]
public sealed record PolicyTraceEntry {
    /// <summary>The assignment.</summary>
    [Id(0)]
    public string AssignmentPath { get; init; } = string.Empty;

    /// <summary>The definition it applies.</summary>
    [Id(1)]
    public string DefinitionPath { get; init; } = string.Empty;

    /// <summary>The effect, as the rule spells it — <c>deny</c>, <c>audit</c> or <c>modify</c>.</summary>
    [Id(2)]
    public string Effect { get; init; } = string.Empty;

    /// <summary>Whether the rule's <c>if</c> held.</summary>
    [Id(3)]
    public bool Matched { get; init; }

    /// <summary>For a modify, the rewrites it made, one line each — <c>replace /properties/sku = "gp1"</c>.</summary>
    [Id(4)]
    public ImmutableArray<string> Applied { get; init; } = [];

    /// <inheritdoc />
    public override string ToString() =>
        $"{Effect} {(Matched ? "matched" : "did not match")} ({AssignmentPath})"
        + (Applied.IsDefaultOrEmpty ? "" : ": " + string.Join("; ", Applied));
}

/// <summary>One rewrite a modify assignment made, for the write path to make on the body it sends.</summary>
[GenerateSerializer]
[Alias("CyberCloud.ResourceManager.PolicyModificationRecord")]
public sealed record PolicyModificationRecord {
    /// <summary>The assignment that made it.</summary>
    [Id(0)]
    public string AssignmentPath { get; init; } = string.Empty;

    /// <summary><c>add</c> or <c>replace</c>.</summary>
    [Id(1)]
    public string Operation { get; init; } = string.Empty;

    /// <summary>The pointer.</summary>
    [Id(2)]
    public string Field { get; init; } = string.Empty;

    /// <summary>The value, as JSON text.</summary>
    [Id(3)]
    public string Value { get; init; } = "null";
}

/// <summary>The assignment that refused a request, and why.</summary>
[GenerateSerializer]
[Alias("CyberCloud.ResourceManager.PolicyDenial")]
public sealed record PolicyDenial {
    /// <summary>The assignment.</summary>
    [Id(0)]
    public string AssignmentPath { get; init; } = string.Empty;

    /// <summary>The definition it applies.</summary>
    [Id(1)]
    public string DefinitionPath { get; init; } = string.Empty;

    /// <summary>The assignment's display name, or its name.</summary>
    [Id(2)]
    public string AssignmentName { get; init; } = string.Empty;

    /// <summary>The definition's display name, or its name.</summary>
    [Id(3)]
    public string DefinitionName { get; init; } = string.Empty;

    /// <summary>The first body pointer the rule tests, for the error's <c>target</c>; empty when it tests none.</summary>
    [Id(4)]
    public string Target { get; init; } = string.Empty;
}

/// <summary>Everything one evaluation decided.</summary>
[GenerateSerializer]
[Alias("CyberCloud.ResourceManager.PolicyEvaluation")]
public sealed record PolicyEvaluation {
    /// <summary>Every assignment that applied, in evaluation order.</summary>
    [Id(0)]
    public ImmutableArray<PolicyTraceEntry> Entries { get; init; } = [];

    /// <summary>The refusal, when a deny rule matched.</summary>
    [Id(1)]
    public PolicyDenial? Denial { get; init; }

    /// <summary>The rewrites, in the order they were made.</summary>
    [Id(2)]
    public ImmutableArray<PolicyModificationRecord> Modifications { get; init; } = [];

    /// <summary>The audit verdicts, to record once the write is accepted.</summary>
    [Id(3)]
    public ImmutableArray<PolicyStateRecord> States { get; init; } = [];

    /// <summary>
    ///     Whether the resource holds states now, so the write path knows an empty
    ///     <see cref="States" /> still has something to clear.
    /// </summary>
    [Id(4)]
    public bool HadStates { get; init; }
}

/// <summary>The catalog's cache counters since activation.</summary>
/// <param name="Compilations">How many times a scope's assignment set was compiled.</param>
/// <param name="Hits">How many times a compiled set was reused.</param>
[GenerateSerializer]
[Alias("CyberCloud.ResourceManager.PolicyCatalogStatistics")]
public sealed record PolicyCatalogStatistics(
    [property: Id(0)] long Compilations,
    [property: Id(1)] long Hits
);
