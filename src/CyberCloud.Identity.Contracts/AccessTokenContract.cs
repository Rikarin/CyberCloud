using System.Collections.Frozen;

namespace CyberCloud.Identity.Contracts;

/// <summary>
///     The claims a Cyber Cloud access token carries, and — more importantly — the ones it does not.
///     docs/plan/11 § Protocol.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/11 § Protocol, verbatim: <i>"<c>aud</c> names the API, <c>tid</c> the tenant,
///         <c>sub</c> the GUID, plus <c>scp</c>, <c>azp</c>, and an <c>auth_time</c>/<c>amr</c> pair
///         so step-up authentication can be required for sensitive actions."</i> That sentence is
///         this class.
///     </para>
///     <para>
///         ⚠ <b>NO ROLE OR PERMISSION CLAIM APPEARS HERE, AND ADDING ONE IS A DESIGN CHANGE RATHER
///         THAN A FEATURE.</b> docs/plan/11 § Protocol: they "are looked up per request from ReBAC.
///         Putting role claims in a 10-minute token means a revoke takes up to 10 minutes, and
///         packing a large user's groups into a JWT produces the header-size failures every large
///         enterprise hits." Both halves are real and they fail differently — the first is a security
///         bug that is invisible until an incident, the second is an outage that arrives the day a
///         customer's admin joins their fortieth group. <see cref="ForbiddenClaims" /> is the
///         checked list and <c>NoRolesInTokenTests</c> is what keeps it true.
///     </para>
/// </remarks>
public static class AccessTokenClaims {
    /// <summary>The subject — the user's or service principal's GUID, <c>N</c> form.</summary>
    public const string Subject = "sub";

    /// <summary>The tenant. Every Cyber Cloud token is scoped to exactly one.</summary>
    public const string TenantId = "tid";

    /// <summary>Who the token is for — the API that will accept it.</summary>
    public const string Audience = "aud";

    /// <summary>The issuer — the identity host's origin.</summary>
    public const string Issuer = "iss";

    /// <summary>The granted scopes, space-separated, OAuth's <c>scope</c> under Azure's name.</summary>
    public const string Scope = "scp";

    /// <summary>The authorized party — the <c>client_id</c> the token was issued to.</summary>
    public const string AuthorizedParty = "azp";

    /// <summary>
    ///     When the subject last actually authenticated, as seconds since the epoch.
    /// </summary>
    /// <remarks>
    ///     ⚠ Not the same as <c>iat</c>, and the difference is the whole point. A refresh mints a new
    ///     token with a new <c>iat</c> and <b>carries the original <c>auth_time</c> forward</b>, so a
    ///     step-up rule that says "re-authenticate if it has been more than five minutes" cannot be
    ///     defeated by refreshing. Recomputing it on refresh would make step-up decorative.
    /// </remarks>
    public const string AuthenticationTime = "auth_time";

    /// <summary>How the subject authenticated — the <see cref="AuthenticationMethod" /> values.</summary>
    public const string AuthenticationMethods = "amr";

    /// <summary>The session this token belongs to, so revocation has something to name.</summary>
    public const string SessionId = "sid";

    /// <summary>Issued at.</summary>
    public const string IssuedAt = "iat";

    /// <summary>Expires at.</summary>
    public const string ExpiresAt = "exp";

    /// <summary>The token's own id.</summary>
    public const string TokenId = "jti";

    /// <summary>Every claim a Cyber Cloud access token may carry. The set is closed.</summary>
    public static FrozenSet<string> Permitted { get; } = new[] {
        Subject, TenantId, Audience, Issuer, Scope, AuthorizedParty,
        AuthenticationTime, AuthenticationMethods, SessionId, IssuedAt, ExpiresAt, TokenId
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    ///     Claim names that must never appear in an access token, whatever they are spelled.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is a list of the spellings, not of the concepts</b>, because the concept
    ///         arrives under whichever name the library that added it happens to use. All six of
    ///         these are what a role claim is called somewhere in the .NET, Azure or OIDC ecosystem:
    ///         <c>role</c> and <c>roles</c> (Entra), <c>groups</c> (Entra and Okta),
    ///         <c>permissions</c> (Auth0), <c>scope</c> spelled out (which we carry as <c>scp</c>,
    ///         and carrying both would be two sources of truth), and the full
    ///         <c>http://schemas.microsoft.com/ws/2008/06/identity/claims/role</c> URI that
    ///         <c>ClaimsIdentity.RoleClaimType</c> defaults to and that
    ///         <see cref="System.Security.Claims.ClaimsPrincipal.IsInRole" /> reads.
    ///     </para>
    ///     <para>
    ///         The last one is the trap: a developer who writes
    ///         <c>identity.AddClaim(ClaimTypes.Role, "Owner")</c> gets that URI without ever typing
    ///         it, and the resulting token looks fine until somebody notices a revoke takes ten
    ///         minutes.
    ///     </para>
    /// </remarks>
    public static FrozenSet<string> ForbiddenClaims { get; } = new[] {
        "role",
        "roles",
        "groups",
        "permissions",
        "scope",
        "http://schemas.microsoft.com/ws/2008/06/identity/claims/role"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
///     The properties of an issued access token that the gateway relies on, stated once so both
///     sides can assert them. docs/plan/11 § Protocol, § Sessions and revocation.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This type exists because the gateway is built against it by a different person on a
///         different branch.</b> Everything the gateway needs to validate a Cyber Cloud token — the
///         algorithm, the discovery path, the lifetime it may assume, and the fact that it must
///         <i>not</i> introspect — is here rather than in prose, so a change to any of it is a
///         compile-time event on both sides rather than a message somebody missed.
///     </para>
/// </remarks>
public static class AccessTokenPolicy {
    /// <summary>
    ///     Ten minutes. docs/plan/11 § Protocol.
    /// </summary>
    /// <remarks>
    ///     ⚠ This number <b>is</b> the revocation story. docs/plan/11 § Sessions and revocation:
    ///     "access tokens are not revocable and are not made so. They live 10 minutes." Lengthening
    ///     it widens the window in which a revoked grant still works; shortening it multiplies
    ///     refresh traffic against the identity host. Anything that genuinely cannot tolerate ten
    ///     minutes of stale authorization uses a <c>FullyConsistent</c> ReBAC check instead, which
    ///     is the right place for that guarantee because it is about authorization rather than
    ///     authentication.
    /// </remarks>
    public static TimeSpan AccessTokenLifetime { get; } = TimeSpan.FromMinutes(10);

    /// <summary>
    ///     Fourteen days of sliding refresh lifetime, rotated on every use.
    /// </summary>
    public static TimeSpan RefreshTokenLifetime { get; } = TimeSpan.FromDays(14);

    /// <summary>
    ///     Ninety days, after which a session dies however active it has been.
    /// </summary>
    /// <remarks>
    ///     ⚠ Without an absolute cap a rotating refresh chain is immortal: every use extends it, so a
    ///     stolen chain that is exercised daily never expires on its own. This is the backstop that
    ///     makes "the user will eventually have to sign in again" true.
    /// </remarks>
    public static TimeSpan AbsoluteSessionLifetime { get; } = TimeSpan.FromDays(90);

    /// <summary>
    ///     Thirty days between signing-key rotations, with both keys published for sixty.
    ///     docs/plan/11 § Protocol.
    /// </summary>
    public static TimeSpan SigningKeyRotation { get; } = TimeSpan.FromDays(30);

    /// <summary>How long a retired signing key stays in the published key set.</summary>
    public static TimeSpan SigningKeyOverlap { get; } = TimeSpan.FromDays(60);

    /// <summary>
    ///     <see langword="false" />, and it is a decision rather than a limitation.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The gateway must validate tokens locally and must not call this host per request.</b>
    ///     docs/plan/11 § Sessions and revocation: "an introspection call per request would put the
    ///     identity system on the hot path of every request, which is precisely what a short token is
    ///     for." An introspection endpoint that exists gets used, so the correct implementation is
    ///     not to publish one for the resource path at all.
    /// </remarks>
    public static bool SupportsIntrospection => false;

    /// <summary>
    ///     <see langword="false" />. An issued access token cannot be recalled; the mitigation is
    ///     that it expires in <see cref="AccessTokenLifetime" />.
    /// </summary>
    public static bool AccessTokensAreRevocable => false;

    /// <summary>The OIDC discovery document, relative to the identity host's origin.</summary>
    public const string DiscoveryPath = "/.well-known/openid-configuration";

    /// <summary>The JSON Web Key Set the gateway fetches its validation keys from.</summary>
    public const string JsonWebKeySetPath = "/.well-known/jwks";

    /// <summary>
    ///     The only signing algorithm. ⚠ A validator that accepts a <i>set</i> of algorithms accepts
    ///     the weakest one in it, and the historical <c>alg: none</c> and RS256-key-as-HMAC-secret
    ///     confusions both needed a validator willing to be told which algorithm to use.
    /// </summary>
    public const string SigningAlgorithm = "ES256";
}
