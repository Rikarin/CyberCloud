using CyberCloud.Identity.Contracts;
using Orleans.Multitenant;
using System.Globalization;
using System.Security.Cryptography;

namespace CyberCloud.Gateway.Host.Principals;

/// <summary>
///     <see cref="IInvitationIssuer" /> over the identity grains: mints the invitation's id and the
///     secret its link carries, and hands both to <c>IInvitationGrain</c> in the caller's tenant.
///     Issue #43, step 7.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Here and not in either module, for <see cref="GrainPrincipalDirectory" />'s reason:</b>
///         the resource manager may not name the identity contracts, and the identity module may not
///         name the manager's seam, so the adapter lives in the one host that references both.
///     </para>
///     <para>
///         ⚠ <b>The secret is made here and leaves only in the grain call.</b> 256 random bits, the
///         grain keeps their SHA-256 and mails the value, and nothing returns it — the answer the
///         gateway writes is the invitation without its link (<c>ResponseBodies.Invitation</c>).
///     </para>
/// </remarks>
/// <param name="grains">The cluster. ⚠ Every reference through <c>ForTenant</c>.</param>
public sealed class GrainInvitationIssuer(IGrainFactory grains) : IInvitationIssuer {
    /// <inheritdoc />
    public async Task<Result<InvitationSnapshot>> IssueAsync(
        InvitationIssue issue,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(issue);

        cancellationToken.ThrowIfCancellationRequested();

        var invitationId = Guid.NewGuid();
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        var created = await grains
            .ForTenant(issue.TenantId.ToString("D", CultureInfo.InvariantCulture))
            .GetGrain<IInvitationGrain>(GrainKeys.Invitation(invitationId))
            .CreateAsync(
                new() {
                    Email = issue.Email,
                    InvitedBy = issue.InvitedBy,
                    TenantName = issue.TenantName,
                    ExpiresAt = DateTimeOffset.UtcNow + InvitationPolicy.Lifetime
                },
                secret
            );

        if (created.TryGetError(out var refused)) {
            return Result<InvitationSnapshot>.Failure(refused);
        }

        var invitation = created.GetValueOrThrow();

        return Result<InvitationSnapshot>.Success(
            new() {
                InvitationId = invitation.InvitationId,
                TenantId = invitation.TenantId,
                UserId = invitation.UserId,
                Email = invitation.Email,
                Status = invitation.Status.ToString().ToLowerInvariant(),
                ExpiresAt = invitation.ExpiresAt
            }
        );
    }
}
