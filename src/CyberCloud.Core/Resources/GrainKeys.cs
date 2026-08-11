using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CyberCloud.Core.Resources;

/// <summary>
///     Which grain a key within a tenant addresses. The set is closed — it is the grain-key table at
///     docs/plan/06 § Grain keys plus the two shapes that table omits for grains docs/plan/04
///     § Grain taxonomy does name (<see cref="Tenant" />, <see cref="PlatformSingleton" />), and
///     nothing else may be a grain key.
/// </summary>
public enum GrainKeyKind
{
    /// <summary><c>default(GrainKey)</c>. Not a key.</summary>
    None = 0,

    /// <summary><c>ISubscriptionGrain</c> — <c>sub/{subscriptionId:N}</c>.</summary>
    Subscription,

    /// <summary><c>IResourceGroupGrain</c> — <c>sub/{subscriptionId:N}/rg/{name}</c>.</summary>
    ResourceGroup,

    /// <summary><c>IResourceGrain</c> — <c>res/{resourceId:N}</c>.</summary>
    Resource,

    /// <summary><c>IResourceIndexGrain</c> — <c>idx/path/{digest}</c>.</summary>
    PathIndex,

    /// <summary><c>IUserGrain</c> — <c>user/{userId:N}</c>.</summary>
    User,

    /// <summary><c>IEmailIndexGrain</c> — <c>idx/email/{digest}</c>.</summary>
    EmailIndex,

    /// <summary><c>IOperationGrain</c> — <c>op/{operationId:N}</c>.</summary>
    Operation,

    /// <summary>
    ///     <c>IClusterConnectionGrain</c> — <c>cluster/{clusterId:N}</c>. ⚠ <b>Null tenant</b>, see
    ///     <see cref="GrainKeys.ClusterConnection" />.
    /// </summary>
    ClusterConnection,

    /// <summary>
    ///     <c>ITenantGrain</c> — <c>tenant/{tenantId:N}</c>. See <see cref="GrainKeys.Tenant" /> for
    ///     why this row is here and not in the table at docs/plan/06 § Grain keys.
    /// </summary>
    Tenant,

    /// <summary>
    ///     A platform singleton — <c>platform/{name}</c>, one activation worldwide. ⚠ <b>Null
    ///     tenant</b>. <see cref="GrainKey.Name" /> carries the singleton's name; the set is closed
    ///     and is <see cref="GrainKeys.PlatformSingletons" />.
    /// </summary>
    PlatformSingleton
}

/// <summary>
///     A decoded grain key within a tenant — what <see cref="GrainKeys.Parse" /> returns.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="Id" />, <see cref="Name" /> and <see cref="Digest" /> are populated per
///         <see cref="Kind" />, and the ones that do not apply are <see cref="Guid.Empty" /> and
///         <see cref="string.Empty" /> respectively:
///     </para>
///     <list type="table">
///         <item><term><see cref="GrainKeyKind.Subscription" /></term><description><see cref="Id" /> = the subscription.</description></item>
///         <item><term><see cref="GrainKeyKind.ResourceGroup" /></term><description><see cref="Id" /> = the <i>subscription</i>, <see cref="Name" /> = the group.</description></item>
///         <item><term><see cref="GrainKeyKind.Resource" /></term><description><see cref="Id" /> = the resource.</description></item>
///         <item><term><see cref="GrainKeyKind.User" /></term><description><see cref="Id" /> = the user.</description></item>
///         <item><term><see cref="GrainKeyKind.Operation" /></term><description><see cref="Id" /> = the operation.</description></item>
///         <item><term><see cref="GrainKeyKind.ClusterConnection" /></term><description><see cref="Id" /> = the cluster.</description></item>
///         <item><term><see cref="GrainKeyKind.PathIndex" /> / <see cref="GrainKeyKind.EmailIndex" /></term><description><see cref="Digest" /> only.</description></item>
///     </list>
///     <para>
///         ⚠ <b>An index key decodes to its digest and no further, and that is the point of a hash.</b>
///         <see cref="GrainKeys.PathIndex" /> and <see cref="GrainKeys.EmailIndex" /> are one-way; the
///         path and the address live in the grain's <i>state</i>, not in its key. "Parse" for those
///         two shapes therefore means "recognise the shape and extract the digest", which is what a
///         caller routing a key to a grain type actually needs.
///     </para>
/// </remarks>
public readonly record struct GrainKey
{
    readonly string? name;
    readonly string? digest;

    internal GrainKey(GrainKeyKind kind, Guid id, string? name, string? digest)
    {
        Kind = kind;
        Id = id;
        this.name = name;
        this.digest = digest;
    }

    /// <summary>Which grain this key addresses.</summary>
    public GrainKeyKind Kind { get; }

    /// <summary>The GUID this key carries — see the table on the type.</summary>
    public Guid Id { get; }

    /// <summary>The resource group name, for <see cref="GrainKeyKind.ResourceGroup" />.</summary>
    public string Name => name ?? string.Empty;

    /// <summary>The index digest, for the two <c>idx/</c> shapes.</summary>
    public string Digest => digest ?? string.Empty;

    /// <summary>
    ///     Re-emits the key. <c>GrainKeys.Parse(k).GetValueOrThrow().ToString() == k</c> for every
    ///     <c>k</c> the parser accepts — enforced by the parser itself, see
    ///     <see cref="GrainKeys.Parse" />.
    /// </summary>
    public override string ToString() => Kind switch
    {
        GrainKeyKind.Subscription => GrainKeys.Subscription(Id),
        GrainKeyKind.ResourceGroup => GrainKeys.ResourceGroup(Id, Name),
        GrainKeyKind.Resource => GrainKeys.Resource(Id),
        GrainKeyKind.PathIndex => GrainKeys.PathIndexPrefix + Digest,
        GrainKeyKind.User => GrainKeys.User(Id),
        GrainKeyKind.EmailIndex => GrainKeys.EmailIndexPrefix + Digest,
        GrainKeyKind.Operation => GrainKeys.Operation(Id),
        GrainKeyKind.ClusterConnection => GrainKeys.ClusterConnection(Id),
        GrainKeyKind.Tenant => GrainKeys.Tenant(Id),
        GrainKeyKind.PlatformSingleton => GrainKeys.PlatformSingletonPrefix + Name,
        _ => string.Empty
    };
}

/// <summary>
///     The <b>only</b> type in Cyber Cloud allowed to format or parse a grain key — ADR-002
///     (docs/plan/02:161-188) and the grain-key table at docs/plan/06:101-110.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why a string key at all.</b> The brief says "GUID as ID"; <c>Orleans.Multitenant</c>
///         only supports string keys. Both requirements are satisfied: identifiers in the API, the
///         storage, the SDK and the URL are GUIDs, and the grain key is a composed string that
///         contains them. Nothing else in the codebase may concatenate one.
///     </para>
///     <para>
///         <b>The ten shapes.</b> Eight of them are the table at docs/plan/06 § Grain keys; the last
///         two — <see cref="Tenant" /> and <see cref="PlatformSingleton" /> — are the rows that table
///         is <i>missing</i> for grains docs/plan/04 § Grain taxonomy names in its Entity and
///         Platform rows. See the remarks on each. Every one of them is formatted <i>and</i> parsed —
///         a key that can
///         be built but not decoded is half a type, and routing a physical key back to a grain type
///         (in a log, in a repair tool, in a dead-letter handler) needs the other half.
///     </para>
///     <list type="table">
///         <item><term><see cref="Subscription" /></term><description><c>sub/{subscriptionId:N}</c></description></item>
///         <item><term><see cref="ResourceGroup" /></term><description><c>sub/{subscriptionId:N}/rg/{name}</c></description></item>
///         <item><term><see cref="Resource" /></term><description><c>res/{resourceId:N}</c></description></item>
///         <item><term><see cref="PathIndex" /></term><description><c>idx/path/{sha256(canonicalPath)[..16]}</c></description></item>
///         <item><term><see cref="User" /></term><description><c>user/{userId:N}</c></description></item>
///         <item><term><see cref="EmailIndex" /></term><description><c>idx/email/{sha256(tenantId + normalizedEmail)[..16]}</c></description></item>
///         <item><term><see cref="Operation" /></term><description><c>op/{operationId:N}</c></description></item>
///         <item><term><see cref="ClusterConnection" /></term><description><c>cluster/{clusterId:N}</c> — <b>null tenant</b></description></item>
///         <item><term><see cref="Tenant" /></term><description><c>tenant/{tenantId:N}</c> — not in docs/plan/06's table</description></item>
///         <item><term><see cref="PlatformSingleton" /></term><description><c>platform/{name}</c> — <b>null tenant</b>, not in docs/plan/06's table</description></item>
///     </list>
///     <para>
///         ⚠ <b><see cref="Resource" /> is keyed by the resource GUID alone</b> — docs/plan/06:112-114.
///         ADR-002 once showed <c>{sub:N}/{rg}/{type}/{res:N}</c> and docs/plan/02:153-159 records
///         that 06 won: a key carrying the name would make a <i>rename</i> a grain migration, and one
///         carrying the resource group would make a <i>move</i> one too. The address stays in
///         <see cref="ResourceId" />, which is a different question — docs/plan/06:41-45.
///     </para>
///     <para>
///         <b>Where this key ends up.</b> These are the <i>within-tenant</i> halves.
///         <c>Orleans.Multitenant</c> prepends the tenant and hands the result to
///         <c>IGrainFactory</c>; the physical key is <c>{escapedTenantId}|{keyWithinTenant}</c>.
///         The encoding was verified against <c>Orleans.Multitenant</c> 4.0.0 three ways — see
///         <c>OrleansMultitenantEncodingTests</c> — and the parts that matter here are:
///     </para>
///     <list type="bullet">
///         <item>
///             The tenant id has each <c>'|'</c> doubled and a single <c>'|'</c> appended as the
///             terminator.
///         </item>
///         <item>
///             ⚠ The key within the tenant is <b>copied verbatim</b> — its <c>'|'</c> characters are
///             <b>not</b> escaped. A <c>'|'</c> in a key is therefore not corrupted, and cannot forge
///             a tenant (the terminator is the first <i>un</i>doubled <c>'|'</c> and the tenant id
///             can no longer contain one), but it does make the physical key stop reading as the key
///             that was constructed.
///         </item>
///         <item>
///             A single <c>'~'</c> is prefixed when, and only when, the key within the tenant starts
///             with <c>'|'</c> or <c>'~'</c>.
///         </item>
///         <item>
///             For a <i>null-tenant</i> grain there is no prefix and no <c>'~'</c> rule; instead the
///             whole key has its <c>'|'</c> doubled. See <see cref="ClusterConnection" />.
///         </item>
///     </list>
///     <para>
///         Every key produced here is drawn from <c>[a-z0-9/-]</c> and starts with a lower-case
///         letter, so it survives all four branches untouched — asserted for every shape over a
///         generated corpus by <c>GrainKeysTests.EveryGeneratedKeyIsSafeForTenantQualification</c>,
///         via <see cref="IsTenantQualificationSafe" />.
///     </para>
///     <para>
///         <b>The shapes cannot collide, and that is a property rather than a coincidence.</b> Each
///         shape is fixed by its first segment (<c>sub</c>, <c>res</c>, <c>user</c>, <c>op</c>,
///         <c>cluster</c>, <c>idx</c>) and its segment count, and the one caller-controlled component
///         — the resource group name in <see cref="ResourceGroup" /> — is validated by
///         <see cref="ResourceNaming" />, which forbids <c>/</c>. A name that somehow carried a
///         <c>/</c> would change the segment count and be rejected on the way back in rather than
///         re-parsed as a different shape. See <c>GrainKeysTests</c> § key-shape collision.
///     </para>
/// </remarks>
public static class GrainKeys
{
    /// <summary><c>sub/</c> — a subscription, and the head of a resource group key.</summary>
    public const string SubscriptionPrefix = "sub/";

    /// <summary><c>res/</c> — a resource.</summary>
    public const string ResourcePrefix = "res/";

    /// <summary><c>user/</c> — a user.</summary>
    public const string UserPrefix = "user/";

    /// <summary><c>op/</c> — an operation.</summary>
    public const string OperationPrefix = "op/";

    /// <summary><c>cluster/</c> — a cluster connection. Null tenant.</summary>
    public const string ClusterConnectionPrefix = "cluster/";

    /// <summary><c>idx/path/</c> — the resource path index.</summary>
    public const string PathIndexPrefix = "idx/path/";

    /// <summary><c>idx/email/</c> — the per-tenant email index.</summary>
    public const string EmailIndexPrefix = "idx/email/";

    /// <summary><c>tenant/</c> — the tenant's own entity grain.</summary>
    public const string TenantPrefix = "tenant/";

    /// <summary><c>platform/</c> — a platform singleton. Null tenant.</summary>
    public const string PlatformSingletonPrefix = "platform/";

    /// <summary><c>platform/shard-map</c> — <c>IShardMapGrain</c>'s singleton name.</summary>
    public const string ShardMapSingleton = "shard-map";

    /// <summary><c>platform/tenant-directory</c> — <c>ITenantDirectoryGrain</c>'s singleton name.</summary>
    public const string TenantDirectorySingleton = "tenant-directory";

    /// <summary>The <c>rg</c> literal in <c>sub/{subscriptionId:N}/rg/{name}</c>.</summary>
    public const string ResourceGroupSegment = "rg";

    /// <summary>
    ///     The number of hexadecimal characters an index digest carries — <c>sha256(x)[..16]</c>,
    ///     docs/plan/06:106 and :108.
    /// </summary>
    /// <remarks>
    ///     ⚠ 16 hex characters is <b>64 bits</b>, not 128. Two consequences, both worth stating
    ///     rather than discovering:
    ///     <list type="bullet">
    ///         <item>
    ///             <b>Accidental collision</b> becomes likely near 2^32 entries in one index. The
    ///             path index is per tenant and the email index is per tenant, so the population is
    ///             a tenant's resources — four billion of them is not a number any tenant reaches,
    ///             and quota (docs/plan/06 § Quota) caps it far below.
    ///         </item>
    ///         <item>
    ///             <b>Deliberate collision</b> against a <i>chosen</i> target needs a 64-bit second
    ///             preimage, which is out of reach of an attacker who can only submit names through
    ///             the API. A birthday search finds <i>some</i> pair at 2^32, but a pair of names the
    ///             attacker controls in their own tenant costs them nothing and gains them nothing —
    ///             a self-inflicted 409.
    ///         </item>
    ///     </list>
    ///     Widening this is a one-line change and a re-index of every claim, so if it is ever wanted
    ///     it should happen before anything ships.
    /// </remarks>
    public const int DigestLength = 16;

    /// <summary>The longest an email address may be — RFC 5321's 254-character forward path.</summary>
    public const int MaxEmailLength = 254;

    const int DigestBytes = DigestLength / 2;

    // ── Formatting ─────────────────────────────────────────────────────────────────────────────

    /// <summary><c>sub/{subscriptionId:N}</c> — <c>ISubscriptionGrain</c>, docs/plan/06:103.</summary>
    public static string Subscription(Guid subscriptionId) =>
        SubscriptionPrefix + N(subscriptionId);

    /// <summary>
    ///     <c>sub/{subscriptionId:N}/rg/{name}</c> — <c>IResourceGroupGrain</c>, docs/plan/06:104.
    /// </summary>
    /// <remarks>
    ///     The key nests under the subscription because a resource group name is unique within its
    ///     subscription, not within the tenant (docs/plan/06:10-11). <paramref name="name" /> is the
    ///     only caller-controlled text in any grain key, and it is validated here rather than
    ///     trusted: see the remarks on <see cref="GrainKeys" /> § collision.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="name" /> breaks <see cref="ResourceNaming" />.</exception>
    public static string ResourceGroup(Guid subscriptionId, string name)
    {
        var validated = ResourceNaming.EnsureValid(name, nameof(name), "resource group name");
        return SubscriptionPrefix + N(subscriptionId) + "/" + ResourceGroupSegment + "/" + validated;
    }

    /// <summary><c>res/{resourceId:N}</c> — <c>IResourceGrain</c>, docs/plan/06:105.</summary>
    public static string Resource(Guid resourceId) => ResourcePrefix + N(resourceId);

    /// <summary><c>tenant/{tenantId:N}</c> — <c>ITenantGrain</c>.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>DOC DEFECT, and this row is the repair.</b> docs/plan/04 § Grain taxonomy lists
    ///         <c>ITenantGrain</c> in the Entity row — <i>tenant-qualified, Durable, long-lived</i> —
    ///         but the grain-key table at docs/plan/06 § Grain keys has <b>no row for it</b>. Its
    ///         eight rows start at <c>ISubscriptionGrain</c>. Since that table is what closes this
    ///         type's set, a tenant grain was unbuildable until one of the two documents moved.
    ///     </para>
    ///     <para>
    ///         <b>Why the tenant id is repeated inside a tenant-qualified key.</b> The physical key is
    ///         <c>{tenantId}|tenant/{tenantId:N}</c>, which looks redundant and is not, for exactly
    ///         the reason <see cref="EmailIndex" /> puts the tenant in its digest as well as in the
    ///         qualification: a key read outside its qualification — in a repair tool, a dead-letter
    ///         handler, an audit export, a <c>psql</c> session — still says which tenant it is. The
    ///         alternative, a bare <c>tenant</c> literal, is a key that means nothing on its own. The
    ///         cost is 33 characters and the grain checks the two halves agree on activation.
    ///     </para>
    /// </remarks>
    public static string Tenant(Guid tenantId) => TenantPrefix + N(tenantId);

    /// <summary>
    ///     <c>platform/{name}</c> — the key of a <b>null-tenant</b> platform singleton.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The Platform row of docs/plan/04 § Grain taxonomy: <c>ITenantDirectoryGrain</c>,
    ///         <c>IShardMapGrain</c>, <c>IProviderRegistryGrain</c> — null-tenant, durable, in the
    ///         global cluster, permanent. There is exactly one activation of each worldwide, so the
    ///         key carries no identifier at all; the grain <i>type</i> is the identity and the key is
    ///         a constant.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A constant key is only acceptable because these are not index grains.</b>
    ///         docs/plan/04 § Grain taxonomy's ⚠ — "an index grain keyed by a low-cardinality value
    ///         is a single activation serialising every create in the platform" — is about grains
    ///         whose <i>traffic</i> scales with creates. These are read-mostly and their write rate
    ///         is O(new tenants per day), which docs/plan/05 § The tenant directory sizes at 0.12
    ///         writes per second. The cardinality question is asked and answered, not dodged:
    ///         cardinality 1, traffic O(day), reads served from an in-process snapshot.
    ///     </para>
    ///     <para>
    ///         The set is closed (<see cref="PlatformSingletons" />) so that "platform singleton" can
    ///         never become a namespace somebody drops a per-tenant key into.
    ///     </para>
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="name" /> is not in <see cref="PlatformSingletons" />.</exception>
    public static string PlatformSingleton(string name)
    {
        if (!PlatformSingletons.Contains(name, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                $"'{name}' is not a platform singleton. The set is closed and is "
                + $"[{string.Join(", ", PlatformSingletons)}] — docs/plan/04 § Grain taxonomy, the "
                + "Platform row. A key that varies per tenant is not a platform singleton.",
                nameof(name));
        }

        return PlatformSingletonPrefix + name;
    }

    /// <summary><c>platform/shard-map</c> — <c>IShardMapGrain</c>, docs/plan/05 § The shard map.</summary>
    public static string ShardMap() => PlatformSingletonPrefix + ShardMapSingleton;

    /// <summary>
    ///     <c>platform/tenant-directory</c> — <c>ITenantDirectoryGrain</c>, docs/plan/05 § The tenant
    ///     directory.
    /// </summary>
    public static string TenantDirectory() => PlatformSingletonPrefix + TenantDirectorySingleton;

    /// <summary>The closed set of platform-singleton names.</summary>
    public static IReadOnlyList<string> PlatformSingletons { get; } =
        [ShardMapSingleton, TenantDirectorySingleton];

    /// <summary><c>user/{userId:N}</c> — <c>IUserGrain</c>, docs/plan/06:107.</summary>
    public static string User(Guid userId) => UserPrefix + N(userId);

    /// <summary><c>op/{operationId:N}</c> — <c>IOperationGrain</c>, docs/plan/06:109.</summary>
    public static string Operation(Guid operationId) => OperationPrefix + N(operationId);

    /// <summary>
    ///     <c>cluster/{clusterId:N}</c> — <c>IClusterConnectionGrain</c>, docs/plan/06:110.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This key is NOT tenant-qualified.</b> docs/plan/06:116-122 makes
    ///     <c>IClusterConnectionGrain</c> a <i>null-tenant</i> grain on purpose: a cluster connection
    ///     holds a live client and a set of watches, there must be exactly one activation per cluster
    ///     platform-wide, and a cluster shared between a tenant and the platform (which every
    ///     in-house cluster is, while it is being created) would otherwise have two. The grain
    ///     carries its owning tenant as <i>state</i> and checks it on every call; this is the single
    ///     place tenancy is enforced by code rather than by key.
    ///     <para>
    ///         The encoding consequence, from ADR-002's corrected table (docs/plan/02:143): the
    ///         null-tenant branch of <c>Orleans.Multitenant</c> is a <b>different</b> encoding — no
    ///         tenant prefix, no <c>'~'</c> rule, and the whole key has its <c>'|'</c> doubled. This
    ///         key contains no <c>'|'</c>, so it passes through as itself, and because the doubling
    ///         leaves no <i>un</i>doubled <c>'|'</c> anywhere, a null-tenant physical key can never
    ///         be read back as a tenanted one. Asserted by
    ///         <c>GrainKeysTests.TheClusterKeyIsANullTenantKeyAndCannotBeMistakenForATenantedOne</c>.
    ///     </para>
    /// </remarks>
    public static string ClusterConnection(Guid clusterId) =>
        ClusterConnectionPrefix + N(clusterId);

    /// <summary>
    ///     <c>idx/path/{sha256(canonicalPath)[..16]}</c> — <c>IResourceIndexGrain</c>,
    ///     docs/plan/06:106.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This takes a <see cref="Resources.ResourceId" /> and not a string, deliberately.</b>
    ///     The digest must be over <see cref="Resources.ResourceId.CanonicalPath" /> and never over
    ///     <see cref="Resources.ResourceId.Path" /> — docs/plan/06:73-78 and docs/plan/02:182-185.
    ///     The provider namespace is case-preserving on the wire (<c>CyberCloud.Cache</c> reads
    ///     better than <c>cybercloud.cache</c>), so one resource has two <c>Path</c> spellings;
    ///     hashing <c>Path</c> would let both claim the name and defeat the two-phase create at
    ///     docs/plan/06:124-140, which is the one place a duplicate claim is a correctness bug rather
    ///     than a cosmetic one. A <c>string</c> overload would accept <c>Path</c> exactly as readily
    ///     as <c>CanonicalPath</c>, and the difference between them <i>is</i> the bug — so there
    ///     isn't one. ADR-002's sketch spells the parameter <c>canonicalPath</c>; this is the same
    ///     rule with the mistake made unrepresentable.
    ///     <para>
    ///         The <see cref="Resources.ResourceId.Id" /> is not part of the digest and must not be:
    ///         the index maps <i>path to GUID</i>, so the GUID is the answer, not the question. A
    ///         path-parsed id whose <see cref="Resources.ResourceId.Id" /> is
    ///         <see cref="Guid.Empty" /> therefore produces the same index key as the resolved one.
    ///     </para>
    /// </remarks>
    public static string PathIndex(ResourceId id) =>
        PathIndexPrefix + Digest(PathIndexPrefix, id.CanonicalPath);

    /// <summary>
    ///     <c>idx/email/{sha256(tenantId + normalizedEmail)[..16]}</c> — <c>IEmailIndexGrain</c>,
    ///     docs/plan/06:108.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The tenant id is in the digest as well as in the tenant qualification, and that is
    ///         not redundant.</b> docs/plan/11:99-101 — <i>"email uniqueness is per tenant; global
    ///         email uniqueness would be a global index — the thing we do not have and do not
    ///         want"</i>. Keying on the tenant twice costs nothing and means the digest stays correct
    ///         if this grain is ever read outside its tenant qualification (a repair tool, an audit
    ///         export), which is exactly when a silently tenant-free key would be dangerous.
    ///     </para>
    ///     <para>
    ///         <b>The normalization rule is <see cref="NormalizeEmail" />, and it is part of the
    ///         contract.</b> Storing a differently-normalized address on the user than the one that
    ///         went into this digest is how an account becomes unfindable by its own email.
    ///     </para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    ///     <paramref name="email" /> is not an address <see cref="NormalizeEmail" /> accepts.
    /// </exception>
    public static string EmailIndex(Guid tenantId, string email)
    {
        var normalized = NormalizeEmail(email);
        if (normalized.TryGetError(out var error))
        {
            throw new ArgumentException(error.Message, nameof(email));
        }

        // The tenant id is written in its fixed-width `N` form, so tenant-then-address is prefix-free
        // and no (tenant, email) pair can be re-cut into a different one.
        return EmailIndexPrefix
            + Digest(EmailIndexPrefix, N(tenantId) + "\n" + normalized.GetValueOrThrow());
    }

    // ── Email normalization ────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     The canonical form of an email address for indexing and storage, or a failure explaining
    ///     why the input is not an address.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The rule, in order.</b> Trim leading and trailing white space; reject empty; reject
    ///         anything longer than <see cref="MaxEmailLength" />; reject any remaining white space or
    ///         control character; require exactly one <c>'@'</c> with a non-empty local part and a
    ///         non-empty domain; then lower-case <c>A</c>-<c>Z</c> and nothing else.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Why ASCII-only case folding, and not <see cref="string.ToLowerInvariant" />.</b>
    ///         The property this function must have is that <b>two different addresses never produce
    ///         one key</b> — a collision here is one account silently claiming another's identity at
    ///         sign-up, and the two-phase claim cannot distinguish it from a genuine duplicate.
    ///         <see cref="string.ToLowerInvariant" /> does not have that property: U+212A KELVIN SIGN
    ///         folds onto <c>'k'</c>, so <c>aK@example.com</c> and <c>ak@example.com</c> would
    ///         become one key. Folding only <c>A</c>-<c>Z</c> merges exactly one thing — the case of
    ///         an ASCII letter — which is the equivalence every mail provider actually implements, and
    ///         merges nothing else. The related trap in the other direction is U+0130 LATIN CAPITAL
    ///         LETTER I WITH DOT ABOVE, whose invariant lower-casing is <i>not</i> <c>"i"</c>; under
    ///         this rule it is simply left alone and stays a different address. Both are asserted by
    ///         <c>GrainKeysTests</c> § email normalization. This is the same argument, for the same
    ///         reason, as <see cref="ResourceTypeName.AsciiLower" />.
    ///     </para>
    ///     <para>
    ///         <b>Consequence, stated rather than hidden.</b> Non-ASCII addresses (RFC 6531) are
    ///         accepted and passed through <i>uncased</i>, so <c>Ä@x.example</c> and
    ///         <c>ä@x.example</c> are two entries. That is a missed duplicate, not a false one —
    ///         the safe direction. Unicode normalization (NFC) would close some of that gap and is
    ///         deliberately not done here: <see cref="string.Normalize(NormalizationForm)" /> is a
    ///         globalization API and this assembly builds with <c>InvariantGlobalization</c>, so its
    ///         behaviour is not the same everywhere it might run — and a canonicaliser that behaves
    ///         differently per host is worse than one that does less.
    ///     </para>
    ///     <para>
    ///         <b>Why the local part is lower-cased too.</b> RFC 5321 makes the domain
    ///         case-insensitive and leaves the local part to the receiving host. Treating it
    ///         case-sensitively would make <c>Alice@x</c> and <c>alice@x</c> two accounts, which is a
    ///         confusion vector at sign-up and matches no mail provider in practice.
    ///     </para>
    ///     <para>
    ///         ⚠ This is a <i>shape</i> check, not RFC 5322. It exists so that a grain key is only
    ///         ever minted for something address-shaped; deliverability is the verification mail's
    ///         job (docs/plan/11 § Sign-up), not a regular expression's.
    ///     </para>
    /// </remarks>
    public static Result<string> NormalizeEmail(string? email)
    {
        if (email is null)
        {
            return InvalidEmail("null", "an email address is required");
        }

        var trimmed = email.AsSpan().Trim();
        if (trimmed.IsEmpty)
        {
            return InvalidEmail(email, "it is empty once surrounding white space is removed");
        }

        if (trimmed.Length > MaxEmailLength)
        {
            return InvalidEmail(
                email,
                "it is " + Int(trimmed.Length) + " characters long and RFC 5321 caps an address at "
                + Int(MaxEmailLength));
        }

        var at = -1;
        for (var i = 0; i < trimmed.Length; i++)
        {
            var c = trimmed[i];

            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                return InvalidEmail(
                    email,
                    "it contains white space or a control character (U+"
                    + ((int)c).ToString("X4", CultureInfo.InvariantCulture) + ") at position "
                    + Int(i));
            }

            if (c != '@')
            {
                continue;
            }

            if (at >= 0)
            {
                return InvalidEmail(email, "it contains more than one '@'");
            }

            at = i;
        }

        if (at < 0)
        {
            return InvalidEmail(email, "it contains no '@'");
        }

        if (at == 0)
        {
            return InvalidEmail(email, "it has an empty local part — nothing before the '@'");
        }

        if (at == trimmed.Length - 1)
        {
            return InvalidEmail(email, "it has an empty domain — nothing after the '@'");
        }

        Span<char> buffer = trimmed.Length <= 256
            ? stackalloc char[trimmed.Length]
            : new char[trimmed.Length];

        for (var i = 0; i < trimmed.Length; i++)
        {
            var c = trimmed[i];
            buffer[i] = c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;
        }

        return Result<string>.Success(new string(buffer));
    }

    // ── Parsing ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Parses a grain key within a tenant. Returns <see langword="false" /> for anything that is
    ///     not exactly one, and never throws.
    /// </summary>
    public static bool TryParse(string? keyWithinTenant, out GrainKey key)
    {
        key = default;
        var parsed = Parse(keyWithinTenant);
        if (parsed.IsFailure)
        {
            return false;
        }

        key = parsed.GetValueOrThrow();
        return true;
    }

    /// <summary><see cref="TryParse" /> with an explanation.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The parser accepts exactly the strings the formatters produce — no second
    ///         spelling.</b> Every shape is recognised by a case-sensitive first segment and an exact
    ///         segment count, every GUID must be the 32-digit lower-case <c>N</c> form, every digest
    ///         must be <see cref="DigestLength" /> lower-case hexadecimal characters, and a resource
    ///         group name is re-validated against <see cref="ResourceNaming" />. As a final guard the
    ///         decoded key is re-formatted and compared to the input, so anything that survived the
    ///         checks but would round-trip to a <i>different</i> string is rejected. That guard is
    ///         what makes "one grain, one key" a property of this method rather than an aspiration:
    ///         Orleans addresses activations by the key string, so a second accepted spelling is a
    ///         second activation of the same entity.
    ///     </para>
    ///     <para>
    ///         The parser is safe even if <see cref="ResourceNaming" /> were bypassed upstream: a
    ///         resource group name carrying a <c>/</c> changes the segment count and falls out, and
    ///         one carrying a <c>|</c> or upper case fails re-validation. Neither can be re-cut into
    ///         a different shape. See <c>GrainKeysTests</c> § key-shape collision.
    ///     </para>
    /// </remarks>
    public static Result<GrainKey> Parse(string? keyWithinTenant)
    {
        if (string.IsNullOrEmpty(keyWithinTenant))
        {
            return Invalid(
                "A grain key within a tenant is required. It is one of 'sub/{id}', "
                + "'sub/{id}/rg/{name}', 'res/{id}', 'user/{id}', 'op/{id}', 'cluster/{id}', "
                + "'tenant/{id}', 'platform/{singleton}', 'idx/path/{digest}' or "
                + "'idx/email/{digest}' — see docs/plan/06 § Grain keys.");
        }

        var segments = keyWithinTenant.Split('/');
        foreach (var segment in segments)
        {
            if (segment.Length == 0)
            {
                return Invalid(
                    $"'{keyWithinTenant}' is not a grain key: it contains an empty segment (a "
                    + "doubled, leading or trailing '/').");
            }
        }

        var parsed = segments.Length switch
        {
            2 => ParseTwoSegments(keyWithinTenant, segments),
            3 => ParseIndex(keyWithinTenant, segments),
            4 => ParseResourceGroup(keyWithinTenant, segments),
            _ => Invalid(
                $"'{keyWithinTenant}' is not a grain key: it has "
                + Int(segments.Length) + " '/'-separated segments and every grain key shape has 2, "
                + "3 or 4.")
        };

        if (parsed.TryGetError(out var error))
        {
            return Result<GrainKey>.Failure(error);
        }

        // The canonicity guard. Everything above validates; this asserts that what was validated
        // re-emits as the same string, so the parser can never accept a second spelling of a key.
        var key = parsed.GetValueOrThrow();
        return string.Equals(key.ToString(), keyWithinTenant, StringComparison.Ordinal)
            ? parsed
            : Invalid(
                $"'{keyWithinTenant}' is not a grain key: it decodes to '{key}', which is a "
                + "different string. One grain has exactly one key, so a second spelling is "
                + "rejected rather than silently accepted as a second activation.");
    }

    /// <summary>
    ///     Whether <paramref name="keyWithinTenant" /> passes through <c>Orleans.Multitenant</c>'s
    ///     tenant qualification unchanged — no <c>'|'</c> anywhere and no leading <c>'|'</c> or
    ///     <c>'~'</c>.
    /// </summary>
    /// <remarks>
    ///     A key that fails this still round-trips (the encoding is lossless, verified against the
    ///     shipped assembly — see <c>OrleansMultitenantEncodingTests</c>). What it loses is
    ///     <i>legibility</i>: the physical key stored in Redis, printed in a log and shown in a trace
    ///     no longer reads as the key that was constructed. Every key this type builds satisfies it
    ///     trivially, so one that does not is a bug worth catching — which is why the predicate is
    ///     public. It is the assertion any future key-formatting code should be held to.
    /// </remarks>
    public static bool IsTenantQualificationSafe(string? keyWithinTenant)
    {
        if (string.IsNullOrEmpty(keyWithinTenant))
        {
            return false;
        }

        if (keyWithinTenant[0] is '|' or '~')
        {
            return false;
        }

        return !keyWithinTenant.Contains('|', StringComparison.Ordinal);
    }

    // ── Internals ──────────────────────────────────────────────────────────────────────────────

    static Result<GrainKey> ParseTwoSegments(string key, string[] segments)
    {
        if (string.Equals(segments[0], "platform", StringComparison.Ordinal))
        {
            return PlatformSingletons.Contains(segments[1], StringComparer.Ordinal)
                ? Result<GrainKey>.Success(
                    new GrainKey(GrainKeyKind.PlatformSingleton, Guid.Empty, segments[1], null))
                : Invalid(
                    $"'{key}' is not a grain key: '{segments[1]}' is not a platform singleton. The "
                    + $"set is closed and is [{string.Join(", ", PlatformSingletons)}].");
        }

        var kind = segments[0] switch
        {
            "sub" => GrainKeyKind.Subscription,
            "res" => GrainKeyKind.Resource,
            "user" => GrainKeyKind.User,
            "op" => GrainKeyKind.Operation,
            "cluster" => GrainKeyKind.ClusterConnection,
            "tenant" => GrainKeyKind.Tenant,
            _ => GrainKeyKind.None
        };

        if (kind == GrainKeyKind.None)
        {
            return Invalid(
                $"'{key}' is not a grain key: '{segments[0]}' is not one of 'sub', 'res', 'user', "
                + "'op', 'cluster', 'tenant' or 'platform'. The prefix is matched case-sensitively — "
                + "see docs/plan/06 § Grain keys.");
        }

        return GuidFormat.TryParseN(segments[1], out var id)
            ? Result<GrainKey>.Success(new GrainKey(kind, id, null, null))
            : Invalid(
                $"'{segments[1]}' is not an id: a grain key spells GUIDs in the 32-digit "
                + "lower-case 'N' form, with no hyphens and no braces.");
    }

    static Result<GrainKey> ParseIndex(string key, string[] segments)
    {
        if (!string.Equals(segments[0], "idx", StringComparison.Ordinal))
        {
            return Invalid(
                $"'{key}' is not a grain key: a three-segment key is an index and must start with "
                + "'idx'.");
        }

        var kind = segments[1] switch
        {
            "path" => GrainKeyKind.PathIndex,
            "email" => GrainKeyKind.EmailIndex,
            _ => GrainKeyKind.None
        };

        if (kind == GrainKeyKind.None)
        {
            return Invalid(
                $"'{key}' is not a grain key: '{segments[1]}' is not an index. The two indexes are "
                + "'idx/path' (docs/plan/06:106) and 'idx/email' (docs/plan/06:108).");
        }

        return IsDigest(segments[2])
            ? Result<GrainKey>.Success(new GrainKey(kind, Guid.Empty, null, segments[2]))
            : Invalid(
                $"'{segments[2]}' is not an index digest: it must be exactly " + Int(DigestLength)
                + " lower-case hexadecimal characters, the first " + Int(DigestLength)
                + " of a SHA-256.");
    }

    static Result<GrainKey> ParseResourceGroup(string key, string[] segments)
    {
        if (!string.Equals(segments[0], "sub", StringComparison.Ordinal)
            || !string.Equals(segments[2], ResourceGroupSegment, StringComparison.Ordinal))
        {
            return Invalid(
                $"'{key}' is not a grain key: the only four-segment shape is "
                + "'sub/{subscriptionId}/rg/{name}' — docs/plan/06:104.");
        }

        if (!GuidFormat.TryParseN(segments[1], out var subscriptionId))
        {
            return Invalid(
                $"'{segments[1]}' is not a subscription id: a grain key spells GUIDs in the "
                + "32-digit lower-case 'N' form, with no hyphens and no braces.");
        }

        var name = ResourceNaming.Validate(segments[3], "resource group name");
        return name.TryGetError(out var error)
            ? Result<GrainKey>.Failure(new Error(ErrorCode.InvalidGrainKey, error.Message))
            : Result<GrainKey>.Success(
                new GrainKey(GrainKeyKind.ResourceGroup, subscriptionId, segments[3], null));
    }

    static bool IsDigest(string value)
    {
        if (value.Length != DigestLength)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    ///     The first <see cref="DigestLength" /> hexadecimal characters of
    ///     <c>SHA-256(purpose + "\n" + value)</c>, in UTF-8.
    /// </summary>
    /// <remarks>
    ///     <paramref name="purpose" /> is the key prefix, which domain-separates the two indexes: a
    ///     path digest and an email digest are drawn from different hash streams even if their inputs
    ///     ever coincided. The <c>'\n'</c> is unambiguous because neither a canonical path nor a
    ///     normalized email can contain one — <see cref="ResourceNaming" /> and
    ///     <see cref="ResourceTypeName" /> reject control characters in a path, and
    ///     <see cref="NormalizeEmail" /> rejects them in an address.
    /// </remarks>
    static string Digest(string purpose, string value)
    {
        var input = purpose + "\n" + value;
        var bytes = Encoding.UTF8.GetBytes(input);

        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(bytes, hash);

        return Convert.ToHexStringLower(hash[..DigestBytes]);
    }

    static string N(Guid value) => value.ToString("N", CultureInfo.InvariantCulture);

    static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);

    static Result<GrainKey> Invalid(string message) =>
        Result<GrainKey>.Failure(ErrorCode.InvalidGrainKey, message);

    static Result<string> InvalidEmail(string shown, string problem) =>
        Result<string>.Failure(
            ErrorCode.InvalidGrainKey,
            $"'{shown}' is not an email address: {problem}. An address is 1-"
            + Int(MaxEmailLength) + " characters with exactly one '@', a non-empty local part and a "
            + "non-empty domain, and no white space or control characters. See docs/plan/11 "
            + "§ Sign-up and tenant creation.");
}
