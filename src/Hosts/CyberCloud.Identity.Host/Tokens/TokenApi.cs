using CyberCloud.Authorization.Contracts;
using CyberCloud.Core;
using CyberCloud.Core.Resources;
using CyberCloud.Core.Time;
using CyberCloud.Identity.Contracts;
using CyberCloud.Identity.Credentials;
using CyberCloud.Identity.SignIn;
using Microsoft.Extensions.Logging;
using OpenIddict.Abstractions;
using Orleans.Multitenant;
using System.Globalization;
using System.Security.Claims;

namespace CyberCloud.Identity.Host.Tokens;

/// <summary>
///     What the token endpoint decides: which client is asking, which session a token belongs to,
///     and what principal to mint for it. docs/plan/11 § Protocol.
/// </summary>
/// <remarks>
///     <para>
///         <b>Three grants, three shapes, one factory.</b> Client credentials authenticates a
///         service principal against the vault seam and mints with no session. The authorization
///         code — minted by <c>AuthorizeApi</c> from the cookie session — is exchanged here for a
///         <i>token session</i>: one <see cref="ISessionGrain" /> per (user, client), opened at the
///         exchange and bound to the interactive session by <c>cyc:isid</c>. The refresh grant
///         rotates that session's chain. Every access token comes out of
///         <see cref="AccessTokenPrincipalFactory.Build" />, so the closed claim set is the same
///         whichever grant produced it.
///     </para>
///     <para>
///         ⚠
///         <b>
///             One token session per (user, client), never the cookie session reused, because one
///             grain has one chain.
///         </b> Two clients refreshing one chain would trip reuse detection against each other:
///         the second client's handle is the first client's retired generation. So the exchange
///         opens a fresh grain and remembers where it came from; a refresh checks the interactive
///         session is still live before rotating, which is what makes <c>/logout</c> end every
///         chain derived from a sign-in without enumerating them.
///     </para>
///     <para>
///         ⚠
///         <b>
///             Every refusal after the token validated is <c>invalid_grant</c> and one of three
///             sentences.
///         </b> A revoked interactive session, a replayed handle, an expired chain and a
///         session that no longer exists are told apart in the log
///         (<c>GrantLog.GrantRefused</c>) and not in the body — a body that distinguished "replayed"
///         from "expired" would tell whoever holds a stolen token whether the legitimate client is
///         still active. The third, <see cref="CodeReplayedDescription" />, is the one that may be
///         specific, and its own remarks say why.
///     </para>
///     <para>
///         ⚠ <b>The <c>client_id</c> of a service principal is its own id, in <c>N</c> form.</b>
///         <see cref="ServicePrincipalDescriptor" /> carries no client id of its own and names an
///         application registration only "when there is one"; a principal with none has nothing else
///         to be addressed by, and a GUID is a grain key this host can resolve without an index. A
///         value that is not a GUID is refused before any grain is touched, which is what keeps this
///         endpoint from being a way to activate grains by name. Every client-credentials refusal
///         is <c>invalid_client</c> and says nothing more; the reason goes to the log through
///         <see cref="IdentityLog.TokenRequestRefused" />.
///     </para>
///     <para>
///         Returns values rather than writing responses, for the reason <c>SignInApi</c> gives: a
///         decision that returns can be asserted on without a <c>TestServer</c>.
///     </para>
/// </remarks>
/// <param name="grains">The cluster. ⚠ Every reference goes through <c>ForTenant</c>.</param>
/// <param name="secrets">The vault seam a service principal's credential is checked through.</param>
/// <param name="tenants">Which tenant a request that names none belongs to.</param>
/// <param name="clock">For <c>auth_time</c> on a grant with no session behind it.</param>
/// <param name="logger">Where the refusal reasons go.</param>
public sealed class TokenApi(
    IGrainFactory grains,
    IClientSecretSeam secrets,
    TenantHint tenants,
    IClock clock,
    ILogger<TokenApi> logger
) {
    /// <summary>
    ///     The one refusal the client-credentials grant sends, whatever happened. RFC 6749 § 5.2's
    ///     <c>invalid_client</c>.
    /// </summary>
    public const string InvalidClientDescription = "The client could not be authenticated.";

    /// <summary>What a code exchange or a refresh answers when the sign-in behind it is gone.</summary>
    public const string SessionRevokedDescription = "The sign-in session was revoked. Sign in again.";

    /// <summary>What a refresh answers when the chain refused the handle — replayed, expired or unknown.</summary>
    public const string RefreshRejectedDescription = "That refresh token is no longer valid. Sign in again.";

    /// <summary>
    ///     What a second exchange of one authorization code answers — and the first exchange's
    ///     token session is revoked as it is said. RFC 6749 § 4.1.2.
    /// </summary>
    /// <remarks>
    ///     ⚠ A third sentence beside the two above, and it is allowed to be specific where they are
    ///     not: a replayed code tells whoever holds it nothing about the legitimate client that a
    ///     generic refusal would hide, because the code was single-use by contract and the client
    ///     that exchanged it first has already been signed out by this very answer.
    /// </remarks>
    public const string CodeReplayedDescription =
        "That authorization code was already used. The session it opened has been revoked; sign in again.";

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
    /// <remarks>
    ///     ⚠ The tenant is <see cref="TenantHint.Default" /> — the configured one, or the platform
    ///     tenant in Development. A service principal is addressed by GUID and the grant carries no
    ///     <c>tenant</c> parameter, so a host that serves several tenants' service principals is a
    ///     host that configures none and takes the tenant from an application registration, which
    ///     is owed with the HTTP surface that creates service principals.
    /// </remarks>
    public async Task<Result<ServicePrincipalDescriptor>> AuthenticateClientAsync(
        string? clientId,
        string? clientSecret,
        CancellationToken cancellationToken = default
    ) {
        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret)) {
            return Refuse(Guid.Empty, "missing-credentials");
        }

        // ⚠ Parsed strictly, before any grain call. GrainKeys.ServicePrincipal would happily build a
        // key from any string, and a key built from caller input is a grain activated by caller
        // input — a table of empty activations named by whoever is probing the endpoint.
        if (!Guid.TryParseExact(clientId, "N", out var servicePrincipalId) || servicePrincipalId == Guid.Empty) {
            return Refuse(Guid.Empty, "client-id-not-a-service-principal-id");
        }

        if (tenants.Default is not { } tenantId) {
            return Refuse(Guid.Empty, "no-tenant-configured");
        }

        var found = await grains.ForTenant(TenantHint.Qualifier(tenantId))
            .GetGrain<IServicePrincipalGrain>(GrainKeys.ServicePrincipal(servicePrincipalId))
            .GetAsync();

        if (found.TryGetError(out _)) {
            return Refuse(tenantId, "unknown-client");
        }

        var principal = found.GetValueOrThrow();

        if (!principal.Enabled) {
            return Refuse(tenantId, "client-disabled");
        }

        if (principal.CredentialSecretRef.IsEmpty) {
            return Refuse(tenantId, "client-has-no-credential");
        }

        var verified = await secrets.VerifyAsync(principal.CredentialSecretRef, clientSecret, cancellationToken);

        if (verified.TryGetError(out var unavailable)) {
            // ⚠ Verbatim: this is the sentence naming the missing IClientSecretSeam registration,
            // and it is the only place an operator will read it.
            return Refuse(tenantId, unavailable.Message);
        }

        return verified.GetValueOrThrow()
            ? Result<ServicePrincipalDescriptor>.Success(principal)
            : Refuse(tenantId, "credential-rejected");
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
    ///         ⚠
    ///         <b>
    ///             The audience is <see cref="AccessTokenPolicy.Audience" /> and is not negotiable
    ///             from the request.
    ///         </b> A <c>resource</c> parameter that chose the audience would let a
    ///         client mint a token for a relying party it was never registered with. There is one API
    ///         and one audience; a second relying party is a design change, not a parameter.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             <c>aud</c> and <c>azp</c> travel as OpenIddict's own audience and presenter as
    ///             well as as claims.
    ///         </b> OpenIddict writes <c>aud</c> from the principal's registered
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
        minted.SetScopes(scopes);

        IdentityLog.TokenIssued(
            logger,
            principal.TenantId,
            SubjectTypes.ServicePrincipal,
            principal.ServicePrincipalId,
            OpenIddictConstants.GrantTypes.ClientCredentials
        );

        return minted;
    }

    // ── The authorization code ─────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Builds the principal an authorization code is minted from — the cookie session, as the
    ///     client will see it.
    /// </summary>
    /// <param name="interactive">The cookie session, from <c>ISessionGrain.GetAsync</c>.</param>
    /// <param name="user">The user's profile, for the id_token's <c>email</c> and <c>name</c>.</param>
    /// <param name="client">The registration the request resolved to.</param>
    /// <param name="scopes">The granted scopes — the request's, cut to the client's.</param>
    /// <returns>A principal <c>/authorize</c> can sign in with.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is not an access token and it is not built by the factory.</b> The code is
    ///         an encrypted, five-minute, single-client envelope that only this server opens, and
    ///         its job is to carry the facts the exchange needs: who, in which tenant, from which
    ///         interactive session, authenticated when and how, for which scopes. The exchange
    ///         reads those and builds the access token through
    ///         <see cref="AccessTokenPrincipalFactory.Build" /> like every other grant — so the
    ///         closed set is checked where the access token is made, not here.
    ///     </para>
    ///     <para>
    ///         <c>auth_time</c> is the interactive session's <see cref="SessionDescriptor.AuthenticatedAt" />
    ///         and is carried, unchanged, into every access token the exchange and its refreshes
    ///         mint — <see cref="AccessTokenClaims.AuthenticationTime" /> says why it must not be
    ///         recomputed. Typed as an integer for the reason the factory gives: OpenIddict refuses
    ///         the sign-in otherwise.
    ///     </para>
    /// </remarks>
    public static ClaimsPrincipal BuildCodePrincipal(
        SessionDescriptor interactive,
        UserProfile user,
        ApplicationRegistration client,
        IReadOnlyList<string> scopes
    ) {
        ArgumentNullException.ThrowIfNull(interactive);
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(scopes);

        var identity = new ClaimsIdentity(
            "CyberCloud.AuthorizationCode",
            AccessTokenClaims.Subject,
            "urn:cybercloud:roles-are-not-in-the-token"
        );

        identity.AddClaim(new Claim(AccessTokenClaims.Subject, N(interactive.UserId)));
        identity.AddClaim(new Claim(AccessTokenClaims.SubjectType, SubjectTypes.User));
        identity.AddClaim(new Claim(AccessTokenClaims.TenantId, N(interactive.TenantId)));
        identity.AddClaim(new Claim(AccessTokenClaims.SessionId, N(interactive.SessionId)));

        identity.AddClaim(
            new Claim(
                AccessTokenClaims.AuthenticationTime,
                interactive.AuthenticatedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
                ClaimValueTypes.Integer64
            )
        );

        foreach (var method in interactive.Methods) {
            identity.AddClaim(
                new Claim(AccessTokenClaims.AuthenticationMethods, AccessTokenPrincipalFactory.AmrValue(method))
            );
        }

        identity.AddClaim(new Claim(AccessTokenClaims.Scope, string.Join(' ', scopes)));

        var principal = new ClaimsPrincipal(identity);

        principal.SetAudiences(AccessTokenPolicy.Audience);
        principal.SetPresenters(client.ClientId);
        principal.SetScopes(scopes);

        return AccessTokenPrincipalFactory.AppendIdentityTokenOnly(principal, user.Email, user.DisplayName);
    }

    /// <summary>
    ///     Exchanges a validated authorization code for a token session and the access-token
    ///     principal.
    /// </summary>
    /// <param name="code">The code's principal, as OpenIddict decrypted it.</param>
    /// <param name="client">The registration the request resolved to.</param>
    /// <param name="context">What the host knows about the request — the device label and the address.</param>
    /// <param name="cancellationToken">The request's token.</param>
    /// <returns>
    ///     The principal to sign in with, or <see cref="ErrorCode.AuthorizationFailed" /> carrying
    ///     <see cref="SessionRevokedDescription" /> — the endpoint answers <c>invalid_grant</c>.
    /// </returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ The interactive session is read first and the token session opened second, so a
    ///         code minted before a sign-out is refused rather than turned into a session that
    ///         outlives the sign-in it came from. The token session is tracked on the user
    ///         (<see cref="IUserGrain.TrackSessionAsync" />) like any other, so "sign out everywhere"
    ///         reaches it.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             The code is burnt between the two, and the order of the three grain calls is the
    ///             whole of RFC 6749 § 4.1.2.
    ///         </b> The token session's id is minted here, before anything
    ///         is written; <see cref="IAuthorizationCodeGrain.ConsumeAsync" /> records it against
    ///         the code's <c>jti</c> in one grain turn; only then is the session opened under that
    ///         id. A second exchange of the same code — a replay from a log, a <c>Referer</c>, or a
    ///         client that retried its callback — finds the record, is refused with
    ///         <see cref="CodeReplayedDescription" />, and revokes the session the record names
    ///         with <see cref="RevocationReason.AuthorizationCodeReuseDetected" />, whether or not
    ///         the first exchange has finished opening it (<c>SessionGrain.OpenAsync</c> refuses to
    ///         open over a revocation). After the interactive-session check rather than before, so
    ///         a code whose sign-in is gone is refused without a write; before the open, so a replay
    ///         can never race a second session into existence.
    ///         <c>GrantsOverHttpTests.AReplayedCodeIsRefusedAndRevokesTheSessionTheFirstExchangeOpened</c>.
    ///     </para>
    ///     <para>
    ///         The code's id is <see cref="DegradedModeHandlers.StampAuthorizationCodeId" />'s. A
    ///         code without one — minted by a host that predates the stamp, inside its five minutes
    ///         — is refused, because a code this host cannot burn is a code it must not exchange.
    ///     </para>
    /// </remarks>
    public async Task<Result<ClaimsPrincipal>> MintForCodeAsync(
        ClaimsPrincipal code,
        ApplicationRegistration client,
        SignInContext context,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(context);

        if (Read(code) is not { } facts) {
            return Refused(
                Guid.Empty,
                OpenIddictConstants.GrantTypes.AuthorizationCode,
                Guid.Empty,
                "code-missing-claims",
                SessionRevokedDescription
            );
        }

        cancellationToken.ThrowIfCancellationRequested();

        var tenant = grains.ForTenant(TenantHint.Qualifier(facts.TenantId));

        var interactive = await tenant.GetGrain<ISessionGrain>(GrainKeys.Session(facts.InteractiveSessionId))
            .GetAsync();

        if (interactive.TryGetError(out _) || !interactive.GetValueOrThrow().IsLive) {
            return Refused(
                facts.TenantId,
                OpenIddictConstants.GrantTypes.AuthorizationCode,
                facts.InteractiveSessionId,
                "interactive-session-not-live",
                SessionRevokedDescription
            );
        }

        var signedIn = interactive.GetValueOrThrow();

        // ⚠ The code's amr, not only the grain's: the grain recorded the first factor and the code
        // carries both — AuthorizeApi.WithCookieMethods says why — so the token session is opened
        // with the union and every access token on its chain names both factors.
        var methods = new List<AuthenticationMethod>(signedIn.Methods);

        foreach (var amr in code.FindAll(AccessTokenClaims.AuthenticationMethods)) {
            if (AccessTokenPrincipalFactory.MethodOf(amr.Value) is { } method && !methods.Contains(method)) {
                methods.Add(method);
            }
        }

        if (!Guid.TryParseExact(code.GetTokenId(), "N", out var codeId) || codeId == Guid.Empty) {
            return Refused(
                facts.TenantId,
                OpenIddictConstants.GrantTypes.AuthorizationCode,
                facts.InteractiveSessionId,
                "code-missing-id",
                SessionRevokedDescription
            );
        }

        var tokenSessionId = Guid.NewGuid();

        // ⚠ Burn the code under the session id BEFORE the session exists — the type's remarks say
        // why the order is the mechanism. The expiry is the code's own, so the record lives exactly
        // as long as OpenIddict would accept the code, plus the grain's skew grace.
        var consumed = await tenant
            .GetGrain<IAuthorizationCodeGrain>(GrainKeys.AuthorizationCode(codeId))
            .ConsumeAsync(
                tokenSessionId,
                code.GetExpirationDate() ?? clock.UtcNow + AccessTokenPolicy.AuthorizationCodeLifetime
            );

        if (consumed.TryGetError(out var notConsumed)) {
            return Refused(
                facts.TenantId,
                OpenIddictConstants.GrantTypes.AuthorizationCode,
                facts.InteractiveSessionId,
                notConsumed.Message,
                SessionRevokedDescription
            );
        }

        if (!consumed.GetValueOrThrow().FirstUse) {
            var firstSession = consumed.GetValueOrThrow().TokenSessionId;

            await tenant.GetGrain<ISessionGrain>(GrainKeys.Session(firstSession))
                .RevokeAsync(RevocationReason.AuthorizationCodeReuseDetected);

            GrantLog.AuthorizationCodeReplayed(logger, facts.TenantId, facts.UserId, codeId, firstSession);

            return Refused(
                facts.TenantId,
                OpenIddictConstants.GrantTypes.AuthorizationCode,
                firstSession,
                "code-replayed",
                CodeReplayedDescription
            );
        }

        var opened = await tenant
            .GetGrain<ISessionGrain>(GrainKeys.Session(tokenSessionId))
            .OpenAsync(
                facts.UserId,
                client.ClientId,
                context.DeviceLabel,
                CredentialDigest.AddressDigest(context.ClientAddress),
                methods
            );

        if (opened.TryGetError(out var failed)) {
            return Refused(
                facts.TenantId,
                OpenIddictConstants.GrantTypes.AuthorizationCode,
                tokenSessionId,
                failed.Message,
                SessionRevokedDescription
            );
        }

        await tenant.GetGrain<IUserGrain>(GrainKeys.User(facts.UserId)).TrackSessionAsync(tokenSessionId);

        GrantLog.TokenSessionOpened(logger, facts.TenantId, facts.UserId, tokenSessionId, facts.InteractiveSessionId);

        var session = new SessionDescriptor {
            SessionId = tokenSessionId,
            UserId = facts.UserId,
            TenantId = facts.TenantId,
            ClientId = client.ClientId,
            AuthenticatedAt = facts.AuthenticatedAt,
            Methods = methods
        };

        return Result<ClaimsPrincipal>.Success(
            Assemble(session, facts, opened.GetValueOrThrow().Handle, OpenIddictConstants.GrantTypes.AuthorizationCode)
        );
    }

    /// <summary>
    ///     Rotates a token session's chain and mints the next access token.
    /// </summary>
    /// <param name="refresh">The refresh token's principal, as OpenIddict decrypted it.</param>
    /// <param name="client">The registration the request resolved to.</param>
    /// <param name="cancellationToken">The request's token.</param>
    /// <returns>
    ///     The principal to sign in with, or <see cref="ErrorCode.AuthorizationFailed" /> with one of
    ///     the two sentences — the endpoint answers <c>invalid_grant</c> and clears the cookie.
    /// </returns>
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             The interactive session is checked before the chain is touched, and a dead one
    ///             revokes the token session on the spot.
    ///         </b> A person who signed out — or whose session
    ///         an administrator revoked — must not be refreshed back into the API by a portal tab
    ///         that still holds a cookie. Revoking the token session here rather than leaving it to
    ///         expire means a stolen refresh token stops working at the same moment.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Rotation is the grain's and the reuse rule is the grain's.</b>
    ///         <see cref="ISessionGrain.RefreshAsync" /> retires the presented handle and mints the
    ///         next, and answers a retired handle by revoking the whole chain — this method only
    ///         carries the answer. It is called last, after everything that could still refuse the
    ///         request, because a rotation followed by a refusal would leave the client holding a
    ///         retired generation and its next honest refresh would read as a replay.
    ///     </para>
    /// </remarks>
    public async Task<Result<ClaimsPrincipal>> MintForRefreshAsync(
        ClaimsPrincipal refresh,
        ApplicationRegistration client,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(refresh);
        ArgumentNullException.ThrowIfNull(client);

        if (Read(refresh) is not { } facts
            || facts.TokenSessionId is null
            || string.IsNullOrEmpty(facts.RefreshHandle)) {
            return Refused(
                Guid.Empty,
                OpenIddictConstants.GrantTypes.RefreshToken,
                Guid.Empty,
                "refresh-token-missing-claims",
                RefreshRejectedDescription
            );
        }

        cancellationToken.ThrowIfCancellationRequested();

        var tenant = grains.ForTenant(TenantHint.Qualifier(facts.TenantId));
        var tokenSession = tenant.GetGrain<ISessionGrain>(GrainKeys.Session(facts.TokenSessionId.Value));

        var interactiveLive = await tenant.GetGrain<ISessionGrain>(GrainKeys.Session(facts.InteractiveSessionId))
            .IsLiveAsync();

        if (interactiveLive.TryGetError(out _) || !interactiveLive.GetValueOrThrow()) {
            await tokenSession.RevokeAsync(RevocationReason.SignOut);

            return Refused(
                facts.TenantId,
                OpenIddictConstants.GrantTypes.RefreshToken,
                facts.TokenSessionId.Value,
                "interactive-session-not-live",
                SessionRevokedDescription
            );
        }

        var described = await tokenSession.GetAsync();

        if (described.TryGetError(out _)) {
            return Refused(
                facts.TenantId,
                OpenIddictConstants.GrantTypes.RefreshToken,
                facts.TokenSessionId.Value,
                "token-session-unknown",
                RefreshRejectedDescription
            );
        }

        var rotated = await tokenSession.RefreshAsync(facts.RefreshHandle);

        if (rotated.TryGetError(out var refused)) {
            return Refused(
                facts.TenantId,
                OpenIddictConstants.GrantTypes.RefreshToken,
                facts.TokenSessionId.Value,
                refused.Message,
                RefreshRejectedDescription
            );
        }

        var session = described.GetValueOrThrow() with { AuthenticatedAt = facts.AuthenticatedAt };

        return Result<ClaimsPrincipal>.Success(
            Assemble(session, facts, rotated.GetValueOrThrow().Handle, OpenIddictConstants.GrantTypes.RefreshToken)
        );
    }

    // ── Internals ──────────────────────────────────────────────────────────────────────────────

    /// <summary>What a code or a refresh token says, read back off its principal.</summary>
    sealed record Facts(
        Guid TenantId,
        Guid UserId,
        Guid InteractiveSessionId,
        Guid? TokenSessionId,
        DateTimeOffset AuthenticatedAt,
        IReadOnlyList<string> Scopes,
        string? RefreshHandle,
        string? Email,
        string? Name
    );

    /// <summary>
    ///     Reads the facts off a code or refresh principal, or <see langword="null" /> when one is
    ///     missing or malformed — a token this server did not mint in this shape.
    /// </summary>
    /// <remarks>
    ///     A code carries the interactive session as <c>sid</c>; a refresh token carries the token
    ///     session as <c>sid</c> and the interactive one as <c>cyc:isid</c>. The two are told apart
    ///     by the presence of the refresh handle.
    /// </remarks>
    static Facts? Read(ClaimsPrincipal principal) {
        if (!TryGuid(principal, AccessTokenClaims.TenantId, out var tenantId)
            || !TryGuid(principal, AccessTokenClaims.Subject, out var userId)
            || !TryGuid(principal, AccessTokenClaims.SessionId, out var sid)
            || !long.TryParse(
                principal.GetClaim(AccessTokenClaims.AuthenticationTime),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var authTime
            )) {
            return null;
        }

        var handle = principal.GetClaim(AccessTokenPrincipalFactory.RefreshHandleClaim);

        if (string.IsNullOrEmpty(handle)) {
            return new(
                tenantId,
                userId,
                sid,
                null,
                DateTimeOffset.FromUnixTimeSeconds(authTime),
                [.. principal.GetScopes()],
                null,
                principal.GetClaim(OpenIddictConstants.Claims.Email),
                principal.GetClaim(OpenIddictConstants.Claims.Name)
            );
        }

        return TryGuid(principal, AccessTokenPrincipalFactory.InteractiveSessionClaim, out var isid)
            ? new(
                tenantId,
                userId,
                isid,
                sid,
                DateTimeOffset.FromUnixTimeSeconds(authTime),
                [.. principal.GetScopes()],
                handle,
                principal.GetClaim(OpenIddictConstants.Claims.Email),
                principal.GetClaim(OpenIddictConstants.Claims.Name)
            )
            : null;
    }

    static bool TryGuid(ClaimsPrincipal principal, string type, out Guid value) =>
        Guid.TryParseExact(principal.GetClaim(type), "N", out value);

    /// <summary>
    ///     The access-token principal for a token session — the factory's closed set, then the
    ///     refresh-only and id_token-only claims beside it.
    /// </summary>
    ClaimsPrincipal Assemble(SessionDescriptor session, Facts facts, string handle, string grantType) {
        var principal = AccessTokenPrincipalFactory.Build(
            session,
            AccessTokenPolicy.Audience,
            facts.Scopes,
            SubjectTypes.User
        );

        principal.SetAudiences(AccessTokenPolicy.Audience);
        principal.SetPresenters(session.ClientId);
        principal.SetScopes(facts.Scopes);

        AccessTokenPrincipalFactory.AppendRefreshOnly(principal, handle, facts.InteractiveSessionId);
        AccessTokenPrincipalFactory.AppendIdentityTokenOnly(
            principal,
            facts.Email ?? string.Empty,
            facts.Name ?? string.Empty
        );

        IdentityLog.TokenIssued(logger, session.TenantId, SubjectTypes.User, session.UserId, grantType);

        return principal;
    }

    Result<ClaimsPrincipal> Refused(
        Guid tenantId,
        string grantType,
        Guid sessionId,
        string reason,
        string description
    ) {
        GrantLog.GrantRefused(logger, tenantId, grantType, sessionId, reason);

        return Result<ClaimsPrincipal>.Failure(ErrorCode.AuthorizationFailed, description);
    }

    Result<ServicePrincipalDescriptor> Refuse(Guid tenantId, string reason) {
        IdentityLog.TokenRequestRefused(logger, tenantId, OpenIddictConstants.GrantTypes.ClientCredentials, reason);

        return Result<ServicePrincipalDescriptor>.Failure(ErrorCode.AuthorizationFailed, InvalidClientDescription);
    }

    static string N(Guid value) => value.ToString("N", CultureInfo.InvariantCulture);
}
