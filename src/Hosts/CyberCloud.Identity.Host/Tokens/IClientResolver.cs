using CyberCloud.Identity.Contracts;

namespace CyberCloud.Identity.Host.Tokens;

/// <summary>
///     Resolves a <c>client_id</c> to its registration, for a tenant. What OpenIddict's application
///     store would do if degraded mode had one — ADR-015, docs/plan/11 § Protocol.
/// </summary>
/// <remarks>
///     ⚠ An interface rather than the class alone so a test can hand the handlers a client without a
///     cluster, and so the one rule about the lookup order — first-party before tenant, see
///     <see cref="FirstPartyClients" /> — has a single implementation to be true of.
/// </remarks>
public interface IClientResolver {
    /// <summary>
    ///     The registration behind <paramref name="clientId" /> in <paramref name="tenantId" />.
    /// </summary>
    /// <param name="tenantId">The tenant the request resolved to. Ignored for a first-party id.</param>
    /// <param name="clientId">The <c>client_id</c>, verbatim.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>
    ///     The registration, or <see langword="null" /> for an unknown id, a malformed one, or one
    ///     whose application has gone. ⚠ One answer for all three, because the endpoint that asks is
    ///     unauthenticated and the difference would enumerate a tenant's registrations.
    /// </returns>
    Task<ApplicationRegistration?> ResolveAsync(
        Guid tenantId,
        string? clientId,
        CancellationToken cancellationToken = default
    );
}
