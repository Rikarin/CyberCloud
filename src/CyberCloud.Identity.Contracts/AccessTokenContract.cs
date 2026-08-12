using CyberCloud.Authorization.Contracts;
using CyberCloud.Core;
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

    /// <summary>
    ///     The <b>type</b> of the subject <see cref="Subject" /> names — one of
    ///     <see cref="SubjectTypes" />.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A dedicated claim, and never a prefix convention on <c>sub</c>.</b> The gateway
    ///         states the requirement in <c>ICallerContextResolver</c>'s remarks, item 4, and the
    ///         reason is docs/plan/07 § The model: ReBAC subjects are <i>typed</i>, so
    ///         <c>user:abc</c> and <c>servicePrincipal:abc</c> are two different subjects that happen
    ///         to share an id. Without this claim the gateway cannot build a correct <c>SubjectRef</c>
    ///         and every <c>Check</c> is made against a guess — which fails <i>open</i> exactly when
    ///         two subject types collide on one GUID, and is invisible until they do.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Why not <c>sub = "user:abc"</c>.</b> A prefix convention makes the type a
    ///         substring of a value that is also a database key, an audit field and a log line; every
    ///         consumer then needs the same splitting rule, one of them gets it wrong for an id
    ///         containing a colon, and the failure is a subject silently reinterpreted as a different
    ///         one. <c>ThereIsNoTypeInsideSubAlone</c> in <c>AccessTokenContractTests</c> is the
    ///         assertion that <c>sub</c> on its own cannot be parsed into a typed subject, so nobody
    ///         can reintroduce the convention by reading one.
    ///     </para>
    ///     <para>
    ///         The spelling is <c>sub_typ</c> rather than Entra's <c>idtyp</c>: neither is IANA
    ///         registered, and one that reads as "the type of <c>sub</c>" is the one that cannot be
    ///         mistaken for something about the <i>token</i>'s type.
    ///     </para>
    /// </remarks>
    public const string SubjectType = "sub_typ";

    /// <summary>
    ///     The platform operator behind an impersonated request, as a user GUID in <c>N</c> form.
    ///     Absent on every ordinary token. docs/plan/06 § Platform administration.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>MINTED HERE AND NEVER ACCEPTED FROM A HEADER. THIS IS THE WHOLE SECURITY
    ///         PROPERTY.</b> docs/plan/06 § Platform administration builds impersonation out of four
    ///         controls — a second operator's approval for a production tenant, a 60-minute box, an
    ///         audit record, and <i>"the tenant sees a notification"</i>. Every one of those is
    ///         defeated by a caller who can set the value themselves: the approval is skipped, the box
    ///         is unbounded, the audit names whoever the attacker typed, and the notification either
    ///         never fires or accuses the wrong operator. A claim inside a signed token is the only
    ///         form of this value that carries the approval it was granted under.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>docs/plan/06 § Platform administration says "header", and that sentence is a doc
    ///         defect.</b> It reads: "every request made under it carries an
    ///         <c>X-CyberCloud-Impersonated-By</c> header into the audit log". A header is
    ///         caller-controlled on every request the gateway serves, so implementing that sentence
    ///         literally would let any caller impersonate any operator in the audit trail. The header
    ///         is the right shape <i>internally</i> — gateway to resource manager, on a hop the caller
    ///         cannot reach, which is what <c>CallerContext.ImpersonatedBy</c> already is — and the
    ///         wrong shape at the edge. This claim is where the value enters the system;
    ///         <c>ICallerContextResolver</c>'s item 5 states the same rule from the other side.
    ///     </para>
    ///     <para>
    ///         The spelling is the flattened form of RFC 8693 § 4.1's <c>act</c> claim, whose value is
    ///         a JSON object with a nested <c>sub</c>. A claims principal carries flat string claims
    ///         (see <c>AccessTokenPrincipalFactory</c>), and a nested object would need a second
    ///         serialization path for one value; <c>act_sub</c> <i>is</i> that nested <c>sub</c>,
    ///         spelled flat, so a reader of RFC 8693 recognises the concept and its meaning is
    ///         unchanged. ⚠ There is deliberately no <c>act</c> claim as well — two spellings of one
    ///         fact is how a consumer ends up reading the one that was not populated.
    ///     </para>
    /// </remarks>
    public const string ImpersonatedBy = "act_sub";

    /// <summary>
    ///     Every claim a Cyber Cloud access token may carry. <b>The set is closed and stays closed.</b>
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Fourteen, and adding the last two did not make this an open set.</b>
    ///     <see cref="SubjectType" /> and <see cref="ImpersonatedBy" /> were added because the gateway
    ///     cannot do its job without them, and each arrived as an entry in this list plus a line in
    ///     the factory that emits it plus an assertion on both sides — which is the cost the closure
    ///     exists to impose. <c>ThePermittedSetIsClosedAndNotMerelyLong</c> holds the count and the
    ///     exact membership, so a fifteenth claim is a test failure rather than a diff nobody read.
    /// </remarks>
    public static FrozenSet<string> Permitted { get; } = new[] {
        Subject, TenantId, Audience, Issuer, Scope, AuthorizedParty,
        AuthenticationTime, AuthenticationMethods, SessionId, IssuedAt, ExpiresAt, TokenId,
        SubjectType, ImpersonatedBy
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

    /// <summary>
    ///     Whether every one of <paramref name="claimTypes" /> is in <see cref="Permitted" />, and a
    ///     failure naming the first that is not.
    /// </summary>
    /// <param name="claimTypes">The claim types about to be put in a token.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>An allow-list check, not a strip.</b> The tempting shape is "remove anything
    ///         forbidden and carry on", and it is wrong twice: it only removes the spellings somebody
    ///         thought of (<see cref="ForbiddenClaims" /> is a list of <i>spellings</i>, and the next
    ///         library will invent a seventh), and it turns a mistake into a silent success — the
    ///         claim the caller added is gone and nothing says so. Refusing means a token that was
    ///         built wrongly is never issued at all.
    ///     </para>
    ///     <para>
    ///         The forbidden set is checked first only so the message can say <i>why</i> a role claim
    ///         in particular is refused. Anything outside <see cref="Permitted" /> is refused
    ///         regardless of whether it is on that list, which is what keeps the set closed against
    ///         claims nobody has thought of yet.
    ///     </para>
    /// </remarks>
    public static Result EnsurePermitted(IEnumerable<string> claimTypes) {
        ArgumentNullException.ThrowIfNull(claimTypes);

        foreach (var claimType in claimTypes) {
            if (Permitted.Contains(claimType)) {
                continue;
            }

            return ForbiddenClaims.Contains(claimType)
                ? Result.Failure(
                    ErrorCode.InternalError,
                    $"'{claimType}' is a role or permission claim and must never be in an access "
                    + "token. docs/plan/11 § Protocol: they are looked up per request from ReBAC, "
                    + "because a claim in a 10-minute token makes a revoke take up to 10 minutes and "
                    + "a large user's groups make the header too big."
                )
                : Result.Failure(
                    ErrorCode.InternalError,
                    $"'{claimType}' is not one of the {Permitted.Count} claims a Cyber Cloud access "
                    + "token may carry. The set is closed — docs/plan/11 § Protocol — so a new claim "
                    + "is an edit to AccessTokenClaims.Permitted and to the gateway that reads it, "
                    + "not a line in whatever is building this principal."
                );
        }

        return Result.Success;
    }
}

// ⚠ SubjectTypes IS NOT IN THIS FILE ANY MORE, AND THE FIX IT USED TO ASK FOR IS THE ONE THAT WAS
// APPLIED. Its own remarks read: "they are declared here rather than referenced from
// CyberCloud.Authorization's ObjectTypes because CyberCloud.Identity.Contracts must not depend on
// the authorization implementation assembly; ⚠ the duplication is owed a fix, which is to lift the
// subject types into CyberCloud.Authorization.Contracts where both sides already look." It now
// lives in CyberCloud.Authorization.Contracts/AuthorizationVocabulary.cs beside ObjectTypes,
// Relations and Permissions, and SubjectTypes.User is ObjectTypes.User rather than a second
// "user" — so the agreement the old SubjectTypesMatchTheReBacSpellings asserted is now partly
// structural. This assembly already referenced CyberCloud.Authorization.Contracts for SubjectRef,
// so nothing was added to reach it; what was removed is CyberCloud.Identity's ProjectReference on
// the authorization IMPLEMENTATION assembly, which existed for the other half of the same
// vocabulary.

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
