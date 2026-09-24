using CyberCloud.Core.Time;
using CyberCloud.Identity.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenIddict.Abstractions;
using OpenIddict.Validation;
using System.Globalization;
using System.Security.Claims;

namespace CyberCloud.Identity.Validation;

/// <summary>
///     The production identity seam: validates a bearer JWT against the identity host's published
///     key set, and turns its claims into <see cref="TokenClaims" />. docs/plan/11 § Protocol.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             This is the implementation the gateway's <c>ICallerContextResolver</c> asked the
///             identity host for, item by item, and where each item is met.
///         </b> The discovery document and the JWKS (item 1) are OpenIddict's server endpoints on
///         <c>CyberCloud.Identity.Host</c>, fetched and cached by <see cref="OpenIddictValidationService" />
///         and refreshed when a token names a <c>kid</c> the cache does not hold — a key rotation is
///         a JWKS change and never a host deploy. The issuer and audience (item 2) are pinned from
///         <see cref="BearerTokenOptions" />. <c>tid</c> (item 3), <c>sub_typ</c> (item 4) and
///         <c>act_sub</c> (item 5) are read by <see cref="ToClaims" /> after the signature, issuer,
///         audience and expiry have passed, and a token missing either of the first two is refused
///         there. Expiry (item 6) is enforced twice: by the validator against <c>exp</c>, and again
///         here against <see cref="IClock" />, so a validator misconfigured to ignore <c>exp</c> is
///         still caught. Item 7, the workload exchange, happens at the identity host and never here.
///     </para>
///     <para>
///         ⚠
///         <b>
///             Every field of <see cref="TokenClaims" /> is read from the <i>validated</i> principal
///             and from nothing else.
///         </b> The token's bytes are handed to OpenIddict and what comes back
///         is a principal or an exception; no claim is read before the signature is checked, which
///         is what makes <c>TenantFromTokenTests.AForgedTokenIs401AndCarriesNoTenantAtAll</c>'s
///         property hold for this implementation as it does for the in-process one.
///     </para>
///     <para>
///         ⚠
///         <b>
///             A token that cannot be validated because the identity host cannot be reached is a
///             <c>500</c> and not a <c>401</c>, deliberately.
///         </b> OpenIddict reports an unreachable discovery document or key set as a protocol error
///         of kind <c>server_error</c>, and this validator rethrows it as an
///         <see cref="InvalidOperationException" /> naming the issuer. A <c>401</c> there would tell
///         every caller their credential is bad while the host's own dependency is down — and
///         https://github.com/Rikarin/CyberCloud/issues/68 is precisely the record of how long a
///         gateway can serve the wrong status for a wiring fault before anyone looks.
///     </para>
///     <para>
///         <b>Per-request resolution of the validation service, through the request's own scope.</b>
///         <see cref="OpenIddictValidationService" /> is registered scoped and this validator is a
///         singleton, because the gateway's <c>AuthenticateStage</c> is; the request's
///         <see cref="HttpContext.RequestServices" /> is the scope OpenIddict expects to be resolved
///         from.
///     </para>
/// </remarks>
/// <param name="clock">For the second expiry check.</param>
/// <param name="logger">Where a refused token's reason goes — never to the caller.</param>
public sealed class JwksBearerTokenValidator(IClock clock, ILogger<JwksBearerTokenValidator> logger)
    : IBearerTokenValidator {
    /// <inheritdoc />
    public async Task<Result<TokenClaims>> ValidateAsync(
        string token,
        HttpContext http,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(http);

        if (token.Length == 0) {
            return Result<TokenClaims>.Failure(BearerTokenErrors.Unauthenticated("the bearer token is empty"));
        }

        var validation = http.RequestServices.GetRequiredService<OpenIddictValidationService>();

        ClaimsPrincipal principal;

        try {
            principal = await validation.ValidateAccessTokenAsync(token, cancellationToken);
        } catch (OpenIddictExceptions.ProtocolException refused)
            when (!string.Equals(refused.Error, OpenIddictConstants.Errors.ServerError, StringComparison.Ordinal)) {
                // ⚠ The reason stays in the log. "Bad signature", "wrong audience" and "expired" are each
                // a hint to whoever is probing, and the caller can act on exactly one thing — that they
                // need a token this platform issued.
                logger.LogInformation(
                    "A bearer token was refused: {Error} — {Description}",
                    refused.Error,
                    refused.ErrorDescription
                );

                return Result<TokenClaims>.Failure(
                    BearerTokenErrors.Unauthenticated("the bearer token was not issued by this platform or has expired")
                );
            } catch (OpenIddictExceptions.ProtocolException unreachable) {
                throw new InvalidOperationException(
                    "No bearer token can be validated because the identity host's discovery document or "
                    + "key set could not be retrieved. The host's bearer-token section names the issuer "
                    + $"— OpenIddict said: {unreachable.ErrorDescription}",
                    unreachable
                );
            }

        return ToClaims(principal, clock.UtcNow);
    }

    /// <summary>
    ///     Reads the platform's claims off a principal the validator has already accepted.
    /// </summary>
    /// <param name="principal">What <see cref="OpenIddictValidationService" /> returned.</param>
    /// <param name="now">For the second expiry check.</param>
    /// <returns>The claims, or the one uniform refusal.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Static and separate so it can be tested without a key set.</b> Signing a token
    ///         needs the identity host; reading one does not. <c>JwksBearerTokenValidatorTests</c>
    ///         drives every refusal below with a hand-built principal, and the end-to-end path —
    ///         a token the identity host actually signed, validated against the JWKS it actually
    ///         published — is <c>TenantOverHttpTests</c>' job.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A forbidden claim is a refusal, not a claim to ignore.</b> The identity host
    ///         publishes <see cref="AccessTokenClaims.ForbiddenClaims" /> at
    ///         <c>/.well-known/cybercloud-token-policy</c> with the instruction that a relying party
    ///         "should treat a token carrying one as suspect rather than as a token with extra
    ///         claims" — a role claim in a Cyber Cloud token can only mean a token minted by
    ///         something other than the factory that refuses to emit one.
    ///     </para>
    /// </remarks>
    public static Result<TokenClaims> ToClaims(ClaimsPrincipal principal, DateTimeOffset now) {
        ArgumentNullException.ThrowIfNull(principal);

        foreach (var claim in principal.Claims) {
            if (AccessTokenClaims.ForbiddenClaims.Contains(claim.Type)) {
                return Refuse($"the token carries '{claim.Type}', which no Cyber Cloud token does");
            }
        }

        // ⚠ tid: refused, never defaulted. docs/plan/00 § The tenant-separation row, corrected makes
        // this claim the whole tenancy boundary for a client-side IGrainFactory, and a token without
        // one is a token that names no tenant — Guid.Empty is the platform tenant, which is the one
        // answer a missing claim must never resolve to.
        if (!Guid.TryParseExact(principal.GetClaim(AccessTokenClaims.TenantId), "N", out var tenantId)
            || tenantId == Guid.Empty) {
            return Refuse("the token carries no usable tid claim");
        }

        var subjectType = principal.GetClaim(AccessTokenClaims.SubjectType);

        if (string.IsNullOrEmpty(subjectType)) {
            return Refuse("the token carries no sub_typ claim");
        }

        var subjectId = principal.GetClaim(AccessTokenClaims.Subject);

        if (string.IsNullOrEmpty(subjectId)) {
            return Refuse("the token carries no sub claim");
        }

        // exp: OpenIddict maps the validated token's expiry onto the principal; the raw claim is the
        // fallback for a principal built some other way. Neither present is a token with no expiry,
        // which docs/plan/10 § Authentication inputs does not allow.
        var expiresAt = principal.GetExpirationDate()
            ?? ReadUnixSeconds(principal.GetClaim(AccessTokenClaims.ExpiresAt));

        if (expiresAt is not { } expiry) {
            return Refuse("the token carries no expiry");
        }

        if (expiry <= now) {
            return Refuse("the bearer token has expired");
        }

        return Result<TokenClaims>.Success(
            new(
                tenantId,
                subjectType,
                subjectId,
                principal.GetClaim(AccessTokenClaims.Scope) ?? string.Empty,
                principal.GetClaim(AccessTokenClaims.ImpersonatedBy) ?? string.Empty,
                expiry,
                principal.GetClaim(AccessTokenClaims.SessionId) ?? string.Empty
            )
        );
    }

    static DateTimeOffset? ReadUnixSeconds(string? value) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;

    static Result<TokenClaims> Refuse(string reason) =>
        Result<TokenClaims>.Failure(BearerTokenErrors.Unauthenticated(reason));
}
