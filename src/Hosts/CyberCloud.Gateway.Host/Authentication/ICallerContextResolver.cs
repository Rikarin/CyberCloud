namespace CyberCloud.Gateway.Host.Authentication;

/// <summary>
///     What a validated token says. The gateway never sees a token's bytes — only this.
/// </summary>
/// <remarks>
///     ⚠ <b>Every field here is <i>output</i> of validation, never input to it.</b> A resolver that
///     read <see cref="TenantId" /> off an unverified token and handed it back would defeat the whole
///     of stage 3: the tenant would once again be caller-controlled, just through a different
///     surface. <c>TenantFromTokenTests.AForgedTokenIs401AndCarriesNoTenantAtAll</c> is the assertion
///     that a token this platform did not issue produces no claims at all.
/// </remarks>
/// <param name="TenantId">The token's <c>tid</c>. The one and only source of a request's tenant.</param>
/// <param name="SubjectType">
///     The ReBAC subject type — the token's <c>sub_typ</c> claim, one of <c>user</c>,
///     <c>servicePrincipal</c>, <c>managedIdentity</c>. ⚠ Its own claim, never a prefix on
///     <see cref="SubjectId" />: docs/plan/07 § The model makes <c>user:abc</c> and
///     <c>servicePrincipal:abc</c> two different subjects, so a resolver that produced the id alone
///     would leave the type to be guessed at the one place a wrong guess is a wrong access decision.
/// </param>
/// <param name="SubjectId">The ReBAC subject id — the <c>sub</c> claim, and only the id.</param>
/// <param name="Scopes">The token's scopes, space-separated as the <c>scp</c> claim carries them.</param>
/// <param name="ImpersonatedBy">
///     The operator behind an impersonated request — the token's <c>act_sub</c> claim — or empty.
///     docs/plan/06 § Platform administration.
///     ⚠ A claim, not a header: a header would let any caller set it, and with it the audit record,
///     the 60-minute box and the tenant's notification all become decoration.
/// </param>
/// <param name="ExpiresAt">When the token expires. docs/plan/10 § Authentication inputs: 10 minutes.</param>
readonly record struct TokenClaims(
    Guid TenantId,
    string SubjectType,
    string SubjectId,
    string Scopes,
    string ImpersonatedBy,
    DateTimeOffset ExpiresAt
);

/// <summary>
///     Stage 2 — turns the request's credential into claims, or refuses it.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This is a seam, and the implementation that closes it lives in the identity host,
///         which is a different component being built separately.</b> docs/plan/10 § Authentication
///         inputs describes five callers and one credential shape; nothing about that shape is the
///         gateway's to decide. What the gateway owns is everything <i>after</i> a token is known
///         good, and that half is complete and tested.
///     </para>
///     <para>
///         <b>Exactly what the identity host must provide for a production implementation:</b>
///     </para>
///     <list type="number">
///         <item>
///             <b>An OIDC discovery document and a JWKS endpoint</b>, reachable from every gateway
///             pod in every region, with the signing keys the platform's tokens are signed with. The
///             gateway caches the JWKS and refreshes on an unknown <c>kid</c> — a rotation must not
///             need a gateway deploy.
///         </item>
///         <item>
///             <b>The issuer and audience values</b> a valid token carries, so validation can pin
///             both. An unpinned audience means a token minted for some other relying party is
///             accepted here.
///         </item>
///         <item>
///             <b>A <c>tid</c> claim on every token</b>, a GUID, present on user tokens, service
///             principal tokens and exchanged workload tokens alike. ⚠ This is the single claim the
///             whole tenancy boundary rests on — docs/plan/00 § The tenant-separation row, corrected
///             makes the gateway the <i>only</i> thing standing between a client-side
///             <c>IGrainFactory</c> and another tenant's grains. A token without <c>tid</c> must be
///             rejected, never defaulted.
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
///                 ⚠ <b>docs/plan/06 § Platform administration says the value travels as an
///                 <c>X-CyberCloud-Impersonated-By</c> header, and read literally that is a doc
///                 defect the gateway must not implement.</b> That header is caller-controlled on
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
///         </item>
///         <item>
///             <b>The trusted OIDC issuer per tenant cluster</b>, for the workload-identity exchange
///             in docs/plan/10 § Authentication inputs. The exchange itself happens at the identity
///             host; the gateway only ever sees the platform token it returns.
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
