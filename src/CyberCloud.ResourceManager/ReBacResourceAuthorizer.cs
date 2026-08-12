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
    public const string ResourceObjectType = ObjectTypes.Resource;

    /// <summary>The ReBAC object type of a resource group — what a create is checked against.</summary>
    /// <remarks>
    ///     ⚠ <b>These two used to be their own literals, and one of them was wrong.</b> The vocabulary
    ///     lived in <c>CyberCloud.Authorization</c> and this assembly references only its
    ///     <c>.Contracts</c>, so the strings could not be shared and this one read
    ///     <c>resourcegroup</c> where the schema says <c>resourceGroup</c>.
    ///     <c>AuthorizationSchema</c> looks a type up through a <c>FrozenDictionary</c> keyed
    ///     <c>StringComparer.Ordinal</c>, so the two never met, and the consequence was not subtle:
    ///     <b>every create in the platform failed</b>. A resource that does not exist is checked
    ///     against its parent group, the evaluator answered <see cref="ErrorCode.SchemaInvalid" /> for
    ///     an unknown object type, and this class correctly renders an unanswerable check as
    ///     <c>404</c> — so a <c>PUT</c> to a fresh name came back "does not exist" with the reason only
    ///     in a log line. It survived because every test of the write path substituted a double for
    ///     this class, so nothing had ever driven a create through the real engine.
    ///     <para>
    ///         The vocabulary now lives in <c>CyberCloud.Authorization.Contracts</c>, which this
    ///         assembly already referenced, so both of these <b>are</b> the schema's constants rather
    ///         than copies that have to agree with them — a misspelling is <c>CS0117</c> and the
    ///         isolation suite no longer asserts the strings match, because they cannot differ. They
    ///         stay named here because they are what this seam <i>uses</i>, which is a fact a test can
    ///         read and the schema alone does not fix: see
    ///         <see cref="CheckedObject" /> and <c>ReBacResourceRelationWriter.ParentRelation</c>.
    ///     </para>
    /// </remarks>
    public const string ResourceGroupObjectType = ObjectTypes.ResourceGroup;

    /// <summary>
    ///     The ReBAC object type of a subscription — what a soft-deleted resource hangs off.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Nothing wrote a <c>subscription:</c> tuple before soft delete, and that is why the id
    ///     form is named here rather than spelled at the call site.</b> docs/plan/08 § Soft delete moves
    ///     a deleted resource's parent edge to <c>#parent@subscription:{sub}</c> so that
    ///     <i>"the people who can see a deleted resource become the people who hold subscription-scoped
    ///     rights, which is exactly who Azure gives <c>deletedVaults/read</c> and <c>purge/action</c>
    ///     to"</i>. <c>CyberCloudSchema</c> already defines the type with the same
    ///     <c>owner</c>/<c>contributor</c>/<c>reader</c> rewrites a resource group has, so the walk
    ///     composes with no schema change and no <c>SchemaVersion</c> bump.
    /// </remarks>
    public const string SubscriptionObjectType = ObjectTypes.Subscription;

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
        var (type, objectId) = CheckedObject(id);

        return tenant.GetGrain<ICheckGrain>(GrainKeys.CheckCache(type, objectId));
    }

    /// <summary>
    ///     The ReBAC object a check about <paramref name="id" /> is asked on: the resource itself, or
    ///     its parent group when the resource does not exist yet.
    /// </summary>
    /// <param name="id">The address being authorized. A <see cref="Guid.Empty" /> id means a create.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The branch is the whole of it, and it is what
    ///         <c>OracleTests.TheObjectACheckIsAskedOnIsTheGroupForACreateAndTheResourceOtherwise</c>
    ///         pins.</b> A create has no ReBAC object of its own, so it is checked against the group
    ///         one level up — docs/plan/08 § The write path, end to end:
    ///         <c>Check(resource | parent rg, "write", caller)</c>. Collapsing the two arms into one
    ///         would either check a create against an object nobody holds a tuple on, which fails
    ///         closed and makes every create impossible, or check an existing resource against its
    ///         group, which grants on a resource whose own <c>#suspended</c> says otherwise.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The group's ReBAC id is the one <see cref="GroupObjectId" /> builds, not the bare
    ///         name.</b> A group name is unique within its <b>subscription</b> and not within the
    ///         tenant (docs/plan/06 § The hierarchy), so a bare name would merge the <c>prod</c> groups
    ///         of every subscription into one authorization object.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Which type is no longer a spelling question, so it is the only question left.</b>
    ///         <see cref="ResourceGroupObjectType" />'s remarks carry the casing bug that made both
    ///         names the schema's own constants; a name the schema does not define is now
    ///         <c>CS0117</c>. Naming the <i>wrong</i> defined type still compiles, and this branch is
    ///         where that mistake would live.
    ///     </para>
    /// </remarks>
    public static (string Type, string Id) CheckedObject(ResourceId id) =>
        id.Id == Guid.Empty
            ? (ResourceGroupObjectType, GroupObjectId(id))
            : (ResourceObjectType, id.Id.ToString("N", CultureInfo.InvariantCulture));

    /// <summary>
    ///     A resource group's ReBAC object id: <c>{subscriptionId:N}-{name}</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ <c>-</c> rather than <c>/</c> because <c>RelationNaming</c> forbids a separator that would
    ///     change the grain key's segment count, and a subscription id in the fixed-width <c>N</c> form
    ///     followed by a DNS-1123 name is prefix-free — no two (subscription, group) pairs can be
    ///     re-cut into a different one.
    /// </remarks>
    public static string GroupObjectId(ResourceId id) =>
        id.SubscriptionId.ToString("N", CultureInfo.InvariantCulture) + "-" + id.ResourceGroup;

    /// <summary>A subscription's ReBAC object id: the subscription GUID in the <c>N</c> form.</summary>
    /// <remarks>
    ///     ⚠ <b>The bare GUID, with no tenant prefix, and unlike <see cref="GroupObjectId" /> it needs
    ///     none.</b> A resource group's name is unique only within its subscription, which is why that
    ///     id is a pair; a subscription id is a GUID and is unique on its own. The tenant is already in
    ///     the grain key — every store this reaches is resolved through <c>ForTenant</c> — so putting it
    ///     in the object id as well would be a second tenant boundary that can disagree with the first.
    ///     <para>
    ///         ⚠ <b>The <c>N</c> form, matching <see cref="CheckedObject" />'s resource id.</b>
    ///         <c>RelationNaming.IsId</c> accepts both forms, so a <c>D</c> here would be legal and
    ///         would silently make <c>subscription:{guid:D}</c> a different object from the one anything
    ///         else writes — one of those permanent, invisible mismatches the resource-group casing bug
    ///         above is the cautionary tale for.
    ///     </para>
    /// </remarks>
    public static string SubscriptionObjectId(ResourceId id) =>
        id.SubscriptionId.ToString("N", CultureInfo.InvariantCulture);

    static Result NotFound(ResourceId id) =>
        Result.Failure(
            ErrorCode.ResourceNotFound,
            // ⚠ Identical to the message a genuinely absent resource gets. That identity is the
            // property — two different messages would be the oracle the status code closed.
            $"'{id.Path}' does not exist."
        );
}
