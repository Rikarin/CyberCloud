using CyberCloud.Identity.Contracts;
using Orleans.Multitenant;
using System.Globalization;
using System.Security.Cryptography;

namespace CyberCloud.Gateway.Host.Principals;

/// <summary>
///     <see cref="IIdentityDirectory" /> over the identity grains: reads the tenant's directory
///     indexes, mints the secrets a resend and a registration need, and reaches the user, invitation,
///     application and session grains in the tenant. Issue #41.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Here and not in either module, for <see cref="GrainPrincipalDirectory" />'s reason:</b>
///         the resource manager may not name the identity contracts, and the identity module may not
///         name the manager's seam, so the adapter lives in the one host that references both.
///     </para>
///     <para>
///         ⚠ <b>A listed id whose grain says "not found" is skipped, not reported.</b> Every
///         directory index is written before the object it points at
///         (<see cref="IDirectoryIndexGrain" />'s remarks), so such an id is a create that died
///         between the two writes. Anything else a grain answers is a failure of the whole list: a
///         member list with a hole in it would be a person an administrator cannot see.
///     </para>
///     <para>
///         ⚠ <b>Secrets are made here and leave in two places only</b> — the grain call that digests
///         them and, for a client secret, the one response that shows it to its owner. An invitation
///         link's secret goes to the grain and the mail, and nowhere else, as
///         <see cref="GrainInvitationIssuer" /> does for the first link.
///     </para>
///     <para>
///         ⚠ <b>A registration's display name and redirect-URI count are checked here</b>, not in the
///         application grain, because the grain has registrations written before either rule and a
///         read-time refusal would strand them. What a redirect URI and a scope may be is the grain's
///         (<c>ApplicationGrain.Validate</c>), and it answers for both.
///     </para>
/// </remarks>
/// <param name="grains">The cluster. ⚠ Every reference through <c>ForTenant</c>.</param>
public sealed class GrainIdentityDirectory(IGrainFactory grains) : IIdentityDirectory {
    /// <summary>
    ///     The one way this class reaches a grain — every reference below goes through it, which is
    ///     what <c>GatewayIsolationTests.NoGatewaySourceFileTakesAGrainReferenceWithoutForTenant</c>
    ///     reads for, and why it is first in the file.
    /// </summary>
    TenantGrainFactory Tenant(Guid tenantId) => grains.ForTenant(tenantId.ToString("D", CultureInfo.InvariantCulture));

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<MemberSnapshot>>> ListMembersAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default
    ) {
        var tenant = Tenant(tenantId);

        return await ListAsync(
            tenant,
            GrainKeys.DirectoryUsers,
            async id => Map(await tenant.GetGrain<IUserGrain>(GrainKeys.User(id)).GetAsync(), Member)
        );
    }

    /// <inheritdoc />
    public async Task<Result<MemberSnapshot>> DeprovisionMemberAsync(
        Guid tenantId,
        Guid userId,
        CancellationToken cancellationToken = default
    ) {
        var user = Tenant(tenantId).GetGrain<IUserGrain>(GrainKeys.User(userId));

        // Read first: SetStatusAsync on a user nobody created is refused anyway, and this says so in
        // the member API's words.
        var existing = await user.GetAsync();

        if (existing.TryGetError(out var missing)) {
            return Result<MemberSnapshot>.Failure(missing);
        }

        var removed = await user.SetStatusAsync(UserStatus.Deprovisioned);

        return removed.TryGetError(out var refused)
            ? Result<MemberSnapshot>.Failure(refused)
            : Result<MemberSnapshot>.Success(Member(removed.GetValueOrThrow()));
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<InvitationSnapshot>>> ListInvitationsAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default
    ) {
        var tenant = Tenant(tenantId);

        return await ListAsync(
            tenant,
            GrainKeys.DirectoryInvitations,
            async id => Map(await tenant.GetGrain<IInvitationGrain>(GrainKeys.Invitation(id)).GetAsync(), Invitation)
        );
    }

    /// <inheritdoc />
    public async Task<Result<InvitationSnapshot>> ResendInvitationAsync(
        Guid tenantId,
        Guid invitationId,
        CancellationToken cancellationToken = default
    ) {
        var resent = await Tenant(tenantId)
            .GetGrain<IInvitationGrain>(GrainKeys.Invitation(invitationId))
            .ResendAsync(NewSecret());

        return Map(resent, Invitation);
    }

    /// <inheritdoc />
    public async Task<Result<InvitationSnapshot>> RevokeInvitationAsync(
        Guid tenantId,
        Guid invitationId,
        CancellationToken cancellationToken = default
    ) =>
        Map(await Tenant(tenantId).GetGrain<IInvitationGrain>(GrainKeys.Invitation(invitationId)).RevokeAsync(), Invitation);

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<ApplicationSnapshot>>> ListApplicationsAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default
    ) {
        var tenant = Tenant(tenantId);

        return await ListAsync(
            tenant,
            GrainKeys.DirectoryApplications,
            async id => Map(await tenant.GetGrain<IApplicationGrain>(GrainKeys.Application(id)).GetAsync(), Application)
        );
    }

    /// <inheritdoc />
    public async Task<Result<ApplicationSnapshot>> GetApplicationAsync(
        Guid tenantId,
        Guid applicationId,
        CancellationToken cancellationToken = default
    ) =>
        Map(await Tenant(tenantId).GetGrain<IApplicationGrain>(GrainKeys.Application(applicationId)).GetAsync(), Application);

    /// <inheritdoc />
    public async Task<Result<ApplicationRegistered>> CreateApplicationAsync(
        Guid tenantId,
        ApplicationDraft draft,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(draft);

        var name = draft.DisplayName.Trim();

        if (name.Length is 0 or > ApplicationPolicy.MaxDisplayNameLength) {
            return Result<ApplicationRegistered>.Failure(
                ErrorCode.InvalidRequestBody,
                $"An application needs a display name of 1 to {ApplicationPolicy.MaxDisplayNameLength} characters: "
                + "the consent page shows it to everyone who signs in to it."
            );
        }

        if (draft.RedirectUris.Count is 0 or > ApplicationPolicy.MaxRedirectUris) {
            return Result<ApplicationRegistered>.Failure(
                ErrorCode.InvalidRequestBody,
                $"An application needs 1 to {ApplicationPolicy.MaxRedirectUris} redirect URIs: the code flow, "
                + "the one interactive flow docs/plan/11 § Protocol allows, sends the person back to one."
            );
        }

        var applicationId = Guid.NewGuid();
        var application = Tenant(tenantId).GetGrain<IApplicationGrain>(GrainKeys.Application(applicationId));

        // ⚠ The client id is minted, never chosen: a GUID can't collide with the first-party
        // `cyc-portal` and `cyc-cli` (consulted first anyway) or with a name another tenant uses to
        // phish, and the index refuses a repeat either way.
        var created = await application.CreateAsync(
            new() {
                ApplicationId = applicationId,
                TenantId = tenantId,
                ClientId = Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture),
                DisplayName = name,
                RedirectUris = [.. draft.RedirectUris],
                AllowedGrants = [.. ApplicationPolicy.InteractiveGrants],
                AllowedScopes = [.. draft.Scopes.Distinct(StringComparer.Ordinal)],
                IsPublicClient = draft.IsPublicClient
            }
        );

        if (created.TryGetError(out var refused)) {
            return Result<ApplicationRegistered>.Failure(refused);
        }

        if (draft.IsPublicClient) {
            return Result<ApplicationRegistered>.Success(
                new() { Application = Application(created.GetValueOrThrow()), ClientSecret = string.Empty }
            );
        }

        // ⚠ A refusal here leaves a confidential registration with no secret, which can sign nobody
        // in (ClientSecretVerifier finds no credential) and which a rotation repairs.
        return await IssueSecretAsync(application);
    }

    /// <inheritdoc />
    public async Task<Result<ApplicationRegistered>> RotateApplicationSecretAsync(
        Guid tenantId,
        Guid applicationId,
        CancellationToken cancellationToken = default
    ) =>
        await IssueSecretAsync(Tenant(tenantId).GetGrain<IApplicationGrain>(GrainKeys.Application(applicationId)));

    /// <inheritdoc />
    public async Task<Result> DeleteApplicationAsync(
        Guid tenantId,
        Guid applicationId,
        CancellationToken cancellationToken = default
    ) =>
        await Tenant(tenantId).GetGrain<IApplicationGrain>(GrainKeys.Application(applicationId)).DeleteAsync();

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<SessionSnapshot>>> ListSessionsAsync(
        Guid tenantId,
        Guid userId,
        CancellationToken cancellationToken = default
    ) {
        var tenant = Tenant(tenantId);
        var tracked = await tenant.GetGrain<IUserGrain>(GrainKeys.User(userId)).ListSessionsAsync();

        if (tracked.TryGetError(out var untracked)) {
            return Result<IReadOnlyList<SessionSnapshot>>.Failure(untracked);
        }

        List<SessionSnapshot> live = [];

        foreach (var sessionId in tracked.GetValueOrThrow()) {
            var grain = tenant.GetGrain<ISessionGrain>(GrainKeys.Session(sessionId));
            var session = await grain.GetAsync();

            // ⚠ Hot tier: a session the tier lost reads "not found", and one that ended stays
            // tracked until "sign out everywhere" walks the list. Neither is a session to show.
            if (session.TryGetError(out var gone)) {
                if (gone.Code == ErrorCode.ResourceNotFound) {
                    continue;
                }

                return Result<IReadOnlyList<SessionSnapshot>>.Failure(gone);
            }

            var descriptor = session.GetValueOrThrow();
            var isLive = await grain.IsLiveAsync();

            if (descriptor.UserId != userId || !isLive.IsSuccess || !isLive.GetValueOrThrow()) {
                continue;
            }

            live.Add(
                new() {
                    SessionId = descriptor.SessionId,
                    ClientId = descriptor.ClientId,
                    DeviceLabel = descriptor.DeviceLabel,
                    CreatedAt = descriptor.CreatedAt,
                    LastUsedAt = descriptor.LastRefreshedAt > descriptor.CreatedAt ? descriptor.LastRefreshedAt : descriptor.CreatedAt,
                    Methods = [.. descriptor.Methods.Select(Camel)]
                }
            );
        }

        return Result<IReadOnlyList<SessionSnapshot>>.Success(live);
    }

    /// <inheritdoc />
    public async Task<Result> RevokeSessionAsync(
        Guid tenantId,
        Guid userId,
        Guid sessionId,
        CancellationToken cancellationToken = default
    ) {
        var tenant = Tenant(tenantId);
        var grain = tenant.GetGrain<ISessionGrain>(GrainKeys.Session(sessionId));
        var session = await grain.GetAsync();

        // ⚠ Somebody else's session and no session at all are one answer. Telling them apart would
        // let a member confirm another member's session ids — which travel inside refresh tokens.
        if (session.IsFailure || session.GetValueOrThrow().UserId != userId) {
            return Result.Failure(ErrorCode.ResourceNotFound, $"Session {sessionId:N} was not found.");
        }

        var revoked = await grain.RevokeAsync(RevocationReason.SignOut);

        if (revoked.TryGetError(out var unrevoked)) {
            return Result.Failure(unrevoked);
        }

        // The explicit single-session sign-out is where the user's list forgets an id — the
        // session grain never calls back (SessionGrain's revoke remarks say why).
        return await tenant.GetGrain<IUserGrain>(GrainKeys.User(userId)).ForgetSessionAsync(sessionId);
    }

    // ── Internals ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Reads one directory index and each object it lists, skipping ids that were never created.</summary>
    static async Task<Result<IReadOnlyList<T>>> ListAsync<T>(
        TenantGrainFactory tenant,
        string collection,
        Func<Guid, Task<Result<T>>> read
    )
        where T : notnull {
        var listed = await tenant.GetGrain<IDirectoryIndexGrain>(GrainKeys.DirectoryIndex(collection)).ListAsync();

        if (listed.TryGetError(out var unlisted)) {
            return Result<IReadOnlyList<T>>.Failure(unlisted);
        }

        var reads = await Task.WhenAll(listed.GetValueOrThrow().Select(read));
        List<T> found = [];

        foreach (var item in reads) {
            if (item.TryGetError(out var error)) {
                if (error.Code == ErrorCode.ResourceNotFound) {
                    continue;
                }

                return Result<IReadOnlyList<T>>.Failure(error);
            }

            found.Add(item.GetValueOrThrow());
        }

        return Result<IReadOnlyList<T>>.Success(found);
    }

    static async Task<Result<ApplicationRegistered>> IssueSecretAsync(IApplicationGrain application) {
        var secret = NewSecret();
        var issued = await application.IssueClientSecretAsync(secret);

        if (issued.TryGetError(out var refused)) {
            return Result<ApplicationRegistered>.Failure(refused);
        }

        return Result<ApplicationRegistered>.Success(
            new() { Application = Application(issued.GetValueOrThrow()), ClientSecret = secret }
        );
    }

    /// <summary>256 random bits, base64url without padding — 43 characters.</summary>
    static string NewSecret() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    static Result<TOut> Map<TIn, TOut>(Result<TIn> result, Func<TIn, TOut> map)
        where TIn : notnull
        where TOut : notnull =>
        result.TryGetError(out var error) ? Result<TOut>.Failure(error) : Result<TOut>.Success(map(result.GetValueOrThrow()));

    static string Camel<T>(T value)
        where T : struct, Enum {
        var name = value.ToString();

        return name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];
    }

    static MemberSnapshot Member(UserProfile user) =>
        new() {
            UserId = user.UserId,
            Email = user.Email,
            DisplayName = user.DisplayName,
            Status = Camel(user.Status),
            CreatedAt = user.CreatedAt
        };

    /// <summary>An invitation as the resource API answers one — <see cref="GrainInvitationIssuer" />'s mapping too.</summary>
    internal static InvitationSnapshot Invitation(Invitation invitation) =>
        new() {
            InvitationId = invitation.InvitationId,
            TenantId = invitation.TenantId,
            UserId = invitation.UserId,
            Email = invitation.Email,
            Status = Camel(invitation.Status),
            ExpiresAt = invitation.ExpiresAt,
            InvitedBy = invitation.InvitedBy,
            SentAt = invitation.SentAt,
            Sendings = invitation.Sendings
        };

    static ApplicationSnapshot Application(ApplicationRegistration registration) =>
        new() {
            ApplicationId = registration.ApplicationId,
            ClientId = registration.ClientId,
            DisplayName = registration.DisplayName,
            RedirectUris = [.. registration.RedirectUris],
            Scopes = [.. registration.AllowedScopes],
            IsPublicClient = registration.IsPublicClient,
            CreatedAt = registration.CreatedAt,
            ClientSecretIssuedAt = registration.ClientSecretIssuedAt
        };
}
