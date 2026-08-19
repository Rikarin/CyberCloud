// ── The ReBAC vocabulary, in the assembly both sides of it already reference ─────────────────────
//
// ⚠ THESE FOUR CLASSES USED TO LIVE IN TWO ASSEMBLIES AND THAT IS WHAT THIS FILE FIXES.
//
// ObjectTypes, Relations and Permissions were declared in CyberCloud.Authorization/Schema/
// CyberCloudSchema.cs — the IMPLEMENTATION assembly — next to the schema they describe.
// CyberCloud.Identity needed `group`, `member` and `user` to write membership tuples, so
// CyberCloud.Identity.csproj carried a ProjectReference on the implementation assembly: the only
// module-to-implementation edge in the tree outside providers. SubjectTypes was the same problem
// from the other end — CyberCloud.Identity.Contracts could not reach ObjectTypes without that same
// reference, so it re-declared `user` and added `servicePrincipal` and `managedIdentity` beside it,
// with its own remarks calling the duplication "owed a fix, which is to lift the subject types into
// CyberCloud.Authorization.Contracts where both sides already look".
//
// Both are gone now: the vocabulary is here, CyberCloud.Identity references only .Contracts, and
// SubjectTypes.User IS ObjectTypes.User rather than a second string that has to agree with it.
//
// ⚠ THE SCHEMA ITSELF DID NOT COME WITH IT, DELIBERATELY. AuthorizationWireTypes.cs § ObjectRef says
// why: "Wire validation is about the grammar, the schema is about the vocabulary, and conflating
// them would put the schema in the .Contracts assembly where the gateway would have to carry it."
// That argument is about AuthorizationSchema and CyberCloudSchema — the built rewrite graph, its
// evaluator and its version — not about the names. Names are what a caller has to spell; the graph
// is what the engine walks. Only the first is a contract.
//
// ⚠ WHAT MOVED CARRIES NO [Alias] AND COULD NOT. Every member below is a `const string` on a static
// class, so nothing here is a wire type, nothing is [GenerateSerializer], and there is no alias to
// retire — which is the one hazard IdentityWireTypes.cs' header block warns about for a type that
// changes assemblies, and the one that stops being free at the first release tag. The strings
// themselves are the compatibility surface, and they are pinned byte-for-byte by
// CyberCloud.Authorization.Contracts.Tests.AuthorizationVocabularyTests.

using CyberCloud.Core;
using System.Collections.Frozen;

namespace CyberCloud.Authorization.Contracts;

/// <summary>The object types the built-in schema defines. Constants so a typo is <c>CS0117</c>.</summary>
/// <remarks>
///     ⚠ <b>Every constant here is a type <c>CyberCloudSchema</c> defines, and
///     <c>PermissionNameTests.EveryObjectTypeConstantIsInTheSchema</c> is what keeps that true.</b>
///     A constant naming a type the schema does not define would compile at every call site and fail
///     every tuple write against it with <see cref="ErrorCode.SchemaInvalid" /> — the failure that
///     reads as a permissions bug. See <see cref="SubjectTypes" /> for the two ReBAC subject
///     spellings that are deliberately <i>not</i> here.
/// </remarks>
public static class ObjectTypes {
    /// <summary>The tenant — docs/plan/06 § The hierarchy, the top of it.</summary>
    public const string Tenant = "tenant";

    /// <summary>A subscription.</summary>
    public const string Subscription = "subscription";

    /// <summary>A resource group.</summary>
    public const string ResourceGroup = "resourceGroup";

    /// <summary>A resource.</summary>
    public const string Resource = "resource";

    /// <summary>A group, whose <c>member</c> userset is the subject of most role assignments.</summary>
    public const string Group = "group";

    /// <summary>A user. A subject type — it has no relations of its own.</summary>
    public const string User = "user";

    /// <summary>The platform itself. <c>platform:root#operator</c> — docs/plan/06 § Platform administration.</summary>
    public const string Platform = "platform";
}

/// <summary>The relation names the built-in schema defines.</summary>
public static class Relations {
    /// <summary>The scope one level up. The tupleset every inheritance rewrite follows.</summary>
    public const string Parent = "parent";

    /// <summary>Azure's <c>Owner</c>.</summary>
    public const string Owner = "owner";

    /// <summary>Azure's <c>Contributor</c>.</summary>
    public const string Contributor = "contributor";

    /// <summary>Azure's <c>Reader</c>.</summary>
    public const string Reader = "reader";

    /// <summary>
    ///     Azure's deny assignment — docs/plan/07 § Azure RBAC, expressed in it, row 4. The only
    ///     relation any permission negates, and therefore the only one that has to be direct.
    /// </summary>
    public const string Suspended = "suspended";

    /// <summary>Group membership. The userset in <c>group:eng#member</c>.</summary>
    public const string Member = "member";

    /// <summary>A platform operator — the relation docs/plan/06 § Platform administration names.</summary>
    public const string Operator = "operator";
}

/// <summary>The permission names the built-in schema defines.</summary>
/// <remarks>
///     ⚠ Using these at a <c>[RequiresPermission]</c> call site is what turns a typo into a
///     compile error. Nothing forces a caller to; see
///     <see cref="RequiresPermissionAttribute" /> for exactly how far enforcement actually goes.
/// </remarks>
public static class Permissions {
    /// <summary>Read the object.</summary>
    public const string Read = "read";

    /// <summary>Change the object.</summary>
    public const string Write = "write";

    /// <summary>Delete the object.</summary>
    public const string Delete = "delete";

    /// <summary>Assign a role at this scope. One of the two permissions with a negation in it.</summary>
    public const string AssignRole = "assignRole";

    /// <summary>
    ///     Destroy a soft-deleted resource permanently, ending its recovery window early.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Defined on <see cref="ObjectTypes.Resource" /> and on nothing else</b>, because a
    ///         tenant, a subscription and a resource group are not soft-deletable — only a resource
    ///         whose type declares <c>SupportsSoftDelete</c> is ever parked.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>THIS STRING IS ALSO WRITTEN DOWN IN A PLACE THAT CANNOT SEE IT.</b>
    ///         <c>SoftDeletePolicy.DefaultPurgePermission</c> in
    ///         <c>CyberCloud.ResourceManager.Contracts</c> is what a provider's
    ///         <c>SupportsSoftDelete</c> defaults to, and that assembly does not reference this one. The
    ///         two must spell the same permission and nothing in the compiler says so, which is exactly
    ///         how this permission came to be named by the registry and defined by no schema: every
    ///         purge test in the repository ran against a doubled authorizer, so a permission that
    ///         always evaluated false looked identical to one that worked.
    ///         <c>PurgePermissionTests</c> is the assertion that they agree.
    ///     </para>
    /// </remarks>
    public const string Purge = "purge";

    /// <summary>Administer the platform.</summary>
    public const string Administer = "administer";
}

/// <summary>
///     The subject types a Cyber Cloud access token's <c>sub_typ</c> claim may name.
///     <b>The set is closed.</b> docs/plan/07 § The model, docs/plan/11 § The object model.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>These are ReBAC subject spellings and they are matched ordinally.</b> A token that
///         said <c>serviceprincipal</c> where the tuple store says <c>servicePrincipal</c> would
///         produce a <see cref="SubjectRef" /> that matches no tuple, so every <c>Check</c> would
///         deny — which looks like a permissions bug and is a spelling bug. That is why
///         <see cref="Ensure" /> does not case-fold and why
///         <c>AuthorizationVocabularyTests</c> pins each literal byte-for-byte.
///     </para>
///     <para>
///         ⚠ <b><see cref="User" /> is <see cref="ObjectTypes.User" /> rather than a second
///         <c>"user"</c>, and that is the point of this class living here.</b> It used to be declared
///         in <c>CyberCloud.Identity.Contracts</c>, which could not name <c>ObjectTypes</c> — so the
///         two spellings agreed by assertion rather than by construction. They now cannot disagree.
///     </para>
///     <para>
///         ⚠ <b><see cref="ServicePrincipal" /> and <see cref="ManagedIdentity" /> are here and not
///         in <see cref="ObjectTypes" />, and the asymmetry is real rather than an oversight.</b>
///         <see cref="ObjectTypes" /> is what <c>CyberCloudSchema</c> <i>defines</i>, and
///         <c>TupleStoreGrain.Validate</c> checks a tuple's <c>Object.Type</c> against the schema and
///         its <c>Subject</c> against nothing — a subject type needs no schema entry, because nothing
///         is ever checked <i>on</i> a service principal. Adding them to <see cref="ObjectTypes" />
///         would fail <c>PermissionNameTests.EveryObjectTypeConstantIsInTheSchema</c> until they were
///         also <c>DefineType</c>d, and defining a type is a schema change and a
///         <c>CyberCloudSchema.SchemaVersion</c> bump — which invalidates every cached check — for no
///         behaviour anything today would gain.
///     </para>
///     <para>
///         ⚠ <b><c>group</c> is deliberately absent.</b> A group is a ReBAC object and the
///         <i>subject</i> of role assignments through its <c>member</c> userset, but nothing ever
///         signs in as one — there is no credential a group holds. A token whose subject type were
///         <c>group</c> would be a bearer credential for everyone in it.
///     </para>
/// </remarks>
public static class SubjectTypes {
    /// <summary>A human. docs/plan/11 § The object model.</summary>
    public const string User = ObjectTypes.User;

    /// <summary>A machine identity with a client secret or a certificate.</summary>
    public const string ServicePrincipal = "servicePrincipal";

    /// <summary>
    ///     A workload identity bound to <c>(cluster, namespace, serviceAccount)</c> — the third
    ///     subject type, and the one that holds no credential at all. docs/plan/11 § Managed identity.
    /// </summary>
    public const string ManagedIdentity = "managedIdentity";

    /// <summary>The closed set, in the order docs/plan/11 § The object model names them.</summary>
    public static FrozenSet<string> All { get; } =
        new[] { User, ServicePrincipal, ManagedIdentity }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    ///     Whether <paramref name="subjectType" /> is one of <see cref="All" />, and a failure that
    ///     names the closed set when it is not.
    /// </summary>
    /// <param name="subjectType">The candidate.</param>
    /// <remarks>
    ///     ⚠ Ordinal and case-sensitive, because the value is a ReBAC subject type and the tuple store
    ///     is case-sensitive. Accepting <c>User</c> here would mint a token whose every check denies.
    /// </remarks>
    public static Result Ensure(string? subjectType) =>
        subjectType is not null && All.Contains(subjectType)
            ? Result.Success
            : Result.Failure(
                ErrorCode.InternalError,
                $"'{subjectType}' is not a subject type. The set is closed and is "
                + $"[{string.Join(", ", All.Order(StringComparer.Ordinal))}] — docs/plan/07 § The "
                + "model. It is matched ordinally because it is a ReBAC subject type, and a token "
                + "carrying a differently-cased spelling would produce a subject no tuple names."
            );
}
