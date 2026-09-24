using CyberCloud.Authorization.Contracts;
using Microsoft.Extensions.Logging;
using Orleans.Multitenant;
using System.Globalization;

namespace CyberCloud.ResourceManager;

/// <summary>
///     Inviting a colleague — the check, then the issuer. <see cref="IInvitationManager" />'s
///     remarks carry the argument for why the check is here and nowhere else. Issue #43, step 7.
/// </summary>
/// <remarks>
///     <para>
///         <b>The order:</b> the tenant in the address must be the caller's (the gateway rebuilt it
///         from the token, so a disagreement is a bug upstream and is answered as absence); the
///         inviter must have a subject id that is a GUID, because the invitation records who sent
///         it; <c>assignRole</c> on the tenant, fully consistent — <see cref="IScopeAuthorizer" />, the
///         seam every scope verb uses, so a suspended owner is refused by the tenant's own
///         <c>#suspended</c>; then the issuer.
///     </para>
///     <para>
///         ⚠ <b>The tenant's name comes from its grain, never from the request.</b> It is printed in
///         the mail a stranger receives; a name the caller chose would let an owner of one
///         organisation send mail that says it is from another.
///     </para>
///     <para>
///         ⚠ <b>Every grain reference goes through <c>ForTenant</c></b>, as in the three services
///         beside it; held by the gateway, an Orleans client.
///     </para>
/// </remarks>
public sealed class InvitationService(
    IScopeAuthorizer scopes,
    IInvitationIssuer issuer,
    IGrainFactory grains,
    ILogger<InvitationService> logger
) : IInvitationManager {
    /// <inheritdoc />
    public async Task<Result<InvitationSnapshot>> InviteAsync(
        InvitationManagerRequest request,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(request);

        var scope = ScopeId.Tenant(request.TenantId);

        if (request.Caller.TenantId != request.TenantId) {
            return Result<InvitationSnapshot>.Failure(
                ErrorCode.ResourceNotFound,
                $"The resource '{scope.Path}' was not found."
            );
        }

        var authorized = await scopes.AuthorizeAsync(
            scope,
            Permissions.AssignRole,
            Permissions.Read,
            request.Caller,
            fullyConsistent: true,
            cancellationToken
        );

        if (authorized.TryGetError(out var refused)) {
            return Result<InvitationSnapshot>.Failure(refused);
        }

        if (!Guid.TryParseExact(request.Caller.SubjectId, "N", out var inviter)) {
            return Result<InvitationSnapshot>.Failure(
                ErrorCode.InvalidRequestBody,
                "An invitation records who sent it, and this caller's subject id is not a GUID."
            );
        }

        var tenant = await grains
            .ForTenant(request.TenantId.ToString("D", CultureInfo.InvariantCulture))
            .GetGrain<ITenantGrain>(GrainKeys.Tenant(request.TenantId))
            .GetAsync();

        var issued = await issuer.IssueAsync(
            new() {
                TenantId = request.TenantId,
                // A tenant with no record of its own is named by its id — ugly in a mail, but never
                // a name somebody typed into the request.
                TenantName = tenant.IsSuccess ? tenant.GetValueOrThrow().Slug : request.TenantId.ToString("D", CultureInfo.InvariantCulture),
                Email = request.Email,
                InvitedBy = inviter
            },
            cancellationToken
        );

        if (issued.IsSuccess) {
            // ⚠ The invitation's id and the inviter, never the address: an address in a log message
            // is the PII docs/plan/11 § Auditing keeps out of message strings.
            logger.LogInformation(
                "{Caller} invited a member into tenant {TenantId}: invitation {InvitationId}.",
                request.Caller,
                request.TenantId,
                issued.GetValueOrThrow().InvitationId
            );
        }

        return issued;
    }
}
