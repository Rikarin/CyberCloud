namespace CyberCloud.Identity.Validation;

/// <summary>
///     What a validated token says. A relying party never sees a token's bytes — only this.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every field here is <i>output</i> of validation, never input to it.</b> A validator
///         that read <see cref="TenantId" /> off an unverified token and handed it back would defeat
///         the whole of the gateway's stage 3: the tenant would once again be caller-controlled, just
///         through a different surface. <c>TenantFromTokenTests.AForgedTokenIs401AndCarriesNoTenantAtAll</c>
///         is the assertion that a token this platform did not issue produces no claims at all.
///     </para>
///     <para>
///         Declared here rather than in the gateway because two hosts read it now — the gateway's
///         stage 2 and the feeds host's credential resolver — and the whole point of
///         <see cref="JwksBearerTokenValidator" /> is that both read the <i>same</i> thing off the
///         same validation.
///     </para>
/// </remarks>
/// <param name="TenantId">The token's <c>tid</c>. The one and only source of a request's tenant.</param>
/// <param name="SubjectType">
///     The ReBAC subject type — the token's <c>sub_typ</c> claim, one of <c>user</c>,
///     <c>servicePrincipal</c>, <c>managedIdentity</c>. ⚠ Its own claim, never a prefix on
///     <see cref="SubjectId" />: docs/plan/07 § The model makes <c>user:abc</c> and
///     <c>servicePrincipal:abc</c> two different subjects, so a validator that produced the id alone
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
/// <param name="SessionId">
///     The token session the token belongs to — its <c>sid</c> claim, the <c>N</c> form of a GUID —
///     or empty for a token minted without one (a client-credentials grant). ⚠ Identification, never
///     authorization: the gateway reads it to mark "this session" in the caller's own session list
///     (#41) and for nothing else, because a revoked session's access token still validates for its
///     ten minutes and a check on this field would pretend otherwise.
/// </param>
public readonly record struct TokenClaims(
    Guid TenantId,
    string SubjectType,
    string SubjectId,
    string Scopes,
    string ImpersonatedBy,
    DateTimeOffset ExpiresAt,
    string SessionId = ""
);
