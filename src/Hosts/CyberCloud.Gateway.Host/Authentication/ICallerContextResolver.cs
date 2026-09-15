using CyberCloud.Identity.Validation;

namespace CyberCloud.Gateway.Host.Authentication;

// ⚠ TokenClaims used to be declared here. It is CyberCloud.Identity.Validation's now, because the
// feeds host reads the same record off the same validator — docs/plan/13 § Artifact feeds, issue
// #29 — and two spellings of "what a validated token says" is how two hosts stop agreeing about
// whose tenant a request is in.

/// <summary>
///     Stage 2 — turns the request's credential into claims, or refuses it.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             This is a seam, and <see cref="JwksCallerContextResolver" /> is the production
///             implementation — registered by <c>GatewayComposition.BuildAsync</c> from
///             <c>CyberCloud:Gateway:Identity</c>, and refused at composition when nothing is.
///         </b> docs/plan/10 § Authentication inputs describes five callers and one credential
///         shape; nothing about that shape is the gateway's to decide. What the gateway owns is
///         everything <i>after</i> a token is known good, and the list below is what it asked the
///         identity host for so that a token could be known good at all. Each item now says where it
///         is met. ⚠ For as long as no implementation was registered, the gateway built, started,
///         passed its health checks and answered <c>500</c> to every other request —
///         https://github.com/Rikarin/CyberCloud/issues/68 — which is why the composition now
///         refuses rather than defaults.
///     </para>
///     <para>
///         <b>Exactly what the identity host provides, and where:</b>
///     </para>
///     <list type="number">
///         <item>
///             <b>An OIDC discovery document and a JWKS endpoint</b>, reachable from every gateway
///             pod in every region, with the signing keys the platform's tokens are signed with. The
///             gateway caches the JWKS and refreshes on an unknown <c>kid</c> — a rotation must not
///             need a gateway deploy. ✅ OpenIddict's server endpoints on the identity host, at
///             <c>AccessTokenPolicy.DiscoveryPath</c> and <c>AccessTokenPolicy.JsonWebKeySetPath</c>;
///             fetched, cached and refreshed by <c>OpenIddictValidationService</c>.
///         </item>
///         <item>
///             <b>The issuer and audience values</b> a valid token carries, so validation can pin
///             both. An unpinned audience means a token minted for some other relying party is
///             accepted here. ✅ <c>GatewayIdentityOptions</c>, from configuration; the audience is
///             <c>AccessTokenPolicy.Audience</c> on both sides.
///         </item>
///         <item>
///             <b>A <c>tid</c> claim on every token</b>, a GUID, present on user tokens, service
///             principal tokens and exchanged workload tokens alike. ⚠ This is the single claim the
///             whole tenancy boundary rests on — docs/plan/00 § The tenant-separation row, corrected
///             makes the gateway the <i>only</i> thing standing between a client-side
///             <c>IGrainFactory</c> and another tenant's grains. A token without <c>tid</c> must be
///             rejected, never defaulted. ✅ Minted on every path by <c>AccessTokenPrincipalFactory</c>;
///             refused when absent by <c>JwksBearerTokenValidator.ToClaims</c>, and
///             <c>JwksBearerTokenValidatorTests.AMissingMalformedOrEmptyTidIsRefusedNeverDefaulted</c>
///             holds the refusal.
///         </item>
///         <item>
///             <b>A subject type</b> distinguishable from the subject id, because ReBAC subjects are
///             typed (docs/plan/07 § The model) and <c>user:abc</c> and <c>servicePrincipal:abc</c>
///             are different subjects. A dedicated claim, not a prefix convention on <c>sub</c>.
///             ✅ <b>Settled: the claim is <c>sub_typ</c></b>, carrying one of <c>user</c>,
///             <c>servicePrincipal</c>, <c>managedIdentity</c> — <c>CyberCloud.Identity.Contracts</c>'s
///             <c>AccessTokenClaims.SubjectType</c> and <c>SubjectTypes</c>. ⚠ The spellings are ReBAC
///             object types and are matched <i>ordinally</i>: <c>serviceprincipal</c> is not
///             <c>servicePrincipal</c>, and the wrong case produces a subject no tuple names, so
///             every check denies and it reads as a permissions bug.
///         </item>
///         <item>
///             <b>The impersonation claim</b> of docs/plan/06 § Platform administration, minted only
///             by the identity host and never accepted from a header.
///             ✅ <b>Settled: the claim is <c>act_sub</c></b> — <c>AccessTokenClaims.ImpersonatedBy</c>,
///             the flattened <c>act.sub</c> of RFC 8693 § 4.1 — carrying the operator's user GUID in
///             <c>N</c> form, and absent entirely on an ordinary token.
///             <para>
///                 ⚠
///                 <b>
///                     docs/plan/06 § Platform administration says the value travels as an
///                     <c>X-CyberCloud-Impersonated-By</c> header, and read literally that is a doc
///                     defect the gateway must not implement.
///                 </b> That header is caller-controlled on
///                 every request this component serves, so honouring it would let anyone name any
///                 operator in the audit trail — defeating the second-operator approval, the 60-minute
///                 box and the tenant's notification in one line. The header is correct on the
///                 <i>internal</i> hop, gateway to resource manager, which is what
///                 <c>CallerContext.ImpersonatedBy</c> already is; at the edge the value comes from
///                 the token or it does not exist. <see cref="ResolveAsync" /> reads the
///                 <c>Authorization</c> header and nothing else, which is what makes that structural
///                 rather than a rule — and
///                 <c>ImpersonationAndSubjectTypeTests.TheImpersonationHeaderCannotInjectAnOperator</c>
///                 is the assertion, with
///                 <c>ImpersonationAndSubjectTypeTests.NoSpellingOfTheHeaderIsRead</c> and
///                 <c>ImpersonationAndSubjectTypeTests.AnImpersonationHeaderCannotOverrideAMintedOne</c>
///                 closing the two ways round it.
///             </para>
///         </item>
///         <item>
///             <b>Token lifetime of 10 minutes</b> and a refresh flow, per docs/plan/10
///             § Authentication inputs. The gateway enforces <see cref="TokenClaims.ExpiresAt" />
///             itself as well, so a validator misconfigured to ignore <c>exp</c> is still caught.
///             ✅ <c>AccessTokenPolicy.AccessTokenLifetime</c> on the server; both checks in the
///             resolver. ⚠ The refresh flow is owed — it follows the authorization-code grant, which
///             the identity host's <c>TokenApi</c> says is waiting on a client index.
///         </item>
///         <item>
///             <b>The trusted OIDC issuer per tenant cluster</b>, for the workload-identity exchange
///             in docs/plan/10 § Authentication inputs. The exchange itself happens at the identity
///             host; the gateway only ever sees the platform token it returns. ⚠ The decision exists
///             (<c>ITokenExchange</c>) and the grant is not yet accepted at <c>/token</c>.
///         </item>
///     </list>
///     <para>
///         ⚠ <b>What the gateway must NOT be asked to do:</b> validate a session cookie.
///         docs/plan/10 § Request pipeline is explicit — a session cookie is honoured
///         <i>"only on the identity host, never here"</i>. A cookie is ambient authority and would
///         make every gateway endpoint CSRF-reachable.
///     </para>
/// </remarks>
interface ICallerContextResolver {
    /// <summary>Validates the request's credential.</summary>
    /// <param name="request">
    ///     The request. ⚠ Only the <c>Authorization</c> header is read. Reading anything else would
    ///     put a caller-controlled surface inside authentication.
    /// </param>
    /// <param name="cancellationToken">Cancels the validation, including a JWKS fetch.</param>
    /// <returns>
    ///     The claims, or <see cref="ErrorCode.AuthorizationFailed" /> with a reason phrased in terms
    ///     of the request rather than the token's contents.
    /// </returns>
    Task<Result<TokenClaims>> ResolveAsync(HttpRequest request, CancellationToken cancellationToken = default);
}
