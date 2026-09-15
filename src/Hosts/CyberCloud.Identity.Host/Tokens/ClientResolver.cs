using CyberCloud.Core.Resources;
using CyberCloud.Identity.Contracts;
using CyberCloud.Tenancy.Contracts;
using Orleans.Multitenant;

namespace CyberCloud.Identity.Host.Tokens;

/// <summary>
///     The static first-party list, then the tenant's client index. docs/plan/11 § Protocol.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The static list is consulted first, unconditionally.</b> A tenant that registered an
///         application called <c>cyc-portal</c> must not have the platform's own client id resolve
///         to its redirect URIs — that is how a tenant would receive codes minted for the portal.
///         Checking the tenant's index first "because it is more specific" is the natural mistake,
///         and <c>AuthorizeHandlerTests.ATenantRegisteredClientCannotShadowAFirstPartyId</c> is
///         what makes it a failing test.
///     </para>
///     <para>
///         ⚠ <b>The client id is validated before it becomes a grain key.</b>
///         <c>GrainKeys.ClientIndex</c> throws on a value <c>EnsureValidClientId</c> refuses, and an
///         exception from an unauthenticated endpoint is a <c>500</c> with a stack trace; the
///         validation runs here first so a malformed id is "unknown" like any other.
///     </para>
///     <para>
///         Every tenant grain is reached through <c>ForTenant</c>, for the reason on
///         <c>SignInApi</c>: this host is an Orleans client and the tenant-separating call filter
///         never sees it, so the qualification is the whole of the separation.
///     </para>
/// </remarks>
public sealed class ClientResolver(IGrainFactory grains, FirstPartyClients firstParty) : IClientResolver {
    /// <inheritdoc />
    public async Task<ApplicationRegistration?> ResolveAsync(
        Guid tenantId,
        string? clientId,
        CancellationToken cancellationToken = default
    ) {
        if (firstParty.Find(clientId) is { } known) {
            return known;
        }

        if (GrainKeys.EnsureValidClientId(clientId).TryGetError(out _)) {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var tenant = grains.ForTenant(TenantHint.Qualifier(tenantId));

        var resolved = await tenant
            .GetGrain<IClientIndexGrain>(GrainKeys.ClientIndex(tenantId, clientId!))
            .ResolveAsync();

        if (resolved.TryGetError(out _)) {
            return null;
        }

        var registration = await tenant
            .GetGrain<IApplicationGrain>(GrainKeys.Application(resolved.GetValueOrThrow()))
            .GetAsync();

        return registration.TryGetError(out _) ? null : registration.GetValueOrThrow();
    }
}
