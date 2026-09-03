// ⚠ ObjectTypes, Relations and Permissions ARE NOT IN THIS FILE ANY MORE. They are the vocabulary
// rather than the schema — the names a caller has to spell, as against the rewrite graph the
// evaluator walks — and they now live in CyberCloud.Authorization.Contracts/
// AuthorizationVocabulary.cs, beside SubjectTypes. That file's header says what the split buys and
// why the schema below stayed here. The types below still reference them by their simple names,
// because the `using` at the top of this file imports the contracts namespace.

using CyberCloud.Authorization.Contracts;
using static CyberCloud.Authorization.Rewrite;

namespace CyberCloud.Authorization;

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
///                 <c>From("parent", …)</c> on every role of every scope.
///                 <b>
///                     No tuple is written per
///                     resource
///                 </b>
///                 , which is the whole argument for <c>From(…)</c> and is what
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
///         ⚠
///         <b>
///             Only <see cref="Permissions.AssignRole" /> carries the deny check, and that is the
///             document's shape rather than a simplification.
///         </b>
///         docs/plan/07 § The model's example
///         puts <c>&amp; !Rel("suspended")</c> on <c>assignRole</c>, and <c>resource</c>'s
///         <c>purge</c> is the second permission to carry it — added with a version bump, which is
///         what that costs. Extending it to <c>delete</c> or <c>write</c> is the same kind of change
///         and the same kind of cost, not an edit.
///     </para>
///     <para>
///         ⚠ <b><see cref="ObjectTypes.User" /> has no members and that is correct.</b> A user is a
///         subject, not a scope: nothing is ever <i>checked on</i> a user. It is declared so the
///         vocabulary is complete and so a tuple naming <c>user:…</c> as an object fails against a
///         known type rather than an unknown one.
///     </para>
/// </remarks>
public static class CyberCloudSchema {
    /// <summary>
    ///     The schema version. ⚠ Bump on <b>every</b> change to the rewrites below: it is a
    ///     component of the check cache key (docs/plan/07 § Caching across requests), and a cached
    ///     answer computed under a different rewrite is not an answer to the same question.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>2 since <see cref="Permissions.Purge" /> was defined on
    ///     <see cref="ObjectTypes.Resource" />.</b> It was 1 while the resource manager checked a
    ///     <c>purge</c> permission this schema did not declare — see the remarks on that permission for
    ///     how a permission that always evaluated false went unnoticed.
    /// </remarks>
    public const int SchemaVersion = 2;

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
                Rel(Relations.Owner) & !Rel(Relations.Suspended)
            )
            .DefineType(ObjectTypes.Subscription)
            .Relation(Relations.Parent)
            .Role(Relations.Owner, This | From(Relations.Parent, Relations.Owner))
            .Role(
                Relations.Contributor,
                This | From(Relations.Parent, Relations.Contributor) | Rel(Relations.Owner)
            )
            .Role(
                Relations.Reader,
                This | From(Relations.Parent, Relations.Reader) | Rel(Relations.Contributor)
            )
            .Relation(Relations.Suspended)
            .Permission(Permissions.Read, Rel(Relations.Reader))
            .Permission(Permissions.Write, Rel(Relations.Contributor))
            .Permission(Permissions.Delete, Rel(Relations.Owner))
            .Permission(
                Permissions.AssignRole,
                Rel(Relations.Owner) & !Rel(Relations.Suspended)
            )
            .DefineType(ObjectTypes.ResourceGroup)
            .Relation(Relations.Parent)
            .Role(Relations.Owner, This | From(Relations.Parent, Relations.Owner))
            .Role(
                Relations.Contributor,
                This | From(Relations.Parent, Relations.Contributor) | Rel(Relations.Owner)
            )
            .Role(
                Relations.Reader,
                This | From(Relations.Parent, Relations.Reader) | Rel(Relations.Contributor)
            )
            .Relation(Relations.Suspended)
            .Permission(Permissions.Read, Rel(Relations.Reader))
            .Permission(Permissions.Write, Rel(Relations.Contributor))
            .Permission(Permissions.Delete, Rel(Relations.Owner))
            .Permission(
                Permissions.AssignRole,
                Rel(Relations.Owner) & !Rel(Relations.Suspended)
            )
            .DefineType(ObjectTypes.Resource)
            .Relation(Relations.Parent)
            .Role(Relations.Owner, This | From(Relations.Parent, Relations.Owner))
            .Role(
                Relations.Contributor,
                This | From(Relations.Parent, Relations.Contributor) | Rel(Relations.Owner)
            )
            .Role(
                Relations.Reader,
                This | From(Relations.Parent, Relations.Reader) | Rel(Relations.Contributor)
            )
            .Relation(Relations.Suspended)
            .Permission(Permissions.Read, Rel(Relations.Reader))
            .Permission(Permissions.Write, Rel(Relations.Contributor))
            .Permission(Permissions.Delete, Rel(Relations.Owner))
            .Permission(
                Permissions.AssignRole,
                Rel(Relations.Owner) & !Rel(Relations.Suspended)
            )
            // ⚠ THE PERMISSION THE RESOURCE MANAGER HAS BEEN CHECKING SINCE SOFT DELETE SHIPPED, AND
            // THAT NOTHING DEFINED UNTIL NOW.
            //
            // SoftDeletePolicy.DefaultPurgePermission is "purge" and ResourceManagerService.PurgeAsync
            // checks it through the real authorizer. A permission this schema does not declare can only
            // ever evaluate false, and the enforcement seam turns a false into the canonical 404 — so
            // on a real silo every purge answered "does not exist", by anybody, forever: the name stayed
            // held and the committed quota was never returned. Every purge test in the repository runs
            // against a doubled authorizer, which answers whatever its author believed, so the gap was
            // invisible until test/CyberCloud.Isolation drove one through this schema.
            //
            // ⚠ ON `resource` AND ON NOTHING ELSE, because nothing else is ever parked. A tenant, a
            // subscription and a resource group are deleted or they are not.
            //
            // ⚠ Rel(owner), WHICH IS WHAT MAKES IT REACHABLE BY THE RIGHT PARTY RATHER THAN BY THE
            // OBVIOUS ONE. docs/plan/08 § Soft delete re-parents a parked resource to its SUBSCRIPTION
            // and drops its direct role assignments, so `owner` here resolves through
            // From(parent, owner) to a subscription owner — "the people who can see a deleted resource
            // become the people who hold subscription-scoped rights, which is exactly who Azure gives
            // deletedVaults/read and purge/action to". The resource-group owner whose DELETE parked it
            // is no longer in that set, which is the separation that actually bites.
            //
            // ⚠ AND THE NEGATION IS THE ONLY SEPARATION FROM `delete` THIS SCHEMA CAN EXPRESS TODAY —
            // SAID PLAINLY BECAUSE IT IS LESS THAN docs/plan/08 DESCRIBES. That section wants "a role
            // can hold the first without the second", copying `deletedVaults/purge/action` sitting in
            // Key Vault Contributor's notActions. Here `delete` is already Rel(owner), so any purge
            // defined in terms of owner is held by everyone who can delete. What this does deliver is
            // a deny assignment that removes purge while leaving delete, which is `notActions` with
            // one row in it — RoleAssignmentViewTests.ADenyAssignmentRemovesPurgeAndLeavesDeleteAndNoGrantSeparatesThem,
            // which runs THIS schema and which nothing did until it was written.
            //
            // ⚠ A GRANTABLE `purger` RELATION WOULD NOT FIX IT, AND BOTH REASONS ARE CONCRETE RATHER
            // THAN "a role-assignment story that does not exist". First, nothing in this platform can
            // WRITE a role tuple: ITupleStoreGrain.WriteAsync is the store, IObjectRelationsGrain's
            // remarks forbid reaching past it, and the only grant above either is
            // IScopeRelationWriter.GrantOwnerAsync at scope creation — there is no PUT
            // /roleAssignments and nothing writes `contributor` or `reader` either. A `purger`
            // relation would be a relation nobody can be given. Second, and the deeper one: the
            // separation Azure achieves lives BETWEEN TWO ROLES, and here there is no role beneath
            // owner that can delete — `delete` is Rel(owner) while Azure's Contributor deletes. So the
            // question the owed item is really asking is whether `delete` should be Rel(contributor),
            // which is a widening of this platform's most destructive verb and not an addition.
            // docs/plan/07 § Azure RBAC carries both.
            .Permission(
                Permissions.Purge,
                Rel(Relations.Owner) & !Rel(Relations.Suspended)
            )
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
