using System.Collections.Immutable;

namespace CyberCloud.ResourceManager.Contracts;

/// <summary>
///     The one place a tenant writes, reads and lists policy definitions and assignments, and reads
///     the compliance an audit recorded — docs/plan/08 § Policy, issue #46.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             BESIDE <see cref="IResourceManager" /> AND NOT A MEMBER OF IT, for the reason
///             <see cref="IRoleAssignmentManager" /> is.
///         </b> A definition and an assignment are objects <i>on</i>
///         a scope — <c>PolicyAddress</c> says why they could not be registry types — and writing
///         one is resolve, check, store: no schema per api-version, no quota, no index claim, no
///         reconciler. What <i>enforces</i> them is step 5 of the write path, which is
///         <see cref="IPolicyEvaluator" /> and never this; nothing here runs inside a resource write.
///     </para>
///     <para>
///         ⚠ <b>The permission to write is <c>assignRole</c>, which is Owner.</b> Azure keeps
///         <c>Microsoft.Authorization/policyAssignments/write</c> and <c>policyDefinitions/write</c>
///         out of Contributor for the reason it keeps role assignments out of it: a contributor who
///         could assign a deny policy at a subscription could stop every other contributor's writes,
///         and one who could assign a modify policy could rewrite them. <c>CyberCloudSchema</c> has no
///         policy-specific permission, and <c>assignRole</c> — <c>Rel(owner) &amp; !Rel(suspended)</c>
///         on every scope type — is exactly the set of callers Azure allows. A separate
///         <c>assignPolicy</c> is one schema line and a version bump away, and is deferred until a role
///         beneath owner needs it. Reading is <c>read</c> on the scope.
///     </para>
///     <para>
///         ⚠ <b>404, never 403, as everywhere else.</b> A caller who may not read the scope learns
///         nothing about it; one who may read it but not write gets the <c>403</c> — docs/plan/07
///         § The enforcement seam.
///     </para>
///     <para>
///         ⚠ <b>A service and not a grain</b>, held by the gateway, with every grain reference through
///         <c>ForTenant</c> — the rule its three neighbours follow.
///     </para>
/// </remarks>
public interface IPolicyManager {
    /// <summary>
    ///     Creates or replaces a definition or an assignment. <c>PUT</c>. Idempotent.
    /// </summary>
    /// <param name="request">The request, as the gateway parsed it.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>
    ///     The object as it now stands, with <see cref="PolicyObjectSnapshot.Created" /> saying
    ///     whether this call created it; or the first failure. A body naming an operator, an effect
    ///     or an operation outside the closed sets is <see cref="ErrorCode.InvalidRequestBody" />
    ///     with the offending member as its <c>target</c> and the set in its message.
    /// </returns>
    Task<Result<PolicyObjectSnapshot>> PutAsync(PolicyRequest request, CancellationToken cancellationToken = default);

    /// <summary>Reads one definition or assignment. <c>GET</c>.</summary>
    /// <param name="request">The request. <see cref="PolicyRequest.Body" /> is ignored.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The object, or the canonical <c>404</c>.</returns>
    Task<Result<PolicyObjectSnapshot>> ReadAsync(PolicyRequest request, CancellationToken cancellationToken = default);

    /// <summary>Deletes one definition or assignment. <c>DELETE</c>.</summary>
    /// <param name="request">The request. <see cref="PolicyRequest.Body" /> is ignored.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>
    ///     Success — also for an object already gone, because absence is the goal — or
    ///     <see cref="ErrorCode.Conflict" /> for a definition an assignment still names, listing the
    ///     assignments. ⚠ Refused rather than cascaded: deleting a deny definition out from under its
    ///     assignments would lift an enforcement nobody lifted on purpose.
    /// </returns>
    Task<Result> DeleteAsync(PolicyRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Lists the definitions or the assignments on one scope — direct only — or the compliance
    ///     states of every resource beneath it. <c>GET</c> on a collection.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>One page, ordered by address; or the canonical <c>404</c>.</returns>
    Task<Result<PolicyListPage>> ListAsync(PolicyListRequest request, CancellationToken cancellationToken = default);
}

/// <summary>One request to <see cref="IPolicyManager" />.</summary>
[GenerateSerializer]
[Alias("CyberCloud.ResourceManager.PolicyRequest")]
public sealed record PolicyRequest {
    /// <summary>The address — a definition or an assignment. ⚠ Rebuilt by the gateway with the token's tenant.</summary>
    [Id(0)]
    public string Path { get; init; } = string.Empty;

    /// <summary>The body, as JSON text, for a <c>PUT</c>.</summary>
    [Id(1)]
    public string Body { get; init; } = "{}";

    /// <summary>Who is asking.</summary>
    [Id(2)]
    public CallerContext Caller { get; init; } = new();
}

/// <summary>A collection request to <see cref="IPolicyManager" />.</summary>
[GenerateSerializer]
[Alias("CyberCloud.ResourceManager.PolicyListRequest")]
public sealed record PolicyListRequest {
    /// <summary>The collection's address. ⚠ Rebuilt by the gateway with the token's tenant.</summary>
    [Id(0)]
    public string Path { get; init; } = string.Empty;

    /// <summary>Who is asking.</summary>
    [Id(1)]
    public CallerContext Caller { get; init; } = new();

    /// <summary><c>$top</c>, or zero for the default.</summary>
    [Id(2)]
    public int Top { get; init; }

    /// <summary><c>$skipToken</c> — the last address the previous page served, or empty.</summary>
    [Id(3)]
    public string Continuation { get; init; } = string.Empty;

    /// <summary>The page size, clamped as every collection of this API clamps it.</summary>
    public int PageSize =>
        Top switch {
            <= 0 => ListRequest.DefaultPageSize,
            > ListRequest.MaxPageSize => ListRequest.MaxPageSize,
            _ => Top
        };
}

/// <summary>A definition or an assignment, as a response renders it.</summary>
[GenerateSerializer]
[Alias("CyberCloud.ResourceManager.PolicyObjectSnapshot")]
public sealed record PolicyObjectSnapshot {
    /// <summary>The address — the response's <c>id</c>.</summary>
    [Id(0)]
    public string Path { get; init; } = string.Empty;

    /// <summary>The name, the last segment of the address.</summary>
    [Id(1)]
    public string Name { get; init; } = string.Empty;

    /// <summary><c>CyberCloud.Policy/policyDefinitions</c> or <c>CyberCloud.Policy/policyAssignments</c>.</summary>
    [Id(2)]
    public string Type { get; init; } = string.Empty;

    /// <summary>The scope the object sits on.</summary>
    [Id(3)]
    public string Scope { get; init; } = string.Empty;

    /// <summary>The <c>properties</c> object, as JSON text — the stored shape, re-rendered.</summary>
    [Id(4)]
    public string Properties { get; init; } = "{}";

    /// <summary>Whether this call created the object — <c>201</c> rather than <c>200</c>.</summary>
    [Id(5)]
    public bool Created { get; init; }
}

/// <summary>One page of <see cref="IPolicyManager.ListAsync" />.</summary>
/// <remarks>
///     ⚠ One of the two lists is populated, by the collection's kind: <see cref="Objects" /> for
///     definitions and assignments, <see cref="States" /> for <c>policyStates</c>.
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.ResourceManager.PolicyListPage")]
public sealed record PolicyListPage {
    /// <summary>The definitions or assignments on the page.</summary>
    [Id(0)]
    public ImmutableArray<PolicyObjectSnapshot> Objects { get; init; } = [];

    /// <summary>The compliance states on the page.</summary>
    [Id(1)]
    public ImmutableArray<PolicyStateRecord> States { get; init; } = [];

    /// <summary>The token for the next page, or empty for the last.</summary>
    [Id(2)]
    public string Continuation { get; init; } = string.Empty;
}

/// <summary>The member names a definition's and an assignment's <c>properties</c> carry.</summary>
public static class PolicyBodyProperties {
    /// <summary>Both: what a person reads. Optional.</summary>
    public const string DisplayName = "displayName";

    /// <summary>A definition's longer explanation. Optional.</summary>
    public const string Description = "description";

    /// <summary>A definition's rule — <c>{ "if": …, "then": … }</c>. Required.</summary>
    public const string PolicyRule = "policyRule";

    /// <summary>An assignment's definition, by address. Required.</summary>
    public const string PolicyDefinitionId = "policyDefinitionId";

    /// <summary>An assignment's exclusions — scope or resource addresses beneath it. Optional.</summary>
    public const string NotScopes = "notScopes";

    /// <summary>The scope, echoed on a response. Read-only.</summary>
    public const string Scope = "scope";

    /// <summary>The most exclusions one assignment may carry.</summary>
    public const int MaxNotScopes = 32;

    /// <summary>The longest display name.</summary>
    public const int MaxDisplayName = 128;

    /// <summary>The longest description.</summary>
    public const int MaxDescription = 1024;
}
