using CyberCloud.Authorization.Contracts;
using CyberCloud.Identity.Contracts;
using OpenIddict.Abstractions;
using System.Globalization;
using System.Security.Claims;

namespace CyberCloud.Identity.Host.Tokens;

/// <summary>
///     Builds the claims principal an access token is serialized from — and, more to the point,
///     leaves out the ones that must never be in it. docs/plan/11 § Protocol.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             NO ROLE OR PERMISSION CLAIM IS ADDED HERE, AND ADDING ONE LATER IS THE MISTAKE THIS
///             TYPE EXISTS TO MAKE HARD.
///         </b> docs/plan/11 § Protocol: roles and permissions "are looked
///         up per request from ReBAC. Putting role claims in a 10-minute token means a revoke takes
///         up to 10 minutes, and packing a large user's groups into a JWT produces the header-size
///         failures every large enterprise hits."
///     </para>
///     <para>
///         Both failures are worth keeping in mind because they arrive at different times and look
///         like different bugs. The revoke lag is invisible until an incident review asks why a
///         dismissed employee could still write for nine minutes. The header-size failure arrives the
///         day one customer's admin joins their fortieth group, affects only that customer, and
///         presents as intermittent <c>431</c>s from a proxy nobody has looked at.
///     </para>
///     <para>
///         ⚠ <b>The claim set is a closed allow-list, not a deny-list of things to strip.</b>
///         <see cref="Build" /> adds exactly the claims in
///         <see cref="AccessTokenClaims.Permitted" /> and has no path that adds anything else, so a
///         future edit that wants a role claim has to add a line here <i>and</i> widen that set — two
///         visible edits in two files rather than one plausible-looking line. <c>NoRolesInTokenTests</c>
///         asserts both directions, and <see cref="Build" /> checks its own output against that set
///         before returning, so the two cannot drift within one file either.
///     </para>
///     <para>
///         <b>Two of the fourteen claims exist for the gateway and nothing else</b>, and both were
///         missing while the two halves were built in parallel:
///         <see cref="AccessTokenClaims.SubjectType" />, without which a ReBAC <c>SubjectRef</c>
///         cannot be constructed and every <c>Check</c> is made against a guessed type; and
///         <see cref="AccessTokenClaims.ImpersonatedBy" />, without which docs/plan/06 § Platform
///         administration's impersonation cannot exist at all, because the operator behind a request
///         has nowhere trustworthy to be recorded. ⚠ The second is minted <i>here</i> and read from
///         no request surface anywhere — that is the whole of its security value.
///     </para>
///     <para>
///         ⚠
///         <b>
///             Every claim is marked for the access token, and a claim without the mark is not in
///             the JWT.
///         </b> OpenIddict serializes only the claims whose <i>destination</i> names the access
///         token and drops the rest without a word — which was invisible while nothing minted a
///         token, and would have shipped as a token carrying <c>iss</c>, <c>exp</c> and nothing the
///         gateway reads. The mark is set in one place, on every claim this type builds, so a
///         fifteenth claim cannot arrive unmarked. <c>NoRolesInTokenTests.EveryClaimIsMarkedForTheAccessToken</c>
///         is the assertion.
///     </para>
/// </remarks>
public static class AccessTokenPrincipalFactory {
    /// <summary>
    ///     Builds the principal for a user's access token.
    /// </summary>
    /// <param name="session">The session the token belongs to.</param>
    /// <param name="audience">The API that will accept the token.</param>
    /// <param name="scopes">The granted scopes.</param>
    /// <param name="subjectType">
    ///     What kind of subject <c>sub</c> names — one of <see cref="SubjectTypes" />.
    /// </param>
    /// <param name="impersonatedBy">
    ///     The platform operator behind an impersonated request, or <see cref="Guid.Empty" /> — which
    ///     is what every ordinary token passes, and which emits no claim at all.
    /// </param>
    /// <returns>A principal carrying only <see cref="AccessTokenClaims.Permitted" /> claim types.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><paramref name="subjectType" /> has no default, deliberately.</b> Defaulting it to
    ///         <see cref="SubjectTypes.User" /> would make the one call site that mints a service
    ///         principal's or a managed identity's token compile while emitting <c>user</c>, and the
    ///         resulting token names a subject the ReBAC store has never heard of — every check denies,
    ///         which reads as a permissions bug. A required parameter makes each caller say which of
    ///         the three it is.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             <paramref name="impersonatedBy" /> is a parameter of this factory and is never
    ///             read from a request.
    ///         </b> docs/plan/06 § Platform administration's controls — a second
    ///         operator's approval, a 60-minute box, the audit record, and the tenant's notification —
    ///         are all properties of the <i>grant</i>, and the only way a token can carry the grant it
    ///         was issued under is for the value to be minted here. See
    ///         <see cref="AccessTokenClaims.ImpersonatedBy" />.
    ///     </para>
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="subjectType" /> is not a subject type.</exception>
    /// <exception cref="InvalidOperationException">The built principal carries a claim outside the closed set.</exception>
    public static ClaimsPrincipal Build(
        SessionDescriptor session,
        string audience,
        IReadOnlyList<string> scopes,
        string subjectType,
        Guid impersonatedBy = default
    ) {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(scopes);

        return Assemble(
            session.UserId,
            subjectType,
            session.TenantId,
            session.SessionId,
            session.ClientId,
            audience,
            scopes,
            session.AuthenticatedAt,
            session.Methods,
            impersonatedBy
        );
    }

    /// <summary>
    ///     Builds the principal for a service principal's client-credentials token.
    ///     docs/plan/11 § Protocol's client-credentials row.
    /// </summary>
    /// <param name="principal">The service principal that presented a valid credential.</param>
    /// <param name="clientId">The <c>client_id</c> it presented, which becomes <c>azp</c>.</param>
    /// <param name="audience">The API that will accept the token.</param>
    /// <param name="scopes">The granted scopes.</param>
    /// <param name="authenticatedAt">
    ///     When the credential was verified — now, for a grant with no session behind it.
    /// </param>
    /// <returns>A principal carrying only <see cref="AccessTokenClaims.Permitted" /> claim types.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>No <c>sid</c>, and no impersonation.</b> A client-credentials grant opens no
    ///         session — there is no refresh chain to revoke and no device to list — so the token
    ///         names none rather than naming a fresh GUID that nothing could ever look up. And a
    ///         machine identity is never the subject of docs/plan/06 § Platform administration's
    ///         "view as tenant", so this path has no way to spell <c>act_sub</c> at all.
    ///     </para>
    ///     <para>
    ///         The subject type is <see cref="SubjectTypes.ServicePrincipal" /> and is not a
    ///         parameter: the caller has a <see cref="ServicePrincipalDescriptor" /> in hand, and a
    ///         parameter would only let it say something else.
    ///     </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">The built principal carries a claim outside the closed set.</exception>
    public static ClaimsPrincipal BuildForServicePrincipal(
        ServicePrincipalDescriptor principal,
        string clientId,
        string audience,
        IReadOnlyList<string> scopes,
        DateTimeOffset authenticatedAt
    ) {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(scopes);

        return Assemble(
            principal.ServicePrincipalId,
            SubjectTypes.ServicePrincipal,
            principal.TenantId,
            sessionId: null,
            clientId,
            audience,
            scopes,
            authenticatedAt,
            [AuthenticationMethod.ClientCredential],
            impersonatedBy: Guid.Empty
        );
    }

    /// <summary>
    ///     The one place a claim is added. Both public entry points are argument lists for this.
    /// </summary>
    static ClaimsPrincipal Assemble(
        Guid subjectId,
        string subjectType,
        Guid tenantId,
        Guid? sessionId,
        string clientId,
        string audience,
        IReadOnlyList<string> scopes,
        DateTimeOffset authenticatedAt,
        IReadOnlyList<AuthenticationMethod> methods,
        Guid impersonatedBy
    ) {
        if (SubjectTypes.Ensure(subjectType).TryGetError(out var invalid)) {
            throw new ArgumentException(invalid.Message, nameof(subjectType));
        }

        // ⚠ The RoleClaimType is set to a claim type this principal never carries, deliberately.
        // ClaimsIdentity defaults it to the Microsoft role URI, and ClaimsPrincipal.IsInRole reads
        // whatever it points at — so leaving the default would leave a working IsInRole that silently
        // answers "no" for everybody. Pointing it at a claim that cannot exist makes any code that
        // reaches for role-based authorization fail visibly in a test rather than quietly in
        // production, which is the outcome docs/plan/11 § Protocol wants: authorization is a ReBAC
        // Check, and there is nothing in the token to check against.
        var identity = new ClaimsIdentity(
            authenticationType: "CyberCloud.AccessToken",
            nameType: AccessTokenClaims.Subject,
            roleType: "urn:cybercloud:roles-are-not-in-the-token"
        );

        Add(identity, AccessTokenClaims.Subject, N(subjectId));

        // ⚠ The subject's TYPE, as its own claim rather than as a prefix on `sub`. The gateway builds
        // a ReBAC SubjectRef out of the pair, and docs/plan/07 § The model makes user:abc and
        // servicePrincipal:abc different subjects — so a token that carried only the id would make
        // every Check a guess. See AccessTokenClaims.SubjectType for why a `type:id` convention on
        // `sub` is the wrong answer even though it needs one fewer claim.
        Add(identity, AccessTokenClaims.SubjectType, subjectType);

        Add(identity, AccessTokenClaims.TenantId, N(tenantId));

        if (sessionId is { } session) {
            Add(identity, AccessTokenClaims.SessionId, N(session));
        }

        Add(identity, AccessTokenClaims.Audience, audience);
        Add(identity, AccessTokenClaims.AuthorizedParty, clientId);

        // Space-separated, which is what `scp` is on the wire. A repeated claim would serialize as a
        // JSON array, and half the validators in existence read `scp` as a string.
        Add(identity, AccessTokenClaims.Scope, string.Join(' ', scopes));

        // ⚠ auth_time comes from the SESSION and not from now. A refresh mints a new token with a new
        // `iat` and carries the original `auth_time` forward, so a step-up rule that says
        // "re-authenticate if it has been more than five minutes" cannot be defeated by refreshing.
        // Recomputing it here would make step-up decorative — see AccessTokenClaims.AuthenticationTime.
        //
        // ⚠ TYPED AS AN INTEGER, OR OPENIDDICT REFUSES THE WHOLE SIGN-IN. Its ValidateSignInDemand
        // checks the claim's ValueType and throws "The 'auth_time' claim present in the specified
        // principal is malformed or isn't of the expected type" for a plain string — a 500 from
        // /token on every grant, found by TenantOverHttpTests on the first token this host ever
        // minted. The value is the same digits either way; only the type tag differs, and no
        // assertion on the principal's values could have seen it.
        Add(
            identity,
            AccessTokenClaims.AuthenticationTime,
            authenticatedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            ClaimValueTypes.Integer64
        );

        foreach (var method in methods) {
            Add(identity, AccessTokenClaims.AuthenticationMethods, AmrValue(method));
        }

        // ⚠ Emitted only when there IS an impersonation, and only from this argument. An empty claim
        // on every ordinary token would be a value consumers learn to ignore, and the audit pipeline
        // would have to distinguish "absent" from "empty" to answer "was this impersonated" — which
        // is the question docs/plan/06 § Platform administration exists to make answerable.
        if (impersonatedBy != Guid.Empty) {
            Add(identity, AccessTokenClaims.ImpersonatedBy, N(impersonatedBy));
        }

        var principal = new ClaimsPrincipal(identity);

        // ⚠ The closed set, checked against what was actually built rather than against what this
        // method intends to build. The two differ the moment somebody adds an AddClaim above without
        // reading the remarks, and this is the line that turns that into a failure here instead of a
        // token in the wild carrying a claim the gateway will not recognise.
        if (AccessTokenClaims.EnsurePermitted(principal.Claims.Select(x => x.Type)).TryGetError(out var error)) {
            throw new InvalidOperationException(error.Message);
        }

        return principal;
    }

    /// <summary>Adds one claim, marked for the access token — see the ⚠ on the type.</summary>
    static void Add(ClaimsIdentity identity, string type, string value, string valueType = ClaimValueTypes.String) {
        var claim = new Claim(type, value, valueType);
        claim.SetDestinations(OpenIddictConstants.Destinations.AccessToken);
        identity.AddClaim(claim);
    }

    /// <summary>
    ///     The RFC 8176 authentication-method reference for one of our methods.
    /// </summary>
    /// <param name="method">How the subject authenticated.</param>
    /// <remarks>
    ///     ⚠ These are the registered <c>amr</c> values, not our enum names. A relying party reading
    ///     <c>amr</c> is reading the IANA registry; emitting <c>"Passkey"</c> would be a value nobody
    ///     else understands, in a claim whose whole purpose is being understood by somebody else.
    /// </remarks>
    public static string AmrValue(AuthenticationMethod method) =>
        method switch {
            // "hwk" — proof-of-possession of a hardware-secured key. The closest registered value for
            // a WebAuthn assertion; there is no `passkey` in the registry.
            AuthenticationMethod.Passkey => "hwk",
            AuthenticationMethod.Password => "pwd",
            AuthenticationMethod.Totp => "otp",
            AuthenticationMethod.RecoveryCode => "otp",
            AuthenticationMethod.EmailOtp => "otp",
            AuthenticationMethod.ClientCredential => "pop",
            _ => "unspecified"
        };

    static string N(Guid value) => value.ToString("N", CultureInfo.InvariantCulture);
}
