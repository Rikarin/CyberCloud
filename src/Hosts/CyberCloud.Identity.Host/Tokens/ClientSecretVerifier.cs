using CyberCloud.Core;
using CyberCloud.Core.Resources;
using CyberCloud.Identity.Contracts;
using Orleans.Multitenant;
using System.Globalization;

namespace CyberCloud.Identity.Host.Tokens;

/// <summary>
///     Checks a confidential tenant client's <c>client_secret</c> wherever its secret lives: in the
///     application grain, as a digest, when the platform issued it; in the vault, through
///     <see cref="IClientSecretSeam" />, when the registration points there. Issue #41.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The issued secret wins.</b> A registration made through the administration API has
///         <see cref="ApplicationRegistration.ClientSecretIssuedAt" /> set and no vault handle, and
///         one written straight to the grain the way #94's tests write one has a handle and no
///         issued secret. A registration with both would be one somebody rotated through the API
///         after pointing it at the vault, and the rotation is the newer intent — so the vault is
///         not consulted, and its old value stops working.
///     </para>
///     <para>
///         ⚠ <b>One answer for every refusal.</b> Missing, wrong, unreadable and "this client has no
///         secret at all" are all <c>false</c> or a failure the caller turns into the same
///         <c>invalid_client</c> sentence; <see cref="Reason" /> says which for the log only.
///     </para>
/// </remarks>
/// <param name="grains">The cluster. ⚠ Every reference through <c>ForTenant</c>.</param>
/// <param name="vault">The vault seam, for a registration that names a handle.</param>
public sealed class ClientSecretVerifier(IGrainFactory grains, IClientSecretSeam vault) {
    /// <summary>What the log says when the client holds no secret of either kind.</summary>
    public const string NoCredential = "client-has-no-credential";

    /// <summary>Whether <paramref name="client" /> holds a secret it could be checked against.</summary>
    /// <param name="client">The resolved registration.</param>
    public static bool HasCredential(ApplicationRegistration client) {
        ArgumentNullException.ThrowIfNull(client);

        return client.ClientSecretIssuedAt is not null || !client.ClientSecretRef.IsEmpty;
    }

    /// <summary>Whether <paramref name="presented" /> is the client's secret.</summary>
    /// <param name="client">The resolved registration — a confidential one.</param>
    /// <param name="presented">What the request carried as <c>client_secret</c>.</param>
    /// <param name="cancellationToken">Cancels a vault read.</param>
    /// <returns>
    ///     <c>true</c> for a match; <c>false</c> for none or no credential; a failure when the store
    ///     that holds the secret couldn't be read, whose message is for the log.
    /// </returns>
    public async Task<Result<bool>> VerifyAsync(
        ApplicationRegistration client,
        string? presented,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(client);

        if (string.IsNullOrEmpty(presented) || !HasCredential(client)) {
            return Result<bool>.Success(false);
        }

        if (client.ClientSecretIssuedAt is not null) {
            return await grains
                .ForTenant(client.TenantId.ToString("D", CultureInfo.InvariantCulture))
                .GetGrain<IApplicationGrain>(GrainKeys.Application(client.ApplicationId))
                .VerifyClientSecretAsync(presented);
        }

        return await vault.VerifyAsync(client.ClientSecretRef, presented, cancellationToken);
    }

    /// <summary>The reason a refusal gets in the log, never in the response.</summary>
    /// <param name="client">The resolved registration.</param>
    /// <param name="presented">What the request carried.</param>
    public static string Reason(ApplicationRegistration client, string? presented) {
        ArgumentNullException.ThrowIfNull(client);

        return string.IsNullOrEmpty(presented) ? "client-secret-missing"
            : !HasCredential(client) ? NoCredential
            : "client-secret-rejected";
    }
}
