using CyberCloud.Authorization.Contracts;
using Microsoft.Extensions.Logging;
using Orleans.Multitenant;
using System.Globalization;

namespace CyberCloud.ResourceManager;

/// <summary>
///     The identity administration API — the check, then the directory.
///     <see cref="IIdentityAdministration" />'s remarks carry the argument for where the check is.
///     Issue #41.
/// </summary>
/// <remarks>
///     <para>
///         <b>The order, for every directory call:</b> the tenant in the address must be the
///         caller's (the gateway rebuilt it from the token, so a disagreement is a bug upstream and
///         is answered as absence); <c>assignRole</c> on the tenant, fully consistent, through
///         <see cref="IScopeAuthorizer" /> — the seam <see cref="InvitationService" /> uses, so a
///         suspended owner is refused by the tenant's own <c>#suspended</c> — then the directory.
///         The session calls skip the role check and ask instead that the caller be a person, whose
///         own user is the only one they read.
///     </para>
///     <para>
///         ⚠ <b>Removing a member deletes the tuples that name them, and does it after the
///         deprovision.</b> A deprovisioned user can't sign in, so a grant left on one is inert —
///         but it is still listed on every scope's access page as a role held by somebody who
///         isn't here, and <c>GrainPrincipalDirectory</c> refuses a new grant to them while the
///         old one stays. The reverse index (<see cref="ISubjectRelationsGrain" />) names every
///         object the user is a direct subject on — roles and group memberships alike — and each
///         is deleted through the tenant's tuple store, the one writer. After, not before: a
///         deprovision that failed would otherwise leave a live account stripped of its roles,
///         which is a lockout, where the other order's failure leaves a dead account holding roles
///         it can't use, which a repeated DELETE clears.
///     </para>
///     <para>
///         ⚠ <b>Nobody removes themselves.</b> An owner who removed their own user would lose the
///         session they did it from and, as the last owner, the tenant. The refusal is by id; a
///         second owner is how an owner leaves. The remover is an owner and stays one, so a tenant
///         can't lose its last owner this way — but a service principal holding <c>owner</c> can
///         remove the one person who does, and a last-person rule is owed, docs/plan/11 § Sign-up
///         and tenant creation.
///     </para>
///     <para>
///         ⚠ <b>Every grain reference goes through <c>ForTenant</c></b>, as in the services beside
///         it; held by the gateway, an Orleans client.
///     </para>
/// </remarks>
public sealed class IdentityAdministrationService(
    IScopeAuthorizer scopes,
    IIdentityDirectory directory,
    IGrainFactory grains,
    ILogger<IdentityAdministrationService> logger
) : IIdentityAdministration {
    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<MemberSnapshot>>> ListMembersAsync(
        IdentityAdministrationRequest request,
        CancellationToken cancellationToken = default
    ) {
        var owner = await OwnerAsync(request, cancellationToken);

        return owner.TryGetError(out var refused)
            ? Result<IReadOnlyList<MemberSnapshot>>.Failure(refused)
            : await directory.ListMembersAsync(request.TenantId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Result<MemberSnapshot>> RemoveMemberAsync(
        IdentityAdministrationRequest request,
        Guid userId,
        CancellationToken cancellationToken = default
    ) {
        var owner = await OwnerAsync(request, cancellationToken);

        if (owner.TryGetError(out var refused)) {
            return Result<MemberSnapshot>.Failure(refused);
        }

        if (IsCaller(request.Caller, userId)) {
            return Result<MemberSnapshot>.Failure(
                ErrorCode.Conflict,
                "You can't remove yourself from the organisation. Another owner can remove you."
            );
        }

        var removed = await directory.DeprovisionMemberAsync(request.TenantId, userId, cancellationToken);

        if (removed.TryGetError(out var unremoved)) {
            return Result<MemberSnapshot>.Failure(unremoved);
        }

        var cleared = await DeleteTuplesNamingAsync(request.TenantId, userId);

        if (cleared.TryGetError(out var uncleared)) {
            return Result<MemberSnapshot>.Failure(uncleared);
        }

        logger.LogInformation(
            "{Caller} removed user {UserId} from tenant {TenantId}; {Tuples} relation tuples naming them were deleted.",
            request.Caller,
            userId,
            request.TenantId,
            cleared.GetValueOrThrow()
        );

        return removed;
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<InvitationSnapshot>>> ListInvitationsAsync(
        IdentityAdministrationRequest request,
        CancellationToken cancellationToken = default
    ) {
        var owner = await OwnerAsync(request, cancellationToken);

        return owner.TryGetError(out var refused)
            ? Result<IReadOnlyList<InvitationSnapshot>>.Failure(refused)
            : await directory.ListInvitationsAsync(request.TenantId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Result<InvitationSnapshot>> ResendInvitationAsync(
        IdentityAdministrationRequest request,
        Guid invitationId,
        CancellationToken cancellationToken = default
    ) {
        var owner = await OwnerAsync(request, cancellationToken);

        if (owner.TryGetError(out var refused)) {
            return Result<InvitationSnapshot>.Failure(refused);
        }

        var resent = await directory.ResendInvitationAsync(request.TenantId, invitationId, cancellationToken);

        if (resent.IsSuccess) {
            logger.LogInformation(
                "{Caller} resent invitation {InvitationId} in tenant {TenantId}.",
                request.Caller,
                invitationId,
                request.TenantId
            );
        }

        return resent;
    }

    /// <inheritdoc />
    public async Task<Result<InvitationSnapshot>> RevokeInvitationAsync(
        IdentityAdministrationRequest request,
        Guid invitationId,
        CancellationToken cancellationToken = default
    ) {
        var owner = await OwnerAsync(request, cancellationToken);

        if (owner.TryGetError(out var refused)) {
            return Result<InvitationSnapshot>.Failure(refused);
        }

        var revoked = await directory.RevokeInvitationAsync(request.TenantId, invitationId, cancellationToken);

        if (revoked.IsSuccess) {
            logger.LogInformation(
                "{Caller} revoked invitation {InvitationId} in tenant {TenantId}.",
                request.Caller,
                invitationId,
                request.TenantId
            );
        }

        return revoked;
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<ApplicationSnapshot>>> ListApplicationsAsync(
        IdentityAdministrationRequest request,
        CancellationToken cancellationToken = default
    ) {
        var owner = await OwnerAsync(request, cancellationToken);

        return owner.TryGetError(out var refused)
            ? Result<IReadOnlyList<ApplicationSnapshot>>.Failure(refused)
            : await directory.ListApplicationsAsync(request.TenantId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Result<ApplicationSnapshot>> GetApplicationAsync(
        IdentityAdministrationRequest request,
        Guid applicationId,
        CancellationToken cancellationToken = default
    ) {
        var owner = await OwnerAsync(request, cancellationToken);

        return owner.TryGetError(out var refused)
            ? Result<ApplicationSnapshot>.Failure(refused)
            : await directory.GetApplicationAsync(request.TenantId, applicationId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Result<ApplicationRegistered>> CreateApplicationAsync(
        IdentityAdministrationRequest request,
        ApplicationDraft draft,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(draft);

        var owner = await OwnerAsync(request, cancellationToken);

        if (owner.TryGetError(out var refused)) {
            return Result<ApplicationRegistered>.Failure(refused);
        }

        var created = await directory.CreateApplicationAsync(request.TenantId, draft, cancellationToken);

        if (created.IsSuccess) {
            // ⚠ The id and the client id, never the secret — it is in the response and nowhere else.
            logger.LogInformation(
                "{Caller} registered application {ApplicationId} ({ClientId}) in tenant {TenantId}.",
                request.Caller,
                created.GetValueOrThrow().Application.ApplicationId,
                created.GetValueOrThrow().Application.ClientId,
                request.TenantId
            );
        }

        return created;
    }

    /// <inheritdoc />
    public async Task<Result<ApplicationRegistered>> RotateApplicationSecretAsync(
        IdentityAdministrationRequest request,
        Guid applicationId,
        CancellationToken cancellationToken = default
    ) {
        var owner = await OwnerAsync(request, cancellationToken);

        if (owner.TryGetError(out var refused)) {
            return Result<ApplicationRegistered>.Failure(refused);
        }

        var rotated = await directory.RotateApplicationSecretAsync(request.TenantId, applicationId, cancellationToken);

        if (rotated.IsSuccess) {
            logger.LogInformation(
                "{Caller} rotated the secret of application {ApplicationId} in tenant {TenantId}.",
                request.Caller,
                applicationId,
                request.TenantId
            );
        }

        return rotated;
    }

    /// <inheritdoc />
    public async Task<Result> DeleteApplicationAsync(
        IdentityAdministrationRequest request,
        Guid applicationId,
        CancellationToken cancellationToken = default
    ) {
        var owner = await OwnerAsync(request, cancellationToken);

        if (owner.TryGetError(out var refused)) {
            return Result.Failure(refused);
        }

        var deleted = await directory.DeleteApplicationAsync(request.TenantId, applicationId, cancellationToken);

        if (deleted.IsSuccess) {
            logger.LogInformation(
                "{Caller} deleted application {ApplicationId} in tenant {TenantId}.",
                request.Caller,
                applicationId,
                request.TenantId
            );
        }

        return deleted;
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<SessionSnapshot>>> ListOwnSessionsAsync(
        IdentityAdministrationRequest request,
        CancellationToken cancellationToken = default
    ) {
        var person = Person(request);

        if (person.TryGetError(out var refused)) {
            return Result<IReadOnlyList<SessionSnapshot>>.Failure(refused);
        }

        var listed = await directory.ListSessionsAsync(request.TenantId, person.GetValueOrThrow(), cancellationToken);

        if (listed.TryGetError(out var unlisted)) {
            return Result<IReadOnlyList<SessionSnapshot>>.Failure(unlisted);
        }

        return Result<IReadOnlyList<SessionSnapshot>>.Success(
            [
                .. listed.GetValueOrThrow()
                    .Select(x => x with { IsCurrent = x.SessionId == request.CurrentSessionId && x.SessionId != Guid.Empty })
                    .OrderByDescending(static x => x.CreatedAt)
            ]
        );
    }

    /// <inheritdoc />
    public async Task<Result> RevokeOwnSessionAsync(
        IdentityAdministrationRequest request,
        Guid sessionId,
        CancellationToken cancellationToken = default
    ) {
        var person = Person(request);

        if (person.TryGetError(out var refused)) {
            return Result.Failure(refused);
        }

        var revoked = await directory.RevokeSessionAsync(request.TenantId, person.GetValueOrThrow(), sessionId, cancellationToken);

        if (revoked.IsSuccess) {
            logger.LogInformation(
                "{Caller} signed out their session {SessionId}.",
                request.Caller,
                sessionId
            );
        }

        return revoked;
    }

    // ── Internals ──────────────────────────────────────────────────────────────────────────────

    /// <summary>The tenant match, then <c>assignRole</c> on the tenant, fully consistent.</summary>
    async Task<Result> OwnerAsync(IdentityAdministrationRequest request, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(request);

        var scope = ScopeId.Tenant(request.TenantId);

        if (request.Caller.TenantId != request.TenantId) {
            return Result.Failure(ErrorCode.ResourceNotFound, $"The resource '{scope.Path}' was not found.");
        }

        return await scopes.AuthorizeAsync(
            scope,
            Permissions.AssignRole,
            Permissions.Read,
            request.Caller,
            fullyConsistent: true,
            cancellationToken
        );
    }

    /// <summary>The caller's own user id, for a person in the address's tenant.</summary>
    static Result<Guid> Person(IdentityAdministrationRequest request) {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Caller.TenantId != request.TenantId) {
            return Result<Guid>.Failure(
                ErrorCode.ResourceNotFound,
                $"The resource '{ScopeId.Tenant(request.TenantId).Path}' was not found."
            );
        }

        return string.Equals(request.Caller.SubjectType, SubjectTypes.User, StringComparison.Ordinal)
            && Guid.TryParseExact(request.Caller.SubjectId, "N", out var userId)
                ? Result<Guid>.Success(userId)
                : Result<Guid>.Failure(
                    ErrorCode.InvalidRequestBody,
                    "Sessions belong to people. This caller is a "
                    + request.Caller.SubjectType
                    + ", which signs in with a credential and holds no session to list or end."
                );
    }

    static bool IsCaller(CallerContext caller, Guid userId) =>
        string.Equals(caller.SubjectType, SubjectTypes.User, StringComparison.Ordinal)
        && Guid.TryParseExact(caller.SubjectId, "N", out var id)
        && id == userId;

    /// <summary>
    ///     Deletes every tuple whose subject is <c>user:{userId}</c>, through the tenant's tuple
    ///     store — the type's remarks say why and when.
    /// </summary>
    /// <returns>How many were deleted.</returns>
    async Task<Result<int>> DeleteTuplesNamingAsync(Guid tenantId, Guid userId) {
        var tenant = grains.ForTenant(tenantId.ToString("D", CultureInfo.InvariantCulture));
        var id = userId.ToString("N", CultureInfo.InvariantCulture);

        var listed = await tenant
            .GetGrain<ISubjectRelationsGrain>(GrainKeys.SubjectRelations(SubjectTypes.User, id))
            .ListAsync();

        if (listed.TryGetError(out var unlisted)) {
            return Result<int>.Failure(unlisted);
        }

        var store = tenant.GetGrain<ITupleStoreGrain>(GrainKeys.TupleStore(tenantId));
        var deleted = 0;

        foreach (var entry in listed.GetValueOrThrow()) {
            var subject = SubjectRef.Create(
                SubjectTypes.User,
                id,
                entry.SubjectRelation.Length == 0 ? null : entry.SubjectRelation
            );

            if (subject.TryGetError(out var badSubject)) {
                return Result<int>.Failure(badSubject);
            }

            var tuple = RelationTuple.Create(entry.Object, entry.Relation, subject.GetValueOrThrow());

            if (tuple.TryGetError(out var badTuple)) {
                return Result<int>.Failure(badTuple);
            }

            var removed = await store.DeleteAsync(tuple.GetValueOrThrow());

            if (removed.TryGetError(out var unremoved)) {
                return Result<int>.Failure(unremoved);
            }

            deleted++;
        }

        return Result<int>.Success(deleted);
    }
}
