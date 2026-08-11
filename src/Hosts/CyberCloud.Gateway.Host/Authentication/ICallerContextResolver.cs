namespace CyberCloud.Gateway.Host.Authentication;

/// <summary>
///     What a validated token says. The gateway never sees a token's bytes — only this.
/// </summary>
/// <remarks>
///     ⚠ <b>Every field here is <i>output</i> of validation, never input to it.</b> A resolver that
///     read <see cref="TenantId" /> off an unverified token and handed it back would defeat the whole
///     of stage 3: the tenant would once again be caller-controlled, just through a different
///     surface. <c>ForgedTokenTests</c> is the assertion that a token this platform did not issue
///     produces no claims at all.
/// </remarks>
/// <param name="TenantId">The token's <c>tid</c>. The one and only source of a request's tenant.</param>
/// <param name="SubjectType">The ReBAC subject type — <c>user</c>, <c>serviceprincipal</c>.</param>
/// <param name="SubjectId">The ReBAC subject id — the <c>sub</c> claim.</param>
/// <param name="Scopes">The token's scopes, space-separated as the <c>scp</c> claim carries them.</param>
/// <param name="ImpersonatedBy">
///     The operator behind an impersonated request, or empty. docs/plan/06 § Platform administration.
///     ⚠ A claim, not a header: a header would let any caller set it.
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
///             typed (docs/plan/07 § The model) and <c>user:abc</c> and <c>serviceprincipal:abc</c>
///             are different subjects. A dedicated claim, not a prefix convention on <c>sub</c>.
///         </item>
///         <item>
///             <b>The impersonation claim</b> of docs/plan/06 § Platform administration, minted only
///             by the identity host and never accepted from a header.
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
