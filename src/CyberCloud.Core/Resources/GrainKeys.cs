using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CyberCloud.Core.Resources;

/// <summary>
///     Which grain a key within a tenant addresses. The set is closed — it is the grain-key table at
///     docs/plan/06 § Grain keys plus the shapes that table omits for grains the other plan documents
///     do name, and nothing else may be a grain key. The full accounting, shape by shape and document
///     by document, is on <see cref="GrainKeys" />; every row of it is a grain that exists.
/// </summary>
public enum GrainKeyKind {
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
    ///     A platform singleton — <c>platform/{name}</c>, one activation worldwide. ⚠
    ///     <b>
    ///         Null
    ///         tenant
    ///     </b>
    ///     . <see cref="GrainKey.Name" /> carries the singleton's name; the set is closed
    ///     and is <see cref="GrainKeys.PlatformSingletons" />.
    /// </summary>
    PlatformSingleton,

    /// <summary>
    ///     <c>IObjectRelationsGrain</c> — <c>rel/obj/{type}/{id}</c>, docs/plan/07 § Storage.
    /// </summary>
    ObjectRelations,

    /// <summary>
    ///     <c>ISubjectRelationsGrain</c> — <c>rel/sub/{type}/{id}</c>, docs/plan/07 § Storage.
    /// </summary>
    SubjectRelations,

    /// <summary>
    ///     <c>ICheckGrain</c> — <c>rel/check/{type}/{id}</c>. See
    ///     <see cref="GrainKeys.CheckCache" /> for why this shape is here and not in docs/plan/07.
    /// </summary>
    CheckCache,

    /// <summary>
    ///     <c>ITupleStoreGrain</c> — <c>rel/store/{tenantId:N}</c>. See
    ///     <see cref="GrainKeys.TupleStore" />.
    /// </summary>
    TupleStore,

    /// <summary><c>IGroupGrain</c> — <c>group/{groupId:N}</c>. See <see cref="GrainKeys.Group" />.</summary>
    Group,

    /// <summary>
    ///     <c>IApplicationGrain</c> — <c>app/{applicationId:N}</c>. See
    ///     <see cref="GrainKeys.Application" />.
    /// </summary>
    Application,

    /// <summary>
    ///     <c>IServicePrincipalGrain</c> — <c>sp/{servicePrincipalId:N}</c>. See
    ///     <see cref="GrainKeys.ServicePrincipal" />.
    /// </summary>
    ServicePrincipal,

    /// <summary>
    ///     <c>ISessionGrain</c> — <c>session/{sessionId:N}</c>. See <see cref="GrainKeys.Session" />.
    /// </summary>
    Session,

    /// <summary>
    ///     <c>IManagedIdentityGrain</c> — <c>mi/{managedIdentityId:N}</c>. See
    ///     <see cref="GrainKeys.ManagedIdentity" />.
    /// </summary>
    ManagedIdentity,

    /// <summary>
    ///     <c>IParkedResourceRegistryGrain</c> — <c>parked/{subscriptionId:N}/rg/{name}</c>,
    ///     docs/plan/08 § Soft delete. See <see cref="GrainKeys.ParkedResourceRegistry" /> for why a
    ///     second shape addresses the same resource group <see cref="ResourceGroup" /> already does.
    /// </summary>
    ParkedResourceRegistry,

    /// <summary>
    ///     <c>IExpirySweeperGrain</c> — <c>sweep/{subscriptionId:N}/rg/{name}</c>, docs/plan/07
    ///     § Azure RBAC and docs/plan/08 § Soft delete. The <i>third</i> shape addressing one
    ///     resource group; see <see cref="GrainKeys.ExpirySweeper" /> for why it is a grain of its
    ///     own rather than a reminder on the registry it reads.
    /// </summary>
    ExpirySweeper
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
///         <item>
///             <term>
///                 <see cref="GrainKeyKind.Subscription" />
///             </term>
///             <description><see cref="Id" /> = the subscription.</description>
///         </item>
///         <item>
///             <term>
///                 <see cref="GrainKeyKind.ResourceGroup" />
///             </term>
///             <description><see cref="Id" /> = the <i>subscription</i>, <see cref="Name" /> = the group.</description>
///         </item>
///         <item>
///             <term>
///                 <see cref="GrainKeyKind.ParkedResourceRegistry" />
///             </term>
///             <description>The same two, for the same reason — it addresses a resource group too.</description>
///         </item>
///         <item>
///             <term>
///                 <see cref="GrainKeyKind.ExpirySweeper" />
///             </term>
///             <description>The same two again, and for the third time the same reason.</description>
///         </item>
///         <item>
///             <term>
///                 <see cref="GrainKeyKind.Resource" />
///             </term>
///             <description><see cref="Id" /> = the resource.</description>
///         </item>
///         <item>
///             <term>
///                 <see cref="GrainKeyKind.User" />
///             </term>
///             <description><see cref="Id" /> = the user.</description>
///         </item>
///         <item>
///             <term>
///                 <see cref="GrainKeyKind.Operation" />
///             </term>
///             <description><see cref="Id" /> = the operation.</description>
///         </item>
///         <item>
///             <term>
///                 <see cref="GrainKeyKind.ClusterConnection" />
///             </term>
///             <description><see cref="Id" /> = the cluster.</description>
///         </item>
///         <item>
///             <term><see cref="GrainKeyKind.PathIndex" /> / <see cref="GrainKeyKind.EmailIndex" /></term>
///             <description><see cref="Digest" /> only.</description>
///         </item>
///     </list>
///     <para>
///         ⚠ <b>An index key decodes to its digest and no further, and that is the point of a hash.</b>
///         <see cref="GrainKeys.PathIndex" /> and <see cref="GrainKeys.EmailIndex" /> are one-way; the
///         path and the address live in the grain's <i>state</i>, not in its key. "Parse" for those
///         two shapes therefore means "recognise the shape and extract the digest", which is what a
///         caller routing a key to a grain type actually needs.
///     </para>
/// </remarks>
public readonly record struct GrainKey {
    readonly string? name;
    readonly string? digest;
    readonly string? objectType;
    readonly string? objectId;

    /// <summary>Which grain this key addresses.</summary>
    public GrainKeyKind Kind { get; }

    /// <summary>The GUID this key carries — see the table on the type.</summary>
    public Guid Id { get; }

    /// <summary>
    ///     The resource group name, for <see cref="GrainKeyKind.ResourceGroup" />,
    ///     <see cref="GrainKeyKind.ParkedResourceRegistry" /> and
    ///     <see cref="GrainKeyKind.ExpirySweeper" />.
    /// </summary>
    public string Name => name ?? string.Empty;

    /// <summary>The index digest, for the two <c>idx/</c> shapes.</summary>
    public string Digest => digest ?? string.Empty;

    /// <summary>
    ///     The ReBAC object type, for <see cref="GrainKeyKind.ObjectRelations" />,
    ///     <see cref="GrainKeyKind.SubjectRelations" /> and <see cref="GrainKeyKind.CheckCache" />.
    /// </summary>
    public string ObjectType => objectType ?? string.Empty;

    /// <summary>The ReBAC object id, for the same three shapes.</summary>
    public string ObjectId => objectId ?? string.Empty;

    internal GrainKey(GrainKeyKind kind, Guid id, string? name, string? digest) {
        Kind = kind;
        Id = id;
        this.name = name;
        this.digest = digest;
        objectType = null;
        objectId = null;
    }

    internal GrainKey(GrainKeyKind kind, string objectType, string objectId) {
        Kind = kind;
        Id = Guid.Empty;
        name = null;
        digest = null;
        this.objectType = objectType;
        this.objectId = objectId;
    }

    /// <summary>
    ///     Re-emits the key. <c>GrainKeys.Parse(k).GetValueOrThrow().ToString() == k</c> for every
    ///     <c>k</c> the parser accepts — enforced by the parser itself, see
    ///     <see cref="GrainKeys.Parse" />.
    /// </summary>
    public override string ToString() =>
        Kind switch {
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
            GrainKeyKind.ObjectRelations => GrainKeys.ObjectRelations(ObjectType, ObjectId),
            GrainKeyKind.SubjectRelations => GrainKeys.SubjectRelations(ObjectType, ObjectId),
            GrainKeyKind.CheckCache => GrainKeys.CheckCache(ObjectType, ObjectId),
            GrainKeyKind.TupleStore => GrainKeys.TupleStore(Id),
            GrainKeyKind.Group => GrainKeys.Group(Id),
            GrainKeyKind.Application => GrainKeys.Application(Id),
            GrainKeyKind.ServicePrincipal => GrainKeys.ServicePrincipal(Id),
            GrainKeyKind.Session => GrainKeys.Session(Id),
            GrainKeyKind.ManagedIdentity => GrainKeys.ManagedIdentity(Id),
            GrainKeyKind.ParkedResourceRegistry => GrainKeys.ParkedResourceRegistry(Id, Name),
            GrainKeyKind.ExpirySweeper => GrainKeys.ExpirySweeper(Id, Name),
            _ => string.Empty
        };
}

/// <summary>
///     The <b>only</b> type in Cyber Cloud allowed to format or parse a grain key — ADR-002
///     (docs/plan/02 § ADR-002) and the grain-key table at docs/plan/06 § Grain keys.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why a string key at all.</b> The brief says "GUID as ID"; <c>Orleans.Multitenant</c>
///         only supports string keys. Both requirements are satisfied: identifiers in the API, the
///         storage, the SDK and the URL are GUIDs, and the grain key is a composed string that
///         contains them. Nothing else in the codebase may concatenate one.
///     </para>
///     <para>
///         <b>The twenty-one shapes.</b> Eight of them are the table at docs/plan/06 § Grain keys;
///         two more — <see cref="Tenant" /> and <see cref="PlatformSingleton" /> — are the rows that
///         table is <i>missing</i> for grains docs/plan/04 § Grain taxonomy names in its Entity and
///         Platform rows; four are docs/plan/07 § Storage's authorization grains; five are
///         the rows that same table is missing for the grains docs/plan/11 § The object model names;
///         the twentieth is <see cref="ParkedResourceRegistry" />, which docs/plan/08 § Soft
///         delete names as <i>"a per-resource-group registry of parked resources … in one grain that
///         does not"</i> exist — it does now, so the row is here; and the twenty-first is
///         <see cref="ExpirySweeper" />, the thing that reads that registry on a clock, which
///         docs/plan/07 § Azure RBAC left owed as <i>"the caller of the mechanism"</i>.
///         See the remarks on each. Every one of them is formatted <i>and</i> parsed —
///         a key that can
///         be built but not decoded is half a type, and routing a physical key back to a grain type
///         (in a log, in a repair tool, in a dead-letter handler) needs the other half.
///     </para>
///     <para>
///         ⚠ <b>Twenty-one was twenty, was nineteen, and was eight before that, and the count is
///         re-derived rather than incremented.</b> Counted on 2026-09-05 off
///         <see cref="GrainKeyKind" />'s members, excluding <see cref="GrainKeyKind.None" />, which
///         is not a key — twenty-one members, of which <see cref="ExpirySweeper" /> is the one added
///         that day. It goes stale the moment a
///         member is added without this sentence being reread, which is exactly how issue #71 came to
///         describe this type as covering "eight key shapes today": eight is the size of
///         docs/plan/06's <i>table</i>, and it stopped being the size of this type eleven shapes ago.
///     </para>
///     <list type="table">
///         <item>
///             <term>
///                 <see cref="Subscription" />
///             </term>
///             <description>
///                 <c>sub/{subscriptionId:N}</c>
///             </description>
///         </item>
///         <item>
///             <term>
///                 <see cref="ResourceGroup" />
///             </term>
///             <description>
///                 <c>sub/{subscriptionId:N}/rg/{name}</c>
///             </description>
///         </item>
///         <item>
///             <term>
///                 <see cref="Resource" />
///             </term>
///             <description>
///                 <c>res/{resourceId:N}</c>
///             </description>
///         </item>
///         <item>
///             <term>
///                 <see cref="PathIndex" />
///             </term>
///             <description>
///                 <c>idx/path/{sha256(canonicalPath)[..16]}</c>
///             </description>
///         </item>
///         <item>
///             <term>
///                 <see cref="User" />
///             </term>
///             <description>
///                 <c>user/{userId:N}</c>
///             </description>
///         </item>
///         <item>
///             <term>
///                 <see cref="EmailIndex" />
///             </term>
///             <description>
///                 <c>idx/email/{sha256(tenantId + normalizedEmail)[..16]}</c>
///             </description>
///         </item>
///         <item>
///             <term>
///                 <see cref="Operation" />
///             </term>
///             <description>
///                 <c>op/{operationId:N}</c>
///             </description>
///         </item>
///         <item>
///             <term>
///                 <see cref="ClusterConnection" />
///             </term>
///             <description><c>cluster/{clusterId:N}</c> — <b>null tenant</b></description>
///         </item>
///         <item>
///             <term>
///                 <see cref="Tenant" />
///             </term>
///             <description><c>tenant/{tenantId:N}</c> — not in docs/plan/06's table</description>
///         </item>
///         <item>
///             <term>
///                 <see cref="PlatformSingleton" />
///             </term>
///             <description><c>platform/{name}</c> — <b>null tenant</b>, not in docs/plan/06's table</description>
///         </item>
///         <item>
///             <term>
///                 <see cref="ObjectRelations" />
///             </term>
///             <description><c>rel/obj/{type}/{id}</c> — docs/plan/07 § Storage</description>
///         </item>
///         <item>
///             <term>
///                 <see cref="SubjectRelations" />
///             </term>
///             <description><c>rel/sub/{type}/{id}</c> — docs/plan/07 § Storage</description>
///         </item>
///         <item>
///             <term>
///                 <see cref="CheckCache" />
///             </term>
///             <description><c>rel/check/{type}/{id}</c> — not in docs/plan/07's table</description>
///         </item>
///         <item>
///             <term>
///                 <see cref="TupleStore" />
///             </term>
///             <description><c>rel/store/{tenantId:N}</c> — not in docs/plan/07's table</description>
///         </item>
///         <item>
///             <term>
///                 <see cref="Group" />
///             </term>
///             <description><c>group/{groupId:N}</c> — not in docs/plan/06's table</description>
///         </item>
///         <item>
///             <term>
///                 <see cref="Application" />
///             </term>
///             <description><c>app/{applicationId:N}</c> — not in docs/plan/06's table</description>
///         </item>
///         <item>
///             <term>
///                 <see cref="ServicePrincipal" />
///             </term>
///             <description><c>sp/{servicePrincipalId:N}</c> — not in docs/plan/06's table</description>
///         </item>
///         <item>
///             <term>
///                 <see cref="Session" />
///             </term>
///             <description><c>session/{sessionId:N}</c> — <b>hot tier</b>, not in docs/plan/06's table</description>
///         </item>
///         <item>
///             <term>
///                 <see cref="ManagedIdentity" />
///             </term>
///             <description><c>mi/{managedIdentityId:N}</c> — docs/plan/11 § Managed identity</description>
///         </item>
///         <item>
///             <term>
///                 <see cref="ParkedResourceRegistry" />
///             </term>
///             <description>
///                 <c>parked/{subscriptionId:N}/rg/{name}</c> — docs/plan/08 § Soft delete
///             </description>
///         </item>
///         <item>
///             <term>
///                 <see cref="ExpirySweeper" />
///             </term>
///             <description>
///                 <c>sweep/{subscriptionId:N}/rg/{name}</c> — docs/plan/07 § Azure RBAC
///             </description>
///         </item>
///     </list>
///     <para>
///         The four <c>rel/</c> shapes are docs/plan/07 § Storage's, plus the two that document
///         names a mechanism for and never gives a key to. See each factory for which is which.
///         ⚠ <c>rel/idx/{usersetType}/{usersetId}</c> — the Leopard membership index — is
///         deliberately <b>absent</b>: it is M2 (docs/plan/07 § Effort and sequencing) and a key
///         shape with no grain behind it is a shape nothing can hold to its meaning.
///     </para>
///     <para>
///         ⚠ <b><see cref="Resource" /> is keyed by the resource GUID alone</b> — docs/plan/06 § Grain keys.
///         ADR-002 once showed <c>{sub:N}/{rg}/{type}/{res:N}</c> and docs/plan/02 § ADR-002 records
///         that 06 won: a key carrying the name would make a <i>rename</i> a grain migration, and one
///         carrying the resource group would make a <i>move</i> one too. The address stays in
///         <see cref="ResourceId" />, which is a different question — docs/plan/06 § Identifiers.
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
///         <c>cluster</c>, <c>group</c>, <c>app</c>, <c>sp</c>, <c>session</c>, <c>mi</c>,
///         <c>parked</c>, <c>sweep</c>, <c>idx</c>, <c>rel</c>, <c>tenant</c>, <c>platform</c>) and
///         its segment count, and the only caller-controlled component
///         — the resource group name, in <see cref="ResourceGroup" />, in
///         <see cref="ParkedResourceRegistry" /> and in <see cref="ExpirySweeper" />, which are all
///         the same name — is validated by
///         <see cref="ResourceNaming" />, which forbids <c>/</c>. A name that somehow carried a
///         <c>/</c> would change the segment count and be rejected on the way back in rather than
///         re-parsed as a different shape. See <c>GrainKeysTests</c> § key-shape collision.
///     </para>
/// </remarks>
public static class GrainKeys {
    /// <summary><c>sub/</c> — a subscription, and the head of a resource group key.</summary>
    public const string SubscriptionPrefix = "sub/";

    /// <summary><c>res/</c> — a resource.</summary>
    public const string ResourcePrefix = "res/";

    /// <summary><c>user/</c> — a user.</summary>
    public const string UserPrefix = "user/";

    /// <summary><c>op/</c> — an operation.</summary>
    public const string OperationPrefix = "op/";

    /// <summary><c>group/</c> — a group, docs/plan/11 § The object model.</summary>
    public const string GroupPrefix = "group/";

    /// <summary><c>app/</c> — an OAuth client registration, docs/plan/11 § The object model.</summary>
    public const string ApplicationPrefix = "app/";

    /// <summary><c>sp/</c> — a service principal, docs/plan/11 § The object model.</summary>
    public const string ServicePrincipalPrefix = "sp/";

    /// <summary><c>session/</c> — a sign-in session, docs/plan/11 § Sessions and revocation.</summary>
    public const string SessionPrefix = "session/";

    /// <summary><c>mi/</c> — a managed identity, docs/plan/11 § Managed identity.</summary>
    public const string ManagedIdentityPrefix = "mi/";

    /// <summary><c>cluster/</c> — a cluster connection. Null tenant.</summary>
    public const string ClusterConnectionPrefix = "cluster/";

    /// <summary>
    ///     <c>parked/</c> — a resource group's registry of soft-deleted resources, docs/plan/08
    ///     § Soft delete.
    /// </summary>
    public const string ParkedResourceRegistryPrefix = "parked/";

    /// <summary>
    ///     <c>sweep/</c> — the thing that ends a resource group's expired recovery windows,
    ///     docs/plan/07 § Azure RBAC.
    /// </summary>
    public const string ExpirySweeperPrefix = "sweep/";

    /// <summary><c>idx/path/</c> — the resource path index.</summary>
    public const string PathIndexPrefix = "idx/path/";

    /// <summary><c>idx/email/</c> — the per-tenant email index.</summary>
    public const string EmailIndexPrefix = "idx/email/";

    /// <summary><c>tenant/</c> — the tenant's own entity grain.</summary>
    public const string TenantPrefix = "tenant/";

    /// <summary><c>platform/</c> — a platform singleton. Null tenant.</summary>
    public const string PlatformSingletonPrefix = "platform/";

    /// <summary><c>rel/obj/</c> — <c>IObjectRelationsGrain</c>, docs/plan/07 § Storage.</summary>
    public const string ObjectRelationsPrefix = "rel/obj/";

    /// <summary><c>rel/sub/</c> — <c>ISubjectRelationsGrain</c>, docs/plan/07 § Storage.</summary>
    public const string SubjectRelationsPrefix = "rel/sub/";

    /// <summary><c>rel/check/</c> — <c>ICheckGrain</c>. Not a row in docs/plan/07's table.</summary>
    public const string CheckCachePrefix = "rel/check/";

    /// <summary><c>rel/store/</c> — <c>ITupleStoreGrain</c>. Not a row in docs/plan/07's table.</summary>
    public const string TupleStorePrefix = "rel/store/";

    /// <summary>The <c>rel</c> head shared by every authorization key shape.</summary>
    public const string RelationSegment = "rel";

    /// <summary><c>platform/shard-map</c> — <c>IShardMapGrain</c>'s singleton name.</summary>
    public const string ShardMapSingleton = "shard-map";

    /// <summary><c>platform/tenant-directory</c> — <c>ITenantDirectoryGrain</c>'s singleton name.</summary>
    public const string TenantDirectorySingleton = "tenant-directory";

    /// <summary>The <c>rg</c> literal in <c>sub/{subscriptionId:N}/rg/{name}</c>.</summary>
    public const string ResourceGroupSegment = "rg";

    /// <summary>
    ///     The number of hexadecimal characters an index digest carries — <c>sha256(x)[..16]</c>,
    ///     docs/plan/06 § Grain keys.
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

    /// <summary>The closed set of platform-singleton names.</summary>
    public static IReadOnlyList<string> PlatformSingletons { get; } =
        [ShardMapSingleton, TenantDirectorySingleton];

    // ── Formatting ─────────────────────────────────────────────────────────────────────────────

    /// <summary><c>sub/{subscriptionId:N}</c> — <c>ISubscriptionGrain</c>, docs/plan/06 § Grain keys.</summary>
    public static string Subscription(Guid subscriptionId) => SubscriptionPrefix + N(subscriptionId);

    /// <summary>
    ///     <c>sub/{subscriptionId:N}/rg/{name}</c> — <c>IResourceGroupGrain</c>, docs/plan/06 § Grain keys.
    /// </summary>
    /// <remarks>
    ///     The key nests under the subscription because a resource group name is unique within its
    ///     subscription, not within the tenant (docs/plan/06 § The hierarchy). <paramref name="name" /> is the
    ///     only caller-controlled text in any grain key, and it is validated here rather than
    ///     trusted: see the remarks on <see cref="GrainKeys" /> § collision.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="name" /> breaks <see cref="ResourceNaming" />.</exception>
    public static string ResourceGroup(Guid subscriptionId, string name) {
        var validated = ResourceNaming.EnsureValid(name, nameof(name), "resource group name");
        return SubscriptionPrefix + N(subscriptionId) + "/" + ResourceGroupSegment + "/" + validated;
    }

    /// <summary>
    ///     <c>parked/{subscriptionId:N}/rg/{name}</c> — <c>IParkedResourceRegistryGrain</c>, the
    ///     resource group's registry of soft-deleted resources (docs/plan/08 § Soft delete).
    /// </summary>
    /// <param name="subscriptionId">The subscription the group belongs to.</param>
    /// <param name="name">The resource group's name, validated as <see cref="ResourceGroup" /> validates it.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A SECOND SHAPE ADDRESSING THE SAME RESOURCE GROUP, AND THAT IS THE DECISION
    ///         RATHER THAN AN OVERSIGHT.</b> Orleans addresses an activation by (grain type, key), so
    ///         <c>IParkedResourceRegistryGrain</c> could have been reached with
    ///         <see cref="ResourceGroup" />'s key and no row would have been added to
    ///         <see cref="GrainKeyKind" /> at all. It must not be: this type documents every kind as
    ///         naming exactly one grain interface, and <see cref="Parse" /> exists so that a physical
    ///         key found in Redis, in a log line or in a dead-letter handler says which grain wrote
    ///         it. Two grain types behind one kind makes that answer ambiguous in precisely the
    ///         situation the parser is for, and the ambiguity is invisible until somebody is reading
    ///         a key at three in the morning.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Resource-group-scoped, and deliberately not subscription-scoped.</b> docs/plan/08
    ///         § Soft delete: <i>"its address … is no longer blocked — <c>ResourceCollectionId</c>
    ///         exists and is resource-group-scoped, so 'what is recoverable in this group of this
    ///         type' is expressible today; anything wider is still the addressing question, because
    ///         <c>ResourceId.ParsePath</c> has <c>const int fixedPrefix = 8</c> and no
    ///         subscription-scoped shape."</i> A <c>parked/{subscriptionId:N}</c> key covering a whole
    ///         subscription would be a listing with no address a caller could ask for, and it would
    ///         take the addressing decision by implication rather than on purpose.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>NOT a member of the group's own membership, which is the same refusal one level
    ///         down.</b> docs/plan/08 § Soft delete: the two collections <i>"answer different
    ///         questions to different callers, and merging them is exactly the <c>410 Gone</c> the
    ///         decision above refuses"</i> — a parked resource left in the group's listing hands a
    ///         caller who may list the group but may not read the resource the "something is held
    ///         here" signal. So it is a separate grain with a separate key, and
    ///         <c>OperationGrain.ParkAsync</c> writes this one at the same moment it clears the other.
    ///     </para>
    ///     <para>
    ///         The prefix is a word rather than an abbreviation because <c>del</c> reads a character
    ///         away from <c>rel</c>, and because "parked" is the verb the resource manager already
    ///         uses for this state — <c>OperationGrain.ParkAsync</c>,
    ///         <c>IResourceIndexGrain.ResolveSoftDeletedAsync</c>'s callers, and the operation
    ///         progress entry the park writes.
    ///     </para>
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="name" /> breaks <see cref="ResourceNaming" />.</exception>
    public static string ParkedResourceRegistry(Guid subscriptionId, string name) {
        var validated = ResourceNaming.EnsureValid(name, nameof(name), "resource group name");
        return ParkedResourceRegistryPrefix
            + N(subscriptionId)
            + "/"
            + ResourceGroupSegment
            + "/"
            + validated;
    }

    /// <summary>
    ///     <c>sweep/{subscriptionId:N}/rg/{name}</c> — <c>IExpirySweeperGrain</c>, the thing that
    ///     drives <c>IResourceManager.PurgeExpiredAsync</c> over that group's parked resources on a
    ///     clock (docs/plan/07 § Azure RBAC, issue #12).
    /// </summary>
    /// <param name="subscriptionId">The subscription the group belongs to.</param>
    /// <param name="name">The resource group's name, validated as <see cref="ResourceGroup" /> validates it.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A THIRD SHAPE FOR ONE RESOURCE GROUP, AND THE REASON IT IS NOT A REMINDER ON
    ///         <see cref="ParkedResourceRegistry" />'S GRAIN IS A CYCLE RATHER THAN A PREFERENCE.</b>
    ///         The sweep's whole job is to call <c>PurgeExpiredAsync</c>, and
    ///         <c>ResourceManagerService.PurgeCoreAsync</c> calls
    ///         <c>IParkedResourceRegistryGrain.UnparkAsync</c> — so a reminder that fired on the
    ///         registry grain would be an activation awaiting a call back into itself. Orleans
    ///         addresses that activation by (grain type, key) and a grain is non-reentrant unless it
    ///         says otherwise, so the
    ///         nested call queues behind the turn that is waiting for it and neither moves. (The
    ///         escape hatches — <c>[Reentrant]</c>, and call-chain reentrancy opted into per call —
    ///         are both ways of saying "let something else run in the middle of this grain's turn",
    ///         which is a strange thing to grant a registry three choreographies write to.) Every
    ///         other grain a purge touches — the resource, the index, the operation — is closed to a
    ///         sweeper for the same reason; a grain the purge never reaches is the only place the
    ///         driver can stand.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And even without the cycle it would still be a second grain.</b> One turn at a
    ///         time also means a sweep in progress delays every <c>ParkAsync</c> and
    ///         <c>UnparkAsync</c> in that group — a delete's park, a restore's unpark, a purge's
    ///         unpark — for as long as the sweep runs. Those are the three choreographies
    ///         <c>ParkedResourceRegistryGrain</c>'s remarks say it never reaches another grain in
    ///         order to stay out of, and the restore is the very request most likely to be racing the
    ///         sweep for the same resource.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A separate <see cref="GrainKeyKind" /> and not the registry's key, which
    ///         <see cref="ParkedResourceRegistry" />'s own remarks already settled for the general
    ///         case:</b> two grain types behind one kind makes <see cref="Parse" />'s answer
    ///         ambiguous in exactly the situation the parser exists for — a physical key found in
    ///         Redis, in a log line or in a dead-letter handler, being routed back to the grain that
    ///         wrote it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Resource-group-scoped because the registry it reads is</b>, and a sweeper wider
    ///         than its input would be a grain that has to enumerate resource groups in order to find
    ///         the enumeration it actually wants. The same boundary, for the same reason
    ///         <see cref="ParkedResourceRegistry" /> gives.
    ///     </para>
    ///     <para>
    ///         The prefix is <c>sweep</c> rather than <c>expiry</c> because the tree already calls
    ///         this kind of thing a sweep — <c>ResourceGroupGrain.OrphanReminderName</c> is
    ///         <c>reap-orphans</c> on an <c>OrphanSweepPeriod</c>, and docs/plan/08 § Soft delete
    ///         asks for a thing that <i>"sweeps an expired window"</i> in as many words.
    ///     </para>
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="name" /> breaks <see cref="ResourceNaming" />.</exception>
    public static string ExpirySweeper(Guid subscriptionId, string name) {
        var validated = ResourceNaming.EnsureValid(name, nameof(name), "resource group name");
        return ExpirySweeperPrefix + N(subscriptionId) + "/" + ResourceGroupSegment + "/" + validated;
    }

    /// <summary><c>res/{resourceId:N}</c> — <c>IResourceGrain</c>, docs/plan/06 § Grain keys.</summary>
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
    public static string PlatformSingleton(string name) {
        if (!PlatformSingletons.Contains(name, StringComparer.Ordinal)) {
            throw new ArgumentException(
                $"'{name}' is not a platform singleton. The set is closed and is "
                + $"[{string.Join(", ", PlatformSingletons)}] — docs/plan/04 § Grain taxonomy, the "
                + "Platform row. A key that varies per tenant is not a platform singleton.",
                nameof(name)
            );
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

    // ── The ReBAC shapes — docs/plan/07 § Storage ──────────────────────────────────────────────

    /// <summary>
    ///     <c>rel/obj/{type}/{id}</c> — <c>IObjectRelationsGrain</c>, every tuple whose <b>object</b>
    ///     is this one (docs/plan/07 § Storage, row 1).
    /// </summary>
    /// <param name="type">The object type, per <see cref="RelationNaming.IsName" />.</param>
    /// <param name="id">The object id, per <see cref="RelationNaming.IsId" />.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The id is a string and not a <see cref="Guid" />, deliberately.</b>
    ///         docs/plan/07 § The model says "ids are GUIDs", and for every tenant-owned object they
    ///         are — the <c>N</c> form satisfies <see cref="RelationNaming.IsId" /> and is what
    ///         callers pass. But docs/plan/06 § Platform administration already requires
    ///         <c>platform:root#operator</c>, whose id is the word <c>root</c>, and docs/plan/07's
    ///         own Azure table is written in named scopes. Forcing a GUID here would make the one
    ///         relation the tenancy layer already depends on unrepresentable, so the rule is
    ///         widened to <see cref="ResourceNaming" />'s — of which the <c>N</c> form is a subset.
    ///     </para>
    /// </remarks>
    /// <exception cref="ArgumentException">Either component breaks <see cref="RelationNaming" />.</exception>
    public static string ObjectRelations(string type, string id) => ObjectRelationsPrefix + EnsureObject(type, id);

    /// <summary>
    ///     <c>rel/sub/{type}/{id}</c> — <c>ISubjectRelationsGrain</c>, the reverse index: every
    ///     tuple whose <b>subject</b> is this one (docs/plan/07 § Storage, row 2).
    /// </summary>
    /// <param name="type">The subject's object type.</param>
    /// <param name="id">The subject's object id.</param>
    /// <remarks>
    ///     ⚠ The key carries no userset relation. <c>group:eng</c> and <c>group:eng#member</c> are
    ///     the <i>same</i> subject grain and two different entries inside it, because the reverse
    ///     index's question is "what does this object appear in", and both answers belong to
    ///     whoever is asking about <c>group:eng</c>.
    /// </remarks>
    /// <exception cref="ArgumentException">Either component breaks <see cref="RelationNaming" />.</exception>
    public static string SubjectRelations(string type, string id) => SubjectRelationsPrefix + EnsureObject(type, id);

    /// <summary>
    ///     <c>rel/check/{type}/{id}</c> — <c>ICheckGrain</c>, the hot-tier check cache for one
    ///     object.
    /// </summary>
    /// <param name="type">The object type.</param>
    /// <param name="id">The object id.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>DOC GAP, and this shape is the repair.</b> docs/plan/07 § Check declares
    ///         <c>ICheckGrain.CheckAsync</c> but its § Storage table has <b>three</b> rows and none
    ///         of them is the check grain — so the document names a grain and never says what
    ///         addresses it.
    ///     </para>
    ///     <para>
    ///         <b>Keyed by the object being checked</b>, because that is what the cache key of
    ///         docs/plan/07 § Caching across requests is anchored on:
    ///         <c>(tenant, object, permission, subject, schemaVersion, tenantRelationVersion)</c>.
    ///         The tenant is the qualification, the object is this key, and the remaining components
    ///         live inside the grain's hot state. One activation per object also means the check
    ///         path fans out no wider than the objects a walk actually visits.
    ///     </para>
    /// </remarks>
    /// <exception cref="ArgumentException">Either component breaks <see cref="RelationNaming" />.</exception>
    public static string CheckCache(string type, string id) => CheckCachePrefix + EnsureObject(type, id);

    /// <summary>
    ///     <c>rel/store/{tenantId:N}</c> — <c>ITupleStoreGrain</c>, the tenant's tuple writer and
    ///     the source of its relation version.
    /// </summary>
    /// <param name="tenantId">The tenant.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>DOC GAP.</b> docs/plan/07 § Consistency requires "a per-tenant monotonic version
    ///         returned by every tuple write", and § Storage requires that the two-grain write be
    ///         "ordered (object first, then subject) and reconciled by a sweeper". Both need a
    ///         per-tenant thing to live in, and the document names none. This is it.
    ///     </para>
    ///     <para>
    ///         <b>The tenant id is repeated inside a tenant-qualified key</b> for the same reason
    ///         <see cref="Tenant" /> repeats it: a key read outside its qualification still says
    ///         which tenant it is. The cardinality question (docs/plan/04 § Grain taxonomy) is
    ///         answered the same way as for the tenant directory — one activation per tenant, on a
    ///         path whose traffic is role assignments, which docs/plan/07 § Caching across requests
    ///         itself calls rare. Checks never write through it.
    ///     </para>
    /// </remarks>
    public static string TupleStore(Guid tenantId) => TupleStorePrefix + N(tenantId);

    /// <summary><c>user/{userId:N}</c> — <c>IUserGrain</c>, docs/plan/06 § Grain keys.</summary>
    public static string User(Guid userId) => UserPrefix + N(userId);

    /// <summary><c>op/{operationId:N}</c> — <c>IOperationGrain</c>, docs/plan/06 § Grain keys.</summary>
    public static string Operation(Guid operationId) => OperationPrefix + N(operationId);

    // ── The identity shapes — docs/plan/11 § The object model ──────────────────────────────────
    //
    // ⚠ DOC DEFECT, and these five rows are the repair. docs/plan/11 § The object model names six
    // grains — IUserGrain, IGroupGrain, IServicePrincipalGrain, IApplicationGrain,
    // IManagedIdentityGrain and ISessionGrain — and the grain-key table at docs/plan/06 § Grain keys
    // carries a row for exactly one of them, IUserGrain. That table's own ⚠ says what the
    // consequence is: "a grain missing from it is a grain that cannot be addressed … If you add a
    // grain, add its row here first."
    //
    // ⚠ ManagedIdentity was ABSENT here on purpose and is now present, and the reason it changed is
    // worth keeping. The earlier note said a key shape with no grain behind it "is a shape nothing
    // can hold to its meaning" — the same argument this type still makes for the Leopard membership
    // index — and that was right while docs/plan/11 § Managed identity was a seam. It is no longer:
    // IManagedIdentityGrain exists, holds the (cluster, namespace, serviceAccount) binding and the
    // cluster's OIDC issuer, and refuses a binding whose discovery document is unreachable. The row
    // was added WITH the grain rather than ahead of it, which is the order the ⚠ above asks for.
    //
    // All five are two-segment GUID keys and tenant-qualified, which is what makes them cheap: the
    // shape rules, the parser, the canonicity guard and the collision argument on this type all
    // already cover that form, so nothing about the closed set had to be loosened to admit them.

    /// <summary><c>group/{groupId:N}</c> — <c>IGroupGrain</c>, docs/plan/11 § The object model.</summary>
    /// <remarks>
    ///     ⚠ <b>A group's key carries no name, and the grain holds no member list.</b> docs/plan/11
    ///     § The object model: membership is the ReBAC tuple <c>group:X#member@user:Y</c>, so "is
    ///     Alice in Eng" is a <c>Check</c> and "who is in Eng" is an <c>Expand</c>. Keying by GUID
    ///     rather than by name is the same decision as <see cref="Resource" />'s — a rename would
    ///     otherwise be a grain migration — and it matters more here, because the ReBAC object id is
    ///     this GUID and re-keying a group would orphan every tuple naming it.
    /// </remarks>
    public static string Group(Guid groupId) => GroupPrefix + N(groupId);

    /// <summary>
    ///     <c>app/{applicationId:N}</c> — <c>IApplicationGrain</c>, the OAuth client registration.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Not keyed by <c>client_id</c>, and that is the trap worth naming.</b> The obvious key
    ///     is the client id, because that is what arrives on <c>/token</c>. It is wrong twice: the
    ///     client id is caller-controlled text on the hottest unauthenticated path in the platform,
    ///     so it would put an attacker in charge of which activation a request creates; and a client
    ///     id is per tenant, so <c>portal</c> in two tenants would be two activations whose keys
    ///     differ only by the qualification. The GUID is the identity and the client id is an
    ///     attribute resolved through the application's own tenant, exactly as an email is resolved
    ///     for a user.
    /// </remarks>
    public static string Application(Guid applicationId) => ApplicationPrefix + N(applicationId);

    /// <summary>
    ///     <c>sp/{servicePrincipalId:N}</c> — <c>IServicePrincipalGrain</c>, a machine identity.
    /// </summary>
    public static string ServicePrincipal(Guid servicePrincipalId) => ServicePrincipalPrefix + N(servicePrincipalId);

    /// <summary>
    ///     <c>session/{sessionId:N}</c> — <c>ISessionGrain</c>, docs/plan/11 § Sessions and
    ///     revocation. <b>Hot tier</b>, unlike every other identity grain.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The session id is not the refresh token, and the two must never be conflated.</b>
    ///         docs/plan/11 § Sessions and revocation: "refresh tokens carry a session id; refresh
    ///         checks the session is live". The session id therefore appears inside a token that
    ///         travels to a client, so it is an identifier an attacker can learn — which is fine,
    ///         because reaching this grain proves nothing on its own. Keying by the refresh token
    ///         instead would make the grain key a bearer secret, put that secret in every log line
    ///         and trace that prints a grain id, and defeat rotation, since a rotated token would be
    ///         a different grain and the chain would have nowhere to live.
    ///     </para>
    ///     <para>
    ///         <b>Cardinality</b> is one activation per sign-in session, which is the high-cardinality
    ///         answer docs/plan/04 § Grain taxonomy asks new grains for.
    ///     </para>
    /// </remarks>
    public static string Session(Guid sessionId) => SessionPrefix + N(sessionId);

    /// <summary>
    ///     <c>mi/{managedIdentityId:N}</c> — <c>IManagedIdentityGrain</c>, docs/plan/11 § Managed
    ///     identity.
    /// </summary>
    /// <param name="managedIdentityId">The managed identity.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Not keyed by <c>(cluster, namespace, serviceAccount)</c>, and that is the trap
    ///         worth naming.</b> The binding is what a token exchange arrives holding, so keying by it
    ///         looks like the shape that saves a lookup. It is wrong three ways: the triple is
    ///         caller-influenced text on an unauthenticated endpoint, so an attacker would choose
    ///         which activation a request creates; a rebind — pointing the same identity at a new
    ///         namespace, which is an ordinary operation — would become a grain migration and orphan
    ///         every ReBAC tuple naming the old key; and the same triple may legitimately be bound by
    ///         at most one identity, which is a <i>uniqueness</i> question and therefore an index's
    ///         job rather than a key's.
    ///     </para>
    ///     <para>
    ///         The GUID is the identity because docs/plan/11 § Managed identity step 6 makes it the
    ///         ReBAC subject id — <c>managedIdentity:{id}</c> — and re-keying would orphan every grant
    ///         made to it, exactly as it would for <see cref="Group" />.
    ///     </para>
    ///     <para>
    ///         <b>Cardinality</b> is one activation per managed identity, which is the high-cardinality
    ///         answer docs/plan/04 § Grain taxonomy asks new grains for. The prefix is <c>mi</c> and
    ///         not <c>managedidentity</c> only for the reason every other prefix here is short; it
    ///         collides with nothing, since shapes are fixed by their first segment and their segment
    ///         count.
    ///     </para>
    /// </remarks>
    public static string ManagedIdentity(Guid managedIdentityId) => ManagedIdentityPrefix + N(managedIdentityId);

    /// <summary>
    ///     <c>cluster/{clusterId:N}</c> — <c>IClusterConnectionGrain</c>, docs/plan/06 § Grain keys.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This key is NOT tenant-qualified.</b> docs/plan/06 § Grain keys makes
    ///     <c>IClusterConnectionGrain</c> a <i>null-tenant</i> grain on purpose: a cluster connection
    ///     holds a live client and a set of watches, there must be exactly one activation per cluster
    ///     platform-wide, and a cluster shared between a tenant and the platform (which every
    ///     in-house cluster is, while it is being created) would otherwise have two. The grain
    ///     carries its owning tenant as <i>state</i> and checks it on every call; this is the single
    ///     place tenancy is enforced by code rather than by key.
    ///     <para>
    ///         The encoding consequence, from ADR-002's corrected table (docs/plan/02 § ADR-002): the
    ///         null-tenant branch of <c>Orleans.Multitenant</c> is a <b>different</b> encoding — no
    ///         tenant prefix, no <c>'~'</c> rule, and the whole key has its <c>'|'</c> doubled. This
    ///         key contains no <c>'|'</c>, so it passes through as itself, and because the doubling
    ///         leaves no <i>un</i>doubled <c>'|'</c> anywhere, a null-tenant physical key can never
    ///         be read back as a tenanted one. Asserted by
    ///         <c>GrainKeysTests.TheClusterKeyIsANullTenantKeyAndCannotBeMistakenForATenantedOne</c>.
    ///     </para>
    /// </remarks>
    public static string ClusterConnection(Guid clusterId) => ClusterConnectionPrefix + N(clusterId);

    /// <summary>
    ///     <c>idx/path/{sha256(canonicalPath)[..16]}</c> — <c>IResourceIndexGrain</c>,
    ///     docs/plan/06 § Grain keys.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This takes a <see cref="Resources.ResourceId" /> and not a string, deliberately.</b>
    ///     The digest must be over <see cref="Resources.ResourceId.CanonicalPath" /> and never over
    ///     <see cref="Resources.ResourceId.Path" /> — docs/plan/06 § Identifiers and docs/plan/02 § ADR-002.
    ///     The provider namespace is case-preserving on the wire (<c>CyberCloud.Cache</c> reads
    ///     better than <c>cybercloud.cache</c>), so one resource has two <c>Path</c> spellings;
    ///     hashing <c>Path</c> would let both claim the name and defeat the two-phase create at
    ///     docs/plan/06 § Two-phase create, which is the one place a duplicate claim is a correctness bug rather
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
    public static string PathIndex(ResourceId id) => PathIndexPrefix + Digest(PathIndexPrefix, id.CanonicalPath);

    /// <summary>
    ///     <c>idx/email/{sha256(tenantId + normalizedEmail)[..16]}</c> — <c>IEmailIndexGrain</c>,
    ///     docs/plan/06 § Grain keys.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>
    ///             The tenant id is in the digest as well as in the tenant qualification, and that is
    ///             not redundant.
    ///         </b>
    ///         docs/plan/11 § Sign-up and tenant creation —
    ///         <i>
    ///             "email uniqueness is per tenant; global
    ///             email uniqueness would be a global index — the thing we do not have and do not
    ///             want"
    ///         </i>
    ///         . Keying on the tenant twice costs nothing and means the digest stays correct
    ///         if this grain is ever read outside its tenant qualification (a repair tool, an audit
    ///         export), which is exactly when a silently tenant-free key would be dangerous.
    ///     </para>
    ///     <para>
    ///         <b>
    ///             The normalization rule is <see cref="NormalizeEmail" />, and it is part of the
    ///             contract.
    ///         </b>
    ///         Storing a differently-normalized address on the user than the one that
    ///         went into this digest is how an account becomes unfindable by its own email.
    ///     </para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    ///     <paramref name="email" /> is not an address <see cref="NormalizeEmail" /> accepts.
    /// </exception>
    public static string EmailIndex(Guid tenantId, string email) {
        var normalized = NormalizeEmail(email);
        if (normalized.TryGetError(out var error)) {
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
    ///         The property this function must have is that
    ///         <b>
    ///             two different addresses never produce
    ///             one key
    ///         </b>
    ///         — a collision here is one account silently claiming another's identity at
    ///         sign-up, and the two-phase claim cannot distinguish it from a genuine duplicate.
    ///         <see cref="string.ToLowerInvariant" /> does not have that property: U+212A KELVIN SIGN
    ///         folds onto <c>'k'</c>, so <c>aK@example.com</c> and <c>ak@example.com</c> would
    ///         become one key. Folding only <c>A</c>-<c>Z</c> merges exactly one thing — the case of
    ///         an ASCII letter — which is the equivalence every mail provider actually implements, and
    ///         merges nothing else. The related trap in the other direction is U+0130 LATIN CAPITAL
    ///         LETTER I WITH DOT ABOVE, whose invariant lower-casing is <i>not</i> <c>"i"</c>; under
    ///         this rule it is left alone and stays a different address. Both are asserted by
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
    public static Result<string> NormalizeEmail(string? email) {
        if (email is null) {
            return InvalidEmail("null", "an email address is required");
        }

        var trimmed = email.AsSpan().Trim();
        if (trimmed.IsEmpty) {
            return InvalidEmail(email, "it is empty once surrounding white space is removed");
        }

        if (trimmed.Length > MaxEmailLength) {
            return InvalidEmail(
                email,
                "it is "
                + Int(trimmed.Length)
                + " characters long and RFC 5321 caps an address at "
                + Int(MaxEmailLength)
            );
        }

        var at = -1;
        for (var i = 0; i < trimmed.Length; i++) {
            var c = trimmed[i];

            if (char.IsWhiteSpace(c) || char.IsControl(c)) {
                return InvalidEmail(
                    email,
                    "it contains white space or a control character (U+"
                    + ((int)c).ToString("X4", CultureInfo.InvariantCulture)
                    + ") at position "
                    + Int(i)
                );
            }

            if (c != '@') {
                continue;
            }

            if (at >= 0) {
                return InvalidEmail(email, "it contains more than one '@'");
            }

            at = i;
        }

        if (at < 0) {
            return InvalidEmail(email, "it contains no '@'");
        }

        if (at == 0) {
            return InvalidEmail(email, "it has an empty local part — nothing before the '@'");
        }

        if (at == trimmed.Length - 1) {
            return InvalidEmail(email, "it has an empty domain — nothing after the '@'");
        }

        var buffer = trimmed.Length <= 256
            ? stackalloc char[trimmed.Length]
            : new char[trimmed.Length];

        for (var i = 0; i < trimmed.Length; i++) {
            var c = trimmed[i];
            buffer[i] = c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;
        }

        return Result<string>.Success(new(buffer));
    }

    // ── Parsing ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Parses a grain key within a tenant. Returns <see langword="false" /> for anything that is
    ///     not exactly one, and never throws.
    /// </summary>
    public static bool TryParse(string? keyWithinTenant, out GrainKey key) {
        key = default;
        var parsed = Parse(keyWithinTenant);
        if (parsed.IsFailure) {
            return false;
        }

        key = parsed.GetValueOrThrow();
        return true;
    }

    /// <summary><see cref="TryParse" /> with an explanation.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>
    ///             The parser accepts exactly the strings the formatters produce — no second
    ///             spelling.
    ///         </b>
    ///         Every shape is recognised by a case-sensitive first segment and an exact
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
    public static Result<GrainKey> Parse(string? keyWithinTenant) {
        if (string.IsNullOrEmpty(keyWithinTenant)) {
            return Invalid(
                "A grain key within a tenant is required. It is one of 'sub/{id}', "
                + "'sub/{id}/rg/{name}', 'parked/{id}/rg/{name}', 'sweep/{id}/rg/{name}', "
                + "'res/{id}', 'user/{id}', "
                + "'op/{id}', 'cluster/{id}', "
                + "'tenant/{id}', 'group/{id}', 'app/{id}', 'sp/{id}', 'session/{id}', 'mi/{id}', "
                + "'platform/{singleton}', 'idx/path/{digest}', "
                + "'idx/email/{digest}', 'rel/store/{tenantId}', 'rel/obj/{type}/{id}', "
                + "'rel/sub/{type}/{id}' or 'rel/check/{type}/{id}' — see docs/plan/06 § Grain keys, "
                + "docs/plan/07 § Storage, docs/plan/08 § Soft delete and docs/plan/11 § The object "
                + "model."
            );
        }

        var segments = keyWithinTenant.Split('/');
        foreach (var segment in segments) {
            if (segment.Length == 0) {
                return Invalid(
                    $"'{keyWithinTenant}' is not a grain key: it contains an empty segment (a "
                    + "doubled, leading or trailing '/')."
                );
            }
        }

        var parsed = segments.Length switch {
            2 => ParseTwoSegments(keyWithinTenant, segments),
            3 => ParseThreeSegments(keyWithinTenant, segments),
            4 => ParseFourSegments(keyWithinTenant, segments),
            _ => Invalid(
                $"'{keyWithinTenant}' is not a grain key: it has "
                + Int(segments.Length)
                + " '/'-separated segments and every grain key shape has 2, "
                + "3 or 4."
            )
        };

        if (parsed.TryGetError(out var error)) {
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
                + "rejected rather than silently accepted as a second activation."
            );
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
    public static bool IsTenantQualificationSafe(string? keyWithinTenant) {
        if (string.IsNullOrEmpty(keyWithinTenant)) {
            return false;
        }

        if (keyWithinTenant[0] is '|' or '~') {
            return false;
        }

        return !keyWithinTenant.Contains('|', StringComparison.Ordinal);
    }

    // ── Internals ──────────────────────────────────────────────────────────────────────────────

    static Result<GrainKey> ParseTwoSegments(string key, string[] segments) {
        if (string.Equals(segments[0], "platform", StringComparison.Ordinal)) {
            return PlatformSingletons.Contains(segments[1], StringComparer.Ordinal)
                ? Result<GrainKey>.Success(new(GrainKeyKind.PlatformSingleton, Guid.Empty, segments[1], null))
                : Invalid(
                    $"'{key}' is not a grain key: '{segments[1]}' is not a platform singleton. The "
                    + $"set is closed and is [{string.Join(", ", PlatformSingletons)}]."
                );
        }

        var kind = segments[0] switch {
            "sub" => GrainKeyKind.Subscription,
            "res" => GrainKeyKind.Resource,
            "user" => GrainKeyKind.User,
            "op" => GrainKeyKind.Operation,
            "cluster" => GrainKeyKind.ClusterConnection,
            "tenant" => GrainKeyKind.Tenant,
            "group" => GrainKeyKind.Group,
            "app" => GrainKeyKind.Application,
            "sp" => GrainKeyKind.ServicePrincipal,
            "session" => GrainKeyKind.Session,
            "mi" => GrainKeyKind.ManagedIdentity,
            _ => GrainKeyKind.None
        };

        if (kind == GrainKeyKind.None) {
            return Invalid(
                $"'{key}' is not a grain key: '{segments[0]}' is not one of 'sub', 'res', 'user', "
                + "'op', 'cluster', 'tenant', 'group', 'app', 'sp', 'session', 'mi' or 'platform'. "
                + "The prefix is matched case-sensitively — see docs/plan/06 § Grain keys and "
                + "docs/plan/11 § The object model."
            );
        }

        return GuidFormat.TryParseN(segments[1], out var id)
            ? Result<GrainKey>.Success(new(kind, id, null, null))
            : Invalid(
                $"'{segments[1]}' is not an id: a grain key spells GUIDs in the 32-digit "
                + "lower-case 'N' form, with no hyphens and no braces."
            );
    }

    static Result<GrainKey> ParseThreeSegments(string key, string[] segments) {
        if (string.Equals(segments[0], RelationSegment, StringComparison.Ordinal)) {
            if (!string.Equals(segments[1], "store", StringComparison.Ordinal)) {
                return Invalid(
                    $"'{key}' is not a grain key: the only three-segment 'rel' shape is "
                    + "'rel/store/{tenantId}' — docs/plan/07 § Consistency. 'rel/obj', 'rel/sub' "
                    + "and 'rel/check' take four segments."
                );
            }

            return GuidFormat.TryParseN(segments[2], out var tenantId)
                ? Result<GrainKey>.Success(new(GrainKeyKind.TupleStore, tenantId, null, null))
                : Invalid(
                    $"'{segments[2]}' is not a tenant id: a grain key spells GUIDs in the 32-digit "
                    + "lower-case 'N' form, with no hyphens and no braces."
                );
        }

        if (!string.Equals(segments[0], "idx", StringComparison.Ordinal)) {
            return Invalid(
                $"'{key}' is not a grain key: a three-segment key is an index or a tuple store and "
                + "must start with 'idx' or 'rel'."
            );
        }

        var kind = segments[1] switch {
            "path" => GrainKeyKind.PathIndex,
            "email" => GrainKeyKind.EmailIndex,
            _ => GrainKeyKind.None
        };

        if (kind == GrainKeyKind.None) {
            return Invalid(
                $"'{key}' is not a grain key: '{segments[1]}' is not an index. The two indexes are "
                + "'idx/path' (docs/plan/06 § Grain keys) and 'idx/email' (docs/plan/06 § Grain keys)."
            );
        }

        return IsDigest(segments[2])
            ? Result<GrainKey>.Success(new(kind, Guid.Empty, null, segments[2]))
            : Invalid(
                $"'{segments[2]}' is not an index digest: it must be exactly "
                + Int(DigestLength)
                + " lower-case hexadecimal characters, the first "
                + Int(DigestLength)
                + " of a SHA-256."
            );
    }

    static Result<GrainKey> ParseFourSegments(string key, string[] segments) {
        if (string.Equals(segments[0], RelationSegment, StringComparison.Ordinal)) {
            return ParseRelation(key, segments);
        }

        // ⚠ THREE SHAPES SHARE THIS TAIL AND ARE TOLD APART BY THEIR FIRST SEGMENT ALONE, which is
        // the same rule every other shape here is cut by. They address the same resource group
        // through three grain types — see ParkedResourceRegistry and ExpirySweeper — so the payload
        // is identical and only the kind differs; getting that fork wrong would route a listing of
        // parked resources at the group's own membership, which is the merge docs/plan/08 § Soft
        // delete refuses, or a sweep's reminder at the grain the sweep exists not to block.
        var groupKind = segments[0] switch {
            "sub" => GrainKeyKind.ResourceGroup,
            "parked" => GrainKeyKind.ParkedResourceRegistry,
            "sweep" => GrainKeyKind.ExpirySweeper,
            _ => GrainKeyKind.None
        };

        if (groupKind == GrainKeyKind.None
            || !string.Equals(segments[2], ResourceGroupSegment, StringComparison.Ordinal)) {
            return Invalid(
                $"'{key}' is not a grain key: the four-segment shapes are "
                + "'sub/{subscriptionId}/rg/{name}' (docs/plan/06 § Grain keys), "
                + "'parked/{subscriptionId}/rg/{name}' (docs/plan/08 § Soft delete), "
                + "'sweep/{subscriptionId}/rg/{name}' (docs/plan/07 § Azure RBAC) and "
                + "'rel/{obj|sub|check}/{type}/{id}' (docs/plan/07 § Storage)."
            );
        }

        if (!GuidFormat.TryParseN(segments[1], out var subscriptionId)) {
            return Invalid(
                $"'{segments[1]}' is not a subscription id: a grain key spells GUIDs in the "
                + "32-digit lower-case 'N' form, with no hyphens and no braces."
            );
        }

        var name = ResourceNaming.Validate(segments[3], "resource group name");
        return name.TryGetError(out var error)
            ? Result<GrainKey>.Failure(new(ErrorCode.InvalidGrainKey, error.Message))
            : Result<GrainKey>.Success(new(groupKind, subscriptionId, segments[3], null));
    }

    static Result<GrainKey> ParseRelation(string key, string[] segments) {
        var kind = segments[1] switch {
            "obj" => GrainKeyKind.ObjectRelations,
            "sub" => GrainKeyKind.SubjectRelations,
            "check" => GrainKeyKind.CheckCache,
            _ => GrainKeyKind.None
        };

        if (kind == GrainKeyKind.None) {
            return Invalid(
                $"'{key}' is not a grain key: '{segments[1]}' is not an authorization shape. The "
                + "four-segment 'rel' shapes are 'rel/obj' (the tuples whose object this is), "
                + "'rel/sub' (the reverse index) and 'rel/check' (the check cache) — docs/plan/07 "
                + "§ Storage."
            );
        }

        var type = RelationNaming.ValidateName(segments[2], "object type");
        if (type.TryGetError(out var typeError)) {
            return Result<GrainKey>.Failure(new(ErrorCode.InvalidGrainKey, typeError.Message));
        }

        var id = RelationNaming.ValidateId(segments[3]);
        return id.TryGetError(out var idError)
            ? Result<GrainKey>.Failure(new(ErrorCode.InvalidGrainKey, idError.Message))
            : Result<GrainKey>.Success(new(kind, segments[2], segments[3]));
    }

    /// <summary>
    ///     Validates the two components of a <c>rel/…/{type}/{id}</c> key and returns
    ///     <c>{type}/{id}</c>.
    /// </summary>
    /// <exception cref="ArgumentException">Either component is not legal.</exception>
    static string EnsureObject(string type, string id) {
        var validType = RelationNaming.ValidateName(type, "object type");
        if (validType.TryGetError(out var typeError)) {
            throw new ArgumentException(typeError.Message, nameof(type));
        }

        var validId = RelationNaming.ValidateId(id);
        if (validId.TryGetError(out var idError)) {
            throw new ArgumentException(idError.Message, nameof(id));
        }

        return type + "/" + id;
    }

    static bool IsDigest(string value) {
        if (value.Length != DigestLength) {
            return false;
        }

        foreach (var c in value) {
            if (c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) {
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
    static string Digest(string purpose, string value) {
        var input = purpose + "\n" + value;
        var bytes = Encoding.UTF8.GetBytes(input);

        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(bytes, hash);

        return Convert.ToHexStringLower(hash[..DigestBytes]);
    }

    static string N(Guid value) => value.ToString("N", CultureInfo.InvariantCulture);

    static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);

    static Result<GrainKey> Invalid(string message) => Result<GrainKey>.Failure(ErrorCode.InvalidGrainKey, message);

    static Result<string> InvalidEmail(string shown, string problem) =>
        Result<string>.Failure(
            ErrorCode.InvalidGrainKey,
            $"'{shown}' is not an email address: {problem}. An address is 1-"
            + Int(MaxEmailLength)
            + " characters with exactly one '@', a non-empty local part and a "
            + "non-empty domain, and no white space or control characters. See docs/plan/11 "
            + "§ Sign-up and tenant creation."
        );
}
