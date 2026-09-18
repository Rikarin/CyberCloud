using Microsoft.AspNetCore.Http;

namespace CyberCloud.Identity.Validation;

/// <summary>
///     Turns a bearer token's bytes into claims, or refuses them. docs/plan/11 § Protocol.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             The token arrives as a string on purpose, and reading it out of the request is the
///             caller's job.
///         </b> The gateway's stage 2 reads exactly one place — the <c>Authorization</c> header's
///         <c>Bearer</c> scheme — and its <c>ICallerContextResolver</c> says why nothing else may be
///         read there. The feeds host has to read three, because the clients it serves do not agree:
///         <c>npm</c> sends <c>Authorization: Bearer</c>, <c>mvn deploy</c> sends the token as a
///         <c>Basic</c> password, and <c>dotnet nuget push</c> sends it as <c>X-NuGet-ApiKey</c>. What
///         the three have in common is everything <i>after</i> the bytes are known, and that is what
///         this seam is.
///     </para>
///     <para>
///         <see cref="JwksBearerTokenValidator" /> is the production implementation, registered by
///         <see cref="BearerTokenServiceCollectionExtensions.AddJwksBearerTokenValidation" />. A test
///         that has no key set registers a table of tokens it issued itself; a host that ends up with
///         neither must refuse to compose, the way <c>GatewayComposition.BuildAsync</c> does.
///     </para>
/// </remarks>
public interface IBearerTokenValidator {
    /// <summary>Validates one token.</summary>
    /// <param name="token">The token's bytes, with any scheme prefix already removed.</param>
    /// <param name="http">
    ///     The request the token arrived on. ⚠ Only its <see cref="HttpContext.RequestServices" />
    ///     is read — the JWKS validator resolves OpenIddict's scoped validation service from the
    ///     request's own scope, which is the one OpenIddict expects to be resolved from. Nothing on
    ///     the request itself is read, so nothing caller-controlled reaches validation through here.
    /// </param>
    /// <param name="cancellationToken">Cancels the validation, including a JWKS fetch.</param>
    /// <returns>
    ///     The claims, or <see cref="ErrorCode.AuthorizationFailed" /> with a reason phrased in terms
    ///     of the request rather than the token's contents.
    /// </returns>
    Task<Result<TokenClaims>> ValidateAsync(
        string token,
        HttpContext http,
        CancellationToken cancellationToken = default
    );
}

/// <summary>The one refusal shape every validator answers with.</summary>
public static class BearerTokenErrors {
    /// <summary>A refusal — <see cref="ErrorCode.AuthorizationFailed" />, worded for the caller.</summary>
    /// <param name="reason">What the request lacked, in terms of the request and not the token's contents.</param>
    public static Error Unauthenticated(string reason) =>
        new(
            ErrorCode.AuthorizationFailed,
            $"The request is not authenticated: {reason}. docs/plan/10 § Authentication inputs — "
            + "every caller presents a bearer token scoped to one tenant, and tokens live 10 minutes."
        );
}
