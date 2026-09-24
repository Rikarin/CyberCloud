namespace CyberCloud.ResourceManager.Contracts;

/// <summary>
///     Inviting a colleague into a tenant — the first half of the M1 exit story's <i>"invite a
///     colleague and grant them Reader on one resource group"</i>, whose second half is
///     <see cref="IRoleAssignmentManager" />. Issue #43, step 7.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>BESIDE <see cref="IRoleAssignmentManager" />, AND FOR ITS REASON: THE CHECK LIVES
///         HERE.</b> docs/plan/10 § Request pipeline keeps exactly one enforcement seam, and it is
///         this module's; the gateway dispatches <c>POST /tenants/{t}/providers/CyberCloud.Identity/invitations</c>
///         here and asks nothing itself. The permission is <c>assignRole</c> on the tenant, checked
///         fully consistent: inviting somebody into the organisation is the same act of
///         administration as granting a role in it, an owner who lost ownership a second ago must
///         not be able to add members out of a warm cache, and a contributor who could invite would
///         be a contributor who could add accounts they control.
///     </para>
///     <para>
///         ⚠ <b>An invitation grants no role.</b> The invitee becomes a member — a user in the
///         tenant's directory, which <see cref="IPrincipalDirectory" /> then answers for — and a
///         grant is the separate <see cref="IRoleAssignmentManager.AssignAsync" /> that follows.
///     </para>
///     <para>
///         The work after the check is <see cref="IInvitationIssuer" />'s, the seam this assembly
///         reaches identity through without referencing it — <see cref="IPrincipalDirectory" />'s
///         arrangement, for its reason.
///     </para>
/// </remarks>
public interface IInvitationManager {
    /// <summary>
    ///     Invites an address into the caller's tenant. <c>POST</c> on the invitations address.
    /// </summary>
    /// <param name="request">The request, as the gateway parsed it.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>
    ///     The invitation; the canonical <see cref="ErrorCode.ResourceNotFound" /> for a caller who
    ///     cannot read the tenant, <see cref="ErrorCode.AuthorizationFailed" /> for one who can and
    ///     is not an owner, and the issuer's refusals — <see cref="ErrorCode.Conflict" /> for an
    ///     address that is a member already.
    /// </returns>
    Task<Result<InvitationSnapshot>> InviteAsync(
        InvitationManagerRequest request,
        CancellationToken cancellationToken = default
    );
}

/// <summary>What <see cref="IInvitationManager.InviteAsync" /> is asked.</summary>
public sealed record InvitationManagerRequest {
    /// <summary>The tenant the address parsed to — the token's, rebuilt by the gateway.</summary>
    public required Guid TenantId { get; init; }

    /// <summary>The address to invite, as the body carried it.</summary>
    public required string Email { get; init; }

    /// <summary>Who is inviting.</summary>
    public required CallerContext Caller { get; init; }
}

/// <summary>What the issuer is asked to do once the check passed.</summary>
public sealed record InvitationIssue {
    /// <summary>The tenant.</summary>
    public required Guid TenantId { get; init; }

    /// <summary>The tenant's name for the mail — its slug.</summary>
    public required string TenantName { get; init; }

    /// <summary>The address.</summary>
    public required string Email { get; init; }

    /// <summary>The inviter's subject id, as a GUID.</summary>
    public required Guid InvitedBy { get; init; }
}

/// <summary>An invitation, as the resource API answers it.</summary>
public sealed record InvitationSnapshot {
    /// <summary>The invitation's id.</summary>
    public required Guid InvitationId { get; init; }

    /// <summary>The tenant.</summary>
    public required Guid TenantId { get; init; }

    /// <summary>The user created for the invitee — the principal id a role is granted to.</summary>
    public required Guid UserId { get; init; }

    /// <summary>The address, normalized.</summary>
    public required string Email { get; init; }

    /// <summary><c>pending</c>, <c>accepted</c>, <c>expired</c>, <c>withdrawn</c> or <c>revoked</c>.</summary>
    public required string Status { get; init; }

    /// <summary>When the link stops working.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>Who sent it. Issue #41's listing; <see cref="Guid.Empty" /> where nobody asked.</summary>
    public Guid InvitedBy { get; init; }

    /// <summary>When the last mail went. Issue #41.</summary>
    public DateTimeOffset SentAt { get; init; }

    /// <summary>How many mails have gone, the first included. Issue #41.</summary>
    public int Sendings { get; init; }
}

/// <summary>
///     Creates an invitation and mails it — the identity module's half, behind a seam.
/// </summary>
/// <remarks>
///     ⚠ Called only by <see cref="IInvitationManager" />, after the check. The gateway's
///     implementation, <c>GrainInvitationIssuer</c>, reaches <c>IInvitationGrain</c>; a host with no
///     issuer refuses every invitation with a sentence naming this seam.
/// </remarks>
public interface IInvitationIssuer {
    /// <summary>Creates and mails one invitation.</summary>
    /// <param name="issue">What to create.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<Result<InvitationSnapshot>> IssueAsync(InvitationIssue issue, CancellationToken cancellationToken = default);
}
