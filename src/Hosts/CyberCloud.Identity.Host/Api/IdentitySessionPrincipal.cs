using CyberCloud.Authorization.Contracts;
using CyberCloud.Identity.Contracts;
using System.Globalization;
using System.Security.Claims;

namespace CyberCloud.Identity.Host.Api;

/// <summary>
///     Builds the principal the session cookie carries, and reads it back.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This is not the access-token principal and must not become it.</b>
///         <c>AccessTokenPrincipalFactory</c> builds a closed set of fourteen claims that leaves the
///         cluster on a bearer token; this builds the <i>interactive</i> session, which never leaves
///         this origin. They share four claim <i>names</i> — <c>sub</c>, <c>sid</c>, <c>tid</c>,
///         <c>amr</c> — deliberately, so an operator reading a cookie dump and a token dump is
///         reading the same vocabulary. They do not share a builder, because the token's claim set is
///         an allow-list <c>NoRolesInTokenTests</c> asserts and this one carries a claim
///         (<see cref="SecondFactorClaim" />) that must never appear in a token.
///     </para>
///     <para>
///         ⚠ <b><see cref="SecondFactorClaim" /> is the reason a partial sign-in is representable at
///         all.</b> <c>SignInService</c> opens the session as soon as the first factor verifies, and
///         sets <see cref="SignInOutcome.SecondFactorRequired" /> for everything but a passkey. The
///         cookie issued at that moment has to be usable by <c>/api/signin/totp</c> — it is how that
///         endpoint knows who is answering — and unusable for anything else. A second cookie would be
///         a second credential on the same origin with its own expiry and its own bugs; a claim on
///         the one cookie is checked wherever the session is read.
///     </para>
///     <para>
///         ⚠ <b>Fails closed on a missing claim.</b> <see cref="IsFullyAuthenticated" /> requires
///         <see cref="Satisfied" /> to be present rather than treating an absent claim as "no second
///         factor was needed". A cookie minted by an older build, or by a path somebody adds and
///         forgets to stamp, is therefore a cookie that cannot complete an authorization request —
///         which is a re-prompt, and the alternative is a partial session that silently counts as
///         whole.
///     </para>
/// </remarks>
public static class IdentitySessionPrincipal {
    /// <summary>
    ///     Whether the second factor has been presented. ⚠ Host-local; never in an access token.
    /// </summary>
    /// <remarks>
    ///     The <c>cyc:</c> prefix keeps it out of the namespace of registered JWT claim names, so it
    ///     cannot collide with something OpenIddict or a future protocol extension means to own.
    /// </remarks>
    public const string SecondFactorClaim = "cyc:2fa";

    /// <summary>The second factor is still owed. The session may not be used.</summary>
    public const string Pending = "pending";

    /// <summary>The second factor was presented, or none was owed.</summary>
    public const string Satisfied = "satisfied";

    /// <summary>
    ///     Builds the principal for a session.
    /// </summary>
    /// <param name="tenantId">Which tenant.</param>
    /// <param name="outcome">What the sign-in produced.</param>
    /// <returns>A principal for <see cref="IdentityHostAuthentication.SchemeName" />.</returns>
    public static ClaimsPrincipal Build(Guid tenantId, SignInOutcome outcome) {
        ArgumentNullException.ThrowIfNull(outcome);

        // ⚠ The authentication TYPE argument is what makes Identity.IsAuthenticated true. A
        // ClaimsIdentity built with the parameterless constructor is an anonymous identity carrying
        // claims, which authorizes nothing and produces a 401 that looks like a missing cookie.
        var identity = new ClaimsIdentity(IdentityHostAuthentication.SchemeName);

        identity.AddClaim(new(AccessTokenClaims.Subject, N(outcome.UserId)));
        identity.AddClaim(new(AccessTokenClaims.SessionId, N(outcome.SessionId)));
        identity.AddClaim(new(AccessTokenClaims.TenantId, N(tenantId)));
        identity.AddClaim(new(AccessTokenClaims.SubjectType, SubjectTypes.User));
        identity.AddClaim(
            new(AccessTokenClaims.AuthenticationMethods, AuthenticationMethodNames.Of(outcome.Method))
        );
        identity.AddClaim(
            new(SecondFactorClaim, outcome.SecondFactorRequired ? Pending : Satisfied)
        );

        return new(identity);
    }

    /// <summary>
    ///     Re-stamps a principal as having satisfied its second factor, keeping everything else.
    /// </summary>
    /// <param name="principal">The pending session's principal, from the cookie.</param>
    /// <param name="method">The factor that was presented, appended to <c>amr</c>.</param>
    /// <returns>A new principal. ⚠ The argument is not mutated.</returns>
    public static ClaimsPrincipal Promote(ClaimsPrincipal principal, AuthenticationMethod method) {
        ArgumentNullException.ThrowIfNull(principal);

        var identity = new ClaimsIdentity(IdentityHostAuthentication.SchemeName);

        // Everything except the two claims this call is here to change.
        identity.AddClaims(
            principal.Claims.Where(
                x => !string.Equals(x.Type, SecondFactorClaim, StringComparison.Ordinal)
                    && !string.Equals(x.Type, AccessTokenClaims.AuthenticationMethods, StringComparison.Ordinal)
            )
        );

        // ⚠ Both methods, not just the second. `amr` is a list of what was presented, and an audit
        // trail that recorded only "totp" would lose the fact that a password was the first factor.
        foreach (var name in principal.FindAll(AccessTokenClaims.AuthenticationMethods)) {
            identity.AddClaim(new(AccessTokenClaims.AuthenticationMethods, name.Value));
        }

        identity.AddClaim(new(AccessTokenClaims.AuthenticationMethods, AuthenticationMethodNames.Of(method)));
        identity.AddClaim(new(SecondFactorClaim, Satisfied));

        return new(identity);
    }

    /// <summary>
    ///     Whether this principal is a session that may be used.
    /// </summary>
    /// <param name="principal">The principal from the cookie, or <see langword="null" />.</param>
    /// <returns>
    ///     <see langword="true" /> only for an authenticated principal explicitly stamped
    ///     <see cref="Satisfied" />. See the ⚠ block on the type for why an absent claim is a
    ///     <see langword="false" />.
    /// </returns>
    public static bool IsFullyAuthenticated(ClaimsPrincipal? principal) =>
        principal?.Identity?.IsAuthenticated == true
        && string.Equals(
            principal.FindFirst(SecondFactorClaim)?.Value,
            Satisfied,
            StringComparison.Ordinal
        );

    /// <summary>The user this principal names, or <see langword="null" /> when it names none.</summary>
    /// <param name="principal">The principal from the cookie, or <see langword="null" />.</param>
    public static Guid? UserId(ClaimsPrincipal? principal) =>
        principal?.Identity?.IsAuthenticated == true
        && Guid.TryParse(principal.FindFirst(AccessTokenClaims.Subject)?.Value, out var id)
            ? id
            : null;

    /// <summary>The session this principal names, or <see langword="null" />.</summary>
    /// <param name="principal">The principal from the cookie, or <see langword="null" />.</param>
    public static Guid? SessionId(ClaimsPrincipal? principal) =>
        principal?.Identity?.IsAuthenticated == true
        && Guid.TryParse(principal.FindFirst(AccessTokenClaims.SessionId)?.Value, out var id)
            ? id
            : null;

    static string N(Guid value) => value.ToString("N", CultureInfo.InvariantCulture);
}

/// <summary>
///     How an <see cref="AuthenticationMethod" /> is spelled in an <c>amr</c> claim.
/// </summary>
/// <remarks>
///     ⚠ RFC 8176's registered values where one exists — <c>pwd</c>, <c>otp</c>, <c>swk</c> — rather
///     than the enum member's name. <c>amr</c> is a registered claim with a registered vocabulary,
///     and emitting <c>"Password"</c> into it produces a token that is syntactically fine and means
///     nothing to any other implementation that reads it.
/// </remarks>
public static class AuthenticationMethodNames {
    static readonly Dictionary<AuthenticationMethod, string> Names = new() {
        [AuthenticationMethod.None] = "none",
        [AuthenticationMethod.Password] = "pwd",
        // RFC 8176 has no passkey value; `swk` ("proof-of-possession of a software key") is what
        // WebAuthn deployments converged on, and it is at least registered.
        [AuthenticationMethod.Passkey] = "swk",
        [AuthenticationMethod.Totp] = "otp",
        // ⚠ Not a registered value. RFC 8176 has none for a recovery code, and mapping it onto `otp`
        // would make a burnt single-use code indistinguishable from a TOTP in an audit trail — which
        // is the one place the difference matters, because burning one is an auditable event.
        [AuthenticationMethod.RecoveryCode] = "rc",
        [AuthenticationMethod.ClientCredential] = "swk"
    };

    /// <summary>The <c>amr</c> spelling of <paramref name="method" />.</summary>
    /// <param name="method">The method.</param>
    public static string Of(AuthenticationMethod method) =>
        Names.TryGetValue(method, out var name) ? name : "none";
}
