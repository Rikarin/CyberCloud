using Orleans.Multitenant;
using System.Globalization;

namespace CyberCloud.Identity.ManagedIdentity;

/// <summary>
///     <see cref="ITokenExchange" /> over <see cref="IManagedIdentityGrain" />. docs/plan/11 § Managed
///     identity, steps 4 to 6.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This type decides nothing, and that is what it is for.</b> It turns a
///         <c>(tenant, managedIdentity)</c> pair into a grain reference and forwards. Every trust
///         decision — which issuer, which key set, whether the binding matches — happens inside the
///         grain, where the state that answers those questions lives. A service that "pre-checked"
///         anything here would be a second place the answer could be different.
///     </para>
///     <para>
///         ⚠ <b>The tenant comes from the caller, and that is safe only because it is not a
///         credential.</b> The <c>/token</c> endpoint gets it from the request, so a caller can name
///         any tenant they like — and learns nothing by doing so, because the identity in that tenant
///         either has a binding whose cluster signed the presented token or it does not. Naming the
///         wrong tenant produces the same refusal as naming the right one with the wrong token, which
///         is the property <see cref="ManagedIdentityFailures.Exchange" /> exists to preserve.
///     </para>
/// </remarks>
public sealed class GrainTokenExchange(IGrainFactory grains) : ITokenExchange {
    /// <inheritdoc />
    public Task<Result<ExchangedSubject>> ExchangeAsync(
        Guid tenantId,
        Guid managedIdentityId,
        string subjectToken,
        string subjectTokenType
    ) {
        // ⚠ Refused before a grain is touched. docs/plan/11 § Credentials' rule — "an authentication
        // endpoint whose failure path costs a grain activation is a denial-of-service amplifier" —
        // is about the lockout counter and applies verbatim here: /token is unauthenticated, and a
        // caller who could provoke an activation per request by sending nonsense GUIDs would be
        // creating durable-tier grain activations for free.
        if (tenantId == Guid.Empty || managedIdentityId == Guid.Empty || string.IsNullOrEmpty(subjectToken)) {
            return Task.FromResult(ManagedIdentityFailures.RejectExchange());
        }

        var identity = grains
            .ForTenant(tenantId.ToString("D", CultureInfo.InvariantCulture))
            .GetGrain<IManagedIdentityGrain>(GrainKeys.ManagedIdentity(managedIdentityId));

        return identity.ExchangeAsync(subjectToken, subjectTokenType);
    }
}
