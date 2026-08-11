namespace CyberCloud.Identity.Seams;

/// <summary>
///     The default <see cref="ITokenExchangeSeam" />: it fails, and says exactly what is missing.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>A seam that succeeds is an authentication bypass, so this one refuses loudly.</b> The
///         tempting alternative — return a token for the named managed identity and "wire the
///         validation later" — would mean any caller who can reach <c>/token</c> can assume any
///         managed identity in the tenant, which is the worst bug this module could have.
///     </para>
///     <para>
///         <b>What has to exist before this can be implemented</b>, from docs/plan/11 § Managed
///         identity: an <c>IManagedIdentityGrain</c> holding the <c>(cluster, namespace,
///         serviceAccount)</c> binding; the cluster's OIDC issuer URL and JWKS, read once and
///         refreshed; a validator for the projected service-account token against that issuer; and —
///         the part the document flags as the real obstacle — a way to <i>reach</i> the discovery
///         document, either because the cluster publishes it publicly or through the
///         <c>AgentInitiated</c> tunnel of docs/plan/09. For a BYO cluster none of that is automatic,
///         and the portal has to say so at binding time rather than failing here.
///     </para>
/// </remarks>
public sealed class UnavailableTokenExchange : ITokenExchangeSeam {
    /// <inheritdoc />
    public Task<Result<SignInOutcome>> ExchangeAsync(
        string subjectToken,
        string subjectTokenType,
        Guid managedIdentityId
    ) =>
        Task.FromResult(
            Result<SignInOutcome>.Failure(
                ErrorCode.InternalError,
                "Token exchange (RFC 8693) is not implemented. docs/plan/11 § Managed identity needs "
                + "a tenant cluster's OIDC issuer and JWKS to validate the projected service-account "
                + "token against, and nothing in this repository provisions a cluster whose discovery "
                + "document is reachable. This seam refuses rather than issuing a token, because a "
                + "token exchange that does not validate the subject token is an authentication "
                + "bypass for every managed identity in the tenant."
            )
        );
}

/// <summary>
///     The default <see cref="IOtpDeliverySeam" />: it fails, and says which module is missing.
/// </summary>
/// <remarks>
///     ⚠ <b>It does not succeed quietly.</b> An OTP factor that reports delivery and sends nothing
///     locks every user who enrols in it out of their own account, and the failure surfaces on the
///     second sign-in rather than at enrolment — which is the worst possible time to discover it.
/// </remarks>
public sealed class UnavailableOtpDelivery : IOtpDeliverySeam {
    /// <inheritdoc />
    public Task<Result> DeliverAsync(CredentialKind kind, string destination, string code) =>
        Task.FromResult(
            Result.Failure(
                ErrorCode.InternalError,
                $"{kind} delivery is not implemented. docs/plan/11 § Credentials routes email, SMS "
                + "and WhatsApp codes through CyberCloud.Communication (docs/plan/17), which does not "
                + "exist. Generating and checking six digits is the small part; provider fan-out, "
                + "per-tenant sender identity, bounce handling and WhatsApp template pre-approval are "
                + "the feature, and they belong to that module."
            )
        );
}
