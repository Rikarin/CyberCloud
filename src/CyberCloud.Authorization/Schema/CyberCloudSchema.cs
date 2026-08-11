using static CyberCloud.Authorization.Rewrite;

namespace CyberCloud.Authorization;

/// <summary>The object types the built-in schema defines. Constants so a typo is <c>CS0117</c>.</summary>
public static class ObjectTypes
{
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
public static class Relations
{
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
///     <c>RequiresPermissionAttribute</c> for exactly how far enforcement actually goes.
/// </remarks>
public static class Permissions
{
    /// <summary>Read the object.</summary>
    public const string Read = "read";

    /// <summary>Change the object.</summary>
    public const string Write = "write";

    /// <summary>Delete the object.</summary>
    public const string Delete = "delete";

    /// <summary>Assign a role at this scope. The one permission with a negation in it.</summary>
    public const string AssignRole = "assignRole";

    /// <summary>Administer the platform.</summary>
    public const string Administer = "administer";
}

/// <summary>
///     The built-in schema — docs/plan/07 § Azure RBAC, expressed in it, as C#.
/// </summary>
/// <remarks>
///     <para>
///         The four mappings of that section, and where each one is in this file:
///     </para>
///     <list type="table">
///         <item>
///             <term><c>Owner</c> on subscription <c>S</c> for user <c>U</c></term>
///             <description>
///                 the tuple <c>subscription:S#owner@user:U</c> — the <c>Role("owner", …)</c> line
///                 on <see cref="ObjectTypes.Subscription" />.
///             </description>
///         </item>
///         <item>
///             <term><c>Contributor</c> on resource group <c>R</c> for group <c>G</c></term>
///             <description>
///                 the tuple <c>resourceGroup:R#contributor@group:G#member</c> — the same line on
///                 <see cref="ObjectTypes.ResourceGroup" />, with a <i>userset</i> subject.
///             </description>
///         </item>
///         <item>
///             <term>Inheritance sub → rg → resource</term>
///             <description>
///                 <c>From("parent", …)</c> on every role of every scope. <b>No tuple is written per
///                 resource</b>, which is the whole argument for <c>From(…)</c> and is what
///                 <c>RoleAssignmentViewTests</c> asserts.
///             </description>
///         </item>
///         <item>
///             <term>Deny assignment</term>
///             <description>
///                 <c>#suspended</c> and the <c>&amp; !Rel("suspended")</c> in
///                 <see cref="Permissions.AssignRole" />.
///             </description>
///         </item>
///     </list>
///     <para>
///         ⚠ <b>Only <see cref="Permissions.AssignRole" /> carries the deny check, and that is the
///         document's shape rather than a simplification.</b> docs/plan/07 § The model's example
///         puts <c>&amp; !Rel("suspended")</c> on <c>assignRole</c> and on nothing else. Extending
///         it to <c>delete</c> or <c>write</c> is a schema change and a version bump, not an edit.
///     </para>
///     <para>
///         ⚠ <b><see cref="ObjectTypes.User" /> has no members and that is correct.</b> A user is a
///         subject, not a scope: nothing is ever <i>checked on</i> a user. It is declared so the
///         vocabulary is complete and so a tuple naming <c>user:…</c> as an object fails against a
///         known type rather than an unknown one.
///     </para>
/// </remarks>
public static class CyberCloudSchema
{
    /// <summary>
    ///     The schema version. ⚠ Bump on <b>every</b> change to the rewrites below: it is a
    ///     component of the check cache key (docs/plan/07 § Caching across requests), and a cached
    ///     answer computed under a different rewrite is not an answer to the same question.
    /// </summary>
    public const int SchemaVersion = 1;

    /// <summary>The built-in schema, built once.</summary>
    public static AuthorizationSchema Instance { get; } = Build();

    static AuthorizationSchema Build() =>
        Schema.Create(SchemaVersion)
            .DefineType(ObjectTypes.Tenant)
                .Role(Relations.Owner, This)
                .Role(Relations.Contributor, This | Rel(Relations.Owner))
                .Role(Relations.Reader, This | Rel(Relations.Contributor))
                .Relation(Relations.Suspended)
                .Permission(Permissions.Read, Rel(Relations.Reader))
                .Permission(Permissions.Write, Rel(Relations.Contributor))
                .Permission(Permissions.Delete, Rel(Relations.Owner))
                .Permission(
                    Permissions.AssignRole,
                    Rel(Relations.Owner) & !Rel(Relations.Suspended))

            .DefineType(ObjectTypes.Subscription)
                .Relation(Relations.Parent)
                .Role(Relations.Owner, This | From(Relations.Parent, Relations.Owner))
                .Role(
                    Relations.Contributor,
                    This | From(Relations.Parent, Relations.Contributor) | Rel(Relations.Owner))
                .Role(
                    Relations.Reader,
                    This | From(Relations.Parent, Relations.Reader) | Rel(Relations.Contributor))
                .Relation(Relations.Suspended)
                .Permission(Permissions.Read, Rel(Relations.Reader))
                .Permission(Permissions.Write, Rel(Relations.Contributor))
                .Permission(Permissions.Delete, Rel(Relations.Owner))
                .Permission(
                    Permissions.AssignRole,
                    Rel(Relations.Owner) & !Rel(Relations.Suspended))

            .DefineType(ObjectTypes.ResourceGroup)
                .Relation(Relations.Parent)
                .Role(Relations.Owner, This | From(Relations.Parent, Relations.Owner))
                .Role(
                    Relations.Contributor,
                    This | From(Relations.Parent, Relations.Contributor) | Rel(Relations.Owner))
                .Role(
                    Relations.Reader,
                    This | From(Relations.Parent, Relations.Reader) | Rel(Relations.Contributor))
                .Relation(Relations.Suspended)
                .Permission(Permissions.Read, Rel(Relations.Reader))
                .Permission(Permissions.Write, Rel(Relations.Contributor))
                .Permission(Permissions.Delete, Rel(Relations.Owner))
                .Permission(
                    Permissions.AssignRole,
                    Rel(Relations.Owner) & !Rel(Relations.Suspended))

            .DefineType(ObjectTypes.Resource)
                .Relation(Relations.Parent)
                .Role(Relations.Owner, This | From(Relations.Parent, Relations.Owner))
                .Role(
                    Relations.Contributor,
                    This | From(Relations.Parent, Relations.Contributor) | Rel(Relations.Owner))
                .Role(
                    Relations.Reader,
                    This | From(Relations.Parent, Relations.Reader) | Rel(Relations.Contributor))
                .Relation(Relations.Suspended)
                .Permission(Permissions.Read, Rel(Relations.Reader))
                .Permission(Permissions.Write, Rel(Relations.Contributor))
                .Permission(Permissions.Delete, Rel(Relations.Owner))
                .Permission(
                    Permissions.AssignRole,
                    Rel(Relations.Owner) & !Rel(Relations.Suspended))

            .DefineType(ObjectTypes.Group)
                // Direct only, and nested groups work because a tuple's SUBJECT may itself be the
                // userset `group:platform#member` — docs/plan/07 § The model's fourth example.
                // That nesting is walked by the evaluator, which is exactly the cost the Leopard
                // index removes in M2.
                .Relation(Relations.Member)
                .Role(Relations.Owner, This)
                .Permission(Permissions.Read, Rel(Relations.Member))
                .Permission(Permissions.Write, Rel(Relations.Owner))

            .DefineType(ObjectTypes.Platform)
                .Relation(Relations.Operator)
                .Permission(Permissions.Administer, Rel(Relations.Operator))

            .DefineType(ObjectTypes.User)

            .Build();
}
