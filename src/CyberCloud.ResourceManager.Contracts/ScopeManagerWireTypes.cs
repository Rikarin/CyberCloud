using System.Collections.Immutable;

namespace CyberCloud.ResourceManager.Contracts;

/// <summary>
///     A scope request as it reaches <see cref="IScopeManager" />, after the gateway has
///     authenticated and resolved the region.
/// </summary>
/// <remarks>
///     ⚠
///     <b>
///         A separate record from <see cref="WriteRequest" /> rather than a reuse of it, and the
///         empty fields are the reason.
///     </b> A <see cref="WriteRequest" /> carries an api-version that
///     selects a schema and a projection, an <c>If-Match</c> etag, and an action name — three fields a
///     scope has no meaning for. Reusing it would put three permanently-empty properties on the scope
///     path, and the next reader would have to determine, per field, whether "empty" meant "not
///     applicable here" or "the caller left it out". The one field they share is
///     <see cref="Caller" />, and that is the field that matters.
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.ResourceManager.ScopeRequest")]
public sealed record ScopeRequest {
    /// <summary>The scope path from the URL.</summary>
    /// <remarks>
    ///     ⚠ The path the gateway <i>rebuilt</i> from the token's tenant, never the one off the wire —
    ///     see <c>GatewayRoute</c>'s remarks. The manager re-parses it and compares the tenant again
    ///     regardless, which is the second of the two defences.
    /// </remarks>
    [Id(0)]
    public string Path { get; init; } = string.Empty;

    /// <summary>The request body, as JSON text. Empty for a read.</summary>
    [Id(1)]
    public string Body { get; init; } = "{}";

    /// <summary>Who is asking.</summary>
    [Id(2)]
    public CallerContext Caller { get; init; } = new();
}

/// <summary>
///     What a tenant needs before it can exist. The argument of
///     <see cref="IScopeManager.CreateTenantAsync" />.
/// </summary>
/// <remarks>
///     ⚠ <b>The owner is required and is not defaulted to the caller.</b>
///     <see cref="IScopeManager.CreateTenantAsync" />'s remarks carry the argument; the short version
///     is that <c>tenant</c> has no <c>parent</c> relation, so the only thing that can make a new
///     tenant visible to anybody is a direct <c>#owner</c> tuple — and defaulting that to the platform
///     operator who ran the command would make platform staff the standing owner of every customer
///     tenant.
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.ResourceManager.TenantCreateRequest")]
public sealed record TenantCreateRequest {
    /// <summary>The tenant's GUID. ⚠ Supplied rather than minted — see the remarks.</summary>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         The caller mints it, which makes a retry idempotent and is why this is not a
    ///         platform-generated id.
    ///     </b> docs/plan/06 § Tenant lifecycle makes tenant creation a
    ///     long-running operation whose <i>"every step is idempotent and re-drivable"</i>, and
    ///     <c>ITenantGrain.CreateAsync</c> honours that by returning the existing descriptor for a
    ///     repeated call with the same arguments. A platform-minted id would make every retry a new
    ///     tenant.
    /// </remarks>
    [Id(0)]
    public Guid TenantId { get; init; }

    /// <summary>The globally unique DNS-1123 slug.</summary>
    [Id(1)]
    public string Slug { get; init; } = string.Empty;

    /// <summary>The human-facing name.</summary>
    [Id(2)]
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>The region the tenant is homed to. Permanent until a migration.</summary>
    [Id(3)]
    public string HomeRegion { get; init; } = string.Empty;

    /// <summary>
    ///     The ReBAC subject type of the tenant's first owner — <c>user</c>,
    ///     <c>servicePrincipal</c> or <c>managedIdentity</c>.
    /// </summary>
    [Id(4)]
    public string OwnerSubjectType { get; init; } = "user";

    /// <summary>The tenant's first owner. Required.</summary>
    [Id(5)]
    public string OwnerSubjectId { get; init; } = string.Empty;
}

/// <summary>A scope as the API renders it — the response body of a scope <c>PUT</c> or <c>GET</c>.</summary>
/// <remarks>
///     ⚠
///     <b>
///         There is no <c>provisioningState</c> and no <c>202</c> anywhere on this path, which is
///         the visible half of "a scope is not a resource".
///     </b> docs/plan/06 § Two-phase create is about
///     resources; a subscription and a resource group are records in one grain activation each, so
///     creating one converges before the call returns and there is nothing to poll. A
///     <see cref="WriteAccepted" /> here would advertise an <c>Azure-AsyncOperation</c> URL that
///     answers <c>404</c> to every client polite enough to follow it — the same mistake
///     <c>DispatchStage.ActionAsync</c> avoids for a synchronous action.
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.ResourceManager.ScopeSnapshot")]
public sealed record ScopeSnapshot {
    /// <summary>The scope's address.</summary>
    [Id(0)]
    public string Path { get; init; } = string.Empty;

    /// <summary>Which scope this is.</summary>
    [Id(1)]
    public ScopeKind Kind { get; init; } = ScopeKind.Unknown;

    /// <summary>The name a human reads: the slug, the display name, or the group's name.</summary>
    [Id(2)]
    public string Name { get; init; } = string.Empty;

    /// <summary>
    ///     The Azure-shaped type string. One of <see cref="ScopeTypeNames" />'s three.
    /// </summary>
    [Id(3)]
    public string Type { get; init; } = string.Empty;

    /// <summary>The region, for a tenant's home region or a group's default. Empty otherwise.</summary>
    [Id(4)]
    public string Location { get; init; } = string.Empty;

    /// <summary>
    ///     Whether this call created the scope. ⚠ <c>false</c> for a repeated identical <c>PUT</c>,
    ///     which is still a success — that is what "idempotent" means and is why the verb is
    ///     <c>PUT</c>.
    /// </summary>
    [Id(5)]
    public bool Created { get; init; }

    /// <summary>The concurrency stamp the owning grain reports.</summary>
    [Id(6)]
    public long Version { get; init; }
}

/// <summary>
///     The properties a scope <c>PUT</c> body carries.
/// </summary>
/// <remarks>
///     ⚠
///     <b>
///         Here rather than on <c>ScopeManagerService</c>, because the generated surfaces are in
///         this assembly and the service is not.
///     </b> The service is where they were, and the day the
///     OpenAPI document grew a scope path (issue #63) that placement would have made them the
///     failure this repository keeps re-finding: two constants in assemblies that cannot see each
///     other, agreeing by hand. A CLI offering <c>--display-name</c> against a manager reading
///     <c>displayName</c> is right by luck; one offering <c>--name</c> is a <c>400</c> nobody can
///     act on, because the flag is spelled correctly and the property it writes is not the one the
///     manager reads.
/// </remarks>
public static class ScopeBodyProperties {
    /// <summary>
    ///     The property a resource group's region arrives in. ⚠ Required — there is no platform-wide
    ///     default, because a group whose region were guessed would place a tenant's data somewhere
    ///     nobody chose.
    /// </summary>
    /// <remarks>
    ///     Azure spells it <c>location</c> on every resource, and a scope answering to a different
    ///     name would be the one field of the API a client had to special-case.
    /// </remarks>
    public const string Location = "location";

    /// <summary>
    ///     The property a subscription's display name arrives in. ⚠ Required — it is the name that
    ///     appears on an invoice and in every scope picker.
    /// </summary>
    public const string DisplayName = "displayName";
}

/// <summary>
///     The three <c>type</c> strings a scope renders as, in Azure's shape.
/// </summary>
/// <remarks>
///     ⚠
///     <b>
///         Constants because they appear in a response body and in the CLI's and portal's parsing of
///         one, and a fourth spelling would be a silent client break.
///     </b> They are display strings and
///     <b>not</b> routing input: <c>GatewayRouter</c> resolves a scope from its path's shape, and
///     nothing anywhere matches on these. That is deliberate — the failure this repository has
///     actually shipped is a constant in one assembly that had to agree with a constant in another
///     that could not see it, so a string that decides nothing is a string that cannot decide wrongly.
/// </remarks>
public static class ScopeTypeNames {
    /// <summary>A tenant.</summary>
    public const string Tenant = "CyberCloud.Resources/tenants";

    /// <summary>A subscription.</summary>
    public const string Subscription = "CyberCloud.Resources/subscriptions";

    /// <summary>A resource group. Azure spells its own the same way, one namespace over.</summary>
    public const string ResourceGroup = "CyberCloud.Resources/subscriptions/resourceGroups";

    /// <summary>The type string for a scope kind, or empty for <see cref="ScopeKind.Unknown" />.</summary>
    /// <param name="kind">The scope kind.</param>
    public static string Of(ScopeKind kind) =>
        kind switch {
            ScopeKind.Tenant => Tenant,
            ScopeKind.Subscription => Subscription,
            ScopeKind.ResourceGroup => ResourceGroup,
            _ => ""
        };
}

/// <summary>
///     A request to list the scopes under one parent — a tenant's subscriptions or a subscription's
///     resource groups. The collection <c>GET</c> of docs/plan/10 § Shape, for scopes.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Not a <see cref="ListRequest" />, for the reason <see cref="ScopeRequest" /> is not
///         a <see cref="WriteRequest" />.</b> A <see cref="ListRequest" /> carries an api-version
///         that selects a projection and a path <c>ResourceCollectionId.ParsePath</c> reads; a
///         scope has no projection and its collection path fails that parser by design. Reusing it
///         would be a request whose own field names a parser that refuses it.
///     </para>
///     <para>
///         ⚠ <b><see cref="Top" /> is a cap this endpoint enforces and not a hint</b>, as it is on
///         a resource listing: the page is one <c>ListObjects</c> plus a grain read per member that
///         survives it, and a <c>Check</c> per member when the engine declines to answer. The cap
///         is higher than <see cref="ListRequest.MaxPageSize" /> because a scope page costs less —
///         every member is one small record in one activation, with no api-version projection and
///         no body — and because the portal's context picker loads a tenant's whole subscription
///         list in one call and a tenant's scope tree is bounded by the organisation rather than
///         by its workloads. The two numbers are <c>ListObjectsRequest</c>'s, so the one
///         <c>ListObjects</c> a page asks for is never asked for fewer objects than the page holds.
///     </para>
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.ResourceManager.ScopeListRequest")]
public sealed record ScopeListRequest {
    /// <summary>The largest page this endpoint will build, whatever <see cref="Top" /> asks for.</summary>
    public const int MaxPageSize = 1_000;

    /// <summary>The page size used when <see cref="Top" /> is zero.</summary>
    public const int DefaultPageSize = 100;

    /// <summary>
    ///     The path of the scope whose children are listed: a tenant for the subscription
    ///     collection, a subscription for the resource-group collection.
    /// </summary>
    /// <remarks>
    ///     ⚠ The parent's path and not the collection's, because the collection has no grain and the
    ///     parent does — <c>ScopeCollectionId</c>'s remarks. It is the path the gateway
    ///     <i>rebuilt</i> from the token's tenant, never the one off the wire, and the manager
    ///     compares the tenant again regardless — the same second defence <see cref="ScopeRequest.Path" />
    ///     describes.
    /// </remarks>
    [Id(0)]
    public string ParentPath { get; init; } = string.Empty;

    /// <summary>Who is asking.</summary>
    [Id(1)]
    public CallerContext Caller { get; init; } = new();

    /// <summary>
    ///     How many scopes to examine. Zero means <see cref="DefaultPageSize" />; anything above
    ///     <see cref="MaxPageSize" /> is clamped to it rather than refused.
    /// </summary>
    [Id(2)]
    public int Top { get; init; }

    /// <summary>
    ///     Where to resume, from a previous page's <see cref="ScopeListPage.Continuation" />, or
    ///     empty for the first page.
    /// </summary>
    /// <remarks>
    ///     ⚠ Named <c>Continuation</c> and not <c>ContinuationToken</c> for <c>CC1005</c>'s sake —
    ///     <see cref="ListRequest.Continuation" /> carries the argument. The value is the last member
    ///     examined on the previous page — a subscription id in the <c>N</c> form, or a group name —
    ///     and paging resumes at the next member that sorts after it ordinally, so a scope created
    ///     or deleted between pages cannot make the walk skip or repeat an unrelated member.
    /// </remarks>
    [Id(3)]
    public string Continuation { get; init; } = string.Empty;

    /// <summary>The page size this request actually gets.</summary>
    public int PageSize =>
        Top switch {
            <= 0 => DefaultPageSize,
            > MaxPageSize => MaxPageSize,
            _ => Top
        };
}

/// <summary>
///     One page of a scope collection <c>GET</c>.
/// </summary>
/// <remarks>
///     ⚠ <b>A page holds only what the caller may read, and a filtered-out member leaves no
///     trace</b> — no count, no gap, no marker — for the reason <see cref="ResourceListPage" />
///     gives: any of the three is the enumeration oracle docs/plan/07 § The enforcement seam closes
///     by answering <c>404</c> rather than <c>403</c>, and a subscription id leaks more than a
///     resource name because it is the billing boundary. So a page can be short, or empty, and
///     still carry a continuation; a client stops when the continuation comes back empty and never
///     when a page is smaller than it asked for.
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.ResourceManager.ScopeListPage")]
public sealed record ScopeListPage {
    /// <summary>
    ///     The scopes, each exactly as <see cref="IScopeManager.ReadAsync" /> would render it,
    ///     ordered by subscription id (<c>N</c> form) or group name, ordinally.
    /// </summary>
    [Id(0)]
    public IReadOnlyList<ScopeSnapshot> Items { get; init; } = [];

    /// <summary>
    ///     What to pass as <see cref="ScopeListRequest.Continuation" /> for the next page, or empty
    ///     when the walk reached the end of the parent's listing.
    /// </summary>
    [Id(1)]
    public string Continuation { get; init; } = string.Empty;

    /// <summary>Whether there is another page.</summary>
    public bool HasMore => Continuation.Length > 0;
}

/// <summary>
///     What <see cref="IScopeAuthorizer.ListReadableAsync" /> concluded: the readable subset, or
///     that the question was not answered and has to be asked per member.
/// </summary>
/// <remarks>
///     ⚠ <b>A sibling of <see cref="CollectionVisibility" /> rather than a reuse of it, because a
///     resource group has no GUID.</b> That type's <c>Visible</c> is a set of resource GUIDs; a
///     subscription has one, a resource group is addressed by <c>{subscriptionId:N}-{name}</c> and
///     never by a GUID, so the only value both scope collections can be keyed on is the
///     <see cref="ScopeId" /> itself. The two-state shape is kept exactly — "answered, and nothing
///     is readable" and "not answered" must never be one value, because the first is an empty page
///     and the second is a fallback, and a caller that confused them would leak a whole collection
///     or hide one.
/// </remarks>
public sealed record ScopeCollectionVisibility {
    /// <summary>The engine did not answer; ask per member.</summary>
    public static ScopeCollectionVisibility Unanswered { get; } = new();

    /// <summary>Whether <see cref="Visible" /> is an answer.</summary>
    public bool IsAnswered { get; private init; }

    /// <summary>The readable members. Meaningless unless <see cref="IsAnswered" />.</summary>
    public IReadOnlySet<ScopeId> Visible { get; private init; } = ImmutableHashSet<ScopeId>.Empty;

    /// <summary>An answer: exactly these members are readable.</summary>
    /// <param name="visible">The readable scopes.</param>
    public static ScopeCollectionVisibility Of(IEnumerable<ScopeId> visible) {
        ArgumentNullException.ThrowIfNull(visible);
        return new() { IsAnswered = true, Visible = visible.ToImmutableHashSet() };
    }
}
