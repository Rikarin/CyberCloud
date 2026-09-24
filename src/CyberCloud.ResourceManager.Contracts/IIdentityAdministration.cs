namespace CyberCloud.ResourceManager.Contracts;

/// <summary>
///     Administering a tenant's directory — its members, its pending invitations, its registered
///     OAuth clients — and a person's own sign-in sessions. The API behind the portal's identity
///     pages, docs/plan/20 § The pages that are not generated, "Identity admin". Issue #41.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THE CHECK LIVES HERE, FOR <see cref="IInvitationManager" />'s REASON.</b> The
///         gateway dispatches every address under <c>/tenants/{t}/providers/CyberCloud.Identity/</c>
///         here and asks nothing itself. Every directory operation needs <c>assignRole</c> on the
///         tenant, checked fully consistent — the tenant owner's permission, and the one inviting
///         already needs: listing who is in the organisation, removing somebody and registering a
///         client that people will be asked to consent to are the same act of administration as
///         granting a role in it.
///     </para>
///     <para>
///         ⚠ <b>The sessions are the exception, and they need no role at all.</b> They are the
///         caller's own — the list is read off the caller's user and a revoke is refused for a
///         session that isn't theirs — so the only check is that the caller is a person in the
///         tenant the address names. A tenant owner can't list or end another member's sessions
///         here; suspending the member, which ends all of them, is the owner's tool.
///     </para>
///     <para>
///         The work after the check is <see cref="IIdentityDirectory" />'s, the seam this assembly
///         reaches identity through without referencing it — <see cref="IInvitationIssuer" />'s
///         arrangement, for its reason.
///     </para>
/// </remarks>
public interface IIdentityAdministration {
    /// <summary>The tenant's members — every user its directory holds, invited ones included.</summary>
    /// <param name="request">The tenant and the caller.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>
    ///     The members, oldest first; the canonical <see cref="ErrorCode.ResourceNotFound" /> for a
    ///     caller who cannot read the tenant, <see cref="ErrorCode.AuthorizationFailed" /> for one who
    ///     can and is not an owner.
    /// </returns>
    Task<Result<IReadOnlyList<MemberSnapshot>>> ListMembersAsync(
        IdentityAdministrationRequest request,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Removes a member: deprovisions the user, which ends every session and clears every
    ///     credential, and deletes every relation tuple that names them.
    /// </summary>
    /// <param name="request">The tenant and the caller.</param>
    /// <param name="userId">The member.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>
    ///     The member as it now stands; the check's refusals;
    ///     <see cref="ErrorCode.ResourceNotFound" /> for a user this tenant doesn't have;
    ///     <see cref="ErrorCode.Conflict" /> for the caller themselves.
    /// </returns>
    Task<Result<MemberSnapshot>> RemoveMemberAsync(
        IdentityAdministrationRequest request,
        Guid userId,
        CancellationToken cancellationToken = default
    );

    /// <summary>Every invitation the tenant has sent, with where each stands.</summary>
    /// <param name="request">The tenant and the caller.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task<Result<IReadOnlyList<InvitationSnapshot>>> ListInvitationsAsync(
        IdentityAdministrationRequest request,
        CancellationToken cancellationToken = default
    );

    /// <summary>Mails an invitation again under a new link; the old link stops working.</summary>
    /// <param name="request">The tenant and the caller.</param>
    /// <param name="invitationId">The invitation.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task<Result<InvitationSnapshot>> ResendInvitationAsync(
        IdentityAdministrationRequest request,
        Guid invitationId,
        CancellationToken cancellationToken = default
    );

    /// <summary>Withdraws an invitation: its link opens nothing from now on.</summary>
    /// <param name="request">The tenant and the caller.</param>
    /// <param name="invitationId">The invitation.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task<Result<InvitationSnapshot>> RevokeInvitationAsync(
        IdentityAdministrationRequest request,
        Guid invitationId,
        CancellationToken cancellationToken = default
    );

    /// <summary>The OAuth clients the tenant has registered.</summary>
    /// <param name="request">The tenant and the caller.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task<Result<IReadOnlyList<ApplicationSnapshot>>> ListApplicationsAsync(
        IdentityAdministrationRequest request,
        CancellationToken cancellationToken = default
    );

    /// <summary>One registered client.</summary>
    /// <param name="request">The tenant and the caller.</param>
    /// <param name="applicationId">The application.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task<Result<ApplicationSnapshot>> GetApplicationAsync(
        IdentityAdministrationRequest request,
        Guid applicationId,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Registers a client and, for a confidential one, issues its secret — returned here and
    ///     nowhere else, ever.
    /// </summary>
    /// <param name="request">The tenant and the caller.</param>
    /// <param name="draft">What the body asked for.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task<Result<ApplicationRegistered>> CreateApplicationAsync(
        IdentityAdministrationRequest request,
        ApplicationDraft draft,
        CancellationToken cancellationToken = default
    );

    /// <summary>Issues a confidential client a new secret; the old one stops working at once.</summary>
    /// <param name="request">The tenant and the caller.</param>
    /// <param name="applicationId">The application.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task<Result<ApplicationRegistered>> RotateApplicationSecretAsync(
        IdentityAdministrationRequest request,
        Guid applicationId,
        CancellationToken cancellationToken = default
    );

    /// <summary>Deletes a registration; its <c>client_id</c> is free again.</summary>
    /// <param name="request">The tenant and the caller.</param>
    /// <param name="applicationId">The application.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task<Result> DeleteApplicationAsync(
        IdentityAdministrationRequest request,
        Guid applicationId,
        CancellationToken cancellationToken = default
    );

    /// <summary>The caller's own live sessions — every browser and device signed in as them.</summary>
    /// <param name="request">The tenant and the caller.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>
    ///     The sessions, newest first; <see cref="ErrorCode.InvalidRequestBody" /> for a caller who
    ///     isn't a person — a service principal has no sessions to list.
    /// </returns>
    Task<Result<IReadOnlyList<SessionSnapshot>>> ListOwnSessionsAsync(
        IdentityAdministrationRequest request,
        CancellationToken cancellationToken = default
    );

    /// <summary>Signs one of the caller's own sessions out, and every refresh chain it opened.</summary>
    /// <param name="request">The tenant and the caller.</param>
    /// <param name="sessionId">The session.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>
    ///     Success, and the same for a session already ended;
    ///     <see cref="ErrorCode.ResourceNotFound" /> for a session that isn't the caller's — never a
    ///     different answer for one that exists and belongs to somebody else.
    /// </returns>
    Task<Result> RevokeOwnSessionAsync(
        IdentityAdministrationRequest request,
        Guid sessionId,
        CancellationToken cancellationToken = default
    );
}

/// <summary>What every <see cref="IIdentityAdministration" /> call is asked with.</summary>
public sealed record IdentityAdministrationRequest {
    /// <summary>The tenant the address parsed to — the token's, rebuilt by the gateway.</summary>
    public required Guid TenantId { get; init; }

    /// <summary>Who is asking.</summary>
    public required CallerContext Caller { get; init; }

    /// <summary>
    ///     The session the caller's token belongs to — its <c>sid</c> — or <see cref="Guid.Empty" />.
    ///     Only the session list reads it, to mark which one is "this one".
    /// </summary>
    public Guid CurrentSessionId { get; init; }
}

/// <summary>A member of the tenant, as the resource API answers one.</summary>
public sealed record MemberSnapshot {
    /// <summary>The user's id — the principal id a role is granted to.</summary>
    public required Guid UserId { get; init; }

    /// <summary>The address.</summary>
    public required string Email { get; init; }

    /// <summary>The name they chose; empty for one still invited.</summary>
    public required string DisplayName { get; init; }

    /// <summary><c>invited</c>, <c>active</c>, <c>suspended</c> or <c>deprovisioned</c>.</summary>
    public required string Status { get; init; }

    /// <summary>When the user was created — the sign-up, or the first invitation.</summary>
    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>A registration as the body of a create asks for it.</summary>
public sealed record ApplicationDraft {
    /// <summary>What the consent page calls it.</summary>
    public required string DisplayName { get; init; }

    /// <summary>The exact redirect URIs.</summary>
    public required IReadOnlyList<string> RedirectUris { get; init; }

    /// <summary>The scopes it may ask for.</summary>
    public required IReadOnlyList<string> Scopes { get; init; }

    /// <summary>A SPA or a native app, which holds no secret; otherwise a server, which does.</summary>
    public required bool IsPublicClient { get; init; }
}

/// <summary>A registered OAuth client, as the resource API answers one — never with its secret.</summary>
public sealed record ApplicationSnapshot {
    /// <summary>The application's id — its address.</summary>
    public required Guid ApplicationId { get; init; }

    /// <summary>The <c>client_id</c> it signs people in as.</summary>
    public required string ClientId { get; init; }

    /// <summary>What the consent page calls it.</summary>
    public required string DisplayName { get; init; }

    /// <summary>The exact redirect URIs.</summary>
    public required IReadOnlyList<string> RedirectUris { get; init; }

    /// <summary>The scopes it may ask for.</summary>
    public required IReadOnlyList<string> Scopes { get; init; }

    /// <summary>Whether it is a public client.</summary>
    public required bool IsPublicClient { get; init; }

    /// <summary>When it was registered.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the platform last issued its secret, or <see langword="null" />.</summary>
    public DateTimeOffset? ClientSecretIssuedAt { get; init; }
}

/// <summary>
///     A client just registered, or just given a new secret — the one answer that carries the
///     secret.
/// </summary>
public sealed record ApplicationRegistered {
    /// <summary>The registration.</summary>
    public required ApplicationSnapshot Application { get; init; }

    /// <summary>
    ///     The secret, for a confidential client; empty for a public one. ⚠ Shown once: the platform
    ///     keeps only its digest and no call returns it again.
    /// </summary>
    public required string ClientSecret { get; init; }
}

/// <summary>One of a person's sessions, as the resource API answers one.</summary>
public sealed record SessionSnapshot {
    /// <summary>The session's id — its address.</summary>
    public required Guid SessionId { get; init; }

    /// <summary>The client it was opened for — <c>cyc-portal</c>, <c>cyc-cli</c>, or a tenant client.</summary>
    public required string ClientId { get; init; }

    /// <summary>Something the person recognises, "Firefox on Windows".</summary>
    public required string DeviceLabel { get; init; }

    /// <summary>When it was signed in.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Its last refresh, or its sign-in if it has never refreshed.</summary>
    public required DateTimeOffset LastUsedAt { get; init; }

    /// <summary>How the person proved who they were, as <c>amr</c> values.</summary>
    public required IReadOnlyList<string> Methods { get; init; }

    /// <summary>Whether the request that listed it came in on this session's token.</summary>
    public bool IsCurrent { get; init; }
}

/// <summary>
///     The identity module's half of <see cref="IIdentityAdministration" />, behind a seam — every
///     call after the check. Issue #41.
/// </summary>
/// <remarks>
///     ⚠ Called only by <see cref="IIdentityAdministration" />, after the check. The gateway's
///     implementation, <c>GrainIdentityDirectory</c>, reaches the identity grains in the tenant;
///     a host with no directory refuses every call with a sentence naming this seam.
/// </remarks>
public interface IIdentityDirectory {
    /// <summary>The tenant's users, oldest first.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<Result<IReadOnlyList<MemberSnapshot>>> ListMembersAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>Deprovisions a user, which ends their sessions and clears their credentials.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="userId">The user.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<Result<MemberSnapshot>> DeprovisionMemberAsync(
        Guid tenantId,
        Guid userId,
        CancellationToken cancellationToken = default
    );

    /// <summary>The tenant's invitations, oldest first.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<Result<IReadOnlyList<InvitationSnapshot>>> ListInvitationsAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default
    );

    /// <summary>Resends an invitation under a new link.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="invitationId">The invitation.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<Result<InvitationSnapshot>> ResendInvitationAsync(
        Guid tenantId,
        Guid invitationId,
        CancellationToken cancellationToken = default
    );

    /// <summary>Revokes an invitation.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="invitationId">The invitation.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<Result<InvitationSnapshot>> RevokeInvitationAsync(
        Guid tenantId,
        Guid invitationId,
        CancellationToken cancellationToken = default
    );

    /// <summary>The tenant's registered clients, oldest first.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<Result<IReadOnlyList<ApplicationSnapshot>>> ListApplicationsAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default
    );

    /// <summary>One registered client.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="applicationId">The application.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<Result<ApplicationSnapshot>> GetApplicationAsync(
        Guid tenantId,
        Guid applicationId,
        CancellationToken cancellationToken = default
    );

    /// <summary>Registers a client and issues a confidential one its secret.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="draft">The registration.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<Result<ApplicationRegistered>> CreateApplicationAsync(
        Guid tenantId,
        ApplicationDraft draft,
        CancellationToken cancellationToken = default
    );

    /// <summary>Issues a confidential client a new secret.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="applicationId">The application.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<Result<ApplicationRegistered>> RotateApplicationSecretAsync(
        Guid tenantId,
        Guid applicationId,
        CancellationToken cancellationToken = default
    );

    /// <summary>Deletes a registration.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="applicationId">The application.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<Result> DeleteApplicationAsync(Guid tenantId, Guid applicationId, CancellationToken cancellationToken = default);

    /// <summary>A user's live sessions.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="userId">The user.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<Result<IReadOnlyList<SessionSnapshot>>> ListSessionsAsync(
        Guid tenantId,
        Guid userId,
        CancellationToken cancellationToken = default
    );

    /// <summary>Revokes one of a user's sessions.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="userId">The user the session must belong to.</param>
    /// <param name="sessionId">The session.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Success; <see cref="ErrorCode.ResourceNotFound" /> for a session that isn't the user's.</returns>
    Task<Result> RevokeSessionAsync(
        Guid tenantId,
        Guid userId,
        Guid sessionId,
        CancellationToken cancellationToken = default
    );
}
