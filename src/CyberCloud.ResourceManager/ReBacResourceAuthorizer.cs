using CyberCloud.Authorization.Contracts;
using Microsoft.Extensions.Logging;
using Orleans.Multitenant;
using System.Globalization;

namespace CyberCloud.ResourceManager;

/// <summary>
///     The enforcement seam. docs/plan/07 § The enforcement seam — <b>the one place in the request
///     path that calls the engine</b>.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/07 § The enforcement seam:
///         <code>
///         // CyberCloud.ResourceManager — before any provider is invoked
///         var check = await authz.CheckAsync(
///             ObjectRef.Resource(resourceId), permission, SubjectRef.From(caller), consistency);
///
///         if (!check.Allowed)
///             return Result.NotFound();          // ← 404, never 403
///         </code>
///     </para>
///     <para>
///         ⚠ <b>404, never 403, on a resource the caller cannot read.</b>
///         <i>
///             "A 403 confirms the resource exists, which is an enumeration oracle: a competitor can
///             discover a customer's resource names by probing. 403 is returned only when the caller
///             can <b>read</b> the object but not perform the <b>action</b> — which is a real and
///             useful distinction, and it means the response code itself is authorization output."
///         </i>
///         That is why this makes a <b>second</b> check on refusal and never on the happy path: the
///         second check is what decides which of the two answers is honest, and paying for it only
///         when refusing keeps the cost off every allowed request.
///     </para>
///     <para>
///         ⚠ <b>A create is checked against the parent resource group.</b> A resource that does not
///         exist has no ReBAC object to check, and checking a <see cref="Guid.Empty" /> object would
///         ask about a thing nobody holds a tuple on — which fails closed, which would make every
///         create impossible. docs/plan/08 § The write path, end to end says so directly:
///         <c>Check(resource | parent rg, "write", caller)</c>.
///     </para>
///     <para>
///         ⚠ <b>A schema failure is rendered as <c>404</c> and is <i>not</i> silently swallowed.</b>
///         docs/plan/07's remarks on <see cref="ErrorCode.SchemaInvalid" />: a check that names a
///         permission the schema does not define is a distinguishable failure, never a denial and
///         never an allow. The caller still gets 404 — telling them "your platform's schema is broken"
///         is an information leak and is not their problem — and the log line is what a dashboard
///         alerts on.
///     </para>
/// </remarks>
public sealed class ReBacResourceAuthorizer(IGrainFactory grains, ILogger<ReBacResourceAuthorizer> logger)
    : IResourceAuthorizer {
    /// <summary>The ReBAC object type of a resource. docs/plan/07 § The model.</summary>
    public const string ResourceObjectType = "resource";

    /// <summary>The ReBAC object type of a resource group — what a create is checked against.</summary>
    /// <remarks>
    ///     ⚠ <b><c>resourceGroup</c>, lower-camel, and the capital <c>G</c> is load-bearing.</b> This
    ///     read <c>resourcegroup</c> and the schema
    ///     (<c>CyberCloud.Authorization.ObjectTypes.ResourceGroup</c>) reads <c>resourceGroup</c>;
    ///     <c>AuthorizationSchema</c> looks a type up through a <c>FrozenDictionary</c> keyed
    ///     <c>StringComparer.Ordinal</c>, so the two never met. The consequence was not a subtle one:
    ///     <b>every create failed</b>. A resource that does not exist is checked against its parent
    ///     group, the evaluator answered <c>SchemaInvalid</c> for an unknown object type, and this
    ///     class correctly renders an unanswerable check as <c>404</c> — so a <c>PUT</c> to a fresh
    ///     name came back "does not exist" with the reason only in a log line.
    ///     <para>
    ///         It survived because every existing test of the write path substitutes a double for this
    ///         class, so nothing had ever driven a create through the real engine. The two constants
    ///         still cannot be shared: <c>ObjectTypes</c> lives in <c>CyberCloud.Authorization</c>, and
    ///         <c>CyberCloud.ResourceManager</c> references only its <c>.Contracts</c>. Moving the
    ///         vocabulary into <c>CyberCloud.Authorization.Contracts</c> would make the mismatch a
    ///         compile error instead of a spelling one, and is the real fix.
    ///     </para>
    /// </remarks>
    public const string ResourceGroupObjectType = "resourceGroup";

    /// <inheritdoc />
    public async Task<Result> AuthorizeAsync(
        ResourceId id,
        string actionPermission,
        string readPermission,
        CallerContext caller,
        bool fullyConsistent = false,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentException.ThrowIfNullOrWhiteSpace(actionPermission);
        ArgumentException.ThrowIfNullOrWhiteSpace(readPermission);

        var subject = SubjectRef.Create(caller.SubjectType, caller.SubjectId);
        if (subject.TryGetError(out var subjectError)) {
            logger.LogError(
                "The caller {Caller} is not a ReBAC subject: {Message}. Answering 404.",
                caller,
                subjectError.Message
            );

            return NotFound(id);
        }

        var consistency = fullyConsistent ? Consistency.FullyConsistent : Consistency.MinimizeLatency;
        var check = Check(id);

        var acted = await check.CheckAsync(actionPermission, subject.GetValueOrThrow(), consistency);
        if (acted.TryGetError(out var actError)) {
            // Not a denial and not an allow — the question was not answerable. See the remarks.
            logger.LogError(
                "Checking '{Permission}' on '{Path}' failed rather than answering: {Message}. "
                + "Answering 404.",
                actionPermission,
                id.Path,
                actError.Message
            );

            return NotFound(id);
        }

        if (acted.GetValueOrThrow().Allowed) {
            return Result.Success;
        }

        // ── The refusal is refused. Which answer is honest? ─────────────────────────────────────
        //
        // Only now do we ask whether the caller can read, because only now does the answer change
        // anything. If they can read, the resource's existence is already known to them and 403 is
        // both true and useful. If they cannot, 403 would be the enumeration oracle.
        if (string.Equals(actionPermission, readPermission, StringComparison.Ordinal)) {
            return NotFound(id);
        }

        var readable = await check.CheckAsync(readPermission, subject.GetValueOrThrow(), consistency);
        if (readable.IsFailure || !readable.GetValueOrThrow().Allowed) {
            return NotFound(id);
        }

        return Result.Failure(
            ErrorCode.AuthorizationFailed,
            $"'{caller}' can read '{id.Path}' but does not have '{actionPermission}' on it."
        );
    }

    /// <summary>
    ///     The check grain for a resource, or for its parent group when the resource does not exist.
    /// </summary>
    ICheckGrain Check(ResourceId id) {
        var tenant = grains.ForTenant(id.TenantId.ToString("D", CultureInfo.InvariantCulture));

        // ⚠ The group's ReBAC id is the grain key GrainKeys.ResourceGroup builds, not the bare name:
        // a group name is unique within its SUBSCRIPTION and not within the tenant
        // (docs/plan/06 § The hierarchy), so a bare name would merge the "prod" groups of every
        // subscription into one authorization object.
        var (type, objectId) = id.Id == Guid.Empty
            ? (ResourceGroupObjectType, GroupObjectId(id))
            : (ResourceObjectType, id.Id.ToString("N", CultureInfo.InvariantCulture));

        return tenant.GetGrain<ICheckGrain>(GrainKeys.CheckCache(type, objectId));
    }

    /// <summary>
    ///     A resource group's ReBAC object id: <c>{subscriptionId:N}-{name}</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ <c>-</c> rather than <c>/</c> because <c>RelationNaming</c> forbids a separator that would
    ///     change the grain key's segment count, and a subscription id in the fixed-width <c>N</c> form
    ///     followed by a DNS-1123 name is prefix-free — no two (subscription, group) pairs can be
    ///     re-cut into a different one.
    /// </remarks>
    internal static string GroupObjectId(ResourceId id) =>
        id.SubscriptionId.ToString("N", CultureInfo.InvariantCulture) + "-" + id.ResourceGroup;

    static Result NotFound(ResourceId id) =>
        Result.Failure(
            ErrorCode.ResourceNotFound,
            // ⚠ Identical to the message a genuinely absent resource gets. That identity is the
            // property — two different messages would be the oracle the status code closed.
            $"'{id.Path}' does not exist."
        );
}
