using CyberCloud.Authorization.Contracts;
using Microsoft.Extensions.Logging;
using Orleans.Multitenant;
using System.Globalization;

namespace CyberCloud.ResourceManager;

/// <summary>
///     The enforcement seam for a scope. docs/plan/07 § The enforcement seam — the same seam
///     <see cref="ReBacResourceAuthorizer" /> is, asked about a tenant, a subscription or a resource
///     group instead of a resource.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>404, never 403, on a scope the caller cannot read</b>, with the second check made only
///         on refusal — identical to the resource seam and for the identical reason. A subscription id
///         is at least as enumerable as a resource name and leaks more, because it is the billing
///         boundary.
///     </para>
///     <para>
///         ⚠ <b>A create is checked against the PARENT scope, exactly as a resource create is checked
///         against its group.</b> A subscription that does not exist has no tuple on it, so a check
///         against it fails closed and would make every create impossible — which is the shape of the
///         <c>resourcegroup</c> casing bug this file's neighbour records. <see cref="ScopeId.Parent" />
///         is the address the caller has to hold <c>write</c> on, and
///         <c>ScopeManagerService</c> is what passes it.
///     </para>
///     <para>
///         ⚠ <b><see cref="AuthorizePlatformAsync" /> is the only check in the platform that names
///         <c>platform:root</c>, and it is the first caller of the relation
///         docs/plan/06 § Platform administration has described since it was written.</b> That section
///         gives the tuple as <c>platform:root#operator@user:X</c>;
///         <c>CyberCloudSchema</c> defines the type, the relation and
///         <c>Permissions.Administer</c> over it; nothing checked it. <c>PlatformObjectId</c> below is
///         the only place the id <c>root</c> is spelled, and <c>ScopeAuthorizationTests</c> drives a
///         real tuple through the real schema rather than pinning the string — a permission that
///         evaluates false is indistinguishable from one that denies, and the enforcement seam turns
///         both into the same answer.
///     </para>
/// </remarks>
public sealed class ReBacScopeAuthorizer(IGrainFactory grains, ILogger<ReBacScopeAuthorizer> logger)
    : IScopeAuthorizer {
    /// <summary>The ReBAC object type of a tenant.</summary>
    public const string TenantObjectType = ObjectTypes.Tenant;

    /// <summary>The ReBAC object type of a subscription.</summary>
    public const string SubscriptionObjectType = ObjectTypes.Subscription;

    /// <summary>The ReBAC object type of a resource group.</summary>
    public const string ResourceGroupObjectType = ObjectTypes.ResourceGroup;

    /// <summary>The ReBAC object type of the platform itself.</summary>
    public const string PlatformObjectType = ObjectTypes.Platform;

    /// <summary>
    ///     The platform singleton's object id — the <c>root</c> of
    ///     docs/plan/06 § Platform administration's <c>platform:root#operator@user:X</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>One spelling, in one place, because the alternative is the failure this repository has
    ///     already shipped twice.</b> An object id nothing else agrees with produces a check against
    ///     an object no tuple names, which evaluates false, which the seam renders as a refusal — with
    ///     nothing in any log to say the id was the problem. There is no second declaration of this
    ///     string anywhere; if one ever appears, it belongs here instead.
    /// </remarks>
    public const string PlatformObjectId = "root";

    /// <summary>
    ///     The tenant whose store holds the platform's own tuples: the platform tenant,
    ///     <see cref="Guid.Empty" />.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b><see cref="Guid.Empty" /> here is the PLATFORM TENANT and not the null tenant, and
    ///     docs/plan/06 § Platform administration spends a table on the difference.</b> The platform
    ///     tenant is "an ordinary tenant id that happens to be all zeroes", whose grains are
    ///     tenant-qualified like any other; the null tenant is the <i>absence</i> of qualification,
    ///     which <c>Orleans.Multitenant</c> spells as the literal string <c>"Null"</c>. A
    ///     <c>platform:root</c> tuple in the null tenant's store would be reachable only from grains
    ///     that are themselves unqualified, and the tuple store is not one.
    /// </remarks>
    public static Guid PlatformTenant => Guid.Empty;

    /// <inheritdoc />
    public async Task<Result> AuthorizeAsync(
        ScopeId scope,
        string actionPermission,
        string readPermission,
        CallerContext caller,
        bool fullyConsistent = false,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentException.ThrowIfNullOrWhiteSpace(actionPermission);
        ArgumentException.ThrowIfNullOrWhiteSpace(readPermission);

        if (scope.Kind == ScopeKind.Unknown) {
            return NotFound(scope);
        }

        var subject = SubjectRef.Create(caller.SubjectType, caller.SubjectId);
        if (subject.TryGetError(out var subjectError)) {
            logger.LogError(
                "The caller {Caller} is not a ReBAC subject: {Message}. Answering 404.",
                caller,
                subjectError.Message
            );

            return NotFound(scope);
        }

        var consistency = fullyConsistent ? Consistency.FullyConsistent : Consistency.MinimizeLatency;
        var check = Check(scope);

        var acted = await check.CheckAsync(actionPermission, subject.GetValueOrThrow(), consistency);
        if (acted.TryGetError(out var actError)) {
            // Not a denial and not an allow — the question was not answerable. The caller still gets
            // the canonical 404 and the log line is what a dashboard alerts on, exactly as
            // ReBacResourceAuthorizer does for ErrorCode.SchemaInvalid.
            logger.LogError(
                "Checking '{Permission}' on scope '{Path}' failed rather than answering: {Message}. "
                + "Answering 404.",
                actionPermission,
                scope.Path,
                actError.Message
            );

            return NotFound(scope);
        }

        if (acted.GetValueOrThrow().Allowed) {
            return Result.Success;
        }

        if (string.Equals(actionPermission, readPermission, StringComparison.Ordinal)) {
            return NotFound(scope);
        }

        var readable = await check.CheckAsync(readPermission, subject.GetValueOrThrow(), consistency);

        return readable.IsFailure || !readable.GetValueOrThrow().Allowed
            ? NotFound(scope)
            : Result.Failure(
                ErrorCode.AuthorizationFailed,
                $"'{caller}' can read '{scope.Path}' but does not have '{actionPermission}' on it."
            );
    }

    /// <inheritdoc />
    public async Task<Result> AuthorizePlatformAsync(
        string permission,
        CallerContext caller,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);

        var subject = SubjectRef.Create(caller.SubjectType, caller.SubjectId);
        if (subject.TryGetError(out var subjectError)) {
            logger.LogError(
                "The caller {Caller} is not a ReBAC subject: {Message}. Refusing the platform check.",
                caller,
                subjectError.Message
            );

            return Denied(permission, caller);
        }

        var check = grains
            .ForTenant(PlatformTenant.ToString("D", CultureInfo.InvariantCulture))
            .GetGrain<ICheckGrain>(GrainKeys.CheckCache(PlatformObjectType, PlatformObjectId));

        // ⚠ ALWAYS FullyConsistent. docs/plan/07 § Consistency wants the cache bypassed for "anything
        // where a stale allow is a real incident", and an operator whose grant was revoked five
        // seconds ago creating tenants out of a warm cache is the definition of one. This is asked
        // once per tenant ever created, so the cost is not a consideration.
        var allowed = await check.CheckAsync(permission, subject.GetValueOrThrow(), Consistency.FullyConsistent);

        if (allowed.TryGetError(out var checkError)) {
            logger.LogError(
                "Checking '{Permission}' on {Type}:{Id} failed rather than answering: {Message}. "
                + "Refusing.",
                permission,
                PlatformObjectType,
                PlatformObjectId,
                checkError.Message
            );

            return Denied(permission, caller);
        }

        return allowed.GetValueOrThrow().Allowed ? Result.Success : Denied(permission, caller);
    }

    /// <summary>
    ///     The ReBAC object a check about a scope is asked on.
    /// </summary>
    /// <param name="scope">The scope.</param>
    /// <remarks>
    ///     ⚠ <b>The two ids here must be the same strings
    ///     <see cref="ReBacResourceAuthorizer.GroupObjectId" /> and
    ///     <see cref="ReBacResourceAuthorizer.SubscriptionObjectId" /> build, and nothing in the
    ///     compiler says so</b> — they are computed from a <see cref="ScopeId" /> here and from a
    ///     <c>ResourceId</c> there. A disagreement would put a resource's <c>parent</c> edge on one
    ///     object and the group's own role assignments on another, so a group owner would be unable to
    ///     see the resources in their own group while every test of either half passed.
    ///     <c>ScopeObjectIdAgreementTests</c> is the assertion, and it compares the two functions
    ///     rather than pinning either one's output.
    /// </remarks>
    public static (string Type, string Id) ObjectOf(ScopeId scope) =>
        scope.Kind switch {
            ScopeKind.Tenant => (TenantObjectType, N(scope.TenantId)),
            ScopeKind.Subscription => (SubscriptionObjectType, N(scope.SubscriptionId)),
            ScopeKind.ResourceGroup => (ResourceGroupObjectType, N(scope.SubscriptionId) + "-" + scope.ResourceGroup),
            _ => ("", "")
        };

    ICheckGrain Check(ScopeId scope) {
        var (type, objectId) = ObjectOf(scope);

        return grains
            .ForTenant(scope.TenantId.ToString("D", CultureInfo.InvariantCulture))
            .GetGrain<ICheckGrain>(GrainKeys.CheckCache(type, objectId));
    }

    static string N(Guid id) => id.ToString("N", CultureInfo.InvariantCulture);

    static Result NotFound(ScopeId scope) =>
        Result.Failure(
            ErrorCode.ResourceNotFound,
            // ⚠ The same sentence a genuinely absent scope gets. The identity is the property.
            $"'{scope.Path}' does not exist."
        );

    static Result Denied(string permission, CallerContext caller) =>
        Result.Failure(
            ErrorCode.AuthorizationFailed,
            $"'{caller}' does not have '{permission}' on the platform. "
            + "docs/plan/06 § Platform administration: a platform operator holds "
            + "'platform:root#operator'."
        );
}
