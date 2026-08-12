using CyberCloud.Core;
using CyberCloud.Core.Resources;
using System.Collections.Immutable;

namespace CyberCloud.Tenancy.Contracts;

/// <summary>
///     The resource path index — <b>this is how uniqueness works without a unique constraint</b>
///     (docs/plan/04 § Grain taxonomy).
/// </summary>
/// <remarks>
///     <para>
///         <b>Kind</b> Index · <b>Tier</b> Durable · <b>Key</b>
///         <c>idx/path/{sha256(canonicalPath)[..16]}</c>, tenant-qualified (docs/plan/06 § Grain
///         keys). Build it with <c>GrainKeys.PathIndex</c>, which takes a <see cref="ResourceId" />
///         and not a string precisely so that the digest cannot be taken over <c>Path</c> instead of
///         <c>CanonicalPath</c>.
///     </para>
///     <para>
///         <b>Cardinality, because that is the review question.</b> docs/plan/04 § Grain taxonomy's
///         ⚠: "an index grain is a hot spot if the indexed value is not high-cardinality … An index
///         grain keyed by <i>resource type</i> would be a single activation serialising every create
///         in the platform. The review question for any new index grain is 'what is the cardinality
///         of the key', and if the answer is 'small', it is not an index grain." The key here is a
///         64-bit digest of the full canonical path — tenant, subscription, resource group, provider
///         namespace, type <i>and</i> name — so the cardinality is one activation per resource
///         address. Two resources of the same type in the same group are two activations.
///         <c>IndexGrainCardinalityTests</c> asserts exactly that, including the negative: nothing in
///         this assembly is keyed by anything as small as a type.
///     </para>
///     <para>
///         <b>The claim is durable and the lease is five minutes</b> — docs/plan/06 § Two-phase
///         create, step 1. Durable because a claim that vanished with the activation would let two
///         creates win; leased because a claim that never expired would burn the name when the silo
///         holding the create died.
///     </para>
/// </remarks>
[Alias("CyberCloud.Tenancy.IResourceIndexGrain")]
public interface IResourceIndexGrain : IGrainWithStringKey {
    /// <summary>
    ///     Step 1 of docs/plan/06 § Two-phase create: claims the name under a lease, or
    ///     <c>ResourceAlreadyExists</c> (the gateway's <c>409 Conflict</c>).
    /// </summary>
    /// <param name="address">The address being claimed. Hashed as <c>CanonicalPath</c>.</param>
    /// <param name="resourceId">The GUID to bind the address to.</param>
    /// <remarks>
    ///     <para>
    ///         <b>
    ///             Idempotent for the same <paramref name="resourceId" />, and that is what makes the
    ///             retried <c>PUT</c> a no-op.
    ///         </b>
    ///         docs/plan/06 § Two-phase create: a silo that dies
    ///         between step 3 and the <c>202</c> leaves a confirmed binding, and "the caller retries
    ///         the <c>PUT</c> — which is idempotent because <c>PUT</c> with the same body on an
    ///         existing resource is a no-op, which is exactly why the API is <c>PUT</c> and not
    ///         <c>POST</c>". Re-claiming an already-confirmed binding with the same GUID therefore
    ///         succeeds and changes nothing; with a <i>different</i> GUID it is a conflict.
    ///     </para>
    ///     <para>
    ///         An expired lease is treated as free, and the expiry is evaluated on read rather than
    ///         by a timer — a timer that has to fire for correctness is a timer whose silo can die.
    ///     </para>
    /// </remarks>
    Task<Result<IndexEntry>> TryClaimAsync(ResourceId address, Guid resourceId);

    /// <summary>
    ///     Step 3: converts the lease into a permanent binding.
    /// </summary>
    /// <param name="resourceId">The GUID claimed in step 1. A mismatch is a <c>Conflict</c>.</param>
    Task<Result<IndexEntry>> ConfirmAsync(Guid resourceId);

    /// <summary>
    ///     Delete, step 1: releases the binding so the name is <b>immediately</b> reusable —
    ///     docs/plan/06 § Two-phase create, "release the index first".
    /// </summary>
    /// <param name="resourceId">The bound GUID. A mismatch is a <c>Conflict</c>.</param>
    /// <remarks>
    ///     ⚠ <b>The child counts go with it.</b> See <see cref="ChildrenAsync" />: the counts belong to
    ///     the <i>resource</i> at this address, and once the name is free the next create at the same
    ///     address is a different resource with a different GUID. Carrying a count across that boundary
    ///     would make a brand-new resource undeletable over children it never had.
    /// </remarks>
    Task<Result> ReleaseAsync(Guid resourceId);

    /// <summary>
    ///     Records that a child of <paramref name="childType" /> now hangs off this address —
    ///     docs/plan/08 § Deleting a parent resource that has children.
    /// </summary>
    /// <param name="childType">The child's type, for example <c>…/accounts/buckets</c>.</param>
    /// <returns>The count for that type after the increment.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is why the counter lives on the index grain rather than in a grain of its
    ///         own.</b> docs/plan/08 asks for a counter "maintained transactionally where the index
    ///         claim and release already happen", and this <i>is</i> that place: one activation per
    ///         parent address, single-threaded, durable, and the same activation the parent's own
    ///         delete calls <see cref="ReleaseAsync" /> on. "Is the name free" and "does it still have
    ///         children" are therefore answered by one entity that cannot disagree with itself, which
    ///         is the property the eventually-consistent resource-graph projection could not offer.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Not idempotent — every call is <c>+1</c>.</b> A set of child ids would be, and
    ///         would cost the row size <see cref="ChildTypeCount" /> explains. The caller pairs each
    ///         increment with exactly one <see cref="RemoveChildAsync" />, and the resource manager's
    ///         write path calls this on the <i>create</i> branch only, once the index claim has been
    ///         confirmed.
    ///     </para>
    /// </remarks>
    Task<Result<int>> AddChildAsync(ResourceTypeName childType);

    /// <summary>
    ///     Records that a child of <paramref name="childType" /> is gone.
    /// </summary>
    /// <param name="childType">The child's type.</param>
    /// <returns>The count for that type after the decrement.</returns>
    /// <remarks>
    ///     ⚠ <b>Clamped at zero and idempotent there</b>, so a re-driven delete cannot push a count
    ///     negative and a decrement for a child that was never counted is not an error. That matters
    ///     because the caller is <c>OperationGrain</c>, which is re-driven from a reminder: the
    ///     decrement has to be safe to run twice for the same reason the ReBAC unlink beside it does.
    /// </remarks>
    Task<Result<int>> RemoveChildAsync(ResourceTypeName childType);

    /// <summary>
    ///     What still hangs off this address, by type. Empty when nothing does.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The gate one step before the lock check on the delete path reads this</b> —
    ///     docs/plan/08 § Deleting a parent resource that has children, <i>"a delete is refused while
    ///     the resource still has children — 409, not a cascade, and not a silent orphan"</i>. It is a
    ///     read of the same activation the release below would mutate, so there is no window between
    ///     "it had no children" and "its name was freed".
    /// </remarks>
    Task<Result<ImmutableArray<ChildTypeCount>>> ChildrenAsync();

    /// <summary>What the index currently holds. An expired lease reads as <c>Free</c>.</summary>
    Task<Result<IndexEntry>> GetAsync();

    /// <summary>Resolves the address to its GUID, or <c>ResourceNotFound</c>.</summary>
    /// <remarks>
    ///     ⚠ Only a <see cref="IndexEntryState.Confirmed" /> binding resolves. A claim under lease is
    ///     not yet a resource, and returning its GUID would let a caller address a resource that may
    ///     never exist.
    /// </remarks>
    Task<Result<Guid>> ResolveAsync();

    /// <summary>Drops this activation — see <c>ITenantGrain.DeactivateAsync</c>.</summary>
    Task DeactivateAsync();
}

/// <summary>
///     The <b>per-tenant</b> email index — "this email is not taken" as a grain whose single-threaded
///     activation <i>is</i> the mutex (docs/plan/04 § Grain taxonomy).
/// </summary>
/// <remarks>
///     <para>
///         <b>Kind</b> Index · <b>Tier</b> Durable · <b>Key</b>
///         <c>idx/email/{sha256(tenantId + normalizedEmail)[..16]}</c>, tenant-qualified
///         (docs/plan/06 § Grain keys). Build it with <c>GrainKeys.EmailIndex</c>.
///     </para>
///     <para>
///         ⚠ <b>Per tenant, not global.</b> docs/plan/06 § Grain keys corrects an earlier table that
///         specified <c>sha256(normalized)</c>: "that specifies a <b>global</b> email index —
///         precisely the thing docs/plan/11 § Sign-up says we do not have and do not want, because a
///         global index is a global hot spot on the sign-up path." The tenant id is in the digest as
///         well as in the tenant qualification, so the key stays correct even when read outside its
///         qualification.
///     </para>
///     <para>
///         <b>Cardinality:</b> one activation per (tenant, address). The sign-up path for a tenant
///         with a million users touches a million distinct grains, not one.
///     </para>
/// </remarks>
[Alias("CyberCloud.Tenancy.IEmailIndexGrain")]
public interface IEmailIndexGrain : IGrainWithStringKey {
    /// <summary>Claims the address under a lease, or <c>Conflict</c>.</summary>
    /// <param name="email">The address. Normalised with <c>GrainKeys.NormalizeEmail</c> before use.</param>
    /// <param name="userId">The user to bind it to.</param>
    /// <remarks>
    ///     ⚠ The address is re-normalised here rather than trusted, and the normalised form is what
    ///     is stored. <c>GrainKeys.NormalizeEmail</c>'s remarks explain why it folds only ASCII
    ///     letters: <c>ToLowerInvariant</c> collapses U+212A KELVIN SIGN onto <c>k</c>, which at
    ///     sign-up is one account silently claiming another's identity and is indistinguishable from
    ///     a legitimate duplicate.
    /// </remarks>
    Task<Result<IndexEntry>> TryClaimAsync(string email, Guid userId);

    /// <summary>Converts the lease into a permanent binding.</summary>
    /// <param name="userId">The user claimed. A mismatch is a <c>Conflict</c>.</param>
    Task<Result<IndexEntry>> ConfirmAsync(Guid userId);

    /// <summary>Releases the binding.</summary>
    /// <param name="userId">The bound user. A mismatch is a <c>Conflict</c>.</param>
    Task<Result> ReleaseAsync(Guid userId);

    /// <summary>What the index currently holds. An expired lease reads as <c>Free</c>.</summary>
    Task<Result<IndexEntry>> GetAsync();

    /// <summary>Resolves the address to its user, or <c>ResourceNotFound</c>.</summary>
    Task<Result<Guid>> ResolveAsync();

    /// <summary>Drops this activation — see <c>ITenantGrain.DeactivateAsync</c>.</summary>
    Task DeactivateAsync();
}
