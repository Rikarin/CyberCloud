using CyberCloud.Identity.Contracts;
using Orleans.Multitenant;
using System.Globalization;

namespace CyberCloud.Gateway.Host.Principals;

/// <summary>
///     The <see cref="IPrincipalDirectory" /> that answers: a principal exists when the identity
///     grain its id names has been created in the assignment's tenant — docs/plan/11 § The object
///     model's four principal rows, read through their own grains.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             In this host and not in <c>CyberCloud.ResourceManager</c> or <c>CyberCloud.Identity</c>,
///             which is a layering fact rather than a preference.
///         </b> The seam is the resource manager's and the grains are identity's, and
///         <c>module-layering.txt</c> declares no edge between those two modules in either direction.
///         Hosts are outside that rule and this one already references both assemblies — the
///         resource manager for stage 8, <c>CyberCloud.Identity.Contracts</c> for the token
///         contract — so this is the adapter <c>ITotpSecretSeam</c>'s remarks describe for the
///         vault, written for the directory: a class over two seams, in the process that holds
///         both. The silo composes the resource manager too and keeps the refusing default,
///         because a silo never serves a grant.
///     </para>
///     <para>
///         ⚠
///         <b>
///             The id must be the <c>N</c> form of a GUID, because that is what the token carries
///             and what the grain is keyed by.
///         </b> <c>AccessTokenPrincipalFactory</c> mints <c>sub</c> as
///         the principal's GUID in <c>N</c> form, so that is the subject id every tuple written for a
///         real caller uses, and <c>GrainKeys.User</c> and its siblings build their keys from the same
///         form. Any other spelling — the <c>D</c> form, a name, a partial id — names a subject no
///         token will ever present and a grain no key can reach, and it answers <c>false</c> here
///         rather than being corrected: five spellings of one principal would be five assignments.
///     </para>
///     <para>
///         ⚠ <b>Cross-tenant is closed by <c>ForTenant</c> and by nothing else.</b> Every principal
///         grain is tenant-qualified, so a user of tenant B asked for under tenant A is an activation
///         nothing ever created, and <c>GetAsync</c> answers "not found". There is no second
///         comparison here to keep in step with that, which is the point.
///     </para>
///     <para>
///         ⚠ <b>What exists and what does not, by type.</b> A user exists unless
///         <see cref="UserStatus.Deprovisioned" /> — a suspended user is still a directory object
///         and a standing grant on one is the ordinary case, but a deprovisioned one is terminal
///         and Azure refuses a deleted principal too. A service principal and a managed identity
///         exist when their descriptor does; a disabled service principal still exists, because
///         disabling is about authentication and a grant is about what it may do once it does. A
///         group exists unless deleted, which <c>IGroupGrain.GetAsync</c> already folds in.
///     </para>
/// </remarks>
public sealed class GrainPrincipalDirectory(IGrainFactory grains) : IPrincipalDirectory {
    /// <inheritdoc />
    public async Task<Result<bool>> ExistsAsync(
        Guid tenantId,
        string principalType,
        string principalId,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(principalType);
        ArgumentNullException.ThrowIfNull(principalId);

        if (!GuidFormat.TryParseN(principalId, out var id)) {
            return Result<bool>.Success(false);
        }

        var tenant = grains.ForTenant(tenantId.ToString("D", CultureInfo.InvariantCulture));

        switch (principalType) {
            case "user": {
                var user = await tenant.GetGrain<IUserGrain>(GrainKeys.User(id)).GetAsync();

                return user.TryGetError(out var error)
                    ? Absent(error)
                    : Result<bool>.Success(user.GetValueOrThrow().Status != UserStatus.Deprovisioned);
            }
            case "servicePrincipal": {
                var principal = await tenant
                    .GetGrain<IServicePrincipalGrain>(GrainKeys.ServicePrincipal(id))
                    .GetAsync();

                return principal.TryGetError(out var error) ? Absent(error) : Result<bool>.Success(true);
            }
            case "managedIdentity": {
                var identity = await tenant
                    .GetGrain<IManagedIdentityGrain>(GrainKeys.ManagedIdentity(id))
                    .GetAsync();

                return identity.TryGetError(out var error) ? Absent(error) : Result<bool>.Success(true);
            }
            case "group": {
                var group = await tenant.GetGrain<IGroupGrain>(GrainKeys.Group(id)).GetAsync();

                return group.TryGetError(out var error) ? Absent(error) : Result<bool>.Success(true);
            }
            default:
                // The manager has already refused anything outside its closed set; a fifth type
                // reaching here is a new principal kind nobody taught this class about, and "does
                // not exist" is the fail-closed reading.
                return Result<bool>.Success(false);
        }
    }

    /// <summary>
    ///     A grain's "not found" is the directory's <c>false</c>; anything else is a failure the
    ///     caller must not read as either answer.
    /// </summary>
    static Result<bool> Absent(Error error) =>
        error.Code == ErrorCode.ResourceNotFound ? Result<bool>.Success(false) : Result<bool>.Failure(error);
}
