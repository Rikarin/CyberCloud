using CyberCloud.Authorization.Contracts;
using Microsoft.Extensions.Logging;
using Orleans.Multitenant;
using System.Globalization;

namespace CyberCloud.ResourceManager;

/// <summary>
///     The write half of the scope authorization seam — the <c>parent</c> edge between scopes and the
///     one direct <c>#owner</c> tuple a tenant needs, over <see cref="ITupleStoreGrain" />.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THE EDGES THIS WRITES DID NOT EXIST, AND THEIR ABSENCE MADE docs/plan/07 § Azure
///         RBAC, expressed in it's FIRST ROW GRANT NOTHING ON A REAL SILO.</b> That table maps
///         <i>Owner on subscription S for user U</i> to <c>subscription:S#owner@user:U</c> and says
///         inheritance sub → rg → resource is <i>"the <c>From("parent", …)</c> rewrites"</i>.
///         <c>ReBacResourceRelationWriter</c> writes the last hop of that chain and nothing wrote the
///         other two: no <c>resourceGroup:{sub}-{rg}#parent@subscription:{s}</c> and no
///         <c>subscription:{s}#parent@tenant:{t}</c> were produced anywhere in the platform. So a
///         subscription owner held nothing on any group or resource beneath them, and the only reason
///         no test said so is that every harness writes the resource group's <c>owner</c> tuple
///         directly — <c>IsolationCluster.CreateAsync</c> still does. That is a check answering a
///         narrower question than it appears to, which is this engine's most-shipped defect.
///     </para>
///     <para>
///         ⚠ <b>Through <see cref="ITupleStoreGrain" /> and never through
///         <c>IObjectRelationsGrain</c> directly</b>, for the reason
///         <see cref="ReBacResourceRelationWriter" /> gives: a tuple written straight into the forward
///         index is one the reverse index never learns about and does not bump the tenant's relation
///         version, so no consistency token covers it and no check cache is invalidated.
///     </para>
///     <para>
///         ⚠ <b>Idempotent, because a scope create is idempotent.</b> <c>TupleStoreGrain</c> makes a
///         repeated write succeed, so a re-drive of the same <c>PUT</c> needs no "did I already?"
///         flag of its own.
///     </para>
/// </remarks>
public sealed class ReBacScopeRelationWriter(IGrainFactory grains, ILogger<ReBacScopeRelationWriter> logger)
    : IScopeRelationWriter {
    /// <summary>
    ///     The relation name of the scope one level up — docs/plan/07 § The model's <c>parent</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ Named rather than inlined for the reason
    ///     <see cref="ReBacResourceRelationWriter.ParentRelation" /> is: naming a <i>defined</i>
    ///     relation that the object type does not declare is written successfully against a relation
    ///     no rewrite follows, so every create reports success and every scope is invisible, with
    ///     nothing in any log. <c>ScopeCreationTests</c> reads this constant to assert it is one
    ///     <c>CyberCloudSchema</c> rewrites through on both scope types.
    /// </remarks>
    public const string ParentRelation = Relations.Parent;

    /// <summary>The relation a direct grant writes. docs/plan/07 § Azure RBAC, expressed in it.</summary>
    public const string OwnerRelation = Relations.Owner;

    /// <inheritdoc />
    public async Task<Result> LinkToParentAsync(ScopeId scope, CancellationToken cancellationToken = default) {
        if (scope.Parent is not { } parent) {
            // Not a domain outcome: the caller asked to give a tenant a parent. CyberCloudSchema
            // declares no `parent` relation on `tenant`, so the tuple would be written successfully
            // against a relation no rewrite follows — see IScopeManager.CreateTenantAsync on why a
            // tenant gets a direct owner instead.
            return Result.Failure(
                ErrorCode.InvalidResourceId,
                $"'{scope.Path}' is a tenant and a tenant has no parent scope. CyberCloudSchema "
                + "declares no 'parent' relation on 'tenant', so there is nothing for a "
                + "From('parent', …) rewrite to follow — a tenant is made visible by a direct "
                + "'#owner' tuple, which is IScopeRelationWriter.GrantOwnerAsync."
            );
        }

        var (parentType, parentId) = ReBacScopeAuthorizer.ObjectOf(parent);

        return await ApplyAsync(scope, ParentRelation, SubjectRef.Of(parentType, parentId));
    }

    /// <inheritdoc />
    public async Task<Result> GrantOwnerAsync(
        ScopeId scope,
        string subjectType,
        string subjectId,
        CancellationToken cancellationToken = default
    ) {
        var subject = SubjectRef.Create(subjectType, subjectId);

        if (subject.TryGetError(out var subjectError)) {
            logger.LogError(
                "The owner of '{Path}' is not a ReBAC subject: {Message}.",
                scope.Path,
                subjectError.Message
            );

            return Result.Failure(subjectError);
        }

        return await ApplyAsync(scope, OwnerRelation, subject.GetValueOrThrow());
    }

    async Task<Result> ApplyAsync(ScopeId scope, string relation, SubjectRef subject) {
        var (type, id) = ReBacScopeAuthorizer.ObjectOf(scope);

        if (type.Length == 0) {
            return Result.Failure(
                ErrorCode.InvalidResourceId,
                "A scope with no kind names no ReBAC object, so there is nothing to write a tuple on."
            );
        }

        // ⚠ Spelled out in full. `ObjectRef` is pinned to the Kubernetes one by this assembly's
        // GlobalUsings — see the comment there — and the ReBAC one is a different type entirely.
        var built = RelationTuple.Create(
            CyberCloud.Authorization.Contracts.ObjectRef.Of(type, id),
            relation,
            subject
        );

        if (built.TryGetError(out var invalid)) {
            logger.LogError(
                "The '{Relation}' tuple for scope {Path} is not a valid tuple: {Message}.",
                relation,
                scope.Path,
                invalid.Message
            );

            return Result.Failure(invalid);
        }

        var store = grains
            .ForTenant(scope.TenantId.ToString("D", CultureInfo.InvariantCulture))
            .GetGrain<ITupleStoreGrain>(GrainKeys.TupleStore(scope.TenantId));

        var written = await store.WriteAsync(built.GetValueOrThrow());

        if (written.TryGetError(out var failure)) {
            logger.LogError(
                "Writing the '{Relation}' edge of scope {Path} failed: {Message}.",
                relation,
                scope.Path,
                failure.Message
            );

            return Result.Failure(failure);
        }

        return Result.Success;
    }
}
