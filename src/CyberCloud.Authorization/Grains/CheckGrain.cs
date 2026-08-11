using System.Globalization;
using CyberCloud.Authorization.Contracts;
using CyberCloud.Authorization.Evaluation;
using CyberCloud.Core;
using CyberCloud.Core.Contracts;
using CyberCloud.Core.Resources;
using Orleans.Multitenant;

namespace CyberCloud.Authorization.Grains;

/// <summary>
///     <see cref="ICheckGrain" /> — Entity, <b>Hot</b>, key <c>rel/check/{type}/{id}</c>.
/// </summary>
/// <remarks>
///     <para>
///         <b>The cache, and the one decision docs/plan/07 leaves contradictory.</b> § Caching
///         across requests puts the tenant relation version <i>in the cache key</i>; § Consistency
///         says <c>MinimizeLatency</c> takes "any cached result". Those cannot both hold — see
///         <c>ConsistencyMode</c>'s remarks for the full argument. Here the version is a
///         <b>stamp on the entry</b> and the mode decides which stamps are acceptable:
///     </para>
///     <list type="table">
///         <item>
///             <term><c>MinimizeLatency</c></term>
///             <description>
///                 any stamp, and <b>the tenant version is not even read</b> — which is what makes
///                 it the fast mode, and what makes the revoke-then-stale-read bug real.
///             </description>
///         </item>
///         <item>
///             <term><c>AtLeastAsFresh(token)</c></term>
///             <description>
///                 a stamp at or after the token, otherwise a walk. On a walk it also drops every
///                 entry stamped before the current version — docs/plan/07's "crude and right"
///                 whole-tenant invalidation, applied at the granularity a grain can apply it.
///             </description>
///         </item>
///         <item>
///             <term><c>FullyConsistent</c></term>
///             <description>
///                 never reads the cache, and the walk re-reads every object's durable row.
///             </description>
///         </item>
///     </list>
///     <para>
///         ⚠ <b>A truncated result is never cached.</b> See <c>CheckEvaluation.IsCacheable</c>: a
///         walk that hit a cap did not compute an answer, and writing "I gave up" into a cache makes
///         one unlucky walk permanent.
///     </para>
///     <para>
///         ⚠ <b>There is no TTL, deliberately.</b> docs/plan/07 § Consistency says "any cached
///         result" and names no expiry. Adding one would soften the bug class that section exists to
///         describe into something that fixes itself in five minutes, which is exactly how a stale
///         permission becomes untraceable. The bound on staleness is the caller's choice of mode,
///         not a clock.
///     </para>
/// </remarks>
public sealed class CheckGrain(
    [PersistentState("check", StorageTiers.Hot)] IPersistentState<CheckCacheState> cache,
    AuthorizationSchema schema,
    AuthorizationLimits limits,
    IMembershipIndex membershipIndex)
    : Grain, ICheckGrain
{
    Guid tenantId;
    ObjectRef self = new();

    /// <inheritdoc />
    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        tenantId = AuthorizationGrainKeys.TenantOf(this);
        self = AuthorizationGrainKeys.DecodeObject(this, GrainKeyKind.CheckCache);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<Result<CheckResult>> CheckAsync(
        string permission, SubjectRef subject, Consistency? consistency)
    {
        if (subject is null || !subject.IsValid)
        {
            return Result<CheckResult>.Failure(
                ErrorCode.InvalidRequestBody,
                $"'{subject}' is not a well-formed subject. It is 'type:id' or "
                + "'type:id#relation' — docs/plan/07 § The model.");
        }

        var mode = consistency ?? Consistency.MinimizeLatency;

        if (mode.Mode == ConsistencyMode.AtLeastAsFresh && mode.Token is null)
        {
            return Result<CheckResult>.Failure(
                ErrorCode.InvalidRequestBody,
                "AtLeastAsFresh needs the token the write returned. Without one there is nothing to "
                + "be at least as fresh as, and silently downgrading to MinimizeLatency is exactly "
                + "the revoke-then-stale-read bug docs/plan/07 § Consistency exists to prevent.");
        }

        if (mode.Token is not null && mode.Token.TenantId != tenantId)
        {
            return Result<CheckResult>.Failure(
                ErrorCode.AuthorizationFailed,
                $"The token names tenant {mode.Token.TenantId:D} and this object is in tenant "
                + $"{tenantId:D}. A token is a per-tenant version (docs/plan/07 § Consistency) and "
                + "comparing one tenant's against another's would make freshness meaningless.");
        }

        var key = CacheKey(permission, subject);

        if (mode.Mode != ConsistencyMode.FullyConsistent
            && cache.State.Entries.TryGetValue(key, out var cached)
            && cached.SchemaVersion == schema.Version
            && (mode.Mode == ConsistencyMode.MinimizeLatency
                || cached.Version >= mode.Token!.Version))
        {
            AuthorizationMetrics.RecordCacheHit();

            return Result<CheckResult>.Success(new CheckResult
            {
                Allowed = cached.Allowed,
                Outcome = cached.Allowed ? CheckOutcome.Allowed : CheckOutcome.Denied,
                Token = new ConsistencyToken { TenantId = tenantId, Version = cached.Version },
                FromCache = true,
            });
        }

        var token = await StoreGrain().GetTokenAsync();
        if (token.TryGetError(out var tokenError))
        {
            return Result<CheckResult>.Failure(tokenError);
        }

        var current = token.GetValueOrThrow();

        var evaluator = new CheckEvaluator(
            schema,
            new GrainRelationReader(
                GrainFactory, tenantId, forceDurable: mode.Mode == ConsistencyMode.FullyConsistent),
            limits,
            membershipIndex);

        var evaluated = await evaluator.EvaluateAsync(
            self, permission, subject, CancellationToken.None);

        if (evaluated.TryGetError(out var error))
        {
            return Result<CheckResult>.Failure(error);
        }

        var evaluation = evaluated.GetValueOrThrow();
        var dirty = DropEntriesOlderThan(current.Version);

        if (evaluation.IsCacheable)
        {
            cache.State.Entries[key] = new CheckCacheEntry
            {
                Allowed = evaluation.Allowed,
                Version = current.Version,
                SchemaVersion = schema.Version,
            };

            dirty = true;
        }

        if (dirty)
        {
            await cache.WriteStateAsync();
        }

        return Result<CheckResult>.Success(new CheckResult
        {
            Allowed = evaluation.Allowed,
            Outcome = evaluation.Outcome,
            Token = current,
            FromCache = false,
            TriplesVisited = evaluation.TriplesVisited,
            MaxDepthReached = evaluation.MaxDepthReached,
            CapDetail = evaluation.CapDetail,
        });
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<RoleAssignment>>> ListRoleAssignmentsAsync(
        bool includeInherited)
    {
        var type = schema.Type(self.Type);
        if (type is null)
        {
            return Result<IReadOnlyList<RoleAssignment>>.Failure(
                ErrorCode.SchemaInvalid,
                $"'{self.Type}' is not an object type in schema version "
                + schema.Version.ToString(CultureInfo.InvariantCulture) + ".");
        }

        var roles = type.Roles;
        var direct = await ObjectGrain(self).ListRoleAssignmentsAsync([.. roles]);
        if (direct.TryGetError(out var error))
        {
            return Result<IReadOnlyList<RoleAssignment>>.Failure(error);
        }

        List<RoleAssignment> assignments = [.. direct.GetValueOrThrow()];

        if (includeInherited)
        {
            var inherited = await WalkAncestorsAsync(roles);
            if (inherited.TryGetError(out var walkError))
            {
                return Result<IReadOnlyList<RoleAssignment>>.Failure(walkError);
            }

            assignments.AddRange(inherited.GetValueOrThrow());
        }

        return Result<IReadOnlyList<RoleAssignment>>.Success(assignments);
    }

    /// <inheritdoc />
    public Task<Result<int>> CachedEntryCountAsync() =>
        Task.FromResult(Result<int>.Success(cache.State.Entries.Count));

    /// <inheritdoc />
    public Task DeactivateAsync()
    {
        this.DeactivateOnIdle();
        return Task.CompletedTask;
    }

    /// <summary>
    ///     The inherited half of the Azure view: walk <c>parent</c> upwards and report each
    ///     ancestor's role assignments as visible here.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Every assignment this returns has no tuple at this scope</b>, which is docs/plan/07
    ///     § Azure RBAC's third row — "Inheritance sub → rg → resource | The <c>From("parent", …)</c>
    ///     rewrites; no tuples written". A role is reported only when this type's own rewrite for
    ///     that role actually inherits it, so the view cannot claim an inheritance the evaluator
    ///     would not honour.
    /// </remarks>
    async Task<Result<IReadOnlyList<RoleAssignment>>> WalkAncestorsAsync(
        IReadOnlyList<string> roles)
    {
        List<RoleAssignment> inherited = [];
        var current = self;
        var currentType = schema.Type(self.Type);

        for (var depth = 0; depth < limits.MaxDepth && currentType is not null; depth++)
        {
            var snapshot = await ObjectGrain(current).ReadAsync();
            if (snapshot.TryGetError(out var error))
            {
                return Result<IReadOnlyList<RoleAssignment>>.Failure(error);
            }

            var parents = snapshot.GetValueOrThrow().Subjects(Relations.Parent);
            if (parents.Count == 0)
            {
                break;
            }

            // The scope chain is a chain: one parent. A second is a data error, and taking the
            // first is the fail-closed reading — a view that merged two chains would show a grant
            // the evaluator reaches only through one of them.
            var parent = parents[0].Object;
            var parentType = schema.Type(parent.Type);
            if (parentType is null)
            {
                break;
            }

            var inheritable = roles
                .Where(role => InheritsThroughParent(currentType, role))
                .Where(role => parentType.Roles.Contains(role, StringComparer.Ordinal))
                .ToList();

            if (inheritable.Count > 0)
            {
                var assignments = await ObjectGrain(parent).ListRoleAssignmentsAsync(inheritable);
                if (assignments.TryGetError(out var listError))
                {
                    return Result<IReadOnlyList<RoleAssignment>>.Failure(listError);
                }

                inherited.AddRange(assignments.GetValueOrThrow().Select(x => x with
                {
                    Scope = self,
                    Inherited = true,
                    InheritedFrom = parent,
                }));
            }

            current = parent;
            currentType = parentType;
            roles = inheritable;

            if (roles.Count == 0)
            {
                break;
            }
        }

        return Result<IReadOnlyList<RoleAssignment>>.Success(inherited);
    }

    static bool InheritsThroughParent(SchemaType type, string role) =>
        type.Member(role) is { } member
        && member.Expression.DescendantsAndSelf().Any(node =>
            node is TuplesetExpression tupleset
            && string.Equals(tupleset.Tupleset, Relations.Parent, StringComparison.Ordinal)
            && string.Equals(tupleset.Computed, role, StringComparison.Ordinal));

    /// <summary>
    ///     The two components of docs/plan/07 § Caching across requests' cache key that are not
    ///     this grain's identity. The separator is a character <c>RelationNaming</c> excludes from
    ///     every name, so no (permission, subject) pair can be re-cut into a different one.
    /// </summary>
    static string CacheKey(string permission, SubjectRef subject) =>
        permission + "\u0001" + subject;

    bool DropEntriesOlderThan(long version)
    {
        // docs/plan/07 § Caching across requests: "a write invalidates the tenant's whole check
        // cache. That is crude and it is right." Crude, at the granularity one grain can be crude
        // at: everything this object has cached under an older version goes.
        var stale = cache.State.Entries
            .Where(x => x.Value.Version < version || x.Value.SchemaVersion != schema.Version)
            .Select(x => x.Key)
            .ToList();

        foreach (var key in stale)
        {
            cache.State.Entries.Remove(key);
        }

        return stale.Count > 0;
    }

    ITupleStoreGrain StoreGrain() =>
        GrainFactory.ForTenant(tenantId.ToString("D", CultureInfo.InvariantCulture))
            .GetGrain<ITupleStoreGrain>(GrainKeys.TupleStore(tenantId));

    IObjectRelationsGrain ObjectGrain(ObjectRef @object) =>
        GrainFactory.ForTenant(tenantId.ToString("D", CultureInfo.InvariantCulture))
            .GetGrain<IObjectRelationsGrain>(
                GrainKeys.ObjectRelations(@object.Type, @object.Id));
}
