using CyberCloud.Authorization.Contracts;
using CyberCloud.Core;
using CyberCloud.Core.Resources;
using CyberCloud.Core.Time;
using CyberCloud.Identity.Contracts;
using CyberCloud.Identity.SignIn;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using Orleans.Multitenant;
using System.Globalization;
using System.Security.Claims;

namespace CyberCloud.Identity.Host.Tokens;

/// <summary>
///     What the token endpoint decides: which client is asking, and what principal to mint for it.
///     docs/plan/11 § Protocol.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             Client credentials only, and the other four rows of docs/plan/11 § Protocol's flow
///             table are owed with their reasons stated.
///         </b> This is the first grant the host serves because it is the one a CI job or a service
///         principal can complete with no browser, no cookie and no page — and therefore the one that
///         can prove, end to end and in a test, that a token minted here is a token the gateway
///         accepts. What each of the others is waiting on:
///     </para>
///     <list type="bullet">
///         <item>
///             <b>Authorization code + PKCE</b> needs <c>/authorize</c> to validate a
///             <c>client_id</c> and a <c>redirect_uri</c> against an <c>ApplicationRegistration</c>,
///             and nothing maps a <c>client_id</c> to its application — <see cref="IdentityHostOptions" />
///             records that the index is a tenancy change rather than a host change. The cookie
///             session it would mint from already exists (<c>IdentitySessionPrincipal</c>).
///         </item>
///         <item>
///             <b>Refresh</b> follows the authorization code: OpenIddict issues one only to a flow
///             that granted <c>offline_access</c>, and <c>ISessionGrain.RefreshAsync</c> is the
///             rotation it would drive.
///         </item>
///         <item>
///             <b>Device authorization</b> needs the verification page and, in the degraded mode
///             this server runs in, a store for device and user codes — see
///             <see cref="IdentityHostOpenIddict" />.
///         </item>
///         <item>
///             <b>Token exchange</b> has its whole decision built — <see cref="ITokenExchange" /> —
///             and is waiting on this endpoint to accept the grant and call it.
///         </item>
///     </list>
///     <para>
///         ⚠ <b>The <c>client_id</c> of a service principal is its own id, in <c>N</c> form.</b>
///         <see cref="ServicePrincipalDescriptor" /> carries no client id of its own and names an
///         application registration only "when there is one"; a principal with none has nothing else
///         to be addressed by, and a GUID is a grain key this host can resolve without an index. A
///         value that is not a GUID is refused before any grain is touched, which is what keeps this
///         endpoint from being a way to activate grains by name.
///     </para>
///     <para>
///         ⚠ <b>Every refusal is <c>invalid_client</c> and says nothing more.</b> An unknown id, a
///         disabled principal, a wrong secret and an unwired verifier all answer identically; the
///         reason goes to the log through <see cref="IdentityLog.TokenRequestRefused" />. A token
///         endpoint that distinguished them would enumerate a tenant's service principals for anyone
///         who can reach it.
///     </para>
///     <para>
///         Returns values rather than writing responses, for the reason <c>SignInApi</c> gives: a
///         decision that returns can be asserted on without a <c>TestServer</c>, and this project's
///         test suite deliberately has none.
///     </para>
/// </remarks>
/// <param name="grains">The cluster. ⚠ Every reference goes through <c>ForTenant</c>.</param>
/// <param name="secrets">The vault seam a service principal's credential is checked through.</param>
/// <param name="options">Which tenant this host serves.</param>
/// <param name="clock">For <c>auth_time</c>.</param>
/// <param name="logger">Where the refusal reasons go.</param>
public sealed class TokenApi(
    IGrainFactory grains,
    IClientSecretSeam secrets,
    IOptions<IdentityHostOptions> options,
    IClock clock,
    ILogger<TokenApi> logger
) {
    /// <summary>
    ///     The one refusal the endpoint sends, whatever happened. RFC 6749 § 5.2's
    ///     <c>invalid_client</c>.
    /// </summary>
    public const string InvalidClientDescription = "The client could not be authenticated.";

    readonly IdentityHostOptions options = options.Value;

    /// <summary>
    ///     Authenticates the client behind a client-credentials request.
    /// </summary>
    /// <param name="clientId">The <c>client_id</c> parameter, verbatim.</param>
    /// <param name="clientSecret">The <c>client_secret</c> parameter, verbatim.</param>
    /// <param name="cancellationToken">The request's token.</param>
    /// <returns>
    ///     The service principal, or <see cref="ErrorCode.AuthorizationFailed" /> carrying
    ///     <see cref="InvalidClientDescription" /> — and only that.
    /// </returns>
    public async Task<Result<ServicePrincipalDescriptor>> AuthenticateClientAsync(
        string? clientId,
        string? clientSecret,
        CancellationToken cancellationToken = default
    ) {
        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret)) {
            return Refuse("missing-credentials");
        }

        // ⚠ Parsed strictly, before any grain call. GrainKeys.ServicePrincipal would happily build a
        // key from any string, and a key built from caller input is a grain activated by caller
        // input — a table of empty activations named by whoever is probing the endpoint.
        if (!Guid.TryParseExact(clientId, "N", out var servicePrincipalId) || servicePrincipalId == Guid.Empty) {
            return Refuse("client-id-not-a-service-principal-id");
        }

        var found = await grains.ForTenant(options.TenantId.ToString("D", CultureInfo.InvariantCulture))
            .GetGrain<IServicePrincipalGrain>(GrainKeys.ServicePrincipal(servicePrincipalId))
            .GetAsync();

        if (found.TryGetError(out _)) {
            return Refuse("unknown-client");
        }

        var principal = found.GetValueOrThrow();

        if (!principal.Enabled) {
            return Refuse("client-disabled");
        }

        if (principal.CredentialSecretRef.IsEmpty) {
            return Refuse("client-has-no-credential");
        }

        var verified = await secrets.VerifyAsync(principal.CredentialSecretRef, clientSecret, cancellationToken);

        if (verified.TryGetError(out var unavailable)) {
            // ⚠ Verbatim: this is the sentence naming the missing IClientSecretSeam registration,
            // and it is the only place an operator will read it.
            return Refuse(unavailable.Message);
        }

        return verified.GetValueOrThrow()
            ? Result<ServicePrincipalDescriptor>.Success(principal)
            : Refuse("credential-rejected");
    }

    /// <summary>
    ///     Builds the access-token principal for an authenticated service principal.
    /// </summary>
    /// <param name="principal">What <see cref="AuthenticateClientAsync" /> returned.</param>
    /// <param name="clientId">The <c>client_id</c> it presented.</param>
    /// <param name="scopes">The scopes the request asked for.</param>
    /// <returns>A principal OpenIddict can sign in with, carrying only the closed claim set.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The audience is <see cref="AccessTokenPolicy.Audience" /> and is not negotiable
    ///         from the request.</b> A <c>resource</c> parameter that chose the audience would let a
    ///         client mint a token for a relying party it was never registered with. There is one API
    ///         and one audience; a second relying party is a design change, not a parameter.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>aud</c> and <c>azp</c> travel as OpenIddict's own audience and presenter as
    ///         well as as claims.</b> OpenIddict writes <c>aud</c> from the principal's registered
    ///         audiences and would otherwise write none, and the gateway pins <c>aud</c> — so a
    ///         principal that carried the claim and not the registration would mint a token the
    ///         gateway refuses. Both are set from the same two values, in this one place.
    ///     </para>
    /// </remarks>
    public ClaimsPrincipal Mint(
        ServicePrincipalDescriptor principal,
        string clientId,
        IReadOnlyList<string> scopes
    ) {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(scopes);

        var minted = AccessTokenPrincipalFactory.BuildForServicePrincipal(
            principal,
            clientId,
            AccessTokenPolicy.Audience,
            scopes,
            clock.UtcNow
        );

        minted.SetAudiences(AccessTokenPolicy.Audience);
        minted.SetPresenters(clientId);

        IdentityLog.TokenIssued(
            logger,
            principal.TenantId,
            SubjectTypes.ServicePrincipal,
            principal.ServicePrincipalId,
            OpenIddictConstants.GrantTypes.ClientCredentials
        );

        return minted;
    }

    Result<ServicePrincipalDescriptor> Refuse(string reason) {
        IdentityLog.TokenRequestRefused(
            logger,
            options.TenantId,
            OpenIddictConstants.GrantTypes.ClientCredentials,
            reason
        );

        return Result<ServicePrincipalDescriptor>.Failure(ErrorCode.AuthorizationFailed, InvalidClientDescription);
    }
}
